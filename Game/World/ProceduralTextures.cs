using System;
using Godot;

namespace River.World;

/// <summary>
/// Tileable textures generated in code (deterministic, ready on the first frame, no asset files needed yet):
/// water ripple normals and a foam pattern.
/// </summary>
public static class ProceduralTextures
{
    /// <summary>Periodic gradient noise: the lattice wraps every <paramref name="period"/> cells.</summary>
    static float PNoise(float x, float y, int period, int seed)
    {
        int ix = (int)MathF.Floor(x), iy = (int)MathF.Floor(y);
        float fx = x - ix, fy = y - iy;
        float G(int gx, int gy, float dx, float dy)
        {
            gx = ((gx % period) + period) % period; gy = ((gy % period) + period) % period;
            float a = River.Core.Noise.Hash(gx, gy, seed) * (MathF.PI * 2f / 4294967296f);
            return MathF.Cos(a) * dx + MathF.Sin(a) * dy;
        }
        float u = fx * fx * fx * (fx * (fx * 6 - 15) + 10), v = fy * fy * fy * (fy * (fy * 6 - 15) + 10);
        float a00 = G(ix, iy, fx, fy), a10 = G(ix + 1, iy, fx - 1, fy), a01 = G(ix, iy + 1, fx, fy - 1), a11 = G(ix + 1, iy + 1, fx - 1, fy - 1);
        return a00 + (a10 - a00) * u + (a01 - a00) * v + (a00 - a10 - a01 + a11) * u * v;
    }

    /// <summary>
    /// Ripple normal map: sharp-crested small waves (|noise| folded, like capillary chop), several octaves.
    /// RGB = tangent-space normal (x, y as world x, z), A = height.
    /// </summary>
    public static ImageTexture WaterNormals(int size, int seed, int basePeriod, float strength)
    {
        var h = new float[size * size];
        System.Threading.Tasks.Parallel.For(0, size, y =>
        {
            for (int x = 0; x < size; x++)
            {
                float sum = 0f, amp = 1f, norm = 0f;
                int period = basePeriod;
                for (int o = 0; o < 5; o++)
                {
                    float fx = x * period / (float)size, fy = y * period / (float)size;
                    float n = PNoise(fx, fy, period, seed + o * 97);
                    // Mostly rounded waves with some peaking (strong folding read as crumpled paper).
                    sum += (n * 0.85f + (0.5f - MathF.Abs(n)) * 0.25f) * amp;
                    norm += amp; amp *= 0.55f; period *= 2;
                }
                h[y * size + x] = sum / norm;
            }
        });
        var img = Image.CreateEmpty(size, size, true, Image.Format.Rgba8);
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float hx = h[y * size + (x + 1) % size] - h[y * size + (x - 1 + size) % size];
                float hy = h[((y + 1) % size) * size + x] - h[((y - 1 + size) % size) * size + x];
                var n = new Vector3(-hx * strength, -hy * strength, 1f).Normalized();
                img.SetPixel(x, y, new Color(n.X * 0.5f + 0.5f, n.Y * 0.5f + 0.5f, n.Z * 0.5f + 0.5f, h[y * size + x] * 0.5f + 0.5f));
            }
        img.GenerateMipmaps();
        return ImageTexture.CreateFromImage(img);
    }

    /// <summary>
    /// Foam pattern, tileable. R: cloudy foam density (domain-warped fBm mixed with billowy noise: soft lumps with
    /// creases, no cell structure). G: small round bubbles, for thin foam. Both channels are histogram-equalised,
    /// so thresholding at 1 − c covers a fraction c of the surface: the shader's coverage means what it says.
    /// </summary>
    public static ImageTexture Foam(int size, int seed)
    {
        var cloud = new float[size * size];
        var bubbles = new float[size * size];
        System.Threading.Tasks.Parallel.For(0, size, y =>
        {
            for (int x = 0; x < size; x++)
            {
                float u = x / (float)size, v = y / (float)size;
                // Domain warp: bends the lumps into curdled, drifting shapes.
                float wx = PNoise(u * 4f, v * 4f, 4, seed + 1), wy = PNoise(u * 4f + 17.3f, v * 4f + 5.1f, 4, seed + 2);
                float uu = u + wx * 0.06f, vv = v + wy * 0.06f;
                float fbm = 0f, billow = 0f, amp = 1f, norm = 0f;
                int period = 4;
                for (int o = 0; o < 5; o++)
                {
                    float n = PNoise(uu * period, vv * period, period, seed + 10 + o * 31);
                    fbm += n * amp;
                    billow += (1f - 2f * MathF.Abs(n)) * amp;   // puffy, with fine creases between puffs
                    norm += amp; amp *= 0.5f; period *= 2;
                }
                cloud[y * size + x] = 0.55f * fbm / norm + 0.45f * billow / norm;
            }
        });
        // Bubbles: ~12k random small discs (radius 0.8–3.4 px), brighter at the rim like real bubbles.
        {
            var rng = new River.Core.Rng(seed + 99);
            for (int c = 0; c < 12000; c++)
            {
                float cx = rng.Next01() * size, cy = rng.Next01() * size, r = 0.8f + 2.6f * rng.Next01() * rng.Next01();
                int x0 = (int)MathF.Floor(cx - r - 1), x1 = (int)MathF.Ceiling(cx + r + 1);
                int y0 = (int)MathF.Floor(cy - r - 1), y1 = (int)MathF.Ceiling(cy + r + 1);
                for (int iy = y0; iy <= y1; iy++)
                    for (int ix = x0; ix <= x1; ix++)
                    {
                        float dx = ix + 0.5f - cx, dy = iy + 0.5f - cy, d = MathF.Sqrt(dx * dx + dy * dy);
                        float k = River.Core.MathUtil.Smoothstep(r, r - 0.9f, d) * (0.6f + 0.4f * River.Core.MathUtil.Smoothstep(0f, r, d));
                        int i = ((iy % size + size) % size) * size + ((ix % size + size) % size);   // wraps: tileable
                        bubbles[i] = MathF.Max(bubbles[i], k);
                    }
            }
        }
        Equalize(cloud);
        var img = Image.CreateEmpty(size, size, true, Image.Format.Rg8);
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                img.SetPixel(x, y, new Color(cloud[y * size + x], bubbles[y * size + x], 0));
        img.GenerateMipmaps();
        return ImageTexture.CreateFromImage(img);
    }

    /// <summary>Replaces each value by its rank (0..1): a uniform distribution with the same shapes.</summary>
    static void Equalize(float[] a)
    {
        var order = new int[a.Length];
        for (int i = 0; i < order.Length; i++) order[i] = i;
        var keys = (float[])a.Clone();
        Array.Sort(keys, order);
        for (int r = 0; r < order.Length; r++) a[order[r]] = r / (float)(order.Length - 1);
    }
}
