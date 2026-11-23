using System;
using System.Collections.Generic;
using Godot;
using River.Core;
using Noise = River.Core.Noise;

namespace River.World;

/// <summary>
/// Water surface meshes: the grid's water (plus a margin tucked under the banks) at the solved surface height,
/// and simple strips where the river continues beyond the grid. One shared river material.
/// </summary>
public static class WaterBuilder
{
    const int ChunkCells = 64;

    public static ShaderMaterial CreateMaterial(FieldTextures tex)
    {
        var mat = new ShaderMaterial { Shader = GD.Load<Shader>("res://Game/Shaders/River.gdshader") };
        tex.Apply(mat);
        mat.SetShaderParameter("ripple_large", ProceduralTextures.WaterNormals(512, 11, 4, 7f));
        mat.SetShaderParameter("ripple_small", ProceduralTextures.WaterNormals(512, 23, 8, 10f));
        mat.SetShaderParameter("foam_tex", ProceduralTextures.Foam(512, 37));
        mat.RenderPriority = 1;
        return mat;
    }

    public static Node3D Build(ReachField f, ShaderMaterial mat)
    {
        var root = new Node3D { Name = "Water" };
        // Rocks for the shader's pillows and hollows (mirror of SurfaceWaves.RockBump), at most 64.
        int nr = Math.Min(64, f.Rocks.Count);
        var ra = new Vector4[64]; var rf = new Vector2[64];
        for (int k = 0; k < nr; k++)
        {
            var r = f.Rocks[k];
            ra[k] = new Vector4(r.X, r.Z, r.Radius, r.Top);
            rf[k] = new Vector2(r.FlowX, r.FlowZ);
        }
        mat.SetShaderParameter("rocks", ra);
        mat.SetShaderParameter("rock_flow", rf);
        mat.SetShaderParameter("rock_count", nr);
        for (int cz = 0; cz < f.Nz - 1; cz += ChunkCells)
            for (int cx = 0; cx < f.Nx - 1; cx += ChunkCells)
            {
                var mesh = ChunkMesh(f, cx, cz, Math.Min(ChunkCells, f.Nx - 1 - cx), Math.Min(ChunkCells, f.Nz - 1 - cz));
                if (mesh == null) continue;
                root.AddChild(new MeshInstance3D
                {
                    Name = $"W{cx}_{cz}", Mesh = mesh, MaterialOverride = mat,
                    CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                });
            }
        root.AddChild(FarStrips(f, mat));
        return root;
    }

    static bool WetEnough(ReachField f, int i) => f.Water[i] || f.ShoreDist[i] < 5f;

    /// <summary>Quads per field cell along each axis: 2 → a 1 m mesh, fine enough to draw riffle standing waves
    /// (≥ 2.5 m) that the vertex shader displaces.</summary>
    const int Sub = 2;

    static ArrayMesh ChunkMesh(ReachField f, int cx, int cz, int w, int h)
    {
        var verts = new List<Vector3>();
        var map = new Dictionary<long, int>();
        var idx = new List<int>();
        int stride = (f.Nx - 1) * Sub + 1;
        int V(int sx, int sz)   // sub-grid vertex
        {
            long key = (long)sz * stride + sx;
            if (map.TryGetValue(key, out int v)) return v;
            v = verts.Count;
            float x = sx * f.Cell / Sub, z = sz * f.Cell / Sub;
            verts.Add(new Vector3(x, f.SampleEta(x, z), z));
            map[key] = v;
            return v;
        }
        for (int z = cz; z < cz + h; z++)
            for (int x = cx; x < cx + w; x++)
            {
                int i00 = z * f.Nx + x;
                if (!(WetEnough(f, i00) || WetEnough(f, i00 + 1) || WetEnough(f, i00 + f.Nx) || WetEnough(f, i00 + f.Nx + 1))) continue;
                for (int qz = 0; qz < Sub; qz++)
                    for (int qx = 0; qx < Sub; qx++)
                    {
                        int sx = x * Sub + qx, sz = z * Sub + qz;
                        int a = V(sx, sz), b = V(sx + 1, sz), c = V(sx, sz + 1), d = V(sx + 1, sz + 1);
                        idx.Add(a); idx.Add(b); idx.Add(d); idx.Add(a); idx.Add(d); idx.Add(c);
                    }
            }
        if (idx.Count == 0) return null;
        var norms = new Vector3[verts.Count];
        Array.Fill(norms, Vector3.Up);
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts.ToArray();
        arrays[(int)Mesh.ArrayType.Normal] = norms;
        arrays[(int)Mesh.ArrayType.Index] = idx.ToArray();
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }

    /// <summary>Flat water following the far trench (TerrainBuilder.FarHeight) up- and downstream of the grid.</summary>
    static MeshInstance3D FarStrips(ReachField f, ShaderMaterial mat)
    {
        const float step = 16f, reach = 5000f;
        var verts = new List<Vector3>();
        var idx = new List<int>();
        void Strip(float zStart, float zEnd)
        {
            int n = (int)(MathF.Abs(zEnd - zStart) / step);
            int baseV = verts.Count;
            int k = zStart < 0.5f * f.SizeZ ? 0 : f.Main.Count - 1;
            for (int s = 0; s <= n; s++)
            {
                float z = zStart + MathF.Sign(zEnd - zStart) * s * step;
                float far = MathF.Abs(z < 0 ? z : z - f.SizeZ);
                float wander = 60f * Noise.Fbm1(z / 500f, f.Seed + 401, 2) * MathUtil.Smoothstep(0f, 400f, far);
                float cx = f.Main.X[k] + wander;
                // Near the grid edge use the grid's own surface so the join is invisible.
                float eta = MathUtil.Lerp(f.SampleEta(f.Main.X[k], Math.Clamp(z, 0, f.SizeZ)), f.EtaTrend(z), MathUtil.Smoothstep(0f, 300f, far));
                verts.Add(new Vector3(cx - 30f, eta, z));
                verts.Add(new Vector3(cx + 30f, eta, z));
            }
            for (int s = 0; s < n; s++)
            {
                int a = baseV + s * 2, b = a + 1, c = a + 2, d = a + 3;
                // Either direction: emit both windings' worth by ordering on z direction.
                if (zEnd > zStart) { idx.Add(a); idx.Add(b); idx.Add(d); idx.Add(a); idx.Add(d); idx.Add(c); }
                else { idx.Add(a); idx.Add(d); idx.Add(b); idx.Add(a); idx.Add(c); idx.Add(d); }
            }
        }
        Strip(-2f, -reach);
        Strip(f.SizeZ + 2f, f.SizeZ + reach);
        var norms = new Vector3[verts.Count];
        Array.Fill(norms, Vector3.Up);
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts.ToArray();
        arrays[(int)Mesh.ArrayType.Normal] = norms;
        arrays[(int)Mesh.ArrayType.Index] = idx.ToArray();
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return new MeshInstance3D { Name = "FarWater", Mesh = mesh, MaterialOverride = mat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
    }
}
