using System;

namespace River.Core;

/// <summary>
/// Solves the steady depth-averaged river flow on the water nodes of a grid.
///
/// Model (Manning friction, gravity-driven, no inertia): unit discharge q = K·(−∇η), where η is the water surface
/// height and K = h^(5/3) / (n·√|S|) with h the depth, n Manning's roughness and S the surface slope. Mass
/// conservation ∇·q = sources gives an elliptic equation for η. K depends on |S|, so we iterate (Picard), each
/// step a symmetric positive definite system solved with Jacobi-preconditioned conjugate gradients.
///
/// What this gives for free: the current splits around islands in proportion to channel conveyance, speeds up
/// in narrows and shallows, goes slack in wide deep lakes; the surface is nearly flat across lakes and steepens
/// through riffles. What it does not give: inertia (eddies, recirculation behind obstacles) — added on top by
/// <see cref="Eddies"/>.
/// </summary>
public static class FlowSolver
{
    public struct Problem
    {
        public int Nx, Nz;
        public float Cell;          // m
        public bool[] Active;       // nodes taking part (connected water)
        public bool[] Fixed;        // Dirichlet nodes (outlet), held at FixedEta
        public float FixedEta;
        public float[] Depth;       // h (m)
        public float[] Source;      // inflow (m³/s) per node
        public float Manning;       // n
        public float RefSlope;      // slope used for the first (linear) iteration
        public float[] ConveyanceScale; // optional per-node factor on K (lee zones carry little net flow); null = 1
    }

    public sealed class Result
    {
        public float[] Eta;         // surface height on active nodes
        public int Iterations;      // total CG iterations
        public double Residual;     // final relative residual of the last solve
    }

    /// <summary>
    /// Multilevel solve: the nonlinear (Picard) iterations run on a grid coarsened 2× per level, and each finer
    /// level starts from the coarser answer, so the expensive fine grid needs one or two CG solves from a good
    /// start instead of several from scratch. Same equations, same final tolerance.
    /// </summary>
    public static Result SolveMultilevel(in Problem p, Action<string> log = null, int depth = 0)
    {
        bool canCoarsen = p.Nx % 2 == 1 && p.Nz % 2 == 1 && p.Nx * p.Nz > 60000;
        if (!canCoarsen) return Solve(p, 3, log, null, depth);
        var coarse = Coarsen(p);
        var cr = SolveMultilevel(coarse, log, depth + 1);
        var start = Prolong(cr.Eta, coarse, p);
        var fine = Solve(p, depth == 0 ? 2 : 1, log, start, depth);
        fine.Iterations += cr.Iterations;
        return fine;
    }

    /// <summary>Every second node in both directions (grid dimensions must be odd).</summary>
    static Problem Coarsen(in Problem p)
    {
        int cx = (p.Nx + 1) / 2, cz = (p.Nz + 1) / 2, ct = cx * cz;
        var c = new Problem
        {
            Nx = cx, Nz = cz, Cell = p.Cell * 2f, FixedEta = p.FixedEta, Manning = p.Manning, RefSlope = p.RefSlope,
            Active = new bool[ct], Fixed = new bool[ct], Depth = new float[ct], Source = new float[ct],
            ConveyanceScale = p.ConveyanceScale != null ? new float[ct] : null,
        };
        for (int z = 0; z < cz; z++)
            for (int x = 0; x < cx; x++)
            {
                int fi = (2 * z) * p.Nx + 2 * x, ci = z * cx + x;
                c.Active[ci] = p.Active[fi]; c.Fixed[ci] = p.Fixed[fi]; c.Depth[ci] = p.Depth[fi];
                if (c.ConveyanceScale != null) c.ConveyanceScale[ci] = p.ConveyanceScale[fi];
            }
        // Coarse nodes cut off from the outlet would make the system singular: drop them.
        var reach = new bool[ct];
        var stack = new System.Collections.Generic.Stack<int>();
        for (int i = 0; i < ct; i++) if (c.Active[i] && c.Fixed[i]) { reach[i] = true; stack.Push(i); }
        while (stack.Count > 0)
        {
            int i = stack.Pop(), ix = i % cx, iz = i / cx;
            void Visit(int j) { if (c.Active[j] && !reach[j]) { reach[j] = true; stack.Push(j); } }
            if (ix > 0) Visit(i - 1); if (ix < cx - 1) Visit(i + 1);
            if (iz > 0) Visit(i - cx); if (iz < cz - 1) Visit(i + cx);
        }
        for (int i = 0; i < ct; i++) c.Active[i] = reach[i];
        // Move each fine source to the nearest reachable coarse node.
        for (int fi = 0; fi < p.Source.Length; fi++)
        {
            if (p.Source[fi] == 0f) continue;
            int fx = fi % p.Nx, fz = fi / p.Nx, best = -1, bd = int.MaxValue;
            for (int dz = -2; dz <= 2; dz++)
                for (int dx = -2; dx <= 2; dx++)
                {
                    int x = (fx + 1) / 2 + dx, z = (fz + 1) / 2 + dz;
                    if (x < 0 || z < 0 || x >= cx || z >= cz || !c.Active[z * cx + x]) continue;
                    int d = (2 * x - fx) * (2 * x - fx) + (2 * z - fz) * (2 * z - fz);
                    if (d < bd) { bd = d; best = z * cx + x; }
                }
            if (best >= 0) c.Source[best] += p.Source[fi];
        }
        return c;
    }

    /// <summary>Coarse surface → starting surface on the fine grid (average of the active coarse corners).</summary>
    static float[] Prolong(float[] ce, in Problem c, in Problem f)
    {
        var e = new float[f.Nx * f.Nz];
        for (int z = 0; z < f.Nz; z++)
            for (int x = 0; x < f.Nx; x++)
            {
                float sum = 0f; int cnt = 0;
                for (int dz = 0; dz <= (z & 1); dz++)
                    for (int dx = 0; dx <= (x & 1); dx++)
                    {
                        int ci = (z / 2 + dz) * c.Nx + x / 2 + dx;
                        if (c.Active[ci]) { sum += ce[ci]; cnt++; }
                    }
                e[z * f.Nx + x] = cnt > 0 ? sum / cnt : float.NaN;
            }
        // Fine nodes with no active coarse corner (narrow spots): take a neighbour's value.
        for (int pass = 0; pass < 6; pass++)
            for (int i = 0; i < e.Length; i++)
            {
                if (!float.IsNaN(e[i])) continue;
                int ix = i % f.Nx, iz = i / f.Nx;
                float v = float.NaN;
                if (ix > 0 && !float.IsNaN(e[i - 1])) v = e[i - 1];
                else if (ix < f.Nx - 1 && !float.IsNaN(e[i + 1])) v = e[i + 1];
                else if (iz > 0 && !float.IsNaN(e[i - f.Nx])) v = e[i - f.Nx];
                else if (iz < f.Nz - 1 && !float.IsNaN(e[i + f.Nx])) v = e[i + f.Nx];
                e[i] = v;
            }
        for (int i = 0; i < e.Length; i++) if (float.IsNaN(e[i])) e[i] = f.FixedEta;
        return e;
    }

    public static Result Solve(in Problem p, int picardSteps = 4, Action<string> log = null, float[] initialEta = null, int level = 0)
    {
        int nx = p.Nx, nz = p.Nz, total = nx * nz;
        // Compact indexing of unknowns.
        var ui = new int[total];
        int n = 0;
        for (int i = 0; i < total; i++) ui[i] = p.Active[i] && !p.Fixed[i] ? n++ : -1;
        var node = new int[n];
        for (int i = 0; i < total; i++) if (ui[i] >= 0) node[ui[i]] = i;

        var eta = new float[total];
        // Initial guess: a coarser solution if we have one, else the fixed level everywhere.
        for (int i = 0; i < total; i++) eta[i] = initialEta != null ? initialEta[i] : p.FixedEta;

        var kNode = new float[total];
        var x = new double[n];
        int totalIters = 0; double lastRes = 0;

        for (int step = 0; step < picardSteps; step++)
        {
            // Node conveyance K for this step.
            for (int i = 0; i < total; i++)
            {
                if (!p.Active[i]) { kNode[i] = 0; continue; }
                float h = MathF.Max(p.Depth[i], 0.03f);
                float s = step == 0 && initialEta == null ? p.RefSlope : Slope(eta, p.Active, nx, nz, i, p.Cell);
                s = Math.Clamp(s, p.RefSlope * 0.15f, p.RefSlope * 60f);
                kNode[i] = MathF.Pow(h, 5f / 3f) / (p.Manning * MathF.Sqrt(s)) * (p.ConveyanceScale?[i] ?? 1f);
            }

            // Assemble: diag, 4 neighbour weights, rhs.
            var diag = new double[n];
            var nb = new int[n * 4];
            var w = new double[n * 4];
            var b = new double[n];
            for (int u = 0; u < n; u++)
            {
                int i = node[u], ix = i % nx, iz = i / nx;
                b[u] = p.Source[i];
                for (int k = 0; k < 4; k++)
                {
                    int jx = ix + (k == 0 ? 1 : k == 1 ? -1 : 0);
                    int jz = iz + (k == 2 ? 1 : k == 3 ? -1 : 0);
                    nb[u * 4 + k] = -1;
                    if (jx < 0 || jz < 0 || jx >= nx || jz >= nz) continue;
                    int j = jz * nx + jx;
                    if (!p.Active[j]) continue;
                    double kf = Harmonic(kNode[i], kNode[j]);
                    diag[u] += kf;
                    // We solve for δ = η − FixedEta, so a fixed neighbour (δ = 0) adds nothing to the rhs.
                    if (!p.Fixed[j]) { nb[u * 4 + k] = ui[j]; w[u * 4 + k] = kf; }
                }
            }

            for (int u = 0; u < n; u++) x[u] = eta[node[u]] - p.FixedEta;
            var (iters, res) = Pcg(n, diag, nb, w, b, x, step == picardSteps - 1 && level == 0 ? 1e-7 : 1e-5, 20000);
            totalIters += iters; lastRes = res;
            for (int u = 0; u < n; u++) eta[node[u]] = p.FixedEta + (float)x[u];
            log?.Invoke($"flow level {level} ({nx}×{nz}) step {step}: {iters} CG iterations, residual {res:E2}");
        }

        return new Result { Eta = eta, Iterations = totalIters, Residual = lastRes };
    }

    static double Harmonic(float a, float b) => a + b > 0 ? 2.0 * a * b / (a + b) : 0.0;

    /// <summary>|∇η| at node i from neighbouring active nodes (one-sided where needed).</summary>
    public static float Slope(float[] eta, bool[] active, int nx, int nz, int i, float cell)
    {
        int ix = i % nx, iz = i / nx;
        float gx = Diff(eta, active, nx, nz, ix, iz, 1, 0, cell);
        float gz = Diff(eta, active, nx, nz, ix, iz, 0, 1, cell);
        return MathF.Sqrt(gx * gx + gz * gz);
    }

    static float Diff(float[] eta, bool[] active, int nx, int nz, int ix, int iz, int dx, int dz, float cell)
    {
        int i = iz * nx + ix;
        int ax = ix + dx, az = iz + dz, bx = ix - dx, bz = iz - dz;
        bool hasA = ax < nx && az < nz && active[az * nx + ax];
        bool hasB = bx >= 0 && bz >= 0 && active[bz * nx + bx];
        if (hasA && hasB) return (eta[az * nx + ax] - eta[bz * nx + bx]) / (2f * cell);
        if (hasA) return (eta[az * nx + ax] - eta[i]) / cell;
        if (hasB) return (eta[i] - eta[bz * nx + bx]) / cell;
        return 0f;
    }

    /// <summary>Jacobi-preconditioned conjugate gradients for the assembled 5-point system.</summary>
    internal static (int, double) Pcg(int n, double[] diag, int[] nb, double[] w, double[] b, double[] x, double tol, int maxIter)
    {
        var r = new double[n]; var z = new double[n]; var pv = new double[n]; var ap = new double[n];
        double bnorm = 0;
        for (int u = 0; u < n; u++) bnorm += b[u] * b[u];
        bnorm = Math.Sqrt(bnorm);
        if (bnorm == 0) bnorm = 1;

        Apply(n, diag, nb, w, x, ap);
        double rz = 0;
        for (int u = 0; u < n; u++) { r[u] = b[u] - ap[u]; z[u] = r[u] / diag[u]; pv[u] = z[u]; rz += r[u] * z[u]; }

        int it = 0; double res = 1;
        for (; it < maxIter; it++)
        {
            double rr = 0;
            for (int u = 0; u < n; u++) rr += r[u] * r[u];
            res = Math.Sqrt(rr) / bnorm;
            if (res < tol) break;

            Apply(n, diag, nb, w, pv, ap);
            double pap = 0;
            for (int u = 0; u < n; u++) pap += pv[u] * ap[u];
            double alpha = rz / pap;
            double rzNew = 0;
            for (int u = 0; u < n; u++)
            {
                x[u] += alpha * pv[u];
                r[u] -= alpha * ap[u];
                z[u] = r[u] / diag[u];
                rzNew += r[u] * z[u];
            }
            double beta = rzNew / rz;
            rz = rzNew;
            for (int u = 0; u < n; u++) pv[u] = z[u] + beta * pv[u];
        }
        return (it, res);
    }

    static void Apply(int n, double[] diag, int[] nb, double[] w, double[] v, double[] outv)
    {
        System.Threading.Tasks.Parallel.For(0, (n + 4095) / 4096, block =>
        {
            int end = Math.Min(n, (block + 1) * 4096);
            for (int u = block * 4096; u < end; u++)
            {
                double s = diag[u] * v[u];
                int o = u * 4;
                for (int k = 0; k < 4; k++) { int j = nb[o + k]; if (j >= 0) s -= w[o + k] * v[j]; }
                outv[u] = s;
            }
        });
    }
}
