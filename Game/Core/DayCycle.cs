using System;

namespace River.Core;

/// <summary>Plain RGB colour (Core has no Godot types).</summary>
public readonly record struct Rgb(float R, float G, float B)
{
    public static Rgb Lerp(Rgb a, Rgb b, float t) => new(a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t);
    public static Rgb operator *(Rgb a, float k) => new(a.R * k, a.G * k, a.B * k);
}

/// <summary>
/// Game time: day, hour, season. One in-game day lasts <see cref="DayLengthMinutes"/> real minutes
/// (sandbox Time.DayLengthMinutes); a year is four seasons of <see cref="SeasonLengthDays"/> days.
/// </summary>
public sealed class GameClock
{
    public static readonly string[] Seasons = { "spring", "summer", "autumn", "winter" };

    public float DayLengthMinutes = 60f;
    public int SeasonLengthDays = 20;
    public int StartSeason;               // index into Seasons
    public double TotalDays;              // days since the journey began (fractional), starts at the start hour
    public float TimeScale = 1f;          // extra speed-up (debug / sleeping)

    public GameClock(float dayLengthMinutes, int seasonLengthDays, string startSeason, float startHour)
    {
        DayLengthMinutes = dayLengthMinutes;
        SeasonLengthDays = Math.Max(1, seasonLengthDays);
        StartSeason = Math.Max(0, Array.IndexOf(Seasons, startSeason));
        TotalDays = startHour / 24.0;
    }

    public void Advance(double realSeconds) => TotalDays += realSeconds * TimeScale / (DayLengthMinutes * 60.0);

    public int Day => (int)Math.Floor(TotalDays) + 1;                       // 1-based
    public float Hour => (float)((TotalDays - Math.Floor(TotalDays)) * 24.0);
    int SeasonOffsetDays => StartSeason * SeasonLengthDays;
    public int SeasonIndex => (int)(((Math.Floor(TotalDays) + SeasonOffsetDays) / SeasonLengthDays) % 4);
    public string Season => Seasons[SeasonIndex];
    public int DayOfSeason => (int)((Math.Floor(TotalDays) + SeasonOffsetDays) % SeasonLengthDays) + 1;

    /// <summary>
    /// Calendar day of year (0..365) for the sun's path: spring starts at the March equinox (day 79) and the
    /// game year is stretched over a real one.
    /// </summary>
    public float DayOfYear
    {
        get
        {
            double yearLen = 4.0 * SeasonLengthDays;
            double t = ((TotalDays + SeasonOffsetDays) % yearLen) / yearLen;
            return (float)((79.0 + t * 365.0) % 365.0);
        }
    }

    public void SetHour(float hour)
    {
        double day = Math.Floor(TotalDays);
        double target = day + hour / 24.0;
        if (target < TotalDays) target += 1.0;   // time only moves forward
        TotalDays = target;
    }

    public string Clock => $"{(int)Hour:00}:{(int)(Hour % 1f * 60f):00}";
}

/// <summary>Sun and moon positions (simple, good-enough astronomy: declination + hour angle).</summary>
public static class Solar
{
    /// <summary>
    /// Unit vector towards the sun in east/north/up components, for a latitude (degrees, + north),
    /// a day of year and a local solar hour (12 = noon).
    /// </summary>
    public static (float east, float north, float up) SunDirection(float latitudeDeg, float dayOfYear, float hour)
    {
        float decl = Deg(-23.44f) * MathF.Cos(MathF.PI * 2f * (dayOfYear + 10f) / 365f);
        return Direction(Deg(latitudeDeg), decl, Deg(15f * (hour - 12f)));
    }

    /// <summary>Roughly opposite the sun (a moon near full), riding high in winter nights, low in summer ones.</summary>
    public static (float east, float north, float up) MoonDirection(float latitudeDeg, float dayOfYear, float hour)
    {
        float decl = -Deg(-23.44f) * MathF.Cos(MathF.PI * 2f * (dayOfYear + 10f) / 365f) * 0.9f;
        return Direction(Deg(latitudeDeg), decl, Deg(15f * (hour - 12f) + 180f + 12f));
    }

    static (float, float, float) Direction(float lat, float decl, float h)
    {
        float east = -MathF.Cos(decl) * MathF.Sin(h);
        float north = MathF.Sin(decl) * MathF.Cos(lat) - MathF.Cos(decl) * MathF.Cos(h) * MathF.Sin(lat);
        float up = MathF.Sin(decl) * MathF.Sin(lat) + MathF.Cos(decl) * MathF.Cos(h) * MathF.Cos(lat);
        return (east, north, up);
    }

    public static float ElevationDeg(float up) => MathF.Asin(Math.Clamp(up, -1f, 1f)) * 180f / MathF.PI;
    static float Deg(float d) => d * MathF.PI / 180f;
}

/// <summary>
/// Lighting as a function of the sun's elevation (so a dawn looks like a dawn in every season), whether the sun is
/// rising (morning mist), and how overcast it is. All values blend smoothly; nothing switches abruptly.
/// </summary>
public static class Daylight
{
    public struct State
    {
        public Rgb SunColor; public float SunEnergy;      // the directional light while the sun is up
        public float MoonEnergy;                          // the same light used as moonlight at night
        public Rgb SkyTop, SkyHorizon;
        public float SkyEnergy;
        public float Exposure;
        public float FogDensity;                          // volumetric fog
        public Rgb FogAlbedo;
        public float Mist;                                // river mist density
        public float Night;                               // 0 day … 1 full night (stars, lanterns)
        public bool UseMoon;                              // the light should follow the moon
    }

    // Keyframes over sun elevation (degrees).
    static readonly (float e, Rgb c)[] SunColors =
    {
        (-2f, new(1f, 0.40f, 0.18f)), (2f, new(1f, 0.52f, 0.26f)), (6f, new(1f, 0.70f, 0.46f)),
        (15f, new(1f, 0.87f, 0.72f)), (35f, new(1f, 0.96f, 0.90f)), (90f, new(1f, 0.99f, 0.96f)),
    };
    static readonly (float e, float v)[] SunEnergies = { (-3f, 0f), (1f, 0.6f), (5f, 1.4f), (15f, 2.1f), (40f, 2.7f), (90f, 2.9f) };
    static readonly (float e, Rgb c)[] Tops =
    {
        (-18f, new(0.006f, 0.009f, 0.022f)), (-9f, new(0.03f, 0.045f, 0.10f)), (-3f, new(0.14f, 0.18f, 0.32f)),
        (2f, new(0.30f, 0.38f, 0.58f)), (12f, new(0.31f, 0.47f, 0.73f)), (40f, new(0.24f, 0.44f, 0.78f)), (90f, new(0.22f, 0.42f, 0.78f)),
    };
    static readonly (float e, Rgb c)[] Horizons =
    {
        (-18f, new(0.012f, 0.016f, 0.03f)), (-9f, new(0.06f, 0.06f, 0.10f)), (-3f, new(0.45f, 0.30f, 0.30f)),
        (1f, new(0.96f, 0.62f, 0.40f)), (8f, new(0.93f, 0.80f, 0.66f)), (25f, new(0.76f, 0.80f, 0.85f)), (90f, new(0.70f, 0.78f, 0.86f)),
    };
    static readonly (float e, float v)[] SkyEnergies = { (-18f, 0.35f), (-6f, 0.6f), (0f, 0.9f), (10f, 1f), (90f, 1f) };
    static readonly (float e, float v)[] Exposures = { (-18f, 1.9f), (-6f, 1.5f), (0f, 1.15f), (15f, 1.0f), (60f, 0.9f), (90f, 0.9f) };

    public static State Evaluate(float sunElevation, float moonElevation, bool rising, float overcast)
    {
        var s = new State();
        float e = sunElevation;
        s.SunColor = Curve(SunColors, e);
        s.SunEnergy = Curve(SunEnergies, e);
        s.SkyTop = Curve(Tops, e);
        s.SkyHorizon = Curve(Horizons, e);
        s.SkyEnergy = Curve(SkyEnergies, e);
        s.Exposure = Curve(Exposures, e);
        s.Night = 1f - MathUtil.Smoothstep(-12f, -2f, e);
        s.UseMoon = e < -2f;
        s.MoonEnergy = s.UseMoon ? 0.12f * MathUtil.Smoothstep(-2f, 10f, moonElevation) * MathUtil.Smoothstep(-2f, -8f, e) : 0f;

        // Mist: thick from late night through sunrise, burning off by mid-morning; a thinner veil in the evening.
        float morning = 1f - MathUtil.Smoothstep(-4f, 22f, e);
        float evening = (1f - MathUtil.Smoothstep(-2f, 10f, e)) * 0.4f;
        s.Mist = 0.085f * (rising ? MathF.Max(morning, evening) : evening);
        s.FogDensity = 0.0005f + 0.0011f * (rising ? morning : evening);
        s.FogAlbedo = Rgb.Lerp(new Rgb(0.92f, 0.90f, 0.88f), new Rgb(0.35f, 0.38f, 0.45f), s.Night);

        // Overcast: grey sky, weak diffuse sun, more fog.
        if (overcast > 0f)
        {
            float o = Math.Clamp(overcast, 0f, 1f);
            float lum = 0.62f * MathUtil.Smoothstep(-8f, 15f, e) + 0.02f;
            var grey = new Rgb(lum, lum * 1.02f, lum * 1.05f);
            s.SkyTop = Rgb.Lerp(s.SkyTop, grey * 0.85f, o);
            s.SkyHorizon = Rgb.Lerp(s.SkyHorizon, grey, o);
            s.SunColor = Rgb.Lerp(s.SunColor, new Rgb(0.85f, 0.88f, 0.92f), o);
            s.SunEnergy *= 1f - 0.78f * o;
            s.MoonEnergy *= 1f - 0.8f * o;
            s.FogDensity += 0.0012f * o;
            s.Mist += 0.02f * o;
            s.Exposure += 0.25f * o;
        }
        return s;
    }

    static float Curve((float e, float v)[] k, float e)
    {
        if (e <= k[0].e) return k[0].v;
        for (int i = 1; i < k.Length; i++)
            if (e <= k[i].e) return MathUtil.Lerp(k[i - 1].v, k[i].v, MathUtil.Smoothstep(k[i - 1].e, k[i].e, e));
        return k[^1].v;
    }

    static Rgb Curve((float e, Rgb c)[] k, float e)
    {
        if (e <= k[0].e) return k[0].c;
        for (int i = 1; i < k.Length; i++)
            if (e <= k[i].e) return Rgb.Lerp(k[i - 1].c, k[i].c, MathUtil.Smoothstep(k[i - 1].e, k[i].e, e));
        return k[^1].c;
    }
}
