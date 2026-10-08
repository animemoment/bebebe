using System;
using System.Collections.Generic;
using Game.Core.WorldStreaming.Integration;
using Godot;

namespace Game.UI.Streaming;

/// <summary>Состояние человека мира (§29).</summary>
public enum WorldAgentState
{
    /// <summary>Свободен: ищет задачу или уходит домой.</summary>
    Idle,

    /// <summary>Идёт к чертежу по найденному пути.</summary>
    Walking,

    /// <summary>Работает на клетке.</summary>
    Working,

    /// <summary>Переброска по морю: цель на суше, но отрезана водой (лодок пока нет).</summary>
    SeaCrossing
}

/// <summary>Человек мира: позиция в клетках (центр клетки = +0.5), задача и путь к ней.</summary>
public sealed class WorldAgent
{
    public float X;
    public float Y;
    public WorldAgentState State;
    public WorldPlan Job;
    public float WorkLeft;
    public float IdleTime;

    /// <summary>Найденный путь (клетки), обход воды и гор.</summary>
    public readonly List<(long X, long Y)> Path = new(256);

    /// <summary>Текущая точка пути.</summary>
    public int PathIndex;

    /// <summary>Путь ещё не считан (считается по бюджету — не каждый кадр).</summary>
    public bool NeedsPath;

    /// <summary>Остаток переброски по морю, сек.</summary>
    public float TravelLeft;
}

/// <summary>
/// Работы и люди ЗА кромкой острова (§29–§30): берут чертежи из <see cref="WorldJobBoard"/>,
/// идут КРАТЧАЙШИМ ОБХОДОМ воды и гор (<see cref="WorldPathFinder"/>), строят и пишут «готово».
/// Отряд: <see cref="Dispatch"/>/<see cref="Recall"/> — API для интерфейса игрока (сам UI не наш).
/// Только main thread, островную симуляцию не трогаем.
/// </summary>
public sealed partial class WorldJobTicker : Node
{
    /// <summary>Максимум людей мира одновременно.</summary>
    [Export] public int MaxAgents = 32;

    /// <summary>Сколько людей выходит само, пока игрок никого не отправил.</summary>
    [Export] public int MaxAutoWorkers = 8;

    /// <summary>Как часто перечитываем чертежи (сек).</summary>
    [Export] public float BoardRefreshSeconds = 0.3f;

    /// <summary>Скорость ходьбы, клеток в секунду.</summary>
    [Export] public float WalkCellsPerSecond = 24f;

    /// <summary>Сколько секунд человек работает на одной клетке.</summary>
    [Export] public float WorkSeconds = 1.5f;

    /// <summary>Через сколько секунд без дела АВТО-человек уходит домой.</summary>
    [Export] public float IdleDespawnSeconds = 8f;

    /// <summary>Минимум между выходами новых людей (сек).</summary>
    [Export] public float SpawnIntervalSeconds = 0.4f;

    /// <summary>Сколько поисков пути делаем за кадр (A* дорогой — держим бюджет).</summary>
    [Export] public int PathSearchesPerFrame = 1;

    /// <summary>Через сколько секунд пробовать снова недостижимый чертёж (сек).</summary>
    [Export] public float UnreachableRetrySeconds = 30f;

    /// <summary>
    /// Разрешить переброску по морю: цель на СУШЕ, но отрезана водой. Люди перебрасываются
    /// абстрактно (лодок/плотов пока нет) — иначе архипелаг за кромкой недостижим пешком.
    /// </summary>
    [Export] public bool AllowSeaCrossing = true;

    /// <summary>Скорость переброски по морю, клеток в секунду (абстрактно).</summary>
    [Export] public float SeaTravelCellsPerSecond = 60f;

    private readonly List<WorldAgent> _agents = new();
    private readonly Dictionary<(long X, long Y), float> _unreachableUntil = new();
    private readonly List<(long X, long Y)> _pruneBuffer = new(16);
    private Func<WorldPlan, bool> _planFilter;
    private WorldJobBoard _board;
    private WorldPathFinder _finder;
    private StreamingWorldManager _manager;
    private StreamingWorldView _view;
    private float _boardTimer;
    private float _spawnTimer;
    private float _clock;
    private int _pathBudget;
    private long _focusX;
    private long _focusY;
    private bool _explicitFocus;
    private bool _manual;
    private int _renderCount = -1;
    private float _renderSignature;

    /// <summary>Люди мира (для рендера).</summary>
    public IReadOnlyList<WorldAgent> Agents => _agents;

    /// <summary>Рендер людей: перерисовку просит тикер, только когда что-то изменилось.</summary>
    public WorldAgentRenderer RenderTarget { get; set; }

    /// <summary>Сколько задач закрыто людьми.</summary>
    public int CompletedJobs { get; private set; }

    /// <summary>Сколько людей вышло за всё время.</summary>
    public int SpawnedTotal { get; private set; }

    /// <summary>Сколько задач оказались недостижимы (цель в воде/горах).</summary>
    public int UnreachableJobs { get; private set; }

    /// <summary>Сколько раз людей перебрасывали по морю (цель на суше, но отрезана водой).</summary>
    public int SeaCrossings { get; private set; }

    /// <summary>Сколько чертежей ждёт людей в загруженных чанках (для HUD).</summary>
    public int PendingPlans => _board?.PendingCount ?? 0;

    /// <summary>Сколько людей отправил игрок (0 — авто-режим).</summary>
    public int Dispatched { get; private set; }

    /// <summary>Отряд: сколько людей сейчас в мире.</summary>
    public int AgentsCount => _agents.Count;

    public void Setup(StreamingWorldView view, StreamingWorldManager manager, ulong seed, uint generatorVersion)
    {
        _view = view;
        _manager = manager;
        _board = new WorldJobBoard(view, manager);
        _finder = new WorldPathFinder(seed, generatorVersion);
        _planFilter = IsPlanAvailable; // делегат один раз, не аллокация в _Process
    }

    /// <summary>Задачу можно брать: недостижимые пропускаем, пока не истечёт повтор.</summary>
    private bool IsPlanAvailable(WorldPlan plan)
        => !_unreachableUntil.TryGetValue((plan.CellX, plan.CellY), out float until) || _clock >= until;

    /// <summary>Явный фокус (тесты/скрипты): иначе берём центр камеры.</summary>
    public void SetFocus(long cellX, long cellY)
    {
        _focusX = cellX;
        _focusY = cellY;
        _explicitFocus = true;
    }

    /// <summary>Сбросить явный фокус — снова следим за камерой (иначе фокус «замерзает» навсегда).</summary>
    public void ClearFocus()
    {
        _explicitFocus = false;
        ResolveCameraFocus();
    }

    /// <summary>
    /// Отправить людей в мир (API интерфейса игрока): с этого момента работают именно они,
    /// авто-выход выключается. Возвращает итоговое число отправленных.
    /// </summary>
    public int Dispatch(int count)
    {
        if (count <= 0)
            return Dispatched;
        _manual = true;
        Dispatched = Math.Clamp(Dispatched + count, 0, MaxAgents);
        _spawnTimer = 0f;
        return Dispatched;
    }

    /// <summary>
    /// Отозвать людей: лишние уйдут домой, закончив текущую задачу. Отзыв ВСЕХ оставляет
    /// ручной режим — авто-выход сам не возобновляется (для этого <see cref="ResumeAuto"/>).
    /// </summary>
    public int Recall(int count)
    {
        if (count <= 0)
            return Dispatched;
        Dispatched = Math.Max(0, Dispatched - count);
        return Dispatched;
    }

    /// <summary>Ручной режим (игрок отправлял людей сам): авто-выход выключен.</summary>
    public bool ManualMode => _manual;

    /// <summary>Вернуть авто-режим: люди выходят сами под свободную работу (до MaxAutoWorkers).</summary>
    public void ResumeAuto()
    {
        _manual = false;
        Dispatched = 0;
    }

    public override void _Process(double delta)
    {
        if (_view == null || _board == null)
            return;

        float dt = (float)delta;
        _clock += dt;
        _pathBudget = Mathf.Max(1, PathSearchesPerFrame);

        if (!_explicitFocus)
            ResolveCameraFocus();

        _boardTimer -= dt;
        if (_boardTimer <= 0f)
        {
            _boardTimer = BoardRefreshSeconds;
            _board.Refresh(_focusX, _focusY);
            PruneUnreachable();
        }

        TickAgents(dt);
        DispatchAgents(dt);
        FlushRenderDirty();
    }

    /// <summary>
    /// Перерисовка точек людей — только когда что-то реально изменилось (иначе рендер
    /// людей каждый кадр перерисовывался бы бесконечно, даже когда все стоят на месте).
    /// </summary>
    private void FlushRenderDirty()
    {
        if (RenderTarget == null)
            return;
        float signature = 0f;
        for (int i = 0; i < _agents.Count; i++)
        {
            WorldAgent agent = _agents[i];
            signature += agent.X * 3.1f + agent.Y * 7.7f + (int)agent.State;
        }
        if (_agents.Count == _renderCount && Mathf.IsEqualApprox(signature, _renderSignature))
            return;
        _renderCount = _agents.Count;
        _renderSignature = signature;
        RenderTarget.QueueRedraw();
    }

    /// <summary>Фокус по камере: клетка = позиция камеры / (64 px × масштаб вида).</summary>
    private void ResolveCameraFocus()
    {
        Camera2D camera = GetViewport()?.GetCamera2D();
        if (camera == null)
            return;
        Vector2 center = camera.GetScreenCenterPosition();
        float scale = _view.Scale.X;
        if (Mathf.Abs(scale) < 0.0001f)
            scale = 1f;
        _focusX = (long)Mathf.Floor(center.X / (MapRenderer.TileSizePx * scale));
        _focusY = (long)Mathf.Floor(center.Y / (MapRenderer.TileSizePx * scale));
    }

    private void TickAgents(float dt)
    {
        for (int i = _agents.Count - 1; i >= 0; i--)
        {
            WorldAgent agent = _agents[i];
            if ((agent.State == WorldAgentState.Walking || agent.State == WorldAgentState.SeaCrossing)
                && !_board.IsStillPlanned(agent.Job))
            {
                DropJob(agent); // чертёж сняли инструментом или чанк выгрузился
                continue;
            }
            switch (agent.State)
            {
                case WorldAgentState.Working:
                    agent.WorkLeft -= dt;
                    if (agent.WorkLeft <= 0f)
                        FinishJob(agent);
                    break;

                case WorldAgentState.Walking:
                    TickWalking(agent, dt);
                    break;

                case WorldAgentState.SeaCrossing:
                    agent.TravelLeft -= dt;
                    if (agent.TravelLeft <= 0f)
                    {
                        agent.X = agent.Job.CellX + 0.5f;
                        agent.Y = agent.Job.CellY + 0.5f;
                        agent.Path.Clear();
                        agent.PathIndex = 0;
                        agent.State = WorldAgentState.Working;
                        agent.WorkLeft = WorkSeconds;
                    }
                    break;

                default:
                    if (TickIdle(agent, dt))
                        _agents.RemoveAt(i);
                    break;
            }
        }
    }

    /// <summary>Свободный человек: отзыв → домой, иначе ищет задачу. true — удалить из мира.</summary>
    private bool TickIdle(WorldAgent agent, float dt)
    {
        agent.IdleTime += dt;
        // Отозванный (людей в мире больше, чем в отряде) уходит домой.
        if (_manual && _agents.Count > Dispatched)
            return true;
        // Авто-человек без дел возвращается; у отряда игрока дела ищутся всегда.
        if (!_manual && agent.IdleTime >= IdleDespawnSeconds)
            return true;
        TryAssign(agent);
        return false;
    }

    private void TickWalking(WorldAgent agent, float dt)
    {
        if (agent.NeedsPath)
        {
            if (_pathBudget <= 0)
                return; // подождём следующий кадр: A* дорогой
            _pathBudget--;
            BuildPath(agent);
            if (agent.State != WorldAgentState.Walking)
                return;
        }

        if (StepAlongPath(agent, dt))
        {
            agent.State = WorldAgentState.Working;
            agent.WorkLeft = WorkSeconds;
        }
    }

    /// <summary>
    /// Путь к чертежу: пешком (A* обходит воду и горы), иначе переброска по морю,
    /// если цель на суше но отрезана; цель в воде/горах — недостижима.
    /// </summary>
    private void BuildPath(WorldAgent agent)
    {
        agent.NeedsPath = false;
        long startX = (long)Mathf.Floor(agent.X);
        long startY = (long)Mathf.Floor(agent.Y);
        bool targetOnLand = _finder.Walkable(agent.Job.CellX, agent.Job.CellY);
        if (targetOnLand
            && _finder.TryFindPath(startX, startY, agent.Job.CellX, agent.Job.CellY, agent.Path)
            && agent.Path.Count > 0)
        {
            agent.X = agent.Path[0].X + 0.5f;
            agent.Y = agent.Path[0].Y + 0.5f;
            agent.PathIndex = agent.Path.Count > 1 ? 1 : 0;
            return;
        }

        if (targetOnLand && AllowSeaCrossing)
        {
            float dx = agent.Job.CellX + 0.5f - agent.X;
            float dy = agent.Job.CellY + 0.5f - agent.Y;
            float distance = Mathf.Sqrt(dx * dx + dy * dy);
            agent.State = WorldAgentState.SeaCrossing;
            agent.TravelLeft = Mathf.Max(0.25f, distance / Mathf.Max(1f, SeaTravelCellsPerSecond));
            SeaCrossings++;
            return;
        }

        _unreachableUntil[(agent.Job.CellX, agent.Job.CellY)] = _clock + UnreachableRetrySeconds;
        UnreachableJobs++;
        DropJob(agent);
    }

    /// <summary>Шаг по пути. true — дошёл до цели.</summary>
    private bool StepAlongPath(WorldAgent agent, float dt)
    {
        // Ноль/минус в экспорте не должен «залипать»: человек вечно стоял бы в Walking.
        float step = Mathf.Max(0.01f, WalkCellsPerSecond) * dt;
        while (step > 0f)
        {
            if (agent.PathIndex >= agent.Path.Count)
                return true;

            (long X, long Y) waypoint = agent.Path[agent.PathIndex];
            float targetX = waypoint.X + 0.5f;
            float targetY = waypoint.Y + 0.5f;
            float dx = targetX - agent.X;
            float dy = targetY - agent.Y;
            float distance = Mathf.Sqrt(dx * dx + dy * dy);
            if (distance <= step || distance <= 0.001f)
            {
                agent.X = targetX;
                agent.Y = targetY;
                step -= distance;
                agent.PathIndex++;
                continue;
            }
            agent.X += dx / distance * step;
            agent.Y += dy / distance * step;
            step = 0f;
        }
        return agent.PathIndex >= agent.Path.Count;
    }

    private void DropJob(WorldAgent agent)
    {
        _board.Release(agent.Job);
        agent.State = WorldAgentState.Idle;
        agent.IdleTime = 0f;
        agent.Path.Clear();
        agent.PathIndex = 0;
        agent.NeedsPath = false;
    }

    private void TryAssign(WorldAgent agent)
    {
        if (!_board.TryTakeNearest(agent.X, agent.Y, out WorldPlan plan, _planFilter))
            return;
        agent.Job = plan;
        agent.State = WorldAgentState.Walking;
        agent.NeedsPath = true;
        agent.Path.Clear();
        agent.PathIndex = 0;
        agent.IdleTime = 0f;
    }

    private void FinishJob(WorldAgent agent)
    {
        if (_board.Complete(agent.Job))
            CompletedJobs++;
        agent.State = WorldAgentState.Idle;
        agent.IdleTime = 0f;
        agent.Path.Clear();
        agent.PathIndex = 0;
    }

    /// <summary>
    /// Вызвать людей: отряд игрока (<see cref="Dispatch"/>) или авто-выход, пока никто не отправлен.
    /// </summary>
    private void DispatchAgents(float dt)
    {
        _spawnTimer -= dt;
        if (_spawnTimer > 0f || _agents.Count >= MaxAgents)
            return;

        if (_manual)
        {
            // Отряд игрока выходит САМ, даже если задач сейчас нет: это люди, а не подрядчики.
            // Dispatched == 0 после отзыва всех — никого не выпускаем (авто не возобновляем).
            if (_agents.Count >= Dispatched)
                return;
            _spawnTimer = SpawnIntervalSeconds;
            _agents.Add(CreateAgent(_focusX, _focusY));
            SpawnedTotal++;
            return;
        }

        // Авто-режим: выходим только под свободную работу.
        if (_agents.Count >= MaxAutoWorkers || !_board.HasFreeWork)
            return;
        _spawnTimer = SpawnIntervalSeconds;

        if (!_board.TryTakeNearest(_focusX, _focusY, out WorldPlan plan, _planFilter))
            return;
        _board.Release(plan);

        // Люди живут на острове: выходят с кромки у фокуса игрока и идут к работе сами
        // (раньше авто-человек материализовался прямо на клетке дальнего чертежа).
        _agents.Add(CreateAgent(_focusX, _focusY));
        SpawnedTotal++;
    }

    /// <summary>Человек выходит с ближайшей к указанной клетке кромки острова (люди живут на острове).</summary>
    private static WorldAgent CreateAgent(long cellX, long cellY) => new WorldAgent
    {
        X = Mathf.Clamp(cellX, 0, MapRenderer.MapWidth - 1) + 0.5f,
        Y = Mathf.Clamp(cellY, 0, MapRenderer.MapHeight - 1) + 0.5f,
        State = WorldAgentState.Idle
    };

    /// <summary>Забыть старые пометки недостижимости (иначе словарь растёт вечно).</summary>
    private void PruneUnreachable()
    {
        if (_unreachableUntil.Count == 0)
            return;
        _pruneBuffer.Clear();
        foreach (KeyValuePair<(long X, long Y), float> pair in _unreachableUntil)
        {
            if (_clock >= pair.Value)
                _pruneBuffer.Add(pair.Key);
        }
        for (int i = 0; i < _pruneBuffer.Count; i++)
            _unreachableUntil.Remove(_pruneBuffer[i]);
    }
}
