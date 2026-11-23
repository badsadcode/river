# RIVER — Sandbox settings (`game_sandbox_settings.json`)

*v0.2 · 2026-11-23*

**Status:** loader implemented (`Game/Core/SandboxSettings.cs`, tested in `Tests/Core.Tests`). The game reads
`%APPDATA%\Godot\app_userdata\River\game_sandbox_settings.json` (written with the Explorer preset on first launch).
In use so far: `Seed`, `River.CurrentStrength` (scales the river's discharge). The rest is stored and validated,
waiting for the systems that use it.

Every new journey is created from a **sandbox settings** file. Instead of us deciding one "correct" scale,
darkness or difficulty, the player picks a **preset** and can override any value. The settings are copied into
the save when the journey starts; most are then **locked** (marked 🔒) so a journey stays what it was created as.

## Principles

- **Presets first, knobs second.** The new-journey screen shows presets; "Advanced" reveals every value
  (same idea as Thornwake's *Auto* parameters).
- **Every value has a sane default** and a documented range. Unknown keys are ignored with a warning; missing
  keys take the preset's value. A broken file never blocks the game: it falls back to the preset.
- **Hand-editable JSON** in the user folder. Players can save their own presets
  (`%APPDATA%\Godot\app_userdata\River\sandbox_presets\<id>.json`).
- **Deterministic:** world generation only depends on `seed` + the 🔒 world values.

## Built-in presets

| Preset | Idea |
|---|---|
| **Drifter** | Gentle. Low survival pressure, darkness *Melancholy*, persistent journey. For drifting and collecting. |
| **Explorer** (default) | The intended experience. Real survival, darkness *Grim*, persistent journey. |
| **Heart of Darkness** | Hard mode: harsh survival, darkness *Abyss*, uncanny *Hints*, **permadeath**. |
| **Custom** | Anything. |

## Example file

```json
{
  "Version": 1,
  "Preset": "explorer",
  "Name": "My journey",
  "Seed": 184467,

  "Mode": {
    "Permadeath": false,
    "SaveAnywhere": true,
    "ReturnOfTheDead": true
  },

  "World": {
    "DistanceScale": 1.0,
    "ClimateZoneLengthKm": 1500,
    "StartingClimate": "highlands",
    "RiverComplexity": 1.0,
    "IslandFrequency": 1.0,
    "LabyrinthFrequency": 1.0,
    "RapidsFrequency": 1.0,
    "WaterfallFrequency": 1.0,
    "TributaryFrequency": 1.0,
    "SecretPlaceRarity": 1.0,
    "SettlementFrequency": 1.0,
    "LandCorridorKm": 4.0
  },

  "Time": {
    "DayLengthMinutes": 60,
    "SeasonLengthDays": 20,
    "StartingSeason": "spring",
    "WinterFreezeUp": true
  },

  "River": {
    "CurrentStrength": 1.0,
    "UpstreamDifficulty": 1.0,
    "FloodSeverity": 1.0,
    "HazardDensity": 1.0
  },

  "Survival": {
    "Hunger": 1.0,
    "Thirst": 1.0,
    "Cold": 1.0,
    "Fatigue": 1.0,
    "Illness": 1.0,
    "InjurySeverity": 1.0,
    "SpoilageRate": 1.0,
    "RaftWear": 1.0,
    "ResourceAbundance": 1.0,
    "WildlifeAggression": 1.0
  },

  "Mind": {
    "Composure": true,
    "ComposureDrain": 1.0,
    "Darkness": "grim",
    "Uncanny": "off"
  },

  "Interface": {
    "QuestMarkers": false,
    "MapAutoDraw": "seen",
    "ShowCompass": true
  }
}
```

## Values

### Mode
| Key | Default | Notes |
|---|---|---|
| `Permadeath` 🔒 | `false` | Death ends the journey and deletes its save. |
| `SaveAnywhere` 🔒 | `true` | `false`: save only when sleeping on the raft or in camp. |
| `ReturnOfTheDead` 🔒 | `true` | Permadeath only: later journeys **on the same seed** may find your previous wreck, belongings and journal downstream. Your old expedition becomes someone else's discovery. |

### World 🔒
| Key | Default | Range | Notes |
|---|---|---|---|
| `DistanceScale` | `1.0` | 0.25–1.0 | 1.0 = real distances (raft ≈ 4–8 km/h). Lower = everything (river lengths, meander wavelengths, valleys) compressed so landscapes change faster. Physics and walking stay real-scale. |
| `ClimateZoneLengthKm` | `1500` | 200–10000 | How far until the climate noticeably shifts (highlands → temperate → wetlands → jungle). |
| `StartingClimate` | `highlands` | enum | Where on the climate arc you start. |
| `RiverComplexity` | `1.0` | 0–2 | Overall weight of non-simple channel forms (braided, multi-channel, lakes). |
| `IslandFrequency`, `LabyrinthFrequency`, `RapidsFrequency`, `WaterfallFrequency`, `TributaryFrequency` | `1.0` | 0–3 | Multipliers on each feature. |
| `SecretPlaceRarity` | `1.0` | 0.25–4 | Higher = rarer. Keep them luck, not content. |
| `SettlementFrequency` | `1.0` | 0–3 | 0 = utterly alone. |
| `LandCorridorKm` | `4.0` | 1–10 | Walkable land each side of the river. |

### Time
| Key | Default | Notes |
|---|---|---|
| `DayLengthMinutes` | `60` | Real minutes per in-game day. |
| `SeasonLengthDays` 🔒 | `20` | In-game days per season. |
| `StartingSeason` 🔒 | `spring` | |
| `WinterFreezeUp` 🔒 | `true` | `false` = the river never freezes (milder climate fiction). |

### River
| Key | Default | Notes |
|---|---|---|
| `CurrentStrength` 🔒 | `1.0` | Multiplier on flow speed. |
| `UpstreamDifficulty` | `1.0` | How hard travel against the current is (see design pillars §6.2). |
| `FloodSeverity` 🔒 | `1.0` | Spring flood height and debris. |
| `HazardDensity` 🔒 | `1.0` | Snags, shoals, rocks. |

### Survival
Multipliers; `0` disables that need entirely. `ResourceAbundance` is inverted (higher = more food/materials).

### Mind
| Key | Default | Values | Notes |
|---|---|---|---|
| `Composure` | `true` | bool | The composure system on/off. |
| `ComposureDrain` | `1.0` | 0–3 | |
| `Darkness` 🔒 | `grim` | `melancholy`, `grim`, `abyss` | Which event and discovery pools are allowed (see below). |
| `Uncanny` 🔒 | `off` | `off`, `hints`, `open` | Whether anything beyond the natural ever happens. |

**Darkness levels**
- **Melancholy** — sadness, loss, abandoned places, natural death, bad weather. Nothing graphic.
- **Grim** — disease, starvation, violence between people, remains, colonial rot, injuries with lasting effects.
- **Abyss** — *really dark*: atrocities discovered, madness, cannibalism and desperation in the backstory of
  places, horrible deaths, the journal turning against you. Still never gratuitous for its own sake.

**Uncanny**
- **Off** — everything is explainable.
- **Hints** — things that are *probably* fever, fog and exhaustion. Never confirmed.
- **Open** — the river is genuinely strange somewhere far downstream.

### Interface
| Key | Default | Notes |
|---|---|---|
| `QuestMarkers` | `false` | Off by default, always (P8). |
| `MapAutoDraw` | `seen` | `none` (you draw), `seen` (charts what you saw), `full`. |
| `ShowCompass` | `true` | |

## Implementation notes
- C# `SandboxSettings` record with nested groups; `System.Text.Json` with defaults on every property.
- `SandboxSettings.Load(path)` → validate → clamp to ranges → log warnings.
- Copied verbatim into the save header. 🔒 values read only from the save, never from the file again.
- Settings get a `Version`; migrations upgrade old files.
