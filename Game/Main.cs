using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Godot;
using River.Core;
using River.Debug;
using River.World;

namespace River;

/// <summary>
/// Entry point: generates (or loads) the reach, builds the world, the raft and the player.
///
/// Command line (after "--"):
///   --shot=NAME            camera preset for a screenshot (see Shots), implies capture
///   --cam=x,y,z,yaw,pitch  explicit camera
///   --out=PATH             screenshot file (default captures/NAME.png)
///   --time=N               time-of-day preset index (EnvironmentRig.PresetNames)
///   --frames=N             frames to wait before capturing (default 90)
///   --seconds=S            simulated seconds before capturing (raft drifting, etc.)
///   --regen                ignore the reach cache
///   --drift-test=S         headless: drift the raft S seconds and report (Tests in tools/drift_test.sh)
/// </summary>
public partial class Main : Node3D
{
    public ReachField Field { get; private set; }
    public FieldTextures Textures { get; private set; }
    public EnvironmentRig Env { get; private set; }
    public ShaderMaterial WaterMaterial { get; private set; }
    public FreeCamera FlyCam { get; private set; }
    public Raft.Raft Raft { get; private set; }
    public Player.PlayerController Player { get; private set; }

    readonly Dictionary<string, string> _args = new();
    Label _status;

    public static Main Instance { get; private set; }

    public override void _Ready()
    {
        Instance = this;
        foreach (var a in OS.GetCmdlineUserArgs())
        {
            var kv = a.TrimStart('-').Split('=', 2);
            _args[kv[0]] = kv.Length > 1 ? kv[1] : "1";
        }

        var ui = new CanvasLayer();
        AddChild(ui);
        _status = new Label { Text = "Generating the river…", Position = new Vector2(40, 40) };
        _status.AddThemeFontSizeOverride("font_size", 28);
        ui.AddChild(_status);

        bool sync = _args.ContainsKey("shot") || _args.ContainsKey("shots") || _args.ContainsKey("cam") || _args.ContainsKey("drift-test");
        if (sync) { Field = LoadOrGenerate(); BuildWorld(); }
        else Task.Run(() => { Field = LoadOrGenerate(); CallDeferred(MethodName.BuildWorld); });
    }

    /// <summary>The journey's sandbox settings (user://game_sandbox_settings.json, written with defaults if missing).</summary>
    public SandboxSettings Sandbox { get; private set; }

    void LoadSandbox()
    {
        string path = ProjectSettings.GlobalizePath("user://game_sandbox_settings.json");
        var warnings = new List<string>();
        if (!System.IO.File.Exists(path))
        {
            Sandbox = SandboxSettings.FromPreset("explorer");
            try { Sandbox.Save(path); GD.Print($"Wrote default sandbox settings: {path}"); }
            catch (Exception e) { GD.PushWarning($"Could not write {path}: {e.Message}"); }
        }
        else Sandbox = SandboxSettings.Load(path, warnings);
        foreach (var w in warnings) GD.PushWarning($"Sandbox settings: {w}");
    }

    ReachField LoadOrGenerate()
    {
        LoadSandbox();
        var settings = new ReachSettings { Seed = Sandbox.Seed };
        // Current strength scales the discharge: same channels, faster (or lazier) water, consistently everywhere.
        settings.DischargeMain *= Sandbox.River.CurrentStrength;
        settings.DischargeTributary *= Sandbox.River.CurrentStrength;
        string key = typeof(ReachGenerator).Assembly.ManifestModule.ModuleVersionId + "/" + settings.Seed + "/" + Sandbox.River.CurrentStrength.ToString(CultureInfo.InvariantCulture);
        string path = ProjectSettings.GlobalizePath($"user://cache/reach_{settings.Seed}.bin");
        if (!_args.ContainsKey("regen"))
        {
            var cached = ReachCache.TryLoad(path, key);
            if (cached != null) { GD.Print("Reach loaded from cache."); return cached; }
        }
        var f = ReachGenerator.Generate(settings, m => GD.Print(m));
        try { ReachCache.Save(f, path, key); } catch (Exception e) { GD.PushWarning($"Reach cache not saved: {e.Message}"); }
        return f;
    }

    void BuildWorld()
    {
        var f = Field;
        Textures = new FieldTextures(f);
        Env = new EnvironmentRig { Name = "Environment" };
        AddChild(Env);
        Env.Setup(f, Textures, Sandbox);
        AddChild(TerrainBuilder.Build(f, Textures));
        WaterMaterial = WaterBuilder.CreateMaterial(Textures);
        Env.Water = WaterMaterial;
        AddChild(WaterBuilder.Build(f, WaterMaterial));
        AddChild(Vegetation.Build(f));

        FlyCam = new FreeCamera { Name = "FlyCam", Far = 9000f, Near = 0.08f, Fov = 70f };
        AddChild(FlyCam);

        // The raft starts in the upper meanders, a little way below the inlet, pointing downstream.
        int k = f.Main.IndexAtZ(160f);
        var start = new Vector3(f.Main.X[k], f.SampleEta(f.Main.X[k], f.Main.Z[k]) + 0.1f, f.Main.Z[k]);
        Raft = new Raft.Raft { Name = "Raft" };
        AddChild(Raft);
        Raft.Setup(f, start, MathF.Atan2(-f.Main.TanX[k], -f.Main.TanZ[k]));
        Player = new Player.PlayerController { Name = "Player" };
        Raft.AddChild(Player);
        Player.Setup(Raft);

        if (_args.TryGetValue("time", out var t)) Env.ApplyPreset(int.Parse(t) % EnvironmentRig.PresetNames.Length);
        // Debug/tuning: --water=name=value;name=value overrides water shader uniforms (floats), --nomist hides the mist.
        if (_args.TryGetValue("water", out var wset))
            foreach (var kv in wset.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var p = kv.Split('=', 2);
                if (p.Length != 2) continue;
                if (int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int iv)) WaterMaterial.SetShaderParameter(p[0], iv);
                else WaterMaterial.SetShaderParameter(p[0], float.Parse(p[1], CultureInfo.InvariantCulture));
            }

        AddChild(new Hud { Name = "Hud" });
        _status.Visible = false;

        if (_args.ContainsKey("drift-test")) { _ = DriftTest(float.Parse(_args["drift-test"], CultureInfo.InvariantCulture)); return; }
        if (_args.ContainsKey("shot") || _args.ContainsKey("shots") || _args.ContainsKey("cam")) _ = Capture();
        else Player.Activate();
    }

    public override void _Process(double delta)
    {
        // The visible waves run on the raft's physics clock, so the raft rides exactly the waves on screen.
        if (WaterMaterial != null && Raft != null) WaterMaterial.SetShaderParameter("wave_time", Raft.WaveTime);
    }

    // ------------------------------------------------------------------ camera presets for screenshots

    public void UseFlyCam(bool on)
    {
        FlyCam.Active = on;
        if (on) { FlyCam.MakeCurrent(); Player.Deactivate(); }
        else Player.Activate();
    }

    void ApplyShot(string name)
    {
        var f = Field;
        Vector3 At(float x, float z, float up) => new(x, f.SampleHeight(x, z) + up, z);
        Vector3 W(float x, float z, float up) => new(x, f.SampleEta(x, z) + up, z);
        var m = f.Main;
        switch (name)
        {
            case "deck":
                Player.Activate();
                return;
            case "raft":
            {
                var p = Raft.GlobalPosition;
                var back = Raft.GlobalTransform.Basis.Z;
                FlyCam.LookAtPoint(p + back * 12f + new Vector3(4, 5, 0), p + Vector3.Up * 1f);
                break;
            }
            case "aerial":
            {
                int i = m.IndexAtZ(1400f);
                FlyCam.LookAtPoint(new Vector3(m.X[i] - 520f, f.SampleEta(m.X[i], m.Z[i]) + 260f, m.Z[i] - 420f), W(m.X[i], m.Z[i], 0));
                break;
            }
            case "lake":
            {
                var isl = f.Islands[0];
                int i = m.IndexAtZ(1330f);
                var (rx, rz) = m.Right(i);
                float l = -m.HalfLeft[i] + 3f;   // standing just inside the water's edge
                var from = new Vector3(m.X[i] + rx * l, 0, m.Z[i] + rz * l);
                from.Y = MathF.Max(f.SampleHeight(from.X, from.Z), f.SampleEta(from.X, from.Z)) + 1.7f;
                FlyCam.LookAtPoint(from, W(isl.X, isl.Z, 2f));
                break;
            }
            case "meander":
            {
                int i = m.IndexAtZ(600f);
                var (rx, rz) = m.Right(i);
                var from = W(m.X[i] + rx * (m.HalfRight[i] - 3f), m.Z[i] + rz * (m.HalfRight[i] - 3f), 1.6f);
                int j = m.IndexAtZ(760f);
                FlyCam.LookAtPoint(from, W(m.X[j], m.Z[j], 0.5f));
                break;
            }
            case "riffle":
            {
                int i = m.IndexAtZ(2930f);
                int j = m.IndexAtZ(2800f);
                var (rx, rz) = m.Right(i);
                var from = At(m.X[i] + rx * (m.HalfRight[i] + 4f), m.Z[i] + rz * (m.HalfRight[i] + 4f), 1.7f);
                FlyCam.LookAtPoint(from, W(m.X[j], m.Z[j], 0f));
                break;
            }
            case "confluence":
            {
                var tr = f.Tributary;
                int a = tr.Count - 1;
                int i = m.IndexAtZ(m.Z[m.Nearest(tr.X[a], tr.Z[a])] + 70f);
                var (rx, rz) = m.Right(i);
                var from = At(m.X[i] - rx * (m.HalfLeft[i] + 5f), m.Z[i] - rz * (m.HalfLeft[i] + 5f), 2.5f);
                FlyCam.LookAtPoint(from, W(tr.X[a], tr.Z[a], 0f));
                break;
            }
            // Eddies: from above and beside an island, looking back up its wake. "eddy" = a lake islet (slow water),
            // "bar" = the gravel bar in the meanders (faster water: foam on the eddy line).
            case "eddy": WakeShot(f.Islands[Math.Min(2, f.Islands.Count - 1)], 38f, 22f, 16f); break;
            case "bar": WakeShot(f.Islands[Math.Min(4, f.Islands.Count - 1)], 26f, 8f, 30f); break;
            case "rock":
            {
                // The most exposed rock in fast water, from the side and a little upstream: pillow, hollow, foam.
                Rock best = default; float score = -1f;
                foreach (var r in f.Rocks)
                {
                    float sp = MathF.Sqrt(r.FlowX * r.FlowX + r.FlowZ * r.FlowZ), s = sp * (r.Top + 0.6f) * r.Radius;
                    if (s > score) { score = s; best = r; }
                }
                float spd = MathF.Max(0.01f, MathF.Sqrt(best.FlowX * best.FlowX + best.FlowZ * best.FlowZ));
                float dx = best.FlowX / spd, dz = best.FlowZ / spd;
                var from = W(best.X - dx * 4f - dz * 4.5f, best.Z - dz * 4f + dx * 4.5f, 3.2f);
                FlyCam.LookAtPoint(from, W(best.X + dx * 0.8f, best.Z + dz * 0.8f, 0f));
                break;
            }
            case "foam_close":
            {
                // Standing in the riffle (as if on the raft), looking down and ahead at the foam from 2.5 m.
                int i = m.IndexAtZ(2840f);
                int j = i + 6;
                FlyCam.LookAtPoint(W(m.X[i], m.Z[i], 2.5f), W(m.X[j], m.Z[j], 0f));
                break;
            }
            case "water_close":
            {
                int i = m.IndexAtZ(420f);
                var (rx, rz) = m.Right(i);
                var from = W(m.X[i] + rx * (m.HalfRight[i] + 1f), m.Z[i] + rz * (m.HalfRight[i] + 1f), 1.2f);
                FlyCam.LookAtPoint(from, W(m.X[i] - rx * 6f + m.TanX[i] * 14f, m.Z[i] - rz * 6f + m.TanZ[i] * 14f, 0f));
                break;
            }
            default:
                GD.PushWarning($"Unknown shot '{name}'");
                break;
        }
        UseFlyCam(true);
    }

    void WakeShot(Island isl, float back, float side, float up)
    {
        var f = Field;
        float sx = -isl.DirZ, sz = isl.DirX;
        float tx = isl.X + isl.DirX * isl.LengthDown, tz = isl.Z + isl.DirZ * isl.LengthDown;
        float cx = tx + isl.DirX * back + sx * side, cz = tz + isl.DirZ * back + sz * side;
        var from = new Vector3(cx, MathF.Max(f.SampleEta(cx, cz), f.SampleHeight(cx, cz)) + up, cz);
        FlyCam.LookAtPoint(from, new Vector3(tx + isl.DirX * 6f, f.SampleEta(tx, tz), tz + isl.DirZ * 6f));
    }

    async Task Capture()
    {
        int frames = _args.TryGetValue("frames", out var fr) ? int.Parse(fr) : 90;
        float seconds = _args.TryGetValue("seconds", out var se) ? float.Parse(se, CultureInfo.InvariantCulture) : 0f;
        var names = _args.TryGetValue("shots", out var many) ? many.Split(',') : new[] { _args.TryGetValue("shot", out var sh) ? sh : "cam" };
        string dir = _args.TryGetValue("outdir", out var od) ? od : ProjectSettings.GlobalizePath("res://captures");

        if (seconds > 0f)
        {
            // Let physics run (raft drifting) faster than real time before framing the shots.
            Engine.TimeScale = 8.0;
            Engine.MaxPhysicsStepsPerFrame = 64;
            double simulated = 0;
            while (simulated < seconds)
            {
                await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
                simulated += GetPhysicsProcessDeltaTime();
            }
            Engine.TimeScale = 1.0;
        }

        foreach (var entry in names)
        {
            // "name@time" picks a time-of-day preset for that shot.
            var parts = entry.Split('@');
            string name = parts[0];
            if (parts.Length > 1) Env.ApplyPreset(int.Parse(parts[1]) % EnvironmentRig.PresetNames.Length);
            if (_args.ContainsKey("nomist")) Env.Mist.Visible = false;
            if (name == "cam" && _args.TryGetValue("cam", out var c))
            {
                var p = Array.ConvertAll(c.Split(','), s => float.Parse(s, CultureInfo.InvariantCulture));
                FlyCam.SetView(new Vector3(p[0], p[1], p[2]), p[3], p[4]);
                UseFlyCam(true);
            }
            else ApplyShot(name);

            for (int i = 0; i < frames; i++) await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            string outPath = names.Length == 1 && _args.TryGetValue("out", out var o) ? o : System.IO.Path.Combine(dir, entry.Replace('@', '_') + ".png");
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(outPath)!);
            GetViewport().GetTexture().GetImage().SavePng(outPath);
            GD.Print($"Captured {outPath}");
        }
        GetTree().Quit();
    }

    async Task DriftTest(float seconds)
    {
        Engine.TimeScale = 1.0;
        Engine.MaxPhysicsStepsPerFrame = 1000;
        var start = Raft.GlobalPosition;
        float maxTilt = 0f, maxSpeed = 0f, t = 0f;
        float nextLog = 0f;
        GD.Print("DRIFT t, x, z, speed, water speed, depth, tilt");
        while (t < seconds)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
            t += (float)GetPhysicsProcessDeltaTime();
            var up = Raft.GlobalTransform.Basis.Y;
            maxTilt = MathF.Max(maxTilt, Mathf.RadToDeg(up.AngleTo(Vector3.Up)));
            maxSpeed = MathF.Max(maxSpeed, Raft.LinearVelocity.Length());
            if (t >= nextLog)
            {
                var p = Raft.GlobalPosition;
                var (wx, wz) = Field.SampleVelocity(p.X, p.Z);
                GD.Print($"PLAYER on raft {Player.OnRaft}, swimming {Player.Swimming}, local {Raft.ToLocal(Player.GlobalPosition)}");
                GD.Print($"DRIFT {t,6:F0} {p.X,8:F1} {p.Z,8:F1} {Raft.LinearVelocity.Length(),5:F2} {MathF.Sqrt(wx * wx + wz * wz),5:F2} {Field.SampleDepth(p.X, p.Z),5:F2} {Mathf.RadToDeg(up.AngleTo(Vector3.Up)),5:F1}");
                nextLog += 10f;
            }
        }
        var end = Raft.GlobalPosition;
        // Steering check: full sweep-oar for 12 s, then sculling forward for 20 s.
        float yaw0 = Raft.GlobalRotation.Y;
        Raft.ForcedSteer = 1f;
        float maxYawRate = 0f;
        for (float s = 0; s < 12f; s += (float)GetPhysicsProcessDeltaTime())
        {
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
            maxYawRate = MathF.Max(maxYawRate, MathF.Abs(Raft.AngularVelocity.Y));
        }
        float turned = Mathf.RadToDeg(Mathf.AngleDifference(yaw0, Raft.GlobalRotation.Y));
        Raft.ForcedSteer = 0f;
        for (float s = 0; s < 8f; s += (float)GetPhysicsProcessDeltaTime()) await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        float yawAfter = MathF.Abs(Raft.AngularVelocity.Y);
        Raft.ForcedRow = 1f;
        float relMax = 0f;
        for (float s = 0; s < 20f; s += (float)GetPhysicsProcessDeltaTime())
        {
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
            var pp = Raft.GlobalPosition;
            relMax = MathF.Max(relMax, (Raft.LinearVelocity - Raft.WaterVelocity(pp.X, pp.Z)).Length());
        }
        Raft.ForcedRow = null; Raft.ForcedSteer = null;
        GD.Print($"STEER RESULT: oar turned the raft {turned:F0}° in 12 s (max {Mathf.RadToDeg(maxYawRate):F1}°/s), " +
                 $"{Mathf.RadToDeg(yawAfter):F1}°/s left 8 s after letting go; sculling: {relMax:F2} m/s through the water");
        GD.Print($"DRIFT RESULT: travelled {start.DistanceTo(end):F0} m (z {start.Z:F0} → {end.Z:F0}), max speed {maxSpeed:F2} m/s, max tilt {maxTilt:F1}°, grounded {Raft.GroundedTime:F0} s");
        GetTree().Quit();
    }
}
