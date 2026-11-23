using System;

namespace River.Core;

/// <summary>
/// Where a segment's grid sits in the world (docs/STREAMING.md §4): an origin in double precision (an endless
/// journey gets far from (0, 0)) and a heading, the rotation of the grid's +z axis away from world +z towards
/// world +x. Grid-local (x, z) → world = origin + R(heading)·(x, z). The prototype reach uses <see cref="Identity"/>.
/// </summary>
public readonly struct Frame
{
    public readonly double OriginX, OriginZ;
    public readonly float Heading;

    public Frame(double originX, double originZ, float heading)
    {
        OriginX = originX; OriginZ = originZ; Heading = heading;
    }

    public static Frame Identity => new(0.0, 0.0, 0f);

    public (double x, double z) ToWorld(float lx, float lz)
    {
        double c = Math.Cos(Heading), s = Math.Sin(Heading);
        return (OriginX + lx * c + lz * s, OriginZ - lx * s + lz * c);
    }

    public (float x, float z) ToLocal(double wx, double wz)
    {
        double c = Math.Cos(Heading), s = Math.Sin(Heading), dx = wx - OriginX, dz = wz - OriginZ;
        return ((float)(dx * c - dz * s), (float)(dx * s + dz * c));
    }
}
