using System;
using System.Collections.Generic;

namespace River.Core;

/// <summary>Knobs of the endless course (docs/STREAMING.md §3, Tom's decisions D1–D3).</summary>
public sealed class CourseSettings
{
    public int Seed = 1890;
    public float BearingDeg = 0f;            // general direction of the journey: 0 = south (+z), 90 = east (+x) (D2)
    public float MaxSwingDeg = 90f;          // the valley never turns further than this from the bearing (D1)
    public float SwingDeg = 75f;             // typical swing (noise amplitude) before the clamp
    public float TurnWavelength = 40000f;    // m: noise wavelength of the valley heading → big turns every 10–30 km (D3)
    public float MinTurnRadius = 3000f;      // m: valley axis; the ±300 m meander belt must fit inside every bend
    public float ConfluenceTurnRadius = 1500f;   // m: sharper just below a confluence (D3)
    public float StartDischarge = 80f;       // m³/s
    public float CreekGrowthKm = 200f;       // slow growth from small creeks: ×(1 + s/this)^0.5
    public float MaxDischarge = 1200f;       // m³/s: a bigger river wouldn't fit the 1.2 km corridor (lift if it widens)
    public float Manning = 0.035f;
    public float StartEta = 100f;            // water level at the start (m); it only goes down
    // Features per 10 km of valley (the sandbox World.*Frequency settings will multiply these).
    public float Lakes = 0.25f, Splits = 0.7f, Riffles = 0.9f, Tributaries = 0.6f, Rapids = 0.3f;   // relative frequencies
    public float MinFeatureGap = 600f;       // m between features (room for a cut and its clearance)
    // Segments (cuts).
    public float MinSegment = 1500f, MaxSegment = 4000f, TargetSegment = 2500f;
    public float CutClearance = 250f;        // no feature within this of a cut
    public float CutWindow = 200f;           // the river must cross the cut line once within ±this
}

public enum FeatureKind { Lake, Split, Riffle, Confluence, Rapid }

/// <summary>
/// The character of a stretch of river, a few km long (blended into the next over 500 m): how strongly and how
/// regularly it meanders, how wide and deep it runs.
/// </summary>
public enum ReachKind { Meander, Lazy, Straight, Wide, Gorge, Braided }

public readonly record struct Reach(ReachKind Kind, double S0, double S1);

/// <summary>Numbers behind a <see cref="ReachKind"/> (blended between reaches).</summary>
public struct ReachParams
{
    public float AmpBase, AmpVar;       // meander amplitude (radians) = AmpBase + AmpVar × noise
    public float Wavelength;            // × the usual meander wavelength
    public float Width, Depth;          // × the usual hydraulic width and depth
    public float Irregular;             // extra wandering of the heading (noise weight)
    public float Gorge;                 // 0..1: cliffs instead of banks (the segment generator), boulders at their feet
    public float Braid;                 // 0..1: many gravel bars splitting the shallow channel

    public static ReachParams Of(ReachKind k) => k switch
    {
        ReachKind.Meander => new ReachParams { AmpBase = 1.45f, AmpVar = 1.0f, Wavelength = 1.0f, Width = 1.0f, Depth = 1.0f, Irregular = 0.25f },
        ReachKind.Lazy => new ReachParams { AmpBase = 0.95f, AmpVar = 0.8f, Wavelength = 1.5f, Width = 1.05f, Depth = 1.0f, Irregular = 0.6f },
        ReachKind.Straight => new ReachParams { AmpBase = 0.3f, AmpVar = 0.3f, Wavelength = 1.3f, Width = 0.92f, Depth = 1.1f, Irregular = 0.35f },
        // Incised: narrow, deep and faster, still winding (incised meanders), between cliffs.
        ReachKind.Gorge => new ReachParams { AmpBase = 0.6f, AmpVar = 0.35f, Wavelength = 1.15f, Width = 0.62f, Depth = 1.45f, Irregular = 0.3f, Gorge = 1f },
        // Wide, shallow and fairly straight; the bars are placed per segment (CourseSegments.GravelBar).
        ReachKind.Braided => new ReachParams { AmpBase = 0.35f, AmpVar = 0.25f, Wavelength = 1.4f, Width = 2.0f, Depth = 0.55f, Irregular = 0.4f, Braid = 1f },
        _ => new ReachParams { AmpBase = 0.8f, AmpVar = 0.6f, Wavelength = 1.3f, Width = 1.6f, Depth = 0.62f, Irregular = 0.4f },   // Wide
    };

    public static ReachParams Lerp(in ReachParams a, in ReachParams b, float t) => new()
    {
        AmpBase = a.AmpBase + (b.AmpBase - a.AmpBase) * t, AmpVar = a.AmpVar + (b.AmpVar - a.AmpVar) * t,
        Wavelength = a.Wavelength + (b.Wavelength - a.Wavelength) * t, Width = a.Width + (b.Width - a.Width) * t,
        Depth = a.Depth + (b.Depth - a.Depth) * t, Irregular = a.Irregular + (b.Irregular - a.Irregular) * t,
        Gorge = a.Gorge + (b.Gorge - a.Gorge) * t, Braid = a.Braid + (b.Braid - a.Braid) * t,
    };
}

/// <summary>A feature placed along the valley (σ = distance along the valley axis, m).</summary>
public readonly record struct CourseFeature(FeatureKind Kind, double S0, double S1, float Ramp, int Side, float Size, int Seed)
{
    /// <summary>0 outside, ramping up over <see cref="Ramp"/> metres to 1 on the plateau.</summary>
    public float Weight(double sigma) =>
        sigma <= S0 || sigma >= S1 ? 0f
        : MathUtil.Smoothstep(0f, Ramp, (float)(sigma - S0)) * MathUtil.Smoothstep(0f, Ramp, (float)(S1 - sigma));
    public double Mid => 0.5 * (S0 + S1);
}

/// <summary>
/// The endless river's course: the whole journey in 1D (docs/STREAMING.md §3). Two walks, both deterministic and
/// incremental (extending in any number of steps gives the same result):
///   • the valley axis, every <see cref="ValleyStep"/> m: heading wanders around the bearing (never more than
///     MaxSwingDeg off it, so the valley can't run back into itself), turning radius ≥ MinTurnRadius except just
///     below confluences, where it may turn sharper and swings away from the tributary;
///   • the river, every <see cref="Centerline.Step"/> m: the prototype's meanders (Kinoshita curve) relative to the
///     local valley heading, pulled back towards the axis, widening/deepening with features and discharge.
/// Features (lakes, island splits, riffles, confluences) and segment cuts are placed along σ, the distance along
/// the valley. The water level follows Manning's equation over each cross-section, so it agrees with what a
/// segment's flow solve will find. Positions are doubles: a journey goes thousands of km from the origin.
/// </summary>
public sealed class Course
{
    public const float ValleyStep = 10f;
    public readonly CourseSettings Settings;

    // Valley axis.
    public readonly List<double> VX = new(), VZ = new();
    public readonly List<float> VHeading = new();          // radians, 0 = +z (south), + towards +x (east)

    // River centre line (every Centerline.Step m along the river).
    public readonly List<double> X = new(), Z = new();
    public readonly List<float> Heading = new();           // flow direction, same convention
    public readonly List<float> HalfLeft = new(), HalfRight = new(), MaxDepth = new();
    public readonly List<float> Discharge = new();         // m³/s
    public readonly List<float> Eta = new();               // water level along the centre line (m)
    public readonly List<double> Sigma = new();            // σ of each river sample (projection on the valley)
    public readonly List<float> Offset = new();            // signed distance from the valley axis (+ = valley's right, +x when heading south)

    public readonly List<CourseFeature> Features = new();
    public readonly List<Reach> Reaches = new();            // the river's changing character along σ
    public readonly List<double> Cuts = new() { 0.0 };     // segment boundaries along σ

    public int Count => X.Count;
    public double RiverLength => (Count - 1) * Centerline.Step;
    public double ValleyLength => (VX.Count - 1) * ValleyStep;

    // Walk state.
    int _nextEvent; double _scheduledTo = 600.0; float _phase, _sAlong; int _vIdx;
    readonly float _bearing, _maxSwing;

    public Course(CourseSettings settings)
    {
        Settings = settings;
        _bearing = settings.BearingDeg * MathF.PI / 180f;
        _maxSwing = settings.MaxSwingDeg * MathF.PI / 180f;
        VX.Add(0.0); VZ.Add(0.0); VHeading.Add(_bearing);
        _phase = Noise.Hash01(1, 2, settings.Seed) * MathF.PI * 2f;
    }

    /// <summary>Generate until the river has passed valley distance σ (and features/cuts are settled up to it).</summary>
    public void ExtendTo(double sigma)
    {
        // Order matters: cuts need the features around them, the valley and river need both.
        // Reaches first: features look at them (riffles in gorges become rapids). Reaches don't depend on features.
        ScheduleReaches(sigma + 2.0 * Settings.MaxSegment + 3000.0);
        ScheduleFeatures(sigma + 2.0 * Settings.MaxSegment + 3000.0);
        ScheduleCuts(sigma + Settings.MaxSegment + 1000.0);
        ExtendValley(sigma + Settings.MaxSegment + 2000.0);
        if (Count == 0) StartRiver();
        while (Sigma[^1] < sigma + Settings.MaxSegment + Settings.CutWindow + 200.0) StepRiver();
    }

    // ================================================================ features

    void ScheduleFeatures(double upTo)
    {
        var s = Settings;
        float total = s.Lakes + s.Splits + s.Riffles + s.Tributaries + s.Rapids;
        if (total <= 0f) { _scheduledTo = upTo; return; }
        float meanGap = MathF.Max(200f, 10000f / total - s.MinFeatureGap);
        while (_scheduledTo < upTo)
        {
            int e = _nextEvent++;
            float H(int k) => Noise.Hash01(e, k, s.Seed + 601);
            double start = _scheduledTo + s.MinFeatureGap + -MathF.Log(1f - 0.95f * H(0)) * meanGap;
            float pick = H(1) * total;
            FeatureKind kind = pick < s.Lakes ? FeatureKind.Lake
                : pick < s.Lakes + s.Splits ? FeatureKind.Split
                : pick < s.Lakes + s.Splits + s.Riffles ? FeatureKind.Riffle
                : pick < s.Lakes + s.Splits + s.Riffles + s.Tributaries ? FeatureKind.Confluence : FeatureKind.Rapid;
            int side = H(3) < 0.5f ? -1 : 1;
            // A segment carries at most one tributary: confluences at least a segment (+ margins) apart.
            if (kind == FeatureKind.Confluence)
                foreach (var prev in Features)
                    if (prev.Kind == FeatureKind.Confluence && start - prev.Mid < s.MaxSegment + 300.0) { kind = FeatureKind.Riffle; break; }
            // In a gorge the river drops hard: its riffles are rapids.
            if (kind == FeatureKind.Riffle && ReachAt(start + 150.0).Gorge > 0.5f) kind = FeatureKind.Rapid;
            CourseFeature f = kind switch
            {
                FeatureKind.Lake => new(kind, start, start + 400.0 + 300f + 250f * H(2), 200f, side, 70f + 90f * H(4), s.Seed + 7000 + e),
                FeatureKind.Split => new(kind, start, start + 240.0 + 60f * H(2), 60f, side, 17f, s.Seed + 7000 + e),
                FeatureKind.Riffle => new(kind, start, start + 300.0 + 120f * H(2), 60f, side, 0f, s.Seed + 7000 + e),
                // Rapids: 200–400 m of steep, narrow, bouldery water (CourseSegments.RapidRocks).
                FeatureKind.Rapid => new(kind, start, start + 200.0 + 200f * H(2), 50f, side, 0f, s.Seed + 7000 + e),
                // Size: share of Q added. Mostly creeks (+2–5 %), now and then a real river (up to +15 %).
                _ => new(kind, start, start + 300.0, 150f, side, 0.02f + 0.13f * H(4) * H(4) * H(4), s.Seed + 7000 + e),
            };
            Features.Add(f);
            _scheduledTo = f.S1;
        }
    }

    // ================================================================ reaches

    /// <summary>
    /// Reaches one after another: tight meanders 30 %, lazy bends 25 %, near-straight 12 %, wide 15 %, gorge 9 %,
    /// braided 9 %. 2–6 km long; gorges 1.2–3 km (and never the first reach: the journey starts on open water).
    /// </summary>
    void ScheduleReaches(double upTo)
    {
        while (Reaches.Count == 0 || Reaches[^1].S1 < upTo)
        {
            int e = Reaches.Count;
            float H(int k) => Noise.Hash01(e, k, Settings.Seed + 811);
            double s0 = e == 0 ? -1000.0 : Reaches[^1].S1;
            float pick = H(1);
            var kind = pick < 0.30f ? ReachKind.Meander : pick < 0.55f ? ReachKind.Lazy : pick < 0.67f ? ReachKind.Straight
                     : pick < 0.82f ? ReachKind.Wide : pick < 0.91f ? ReachKind.Gorge : ReachKind.Braided;
            if (e > 0 && kind == Reaches[^1].Kind) kind = (ReachKind)(((int)kind + 1 + (int)(H(2) * 5f)) % 6);   // a change
            if (e == 0 && kind == ReachKind.Gorge) kind = ReachKind.Meander;
            double len = kind == ReachKind.Gorge ? 1200.0 + 1800.0 * H(0) : 2000.0 + 4000.0 * H(0);
            Reaches.Add(new Reach(kind, s0, s0 + len));
        }
    }

    /// <summary>The river's character at σ: its reach's, blended from the previous reach over the first 500 m.</summary>
    public ReachParams ReachAt(double sigma)
    {
        for (int i = Reaches.Count - 1; i >= 0; i--)
        {
            if (Reaches[i].S0 > sigma) continue;
            var cur = ReachParams.Of(Reaches[i].Kind);
            if (i == 0) return cur;
            float t = MathUtil.Smoothstep(0f, 500f, (float)(sigma - Reaches[i].S0));
            return ReachParams.Lerp(ReachParams.Of(Reaches[i - 1].Kind), cur, t);
        }
        return ReachParams.Of(ReachKind.Meander);
    }

    float FeatureWeight(FeatureKind kind, double sigma)
    {
        float w = 0f;
        foreach (var f in Features) if (f.Kind == kind && sigma > f.S0 - 1 && sigma < f.S1 + 1) w = MathF.Max(w, f.Weight(sigma));
        return w;
    }

    /// <summary>Discharge at valley distance σ: slow creek growth and a step (over 200 m) at each confluence.</summary>
    public float DischargeAt(double sigma)
    {
        double q = Settings.StartDischarge * Math.Sqrt(1.0 + sigma / (Settings.CreekGrowthKm * 1000.0));
        foreach (var f in Features)
        {
            if (f.Kind != FeatureKind.Confluence || f.Mid > sigma + 100.0) continue;
            q *= 1.0 + f.Size * MathUtil.Smoothstep(-100f, 100f, (float)(sigma - f.Mid));
        }
        return (float)Math.Min(q, Settings.MaxDischarge);
    }

    // ================================================================ valley

    void ExtendValley(double upTo)
    {
        var s = Settings;
        while (ValleyLength < upTo)
        {
            double sigma = ValleyLength;
            float swing = s.SwingDeg * MathF.PI / 180f;
            float target = _bearing + swing * MathF.Tanh(2f * Noise.Fbm1((float)(sigma / s.TurnWavelength), s.Seed + 501, 3));
            float radius = s.MinTurnRadius;
            foreach (var f in Features)
            {
                if (f.Kind != FeatureKind.Confluence) continue;
                double d = sigma - f.Mid;
                // Below a confluence the valley swings away from the tributary for a while, and may turn sharper.
                target -= f.Side * 0.45f * MathUtil.Smoothstep(0f, 800f, (float)d) * (1f - MathUtil.Smoothstep(3000f, 12000f, (float)d));
                if (d > 0 && d < 1500) radius = s.ConfluenceTurnRadius;
            }
            float h = VHeading[^1];
            float maxTurn = ValleyStep / radius;
            h += Math.Clamp(target - h, -maxTurn, maxTurn);
            h = Math.Clamp(h, _bearing - _maxSwing, _bearing + _maxSwing);
            VHeading.Add(h);
            VX.Add(VX[^1] + MathF.Sin(h) * ValleyStep);
            VZ.Add(VZ[^1] + MathF.Cos(h) * ValleyStep);
        }
    }

    /// <summary>Point on the valley axis at σ (linear between the 10 m samples).</summary>
    public (double x, double z) ValleyPoint(double sigma)
    {
        double f = Math.Clamp(sigma / ValleyStep, 0.0, VX.Count - 1.001);
        int i = (int)f; double t = f - i;
        return (VX[i] + (VX[i + 1] - VX[i]) * t, VZ[i] + (VZ[i + 1] - VZ[i]) * t);
    }

    /// <summary>Valley heading at σ (radians, 0 = south, + towards east).</summary>
    public float ValleyHeadingAt(double sigma) => VHeading[(int)Math.Clamp(sigma / ValleyStep, 0.0, VHeading.Count - 1)];

    /// <summary>First river sample at or past valley distance σ (σ is nearly monotone; at features and cuts strictly).</summary>
    public int RiverIndexAt(double sigma)
    {
        int lo = 0, hi = Count - 1;
        while (lo < hi) { int mid = (lo + hi) / 2; if (Sigma[mid] < sigma) lo = mid + 1; else hi = mid; }
        return lo;
    }

    /// <summary>The river's right-hand normal at sample i (looking downstream), as <see cref="Centerline.Right"/>.</summary>
    public (double x, double z) RiverRight(int i) => (-MathF.Cos(Heading[i]), MathF.Sin(Heading[i]));

    /// <summary>Projection of (x, z) on the valley near index hint: σ and signed offset (+ = valley's right).</summary>
    public (double sigma, float offset, int idx) ProjectOnValley(double x, double z, int hint)
    {
        int best = hint; double bd = double.MaxValue;
        for (int i = Math.Max(0, hint - 60); i < Math.Min(VX.Count, hint + 60); i++)
        {
            double dx = x - VX[i], dz = z - VZ[i], d = dx * dx + dz * dz;
            if (d < bd) { bd = d; best = i; }
        }
        float h = VHeading[best];
        double tx = MathF.Sin(h), tz = MathF.Cos(h);           // valley direction
        double px = x - VX[best], pz = z - VZ[best];
        double along = px * tx + pz * tz;
        double off = px * tz - pz * tx;                         // right = (cos h, −sin h)
        return (best * ValleyStep + along, (float)off, best);
    }

    // ================================================================ river

    void StartRiver()
    {
        X.Add(VX[0]); Z.Add(VZ[0]); _vIdx = 0;
        AddSample(0.0, 0f, VHeading[0], 0f);
    }

    /// <summary>Hydraulic half-width for the discharge (∝ Q^0.5, capped for the 1.2 km corridor), × the reach's width.</summary>
    /// <summary>Extra friction slope in rapids, × the bed's (tuned so rapid segments calibrate like plain ones).</summary>
    public static float RapidLoss = 0.25f;

    float BaseHalf(float q, in ReachParams reach) => MathF.Min(60f, 18f * MathF.Sqrt(q / Settings.StartDischarge)) * reach.Width;

    /// <param name="turn">−1 … 1: how sharply the river turns here (1 at a bend's apex), + turning left (inner bank left).</param>
    void AddSample(double sigma, float offset, float heading, float turn)
    {
        var s = Settings;
        float lake = FeatureWeight(FeatureKind.Lake, sigma);
        float riffle = FeatureWeight(FeatureKind.Riffle, sigma);
        float split = FeatureWeight(FeatureKind.Split, sigma);
        float rapid = FeatureWeight(FeatureKind.Rapid, sigma);
        float q = DischargeAt(sigma);
        float qr = q / s.StartDischarge;
        var reach = ReachAt(sigma);

        // Width that breathes: on two scales, wider pools at bend apexes, narrower crossings between bends, and now and
        // then a pinch (narrower, deeper, faster). At an apex the inner side gives way to a point bar and the outer
        // side scours wider. (Lakes and splits shape their own widths.)
        float apex = MathF.Abs(turn), plain = (1f - lake) * (1f - split);
        float breathe = 1f + 0.18f * Noise.Fbm1(_sAlong / 160f, s.Seed + 3) + 0.14f * Noise.Fbm1(_sAlong / 650f, s.Seed + 4);
        float pinch = MathUtil.Smoothstep(0.16f, 0.3f, Noise.Fbm1((float)sigma / 1300f, s.Seed + 21)) * plain;
        float baseHalf = BaseHalf(q, reach) * breathe * (0.94f + 0.16f * apex) * (1f - 0.35f * pinch);
        float inner = 1f - 0.22f * apex * plain, outer = 1f + 0.12f * apex * plain;
        float hl = baseHalf * (turn > 0f ? inner : outer), hr = baseHalf * (turn > 0f ? outer : inner);
        foreach (var f in Features)
        {
            if (f.Kind != FeatureKind.Lake) continue;
            float w = f.Weight(sigma);
            if (w <= 0f) continue;
            float big = f.Size * (1f + 0.3f * Noise.Fbm1((float)sigma / 150f, f.Seed)), small = 0.5f * big;
            if (f.Side > 0) { hr += big * w; hl += small * w; } else { hl += big * w; hr += small * w; }
        }
        hl += 17f * split; hr += 17f * split;
        hl *= 1f - 0.1f * riffle; hr *= 1f - 0.1f * riffle;
        hl *= 1f - 0.1f * rapid; hr *= 1f - 0.1f * rapid;     // a rapid squeezes the river
        float depth = MathF.Min(6f, 2.6f * MathF.Pow(qr, 0.35f)) * (1f + 0.1f * Noise.Fbm1(_sAlong / 220f, s.Seed + 7))
                    * reach.Depth * (1f + 0.15f * apex * plain) * (1f + 0.35f * pinch);   // pools at bends, deep in pinches
        hl = MathF.Max(hl, 6f); hr = MathF.Max(hr, 6f);
        depth = MathUtil.Lerp(depth, 2f * depth, lake);
        depth = MathUtil.Lerp(depth, 1.05f, riffle);
        depth = MathUtil.Lerp(depth, 1.3f, rapid);

        // Water level: friction slope from the same conveyance the 2D flow solver uses, ∫ h^(5/3) dy across the bed
        // profile segments carve (ReachGenerator.ProfileDepth), so Q = ∫h^(5/3)dy · √S / n. (The textbook A·R^(2/3)
        // underestimates this profile's conveyance by ~1.6×, which put metres of water-level step at every seam.)
        float eta;
        if (Eta.Count == 0) eta = s.StartEta;
        else
        {
            float conv = SideConveyance(HalfLeft[^1], MaxDepth[^1]) + SideConveyance(HalfRight[^1], MaxDepth[^1]);
            float sf = Discharge[^1] * s.Manning / conv;
            // A rapid's boulders and ledges hold the water back far more than its bed alone (the 2D solve sees them):
            // extra head loss, so the course's level agrees with what its segment will compute.
            float rocks = 1f + RapidLoss * FeatureWeight(FeatureKind.Rapid, Sigma[^1]);
            eta = Eta[^1] - sf * sf * rocks * Centerline.Step;
        }

        Heading.Add(heading); HalfLeft.Add(hl); HalfRight.Add(hr); MaxDepth.Add(depth);
        Discharge.Add(q); Eta.Add(eta); Sigma.Add(sigma); Offset.Add(offset);
    }

    /// <summary>∫₀^1 (1 − (1 − u)²)^(5/3) du: the shelving part of the carved bed profile, in conveyance terms.</summary>
    static readonly float ShelfIntegral = ComputeShelfIntegral();
    static float ComputeShelfIntegral()
    {
        double sum = 0; const int n = 2000;
        for (int i = 0; i < n; i++) { double u = (i + 0.5) / n, p = 1 - (1 - u) * (1 - u); sum += Math.Pow(p, 5.0 / 3.0); }
        return (float)(sum / n);
    }

    /// <summary>
    /// Conveyance (∫ h^(5/3) dy) of one side of the channel, half-width <paramref name="half"/>, deepest depth
    /// <paramref name="depth"/>: shelving over τ = max(3, 0.55 × half) from the shore (a straight reach), then flat.
    /// </summary>
    static float SideConveyance(float half, float depth)
    {
        float tau = MathF.Min(half, MathF.Max(3f, 0.55f * half));
        return MathF.Pow(depth, 5f / 3f) * (tau * ShelfIntegral + (half - tau));
    }

    void StepRiver()
    {
        var s = Settings;
        double sigma = Sigma[^1];
        float offset = Offset[^1];
        float lake = FeatureWeight(FeatureKind.Lake, sigma);
        float riffle = FeatureWeight(FeatureKind.Riffle, sigma);
        float rapid = FeatureWeight(FeatureKind.Rapid, sigma);
        var reach = ReachAt(sigma);
        // The meanders' scale: the hydraulic width for the discharge and reach (not the breathing width, which would
        // feed back into the bends); a lake doesn't stretch the meanders.
        float baseHalf = BaseHalf(Discharge[^1], reach) * (1f - 0.9f * lake);
        baseHalf = MathF.Max(baseHalf, 0.8f * MathF.Min(60f, 18f * MathF.Sqrt(Discharge[^1] / s.StartDischarge)));

        // Kinoshita meanders, as in the prototype, relative to the valley; the reach sets how strong and regular.
        float straight = CutWeight(sigma);   // runs straighter through segment cuts
        float ampNoise = 0.8f * Noise.Fbm1(_sAlong / 900f, s.Seed + 11) + 0.35f * Noise.Fbm1(_sAlong / 260f, s.Seed + 15);
        float amp = Math.Clamp(reach.AmpBase + reach.AmpVar * ampNoise, 0.15f, 1.95f)
                  * (1f - 0.92f * lake) * (1f - 0.6f * riffle) * (1f - 0.7f * rapid) * (1f - 0.85f * straight);
        // Along the river (not the valley): ×16 widths here ≈ 10–14 widths along the valley, as real meanders.
        float wavelength = 16f * 2f * baseHalf * reach.Wavelength * MathF.Max(0.45f, 1f + 0.6f * Noise.Fbm1(_sAlong / 600f, s.Seed + 12)
                                                                                  + 0.25f * Noise.Fbm1(_sAlong / 170f, s.Seed + 16));
        float skew = 0.06f * Noise.Fbm1(_sAlong / 800f, s.Seed + 14);
        _phase += MathF.PI * 2f * Centerline.Step / wavelength;
        float theta = amp * MathF.Sin(_phase) + amp * amp * amp * (0.02f * MathF.Cos(3f * _phase) - skew * MathF.Sin(3f * _phase));
        float restore = -1.1f * Math.Clamp(offset / 300f, -1f, 1f);
        float rel = theta * MathF.Cos(restore) + restore + reach.Irregular * Noise.Fbm1(_sAlong / 400f, s.Seed + 13);
        float heading = VHeading[Math.Min(_vIdx, VHeading.Count - 1)] + rel;
        // How sharply it turns (dθ/dφ of the meander curve): 1 at a tight bend's apex; + = heading increasing = turning
        // left, where the inner bank (the point bar) is on the left.
        float dTheta = amp * MathF.Cos(_phase) + amp * amp * amp * (-0.06f * MathF.Sin(3f * _phase) - 3f * skew * MathF.Cos(3f * _phase));
        float turn = Math.Clamp(dTheta / 1.6f, -1f, 1f);

        double x = X[^1] + MathF.Sin(heading) * Centerline.Step, z = Z[^1] + MathF.Cos(heading) * Centerline.Step;
        _sAlong += Centerline.Step;
        var (sig, off, idx) = ProjectOnValley(x, z, _vIdx);
        _vIdx = idx;
        X.Add(x); Z.Add(z);
        AddSample(sig, off, heading, turn);
    }

    // ================================================================ cuts

    /// <summary>
    /// Cuts are scheduled with the features, before the river is walked: as near the target segment length as
    /// possible, never within the clearance of a feature (MinFeatureGap > 2 × clearance guarantees room). The river
    /// then runs straighter through each cut (<see cref="CutWeight"/>), so it crosses the cut line once, in a plain
    /// channel, by construction.
    /// </summary>
    void ScheduleCuts(double upTo)
    {
        var s = Settings;
        while (Cuts[^1] < upTo)
        {
            double last = Cuts[^1], best = last + s.TargetSegment, bestScore = double.MaxValue;
            for (double c = last + s.MinSegment; c <= last + s.MaxSegment; c += 10.0)
            {
                if (!ClearOfFeatures(c)) continue;
                double score = Math.Abs(c - (last + s.TargetSegment));
                if (score < bestScore) { bestScore = score; best = c; }
            }
            Cuts.Add(best);
        }
    }

    public bool ClearOfFeatures(double c)
    {
        foreach (var f in Features)
            if (c > f.S0 - Settings.CutClearance && c < f.S1 + Settings.CutClearance) return false;
        return true;
    }

    /// <summary>1 within ±CutWindow of a cut, fading out over the next 150 m: how much the meanders calm down there.</summary>
    float CutWeight(double sigma)
    {
        float w = 0f;
        for (int k = Cuts.Count - 1; k >= 0; k--)
        {
            double d = Math.Abs(sigma - Cuts[k]);
            if (Cuts[k] < sigma - 1000.0) break;
            w = MathF.Max(w, MathUtil.Smoothstep(Settings.CutWindow + 150f, Settings.CutWindow, (float)d));
        }
        return w;
    }

    /// <summary>
    /// The river samples within ±window of σ = c are one contiguous run with σ strictly increasing: the river crosses
    /// the cut line once and doesn't loop back over it. Scans from sample <paramref name="from"/>.
    /// </summary>
    public bool CrossesOnce(double c, double window, int from = 0)
    {
        int first = -1, last = -1;
        for (int i = from; i < Count; i++)
        {
            if (Math.Abs(Sigma[i] - c) > window) continue;
            if (first < 0) first = i;
            else if (i != last + 1) return false;      // left the window and came back: a meander loop
            last = i;
        }
        if (first < 0) return false;
        for (int i = first + 1; i <= last; i++) if (Sigma[i] <= Sigma[i - 1]) return false;
        return true;
    }
}
