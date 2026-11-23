# Endless streaming — plan

*2026-11-23, Sage. Status: accepted; Tom's decisions in §9 (all four as recommended). Phase 0 done (D12), phase 1 (D13), phase 2 (D14). Next: phase 3.*

Today the world is one fixed 1.2 × 3.2 km reach laid along +z. This plan turns it into a river that never ends
and can change direction, generated ahead of the raft and dropped behind it, with the water, terrain, raft
physics and look we have now. Design basis: DESIGN_PILLARS.md §3 P3, §5 (river-first pipeline), §12 (floating
origin, determinism), SANDBOX_SETTINGS.md (`World.*` frequencies, `ClimateZoneLengthKm`).

---

## 1. Goals and non-goals

**Goals**
- The river goes on forever, deterministic from the seed: same seed, same river, on any PC.
- Its course turns: hours heading east, a long bend to the southwest after a confluence (Tom's question).
- No loading screens, no hitches: the next stretch is ready long before the raft gets there.
- Any point of a journey can be loaded directly (a save 400 km downstream must not regenerate the first 400 km).
- Everything we have keeps working: flow solve, eddies, foam, waves, raft physics, shaders, captures, tests.
- Going back upstream (DESIGN_PILLARS §6.2) works: stretches behind are regenerated or loaded from a disk cache.

**Not in this plan** (they plug into it later): new segment types (rapids, waterfalls, labyrinths, braided), climate
zones and biomes, settlements, secret places, weather. The plan leaves a slot for each.

---

## 2. The shape of it

```
seed
 └─ Course      1D, cheap: the river's path for the whole journey (valley heading, centre line, widths,
 │              discharge, water level, which features go where). Generated incrementally, cached.
 └─ Segment k   2D, expensive: one stretch of 1.5–4 km on its own rotated grid. A pure function of
 │              (seed, k, course): water shape, flow solve, eddies, terrain, foam, rocks, vegetation.
 └─ Streamer    Godot: keeps segments k−1 … k+2 alive around the raft, generates ahead on a worker thread,
                builds nodes a few at a time, drops what's far behind, shifts the floating origin.
```

The key move is splitting today's `ReachGenerator.Generate` into a **Course** (what used to be the hard-coded
layout constants: `LakeA0`, `SplitA0`, `RiffleA0`, the tributary position…) and a **Segment generator** (everything
else, unchanged in spirit).

---

## 3. The Course (1D, whole journey)

Generated in steps of `Centerline.Step` (2 m) along the river, as now, but with a **valley heading** that drifts:

- **Valley heading θ(s)**: wanders with low-frequency noise, its rate limited to a minimum turning radius of ~3 km
  (the meander belt, ±300 m, must fit inside every bend without the corridor folding onto itself). A restoring
  pull keeps θ within ±90° of a general **bearing** (decision D1, §9). Within ±90° the river always makes progress
  along the bearing, so the corridor can never loop back over itself: no overlap checks needed.
- **Meanders** ride on the valley axis exactly as today (Kinoshita curve, wavelength from width).
- **Discharge** grows downstream with drainage area and jumps at each tributary; widths and depths follow it
  (hydraulic geometry: width ∝ Q^0.5, depth ∝ Q^0.4), so the river visibly grows over a journey.
- **Water level** η(s): a profile of gentle reaches, steeper riffles, flat lakes, later rapids and falls.
  It only ever goes down. At 1 m/km a 1,000 km journey drops 1 km: fine in float, no vertical rebasing needed yet.
- **Feature schedule**: lakes, island splits, riffles, tributaries, (later rapids, falls, labyrinths) placed along
  s with spacing and probabilities from the sandbox `World.*Frequency` settings, never across a segment cut.
- **Segment cuts**: placed every 1.5–4 km *in plain single-channel stretches* (no island, lake or confluence within
  ~250 m), so both sides of a cut see a simple channel.

Cost: the course is a 1D walk, ~0.5 M samples per 1,000 km: milliseconds per segment. It is generated forward from
the start and cached (in memory and in the save), so loading segment 500 only needs the course, not segments 0–499.

Lives in `Game/Core/Course.cs`, headless-testable. The climate arc (`ClimateZoneLengthKm`) later reads s from it.

---

## 4. Segments (2D, one stretch each)

Each segment k has:
- a **frame**: origin (double-precision world position) and rotation (the valley heading across the segment), and
  a rectangular 2 m grid in that frame: width = corridor (1.2 km as now), length = segment length + 2 × 150 m overlap;
- its slice of the course centre line (and any tributary), transformed into the frame;
- the same pipeline as today: water mask → shore distance → depth → flow solve → lee re-solve and eddies → surface
  over land → terrain → rocks' approach currents → foam. `ReachField` becomes the per-segment field, plus its frame.

### 4.1 Boundaries without a chain (the important part)
If segment k took its inflow from segment k−1's solved outflow, every segment would depend on all the ones before
it. Instead **both sides of a cut agree on the cut from the course alone**:
- the upstream segment holds the water level at its outlet cut to the course's η(s_cut) (as today's outlet row);
- the downstream segment feeds the course's discharge across its inlet cut, distributed by conveyance over the
  channel cross-section (exactly today's `Distribute`).

Because cuts sit in plain channels, both sides see the same cross-section, so the two solutions meet within
centimetres and a few cm/s. The 150 m overlap absorbs the rest: inside it, everything is **blended** between the
two segments (water level, current, depth, terrain height, foam), with weights crossing over at the cut. The blend
is computed once when a segment is built, so meshes, textures and physics all see the same seamless values.

Result: segment k is a pure function of (seed, k, course). Any segment can be generated alone, in any order, on
any thread.

### 4.2 Cost
Today's 3.2 km reach takes ~5 s on Tom's PC (35 s on the cloud's software CPU). A 2.5 km segment ≈ 4 s here. The
raft covers 2.5 km in ~35 min at 1.2 m/s, so generating two segments ahead leaves a huge margin, even on slow CPUs
or at the fast end of the current. Multigrid-preconditioned CG (HANDOFF #2) stays optional.

Memory per segment ~60–80 MB (field arrays, textures, meshes); four alive ≈ 300 MB. Fine; solver scratch is freed
after generation, and float arrays nothing reads at runtime are dropped.

---

## 5. Streaming in Godot

`Game/World/WorldStreamer.cs` (new) owns the segments:
- **Window**: k−1 (behind, for looking back), k (raft), k+1 and k+2 (ahead). When the raft crosses into k+1, it
  requests k+3 and frees k−1 (kept in the disk cache).
- **Worker thread** runs the pure C# segment generation; results come back as data. The **main thread** turns
  them into nodes in small slices per frame (terrain chunk, water chunk, vegetation multimesh, rocks) within a
  ~2 ms budget, so there are no hitches.
- **Disk cache** (`user://cache/seg_<seed>_<k>.bin`, `ReachCache` per segment, keyed by build like now): going
  back upstream, or reloading, skips the solve.
- **Water/terrain queries** (raft, player, pole, anchor): a `RiverWorld` façade replaces `ReachField` lookups.
  Given a world point it finds the owning segment by its frame and cuts, and samples there (blended in overlaps).
- **Materials per segment**: each segment's water and terrain materials get its own field textures and a
  world→segment transform uniform (shaders sample the field in segment-local coordinates instead of world xz).
- **Far land and horizon**: today's `FarLand` ring and `FarStrips` assume one grid along z. They become a coarse
  far-terrain band generated from the course (valley relief around the course axis), out to the horizon, so the
  turning valley looks right from afar. The mist fog volume becomes one per segment (rotated box).
- **Raft start / saves**: a journey save stores seed, course cache, segment index and the raft's pose in that
  segment's frame. Loading regenerates (or loads) just the window.

---

## 6. Floating origin and precision

Float32 positions lose millimetres beyond ~10 km and physics starts jittering around 20 km; a day of drifting is
~40 km. So:
- **World positions that matter long-term are doubles** in C# (course samples, segment origins, saves).
- **Godot scene coordinates are relative to a floating origin.** When the raft gets more than 1 km from it, the
  streamer shifts the world root (and the raft, player and any free bodies, between physics steps) by a whole
  number of metres and moves the origin.
- **Procedural patterns must not jump at a shift.** Ripple and foam textures, gusts, wind waves, boils and
  caustics use world coordinates. C# computes their phase offsets for the current origin in double precision
  (`fract(origin / tile)` per texture scale, `k·origin mod 2π` per sine term) and passes them as uniforms, and
  `SurfaceWaves` uses the same offsets, so the raft keeps riding the waves on screen. Test: a world point's water
  height is identical before and after a shift.

---

## 7. Phases

Each phase ends in something that runs, with the headless tests passing and a capture or map to look at.

| # | Phase | Done when |
|---|---|---|
| 0 | **Refactor, no visible change.** `ReachField` gets a frame. The prototype layout moves out of `ReachGenerator` into a hand-written course. Segment generation takes a course slice. | Today's reach regenerates identically from the new code path (tests, map diff). |
| 1 | **Course generator.** Valley heading with bearing and turn-radius limits, discharge growth, level profile, feature schedule from sandbox frequencies, cuts in plain stretches. | Headless: 50 km course map PNG; checks: never folds, min radius, η monotone, features never straddle cuts, deterministic. |
| 2 | **Independent segments + seams.** Rotated grids, boundary rule (§4.1), overlap blending. | Headless: 4 segments generated *out of order*, stitched map PNG; seam checks: η jump < 3 cm, current jump < 10 cm/s in the channel (2 m rasters differ a few degrees at curved banks), blended seams no rougher than the river's own bends, discharge conserved across cuts, no terrain step. ✔ D14 |
| 3 | **Streamer in Godot.** Window k−1…k+2, worker thread, sliced node building, `RiverWorld` façade, per-segment materials, disk cache. | Drift test runs 3 hours of game time unattended: no hitch > 8 ms, memory flat, the raft never grounds at a seam. |
| 4 | **Floating origin.** Root shifting, double positions, pattern phase offsets. | Drift test 60 km: no jitter, waves continuous across shifts (test), captures identical before/after a shift. |
| 5 | **Far land, mist, saves.** Course-based horizon, per-segment mist, journey save/load at any segment. | A save at km 40 loads in seconds; looking back up a long straight shows a believable valley. |

Phases 0–2 are pure C# (`Game/Core`) and fully testable headlessly; 3–5 are Godot-side. Each phase is its own
series of commits; the game stays playable throughout (phase 0 and 1 don't change what you see).

---

## 8. Risks

- **Seams.** The boundary rule makes them small; blending hides the rest. If a seam ever shows, the fallback is a
  wider overlap, or solving the overlap jointly from both sides (still independent of other segments).
- **Generation hitches on the main thread** (mesh upload, multimesh creation). Mitigation: strict per-frame budget,
  smaller chunks.
- **Feature generation that needs the whole reach** (eddy lee tracing looks up to 240 m upstream, foam advects
  ~125 s of flow). Overlap of 150 m plus starting foam at the inlet from the course's expected foam covers it;
  watch lee zones right below a cut (cuts are in plain stretches, so there should be none).
- **Raft or player exactly on a seam or during an origin shift.** The façade blends; shifts happen between physics
  steps; covered by the drift test.
- **Weak PCs.** Generation is background work with lots of slack (§4.2). If needed: generate further ahead, or
  multigrid CG.

---

## 9. Decisions (Tom, 2026-11-23)

**Decided:** D1 a general bearing with swings up to ±90°, never back the way it came · D2 bearing south ·
D3 sweeping turns every 10–30 km, sharper now and then at confluences and gorges · D4 one segment kept behind the
raft, older ones from the disk cache.

| | Question | Options | Sage's recommendation |
|---|---|---|---|
| D1 | How far may the river's overall course wander? | **A.** A general bearing (south-ish, matching the climate arc) with swings up to ±90°: it can run east or west for hours, never back north. **B.** Free: it may loop back the way it came. | **A.** Fits the climate arc, the sun/latitude model and a map that drifts down the page, and it guarantees the valley never runs into itself. B is possible but needs overlap avoidance and makes the climate arc meaningless. |
| D2 | Which way is the bearing? | South (warmer downstream: highlands → jungle), or another compass direction. | South. |
| D3 | How fast should the course change direction? | Big sweeping turns every 10–30 km, or restless turns every few km. | Sweeping (10–30 km), with occasional sharper turns at confluences and gorges. |
| D4 | How much stays loaded behind the raft? | One segment (~2.5 km) or two. | One; older ones come back from the disk cache within a second or two when going upstream. |

Technical choices in this plan (segment size, overlap, boundary rule, floating-origin approach) are mine to make
and will be logged in DECISIONS.md as each phase lands.
