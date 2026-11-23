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
}
