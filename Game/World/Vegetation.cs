using System;
using System.Collections.Generic;
using Godot;
using River.Core;
using Noise = River.Core.Noise;

namespace River.World;

/// <summary>
/// Placeholder vegetation good enough to judge the river against: low-poly conifers and broadleaf trees
/// (alders and willows near the water), riverside bushes, and boulders in the riffle.
/// Built procedurally as MultiMeshes per 256 m block with visibility ranges.
/// </summary>
public static class Vegetation
{
    const float Block = 256f;
    const float BoardBlock = 512f;   // billboard MultiMeshes: one per variant per block (20 variants → keep blocks big)

    /// <summary>
    /// Vegetation and rocks in the field's grid coordinates. <paramref name="owns"/> (grid x, z → bool), if given,
    /// keeps only what this segment owns (neighbours overlap; each plants its own side of the cuts).
    /// </summary>
    /// <param name="trees">If given and the tree packs are loaded (TreeLibrary): real trees go here (grid-local
    /// transform, variant id) for TreeLod to draw near the camera, and only their billboards are built here.
    /// Otherwise (or for bushes) the placeholder meshes.</param>
    public static Node3D Build(ReachField f, Func<float, float, bool> owns = null, List<(Transform3D, ushort)> trees = null)
    {
        bool real = trees != null && TreeLibrary.Loaded;
        int birch = TreeLibrary.IndexOf(TreeLibrary.Kind.Birch), oak = TreeLibrary.IndexOf(TreeLibrary.Kind.Oak);
        int pine = TreeLibrary.IndexOf(TreeLibrary.Kind.Pine), spruce = TreeLibrary.IndexOf(TreeLibrary.Kind.Spruce);
        int willow = TreeLibrary.IndexOf(TreeLibrary.Kind.Willow);
        var boards = new Dictionary<(int, int, int), List<Transform3D>>();
        var root = new Node3D { Name = "Vegetation" };
        var mat = new ShaderMaterial { Shader = GD.Load<Shader>("res://Game/Shaders/Tree.gdshader") };
        var conifer = ConiferMesh(); var broadleaf = BroadleafMesh(); var bush = BushMesh();

        var rng = new Rng(f.Seed + 5000);
        var buckets = new Dictionary<(int, int, int), List<(Transform3D, Color)>>();
        void Add(int kind, Transform3D t, Color tint)
        {
            var key = ((int)(t.Origin.X / Block), (int)(t.Origin.Z / Block), kind);
            if (!buckets.TryGetValue(key, out var l)) buckets[key] = l = new List<(Transform3D, Color)>();
            l.Add((t, tint));
        }

        const float spacing = 5.5f;
        for (float z = spacing * 0.5f; z < f.SizeZ; z += spacing)
            for (float x = spacing * 0.5f; x < f.SizeX; x += spacing)
            {
                float px = x + rng.Range(-0.45f, 0.45f) * spacing, pz = z + rng.Range(-0.45f, 0.45f) * spacing;
                if (!f.InDomain(px, pz)) continue;
                if (owns != null && !owns(px, pz)) continue;
                float shore = f.Sample(f.ShoreDist, px, pz);
                if (shore < 3.5f) continue;
                float hgt = f.SampleHeight(px, pz);
                float above = hgt - f.SampleEta(px, pz);
                if (above < 0.7f) continue;                       // bars and wet margins stay open
                float hx = f.SampleHeight(px + 1.5f, pz) - f.SampleHeight(px - 1.5f, pz);
                float hz = f.SampleHeight(px, pz + 1.5f) - f.SampleHeight(px, pz - 1.5f);
                float slope = MathF.Sqrt(hx * hx + hz * hz) / 3f;
                if (slope > 0.8f) continue;
                if (f.Falls != null && NearTrail(f.Falls, px, pz, 6f)) continue;   // the portage trail stays clear

                // Forest mask: woods with clearings and meadows; riparian belt along the water; walls wooded.
                float forest = Noise.Fbm(px / 260f, pz / 260f, f.Seed + 5101, 3) * 0.5f + 0.5f;
                float riparian = MathUtil.Smoothstep(28f, 6f, shore);
                float wall = MathUtil.Smoothstep(2f, 25f, above - 3f);
                float density = MathUtil.Smoothstep(0.42f, 0.62f, forest) * 0.85f + riparian * 0.55f + wall * 0.35f;
                if (rng.Next01() > density) continue;

                float coniferChance = MathUtil.Clamp01(0.25f + wall * 0.6f - riparian * 0.4f + Noise.Fbm(px / 400f, pz / 400f, f.Seed + 5102, 2) * 0.3f);
                bool isBush = riparian > 0.5f && rng.Next01() < 0.45f;
                int kind = isBush ? 2 : rng.Next01() < coniferChance ? 0 : 1;
                float scale = kind switch
                {
                    0 => rng.Range(0.75f, 1.35f),
                    1 => rng.Range(0.7f, 1.3f) * (riparian > 0.3f ? 0.85f : 1f),
                    _ => rng.Range(0.6f, 1.3f),
                };
                var basis = new Basis(Vector3.Up, rng.Range(0f, MathF.Tau)).Scaled(new Vector3(scale, scale * rng.Range(0.9f, 1.15f), scale));
                var t = new Transform3D(basis, new Vector3(px, hgt - 0.15f, pz));
                float tv = rng.Range(0.82f, 1.12f);
                var tint = new Color(tv * rng.Range(0.95f, 1.05f), tv, tv * rng.Range(0.9f, 1.05f));
                if (real && kind != 2)
                {
                    // Species by habitat: spruce on the valley walls and in cool patches, pine on drier ground;
                    // weeping willows right at the water's edge (rivers and oxbows), leaning over it; birch along
                    // the banks and at wood edges, oak in the open floodplain.
                    int sp = kind == 0
                        ? (wall > 0.35f || Noise.Fbm(px / 500f, pz / 500f, f.Seed + 5103, 2) > 0.05f ? spruce : pine)
                        : (willow >= 0 && shore < 12f && above < 3.5f && rng.Next01() < 0.6f ? willow
                           : riparian > 0.25f || Noise.Fbm(px / 350f, pz / 350f, f.Seed + 5104, 2) > 0.2f ? birch : oak);
                    if (sp < 0) sp = kind == 0 ? Math.Max(spruce, pine) : Math.Max(birch, oak);
                    if (sp >= 0)
                    {
                        var vars = TreeLibrary.Species[sp].Variants;
                        var tvar = vars[Math.Min(vars.Count - 1, (int)(rng.Next01() * vars.Count))];
                        // The packs are full-size trees (11–18 m): vary them less than the placeholders.
                        float s2 = rng.Range(0.8f, 1.12f);
                        var tb = new Basis(Vector3.Up, rng.Range(0f, MathF.Tau)).Scaled(new Vector3(s2, s2 * rng.Range(0.93f, 1.07f), s2));
                        var tt = new Transform3D(tb, new Vector3(px, hgt - 0.1f, pz));
                        trees.Add((tt, (ushort)tvar.Id));
                        var bk = ((int)(px / BoardBlock), (int)(pz / BoardBlock), tvar.Id);
                        if (!boards.TryGetValue(bk, out var bl)) boards[bk] = bl = new List<Transform3D>();
                        bl.Add(tt);
                        continue;
                    }
                }
                Add(kind, t, tint);
            }

        foreach (var (key, list) in boards)
        {
            var tvar = TreeLibrary.All[key.Item3];
            var mm = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = tvar.Lods[3], InstanceCount = list.Count };
            for (int i = 0; i < list.Count; i++) mm.SetInstanceTransform(i, list[i]);
            root.AddChild(new MultiMeshInstance3D
            {
                Name = $"B{key.Item1}_{key.Item2}_{key.Item3}", Multimesh = mm,
                VisibilityRangeEnd = 1900f, VisibilityRangeEndMargin = 60f,
                VisibilityRangeFadeMode = GeometryInstance3D.VisibilityRangeFadeModeEnum.Self,
            });
        }

        var meshes = new Mesh[] { conifer, broadleaf, bush };
        foreach (var (key, list) in buckets)
        {
            var mm = new MultiMesh
            {
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseCustomData = true,
                Mesh = meshes[key.Item3], InstanceCount = list.Count,
            };
            for (int i = 0; i < list.Count; i++) { mm.SetInstanceTransform(i, list[i].Item1); mm.SetInstanceCustomData(i, list[i].Item2); }
            root.AddChild(new MultiMeshInstance3D
            {
                Name = $"V{key.Item1}_{key.Item2}_{key.Item3}", Multimesh = mm, MaterialOverride = mat,
                VisibilityRangeEnd = key.Item3 == 2 ? 450f : 1800f, VisibilityRangeEndMargin = 40f,
                VisibilityRangeFadeMode = GeometryInstance3D.VisibilityRangeFadeModeEnum.Self,
            });
        }

        root.AddChild(BuildRocks(f));
        if (f.Snags.Count > 0) root.AddChild(BuildSnags(f));
        return root;
    }

    static bool NearTrail(FallsSpec fa, float x, float z, float r)
    {
        for (int j = 0; j + 1 < fa.TrailX.Length; j++)
        {
            float ax = fa.TrailX[j], az = fa.TrailZ[j], bx = fa.TrailX[j + 1] - ax, bz = fa.TrailZ[j + 1] - az;
            float t = Math.Clamp(((x - ax) * bx + (z - az) * bz) / MathF.Max(1e-4f, bx * bx + bz * bz), 0f, 1f);
            float dx = x - (ax + bx * t), dz = z - (az + bz * t);
            if (dx * dx + dz * dz < r * r) return true;
        }
        return false;
    }

    // ---------------------------------------------------------------- snags (fallen trees in the water)

    /// <summary>Oriented tapered cylinder from a to c (outward faces).</summary>
    static void Limb(MeshBuilder b, Vector3 a, Vector3 c, float r0, float r1, int sides, Func<float, Color> colAt)
    {
        var axis = (c - a).Normalized();
        var u = axis.Cross(MathF.Abs(axis.Y) < 0.9f ? Vector3.Up : Vector3.Right).Normalized();
        var v = u.Cross(axis);
        for (int s = 0; s < sides; s++)
        {
            float a0 = s * MathF.Tau / sides, a1 = (s + 1) * MathF.Tau / sides;
            Vector3 D(float ang) => u * MathF.Cos(ang) + v * MathF.Sin(ang);
            var p0 = a + D(a0) * r0; var p1 = a + D(a1) * r0;
            var q0 = c + D(a0) * r1; var q1 = c + D(a1) * r1;
            var col = colAt(((p0 + q1) * 0.5f).Y);
            b.Tri(p0, q0, p1, col); b.Tri(p1, q0, q1, col);
        }
    }

    /// <summary>
    /// Snags: a bark trunk from the root plate on the bank down into the water (half submerged at its tip), a few
    /// broken branch stubs, darker where wet. One mesh for the segment; a capsule collider per trunk (the raft bumps).
    /// </summary>
    static Node3D BuildSnags(ReachField f)
    {
        var root = new Node3D { Name = "Snags" };
        var body = new StaticBody3D { Name = "SnagCollision", CollisionLayer = 1, CollisionMask = 0 };
        root.AddChild(body);
        var b = new MeshBuilder();
        foreach (var sn in f.Snags)
        {
            var rng = new Rng(sn.Seed);
            float tipX = sn.X + sn.DirX * sn.Length, tipZ = sn.Z + sn.DirZ * sn.Length;
            float eta = f.SampleEta(tipX, tipZ);
            var basePt = new Vector3(sn.X, MathF.Max(f.SampleHeight(sn.X, sn.Z), eta) + sn.Radius * 0.6f, sn.Z);
            var tip = new Vector3(tipX, eta - sn.Radius * 0.3f, tipZ);
            var bark = new Color(0.12f, 0.095f, 0.07f) * rng.Range(0.8f, 1.15f);   // linear: a weathered grey-brown
            var wet = new Color(0.045f, 0.038f, 0.03f);
            Color ColAt(float y) => y < eta + 0.12f ? wet : bark;
            Limb(b, basePt, tip, sn.Radius, sn.Radius * 0.55f, 7, ColAt);
            // Root plate: a squat disc across the trunk at its base.
            var axis = (tip - basePt).Normalized();
            Limb(b, basePt - axis * 0.25f, basePt + axis * 0.15f, sn.Radius * 4.5f, sn.Radius * 3.2f, 9, _ => new Color(0.09f, 0.07f, 0.05f));
            // Broken branches.
            int nb = 3 + (int)(rng.Next01() * 4f);
            for (int i = 0; i < nb; i++)
            {
                float t = rng.Range(0.35f, 0.9f);
                var at = basePt.Lerp(tip, t);
                var side = axis.Cross(Vector3.Up).Normalized() * (rng.Next01() < 0.5f ? -1f : 1f);
                var dir = (side + axis * rng.Range(0.3f, 0.9f) + Vector3.Up * rng.Range(-0.1f, 0.7f)).Normalized();
                Limb(b, at, at + dir * rng.Range(1.2f, 3.5f), sn.Radius * 0.35f, sn.Radius * 0.12f, 5, ColAt);
            }
            var mid = (basePt + tip) * 0.5f;
            var x = axis.Cross(MathF.Abs(axis.Y) < 0.9f ? Vector3.Up : Vector3.Right).Normalized();
            var basis = new Basis(x, axis, x.Cross(axis));
            body.AddChild(new CollisionShape3D
            {
                Shape = new CapsuleShape3D { Radius = sn.Radius, Height = (tip - basePt).Length() + 2f * sn.Radius },
                Transform = new Transform3D(basis, mid),
            });
        }
        var mat = new ShaderMaterial { Shader = GD.Load<Shader>("res://Game/Shaders/Snag.gdshader") };
        root.AddChild(new MeshInstance3D { Name = "SnagMesh", Mesh = b.Build(), MaterialOverride = mat });
        return root;
    }

    // ---------------------------------------------------------------- meshes (vertex colours, alpha = translucency)

    sealed class MeshBuilder
    {
        public readonly List<Vector3> V = new(), N = new();
        public readonly List<Color> C = new();
        public readonly List<int> I = new();

        public void Tri(Vector3 a, Vector3 b, Vector3 c, Color col)
        {
            var n = (b - a).Cross(c - a).Normalized();
            // Godot front faces are clockwise seen from the front: emit a, c, b. Normal from the a, b, c order.
            int k = V.Count;
            V.Add(a); V.Add(c); V.Add(b);
            N.Add(n); N.Add(n); N.Add(n);
            C.Add(col); C.Add(col); C.Add(col);
            I.Add(k); I.Add(k + 1); I.Add(k + 2);
        }

        /// <summary>Smooth-ish blob normals: point away from a centre (soft crowns).</summary>
        public void Blob(Vector3 a, Vector3 b, Vector3 c, Vector3 centre, Color col)
        {
            // Keep every face pointing outwards (consistent winding lets us cull back faces: no shadow acne).
            var fn = (b - a).Cross(c - a);
            if (fn.Dot((a + b + c) / 3f - centre) < 0f) (b, c) = (c, b);
            int k = V.Count;
            V.Add(a); V.Add(c); V.Add(b);
            N.Add((a - centre).Normalized()); N.Add((c - centre).Normalized()); N.Add((b - centre).Normalized());
            C.Add(col); C.Add(col); C.Add(col);
            I.Add(k); I.Add(k + 1); I.Add(k + 2);
        }

        public ArrayMesh Build()
        {
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = V.ToArray();
            arrays[(int)Mesh.ArrayType.Normal] = N.ToArray();
            arrays[(int)Mesh.ArrayType.Color] = C.ToArray();
            arrays[(int)Mesh.ArrayType.Index] = I.ToArray();
            var m = new ArrayMesh();
            m.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            return m;
        }
    }

    static void Cylinder(MeshBuilder b, Vector3 bottom, float r0, float r1, float h, int sides, Color col)
    {
        for (int s = 0; s < sides; s++)
        {
            float a0 = s * MathF.Tau / sides, a1 = (s + 1) * MathF.Tau / sides;
            var p0 = bottom + new Vector3(MathF.Cos(a0) * r0, 0, MathF.Sin(a0) * r0);
            var p1 = bottom + new Vector3(MathF.Cos(a1) * r0, 0, MathF.Sin(a1) * r0);
            var q0 = bottom + new Vector3(MathF.Cos(a0) * r1, h, MathF.Sin(a0) * r1);
            var q1 = bottom + new Vector3(MathF.Cos(a1) * r1, h, MathF.Sin(a1) * r1);
            b.Tri(p0, q0, p1, col); b.Tri(p1, q0, q1, col);
        }
    }

    static ArrayMesh ConiferMesh()
    {
        var b = new MeshBuilder();
        var bark = new Color(0.25f, 0.18f, 0.12f, 0f);
        Cylinder(b, Vector3.Zero, 0.28f, 0.12f, 4f, 6, bark);
        var rng = new Rng(77);
        float y = 2.2f;
        for (int tier = 0; tier < 5; tier++)
        {
            float r = 2.6f - tier * 0.45f, h = 3.4f - tier * 0.35f;
            var col = new Color(0.11f + tier * 0.01f, 0.20f + tier * 0.012f, 0.10f, 0.6f);
            int sides = 9;
            var apex = new Vector3(rng.Range(-0.1f, 0.1f), y + h, rng.Range(-0.1f, 0.1f));
            var centre = new Vector3(0, y + h * 0.35f, 0);
            for (int s = 0; s < sides; s++)
            {
                float a0 = s * MathF.Tau / sides, a1 = (s + 1) * MathF.Tau / sides;
                float j0 = 1f + 0.18f * MathF.Sin(a0 * 3f + tier), j1 = 1f + 0.18f * MathF.Sin(a1 * 3f + tier);
                var p0 = new Vector3(MathF.Cos(a0) * r * j0, y - 0.25f * j0, MathF.Sin(a0) * r * j0);
                var p1 = new Vector3(MathF.Cos(a1) * r * j1, y - 0.25f * j1, MathF.Sin(a1) * r * j1);
                b.Blob(p0, apex, p1, centre, col);
                b.Blob(p1, new Vector3(0, y + 0.3f, 0), p0, centre, col * 0.7f); // underside
            }
            y += h * 0.55f;
        }
        return b.Build();
    }

    static void Crown(MeshBuilder b, Vector3 c, float r, Color col, Rng rng)
    {
        // Icosahedron subdivided once, jittered.
        float t = (1f + MathF.Sqrt(5f)) / 2f;
        var v = new List<Vector3> { new(-1, t, 0), new(1, t, 0), new(-1, -t, 0), new(1, -t, 0), new(0, -1, t), new(0, 1, t),
            new(0, -1, -t), new(0, 1, -t), new(t, 0, -1), new(t, 0, 1), new(-t, 0, -1), new(-t, 0, 1) };
        int[] f = { 0,11,5, 0,5,1, 0,1,7, 0,7,10, 0,10,11, 1,5,9, 5,11,4, 11,10,2, 10,7,6, 7,1,8,
                    3,9,4, 3,4,2, 3,2,6, 3,6,8, 3,8,9, 4,9,5, 2,4,11, 6,2,10, 8,6,7, 9,8,1 };
        for (int i = 0; i < v.Count; i++) v[i] = v[i].Normalized();
        for (int k = 0; k < f.Length; k += 3)
        {
            var a = v[f[k]]; var bb = v[f[k + 1]]; var cc = v[f[k + 2]];
            var ab = ((a + bb) * 0.5f).Normalized(); var bc = ((bb + cc) * 0.5f).Normalized(); var ca = ((cc + a) * 0.5f).Normalized();
            Vector3 P(Vector3 d) => c + d * r * (1f + 0.22f * River.Core.Noise.Gradient(d.X * 2.1f + c.X, d.Y * 2.1f + d.Z * 1.3f + c.Y, 5));
            var shade = col * (0.85f + 0.15f * ((a.Y + 1f) * 0.5f));
            shade.A = col.A;
            b.Blob(P(a), P(ca), P(ab), c, shade); b.Blob(P(ab), P(bc), P(bb), c, shade);
            b.Blob(P(ca), P(cc), P(bc), c, shade); b.Blob(P(ab), P(ca), P(bc), c, shade);
        }
    }

    static ArrayMesh BroadleafMesh()
    {
        var b = new MeshBuilder();
        var rng = new Rng(91);
        Cylinder(b, Vector3.Zero, 0.3f, 0.16f, 5.5f, 6, new Color(0.30f, 0.25f, 0.19f, 0f));
        var col = new Color(0.20f, 0.30f, 0.11f, 0.7f);
        Crown(b, new Vector3(0, 7.2f, 0), 3.2f, col, rng);
        Crown(b, new Vector3(1.8f, 6.0f, 0.6f), 2.4f, col * 0.95f, rng);
        Crown(b, new Vector3(-1.5f, 6.3f, -0.9f), 2.5f, col * 1.05f, rng);
        Crown(b, new Vector3(0.2f, 8.8f, -0.4f), 2.1f, col * 1.08f, rng);
        return b.Build();
    }

    static ArrayMesh BushMesh()
    {
        var b = new MeshBuilder();
        var rng = new Rng(13);
        var col = new Color(0.22f, 0.31f, 0.12f, 0.7f);
        Crown(b, new Vector3(0, 0.9f, 0), 1.3f, col, rng);
        Crown(b, new Vector3(0.9f, 0.7f, 0.3f), 1.0f, col * 0.92f, rng);
        Crown(b, new Vector3(-0.7f, 0.75f, -0.4f), 1.05f, col * 1.05f, rng);
        return b.Build();
    }

    // ---------------------------------------------------------------- rocks

    static Node3D BuildRocks(ReachField f)
    {
        var root = new Node3D { Name = "Rocks" };
        // Rock.gdshader: wet band, water film and foam line, from each rock's water level and current.
        var mat = new ShaderMaterial { Shader = GD.Load<Shader>("res://Game/Shaders/Rock.gdshader") };
        var body = new StaticBody3D { Name = "RockCollision", CollisionLayer = 1, CollisionMask = 0 };
        root.AddChild(body);
        foreach (var r in f.Rocks)
        {
            var mesh = RockMesh(r.Seed, r.Radius);
            float eta = f.SampleEta(r.X, r.Z);
            var pos = new Vector3(r.X, eta + r.Top - r.Radius * 0.55f, r.Z);
            var mi = new MeshInstance3D { Mesh = mesh, MaterialOverride = mat, Position = pos };
            float speed = MathF.Sqrt(r.FlowX * r.FlowX + r.FlowZ * r.FlowZ);
            var dir = speed > 1e-3f ? new Vector2(r.FlowX / speed, r.FlowZ / speed) : new Vector2(0, 1);
            // Grid axes → world axes (Rock.gdshader works in world space).
            float co = MathF.Cos(f.Frame.Heading), si = MathF.Sin(f.Frame.Heading);
            dir = new Vector2(dir.X * co + dir.Y * si, -dir.X * si + dir.Y * co);
            mi.SetInstanceShaderParameter("water_level", eta);
            mi.SetInstanceShaderParameter("flow", new Vector4(dir.X, dir.Y, speed, r.Radius));
            root.AddChild(mi);
            body.AddChild(new CollisionShape3D { Shape = new SphereShape3D { Radius = r.Radius * 0.85f }, Position = pos });
            var splash = RockSplash.Create(f, r);
            if (splash != null) root.AddChild(splash);
        }
        return root;
    }

    static ArrayMesh RockMesh(int seed, float radius)
    {
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        var sphere = new SphereMesh { Radius = 1f, Height = 2f, RadialSegments = 14, Rings = 8 };
        var arr = sphere.GetMeshArrays();
        var verts = (Vector3[])arr[(int)Mesh.ArrayType.Vertex];
        var idx = (int[])arr[(int)Mesh.ArrayType.Index];
        for (int i = 0; i < verts.Length; i++)
        {
            var d = verts[i];
            float n = River.Core.Noise.Fbm(d.X * 1.3f + seed, d.Y * 1.3f + d.Z * 0.7f, seed, 3);
            var scale = new Vector3(1.15f, 0.75f, 1f);
            verts[i] = d * scale * radius * (1f + 0.28f * n);
        }
        for (int k = 0; k < idx.Length; k++) st.AddVertex(verts[idx[k]]);
        st.GenerateNormals();
        return st.Commit();
    }
}
