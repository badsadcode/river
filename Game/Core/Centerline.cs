using System;
using System.Collections.Generic;

namespace River.Core;

/// <summary>
/// A river channel's centre line, sampled every <see cref="Step"/> metres along its length.
/// Each sample also carries the channel's half-widths (left/right of the flow) and maximum depth there.
/// Coordinates: x east, z downstream-ish (the reach flows towards +z), metres.
/// </summary>
public sealed class Centerline
{
    public const float Step = 2f;

    public readonly List<float> X = new(), Z = new();
    public readonly List<float> TanX = new(), TanZ = new();     // unit tangent (flow direction)
    public readonly List<float> BendX = new(), BendZ = new();   // dT/ds: points to the centre of curvature, |.| = 1/R
    public readonly List<float> HalfLeft = new(), HalfRight = new();
    public readonly List<float> MaxDepth = new();

    public int Count => X.Count;
    public float Length => (Count - 1) * Step;

    /// <summary>Right-hand normal of the flow (looking downstream, Y up).</summary>
    public (float x, float z) Right(int i) => (-TanZ[i], TanX[i]);

    public void Add(float x, float z, float hl, float hr, float depth)
    {
        X.Add(x); Z.Add(z); HalfLeft.Add(hl); HalfRight.Add(hr); MaxDepth.Add(depth);
    }

    /// <summary>Computes tangents and curvature vectors from the positions (smoothed).</summary>
    public void Finish()
    {
        int n = Count;
        TanX.Clear(); TanZ.Clear(); BendX.Clear(); BendZ.Clear();
        for (int i = 0; i < n; i++)
        {
            int a = Math.Max(0, i - 2), b = Math.Min(n - 1, i + 2);
            float dx = X[b] - X[a], dz = Z[b] - Z[a];
            float l = MathF.Max(1e-6f, MathF.Sqrt(dx * dx + dz * dz));
            TanX.Add(dx / l); TanZ.Add(dz / l);
        }
        const int k = 6; // curvature over ±12 m: smooth enough to classify inner/outer banks
        for (int i = 0; i < n; i++)
        {
            int a = Math.Max(0, i - k), b = Math.Min(n - 1, i + k);
            float ds = (b - a) * Step;
            BendX.Add(ds > 0 ? (TanX[b] - TanX[a]) / ds : 0f);
            BendZ.Add(ds > 0 ? (TanZ[b] - TanZ[a]) / ds : 0f);
        }
    }

    /// <summary>First sample index whose z is at or beyond <paramref name="z"/>.</summary>
    public int IndexAtZ(float z)
    {
        for (int i = 0; i < Count; i++) if (Z[i] >= z) return i;
        return Count - 1;
    }

    /// <summary>Index of the sample nearest to (x, z) (brute force; for planning only).</summary>
    public int Nearest(float x, float z)
    {
        int best = 0; float bd = float.MaxValue;
        for (int i = 0; i < Count; i++)
        {
            float dx = X[i] - x, dz = Z[i] - z, d = dx * dx + dz * dz;
            if (d < bd) { bd = d; best = i; }
        }
        return best;
    }
}

/// <summary>
/// An island: a teardrop (blunt upstream, tapered downstream, as real river islands are) with a noisy outline.
/// </summary>
public sealed class Island
{
    public float X, Z;           // centre
    public float DirX, DirZ;     // flow direction at the island (unit)
    public float LengthUp;       // centre → upstream tip (m)
    public float LengthDown;     // centre → downstream tip (m)
    public float HalfWidth;      // (m)
    public float Roughness;      // outline noise as a share of the half-width
    public float MaxHeight;      // highest point above the water (m): low bars ≈ 0.5, forested islands 2–4
    public int Seed;

    /// <summary>Approximate signed distance (m): negative inside the island.</summary>
    public float Sdf(float x, float z)
    {
        float dx = x - X, dz = z - Z;
        float u = dx * DirX + dz * DirZ;          // along the flow
        float v = -dx * DirZ + dz * DirX;         // across
        float a = u < 0 ? LengthUp : LengthDown;
        float b = HalfWidth;
        // Taper the downstream half: narrower towards the tail.
        if (u > 0) b *= 1f - 0.45f * MathUtil.Clamp01(u / LengthDown);
        float r = MathF.Sqrt((u / a) * (u / a) + (v / b) * (v / b));
        float ang = MathF.Atan2(v / b, u / a);
        float noise = Noise.Fbm(MathF.Cos(ang) * 1.7f + Seed * 0.13f, MathF.Sin(ang) * 1.7f, Seed, 3) * Roughness;
        return (r - 1f - noise) * MathF.Min(a, b);
    }
}
