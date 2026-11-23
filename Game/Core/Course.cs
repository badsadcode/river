using System;
using System.Collections.Generic;

namespace River.Core;

/// <summary>
/// The river's course: its path and features before anything is 2D (docs/STREAMING.md §3). Centre lines, islands
/// and rocks are in world coordinates here; a segment takes a slice of them into its own frame (<see cref="SegmentSpec"/>).
/// For now there is one hand-made course, the prototype reach (<see cref="PrototypeCourse"/>); the endless course
/// generator (phase 1) will produce the same kind of object.
/// </summary>
public sealed class Course
{
    public Centerline Main, Tributary;
    public readonly List<Island> Islands = new();
    public readonly List<Rock> Rocks = new();
}

/// <summary>Which edge of a segment's grid water flows in through.</summary>
public enum GridEdge { Upstream, Downstream, Left, Right }   // z = 0, z = max, x = 0, x = max

/// <summary>Water entering a segment: through an edge, inside one channel (0 = main, 1 = tributary).</summary>
public readonly record struct Inflow(GridEdge Edge, int Line, float Discharge);

/// <summary>
/// Everything one segment's generation needs, in the segment's own frame: the slice of the course that falls on
/// its grid, how water enters and leaves it, and the solver settings. Generation is a pure function of this.
/// </summary>
public sealed class SegmentSpec
{
    public int Seed;
    public Frame Frame = Frame.Identity;
    public float SizeX, SizeZ, Cell = 2f;
    public Centerline Main, Tributary;             // grid-local coordinates
    public List<Island> Islands = new();
    public List<Rock> Rocks = new();
    public Inflow[] Inflows = Array.Empty<Inflow>();
    public float OutletEta;                         // water level held along the downstream edge (m)
    public float Manning = 0.035f, RefSlope = 0.0005f;
    public int ReferencePicardSteps;                // tests: > 0 = single-level solve with this many nonlinear steps
}

/// <summary>
/// The land around the river, as functions of position: the valley's axis and walls, gentle floodplain relief.
/// Shared by the segment terrain and the far horizon land so they meet without a seam. (Prototype: relative to a
/// valley along +z; phase 1 will express it around the course's own axis.)
/// </summary>
public static class Valley
{
    /// <summary>The valley's axis (x) at z: the meander belt wanders around it, valley walls rise either side.</summary>
    public static float AxisX(float z, int seed, float sizeX) =>
        sizeX * 0.5f + 110f * Noise.Fbm1(z / 1600f, seed + 71, 2);

    /// <summary>
    /// Valley walls and hills: height added on top of the floodplain at (x, z), zero near the river.
    /// Shared with the far horizon terrain so the two meet without a seam.
    /// </summary>
    public static float Relief(float x, float z, int seed, float sizeX)
    {
        float q = MathF.Abs(x - AxisX(z, seed, sizeX));
        float halfWidth = 360f + 60f * Noise.Fbm1(z / 900f, seed + 72, 2);
        float wall = MathUtil.Smoothstep(halfWidth, halfWidth + 230f, q);
        float far = MathUtil.Smoothstep(halfWidth + 200f, halfWidth + 1400f, q);
        float ridges = Noise.Ridged(x / 420f, z / 420f, seed + 73, 4);
        float hills = Noise.Fbm(x / 900f, z / 900f, seed + 74, 3);
        return MathF.Pow(wall, 1.6f) * (48f + 26f * hills) + wall * 30f * ridges + far * (60f + 60f * hills);
    }

    /// <summary>Gentle floodplain relief (m), independent of the river (also used by the far terrain).</summary>
    public static float FloodplainRelief(float x, float z, int seed) =>
        0.9f + 0.9f * Noise.Fbm(x / 190f, z / 190f, seed + 81, 3) + 0.35f * Noise.Fbm(x / 28f, z / 28f, seed + 82, 2);
}

/// <summary>
/// The prototype course, laid out to show the feature catalogue (docs/DESIGN_PILLARS.md §4): meanders at the top,
/// a river-lake with one large island and islets (Tom's sketch #1) in the middle, a tributary joining from the
/// east, a forested island splitting the channel, and a shallow rocky riffle near the bottom. One segment covers it.
/// </summary>
public static class PrototypeCourse
{    // Layout of the prototype reach along z (m).
    const float LakeA0 = 1050, LakeA1 = 1250, LakeB0 = 1550, LakeB1 = 1750;
    const float SplitA0 = 2380, SplitA1 = 2440, SplitB0 = 2560, SplitB1 = 2620;
    const float RiffleA0 = 2600 + 120, RiffleA1 = 2780, RiffleB0 = 2900, RiffleB1 = 2960;
    const float ConfluenceZ = 2250;

    public static Course Build(ReachSettings s)
    {
        var c = new Course();
        c.Main = BuildMain(s);
        c.Tributary = BuildTributary(s, c.Main);
        PlaceIslands(s, c);
        PlaceRocks(s, c);
        return c;
    }

    /// <summary>The prototype's single segment: the whole course on one grid at the world origin.</summary>
    public static SegmentSpec Spec(ReachSettings s)
    {
        var c = Build(s);
        return new SegmentSpec
        {
            Seed = s.Seed, Frame = Frame.Identity, SizeX = s.SizeX, SizeZ = s.SizeZ, Cell = s.Cell,
            Main = c.Main, Tributary = c.Tributary, Islands = c.Islands.ConvertAll(i => i), Rocks = c.Rocks.ConvertAll(r => r),
            Inflows = new[] { new Inflow(GridEdge.Upstream, 0, s.DischargeMain), new Inflow(GridEdge.Right, 1, s.DischargeTributary) },
            OutletEta = s.OutletEta, Manning = s.Manning, RefSlope = s.RefSlope, ReferencePicardSteps = s.ReferencePicardSteps,
        };
    }
    static Centerline BuildMain(ReachSettings s)
    {
        var c = new Centerline();
        int seed = s.Seed;
        float x = Valley.AxisX(-60f, seed, s.SizeX), z = -60f;
        float phase = Noise.Hash01(1, 2, seed) * MathF.PI * 2f;
        float sAlong = 0f;
        for (int guard = 0; guard < 20000 && z < s.SizeZ + 60f; guard++)
        {
            float lake = MathUtil.Plateau(z, LakeA0, LakeA1, LakeB0, LakeB1);
            float riffle = MathUtil.Plateau(z, RiffleA0, RiffleA1, RiffleB0, RiffleB1);
            float split = MathUtil.Plateau(z, SplitA0, SplitA1, SplitB0, SplitB1);
            float afterConfluence = MathUtil.Smoothstep(ConfluenceZ - 40f, ConfluenceZ + 80f, z);
            float edges = MathUtil.Smoothstep(-60f, 180f, z) * (1f - MathUtil.Smoothstep(s.SizeZ - 200f, s.SizeZ + 40f, z));

            float baseHalf = 18f * (1f + 0.12f * Noise.Fbm1(sAlong / 300f, seed + 3)) * (1f + 0.12f * afterConfluence);
            float hl = baseHalf, hr = baseHalf;
            hl += 72f * lake * (1f + 0.35f * Noise.Fbm1(z / 140f, seed + 5));
            hr += 150f * lake * (1f + 0.30f * Noise.Fbm1(z / 170f, seed + 6));
            hl += 17f * split; hr += 17f * split;
            hl *= 1f - 0.1f * riffle; hr *= 1f - 0.1f * riffle;

            float depth = 2.6f * (1f + 0.1f * Noise.Fbm1(sAlong / 220f, seed + 7)) * (1f + 0.1f * afterConfluence);
            depth = MathUtil.Lerp(depth, 5.2f, lake);
            depth = MathUtil.Lerp(depth, 1.05f, riffle);
            c.Add(x, z, hl, hr, depth);

            // Kinoshita meander curve: θ = θ0 sin φ + θ0³ (Js cos 3φ − Jf sin 3φ), wavelength ≈ 11 widths.
            // Amplitude, wavelength and skew all drift along the river so no two bends are alike.
            float amp = MathF.Min(1.9f, 1.55f + 0.6f * Noise.Fbm1(sAlong / 700f, seed + 11)) * edges * (1f - 0.92f * lake) * (1f - 0.6f * riffle);
            float wavelength = 12f * 2f * baseHalf * (1f + 0.35f * Noise.Fbm1(sAlong / 450f, seed + 12));
            float skew = 0.06f * Noise.Fbm1(sAlong / 800f, seed + 14);
            phase += MathF.PI * 2f * Centerline.Step / wavelength;
            float theta = amp * MathF.Sin(phase) + amp * amp * amp * (0.02f * MathF.Cos(3f * phase) - skew * MathF.Sin(3f * phase));
            float restore = -1.1f * Math.Clamp((x - Valley.AxisX(z, seed, s.SizeX)) / 300f, -1f, 1f);
            float heading = theta * MathF.Cos(restore) + restore + 0.25f * Noise.Fbm1(sAlong / 400f, seed + 13);
            x += MathF.Sin(heading) * Centerline.Step;
            z += MathF.Cos(heading) * Centerline.Step;
            sAlong += Centerline.Step;
        }
        c.Finish();
        return c;
    }

    static Centerline BuildTributary(ReachSettings s, Centerline main)
    {
        var c = new Centerline();
        int seed = s.Seed + 1000;
        int join = main.IndexAtZ(ConfluenceZ);
        float px = main.X[join], pz = main.Z[join];
        float x = s.SizeX + 40f, z = ConfluenceZ - 380f;
        float phase = 0f, sAlong = 0f;
        for (int guard = 0; guard < 6000; guard++)
        {
            float dx = px - x, dz = pz - z, dist = MathF.Sqrt(dx * dx + dz * dz);
            float joinHalf = MathF.Min(main.HalfLeft[join], main.HalfRight[join]);
            c.Add(x, z, 7f, 7f, 1.5f);
            if (dist < joinHalf * 0.5f || (dist < 120f && InsideMain(main, join, x, z))) break;
            // Aim at the junction; near it, turn to follow the main flow (tributaries join at an acute angle).
            float w = 1.6f * MathUtil.Smoothstep(160f, 30f, dist);
            float tx = dx / dist + main.TanX[join] * w, tz = dz / dist + main.TanZ[join] * w;
            float aim = MathF.Atan2(tx, tz);
            float amp = 0.6f * MathUtil.Smoothstep(60f, 260f, dist) * MathUtil.Smoothstep(0f, 120f, sAlong);
            phase += MathF.PI * 2f * Centerline.Step / 150f;
            float heading = aim + amp * MathF.Sin(phase);
            x += MathF.Sin(heading) * Centerline.Step;
            z += MathF.Cos(heading) * Centerline.Step;
            sAlong += Centerline.Step;
        }
        c.Finish();
        return c;
    }

    static bool InsideMain(Centerline m, int around, float x, float z)
    {
        int best = around; float bd = float.MaxValue;
        for (int i = Math.Max(0, around - 120); i < Math.Min(m.Count, around + 120); i++)
        {
            float d = (m.X[i] - x) * (m.X[i] - x) + (m.Z[i] - z) * (m.Z[i] - z);
            if (d < bd) { bd = d; best = i; }
        }
        float half = MathF.Min(m.HalfLeft[best], m.HalfRight[best]);
        return bd < half * half;
    }

    static void PlaceIslands(ReachSettings s, Course f)
    {
        var m = f.Main;
        var rng = new Rng(s.Seed + 2000);

        Island At(int i, float lateral, float up, float down, float half, float height, float rough)
        {
            var (rx, rz) = m.Right(i);
            var isl = new Island
            {
                X = m.X[i] + rx * lateral, Z = m.Z[i] + rz * lateral, DirX = m.TanX[i], DirZ = m.TanZ[i],
                LengthUp = up, LengthDown = down, HalfWidth = half, MaxHeight = height, Roughness = rough,
                Seed = s.Seed + 2100 + f.Islands.Count * 17,
            };
            f.Islands.Add(isl);
            return isl;
        }

        // Tom's sketch #1: the river-lake with one big island near its left shore and a few islets on the right.
        int iL = m.IndexAtZ(1400f);
        float hl = m.HalfLeft[iL], hr = m.HalfRight[iL];
        // Narrow channel on the right of the flow (screen left on the map, as in the sketch), open lake on the left.
        float half = Math.Clamp(hr * 0.22f, 22f, 48f);
        float lat = hr - 26f - half;
        At(iL, lat, 130f, 220f, half, 3.4f, 0.16f);
        for (int k = 0, tries = 0; k < 3 && tries < 60; tries++)
        {
            int i = iL + rng.Range(-70, 100);
            float r = rng.Range(5f, 13f);
            float l = -rng.Range(20f, m.HalfLeft[i] - 30f) + MathF.Min(0f, lat - half - 40f) * 0.3f;
            var (rx, rz) = m.Right(i);
            float cx = m.X[i] + rx * l, cz = m.Z[i] + rz * l;
            bool clear = true;
            foreach (var o in f.Islands) if (o.Sdf(cx, cz) < r + 18f) { clear = false; break; }
            if (!clear) continue;
            At(i, l, r, r * 1.7f, r * 0.85f, rng.Range(1.1f, 2.2f), 0.3f);
            k++;
        }

        // A low gravel bar on the inside of the sharpest bend in the upper meanders.
        int best = -1; float bestBend = 0f;
        for (int i = m.IndexAtZ(350f); i < m.IndexAtZ(900f); i++)
        {
            float b = MathF.Sqrt(m.BendX[i] * m.BendX[i] + m.BendZ[i] * m.BendZ[i]);
            if (b > bestBend) { bestBend = b; best = i; }
        }
        if (best >= 0)
        {
            var (rx, rz) = m.Right(best);
            float side = MathF.Sign(m.BendX[best] * rx + m.BendZ[best] * rz); // +1: inner bank on the right
            float hw = side > 0 ? m.HalfRight[best] : m.HalfLeft[best];
            At(best, side * hw * 0.55f, 18f, 34f, 5.5f, 0.45f, 0.25f);
        }

        // A forested island splitting the channel below the confluence (anabranch).
        int iS = m.IndexAtZ(0.5f * (SplitA1 + SplitB0));
        At(iS, 0f, 55f, 100f, 13f, 2.6f, 0.2f);
    }

    static void PlaceRocks(ReachSettings s, Course f)
    {
        var m = f.Main;
        var rng = new Rng(s.Seed + 3000);
        int a = m.IndexAtZ(RiffleA0), b = m.IndexAtZ(RiffleB1);
        for (int k = 0; k < 34; k++)
        {
            int i = rng.Range(a, b);
            var (rx, rz) = m.Right(i);
            float l = rng.Range(-0.85f, 0.85f) * (rng.Next01() < 0.5f ? m.HalfLeft[i] : m.HalfRight[i]);
            f.Rocks.Add(new Rock
            {
                X = m.X[i] + rx * l, Z = m.Z[i] + rz * l, Radius = rng.Range(0.5f, 1.9f),
                Top = rng.Range(-0.5f, 0.5f), Seed = s.Seed + 3100 + k,
            });
        }
    }
}
