using System;
using Godot;

namespace River.Player;

/// <summary>
/// First-person explorer. Walks on the raft while it drifts (simulated in the raft's frame: the player is a
/// child of the raft while standing on its deck), steps off onto land, swims if they fall in.
///
/// Controls: WASD move, Shift run, Space jump/climb out, E take/release the sweep oar (then A/D steer,
/// W/S scull), hold Q to pole off the bottom, G anchor, V chase camera, T jump to the next time of day,
/// Y overcast, [ ] slower/faster time, F1 fly camera.
/// </summary>
public partial class PlayerController : CharacterBody3D
{
    const float EyeHeight = 1.62f, WalkSpeed = 2.1f, RunSpeed = 4.2f, JumpSpeed = 3.6f, Gravity = 9.81f;
    const float SwimSpeed = 1.1f;

    Raft.Raft _raft;
    Camera3D _cam;
    Camera3D _chase;
    float _yaw, _pitch;            // yaw relative to the parent (raft or world)
    bool _active, _atOar, _chaseView;
    float _chaseYaw = 0.6f, _chasePitch = -0.35f, _chaseDist = 16f;
    public bool OnRaft => GetParent() == _raft;
    public bool AtOar => _atOar;
    public bool Swimming { get; private set; }
    public bool Poling { get; private set; }

    public void Setup(Raft.Raft raft)
    {
        _raft = raft;
        CollisionLayer = 8; CollisionMask = 1 | 4;
        FloorMaxAngle = Mathf.DegToRad(50f);
        FloorSnapLength = 0.3f;
        PlatformFloorLayers = 0;    // the raft frame already carries us; don't add platform velocity on top
        PlatformOnLeave = PlatformOnLeaveEnum.DoNothing;
        AddChild(new CollisionShape3D
        {
            Shape = new CapsuleShape3D { Radius = 0.3f, Height = 1.78f }, Position = new Vector3(0, 0.89f, 0),
        });
        _cam = new Camera3D { Name = "Eyes", Position = new Vector3(0, EyeHeight, 0), Fov = 72f, Near = 0.05f, Far = 9000f };
        AddChild(_cam);
        _chase = new Camera3D { Name = "Chase", Fov = 60f, Near = 0.1f, Far = 9000f, TopLevel = true };
        AddChild(_chase);
        // Start on the deck behind the tent, facing the bow.
        Position = new Vector3(0.6f, Raft.Raft.DeckTop + 0.05f, Raft.Raft.Length * 0.32f);
        _yaw = 0f;
    }

    public void Activate()
    {
        _active = true;
        Input.MouseMode = Input.MouseModeEnum.Captured;
        (_chaseView ? _chase : _cam).MakeCurrent();
    }

    public void Deactivate() { _active = false; }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is InputEventKey k && k.Pressed && !k.Echo)
        {
            switch (k.PhysicalKeycode)
            {
                case Key.Escape: Input.MouseMode = Input.MouseMode == Input.MouseModeEnum.Captured ? Input.MouseModeEnum.Visible : Input.MouseModeEnum.Captured; break;
                case Key.T: Main.Instance.Env.NextPreset(); break;
                case Key.Y: Main.Instance.Env.ToggleOvercast(); break;
                case Key.Bracketleft: Main.Instance.Env.ChangeTimeScale(-1); break;
                case Key.Bracketright: Main.Instance.Env.ChangeTimeScale(1); break;
                case Key.F1: Main.Instance.UseFlyCam(!Main.Instance.FlyCam.Active); if (Main.Instance.FlyCam.Active) Main.Instance.FlyCam.LookAtPoint(_cam.GlobalPosition, _cam.GlobalPosition - _cam.GlobalBasis.Z); break;
            }
        }
        if (!_active) return;
        if (e is InputEventMouseButton mb && mb.Pressed && Input.MouseMode != Input.MouseModeEnum.Captured)
            Input.MouseMode = Input.MouseModeEnum.Captured;
        if (e is InputEventMouseMotion m && Input.MouseMode == Input.MouseModeEnum.Captured)
        {
            if (_chaseView)
            {
                _chaseYaw -= m.Relative.X * 0.003f;
                _chasePitch = Mathf.Clamp(_chasePitch - m.Relative.Y * 0.003f, -1.3f, 0.2f);
            }
            else
            {
                _yaw -= m.Relative.X * 0.0022f;
                _pitch = Mathf.Clamp(_pitch - m.Relative.Y * 0.0022f, -1.45f, 1.45f);
            }
        }
        if (e is InputEventMouseButton wheel && _chaseView)
        {
            if (wheel.ButtonIndex == MouseButton.WheelUp) _chaseDist = MathF.Max(5f, _chaseDist * 0.9f);
            if (wheel.ButtonIndex == MouseButton.WheelDown) _chaseDist = MathF.Min(80f, _chaseDist * 1.1f);
        }
        if (e is InputEventKey key && key.Pressed && !key.Echo)
        {
            switch (key.PhysicalKeycode)
            {
                case Key.E: ToggleOar(); break;
                case Key.G: if (OnRaft) _raft.ToggleAnchor(); break;
                case Key.V: _chaseView = !_chaseView; (_chaseView ? _chase : _cam).MakeCurrent(); break;
            }
        }
    }

    void ToggleOar()
    {
        if (_atOar) { _atOar = false; return; }
        if (!OnRaft) return;
        if (Position.DistanceTo(_raft.OarHandleLocal with { Y = Position.Y }) < 1.8f) _atOar = true;
    }

    public override void _PhysicsProcess(double delta)
    {
        float dt = (float)delta;
        bool onRaft = OnRaft;
        UpDirection = onRaft ? _raft.GlobalBasis.Y.Normalized() : Vector3.Up;

        Vector2 input = Vector2.Zero;
        bool run = false, jump = false, pole = false;
        if (_active)
        {
            if (Input.IsPhysicalKeyPressed(Key.W)) input.Y += 1;
            if (Input.IsPhysicalKeyPressed(Key.S)) input.Y -= 1;
            if (Input.IsPhysicalKeyPressed(Key.A)) input.X -= 1;
            if (Input.IsPhysicalKeyPressed(Key.D)) input.X += 1;
            run = Input.IsPhysicalKeyPressed(Key.Shift);
            jump = Input.IsPhysicalKeyPressed(Key.Space);
            pole = Input.IsPhysicalKeyPressed(Key.Q);
        }

        // Look direction in the parent's frame.
        Rotation = new Vector3(0, _yaw, 0);
        var forward = -GlobalBasis.Z; var right = GlobalBasis.X;

        // ------------------------------------------------ at the oar: stand at the handle, steer instead of walking
        _raft.SteerInput = 0; _raft.RowInput = 0;
        if (_atOar && onRaft)
        {
            var target = _raft.OarHandleLocal with { Y = Position.Y };
            Position = Position.Lerp(target, 1f - MathF.Exp(-dt * 6f));
            // A pushes the handle one way: the raft's stern swings the other (as with a real sweep or tiller).
            _raft.SteerInput = -input.X;
            _raft.RowInput = input.Y;
            input = Vector2.Zero;
        }

        // ------------------------------------------------ pole
        Poling = pole && onRaft && !_atOar && _raft.PoleReachDepth < 3.6f;
        var poleDir = forward with { Y = 0 };
        _raft.PoleDirection = Poling && poleDir.LengthSquared() > 0.01f ? poleDir.Normalized() : Vector3.Zero;
        if (Poling) input *= 0.3f;

        // ------------------------------------------------ swimming
        float eta = _raft.WaterHeight(GlobalPosition.X, GlobalPosition.Z);
        Swimming = !onRaft && GlobalPosition.Y < eta - 1.0f;

        var up = UpDirection;
        var v = Velocity;
        var planar = forward * input.Y + right * input.X;
        planar -= up * planar.Dot(up);
        if (planar.LengthSquared() > 1f) planar = planar.Normalized();

        if (Swimming)
        {
            var current = _raft.WaterVelocity(GlobalPosition.X, GlobalPosition.Z);
            var target = planar * SwimSpeed + current;
            v.X = Mathf.Lerp(v.X, target.X, 1f - MathF.Exp(-dt * 2f));
            v.Z = Mathf.Lerp(v.Z, target.Z, 1f - MathF.Exp(-dt * 2f));
            // Float with the head out of the water.
            float floatY = eta - 1.35f;
            v.Y = Mathf.Lerp(v.Y, (floatY - GlobalPosition.Y) * 2.5f, 1f - MathF.Exp(-dt * 4f));
            if (jump) TryClimbOut();
        }
        else
        {
            float speed = run ? RunSpeed : WalkSpeed;
            var vUp = up * v.Dot(up);
            var vPlanar = v - vUp;
            float accel = IsOnFloor() ? 14f : 2.5f;
            vPlanar = vPlanar.Lerp(planar * speed, 1f - MathF.Exp(-dt * accel));
            if (IsOnFloor())
            {
                vUp = Vector3.Zero;
                if (jump) vUp = up * JumpSpeed;
            }
            else vUp -= up * Gravity * dt;
            v = vPlanar + vUp;
        }
        Velocity = v;
        MoveAndSlide();

        UpdateSurface();
        UpdateCameras(dt);

        // Tell the raft where our weight is.
        if (OnRaft) { _raft.LoadMass = 80f; _raft.LoadPoint = GlobalPosition; }
        else _raft.LoadMass = 0f;
    }

    /// <summary>Switch frames when we step from the raft to land or back.</summary>
    void UpdateSurface()
    {
        for (int i = 0; i < GetSlideCollisionCount(); i++)
        {
            var c = GetSlideCollision(i);
            if (c.GetNormal().Dot(UpDirection) < 0.6f) continue;
            var body = c.GetCollider() as Node;
            if (body == _raft.Deck && !OnRaft) { Reparent(_raft, true); _yaw = Rotation.Y; return; }
            if (body != _raft.Deck && body != null && OnRaft) { Reparent(Main.Instance, true); _yaw = Rotation.Y; _atOar = false; return; }
        }
        // Safety net: with several physics steps per frame (low fps) the deck collider can lag a step behind the
        // raft and let us sink through it. Inside the deck's outline we can only be on it, so put us back.
        if (OnRaft)
        {
            var lp = Position;
            bool overDeck = MathF.Abs(lp.X) < Raft.Raft.Width * 0.5f - 0.1f && MathF.Abs(lp.Z) < Raft.Raft.Length * 0.5f - 0.1f;
            if (overDeck && lp.Y < Raft.Raft.DeckTop - 0.05f && lp.Y > Raft.Raft.DeckTop - 1.5f)
            {
                Position = lp with { Y = Raft.Raft.DeckTop };
                Velocity = Velocity with { Y = 0f };
                return;
            }
        }
        // Fell off the deck into the water: leave the raft frame.
        if (OnRaft && GlobalPosition.Y < _raft.GlobalPosition.Y - 0.6f) { Reparent(Main.Instance, true); _yaw = Rotation.Y; _atOar = false; }
    }

    void TryClimbOut()
    {
        // Onto the raft if we're next to it.
        var local = _raft.ToLocal(GlobalPosition);
        if (MathF.Abs(local.X) < Raft.Raft.Width * 0.5f + 1.2f && MathF.Abs(local.Z) < Raft.Raft.Length * 0.5f + 1.2f)
        {
            var onDeck = new Vector3(Math.Clamp(local.X, -Raft.Raft.Width * 0.5f + 0.5f, Raft.Raft.Width * 0.5f - 0.5f), Raft.Raft.DeckTop + 0.1f,
                                     Math.Clamp(local.Z, -Raft.Raft.Length * 0.5f + 0.5f, Raft.Raft.Length * 0.5f - 0.5f));
            Reparent(_raft, false);
            Position = onDeck;
            _yaw = 0f;
            Velocity = Vector3.Zero;
        }
        else Velocity = Velocity with { Y = 2.5f };   // a stroke upwards: lets you scramble up a low bank
    }

    void UpdateCameras(float dt)
    {
        // First person: pitch on the camera. On the raft, keep the horizon steadier than the deck
        // (the eye compensates for most of the raft's roll and pitch).
        var level = Basis.FromEuler(new Vector3(_pitch, GlobalRotation.Y, 0));
        var attached = GlobalBasis * Basis.FromEuler(new Vector3(_pitch, 0, 0));
        var b = OnRaft ? attached.Orthonormalized().Slerp(level.Orthonormalized(), 0.7f) : level;
        _cam.GlobalTransform = new Transform3D(b, GlobalPosition + UpDirection * EyeHeight);

        // Chase camera orbiting the raft (or the player when ashore).
        var focus = OnRaft || _atOar ? _raft.GlobalPosition + Vector3.Up * 1.2f : GlobalPosition + Vector3.Up * 1.4f;
        var offset = new Vector3(MathF.Sin(_chaseYaw) * MathF.Cos(_chasePitch), -MathF.Sin(_chasePitch), MathF.Cos(_chaseYaw) * MathF.Cos(_chasePitch)) * _chaseDist;
        var pos = focus + offset;
        var here = Main.Instance.World.At(pos.X, pos.Z);
        float ground = MathF.Max(here.Height, here.Eta);
        pos.Y = MathF.Max(pos.Y, ground + 1.0f);
        _chase.GlobalPosition = _chase.GlobalPosition.Lerp(pos, 1f - MathF.Exp(-dt * 5f));
        _chase.LookAt(focus, Vector3.Up);
    }
}
