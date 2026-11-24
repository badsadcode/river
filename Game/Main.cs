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
///   --start=NAME           start the raft just above the first rapid, falls, riffle, lake, split, confluence, or 400 m into the
///                          first gorge, braided, … reach (playtesting; works in the exported game too)
///   --no-trees             placeholder trees instead of the NoorForm packs
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
    public Raft.Portage Portage { get; private set; }
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
        // Where the raft starts: 300 m into the start segment, or just above the first feature asked for (--start=…).
        var c = World.Course;
        while (c.Cuts.Count <= StartSegment + 2) c.ExtendTo(c.Cuts[^1] + 1000.0);
        _startSigma = c.Cuts[StartSegment] + 300.0;
        int startK = StartSegment;
        if (_args.TryGetValue("start", out var startAt))
        {
            double? s = StartAbove(startAt);
            if (s is { } sigma)
            {
                _startSigma = sigma;
                while (startK + 1 < c.Cuts.Count && c.Cuts[startK + 1] <= sigma) startK++;
                GD.Print($"Starting above the first {startAt} at σ {sigma:F0} m, segment {startK}");
            }
            else GD.PushWarning($"--start={startAt}: nothing like that found");
        }
        // The floating origin starts where the raft does (D20): the scene is built around it.
        int i = c.RiverIndexAt(_startSigma);
        Origin.Set(c.X[i], c.Z[i]);
        var sw = Stopwatch.StartNew();
        Streamer.LoadSync(startK, Capturing ? 1 : 0);
        GD.Print($"River ready around segment {StartSegment} in {sw.ElapsedMilliseconds} ms " +
                 $"({Streamer.SegmentsGenerated} generated, {Streamer.SegmentsFromCache} from cache)");

        FlyCam = new FreeCamera { Name = "FlyCam", Far = 9000f, Near = 0.08f, Fov = 70f };
        AddChild(FlyCam);

        // The raft starts on the river's centre line, pointing downstream.
        float h = c.Heading[i];
        var start = Origin.ToScene(c.X[i], World.EtaAt(c.X[i], c.Z[i]) + 0.1f, c.Z[i]);
        Raft = new Raft.Raft { Name = "Raft" };
        AddChild(Raft);
        Raft.Setup(World, start, MathF.Atan2(-MathF.Sin(h), -MathF.Cos(h)));
        Player = new Player.PlayerController { Name = "Player" };
        Raft.AddChild(Player);
        Player.Setup(Raft);
        Portage = new Raft.Portage { Name = "Portage" };
        AddChild(Portage);
        Portage.Setup(Raft, Player, World);
        Streamer.Focus = () => Raft.GlobalPosition;
        Origin.Shifted += d =>
        {
            Raft.GlobalPosition -= d;
            FlyCam.GlobalPosition -= d;
        };

        if (_args.TryGetValue("time", out var t)) Env.ApplyPreset(int.Parse(t) % EnvironmentRig.PresetNames.Length);

        AddChild(new Hud { Name = "Hud" });
        _status.Visible = false;

        if (_args.ContainsKey("portage-test")) { Streamer.StartWorker(); _ = PortageTest(); return; }
        if (_args.ContainsKey("drift-test"))
        {
            // In a window (performance runs) look from the deck; headless there is nothing to look at.
            if (DisplayServer.GetName() != "headless") Player.Activate();
            Streamer.StartWorker(); _ = DriftTest(float.Parse(_args["drift-test"], CultureInfo.InvariantCulture)); return;
        }
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
        var (px, pz) = Origin.ToWorld(Raft.GlobalPosition);
        if (_sigmaHint == 0) _sigmaHint = (int)(_startSigma / Course.ValleyStep);
        var (sigma, _, idx) = World.Course.ProjectOnValley(px, pz, _sigmaHint);
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

    /// <summary>
    /// --start=NAME: valley distance just above the first feature (lake, split, riffle, confluence, rapid: 150 m before
    /// it) or reach (gorge, braided, …: 400 m into it) downstream of the usual start. Null if none within 80 km.
    /// </summary>
    double? StartAbove(string name)
    {
        var c = World.Course;
        double from = c.Cuts[StartSegment];
        c.ExtendTo(from + 80000.0);
        if (Enum.TryParse<FeatureKind>(name, true, out var fk))
            foreach (var f in c.Features)
                if (f.Kind == fk && f.S0 - 150.0 > from) return f.S0 - 150.0;
        if (Enum.TryParse<ReachKind>(name, true, out var rk))
            foreach (var r in c.Reaches)
                if (r.Kind == rk && r.S0 > from)
                {
                    // Not on a cut (the raft would start on a seam): nudge forward past it.
                    double s = r.S0 + 400.0;
                    foreach (double cut in c.Cuts) if (Math.Abs(s - cut) < 150.0) s = cut + 150.0;
                    return s;
                }
        return null;
    }

    /// <summary>First reach of a kind downstream of the start (loading the river around σ = its start + into).</summary>
    double? FindReach(ReachKind kind, double into)
    {
        var c = World.Course;
        c.ExtendTo(c.Cuts[StartSegment] + 80000.0);
        foreach (var r in c.Reaches)
            if (r.Kind == kind && r.S0 > c.Cuts[StartSegment])
            {
                double s = r.S0 + Math.Min(into, 0.5 * (r.S1 - r.S0));
                int k = 1; while (k + 1 < c.Cuts.Count && c.Cuts[k + 1] <= s) k++;
                Streamer.LoadSync(k, 1);
                GD.Print($"Shot: first {kind} reach at σ {r.S0:F0}–{r.S1:F0} m, segment {k}");
                return s;
            }
        return null;
    }

    /// <summary>First segment from the start whose spec passes the test (loaded, with its field).</summary>
    (ReachField f, SegmentSpec spec) FindSegment(Func<SegmentSpec, bool> test)
    {
        for (int k = StartSegment; k < StartSegment + 30; k++)
        {
            var spec = CourseSegments.Spec(World.Course, k);
            if (!test(spec)) continue;
            Streamer.LoadSync(k, 1);
            GD.Print($"Shot: segment {k}");
            return World.TryGet(k, out var f) ? (f, spec) : (null, null);
        }
        return (null, null);
    }

    Vector3 Ground(double x, double z, float up)
    {
        var s = World.At(x, z);
        return Origin.ToScene(x, MathF.Max(s.Height, s.Eta) + up, z);
    }

    /// <summary>
    /// --portage-test (use with --start=falls): 1) let the raft drift to the falls and check it is wrecked and rebuilt
    /// at the landing; 2) put the player ashore at the trail post, haul the raft out and push it (the player walking
    /// behind it) until it is afloat below the falls. Prints PORTAGE lines and a PORTAGE RESULT.
    /// </summary>
    async System.Threading.Tasks.Task PortageTest()
    {
        await ToSignal(GetTree().CreateTimer(1.0), SceneTreeTimer.SignalName.Timeout);
        if (World.Falls.Count == 0) { GD.Print("PORTAGE RESULT: no waterfall loaded"); GetTree().Quit(); return; }
        var fa = World.Falls[0];
        // 1. Drift over the edge (up to 10 minutes, the raft pushed downstream a little to speed it up).
        double t = 0;
        while (Portage.Wrecks == 0 && t < 600)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
            t += GetPhysicsProcessDeltaTime();
            Raft.ApplyCentralForce(new Vector3(fa.DirX, 0, fa.DirZ) * 3000f);
        }
        GD.Print($"PORTAGE: over the falls after {t:F0} s, wrecks {Portage.Wrecks}");
        await ToSignal(GetTree().CreateTimer(2.5), SceneTreeTimer.SignalName.Timeout);
        var (lx, lz) = Origin.ToWorld(Raft.GlobalPosition);
        double toLanding = Math.Sqrt((lx - fa.LandingX) * (lx - fa.LandingX) + (lz - fa.LandingZ) * (lz - fa.LandingZ));
        GD.Print($"PORTAGE: raft {toLanding:F1} m from the landing, anchored {Raft.AnchorDown}");
        // 2. Ashore, haul, push.
        Player.Reparent(this, true);
        Player.GlobalPosition = Origin.ToScene(fa.TrailX[0], fa.TrailH[0] + 1.2f, fa.TrailZ[0]);
        await ToSignal(GetTree().CreateTimer(1.0), SceneTreeTimer.SignalName.Timeout);
        Portage.BeginForTest(fa);
        Portage.ForcePush = true;
        double pushT = 0; float minDeckY = float.MaxValue;
        while (Portage.Relaunches == 0 && pushT < 900)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
            pushT += GetPhysicsProcessDeltaTime();
            // The player walks right behind the raft.
            var back = Raft.GlobalTransform.Basis.Z;   // bow is −Z
            var target = Raft.GlobalPosition + new Vector3(back.X, 0, back.Z).Normalized() * 4.2f;
            Player.GlobalPosition = new Vector3(target.X, Raft.GlobalPosition.Y + 0.5f, target.Z);
            minDeckY = MathF.Min(minDeckY, Raft.GlobalPosition.Y);
        }
        Portage.ForcePush = false;
        await ToSignal(GetTree().CreateTimer(3.0), SceneTreeTimer.SignalName.Timeout);
        var (px, pz) = Origin.ToWorld(Raft.GlobalPosition);
        double toPutIn = Math.Sqrt((px - fa.PutInX) * (px - fa.PutInX) + (pz - fa.PutInZ) * (pz - fa.PutInZ));
        float depth = World.DepthAt(px, pz);
        GD.Print($"PORTAGE RESULT: wrecked {Portage.Wrecks}×, rebuilt {toLanding:F1} m from the landing; portage took {pushT:F0} s of pushing, " +
                 $"relaunched {Portage.Relaunches}×, afloat {toPutIn:F1} m from the put-in in {depth:F1} m of water, raft speed {Raft.LinearVelocity.Length():F2} m/s");
        GetTree().Quit();
    }

    /// <summary>A scene point on the water at world (x, z), <paramref name="up"/> above it.</summary>
    Vector3 OnWater(double x, double z, float up) => Origin.ToScene(x, World.EtaAt(x, z) + up, z);

    /// <summary>The river at a scene position.</summary>
    RiverSample RiverAt(Vector3 scene) { var (x, z) = Origin.ToWorld(scene); return World.At(x, z); }
    int SegmentAtScene(Vector3 scene) { var (x, z) = Origin.ToWorld(scene); return World.SegmentAt(x, z); }

    public override void _PhysicsProcess(double delta)
    {
        // Keep the scene near the raft (between physics steps, before the raft integrates this one).
        if (Raft == null) return;
        var p = Raft.GlobalPosition;
        if (p.X * p.X + p.Z * p.Z > Origin.ShiftDistance * Origin.ShiftDistance) Origin.Shift(p);
    }

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
                // (raft is a scene position; OnWater takes world coordinates)
                var (rwx, rwz) = Origin.ToWorld(raft);
                var from = OnWater(rwx + right.X * 9f, rwz + right.Z * 9f, 1.2f);
                FlyCam.LookAtPoint(from, OnWater(rwx + fwd.X * 14f - right.X * 4f, rwz + fwd.Z * 14f - right.Z * 4f, 0f));
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
            case "falls_brink":
            {
                // Just above the edge, looking over it (as from a raft that came too close).
                if (FindFeature(FeatureKind.Falls) is not { } f) break;
                var up = At(f.Lip - 22.0); var down = At(f.Lip + 14.0);
                FlyCam.LookAtPoint(OnWater(up.x + up.rx * 6.0, up.z + up.rz * 6.0, 4.5f), OnWater(down.x, down.z, -1f));
                break;
            }
            case "falls_side":
            {
                // From the bank beside the falls, above the pool, looking across the face and down (Tom's view).
                if (FindFeature(FeatureKind.Falls) is not { } f) break;
                var l = At(f.Lip + 4.0); var pool = At(f.Lip + 18.0);
                double side = f.Side > 0 ? -1.0 : 1.0;   // the bank opposite the trail
                float hw = side > 0 ? l.hr : l.hl;
                FlyCam.LookAtPoint(OnWater(l.x + l.rx * side * (hw + 8.0), l.z + l.rz * side * (hw + 8.0), 14f), OnWater(pool.x, pool.z, 0f));
                break;
            }
            case "falls_map":
            {
                // Straight down over the lip from 90 m: the shape of the falls, the pool and the sheet.
                if (FindFeature(FeatureKind.Falls) is not { } f) break;
                var l = At(f.Lip);
                var above = OnWater(l.x, l.z, 90f);
                FlyCam.SetView(above, 0f, -89.9f);
                break;
            }
            case "falls_over":
            {
                // From above the pool, looking up at the falls and the river beyond (Tom's view).
                if (FindFeature(FeatureKind.Falls) is not { } f) break;
                var p = At(f.Lip + 22.0); var l = At(f.Lip - 2.0);
                FlyCam.LookAtPoint(OnWater(p.x, p.z, 11f), OnWater(l.x, l.z, -5f));
                break;
            }
            case "falls_foot":
            {
                // Low on the pool, close beside where the water lands, looking along the falls (Tom's third view).
                if (FindFeature(FeatureKind.Falls) is not { } f) break;
                var p = At(f.Lip + 9.0); var l = At(f.Lip + 2.0);
                FlyCam.LookAtPoint(OnWater(p.x - p.rx * 0.7f * p.hl, p.z - p.rz * 0.7f * p.hl, 2.2f), OnWater(l.x + l.rx * 0.4f * l.hr, l.z + l.rz * 0.4f * l.hr, 1f));
                break;
            }
            case "falls_flank":
            {
                // On the upper water near one end of the falls, looking across the curve and down into the pool.
                if (FindFeature(FeatureKind.Falls) is not { } f) break;
                var u = At(f.Lip - 5.0); var d = At(f.Lip + 10.0);
                FlyCam.LookAtPoint(OnWater(u.x + u.rx * 0.8f * u.hr, u.z + u.rz * 0.8f * u.hr, 3.5f), OnWater(d.x - d.rx * 0.2f * d.hl, d.z - d.rz * 0.2f * d.hl, -4f));
                break;
            }
            case "falls_edge":
            {
                // On the water a few metres above the edge, looking over it.
                if (FindFeature(FeatureKind.Falls) is not { } f) break;
                var u = At(f.Lip - 16.0); var d = At(f.Lip + 6.0);
                FlyCam.LookAtPoint(OnWater(u.x + u.rx * 0.35f * u.hr, u.z + u.rz * 0.35f * u.hr, 2.2f), OnWater(d.x, d.z, -5f));
                break;
            }
            case "falls_close":
            {
                // In the plunge pool, close in front of the falling water.
                if (FindFeature(FeatureKind.Falls) is not { } f) break;
                var p = At(f.Lip + 16.0); var lip = At(f.Lip - 6.0);
                FlyCam.LookAtPoint(OnWater(p.x + p.rx * 4.0, p.z + p.rz * 4.0, 1.6f), OnWater(lip.x, lip.z, -3f));
                break;
            }
            case "falls":
            case "falls_top":
            {
                if (FindFeature(FeatureKind.Falls) is not { } f) break;
                var lip = At(f.Lip); var below = At(f.Lip + 38.0);
                if (name == "falls")
                    // From the plunge pool, looking back up at the curtain.
                    FlyCam.LookAtPoint(OnWater(below.x - below.rx * 0.25f * below.hr, below.z - below.rz * 0.25f * below.hr, 5f), OnWater(lip.x, lip.z, -3f));
                else
                    FlyCam.LookAtPoint(OnWater(lip.x - lip.tx * 160.0 + lip.rx * 110.0, lip.z - lip.tz * 160.0 + lip.rz * 110.0, 80f), OnWater(lip.x, lip.z, -3f));
                break;
            }
            case "portage":
            {
                // The raft hauled out at the trail head, on its rollers, seen from the bank above.
                if (FindFeature(FeatureKind.Falls) is null || World.Falls.Count == 0) break;
                var fa = World.Falls[0];
                Portage.BeginForTest(fa);
                // Where the raft will sit: 4 m along the trail from its head.
                var p0 = Origin.ToScene(fa.TrailX[0], fa.TrailH[0], fa.TrailZ[0]);
                var p1 = Origin.ToScene(fa.TrailX[1], fa.TrailH[1], fa.TrailZ[1]);
                var raftAt = p0 + (p1 - p0).Normalized() * 4f;
                var along = (p1 - p0) with { Y = 0 };
                var across = new Vector3(-along.Z, 0, along.X).Normalized();
                FlyCam.LookAtPoint(raftAt + across * 8f - along.Normalized() * 5f + Vector3.Up * 4f, raftAt + Vector3.Up * 0.8f);
                break;
            }
            case "shift":
                // Floating-origin check: move the origin 360 m without moving the view (the camera is shifted with
                // the world); the capture should match the one before.
                Origin.Shift(raft + new Vector3(300f, 0f, 200f));
                break;
            case "willow":
            case "willow_far":
            {
                // The weeping willow nearest the raft, from 14 m (or 120 m) away, a little above.
                int sp = TreeLibrary.IndexOf(TreeLibrary.Kind.Willow);
                if (sp < 0 || Streamer.Trees?.Nearest(sp, raft) is not { } w) break;
                float dist = name == "willow" ? 14f : 160f;
                var dir = (raft - w) with { Y = 0 };
                dir = dir.LengthSquared() > 1f ? dir.Normalized() : Vector3.Forward;
                FlyCam.LookAtPoint(w + dir * dist + Vector3.Up * (name == "willow" ? 4f : 30f), w + Vector3.Up * 5f);
                break;
            }
            case "rapid":
            {
                if (FindFeature(FeatureKind.Rapid) is not { } f) break;
                // Low over the water just below the rapid's middle, looking up into it.
                var p = At(f.Mid + 45.0); var q = At(f.Mid - 40.0);
                FlyCam.LookAtPoint(OnWater(p.x + p.rx * 0.3f * p.hr, p.z + p.rz * 0.3f * p.hr, 3.5f), OnWater(q.x, q.z, 0f));
                break;
            }
            case "rapid_top":
            {
                if (FindFeature(FeatureKind.Rapid) is not { } f) break;
                var p = At(f.S0 - 40.0); var q = At(f.Mid);
                FlyCam.LookAtPoint(OnWater(p.x - p.tx * 30.0 + p.rx * 25.0, p.z - p.tz * 30.0 + p.rz * 25.0, 35f), OnWater(q.x, q.z, 0f));
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
            case "gorge":
            {
                if (FindReach(ReachKind.Gorge, 700.0) is not { } s) break;
                var p = At(s); var q = At(s + 160.0);
                FlyCam.LookAtPoint(OnWater(p.x, p.z, 6f), OnWater(q.x, q.z, 4f));
                break;
            }
            case "gorge_top":
            {
                if (FindReach(ReachKind.Gorge, 700.0) is not { } s) break;
                var p = At(s);
                FlyCam.LookAtPoint(OnWater(p.x - p.tx * 140.0 + p.rx * 90.0, p.z - p.tz * 140.0 + p.rz * 90.0, 70f), OnWater(p.x, p.z, 0f));
                break;
            }
            case "braided":
            {
                if (FindReach(ReachKind.Braided, 900.0) is not { } s) break;
                var p = At(s);
                FlyCam.LookAtPoint(OnWater(p.x - p.tx * 220.0 + p.rx * 130.0, p.z - p.tz * 220.0 + p.rz * 130.0, 90f), OnWater(p.x + p.tx * 60.0, p.z + p.tz * 60.0, 0f));
                break;
            }
            case "side_channel":
            case "oxbow":
            {
                bool side = name == "side_channel";
                var (f, spec) = FindSegment(sp => side ? sp.SideChannels.Count > 0 : sp.StillWaters.Count > 0);
                if (f == null) break;
                var l = side ? spec.SideChannels[0] : spec.StillWaters[0].Line;
                int m = l.Count / 2;
                var (mx, mz) = f.Frame.ToWorld(l.X[m], l.Z[m]);
                var (tx, tz) = f.Frame.ToWorld(l.X[m] + l.TanX[m], l.Z[m] + l.TanZ[m]);
                double dx = tx - mx, dz = tz - mz;
                float back = side ? 160f : 110f, up = side ? 65f : 55f;
                FlyCam.LookAtPoint(OnWater(mx - dx * back - dz * 60.0, mz - dz * back + dx * 60.0, up), OnWater(mx, mz, 0f));
                break;
            }
            case "snag":
            {
                var (f, _) = FindSegment(sp => sp.Snags.Count > 0);
                if (f == null) break;
                var sn = f.Snags[0];
                foreach (var o in f.Snags) if (o.Length > sn.Length) sn = o;
                var (rx, rz) = f.Frame.ToWorld(sn.X + sn.DirX * sn.Length * 0.6f, sn.Z + sn.DirZ * sn.Length * 0.6f);
                var (bx, bz) = f.Frame.ToWorld(sn.X - sn.DirZ * 12f + sn.DirX * 4f, sn.Z + sn.DirX * 12f + sn.DirZ * 4f);
                FlyCam.LookAtPoint(OnWater(bx, bz, 3f), OnWater(rx, rz, 0f));
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
            var here = RiverAt(cp);
            GD.Print($"Shot {name}: camera at {cp.X:F0}, {cp.Y:F1}, {cp.Z:F0} (segment {SegmentAtScene(cp)}, covered {World.Covers(Origin.ToWorld(cp).x, Origin.ToWorld(cp).z)}, " +
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
        var startWorld = Origin.ToWorld(start);
        float maxTilt = 0f, maxSpeed = 0f, t = 0f, nextLog = 0f, nextStream = 0f;
        double worstFrameMs = 0, windowFrameMs = 0, windowFrameSum = 0; int windowFrames = 0, windowSlow = 0, totalSlow = 0, totalFrames = 0;
        int firstSeg = SegmentAtScene(start), lastSeg = firstSeg, seamsCrossed = 0, groundedAtSeam = 0;
        bool wasGrounded = false;
        int gc1 = GC.CollectionCount(1), gc2 = GC.CollectionCount(2);
        var gcPause = GC.GetTotalPauseDuration(); long gcAlloc = GC.GetTotalAllocatedBytes();
        long memStart = (long)OS.GetStaticMemoryUsage(), memPeak = memStart;
        double fpsSum = 0, fpsMin = double.MaxValue; int fpsN = 0;
        // Floating origin check: every 30 s shift the world by however far the raft is from the scene origin (plus
        // the automatic shifts past 1 km). The water at a fixed world point must not change across a shift.
        int shifts = 0, autoShifts = 0; float worstShiftJump = 0f, nextShift = 30f;
        void OnShift(Vector3 d) => autoShifts++;
        Origin.Shifted += OnShift;
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
                fpsSum += Engine.GetFramesPerSecond(); fpsN++; fpsMin = Math.Min(fpsMin, Engine.GetFramesPerSecond());
            }
            if (t >= nextShift)
            {
                nextShift += 30f;
                var probe = Raft.GlobalPosition + new Vector3(3f, 0f, 2f);
                float before = Raft.WaterHeight(probe.X, probe.Z);
                var d = Origin.Shift(Raft.GlobalPosition + new Vector3(37f, 0f, -21f));
                float after = Raft.WaterHeight(probe.X - d.X, probe.Z - d.Z);
                worstShiftJump = MathF.Max(worstShiftJump, MathF.Abs(after - before));
                shifts++;
            }
            var up = Raft.GlobalTransform.Basis.Y;
            maxTilt = MathF.Max(maxTilt, Mathf.RadToDeg(up.AngleTo(Vector3.Up)));
            maxSpeed = MathF.Max(maxSpeed, Raft.LinearVelocity.Length());
            var p = Raft.GlobalPosition;
            var (pwx, pwz) = Origin.ToWorld(p);
            int seg = World.SegmentAt(pwx, pwz);
            if (seg != lastSeg) { seamsCrossed++; lastSeg = seg; }
            if (Raft.Grounded && !wasGrounded)
            {
                double toCut = Math.Min(Math.Abs(World.PastCut(seg, pwx, pwz)), Math.Abs(World.PastCut(seg + 1, pwx, pwz)));
                GD.Print($"GROUND {t,6:F0} s at {pwx:F0}, {pwz:F0}: segment {seg}, {toCut:F0} m from the nearest cut, depth {World.DepthAt(pwx, pwz):F2} m");
                if (toCut < RiverWorld.Blend) groundedAtSeam++;
            }
            wasGrounded = Raft.Grounded;
            if (t >= nextLog)
            {
                var s = World.At(pwx, pwz);
                GD.Print($"DRIFT {t,6:F0} {pwx,8:F1} {pwz,8:F1} {Raft.LinearVelocity.Length(),5:F2} {MathF.Sqrt(s.VelX * s.VelX + s.VelZ * s.VelZ),5:F2} {s.Depth,5:F2} {Mathf.RadToDeg(up.AngleTo(Vector3.Up)),5:F1}");
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
        var endWorld = Origin.ToWorld(Raft.GlobalPosition);
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
        GD.Print($"DRIFT RESULT: travelled {Distance / 1000f:F2} km down the valley ({Math.Sqrt((endWorld.x - startWorld.x) * (endWorld.x - startWorld.x) + (endWorld.z - startWorld.z) * (endWorld.z - startWorld.z)):F0} m straight), " +
                 $"segments {firstSeg} → {lastSeg} ({seamsCrossed} seams crossed), max speed {maxSpeed:F2} m/s, max tilt {maxTilt:F1}°, " +
                 $"grounded {Raft.GroundedTime:F0} s ({groundedAtSeam} times within {RiverWorld.Blend:F0} m of a seam)");
        totalFrames += windowFrames;
        Origin.Shifted -= OnShift;
        GD.Print($"SHIFT RESULT: {shifts} forced origin shifts ({autoShifts - shifts} automatic), water height jump at a fixed world point " +
                 $"{worstShiftJump * 1000f:F2} mm; raft {Raft.GlobalPosition.Length():F0} m from the scene origin, origin at {Origin.X:F0}, {Origin.Z:F0}");
        GD.Print($"STREAM RESULT: rendering {fpsSum / Math.Max(1, fpsN):F0} fps mean, {(fpsN > 0 ? fpsMin : 0):F0} lowest; worst frame {worstFrameMs:F1} ms after the first 5 s, {totalSlow} of {totalFrames} frames over 8 ms, " +
                 $"memory {memStart / 1048576} → {memEnd / 1048576} MB (peak {memPeak / 1048576} MB)");
        GetTree().Quit();
    }

    static int Count(IEnumerable<int> e) { int n = 0; foreach (var _ in e) n++; return n; }
}
