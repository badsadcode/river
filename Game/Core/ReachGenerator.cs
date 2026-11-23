using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace River.Core;

public sealed class ReachSettings
{
    public int Seed = 1890;
    public float SizeX = 1200f;          // m (across the valley)
    public float SizeZ = 3200f;          // m (down the valley)
    public float Cell = 2f;              // m
    public float DischargeMain = 80f;    // m³/s entering at the top
    public float DischargeTributary = 14f;
    public float OutletEta = 20f;        // water surface height where the reach leaves the grid (m)
    public float Manning = 0.035f;       // natural channel with some gravel and weeds
    public float RefSlope = 0.0005f;     // typical surface slope (first flow iteration only)
}

/// <summary>
/// Generates a prototype river reach, river first: centre lines → water shape (with islands) → bed →
/// flow (surface + current) → banks, floodplain and valley → foam.
///
/// The prototype reach is laid out to show the feature catalogue (docs/DESIGN_PILLARS.md §4): meanders at the
/// top, a river-lake with one large island and islets (Tom's sketch #1) in the middle, a tributary joining from
/// the east, a forested island splitting the channel, and a shallow rocky riffle near the bottom.
/// Endless streaming of segments comes later; everything here is already a pure function of the seed.
/// </summary>
public static class ReachGenerator
{
    // Layout of the prototype reach along z (m).
    const float LakeA0 = 1050, LakeA1 = 1250, LakeB0 = 1550, LakeB1 = 1750;
    const float SplitA0 = 2380, SplitA1 = 2440, SplitB0 = 2560, SplitB1 = 2620;
    const float RiffleA0 = 2600 + 120, RiffleA1 = 2780, RiffleB0 = 2900, RiffleB1 = 2960;
    const float ConfluenceZ = 2250;

    /// <summary>The valley's axis (x) at z: the meander belt wanders around it, valley walls rise either side.</summary>
    public static float ValleyAxisX(float z, int seed, float sizeX) =>
        sizeX * 0.5f + 110f * Noise.Fbm1(z / 1600f, seed + 71, 2);

    /// <summary>
    /// Valley walls and hills: height added on top of the floodplain at (x, z), zero near the river.
    /// Shared with the far horizon terrain so the two meet without a seam.
    /// </summary>
    public static float ValleyRelief(float x, float z, int seed, float sizeX)
    {
        float q = MathF.Abs(x - ValleyAxisX(z, seed, sizeX));
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

    public static ReachField Generate(ReachSettings s, Action<string> log = null)
    {
        var sw = Stopwatch.StartNew();
        void Log(string m) => log?.Invoke($"[{sw.ElapsedMilliseconds,6} ms] {m}");

        int nx = (int)MathF.Round(s.SizeX / s.Cell) + 1, nz = (int)MathF.Round(s.SizeZ / s.Cell) + 1, total = nx * nz;
        var f = new ReachField
        {
            Nx = nx, Nz = nz, Cell = s.Cell, Seed = s.Seed,
            Water = new bool[total], Still = new bool[total], Height = new float[total], Eta = new float[total],
            Depth = new float[total], VelX = new float[total], VelZ = new float[total], Foam = new float[total],
            ShoreDist = new float[total],
        };

        // ------------------------------------------------------------ 1. centre lines and islands
        f.Main = BuildMain(s);
        f.Tributary = BuildTributary(s, f.Main);
        PlaceIslands(s, f);
        PlaceRocks(s, f);
        Log($"centre lines: main {f.Main.Length:F0} m, tributary {f.Tributary.Length:F0} m, {f.Islands.Count} islands, {f.Rocks.Count} rocks");

        // ------------------------------------------------------------ 2. nearest centre-line sample per node
        var mainIdx = new int[total]; var mainD2 = new float[total];
        var tribIdx = new int[total]; var tribD2 = new float[total];
        Stamp(f.Main, f, 110f, mainIdx, mainD2);
        Stamp(f.Tributary, f, 60f, tribIdx, tribD2);
        Log("stamped centre lines");

        // ------------------------------------------------------------ 3. water mask
        var insideMain = new bool[total]; var insideTrib = new bool[total];
        var lateral = new float[total]; var inner = new float[total]; var lineOf = new byte[total];
        for (int i = 0; i < total; i++)
        {
            float x = (i % nx) * s.Cell, z = (i / nx) * s.Cell;
            insideMain[i] = Inside(f.Main, mainIdx[i], x, z, out float nM, out float innM);
            insideTrib[i] = Inside(f.Tributary, tribIdx[i], x, z, out float nT, out float innT);
            bool useTrib = tribIdx[i] >= 0 && (mainIdx[i] < 0 || (insideTrib[i] && !insideMain[i]) ||
                                               (!insideMain[i] && tribD2[i] < mainD2[i]));
            lineOf[i] = (byte)(useTrib ? 1 : 0);
            lateral[i] = useTrib ? nT : nM;
            inner[i] = useTrib ? innT : innM;
            f.Water[i] = insideMain[i] || insideTrib[i];
        }
        foreach (var isl in f.Islands)
            ForNodesNear(f, isl.X, isl.Z, MathF.Max(isl.LengthDown, isl.LengthUp) * 1.4f + 10f, i =>
            {
                float x = (i % nx) * s.Cell, z = (i / nx) * s.Cell;
                if (isl.Sdf(x, z) < 0f) f.Water[i] = false;
            });
        Log("water mask");

        // ------------------------------------------------------------ 4. distance to the shore (both sides)
        var distToWater = new float[total]; var nearestWater = new int[total];
        var distToLand = new float[total]; var nearestLand = new int[total];
        Edt.Compute(f.Water, nx, nz, distToWater, nearestWater);
        var land = new bool[total];
        for (int i = 0; i < total; i++) land[i] = !f.Water[i];
        Edt.Compute(land, nx, nz, distToLand, nearestLand);
        for (int i = 0; i < total; i++)
            f.ShoreDist[i] = f.Water[i] ? -(distToLand[i] - 0.5f) * s.Cell : (distToWater[i] - 0.5f) * s.Cell;
        Log("shore distance");

        // ------------------------------------------------------------ 5. depth (bed shape relative to the surface)
        for (int i = 0; i < total; i++)
        {
            if (!f.Water[i]) continue;
            float x = (i % nx) * s.Cell, z = (i / nx) * s.Cell;
            float t = -f.ShoreDist[i];
            float h = 0f;
            if (insideMain[i]) h = MathF.Max(h, ProfileDepth(f.Main, mainIdx[i], x, z, t, s.Seed));
            if (insideTrib[i]) h = MathF.Max(h, ProfileDepth(f.Tributary, tribIdx[i], x, z, t, s.Seed + 5));
            if (!insideMain[i] && !insideTrib[i]) h = 0.3f;
            f.Depth[i] = MathF.Max(h, 0.06f);
        }
        foreach (var r in f.Rocks)
            ForNodesNear(f, r.X, r.Z, r.Radius * 1.6f, i =>
            {
                if (!f.Water[i]) return;
                float x = (i % nx) * s.Cell, z = (i / nx) * s.Cell;
                float d = MathF.Sqrt((x - r.X) * (x - r.X) + (z - r.Z) * (z - r.Z)) / (r.Radius * 1.6f);
                float rockDepth = MathF.Max(0.03f, -r.Top);   // depth of water above the rock top
                float k = MathUtil.Smoothstep(1f, 0.35f, d);
                f.Depth[i] = MathF.Min(f.Depth[i], MathUtil.Lerp(f.Depth[i], rockDepth, k));
            });
        Log("depth");

        // ------------------------------------------------------------ 6. connectivity, inflow, outlet
        var connected = new bool[total];
        var stack = new Stack<int>();
        for (int x = 0; x < nx; x++) { int i = (nz - 1) * nx + x; if (f.Water[i]) { connected[i] = true; stack.Push(i); } }
        while (stack.Count > 0)
        {
            int i = stack.Pop(), ix = i % nx, iz = i / nx;
            void Visit(int j) { if (!connected[j] && f.Water[j]) { connected[j] = true; stack.Push(j); } }
            if (ix > 0) Visit(i - 1); if (ix < nx - 1) Visit(i + 1);
            if (iz > 0) Visit(i - nx); if (iz < nz - 1) Visit(i + nx);
        }
        var source = new float[total];
        var fixedNode = new bool[total];
        for (int x = 0; x < nx; x++) if (connected[(nz - 1) * nx + x]) fixedNode[(nz - 1) * nx + x] = true;
        float inMain = Distribute(f, source, connected, i => i / nx == 0 && insideMain[i], s.DischargeMain);
        float inTrib = Distribute(f, source, connected, i => i % nx == nx - 1 && insideTrib[i], s.DischargeTributary);
        f.InflowTotal = inMain + inTrib;
        // Water cut off from the outlet (slivers left by the layout): turned into land for now. Real ponds and
        // oxbow lakes will be placed deliberately later, with their own level.
        int stillCount = 0;
        for (int i = 0; i < total; i++) if (f.Water[i] && !connected[i]) { f.Water[i] = false; f.Depth[i] = 0f; stillCount++; }
        if (stillCount > 0)
        {
            Edt.Compute(f.Water, nx, nz, distToWater, nearestWater);
            for (int i = 0; i < total; i++) land[i] = !f.Water[i];
            Edt.Compute(land, nx, nz, distToLand, nearestLand);
            for (int i = 0; i < total; i++)
                f.ShoreDist[i] = f.Water[i] ? -(distToLand[i] - 0.5f) * s.Cell : (distToWater[i] - 0.5f) * s.Cell;
        }
        Log($"inflow main {inMain:F1} m³/s, tributary {inTrib:F1} m³/s; {stillCount} isolated water nodes turned to land");

        // ------------------------------------------------------------ 7. flow: surface and current
        var problem = new FlowSolver.Problem
        {
            Nx = nx, Nz = nz, Cell = s.Cell, Active = connected, Fixed = fixedNode, FixedEta = s.OutletEta,
            Depth = f.Depth, Source = source, Manning = s.Manning, RefSlope = s.RefSlope,
        };
        var res = FlowSolver.Solve(problem, 3, m => Log(m));
        Array.Copy(res.Eta, f.Eta, total);
        ComputeVelocity(f, connected, s);
        Log($"flow solved ({res.Iterations} CG iterations), outflow {f.OutflowMeasured:F1} m³/s");

        // Surface trend along the valley (for far terrain and the bits of water outside the grid).
        double sz = 0, se = 0, szz = 0, sze = 0; int cnt = 0;
        for (int i = 0; i < total; i++)
        {
            if (!connected[i]) continue;
            double z = (i / nx) * s.Cell; sz += z; se += f.Eta[i]; szz += z * z; sze += z * f.Eta[i]; cnt++;
        }
        double slope = (cnt * sze - sz * se) / (cnt * szz - sz * sz);
        f.EtaTrendB = (float)slope; f.EtaTrendA = (float)((se - slope * sz) / cnt);

        // Still water: level of the nearest flowing water a little lower (ponds sit below the river).
        // Land: η of the nearest water node, then smoothed with distance so different waters blend.
        for (int i = 0; i < total; i++)
        {
            if (f.Still[i]) f.Eta[i] = float.NaN;
        }
        for (int i = 0; i < total; i++)
        {
            if (connected[i]) continue;
            int j = nearestWater[i];
            float e = j >= 0 && connected[j] ? f.Eta[j] : f.EtaTrend((i / nx) * s.Cell);
            if (float.IsNaN(e)) e = f.EtaTrend((i / nx) * s.Cell);
            f.Eta[i] = f.Still[i] ? e - 0.25f : e;
        }
        SmoothLandEta(f, connected);
        Log("surface extended over land");

        // ------------------------------------------------------------ 8. terrain height
        // Bank character (inner/outer) is smoothed first: it flips where the nearest centre-line sample jumps from
        // one arm of a bend to the other, which would draw creases across the land.
        {
            var src = new float[total];
            for (int i = 0; i < total; i++)
                src[i] = (lineOf[i] == 0 ? mainIdx[i] : tribIdx[i]) >= 0 ? inner[i] : 0f;
            var tmp = new float[total];
            for (int pass = 0; pass < 2; pass++) { BoxBlur(src, tmp, nx, nz, 6, true); BoxBlur(tmp, src, nx, nz, 6, false); }
            Array.Copy(src, inner, total);
        }
        for (int i = 0; i < total; i++)
        {
            float x = (i % nx) * s.Cell, z = (i / nx) * s.Cell;
            if (f.Water[i]) { f.Height[i] = f.Eta[i] - f.Depth[i]; continue; }
            float dW = MathF.Max(f.ShoreDist[i], 0.2f);
            float inn = inner[i] * MathUtil.Smoothstep(70f, 25f, dW);
            float a = MathF.Abs(inn);
            float bankH = MathUtil.Lerp(1.3f, inn > 0 ? 0.45f : 3.1f, a) * (1f + 0.3f * Noise.Fbm(x / 60f, z / 60f, s.Seed + 91, 2));
            float bankW = MathUtil.Lerp(7f, inn > 0 ? 34f : 3.5f, a);
            float rise = bankH * MathF.Pow(MathUtil.Clamp01(dW / bankW), MathUtil.Lerp(1f, inn < 0 ? 0.55f : 1.25f, a));
            float flood = FloodplainRelief(x, z, s.Seed) * MathUtil.Smoothstep(4f, 60f, dW);
            float h = f.Eta[i] + rise + flood + ValleyRelief(x, z, s.Seed, s.SizeX);
            f.Height[i] = h;
        }
        foreach (var isl in f.Islands)
            ForNodesNear(f, isl.X, isl.Z, MathF.Max(isl.LengthDown, isl.LengthUp) * 1.4f + 10f, i =>
            {
                if (f.Water[i]) return;
                float x = (i % nx) * s.Cell, z = (i / nx) * s.Cell;
                float sdf = isl.Sdf(x, z);
                if (sdf > 4f) return;
                float top = f.Eta[i] + 0.15f + isl.MaxHeight * MathF.Pow(MathUtil.Clamp01(-sdf / (isl.HalfWidth * 0.8f)), 0.7f)
                            + 0.25f * Noise.Fbm(x / 14f, z / 14f, isl.Seed, 2) * MathUtil.Clamp01(-sdf / 6f);
                float k = MathUtil.Smoothstep(4f, -2f, sdf);
                f.Height[i] = MathUtil.Lerp(f.Height[i], MathF.Min(f.Height[i], top), k);
            });
        Log("terrain");

        // ------------------------------------------------------------ 9. foam
        ComputeFoam(f, connected, s);
        Log("foam");
        return f;
    }

    // ================================================================ centre lines

    static Centerline BuildMain(ReachSettings s)
    {
        var c = new Centerline();
        int seed = s.Seed;
        float x = ValleyAxisX(-60f, seed, s.SizeX), z = -60f;
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
            float restore = -1.1f * Math.Clamp((x - ValleyAxisX(z, seed, s.SizeX)) / 300f, -1f, 1f);
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

    static void PlaceIslands(ReachSettings s, ReachField f)
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

    static void PlaceRocks(ReachSettings s, ReachField f)
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

    // ================================================================ grid helpers

    static void ForNodesNear(ReachField f, float cx, float cz, float r, Action<int> act)
    {
        int x0 = Math.Max(0, (int)((cx - r) / f.Cell)), x1 = Math.Min(f.Nx - 1, (int)((cx + r) / f.Cell) + 1);
        int z0 = Math.Max(0, (int)((cz - r) / f.Cell)), z1 = Math.Min(f.Nz - 1, (int)((cz + r) / f.Cell) + 1);
        for (int z = z0; z <= z1; z++) for (int x = x0; x <= x1; x++) act(z * f.Nx + x);
    }

    /// <summary>For every node within (half-width + extra) of the line: index of the nearest sample.</summary>
    static void Stamp(Centerline c, ReachField f, float extra, int[] idx, float[] d2)
    {
        Array.Fill(idx, -1); Array.Fill(d2, float.MaxValue);
        for (int k = 0; k < c.Count; k++)
        {
            float r = MathF.Max(c.HalfLeft[k], c.HalfRight[k]) + extra, r2 = r * r;
            float px = c.X[k], pz = c.Z[k];
            int x0 = Math.Max(0, (int)((px - r) / f.Cell)), x1 = Math.Min(f.Nx - 1, (int)((px + r) / f.Cell) + 1);
            int z0 = Math.Max(0, (int)((pz - r) / f.Cell)), z1 = Math.Min(f.Nz - 1, (int)((pz + r) / f.Cell) + 1);
            for (int z = z0; z <= z1; z++)
            {
                float dz = z * f.Cell - pz;
                for (int x = x0; x <= x1; x++)
                {
                    float dx = x * f.Cell - px, d = dx * dx + dz * dz;
                    int i = z * f.Nx + x;
                    if (d < r2 && d < d2[i]) { d2[i] = d; idx[i] = k; }
                }
            }
        }
    }

    /// <summary>
    /// Is (x, z) inside the channel around sample k? Also returns the signed lateral offset (right +) and the
    /// "innerness" of that bank: +1 on the inside of a bend (point bar), −1 on the outside (cut bank).
    /// </summary>
    static bool Inside(Centerline c, int k, float x, float z, out float lateral, out float innerness)
    {
        lateral = 0f; innerness = 0f;
        if (k < 0) return false;
        float dx = x - c.X[k], dz = z - c.Z[k];
        var (rx, rz) = c.Right(k);
        lateral = dx * rx + dz * rz;
        float along = dx * c.TanX[k] + dz * c.TanZ[k];
        float side = MathF.Sign(lateral);
        innerness = Math.Clamp((c.BendX[k] * rx + c.BendZ[k] * rz) * side * 110f, -1f, 1f);
        bool atEnd = (k == 0 && along < -1.5f) || (k == c.Count - 1 && along > 1.5f);
        return !atEnd && lateral >= -c.HalfLeft[k] && lateral <= c.HalfRight[k];
    }

    /// <summary>Cross-section depth at distance t from the shore: steep on the outside of bends, shelving inside.</summary>
    static float ProfileDepth(Centerline c, int k, float x, float z, float t, int seed)
    {
        float dx = x - c.X[k], dz = z - c.Z[k];
        var (rx, rz) = c.Right(k);
        float lateral = dx * rx + dz * rz;
        float halfSide = lateral < 0 ? c.HalfLeft[k] : c.HalfRight[k];
        float inn = Math.Clamp((c.BendX[k] * rx + c.BendZ[k] * rz) * MathF.Sign(lateral) * 110f, -1f, 1f);
        float frac = inn > 0 ? MathUtil.Lerp(0.55f, 0.95f, inn) : MathUtil.Lerp(0.55f, 0.22f, -inn);
        float tau = MathF.Max(3f, halfSide * frac);
        float u = MathUtil.Clamp01(t / tau);
        float prof = 1f - (1f - u) * (1f - u);
        float var = 1f + 0.12f * Noise.Fbm(x / 45f, z / 45f, seed + 31, 3);
        float dunes = 0.06f * Noise.Gradient(x / 9f, z / 9f, seed + 32) * u;
        return c.MaxDepth[k] * prof * var + dunes;
    }

    static float Distribute(ReachField f, float[] source, bool[] connected, Func<int, bool> pick, float q)
    {
        float wsum = 0f;
        var nodes = new List<int>();
        for (int i = 0; i < source.Length; i++)
            if (connected[i] && pick(i)) { nodes.Add(i); wsum += MathF.Pow(f.Depth[i], 5f / 3f); }
        if (nodes.Count == 0 || wsum <= 0f) return 0f;
        foreach (int i in nodes) source[i] += q * MathF.Pow(f.Depth[i], 5f / 3f) / wsum;
        return q;
    }

    static void ComputeVelocity(ReachField f, bool[] active, ReachSettings s)
    {
        int nx = f.Nx, nz = f.Nz;
        float Kn(int i)
        {
            float h = MathF.Max(f.Depth[i], 0.03f);
            float sl = Math.Clamp(FlowSolver.Slope(f.Eta, active, nx, nz, i, s.Cell), s.RefSlope * 0.15f, s.RefSlope * 60f);
            return MathF.Pow(h, 5f / 3f) / (s.Manning * MathF.Sqrt(sl));
        }
        var k = new float[nx * nz];
        for (int i = 0; i < k.Length; i++) k[i] = active[i] ? Kn(i) : 0f;

        // Face unit discharges (m²/s), +x and +z faces of each node.
        float FaceQ(int i, int j) => active[i] && active[j]
            ? (float)(2.0 * k[i] * k[j] / (k[i] + k[j])) * (f.Eta[i] - f.Eta[j]) / s.Cell : 0f;

        double outflow = 0;
        for (int i = 0; i < nx * nz; i++)
        {
            if (!active[i]) continue;
            int ix = i % nx, iz = i / nx;
            float qxp = ix < nx - 1 ? FaceQ(i, i + 1) : 0f, qxm = ix > 0 ? FaceQ(i - 1, i) : 0f;
            float qzp = iz < nz - 1 ? FaceQ(i, i + nx) : 0f, qzm = iz > 0 ? FaceQ(i - nx, i) : 0f;
            float qx = 0.5f * (qxp + qxm), qz = 0.5f * (qzp + qzm);
            // At the outlet row the outflow is whatever comes in from upstream.
            if (iz == nz - 1) { qz = qzm; outflow += qzm * s.Cell; }
            float h = MathF.Max(f.Depth[i], 0.25f);
            float vx = qx / h, vz = qz / h;
            float sp = MathF.Sqrt(vx * vx + vz * vz);
            if (sp > 3.5f) { vx *= 3.5f / sp; vz *= 3.5f / sp; }
            f.VelX[i] = vx; f.VelZ[i] = vz;
        }
        f.OutflowMeasured = (float)outflow;
    }

    static void SmoothLandEta(ReachField f, bool[] connected)
    {
        int nx = f.Nx, nz = f.Nz;
        var src = (float[])f.Eta.Clone();
        var tmp = new float[src.Length];
        var blur = new float[src.Length];
        BoxBlur(src, tmp, nx, nz, 20, true);
        BoxBlur(tmp, blur, nx, nz, 20, false);
        for (int i = 0; i < src.Length; i++)
        {
            if (f.Water[i]) continue;
            float k = MathUtil.Smoothstep(4f, 50f, f.ShoreDist[i]);
            f.Eta[i] = MathUtil.Lerp(src[i], blur[i], k);
        }
    }

    static void BoxBlur(float[] a, float[] o, int nx, int nz, int r, bool alongX)
    {
        int n = alongX ? nx : nz, lines = alongX ? nz : nx;
        System.Threading.Tasks.Parallel.For(0, lines, line =>
        {
            double sum = 0; int cnt = 0;
            int At(int t) => alongX ? line * nx + t : t * nx + line;
            for (int t = 0; t <= Math.Min(r, n - 1); t++) { sum += a[At(t)]; cnt++; }
            for (int t = 0; t < n; t++)
            {
                o[At(t)] = (float)(sum / cnt);
                int add = t + r + 1, rem = t - r;
                if (add < n) { sum += a[At(add)]; cnt++; }
                if (rem >= 0) { sum -= a[At(rem)]; cnt--; }
            }
        });
    }

    static void ComputeFoam(ReachField f, bool[] active, ReachSettings s)
    {
        int nx = f.Nx, nz = f.Nz, total = nx * nz;
        var src = new float[total];
        for (int i = 0; i < total; i++)
        {
            if (!active[i]) continue;
            int ix = i % nx, iz = i / nx;
            float vx = f.VelX[i], vz = f.VelZ[i], sp = MathF.Sqrt(vx * vx + vz * vz);
            float h = MathF.Max(f.Depth[i], 0.05f);
            float froude = sp / MathF.Sqrt(9.81f * h);
            float v = 0.55f * MathUtil.Clamp01((froude - 0.5f) / 0.6f);
            // Current pushing into a shore (upstream tips of islands, outer banks): foam lines.
            float t = -f.ShoreDist[i];
            if (t < 6f && ix > 0 && iz > 0 && ix < nx - 1 && iz < nz - 1)
            {
                float gx = f.ShoreDist[i + 1] - f.ShoreDist[i - 1], gz = f.ShoreDist[i + nx] - f.ShoreDist[i - nx];
                float gl = MathF.Sqrt(gx * gx + gz * gz);
                if (gl > 1e-4f)
                {
                    float into = (vx * gx + vz * gz) / gl;
                    v += MathUtil.Clamp01(into / 0.6f) * (1f - t / 6f) * 0.8f;
                }
            }
            src[i] = MathUtil.Clamp01(v);
        }
        foreach (var r in f.Rocks)
        {
            if (r.Top < -0.35f) continue;
            ForNodesNear(f, r.X, r.Z, r.Radius + 1.5f, i =>
            {
                if (!active[i]) return;
                float x = (i % nx) * s.Cell, z = (i / nx) * s.Cell;
                float d = MathF.Sqrt((x - r.X) * (x - r.X) + (z - r.Z) * (z - r.Z));
                src[i] = MathF.Max(src[i], MathUtil.Smoothstep(r.Radius + 1.5f, r.Radius, d));
            });
        }

        // Advect downstream with decay: foam born at a rock or a shore trails away with the current.
        var foam = (float[])src.Clone();
        var next = new float[total];
        const float dt = 2.5f, decay = 0.93f;
        for (int it = 0; it < 50; it++)
        {
            System.Threading.Tasks.Parallel.For(0, nz, iz =>
            {
                for (int ix = 0; ix < nx; ix++)
                {
                    int i = iz * nx + ix;
                    if (!active[i]) { next[i] = 0f; continue; }
                    float bx = ix * s.Cell - f.VelX[i] * dt, bz = iz * s.Cell - f.VelZ[i] * dt;
                    next[i] = MathF.Max(src[i], decay * f.Sample(foam, bx, bz));
                }
            });
            (foam, next) = (next, foam);
        }
        Array.Copy(foam, f.Foam, total);
    }
}
