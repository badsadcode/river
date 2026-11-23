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
    public int ReferencePicardSteps = 0; // tests: > 0 = single-level solve with this many nonlinear steps
}

/// <summary>
/// Generates one segment of river, river first: centre lines → water shape (with islands) → bed →
/// flow (surface + current) → eddies → banks, floodplain and valley → foam.
/// A pure function of its <see cref="SegmentSpec"/> (the slice of the course on this grid, docs/STREAMING.md §4).
/// The prototype reach is one segment of <see cref="PrototypeCourse"/>.
/// </summary>
public static class ReachGenerator
{
    /// <summary>The prototype reach (one segment covering the prototype course).</summary>
    public static ReachField Generate(ReachSettings s, Action<string> log = null) => Generate(PrototypeCourse.Spec(s), log);

    public static ReachField Generate(SegmentSpec s, Action<string> log = null)
    {
        var sw = Stopwatch.StartNew();
        void Log(string m) => log?.Invoke($"[{sw.ElapsedMilliseconds,6} ms] {m}");
        // World position of a grid point, for noise: overlapping segments must sample the same noise.
        // (The prototype's identity frame gives exactly the grid coordinates.)
        var frame = s.Frame;
        (float, float) Wn(float x, float z) { var (a, b) = frame.ToWorld(x, z); return ((float)a, (float)b); }

        int nx = (int)MathF.Round(s.SizeX / s.Cell) + 1, nz = (int)MathF.Round(s.SizeZ / s.Cell) + 1, total = nx * nz;
        var f = new ReachField
        {
            Nx = nx, Nz = nz, Cell = s.Cell, Seed = s.Seed, Frame = s.Frame,
            Water = new bool[total], Still = new bool[total], Height = new float[total], Eta = new float[total],
            Depth = new float[total], VelX = new float[total], VelZ = new float[total], Foam = new float[total],
            ShoreDist = new float[total],
        };

        // ------------------------------------------------------------ 1. centre lines and islands (from the spec)
        f.Main = s.Main;
        f.Tributary = s.Tributary;
        f.Islands.AddRange(s.Islands);
        f.Rocks.AddRange(s.Rocks);
        f.SideChannels.AddRange(s.SideChannels);
        f.Snags.AddRange(s.Snags);
        Log($"centre lines: main {f.Main.Length:F0} m, tributary {f.Tributary.Length:F0} m, {f.SideChannels.Count} side channels, " +
            $"{s.StillWaters.Count} still waters, {f.Islands.Count} islands, {f.Rocks.Count} rocks, {f.Snags.Count} snags");

        // ------------------------------------------------------------ 2. nearest centre-line sample per node
        // Lines: 0 main, 1 tributary, then side channels (flowing), then still waters (oxbows, at their own level).
        var lines = new List<Centerline> { f.Main, f.Tributary };
        lines.AddRange(s.SideChannels);
        int firstStill = lines.Count;
        foreach (var still in s.StillWaters) lines.Add(still.Line);
        int nl = lines.Count;
        var nearIdx = new int[nl][]; var nearD2 = new float[nl][];
        for (int l = 0; l < nl; l++)
        {
            nearIdx[l] = new int[total]; nearD2[l] = new float[total];
            Stamp(lines[l], f, l == 0 ? 110f : 60f, nearIdx[l], nearD2[l]);
        }
        var mainIdx = nearIdx[0]; var tribIdx = nearIdx[1];
        Log("stamped centre lines");

        // ------------------------------------------------------------ 3. water mask
        // A node belongs to the first line it is inside of (main, tributary, side channels, still waters); outside
        // all of them, to the nearest line (ties: the earlier one).
        var insideMain = new bool[total]; var insideTrib = new bool[total];
        var lateral = new float[total]; var inner = new float[total]; var lineOf = new byte[total];
        var stillLevel = new float[total];
        Par.For(0, nz, iz =>
        {
            for (int ix = 0; ix < nx; ix++)
            {
                int i = iz * nx + ix;
                float x = ix * s.Cell, z = iz * s.Cell;
                int best = -1;
                float nearD = float.MaxValue, bestN = 0f, bestInn = 0f, nearN = 0f, nearInn = 0f;
                for (int l = 0; l < nl; l++)
                {
                    bool ins = Inside(lines[l], nearIdx[l][i], x, z, out float n, out float inn);
                    if (l == 0) { insideMain[i] = ins; nearN = n; nearInn = inn; }
                    else if (l == 1) insideTrib[i] = ins;
                    if (ins && best < 0) { best = l; bestN = n; bestInn = inn; }
                    if (nearD2[l][i] < nearD) { nearD = nearD2[l][i]; nearN = n; nearInn = inn; lineOf[i] = (byte)l; }
                }
                bool water = best >= 0;
                if (water) lineOf[i] = (byte)best;
                lateral[i] = water ? bestN : nearN;
                inner[i] = water ? bestInn : nearInn;
                f.Water[i] = water;
                stillLevel[i] = water && best >= firstStill ? s.StillWaters[best - firstStill].Level : float.NaN;
            }
        });
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
            var (wx, wz) = Wn(x, z);
            if (insideMain[i]) h = MathF.Max(h, ProfileDepth(f.Main, mainIdx[i], x, z, t, s.Seed, wx, wz));
            if (insideTrib[i]) h = MathF.Max(h, ProfileDepth(f.Tributary, tribIdx[i], x, z, t, s.Seed + 5, wx, wz));
            bool any = insideMain[i] || insideTrib[i];
            for (int l = 2; l < nl; l++)
                if (Inside(lines[l], nearIdx[l][i], x, z, out _, out _))
                {
                    h = MathF.Max(h, ProfileDepth(lines[l], nearIdx[l][i], x, z, t, s.Seed + 5 + 7 * l, wx, wz));
                    any = true;
                }
            if (!any) h = 0.3f;
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
        // Inflows: each enters through one grid edge, inside its own channel, spread by conveyance.
        float inMain = 0f, inTrib = 0f;
        foreach (var inflow in s.Inflows)
        {
            var inside = inflow.Line == 0 ? insideMain : insideTrib;
            bool OnEdge(int i) => inflow.Edge switch
            {
                GridEdge.Upstream => i / nx == 0,
                GridEdge.Downstream => i / nx == nz - 1,
                GridEdge.Left => i % nx == 0,
                _ => i % nx == nx - 1,
            };
            float q = Distribute(f, source, connected, i => OnEdge(i) && inside[i], inflow.Discharge);
            if (inflow.Line == 0) inMain += q; else inTrib += q;
        }
        f.InflowTotal = inMain + inTrib;
        // Water cut off from the outlet: still waters (oxbow lakes, placed deliberately with their own level) stay;
        // slivers left by the layout are turned into land.
        int stillCount = 0, pondCount = 0;
        for (int i = 0; i < total; i++)
        {
            if (!f.Water[i] || connected[i]) continue;
            if (!float.IsNaN(stillLevel[i])) { f.Still[i] = true; pondCount++; continue; }
            f.Water[i] = false; f.Depth[i] = 0f; stillCount++;
        }
        if (stillCount > 0)
        {
            Edt.Compute(f.Water, nx, nz, distToWater, nearestWater);
            for (int i = 0; i < total; i++) land[i] = !f.Water[i];
            Edt.Compute(land, nx, nz, distToLand, nearestLand);
            for (int i = 0; i < total; i++)
                f.ShoreDist[i] = f.Water[i] ? -(distToLand[i] - 0.5f) * s.Cell : (distToWater[i] - 0.5f) * s.Cell;
        }
        Log($"inflow main {inMain:F1} m³/s, tributary {inTrib:F1} m³/s; {pondCount} still-water nodes; {stillCount} isolated water nodes turned to land");

        // ------------------------------------------------------------ 7. flow: surface and current
        var problem = new FlowSolver.Problem
        {
            Nx = nx, Nz = nz, Cell = s.Cell, Active = connected, Fixed = fixedNode, FixedEta = s.OutletEta,
            Depth = f.Depth, Source = source, Manning = s.Manning, RefSlope = s.RefSlope,
        };
        var res = s.ReferencePicardSteps > 0 ? FlowSolver.Solve(problem, s.ReferencePicardSteps, m => Log(m)) : FlowSolver.SolveMultilevel(problem, m => Log(m));
        Array.Copy(res.Eta, f.Eta, total);
        ComputeVelocity(f, connected, s, null);
        Log($"flow solved ({res.Iterations} CG iterations), outflow {f.OutflowMeasured:F1} m³/s");

        // ------------------------------------------------------------ 7b. eddies (docs/DECISIONS.md D11)
        // Lee zones behind obstacles carry little net flow (the main current shoots past them): re-solve with their
        // conveyance cut, starting from the answer above, then add the recirculation inside them.
        var lineArr = lines.ToArray();
        var lee = Eddies.LeeZones(f, connected, lineArr, nearIdx, lineOf, s.Manning);
        var keep = new float[total];
        for (int i = 0; i < total; i++) keep[i] = 1f - Eddies.LeeBlockage * lee[i];
        problem.ConveyanceScale = keep;
        var res2 = FlowSolver.Solve(problem, 1, m => Log("lee " + m), f.Eta);
        Array.Copy(res2.Eta, f.Eta, total);
        ComputeVelocity(f, connected, s, keep);

        // ------------------------------------------------------------ 7c. calibration to the course (streaming segments)
        // The 1D course can't see every 2D loss (bends, bed roughness, lee zones, rocks), so a segment's own drop
        // differs from the course's by ~15–25 %, which would leave a step at every seam. Measure the ratio r and
        // re-solve once from the current answer with conveyance × r: one linear solve with K taken at the old slopes
        // lands on the target drop, and the matching consistent conveyance at the new slopes is × √r (used for the
        // velocities, so face fluxes equal the solve's and mass stays conserved).
        if (!float.IsNaN(s.InletEtaTarget))
        {
            float inEta = DeepestEtaInRow(f, connected, 1);
            float r = (inEta - s.OutletEta) / MathF.Max(0.01f, s.InletEtaTarget - s.OutletEta);
            if (!float.IsNaN(r) && MathF.Abs(r - 1f) > 0.002f)
            {
                r = Math.Clamp(r, 0.5f, 2f);
                var solveScale = new float[total]; var velScale = new float[total];
                float sr = MathF.Sqrt(r);
                for (int i = 0; i < total; i++) { solveScale[i] = keep[i] * r; velScale[i] = keep[i] * sr; }
                problem.ConveyanceScale = solveScale;
                var res3 = FlowSolver.Solve(problem, 1, m => Log("calibration " + m), f.Eta);
                Array.Copy(res3.Eta, f.Eta, total);
                ComputeVelocity(f, connected, s, velScale);
                Log($"calibrated to the course: drop ratio {r:F3}, inlet now {DeepestEtaInRow(f, connected, 1) - s.InletEtaTarget:+0.000;-0.000} m off");

                // Strongly nonlinear segments (a rapid's boulders) miss in that one linear step. Then iterate on
                // converged solves: for converged Manning flow the drop goes as 1/K², so conveyance × √(drop ratio)
                // each time. The state above is the converged one for conveyance × √r, so start from there.
                float c = sr;
                for (int pass = 0; pass < 3; pass++)
                {
                    float inNow = DeepestEtaInRow(f, connected, 1);
                    if (MathF.Abs(inNow - s.InletEtaTarget) < 0.012f) break;
                    float r2 = (inNow - s.OutletEta) / MathF.Max(0.01f, s.InletEtaTarget - s.OutletEta);
                    if (float.IsNaN(r2)) break;
                    c = Math.Clamp(c * MathF.Sqrt(Math.Clamp(r2, 0.5f, 2f)), 0.4f, 2.5f);
                    for (int i = 0; i < total; i++) velScale[i] = keep[i] * c;
                    problem.ConveyanceScale = velScale;
                    var res4 = FlowSolver.Solve(problem, 3, m => Log("calibration+ " + m), f.Eta);
                    Array.Copy(res4.Eta, f.Eta, total);
                    ComputeVelocity(f, connected, s, velScale);
                    Log($"recalibrated (converged, pass {pass + 1}): conveyance × {c:F3}, inlet now {DeepestEtaInRow(f, connected, 1) - s.InletEtaTarget:+0.000;-0.000} m off");
                }
            }
        }
        Eddies.AddRecirculation(f, connected, lee, lineArr, nearIdx, lineOf, m => Log(m));
        Log($"flow with lee zones, outflow {f.OutflowMeasured:F1} m³/s");
        // Each rock's approaching current: the mean on a ring around it (at the rock itself the water is held back).
        for (int k = 0; k < f.Rocks.Count; k++)
        {
            var r = f.Rocks[k];
            float ringX = 0f, ringZ = 0f;
            for (int a = 0; a < 12; a++)
            {
                float ang = a * MathF.PI / 6f, rr = r.Radius * 2.5f + 2f;
                var (vx, vz) = f.SampleVelocity(r.X + MathF.Cos(ang) * rr, r.Z + MathF.Sin(ang) * rr);
                ringX += vx; ringZ += vz;
            }
            r.FlowX = ringX / 12f; r.FlowZ = ringZ / 12f;
            f.Rocks[k] = r;
        }

        // Surface trend along the valley (for far terrain and the bits of water outside the grid).
        double sz = 0, se = 0, szz = 0, sze = 0; int cnt = 0;
        for (int i = 0; i < total; i++)
        {
            if (!connected[i]) continue;
            double z = (i / nx) * s.Cell; sz += z; se += f.Eta[i]; szz += z * z; sze += z * f.Eta[i]; cnt++;
        }
        double slope = (cnt * sze - sz * se) / (cnt * szz - sz * sz);
        f.EtaTrendB = (float)slope; f.EtaTrendA = (float)((se - slope * sz) / cnt);

        // Still water: its own level. Land: η of the nearest water node (flowing or still), then smoothed with
        // distance so different waters blend.
        for (int i = 0; i < total; i++)
            if (f.Still[i]) f.Eta[i] = stillLevel[i];
        for (int i = 0; i < total; i++)
        {
            if (connected[i] || f.Still[i]) continue;
            int j = nearestWater[i];
            float e = j >= 0 && (connected[j] || f.Still[j]) ? f.Eta[j] : f.EtaTrend((i / nx) * s.Cell);
            if (float.IsNaN(e)) e = f.EtaTrend((i / nx) * s.Cell);
            f.Eta[i] = e;
        }
        SmoothLandEta(f, connected);
        Log("surface extended over land");

        // ------------------------------------------------------------ 8. terrain height
        // Bank character (inner/outer) is smoothed first: it flips where the nearest centre-line sample jumps from
        // one arm of a bend to the other, which would draw creases across the land.
        {
            var src = new float[total];
            for (int i = 0; i < total; i++)
                // (Around still water: plain banks. The land inside an oxbow's crescent is its old point bar, long
                // since grown over, not a bar of bare sand.)
                src[i] = nearIdx[lineOf[i]][i] >= 0 && lineOf[i] < firstStill ? inner[i] : 0f;
            var tmp = new float[total];
            for (int pass = 0; pass < 2; pass++) { BoxBlur(src, tmp, nx, nz, 6, true); BoxBlur(tmp, src, nx, nz, 6, false); }
            Array.Copy(src, inner, total);
        }
        int valleyHint = 0;
        for (int i = 0; i < total; i++)
        {
            float x = (i % nx) * s.Cell, z = (i / nx) * s.Cell;
            if (f.Water[i]) { f.Height[i] = f.Eta[i] - f.Depth[i]; continue; }
            var (wx, wz) = Wn(x, z);   // noise and valley walls at the world position: overlapping segments agree
            float dW = MathF.Max(f.ShoreDist[i], 0.2f);
            float inn = inner[i] * MathUtil.Smoothstep(70f, 25f, dW);
            float a = MathF.Abs(inn);
            float bankH = MathUtil.Lerp(1.3f, inn > 0 ? 0.45f : 3.1f, a) * (1f + 0.3f * Noise.Fbm(wx / 60f, wz / 60f, s.Seed + 91, 2));
            float bankW = MathUtil.Lerp(7f, inn > 0 ? 34f : 3.5f, a);
            if (s.ValleyAt != null)
            {
                // Bank character changes along the river (endless course only; the prototype stays as it was):
                // stretches of high, steep cut banks (bare earth and rock show on the slopes) and of low, broad banks.
                float high = MathUtil.Smoothstep(-0.3f, 0.35f, Noise.Fbm(wx / 420f, wz / 420f, s.Seed + 95, 2));
                bankH *= MathUtil.Lerp(0.45f, 2.3f, high);
                bankW *= MathUtil.Lerp(1.6f, 0.75f, high);
            }
            float rise = bankH * MathF.Pow(MathUtil.Clamp01(dW / bankW), MathUtil.Lerp(1f, inn < 0 ? 0.55f : 1.25f, a));
            float flood = Valley.FloodplainRelief(wx, wz, s.Seed) * MathUtil.Smoothstep(4f, 60f, dW);
            float walls, baseLevel = f.Eta[i];
            if (s.ValleyAt == null) walls = Valley.Relief(wx, wz, s.Seed, s.SizeX);      // prototype: straight valley along z
            else
            {
                var (sigma, offset) = s.ValleyAt(f.Frame.ToWorld(x, z), ref valleyHint);   // endless course: around its axis
                float g = s.GorgeAt != null ? s.GorgeAt(sigma) : 0f;
                if (g > 0.001f)
                {
                    // Gorge: a narrow rocky ledge, then a cliff of 9–25 m (bulging, jagged), then the plateau the
                    // river has cut through. Replaces the bank and the floodplain.
                    float n1 = 0.5f + 0.5f * Noise.Fbm(wx / 350f, wz / 350f, s.Seed + 97, 2);
                    float n2 = 0.5f + 0.5f * Noise.Fbm(wx / 40f, wz / 40f, s.Seed + 98, 2);
                    float cliffH = 9f + 16f * n1, ledge = 1.5f + 3f * n2, wallW = 5f + 5f * (1f - n2);
                    float u = MathUtil.Clamp01((dW - ledge) / wallW);
                    float face = u * u * (3f - 2f * u);
                    float cliff = 0.5f * MathUtil.Clamp01(dW / ledge) + cliffH * face
                                + 1.6f * Noise.Fbm(wx / 7f, wz / 7f, s.Seed + 99, 2) * 4f * u * (1f - u)
                                + 2.5f * Noise.Fbm(wx / 90f, wz / 90f, s.Seed + 96, 2) * face;
                    rise = MathUtil.Lerp(rise, cliff, g);
                    flood *= 1f - g;
                }
                walls = Valley.ReliefAround(MathF.Abs(offset), (float)sigma, wx, wz, s.Seed);
                // Far from water, stand on the course's level along the valley (shared by every segment), not on
                // this grid's nearest water.
                if (s.LevelAt != null) baseLevel = MathUtil.Lerp(f.Eta[i], s.LevelAt(sigma), MathUtil.Smoothstep(60f, 250f, dW));
            }
            f.Height[i] = baseLevel + rise + flood + walls;
        }
        foreach (var isl in f.Islands)
            ForNodesNear(f, isl.X, isl.Z, MathF.Max(isl.LengthDown, isl.LengthUp) * 1.4f + 10f, i =>
            {
                if (f.Water[i]) return;
                float x = (i % nx) * s.Cell, z = (i / nx) * s.Cell;
                float sdf = isl.Sdf(x, z);
                if (sdf > 4f) return;
                var (wx, wz) = Wn(x, z);
                float top = f.Eta[i] + 0.15f + isl.MaxHeight * MathF.Pow(MathUtil.Clamp01(-sdf / (isl.HalfWidth * 0.8f)), 0.7f)
                            + 0.25f * Noise.Fbm(wx / 14f, wz / 14f, isl.Seed, 2) * MathUtil.Clamp01(-sdf / 6f);
                float k = MathUtil.Smoothstep(4f, -2f, sdf);
                f.Height[i] = MathUtil.Lerp(f.Height[i], MathF.Min(f.Height[i], top), k);
                // Course segments: small bars stand clear of the water (the bank shape alone keeps them within a few
                // cm of it near their shores, and the water mesh, drawn 5 m onto land, then covered them).
                if (s.ValleyAt != null && sdf < 0f)
                    f.Height[i] = MathF.Max(f.Height[i], f.Eta[i] + 0.1f + MathF.Min(isl.MaxHeight, 0.6f) * MathUtil.Clamp01(-sdf / 3f));
            });
        Log("terrain");

        // ------------------------------------------------------------ 9. foam
        ComputeFoam(f, connected, s);
        Log("foam");
        return f;
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
    static float ProfileDepth(Centerline c, int k, float x, float z, float t, int seed, float wx, float wz)
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
        // Noise at the world position (wx, wz), so overlapping segments carve the same bed.
        float var = 1f + 0.12f * Noise.Fbm(wx / 45f, wz / 45f, seed + 31, 3);
        float dunes = 0.06f * Noise.Gradient(wx / 9f, wz / 9f, seed + 32) * u;
        return c.MaxDepth[k] * prof * var + dunes;
    }

    /// <summary>Water level at the deepest water node of a grid row (the river's thalweg there).</summary>
    static float DeepestEtaInRow(ReachField f, bool[] active, int row)
    {
        int best = -1; float bd = 0f;
        for (int x = 0; x < f.Nx; x++)
        {
            int i = row * f.Nx + x;
            if (active[i] && f.Depth[i] > bd) { bd = f.Depth[i]; best = i; }
        }
        return best < 0 ? float.NaN : f.Eta[best];
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

    static void ComputeVelocity(ReachField f, bool[] active, SegmentSpec s, float[] conveyanceScale)
    {
        int nx = f.Nx, nz = f.Nz;
        float Kn(int i)
        {
            float h = MathF.Max(f.Depth[i], 0.03f);
            float sl = Math.Clamp(FlowSolver.Slope(f.Eta, active, nx, nz, i, s.Cell), s.RefSlope * 0.15f, s.RefSlope * 60f);
            return MathF.Pow(h, 5f / 3f) / (s.Manning * MathF.Sqrt(sl)) * (conveyanceScale?[i] ?? 1f);
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

    internal static void BoxBlur(float[] a, float[] o, int nx, int nz, int r, bool alongX)
    {
        int n = alongX ? nx : nz, lines = alongX ? nz : nx;
        Par.For(0, lines, line =>
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

    static void ComputeFoam(ReachField f, bool[] active, SegmentSpec s)
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
            // Eddy lines: where the main current shears past a slack lee, the water folds and traps foam in a line
            // along the lee's edge. Strongest mid-way into the lee (lee·(1−lee) peaks at 0.5), scaled by how fast
            // the passing water is (barely any in the slow lake).
            if (f.Eddy != null)
            {
                float e = f.Eddy[i];
                v += 0.6f * MathUtil.Smoothstep(0.35f, 0.9f, 4f * e * (1f - e)) * MathUtil.Smoothstep(0.25f, 1.0f, sp);
            }
            src[i] = MathUtil.Clamp01(v);
        }
        // Snags: the water folds around a trunk lying in the current.
        foreach (var sn in f.Snags)
            for (float t = 0.15f * sn.Length; t <= sn.Length; t += 1f)
            {
                float px = sn.X + sn.DirX * t, pz = sn.Z + sn.DirZ * t;
                ForNodesNear(f, px, pz, sn.Radius + 1.5f, i =>
                {
                    if (!active[i]) return;
                    float x = (i % nx) * s.Cell, z = (i / nx) * s.Cell;
                    float d = MathF.Sqrt((x - px) * (x - px) + (z - pz) * (z - pz));
                    src[i] = MathF.Max(src[i], 0.7f * MathUtil.Smoothstep(sn.Radius + 1.5f, sn.Radius, d));
                });
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

        // Advect with decay: foam born at a rock or a shore trails away with the current, and in backwaters (lee
        // zones) it lasts much longer, so the recirculation gathers it into slowly turning rafts.
        var foam = (float[])src.Clone();
        var next = new float[total];
        const float dt = 2.5f;
        var decay = new float[total];
        for (int i = 0; i < total; i++) decay[i] = MathUtil.Lerp(0.93f, 0.985f, f.Eddy != null ? f.Eddy[i] : 0f);
        for (int it = 0; it < 50; it++)
        {
            Par.For(0, nz, iz =>
            {
                for (int ix = 0; ix < nx; ix++)
                {
                    int i = iz * nx + ix;
                    if (!active[i]) { next[i] = 0f; continue; }
                    float bx = ix * s.Cell - f.VelX[i] * dt, bz = iz * s.Cell - f.VelZ[i] * dt;
                    next[i] = MathF.Max(src[i], decay[i] * f.Sample(foam, bx, bz));
                }
            });
            (foam, next) = (next, foam);
        }
        Array.Copy(foam, f.Foam, total);
    }
}
