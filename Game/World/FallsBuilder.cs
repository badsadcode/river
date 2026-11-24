using System;
using System.Collections.Generic;
using Godot;
using River.Core;

namespace River.World;

/// <summary>
/// What a waterfall adds to its segment (grid-local, under the segment root), D21/D22:
///   • the falling sheet: columns every 0.5 m along the curved, ragged lip the generator made, each with its own
///     amount of water (full sheets, thin threads), curving over the brink and falling along a parabola;
///   • a veil of spray just in front of it, the white churn spreading over the plunge pool, spray and a mist cloud;
///   • a post at the portage trail's head.
/// The water surface itself stops at the lip (WaterBuilder skips cells across the drop).
/// </summary>
public static class FallsBuilder
{
    public static Node3D Build(ReachField f)
    {
        var fa = f.Falls;
        var root = new Node3D { Name = "Waterfall" };
        var outcrops = Outcrops(f, fa);
        var cols = Columns(f, fa, outcrops);
        var shader = GD.Load<Shader>("res://Game/Shaders/Falls.gdshader");
        float drop = fa.TopEta - fa.BottomEta;
        ShaderMaterial Mat(int mode)
        {
            var m = new ShaderMaterial { Shader = shader };
            m.SetShaderParameter("drop", drop);
            m.SetShaderParameter("mode", mode);
            m.RenderPriority = mode == 1 ? 3 : 0;   // the veil last (it writes depth too: nothing see-through may come after it)
            return m;
        }
        root.AddChild(Sheet(cols, fa, veil: false, Mat(0)));
        // Volume: two looser layers of streamers in front of the sheet, thrown further out the lower they fall.
        // They must not write depth (they would hide the sheet behind them), so their shader is a copy without it.
        var outerShader = new Shader { Code = shader.Code.Replace("depth_draw_always", "depth_draw_never") };
        for (int layer = 1; layer <= 2; layer++)
        {
            var m = new ShaderMaterial { Shader = outerShader };
            m.SetShaderParameter("drop", drop);
            m.SetShaderParameter("mode", 0);
            m.SetShaderParameter("layer", (float)layer);
            m.RenderPriority = 2;
            root.AddChild(Sheet(cols, fa, veil: false, m, layer));
        }
        root.AddChild(Sheet(cols, fa, veil: true, Mat(1)));
        // Spray and mist rise all along the foot, where the water lands: an emitter every ~6 m of the curved sheet,
        // facing the way the water there falls.
        for (int q0 = 0; q0 < cols.Count; q0 += 12)
        {
            int q1 = Math.Min(cols.Count, q0 + 12);
            var foot = Vector3.Zero; var dir = Vector3.Zero;
            for (int q = q0; q < q1; q++) { foot += cols[q].Lip + cols[q].Dir * Throw(cols[q], drop); dir += cols[q].Dir; }
            foot /= q1 - q0; dir = dir.Normalized();
            float halfLen = 0.5f * (cols[q1 - 1].Across - cols[q0].Across) + 0.6f;
            root.AddChild(Spray(f, fa, foot, dir, halfLen, mist: false));
            root.AddChild(Spray(f, fa, foot, dir, halfLen, mist: true));
            root.AddChild(Boil(f, fa, foot, dir, halfLen));
            // And at the brink: droplets torn off the edge, and a thin mist lying over it (blends the river into the drop).
            var brink = Vector3.Zero;
            for (int q = q0; q < q1; q++) brink += cols[q].Lip;
            brink /= q1 - q0;
            root.AddChild(Brink(f, brink, dir, halfLen, mist: false));
            root.AddChild(Brink(f, brink, dir, halfLen, mist: true));
        }
        root.AddChild(TrailPost(f, fa));
        root.AddChild(Stones(f, fa, cols, outcrops));
        return root;
    }

    /// <summary>A rock outcrop standing up on the lip: the water splits around it.</summary>
    struct Outcrop { public float Across, Half; }

    /// <summary>2-3 outcrops spread along the lip, leaving streams of water between them.</summary>
    static List<Outcrop> Outcrops(ReachField f, FallsSpec fa)
    {
        var rng = new Rng(f.Seed + 601);
        int n = 2 + rng.Range(0, 2);
        float span = fa.HalfWidth * 1.5f;
        var list = new List<Outcrop>();
        for (int k = 0; k < n; k++)
        {
            float c = -span * 0.5f + span * (k + 0.5f) / n + rng.Range(-0.15f, 0.15f) * span / n;
            list.Add(new Outcrop { Across = c, Half = rng.Range(1.2f, 2.4f) });
        }
        return list;
    }

    // ================================================================ rock (D22, after Tom's reference photos)

    /// <summary>
    /// The rock of the falls, as stacked slabs of dark layered stone (one mesh, FallsRock.gdshader):
    ///   - the face behind the falling water, all along the lip, its top just under the water at the brink, its lower
    ///     slabs set back (undercut, as falls erode);
    ///   - the outcrops on the lip, standing above the water, splitting it into streams;
    ///   - walls carrying the face 8 m into both banks, their tops at the ground (rock showing in the bank);
    ///   - boulders in the plunge pool.
    /// Outcrops and pool boulders collide (the raft bumps them).
    /// </summary>
    static Node3D Stones(ReachField f, FallsSpec fa, List<Column> cols, List<Outcrop> outcrops)
    {
        var root = new Node3D { Name = "FallsRock" };
        if (cols.Count == 0) return root;
        var verts = new List<Vector3>(); var colors = new List<Color>();
        var body = new StaticBody3D { Name = "FallsRockCollision", CollisionLayer = 1, CollisionMask = 0 };
        root.AddChild(body);
        var rng = new Rng(f.Seed + 811);
        float ax = -fa.DirZ, az = fa.DirX;
        float bottom = fa.BottomEta - 1.2f, top = fa.TopEta;
        (Vector3 p, Vector3 n) LipPoint(float c)
        {
            float lipS = FallsSpec.LipAt(c, fa.HalfWidth, fa.Height, f.Seed);
            float slope = FallsSpec.LipAt(c + 0.5f, fa.HalfWidth, fa.Height, f.Seed) - FallsSpec.LipAt(c - 0.5f, fa.HalfWidth, fa.Height, f.Seed);
            var n = new Vector3(fa.DirX - ax * slope, 0f, fa.DirZ - az * slope).Normalized();
            return (new Vector3(fa.LipX + ax * c + fa.DirX * lipS, 0f, fa.LipZ + az * c + fa.DirZ * lipS), n);
        }
        float minC = cols[0].Across, maxC = cols[^1].Across;
        // The face, and the walls beyond the ends of the water.
        for (float c = minC - 8f; c <= maxC + 8f; c += rng.Range(2.2f, 3.4f))
        {
            var (p, n) = LipPoint(c);
            bool bank = c < minC - 0.5f || c > maxC + 0.5f;
            float ground = f.InDomain(p.X, p.Z) ? f.SampleHeight(p.X - n.X * 1.5f, p.Z - n.Z * 1.5f) : top;
            float stackTop = bank ? MathF.Min(top + 0.4f, ground - rng.Range(0.05f, 0.35f)) : top - rng.Range(0.1f, 0.25f);
            float depth = bank ? rng.Range(2.5f, 4f) : rng.Range(1.8f, 2.6f);
            // Front just behind the lip line; the stack reaches back upstream (under the brink water).
            AddStack(verts, colors, rng, p - n * (0.12f + depth * 0.5f), n, rng.Range(3.2f, 4.6f), depth, bottom, stackTop, bank ? 0.2f : 0.9f);
        }
        // Outcrops on the lip.
        foreach (var o in outcrops)
        {
            var (p, n) = LipPoint(o.Across);
            float stackTop = top + rng.Range(0.6f, 1.5f), depth = rng.Range(3f, 4.2f);
            var centre = p + n * rng.Range(0.2f, 1.0f) - n * depth * 0.5f;
            AddStack(verts, colors, rng, centre, n, o.Half * 2f, depth, bottom, stackTop, 0.3f);
            AddBox(body, centre, n, o.Half * 2f, depth, bottom, stackTop);
        }
        // Boulders in the plunge pool, away from the put-in.
        float drop = fa.TopEta - fa.BottomEta;
        var boulderMat = new ShaderMaterial { Shader = GD.Load<Shader>("res://Game/Shaders/Rock.gdshader") };
        float hco = MathF.Cos(f.Frame.Heading), hsi = MathF.Sin(f.Frame.Heading);
        var wdir = new Vector2(fa.DirX * hco + fa.DirZ * hsi, -fa.DirX * hsi + fa.DirZ * hco);
        for (int k = 0, tries = 0; k < 4 && tries < 60; tries++)
        {
            var col = cols[rng.Range(0, cols.Count)];
            var pos = col.Lip + col.Dir * (Throw(col, drop) + rng.Range(4f, 20f)) + new Vector3(ax, 0, az) * rng.Range(-3f, 3f);
            if (!f.InDomain(pos.X, pos.Z) || f.SampleDepth(pos.X, pos.Z) < 0.6f) continue;
            if (new Vector2(pos.X - fa.PutInX, pos.Z - fa.PutInZ).Length() < 12f) continue;
            float r = rng.Range(0.9f, 1.8f);
            var boulder = new MeshInstance3D
            {
                Mesh = Vegetation.RockMesh(f.Seed + 1300 + k * 17, r), MaterialOverride = boulderMat,
                Position = new Vector3(pos.X, fa.BottomEta - rng.Range(0.35f, 0.6f) * r, pos.Z),
                Rotation = new Vector3(rng.Range(-0.25f, 0.25f), rng.Range(0f, MathF.Tau), rng.Range(-0.2f, 0.2f)),
                Scale = new Vector3(rng.Range(1.1f, 1.5f), rng.Range(0.55f, 0.75f), rng.Range(1.0f, 1.4f)),
            };
            boulder.SetInstanceShaderParameter("water_level", fa.BottomEta);
            boulder.SetInstanceShaderParameter("flow", new Vector4(wdir.X, wdir.Y, 1.2f, r));
            root.AddChild(boulder);
            body.AddChild(new CollisionShape3D { Shape = new SphereShape3D { Radius = r * 0.85f }, Position = boulder.Position });
            k++;
        }
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts.ToArray();
        arrays[(int)Mesh.ArrayType.Color] = colors.ToArray();
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        var st = new SurfaceTool();
        st.CreateFrom(mesh, 0);
        st.GenerateNormals();
        var mat = new ShaderMaterial { Shader = GD.Load<Shader>("res://Game/Shaders/FallsRock.gdshader") };
        mat.SetShaderParameter("pool_level", fa.BottomEta);
        mat.SetShaderParameter("top_level", fa.TopEta);
        root.AddChild(new MeshInstance3D { Name = "Rock", Mesh = st.Commit(), MaterialOverride = mat });
        return root;
    }

    /// <summary>
    /// A stack of stone slabs 0.5-0.8 m thick from y0 to y1, centred at (centre.x, centre.z), facing front: each slab
    /// a little different in size and offset, corners jittered; lower slabs set back by up to undercut metres.
    /// </summary>
    static void AddStack(List<Vector3> verts, List<Color> colors, Rng rng, Vector3 centre, Vector3 front, float width, float depth,
                         float y0, float y1, float undercut)
    {
        if (y1 <= y0 + 0.1f) return;
        int n = Math.Max(1, (int)MathF.Round((y1 - y0) / rng.Range(0.7f, 1.2f)));
        var side = new Vector3(-front.Z, 0f, front.X);
        for (int i = 0; i < n; i++)
        {
            float a = y0 + (y1 - y0) * i / n, b = y0 + (y1 - y0) * (i + 1) / n + 0.04f;
            float t = n > 1 ? i / (float)(n - 1) : 1f;   // 0 bottom .. 1 top
            float w = width * rng.Range(0.82f, 1.06f), d = depth * rng.Range(0.8f, 1.05f);
            var c = centre + front * (rng.Range(-0.15f, 0.15f) - undercut * (1f - t)) + side * rng.Range(-0.25f, 0.25f);
            float turn = rng.Range(-0.1f, 0.1f);
            var fr = (front * MathF.Cos(turn) + side * MathF.Sin(turn)).Normalized();
            var sd = new Vector3(-fr.Z, 0f, fr.X);
            float tone = rng.Range(0.8f, 1.15f);
            var k = new Vector3[8];
            for (int q = 0; q < 8; q++)
            {
                int cx = q & 1, cy = (q >> 1) & 1, cz = (q >> 2) & 1;
                var p = c + sd * (cx * 2 - 1) * w * 0.5f + fr * (cz * 2 - 1) * d * 0.5f;
                p.Y = cy == 0 ? a : b;
                // Uneven: corners jittered.
                p += sd * rng.Range(-0.3f, 0.3f) + fr * rng.Range(-0.3f, 0.3f) + Vector3.Up * rng.Range(-0.1f, 0.1f);
                k[q] = p;
            }
            // Each face as a 4 x 4 grid, pushed in by noise (chipped, lumpy stone rather than a crate).
            var mid = (k[0] + k[7]) * 0.5f;
            Vector3 Rough(Vector3 q, float edgeK)
            {
                float nz = 0.5f + 0.5f * River.Core.Noise.Fbm(q.X * 0.8f + q.Y * 0.6f, q.Z * 0.8f - q.Y * 0.4f, 9137, 3);
                return q + (mid - q).Normalized() * (0.05f + 0.28f * nz) * edgeK;
            }
            void Quad(int i0, int i1, int i2, int i3)
            {
                const int S = 4;
                var g = new Vector3[(S + 1) * (S + 1)];
                for (int v = 0; v <= S; v++)
                    for (int u = 0; u <= S; u++)
                    {
                        float fu = u / (float)S, fv = v / (float)S;
                        var e0 = k[i0].Lerp(k[i1], fu); var e1 = k[i3].Lerp(k[i2], fu);
                        // Corners and edges pulled in less: the slab keeps its shape.
                        float inner = MathF.Min(MathF.Min(fu, 1f - fu), MathF.Min(fv, 1f - fv)) * 2f;
                        g[v * (S + 1) + u] = Rough(e0.Lerp(e1, fv), 0.35f + 0.65f * inner);
                    }
                for (int v = 0; v < S; v++)
                    for (int u = 0; u < S; u++)
                    {
                        int a0 = v * (S + 1) + u, a1 = a0 + 1, a2 = a0 + S + 2, a3 = a0 + S + 1;
                        foreach (int q in new[] { a0, a2, a1, a0, a3, a2 }) { verts.Add(g[q]); colors.Add(new Color(tone, 0, 0)); }
                    }
            }
            // Outward faces (Quad emits each triangle reversed: Godot's front faces are clockwise).
            Quad(2, 3, 7, 6);   // top
            Quad(0, 4, 5, 1);   // bottom
            Quad(4, 6, 7, 5);   // front (+fr)
            Quad(0, 1, 3, 2);   // back
            Quad(1, 5, 7, 3);   // +sd
            Quad(0, 2, 6, 4);   // -sd
        }
    }

    static void AddBox(StaticBody3D body, Vector3 centre, Vector3 front, float width, float depth, float y0, float y1)
    {
        body.AddChild(new CollisionShape3D
        {
            Shape = new BoxShape3D { Size = new Vector3(width, y1 - y0, depth) },
            Transform = new Transform3D(new Basis(Vector3.Up, MathF.Atan2(front.X, front.Z)), new Vector3(centre.X, 0.5f * (y0 + y1), centre.Z)),
        });
    }

    /// <summary>One strip of falling water: where it leaves the lip, which way it goes, how much water it carries.</summary>
    struct Column
    {
        public float Across;        // metres from the centre line
        public Vector3 Lip;         // where the water leaves the edge (grid-local, at the top water level)
        public Vector3 Dir;         // horizontal, downstream at the lip
        public float Amount;        // 0.25 (threads) … 1 (a full sheet)
        public float Seed;
    }

    /// <summary>
    /// Columns every 0.5 m across the river, on the lip's smooth curve (FallsSpec.LipAt, the same curve the water
    /// surface is clipped to): wherever there is water just above it and the water below it has dropped; none where a
    /// lip ledge stands. Each gets its own amount of water (slow noise across: full sheets here, thin threads there).
    /// </summary>
    static List<Column> Columns(ReachField f, FallsSpec fa, List<Outcrop> outcrops)
    {
        var cols = new List<Column>();
        float ax = -fa.DirZ, az = fa.DirX;
        float drop = fa.TopEta - fa.BottomEta;
        float reach = fa.HalfWidth * 1.6f + 10f;
        for (float c = -reach; c <= reach; c += 0.5f)
        {
            float lipS = FallsSpec.LipAt(c, fa.HalfWidth, fa.Height, f.Seed);
            float lx = fa.LipX + ax * c + fa.DirX * lipS, lz = fa.LipZ + az * c + fa.DirZ * lipS;
            if (!f.InDomain(lx, lz)) continue;
            // Water pours over at right angles to its edge: on the horseshoe's sides it falls inwards, not downstream.
            float slope = FallsSpec.LipAt(c + 0.5f, fa.HalfWidth, fa.Height, f.Seed) - FallsSpec.LipAt(c - 0.5f, fa.HalfWidth, fa.Height, f.Seed);
            float nx = fa.DirX - ax * slope, nz = fa.DirZ - az * slope, nl = MathF.Sqrt(nx * nx + nz * nz);
            nx /= nl; nz /= nl;
            if (f.SampleDepth(lx - nx * 2.5f, lz - nz * 2.5f) < 0.15f) { Dbg(c, "no water above"); continue; }
            if (f.SampleEta(lx + nx * 3f, lz + nz * 3f) > fa.TopEta - 0.5f * drop) { Dbg(c, $"no drop (eta {f.SampleEta(lx + nx * 3f, lz + nz * 3f):F2})"); continue; }
            bool rock = false;
            foreach (var r in f.Rocks)
                if ((r.X - lx) * (r.X - lx) + (r.Z - lz) * (r.Z - lz) < (r.Radius * 0.85f) * (r.Radius * 0.85f)) { rock = true; break; }
            if (rock) continue;
            // No water where an outcrop stands; thinner towards its edges (each stream frays at its sides).
            float edge = float.MaxValue;
            foreach (var o in outcrops) edge = MathF.Min(edge, MathF.Abs(c - o.Across) - o.Half);
            if (edge < 0f) continue;
            float amount = Math.Clamp(0.55f + 0.75f * River.Core.Noise.Fbm1(c / 4.5f, f.Seed + 77, 2), 0.25f, 1f);
            amount = MathF.Max(0.2f, amount * MathUtil.Smoothstep(0f, 2.5f, edge));
            cols.Add(new Column
            {
                // The top follows the water just above the brink (drawn down there), tucked 2 cm under it.
                Across = c, Lip = new Vector3(lx, MathF.Max(f.SampleEta(lx - nx * 2.5f, lz - nz * 2.5f), fa.TopEta - 0.4f) - 0.02f, lz),
                Dir = new Vector3(nx, 0f, nz),
                Amount = amount, Seed = River.Core.Noise.Hash01((int)MathF.Round(c * 2f), 3, f.Seed + 79),
            });
        }
        return cols;
    }

    static void Dbg(float c, string why) { if (OS.HasEnvironment("RIVER_FALLS_DEBUG")) GD.Print($"FALLS column {c:F1}: {why}"); }

    const int Rows = 22;

    // How far out a column lands. At least clear of the rock face, which on the 2 m grid slopes out over a cell below
    // the lip (thinner columns thrown less would show it through them as dark wedges).
    static float Throw(in Column col, float drop) => (1.6f + 1.4f * col.Amount) * (1f + 0.08f * drop);

    /// <summary>
    /// The falling sheet (or the veil of spray just in front of it). From 0.8 m upstream of the edge the surface curves
    /// over (the brink), then each column falls along a parabola: fuller columns are thrown further out, thin ones
    /// cling closer to the rock. Columns join into a sheet where their lips are close; gaps where they aren't.
    /// COLOR: r = amount, g = seed.
    /// </summary>
    static MeshInstance3D Sheet(List<Column> cols, FallsSpec fa, bool veil, ShaderMaterial mat, int layer = 0)
    {
        float drop = fa.TopEta - fa.BottomEta;
        var verts = new List<Vector3>(); var uvs = new List<Vector2>(); var colors = new List<Color>(); var idx = new List<int>();
        int prev = -1; Vector3 prevLip = default; float prevC = float.NaN;
        foreach (var col in cols)
        {
            int start = verts.Count;
            float th = Throw(col, drop);
            float out0 = veil ? 0.25f + 0.35f * col.Amount : layer * (0.22f + 0.2f * col.Amount);
            for (int r = 0; r <= Rows; r++)
            {
                float u = r / (float)Rows;
                float brink = MathUtil.Smoothstep(0.12f, 0f, u);
                // (The brink starts 0.6 m upstream, just under the river surface, which ends exactly at the lip.)
                float fwd = -0.6f * brink + th * MathF.Sqrt(u) + out0 * MathUtil.Smoothstep(0.05f, 0.4f, u) * (1f + u);
                // Slower at first (the water leaves the edge horizontally), then falling.
                float fall = (col.Lip.Y - fa.BottomEta + 0.4f) * (0.35f * u * u + 0.65f * u);
                verts.Add(col.Lip + col.Dir * fwd + new Vector3(0f, -fall, 0f));
                uvs.Add(new Vector2(col.Across, u));
                colors.Add(new Color(col.Amount, layer == 0 ? col.Seed : (col.Seed + 0.37f * layer) % 1f, veil ? 1f : 0f));
            }
            // Side by side columns always join (gaps only where a ledge stands between them).
            if (prev >= 0 && MathF.Abs(col.Across - prevC) < 0.75f && col.Lip.DistanceTo(prevLip) < 3f)
                for (int r = 0; r < Rows; r++)
                {
                    int a = prev + r, b = start + r, d = start + r + 1, e = prev + r + 1;
                    idx.Add(a); idx.Add(b); idx.Add(d); idx.Add(a); idx.Add(d); idx.Add(e);
                }
            prev = start; prevLip = col.Lip; prevC = col.Across;
        }
        return MeshOf(veil ? "Veil" : layer == 0 ? "Curtain" : $"CurtainOuter{layer}", verts, uvs, colors, idx, mat);
    }

    /// <summary>
    /// The churn at the foot: a skirt on the plunge pool from just behind where each column lands to 9 m beyond it,
    /// the falling water's white boil spreading out and thinning (COLOR: r = amount).
    /// </summary>
    static MeshInstance3D Churn(List<Column> cols, FallsSpec fa, ShaderMaterial mat)
    {
        float drop = fa.TopEta - fa.BottomEta;
        var verts = new List<Vector3>(); var uvs = new List<Vector2>(); var colors = new List<Color>(); var idx = new List<int>();
        const int rings = 6;
        int prev = -1; Vector3 prevLip = default; float prevC = float.NaN;
        foreach (var col in cols)
        {
            int start = verts.Count;
            float th = Throw(col, drop);
            for (int r = 0; r <= rings; r++)
            {
                float d = r / (float)rings;
                var p = col.Lip + col.Dir * (th - 0.6f + 9.5f * d);
                verts.Add(new Vector3(p.X, fa.BottomEta + 0.07f, p.Z));
                uvs.Add(new Vector2(col.Across, d));
                colors.Add(new Color(col.Amount, col.Seed, 0f));
            }
            if (prev >= 0 && MathF.Abs(col.Across - prevC) < 0.75f && col.Lip.DistanceTo(prevLip) < 2.5f)
                for (int r = 0; r < rings; r++)
                {
                    int a = prev + r, b = start + r, d = start + r + 1, e = prev + r + 1;
                    idx.Add(a); idx.Add(b); idx.Add(d); idx.Add(a); idx.Add(d); idx.Add(e);
                }
            prev = start; prevLip = col.Lip; prevC = col.Across;
        }
        return MeshOf("Churn", verts, uvs, colors, idx, mat);
    }

    static MeshInstance3D MeshOf(string name, List<Vector3> verts, List<Vector2> uvs, List<Color> colors, List<int> idx, ShaderMaterial mat)
    {
        var mi = new MeshInstance3D { Name = name, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, MaterialOverride = mat };
        if (idx.Count == 0) return mi;
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts.ToArray();
        arrays[(int)Mesh.ArrayType.TexUV] = uvs.ToArray();
        arrays[(int)Mesh.ArrayType.Color] = colors.ToArray();
        arrays[(int)Mesh.ArrayType.Index] = idx.ToArray();
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        var st = new SurfaceTool();
        st.CreateFrom(mesh, 0);
        st.GenerateNormals();
        mi.Mesh = st.Commit();
        return mi;
    }

    /// <summary>Spray at the foot of the falls (fast droplets) or the billowing mist cloud above the pool (slow, large).</summary>
    static GpuParticles3D Spray(ReachField f, FallsSpec fa, Vector3 foot, Vector3 dir, float halfSpan, bool mist)
    {
        float drop = fa.TopEta - fa.BottomEta;
        float co = MathF.Cos(f.Frame.Heading), si = MathF.Sin(f.Frame.Heading);
        // Particles fly in world space: directions in world axes.
        float wdx = dir.X * co + dir.Z * si, wdz = -dir.X * si + dir.Z * co;
        var pm = new ParticleProcessMaterial
        {
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Box,
            EmissionBoxExtents = new Vector3(halfSpan, 0.3f, mist ? 2.2f : 1.2f),
            Direction = new Vector3(wdx * 0.5f, 1f, wdz * 0.5f).Normalized(),
            Spread = mist ? 50f : 35f,
            InitialVelocityMin = mist ? 0.3f : 2f + 0.25f * drop,
            InitialVelocityMax = mist ? 1.1f : 4f + 0.4f * drop,
            // The mist hugs the foot (barely rising, drifting out over the pool); spray arcs out and falls back.
            Gravity = mist ? new Vector3(wdx * 0.3f, 0.05f, wdz * 0.3f) : new Vector3(wdx * 1.5f, -6f, wdz * 1.5f),
            ScaleMin = mist ? 0.6f : 0.5f, ScaleMax = mist ? 2.0f : 1.4f,
            // Each puff turned at random and turning slowly: the cloudy texture never lines up.
            AngleMin = -180f, AngleMax = 180f,
            AngularVelocityMin = mist ? -12f : 0f, AngularVelocityMax = mist ? 12f : 0f,
            ColorRamp = new GradientTexture1D
            {
                Gradient = new Gradient
                {
                    Offsets = new[] { 0f, 0.2f, 1f },
                    Colors = mist
                        ? new[] { new Color(1, 1, 1, 0f), new Color(1, 1, 1, 0.32f), new Color(1, 1, 1, 0f) }
                        : new[] { new Color(1, 1, 1, 0f), new Color(1, 1, 1, 0.8f), new Color(1, 1, 1, 0f) },
                },
            },
        };
        // The emitter box is in the segment's grid axes (its parent's); align it with the lip.
        float yaw = MathF.Atan2(dir.X, dir.Z);
        return new GpuParticles3D
        {
            Name = mist ? "FallsMist" : "FallsSpray",
            Amount = mist ? 45 : 110,
            Lifetime = mist ? 6f : 1.6f,
            ProcessMaterial = pm,
            DrawPass1 = new QuadMesh { Size = mist ? new Vector2(6.5f, 6.5f) : new Vector2(0.55f, 0.55f), Material = mist ? MistMaterial() : SprayMaterial() },
            Position = new Vector3(foot.X + dir.X * (mist ? 1.5f : 0.3f), fa.BottomEta + (mist ? 0.8f : 0.2f), foot.Z + dir.Z * (mist ? 1.5f : 0.3f)),
            Rotation = new Vector3(0f, yaw, 0f),
            VisibilityAabb = new Aabb(new Vector3(-60, -5, -60), new Vector3(120, 40, 120)),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Randomness = 0.6f,
        };
    }

    /// <summary>
    /// Spray and mist at the brink: fine droplets leaving the edge with the water (thrown out, then falling) and a thin,
    /// slow mist lying just over the edge, drifting a little downstream and up from the falls below.
    /// </summary>
    static GpuParticles3D Brink(ReachField f, Vector3 lip, Vector3 dir, float halfSpan, bool mist)
    {
        float co = MathF.Cos(f.Frame.Heading), si = MathF.Sin(f.Frame.Heading);
        float wdx = dir.X * co + dir.Z * si, wdz = -dir.X * si + dir.Z * co;
        var pm = new ParticleProcessMaterial
        {
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Box,
            EmissionBoxExtents = new Vector3(halfSpan, mist ? 0.4f : 0.1f, mist ? 1.6f : 0.4f),
            Direction = mist ? new Vector3(wdx * 0.4f, 0.6f, wdz * 0.4f).Normalized() : new Vector3(wdx, 0.35f, wdz).Normalized(),
            Spread = mist ? 40f : 18f,
            InitialVelocityMin = mist ? 0.15f : 1.2f,
            InitialVelocityMax = mist ? 0.5f : 2.4f,
            Gravity = mist ? new Vector3(wdx * 0.15f, 0.08f, wdz * 0.15f) : new Vector3(0f, -9.81f, 0f),
            ScaleMin = mist ? 0.6f : 0.4f, ScaleMax = mist ? 1.4f : 1.1f,
            AngleMin = -180f, AngleMax = 180f,
            AngularVelocityMin = mist ? -8f : 0f, AngularVelocityMax = mist ? 8f : 0f,
            ColorRamp = new GradientTexture1D
            {
                Gradient = new Gradient
                {
                    Offsets = new[] { 0f, 0.25f, 1f },
                    Colors = mist
                        ? new[] { new Color(1, 1, 1, 0f), new Color(1, 1, 1, 0.18f), new Color(1, 1, 1, 0f) }
                        : new[] { new Color(1, 1, 1, 0f), new Color(1, 1, 1, 0.45f), new Color(1, 1, 1, 0f) },
                },
            },
        };
        return new GpuParticles3D
        {
            Name = mist ? "BrinkMist" : "BrinkSpray",
            Amount = mist ? 24 : 70,
            Lifetime = mist ? 5f : 1.0f,
            ProcessMaterial = pm,
            DrawPass1 = new QuadMesh { Size = mist ? new Vector2(4f, 4f) : new Vector2(0.14f, 0.14f), Material = mist ? MistMaterial() : SprayMaterial() },
            Position = lip + dir * (mist ? 0.8f : 0.2f) + Vector3.Up * (mist ? 0.5f : 0.05f),
            Rotation = new Vector3(0f, MathF.Atan2(dir.X, dir.Z), 0f),
            VisibilityAabb = new Aabb(new Vector3(-30, -15, -30), new Vector3(60, 30, 60)),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Randomness = 0.6f,
        };
    }

    /// <summary>
    /// The boil where the falling water hits the pool: dense, short-lived puffs of white froth at the water line,
    /// heaving up and spreading out, covering the join between the sheet and the pool.
    /// </summary>
    static GpuParticles3D Boil(ReachField f, FallsSpec fa, Vector3 foot, Vector3 dir, float halfSpan)
    {
        float co = MathF.Cos(f.Frame.Heading), si = MathF.Sin(f.Frame.Heading);
        float wdx = dir.X * co + dir.Z * si, wdz = -dir.X * si + dir.Z * co;
        var pm = new ParticleProcessMaterial
        {
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Box,
            EmissionBoxExtents = new Vector3(halfSpan, 0.15f, 0.9f),
            Direction = new Vector3(wdx * 0.6f, 1f, wdz * 0.6f).Normalized(),
            Spread = 55f,
            InitialVelocityMin = 0.6f, InitialVelocityMax = 2.2f,
            Gravity = new Vector3(wdx * 0.8f, -3.5f, wdz * 0.8f),
            ScaleMin = 0.5f, ScaleMax = 1.3f,
            AngleMin = -180f, AngleMax = 180f,
            AngularVelocityMin = -40f, AngularVelocityMax = 40f,
            ColorRamp = new GradientTexture1D
            {
                Gradient = new Gradient
                {
                    Offsets = new[] { 0f, 0.15f, 1f },
                    Colors = new[] { new Color(1, 1, 1, 0f), new Color(1, 1, 1, 0.75f), new Color(1, 1, 1, 0f) },
                },
            },
        };
        return new GpuParticles3D
        {
            Name = "FallsBoil",
            Amount = 90,
            Lifetime = 1.3f,
            ProcessMaterial = pm,
            DrawPass1 = new QuadMesh { Size = new Vector2(2.2f, 2.2f), Material = MistMaterial() },
            Position = new Vector3(foot.X, fa.BottomEta + 0.15f, foot.Z),
            Rotation = new Vector3(0f, MathF.Atan2(dir.X, dir.Z), 0f),
            VisibilityAabb = new Aabb(new Vector3(-30, -5, -30), new Vector3(60, 20, 60)),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Randomness = 0.7f,
        };
    }

    static StandardMaterial3D _mist;

    /// <summary>
    /// The mist's puffs: a soft blob with cloudy noise inside it (fbm, gently contrasted), so the cloud billows
    /// unevenly instead of being a uniform haze. Built once, 128 px.
    /// </summary>
    static StandardMaterial3D MistMaterial()
    {
        if (_mist != null) return _mist;
        const int n = 128;
        var img = Image.CreateEmpty(n, n, true, Image.Format.Rgba8);
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float u = (x + 0.5f) / n * 2f - 1f, v = (y + 0.5f) / n * 2f - 1f;
                float r = MathF.Sqrt(u * u + v * v);
                float cloud = 0.5f + 0.5f * River.Core.Noise.Fbm(u * 2.2f + 5f, v * 2.2f + 3f, 4711, 4);
                float a = MathUtil.Smoothstep(1f, 0.25f, r) * MathUtil.Smoothstep(0.25f, 0.75f, cloud);
                img.SetPixel(x, y, new Color(1, 1, 1, a));
            }
        img.GenerateMipmaps();
        return _mist = new StandardMaterial3D
        {
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles,
            ParticlesAnimHFrames = 1, ParticlesAnimVFrames = 1,
            VertexColorUseAsAlbedo = true,
            AlbedoColor = new Color(0.93f, 0.95f, 0.96f),
            AlbedoTexture = ImageTexture.CreateFromImage(img),
            Roughness = 1f,
            RenderPriority = 2,   // after the river (1), or its surface is painted over the mist
        };
    }

    static StandardMaterial3D _spray;
    static StandardMaterial3D SprayMaterial()
    {
        if (_spray != null) return _spray;
        // A soft round sprite, built once.
        const int n = 32;
        var img = Image.CreateEmpty(n, n, true, Image.Format.Rgba8);
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float dx = (x + 0.5f) / n * 2f - 1f, dy = (y + 0.5f) / n * 2f - 1f;
                img.SetPixel(x, y, new Color(1, 1, 1, MathUtil.Smoothstep(1f, 0.2f, MathF.Sqrt(dx * dx + dy * dy))));
            }
        img.GenerateMipmaps();
        return _spray = new StandardMaterial3D
        {
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles,
            VertexColorUseAsAlbedo = true,
            AlbedoColor = new Color(0.94f, 0.96f, 0.97f),
            AlbedoTexture = ImageTexture.CreateFromImage(img),
            Roughness = 1f,
            RenderPriority = 2,   // after the river (1), or its surface is painted over the spray
            // Lit (not unshaded): at dusk the mist must darken with everything else.
        };
    }

    /// <summary>A weathered post with a board at the trail head, so the landing can be found from the water.</summary>
    static Node3D TrailPost(ReachField f, FallsSpec fa)
    {
        float x = fa.TrailX[0], z = fa.TrailZ[0];
        // Beside the trail, on the landward side of its first point.
        float tx = fa.TrailX[1] - x, tz = fa.TrailZ[1] - z, tl = MathF.Max(0.01f, MathF.Sqrt(tx * tx + tz * tz));
        float px = x + tz / tl * 2.2f, pz = z - tx / tl * 2.2f;
        float ground = f.SampleHeight(px, pz);
        var mat = new StandardMaterial3D { AlbedoColor = new Color(0.36f, 0.28f, 0.2f), Roughness = 0.95f };
        var root = new Node3D { Name = "TrailPost", Position = new Vector3(px, ground, pz) };
        root.AddChild(new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = 0.09f, BottomRadius = 0.11f, Height = 2.2f, RadialSegments = 7 },
            MaterialOverride = mat, Position = new Vector3(0, 1.0f, 0),
        });
        root.AddChild(new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(0.9f, 0.32f, 0.05f) },
            MaterialOverride = mat, Position = new Vector3(0, 1.85f, 0),
            Rotation = new Vector3(0, MathF.Atan2(tx, tz), -0.12f),
        });
        return root;
    }
}
