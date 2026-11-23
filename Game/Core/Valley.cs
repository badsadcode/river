using System;

namespace River.Core;

/// <summary>
/// The land around the river, as functions of position: the valley's axis and walls, gentle floodplain relief.
/// Shared by the segment terrain and the far horizon land so they meet without a seam. The prototype's valley runs
/// straight along +z (<see cref="Relief"/>); the endless course's valley follows its own axis (<see cref="ReliefAround"/>).
/// </summary>
public static class Valley
{
    /// <summary>The prototype valley's axis (x) at z: the meander belt wanders around it, valley walls rise either side.</summary>
    public static float AxisX(float z, int seed, float sizeX) =>
        sizeX * 0.5f + 110f * Noise.Fbm1(z / 1600f, seed + 71, 2);

    /// <summary>
    /// Prototype valley walls and hills: height added on top of the floodplain at (x, z), zero near the river.
    /// Shared with the far horizon terrain so the two meet without a seam.
    /// </summary>
    public static float Relief(float x, float z, int seed, float sizeX) =>
        ReliefAround(MathF.Abs(x - AxisX(z, seed, sizeX)), z, x, z, seed);

    /// <summary>
    /// Valley walls and hills at world (x, z), given its distance q from the valley axis and its position along the
    /// valley (m). Zero near the river, walls from ~360 m out, open hills beyond.
    /// </summary>
    public static float ReliefAround(float q, float along, float x, float z, int seed)
    {
        float halfWidth = 360f + 60f * Noise.Fbm1(along / 900f, seed + 72, 2);
        float wall = MathUtil.Smoothstep(halfWidth, halfWidth + 230f, q);
        float far = MathUtil.Smoothstep(halfWidth + 200f, halfWidth + 1400f, q);
        float ridges = Noise.Ridged(x / 420f, z / 420f, seed + 73, 4);
        float hills = Noise.Fbm(x / 900f, z / 900f, seed + 74, 3);
        return MathF.Pow(wall, 1.6f) * (48f + 26f * hills) + wall * 30f * ridges + far * (60f + 60f * hills);
    }

    /// <summary>Gentle floodplain relief (m), independent of the river (also used by the far terrain).</summary>
    public static float FloodplainRelief(float x, float z, int seed) =>
        0.9f + 0.9f * Noise.Fbm(x / 190f, z / 190f, seed + 81, 3) + 0.35f * Noise.Fbm(x / 28f, z / 28f, seed + 82, 2);
}
