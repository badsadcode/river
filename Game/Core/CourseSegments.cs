using System;
using System.Collections.Generic;

namespace River.Core;

/// <summary>
/// Cuts the endless <see cref="Course"/> into segments (docs/STREAMING.md §4): segment k covers the valley from cut k
/// to cut k+1 plus <see cref="Overlap"/> on both sides, on its own grid aligned with the chord of that stretch.
/// Everything a segment contains is first built in world coordinates from the course alone (river samples, islands
/// of lakes and splits, rocks of riffles, the tributary of a confluence, a gravel bar), then moved into the grid's
/// frame. Neighbours therefore agree wherever they overlap, and any segment can be generated alone, in any order.
///
/// Boundaries (§4.1): water enters at the upstream edge with the course's discharge, spread by conveyance, and the
/// level is held at the downstream edge to the course's water level. Both come from the course, never from a
/// neighbour's solve, so there is no chain between segments.
/// </summary>
public static class CourseSegments
{
    // m beyond each cut, shared with the neighbour. 250 m (≈ 7 channel widths): water enters a grid perpendicular to
    // its edge, up to ~20° off the river's own direction, and needs that distance to settle before the cut (at 150 m
    // the neighbours' currents still differed by up to 8° near the banks).
    public const float Overlap = 250f;
    public const float Corridor = 650f;     // m either side of the valley axis on the grid
    public const float SizeStep = 32f;      // grid sizes round up to this (odd node counts coarsen several times)

    public static SegmentSpec Spec(Course c, int k)
    {
        if (k < 1) throw new ArgumentOutOfRangeException(nameof(k), "segment 0 has no upstream context yet (course starts at σ = 0)");
        while (c.Cuts.Count <= k + 1) c.ExtendTo(c.Cuts[^1] + 1000.0);
        c.ExtendTo(c.Cuts[k + 1] + 1000.0);
        var cs = c.Settings;
        double sa = c.Cuts[k] - Overlap, sb = c.Cuts[k + 1] + Overlap;

        // ------------------------------------------------------------ frame: chord-aligned box around the corridor
        var (ax, az) = c.ValleyPoint(sa);
        var (bx, bz) = c.ValleyPoint(sb);
        float heading = (float)Math.Atan2(bx - ax, bz - az);
        var f0 = new Frame(ax, az, heading);
        float length = (float)Math.Sqrt((bx - ax) * (bx - ax) + (bz - az) * (bz - az));
        float xmin = float.MaxValue, xmax = float.MinValue;
        for (double sg = sa; sg <= sb; sg += Course.ValleyStep)
        {
            var (vx, vz) = c.ValleyPoint(sg);
            float hv = c.ValleyHeadingAt(sg);
            foreach (float t in new[] { -Corridor, Corridor })
            {
                var (lx, _) = f0.ToLocal(vx + MathF.Cos(hv) * t, vz - MathF.Sin(hv) * t);
                xmin = MathF.Min(xmin, lx); xmax = MathF.Max(xmax, lx);
            }
        }
        float sizeX = MathF.Ceiling((xmax - xmin) / SizeStep) * SizeStep;
        float sizeZ = MathF.Ceiling(length / SizeStep) * SizeStep;
        var (ox, oz) = f0.ToWorld(xmin, 0f);
        var frame = new Frame(ox, oz, heading);

        (float x, float z) L(double wx, double wz) => frame.ToLocal(wx, wz);
        (float x, float z) LDir(double dx, double dz)
        {
            double co = Math.Cos(heading), si = Math.Sin(heading);
            return ((float)(dx * co - dz * si), (float)(dx * si + dz * co));
        }

        var spec = new SegmentSpec
        {
            Seed = cs.Seed, Frame = frame, SizeX = sizeX, SizeZ = sizeZ, Cell = 2f,
            Manning = cs.Manning, RefSlope = 0.0005f,
        };

        // ------------------------------------------------------------ main channel: the course's samples, in the frame
        int i0 = c.RiverIndexAt(sa - 250.0), i1 = c.RiverIndexAt(sb + 250.0);
        var main = new Centerline();
        for (int i = i0; i <= i1; i++)
        {
            var (lx, lz) = L(c.X[i], c.Z[i]);
            main.Add(lx, lz, c.HalfLeft[i], c.HalfRight[i], c.MaxDepth[i]);
        }
        main.Finish();
        spec.Main = main;

        // Outlet level: the course's level where the river crosses the grid's downstream edge; the inlet target where
        // it crosses the first grid row (the segment calibrates its friction to drop from one to the other).
        spec.OutletEta = c.Eta[i1];
        for (int i = i0; i < i1; i++)
            if (L(c.X[i + 1], c.Z[i + 1]).z >= sizeZ) { spec.OutletEta = c.Eta[i + 1]; break; }
        for (int i = i0; i < i1; i++)
            if (L(c.X[i + 1], c.Z[i + 1]).z >= spec.Cell) { spec.InletEtaTarget = c.Eta[i + 1]; break; }

        var inflows = new List<Inflow> { new(GridEdge.Upstream, 0, c.DischargeAt(sa)) };

        // ------------------------------------------------------------ features of this segment
        foreach (var f in c.Features)
        {
            if (f.Mid < c.Cuts[k] || f.Mid >= c.Cuts[k + 1]) continue;
            switch (f.Kind)
            {
                case FeatureKind.Lake: LakeIslands(c, f, spec, L, LDir); break;
                case FeatureKind.Split: spec.Islands.Add(MakeIsland(c, c.RiverIndexAt(f.Mid), 0f, 55f, 100f, 13f, 2.6f, 0.2f, f.Seed, L, LDir)); break;
                case FeatureKind.Riffle: RiffleRocks(c, f, spec, L); break;
                case FeatureKind.Confluence:
                    var (trib, edge, q) = Tributary(c, f, frame, sizeX, sizeZ, L);
                    if (trib != null) { spec.Tributary = trib; inflows.Add(new Inflow(edge, 1, q)); }
                    break;
            }
        }
        GravelBar(c, k, spec, L, LDir);
        spec.Tributary ??= new Centerline();            // no tributary: an empty line (generator copes)
        if (spec.Tributary.Count == 0) spec.Tributary.Finish();
        spec.Inflows = inflows.ToArray();

        // ------------------------------------------------------------ valley walls follow the course
        spec.ValleyAt = ((double x, double z) world, ref int hint) =>
        {
            if (hint <= 0) hint = (int)(sa / Course.ValleyStep);
            var (sigma, offset, idx) = c.ProjectOnValley(world.x, world.z, hint);
            hint = idx;
            return (sigma, offset);
        };
        spec.LevelAt = sigma => c.Eta[c.RiverIndexAt(sigma)];
        return spec;
    }

    // ================================================================ islands and rocks (world, then frame)

    static Island MakeIsland(Course c, int i, float lateral, float up, float down, float half, float height, float rough, int seed,
                             Func<double, double, (float, float)> L, Func<double, double, (float, float)> LDir)
    {
        var (rx, rz) = c.RiverRight(i);
        var (lx, lz) = L(c.X[i] + rx * lateral, c.Z[i] + rz * lateral);
        var (dx, dz) = LDir(MathF.Sin(c.Heading[i]), MathF.Cos(c.Heading[i]));
        return new Island
        {
            X = lx, Z = lz, DirX = dx, DirZ = dz, LengthUp = up, LengthDown = down, HalfWidth = half,
            MaxHeight = height, Roughness = rough, Seed = seed,
        };
    }

    /// <summary>Tom's sketch #1 in every lake: one big island near the wide shore, a few islets on the other side.</summary>
    static void LakeIslands(Course c, CourseFeature f, SegmentSpec spec,
                            Func<double, double, (float, float)> L, Func<double, double, (float, float)> LDir)
    {
        int iL = c.RiverIndexAt(f.Mid);
        float wide = f.Side > 0 ? c.HalfRight[iL] : c.HalfLeft[iL];
        float half = Math.Clamp(wide * 0.22f, 22f, 48f);
        float lat = f.Side * (wide - 26f - half);
        float len = (float)(f.S1 - f.S0);
        var big = MakeIsland(c, iL, lat, MathF.Min(130f, 0.25f * len), MathF.Min(220f, 0.4f * len), half, 3.4f, 0.16f, f.Seed, L, LDir);
        spec.Islands.Add(big);
        var rng = new Rng(f.Seed);
        for (int n = 0, tries = 0; n < 3 && tries < 60; tries++)
        {
            int i = iL + rng.Range(-70, 100);
            float r = rng.Range(5f, 13f);
            float o = f.Side > 0 ? c.HalfLeft[i] : c.HalfRight[i];
            if (o < 45f) continue;
            float l = -f.Side * rng.Range(20f, o - 30f);
            var isl = MakeIsland(c, i, l, r, r * 1.7f, r * 0.85f, rng.Range(1.1f, 2.2f), 0.3f, f.Seed + 1 + n, L, LDir);
            bool clear = true;
            foreach (var o2 in spec.Islands) if (o2.Sdf(isl.X, isl.Z) < r + 18f) { clear = false; break; }
            if (!clear) continue;
            spec.Islands.Add(isl);
            n++;
        }
    }

    static void RiffleRocks(Course c, CourseFeature f, SegmentSpec spec, Func<double, double, (float, float)> L)
    {
        var rng = new Rng(f.Seed);
        int a = c.RiverIndexAt(f.S0 + 60.0), b = c.RiverIndexAt(f.S1 - 60.0);
        int count = 20 + rng.Range(0, 15);
        for (int n = 0; n < count && b > a; n++)
        {
            int i = rng.Range(a, b);
            var (rx, rz) = c.RiverRight(i);
            float l = rng.Range(-0.85f, 0.85f) * (rng.Next01() < 0.5f ? c.HalfLeft[i] : c.HalfRight[i]);
            var (lx, lz) = L(c.X[i] + rx * l, c.Z[i] + rz * l);
            spec.Rocks.Add(new Rock { X = lx, Z = lz, Radius = rng.Range(0.5f, 1.9f), Top = rng.Range(-0.5f, 0.5f), Seed = f.Seed + 100 + n });
        }
    }

    /// <summary>
    /// Gravel and sand bars in the segment's interior (away from cuts and features):
    ///   • point bars: on the inside of sharp bends (local peaks of turning, ≥ 250 m apart), 60 % of them, sized to
    ///     the river, low (they're what's left of the inner bank at the apex);
    ///   • mid-channel bars: in wide reaches, every ~300 m half the time, low and long, a little off centre.
    /// </summary>
    static void GravelBar(Course c, int k, SegmentSpec spec,
                          Func<double, double, (float, float)> L, Func<double, double, (float, float)> LDir)
    {
        int a = c.RiverIndexAt(c.Cuts[k] + 400.0), b = c.RiverIndexAt(c.Cuts[k + 1] - 400.0);
        bool NearFeature(int i)
        {
            foreach (var f in c.Features) if (c.Sigma[i] > f.S0 - 100.0 && c.Sigma[i] < f.S1 + 100.0) return true;
            return false;
        }
        bool Clear(Island isl, float margin)
        {
            foreach (var o in spec.Islands) if (o.Sdf(isl.X, isl.Z) < isl.HalfWidth + margin) return false;
            return true;
        }
        float Turn(int i) => c.Heading[i + 6] - c.Heading[i - 6];     // turning over ±12 m
        int n = 0;
        double lastBar = double.MinValue;
        for (int i = a + 7; i < b - 7; i++)
        {
            float d = Turn(i);
            // A local peak of turning, sharp enough.
            if (MathF.Abs(d) < 0.25f || MathF.Abs(d) < MathF.Abs(Turn(i - 1)) || MathF.Abs(d) < MathF.Abs(Turn(i + 1))) continue;
            if (c.Sigma[i] - lastBar < 250.0 || NearFeature(i)) continue;
            lastBar = c.Sigma[i];
            float h = Noise.Hash01(i, 1, c.Settings.Seed + 9100);
            if (h > 0.6f) continue;
            // Heading increasing = turning towards the river's left (Centerline right = (−cos h, sin h)): inner bank on the left.
            float side = d > 0 ? -1f : 1f;
            float hw = side > 0 ? c.HalfRight[i] : c.HalfLeft[i];
            float half = Math.Clamp(0.3f * hw, 4f, 12f);
            var bar = MakeIsland(c, i, side * hw * 0.6f, half * 3f, half * 6f, half, 0.35f + 0.25f * Noise.Hash01(i, 2, c.Settings.Seed + 9100),
                                 0.3f, c.Settings.Seed + 9000 + k * 64 + n, L, LDir);
            if (!Clear(bar, 10f)) continue;
            spec.Islands.Add(bar);
            n++;
        }
        // Mid-channel bars in wide reaches.
        double next = c.Sigma[a];
        for (int i = a; i < b; i++)
        {
            if (c.Sigma[i] < next) continue;
            next = c.Sigma[i] + 300.0;
            if (c.ReachAt(c.Sigma[i]).Width < 1.35f || NearFeature(i)) continue;
            float h1 = Noise.Hash01(i, 3, c.Settings.Seed + 9200), h2 = Noise.Hash01(i, 4, c.Settings.Seed + 9200);
            if (h1 > 0.5f) continue;
            float hw = MathF.Min(c.HalfLeft[i], c.HalfRight[i]);
            float half = Math.Clamp(0.3f * hw, 4f, 10f);
            float lateral = (h2 - 0.5f) * 0.5f * hw;
            var bar = MakeIsland(c, i, lateral, half * 2.5f, half * 5f, half, 0.3f + 0.4f * h2, 0.35f, c.Settings.Seed + 9500 + k * 64 + n, L, LDir);
            if (!Clear(bar, 12f)) continue;
            spec.Islands.Add(bar);
            n++;
        }
    }

    /// <summary>
    /// The tributary of a confluence: it enters the grid through a side edge and winds to the junction, turning to
    /// join the main flow at an acute angle (as the prototype's). Discharge: the course's step at the confluence.
    /// </summary>
    static (Centerline line, GridEdge edge, float q) Tributary(Course c, CourseFeature f, Frame frame, float sizeX, float sizeZ,
                                                               Func<double, double, (float, float)> L)
    {
        int iJ = c.RiverIndexAt(f.Mid);
        double jx = c.X[iJ], jz = c.Z[iJ];
        float hv = c.ValleyHeadingAt(f.Mid);
        double nx = MathF.Cos(hv) * f.Side, nz = -MathF.Sin(hv) * f.Side;   // towards the tributary's side of the valley
        double tx = MathF.Sin(hv), tz = MathF.Cos(hv);
        float qMain = c.DischargeAt(f.Mid - 150.0), q = f.Size * qMain;
        float half = Math.Clamp(18f * MathF.Sqrt(q / 80f), 4f, 25f), depth = MathF.Max(1.1f, 2.6f * MathF.Pow(q / 80f, 0.35f));

        // Start just outside the grid on the tributary's side, 200 m upstream of the junction.
        double sx = jx - tx * 200.0, sz = jz - tz * 200.0;
        for (int n = 0; n < 60; n++)
        {
            var (lx, _) = L(sx, sz);
            if (lx < -30f || lx > sizeX + 30f) break;
            sx += nx * 50.0; sz += nz * 50.0;
        }
        var (slx, _) = L(sx, sz);
        var edge = slx < 0 ? GridEdge.Left : GridEdge.Right;

        var line = new Centerline();
        double x = sx, z = sz;
        float phase = 0f, along = 0f;
        float mainTx = MathF.Sin(c.Heading[iJ]), mainTz = MathF.Cos(c.Heading[iJ]);
        float joinHalf = MathF.Min(c.HalfLeft[iJ], c.HalfRight[iJ]);
        for (int guard = 0; guard < 4000; guard++)
        {
            var (lx, lz) = L(x, z);
            line.Add(lx, lz, half, half, depth);
            double dx = jx - x, dz = jz - z, dist = Math.Sqrt(dx * dx + dz * dz);
            if (dist < joinHalf * 0.5) break;
            if (dist < 120.0 && InsideMain(c, iJ, x, z)) break;   // reached the main channel (it turns to follow it)
            float w = 1.6f * MathUtil.Smoothstep(160f, 30f, (float)dist);
            float ax = (float)(dx / dist) + mainTx * w, az = (float)(dz / dist) + mainTz * w;
            float aim = MathF.Atan2(ax, az);
            float amp = 0.6f * MathUtil.Smoothstep(60f, 260f, (float)dist) * MathUtil.Smoothstep(0f, 120f, along);
            phase += MathF.PI * 2f * Centerline.Step / 150f;
            float h = aim + amp * MathF.Sin(phase + f.Seed);
            x += MathF.Sin(h) * Centerline.Step; z += MathF.Cos(h) * Centerline.Step;
            along += Centerline.Step;
        }
        line.Finish();
        return (line, edge, q);
    }

    static bool InsideMain(Course c, int around, double x, double z)
    {
        int best = around; double bd = double.MaxValue;
        for (int i = Math.Max(0, around - 120); i < Math.Min(c.Count, around + 120); i++)
        {
            double d = (c.X[i] - x) * (c.X[i] - x) + (c.Z[i] - z) * (c.Z[i] - z);
            if (d < bd) { bd = d; best = i; }
        }
        float half = MathF.Min(c.HalfLeft[best], c.HalfRight[best]);
        return bd < half * half;
    }
}
