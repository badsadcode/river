using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace River.Core;

/// <summary>
/// game_sandbox_settings.json — how a journey is set up (docs/SANDBOX_SETTINGS.md).
/// Every value has a default; loading never fails: unknown keys are ignored, missing keys keep the preset's value,
/// out-of-range values are clamped, and each fix is reported as a warning.
/// </summary>
public sealed class SandboxSettings
{
    public int Version { get; set; } = 1;
    public string Preset { get; set; } = "explorer";
    public string Name { get; set; } = "My journey";
    public int Seed { get; set; } = 1890;
    public ModeGroup Mode { get; set; } = new();
    public WorldGroup World { get; set; } = new();
    public TimeGroup Time { get; set; } = new();
    public RiverGroup River { get; set; } = new();
    public SurvivalGroup Survival { get; set; } = new();
    public MindGroup Mind { get; set; } = new();
    public InterfaceGroup Interface { get; set; } = new();

    public sealed class ModeGroup
    {
        public bool Permadeath { get; set; }
        public bool SaveAnywhere { get; set; } = true;
        public bool ReturnOfTheDead { get; set; } = true;
    }

    public sealed class WorldGroup
    {
        public float DistanceScale { get; set; } = 1f;
        public float ClimateZoneLengthKm { get; set; } = 1500f;
        public string StartingClimate { get; set; } = "highlands";
        public float RiverComplexity { get; set; } = 1f;
        public float IslandFrequency { get; set; } = 1f;
        public float LabyrinthFrequency { get; set; } = 1f;
        public float RapidsFrequency { get; set; } = 1f;
        public float WaterfallFrequency { get; set; } = 1f;
        public float TributaryFrequency { get; set; } = 1f;
        public float SecretPlaceRarity { get; set; } = 1f;
        public float SettlementFrequency { get; set; } = 1f;
        public float LandCorridorKm { get; set; } = 4f;
    }

    public sealed class TimeGroup
    {
        public float DayLengthMinutes { get; set; } = 60f;
        public int SeasonLengthDays { get; set; } = 20;
        public string StartingSeason { get; set; } = "spring";
        public bool WinterFreezeUp { get; set; } = true;
    }

    public sealed class RiverGroup
    {
        public float CurrentStrength { get; set; } = 1f;
        public float UpstreamDifficulty { get; set; } = 1f;
        public float FloodSeverity { get; set; } = 1f;
        public float HazardDensity { get; set; } = 1f;
    }

    public sealed class SurvivalGroup
    {
        public float Hunger { get; set; } = 1f;
        public float Thirst { get; set; } = 1f;
        public float Cold { get; set; } = 1f;
        public float Fatigue { get; set; } = 1f;
        public float Illness { get; set; } = 1f;
        public float InjurySeverity { get; set; } = 1f;
        public float SpoilageRate { get; set; } = 1f;
        public float RaftWear { get; set; } = 1f;
        public float ResourceAbundance { get; set; } = 1f;
        public float WildlifeAggression { get; set; } = 1f;
    }

    public sealed class MindGroup
    {
        public bool Composure { get; set; } = true;
        public float ComposureDrain { get; set; } = 1f;
        public string Darkness { get; set; } = "grim";
        public string Uncanny { get; set; } = "off";
    }

    public sealed class InterfaceGroup
    {
        public bool QuestMarkers { get; set; }
        public string MapAutoDraw { get; set; } = "seen";
        public bool ShowCompass { get; set; } = true;
    }

    // ------------------------------------------------------------------ presets

    public static readonly string[] PresetIds = { "drifter", "explorer", "heart_of_darkness" };

    /// <summary>A built-in preset ("custom" = explorer defaults).</summary>
    public static SandboxSettings FromPreset(string id)
    {
        var s = new SandboxSettings { Preset = id };
        switch (id)
        {
            case "drifter":
                s.Mind.Darkness = "melancholy";
                s.Mind.ComposureDrain = 0.5f;
                s.Survival.Hunger = s.Survival.Thirst = s.Survival.Cold = s.Survival.Fatigue = 0.5f;
                s.Survival.Illness = 0.3f; s.Survival.InjurySeverity = 0.5f; s.Survival.RaftWear = 0.5f;
                s.Survival.ResourceAbundance = 1.5f; s.Survival.WildlifeAggression = 0.5f;
                s.River.HazardDensity = 0.6f;
                break;
            case "heart_of_darkness":
                s.Mode.Permadeath = true; s.Mode.SaveAnywhere = false;
                s.Mind.Darkness = "abyss"; s.Mind.Uncanny = "hints"; s.Mind.ComposureDrain = 1.6f;
                s.Survival.Hunger = s.Survival.Thirst = s.Survival.Cold = s.Survival.Fatigue = 1.5f;
                s.Survival.Illness = 1.8f; s.Survival.InjurySeverity = 1.6f; s.Survival.RaftWear = 1.5f;
                s.Survival.ResourceAbundance = 0.7f; s.Survival.WildlifeAggression = 1.5f;
                s.River.HazardDensity = 1.5f; s.River.UpstreamDifficulty = 1.4f;
                s.World.SettlementFrequency = 0.5f;
                break;
        }
        return s;
    }

    // ------------------------------------------------------------------ load / save

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true, NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>
    /// Reads settings from JSON text. Keys missing from the file take the value of the file's "Preset"
    /// (default explorer). Never throws: a broken file falls back to the preset, with a warning.
    /// </summary>
    public static SandboxSettings Parse(string json, List<string> warnings = null)
    {
        warnings ??= new List<string>();
        string preset = "explorer";
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }); }
        catch (JsonException e) { warnings.Add($"settings file is not valid JSON ({e.Message}); using the explorer preset"); return FromPreset(preset).Validated(warnings); }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object) { warnings.Add("settings file is not a JSON object; using the explorer preset"); return FromPreset(preset).Validated(warnings); }
            foreach (var p in doc.RootElement.EnumerateObject())
                if (string.Equals(p.Name, "Preset", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String) preset = p.Value.GetString();
            bool known = Array.IndexOf(PresetIds, preset) >= 0;
            if (!known && preset != "custom") warnings.Add($"unknown preset '{preset}': missing values taken from explorer");
            var s = FromPreset(known ? preset : "explorer");
            s.Preset = preset;
            Overlay(s, doc.RootElement, "", warnings);
            return s.Validated(warnings);
        }
    }

    public static SandboxSettings Load(string path, List<string> warnings = null)
    {
        warnings ??= new List<string>();
        if (!File.Exists(path)) { warnings.Add($"{path} not found; using the explorer preset"); return FromPreset("explorer"); }
        return Parse(File.ReadAllText(path), warnings);
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, ToJson());
    }

    /// <summary>Copies every property present in the JSON onto the object (recursively), warning about the rest.</summary>
    static void Overlay(object target, JsonElement el, string path, List<string> warnings)
    {
        var props = target.GetType().GetProperties();
        foreach (var jp in el.EnumerateObject())
        {
            var prop = Array.Find(props, p => string.Equals(p.Name, jp.Name, StringComparison.OrdinalIgnoreCase));
            string where = path + jp.Name;
            if (prop == null) { warnings.Add($"unknown setting '{where}' ignored"); continue; }
            var t = prop.PropertyType;
            try
            {
                if (t.IsClass && t != typeof(string))
                {
                    if (jp.Value.ValueKind == JsonValueKind.Object) Overlay(prop.GetValue(target)!, jp.Value, where + ".", warnings);
                    else warnings.Add($"'{where}' should be a group {{ … }}; ignored");
                }
                else prop.SetValue(target, jp.Value.Deserialize(t, Json));
            }
            catch (Exception) { warnings.Add($"'{where}' has an invalid value; kept {prop.GetValue(target)}"); }
        }
    }

    // ------------------------------------------------------------------ validation

    /// <summary>Clamps every value into its documented range (SANDBOX_SETTINGS.md); returns this.</summary>
    public SandboxSettings Validated(List<string> warnings = null)
    {
        warnings ??= new List<string>();
        float C(string name, float v, float lo, float hi)
        {
            if (float.IsNaN(v)) { warnings.Add($"{name} is not a number; set to {lo}"); return lo; }
            if (v < lo || v > hi) { float c = Math.Clamp(v, lo, hi); warnings.Add($"{name} = {v} is outside {lo}–{hi}; clamped to {c}"); return c; }
            return v;
        }
        string E(string name, string v, params string[] allowed)
        {
            foreach (var a in allowed) if (string.Equals(a, v, StringComparison.OrdinalIgnoreCase)) return a;
            warnings.Add($"{name} = '{v}' is not one of {string.Join(", ", allowed)}; set to {allowed[0]}");
            return allowed[0];
        }

        var w = World;
        w.DistanceScale = C("World.DistanceScale", w.DistanceScale, 0.25f, 1f);
        w.ClimateZoneLengthKm = C("World.ClimateZoneLengthKm", w.ClimateZoneLengthKm, 200f, 10000f);
        w.StartingClimate = E("World.StartingClimate", w.StartingClimate, "highlands", "temperate", "wetlands", "jungle");
        w.RiverComplexity = C("World.RiverComplexity", w.RiverComplexity, 0f, 2f);
        w.IslandFrequency = C("World.IslandFrequency", w.IslandFrequency, 0f, 3f);
        w.LabyrinthFrequency = C("World.LabyrinthFrequency", w.LabyrinthFrequency, 0f, 3f);
        w.RapidsFrequency = C("World.RapidsFrequency", w.RapidsFrequency, 0f, 3f);
        w.WaterfallFrequency = C("World.WaterfallFrequency", w.WaterfallFrequency, 0f, 3f);
        w.TributaryFrequency = C("World.TributaryFrequency", w.TributaryFrequency, 0f, 3f);
        w.SecretPlaceRarity = C("World.SecretPlaceRarity", w.SecretPlaceRarity, 0.25f, 4f);
        w.SettlementFrequency = C("World.SettlementFrequency", w.SettlementFrequency, 0f, 3f);
        w.LandCorridorKm = C("World.LandCorridorKm", w.LandCorridorKm, 1f, 10f);

        Time.DayLengthMinutes = C("Time.DayLengthMinutes", Time.DayLengthMinutes, 5f, 1440f);
        Time.SeasonLengthDays = (int)C("Time.SeasonLengthDays", Time.SeasonLengthDays, 3f, 120f);
        Time.StartingSeason = E("Time.StartingSeason", Time.StartingSeason, "spring", "summer", "autumn", "winter");

        River.CurrentStrength = C("River.CurrentStrength", River.CurrentStrength, 0.25f, 2f);
        River.UpstreamDifficulty = C("River.UpstreamDifficulty", River.UpstreamDifficulty, 0.25f, 3f);
        River.FloodSeverity = C("River.FloodSeverity", River.FloodSeverity, 0f, 3f);
        River.HazardDensity = C("River.HazardDensity", River.HazardDensity, 0f, 3f);

        var s = Survival;
        s.Hunger = C("Survival.Hunger", s.Hunger, 0f, 3f);
        s.Thirst = C("Survival.Thirst", s.Thirst, 0f, 3f);
        s.Cold = C("Survival.Cold", s.Cold, 0f, 3f);
        s.Fatigue = C("Survival.Fatigue", s.Fatigue, 0f, 3f);
        s.Illness = C("Survival.Illness", s.Illness, 0f, 3f);
        s.InjurySeverity = C("Survival.InjurySeverity", s.InjurySeverity, 0f, 3f);
        s.SpoilageRate = C("Survival.SpoilageRate", s.SpoilageRate, 0f, 3f);
        s.RaftWear = C("Survival.RaftWear", s.RaftWear, 0f, 3f);
        s.ResourceAbundance = C("Survival.ResourceAbundance", s.ResourceAbundance, 0.1f, 3f);
        s.WildlifeAggression = C("Survival.WildlifeAggression", s.WildlifeAggression, 0f, 3f);

        Mind.ComposureDrain = C("Mind.ComposureDrain", Mind.ComposureDrain, 0f, 3f);
        Mind.Darkness = E("Mind.Darkness", Mind.Darkness, "grim", "melancholy", "abyss");
        Mind.Uncanny = E("Mind.Uncanny", Mind.Uncanny, "off", "hints", "open");
        Interface.MapAutoDraw = E("Interface.MapAutoDraw", Interface.MapAutoDraw, "seen", "none", "full");
        return this;
    }
}
