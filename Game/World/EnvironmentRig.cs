using System;
using Godot;
using River.Core;

namespace River.World;

/// <summary>
/// Sky, sun, fog and river mist, with a few time-of-day presets (T cycles them in game).
/// Mood target: soft diffuse light, morning mist on the water (docs/DESIGN_PILLARS.md §11).
/// </summary>
public partial class EnvironmentRig : Node3D
{
    public DirectionalLight3D Sun;
    public WorldEnvironment WorldEnv;
    public FogVolume Mist;
    ShaderMaterial _mistMat;
    ProceduralSkyMaterial _sky;
    int _preset;

    public static readonly string[] PresetNames = { "dawn mist", "morning", "noon", "golden evening", "overcast" };
    public string PresetName => PresetNames[_preset];

    public void Setup(ReachField f, FieldTextures tex)
    {
        // Procedural sky (art-directable per preset; a physical sky looked flat grey under AgX).
        _sky = new ProceduralSkyMaterial
        {
            SunAngleMax = 22f, SunCurve = 0.12f, GroundBottomColor = new Color(0.16f, 0.17f, 0.13f),
            GroundHorizonColor = new Color(0.62f, 0.64f, 0.6f), UseDebanding = true,
        };
        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Sky,
            Sky = new Sky { SkyMaterial = _sky, ProcessMode = Sky.ProcessModeEnum.Automatic, RadianceSize = Sky.RadianceSizeEnum.Size256 },
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

        // Mist over the whole reach, hugging the water (Mist.gdshader reads the water level from the field).
        float etaMin = float.MaxValue, etaMax = float.MinValue;
        for (int i = 0; i < f.Eta.Length; i++) if (f.Water[i]) { etaMin = MathF.Min(etaMin, f.Eta[i]); etaMax = MathF.Max(etaMax, f.Eta[i]); }
        _mistMat = new ShaderMaterial { Shader = GD.Load<Shader>("res://Game/Shaders/Mist.gdshader") };
        tex.Apply(_mistMat);
        _mistMat.SetShaderParameter("noise3d", new NoiseTexture3D
        {
            Width = 64, Height = 32, Depth = 64, Seamless = true,
            Noise = new FastNoiseLite { Frequency = 0.06f, FractalOctaves = 3, Seed = f.Seed },
        });
        float h = etaMax - etaMin + 30f;
        Mist = new FogVolume
        {
            Name = "RiverMist", Shape = RenderingServer.FogVolumeShape.Box,
            Size = new Vector3(f.SizeX, h, f.SizeZ), Material = _mistMat,
            Position = new Vector3(f.SizeX * 0.5f, etaMin - 2f + h * 0.5f, f.SizeZ * 0.5f),
        };
        AddChild(Mist);

        ApplyPreset(0);
    }

    public void NextPreset() => ApplyPreset((_preset + 1) % PresetNames.Length);

    public void ApplyPreset(int p)
    {
        _preset = p;
        var env = WorldEnv.Environment;
        // elevation°, azimuth° (from north, clockwise), colour, energy, mist, fog, sky turbidity
        (float el, float az, Color col, float energy, float mist, float fog, float turb, float exposure) s = p switch
        {
            0 => (9f, 100f, new Color(1f, 0.76f, 0.55f), 1.6f, 0.07f, 0.0012f, 10f, 1.1f),
            1 => (24f, 120f, new Color(1f, 0.9f, 0.78f), 2.3f, 0.025f, 0.0006f, 6f, 1.0f),
            2 => (58f, 170f, new Color(1f, 0.98f, 0.94f), 2.8f, 0.0f, 0.0003f, 4f, 0.9f),
            3 => (8f, 265f, new Color(1f, 0.62f, 0.38f), 1.6f, 0.02f, 0.0008f, 8f, 1.1f),
            _ => (40f, 200f, new Color(0.85f, 0.88f, 0.92f), 0.55f, 0.03f, 0.0018f, 20f, 1.25f),
        };
        float el = Mathf.DegToRad(s.el), az = Mathf.DegToRad(s.az);
        // Direction the light travels: from the sun towards the ground. North = −z, east = +x.
        var toSun = new Vector3(MathF.Sin(az) * MathF.Cos(el), MathF.Sin(el), -MathF.Cos(az) * MathF.Cos(el));
        Sun.LookAtFromPosition(Vector3.Zero, -toSun, MathF.Abs(toSun.Y) > 0.99f ? Vector3.Forward : Vector3.Up);
        Sun.LightColor = s.col;
        Sun.LightEnergy = s.energy;
        // Sky colours per preset: zenith, horizon.
        (Color top, Color hor) sky = p switch
        {
            0 => (new Color(0.36f, 0.45f, 0.62f), new Color(0.93f, 0.78f, 0.62f)),
            1 => (new Color(0.30f, 0.48f, 0.76f), new Color(0.76f, 0.80f, 0.84f)),
            2 => (new Color(0.24f, 0.44f, 0.78f), new Color(0.70f, 0.78f, 0.86f)),
            3 => (new Color(0.30f, 0.36f, 0.58f), new Color(0.98f, 0.66f, 0.42f)),
            _ => (new Color(0.52f, 0.56f, 0.60f), new Color(0.70f, 0.72f, 0.73f)),
        };
        _sky.SkyTopColor = sky.top;
        _sky.SkyHorizonColor = sky.hor;
        _sky.GroundHorizonColor = sky.hor * 0.8f;
        _sky.SkyEnergyMultiplier = p == 4 ? 0.8f : 1.0f;
        _sky.SunAngleMax = p == 4 ? 60f : 22f;
        env.VolumetricFogDensity = s.fog;
        env.VolumetricFogAlbedo = p == 4 ? new Color(0.8f, 0.82f, 0.85f) : new Color(0.92f, 0.9f, 0.88f);
        env.TonemapExposure = s.exposure;
        _mistMat.SetShaderParameter("density", s.mist);
        Mist.Visible = s.mist > 0f;
    }
}
