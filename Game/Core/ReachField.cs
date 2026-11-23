using System;
using System.Collections.Generic;

namespace River.Core;

/// <summary>A boulder standing in (or near) the water. Top is relative to the local water surface.</summary>
public struct Rock
{
    public float X, Z, Radius, Top;
    public int Seed;
}

/// <summary>
/// The generated river reach: every grid node's terrain height, water surface, depth, current and foam.
/// This is the single source of truth for rendering (terrain, water shader), physics (raft) and gameplay.
/// Grid: Nx × Nz nodes, <see cref="Cell"/> metres apart, node (0, 0) at world (0, 0). X east, Z downstream.
/// </summary>
public sealed class ReachField
{
    public int Nx, Nz;
    public float Cell;
    public float SizeX => (Nx - 1) * Cell;
    public float SizeZ => (Nz - 1) * Cell;
    public int Seed;

    public bool[] Water;        // node is under water (river, lake, connected)
    public bool[] Still;        // water not connected to the outlet (ponds): no current
    public float[] Height;      // terrain height (bed under water)
    public float[] Eta;         // water surface height (on land: of the nearest water, so meshes can overhang)
    public float[] Depth;       // water depth (0 on land)
    public float[] VelX, VelZ;  // depth-averaged current (m/s)
    public float[] Foam;        // 0..1 foam coverage (advected downstream from its sources)
    public float[] Eddy;        // 0..1 lee/recirculation zone strength (backwaters behind islands, points, rocks)
    public float[] ShoreDist;   // signed distance to the shoreline (m): negative in water, positive on land

    public Centerline Main, Tributary;
    public readonly List<Island> Islands = new();
    public readonly List<Rock> Rocks = new();

    /// <summary>Linear fit of the water surface along the valley: EtaTrendA + EtaTrendB·z.</summary>
    public float EtaTrendA, EtaTrendB;
    public float EtaTrend(float z) => EtaTrendA + EtaTrendB * z;

    /// <summary>Total discharge leaving the reach (m³/s), measured after solving (a conservation check).</summary>
    public float OutflowMeasured;
    public float InflowTotal;

    // ---------------------------------------------------------------- sampling (bilinear, clamped)

    public float Sample(float[] a, float x, float z)
    {
        float fx = Math.Clamp(x / Cell, 0f, Nx - 1.001f);
        float fz = Math.Clamp(z / Cell, 0f, Nz - 1.001f);
        int ix = (int)fx, iz = (int)fz;
        float tx = fx - ix, tz = fz - iz;
        int i = iz * Nx + ix;
        float a00 = a[i], a10 = a[i + 1], a01 = a[i + Nx], a11 = a[i + Nx + 1];
        return (a00 + (a10 - a00) * tx) + ((a01 + (a11 - a01) * tx) - (a00 + (a10 - a00) * tx)) * tz;
    }

    public float SampleEta(float x, float z) => Sample(Eta, x, z);
    public float SampleHeight(float x, float z) => Sample(Height, x, z);
    public float SampleDepth(float x, float z) => Sample(Depth, x, z);
    public (float x, float z) SampleVelocity(float x, float z) => (Sample(VelX, x, z), Sample(VelZ, x, z));

    public bool InDomain(float x, float z) => x >= 0 && z >= 0 && x <= SizeX && z <= SizeZ;

    /// <summary>Distance along the main river of the centre-line sample nearest to (x, z) (planning/debug).</summary>
    public float MainDistanceAt(float x, float z) => Main.Nearest(x, z) * Centerline.Step;
}
