using System;
using Godot;
using River.Core;

namespace River.World;

/// <summary>
/// Spray thrown up where the current hits a rock that breaks (or nearly breaks) the surface: droplets leave the
/// upstream face, rise a little and fall back. Amount and height scale with the approaching current
/// (Rock.FlowX/Z), so a riffle boulder spits and a rock in slack water stays quiet.
/// </summary>
public static class RockSplash
{
    static ImageTexture _dot;
    static StandardMaterial3D _drawMat;

    public static GpuParticles3D Create(ReachField f, in Rock r)
    {
        float speed = MathF.Sqrt(r.FlowX * r.FlowX + r.FlowZ * r.FlowZ);
        if (speed < 0.25f || r.Top < -0.25f) return null;
        float dx = r.FlowX / speed, dz = r.FlowZ / speed;
        float eta = f.SampleEta(r.X, r.Z);

        var pm = new ParticleProcessMaterial
        {
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Sphere,
            EmissionSphereRadius = r.Radius * 0.35f,
            // Up and back against the current: droplets bounce off the upstream face.
            Direction = new Vector3(-dx * 0.6f, 1f, -dz * 0.6f).Normalized(),
            Spread = 38f,
            InitialVelocityMin = 0.5f + 0.5f * speed,
            InitialVelocityMax = 0.9f + 1.0f * speed,
            Gravity = new Vector3(dx * speed * 1.5f, -9.81f, dz * speed * 1.5f),   // and get carried downstream
            ScaleMin = 0.5f, ScaleMax = 1.4f,
            ColorRamp = new GradientTexture1D
            {
                Gradient = new Gradient
                {
                    Offsets = new[] { 0f, 0.15f, 1f },
                    Colors = new[] { new Color(1, 1, 1, 0f), new Color(1, 1, 1, 0.85f), new Color(1, 1, 1, 0f) },
                },
            },
        };
        var ps = new GpuParticles3D
        {
            Name = "Splash",
            Amount = (int)Math.Clamp(12f + 45f * speed * MathUtil.Smoothstep(-0.25f, 0.1f, r.Top), 8f, 160f),
            Lifetime = 0.55f + 0.15f * speed,
            ProcessMaterial = pm,
            DrawPass1 = new QuadMesh { Size = new Vector2(0.09f, 0.09f), Material = DrawMaterial() },
            Position = new Vector3(r.X - dx * r.Radius * 0.8f, eta + 0.05f, r.Z - dz * r.Radius * 0.8f),
            VisibilityAabb = new Aabb(new Vector3(-4, -2, -4), new Vector3(8, 5, 8)),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Randomness = 0.5f,
        };
        return ps;
    }

    static StandardMaterial3D DrawMaterial()
    {
        if (_drawMat != null) return _drawMat;
        _drawMat = new StandardMaterial3D
        {
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles,
            VertexColorUseAsAlbedo = true,
            AlbedoColor = new Color(0.9f, 0.92f, 0.92f),
            AlbedoTexture = Dot(),
            Roughness = 1f,
        };
        return _drawMat;
    }

    /// <summary>A soft round droplet sprite.</summary>
    static ImageTexture Dot()
    {
        if (_dot != null) return _dot;
        const int n = 32;
        var img = Image.CreateEmpty(n, n, true, Image.Format.Rgba8);
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float dx = (x + 0.5f) / n * 2f - 1f, dy = (y + 0.5f) / n * 2f - 1f;
                float a = MathUtil.Smoothstep(1f, 0.35f, MathF.Sqrt(dx * dx + dy * dy));
                img.SetPixel(x, y, new Color(1, 1, 1, a));
            }
        img.GenerateMipmaps();
        return _dot = ImageTexture.CreateFromImage(img);
    }
}
