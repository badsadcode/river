using System;
using System.Collections.Generic;
using Godot;
using River.Core;

namespace River.Raft;

/// <summary>
/// The log raft: a rigid body floating on the reach field.
///
/// Physics (all forces are applied per sample point under the hull, so tilting, partial grounding and current
/// shear come out naturally):
///   buoyancy   ρ·g·(displaced volume) from how deep each point sits below the local water surface
///   drag       quadratic + linear drag against the local current (the raft drifts with the river)
///   heave/roll damping from the water
///   the sweep oar (stern), the pole (shallow water) and the anchor are extra forces.
///
/// "Realistic but not annoying" (Tom): heavy and slow to turn like a real raft, but with enough yaw damping that
/// it doesn't spin forever in eddies, a strong-enough sweep oar, a soft push off sandbars, and low friction on
/// the bed so you can always pole free.
/// </summary>
public partial class Raft : RigidBody3D
{
    // Dimensions (local: -Z is the bow, +Z the stern, X across, Y up). Origin at the log centre line.
    public const float Length = 7.0f, Width = 4.2f, LogDiameter = 0.36f;
    public const float DeckTop = LogDiameter * 0.5f + 0.05f;
    public const float RaftMass = 3800f;

    const float WaterDensity = 1000f, G = 9.81f;
    const int PointsAlong = 7, PointsAcross = 4;

    public ReachField Field;
    public AnimatableBody3D Deck;             // what the player walks on (moves with the raft)
    public Node3D OarPivot;                   // the sweep oar, rotated by steering input
    public Vector3 OarHandleLocal;            // where the player stands to use the oar

    // Control inputs (set by the player each physics frame)
    public float SteerInput, RowInput;
    public float? ForcedSteer, ForcedRow;     // tests override the player's inputs
    public Vector3 PoleDirection;             // world, horizontal, |.| ≤ 1; zero = not poling
    public bool AnchorDown { get; private set; }
    public Vector3 AnchorPoint { get; private set; }
    public float AnchorRope { get; private set; }

    // Extra load (the player standing somewhere on the deck), world position and mass.
    public Vector3 LoadPoint; public float LoadMass;

    // Telemetry
    public float GroundedTime { get; private set; }
    public bool Grounded { get; private set; }
    public float SubmergedShare { get; private set; }
    public float PoleReachDepth { get; private set; }

    readonly List<Vector3> _points = new();
    float _oarAngle;
    float _time;

    public void Setup(ReachField field, Vector3 position, float yaw)
    {
        Field = field;
        Mass = RaftMass;
        CenterOfMassMode = CenterOfMassModeEnum.Custom;
        CenterOfMass = new Vector3(0, -0.04f, 0.2f);
        GravityScale = 1f;
        LinearDamp = 0f; AngularDamp = 0f;
        LinearDampMode = DampMode.Replace; AngularDampMode = DampMode.Replace;
        CollisionLayer = 2; CollisionMask = 1;
        ContactMonitor = true; MaxContactsReported = 8;
        ContinuousCd = true;
        CanSleep = false;
        PhysicsMaterialOverride = new PhysicsMaterial { Friction = 0.25f, Bounce = 0.02f };

        var hull = new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(Length, LogDiameter, Width) } };
        AddChild(hull);

        for (int a = 0; a < PointsAlong; a++)
            for (int c = 0; c < PointsAcross; c++)
                _points.Add(new Vector3(
                    (c + 0.5f) / PointsAcross * Width - Width * 0.5f,
                    -LogDiameter * 0.5f,
                    (a + 0.5f) / PointsAlong * Length - Length * 0.5f));

        RaftModel.Build(this);
        GlobalPosition = position;
        Rotation = new Vector3(0, yaw, 0);
    }

    // ------------------------------------------------------------------ water sampling

    public float WaterHeight(float x, float z)
    {
        float eta = Field.InDomain(x, z) ? Field.SampleEta(x, z) : Field.EtaTrend(z);
        return eta + SmallWaves(x, z, _time);
    }

    public Vector3 WaterVelocity(float x, float z)
    {
        if (!Field.InDomain(x, z)) return new Vector3(0, 0, 0.6f);
        var (vx, vz) = Field.SampleVelocity(x, z);
        return new Vector3(vx, 0, vz);
    }

    /// <summary>Bed height (for poling, anchoring, grounding).</summary>
    public float BedHeight(float x, float z) => Field.InDomain(x, z) ? Field.SampleHeight(x, z) : Field.EtaTrend(z) - 3f;

    /// <summary>Gentle waves for bobbing: a few long ripples everywhere, choppier where the water is fast and shallow.</summary>
    float SmallWaves(float x, float z, float t)
    {
        float chop = 0f;
        if (Field.InDomain(x, z))
        {
            var (vx, vz) = Field.SampleVelocity(x, z);
            float d = MathF.Max(Field.SampleDepth(x, z), 0.1f);
            float fr = MathF.Sqrt(vx * vx + vz * vz) / MathF.Sqrt(G * d);
            chop = MathUtil.Smoothstep(0.35f, 0.9f, fr);
        }
        float w = 0.018f * MathF.Sin(x * 0.31f + t * 1.7f) + 0.014f * MathF.Sin(z * 0.27f - t * 1.3f + 1.3f)
                + 0.010f * MathF.Sin((x + z) * 0.53f + t * 2.3f);
        w += chop * (0.06f * MathF.Sin(x * 1.1f + t * 3.1f) + 0.05f * MathF.Sin(z * 1.4f - t * 3.7f));
        return w;
    }

    // ------------------------------------------------------------------ controls

    public void ToggleAnchor()
    {
        if (AnchorDown) { AnchorDown = false; return; }
        var bow = ToGlobal(new Vector3(0, 0, -Length * 0.5f));
        float bed = BedHeight(bow.X, bow.Z);
        float depth = WaterHeight(bow.X, bow.Z) - bed;
        if (depth > 12f) return; // rope too short
        AnchorPoint = new Vector3(bow.X, bed, bow.Z);
        AnchorRope = MathF.Max(depth, 0.5f) + 5f;
        AnchorDown = true;
    }

    // ------------------------------------------------------------------ simulation

    public override void _PhysicsProcess(double delta)
    {
        float dt = (float)delta;
        _time += dt;
        var xf = GlobalTransform;
        var com = xf * CenterOfMass;
        var v = LinearVelocity;
        var w = AngularVelocity;

        float area = Length * Width / _points.Count;
        float maxSub = LogDiameter + 0.25f;      // logs plus the deck before it's fully awash
        float submergedSum = 0f;
        float shallowest = float.MaxValue;
        foreach (var lp in _points)
        {
            var p = xf * lp;
            float eta = WaterHeight(p.X, p.Z);
            float sub = Math.Clamp(eta - p.Y, 0f, maxSub);
            if (sub <= 0f) continue;
            float share = sub / LogDiameter;
            submergedSum += MathF.Min(share, 1f);
            // Logs are round: the displaced cross-section grows slower near the bottom and top.
            float fill = sub <= LogDiameter ? CircleFill(sub / LogDiameter) * LogDiameter : LogDiameter * 0.785f + (sub - LogDiameter) * 0.6f;
            var r = p - com;
            var pv = v + w.Cross(r);

            var force = new Vector3(0, WaterDensity * G * area * fill, 0);
            // Heave/roll damping.
            force.Y -= 900f * area * MathF.Min(share, 1f) * pv.Y;
            // Drag against the current (horizontal).
            var rel = WaterVelocity(p.X, p.Z) - new Vector3(pv.X, 0, pv.Z);
            float relLen = rel.Length();
            float k = MathF.Min(share, 1f);
            force += rel * area * k * (55f + 140f * relLen);

            // Soft push off the bed: when a point nearly touches bottom, nudge towards deeper water.
            float bed = BedHeight(p.X, p.Z);
            float clearance = p.Y - bed;
            shallowest = MathF.Min(shallowest, clearance);
            if (clearance < 0.25f)
            {
                float gx = BedHeight(p.X + 1.5f, p.Z) - BedHeight(p.X - 1.5f, p.Z);
                float gz = BedHeight(p.X, p.Z + 1.5f) - BedHeight(p.X, p.Z - 1.5f);
                var down = new Vector3(-gx, 0, -gz);
                if (down.LengthSquared() > 1e-6f) force += down.Normalized() * 450f * (0.25f - clearance) / 0.25f;
            }
            ApplyForce(force, p - GlobalPosition);
        }
        SubmergedShare = submergedSum / _points.Count;

        // Yaw damping from the water around the whole hull (not annoying: no endless spinning).
        float yawRate = w.Dot(xf.Basis.Y);
        ApplyTorque(-xf.Basis.Y * yawRate * 9000f * Math.Clamp(SubmergedShare * 2f, 0f, 1f));
        // A little extra roll/pitch damping so it settles calmly.
        var tilt = w - xf.Basis.Y * yawRate;
        ApplyTorque(-tilt * 4000f);

        // Sweep oar at the stern: a sideways push (turns the raft) and a little forward/back sculling.
        float steer = ForcedSteer ?? SteerInput, row = ForcedRow ?? RowInput;
        _oarAngle = Mathf.Lerp(_oarAngle, steer * 0.55f, 1f - MathF.Exp(-dt * 3f));
        if (OarPivot != null) OarPivot.Rotation = new Vector3(0, _oarAngle, 0);
        var stern = xf * new Vector3(0, -0.1f, Length * 0.5f + 2.5f);
        var side = xf.Basis.X;
        var fwd = -xf.Basis.Z;
        if (MathF.Abs(steer) > 0.01f) ApplyForce(side * steer * 700f, stern - GlobalPosition);
        if (MathF.Abs(row) > 0.01f) ApplyForce(fwd * row * 520f, stern - GlobalPosition);

        // Pole: push off the bottom where the water is shallow enough.
        var poleAt = LoadMass > 0 ? LoadPoint : GlobalPosition;
        PoleReachDepth = WaterHeight(poleAt.X, poleAt.Z) - BedHeight(poleAt.X, poleAt.Z);
        if (PoleDirection.LengthSquared() > 0.01f && PoleReachDepth < 3.6f)
            ApplyForce(PoleDirection * 1600f, poleAt - GlobalPosition);

        // Anchor rope: slack until taut, then a stiff, damped spring.
        if (AnchorDown)
        {
            var bow = xf * new Vector3(0, 0, -Length * 0.5f);
            var d = bow - AnchorPoint;
            float len = d.Length();
            if (len > AnchorRope)
            {
                var dir = d / len;
                var bowVel = v + w.Cross(bow - com);
                float stretch = len - AnchorRope;
                float pull = 6000f * stretch + 2500f * MathF.Max(0f, bowVel.Dot(dir));
                ApplyForce(-dir * MathF.Min(pull, 30000f), bow - GlobalPosition);
            }
        }

        // The player's weight.
        if (LoadMass > 0f) ApplyForce(Vector3.Down * LoadMass * G, LoadPoint - GlobalPosition);

        // Grounding telemetry.
        Grounded = GetContactCount() > 0 && shallowest < 0.1f;
        if (Grounded) GroundedTime += dt;
    }

    /// <summary>Area share of a unit circle below a chord at height h ∈ [0, 1] (as a share of the diameter).</summary>
    static float CircleFill(float h)
    {
        h = Math.Clamp(h, 0f, 1f);
        float theta = 2f * MathF.Acos(1f - 2f * h);
        return (theta - MathF.Sin(theta)) / (2f * MathF.PI) * 0.785f; // segment share × circle/square ratio
    }
}
