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

    public static Node3D Build(ReachField f)
    {
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
                float shore = f.Sample(f.ShoreDist, px, pz);
                if (shore < 3.5f) continue;
                float hgt = f.SampleHeight(px, pz);
                float above = hgt - f.SampleEta(px, pz);
                if (above < 0.7f) continue;                       // bars and wet margins stay open
                float hx = f.SampleHeight(px + 1.5f, pz) - f.SampleHeight(px - 1.5f, pz);
                float hz = f.SampleHeight(px, pz + 1.5f) - f.SampleHeight(px, pz - 1.5f);
                float slope = MathF.Sqrt(hx * hx + hz * hz) / 3f;
                if (slope > 0.8f) continue;

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
                Add(kind, t, tint);
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
        var mat = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.36f, 0.35f, 0.33f), Roughness = 0.85f,
        };
        var body = new StaticBody3D { Name = "RockCollision", CollisionLayer = 1, CollisionMask = 0 };
        root.AddChild(body);
        foreach (var r in f.Rocks)
        {
            var mesh = RockMesh(r.Seed, r.Radius);
            float eta = f.SampleEta(r.X, r.Z);
            var pos = new Vector3(r.X, eta + r.Top - r.Radius * 0.55f, r.Z);
            root.AddChild(new MeshInstance3D { Mesh = mesh, MaterialOverride = mat, Position = pos });
            body.AddChild(new CollisionShape3D { Shape = new SphereShape3D { Radius = r.Radius * 0.85f }, Position = pos });
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
