using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Tasks;
using Godot;
using River.Core;
using River.Debug;
using River.World;

namespace River;

/// <summary>
/// Entry point: the endless river (streamed around the raft, docs/STREAMING.md), the raft and the player.
///
/// Command line (after "--"):
///   --shot=NAME / --shots=A,B@time,…  camera presets for screenshots (see ApplyShot), implies capture
///   --cam=x,y,z,yaw,pitch  explicit camera
///   --out=PATH             screenshot file (single shot; default captures/NAME.png), --outdir=DIR for several
///   --time=N               time-of-day preset index (EnvironmentRig.PresetNames)
///   --frames=N             frames to wait before capturing (default 90)
///   --seconds=S            simulated seconds before capturing (raft drifting, etc.)
///   --regen                ignore the segment cache
///   --drift-test=S         headless: drift the raft S seconds with streaming and report (tools/drift_test.sh)
///   --water=a=1;b=2        override water shader uniforms (tuning), --nomist hide the mist
/// </summary>
public partial class Main : Node3D
{
    public const int StartSegment = 2;          // segment 1 exists behind the raft, so the world doesn't end at its back

    public WorldStreamer Streamer { get; private set; }
    public RiverWorld World => Streamer.World;
    public EnvironmentRig Env { get; private set; }
    public FreeCamera FlyCam { get; private set; }
    public Raft.Raft Raft { get; private set; }
    public Player.PlayerController Player { get; private set; }
    /// <summary>Metres travelled down the valley since the start.</summary>
    public float Distance { get; private set; }

    readonly Dictionary<string, string> _args = new();
    Label _status;
    double _startSigma;
    int _sigmaHint;

    public static Main Instance { get; private set; }

    // Shader parameter names set every frame: a string literal would allocate a new StringName each call, and that
    // garbage made the collector pause ~33 ms every couple of minutes.
    static readonly StringName WaveTimeName = "wave_time";

    /// <summary>The journey's sandbox settings (user://game_sandbox_settings.json, written with defaults if missing).</summary>
    public SandboxSettings Sandbox { get; private set; }

    public override async void _Ready()
    {
        Instance = this;
        // A game: avoid blocking full garbage collections (the streamed river keeps ~400 MB of managed arrays; full
        // collections every couple of minutes stalled a frame for 30–50 ms).
        System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;
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
        // Let the message show before the (first-launch) generation blocks the main thread.
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        BuildWorld();
    }

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

    bool Capturing => _args.ContainsKey("shot") || _args.ContainsKey("shots") || _args.ContainsKey("cam");

    void BuildWorld()
    {
        LoadSandbox();
        var cs = new CourseSettings { Seed = Sandbox.Seed };
        // Current strength scales the discharge: same channels, faster (or lazier) water, consistently everywhere.
        cs.StartDischarge *= Sandbox.River.CurrentStrength;

        Env = new EnvironmentRig { Name = "Environment" };
        AddChild(Env);
        Env.Setup(cs.Seed, Sandbox);
        Env.MistHidden = _args.ContainsKey("nomist");

        string cacheDir = ProjectSettings.GlobalizePath("user://cache");
        string key = typeof(ReachGenerator).Assembly.ManifestModule.ModuleVersionId + "/" + cs.Seed + "/" +
                     Sandbox.River.CurrentStrength.ToString(CultureInfo.InvariantCulture);
        if (_args.ContainsKey("regen")) key += "/" + DateTime.Now.Ticks;   // nothing in the cache matches
        Streamer = new WorldStreamer { Name = "River" };
        AddChild(Streamer);
        Streamer.Setup(cs, cacheDir, key, Env);
        Streamer.OnWaterMaterial = ApplyWaterOverrides;
        var sw = Stopwatch.StartNew();
        Streamer.LoadSync(StartSegment, Capturing ? 1 : 0);
        GD.Print($"River ready around segment {StartSegment} in {sw.ElapsedMilliseconds} ms " +
                 $"({Streamer.SegmentsGenerated} generated, {Streamer.SegmentsFromCache} from cache)");

        FlyCam = new FreeCamera { Name = "FlyCam", Far = 9000f, Near = 0.08f, Fov = 70f };
        AddChild(FlyCam);

        // The raft starts 300 m into the start segment, on the river's centre line, pointing downstream.
        var c = World.Course;
        _startSigma = c.Cuts[StartSegment] + 300.0;
        int i = c.RiverIndexAt(_startSigma);
        float h = c.Heading[i];
        var start = new Vector3((float)c.X[i], World.EtaAt(c.X[i], c.Z[i]) + 0.1f, (float)c.Z[i]);
        Raft = new Raft.Raft { Name = "Raft" };
        AddChild(Raft);
        Raft.Setup(World, start, MathF.Atan2(-MathF.Sin(h), -MathF.Cos(h)));
        Player = new Player.PlayerController { Name = "Player" };
        Raft.AddChild(Player);
        Player.Setup(Raft);
        Streamer.Focus = () => Raft.GlobalPosition;

        if (_args.TryGetValue("time", out var t)) Env.ApplyPreset(int.Parse(t) % EnvironmentRig.PresetNames.Length);

        AddChild(new Hud { Name = "Hud" });
        _status.Visible = false;

        if (_args.ContainsKey("drift-test")) { Streamer.StartWorker(); _ = DriftTest(float.Parse(_args["drift-test"], CultureInfo.InvariantCulture)); return; }
        if (Capturing) { _ = Capture(); return; }
        Streamer.StartWorker();
        Player.Activate();
    }

    /// <summary>--water=name=value;…: tuning overrides on every river material (also ones streamed in later).</summary>
    void ApplyWaterOverrides(ShaderMaterial m)
    {
        if (!_args.TryGetValue("water", out var wset)) return;
        foreach (var kv in wset.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = kv.Split('=', 2);
            if (p.Length != 2) continue;
            if (int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int iv)) m.SetShaderParameter(p[0], iv);
            else m.SetShaderParameter(p[0], float.Parse(p[1], CultureInfo.InvariantCulture));
        }
    }

    public override void _Process(double delta)
    {
        if (Raft == null) return;
        // The visible waves run on the raft's physics clock, so the raft rides exactly the waves on screen.
        foreach (var m in Streamer.WaterMaterials) m.SetShaderParameter(WaveTimeName, Raft.WaveTime);
        var p = Raft.GlobalPosition;
        if (_sigmaHint == 0) _sigmaHint = (int)(_startSigma / Course.ValleyStep);
        var (sigma, _, idx) = World.Course.ProjectOnValley(p.X, p.Z, _sigmaHint);
        _sigmaHint = idx;
        Distance = (float)(sigma - _startSigma);
    }

    // ------------------------------------------------------------------ camera presets for screenshots

    public void UseFlyCam(bool on)
    {
        FlyCam.Active = on;
        if (on) { FlyCam.MakeCurrent(); Player.Deactivate(); }
        else Player.Activate();
    }

    /// <summary>First course feature of a kind downstream of the start (loading the river around it).</summary>
    CourseFeature? FindFeature(FeatureKind kind)
    {
        var c = World.Course;
        c.ExtendTo(c.Cuts[StartSegment] + 60000.0);
        foreach (var f in c.Features)
            if (f.Kind == kind && f.S0 > c.Cuts[StartSegment])
            {
                int k = 1; while (k + 1 < c.Cuts.Count && c.Cuts[k + 1] <= f.Mid) k++;
                Streamer.LoadSync(k, 1);
                GD.Print($"Shot: first {kind} at σ {f.Mid:F0} m, segment {k}");
                return f;
            }
        return null;
    }

    Vector3 Ground(double x, double z, float up)
    {
        var s = World.At(x, z);
        return new Vector3((float)x, MathF.Max(s.Height, s.Eta) + up, (float)z);
    }

    Vector3 OnWater(double x, double z, float up) => new((float)x, World.EtaAt(x, z) + up, (float)z);

    void ApplyShot(string name)
    {
        var c = World.Course;
        // River point at valley distance σ: position, flow direction, right-hand normal, half-widths.
        (double x, double z, double tx, double tz, double rx, double rz, float hl, float hr) At(double sigma)
        {
            int i = c.RiverIndexAt(sigma);
            float h = c.Heading[i];
            return (c.X[i], c.Z[i], MathF.Sin(h), MathF.Cos(h), -MathF.Cos(h), MathF.Sin(h), c.HalfLeft[i], c.HalfRight[i]);
        }
        var raft = Raft.GlobalPosition;
        var fwd = -Raft.GlobalTransform.Basis.Z; fwd.Y = 0; fwd = fwd.Normalized();
        var right = new Vector3(-fwd.Z, 0, fwd.X);
        switch (name)
        {
            case "deck":
                Player.Activate();
                return;
            case "raft":
            {
                var back = Raft.GlobalTransform.Basis.Z;
                FlyCam.LookAtPoint(raft + back * 12f + new Vector3(4, 5, 0), raft + Vector3.Up * 1f);
                break;
            }
            case "aerial":
                FlyCam.LookAtPoint(raft - fwd * 450f + right * 300f + Vector3.Up * 260f, raft + fwd * 300f);
                break;
            case "top":
                // Straight down from high above, north up: seams, coverage, the course's shape.
                FlyCam.LookAtPoint(raft + Vector3.Up * 2200f + new Vector3(0, 0, 0.5f), raft + new Vector3(0, 0, 0.6f));
                FlyCam.SetView(raft + Vector3.Up * 2200f, 0f, -89.9f);
                break;
            case "meander":
                FlyCam.LookAtPoint(raft - fwd * 60f + right * 70f + Vector3.Up * 45f, raft + fwd * 150f);
                break;
            case "water_close":
            {
                var from = OnWater(raft.X + right.X * 9f, raft.Z + right.Z * 9f, 1.2f);
                FlyCam.LookAtPoint(from, OnWater(raft.X + fwd.X * 14f - right.X * 4f, raft.Z + fwd.Z * 14f - right.Z * 4f, 0f));
                break;
            }
            case "lake":
            {
                if (FindFeature(FeatureKind.Lake) is not { } f) break;
                var p = At(f.Mid);
                float other = f.Side > 0 ? p.hl : p.hr, wide = f.Side > 0 ? p.hr : p.hl;
                double s = -f.Side * (other - 3f);   // standing just inside the water at the narrow side
                var from = Ground(p.x + p.rx * s, p.z + p.rz * s, 1.7f);
                double li = f.Side * (wide * 0.6f);
                FlyCam.LookAtPoint(from, OnWater(p.x + p.rx * li, p.z + p.rz * li, 2f));
                break;
            }
            case "riffle":
            {
                if (FindFeature(FeatureKind.Riffle) is not { } f) break;
                var p = At(f.S1 - 20.0); var q = At(f.Mid - 40.0);
                var from = Ground(p.x + p.rx * (p.hr + 4f), p.z + p.rz * (p.hr + 4f), 1.7f);
                FlyCam.LookAtPoint(from, OnWater(q.x, q.z, 0f));
                break;
            }
            case "foam_close":
            {
                if (FindFeature(FeatureKind.Riffle) is not { } f) break;
                var p = At(f.Mid);
                FlyCam.LookAtPoint(OnWater(p.x, p.z, 2.5f), OnWater(p.x + p.tx * 12.0, p.z + p.tz * 12.0, 0f));
                break;
            }
            case "rock":
            {
                if (FindFeature(FeatureKind.Riffle) is null) break;
                Rock best = default; float score = -1f;
                foreach (var r in World.Rocks)
                {
                    float sp = MathF.Sqrt(r.FlowX * r.FlowX + r.FlowZ * r.FlowZ), s = sp * (r.Top + 0.6f) * r.Radius;
                    if (s > score) { score = s; best = r; }
                }
                float spd = MathF.Max(0.01f, MathF.Sqrt(best.FlowX * best.FlowX + best.FlowZ * best.FlowZ));
                float dx = best.FlowX / spd, dz = best.FlowZ / spd;
                FlyCam.LookAtPoint(OnWater(best.X - dx * 4f - dz * 4.5f, best.Z - dz * 4f + dx * 4.5f, 3.2f),
                                   OnWater(best.X + dx * 0.8f, best.Z + dz * 0.8f, 0f));
                break;
            }
            case "confluence":
            {
                if (FindFeature(FeatureKind.Confluence) is not { } f) break;
                // From high on the far side, downstream of the junction, looking back at it.
                float hv = c.ValleyHeadingAt(f.Mid);
                double nx = MathF.Cos(hv) * f.Side, nz = -MathF.Sin(hv) * f.Side;   // towards the tributary
                var j = At(f.Mid);
                var from = OnWater(j.x - nx * 90.0 + j.tx * 110.0, j.z - nz * 90.0 + j.tz * 110.0, 45f);
                FlyCam.LookAtPoint(from, OnWater(j.x + nx * 15.0, j.z + nz * 15.0, 0f));
                break;
            }
            case "split":
            {
                if (FindFeature(FeatureKind.Split) is not { } f) break;
                var p = At(f.Mid);
                FlyCam.LookAtPoint(OnWater(p.x - p.tx * 120.0 + p.rx * 60.0, p.z - p.tz * 120.0 + p.rz * 60.0, 70f), OnWater(p.x, p.z, 0f));
                break;
            }
            default:
                GD.PushWarning($"Unknown shot '{name}'");
                break;
        }
        UseFlyCam(true);
    }

    async Task Capture()
    {
        int frames = _args.TryGetValue("frames", out var fr) ? int.Parse(fr) : 90;
        float seconds = _args.TryGetValue("seconds", out var se) ? float.Parse(se, CultureInfo.InvariantCulture) : 0f;
        var names = _args.TryGetValue("shots", out var many) ? many.Split(',') : new[] { _args.TryGetValue("shot", out var sh) ? sh : "cam" };
        string dir = _args.TryGetValue("outdir", out var od) ? od : ProjectSettings.GlobalizePath("res://captures");
        // Shots of features far from the raft load the river around the camera: keep it there.
        Streamer.Focus = () => FlyCam.Active ? FlyCam.GlobalPosition : Raft.GlobalPosition;

        if (seconds > 0f)
        {
            // Let physics run (raft drifting) faster than real time before framing the shots. More ticks per second
            // keep each physics step at 1/60 s: scaling time alone made 0.13 s steps and the raft went unstable.
            int ticks = Engine.PhysicsTicksPerSecond;
            Engine.PhysicsTicksPerSecond = ticks * 8;
            Engine.TimeScale = 8.0;
            Engine.MaxPhysicsStepsPerFrame = 64;
            double simulated = 0;
            while (simulated < seconds)
            {
                await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
                simulated += GetPhysicsProcessDeltaTime();
            }
            Engine.TimeScale = 1.0;
            Engine.PhysicsTicksPerSecond = ticks;
        }

        foreach (var entry in names)
        {
            // "name@time" picks a time-of-day preset for that shot.
            var parts = entry.Split('@');
            string name = parts[0];
            if (parts.Length > 1) Env.ApplyPreset(int.Parse(parts[1]) % EnvironmentRig.PresetNames.Length);
            if (name == "cam" && _args.TryGetValue("cam", out var c))
            {
                var p = Array.ConvertAll(c.Split(','), s => float.Parse(s, CultureInfo.InvariantCulture));
                FlyCam.SetView(new Vector3(p[0], p[1], p[2]), p[3], p[4]);
                UseFlyCam(true);
            }
            else ApplyShot(name);
            var cp = FlyCam.GlobalPosition;
            var here = World.At(cp.X, cp.Z);
            GD.Print($"Shot {name}: camera at {cp.X:F0}, {cp.Y:F1}, {cp.Z:F0} (segment {World.SegmentAt(cp.X, cp.Z)}, covered {World.Covers(cp.X, cp.Z)}, " +
                     $"ground {here.Height:F1}, water {here.Eta:F1}), {Streamer.DrawnCount} segments drawn");

            for (int i = 0; i < frames; i++) await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            string outPath = names.Length == 1 && _args.TryGetValue("out", out var o) ? o : System.IO.Path.Combine(dir, entry.Replace('@', '_') + ".png");
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(outPath)!);
            GetViewport().GetTexture().GetImage().SavePng(outPath);
            GD.Print($"Captured {outPath}");
        }
        GetTree().Quit();
    }

    // ------------------------------------------------------------------ drift test

    /// <summary>
    /// Drift unattended with streaming running: the track every 10 s, the streamer every 10 min (segment, drawn
    /// views, loaded data, worst frame and attach times, memory), then a steering check and a summary.
    /// </summary>
    async Task DriftTest(float seconds)
    {
        Engine.TimeScale = 1.0;
        Engine.MaxPhysicsStepsPerFrame = 1000;
        var start = Raft.GlobalPosition;
        float maxTilt = 0f, maxSpeed = 0f, t = 0f, nextLog = 0f, nextStream = 0f;
        double worstFrameMs = 0, windowFrameMs = 0, windowFrameSum = 0; int windowFrames = 0, windowSlow = 0, totalSlow = 0, totalFrames = 0;
        int firstSeg = World.SegmentAt(start.X, start.Z), lastSeg = firstSeg, seamsCrossed = 0, groundedAtSeam = 0;
        bool wasGrounded = false;
        int gc1 = GC.CollectionCount(1), gc2 = GC.CollectionCount(2);
        var gcPause = GC.GetTotalPauseDuration(); long gcAlloc = GC.GetTotalAllocatedBytes();
        long memStart = (long)OS.GetStaticMemoryUsage(), memPeak = memStart;
        var frame = Stopwatch.StartNew();
        GD.Print("DRIFT t, x, z, speed, water speed, depth, tilt");
        while (t < seconds)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
            double ms = frame.Elapsed.TotalMilliseconds; frame.Restart();
            t += (float)GetPhysicsProcessDeltaTime();
            if (t > 5f)
            {
                worstFrameMs = Math.Max(worstFrameMs, ms); windowFrameMs = Math.Max(windowFrameMs, ms); windowFrameSum += ms; windowFrames++;
                if (ms > 8.0) { windowSlow++; totalSlow++; }
            }
            var up = Raft.GlobalTransform.Basis.Y;
            maxTilt = MathF.Max(maxTilt, Mathf.RadToDeg(up.AngleTo(Vector3.Up)));
            maxSpeed = MathF.Max(maxSpeed, Raft.LinearVelocity.Length());
            var p = Raft.GlobalPosition;
            int seg = World.SegmentAt(p.X, p.Z);
            if (seg != lastSeg) { seamsCrossed++; lastSeg = seg; }
            if (Raft.Grounded && !wasGrounded)
            {
                double toCut = Math.Min(Math.Abs(World.PastCut(seg, p.X, p.Z)), Math.Abs(World.PastCut(seg + 1, p.X, p.Z)));
                GD.Print($"GROUND {t,6:F0} s at {p.X:F0}, {p.Z:F0}: segment {seg}, {toCut:F0} m from the nearest cut, depth {World.DepthAt(p.X, p.Z):F2} m");
                if (toCut < RiverWorld.Blend) groundedAtSeam++;
            }
            wasGrounded = Raft.Grounded;
            if (t >= nextLog)
            {
                var s = World.At(p.X, p.Z);
                GD.Print($"DRIFT {t,6:F0} {p.X,8:F1} {p.Z,8:F1} {Raft.LinearVelocity.Length(),5:F2} {MathF.Sqrt(s.VelX * s.VelX + s.VelZ * s.VelZ),5:F2} {s.Depth,5:F2} {Mathf.RadToDeg(up.AngleTo(Vector3.Up)),5:F1}");
                nextLog += 10f;
            }
            if (t >= nextStream)
            {
                long mem = (long)OS.GetStaticMemoryUsage(); memPeak = Math.Max(memPeak, mem);
                GD.Print($"STREAM {t / 60f,5:F0} min: segment {seg}, {Distance / 1000f:F1} km, {Streamer.DrawnCount} drawn, " +
                         $"{Count(World.Loaded)} in data, {Streamer.SegmentsGenerated} generated / {Streamer.SegmentsFromCache} cached / {Streamer.ViewsBuilt} views, " +
                         $"worst frame {windowFrameMs:F1} ms (mean {windowFrameSum / Math.Max(1, windowFrames):F2}, {windowSlow} of {windowFrames} over 8 ms), worst attach slice {Streamer.MaxAttachMs:F1} ms" +
                         $"{(Streamer.SlowestAttachMs > 1 ? $" (slowest node {Streamer.SlowestAttach}: {Streamer.SlowestAttachMs:F1} ms)" : "")}, " +
                         $"memory {mem / 1048576} MB static, {GC.GetTotalMemory(false) / 1048576} MB managed, " +
                         $"GC gen1 ×{GC.CollectionCount(1) - gc1} gen2 ×{GC.CollectionCount(2) - gc2}, " +
                         $"GC paused {(GC.GetTotalPauseDuration() - gcPause).TotalMilliseconds:F0} ms, allocated {(GC.GetTotalAllocatedBytes() - gcAlloc) / 1048576} MB");
                gc1 = GC.CollectionCount(1); gc2 = GC.CollectionCount(2);
                gcPause = GC.GetTotalPauseDuration(); gcAlloc = GC.GetTotalAllocatedBytes();
                totalFrames += windowFrames;
                windowFrameMs = 0; windowFrameSum = 0; windowFrames = 0; windowSlow = 0; Streamer.MaxAttachMs = 0; Streamer.SlowestAttachMs = 0;
                nextStream += 600f;
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
        long memEnd = (long)OS.GetStaticMemoryUsage();
        GD.Print($"STEER RESULT: oar turned the raft {turned:F0}° in 12 s (max {Mathf.RadToDeg(maxYawRate):F1}°/s), " +
                 $"{Mathf.RadToDeg(yawAfter):F1}°/s left 8 s after letting go; sculling: {relMax:F2} m/s through the water");
        GD.Print($"DRIFT RESULT: travelled {Distance / 1000f:F2} km down the valley ({start.DistanceTo(end):F0} m straight), " +
                 $"segments {firstSeg} → {lastSeg} ({seamsCrossed} seams crossed), max speed {maxSpeed:F2} m/s, max tilt {maxTilt:F1}°, " +
                 $"grounded {Raft.GroundedTime:F0} s ({groundedAtSeam} times within {RiverWorld.Blend:F0} m of a seam)");
        totalFrames += windowFrames;
        GD.Print($"STREAM RESULT: worst frame {worstFrameMs:F1} ms after the first 5 s, {totalSlow} of {totalFrames} frames over 8 ms, " +
                 $"memory {memStart / 1048576} → {memEnd / 1048576} MB (peak {memPeak / 1048576} MB)");
        GetTree().Quit();
    }

    static int Count(IEnumerable<int> e) { int n = 0; foreach (var _ in e) n++; return n; }
}
