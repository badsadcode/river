# Decisions

Technical decisions and why. Newest at the bottom. (Design decisions live in DESIGN_PILLARS.md §14.)

## D1 — River first, terrain second
The river's centre lines and water shape are generated first; bed, banks, floodplain and valley are shaped around
them. Thornwake's approach (terrain first, hydrology solves where water goes) suits finite maps but can't give an
endless, always-interesting river. Everything is a pure function of the seed.

## D2 — Water as a 2D shape on a grid, not "spine + width"
Water is a mask on a 2 m grid built from channel ribbons minus island shapes, so lakes, islands, splits and
labyrinths (Tom's sketch #1) are possible. A centre line with a width can't do that.

## D3 — Flow from a Manning-friction potential solve
Unit discharge q = h^(5/3)/(n√S)·(−∇η), mass conservation → elliptic equation for the surface η, Picard iterations
on the nonlinear conveyance, Jacobi-preconditioned CG. One solve gives the water surface (flat lakes, steeper
riffles) and the current (splits around islands, fast narrows, slack bays) consistently. No inertia, so no
eddies or recirculation behind obstacles: those are added on top (D11).
The bed is defined *relative to the solved surface* (designed depth below η), so depth is exactly as designed.

## D4 — Foam is precomputed and advected
Foam sources (high Froude number, current running into a shore, rocks breaking the surface) are found on the grid
and advected downstream with decay, so foam trails off behind rocks and island tips. Since D11, eddy lines (the
edges of lee zones in water faster than ~0.25 m/s) are foam sources too, and foam decays much more slowly inside
lee zones, so the recirculation gathers it into backwater rafts. The shader adds fine moving
detail on top.

## D5 — Water shader: lit scattering + emitted transmission + own SSR
Transparent pass reading the screen and depth textures. ALBEDO carries in-water scattering (so it takes sun and
shadows), EMISSION carries the refracted bed (absorbed by path length) and screen-space reflections, SPECULAR lets
the engine add sky reflection and sun glint except where our SSR already found geometry. Ripples are flow-mapped
(two phases) so they visibly travel with the current.

## D6 — Raft = rigid body with per-point forces
28 sample points under the hull: buoyancy (round-log fill), heave damping, drag against the local current, a soft
push off the bed. Plus yaw damping (no endless spinning), sweep oar at the stern, pole in shallow water, anchor
rope. Heavy (3.8 t) like a real raft, tuned to stay controllable.

## D7 — Player walks in the raft's frame
On deck, the player node is a child of the raft and the deck colliders (AnimatableBody3D) move with it, so walking
on a drifting, rocking raft is rock solid. Platform velocity is disabled (the parent already carries us). Stepping
onto land or into the water reparents to the world. The first-person camera keeps the horizon 70 % steadier than
the deck.

## D8 — Software-rendered verification in the cloud
Xvfb + Mesa lavapipe render real Forward+ frames without a GPU. Slow (minutes per batch), so captures take several
shots per launch (`tools/capture.sh`).

## D9 — Multilevel flow solve
The nonlinear Picard iterations run on grids coarsened 2× per level (down to 76×201); each finer level starts from
the coarser surface, and the 2 m grid needs only two CG solves. ~35 % faster than the previous three single-level steps; checked against a fully converged 8-step
reference: current within 0.025 m/s rms, surface within 9 cm rms (`Tests/Core.Tests -- --reference`).
Plain Jacobi-CG still needs ~5000 iterations on the fine grid; a better preconditioner (multigrid-preconditioned
CG) is the next step if generation time matters again.

## D10 — Day/night cycle keyed on the sun's elevation
A game clock (sandbox `Time.*`) drives a simple solar model (declination + hour angle, latitude 52°N for now) for
the sun and a near-full moon. Every lighting value (sun colour/energy, sky colours, exposure, fog, river mist) is a
smooth curve over the sun's *elevation* rather than the clock, so a dawn looks like a dawn in every season and at
every latitude, and the journey south can later lower the latitude without retuning. Mist is thick from late night
through sunrise and burns off by mid-morning; a thinner veil returns in the evening. At night the one shadowed
directional light becomes moonlight; stars are an equirectangular texture in the procedural sky's cover slot.
Lights in the `night_lights` group (the raft lantern) fade in at dusk. Logic and tests: `Game/Core/DayCycle.cs`.

## D11 — Eddies: lee zones from the centre lines, recirculation from a stream function
The potential solve has no inertia, so behind an island or a point the current just wraps around and closes up.
Real rivers separate there: the main current shoots past and a slack, recirculating backwater forms. Running real
shallow-water dynamics to a steady state was ruled out: too slow for generation, and it sheds unsteady vortices
we couldn't cache. Instead (`Game/Core/Eddies.cs`):
1. **Lee zones from geometry.** For each water node, walk back upstream *along its own channel's centre line at the
   same lateral offset*. Hitting an obstacle (bank, island, rock top) that sticks out `a` metres past the node's line
   makes the node part of that obstacle's lee if it is within ~6·a downstream (reattachment behind a step) and
   within the bed-friction length 0.25·h/c_f (shallow wakes are short). Curvilinear tracing is what keeps outer
   banks of meander bends from looking like obstacles; where offset lines fold over on the inside of tight bends,
   the trace stops.
2. **Blocked re-solve.** The flow is solved again (one Picard step from the first answer) with conveyance cut by 97 %
   in lee zones, so the main current stays a jet and the lee carries almost no net flow. Mass stays conserved.
3. **Recirculation.** Vorticity with the shear layer's sense is spread over each lee, sized so the eddy turns at
   ~0.35× the passing current. A stream function ψ (zero on every shore and around the zone) is solved from it with
   the same CG, and the eddy current is curl ψ/h: divergence-free and never crossing a shore. A one-sided lee
   gets one cell; an island wake gets a counter-rotating pair with water running back along its axis.
Cost: ~1.5 s more generation (the blocked re-solve). Checks in `Tests/Core.Tests` (island wakes flow back, the
lake inlet has backwaters, plain meanders stay free of them, eddies stay minor and slower than the river);
`-- --flow out.png` draws the current (blue with the channel, red against it, green = lee).

## D12 — Course and segments (streaming phase 0)
Generation is split the way docs/STREAMING.md plans: a **Course** (the river's path and features: centre lines,
islands, rocks; `Game/Core/Course.cs`) and **segments**, each generated by `ReachGenerator.Generate(SegmentSpec)`
as a pure function of its spec: the course slice in the segment's own **Frame** (double-precision origin +
heading), its inflows (edge, channel, discharge) and the water level held at its outlet. The prototype layout now
lives in `PrototypeCourse`; the valley shape functions in `Valley`. Refactor verified bit-for-bit: the headless
tests print a fingerprint of the whole field (`EBCEAE30D3E8F142` on .NET 9/Windows before and after).

## D13 — The endless course (streaming phase 1)
`Game/Core/Course.cs` generates the journey in 1D, incrementally and deterministically (built in steps or at once,
identical). **Valley axis** every 10 m: heading = bearing (south) + up to ±75° of slow noise (40 km wavelength),
clamped to ±90° (so it always progresses south and can't run into itself), turning radius ≥ 3 km, ≥ 1.5 km for
1.5 km below a confluence, where it also swings away from the tributary (Tom's D1–D3). **Features** (lake, island
split, riffle, confluence) are scheduled along σ (distance along the valley) with per-10-km frequencies and ≥ 600 m
gaps. **Cuts** are scheduled with them (target 2.5 km, 1.5–4 km, ≥ 250 m from any feature), and the river runs
straighter within ~350 m of each cut, so it crosses every cut line once, in a plain channel, by construction
(searching for naturally straight stretches failed: at sinuosity 1.9 there often was none). **River** every 2 m:
Kinoshita meanders relative to the local valley heading, with wider amplitude/wavelength variation than the
prototype (a whole journey of identical loops reads as a spring) and wavelength ≈ 16 widths along the river.
Width ∝ Q^0.5, depth ∝ Q^0.35 (capped); discharge grows slowly (creeks) and at confluences (+2–15 %, mostly small),
capped at 1,200 m³/s for the 1.2 km corridor. **Water level** from Manning over each cross-section, so the course
agrees with the segment flow solve (≈ 1.4 m/km here, as the prototype's solve). Known limit: with widths capped, a
big river stays steep (≈ 3 km of drop per 500 km of valley); fine in float, revisit if the corridor widens.
Checks in `Tests/Core.Tests` (50 km); 500 km × 3 seeds verified once (≈ 590 cuts, all valid). Map: `-- --course out.png`.

## D14 — Independent segments and seams (streaming phase 2)
`CourseSegments.Spec(course, k)` builds segment k from the course alone: a chord-aligned grid over cut k … cut k+1
plus 150 m overlap each side and a 650 m corridor (sizes rounded to 32 m so the multigrid can coarsen); the river,
islands (Tom's sketch in every lake, split islands, a gravel bar on the sharpest interior bend), riffle rocks and
the tributary (≤ 1 per segment: confluences ≥ 4.3 km apart) are built in world coordinates, then moved into the frame.
All generator noise (bed, banks, floodplain, valley walls, island tops) now samples **world** positions; the valley
walls follow the course's axis (`Valley.ReliefAround`). The prototype's identity frame makes this exact (its
fingerprint is unchanged). Making neighbours meet took three findings:
1. **Course water level from the solver's conveyance.** The textbook A·R^(2/3) underrated the carved profile's
   ∫h^(5/3)dy by ~1.6×: 3 m steps at seams. Now the course integrates the same profile (31 cm left).
2. **Per-segment calibration.** The 2D solve still drops ~18 % more than the 1D course (bends, bed noise, lee
   zones; ~25 % with riffle rocks). Each segment measures its own drop and re-solves once with conveyance × r
   (K at the old slopes → lands on the target drop; velocities with × √r keep the solve's face fluxes, so mass is
   conserved). Chain-free: it only compares itself with the course. Seam level step: 1.7 cm. +1.5 s per segment.
3. **Land far from the river stands on the course's level** (`SegmentSpec.LevelAt`), not on the grid's nearest
   water, which can be a different stretch of river on a neighbour's grid (18 cm → 2 cm).
Remaining raw differences are rasterisation: two rotated 2 m grids draw a shore a cell apart (≤ 6 m from the shore:
currents differ ≤ 15 cm/s, bank height ≤ 34 cm) and resolve a curved bank's flow a few degrees apart (≤ 8 cm/s).
`RiverWorld` assigns each point to the segment between its cut lines and blends neighbours over ±120 m; blended,
the current changes ≤ 2 cm/s per 2 m along the river across seams versus ≤ 9 cm/s in its own bends.
Segment generation ≈ 3.7 s here. Checks in `Tests/Core.Tests` (segments 1–4 out of order); map `-- --world out.png`.

## D15 — Streaming in the game (phase 3)
The game now plays the endless course (the prototype reach stays in the headless tests). `World/WorldStreamer`
keeps segments f−1 … f+2 drawn and f−2 … f+4 as data around the raft's segment f. A **worker thread** with its
own `Course` and `RiverWorld` (deterministic, so they match the main thread's) loads a segment from the disk cache
(`user://cache/seg_<seed>_<k>.bin`) or generates it, bakes its seams and builds its whole node tree off the main
thread (Godot allows building resources and nodes outside the tree there). The main thread attaches the nodes a few
per frame within 2 ms, and drops what leaves the window.
- **Seams**: segments overlap by 150 m (250 m since D16). `Core/SegmentBake` writes `RiverWorld`'s blended values (±120 m of each cut)
  into a copy of the field before its meshes and textures are built, so both neighbours show the same surface at the
  cut line; their shaders then discard everything across their cut lines (`clip_a`/`clip_b`, enabled once the
  neighbour is attached). A segment is only drawn when both neighbours' data exist, so its bake is final.
- **Placement**: each segment's node sits at its frame (position + Y rotation); meshes stay in grid coordinates.
  Shaders sample the field with the model-space position and use world positions for every pattern (ripples, waves,
  foam, caustics, gusts), so patterns run on across seams; the field's current is rotated into world axes first.
- **Physics** (raft, player, HUD) query `RiverWorld` (blended), which also keeps every loaded rock in world space for
  the shared `SurfaceWaves` (the raft rides the same pillows the water shows).
- **Far land**: a coarse skirt from the course's valley 1.5 km either side of each grid and 800 m beyond its ends,
  clipped at the cuts (rotated neighbours left wedges otherwise). Phase 5 replaces it with a proper horizon.
- The editor/Debug build is now compiled optimised (segment generation was 4.5× slower than the exported game).
- **Hitches, found by the 3-hour drift test** (`--drift-test`, STREAM lines): one heightmap collision per segment
  took 1.4 s to enter the physics world → 128 m collision chunks, attached one by one (≤ 4 ms). Per-frame garbage
  (string literals turned into `StringName` on every `SetShaderParameter`, a group query and lists per frame, HUD
  text at 60 Hz) made the collector pause ~33 ms every ~100 s → names cached, slow updates at 5 Hz, HUD at 4 Hz,
  `GCLatencyMode.SustainedLowLatency` plus a background collection after each generated segment. Generation's
  parallel loops leave two cores free while streaming (`Core/Par`). Result over 3 h: 26 of 650 k frames over
  8 ms (worst 23 ms, about one every 7 min), memory flat (≈ 410–530 MB), the raft crosses seams without grounding.
- Terrain LOD ranges overlap by 40 m: with touching ranges and hysteresis margins a freshly added chunk could show
  neither level (a hole to the sky).

## D16 — River variety, tier 1 (reach types, widths, bars, banks)
Tom: the banks were boring, the river too snake-shaped and too uniform in width. First tier, all in the course and the
segment generator (deterministic, world-coordinate noise, so seams still match):
- **Reach types** (`Course.ReachKind`): the course is cut into reaches of 2–6 km, each `Meander` (35 %), `Lazy`
  (30 %, long wavelength, more irregular), `Straight` (15 %, small amplitude, narrower and deeper) or `Wide` (20 %,
  1.6× width, shallow). Consecutive reaches differ; parameters blend over the first 500 m of a reach.
- **Breathing width**: half-width varies with two noise octaves (160 m, 650 m), occasional pinches (narrower, deeper),
  bends are wider and deeper at the apex and asymmetric (cut bank outside, inside shallower). Floor 6 m per side.
- **Bars**: gravel point bars on the inside of strong bends (≥ 250 m apart, 60 % chance) and mid-channel bars in wide
  reaches, as low islands in `CourseSegments.GravelBar`, kept clear of other islands.
- **Bank character**: a 420 m world noise switches between low wide banks and high steep banks (course segments only;
  the prototype reach and its fingerprint are unchanged).
- **Overlap 150 → 250 m**: the variable widths made the raw current at the inlet differ more between neighbours
  (15 cm/s); a longer overlap lets the inlet settle (12 cm/s raw; blended values remain continuous). The test limit is
  15 cm/s raw.
Next tiers (not done): oxbows, side channels, gorges, rapids, snags, braided reaches, island labyrinths.

## D17 — River variety, tier 2 (gorges, braids, side channels, oxbows, snags)
- **Two more reach types**: `Gorge` (9 %, 1.2–3 km, never the first reach): narrow (0.62×), deep (1.45×), still
  winding; the segment generator replaces bank and floodplain with a rock ledge and a 9–25 m cliff (`SegmentSpec.GorgeAt`,
  from the blended reach, so cliffs ramp in over 500 m and seams agree), boulders at the foot of the walls (≤ 60 rocks
  per segment, the water shader's limit is 64). The terrain shader draws rock bedding (bands along the height).
  `Braided` (9 %): 2× wide, 0.55× deep, nearly straight, rows of gravel bars every ~85 m, 2–3 slots across, staggered.
- **Side channels** (45 % of segments, plain reaches only): a second channel leaves the river, runs 25–70 m beside it
  behind a long island and rejoins it 400–900 m on. It is just another channel line in the generator (lines are now a
  list: main, tributary, side channels, still waters); the flow solve splits the water by itself.
- **Oxbow lakes** (0–2 per segment): crescent arcs on the far side of the valley from the river, at the course's level
  minus 0.3–0.7 m. They use the field's `Still` flag (it existed, unused): water, no current, their own level, no foam;
  plain banks around them (no sand bar inside the crescent).
- **Snags**: fallen trees on the banks, mostly on the outside of bends, reaching ≤ 40 % of the half-width into the
  river, foam where the current folds around them, a capsule collider (the raft bumps). `Snag.gdshader`.
- All placed in the interior of a segment (≥ 300–500 m from cuts; snags 60 m), from the course and world positions, so
  neighbours never disagree; side channels, oxbows and bars keep clear of each other.
- In course segments, small bars stand ≥ 0.1 m clear of the water (the water mesh, drawn 5 m onto land, hid them).
- Shots for checking: `gorge`, `gorge_top`, `braided`, `side_channel`, `oxbow`, `snag`.
Still to do (tier 3): rapids and a waterfall with portage, island labyrinths, reeds and lily pads on oxbows.
