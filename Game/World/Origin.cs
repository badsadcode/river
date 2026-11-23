using System;
using Godot;

namespace River.World;

/// <summary>
/// The floating origin (docs/STREAMING.md §6, D20). Godot's scene uses 32-bit floats; far from (0, 0) positions lose
/// precision (millimetres beyond ~10 km, physics jitter around 20 km). So the scene is kept near the raft: scene
/// position = world position − (X, Z). World positions (the course, segment frames, RiverWorld queries) stay doubles.
///
/// When the raft gets more than <see cref="ShiftDistance"/> from the scene origin, <see cref="Shift"/> moves the
/// origin by a whole number of metres; every node that lives in scene space (raft, cameras, a player in the water,
/// segment roots) is moved back by the same amount by its owner through <see cref="Shifted"/>. Shaders add
/// <c>world_origin</c> (a global uniform, whole metres, exact in float) to their scene positions before computing
/// patterns, so ripples, foam, waves and caustics see the same world coordinates as before and don't jump.
/// Only X and Z shift; heights stay as they are.
/// </summary>
public static class Origin
{
    public const float ShiftDistance = 1024f;

    public static double X { get; private set; }
    public static double Z { get; private set; }

    /// <summary>Raised after the origin moved, with the scene-space delta every scene-space node must subtract.</summary>
    public static event Action<Vector3> Shifted;

    static readonly StringName Param = new("world_origin");

    public static (double x, double z) ToWorld(float sx, float sz) => (sx + X, sz + Z);
    public static (double x, double z) ToWorld(Vector3 scene) => (scene.X + X, scene.Z + Z);
    public static Vector3 ToScene(double x, float y, double z) => new((float)(x - X), y, (float)(z - Z));

    /// <summary>Place the origin (start-up, before anything is in the scene), rounded to whole metres.</summary>
    public static void Set(double x, double z)
    {
        X = Math.Round(x); Z = Math.Round(z);
        RenderingServer.GlobalShaderParameterSet(Param, new Vector3((float)X, 0f, (float)Z));
    }

    /// <summary>Move the origin to the scene point <paramref name="sceneCentre"/> (rounded to whole metres).</summary>
    public static Vector3 Shift(Vector3 sceneCentre)
    {
        var d = new Vector3(MathF.Round(sceneCentre.X), 0f, MathF.Round(sceneCentre.Z));
        if (d == Vector3.Zero) return d;
        X += d.X; Z += d.Z;
        RenderingServer.GlobalShaderParameterSet(Param, new Vector3((float)X, 0f, (float)Z));
        Shifted?.Invoke(d);
        return d;
    }
}
