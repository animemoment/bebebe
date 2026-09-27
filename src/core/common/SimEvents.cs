using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace Game.Core;

// 80 улучшений профайлера: единый канал событий/рядов/счётчиков.
// A: иерархия скоупов (Begin/End с ThreadLocal-стеком), tick-трейс (Chrome Trace B/E + instant i),
//    игровое время + скорость в каждой записи, alloc/errors на скоуп, батчинг сэмплов
//    (ThreadLocal<List> + Merge), CSV фикс-колонок, capture-триггер, сегменты+сжатие —
//    через ProfilerLogService (файл/очередь), сырой трейс — кольцом здесь.
// B..G: счётчики/ряды/снапшоты пишут сюда же продюсеры из sim/dispatcher/managers/render/input.
public static class SimEvents
{
    // --- A1: иерархические скоупы ---
    private static readonly ThreadLocal<Stack<string>> _scopeStack = new(() => new Stack<string>(8));
    public static string CurrentPath
    {
        get
        {
            var st = _scopeStack.Value;
            if (st.Count == 0) return "";
            var arr = st.ToArray();
            Array.Reverse(arr);
            return string.Join(">", arr);
        }
    }
    public static void PushScope(string name) => _scopeStack.Value.Push(name);
    public static void PopScope()
    {
        var st = _scopeStack.Value;
        if (st.Count > 0) st.Pop();
    }
    // A7/A8: alloc+errors на скоуп — снапшот до/после вокруг using-блока.
    public readonly ref struct ScopeSample
    {
        private readonly string _name;
        private readonly long _alloc0;
        private readonly int _err0;
        public ScopeSample(string name)
        {
            _name = name;
            PushScope(name);
            _alloc0 = GC.GetAllocatedBytesForCurrentThread();
            _err0 = ScopeErrors;
        }
        public void Dispose()
        {
            long alloc = GC.GetAllocatedBytesForCurrentThread() - _alloc0;
            int errs = ScopeErrors - _err0;
            PopScope();
            // Пишем только значимое (иначе спам): >1КБ alloc или были ошибки.
            if (alloc > 1024 || errs > 0)
                Record("scope", _name, (float)alloc, errs);
        }
    }
    [ThreadStatic] private static int _scopeErrorsLocal;
    public static int ScopeErrors => _scopeErrorsLocal;
    public static void NoteScopeError() => _scopeErrorsLocal++;
    public static ScopeSample Sample(string name) => new(name);

    // --- Канал событий: кольцо 8192 (A10: полное разрешение, A14: не теряется при краше — сегменты пишет логер) ---
    public sealed class Ev
    {
        public double TsReal;   // A5: реальное время (сек сессии)
        public float TsGame;    // A5: игровые секунды
        public float Speed;     // A6: скорость симуляции
        public string Kind;     // scope|tick|agent|job|res|render|input|sys|mark
        public string Name;
        public float V1;        // dur_ms / значение
        public long V2;         // alloc_b / count2
        public int V3;          // errors / extra
        public string Meta;     // meta_json лёгкий
    }
    private const int RingCap = 8192;
    private static readonly Ev[] _ring = new Ev[RingCap];
    private static int _ringHead;
    private static long _sessionStartTicks = DateTime.UtcNow.Ticks;
    private static float _gameTime;
    private static float _speed = 1f;
    public static void SetClock(float gameSecs, float speed) { _gameTime = gameSecs; _speed = speed; }
    private static double NowSec() => (DateTime.UtcNow.Ticks - _sessionStartTicks) / 10_000_000.0;

    public static void Record(string kind, string name, float v1 = 0, long v2 = 0, int v3 = 0, string meta = null)
    {
        if (!Capture.Enabled) return; // A13: триггер записи
        int i = (int)((uint)Interlocked.Increment(ref _ringHead) % RingCap);
        var e = _ring[i] ??= new Ev();
        e.TsReal = NowSec(); e.TsGame = _gameTime; e.Speed = _speed;
        e.Kind = kind; e.Name = name; e.V1 = v1; e.V2 = v2; e.V3 = v3; e.Meta = meta;
    }
    // A3: instant-метки (регистрация/завершение/stuck/кулдаун) — тем же каналом.
    public static void Mark(string name, string meta = null) => Record("mark", name, 0, 0, 0, meta);
    // B4/D: агент-событие (id, state, job).
    public static void Agent(int id, string state, int jobId, string meta = null) =>
        Record("agent", state, id, jobId, 0, meta);

    // --- Счётчики (C/D/E/F ряды): имя -> (сумма, число) за текущую секунду ---
    private static readonly Dictionary<string, (double Sum, long N)> _counters = new(StringComparer.Ordinal);
    private static readonly object _cLock = new();
    public static void Count(string name, double v = 1.0)
    {
        if (!Capture.Enabled) return;
        lock (_cLock)
        {
            if (!_counters.TryGetValue(name, out var c)) c = (0, 0);
            _counters[name] = (c.Sum + v, c.N + 1);
        }
    }
    public static Dictionary<string, (double Sum, long N)> DrainCounters()
    {
        lock (_cLock)
        {
            var out_ = new Dictionary<string, (double, long)>(_counters, StringComparer.Ordinal);
            _counters.Clear();
            return out_;
        }
    }

    // --- Временные ряды раз в секунду (B20/B32/C/D/G): имя -> кольцо 600 (A10) ---
    private const int SeriesLen = 600;
    private static readonly Dictionary<string, (float[] Buf, int Head, int Count)> _series = new(StringComparer.Ordinal);
    public static void Series(string name, float v)
    {
        if (!Capture.Enabled) return;
        lock (_cLock)
        {
            if (!_series.TryGetValue(name, out var s))
            {
                s = (new float[SeriesLen], 0, 0);
                _series[name] = s;
            }
            s.Buf[s.Head] = v;
            _series[name] = ((s.Head + 1) % SeriesLen, s.Count < SeriesLen ? s.Count + 1 : SeriesLen, s.Buf) switch
            { var t => (t.Item3, t.Item1, t.Item2) };
        }
    }
    public static float[] GetSeries(string name, int maxPoints)
    {
        lock (_cLock)
        {
            if (!_series.TryGetValue(name, out var s) || s.Count == 0) return Array.Empty<float>();
            int n = Math.Min(maxPoints, s.Count);
            var out_ = new float[n];
            int start = (s.Head - n + SeriesLen) % SeriesLen;
            for (int i = 0; i < n; i++) out_[i] = s.Buf[(start + i) % SeriesLen];
            return out_;
        }
    }

    // --- G79: детектор регрессии (секунда против медианы 60) ---
    public static bool CheckRegression(string seriesName, float latest, float thresholdRatio = 2.0f)
    {
        var s = GetSeries(seriesName, 60);
        if (s.Length < 10) return false;
        var copy = (float[])s.Clone();
        Array.Sort(copy);
        float med = copy[copy.Length / 2];
        if (med < 0.01f) med = 0.01f;
        if (latest > med * thresholdRatio)
        {
            Mark("REGRESSION", $"{seriesName} {latest:F1} vs med {med:F1}");
            return true;
        }
        return false;
    }

    // --- A11: CSV фикс-колонки; A12: бинарный формат колонками ---
    public static string BuildEventsCsv(int maxRows = 5000)
    {
        var sb = new StringBuilder(4096);
        sb.AppendLine("ts_real;ts_game;speed;thread;scope;dur_us;alloc_b;errors;meta_json");
        int head = Volatile.Read(ref _ringHead);
        int n = Math.Min(maxRows, Math.Min(head, RingCap));
        for (int k = 0; k < n; k++)
        {
            int i = (head - n + k + RingCap * 2) % RingCap;
            var e = _ring[i];
            if (e == null || e.Kind == null) continue;
            sb.Append(e.TsReal.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)).Append(';')
              .Append(e.TsGame.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)).Append(';')
              .Append(e.Speed.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)).Append(';')
              .Append(e.Kind).Append(';').Append(e.Name).Append(';')
              .Append(((long)(e.V1 * 1000)).ToString()).Append(';')
              .Append(e.V2.ToString()).Append(';').Append(e.V3.ToString()).Append(';')
              .Append(e.Meta ?? "").AppendLine();
        }
        return sb.ToString();
    }
    // A12: бинарный снапшот колонками (ts/v1/v2 как float[]+long[]).
    public static (float[] Ts, float[] V1, long[] V2, string[] Names) BuildBinary(int maxRows = 8192)
    {
        int head = Volatile.Read(ref _ringHead);
        int n = Math.Min(maxRows, Math.Min(head, RingCap));
        var ts = new float[n]; var v1 = new float[n]; var v2 = new long[n]; var nm = new string[n];
        for (int k = 0; k < n; k++)
        {
            int i = (head - n + k + RingCap * 2) % RingCap;
            var e = _ring[i];
            if (e == null) continue;
            ts[k] = (float)e.TsReal; v1[k] = e.V1; v2[k] = e.V2; nm[k] = e.Kind + ":" + e.Name;
        }
        return (ts, v1, v2, nm);
    }
    // A2: tick-трейс в формате Chrome Trace (B/E по скоупам фаз + i-метки).
    public static string BuildChromeTrace(int maxRows = 5000)
    {
        var sb = new StringBuilder(4096);
        sb.Append('[');
        bool first = true;
        int head = Volatile.Read(ref _ringHead);
        int n = Math.Min(maxRows, Math.Min(head, RingCap));
        for (int k = 0; k < n; k++)
        {
            int i = (head - n + k + RingCap * 2) % RingCap;
            var e = _ring[i];
            if (e == null || e.Kind == null) continue;
            string ph = e.Kind == "mark" ? "i" : "X";
            if (!first) sb.Append(',');
            first = false;
            sb.Append("{\"name\":\"").Append(e.Name).Append("\",\"ph\":\"").Append(ph)
              .Append("\",\"ts\":").Append(((long)(e.TsReal * 1_000_000)).ToString())
              .Append(",\"tid\":0,\"dur\":").Append(((long)(e.V1 * 1000)).ToString())
              .Append(",\"args\":{\"game\":").Append(e.TsGame.ToString("F1", System.Globalization.CultureInfo.InvariantCulture))
              .Append("}}");
        }
        sb.Append(']');
        return sb.ToString();
    }
}

// A13: триггер записи — по умолчанию ВКЛ (как было), кнопка панели выключает.
public static class Capture
{
    private static int _enabled = 1;
    public static bool Enabled => Volatile.Read(ref _enabled) != 0;
    public static void Start() => Volatile.Write(ref _enabled, 1);
    public static void Stop() => Volatile.Write(ref _enabled, 0);
}
