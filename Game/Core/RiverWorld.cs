using System;
using System.Collections.Generic;

namespace River.Core;

/// <summary>What the river is like at one world point (blended across segment seams).</summary>
public struct RiverSample
{
    public float Eta, Depth, Height;   // water surface, water depth (0 on land), terrain/bed height (m)
    public float VelX, VelZ;           // current in world axes (m/s)
    public float Foam, Eddy;
    public bool Water => Depth > 0.02f;
}

/// <summary>
/// The endless river as one world (docs/STREAMING.md §4–5): the loaded segments of a <see cref="Course"/>, queried by
/// world position. Each point belongs to the segment between the two cut lines around it (a cut line crosses the
/// valley at the cut, perpendicular to it); within ±<see cref="Blend"/> m of a cut the two neighbours are blended,
/// so values are continuous across seams. Headless; the game's raft, player and streamer use it from phase 3.
/// </summary>
public sealed class RiverWorld
{
    public const float Blend = 120f;   // m either side of a cut (inside the 150 m overlap)

    public readonly Course Course;
    readonly Dictionary<int, ReachField> _segments = new();
    readonly List<Rock> _rocks = new();
    int _hint = 1;

    public RiverWorld(Course course) { Course = course; }

    public void Add(int k, ReachField f) { _segments[k] = f; RebuildRocks(); }
    public bool Remove(int k) { bool r = _segments.Remove(k); RebuildRocks(); return r; }
    public bool TryGet(int k, out ReachField f) => _segments.TryGetValue(k, out f);
    public IEnumerable<int> Loaded => _segments.Keys;
    /// <summary>The loaded segment indices into a list (no allocation, for per-frame use).</summary>
    public void LoadedInto(List<int> into) { foreach (int k in _segments.Keys) into.Add(k); }

    /// <summary>Rocks of all loaded segments in world coordinates (position and approaching current).</summary>
    public IReadOnlyList<Rock> Rocks => _rocks;

    /// <summary>Waterfalls of all loaded segments, in world coordinates (portage, wrecks).</summary>
    public IReadOnlyList<WorldFalls> Falls => _falls;
    readonly List<WorldFalls> _falls = new();

    void RebuildRocks()
    {
        _rocks.Clear();
        foreach (var f in _segments.Values)
        {
            double co = Math.Cos(f.Frame.Heading), si = Math.Sin(f.Frame.Heading);
            foreach (var r in f.Rocks)
            {
                var (wx, wz) = f.Frame.ToWorld(r.X, r.Z);
                var w = r;
                w.X = (float)wx; w.Z = (float)wz;
                w.FlowX = (float)(r.FlowX * co + r.FlowZ * si); w.FlowZ = (float)(-r.FlowX * si + r.FlowZ * co);
                _rocks.Add(w);
            }
        }
        _falls.Clear();
        foreach (var (k, f) in _segments)
        {
            if (f.Falls is not { } fa) continue;
            double co = Math.Cos(f.Frame.Heading), si = Math.Sin(f.Frame.Heading);
            var wf = new WorldFalls
            {
                Segment = k, Height = fa.Height, HalfWidth = fa.HalfWidth, TopEta = fa.TopEta, BottomEta = fa.BottomEta, Side = fa.Side,
                DirX = (float)(fa.DirX * co + fa.DirZ * si), DirZ = (float)(-fa.DirX * si + fa.DirZ * co),
                TrailX = new double[fa.TrailX.Length], TrailZ = new double[fa.TrailX.Length], TrailH = (float[])fa.TrailH.Clone(),
            };
            (wf.LipX, wf.LipZ) = f.Frame.ToWorld(fa.LipX, fa.LipZ);
            (wf.LandingX, wf.LandingZ) = f.Frame.ToWorld(fa.LandingX, fa.LandingZ);
            (wf.PutInX, wf.PutInZ) = f.Frame.ToWorld(fa.PutInX, fa.PutInZ);
            for (int j = 0; j < fa.TrailX.Length; j++) (wf.TrailX[j], wf.TrailZ[j]) = f.Frame.ToWorld(fa.TrailX[j], fa.TrailZ[j]);
            _falls.Add(wf);
        }
    }

    // ---------------------------------------------------------------- point queries (main thread: raft, player, HUD)

    /// <summary>
    /// The river at a world point. Where no loaded segment covers it (shouldn't happen around the raft), a dry
    /// fallback at the course's level, so nothing falls through the world.
    /// </summary>
    public RiverSample At(double x, double z)
    {
        if (Sample(x, z, ref _hint, out var s)) return s;
        var (sigma, _, _) = Course.ProjectOnValley(x, z, (int)(Course.Cuts[Math.Clamp(_hint, 0, Course.Cuts.Count - 1)] / Course.ValleyStep));
        float eta = Course.Eta[Course.RiverIndexAt(sigma)];
        return new RiverSample { Eta = eta, Height = eta + 1f, Depth = 0f };
    }

    public bool Covers(double x, double z) => Sample(x, z, ref _hint, out _);
    public float EtaAt(double x, double z) => At(x, z).Eta;
    public float HeightAt(double x, double z) => At(x, z).Height;
    public float DepthAt(double x, double z) => At(x, z).Depth;
    public (float x, float z) VelocityAt(double x, double z) { var s = At(x, z); return (s.VelX, s.VelZ); }

    /// <summary>Segment index at a world point (around the last query).</summary>
    public int SegmentAt(double x, double z) => _hint = SegmentAt(x, z, _hint);

    /// <summary>Signed distance of (x, z) downstream of cut j, measured along the valley there.</summary>
    public double PastCut(int j, double x, double z)
    {
        double s = Course.Cuts[j];
        var (vx, vz) = Course.ValleyPoint(s);
        float h = Course.ValleyHeadingAt(s);
        return (x - vx) * MathF.Sin(h) + (z - vz) * MathF.Cos(h);
    }

    /// <summary>The segment whose cuts bracket (x, z), searched around <paramref name="hint"/> (a segment index).</summary>
    public int SegmentAt(double x, double z, int hint)
    {
        int k = Math.Clamp(hint, 0, Course.Cuts.Count - 2);
        for (int guard = 0; guard < 64; guard++)
        {
            if (PastCut(k, x, z) < 0 && k > 0) { k--; continue; }
            if (k + 1 < Course.Cuts.Count - 1 && PastCut(k + 1, x, z) >= 0) { k++; continue; }
            break;
        }
        return k;
    }

    /// <summary>Raw value of one segment at a world point (false if the point is off its grid or it isn't loaded).</summary>
    public bool SampleSegment(int k, double x, double z, out RiverSample s)
    {
        s = default;
        if (!_segments.TryGetValue(k, out var f)) return false;
        var (lx, lz) = f.Frame.ToLocal(x, z);
        if (lx < 0 || lz < 0 || lx > f.SizeX || lz > f.SizeZ) return false;
        s.Eta = f.SampleEta(lx, lz);
        s.Depth = f.SampleDepth(lx, lz);
        s.Height = f.SampleHeight(lx, lz);
        var (vx, vz) = f.SampleVelocity(lx, lz);
        double co = Math.Cos(f.Frame.Heading), si = Math.Sin(f.Frame.Heading);   // grid axes → world axes
        s.VelX = (float)(vx * co + vz * si);
        s.VelZ = (float)(-vx * si + vz * co);
        s.Foam = f.Sample(f.Foam, lx, lz);
        s.Eddy = f.Eddy != null ? f.Sample(f.Eddy, lx, lz) : 0f;
        return true;
    }

    /// <summary>The river at a world point, blended across seams. False where no loaded segment covers it.</summary>
    public bool Sample(double x, double z, ref int hint, out RiverSample s)
    {
        int k = SegmentAt(x, z, hint);
        hint = k;
        bool has = SampleSegment(k, x, z, out s);
        // Blend with the upstream neighbour near cut k, or the downstream one near cut k+1. (No allocations here: the
        // raft calls this thousands of times a second, and garbage means collector pauses.)
        for (int j = k; j <= k + 1; j++)
        {
            if (j <= 0 || j >= Course.Cuts.Count - 1) continue;
            double d = PastCut(j, x, z);
            if (Math.Abs(d) >= Blend) continue;
            int other = j == k ? k - 1 : k + 1;
            if (!SampleSegment(other, x, z, out var o)) continue;
            if (!has) { s = o; has = true; continue; }
            float wDown = MathUtil.Smoothstep(-Blend, Blend, (float)d);           // weight of the segment below cut j
            float wOther = j == k ? 1f - wDown : wDown;
            s = Lerp(s, o, wOther);
        }
        return has;
    }

    static RiverSample Lerp(in RiverSample a, in RiverSample b, float t) => new()
    {
        Eta = a.Eta + (b.Eta - a.Eta) * t, Depth = a.Depth + (b.Depth - a.Depth) * t, Height = a.Height + (b.Height - a.Height) * t,
        VelX = a.VelX + (b.VelX - a.VelX) * t, VelZ = a.VelZ + (b.VelZ - a.VelZ) * t,
        Foam = a.Foam + (b.Foam - a.Foam) * t, Eddy = a.Eddy + (b.Eddy - a.Eddy) * t,
    };
}

/// <summary>A loaded waterfall in world coordinates (see <see cref="FallsSpec"/>).</summary>
public sealed class WorldFalls
{
    public int Segment, Side;
    public double LipX, LipZ, LandingX, LandingZ, PutInX, PutInZ;
    public float DirX, DirZ, HalfWidth, Height, TopEta, BottomEta;
    public double[] TrailX, TrailZ;
    public float[] TrailH;

    /// <summary>Signed distance past the lip along the flow (+ downstream) and across it, of a world point.</summary>
    public (double along, double across) Relative(double x, double z)
    {
        double dx = x - LipX, dz = z - LipZ;
        return (dx * DirX + dz * DirZ, -dx * DirZ + dz * DirX);
    }
}
