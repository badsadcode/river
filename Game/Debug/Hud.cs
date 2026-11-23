using System;
using Godot;

namespace River.Debug;

/// <summary>Minimal on-screen info: controls (H hides them) and raft/water telemetry (F3).</summary>
public partial class Hud : CanvasLayer
{
    Label _info, _help;
    bool _showHelp = true, _showInfo;

    public override void _Ready()
    {
        _help = new Label { Position = new Vector2(24, 20) };
        _help.AddThemeFontSizeOverride("font_size", 15);
        _help.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0.8f));
        _help.AddThemeConstantOverride("shadow_offset_x", 1); _help.AddThemeConstantOverride("shadow_offset_y", 1);
        _help.Text = "WASD walk · Shift run · Space jump · E take/release oar (A/D steer, W/S scull) · hold Q pole · G anchor\n" +
                     "V chase camera (mouse orbit, wheel zoom) · T next time of day · Y overcast · [ ] time speed · F1 fly camera · F3 info · H hide";
        AddChild(_help);
        _info = new Label { Position = new Vector2(24, 70) };
        _info.AddThemeFontSizeOverride("font_size", 15);
        _info.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0.8f));
        _info.AddThemeConstantOverride("shadow_offset_x", 1); _info.AddThemeConstantOverride("shadow_offset_y", 1);
        AddChild(_info);
        // Hidden during screenshots.
        Visible = !(OS.GetCmdlineUserArgs().Length > 0 && Array.Exists(OS.GetCmdlineUserArgs(), a => a.StartsWith("--shot") || a.StartsWith("--cam")));
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is InputEventKey k && k.Pressed && !k.Echo)
        {
            if (k.PhysicalKeycode == Key.H) { _showHelp = !_showHelp; }
            if (k.PhysicalKeycode == Key.F3) { _showInfo = !_showInfo; }
        }
    }

    double _refresh;

    public override void _Process(double delta)
    {
        var main = River.Main.Instance;
        if (main?.Raft == null) return;
        _help.Visible = _showHelp;
        _info.Position = new Vector2(24, _showHelp ? 70 : 20);
        // Text 4 times a second: rebuilding strings every frame is garbage the collector has to pause for.
        _refresh -= delta;
        if (_refresh > 0) return;
        _refresh = 0.25;
        var r = main.Raft; var p = r.GlobalPosition; var pl = main.Player;
        var wv = r.WaterVelocity(p.X, p.Z);
        string state = pl.AtOar ? "at the sweep oar" : pl.Poling ? "poling" : pl.Swimming ? "swimming" : pl.OnRaft ? "on deck" : "ashore";
        string line = $"{main.Env.TimeLabel} · {state}{(r.AnchorDown ? " · anchored" : "")}{(r.Grounded ? " · aground!" : "")}";
        if (_showInfo)
            line += $"\nraft {r.LinearVelocity.Length():F2} m/s · current {wv.Length():F2} m/s · depth {main.World.DepthAt(p.X, p.Z):F1} m · " +
                    $"sun {main.Env.SunElevation:F0}° · {Engine.GetFramesPerSecond()} fps · pos {p.X:F0}, {p.Z:F0} · " +
                    $"segment {main.World.SegmentAt(p.X, p.Z)} · {main.Distance / 1000f:F1} km";
        _info.Text = line;
        _info.Position = new Vector2(24, _showHelp ? 70 : 20);
    }
}
