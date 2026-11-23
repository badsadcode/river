using System;

namespace River.Core;

/// <summary>
/// Bakes the seam blend into a segment before it is drawn (docs/STREAMING.md §4.1): within ±RiverWorld.Blend of
/// its two cuts, the segment's field is replaced by the blended value of the world (itself and the neighbour), so its
/// meshes and textures already carry the seamless values. Neighbours do the same from their side, so both meshes
/// show (nearly) the same surface at the cut line, where the shaders split them pixel-exactly. Where a neighbour
/// isn't loaded the blend is simply the segment's own value. The raw field is not changed.
/// </summary>
public static class SegmentBake
{
    public static ReachField Bake(ReachField raw, int k, RiverWorld world)
    {
        var f = raw.Clone();
        double co = Math.Cos(f.Frame.Heading), si = Math.Sin(f.Frame.Heading);
        int hint = k;
        for (int iz = 0; iz < f.Nz; iz++)
            for (int ix = 0; ix < f.Nx; ix++)
            {
                int i = iz * f.Nx + ix;
                var (wx, wz) = f.Frame.ToWorld(ix * f.Cell, iz * f.Cell);
                bool inBand = false;
                foreach (int j in new[] { k, k + 1 })
                    if (j > 0 && j < world.Course.Cuts.Count && Math.Abs(world.PastCut(j, wx, wz)) < RiverWorld.Blend) inBand = true;
                if (!inBand || !world.Sample(wx, wz, ref hint, out var s)) continue;
                f.Eta[i] = s.Eta; f.Depth[i] = s.Depth; f.Height[i] = s.Height; f.Foam[i] = s.Foam;
                if (f.Eddy != null) f.Eddy[i] = s.Eddy;
                f.Water[i] = s.Water;
                f.VelX[i] = (float)(s.VelX * co - s.VelZ * si);   // world axes → grid axes
                f.VelZ[i] = (float)(s.VelX * si + s.VelZ * co);
            }
        return f;
    }
}
