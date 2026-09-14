using Godot;
using System;
using System.Collections.Generic;

namespace Game.Simulation.Gpu;

// Общая GPU-инфраструктура для треков 1+2+4+5 (flowfield / диффузия / fbm / редукция).
// Legacy AgentGpuComputeService.cs НЕ трогаем — он живёт сам по себе.
// Все методы RenderingDevice ниже — только те, что уже скомпилированы в проекте
// (см. AgentGpuComputeService.cs): CreateLocalRenderingDevice / RDShaderFile.GetSpirV /
// ShaderCreateFromSpirV / ComputePipelineCreate / StorageBufferCreate / UniformSetCreate /
// BufferUpdate / ComputeListBegin-BindPipeline-BindUniformSet-SetPushConstant-Dispatch-End /
// Submit / Sync / BufferGetData / FreeRid / Free.
// <remarks>
// Проверка метода рендера: прямого вызова RenderingServer.GetCurrentRenderingMethod()
// в коде НЕТ сознательно — в статически типизированном C# отсутствующий метод дал бы
// ошибку компиляции, а не исключение, которое можно поймать. Поэтому метод ищется
// через рефлексию (скомпилируется при любом API 4.7): если метод есть — используется
// его результат, если нет — fallback на ProjectSettings "renderer/rendering_method".
// </remarks>
public sealed class GpuComputeContext : IDisposable
{
    // Лимит push-констант Vulkan/Godot: MAX_PUSH_CONSTANT_SIZE = 128 байт.
    // Массивы больше 128 байт запрещены — Dispatch1D/Dispatch2D кидают ArgumentException.
    public const int MaxPushConstantSize = 128;

    private readonly object _lock = new();
    private RenderingDevice _rd;
    private bool _initialized;
    private bool _disposed;
    private bool _compatibilityRenderer;

    // Персистентные storage-буферы: key -> (Rid, размер).
    private readonly Dictionary<string, (Rid Rid, uint Size)> _buffers = new();
    // Кэш пайплайнов: glsl-путь -> (pipeline, shader).
    private readonly Dictionary<string, (Rid Pipeline, Rid Shader)> _pipelines = new();
    // UniformSet'ы, созданные через CreateUniformSet — освобождаем в ReleaseAll.
    private readonly List<Rid> _uniformSets = new();
    // Чтобы GD.PrintErr про битый шейдер печаталась один раз на путь.
    private readonly HashSet<string> _pipelineErrorLogged = new();

    // true только после успешного EnsureInitialized(): device создан
    // и метод рендера — НЕ Compatibility. До инициализации — false
    // (конструктор лёгкий, device не создаёт).
    public bool IsAvailable
    {
        get
        {
            lock (_lock)
            {
                return _initialized && !_disposed && _rd != null && !_compatibilityRenderer;
            }
        }
    }

    public GpuComputeContext()
    {
        // Намеренно пусто: тяжёлое создание device — только в EnsureInitialized().
    }

    // Ленивая потокобезопасная инициализация (можно звать из главного и sim-потоков).
    // Возвращает true, если контекст готов к диспатчу.
    public bool EnsureInitialized()
    {
        lock (_lock)
        {
            if (_disposed)
                return false;
            if (_initialized)
                return _rd != null && !_compatibilityRenderer;

            RenderingDevice rd;
            try
            {
                rd = RenderingServer.CreateLocalRenderingDevice();
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[GpuComputeContext] CreateLocalRenderingDevice упал: {ex.Message}. CPU fallback.");
                return false;
            }

            if (rd == null)
            {
                GD.Print("[GpuComputeContext] Локальный RenderingDevice недоступен. CPU fallback.");
                return false;
            }

            _compatibilityRenderer = CheckIsCompatibilityRenderer();
            if (_compatibilityRenderer)
            {
                GD.Print("[GpuComputeContext] Метод рендера Compatibility — compute недоступен. CPU fallback.");
                rd.Free();
                return false;
            }

            _rd = rd;
            _initialized = true;
            return true;
        }
    }

    // Пулы персистентных storage-буферов. При запросе большего размера
    // старый буфер FreeRid + создаётся новый. Потокобезопасно.
    public Rid GetOrCreateBuffer(string key, uint byteSize)
    {
        if (string.IsNullOrEmpty(key))
            throw new ArgumentException("Ключ буфера пуст.", nameof(key));
        if (byteSize == 0)
            throw new ArgumentOutOfRangeException(nameof(byteSize), "Размер буфера должен быть > 0.");

        lock (_lock)
        {
            ThrowIfNotReady();
            if (_buffers.TryGetValue(key, out var entry) && entry.Rid.IsValid)
            {
                if (entry.Size >= byteSize)
                    return entry.Rid;
                // Мал — пересоздаём.
                _rd.FreeRid(entry.Rid);
                _buffers.Remove(key);
            }
            Rid rid = _rd.StorageBufferCreate(byteSize);
            _buffers[key] = (rid, byteSize);
            return rid;
        }
    }

    // Обёртка над BufferUpdate с offset=0.
    public void BufferUpdate(Rid buffer, byte[] data)
    {
        if (data == null)
            throw new ArgumentNullException(nameof(data));
        lock (_lock)
        {
            ThrowIfNotReady();
            if (!buffer.IsValid)
                throw new ArgumentException("Невалидный Rid буфера.", nameof(buffer));
            _rd.BufferUpdate(buffer, 0, (uint)data.Length, data);
        }
    }

    // Кэш compute-пайплайнов по res:// пути к .glsl. Ошибки — GD.PrintErr один раз
    // на путь + возврат невалидного Rid (IsValid == false).
    public Rid GetOrCreatePipeline(string glslPath, out Rid shader)
    {
        lock (_lock)
        {
            shader = default;
            if (string.IsNullOrEmpty(glslPath))
                return default;
            // Без готового device кэшировать нечего — сразу invalid.
            if (!_initialized || _disposed || _rd == null)
                return default;

            if (_pipelines.TryGetValue(glslPath, out var cached) && cached.Pipeline.IsValid)
            {
                shader = cached.Shader;
                return cached.Pipeline;
            }

            try
            {
                var shaderFile = ResourceLoader.Load<RDShaderFile>(glslPath);
                if (shaderFile == null)
                {
                    LogPipelineErrorOnce(glslPath, "не удалось загрузить RDShaderFile");
                    return default;
                }
                var spirV = shaderFile.GetSpirV();
                if (spirV == null)
                {
                    LogPipelineErrorOnce(glslPath, "ошибка SPIR-V байткода");
                    return default;
                }
                Rid sh = _rd.ShaderCreateFromSpirV(spirV);
                Rid pipe = _rd.ComputePipelineCreate(sh);
                _pipelines[glslPath] = (pipe, sh);
                shader = sh;
                return pipe;
            }
            catch (Exception ex)
            {
                LogPipelineErrorOnce(glslPath, ex.Message);
                return default;
            }
        }
    }

    // Хелпер: собрать UniformSet из пар (binding, buffer) для set=0.
    // Созданный set трекается и освобождается в ReleaseAll().
    public Rid CreateUniformSet(Rid shader, params (uint Binding, Rid Buffer)[] bindings)
    {
        if (bindings == null)
            throw new ArgumentNullException(nameof(bindings));
        lock (_lock)
        {
            ThrowIfNotReady();
            if (!shader.IsValid)
                throw new ArgumentException("Невалидный Rid шейдера.", nameof(shader));
            var arr = new Godot.Collections.Array<RDUniform>();
            foreach (var (binding, buffer) in bindings)
            {
                if (!buffer.IsValid)
                    throw new ArgumentException($"Невалидный Rid буфера для binding {binding}.", nameof(bindings));
                var u = new RDUniform
                {
                    UniformType = RenderingDevice.UniformType.StorageBuffer,
                    Binding = (int)binding,
                };
                u.AddId(buffer);
                arr.Add(u);
            }
            Rid set = _rd.UniformSetCreate(arr, shader, 0);
            _uniformSets.Add(set);
            return set;
        }
    }

    // 1D-диспатч БЕЗ Sync (синхронизация — дело вызывателя через ReadbackSync/Async).
    public void Dispatch1D(Rid pipeline, Rid uniformSet, byte[] pushConstants, uint xGroups)
    {
        Dispatch(pipeline, uniformSet, pushConstants, xGroups, 1, 1);
    }

    // 2D-диспатч БЕЗ Sync.
    public void Dispatch2D(Rid pipeline, Rid uniformSet, byte[] pushConstants, uint xGroups, uint yGroups)
    {
        Dispatch(pipeline, uniformSet, pushConstants, xGroups, yGroups, 1);
    }

    private void Dispatch(Rid pipeline, Rid uniformSet, byte[] pushConstants, uint x, uint y, uint z)
    {
        if (pushConstants != null && pushConstants.Length > MaxPushConstantSize)
            throw new ArgumentException(
                $"Push-константы {pushConstants.Length} байт > MAX_PUSH_CONSTANT_SIZE={MaxPushConstantSize}.",
                nameof(pushConstants));
        if (x == 0 || y == 0 || z == 0)
            throw new ArgumentOutOfRangeException("Число групп по каждой оси должно быть > 0.");
        lock (_lock)
        {
            ThrowIfNotReady();
            if (!pipeline.IsValid)
                throw new ArgumentException("Невалидный Rid пайплайна.", nameof(pipeline));
            if (!uniformSet.IsValid)
                throw new ArgumentException("Невалидный Rid uniform set.", nameof(uniformSet));

            long list = _rd.ComputeListBegin();
            _rd.ComputeListBindComputePipeline(list, pipeline);
            _rd.ComputeListBindUniformSet(list, uniformSet, 0);
            if (pushConstants != null && pushConstants.Length > 0)
                _rd.ComputeListSetPushConstant(list, pushConstants, (uint)pushConstants.Length);
            _rd.ComputeListDispatch(list, x, y, z);
            _rd.ComputeListEnd();
            _rd.Submit();
        }
    }

    // Синхронный readback: Sync + BufferGetData (для одноразовых задач: fbm-генерация).
    public byte[] ReadbackSync(Rid buffer)
    {
        lock (_lock)
        {
            ThrowIfNotReady();
            if (!buffer.IsValid)
                throw new ArgumentException("Невалидный Rid буфера.", nameof(buffer));
            _rd.Sync();
            return _rd.BufferGetData(buffer);
        }
    }

    // Асинхронный readback.
    // TODO: в C# API Godot 4.7 у RenderingDevice НЕТ метода BufferGetDataAsync
    // (проверено по факту компиляции — такой вызов не собирается), поэтому пока
    // синхронная реализация: Sync + BufferGetData + callback. Когда/если метод
    // появится в биндингах — заменить тело на настоящий async-вызов.
    public void ReadbackAsync(Rid buffer, Action<byte[]> callback)
    {
        if (callback == null)
            throw new ArgumentNullException(nameof(callback));
        byte[] data = ReadbackSync(buffer);
        callback(data);
    }

    // Явное освобождение всех буферов/пайплайнов/сетов БЕЗ уничтожения device.
    public void ReleaseAll()
    {
        lock (_lock)
        {
            if (_rd == null)
            {
                _buffers.Clear();
                _pipelines.Clear();
                _uniformSets.Clear();
                return;
            }
            foreach (var kv in _buffers)
            {
                if (kv.Value.Rid.IsValid)
                    _rd.FreeRid(kv.Value.Rid);
            }
            _buffers.Clear();
            foreach (Rid set in _uniformSets)
            {
                if (set.IsValid)
                    _rd.FreeRid(set);
            }
            _uniformSets.Clear();
            foreach (var kv in _pipelines)
            {
                if (kv.Value.Pipeline.IsValid)
                    _rd.FreeRid(kv.Value.Pipeline);
                if (kv.Value.Shader.IsValid)
                    _rd.FreeRid(kv.Value.Shader);
            }
            _pipelines.Clear();
            _pipelineErrorLogged.Clear();
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
            // ReleaseAll тоже берёт _lock — lock реентерабелен в C#, безопасно.
            ReleaseAll();
            if (_rd != null)
            {
                _rd.Free();
                _rd = null;
            }
            _initialized = false;
        }
        GC.SuppressFinalize(this);
    }

    private void ThrowIfNotReady()
    {
        if (_disposed || !_initialized || _rd == null)
            throw new InvalidOperationException("GpuComputeContext не инициализирован: вызовите EnsureInitialized().");
    }

    private void LogPipelineErrorOnce(string path, string reason)
    {
        if (_pipelineErrorLogged.Add(path))
            GD.PrintErr($"[GpuComputeContext] Пайплайн '{path}': {reason}.");
    }

    // true, если текущий метод рендера — Compatibility (compute-шейдеры недоступны).
    // Сначала пробуем RenderingServer.GetCurrentRenderingMethod() через рефлексию
    // (чтобы не ломать компиляцию, если метода нет в API 4.7), иначе fallback
    // на ProjectSettings "renderer/rendering_method".
    private static bool CheckIsCompatibilityRenderer()
    {
        try
        {
            var m = typeof(RenderingServer).GetMethod("GetCurrentRenderingMethod");
            if (m != null)
            {
                var v = m.Invoke(null, null) as string;
                if (!string.IsNullOrEmpty(v))
                    return v.Contains("gl_compatibility", StringComparison.OrdinalIgnoreCase)
                        || v.Contains("compatibility", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch (Exception ex)
        {
            GD.Print($"[GpuComputeContext] GetCurrentRenderingMethod недоступен ({ex.GetType().Name}), fallback на ProjectSettings.");
        }

        try
        {
            var setting = ProjectSettings.GetSetting("renderer/rendering_method", "forward_plus").AsString();
            return setting.Contains("gl_compatibility", StringComparison.OrdinalIgnoreCase)
                || setting.Equals("compatibility", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
