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
    public Inflow[] Inflows = Array.Empty<Inflow>();
    public float OutletEta;                         // water level held along the downstream edge (m)
    public float Manning = 0.035f, RefSlope = 0.0005f;
    public int ReferencePicardSteps;                // tests: > 0 = single-level solve with this many nonlinear steps
}
