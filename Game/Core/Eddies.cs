using System;

namespace River.Core;

/// <summary>
/// Eddies and recirculation: the inertia the potential flow solve (<see cref="FlowSolver"/>) cannot produce.
///
/// 1. <b>Where the flow separates.</b> Water in the lee of an obstacle (an island, a point jutting out of the bank,
///    a sudden widening such as a lake inlet or a confluence corner, a rock breaking the surface) does not follow
///    the obstacle's outline: the main current shoots past and a slow, recirculating zone forms behind it. A node is
///    in such a lee if, looking back upstream <i>along its own channel at the same distance from the centre line</i>
///    (so meander bends don't fake obstacles), it finds an obstacle that sticks out far enough: the zone reaches
///    about <see cref="ReattachRatio"/>× the obstacle's protrusion downstream (reattachment behind a step), and no
///    further than bed friction allows (a shallow-wake length, ≈ depth / friction coefficient).
/// 2. <b>How it circulates.</b> Vorticity is put into each lee zone with the sense of the shear layer between the
///    fast main current and the slack lee (so a one-sided lee turns one way, and an island's wake gets a
///    counter-rotating pair). A stream function ψ, zero on every shore and around the zone, is solved from it, and
///    the eddy current is curl ψ / h. That current is divergence-free and never crosses a shore, so it moves water
///    round in circles without changing the discharge anywhere.
///
/// The result is added to the potential current: water runs upstream along the bank behind points and islands,
/// foam and ripples (which follow the current) swirl, and the raft can be caught in a backwater.
/// </summary>
public static class Eddies
{
    /// <summary>Lee length per metre of obstacle protrusion (reattachment behind a step is ~6–8 step heights).</summary>
    public const float ReattachRatio = 6f;
    /// <summary>Recirculation speed as a share of the passing main current (field data: ~0.2–0.4).</summary>
    public const float Strength = 0.35f;
    /// <summary>Protrusions beyond this (m) are treated as this big: very wide expansions don't get endless lees.</summary>
    const float MaxProtrusion = 40f;
    /// <summary>Water shallower than this (m) counts as an obstacle (rock tops at the surface).</summary>
    const float ObstacleDepth = 0.15f;

    /// <summary>Share of the channel's conveyance a full lee loses: the main current shoots past, so little net
    /// flow goes through the lee (the generator re-solves the flow with this before adding the recirculation).</summary>
    public const float LeeBlockage = 0.97f;

    static bool IsObstacle(ReachField f, bool[] active, int i) => !active[i] || f.Depth[i] < ObstacleDepth;

    /// <summary>
    /// Step 1: lee (separation zone) strength per node, 0..1, from the geometry alone; also stored in <c>f.Eddy</c>.
    /// <paramref name="lines"/>/<paramref name="nearest"/>: each channel's centre line and, per node, the index of its
    /// nearest sample (−1 if none); <paramref name="lineOf"/>: which line a node belongs to.
    /// </summary>
    public static float[] LeeZones(ReachField f, bool[] active, Centerline[] lines, int[][] nearest, byte[] lineOf, float manning)
    {
        int nx = f.Nx, nz = f.Nz, total = nx * nz;
        float cell = f.Cell;
        bool Obstacle(int i) => IsObstacle(f, active, i);
        int NodeAt(float x, float z)
        {
            int ix = (int)MathF.Round(x / cell), iz = (int)MathF.Round(z / cell);
            return ix < 0 || iz < 0 || ix >= nx || iz >= nz ? -1 : iz * nx + ix;
        }

        var lee = new float[total];
        Par.For(0, nz, iz =>
        {
            for (int ix = 0; ix < nx; ix++)
            {
                int i = iz * nx + ix;
                if (Obstacle(i)) continue;
                var c = lines[lineOf[i]];
                int k = nearest[lineOf[i]][i];
                if (k < 0) continue;
                float x = ix * cell, z = iz * cell;
                var (rx, rz) = c.Right(k);
                float n = (x - c.X[k]) * rx + (z - c.Z[k]) * rz;

                // Bed friction limits how far a lee reaches: shallow-wake length ≈ 0.25·h / c_f, c_f = g n² / h^(1/3).
                float h = MathF.Max(c.MaxDepth[k], 0.5f);
                float cf = 9.81f * manning * manning / MathF.Pow(h, 1f / 3f);
                float lFric = Math.Clamp(0.25f * h / cf, 8f, 200f);
                float reach = MathF.Min(lFric, ReattachRatio * MaxProtrusion);

                float best = 0f;
                int steps = (int)(reach / Centerline.Step);
                for (int s = 1; s <= steps; s++)
                {
                    int ks = k - s;
                    if (ks < 0) break;
                    var (sx, sz) = c.Right(ks);
                    // On the inside of a tight bend, lines at a fixed offset from the centre fold over beyond the
                    // radius of curvature: the trace means nothing there, stop.
                    if (n * (c.BendX[ks] * sx + c.BendZ[ks] * sz) > 0.6f) break;
                    int j = NodeAt(c.X[ks] + sx * n, c.Z[ks] + sz * n);
                    if (j < 0) break;
                    if (!Obstacle(j)) continue;
                    // Protrusion: how far, across the channel at that station, the obstacle reaches past us.
                    float a = MaxProtrusion;
                    for (int dir = -1; dir <= 1; dir += 2)
                        for (float off = cell; off < a; off += cell)
                        {
                            int q = NodeAt(c.X[ks] + sx * (n + dir * off), c.Z[ks] + sz * (n + dir * off));
                            if (q >= 0 && !Obstacle(q)) { a = off; break; }
                        }
                    float t = s * Centerline.Step;
                    float sep = MathUtil.Smoothstep(1f, 0.6f, t / (ReattachRatio * a));   // inside the reattachment length
                    float fric = MathUtil.Smoothstep(lFric, 0.6f * lFric, t);              // inside the friction length
                    best = MathF.Max(best, sep * fric);
                    if (best >= 1f) break;
                }
                lee[i] = best;
            }
        });
        // Soften the edges a little (shear layers are a few metres wide), keep obstacles at zero.
        {
            var tmp = new float[total];
            ReachGenerator.BoxBlur(lee, tmp, nx, nz, 1, true);
            ReachGenerator.BoxBlur(tmp, lee, nx, nz, 1, false);
            for (int i = 0; i < total; i++) if (Obstacle(i)) lee[i] = 0f;
        }
        f.Eddy = lee;
        return lee;
    }

    /// <summary>
    /// Step 2 (after the flow has been re-solved with the lees blocked): adds the recirculating current inside the
    /// lee zones to <c>f.VelX/VelZ</c>.
    /// </summary>
    public static void AddRecirculation(ReachField f, bool[] active, float[] lee, Centerline[] lines, int[][] nearest,
                                        byte[] lineOf, Action<string> log = null)
    {
        int nx = f.Nx, nz = f.Nz, total = nx * nz;
        float cell = f.Cell;
        bool Obstacle(int i) => IsObstacle(f, active, i);

        // ------------------------------------------------------------ vorticity from the shear layers
        // Speed of the passing main current: the fastest water within ~16 m (the lee itself is slack after the
        // blocked re-solve, so an average would underrate what drives the eddy).
        var speed = new float[total];
        for (int i = 0; i < total; i++)
            if (active[i]) speed[i] = MathF.Sqrt(f.VelX[i] * f.VelX[i] + f.VelZ[i] * f.VelZ[i]);
        speed = MaxFilter(MaxFilter(speed, nx, nz, 8, true), nx, nz, 8, false);

        // Shear layer = the lee's edge. Its vorticity sense: curl of the velocity deficit −U·lee·d (d = flow
        // direction), i.e. the sign of −(d_z ∂lee/∂x − d_x ∂lee/∂z).
        var shear = new bool[total];
        var sign = new float[total];
        int shearCount = 0;
        for (int i = 0; i < total; i++)
        {
            if (lee[i] <= 0.12f || lee[i] >= 0.7f || Obstacle(i)) continue;
            int ix = i % nx, iz = i / nx;
            if (ix == 0 || iz == 0 || ix == nx - 1 || iz == nz - 1) continue;
            int k = nearest[lineOf[i]][i];
            if (k < 0) continue;
            var c = lines[lineOf[i]];
            float gx = lee[i + 1] - lee[i - 1], gz = lee[i + nx] - lee[i - nx];
            float cross = -(c.TanZ[k] * gx - c.TanX[k] * gz);
            if (MathF.Abs(cross) < 0.02f) continue;
            shear[i] = true; sign[i] = MathF.Sign(cross); shearCount++;
        }
        var dShear = new float[total]; var nShear = new int[total];
        Edt.Compute(shear, nx, nz, dShear, nShear);

        // Uniform-sign vorticity across each lee, sized so the eddy turns at about Strength × the passing current:
        // a cell of width W with vorticity ω turns at ~ω·W/2 along its edges.
        var omega = new double[total];
        for (int i = 0; i < total; i++)
        {
            if (lee[i] <= 0f || nShear[i] < 0) continue;
            int j = nShear[i];
            float w = dShear[i] * cell + MathF.Max(-f.ShoreDist[i], 0f);
            omega[i] = lee[i] * sign[j] * 2f * Strength * speed[j] / MathF.Max(w, 4f);
        }

        // ------------------------------------------------------------ stream function: −∇·(∇ψ / h) = ω, ψ = 0 outside
        var region = new bool[total];
        {
            var seed = new bool[total];
            for (int i = 0; i < total; i++) seed[i] = lee[i] > 0.02f;
            var d = new float[total]; var nn = new int[total];
            Edt.Compute(seed, nx, nz, d, nn);
            for (int i = 0; i < total; i++) region[i] = d[i] <= 3f && !Obstacle(i);
        }
        float InvH(int i) => 1f / MathF.Max(f.Depth[i], 0.3f);
        var ui = new int[total];
        int nu = 0;
        for (int i = 0; i < total; i++) ui[i] = region[i] ? nu++ : -1;
        var node = new int[nu];
        for (int i = 0; i < total; i++) if (ui[i] >= 0) node[ui[i]] = i;
        var diag = new double[nu]; var nb = new int[nu * 4]; var wts = new double[nu * 4]; var b = new double[nu];
        for (int u = 0; u < nu; u++)
        {
            int i = node[u], ix = i % nx, iz = i / nx;
            b[u] = omega[i] * cell * cell;
            for (int k = 0; k < 4; k++)
            {
                nb[u * 4 + k] = -1;
                int jx = ix + (k == 0 ? 1 : k == 1 ? -1 : 0), jz = iz + (k == 2 ? 1 : k == 3 ? -1 : 0);
                if (jx < 0 || jz < 0 || jx >= nx || jz >= nz) { diag[u] += InvH(i); continue; }
                int j = jz * nx + jx;
                // Neighbours outside the region (shore, rock, open river) hold ψ = 0.
                double wf = ui[j] >= 0 ? 2.0 * InvH(i) * InvH(j) / (InvH(i) + InvH(j)) : InvH(i);
                diag[u] += wf;
                if (ui[j] >= 0) { nb[u * 4 + k] = ui[j]; wts[u * 4 + k] = wf; }
            }
        }
        var psiU = new double[nu];
        var (iters, res) = nu > 0 ? FlowSolver.Pcg(nu, diag, nb, wts, b, psiU, 1e-6, 20000) : (0, 0.0);
        var psi = new float[total];
        for (int u = 0; u < nu; u++) psi[node[u]] = (float)psiU[u];

        // ------------------------------------------------------------ eddy current = curl ψ / h, added on
        double maxEddy = 0; int reversed = 0;
        for (int i = 0; i < total; i++)
        {
            if (Obstacle(i)) continue;
            int ix = i % nx, iz = i / nx;
            if (ix == 0 || iz == 0 || ix == nx - 1 || iz == nz - 1) continue;
            float px = psi[i + nx] - psi[i - nx], pz = psi[i + 1] - psi[i - 1];
            if (px == 0f && pz == 0f) continue;
            float hh = MathF.Max(f.Depth[i], 0.3f);
            float ex = px / (2f * cell * hh), ez = -pz / (2f * cell * hh);
            // Shallow edges could spin implausibly fast: keep the eddy below the passing current.
            float es = MathF.Sqrt(ex * ex + ez * ez), cap = 0.5f * MathF.Max(speed[i], 0.05f);
            if (es > cap) { ex *= cap / es; ez *= cap / es; es = cap; }
            maxEddy = Math.Max(maxEddy, es);
            float vx = f.VelX[i] + ex, vz = f.VelZ[i] + ez;
            float sp = MathF.Sqrt(vx * vx + vz * vz);
            if (sp > 3.5f) { vx *= 3.5f / sp; vz *= 3.5f / sp; }
            int k = nearest[lineOf[i]][i];
            if (k >= 0 && vx * lines[lineOf[i]].TanX[k] + vz * lines[lineOf[i]].TanZ[k] < 0f) reversed++;
            f.VelX[i] = vx; f.VelZ[i] = vz;
        }
        log?.Invoke($"eddies: {nu} nodes in lee zones, {shearCount} shear nodes, ψ solve {iters} CG iterations " +
                    $"(residual {res:E1}), max eddy {maxEddy:F2} m/s, {reversed} nodes flowing upstream");
    }

    /// <summary>Running maximum over ±r nodes along one axis.</summary>
    static float[] MaxFilter(float[] a, int nx, int nz, int r, bool alongX)
    {
        var o = new float[a.Length];
        int n = alongX ? nx : nz, lines = alongX ? nz : nx;
        Par.For(0, lines, line =>
        {
            int At(int t) => alongX ? line * nx + t : t * nx + line;
            for (int t = 0; t < n; t++)
            {
                float m = 0f;
                for (int q = Math.Max(0, t - r); q <= Math.Min(n - 1, t + r); q++) m = MathF.Max(m, a[At(q)]);
                o[At(t)] = m;
            }
        });
        return o;
    }
}
