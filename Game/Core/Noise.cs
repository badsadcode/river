using System;

namespace River.Core;

/// <summary>
/// Deterministic noise (no Godot types, so the generator can run in headless tests).
/// Same seed + same input = same output on every machine.
/// </summary>
public static class Noise
{
    /// <summary>32-bit integer hash (lowbias32 variant).</summary>
    public static uint Hash(uint x)
    {
        x ^= x >> 16; x *= 0x7feb352dU;
        x ^= x >> 15; x *= 0x846ca68bU;
        x ^= x >> 16;
        return x;
    }

    public static uint Hash(int x, int y, int seed) =>
        Hash((uint)x * 0x8da6b343U ^ Hash((uint)y * 0xd8163841U ^ Hash((uint)seed)));

    /// <summary>Hash to [0, 1).</summary>
    public static float Hash01(int x, int y, int seed) => (Hash(x, y, seed) >> 8) * (1f / 16777216f);

    static float Fade(float t) => t * t * t * (t * (t * 6f - 15f) + 10f);

    static float Grad(int ix, int iy, int seed, float dx, float dy)
    {
        float a = Hash(ix, iy, seed) * (MathF.PI * 2f / 4294967296f);
        return MathF.Cos(a) * dx + MathF.Sin(a) * dy;
    }

    /// <summary>2D gradient noise, roughly in [-0.7, 0.7].</summary>
    public static float Gradient(float x, float y, int seed)
    {
        int ix = (int)MathF.Floor(x), iy = (int)MathF.Floor(y);
        float fx = x - ix, fy = y - iy;
        float u = Fade(fx), v = Fade(fy);
        float a = Grad(ix, iy, seed, fx, fy);
        float b = Grad(ix + 1, iy, seed, fx - 1f, fy);
        float c = Grad(ix, iy + 1, seed, fx, fy - 1f);
        float d = Grad(ix + 1, iy + 1, seed, fx - 1f, fy - 1f);
        return a + (b - a) * u + (c - a) * v + (a - b - c + d) * u * v;
    }

    /// <summary>1D gradient noise (a slice of the 2D one).</summary>
    public static float Gradient1(float x, int seed) => Gradient(x, 0.5f, seed);

    /// <summary>Fractal sum of gradient noise, roughly in [-1, 1].</summary>
    public static float Fbm(float x, float y, int seed, int octaves = 4, float lacunarity = 2f, float gain = 0.5f)
    {
        float sum = 0f, amp = 1f, norm = 0f;
        for (int o = 0; o < octaves; o++)
        {
            sum += Gradient(x, y, seed + o * 1013) * amp;
            norm += amp;
            amp *= gain;
            x *= lacunarity; y *= lacunarity;
        }
        return sum / norm * 1.4f;
    }

    public static float Fbm1(float x, int seed, int octaves = 3) => Fbm(x, 0.37f, seed, octaves);

    /// <summary>Ridged fractal noise in [0, 1] (sharp crests for hills).</summary>
    public static float Ridged(float x, float y, int seed, int octaves = 4)
    {
        float sum = 0f, amp = 0.5f, norm = 0f;
        for (int o = 0; o < octaves; o++)
        {
            float n = 1f - MathF.Abs(Gradient(x, y, seed + o * 733) * 1.4f);
            sum += n * n * amp;
            norm += amp;
            amp *= 0.5f;
            x *= 2f; y *= 2f;
        }
        return sum / norm;
    }
}

/// <summary>Small deterministic random generator (xorshift-based).</summary>
public sealed class Rng
{
    uint _state;
    public Rng(int seed) { _state = Noise.Hash((uint)seed ^ 0x9e3779b9U) | 1u; }
    public uint NextUInt() { _state ^= _state << 13; _state ^= _state >> 17; _state ^= _state << 5; return _state; }
    public float Next01() => (NextUInt() >> 8) * (1f / 16777216f);
    public float Range(float a, float b) => a + (b - a) * Next01();
    public int Range(int a, int bExclusive) => a + (int)(NextUInt() % (uint)(bExclusive - a));
}

public static class MathUtil
{
    public static float Clamp01(float x) => x < 0f ? 0f : x > 1f ? 1f : x;
    public static float Lerp(float a, float b, float t) => a + (b - a) * t;
    public static float Smoothstep(float e0, float e1, float x)
    {
        float t = Clamp01((x - e0) / (e1 - e0));
        return t * t * (3f - 2f * t);
    }
    /// <summary>1 inside [a1, b0], ramps over [a0, a1] and [b0, b1], 0 outside.</summary>
    public static float Plateau(float x, float a0, float a1, float b0, float b1) =>
        Smoothstep(a0, a1, x) * (1f - Smoothstep(b0, b1, x));
}
