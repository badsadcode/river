using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Godot;
using River.Core;

namespace River.World;

/// <summary>
/// Streams the endless river around the raft (docs/STREAMING.md §5).
///
/// Windows, by segment index around the focus segment f (the raft's): segments f−1 … f+2 are drawn; segments
/// f−2 … f+4 are held as data (so the raft's physics can blend across seams, and a segment is only drawn once both
/// neighbours' data exist: its seam bake is then final).
///
/// Threads: a worker owns its own Course and RiverWorld (deterministic: they match the main thread's), loads each
/// segment from the disk cache or generates it, bakes the seams and builds the segment's whole node tree off the
/// main thread. The main thread adds finished data to <see cref="World"/> (physics, queries), attaches view children
/// within a per-frame time budget, unloads what falls out of the window, and switches cut clipping on as neighbours
/// arrive. <see cref="LoadSync"/> does the same synchronously (start-up, screenshots).
/// </summary>
public partial class WorldStreamer : Node3D
{
    public const int ViewsBehind = 1, ViewsAhead = 2, DataBehind = 2, DataAhead = 4;

    public RiverWorld World { get; private set; }
    public EnvironmentRig Env;
    public Func<Vector3> Focus;
    public readonly List<ShaderMaterial> WaterMaterials = new();
    public Action<ShaderMaterial> OnWaterMaterial;      // e.g. debug overrides
    public float AttachBudgetMs = 2f;

    // Telemetry for the drift test.
    public double MaxAttachMs;
    public int SegmentsGenerated, SegmentsFromCache, ViewsBuilt;
    public double SlowestAttachMs; public string SlowestAttach = "";

    CourseSettings _cs;
    string _cacheDir, _cacheKey;

    // Worker side (also used by LoadSync before the worker starts).
    Course _wCourse;
    RiverWorld _wWorld;
    readonly Dictionary<int, ReachField> _wRaw = new();
    readonly HashSet<int> _wBuilt = new();
    Thread _thread;
    volatile bool _run;
    volatile int _focusK = 1;
    readonly ConcurrentQueue<(int k, ReachField raw)> _rawReady = new();
    readonly ConcurrentQueue<SegmentView> _viewReady = new();
    readonly ConcurrentQueue<int> _unbuilt = new();

    // Main side.
    readonly Dictionary<int, SegmentView> _views = new();
    readonly List<SegmentView> _attaching = new();

    public void Setup(CourseSettings cs, string cacheDir, string cacheKey, EnvironmentRig env)
    {
        _cs = cs; _cacheDir = cacheDir; _cacheKey = cacheKey; Env = env;
        World = new RiverWorld(new Course(cs));
        _wCourse = new Course(cs);
        _wWorld = new RiverWorld(_wCourse);
    }

    /// <summary>Synchronously load data around segment k and draw segments k−viewRadius … k+viewRadius (before the worker runs).</summary>
    public void LoadSync(int k, int viewRadius)
    {
        if (_run) throw new InvalidOperationException("LoadSync with the worker running");
        for (int j = Math.Max(1, k - viewRadius - 1); j <= k + viewRadius + 1; j++) EnsureRaw(j);
        DrainRaw();
        for (int j = Math.Max(1, k - viewRadius); j <= k + viewRadius; j++)
        {
            if (_wBuilt.Contains(j)) continue;
            var v = BuildView(j);
            _wBuilt.Add(j);
            _viewReady.Enqueue(v);
        }
        DrainViews(k);
        foreach (var v in _attaching) while (v.Pending.TryDequeue(out var pc)) pc.parent.AddChild(pc.child);
        _attaching.Clear();
        UpdateClips();
        _focusK = k;
    }

    public void StartWorker()
    {
        Par.LeaveFree(2);   // generation in the background leaves the main thread (and the render thread) a core each
        _run = true;
        _thread = new Thread(WorkerLoop) { IsBackground = true, Name = "RiverStreamer", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    public override void _ExitTree()
    {
        _run = false;
        _thread?.Join(5000);
    }

    // ================================================================ worker

    void WorkerLoop()
    {
        while (_run)
        {
            try
            {
                while (_unbuilt.TryDequeue(out int u)) _wBuilt.Remove(u);
                int f = _focusK;
                if (WorkStep(f)) continue;
                // Idle: forget data far outside the window.
                var drop = new List<int>();
                foreach (int k in _wRaw.Keys) if (k < f - DataBehind - 1 || k > f + DataAhead + 1) drop.Add(k);
                foreach (int k in drop) { _wRaw.Remove(k); _wWorld.Remove(k); _wBuilt.Remove(k); }
                Thread.Sleep(50);
            }
            catch (Exception e) { GD.PushError($"Streamer worker: {e}"); Thread.Sleep(500); }
        }
    }

    /// <summary>One unit of work in priority order: data near the raft first, then views. False if nothing to do.</summary>
    bool WorkStep(int f)
    {
        foreach (int k in new[] { f, f + 1, f - 1, f + 2, f + 3, f - 2, f + 4 })
            if (k >= 1 && !_wRaw.ContainsKey(k)) { EnsureRaw(k); return true; }
        foreach (int k in new[] { f, f + 1, f - 1, f + 2 })
        {
            if (k < 1 || _wBuilt.Contains(k) || !_wRaw.ContainsKey(k)) continue;
            if (!_wRaw.ContainsKey(k + 1) || (k > 1 && !_wRaw.ContainsKey(k - 1))) continue;   // bake needs both neighbours
            var v = BuildView(k);
            _wBuilt.Add(k);
            _viewReady.Enqueue(v);
            return true;
        }
        return false;
    }

    void EnsureRaw(int k)
    {
        if (_wRaw.ContainsKey(k)) return;
        while (_wCourse.Cuts.Count <= k + 2) _wCourse.ExtendTo(_wCourse.Cuts[^1] + 1000.0);
        string path = $"{_cacheDir}/seg_{_cs.Seed}_{k}.bin";
        var sw = Stopwatch.StartNew();
        var raw = ReachCache.TryLoad(path, _cacheKey);
        if (raw != null) SegmentsFromCache++;
        else
        {
            raw = ReachGenerator.Generate(CourseSegments.Spec(_wCourse, k));
            SegmentsGenerated++;
            try { ReachCache.Save(raw, path, _cacheKey); } catch (Exception e) { GD.PushWarning($"Segment cache not saved: {e.Message}"); }
            // Generation leaves ~0.5 GB of short-lived arrays. The game runs the collector in low-latency mode (no
            // blocking full collections), so ask for a background one now: frees it without pausing the frame.
            if (_run) GC.Collect(2, GCCollectionMode.Optimized, blocking: false);
        }
        GD.Print($"Segment {k} data in {sw.ElapsedMilliseconds} ms");
        _wRaw[k] = raw;
        _wWorld.Add(k, raw);
        _rawReady.Enqueue((k, raw));
    }

    SegmentView BuildView(int k)
    {
        var sw = Stopwatch.StartNew();
        _wRaw.TryGetValue(k, out var raw);
        var baked = SegmentBake.Bake(raw, k, _wWorld);
        long bakeMs = sw.ElapsedMilliseconds;
        var v = SegmentView.Build(k, baked, _wWorld, Env);
        ViewsBuilt++;
        GD.Print($"Segment {k} view in {sw.ElapsedMilliseconds} ms (bake {bakeMs} ms), {v.Pending.Count} nodes to attach");
        return v;
    }

    // ================================================================ main thread

    public override void _Process(double delta)
    {
        if (World == null) return;
        if (Focus != null)
        {
            var p = Focus();
            _focusK = Math.Max(1, World.SegmentAt(p.X, p.Z));
        }
        int f = _focusK;
        DrainRaw();
        DrainViews(f);

        // Attach view children within the frame budget.
        var sw = Stopwatch.StartNew();
        while (_attaching.Count > 0 && sw.Elapsed.TotalMilliseconds < AttachBudgetMs)
        {
            var v = _attaching[0];
            if (v.Pending.TryDequeue(out var pc))
            {
                double before = sw.Elapsed.TotalMilliseconds;
                pc.parent.AddChild(pc.child);
                double one = sw.Elapsed.TotalMilliseconds - before;
                if (one > SlowestAttachMs) { SlowestAttachMs = one; SlowestAttach = $"{pc.child.Name} ({pc.child.GetClass()})"; }
            }
            else { _attaching.RemoveAt(0); UpdateClips(); }
        }
        MaxAttachMs = Math.Max(MaxAttachMs, sw.Elapsed.TotalMilliseconds);

        // Unload views and data out of their windows (reused list: no garbage per frame).
        _scratch.Clear();
        foreach (var (k, v) in _views) if (k < f - ViewsBehind || k > f + ViewsAhead) _scratch.Add(k);
        foreach (int k in _scratch) DropView(k);
        if (_scratch.Count > 0) UpdateClips();
        _scratch.Clear();
        World.LoadedInto(_scratch);
        for (int i = _scratch.Count - 1; i >= 0; i--) if (_scratch[i] >= f - DataBehind && _scratch[i] <= f + DataAhead) _scratch.RemoveAt(i);
        foreach (int k in _scratch) World.Remove(k);
    }

    readonly List<int> _scratch = new();

    void DrainRaw()
    {
        while (_rawReady.TryDequeue(out var r))
        {
            var c = World.Course;
            while (c.Cuts.Count <= r.k + 2) c.ExtendTo(c.Cuts[^1] + 1000.0);
            World.Add(r.k, r.raw);
        }
    }

    void DrainViews(int f)
    {
        while (_viewReady.TryDequeue(out var v))
        {
            if (v.K < f - ViewsBehind || v.K > f + ViewsAhead || _views.ContainsKey(v.K))
            {
                Env.ForgetMist(v.Mist);
                v.Root.QueueFree();
                if (!_views.ContainsKey(v.K)) _unbuilt.Enqueue(v.K);
                continue;
            }
            _views[v.K] = v;
            AddChild(v.Root);
            _attaching.Add(v);
            WaterMaterials.Add(v.WaterMat);
            Env.Waters.Add(v.WaterMat);
            OnWaterMaterial?.Invoke(v.WaterMat);
        }
    }

    void DropView(int k)
    {
        if (!_views.Remove(k, out var v)) return;
        _attaching.Remove(v);
        WaterMaterials.Remove(v.WaterMat);
        Env.Waters.Remove(v.WaterMat);
        Env.ForgetMist(v.Mist);
        v.Root.QueueFree();
        _unbuilt.Enqueue(k);
    }

    /// <summary>Clip each drawn segment at a cut only where the neighbour across it is fully attached.</summary>
    void UpdateClips()
    {
        foreach (var (k, v) in _views)
        {
            bool up = _views.TryGetValue(k - 1, out var a) && a.Attached;
            bool down = _views.TryGetValue(k + 1, out var b) && b.Attached;
            v.SetClips(World, up && v.Attached, down && v.Attached);
        }
    }

    public int DrawnCount => _views.Count;
    public bool IsDrawn(int k) => _views.TryGetValue(k, out var v) && v.Attached;
}
