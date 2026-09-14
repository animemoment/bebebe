using Godot;
using Game.Simulation;

namespace Game.UI;

/// <summary>
/// Смена суток: один <see cref="CanvasModulate"/> на весь мир, цвет = f(игровое время).
/// O(1) для GPU: один uniform на canvas, 0 draw-call, не зависит от числа
/// агентов/тайлов/MultiMesh-инстансов. Реальных Light2D-теней нет осознанно:
/// shadow-pass по 10k инстансов + TileMap 512x512 в gl_compatibility убил бы FPS.
/// «Тень» = ночное затемнение + холодный оттенок, рассвет/закат = тёплый.
/// Движок — <see cref="TimeManager.GameTimeSeconds"/> (авторитет — SimThread),
/// поэтому корректно на паузе и скоростях 1/5/25/100x. UI в CanvasLayer не тintится.
/// </summary>
public partial class DayNightCycle : Node
{
    // Ключи суток (игровые часы 0..24). Держатся на WorldTime.SecondsPerHour=500с.
    public const float NightEndHour = 5f;    // до этого — глубокая ночь
    public const float DawnHour = 6.5f;      // рассвет (тёплый)
    public const float DayStartHour = 8f;    // полный день
    public const float DayEndHour = 17f;     // конец полного дня
    public const float DuskHour = 18.5f;     // закат (оранжевый)
    public const float NightStartHour = 20f; // глубокая ночь

    public static readonly Color NightColor = new(0.23f, 0.27f, 0.46f);
    public static readonly Color DawnColor = new(1.0f, 0.78f, 0.58f);
    public static readonly Color DayColor = new(1.0f, 1.0f, 1.0f);
    public static readonly Color DuskColor = new(1.0f, 0.55f, 0.35f);

    // Солнце: дуга слева направо. Утром тень длинная влево, в полдень 0, вечером вправо.
    public const float SunriseHour = 6f;
    public const float SolarNoonHour = 13f;
    public const float SunsetHour = 20f;
    public const float MaxShadowLengthPx = 28f;
    public const float ShadowAlphaMax = 0.32f;
    public const float ShadowWidthScale = 0.55f;

    /// <summary>Как часто (реал. сек) можно трогать CanvasModulate.color. Дешевле некуда, но и дёргать каждый кадр незачем.</summary>
    public float UpdateIntervalRealSec = 0.1f;

    private CanvasModulate _modulate;
    private float _timer;
    private bool _appliedOnce;
    private Color _current = DayColor;

    /// <summary>Текущий применённый тинт (для отладки/оверлея).</summary>
    public Color CurrentTint => _current;

    public void Initialize(CanvasModulate modulate)
    {
        _modulate = modulate;
    }

    public override void _ExitTree()
    {
        _modulate = null;
        base._ExitTree();
    }

    /// <summary>
    /// Вызывать из Main._Process после SyncGameTime. Только главный поток (трогает Node).
    /// </summary>
    public void Tick(float gameTimeSeconds, float realDeltaSeconds)
    {
        if (_modulate == null || !IsInstanceValid(_modulate))
            return;
        _timer += realDeltaSeconds;
        if (_appliedOnce && _timer < UpdateIntervalRealSec)
            return;
        _timer = 0f;

        Color target = SampleTint(WorldTime.TimeOfDaySeconds(gameTimeSeconds));
        if (_appliedOnce && ColorDistanceSquared(_current, target) < 0.000012f) // ~0.002 на канал
            return;
        _current = target;
        _modulate.Color = target;
        _appliedOnce = true;
    }

    /// <summary>Ночь ли сейчас (для HUD-индикатора и будущего геймплея). Чистая функция.</summary>
    public static bool IsNight(float timeOfDaySeconds)
    {
        float h = timeOfDaySeconds / WorldTime.SecondsPerHour;
        return h < NightEndHour || h >= NightStartHour;
    }

    private static float ColorDistanceSquared(Color a, Color b)
    {
        float dr = a.R - b.R;
        float dg = a.G - b.G;
        float db = a.B - b.B;
        return dr * dr + dg * dg + db * db;
    }

    /// <summary>
    /// Тинт мира по времени суток [0, SecondsPerDay). Кусочно-линейная, без аллокаций.
    /// Ночь держится плоской, переходы рассвет/закат плавные.
    /// </summary>
    public static Color SampleTint(float timeOfDaySeconds)
    {
        float h = timeOfDaySeconds / WorldTime.SecondsPerHour;
        if (h < NightEndHour) return NightColor;
        if (h < DawnHour) return NightColor.Lerp(DawnColor, (h - NightEndHour) / (DawnHour - NightEndHour));
        if (h < DayStartHour) return DawnColor.Lerp(DayColor, (h - DawnHour) / (DayStartHour - DawnHour));
        if (h < DayEndHour) return DayColor;
        if (h < DuskHour) return DayColor.Lerp(DuskColor, (h - DayEndHour) / (DuskHour - DayEndHour));
        if (h < NightStartHour) return DuskColor.Lerp(NightColor, (h - DuskHour) / (NightStartHour - DuskHour));
        return NightColor;
    }

    /// <summary>
    /// Солнце по дуге слева направо: направление тени + длина + ширина + альфа.
    /// Утро: тень слева (-X), полдень: сверху, длина 0. Вечер: тень справа (+X).
    /// Тень идёт слева вправо вслед за солнцем. Ночью alpha=0 (тени гаснут).
    /// Тень растягивается от точки опоры: Dir — единичный вектор, LengthPx — длина полосы.
    /// Чистая функция, без аллокаций.
    /// </summary>
    public static SunState SampleSun(float timeOfDaySeconds)
    {
        float h = timeOfDaySeconds / WorldTime.SecondsPerHour;
        if (h < SunriseHour || h >= SunsetHour)
            return new SunState(Vector2.Zero, 0f, ShadowWidthScale, 0f);

        // Прогресс дня 0..1 (восход->полдень->закат).
        float dayT = (h - SunriseHour) / (SunsetHour - SunriseHour);
        // Высота солнца: sin-дуга, 0 на горизонте, 1 в полдень.
        // Полдень выравниваем на SolarNoonHour через асимметричный sin.
        float noonT = (SolarNoonHour - SunriseHour) / (SunsetHour - SunriseHour);
        float elev = dayT <= noonT
            ? Mathf.Sin(dayT / noonT * Mathf.Pi * 0.5f)
            : Mathf.Sin(Mathf.Pi * 0.5f + (dayT - noonT) / (1f - noonT) * Mathf.Pi * 0.5f);

        // Тень идёт слева направо вслед за солнцем: утром -X, вечером +X, в полдень 0.
        float azim = dayT * 2f - 1f;
        float len = MaxShadowLengthPx * (1f - elev);
        float alpha = ShadowAlphaMax * Mathf.Clamp(elev * 3f, 0f, 1f) * Mathf.Clamp((1f - elev) * 4f + 0.25f, 0f, 1f);
        if (len < 0.5f)
            return new SunState(Vector2.Zero, 0f, ShadowWidthScale, 0f);
        float inv = 1f / Mathf.Sqrt(azim * azim + 0.1225f);
        var dir = new Vector2(azim * inv, 0.35f * inv);
        return new SunState(dir, len, ShadowWidthScale, alpha);
    }

    /// <summary>Совместимость: offset+alpha для старого кода (близнецы). Новые рендеры используют <see cref="SunState"/>.</summary>
    public static (Vector2 Offset, float Alpha) SampleSunOffset(float timeOfDaySeconds)
    {
        var s = SampleSun(timeOfDaySeconds);
        return (s.Dir * s.LengthPx, s.Alpha);
    }

    /// <summary>Состояние солнца за тик: направление + длина + ширина тени и альфа.</summary>
    public readonly struct SunState
    {
        public readonly Vector2 Dir;
        public readonly float LengthPx;
        public readonly float WidthScale;
        public readonly float Alpha;

        public SunState(Vector2 dir, float lengthPx, float widthScale, float alpha)
        {
            Dir = dir;
            LengthPx = lengthPx;
            WidthScale = widthScale;
            Alpha = alpha;
        }
    }
}
