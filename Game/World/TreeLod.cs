using System;
using System.Collections.Generic;
using System.Diagnostics;
using Godot;

namespace River.World;

/// <summary>One segment's trees in world space, bucketed in <see cref="TreeLod.Cell"/>-metre cells.</summary>
public sealed class TreeSet
{
    public Transform3D[] Xf;
    public ushort[] Id;                                   // TreeLibrary.All index
    public readonly Dictionary<long, List<int>> Cells = new();

    public static long Key(int cx, int cz) => ((long)cx << 32) ^ (uint)cz;

    public TreeSet(List<(Transform3D xf, ushort id)> trees)
    {
        Xf = new Transform3D[trees.Count]; Id = new ushort[trees.Count];
        for (int i = 0; i < trees.Count; i++)
        {
            Xf[i] = trees[i].xf; Id[i] = trees[i].id;
            long k = Key((int)MathF.Floor(Xf[i].Origin.X / TreeLod.Cell), (int)MathF.Floor(Xf[i].Origin.Z / TreeLod.Cell));
            if (!Cells.TryGetValue(k, out var l)) Cells[k] = l = new List<int>();
            l.Add(i);
        }
    }
}

/// <summary>
/// Real tree meshes near the camera (docs/DECISIONS.md D18). Every loaded segment's trees are known here; a few times a
/// second (when the camera has moved) each tree within its species' LOD 2 range is put into the MultiMesh of its
/// variant and LOD by distance (the pack manifest's ranges). Beyond that the segment's static billboards draw it
/// (they hide within the same range, per frame, in Foliage.gdshader). A MultiMesh per variant and LOD (5 × 4 × 3 = 60
/// draws), buffers reused: no garbage per update.
/// </summary>
public partial class TreeLod : Node3D
{
    public const float Cell = 32f;
    const float MoveToUpdate = 2f;
    const double MinInterval = 0.1;

    readonly Dictionary<int, TreeSet> _sets = new();
    MultiMesh[] _mm;
    float[][] _buf;
    int[] _count;
    Vector3 _last = new(float.MaxValue, 0, 0);
    double _since;
    bool _dirty = true;
    readonly StringName _camParam = new("tree_camera");

    public double LastUpdateMs, MaxUpdateMs;
    public int Drawn;

    public override void _Ready()
    {
        int n = TreeLibrary.All.Count * 3;
        _mm = new MultiMesh[n]; _buf = new float[n][]; _count = new int[n];
        for (int v = 0; v < TreeLibrary.All.Count; v++)
            for (int l = 0; l < 3; l++)
            {
                int j = v * 3 + l;
                _mm[j] = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = TreeLibrary.All[v].Lods[l], InstanceCount = 0 };
                _buf[j] = Array.Empty<float>();
                AddChild(new MultiMeshInstance3D
                {
                    Name = $"{TreeLibrary.All[v].Name}_LOD{l}", Multimesh = _mm[j],
                    // Instances move between updates: no per-node culling bounds to keep right.
                    CustomAabb = new Aabb(new Vector3(-1e6f, -1e4f, -1e6f), new Vector3(2e6f, 2e4f, 2e6f)),
                });
            }
    }

    public void Add(int k, TreeSet s) { _sets[k] = s; _dirty = true; }

    /// <summary>The tree of a species nearest to a point (screenshots, debugging); null if none is loaded.</summary>
    public Vector3? Nearest(int species, Vector3 near)
    {
        Vector3? best = null; float bd = float.MaxValue;
        foreach (var set in _sets.Values)
            for (int i = 0; i < set.Xf.Length; i++)
            {
                if (TreeLibrary.All[set.Id[i]].Species != species) continue;
                float d = set.Xf[i].Origin.DistanceSquaredTo(near);
                if (d < bd) { bd = d; best = set.Xf[i].Origin; }
            }
        return best;
    }
    public void Remove(int k) { if (_sets.Remove(k)) _dirty = true; }

    public override void _Process(double delta)
    {
        var cam = GetViewport().GetCamera3D();
        if (cam == null || _mm == null) return;
        var p = cam.GlobalPosition;
        RenderingServer.GlobalShaderParameterSet(_camParam, p);
        _since += delta;
        float dx = p.X - _last.X, dz = p.Z - _last.Z;
        if (!_dirty && dx * dx + dz * dz < MoveToUpdate * MoveToUpdate) return;
        if (_since < MinInterval && !_dirty) return;
        _since = 0; _dirty = false; _last = p;
        Rebuild(p);
    }

    void Rebuild(Vector3 cam)
    {
        var sw = Stopwatch.StartNew();
        Array.Clear(_count);
        var lib = TreeLibrary.All;
        float range = TreeLibrary.MaxLodEnd;
        int c0x = (int)MathF.Floor((cam.X - range) / Cell), c1x = (int)MathF.Floor((cam.X + range) / Cell);
        int c0z = (int)MathF.Floor((cam.Z - range) / Cell), c1z = (int)MathF.Floor((cam.Z + range) / Cell);
        foreach (var s in _sets.Values)
            for (int cz = c0z; cz <= c1z; cz++)
                for (int cx = c0x; cx <= c1x; cx++)
                {
                    if (!s.Cells.TryGetValue(TreeSet.Key(cx, cz), out var list)) continue;
                    foreach (int i in list)
                    {
                        var o = s.Xf[i].Origin;
                        float ddx = o.X - cam.X, ddz = o.Z - cam.Z, d = MathF.Sqrt(ddx * ddx + ddz * ddz);
                        var tv = lib[s.Id[i]];
                        var end = TreeLibrary.Species[tv.Species].LodEnd;
                        int lod = d < end[0] ? 0 : d < end[1] ? 1 : d < end[2] ? 2 : -1;
                        if (lod < 0) continue;
                        Put(tv.Id * 3 + lod, s.Xf[i]);
                    }
                }
        int drawn = 0;
        for (int j = 0; j < _mm.Length; j++)
        {
            int n = _count[j];
            drawn += n;
            // The buffer handed over must be exactly InstanceCount × 12 floats; it only grows (Put doubles it).
            int cap = _buf[j].Length / 12;
            if (cap != _mm[j].InstanceCount) _mm[j].InstanceCount = cap;
            _mm[j].VisibleInstanceCount = n;
            if (n > 0) RenderingServer.MultimeshSetBuffer(_mm[j].GetRid(), _buf[j]);
        }
        Drawn = drawn;
        LastUpdateMs = sw.Elapsed.TotalMilliseconds;
        MaxUpdateMs = Math.Max(MaxUpdateMs, LastUpdateMs);
    }

    void Put(int j, in Transform3D t)
    {
        int n = _count[j]++;
        var b = _buf[j];
        if ((n + 1) * 12 > b.Length)
        {
            var nb = new float[Math.Max(64, b.Length / 12 * 2) * 12];
            Array.Copy(b, nb, b.Length);
            _buf[j] = b = nb;
        }
        int o = n * 12;
        // MultiMesh buffer: the 3×4 matrix row by row.
        b[o + 0] = t.Basis.X.X; b[o + 1] = t.Basis.Y.X; b[o + 2] = t.Basis.Z.X; b[o + 3] = t.Origin.X;
        b[o + 4] = t.Basis.X.Y; b[o + 5] = t.Basis.Y.Y; b[o + 6] = t.Basis.Z.Y; b[o + 7] = t.Origin.Y;
        b[o + 8] = t.Basis.X.Z; b[o + 9] = t.Basis.Y.Z; b[o + 10] = t.Basis.Z.Z; b[o + 11] = t.Origin.Z;
    }
}
