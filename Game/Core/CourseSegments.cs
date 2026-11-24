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
                case FeatureKind.Rapid: RapidRocks(c, f, spec, L); break;
                // (No rock ledges on the lip: the gaps they left in the sheet showed as dark wedges, and they read as logs.)
                case FeatureKind.Falls: spec.Falls = MakeFalls(c, f, L, LDir); break;
                case FeatureKind.Confluence:
                    var (trib, edge, q) = Tributary(c, f, frame, sizeX, sizeZ, L);
                    if (trib != null) { spec.Tributary = trib; inflows.Add(new Inflow(edge, 1, q)); }
                    break;
            }
        }
        var occupied = new List<Centerline>();          // lines placed so far (tributary, side channel, oxbows)
        if (spec.Tributary != null) occupied.Add(spec.Tributary);
        SideChannel(c, k, spec, L, occupied);
        Oxbows(c, k, spec, L, occupied);
        GorgeBoulders(c, k, spec, L);
        GravelBar(c, k, spec, L, LDir, occupied);
        Snags(c, k, spec, L, LDir);
        spec.Tributary ??= new Centerline();           // no tributary: an empty line (generator copes)
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
        // Far from the river the land doesn't drop at a waterfall in one step: it slopes down over ±150 m.
        var fallsHere = new List<CourseFeature>();
        foreach (var f in c.Features) if (f.Kind == FeatureKind.Falls && f.S1 > sa - 400.0 && f.S0 < sb + 400.0) fallsHere.Add(f);
        spec.LevelAt = sigma =>
        {
            foreach (var f in fallsHere)
                if (Math.Abs(sigma - f.Lip) < 150.0)
                {
                    float above = c.Eta[c.RiverIndexAt(f.Lip - 151.0)], below = c.Eta[c.RiverIndexAt(f.Lip + 151.0)];
                    return MathUtil.Lerp(above, below, MathUtil.Smoothstep(-150f, 150f, (float)(sigma - f.Lip)));
                }
            return c.Eta[c.RiverIndexAt(sigma)];
        };
        spec.GorgeAt = sigma => c.ReachAt(sigma).Gorge;
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
    /// A rapid's boulders (≤ 44 rocks; the water shader shows 64): two ledges across the river, rocks 3.5–5 m apart just
    /// under the surface that the water pours over, then big boulders scattered between, many breaking the surface. A chute stays clear
    /// all the way down: a winding line (±30 % of the half-width) at least 4.5 m either side, so the raft has a way
    /// through that a steersman can follow rather than a pinball table.
    /// </summary>
    static void RapidRocks(Course c, CourseFeature f, SegmentSpec spec, Func<double, double, (float, float)> L)
    {
        var rng = new Rng(f.Seed);
        int a = c.RiverIndexAt(f.S0 + 25.0), b = c.RiverIndexAt(f.S1 - 25.0);
        if (b <= a) return;
        int budget = 44 - spec.Rocks.Count, n = 0;
        float phase = rng.Range(0f, MathF.Tau);
        float Chute(int i)
        {
            float t = (float)((c.Sigma[i] - f.S0) / (f.S1 - f.S0));
            return 0.3f * MathF.Min(c.HalfLeft[i], c.HalfRight[i]) * MathF.Sin(MathF.PI * 1.3f * t + phase);
        }
        float ChuteHalf(int i) => MathF.Max(4.5f, 0.18f * MathF.Min(c.HalfLeft[i], c.HalfRight[i]));
        void Put(int i, float lat, float radius, float top)
        {
            if (n >= budget) return;
            if (MathF.Abs(lat - Chute(i)) < ChuteHalf(i) + radius) return;
            float hw = lat < 0 ? c.HalfLeft[i] : c.HalfRight[i];
            if (MathF.Abs(lat) > 0.9f * hw - radius) return;
            var (rx, rz) = c.RiverRight(i);
            var (lx, lz) = L(c.X[i] + rx * lat, c.Z[i] + rz * lat);
            foreach (var o in spec.Rocks)
                if ((o.X - lx) * (o.X - lx) + (o.Z - lz) * (o.Z - lz) < (o.Radius + radius + 0.6f) * (o.Radius + radius + 0.6f)) return;
            spec.Rocks.Add(new Rock { X = lx, Z = lz, Radius = radius, Top = top, Seed = f.Seed + 100 + n });
            n++;
        }
        // Ledges.
        foreach (float tl in new[] { 0.3f + rng.Range(-0.05f, 0.05f), 0.64f + rng.Range(-0.05f, 0.05f) })
        {
            int i = a + (int)(tl * (b - a));
            for (float lat = -c.HalfLeft[i]; lat < c.HalfRight[i]; lat += rng.Range(3.5f, 5f))
                Put(Math.Clamp(i + rng.Range(-2, 3), a, b), lat, rng.Range(0.6f, 1.1f), rng.Range(-0.5f, -0.1f));   // just under: pour-overs
        }
        // Boulders.
        for (int tries = 0; tries < 400 && n < budget; tries++)
        {
            int i = rng.Range(a, b);
            float side = rng.Next01() < 0.5f ? -1f : 1f;
            float lat = side * rng.Range(0.05f, 0.85f) * (side < 0 ? c.HalfLeft[i] : c.HalfRight[i]);
            float r = 0.7f + 1.5f * rng.Next01() * rng.Next01();
            Put(i, lat, r, rng.Range(-0.4f, 0.9f));
        }
    }

    /// <summary>
    /// Gravel and sand bars in the segment's interior (away from cuts and features):
    ///   • point bars: on the inside of sharp bends (local peaks of turning, ≥ 250 m apart), 60 % of them, sized to
    ///     the river, low (they're what's left of the inner bank at the apex);
    ///   • mid-channel bars: in wide reaches, every ~300 m half the time, low and long, a little off centre.
    /// </summary>
    static void GravelBar(Course c, int k, SegmentSpec spec,
                          Func<double, double, (float, float)> L, Func<double, double, (float, float)> LDir, List<Centerline> occupied)
    {
        int a = c.RiverIndexAt(c.Cuts[k] + 400.0), b = c.RiverIndexAt(c.Cuts[k + 1] - 400.0);
        bool NearFeature(int i) => CourseSegments.NearFeature(c, i, 100.0);
        bool Clear(Island isl, float margin)
        {
            foreach (var o in spec.Islands) if (o.Sdf(isl.X, isl.Z) < isl.HalfWidth + margin) return false;
            // Never across a side channel or its mouth.
            foreach (var l in occupied)
            {
                float reach = MathF.Max(isl.LengthUp, isl.LengthDown) + 40f;
                for (int j = 0; j < l.Count; j += 2)
                {
                    float dx = l.X[j] - isl.X, dz = l.Z[j] - isl.Z;
                    if (dx * dx + dz * dz > reach * reach) continue;
                    if (isl.Sdf(l.X[j], l.Z[j]) < MathF.Max(l.HalfLeft[j], l.HalfRight[j]) + 6f) return false;
                }
            }
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
        // Braided reaches: rows of bars every ~85 m, 2–3 slots across (staggered row to row), most of them filled,
        // splitting the shallow river into threads that part and rejoin.
        double next = c.Sigma[a];
        int row = 0;
        for (int i = a; i < b; i++)
        {
            if (c.Sigma[i] < next) continue;
            next = c.Sigma[i] + 85.0;
            if (c.ReachAt(c.Sigma[i]).Braid < 0.5f || NearFeature(i)) continue;
            row++;
            float hw = MathF.Min(c.HalfLeft[i], c.HalfRight[i]);
            float half = Math.Clamp(0.2f * hw, 5f, 15f);
            int slots = hw > 28f ? 3 : 2;
            float spacing = 1.5f * hw / slots;
            for (int p = 0; p < slots; p++)
            {
                float h3 = Noise.Hash01(i, 7 + p, c.Settings.Seed + 9250), h4 = Noise.Hash01(i, 17 + p, c.Settings.Seed + 9250);
                if (h4 > 0.75f) continue;
                float lateral = (p - (slots - 1) * 0.5f + (row % 2 == 0 ? 0.25f : -0.25f)) * spacing + (h3 - 0.5f) * 0.16f * hw;
                var bar = MakeIsland(c, i + (int)(h4 * 12f), lateral, half * (1.8f + h3), half * (4f + 2f * h3), half * (0.8f + 0.4f * h3),
                                     0.3f + 0.3f * h3, 0.4f, c.Settings.Seed + 9700 + k * 256 + n, L, LDir);
                if (!Clear(bar, 2f)) continue;
                spec.Islands.Add(bar);
                n++;
            }
        }
        // Mid-channel bars in wide reaches.
        next = c.Sigma[a];
        for (int i = a; i < b; i++)
        {
            if (c.Sigma[i] < next) continue;
            next = c.Sigma[i] + 300.0;
            var rp = c.ReachAt(c.Sigma[i]);
            if (rp.Width < 1.35f || rp.Braid >= 0.5f || NearFeature(i)) continue;
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

    // ================================================================ waterfalls (docs/DECISIONS.md D21)

    /// <summary>
    /// 2–4 low rock ledges on a waterfall's lip (just breaking the surface, wide and flat-topped rather than round):
    /// they split the curtain into separate falls. On the curved lip (FallsSpec.LipAt), a metre upstream of it.
    /// </summary>
    static void LipRocks(Course c, CourseFeature f, SegmentSpec spec, Func<double, double, (float, float)> L)
    {
        var rng = new Rng(f.Seed + 31);
        int iL = c.RiverIndexAt(f.Lip);
        float hl = c.HalfLeft[iL], hr = c.HalfRight[iL], hw = MathF.Max(hl, hr);
        int n = 1 + rng.Range(0, 3);
        for (int q = 0; q < n; q++)
        {
            float lat = -hl * 0.8f + (hl + hr) * 0.8f * (q + rng.Range(0.25f, 0.75f)) / n;
            float lip = FallsSpec.LipAt(lat, hw, f.Size, c.Settings.Seed);
            int i = c.RiverIndexAt(f.Lip + lip - 1.0);
            var (rx, rz) = c.RiverRight(i);
            var (lx, lz) = L(c.X[i] + rx * lat, c.Z[i] + rz * lat);
            spec.Rocks.Add(new Rock { X = lx, Z = lz, Radius = rng.Range(1.1f, 1.9f), Top = rng.Range(0.05f, 0.22f), Seed = f.Seed + 300 + q });
        }
    }

    static bool FallsNear(Course c, int i)
    {
        foreach (var f in c.Features)
            if (f.Kind == FeatureKind.Falls && c.Sigma[i] > f.S0 - 50.0 && c.Sigma[i] < f.S1 + 50.0) return true;
        return false;
    }

    /// <summary>
    /// A waterfall: its lip across the river at the feature's lip, the landing in the slack bay 110 m above it on
    /// the feature's side, the put-in 75 m below it, and a portage trail between them that leaves the bank, runs
    /// downstream 28 m back from the water (past the lip, where the generator ramps it down the scarp) and comes
    /// back to the water.
    /// </summary>
    static FallsSpec MakeFalls(Course c, CourseFeature f, Func<double, double, (float, float)> L, Func<double, double, (float, float)> LDir)
    {
        int iLip = c.RiverIndexAt(f.Lip), iLand = c.RiverIndexAt(f.Lip - 110.0), iPut = c.RiverIndexAt(f.Lip + 75.0);
        int side = f.Side;
        float HwSide(int i) => side > 0 ? c.HalfRight[i] : c.HalfLeft[i];
        (float, float) At(int i, float lateral)
        {
            var (rx, rz) = c.RiverRight(i);
            return L(c.X[i] + rx * side * lateral, c.Z[i] + rz * side * lateral);
        }
        var (lx, lz) = L(c.X[iLip], c.Z[iLip]);
        var (dx, dz) = LDir(MathF.Sin(c.Heading[iLip]), MathF.Cos(c.Heading[iLip]));
        var tx = new List<float>(); var tz = new List<float>();
        void Add((float x, float z) p) { tx.Add(p.x); tz.Add(p.z); }
        Add(At(iLand, HwSide(iLand) + 3f));
        for (int i = iLand; i <= iPut; i += 8) Add(At(i, HwSide(i) + 28f));
        Add(At(iPut, HwSide(iPut) + 28f));
        Add(At(iPut, HwSide(iPut) + 3f));
        var landing = At(iLand, HwSide(iLand) - 5f);
        var putIn = At(iPut, HwSide(iPut) - 6f);
        return new FallsSpec
        {
            LipX = lx, LipZ = lz, DirX = dx, DirZ = dz,
            HalfWidth = MathF.Max(c.HalfLeft[iLip], c.HalfRight[iLip]), Height = f.Size, Side = side,
            LandingX = landing.Item1, LandingZ = landing.Item2, PutInX = putIn.Item1, PutInZ = putIn.Item2,
            TrailX = tx.ToArray(), TrailZ = tz.ToArray(),
        };
    }

    // ================================================================ tier-2 variety (docs/DECISIONS.md D17)

    static bool NearFeature(Course c, int i, double margin)
    {
        foreach (var f in c.Features) if (c.Sigma[i] > f.S0 - margin && c.Sigma[i] < f.S1 + margin) return true;
        return false;
    }

    /// <summary>Distance from (x, z) to the nearest sample of a line, minus that sample's wider half-width.</summary>
    static float GapToLine(Centerline l, float x, float z)
    {
        float best = float.MaxValue;
        for (int j = 0; j < l.Count; j++)
        {
            float dx = l.X[j] - x, dz = l.Z[j] - z, d2 = dx * dx + dz * dz;
            float h = MathF.Max(l.HalfLeft[j], l.HalfRight[j]);
            if (d2 > (best + h) * (best + h)) continue;
            best = MathF.Min(best, MathF.Sqrt(d2) - h);
        }
        return best;
    }

    /// <summary>A polyline resampled every <see cref="Centerline.Step"/> m (half-width and depth per point).</summary>
    static Centerline Resample(List<(float x, float z, float half, float depth)> p)
    {
        var line = new Centerline();
        line.Add(p[0].x, p[0].z, p[0].half, p[0].half, p[0].depth);
        float acc = 0f;                                // distance walked since the last emitted point
        for (int j = 1; j < p.Count; j++)
        {
            float dx = p[j].x - p[j - 1].x, dz = p[j].z - p[j - 1].z, len = MathF.Sqrt(dx * dx + dz * dz);
            float t = Centerline.Step - acc;
            for (; t <= len; t += Centerline.Step)
            {
                float u = t / MathF.Max(len, 1e-6f);
                float h = MathUtil.Lerp(p[j - 1].half, p[j].half, u), d = MathUtil.Lerp(p[j - 1].depth, p[j].depth, u);
                line.Add(p[j - 1].x + dx * u, p[j - 1].z + dz * u, h, h, d);
            }
            acc = len - (t - Centerline.Step);
        }
        line.Finish();
        return line;
    }

    /// <summary>
    /// A side channel (anabranch), in 45 % of segments: it leaves the main river, runs parallel to it 25–70 m away
    /// behind a long wooded island, and rejoins it 400–900 m downstream. The flow solve gives it its own share of the
    /// water (a raft can take it). Plain reaches only (no features, gorges or braids), away from the cuts.
    /// </summary>
    static void SideChannel(Course c, int k, SegmentSpec spec, Func<double, double, (float, float)> L, List<Centerline> occupied)
    {
        int seed = c.Settings.Seed;
        if (Noise.Hash01(k, 1, seed + 9300) > 0.45f) return;
        int a = c.RiverIndexAt(c.Cuts[k] + 500.0), b = c.RiverIndexAt(c.Cuts[k + 1] - 500.0);
        for (int attempt = 0; attempt < 14; attempt++)
        {
            float H(int j) => Noise.Hash01(k * 32 + attempt, j, seed + 9301);
            int n = (int)((400f + 500f * H(0)) / Centerline.Step);
            if (b - a <= n + 10) return;
            int ia = a + (int)(H(1) * (b - a - n)), ib = ia + n;
            bool plain = true;
            for (int i = ia; i <= ib && plain; i += 10)
            {
                var rp = c.ReachAt(c.Sigma[i]);
                if (NearFeature(c, i, 150.0) || rp.Gorge > 0.2f || rp.Braid > 0.3f) plain = false;
            }
            if (!plain) continue;
            float hwMain = MathF.Min(c.HalfLeft[ia], c.HalfRight[ia]);
            float chanHalf = Math.Clamp(0.32f * hwMain, 5f, 14f);
            float depth = Math.Clamp(0.55f * c.MaxDepth[ia], 1.1f, 2.6f);
            float gap = 25f + 45f * H(3);
            int side0 = H(2) < 0.5f ? -1 : 1;
            for (int ts = 0; ts < 2; ts++)
            {
                int side = ts == 0 ? side0 : -side0;
                var pts = new List<(float x, float z, float half, float depth)>();
                bool ok = true;
                for (int i = ia; i <= ib && ok; i++)
                {
                    float t = (i - ia) / (float)n;
                    float p = MathUtil.Smoothstep(0f, 0.3f, t) * MathUtil.Smoothstep(1f, 0.7f, t);
                    float hwSide = side > 0 ? c.HalfRight[i] : c.HalfLeft[i];
                    float far = hwSide + chanHalf + gap;
                    float o = side * (0.35f * hwSide + (far - 0.35f * hwSide) * p
                                      + 0.25f * gap * MathF.Sin(MathF.PI * 3f * t + 6f * H(4)) * p);
                    var (rx, rz) = c.RiverRight(i);
                    var (lx, lz) = L(c.X[i] + rx * o, c.Z[i] + rz * o);
                    if (lx < 30f || lx > spec.SizeX - 30f || lz < 30f || lz > spec.SizeZ - 30f) ok = false;
                    pts.Add((lx, lz, chanHalf, depth));
                }
                if (!ok) continue;
                // No folds (offset curves fold on the inside of tight bends): consecutive steps keep their direction.
                for (int j = 2; j < pts.Count && ok; j++)
                {
                    float ax = pts[j - 1].x - pts[j - 2].x, az = pts[j - 1].z - pts[j - 2].z;
                    float bx = pts[j].x - pts[j - 1].x, bz = pts[j].z - pts[j - 1].z;
                    float la = MathF.Sqrt(ax * ax + az * az), lb = MathF.Sqrt(bx * bx + bz * bz);
                    if (lb < 0.3f || lb > 8f || (ax * bx + az * bz) < 0.6f * la * lb) ok = false;
                }
                if (!ok) continue;
                // A real island between the two: away from the main river (any arm of it) and other lines.
                for (int j = 0; j < pts.Count && ok; j += 3)
                {
                    float t = j / (float)(pts.Count - 1);
                    if (t < 0.3f || t > 0.7f) continue;
                    if (GapToLine(spec.Main, pts[j].x, pts[j].z) - chanHalf < 0.6f * gap) ok = false;
                    foreach (var l in occupied) if (GapToLine(l, pts[j].x, pts[j].z) - chanHalf < 25f) ok = false;
                }
                if (!ok) continue;
                var line = Resample(pts);
                spec.SideChannels.Add(line);
                occupied.Add(line);
                return;
            }
        }
    }

    /// <summary>
    /// Oxbow lakes: 0–2 per segment, crescent-shaped cut-off meander loops on the floodplain, on the far side of the
    /// valley from the river, at the course's level there minus 0.3–0.7 m. Still water (no current).
    /// </summary>
    static void Oxbows(Course c, int k, SegmentSpec spec, Func<double, double, (float, float)> L, List<Centerline> occupied)
    {
        int seed = c.Settings.Seed;
        float hc = Noise.Hash01(k, 1, seed + 9600);
        int want = hc < 0.35f ? 0 : hc < 0.8f ? 1 : 2, placed = 0;
        double span = c.Cuts[k + 1] - c.Cuts[k] - 900.0;
        if (span <= 0) return;
        for (int attempt = 0; attempt < 30 && placed < want; attempt++)
        {
            float H(int j) => Noise.Hash01(k * 64 + attempt, j, seed + 9601);
            double sc = c.Cuts[k] + 450.0 + span * H(0);
            if (c.ReachAt(sc).Gorge > 0.05f) continue;
            int iR = c.RiverIndexAt(sc);
            float hw = 0.5f * (c.HalfLeft[iR] + c.HalfRight[iR]);
            float R = Math.Clamp(hw * (3f + 2f * H(1)), 50f, 160f);
            float half = MathF.Min(Math.Clamp(0.7f * hw, 7f, 32f) * (0.8f + 0.3f * H(2)), 0.22f * R);
            // On the other side of the valley axis from the river, inside the floodplain (walls start ~330 m out).
            float riverOff = c.Offset[iR];
            float lo = R + half + 20f, hi = 330f - R - half;
            if (hi <= lo) continue;
            float off = -MathF.Sign(riverOff == 0f ? 1f : riverOff) * (lo + (hi - lo) * H(3));
            var (vx, vz) = c.ValleyPoint(sc);
            float hv = c.ValleyHeadingAt(sc);
            double cx = vx + MathF.Cos(hv) * off, cz = vz - MathF.Sin(hv) * off;
            float a0 = MathF.Tau * H(4), arc = (200f + 70f * H(5)) * MathF.PI / 180f;
            float dir = H(6) < 0.5f ? -1f : 1f;
            int steps = (int)(arc * R / Centerline.Step);
            float baseDepth = 1.2f + 1.3f * H(7);
            var pts = new List<(float x, float z, float half, float depth)>();
            bool ok = true;
            for (int j = 0; j <= steps && ok; j++)
            {
                float t = j / (float)steps, ang = a0 + dir * arc * t;
                float r = R * (1f + 0.12f * Noise.Fbm1(ang * 1.3f, seed + 9602 + attempt, 2));
                float taper = MathUtil.Smoothstep(0f, 0.15f, t) * MathUtil.Smoothstep(1f, 0.85f, t);
                var (lx, lz) = L(cx + MathF.Cos(ang) * r, cz + MathF.Sin(ang) * r);
                if (lx < 30f || lx > spec.SizeX - 30f || lz < 30f || lz > spec.SizeZ - 30f) ok = false;
                pts.Add((lx, lz, half * (0.45f + 0.55f * taper), baseDepth * (0.5f + 0.5f * taper)));
            }
            if (!ok) continue;
            for (int j = 0; j < pts.Count && ok; j += 4)
            {
                if (GapToLine(spec.Main, pts[j].x, pts[j].z) - pts[j].half < 35f) ok = false;
                foreach (var l in occupied) if (GapToLine(l, pts[j].x, pts[j].z) - pts[j].half < 30f) ok = false;
            }
            if (!ok) continue;
            var line = Resample(pts);
            spec.StillWaters.Add(new StillWater { Line = line, Level = c.Eta[iR] - (0.3f + 0.4f * H(8)) });
            occupied.Add(line);
            placed++;
        }
    }

    /// <summary>Boulders fallen from the cliffs, at the foot of the walls in gorges (some awash). ≤ 60 rocks a segment.</summary>
    static void GorgeBoulders(Course c, int k, SegmentSpec spec, Func<double, double, (float, float)> L)
    {
        int seed = c.Settings.Seed;
        int a = c.RiverIndexAt(c.Cuts[k] + 300.0), b = c.RiverIndexAt(c.Cuts[k + 1] - 300.0);
        int budget = 60 - spec.Rocks.Count;
        for (int i = a; i < b && budget > 0; i += 12)
        {
            if (c.ReachAt(c.Sigma[i]).Gorge < 0.6f || NearFeature(c, i, 60.0)) continue;
            float H(int j) => Noise.Hash01(i, j, seed + 9800);
            if (H(0) > 0.4f) continue;
            int side = H(1) < 0.5f ? -1 : 1;
            float hw = side > 0 ? c.HalfRight[i] : c.HalfLeft[i];
            float lat = side * hw * (0.72f + 0.25f * H(2));
            var (rx, rz) = c.RiverRight(i);
            var (lx, lz) = L(c.X[i] + rx * lat, c.Z[i] + rz * lat);
            spec.Rocks.Add(new Rock { X = lx, Z = lz, Radius = 0.7f + 1.6f * H(3) * H(3), Top = -0.3f + 1.1f * H(4), Seed = seed + 9800 + i });
            budget--;
        }
    }

    /// <summary>
    /// Snags: fallen trees, root plate on the bank, trunk lying in the water at 20–55° to the current, mostly on the
    /// outside of bends (where the bank is undercut). They reach at most 40 % of the half-width into the river.
    /// </summary>
    static void Snags(Course c, int k, SegmentSpec spec, Func<double, double, (float, float)> L, Func<double, double, (float, float)> LDir)
    {
        int seed = c.Settings.Seed;
        int a = c.RiverIndexAt(c.Cuts[k] + 60.0), b = c.RiverIndexAt(c.Cuts[k + 1] - 60.0);
        for (int i = a + 7; i < b - 7; i += 15)
        {
            if (c.ReachAt(c.Sigma[i]).Gorge > 0.5f || FallsNear(c, i)) continue;
            float H(int j) => Noise.Hash01(i, j, seed + 9400);
            float d = c.Heading[i + 6] - c.Heading[i - 6];
            float apex = MathUtil.Clamp01(MathF.Abs(d) / 0.5f);
            if (H(0) > 0.04f + 0.22f * apex) continue;
            // Turning left (d > 0): the inner bank is on the left, the undercut outer bank on the right.
            int side = MathF.Abs(d) < 0.05f ? (H(1) < 0.5f ? -1 : 1) : d > 0 ? 1 : -1;
            float hw = side > 0 ? c.HalfRight[i] : c.HalfLeft[i];
            float phi = (20f + 35f * H(2)) * MathF.PI / 180f;
            float len = MathF.Min(7f + 8f * H(3), 0.4f * hw / MathF.Sin(phi));
            if (len < 5f) continue;
            var (rx, rz) = c.RiverRight(i);
            float tx = MathF.Sin(c.Heading[i]), tz = MathF.Cos(c.Heading[i]);
            double rootX = c.X[i] + rx * side * (hw + 1.5f), rootZ = c.Z[i] + rz * side * (hw + 1.5f);
            double dx = tx * MathF.Cos(phi) - side * rx * MathF.Sin(phi), dz = tz * MathF.Cos(phi) - side * rz * MathF.Sin(phi);
            var (lx, lz) = L(rootX, rootZ);
            var (ldx, ldz) = LDir(dx, dz);
            float tipX = lx + ldx * len, tipZ = lz + ldz * len;
            bool clear = true;
            foreach (var isl in spec.Islands) if (isl.Sdf(tipX, tipZ) < 3f || isl.Sdf(lx, lz) < 3f) { clear = false; break; }
            if (!clear) continue;
            spec.Snags.Add(new Snag { X = lx, Z = lz, DirX = ldx, DirZ = ldz, Length = len, Radius = 0.25f + 0.2f * H(4), Seed = seed + 9400 + i });
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
