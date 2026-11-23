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
///   • wind waves: three short trains (3–6 m, a few cm) spread around the wind, stronger in gusts and on deeper,
///     open water, damped in backwaters (calm = the eddy/lee field)
///   • boils: slow domes welling up in faster, deeper water and drifting with the current (two cross-faded phases)
/// Gust() is shared with the shader's ripples, so gusty patches ruffle both the ripples and the waves.
/// </summary>
public static class SurfaceWaves
{
    const float G = 9.81f;

    /// <summary>Rough-water factor 0..1 from the Froude number (riffles, rapids).</summary>
    public static float Roughness(float speed, float depth) =>
        MathUtil.Smoothstep(0.35f, 0.9f, speed / MathF.Sqrt(G * MathF.Max(depth, 0.08f)));

    /// <summary>Wind strength 0..1: large patches (~200–700 m) drifting slowly, so the surface goes from glassy to ruffled.</summary>
    public static float Gust(float x, float z, float t)
    {
        float g = 0.5f + 0.5f * (0.5f * MathF.Sin(0.023f * x + 0.011f * z + 0.13f * t)
                               + 0.3f * MathF.Sin(-0.009f * x + 0.027f * z - 0.09f * t + 2.1f)
                               + 0.2f * MathF.Sin(0.031f * x - 0.017f * z + 0.17f * t + 4.0f));
        return MathUtil.Smoothstep(0.2f, 0.8f, g);
    }

    static float Boil(float qx, float qz)
    {
        float b = MathF.Max(0f, MathF.Sin(qx * 0.41f + 1.1f) * MathF.Sin(qz * 0.33f + 0.4f) + 0.4f * MathF.Sin((qx - qz) * 0.27f));
        return b * b;
    }

    public static float Height(float x, float z, float t, float vx, float vz, float depth, float calm)
    {
        float speed = MathF.Sqrt(vx * vx + vz * vz);
        float rough = Roughness(speed, depth);
        float shallowFade = MathUtil.Smoothstep(0.05f, 0.5f, depth);

        float h = 0.018f * MathF.Sin(x * 0.31f + t * 1.7f) + 0.014f * MathF.Sin(z * 0.27f - t * 1.3f + 1.3f)
                + 0.010f * MathF.Sin((x + z) * 0.53f + t * 2.3f);
        h += rough * (0.05f * MathF.Sin(x * 1.1f + t * 3.1f) + 0.04f * MathF.Sin(z * 1.4f - t * 3.7f));

        // Wind waves (wind towards +x, slightly +z), deep-water dispersion ω = √(gk).
        float wind = Gust(x, z, t) * (1f - 0.75f * calm) * MathUtil.Smoothstep(0.4f, 2.5f, depth);
        h += wind * (0.012f * MathF.Sin(x * 1.71f + z * 0.83f - t * 4.33f)
                   + 0.016f * MathF.Sin(x * 1.26f + z * 0.52f - t * 3.66f + 1.9f)
                   + 0.014f * MathF.Sin(x * 0.83f + z * 0.67f - t * 3.18f + 4.2f));

        // Boils: domes carried by the current, re-seeded every 9 s with a cross-fade.
        float boilK = MathUtil.Smoothstep(0.5f, 1.4f, speed) * MathUtil.Smoothstep(1.2f, 2.6f, depth) * (1f - calm);
        if (boilK > 0f)
        {
            const float T = 9f;
            float p0 = t / T - MathF.Floor(t / T), p1 = (t / T + 0.5f) - MathF.Floor(t / T + 0.5f);
            float w0 = 1f - MathF.Abs(1f - 2f * p0);
            float a = Boil(x - vx * p0 * T, z - vz * p0 * T), b = Boil(x - vx * p1 * T + 7.3f, z - vz * p1 * T + 3.1f);
            h += 0.025f * boilK * (b + (a - b) * w0);
        }

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
    /// where it speeds up, and a hollow just behind. Its scale follows the velocity head v²/2g, exaggerated 1.6× with a
    /// 5 cm floor so it reads next to a boulder (≈ 17 cm at 1.2 m/s, capped at 45 cm). A rock just under the surface
    /// makes a smooth hump with a hollow behind instead.
    /// </summary>
    public static float RockBump(float x, float z, in Rock r)
    {
        float speed = MathF.Sqrt(r.FlowX * r.FlowX + r.FlowZ * r.FlowZ);
        if (speed < 0.05f) return 0f;
        float dx = x - r.X, dz = z - r.Z, reach = r.Radius * 3.5f;
        if (dx * dx + dz * dz > reach * reach) return 0f;
        // Velocity head, exaggerated a little and given a floor so even a gentle current visibly climbs the rock.
        float hv = MathF.Min(0.05f + 1.6f * speed * speed / (2f * G), 0.45f);
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

    /// <summary>Everything above the solved level at a world point of the endless river (what the raft floats on).</summary>
    public static float Total(RiverWorld w, float x, float z, float t)
    {
        var s = w.At(x, z);
        float h = Height(x, z, t, s.VelX, s.VelZ, s.Depth, s.Eddy);
        var rocks = w.Rocks;
        for (int i = 0; i < rocks.Count; i++) h += RockBump(x, z, rocks[i]);   // indexed: no boxed enumerator per call
        return h;
    }

    /// <summary>Everything above the solved level at (x, z): waves plus rock bumps (what the raft floats on).</summary>
    public static float Total(ReachField f, float x, float z, float t)
    {
        if (!f.InDomain(x, z)) return Height(x, z, t, 0f, 0.6f, 3f, 0f);
        var (vx, vz) = f.SampleVelocity(x, z);
        float calm = f.Eddy != null ? f.Sample(f.Eddy, x, z) : 0f;
        float h = Height(x, z, t, vx, vz, f.SampleDepth(x, z), calm);
        foreach (var r in f.Rocks) h += RockBump(x, z, r);
        return h;
    }
}
