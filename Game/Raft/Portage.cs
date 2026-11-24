using System;
using Godot;
using River.Core;
using River.World;

namespace River.Raft;

/// <summary>
/// Waterfalls on the raft's side (docs/DECISIONS.md D21, Tom's choices): the raft can't go over a waterfall.
///
/// <b>Portage</b>: land in the slack bay above the falls by the trail post, step ashore and press F at the post: the
/// raft is hauled out onto log rollers at the trail head. Walk into it from behind (W) to push it along the trail,
/// which runs back from the bank, down a ramp past the falls and back to the water; at the end it slides into the
/// plunge pool and is afloat again. Slow and physical: walking pace, only while you push.
///
/// <b>Wreck</b>: if the raft goes over the lip anyway, it breaks up in the plunge pool. For now (no survival yet) the
/// screen fades and a new raft is lashed together at the landing above the falls, anchored; you are put ashore by the
/// post if you were not aboard.
///
/// Everything here works in scene coordinates for nodes and world coordinates for the river (floating origin).
/// </summary>
public partial class Portage : Node3D
{
    public const float PushSpeed = 1.0f;       // m/s along the trail while pushed (a 240 m trail: ~4 minutes)
    const float RollerLift = 0.55f;            // raft hull centre above the trail on its rollers

    Raft _raft;
    Player.PlayerController _player;
    RiverWorld _world;

    WorldFalls _falls;                          // the waterfall being portaged (null: not portaging)
    double[] _cum;                              // cumulative trail length at each point
    float _s, _rollerTurn;
    Node3D _rollers;
    CanvasLayer _fadeLayer;
    ColorRect _fade;
    bool _fPressed, _busy;
    string _message; double _messageUntil;
    WorldFalls _above; double _aboveAt;         // the waterfall the raft was last seen above, and when
    float _pinned;                              // seconds the raft has been stuck right at a lip

    /// <summary>What the HUD shows the player right now (null: nothing).</summary>
    public string Prompt { get; private set; }
    public bool Active => _falls != null;
    public bool ForcePush;                      // tests: push without the keyboard
    public int Wrecks { get; private set; }
    public int Relaunches { get; private set; }

    /// <summary>Tests: start hauling at this waterfall now (as if F were pressed at its post).</summary>
    public void BeginForTest(WorldFalls fa) => Begin(fa);

    public void Setup(Raft raft, Player.PlayerController player, RiverWorld world)
    {
        _raft = raft; _player = player; _world = world;
        _rollers = new Node3D { Name = "Rollers", Visible = false, TopLevel = true };
        var logMat = new StandardMaterial3D { AlbedoColor = new Color(0.33f, 0.26f, 0.18f), Roughness = 0.95f };
        for (int i = 0; i < 3; i++)
            _rollers.AddChild(new MeshInstance3D
            {
                Mesh = new CylinderMesh { TopRadius = 0.18f, BottomRadius = 0.18f, Height = Raft.Width + 0.8f, RadialSegments = 10 },
                MaterialOverride = logMat,
                Position = new Vector3(0f, 0.18f, (i - 1) * 2.6f),
                Rotation = new Vector3(0f, 0f, MathF.PI / 2f),
            });
        AddChild(_rollers);
        _fadeLayer = new CanvasLayer { Layer = 20 };
        _fade = new ColorRect { Color = new Color(0, 0, 0, 0), MouseFilter = Control.MouseFilterEnum.Ignore };
        _fade.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _fadeLayer.AddChild(_fade);
        AddChild(_fadeLayer);
        Origin.Shifted += d => { if (IsInstanceValid(_rollers)) _rollers.GlobalPosition -= d; };
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is InputEventKey k && k.Pressed && !k.Echo && k.PhysicalKeycode == Key.F) _fPressed = true;
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_raft == null) return;
        float dt = (float)delta;
        bool f = _fPressed; _fPressed = false;
        Prompt = Time.GetTicksMsec() / 1000.0 < _messageUntil ? _message : null;
        if (_busy) return;
        if (_falls != null) { Push(dt); return; }

        var rp = _raft.GlobalPosition;
        var (rwx, rwz) = Origin.ToWorld(rp);
        foreach (var fa in _world.Falls)
        {
            // Over the edge? Only by crossing the lip from above (a raft relaunched below the falls is fine).
            var (along, across) = fa.Relative(rwx, rwz);
            double now = Time.GetTicksMsec() / 1000.0;
            if (along < 0.0 && along > -300.0 && Math.Abs(across) < fa.HalfWidth + 15.0) { _above = fa; _aboveAt = now; }
            if (_above == fa && now - _aboveAt < 30.0 && along > 3.0 && along < 80.0 && Math.Abs(across) < fa.HalfWidth + 15.0
                && rp.Y < fa.TopEta - 2f) { _above = null; _pinned = 0f; Wreck(fa); return; }
            // Pinned against the lip boulders at the very edge: the river drags it over in the end.
            if (along > -14.0 && along < 3.0 && Math.Abs(across) < fa.HalfWidth + 15.0 && _raft.LinearVelocity.Length() < 0.3f)
            {
                _pinned += dt;
                if (_pinned > 6f) { _above = null; _pinned = 0f; Wreck(fa); return; }
            }
            else if (along < -14.0 && along > -300.0 && Math.Abs(across) < fa.HalfWidth + 15.0) _pinned = 0f;

            var head = Origin.ToScene(fa.TrailX[0], fa.TrailH[0], fa.TrailZ[0]);
            var landing = Origin.ToScene(fa.LandingX, fa.TopEta, fa.LandingZ);
            var pp = _player.GlobalPosition;
            bool ashore = !_player.OnRaft && !_player.Swimming;
            if (ashore && Flat(pp - head) < 9f && Flat(rp - landing) < 28f)
            {
                Prompt ??= "F: haul the raft out onto the rollers";
                if (f) { Begin(fa); return; }
            }
            else if (Prompt == null && along > -260.0 && along < -20.0 && Math.Abs(across) < fa.HalfWidth + 40.0)
                Prompt = fa.Side > 0
                    ? "A waterfall ahead. Land in the bay on the right bank by the post and haul the raft round."
                    : "A waterfall ahead. Land in the bay on the left bank by the post and haul the raft round.";
        }
    }

    static float Flat(Vector3 v) => MathF.Sqrt(v.X * v.X + v.Z * v.Z);

    // ------------------------------------------------------------------ portage

    void Begin(WorldFalls fa)
    {
        _falls = fa;
        _cum = new double[fa.TrailX.Length];
        for (int j = 1; j < _cum.Length; j++)
            _cum[j] = _cum[j - 1] + Math.Sqrt((fa.TrailX[j] - fa.TrailX[j - 1]) * (fa.TrailX[j] - fa.TrailX[j - 1]) +
                                              (fa.TrailZ[j] - fa.TrailZ[j - 1]) * (fa.TrailZ[j] - fa.TrailZ[j - 1]));
        _s = 4f;
        FadeThrough(0.35f, () =>
        {
            if (_raft.AnchorDown) _raft.ToggleAnchor();
            _raft.LinearVelocity = Vector3.Zero; _raft.AngularVelocity = Vector3.Zero;
            _raft.FreezeMode = RigidBody3D.FreezeModeEnum.Kinematic;
            _raft.Freeze = true;
            _rollers.Visible = true;
            PlaceOnTrail();
        });
    }

    void Push(float dt)
    {
        var (pos, dir) = TrailPose(_s);
        var pp = _player.GlobalPosition;
        var rel = pp - pos;
        float behind = rel.X * dir.X + rel.Z * dir.Z, lateral = -rel.X * dir.Z + rel.Z * dir.X;
        bool pushing = (ForcePush || Input.IsActionPressed("move_forward")) && behind > -6.5f && behind < -2.0f && MathF.Abs(lateral) < 3.2f;
        float length = (float)_cum[^1];
        Prompt ??= pushing ? $"Pushing the raft along the portage trail… {(int)(length - _s)} m to go"
                           : "Walk into the raft from behind (W) to push it along the trail.";
        if (pushing) { _s += PushSpeed * dt; _rollerTurn += PushSpeed * dt / 0.18f; }
        if (_s >= length - 4f) { Relaunch(); return; }
        PlaceOnTrail();
    }

    void PlaceOnTrail()
    {
        var (pos, dir) = TrailPose(_s);
        // The raft's bow (−Z) along the trail; tilted with the slope.
        var (ahead, _) = TrailPose(_s + 2.5f); var (back, _) = TrailPose(_s - 2.5f);
        float pitch = MathF.Atan2(ahead.Y - back.Y, 5f);
        var basis = new Basis(Vector3.Up, MathF.Atan2(-dir.X, -dir.Z)) * new Basis(Vector3.Right, pitch);
        _raft.GlobalTransform = new Transform3D(basis, pos + Vector3.Up * RollerLift);
        // Rollers under it, turning as it moves.
        _rollers.GlobalTransform = new Transform3D(basis, pos);
        foreach (var c in _rollers.GetChildren()) if (c is Node3D n) n.Rotation = new Vector3(_rollerTurn, 0f, MathF.PI / 2f);
    }

    /// <summary>Scene position on the trail (on the ground) and its horizontal direction, at distance s.</summary>
    (Vector3 pos, Vector3 dir) TrailPose(float s)
    {
        var fa = _falls;
        s = Math.Clamp(s, 0f, (float)_cum[^1]);
        int j = 0; while (j + 2 < _cum.Length && _cum[j + 1] < s) j++;
        double seg = Math.Max(1e-6, _cum[j + 1] - _cum[j]);
        double t = Math.Clamp((s - _cum[j]) / seg, 0.0, 1.0);
        double x = fa.TrailX[j] + (fa.TrailX[j + 1] - fa.TrailX[j]) * t, z = fa.TrailZ[j] + (fa.TrailZ[j + 1] - fa.TrailZ[j]) * t;
        float h = fa.TrailH[j] + (fa.TrailH[j + 1] - fa.TrailH[j]) * (float)t;
        // Direction smoothed over the polyline's corners: from 3 m behind to 3 m ahead.
        var (ax, az) = PointAt(Math.Min(s + 3f, (float)_cum[^1])); var (bx, bz) = PointAt(Math.Max(s - 3f, 0f));
        var dir = new Vector3((float)(ax - bx), 0f, (float)(az - bz));
        dir = dir.LengthSquared() > 1e-6f ? dir.Normalized() : new Vector3(fa.DirX, 0f, fa.DirZ);
        return (Origin.ToScene(x, h, z), dir);
    }

    (double x, double z) PointAt(float s)
    {
        var fa = _falls;
        int j = 0; while (j + 2 < _cum.Length && _cum[j + 1] < s) j++;
        double t = Math.Clamp((s - _cum[j]) / Math.Max(1e-6, _cum[j + 1] - _cum[j]), 0.0, 1.0);
        return (fa.TrailX[j] + (fa.TrailX[j + 1] - fa.TrailX[j]) * t, fa.TrailZ[j] + (fa.TrailZ[j + 1] - fa.TrailZ[j]) * t);
    }

    void Relaunch()
    {
        var fa = _falls;
        FadeThrough(0.35f, () =>
        {
            PutAfloat(Origin.ToScene(fa.PutInX, fa.BottomEta + 0.1f, fa.PutInZ), fa);
            _rollers.Visible = false;
            _falls = null;
            _above = null;
            Say("The raft is afloat again below the falls.", 5);
            Relaunches++;
            GD.Print($"PORTAGE: raft afloat below the falls (segment {fa.Segment})");
        });
    }

    void PutAfloat(Vector3 at, WorldFalls fa)
    {
        _raft.Freeze = false;
        _raft.GlobalTransform = new Transform3D(new Basis(Vector3.Up, MathF.Atan2(-fa.DirX, -fa.DirZ)), at);
        _raft.LinearVelocity = Vector3.Zero; _raft.AngularVelocity = Vector3.Zero;
    }

    // ------------------------------------------------------------------ wreck

    void Wreck(WorldFalls fa)
    {
        bool aboard = _player.OnRaft;
        Wrecks++;
        GD.Print($"PORTAGE: raft wrecked over the falls (segment {fa.Segment}), rebuilt at the landing");
        FadeThrough(1.2f, () =>
        {
            PutAfloat(Origin.ToScene(fa.LandingX, fa.TopEta + 0.1f, fa.LandingZ), fa);
            if (!_raft.AnchorDown) _raft.ToggleAnchor();
            if (!aboard)
            {
                var head = Origin.ToScene(fa.TrailX[0], fa.TrailH[0] + 1.2f, fa.TrailZ[0]);
                _player.GlobalPosition = head;
                _player.Velocity = Vector3.Zero;
            }
            Say("The raft was smashed to pieces in the falls. A new one has been lashed together at the landing.", 8);
        });
    }

    // ------------------------------------------------------------------ helpers

    void Say(string text, double seconds) { _message = text; _messageUntil = Time.GetTicksMsec() / 1000.0 + seconds; }

    /// <summary>Fade to black, run the action while the screen is black, fade back.</summary>
    void FadeThrough(float seconds, Action atBlack)
    {
        _busy = true;
        var tw = CreateTween();
        tw.TweenProperty(_fade, "color:a", 1f, seconds * 0.5f);
        tw.TweenCallback(Callable.From(() => { atBlack(); }));
        tw.TweenInterval(0.15f);
        tw.TweenProperty(_fade, "color:a", 0f, seconds * 0.5f);
        tw.TweenCallback(Callable.From(() => { _busy = false; }));
    }
}
