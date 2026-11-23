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
    public float Lakes = 0.25f, Splits = 0.7f, Riffles = 0.9f, Tributaries = 0.6f;
    public float MinFeatureGap = 600f;       // m between features (room for a cut and its clearance)
    // Segments (cuts).
    public float MinSegment = 1500f, MaxSegment = 4000f, TargetSegment = 2500f;
    public float CutClearance = 250f;        // no feature within this of a cut
    public float CutWindow = 200f;           // the river must cross the cut line once within ±this
}

public enum FeatureKind { Lake, Split, Riffle, Confluence }

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
        float total = s.Lakes + s.Splits + s.Riffles + s.Tributaries;
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
                : pick < s.Lakes + s.Splits + s.Riffles ? FeatureKind.Riffle : FeatureKind.Confluence;
            int side = H(3) < 0.5f ? -1 : 1;
            CourseFeature f = kind switch
            {
                FeatureKind.Lake => new(kind, start, start + 400.0 + 300f + 250f * H(2), 200f, side, 70f + 90f * H(4), s.Seed + 7000 + e),
                FeatureKind.Split => new(kind, start, start + 240.0 + 60f * H(2), 60f, side, 17f, s.Seed + 7000 + e),
                FeatureKind.Riffle => new(kind, start, start + 300.0 + 120f * H(2), 60f, side, 0f, s.Seed + 7000 + e),
                // Size: share of Q added. Mostly creeks (+2–5 %), now and then a real river (up to +15 %).
                _ => new(kind, start, start + 300.0, 150f, side, 0.02f + 0.13f * H(4) * H(4) * H(4), s.Seed + 7000 + e),
            };
            Features.Add(f);
            _scheduledTo = f.S1;
        }
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
        AddSample(0.0, 0f, VHeading[0]);
    }

    void AddSample(double sigma, float offset, float heading)
    {
        var s = Settings;
        float lake = FeatureWeight(FeatureKind.Lake, sigma);
        float riffle = FeatureWeight(FeatureKind.Riffle, sigma);
        float split = FeatureWeight(FeatureKind.Split, sigma);
        float q = DischargeAt(sigma);
        float qr = q / s.StartDischarge;

        // Hydraulic geometry: width ∝ Q^0.5, depth ∝ Q^0.35 (capped: the corridor is 1.2 km wide for now).
        float baseHalf = MathF.Min(60f, 18f * MathF.Sqrt(qr)) * (1f + 0.12f * Noise.Fbm1(_sAlong / 300f, s.Seed + 3));
        float hl = baseHalf, hr = baseHalf;
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
        float depth = MathF.Min(6f, 2.6f * MathF.Pow(qr, 0.35f)) * (1f + 0.1f * Noise.Fbm1(_sAlong / 220f, s.Seed + 7));
        depth = MathUtil.Lerp(depth, 2f * depth, lake);
        depth = MathUtil.Lerp(depth, 1.05f, riffle);

        // Water level: Manning over the cross-section (mean depth ≈ 0.62 × max for the bed profile we carve).
        float eta;
        if (Eta.Count == 0) eta = s.StartEta;
        else
        {
            float width = HalfLeft[^1] + HalfRight[^1], meanD = 0.62f * MaxDepth[^1];
            float area = width * meanD, radius = area / (width + 2f * meanD);
            float sf = Discharge[^1] * s.Manning / (area * MathF.Pow(radius, 2f / 3f));
            eta = Eta[^1] - sf * sf * Centerline.Step;
        }

        Heading.Add(heading); HalfLeft.Add(hl); HalfRight.Add(hr); MaxDepth.Add(depth);
        Discharge.Add(q); Eta.Add(eta); Sigma.Add(sigma); Offset.Add(offset);
    }

    void StepRiver()
    {
        var s = Settings;
        double sigma = Sigma[^1];
        float offset = Offset[^1];
        float lake = FeatureWeight(FeatureKind.Lake, sigma);
        float riffle = FeatureWeight(FeatureKind.Riffle, sigma);
        float baseHalf = 0.5f * (HalfLeft[^1] + HalfRight[^1]) * (1f - 0.9f * lake);   // a lake doesn't stretch the meanders
        baseHalf = MathF.Max(baseHalf, 0.8f * MathF.Min(60f, 18f * MathF.Sqrt(Discharge[^1] / s.StartDischarge)));

        // Kinoshita meanders, as in the prototype, relative to the valley.
        float straight = CutWeight(sigma);   // runs straighter through segment cuts
        // Over a whole journey the prototype's narrow ranges read as a regular spring: here loops vary from
        // lazy bends to tight loops (amplitude) and from short to long (wavelength), on two scales.
        float ampNoise = 0.8f * Noise.Fbm1(_sAlong / 900f, s.Seed + 11) + 0.35f * Noise.Fbm1(_sAlong / 260f, s.Seed + 15);
        float amp = Math.Clamp(1.35f + ampNoise, 0.55f, 1.95f)
                  * (1f - 0.92f * lake) * (1f - 0.6f * riffle) * (1f - 0.85f * straight);
        // Along the river (not the valley): ×16 widths here ≈ 10–14 widths along the valley, as real meanders.
        float wavelength = 16f * 2f * baseHalf * MathF.Max(0.45f, 1f + 0.6f * Noise.Fbm1(_sAlong / 600f, s.Seed + 12)
                                                                    + 0.25f * Noise.Fbm1(_sAlong / 170f, s.Seed + 16));
        float skew = 0.06f * Noise.Fbm1(_sAlong / 800f, s.Seed + 14);
        _phase += MathF.PI * 2f * Centerline.Step / wavelength;
        float theta = amp * MathF.Sin(_phase) + amp * amp * amp * (0.02f * MathF.Cos(3f * _phase) - skew * MathF.Sin(3f * _phase));
        float restore = -1.1f * Math.Clamp(offset / 300f, -1f, 1f);
        float rel = theta * MathF.Cos(restore) + restore + 0.25f * Noise.Fbm1(_sAlong / 400f, s.Seed + 13);
        float heading = VHeading[Math.Min(_vIdx, VHeading.Count - 1)] + rel;

        double x = X[^1] + MathF.Sin(heading) * Centerline.Step, z = Z[^1] + MathF.Cos(heading) * Centerline.Step;
        _sAlong += Centerline.Step;
        var (sig, off, idx) = ProjectOnValley(x, z, _vIdx);
        _vIdx = idx;
        X.Add(x); Z.Add(z);
        AddSample(sig, off, heading);
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
