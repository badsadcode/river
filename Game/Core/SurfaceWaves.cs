using System;

namespace River.Core;

/// <summary>
/// Small surface waves on top of the solved water level: what the raft floats on and what the water mesh shows.
/// <b>Mirrored in Game/Shaders/River.gdshader (wave_height): change both together</b>, or the raft will bob out
/// of step with the water you see. Time is the raft's physics clock (Raft.WaveTime), passed to the shader.
///
///   • swell: a few long, low ripples everywhere (12–23 m, 1–2 cm)
///   • chop: short, choppier waves where the water is fast and shallow (Froude number), moving with time
///   • standing waves: in fast water the surface forms waves that stay put while the water rushes through them,
///     crests across the current, wavelength 2πv²/g (≈ 5 m at 2.8 m/s), broken up across the flow
/// </summary>
public static class SurfaceWaves
{
    const float G = 9.81f;

    /// <summary>Rough-water factor 0..1 from the Froude number (riffles, rapids).</summary>
    public static float Roughness(float speed, float depth) =>
        MathUtil.Smoothstep(0.35f, 0.9f, speed / MathF.Sqrt(G * MathF.Max(depth, 0.08f)));

    public static float Height(float x, float z, float t, float vx, float vz, float depth)
    {
        float speed = MathF.Sqrt(vx * vx + vz * vz);
        float rough = Roughness(speed, depth);
        float shallowFade = MathUtil.Smoothstep(0.05f, 0.5f, depth);

        float h = 0.018f * MathF.Sin(x * 0.31f + t * 1.7f) + 0.014f * MathF.Sin(z * 0.27f - t * 1.3f + 1.3f)
                + 0.010f * MathF.Sin((x + z) * 0.53f + t * 2.3f);
        h += rough * (0.05f * MathF.Sin(x * 1.1f + t * 3.1f) + 0.04f * MathF.Sin(z * 1.4f - t * 3.7f));

        if (speed > 0.3f)
        {
            float dx = vx / speed, dz = vz / speed;
            float along = x * dx + z * dz, across = -x * dz + z * dx;
            float k = Math.Clamp(G / (speed * speed), 2f * MathF.PI / 8f, 2f * MathF.PI / 2.5f);
            float amp = 0.13f * MathUtil.Smoothstep(0.45f, 1.0f, speed / MathF.Sqrt(G * MathF.Max(depth, 0.08f)));
            float breakup = 0.55f + 0.45f * MathF.Sin(across * 0.9f + 1.7f * MathF.Sin(along * 0.23f));
            h += amp * breakup * MathF.Sin(along * k + 0.25f * MathF.Sin(t * 1.9f + across * 0.4f));
        }
        return h * shallowFade;
    }

    /// <summary>
    /// Water piling up around a rock (also mirrored in the shader: rock_bump). In the rock's frame (u along the
    /// approaching current, v across, both in rock radii): a pillow on the upstream face, water drawn down beside it
    /// where it speeds up, and a hollow just behind. Its scale is the velocity head v²/2g (7 cm at 1.2 m/s, 40 cm at
    /// 2.8 m/s). A rock just under the surface makes a smooth hump with a hollow behind instead.
    /// </summary>
    public static float RockBump(float x, float z, in Rock r)
    {
        float speed = MathF.Sqrt(r.FlowX * r.FlowX + r.FlowZ * r.FlowZ);
        if (speed < 0.05f) return 0f;
        float dx = x - r.X, dz = z - r.Z, reach = r.Radius * 3.5f;
        if (dx * dx + dz * dz > reach * reach) return 0f;
        float hv = MathF.Min(speed * speed / (2f * G), 0.35f);
        float ux = r.FlowX / speed, uz = r.FlowZ / speed;
        float u = (dx * ux + dz * uz) / r.Radius, v = (-dx * uz + dz * ux) / r.Radius;
        float emerg = MathUtil.Smoothstep(-0.6f, 0.0f, r.Top);          // 1: breaks the surface
        float deep = MathUtil.Smoothstep(-1.4f, -0.6f, r.Top);          // 0: too deep to show
        float pillow = MathF.Exp(-((u + 1.05f) * (u + 1.05f) / 0.18f + v * v / 0.55f));
        float sides = MathF.Exp(-((u - 0.2f) * (u - 0.2f) / 0.5f + (MathF.Abs(v) - 1.15f) * (MathF.Abs(v) - 1.15f) / 0.12f));
        float hollow = MathF.Exp(-((u - 1.5f) * (u - 1.5f) / 0.6f + v * v / 0.35f));
        float hump = MathF.Exp(-((u + 0.2f) * (u + 0.2f) + v * v) / 0.8f);
        return hv * deep * (emerg * (pillow - 0.35f * sides - 0.55f * hollow) + (1f - emerg) * (0.6f * hump - 0.4f * hollow));
    }

    /// <summary>Everything above the solved level at (x, z): waves plus rock bumps (what the raft floats on).</summary>
    public static float Total(ReachField f, float x, float z, float t)
    {
        if (!f.InDomain(x, z)) return Height(x, z, t, 0f, 0.6f, 3f);
        var (vx, vz) = f.SampleVelocity(x, z);
        float h = Height(x, z, t, vx, vz, f.SampleDepth(x, z));
        foreach (var r in f.Rocks) h += RockBump(x, z, r);
        return h;
    }
}
