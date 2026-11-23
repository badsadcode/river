using System;
using Godot;
using River.Core;

namespace River.World;

/// <summary>
/// Sky, sun/moon, fog, river mist and stars, driven by the game clock (day/night cycle).
/// The sun follows a real path for the latitude and season (Core/DayCycle.cs); colours, fog and mist follow the
/// sun's elevation, so mornings start misty and the mist burns off. Lanterns (group "night_lights") come on at dusk.
/// Mood target: soft diffuse light, morning mist on the water (docs/DESIGN_PILLARS.md §11).
/// </summary>
public partial class EnvironmentRig : Node3D
{
    public DirectionalLight3D Sun;          // the sun by day, the moon by night
    public WorldEnvironment WorldEnv;
    public GameClock Clock;
    public float Latitude = 52f;            // degrees north; later: falls as the journey heads for the tropics
    public float Overcast;                  // 0 clear … 1 grey
    public float SunElevation { get; private set; }
    public Daylight.State Light { get; private set; }

    /// <summary>The live river materials (one per loaded segment), for the night roughness.</summary>
    public readonly System.Collections.Generic.List<ShaderMaterial> Waters = new();
    /// <summary>The live mist volumes (one per loaded segment), for density and colour.</summary>
    readonly System.Collections.Generic.List<(FogVolume vol, ShaderMaterial mat)> _mists = new();
    public bool MistHidden;                 // --nomist (captures)
    NoiseTexture3D _mistNoise;
    ProceduralSkyMaterial _sky;
    float _skyTimer;

    // Jump-to times for T (and for screenshots: "shot@N").
    // Hours are for the equinox at 52°N (sunrise ≈ 06:05, sunset ≈ 18:10): just after sunrise, mid-morning, noon,
    // low evening sun, overcast late morning, midnight.
    public static readonly string[] PresetNames = { "dawn mist", "morning", "noon", "golden evening", "overcast", "night" };
    static readonly float[] PresetHours = { 6.4f, 8.5f, 12.5f, 17.4f, 11f, 23.5f };
    int _preset;
    static readonly float[] TimeScales = { 1f, 10f, 60f, 360f };
    int _timeScale;

    public string PresetName => TimeLabel;
    public string TimeLabel => $"day {Clock.Day} · {Clock.Season} · {Clock.Clock}{(Overcast > 0.5f ? " · overcast" : "")}{(Clock.TimeScale > 1f ? $" · ×{Clock.TimeScale:0}" : "")}";

    public void Setup(int seed, SandboxSettings sandbox)
    {
        var t = sandbox?.Time ?? new SandboxSettings.TimeGroup();
        Clock = new GameClock(t.DayLengthMinutes, t.SeasonLengthDays, t.StartingSeason, PresetHours[0]);

        // Procedural sky: art-directable colours; stars painted in as the "sky cover" texture, faded in at night.
        _sky = new ProceduralSkyMaterial
        {
            SunAngleMax = 22f, SunCurve = 0.12f, GroundBottomColor = new Color(0.16f, 0.17f, 0.13f),
            UseDebanding = true, SkyCover = StarTexture(2048, 1024, seed), SkyCoverModulate = new Color(1, 1, 1, 0),
        };
        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Sky,
            Sky = new Sky { SkyMaterial = _sky, ProcessMode = Sky.ProcessModeEnum.Incremental, RadianceSize = Sky.RadianceSizeEnum.Size256 },
            AmbientLightSource = Godot.Environment.AmbientSource.Sky,
            ReflectedLightSource = Godot.Environment.ReflectionSource.Sky,
            AmbientLightSkyContribution = 1f,
            TonemapMode = Godot.Environment.ToneMapper.Agx,
            TonemapExposure = 1.0f,
            SsaoEnabled = true, SsaoRadius = 1.6f, SsaoIntensity = 1.4f,
            GlowEnabled = true, GlowIntensity = 0.35f, GlowBloom = 0.04f,
            FogEnabled = true, FogDensity = 0.00012f, FogAerialPerspective = 0.75f, FogSkyAffect = 0.25f,
            VolumetricFogEnabled = true, VolumetricFogDensity = 0.0025f, VolumetricFogAnisotropy = 0.55f,
            VolumetricFogLength = 260f, VolumetricFogDetailSpread = 2.2f, VolumetricFogAmbientInject = 0.6f,
            AdjustmentEnabled = true, AdjustmentSaturation = 1.04f, AdjustmentContrast = 1.03f,
        };
        WorldEnv = new WorldEnvironment { Environment = env };
        AddChild(WorldEnv);

        Sun = new DirectionalLight3D
        {
            Name = "Sun", ShadowEnabled = true,
            DirectionalShadowMode = DirectionalLight3D.ShadowMode.Parallel4Splits,
            DirectionalShadowMaxDistance = 420f, LightAngularDistance = 0.6f,
            ShadowBias = 0.06f, ShadowNormalBias = 2.6f, LightVolumetricFogEnergy = 1.2f,
        };
        AddChild(Sun);

        _mistNoise = new NoiseTexture3D
        {
            Width = 64, Height = 32, Depth = 64, Seamless = true,
            Noise = new FastNoiseLite { Frequency = 0.06f, FractalOctaves = 3, Seed = seed },
        };
        UpdateLighting(true);
    }

    /// <summary>
    /// A mist volume over one segment (a rotated box hugging its water; Mist.gdshader reads the water level from the
    /// segment's field). Returned for the caller to place in the segment's node; registered for density updates.
    /// </summary>
    public FogVolume CreateMist(ReachField f, FieldTextures tex)
    {
        float etaMin = float.MaxValue, etaMax = float.MinValue;
        for (int i = 0; i < f.Eta.Length; i++) if (f.Water[i]) { etaMin = MathF.Min(etaMin, f.Eta[i]); etaMax = MathF.Max(etaMax, f.Eta[i]); }
        if (etaMin > etaMax) { etaMin = 0f; etaMax = 0f; }
        var mat = new ShaderMaterial { Shader = GD.Load<Shader>("res://Game/Shaders/Mist.gdshader") };
        tex.Apply(mat);
        mat.SetShaderParameter("noise3d", _mistNoise);
        mat.SetShaderParameter("frame_origin", new Vector2((float)f.Frame.OriginX, (float)f.Frame.OriginZ));
        mat.SetShaderParameter("frame_rot", new Vector2(MathF.Cos(f.Frame.Heading), MathF.Sin(f.Frame.Heading)));
        float h = etaMax - etaMin + 30f;
        var vol = new FogVolume
        {
            Name = "RiverMist", Shape = RenderingServer.FogVolumeShape.Box,
            Size = new Vector3(f.SizeX, h, f.SizeZ), Material = mat,
            Position = new Vector3(f.SizeX * 0.5f, etaMin - 2f + h * 0.5f, f.SizeZ * 0.5f),
        };
        lock (_mists) _mists.Add((vol, mat));
        return vol;
    }

    public void ForgetMist(FogVolume vol) { lock (_mists) _mists.RemoveAll(m => m.vol == vol); }

    // ------------------------------------------------------------------ controls

    /// <summary>T: jump forward to the next notable time of day.</summary>
    public void NextPreset() => ApplyPreset((_preset + 1) % PresetNames.Length);

    public void ApplyPreset(int p)
    {
        _preset = p;
        Overcast = p == 4 ? 1f : 0f;
        Clock.SetHour(PresetHours[p]);
        UpdateLighting(true);
    }

    public void ToggleOvercast() { Overcast = Overcast > 0.5f ? 0f : 1f; UpdateLighting(true); }

    public void ChangeTimeScale(int dir)
    {
        _timeScale = Math.Clamp(_timeScale + dir, 0, TimeScales.Length - 1);
        Clock.TimeScale = TimeScales[_timeScale];
    }

    // ------------------------------------------------------------------ per frame

    public override void _Process(double delta)
    {
        if (Clock == null) return;
        Clock.Advance(delta);
        _skyTimer -= (float)delta;
        UpdateLighting(_skyTimer <= 0f);
    }

    void UpdateLighting(bool updateSky)
    {
        float hour = Clock.Hour, doy = Clock.DayOfYear;
        var sun = Solar.SunDirection(Latitude, doy, hour);
        var moon = Solar.MoonDirection(Latitude, doy, hour);
        SunElevation = Solar.ElevationDeg(sun.up);
        var st = Daylight.Evaluate(SunElevation, Solar.ElevationDeg(moon.up), hour < 12f, Overcast);
        Light = st;

        // Godot: east = +x, north = −z. The light points from the body towards the ground.
        var d = st.UseMoon ? moon : sun;
        var toBody = new Vector3(d.east, MathF.Max(d.up, 0.02f), -d.north).Normalized();
        Sun.LookAtFromPosition(Vector3.Zero, -toBody, MathF.Abs(toBody.Y) > 0.99f ? Vector3.Forward : Vector3.Up);
        Sun.LightColor = st.UseMoon ? new Color(0.62f, 0.72f, 1f) : C(st.SunColor);
        Sun.LightEnergy = st.UseMoon ? st.MoonEnergy : st.SunEnergy;
        Sun.Visible = Sun.LightEnergy > 0.001f;

        var env = WorldEnv.Environment;
        env.TonemapExposure = st.Exposure;
        env.VolumetricFogDensity = st.FogDensity;
        env.VolumetricFogAlbedo = C(st.FogAlbedo);
        if (!updateSky) return;
        _skyTimer = 0.2f;   // slow changes (sky radiance, mist, lanterns): 5 times a second, not every frame

        var mistAlbedo = Color.FromHsv(0.6f, 0.05f, Mathf.Lerp(0.95f, 0.45f, st.Night));
        lock (_mists)
            foreach (var (vol, mat) in _mists)
            {
                mat.SetShaderParameter(DensityName, st.Mist);
                mat.SetShaderParameter(AlbedoName, mistAlbedo);
                if (vol.IsInsideTree()) vol.Visible = st.Mist > 0.001f && !MistHidden;
            }

        // At night the water's glints are moonlight on wet ripples: soften them so they don't bloom into blobs.
        foreach (var w in Waters) w.SetShaderParameter(RoughnessBoostName, 0.07f * st.Night);

        // Lanterns: on from dusk to dawn.
        float lamp = 1f - Mathf.SmoothStep(1f, 7f, SunElevation);
        foreach (var n in GetTree().GetNodesInGroup(NightLightsName))
            if (n is Light3D l) { float max = (float)l.GetMeta(MaxEnergyName, 1f); l.LightEnergy = max * lamp; l.Visible = lamp > 0.01f; }

        _sky.SkyTopColor = C(st.SkyTop);
        _sky.SkyHorizonColor = C(st.SkyHorizon);
        _sky.GroundHorizonColor = C(st.SkyHorizon) * 0.8f;
        _sky.SkyEnergyMultiplier = st.SkyEnergy;
        _sky.SunAngleMax = Mathf.Lerp(22f, 60f, Overcast);
        _sky.SkyCoverModulate = new Color(1, 1, 1, st.Night * (1f - Overcast) * 0.9f);
    }

    static Color C(Rgb c) => new(c.R, c.G, c.B);

    // Names used repeatedly: string literals would allocate a new StringName on every call.
    static readonly StringName DensityName = "density", AlbedoName = "albedo", RoughnessBoostName = "roughness_boost",
                               NightLightsName = "night_lights", MaxEnergyName = "max_energy";

    /// <summary>Equirectangular star field: uniform over the sphere, a few bright ones, faint Milky Way band.</summary>
    static ImageTexture StarTexture(int w, int h, int seed)
    {
        var img = Image.CreateEmpty(w, h, false, Image.Format.Rgb8);
        var rng = new Rng(seed + 777);
        for (int i = 0; i < 9000; i++)
        {
            float u = rng.Next01(), v = MathF.Acos(1f - 2f * rng.Next01()) / MathF.PI;   // uniform on the sphere
            // Denser along a tilted band (the Milky Way).
            float band = MathF.Abs(MathF.Sin((u * MathF.Tau) + 0.6f) * 0.35f + (v - 0.5f));
            if (band > 0.08f && rng.Next01() < 0.45f) continue;
            int x = (int)(u * w) % w, y = Math.Clamp((int)(v * h), 0, h - 1);
            float b = MathF.Pow(rng.Next01(), 6f) * 0.9f + 0.1f;
            float tint = rng.Range(-0.08f, 0.08f);
            var col = new Color(b * (1f + tint), b, b * (1f - tint));
            img.SetPixel(x, y, col);
            if (b > 0.75f) { img.SetPixel((x + 1) % w, y, col * 0.4f); img.SetPixel(x, Math.Min(h - 1, y + 1), col * 0.4f); }
        }
        return ImageTexture.CreateFromImage(img);
    }
}
