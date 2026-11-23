using System;

namespace River.Core;

/// <summary>
/// Exact Euclidean distance transform (Felzenszwalb &amp; Huttenlocher) with nearest-feature indices.
/// Used for "distance to the shore" on both sides of the water, and for "nearest water node" lookups.
/// </summary>
public static class Edt
{
    const float Inf = 1e20f;

    /// <summary>
    /// For every node, the distance (in nodes) to the nearest node where <paramref name="feature"/> is true,
    /// and that node's flat index (-1 if there is none).
    /// </summary>
    public static void Compute(bool[] feature, int nx, int nz, float[] dist, int[] nearest)
    {
        var f = new float[Math.Max(nx, nz)];
        var d = new float[Math.Max(nx, nz)];
        var arg = new int[Math.Max(nx, nz)];
        var v = new int[Math.Max(nx, nz)];
        var zb = new float[Math.Max(nx, nz) + 1];

        // Pass 1: columns (along z). colD = squared distance in z, colArg = z of the nearest feature.
        var colD = new float[nx * nz];
        var colArg = new int[nx * nz];
        for (int x = 0; x < nx; x++)
        {
            for (int z = 0; z < nz; z++) f[z] = feature[z * nx + x] ? 0f : Inf;
            Dt1(f, nz, d, arg, v, zb);
            for (int z = 0; z < nz; z++) { colD[z * nx + x] = d[z]; colArg[z * nx + x] = arg[z]; }
        }

        // Pass 2: rows (along x) on the column results.
        for (int z = 0; z < nz; z++)
        {
            for (int x = 0; x < nx; x++) f[x] = colD[z * nx + x];
            Dt1(f, nx, d, arg, v, zb);
            for (int x = 0; x < nx; x++)
            {
                int i = z * nx + x;
                if (d[x] >= Inf * 0.5f) { dist[i] = float.MaxValue; nearest[i] = -1; continue; }
                dist[i] = MathF.Sqrt(d[x]);
                int sx = arg[x];
                int sz = colArg[z * nx + sx];
                nearest[i] = sz * nx + sx;
            }
        }
    }

    static float Intersect(float[] f, int q, int p) =>
        ((f[q] + q * (float)q) - (f[p] + p * (float)p)) / (2f * q - 2f * p);

    /// <summary>1D squared distance transform of sampled function f (lower envelope of parabolas).</summary>
    static void Dt1(float[] f, int n, float[] d, int[] arg, int[] v, float[] z)
    {
        int k = 0;
        // Skip leading infinities so the envelope starts at a real parabola.
        int first = 0;
        while (first < n && f[first] >= Inf * 0.5f) first++;
        if (first == n)
        {
            for (int q = 0; q < n; q++) { d[q] = Inf; arg[q] = -1; }
            return;
        }
        v[0] = first; z[0] = -Inf; z[1] = Inf;
        for (int q = first + 1; q < n; q++)
        {
            if (f[q] >= Inf * 0.5f) continue;
            float s = Intersect(f, q, v[k]);
            while (s <= z[k]) { k--; s = Intersect(f, q, v[k]); } // z[0] = -Inf stops the loop
            k++;
            v[k] = q; z[k] = s; z[k + 1] = Inf;
        }
        k = 0;
        for (int q = 0; q < n; q++)
        {
            while (z[k + 1] < q) k++;
            int p = v[k];
            d[q] = (q - p) * (float)(q - p) + f[p];
            arg[q] = p;
        }
    }
}
