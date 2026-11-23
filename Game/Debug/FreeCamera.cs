using Godot;

namespace River.Debug;

/// <summary>Fly camera for looking around and for screenshots. WASD + mouse, Q/E down/up, Shift fast.</summary>
public partial class FreeCamera : Camera3D
{
    public float Speed = 12f;
    float _yaw, _pitch;
    public bool Active;

    public void SetView(Vector3 pos, float yawDeg, float pitchDeg)
    {
        Position = pos;
        _yaw = Mathf.DegToRad(yawDeg);
        _pitch = Mathf.DegToRad(pitchDeg);
        Rotation = new Vector3(_pitch, _yaw, 0);
    }

    public void LookAtPoint(Vector3 from, Vector3 target)
    {
        Position = from;
        var d = (target - from).Normalized();
        _yaw = Mathf.Atan2(-d.X, -d.Z);
        _pitch = Mathf.Asin(d.Y);
        Rotation = new Vector3(_pitch, _yaw, 0);
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (!Active) return;
        if (e is InputEventMouseMotion m && Input.MouseMode == Input.MouseModeEnum.Captured)
        {
            _yaw -= m.Relative.X * 0.0025f;
            _pitch = Mathf.Clamp(_pitch - m.Relative.Y * 0.0025f, -1.5f, 1.5f);
            Rotation = new Vector3(_pitch, _yaw, 0);
        }
    }

    public override void _Process(double delta)
    {
        if (!Active) return;
        var dir = Vector3.Zero;
        if (Input.IsPhysicalKeyPressed(Key.W)) dir -= Transform.Basis.Z;
        if (Input.IsPhysicalKeyPressed(Key.S)) dir += Transform.Basis.Z;
        if (Input.IsPhysicalKeyPressed(Key.A)) dir -= Transform.Basis.X;
        if (Input.IsPhysicalKeyPressed(Key.D)) dir += Transform.Basis.X;
        if (Input.IsPhysicalKeyPressed(Key.E)) dir += Vector3.Up;
        if (Input.IsPhysicalKeyPressed(Key.Q)) dir -= Vector3.Up;
        float sp = Speed * (Input.IsPhysicalKeyPressed(Key.Shift) ? 6f : 1f);
        Position += dir.Normalized() * sp * (float)delta;
    }
}
