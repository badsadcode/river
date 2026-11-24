using System;
using System.Collections.Generic;
using Godot;
using River.Core;

namespace River.World;

/// <summary>
/// Everything drawn for one segment of the endless river (docs/STREAMING.md §5): terrain (with collision), water,
/// vegetation and rocks, mist, and a coarse far-land skirt from the course either side of the grid. Built from the
/// segment's *baked* field (seams blended, SegmentBake) in its grid coordinates, under a root node placed by the
/// segment's frame. The whole subtree is built off the main thread; the streamer then attaches its children a few at
/// a time (<see cref="Pending"/>), so a new segment never causes a hitch.
/// </summary>
public sealed class SegmentView
{
    public int K;
    public Node3D Root;
    public ReachField Field;                       // baked
    public ShaderMaterial WaterMat, TerrainMat;
    public FogVolume Mist;
    public TreeSet Trees;                          // real trees in world space (TreeLod draws them near the camera)
    /// <summary>(parent, child) pairs still to be attached, in order.</summary>
    public readonly Queue<(Node parent, Node child)> Pending = new();
    public bool Attached => Pending.Count == 0;

    public static SegmentView Build(int k, ReachField baked, RiverWorld world, EnvironmentRig env)
    {
        var f = baked;
        var v = new SegmentView { K = k, Field = f };
        v.Root = new Node3D
        {
            Name = $"Segment{k}",
            Position = Origin.ToScene(f.Frame.OriginX, 0f, f.Frame.OriginZ),   // the streamer re-places it on the main thread
            Rotation = new Vector3(0f, f.Frame.Heading, 0f),
        };
        var tex = new FieldTextures(f);

        // Ownership: between cut k (inclusive) and cut k+1, measured in world space.
        bool Owns(float lx, float lz)
        {
            var (wx, wz) = f.Frame.ToWorld(lx, lz);
            return world.PastCut(k, wx, wz) >= 0 && world.PastCut(k + 1, wx, wz) < 0;
        }

        var terrain = TerrainBuilder.Build(f, tex, false, out v.TerrainMat);
        terrain.AddChild(FarLand(k, f, world, v.TerrainMat));
        v.WaterMat = WaterBuilder.CreateMaterial(tex);
        v.WaterMat.SetShaderParameter("frame_rot", new Vector2(MathF.Cos(f.Frame.Heading), MathF.Sin(f.Frame.Heading)));
        var water = WaterBuilder.Build(f, v.WaterMat, false);
        var trees = new List<(Transform3D, ushort)>();
        var veg = Vegetation.Build(f, Owns, trees);
        if (trees.Count > 0)
        {
            // Rotated into world axes, relative to the frame origin (TreeSet keeps that origin in double).
            var rot = new Transform3D(new Basis(Vector3.Up, f.Frame.Heading), Vector3.Zero);
            for (int i = 0; i < trees.Count; i++) trees[i] = (rot * trees[i].Item1, trees[i].Item2);
            v.Trees = new TreeSet(trees, f.Frame.OriginX, f.Frame.OriginZ);
        }
        v.Mist = env.CreateMist(f, tex);

        // Attach order: the (emptied) groups, then their children one by one, then the mist.
        if (f.Falls != null) water.AddChild(FallsBuilder.Build(f));
        var groups = new Node[] { terrain, water, veg };
        var children = new List<(Node, Node)>();
        foreach (var group in groups)
        {
            var kids = new List<Node>();
            foreach (var c in group.GetChildren()) kids.Add(c);
            foreach (var c in kids) { group.RemoveChild(c); children.Add((group, c)); }
        }
        foreach (var group in groups) v.Pending.Enqueue((v.Root, group));
        foreach (var p in children) v.Pending.Enqueue(p);
        v.Pending.Enqueue((v.Root, v.Mist));
        return v;
    }

    /// <summary>Place the root in the scene for the current floating origin (main thread: on attach and after shifts).</summary>
    public void Place()
    {
        Root.Position = Origin.ToScene(Field.Frame.OriginX, 0f, Field.Frame.OriginZ);
        if (Mist?.Material is ShaderMaterial m) m.SetShaderParameter(MistOrigin, new Vector3((float)Origin.X, 0f, (float)Origin.Z));
    }

    static readonly StringName MistOrigin = new("world_origin");

    /// <summary>Cut-line clipping on this segment's materials (only once the neighbour on that side is drawn).</summary>
    public void SetClips(RiverWorld world, bool upstreamNeighbour, bool downstreamNeighbour)
    {
        foreach (var m in new[] { WaterMat, TerrainMat })
        {
            m.SetShaderParameter("clip_a", CutPlane(world, K));
            m.SetShaderParameter("clip_b", CutPlane(world, K + 1));
            m.SetShaderParameter("clip_a_on", upstreamNeighbour ? 1f : 0f);
            m.SetShaderParameter("clip_b_on", downstreamNeighbour ? 1f : 0f);
        }
    }

    static Vector4 CutPlane(RiverWorld world, int j)
    {
        var c = world.Course;
        var (x, z) = c.ValleyPoint(c.Cuts[j]);
        float h = c.ValleyHeadingAt(c.Cuts[j]);
        return new Vector4((float)x, (float)z, MathF.Sin(h), MathF.Cos(h));
    }

    /// <summary>
    /// Coarse land 1.5 km either side of the grid, from the course's valley (floodplain, walls, hills around its axis,
    /// on its water level), blended into the grid's edge. Clipped at the cuts like the terrain (same material).
    /// </summary>
    static MeshInstance3D FarLand(int k, ReachField f, RiverWorld world, ShaderMaterial mat)
    {
        // Beyond the grid's ends too: neighbouring grids are rotated against each other, so on the outside of a bend
        // their chord-aligned ends leave a wedge between them. The cut-line clipping trims the extension exactly.
        const float step = 32f, margin = 1500f, beyond = 800f;
        var c = world.Course;
        float x0 = -margin, x1 = f.SizeX + margin, z0 = -beyond, z1 = f.SizeZ + beyond;
        int vx = (int)((x1 - x0) / step) + 1, vz = (int)((z1 - z0) / step) + 1;
        var verts = new Vector3[vx * vz];
        int hint = (int)((c.Cuts[k] - CourseSegments.Overlap - beyond) / Course.ValleyStep);
        hint = Math.Max(0, hint);
        for (int z = 0; z < vz; z++)
            for (int x = 0; x < vx; x++)
            {
                float lx = x0 + x * step, lz = z0 + z * step;
                var (wx, wz) = f.Frame.ToWorld(lx, lz);
                var (sigma, offset, idx) = c.ProjectOnValley(wx, wz, hint);
                hint = idx;
                float level = c.Eta[c.RiverIndexAt(sigma)];
                float h = level + 1.2f + Valley.FloodplainRelief((float)wx, (float)wz, f.Seed)
                          + Valley.ReliefAround(MathF.Abs(offset), (float)sigma, (float)wx, (float)wz, f.Seed);
                // Meet the grid's edge: blend towards its edge height over 200 m (slightly below, under the grid).
                float cx = Math.Clamp(lx, 0, f.SizeX), cz = Math.Clamp(lz, 0, f.SizeZ);
                float dEdge = MathF.Sqrt((lx - cx) * (lx - cx) + (lz - cz) * (lz - cz));
                if (dEdge < 200f) h = MathUtil.Lerp(f.SampleHeight(cx, cz) - 0.3f, h, MathUtil.Smoothstep(0f, 200f, dEdge));
                verts[z * vx + x] = new Vector3(lx, h, lz);
            }
        var mesh = TerrainBuilder.LatticeMesh(verts, vx, vz, (x, z) =>
        {
            float lx = x0 + x * step, lz = z0 + z * step;
            return lx > step && lx + step < f.SizeX - step && lz > step && lz + step < f.SizeZ - step;   // the detailed terrain covers it
        });
        return new MeshInstance3D { Name = "FarLand", Mesh = mesh, MaterialOverride = mat };
    }
}
