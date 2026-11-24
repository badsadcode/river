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
        var cols = Columns(f, fa);
        var shader = GD.Load<Shader>("res://Game/Shaders/Falls.gdshader");
        float drop = fa.TopEta - fa.BottomEta;
        ShaderMaterial Mat(int mode)
        {
            var m = new ShaderMaterial { Shader = shader };
            m.SetShaderParameter("drop", drop);
            m.SetShaderParameter("mode", mode);
            return m;
        }
        root.AddChild(Sheet(cols, fa, veil: false, Mat(0)));
        root.AddChild(Sheet(cols, fa, veil: true, Mat(1)));
        root.AddChild(Spray(f, fa, mist: false));
        root.AddChild(Spray(f, fa, mist: true));
        root.AddChild(TrailPost(f, fa));
        root.AddChild(LedgeRocks(f, fa, cols));
        return root;
    }

    /// <summary>
    /// Rock ledges on the face beside the falls: big flattened blocks half sunk into the scarp on both banks, from the
    /// edge of the water outward, at different heights between the pool and the brink (wet near the pool: Rock
    /// shader). They break up the smooth slope the terrain alone would show there. Decoration only (no collision).
    /// </summary>
    static Node3D LedgeRocks(ReachField f, FallsSpec fa, List<Column> cols)
    {
        var root = new Node3D { Name = "LedgeRocks" };
        if (cols.Count == 0) return root;
        float drop = fa.TopEta - fa.BottomEta;
        float ax = -fa.DirZ, az = fa.DirX;
        var mat = new ShaderMaterial { Shader = GD.Load<Shader>("res://Game/Shaders/Rock.gdshader") };
        var rng = new Rng(f.Seed + 811);
        float yaw = MathF.Atan2(fa.DirX, fa.DirZ);
        float co = MathF.Cos(f.Frame.Heading), si = MathF.Sin(f.Frame.Heading);
        var wdir = new Vector2(fa.DirX * co + fa.DirZ * si, -fa.DirX * si + fa.DirZ * co);
        void Place(float across, float lipS)
        {
            float r = rng.Range(1.2f, 2.6f);
            float y = fa.BottomEta + drop * rng.Range(0.05f, 0.85f);
            // On the face: a little downstream of the lip line, more so lower down (the face leans back).
            float s = lipS + rng.Range(-0.5f, 1.5f);
            var pos = new Vector3(fa.LipX + ax * across + fa.DirX * s, y, fa.LipZ + az * across + fa.DirZ * s);
            var mi = new MeshInstance3D
            {
                Mesh = Vegetation.RockMesh(f.Seed + 900 + rng.Range(0, 100000), r), MaterialOverride = mat, Position = pos,
                Rotation = new Vector3(rng.Range(-0.2f, 0.2f), yaw + rng.Range(-0.5f, 0.5f), rng.Range(-0.15f, 0.15f)),
                Scale = new Vector3(rng.Range(1.1f, 1.6f), rng.Range(0.55f, 0.8f), rng.Range(0.9f, 1.3f)),
            };
            mi.SetInstanceShaderParameter("water_level", fa.BottomEta);
            mi.SetInstanceShaderParameter("flow", new Vector4(wdir.X, wdir.Y, 0.4f, r));
            root.AddChild(mi);
        }
        // From each edge of the falls outward along the lip line, 16 m into the banks.
        float minC = cols[0].Across, maxC = cols[^1].Across;
        float LipS(float across) => FallsSpec.LipAt(across, fa.HalfWidth, fa.Height, f.Seed);
        for (float d = 0.5f; d < 16f; d += rng.Range(1.8f, 3.2f))
        {
            Place(minC - d, LipS(minC - d));
            Place(maxC + d, LipS(maxC + d));
            if (rng.Next01() < 0.5f) { Place(minC - d, LipS(minC - d)); Place(maxC + d, LipS(maxC + d)); }
        }
        // And at the foot of the face between separate falls (where the lip ledges split them).
        for (int i = 1; i < cols.Count; i++)
            if (cols[i].Across - cols[i - 1].Across > 1.5f)
                for (float c = cols[i - 1].Across + 0.8f; c < cols[i].Across - 0.5f; c += 1.6f) Place(c, LipS(c));
        return root;
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
    /// Columns every 0.5 m across the river: for each, march along the flow until the water surface drops (the lip,
    /// curved and ragged as the generator made it); none where a lip ledge stands or there is no water above. Each
    /// gets its own amount of water (slow noise across: full sheets here, thin threads there).
    /// </summary>
    static List<Column> Columns(ReachField f, FallsSpec fa)
    {
        var cols = new List<Column>();
        float ax = -fa.DirZ, az = fa.DirX;
        float drop = fa.TopEta - fa.BottomEta;
        float reach = fa.HalfWidth * 1.6f + 10f;
        var raw = new List<(float c, float s)>();
        for (float c = -reach; c <= reach; c += 0.5f)
        {
            float bx = fa.LipX + ax * c, bz = fa.LipZ + az * c;
            float lipS = float.NaN;
            for (float t = -18f; t <= 10f; t += 0.25f)
            {
                float px = bx + fa.DirX * t, pz = bz + fa.DirZ * t;
                if (!f.InDomain(px, pz)) break;
                if (f.SampleEta(px, pz) < fa.TopEta - 0.5f * drop) { lipS = t - 0.25f; break; }
            }
            if (float.IsNaN(lipS)) continue;
            raw.Add((c, lipS));
        }
        // Neighbouring columns leave the edge at nearly the same place: smooth the ragged lip over ±1.5 m, or the
        // strips between them twist into folded ribbons.
        for (int q = 0; q < raw.Count; q++)
        {
            float c = raw[q].c, sum = 0f; int n = 0;
            for (int o = Math.Max(0, q - 5); o <= Math.Min(raw.Count - 1, q + 5); o++)
                if (MathF.Abs(raw[o].c - c) < 2.6f) { sum += raw[o].s; n++; }
            float lipS = sum / n;
            float bx = fa.LipX + ax * c, bz = fa.LipZ + az * c;
            float lx = bx + fa.DirX * lipS, lz = bz + fa.DirZ * lipS;
            if (f.SampleDepth(lx - fa.DirX * 1.5f, lz - fa.DirZ * 1.5f) < 0.15f) continue;
            bool rock = false;
            foreach (var r in f.Rocks)
                if ((r.X - lx) * (r.X - lx) + (r.Z - lz) * (r.Z - lz) < (r.Radius * 0.85f) * (r.Radius * 0.85f)) { rock = true; break; }
            if (rock) continue;
            float amount = Math.Clamp(0.55f + 0.75f * River.Core.Noise.Fbm1(c / 4.5f, f.Seed + 77, 2), 0.25f, 1f);
            cols.Add(new Column
            {
                // The top follows the water at the brink (drawn down there), tucked 2 cm under it: no raised band.
                // (Read 2.5 m upstream and never below the pool above: just at the edge the sample can fall into the
                // pool below and the strip would start metres too low.)
                Across = c, Lip = new Vector3(lx, MathF.Max(f.SampleEta(lx - fa.DirX * 2.5f, lz - fa.DirZ * 2.5f), fa.TopEta - 0.4f) - 0.02f, lz),
                Dir = new Vector3(fa.DirX, 0f, fa.DirZ),
                Amount = amount, Seed = River.Core.Noise.Hash01((int)MathF.Round(c * 2f), 3, f.Seed + 79),
            });
        }
        return cols;
    }

    const int Rows = 22;

    static float Throw(in Column col, float drop) => (0.5f + 1.6f * col.Amount) * (1f + 0.08f * drop);

    /// <summary>
    /// The falling sheet (or the veil of spray just in front of it). From 0.8 m upstream of the edge the surface curves
    /// over (the brink), then each column falls along a parabola: fuller columns are thrown further out, thin ones
    /// cling closer to the rock. Columns join into a sheet where their lips are close; gaps where they aren't.
    /// COLOR: r = amount, g = seed.
    /// </summary>
    static MeshInstance3D Sheet(List<Column> cols, FallsSpec fa, bool veil, ShaderMaterial mat)
    {
        float drop = fa.TopEta - fa.BottomEta;
        var verts = new List<Vector3>(); var uvs = new List<Vector2>(); var colors = new List<Color>(); var idx = new List<int>();
        int prev = -1; Vector3 prevLip = default; float prevC = float.NaN;
        foreach (var col in cols)
        {
            int start = verts.Count;
            float th = Throw(col, drop);
            float out0 = veil ? 0.25f + 0.35f * col.Amount : 0f;
            for (int r = 0; r <= Rows; r++)
            {
                float u = r / (float)Rows;
                float brink = MathUtil.Smoothstep(0.12f, 0f, u);
                // (The brink starts 1.5 m upstream, under the river surface's last cells: no gap between them.)
                float fwd = -1.5f * brink + th * MathF.Sqrt(u) + out0 * MathUtil.Smoothstep(0.05f, 0.4f, u) * (1f + u);
                // Slower at first (the water leaves the edge horizontally), then falling.
                float fall = (col.Lip.Y - fa.BottomEta + 0.4f) * (0.35f * u * u + 0.65f * u);
                verts.Add(col.Lip + col.Dir * fwd + new Vector3(0f, -fall, 0f));
                uvs.Add(new Vector2(col.Across, u));
                colors.Add(new Color(col.Amount, col.Seed, veil ? 1f : 0f));
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
        return MeshOf(veil ? "Veil" : "Curtain", verts, uvs, colors, idx, mat);
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
    static GpuParticles3D Spray(ReachField f, FallsSpec fa, bool mist)
    {
        float drop = fa.TopEta - fa.BottomEta;
        float co = MathF.Cos(f.Frame.Heading), si = MathF.Sin(f.Frame.Heading);
        // Particles fly in world space: directions in world axes.
        float wdx = fa.DirX * co + fa.DirZ * si, wdz = -fa.DirX * si + fa.DirZ * co;
        var pm = new ParticleProcessMaterial
        {
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Box,
            EmissionBoxExtents = new Vector3(fa.HalfWidth * 1.1f, 0.3f, mist ? 4f : 2f),
            Direction = new Vector3(wdx * 0.5f, 1f, wdz * 0.5f).Normalized(),
            Spread = mist ? 50f : 35f,
            InitialVelocityMin = mist ? 0.6f : 2f + 0.25f * drop,
            InitialVelocityMax = mist ? 1.8f : 4f + 0.4f * drop,
            Gravity = mist ? new Vector3(wdx * 0.4f, 0.2f, wdz * 0.4f) : new Vector3(wdx * 1.5f, -6f, wdz * 1.5f),
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
                        ? new[] { new Color(1, 1, 1, 0f), new Color(1, 1, 1, 0.2f), new Color(1, 1, 1, 0f) }
                        : new[] { new Color(1, 1, 1, 0f), new Color(1, 1, 1, 0.8f), new Color(1, 1, 1, 0f) },
                },
            },
        };
        // The emitter box is in the segment's grid axes (its parent's); align it with the lip.
        float yaw = MathF.Atan2(fa.DirX, fa.DirZ);
        return new GpuParticles3D
        {
            Name = mist ? "FallsMist" : "FallsSpray",
            Amount = mist ? 220 : 420,
            Lifetime = mist ? 7f : 1.6f,
            ProcessMaterial = pm,
            DrawPass1 = new QuadMesh { Size = mist ? new Vector2(8f, 8f) : new Vector2(0.4f, 0.4f), Material = mist ? MistMaterial() : SprayMaterial() },
            Position = new Vector3(fa.LipX + fa.DirX * 3.5f, fa.BottomEta + 0.2f, fa.LipZ + fa.DirZ * 3.5f),
            Rotation = new Vector3(0f, yaw, 0f),
            VisibilityAabb = new Aabb(new Vector3(-60, -5, -60), new Vector3(120, 40, 120)),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Randomness = 0.6f,
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
