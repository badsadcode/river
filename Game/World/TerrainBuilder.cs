using System;
using Godot;
using River.Core;
using Noise = River.Core.Noise;

namespace River.World;

/// <summary>
/// Terrain meshes for the reach (chunks with two levels of detail and skirts), one heightmap collision shape,
/// and a coarse "far land" ring out to the horizon so the world doesn't end at the edge of the grid.
/// </summary>
public static class TerrainBuilder
{
    const int ChunkCells = 64;          // 128 m chunks at 2 m
    const float LodSwitch = 650f;       // m: full detail inside, quarter detail outside

    public static Node3D Build(ReachField f, FieldTextures tex)
    {
        var root = new Node3D { Name = "Terrain" };
        var shader = GD.Load<Shader>("res://Game/Shaders/Terrain.gdshader");
        var mat = new ShaderMaterial { Shader = shader };
        tex.Apply(mat);
        mat.SetShaderParameter("far_eta_a", f.EtaTrendA);
        mat.SetShaderParameter("far_eta_b", f.EtaTrendB);

        // Normals from the whole field (seamless across chunks).
        var normals = ComputeNormals(f);

        for (int cz = 0; cz < f.Nz - 1; cz += ChunkCells)
            for (int cx = 0; cx < f.Nx - 1; cx += ChunkCells)
            {
                int w = Math.Min(ChunkCells, f.Nx - 1 - cx), h = Math.Min(ChunkCells, f.Nz - 1 - cz);
                var centre = new Vector3((cx + w * 0.5f) * f.Cell, 0, (cz + h * 0.5f) * f.Cell);
                var near = new MeshInstance3D
                {
                    Name = $"T{cx}_{cz}", Mesh = ChunkMesh(f, normals, cx, cz, w, h, 1), MaterialOverride = mat,
                    VisibilityRangeEnd = LodSwitch, VisibilityRangeEndMargin = 20f,
                };
                var far = new MeshInstance3D
                {
                    Name = $"T{cx}_{cz}_far", Mesh = ChunkMesh(f, normals, cx, cz, w, h, 4), MaterialOverride = mat,
                    VisibilityRangeBegin = LodSwitch, VisibilityRangeBeginMargin = 20f,
                };
                root.AddChild(near);
                root.AddChild(far);
            }

        root.AddChild(BuildCollision(f));
        root.AddChild(BuildFarLand(f, mat));
        return root;
    }

    static Vector3[] ComputeNormals(ReachField f)
    {
        var n = new Vector3[f.Nx * f.Nz];
        System.Threading.Tasks.Parallel.For(0, f.Nz, z =>
        {
            for (int x = 0; x < f.Nx; x++)
            {
                float hl = f.Height[z * f.Nx + Math.Max(0, x - 1)], hr = f.Height[z * f.Nx + Math.Min(f.Nx - 1, x + 1)];
                float hd = f.Height[Math.Max(0, z - 1) * f.Nx + x], hu = f.Height[Math.Min(f.Nz - 1, z + 1) * f.Nx + x];
                n[z * f.Nx + x] = new Vector3(hl - hr, 2f * f.Cell, hd - hu).Normalized();
            }
        });
        return n;
    }

    static ArrayMesh ChunkMesh(ReachField f, Vector3[] normals, int cx, int cz, int w, int h, int step)
    {
        int vx = w / step + 1, vz = h / step + 1;
        // Grid vertices + a skirt ring (hangs 3 m down) to hide cracks between levels of detail.
        int gridCount = vx * vz, ring = 2 * (vx + vz);
        var verts = new Vector3[gridCount + ring];
        var norms = new Vector3[gridCount + ring];
        for (int z = 0; z < vz; z++)
            for (int x = 0; x < vx; x++)
            {
                int gx = Math.Min(cx + x * step, f.Nx - 1), gz = Math.Min(cz + z * step, f.Nz - 1);
                int i = gz * f.Nx + gx;
                verts[z * vx + x] = new Vector3(gx * f.Cell, f.Height[i], gz * f.Cell);
                norms[z * vx + x] = normals[i];
            }
        var idx = new System.Collections.Generic.List<int>(vx * vz * 6 + ring * 6);
        for (int z = 0; z < vz - 1; z++)
            for (int x = 0; x < vx - 1; x++)
            {
                int a = z * vx + x, b = a + 1, c = a + vx, d = c + 1;
                // Alternate the diagonal for a less regular look.
                if (((x + z) & 1) == 0) { idx.Add(a); idx.Add(b); idx.Add(d); idx.Add(a); idx.Add(d); idx.Add(c); }
                else { idx.Add(a); idx.Add(b); idx.Add(c); idx.Add(b); idx.Add(d); idx.Add(c); }
            }
        // Skirt: walk the border.
        var border = new System.Collections.Generic.List<int>();
        for (int x = 0; x < vx; x++) border.Add(x);
        for (int z = 1; z < vz; z++) border.Add(z * vx + vx - 1);
        for (int x = vx - 2; x >= 0; x--) border.Add((vz - 1) * vx + x);
        for (int z = vz - 2; z >= 1; z--) border.Add(z * vx);
        int s0 = gridCount;
        for (int k = 0; k < border.Count; k++)
        {
            verts[s0 + k] = verts[border[k]] - new Vector3(0, 3f, 0);
            norms[s0 + k] = norms[border[k]];
        }
        for (int k = 0; k < border.Count; k++)
        {
            int a = border[k], b = border[(k + 1) % border.Count], sa = s0 + k, sb = s0 + (k + 1) % border.Count;
            idx.Add(a); idx.Add(sa); idx.Add(b); idx.Add(b); idx.Add(sa); idx.Add(sb);
        }
        Array.Resize(ref verts, s0 + border.Count);
        Array.Resize(ref norms, s0 + border.Count);

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts;
        arrays[(int)Mesh.ArrayType.Normal] = norms;
        arrays[(int)Mesh.ArrayType.Index] = idx.ToArray();
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        // Godot culls by winding; make sure faces point up (counter-clockwise seen from above in Godot = clockwise in x/z).
        return mesh;
    }

    static StaticBody3D BuildCollision(ReachField f)
    {
        // HeightMapShape3D samples are 1 unit apart: scale the shape node uniformly by the cell size.
        var data = new float[f.Nx * f.Nz];
        for (int i = 0; i < data.Length; i++) data[i] = f.Height[i] / f.Cell;
        var shape = new HeightMapShape3D { MapWidth = f.Nx, MapDepth = f.Nz, MapData = data };
        var body = new StaticBody3D { Name = "TerrainCollision", CollisionLayer = 1, CollisionMask = 0 };
        body.PhysicsMaterialOverride = new PhysicsMaterial { Friction = 0.6f };
        var cs = new CollisionShape3D { Shape = shape };
        cs.Scale = Vector3.One * f.Cell;
        cs.Position = new Vector3(f.SizeX * 0.5f, 0, f.SizeZ * 0.5f);
        body.AddChild(cs);
        return body;
    }

    /// <summary>Height of the land outside the grid: floodplain + valley relief, the river continuing in a trench.</summary>
    public static float FarHeight(ReachField f, float x, float z, out float waterEta)
    {
        float eta = f.EtaTrend(z);
        float h = eta + 1.2f + Valley.FloodplainRelief(x, z, f.Seed) + Valley.Relief(x, z, f.Seed, f.SizeX);
        waterEta = float.NaN;
        // The river continues up- and downstream beyond the grid: a channel towards the inlet/outlet.
        if (z < 0f || z > f.SizeZ)
        {
            var c = f.Main;
            int k = z < 0f ? 0 : c.Count - 1;
            float ex = c.X[k] + (z < 0f ? -1f : 1f) * 0f;
            float wander = 60f * Noise.Fbm1(z / 500f, f.Seed + 401, 2) * MathUtil.Smoothstep(0f, 400f, MathF.Abs(z < 0 ? z : z - f.SizeZ));
            float d = MathF.Abs(x - (ex + wander));
            float half = 20f;
            float bank = MathUtil.Smoothstep(half, half + 25f, d);
            float riverEta = eta;
            h = MathUtil.Lerp(riverEta - 1.6f, h, bank);
            if (d < half + 6f) waterEta = riverEta;
        }
        return h;
    }

    static MeshInstance3D BuildFarLand(ReachField f, ShaderMaterial mat)
    {
        const float step = 24f, margin = 5000f;
        float x0 = -margin, x1 = f.SizeX + margin, z0 = -margin, z1 = f.SizeZ + margin;
        int vx = (int)((x1 - x0) / step) + 1, vz = (int)((z1 - z0) / step) + 1;
        var verts = new Vector3[vx * vz];
        for (int z = 0; z < vz; z++)
            for (int x = 0; x < vx; x++)
            {
                float wx = x0 + x * step, wz = z0 + z * step;
                bool insideGrid = wx > 0 && wx < f.SizeX && wz > 0 && wz < f.SizeZ;
                float hgt = insideGrid ? f.SampleHeight(wx, wz) - 1.5f : FarHeight(f, wx, wz, out _);
                // Blend into the grid's own edge heights so the seam is hidden.
                if (!insideGrid)
                {
                    float cx = Math.Clamp(wx, 0, f.SizeX), czz = Math.Clamp(wz, 0, f.SizeZ);
                    float dEdge = MathF.Sqrt((wx - cx) * (wx - cx) + (wz - czz) * (wz - czz));
                    float edgeH = f.SampleHeight(cx, czz) - 0.3f;
                    hgt = MathUtil.Lerp(edgeH, hgt, MathUtil.Smoothstep(0f, 200f, dEdge));
                }
                verts[z * vx + x] = new Vector3(wx, hgt, wz);
            }
        var idx = new System.Collections.Generic.List<int>();
        for (int z = 0; z < vz - 1; z++)
            for (int x = 0; x < vx - 1; x++)
            {
                float wx = x0 + x * step, wz = z0 + z * step;
                // Skip quads well inside the grid (the detailed terrain covers them).
                if (wx > step && wx + step < f.SizeX - step && wz > step && wz + step < f.SizeZ - step) continue;
                int a = z * vx + x, b = a + 1, c = a + vx, d = c + 1;
                idx.Add(a); idx.Add(b); idx.Add(d); idx.Add(a); idx.Add(d); idx.Add(c);
            }
        var norms = new Vector3[verts.Length];
        for (int z = 0; z < vz; z++)
            for (int x = 0; x < vx; x++)
            {
                float hl = verts[z * vx + Math.Max(0, x - 1)].Y, hr = verts[z * vx + Math.Min(vx - 1, x + 1)].Y;
                float hd = verts[Math.Max(0, z - 1) * vx + x].Y, hu = verts[Math.Min(vz - 1, z + 1) * vx + x].Y;
                norms[z * vx + x] = new Vector3(hl - hr, 2f * step, hd - hu).Normalized();
            }
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts;
        arrays[(int)Mesh.ArrayType.Normal] = norms;
        arrays[(int)Mesh.ArrayType.Index] = idx.ToArray();
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return new MeshInstance3D { Name = "FarLand", Mesh = mesh, MaterialOverride = mat };
    }
}
