using System;
using System.IO;
using System.IO.Compression;
using River.Core;

// Headless checks of the river generator. Exit code 0 = all pass.
string mapPath = null, flowPath = null, coursePath = null, worldPath = null;
for (int a = 0; a < args.Length; a++)
{
    if (args[a] == "--map" && a + 1 < args.Length) mapPath = args[++a];
    else if (args[a] == "--flow" && a + 1 < args.Length) flowPath = args[++a];
    else if (args[a] == "--course" && a + 1 < args.Length) coursePath = args[++a];
    else if (args[a] == "--world" && a + 1 < args.Length) worldPath = args[++a];
}

int failures = 0;
void Check(bool ok, string what) { Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}"); if (!ok) failures++; }

// ---------------------------------------------------------------- sandbox settings (fast)
Console.WriteLine("Sandbox settings:");
{
    var w = new System.Collections.Generic.List<string>();
    var hod = SandboxSettings.Parse(SandboxSettings.FromPreset("heart_of_darkness").ToJson(), w);
    Check(hod.Mode.Permadeath && hod.Mind.Darkness == "abyss" && w.Count == 0, "preset round-trips through JSON without warnings");

    w.Clear();
    var part = SandboxSettings.Parse("{ \"Preset\": \"drifter\", \"Survival\": { \"Hunger\": 2 } }", w);
    Check(part.Survival.Hunger == 2f && part.Survival.Thirst == 0.5f && w.Count == 0, "missing keys come from the file's preset");

    w.Clear();
    var bad = SandboxSettings.Parse("{ \"World\": { \"DistanceScale\": 7 }, \"Mind\": { \"Darkness\": \"pitch\" }, \"Foo\": 1 }", w);
    Check(bad.World.DistanceScale == 1f && bad.Mind.Darkness == "grim" && w.Count == 3, $"bad values clamped/replaced with warnings ({w.Count})");

    w.Clear();
    var broken = SandboxSettings.Parse("{ not json", w);
    Check(broken.Preset == "explorer" && w.Count == 1, "broken file falls back to explorer");

    w.Clear();
    var lenient = SandboxSettings.Parse("{ // comment\n \"Seed\": 42, \"Time\": { \"DayLengthMinutes\": \"90\", }, }", w);
    Check(lenient.Seed == 42 && lenient.Time.DayLengthMinutes == 90f && w.Count == 0, "comments, trailing commas and quoted numbers are accepted");
}
// ---------------------------------------------------------------- day cycle (fast)
Console.WriteLine("Day cycle:");
{
    float lat = 52f;
    float Elev(float doy, float h) => Solar.ElevationDeg(Solar.SunDirection(lat, doy, h).up);
    float sunrise = -1f;
    for (float h = 3f; h < 9f; h += 0.01f) if (Elev(79f, h) >= 0f) { sunrise = h; break; }
    Check(sunrise > 5.6f && sunrise < 6.4f, $"equinox sunrise near 06:00 at 52°N ({sunrise:F2} h)");
    Check(MathF.Abs(Elev(79f, 12f) - 38f) < 1.5f, $"equinox noon elevation ≈ 38° ({Elev(79f, 12f):F1}°)");
    Check(MathF.Abs(Elev(172f, 12f) - 61.4f) < 1.5f && MathF.Abs(Elev(355f, 12f) - 14.6f) < 1.5f,
          $"summer/winter noon elevations ≈ 61°/15° ({Elev(172f, 12f):F1}°/{Elev(355f, 12f):F1}°)");
    var noon = Solar.SunDirection(lat, 79f, 12f); var aft = Solar.SunDirection(lat, 79f, 16f);
    Check(noon.north < 0f && aft.east < 0f, "the sun is south at noon and west in the afternoon");

    var clock = new GameClock(60f, 20, "spring", 6f);
    clock.Advance(3600.0);
    Check(clock.Day == 2 && MathF.Abs(clock.Hour - 6f) < 0.01f, $"one real hour = one day at DayLengthMinutes 60 (day {clock.Day} {clock.Clock})");
    clock.TotalDays = 20.5;
    Check(clock.Season == "summer" && clock.DayOfSeason == 1, $"day 21 is the first day of summer ({clock.Season} {clock.DayOfSeason})");
    clock.SetHour(5f);
    Check(clock.Day == 22 && MathF.Abs(clock.Hour - 5f) < 0.01f, "setting the hour only moves time forward");

    float worst = 0f; var prev = Daylight.Evaluate(-20f, 30f, true, 0f);
    for (float e = -20f; e <= 90f; e += 0.1f)
    {
        var st = Daylight.Evaluate(e, 30f, true, 0f);
        worst = MathF.Max(worst, MathF.Abs(st.SunEnergy - prev.SunEnergy));
        worst = MathF.Max(worst, MathF.Abs(st.SkyHorizon.R - prev.SkyHorizon.R));
        worst = MathF.Max(worst, MathF.Abs(st.Exposure - prev.Exposure));
        worst = MathF.Max(worst, MathF.Abs(st.MoonEnergy - prev.MoonEnergy) * 5f);
        prev = st;
    }
    Check(worst < 0.08f, $"lighting changes smoothly with the sun (largest step {worst:F3} per 0.1°)");
    Check(Daylight.Evaluate(1f, 0f, true, 0f).Mist > 3f * Daylight.Evaluate(40f, 0f, false, 0f).Mist + 0.01f, "mist is thick at sunrise and gone by midday");
}

// ---------------------------------------------------------------- frames (fast)
Console.WriteLine("Frames:");
{
    var fr = new Frame(123456.75, -98765.5, 0.7f);
    var (wx, wz) = fr.ToWorld(310.25f, 1820.5f);
    var (lx, lz) = fr.ToLocal(wx, wz);
    Check(MathF.Abs(lx - 310.25f) < 1e-3f && MathF.Abs(lz - 1820.5f) < 1e-3f, $"grid → world → grid round-trips far from the origin ({lx:F4}, {lz:F4})");
    var (ax, az) = fr.ToWorld(0f, 1f);
    Check(Math.Abs(ax - fr.OriginX - MathF.Sin(0.7f)) < 1e-5 && Math.Abs(az - fr.OriginZ - MathF.Cos(0.7f)) < 1e-5, "the grid's +z points along the heading");
}

// ---------------------------------------------------------------- the endless course (fast, 50 km)
Console.WriteLine("Course:");
{
    var cs = new CourseSettings();
    var course = new Course(cs);
    var sw = System.Diagnostics.Stopwatch.StartNew();
    course.ExtendTo(50000.0);
    long ms = sw.ElapsedMilliseconds;
    float bearing = cs.BearingDeg * MathF.PI / 180f;

    // Valley: never more than MaxSwing off the bearing, always progressing along it, turning gently.
    float maxOff = 0f, minHead = 9f, maxHead = -9f, tightest = float.MaxValue, tightestNormal = float.MaxValue;
    bool progresses = true;
    double bx = MathF.Sin(bearing), bz = MathF.Cos(bearing);
    for (int i = 1; i < course.VHeading.Count; i++)
    {
        float h = course.VHeading[i];
        maxOff = MathF.Max(maxOff, MathF.Abs(h - bearing)); minHead = MathF.Min(minHead, h); maxHead = MathF.Max(maxHead, h);
        double prog = (course.VX[i] - course.VX[i - 1]) * bx + (course.VZ[i] - course.VZ[i - 1]) * bz;
        if (prog < -1e-9) progresses = false;
        float dh = MathF.Abs(h - course.VHeading[i - 1]);
        if (dh < 1e-7f) continue;
        float r = Course.ValleyStep / dh;
        tightest = MathF.Min(tightest, r);
        double sig = i * Course.ValleyStep;
        bool nearConfluence = false;
        foreach (var f in course.Features) if (f.Kind == FeatureKind.Confluence && sig - f.Mid > -10 && sig - f.Mid < 1500) nearConfluence = true;
        if (!nearConfluence) tightestNormal = MathF.Min(tightestNormal, r);
    }
    // River: level only goes down; meander belt stays near the valley axis; discharge grows.
    bool etaDown = true; float maxOffset = 0f;
    for (int i = 1; i < course.Count; i++)
    {
        if (course.Eta[i] > course.Eta[i - 1] + 1e-6f) etaDown = false;
        maxOffset = MathF.Max(maxOffset, MathF.Abs(course.Offset[i]));
    }
    // Cuts: segment lengths, clear of features, crossed once.
    bool cutsOk = true, lengthsOk = true; int cutsChecked = 0;
    for (int k = 1; k < course.Cuts.Count && course.Cuts[k] < 48000.0; k++)
    {
        double len = course.Cuts[k] - course.Cuts[k - 1];
        if (len < cs.MinSegment - 1 || len > cs.MaxSegment + 1) lengthsOk = false;
        foreach (var f in course.Features)
            if (course.Cuts[k] > f.S0 - cs.CutClearance && course.Cuts[k] < f.S1 + cs.CutClearance) cutsOk = false;
        if (!course.CrossesOnce(course.Cuts[k], cs.CutWindow)) cutsOk = false;
        cutsChecked++;
    }
    var counts = new int[4];
    foreach (var f in course.Features) if (f.S1 < 50000.0) counts[(int)f.Kind]++;
    int end = course.Count - 1; while (end > 0 && course.Sigma[end] > 50000.0) end--;
    double riverKm = end * Centerline.Step / 1000.0;
    Console.WriteLine($"  {riverKm:F1} km of river over 50 km of valley (sinuosity {riverKm / 50.0:F2}) in {ms} ms; " +
                      $"{counts[0]} lakes, {counts[1]} splits, {counts[2]} riffles, {counts[3]} confluences, {cutsChecked} cuts");
    Console.WriteLine($"  valley heading {minHead * 180 / MathF.PI:F0}°…{maxHead * 180 / MathF.PI:F0}° (0 = south, + = east); " +
                      $"tightest turn {tightestNormal:F0} m ({tightest:F0} m below confluences); " +
                      $"level {course.Eta[0]:F1} → {course.Eta[end]:F1} m; discharge {course.Discharge[0]:F0} → {course.Discharge[end]:F0} m³/s");
    Check(maxOff <= cs.MaxSwingDeg * MathF.PI / 180f + 1e-4f && progresses, "the valley stays within ±90° of south and never heads back");
    Check(tightestNormal >= cs.MinTurnRadius * 0.99f && tightest >= cs.ConfluenceTurnRadius * 0.99f, "valley turns are sweeping (radius ≥ 3 km, ≥ 1.5 km below confluences)");
    Check(maxHead - minHead > 40f * MathF.PI / 180f, "the course really changes direction over 50 km");
    Check(etaDown, "the water level only goes down");
    Check(maxOffset < 450f, $"the meander belt stays within the corridor (max {maxOffset:F0} m from the valley axis)");
    Check(lengthsOk && cutsOk && cutsChecked >= 12, "segments are 1.5–4 km, cut in plain stretches the river crosses once");
    Check(counts[0] + counts[1] + counts[2] + counts[3] >= 8, "features along the way");

    // Determinism: built in uneven steps, the same course.
    var stepped = new Course(cs);
    foreach (double to in new[] { 3000.0, 3100.0, 11000.0, 27000.0, 50000.0 }) stepped.ExtendTo(to);
    bool sameCourse = stepped.Count >= end && stepped.Cuts.Count >= cutsChecked;
    for (int i = 0; i <= end && sameCourse; i++) sameCourse = stepped.X[i] == course.X[i] && stepped.Z[i] == course.Z[i] && stepped.Eta[i] == course.Eta[i];
    for (int k = 0; k < cutsChecked && sameCourse; k++) sameCourse = stepped.Cuts[k] == course.Cuts[k];
    Check(sameCourse, "built in steps or at once, the course is identical");

    if (coursePath != null)
    {
        CourseMap.Write(course, 0.0, 50000.0, 20.0, coursePath);
        string zoom = System.IO.Path.ChangeExtension(coursePath, null) + "_zoom.png";
        CourseMap.Write(course, 0.0, 8000.0, 5.0, zoom);
        Console.WriteLine($"  course maps: {coursePath}, {zoom}");
    }
}

if (Array.IndexOf(args, "--sandbox-only") >= 0) { Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILED"); return failures == 0 ? 0 : 1; }

// ---------------------------------------------------------------- segments of the endless course (≈ 15 s)
Console.WriteLine("Segments:");
{
    var course = new Course(new CourseSettings());
    var world = new RiverWorld(course);
    var sw = System.Diagnostics.Stopwatch.StartNew();
    foreach (int k in new[] { 3, 1, 4, 2 }) world.Add(k, ReachGenerator.Generate(CourseSegments.Spec(course, k)));   // out of order
    Console.WriteLine($"  segments 1–4 ({course.Cuts[5] - course.Cuts[1]:F0} m of valley) generated out of order in {sw.ElapsedMilliseconds / 1000.0:F1} s");

    // Along each interior cut line, compare the two neighbours' independent solutions (before any blending).
    float worstEta = 0f, worstVel = 0f, worstLandFar = 0f, worstLandNear = 0f, worstBankVel = 0f, worstBankLand = 0f;
    double worstFlux = 0, worstFluxPair = 0;
    for (int j = 2; j <= 4; j++)
    {
        double cut = course.Cuts[j];
        var (vx, vz) = course.ValleyPoint(cut);
        float h = course.ValleyHeadingAt(cut);
        double rx = MathF.Cos(h), rz = -MathF.Sin(h), tx = MathF.Sin(h), tz = MathF.Cos(h);
        // The shore raster zone: ±6 m of where either grid's water edge is. The two rotated 2 m grids draw the shore a
        // cell apart, so right there one is dry where the other has a few dm of water, and the flow along a stepped
        // bank is deflected differently. Reported separately (blending smooths it out), not counted as a seam.
        var shoreT = new System.Collections.Generic.List<double>();
        bool? prevA = null, prevB = null;
        for (double t = -600; t <= 600; t += 1.0)
        {
            if (!world.SampleSegment(j - 1, vx + rx * t, vz + rz * t, out var a) || !world.SampleSegment(j, vx + rx * t, vz + rz * t, out var b)) continue;
            if ((prevA.HasValue && prevA != a.Water) || (prevB.HasValue && prevB != b.Water)) shoreT.Add(t);
            prevA = a.Water; prevB = b.Water;
        }
        bool NearShore(double t) { foreach (double st in shoreT) if (Math.Abs(t - st) <= 6) return true; return false; }

        double fluxA = 0, fluxB = 0, tRiver = 0; float deepest = 0f;
        for (double t = -600; t <= 600; t += 1.0)
        {
            if (!world.SampleSegment(j - 1, vx + rx * t, vz + rz * t, out var a) || !world.SampleSegment(j, vx + rx * t, vz + rz * t, out var b)) continue;
            if (a.Depth > deepest) { deepest = a.Depth; tRiver = t; }
            fluxA += a.Depth * (a.VelX * tx + a.VelZ * tz);
            fluxB += b.Depth * (b.VelX * tx + b.VelZ * tz);
            if (a.Depth > 0.3f && b.Depth > 0.3f) worstEta = MathF.Max(worstEta, MathF.Abs(a.Eta - b.Eta));
            float dv = MathF.Sqrt((a.VelX - b.VelX) * (a.VelX - b.VelX) + (a.VelZ - b.VelZ) * (a.VelZ - b.VelZ));
            if (!NearShore(t) && a.Water && b.Water) worstVel = MathF.Max(worstVel, dv);
            else if (a.Water || b.Water) worstBankVel = MathF.Max(worstBankVel, dv);
        }
        // Terrain, outside the shore zone, allowing for bilinear interpolation on steep, creased slopes (both grids
        // sample the same functions at different points).
        for (double t = -600; t <= 600; t += 1.0)
        {
            if (!world.SampleSegment(j - 1, vx + rx * t, vz + rz * t, out var a) || !world.SampleSegment(j, vx + rx * t, vz + rz * t, out var b)) continue;
            if (a.Water || b.Water) continue;
            world.SampleSegment(j - 1, vx + rx * (t + 1), vz + rz * (t + 1), out var a2);
            float dh = MathF.Abs(a.Height - b.Height), slope = MathF.Abs(a2.Height - a.Height);
            if (NearShore(t)) { worstBankLand = MathF.Max(worstBankLand, dh); continue; }
            float excess = dh - slope * 2f * 0.5f;   // half a cell's worth of slope is interpolation, not a step
            if (Math.Abs(t - tRiver) > 80) worstLandFar = MathF.Max(worstLandFar, excess); else worstLandNear = MathF.Max(worstLandNear, excess);
        }
        double q = course.DischargeAt(cut);
        worstFlux = Math.Max(worstFlux, Math.Max(Math.Abs(fluxA - q), Math.Abs(fluxB - q)) / q);
        worstFluxPair = Math.Max(worstFluxPair, Math.Abs(fluxA - fluxB) / q);
    }
    Console.WriteLine($"  seams (raw, before blending): level {worstEta * 100:F1} cm, current {worstVel * 100:F1} cm/s in the channel, " +
                      $"terrain {worstLandFar * 100:F1} cm away from the river / {worstLandNear * 100:F1} cm near it (beyond interpolation), " +
                      $"discharge {worstFlux:P1} off the course, {worstFluxPair:P1} between neighbours");
    Console.WriteLine($"  within 6 m of the shore, where the two 2 m grids rasterise it a cell apart: current {worstBankVel * 100:F1} cm/s, " +
                      $"terrain {worstBankLand * 100:F0} cm (blended away over {2 * RiverWorld.Blend:F0} m)");
    // Blended (what the game sees): walking the river's centre line, the current and level change no faster across
    // a seam than they do anyway along the plain river inside the segments.
    float seamDv = 0f, plainDv = 0f, seamDeta = 0f, plainDeta = 0f;
    int bhint = 1;
    for (int j = 2; j <= 4; j++)
    {
        int a0 = course.RiverIndexAt(course.Cuts[j] - 200.0), a1 = course.RiverIndexAt(course.Cuts[j] + 200.0);
        int p0 = course.RiverIndexAt(course.Cuts[j] + 400.0), p1 = course.RiverIndexAt(course.Cuts[j] + 800.0);
        RiverSample prev = default; bool havePrev = false;
        for (int i = Math.Min(a0, p0); i <= Math.Max(a1, p1); i++)
        {
            bool seam = i >= a0 && i <= a1, plain = i >= p0 && i <= p1;
            if (!seam && !plain) { havePrev = false; continue; }
            if (!world.Sample(course.X[i], course.Z[i], ref bhint, out var s) || !s.Water) { havePrev = false; continue; }
            if (havePrev)
            {
                float dv = MathF.Sqrt((s.VelX - prev.VelX) * (s.VelX - prev.VelX) + (s.VelZ - prev.VelZ) * (s.VelZ - prev.VelZ));
                float de = MathF.Abs(s.Eta - prev.Eta);
                if (seam) { seamDv = MathF.Max(seamDv, dv); seamDeta = MathF.Max(seamDeta, de); }
                else { plainDv = MathF.Max(plainDv, dv); plainDeta = MathF.Max(plainDeta, de); }
            }
            prev = s; havePrev = true;
        }
    }
    Console.WriteLine($"  blended, along the river every 2 m: current changes ≤ {seamDv * 100:F1} cm/s at seams vs ≤ {plainDv * 100:F1} in plain river, " +
                      $"level ≤ {seamDeta * 1000:F1} vs ≤ {plainDeta * 1000:F1} mm");
    Check(worstEta < 0.03f, "the water level matches across seams (< 3 cm)");
    // 15 cm/s ≈ 8° of direction at 1 m/s: two rotated 2 m grids resolve the flow along a sloping, curving bank a few
    // degrees apart (with breathing widths that zone reaches ~10 m into the channel). What matters is the blended
    // check below: across seams the river changes no faster than along its own bends.
    Check(worstVel < 0.15f, "the current in the channel matches across seams (< 15 cm/s)");
    Check(seamDv <= 1.5f * plainDv && seamDeta <= 1.5f * plainDeta, "blended, seams change the river no faster than its own bends do");
    Check(worstLandFar < 0.05f && worstLandNear < 0.1f, "no terrain step at seams (beyond 2 m-grid interpolation)");
    Check(worstFlux < 0.05 && worstFluxPair < 0.03, "discharge is conserved across seams");

    world.TryGet(2, out var seg2);
    var alone = ReachGenerator.Generate(CourseSegments.Spec(new Course(new CourseSettings()), 2));
    Check(Fingerprint.Of(alone) == Fingerprint.Of(seg2), "a segment generated alone is identical (no chain between segments)");

    // Baked seams: segments 2 and 3, each baked with the blend, show the same surface along their shared cut line
    // (that is where the shaders split them pixel-exactly).
    {
        world.TryGet(3, out var seg3);
        var b2 = SegmentBake.Bake(seg2, 2, world);
        var b3 = SegmentBake.Bake(seg3, 3, world);
        double cut = course.Cuts[3];
        var (vx, vz) = course.ValleyPoint(cut);
        float hc = course.ValleyHeadingAt(cut);
        float worstH = 0f, worstE = 0f;
        for (double t = -550; t <= 550; t += 1.0)
        {
            double px = vx + MathF.Cos(hc) * t, pz = vz - MathF.Sin(hc) * t;
            var (l2x, l2z) = b2.Frame.ToLocal(px, pz); var (l3x, l3z) = b3.Frame.ToLocal(px, pz);
            if (!b2.InDomain(l2x, l2z) || !b3.InDomain(l3x, l3z)) continue;
            worstH = MathF.Max(worstH, MathF.Abs(b2.SampleHeight(l2x, l2z) - b3.SampleHeight(l3x, l3z)));
            if (b2.SampleDepth(l2x, l2z) > 0.3f && b3.SampleDepth(l3x, l3z) > 0.3f)
                worstE = MathF.Max(worstE, MathF.Abs(b2.SampleEta(l2x, l2z) - b3.SampleEta(l3x, l3z)));
        }
        Console.WriteLine($"  baked seams, along the cut line: terrain within {worstH * 100:F1} cm, water level within {worstE * 100:F1} cm");
        Check(worstH < 0.15f && worstE < 0.01f, "baked neighbours meet at the cut line (where the shaders split them)");
    }

    if (worldPath != null) { WorldMap.Write(world, new[] { 1, 2, 3, 4 }, 4.0, worldPath); Console.WriteLine($"  world map: {worldPath}"); }
}

var settings = new ReachSettings();
var field = ReachGenerator.Generate(settings, Console.WriteLine);

Console.WriteLine("Checks:");
// Mass conservation: what flows in flows out.
Check(MathF.Abs(field.OutflowMeasured - field.InflowTotal) < 0.03f * field.InflowTotal,
      $"discharge conserved (in {field.InflowTotal:F1}, out {field.OutflowMeasured:F1} m³/s)");
Check(field.Tributary.Count > 50, "tributary reaches the main river");
Check(field.Islands.Count >= 4, $"islands placed ({field.Islands.Count})");

// Speeds: realistic river currents.
float maxSpeed = 0f; double sumSpeed = 0; int water = 0, lakeN = 0; double lakeSpeed = 0, riffleSpeed = 0; int riffleN = 0;
for (int i = 0; i < field.Water.Length; i++)
{
    if (!field.Water[i] || field.Still[i]) continue;
    float sp = MathF.Sqrt(field.VelX[i] * field.VelX[i] + field.VelZ[i] * field.VelZ[i]);
    maxSpeed = MathF.Max(maxSpeed, sp); sumSpeed += sp; water++;
    float z = (i / field.Nx) * field.Cell;
    if (z > 1300 && z < 1500) { lakeSpeed += sp; lakeN++; }
    if (z > 2800 && z < 2880) { riffleSpeed += sp; riffleN++; }
}
float mean = (float)(sumSpeed / water), lake = (float)(lakeSpeed / Math.Max(1, lakeN)), riffle = (float)(riffleSpeed / Math.Max(1, riffleN));
Console.WriteLine($"  speeds: mean {mean:F2}, lake {lake:F2}, riffle {riffle:F2}, max {maxSpeed:F2} m/s");
Check(mean > 0.3f && mean < 1.8f, "mean current is river-like (0.3–1.8 m/s)");
Check(lake < mean, "the lake is slower than the river on average");
Check(riffle > mean, "the riffle is faster than the river on average");

// Surface: flows downhill overall, modest total drop.
float etaTop = field.SampleEta(field.Main.X[field.Main.IndexAtZ(10)], 10f);
float etaBottom = settings.OutletEta;
Console.WriteLine($"  surface: top {etaTop:F2} m, outlet {etaBottom:F2} m, trend slope {field.EtaTrendB * 1000:F3} m/km");
Check(etaTop > etaBottom + 0.3f && etaTop < etaBottom + 12f, "water surface drops downstream by a plausible amount");

// Water surface under land meshes must stay below the terrain near shores (no water floating over banks).
int floating = 0;
for (int i = 0; i < field.Water.Length; i++)
    if (!field.Water[i] && field.ShoreDist[i] < 3f && field.Height[i] < field.Eta[i] - 0.05f) floating++;
Check(floating < 50, $"banks stay above the water next to the shore ({floating} low nodes)");

// Eddies: water should turn back behind obstacles, and only there.
{
    var (tanX, tanZ) = Tangents.PerNode(field);
    float Along(int i) => field.VelX[i] * tanX[i] + field.VelZ[i] * tanZ[i];
    int wet = 0, back = 0, lakeWet = 0, lakeBack = 0, upWet = 0, upBack = 0; float worstBack = 0f;
    for (int i = 0; i < field.Water.Length; i++)
    {
        if (!field.Water[i] || field.Depth[i] < 0.15f) continue;
        float u = Along(i), z = (i / field.Nx) * field.Cell;
        bool b = u < -0.02f;
        wet++; if (b) { back++; worstBack = MathF.Max(worstBack, -u); }
        if (z > 1100 && z < 1350) { lakeWet++; if (b) lakeBack++; }
        if (z > 150 && z < 1000) { upWet++; if (b) upBack++; }
    }
    float fBack = back / (float)wet, fLake = lakeBack / (float)Math.Max(1, lakeWet), fUp = upBack / (float)Math.Max(1, upWet);
    Console.WriteLine($"  eddies: {fBack:P1} of the water flows upstream (lake inlet {fLake:P1}, upper meanders {fUp:P1}), fastest {worstBack:F2} m/s");

    // Island wakes: along each island's axis, just past its tail, water comes back towards it. Only islands whose
    // tail ends in open water count (in this seed the split island's tail runs into the bank of a bend).
    int wakesTested = 0, wakesBack = 0;
    foreach (var isl in field.Islands)
    {
        // Near wake: from the tail (the noisy outline can reach past LengthDown) one island width downstream,
        // half a width across. Share of it where the water runs back towards the island.
        float d0 = isl.LengthDown;
        while (d0 < isl.LengthDown * 1.6f && isl.Sdf(isl.X + isl.DirX * d0, isl.Z + isl.DirZ * d0) < 1f) d0 += 1f;
        int wn = 0, wb = 0; bool open = true;
        for (float d = d0 + 1f; d <= d0 + 1f + 2f * isl.HalfWidth && open; d += 1f)
            for (float v = -0.5f * isl.HalfWidth; v <= 0.5f * isl.HalfWidth; v += 1f)
            {
                float x = isl.X + isl.DirX * d - isl.DirZ * v, z = isl.Z + isl.DirZ * d + isl.DirX * v;
                if (!field.InDomain(x, z) || field.SampleDepth(x, z) < 0.15f) { open = false; break; }
                var (vx, vz) = field.SampleVelocity(x, z);
                wn++; if (vx * isl.DirX + vz * isl.DirZ < 0f) wb++;
            }
        if (!open || wn == 0) continue;
        float share = wb / (float)wn;
        // Potential flow alone gives 0 %: the wake just closes. A recirculation bubble's reversed core is ~1/3–1/2.
        wakesTested++; if (share > 0.25f) wakesBack++;
        Console.WriteLine($"  island wake (half-width {isl.HalfWidth:F0} m): {share:P0} of the near wake flows back towards it");
    }
    Check(wakesTested >= 2 && wakesBack * 2 > wakesTested, $"water turns back into island wakes ({wakesBack}/{wakesTested})");
    Check(fLake > 0.03f, "the lake inlet has backwaters beside the main current");
    Check(fUp < 0.04f, "plain meanders stay mostly free of backflow (no false eddies on outer banks)");
    Check(fBack > 0.005f && fBack < 0.15f && worstBack < 0.5f * maxSpeed, "eddies are a minor, slower part of the river");
}

// Determinism.
var again = ReachGenerator.Generate(settings);
bool same = true;
for (int i = 0; i < field.Height.Length && same; i++) same = field.Height[i] == again.Height[i] && field.VelX[i] == again.VelX[i];
Check(same, "same seed gives the same reach");

if (Array.IndexOf(args, "--reference") >= 0)
{
    // Compare against a fully converged single-level solve (slow).
    var refField = ReachGenerator.Generate(new ReachSettings { ReferencePicardSteps = 8 }, m => { if (m.Contains("step")) Console.WriteLine("  ref " + m); });
    double se = 0, sv = 0; int n = 0; float worst = 0;
    for (int i = 0; i < field.Water.Length; i++)
    {
        if (!field.Water[i]) continue;
        float de = field.Eta[i] - refField.Eta[i];
        float dv = MathF.Sqrt(MathF.Pow(field.VelX[i] - refField.VelX[i], 2) + MathF.Pow(field.VelZ[i] - refField.VelZ[i], 2));
        se += de * de; sv += dv * dv; n++; worst = MathF.Max(worst, dv);
    }
    Console.WriteLine($"  vs reference: surface rms {Math.Sqrt(se / n) * 100:F1} cm, current rms {Math.Sqrt(sv / n):F3} m/s, worst {worst:F2} m/s");
}

// Fingerprint of the whole generated reach: refactors that must not change the river compare it before/after.
// (Not a pass/fail check: floating-point maths can differ between platforms and .NET versions.)
Console.WriteLine($"  fingerprint: {Fingerprint.Of(field):X16}");

if (mapPath != null) { MapRender.Write(field, mapPath); Console.WriteLine($"map: {mapPath}"); }
if (flowPath != null) { MapRender.WriteFlow(field, flowPath); Console.WriteLine($"flow map: {flowPath}"); }
Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILED");
return failures == 0 ? 0 : 1;

static class MapRender
{
    // Top-down map: land shaded by height, water by depth, current as brightness, foam white.
    public static void Write(ReachField f, string path, int scale = 1)
    {
        int w = f.Nx, h = f.Nz;
        var px = new byte[w * h * 3];
        for (int z = 0; z < h; z++)
            for (int x = 0; x < w; x++)
            {
                int i = z * w + x;
                float r, g, b;
                if (f.Water[i])
                {
                    float d = MathF.Min(f.Depth[i] / 5f, 1f);
                    float sp = MathF.Min(MathF.Sqrt(f.VelX[i] * f.VelX[i] + f.VelZ[i] * f.VelZ[i]) / 2.5f, 1f);
                    r = 0.1f + 0.3f * sp; g = 0.35f + 0.4f * sp - 0.2f * d; b = 0.7f - 0.35f * d + 0.2f * sp;
                    if (f.Still[i]) { r = 0.4f; g = 0.2f; b = 0.5f; }
                    float fo = f.Foam[i]; r += (1 - r) * fo; g += (1 - g) * fo; b += (1 - b) * fo;
                }
                else
                {
                    float above = f.Height[i] - f.Eta[i];
                    float hx = x > 0 && x < w - 1 ? f.Height[i + 1] - f.Height[i - 1] : 0;
                    float hz = z > 0 && z < h - 1 ? f.Height[i + w] - f.Height[i - w] : 0;
                    float shade = Math.Clamp(0.75f - 0.25f * (hx - hz), 0.3f, 1.1f);
                    float t = MathF.Min(above / 80f, 1f);
                    r = (0.35f + 0.4f * t) * shade; g = (0.55f + 0.1f * t) * shade; b = (0.25f + 0.35f * t) * shade;
                    if (above < 0.8f) { r = 0.8f * shade; g = 0.74f * shade; b = 0.55f * shade; }
                }
                px[i * 3] = (byte)(Math.Clamp(r, 0, 1) * 255); px[i * 3 + 1] = (byte)(Math.Clamp(g, 0, 1) * 255); px[i * 3 + 2] = (byte)(Math.Clamp(b, 0, 1) * 255);
            }
        WritePng(path, w, h, px);
    }

    // Flow map: blue = with the channel, red = against it (eddies), brightness = speed; lee zones tinted green;
    // land dark grey.
    public static void WriteFlow(ReachField f, string path)
    {
        var (tx, tz) = Tangents.PerNode(f);
        int w = f.Nx, h = f.Nz;
        var px = new byte[w * h * 3];
        for (int i = 0; i < w * h; i++)
        {
            float r = 0.18f, g = 0.18f, b = 0.18f;
            if (f.Water[i])
            {
                float u = f.VelX[i] * tx[i] + f.VelZ[i] * tz[i];
                float sp = MathF.Sqrt(f.VelX[i] * f.VelX[i] + f.VelZ[i] * f.VelZ[i]);
                float e = f.Eddy != null ? f.Eddy[i] : 0f;
                if (u >= 0) { r = 0.05f; g = 0.1f + 0.35f * e; b = 0.25f + 0.75f * MathF.Min(sp / 1.5f, 1f); }
                else { r = 0.35f + 0.65f * MathF.Min(-u / 0.4f, 1f); g = 0.1f + 0.35f * e; b = 0.1f; }
            }
            px[i * 3] = (byte)(Math.Clamp(r, 0, 1) * 255); px[i * 3 + 1] = (byte)(Math.Clamp(g, 0, 1) * 255); px[i * 3 + 2] = (byte)(Math.Clamp(b, 0, 1) * 255);
        }
        WritePng(path, w, h, px);
    }

    public static void WritePng(string path, int w, int h, byte[] rgb)
    {
        using var fs = File.Create(path);
        fs.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var ihdr = new byte[13];
        Be(ihdr, 0, w); Be(ihdr, 4, h); ihdr[8] = 8; ihdr[9] = 2;
        Chunk(fs, "IHDR", ihdr);
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Fastest, true))
            for (int y = 0; y < h; y++) { z.WriteByte(0); z.Write(rgb, y * w * 3, w * 3); }
        Chunk(fs, "IDAT", ms.ToArray());
        Chunk(fs, "IEND", Array.Empty<byte>());
    }
    static void Be(byte[] b, int o, int v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }
    static void Chunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4]; Be(len, 0, data.Length); s.Write(len);
        var t = System.Text.Encoding.ASCII.GetBytes(type); s.Write(t); s.Write(data);
        uint crc = Crc(t, 0xffffffff); crc = Crc(data, crc) ^ 0xffffffff;
        var c = new byte[4]; Be(c, 0, (int)crc); s.Write(c);
    }
    static uint Crc(byte[] d, uint c)
    {
        foreach (byte x in d) { c ^= x; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xedb88320 ^ (c >> 1) : c >> 1; }
        return c;
    }
}

static class Tangents
{
    /// <summary>Per node: the flow direction of the nearest centre-line sample (main river or tributary).</summary>
    public static (float[] x, float[] z) PerNode(ReachField f)
    {
        int n = f.Nx * f.Nz;
        var tx = new float[n]; var tz = new float[n]; var d2 = new float[n];
        Array.Fill(d2, float.MaxValue);
        foreach (var c in new[] { f.Main, f.Tributary })
            for (int k = 0; k < c.Count; k++)
            {
                float r = MathF.Max(c.HalfLeft[k], c.HalfRight[k]) + 8f;
                int x0 = Math.Max(0, (int)((c.X[k] - r) / f.Cell)), x1 = Math.Min(f.Nx - 1, (int)((c.X[k] + r) / f.Cell) + 1);
                int z0 = Math.Max(0, (int)((c.Z[k] - r) / f.Cell)), z1 = Math.Min(f.Nz - 1, (int)((c.Z[k] + r) / f.Cell) + 1);
                for (int z = z0; z <= z1; z++)
                    for (int x = x0; x <= x1; x++)
                    {
                        float dx = x * f.Cell - c.X[k], dz = z * f.Cell - c.Z[k], d = dx * dx + dz * dz;
                        int i = z * f.Nx + x;
                        if (d < d2[i]) { d2[i] = d; tx[i] = c.TanX[k]; tz[i] = c.TanZ[k]; }
                    }
            }
        return (tx, tz);
    }
}

static class Fingerprint
{
    /// <summary>FNV-1a over every field array, the centre lines, islands and rocks.</summary>
    public static ulong Of(ReachField f)
    {
        ulong h = 14695981039346656037UL;
        void B(byte b) { h ^= b; h *= 1099511628211UL; }
        void F(float v) { foreach (var b in BitConverter.GetBytes(v)) B(b); }
        void I(int v) { foreach (var b in BitConverter.GetBytes(v)) B(b); }
        I(f.Nx); I(f.Nz); F(f.Cell);
        foreach (var a in new[] { f.Height, f.Eta, f.Depth, f.VelX, f.VelZ, f.Foam, f.ShoreDist, f.Eddy })
            foreach (var v in a) F(v);
        foreach (var w in f.Water) B(w ? (byte)1 : (byte)0);
        foreach (var c in new[] { f.Main, f.Tributary })
            for (int k = 0; k < c.Count; k++) { F(c.X[k]); F(c.Z[k]); F(c.HalfLeft[k]); F(c.HalfRight[k]); F(c.MaxDepth[k]); }
        foreach (var i in f.Islands) { F(i.X); F(i.Z); F(i.HalfWidth); F(i.LengthUp); F(i.LengthDown); I(i.Seed); }
        foreach (var r in f.Rocks) { F(r.X); F(r.Z); F(r.Radius); F(r.Top); F(r.FlowX); F(r.FlowZ); }
        F(f.EtaTrendA); F(f.EtaTrendB); F(f.OutflowMeasured);
        return h;
    }
}

static class CourseMap
{
    /// <summary>
    /// Top-down map of the course between valley distances σ0 and σ1 (north up, east right): the corridor, the river
    /// (blue; lakes light blue, riffles white, island splits green), the valley axis, confluences (magenta) and the
    /// segment cuts (red, across the valley).
    /// </summary>
    public static void Write(Course c, double s0, double s1, double mpp, string path)
    {
        double x0 = double.MaxValue, x1 = double.MinValue, z0 = double.MaxValue, z1 = double.MinValue;
        for (int i = 0; i < c.VX.Count; i++)
        {
            double sg = i * Course.ValleyStep;
            if (sg < s0 || sg > s1) continue;
            x0 = Math.Min(x0, c.VX[i]); x1 = Math.Max(x1, c.VX[i]); z0 = Math.Min(z0, c.VZ[i]); z1 = Math.Max(z1, c.VZ[i]);
        }
        x0 -= 900; x1 += 900; z0 -= 900; z1 += 900;
        int w = (int)((x1 - x0) / mpp), h = (int)((z1 - z0) / mpp);
        var px = new byte[w * h * 3];
        void Put(int x, int y, byte r, byte g, byte b) { if (x < 0 || y < 0 || x >= w || y >= h) return; int k = (y * w + x) * 3; px[k] = r; px[k + 1] = g; px[k + 2] = b; }
        void Disc(double wx, double wz, double rad, byte r, byte g, byte b)
        {
            double cx = (wx - x0) / mpp, cy = (wz - z0) / mpp, rr = Math.Max(0.7, rad / mpp);
            for (int y = (int)(cy - rr); y <= (int)(cy + rr) + 1; y++)
                for (int x = (int)(cx - rr); x <= (int)(cx + rr) + 1; x++)
                    if ((x - cx) * (x - cx) + (y - cy) * (y - cy) <= rr * rr) Put(x, y, r, g, b);
        }
        for (int i = 0; i < w * h; i++) { px[i * 3] = 70; px[i * 3 + 1] = 92; px[i * 3 + 2] = 58; }
        // Corridor (±600 m around the valley axis).
        for (int i = 0; i < c.VX.Count; i += 4)
        {
            double sg = i * Course.ValleyStep;
            if (sg >= s0 && sg <= s1) Disc(c.VX[i], c.VZ[i], 600, 96, 124, 74);
        }
        // River, coloured by feature.
        for (int i = 0; i < c.Count; i++)
        {
            double sg = c.Sigma[i];
            if (sg < s0 || sg > s1) continue;
            byte r = 40, g = 90, b = 200;
            foreach (var f in c.Features)
            {
                if (sg < f.S0 || sg > f.S1) continue;
                if (f.Kind == FeatureKind.Lake) { r = 110; g = 170; b = 235; }
                else if (f.Kind == FeatureKind.Riffle) { r = 235; g = 240; b = 245; }
                else if (f.Kind == FeatureKind.Split) { r = 60; g = 170; b = 90; }
            }
            // Draw the channel across its width (left and right banks differ in lakes).
            var (rx, rz) = c.RiverRight(i);
            for (double t = -c.HalfLeft[i]; t <= c.HalfRight[i]; t += Math.Max(mpp * 0.7, 1.0))
                Disc(c.X[i] + rx * t, c.Z[i] + rz * t, mpp * 0.6, r, g, b);
        }
        // Valley axis.
        for (int i = 0; i < c.VX.Count; i += 3)
        {
            double sg = i * Course.ValleyStep;
            if (sg >= s0 && sg <= s1) Disc(c.VX[i], c.VZ[i], mpp * 0.5, 30, 40, 30);
        }
        // Confluences: a stub coming in from the tributary's side.
        foreach (var f in c.Features)
        {
            if (f.Kind != FeatureKind.Confluence || f.Mid < s0 || f.Mid > s1) continue;
            int vi = (int)(f.Mid / Course.ValleyStep);
            float hv = c.VHeading[vi];
            double rx = Math.Cos(hv) * f.Side, rz = -Math.Sin(hv) * f.Side;
            for (double t = 0; t < 600; t += mpp * 0.7) Disc(c.VX[vi] + rx * t - Math.Sin(hv) * t * 0.5, c.VZ[vi] + rz * t - Math.Cos(hv) * t * 0.5, mpp, 200, 60, 200);
        }
        // Cuts across the valley.
        foreach (double cut in c.Cuts)
        {
            if (cut < s0 || cut > s1) continue;
            int vi = (int)(cut / Course.ValleyStep);
            if (vi >= c.VX.Count) continue;
            float hv = c.VHeading[vi];
            double rx = Math.Cos(hv), rz = -Math.Sin(hv);
            for (double t = -600; t <= 600; t += mpp * 0.7) Disc(c.VX[vi] + rx * t, c.VZ[vi] + rz * t, mpp * 0.5, 220, 40, 40);
        }
        MapRender.WritePng(path, w, h, px);
    }
}

static class WorldMap
{
    /// <summary>Stitched top-down map of loaded segments, sampled through RiverWorld (blended seams), north up.</summary>
    public static void Write(RiverWorld world, int[] segments, double mpp, string path)
    {
        double x0 = double.MaxValue, x1 = double.MinValue, z0 = double.MaxValue, z1 = double.MinValue;
        foreach (int k in segments)
        {
            world.TryGet(k, out var f);
            foreach (var (cx, cz) in new[] { (0f, 0f), (f.SizeX, 0f), (0f, f.SizeZ), (f.SizeX, f.SizeZ) })
            {
                var (wx, wz) = f.Frame.ToWorld(cx, cz);
                x0 = Math.Min(x0, wx); x1 = Math.Max(x1, wx); z0 = Math.Min(z0, wz); z1 = Math.Max(z1, wz);
            }
        }
        int w = (int)((x1 - x0) / mpp), h = (int)((z1 - z0) / mpp);
        var samples = new RiverSample[w * h]; var has = new bool[w * h];
        int hint = segments[0];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                has[y * w + x] = world.Sample(x0 + (x + 0.5) * mpp, z0 + (y + 0.5) * mpp, ref hint, out samples[y * w + x]);
        var px = new byte[w * h * 3];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                float r = 0.08f, g = 0.08f, b = 0.1f;
                if (has[i])
                {
                    var s = samples[i];
                    if (s.Water)
                    {
                        float d = MathF.Min(s.Depth / 5f, 1f), sp = MathF.Min(MathF.Sqrt(s.VelX * s.VelX + s.VelZ * s.VelZ) / 2.5f, 1f);
                        r = 0.1f + 0.3f * sp; g = 0.35f + 0.4f * sp - 0.2f * d; b = 0.7f - 0.35f * d + 0.2f * sp;
                        r += (1 - r) * s.Foam; g += (1 - g) * s.Foam; b += (1 - b) * s.Foam;
                    }
                    else
                    {
                        float hx = x + 1 < w && has[i + 1] ? samples[i + 1].Height - s.Height : 0f;
                        float hz = y + 1 < h && has[i + w] ? samples[i + w].Height - s.Height : 0f;
                        float shade = Math.Clamp(0.75f - 0.6f * (hx - hz), 0.3f, 1.1f);
                        float above = s.Height - s.Eta, t = MathF.Min(above / 80f, 1f);
                        r = (0.35f + 0.4f * t) * shade; g = (0.55f + 0.1f * t) * shade; b = (0.25f + 0.35f * t) * shade;
                        if (above < 0.8f) { r = 0.8f * shade; g = 0.74f * shade; b = 0.55f * shade; }
                    }
                }
                px[i * 3] = (byte)(Math.Clamp(r, 0, 1) * 255); px[i * 3 + 1] = (byte)(Math.Clamp(g, 0, 1) * 255); px[i * 3 + 2] = (byte)(Math.Clamp(b, 0, 1) * 255);
            }
        MapRender.WritePng(path, w, h, px);
    }
}
