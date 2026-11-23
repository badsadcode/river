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

    public RiverWorld(Course course) { Course = course; }

    public void Add(int k, ReachField f) => _segments[k] = f;
    public bool Remove(int k) => _segments.Remove(k);
    public bool TryGet(int k, out ReachField f) => _segments.TryGetValue(k, out f);
    public IEnumerable<int> Loaded => _segments.Keys;

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
        // Blend with the upstream neighbour near cut k, or the downstream one near cut k+1.
        foreach (int j in new[] { k, k + 1 })
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
