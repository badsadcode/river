using System;
using System.Collections.Generic;
using Godot;
using River.Core;

namespace River.World;

/// <summary>
/// What a waterfall adds to its segment (grid-local, under the segment root): the falling curtain from the lip to the
/// plunge pool, spray and drifting mist at its foot, and a post at the portage trail's head (D21). The water surface
/// itself stops at the lip (WaterBuilder skips cells across the drop); the generator lowered the water below it.
/// </summary>
public static class FallsBuilder
{
    public static Node3D Build(ReachField f)
    {
        var fa = f.Falls;
        var root = new Node3D { Name = "Waterfall" };
        root.AddChild(Curtain(f, fa));
        root.AddChild(Spray(f, fa, mist: false));
        root.AddChild(Spray(f, fa, mist: true));
        root.AddChild(TrailPost(f, fa));
        return root;
    }

    /// <summary>
    /// The sheet: columns every 0.5 m across the lip wherever there is water just above it, each falling along
    /// x = throw·√u, y = top − drop·u (a parabola), starting 1 m upstream of the lip line where the grid's drop is.
    /// </summary>
    static MeshInstance3D Curtain(ReachField f, FallsSpec fa)
    {
        float ax = -fa.DirZ, az = fa.DirX;                    // across the river
        float drop = fa.TopEta - fa.BottomEta;
        float throwDist = 1.2f + 0.12f * drop;
        const int rows = 16;
        var verts = new List<Vector3>(); var uvs = new List<Vector2>(); var idx = new List<int>();
        var cols = new List<float>();
        float reach = fa.HalfWidth * 1.6f + 10f;
        for (float c = -reach; c <= reach; c += 0.5f)
        {
            float px = fa.LipX + ax * c - fa.DirX * 1.5f, pz = fa.LipZ + az * c - fa.DirZ * 1.5f;
            if (!f.InDomain(px, pz) || f.SampleDepth(px, pz) < 0.15f) continue;
            // Boulders on the lip split the sheet.
            float qx = fa.LipX + ax * c, qz = fa.LipZ + az * c;
            bool rock = false;
            foreach (var r in f.Rocks)
                if ((r.X - qx) * (r.X - qx) + (r.Z - qz) * (r.Z - qz) < (r.Radius * 0.9f) * (r.Radius * 0.9f)) { rock = true; break; }
            if (rock) continue;
            cols.Add(c);
        }
        int prev = -1; float prevC = float.NaN;
        foreach (float c in cols)
        {
            int start = verts.Count;
            // Each part of the sheet falls a little differently: more or less water, a ragged lip.
            float vary = River.Core.Noise.Fbm1(c * 0.21f, f.Seed + 77, 2);
            float th = throwDist * (1f + 0.35f * vary), lipOff = 0.25f * River.Core.Noise.Fbm1(c * 0.6f, f.Seed + 78, 2);
            for (int r = 0; r <= rows; r++)
            {
                float u = r / (float)rows;
                float fwd = 1.0f + lipOff + th * MathF.Sqrt(u);
                float y = fa.TopEta + 0.04f - (drop + 0.35f) * u;
                verts.Add(new Vector3(fa.LipX + ax * c + fa.DirX * fwd, y, fa.LipZ + az * c + fa.DirZ * fwd));
                uvs.Add(new Vector2(c, u));
            }
            if (prev >= 0 && MathF.Abs(c - prevC) < 0.75f)
                for (int r = 0; r < rows; r++)
                {
                    int a = prev + r, b = start + r, d = start + r + 1, e = prev + r + 1;
                    idx.Add(a); idx.Add(b); idx.Add(d); idx.Add(a); idx.Add(d); idx.Add(e);
                }
            prev = start; prevC = c;
        }
        var mi = new MeshInstance3D { Name = "Curtain", CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        if (idx.Count == 0) return mi;
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts.ToArray();
        arrays[(int)Mesh.ArrayType.TexUV] = uvs.ToArray();
        arrays[(int)Mesh.ArrayType.Index] = idx.ToArray();
        var st = new SurfaceTool();
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        st.CreateFrom(mesh, 0);
        st.GenerateNormals();
        var mat = new ShaderMaterial { Shader = GD.Load<Shader>("res://Game/Shaders/Falls.gdshader") };
        mat.SetShaderParameter("drop", drop);
        mi.Mesh = st.Commit();
        mi.MaterialOverride = mat;
        return mi;
    }

    /// <summary>Spray at the foot of the falls (fast droplets) or the drifting mist cloud above the pool (slow, large).</summary>
    static GpuParticles3D Spray(ReachField f, FallsSpec fa, bool mist)
    {
        float drop = fa.TopEta - fa.BottomEta;
        float co = MathF.Cos(f.Frame.Heading), si = MathF.Sin(f.Frame.Heading);
        // Particles fly in world space: directions in world axes.
        float wdx = fa.DirX * co + fa.DirZ * si, wdz = -fa.DirX * si + fa.DirZ * co;
        var pm = new ParticleProcessMaterial
        {
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Box,
            EmissionBoxExtents = new Vector3(fa.HalfWidth * 1.1f, 0.3f, 2f),
            Direction = new Vector3(wdx * 0.5f, 1f, wdz * 0.5f).Normalized(),
            Spread = mist ? 50f : 35f,
            InitialVelocityMin = mist ? 0.6f : 2f + 0.25f * drop,
            InitialVelocityMax = mist ? 1.6f : 4f + 0.4f * drop,
            Gravity = mist ? new Vector3(wdx * 0.4f, 0.15f, wdz * 0.4f) : new Vector3(wdx * 1.5f, -6f, wdz * 1.5f),
            ScaleMin = mist ? 0.7f : 0.5f, ScaleMax = mist ? 1.6f : 1.4f,
            ColorRamp = new GradientTexture1D
            {
                Gradient = new Gradient
                {
                    Offsets = new[] { 0f, 0.2f, 1f },
                    Colors = mist
                        ? new[] { new Color(1, 1, 1, 0f), new Color(1, 1, 1, 0.3f), new Color(1, 1, 1, 0f) }
                        : new[] { new Color(1, 1, 1, 0f), new Color(1, 1, 1, 0.8f), new Color(1, 1, 1, 0f) },
                },
            },
        };
        // The emitter box is in the segment's grid axes (its parent's); align it with the lip.
        float yaw = MathF.Atan2(fa.DirX, fa.DirZ);
        return new GpuParticles3D
        {
            Name = mist ? "FallsMist" : "FallsSpray",
            Amount = mist ? 140 : 320,
            Lifetime = mist ? 5f : 1.4f,
            ProcessMaterial = pm,
            DrawPass1 = new QuadMesh { Size = mist ? new Vector2(6f, 6f) : new Vector2(0.35f, 0.35f), Material = SprayMaterial() },
            Position = new Vector3(fa.LipX + fa.DirX * 3.5f, fa.BottomEta + 0.2f, fa.LipZ + fa.DirZ * 3.5f),
            Rotation = new Vector3(0f, yaw, 0f),
            VisibilityAabb = new Aabb(new Vector3(-60, -5, -60), new Vector3(120, 40, 120)),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Randomness = 0.6f,
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
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
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
