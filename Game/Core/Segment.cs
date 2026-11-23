using System;
using System.Collections.Generic;

namespace River.Core;

/// <summary>Which edge of a segment's grid water flows in through.</summary>
public enum GridEdge { Upstream, Downstream, Left, Right }   // z = 0, z = max, x = 0, x = max

/// <summary>Water entering a segment: through an edge, inside one channel (0 = main, 1 = tributary).</summary>
public readonly record struct Inflow(GridEdge Edge, int Line, float Discharge);

/// <summary>
/// Everything one segment's generation needs, in the segment's own frame: the slice of the course that falls on
/// its grid, how water enters and leaves it, and the solver settings. Generation is a pure function of this.
/// </summary>
public sealed class SegmentSpec
{
    public int Seed;
    public Frame Frame = Frame.Identity;
    public float SizeX, SizeZ, Cell = 2f;
    public Centerline Main, Tributary;             // grid-local coordinates
    public List<Island> Islands = new();
    public List<Rock> Rocks = new();
    /// <summary>Secondary channels leaving the main river and rejoining it downstream (water finds its own share).</summary>
    public List<Centerline> SideChannels = new();
    /// <summary>Still water on the floodplain (oxbow lakes): not connected to the river, at their own level.</summary>
    public List<StillWater> StillWaters = new();
    public List<Snag> Snags = new();
    public Inflow[] Inflows = Array.Empty<Inflow>();
    public float OutletEta;                         // water level held along the downstream edge (m)
    /// <summary>
    /// The course's water level where the river enters (NaN = none, the prototype). If set, the segment calibrates its
    /// friction so its own solve drops from this level to <see cref="OutletEta"/>: neighbours then meet at seams.
    /// </summary>
    public float InletEtaTarget = float.NaN;
    public float Manning = 0.035f, RefSlope = 0.0005f;
    public int ReferencePicardSteps;                // tests: > 0 = single-level solve with this many nonlinear steps
    /// <summary>
    /// For segments of the endless course: world position → (σ along the valley, signed offset from its axis), so the
    /// valley walls follow the course. Null for the prototype (straight valley along z). The int is a search hint.
    /// </summary>
    public ValleyLookup ValleyAt;
    /// <summary>
    /// For segments of the endless course: the course's water level at valley distance σ. Land far from the river
    /// (beyond ~250 m) is built on this rather than on the nearest water of this grid, which can be a different
    /// stretch of river in a neighbour's grid. Null for the prototype.
    /// </summary>
    public Func<double, float> LevelAt;
    /// <summary>For segments of the endless course: how much of a gorge the river runs in at σ (0..1). Null = none.</summary>
    public Func<double, float> GorgeAt;
}

/// <summary>An oxbow lake or pond: its outline as a channel (centre line with half-widths and depth) and its level.</summary>
public sealed class StillWater
{
    public Centerline Line;
    public float Level;
}

/// <summary>World position → (σ along the course's valley, signed offset from its axis); hint speeds up the search.</summary>
public delegate (double sigma, float offset) ValleyLookup((double x, double z) world, ref int hint);
