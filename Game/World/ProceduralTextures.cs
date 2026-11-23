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
                    // Mix of rounded and slightly peaked waves.
                    sum += (n * 0.6f + (0.5f - MathF.Abs(n)) * 0.8f) * amp;
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
    /// Foam pattern: a net of bubbles and lace (inverted cellular noise with fine speckle), 0..1 in R.
    /// Thresholding it at different levels gives anything from scattered flecks to dense whitewater.
    /// </summary>
    public static ImageTexture Foam(int size, int seed)
    {
        const int cells = 16;
        var img = Image.CreateEmpty(size, size, true, Image.Format.L8);
        var pts = new Vector2[cells * cells];
        var rng = new River.Core.Rng(seed);
        for (int i = 0; i < pts.Length; i++) pts[i] = new Vector2(rng.Next01(), rng.Next01());
        var val = new float[size * size];
        System.Threading.Tasks.Parallel.For(0, size, y =>
        {
            for (int x = 0; x < size; x++)
            {
                float fx = x * cells / (float)size, fy = y * cells / (float)size;
                int cx = (int)fx, cy = (int)fy;
                float d1 = 9f, d2 = 9f;
                for (int oy = -1; oy <= 1; oy++)
                    for (int ox = -1; ox <= 1; ox++)
                    {
                        int gx = cx + ox, gy = cy + oy;
                        var p = pts[((gy % cells + cells) % cells) * cells + ((gx % cells + cells) % cells)];
                        float dx = gx + p.X - fx, dy = gy + p.Y - fy, d = MathF.Sqrt(dx * dx + dy * dy);
                        if (d < d1) { d2 = d1; d1 = d; } else if (d < d2) d2 = d;
                    }
                float edge = 1f - River.Core.MathUtil.Smoothstep(0f, 0.28f, d2 - d1);   // lace along cell borders
                float fine = PNoise(x * 32f / size, y * 32f / size, 32, seed + 5) * 0.5f + 0.5f;
                float blob = PNoise(x * 4f / size, y * 4f / size, 4, seed + 9) * 0.5f + 0.5f;
                val[y * size + x] = Math.Clamp(edge * 0.6f + fine * 0.3f + blob * 0.35f - 0.1f, 0f, 1f);
            }
        });
        for (int y = 0; y < size; y++) for (int x = 0; x < size; x++) img.SetPixel(x, y, new Color(val[y * size + x], 0, 0));
        img.GenerateMipmaps();
        return ImageTexture.CreateFromImage(img);
    }
}
