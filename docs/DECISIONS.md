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

## D18 — Real trees (NoorForm packs)
- Tom's NoorForm packs in `Assets/Trees` (Noor Interchange: GLB per LOD + `*.manifest.json`): Birch, Oak, Pine,
  Spruce, 5 variants each (`TreeLibrary.VariantsPerSpecies`; the export excludes variants 06–10 and the jungle tree).
  Loaded once on the main thread (~0.8 s). Each LOD's Trunk + Leaves are merged into one two-surface mesh; LODs 1–2
  reuse LOD 0's materials (the packs embed the same textures in every LOD). `Foliage.gdshader`: pack maps, alpha cut
  0.5 boosted by mip level (crowns don't thin out far away), simple wind (height sway; the pack's UV2 bend/flutter).
  Tree textures import VRAM-compressed.
- **LOD**: real meshes are chosen per tree, by distance, using the manifest's ranges (`TreeLod`): every drawn
  segment's trees sit in 32 m cells in world space; when the camera has moved 2 m (≤ 10×/s) the trees within range
  are written into one MultiMesh per variant and LOD (60 draws, buffers reused, no garbage). Beyond LOD 2 the
  segment's static billboard MultiMeshes (512 m blocks) draw them; billboards hide per tree within that range in the
  vertex shader, against a global `tree_camera` (set each frame, so shadow passes use the main camera too).
- **Placement** (same density mask as before): spruce on valley walls and cool patches, pine on drier ground, birch
  along the water and wood edges (a stand-in for alder/willow, no packs yet), oak in the open floodplain. Bushes are
  still the placeholders.
- Cost: 80 vs 100 fps from the deck at 1600×900 on Tom's machine (≈ 2.5 ms a frame). `--no-trees` brings the old
  placeholders back. The windowed drift test now looks from the deck and reports fps.
- The packs are **not in git** (Tom: keep them local; 247 MB). `/Assets/Trees/` is ignored; a clone without them gets
  the placeholder trees. Copy the packs (NoorForm export) into `Assets/Trees` to get real trees.

## D19 — Rapids, and the weeping willow
- **Rapids** (`FeatureKind.Rapid`, relative frequency 0.3; every riffle in a gorge becomes one): 200–400 m, the river
  narrower (−10 %) and shallow (1.3 m), meanders damped. `CourseSegments.RapidRocks`: ≤ 44 rocks, two ledges of
  pour-over rocks just under the surface, boulders between (many emergent), and a winding chute (±30 % of the
  half-width, ≥ 4.5 m either side) kept clear, so a raft can be steered through rather than pinballed. Course head
  loss in rapids × (1 + `Course.RapidLoss` = 0.25) for the boulders the 1D model can't see.
- Result on the first rapid (σ ≈ 4.9 km): 3.1 m/s along it vs 1.1 m/s around, chute ≥ 1.24 m deep, water balanced.
  The raft runs it at ~3.4 m/s, bumps a boulder or two (≤ 4.8° tilt), never grounds.
- **Calibration**: when the one-step calibration misses the course's inlet level by > 1.2 cm (rapids are strongly
  nonlinear), up to three more passes on converged solves scale conveyance by √(drop ratio) each.
- **White water**: standing waves grow into haystacks (≤ 0.37 m) in fast supercritical water (`SurfaceWaves` and the
  shader, mirrored); foam source + 0.5 above 1.8 m/s; the water shader lets coverage reach 85 % there instead of 60 %
  and keeps the patches from thinning it. (The prototype reach's foam changed slightly: fingerprint 4A2ECF4ED9FD32BA.)
- `--start=rapid` (or lake, riffle, split, confluence, gorge, braided, …) starts the raft above the first one.
  Shots `rapid`, `rapid_top`, `willow`, `willow_far`.
- Seam test: terrain allowance now uses the slope along the valley too (a cut across a steep wall failed it).
- **Weeping willow** (Tom's pack): the fifth species, within 12 m of rivers and oxbows (60 % of broadleaves there).
- `docs/.gdignore`: Godot no longer imports the docs' images.

## D20 — Floating origin (streaming phase 4)
- `World/Origin.cs`: scene = world − (X, Z), whole metres; Y not shifted. It starts at the raft's start; when the raft
  is more than 1024 m from the scene origin, `Main._PhysicsProcess` shifts it there (before the raft integrates).
  Subscribers to `Origin.Shifted` move what lives in scene space: the raft and fly camera (Main), the player when not on
  the raft and the chase camera (PlayerController), segment roots (`SegmentView.Place`), tree LOD (rebuilds).
- World data stays double: the course, segment frames, `RiverWorld` (all queries take world coordinates). Godot code
  converts at the boundary (`Origin.ToWorld/ToScene`; raft, player, HUD, streamer focus, shots, drift test).
  `TreeSet` keeps its trees relative to the segment's frame origin, in double.
- **Patterns**: simpler than the plan's per-term phase offsets. Shaders add a global `world_origin` (whole metres, exact
  in float) to their scene positions, so ripples, foam, waves, caustics, gusts and clipping see the same float world
  coordinates as before a shift, and `SurfaceWaves` (raft) computes in the same float world coordinates. Pattern
  precision is that of float world coordinates (~4 mm at 50 km, ~6 cm at 1000 km), fine for patterns; geometry and
  physics stay near the origin. Fog shaders don't receive global uniforms: the mist gets `world_origin` per material.
- Checks: drift test with a forced shift every 30 s — water height at a fixed world point jumps 0.00 mm, the raft
  drifts on smoothly (8 min in the first gorge, 28 km out); a capture before and after a 360 m shift matches.
  Pseudo-shot `shift` for that comparison.

## D21 — Waterfalls and portage
Tom's choices: portage by **hauling the raft on log rollers**; a raft that goes over is **wrecked** (for now rebuilt
at the landing, since there is no survival yet).
- **Course**: `FeatureKind.Falls`, on its own sparse schedule (first at 14 ± 3 km, then every 35 ± 10 km;
  `CourseSettings.FallsFirstKm/FallsSpacingKm`). 450 m: a wide, deep, slow pool 280 m above the lip with a slack bay
  on the landing side, the lip (the course's level drops by the falls' height, 6–14 m, in one step), a deep plunge
  pool 170 m below. Meanders damped through it.
- **Segment** (`CourseSegments.MakeFalls`, `FallsSpec`): lip centre and flow direction, landing (in the bay, 110 m
  above), put-in (75 m below), a trail polyline (bank → 28 m inland → downstream past the lip → back to the water) and
  3–6 boulders on the lip that split the curtain.
- **Generator**: the drop can't be solved for (it isn't friction), so the flow is solved with the outlet held its
  height higher and the water below the lip (by the nearest main centre-line sample) is then lowered by it; the
  course's level already has the step, so the valley gets a scarp across it. The plunge pool's first 24 m are foam
  sources. The trail is carved 4 m wide, level at both ends and ramped over 70 m around the lip (≤ 13 % on the first
  falls). `RiverWorld.Falls` lists loaded falls in world coordinates.
- **Look** (`FallsBuilder`, `Falls.gdshader`): the water surface stops at the lip (cells across a > 1 m step aren't
  drawn); a curtain mesh falls along a parabola, split at the lip boulders, glassy at the top and white below with
  streaks racing down; spray and a mist cloud at the foot; very foamy water reads white even when slow; a post at the
  trail head.
- **Portage** (`Raft/Portage.cs`): ashore by the post with the raft in the bay, F hauls it out onto rollers (fade);
  walking into it from behind pushes it along the trail at 1 m/s (≈ 4 min for 240 m); at the end it is put afloat
  below the falls. **Wreck**: crossing the lip from above and dropping 2 m → fade, raft rebuilt and anchored at the
  landing, the player put ashore by the post if not aboard; a raft pinned against the lip boulders within 8 m of the
  edge for 6 s is dragged over (a lip-pinned raft would otherwise be stuck there). HUD prompts warn of falls ahead
  and say what to do.
- Checks: headless test of the first falls (drop = height, landing current 0.16 m/s, trail ≤ 13 %, carved to within
  5 cm, water balanced); `--portage-test` (with `--start=falls`): drift over → wrecked and rebuilt 0.1 m from the
  landing; haul and push → afloat 0.3 m from the put-in after 233 s. Shots `falls`, `falls_top`, `portage`.
- Fixed on the way: the raft's anchor point now moves with floating-origin shifts.
- Not yet: lining the raft down with ropes, sound, a proper waterfall pool as a "secret place", tuning the look
  (whiter churn at the foot; the lip boulders sit a bit like eggs).

## D22 — Waterfalls, second look (Tom: "the river is gorgeous, the waterfalls not so much")
- **Lip**: a horseshoe (middle cut back by up to 35 % of the half-width, 3–12 m) with a ragged edge (±1.5 m):
  `FallsSpec.LipAt`, shared by the generator (which water drops), the lip ledges and the curtain. Lip "boulders" are
  now 2–4 wide, low ledges just breaking the surface.
- **Sheet**: columns every 0.5 m follow the actual lip; each carries its own amount of water (threads … full sheet,
  slow noise across); a brink curving over from 0.8 m upstream; a parabola thrown further for fuller columns; a glassy
  dark tongue at the top tearing into white streamers that race down and stretch as they speed up; thin columns break
  into threads; the sheet bulges with slow waves. A thin veil of spray in front.
- **Foot**: a heaving white churn spreading 9 m over the pool; more spray and a larger mist cloud, now lit (unshaded
  mist glowed at dusk).
- **Rock and land**: large flattened rock ledges sunk into the face beside the falls and between them (Rock shader:
  wet at the bottom); steep rock next to water is darker and glossier (wet) everywhere. The land no longer steps down
  across the valley at the lip: the drop is a rock face at the water, easing 0.8 m per metre from the shore into a
  walkable slope (`ReachGenerator.SoftenScarp`), and far from the river `LevelAt` ramps over ±150 m.
- Portage still passes (233 s of pushing, afloat 0.3 m from the put-in).
- **Third look** (Tom's close-up screenshots: folded ribbons, a carpet-like churn, a smooth paper curtain; "the closer
  to the edge, the faster and more turbulent"): the churn skirt is gone (the river surface churns white itself, foam
  capped at 0.8 so it stays broken foam, not a solid patch); the lip's raggedness halved (±0.7 m) and the columns'
  edges smoothed over ±2.5 m, each strip starting at the pool's level read 2.5 m upstream (just at the edge the sample
  could fall into the pool below: strips started metres low, a sawtooth top); river-surface cells dipping > 0.3 m
  aren't drawn (a half-over cell sagged into a dark band); hair-fine fast streaks and see-through gaps in the sheet;
  lip ledges fewer, smaller, lower; mist puffs have a cloudy noise texture and random slow rotation. **Approach**: over
  the last 30 m above the lip the current speeds up to ×3 (≤ 3.5 m/s), the surface draws down by up to 0.35 m, and
  the shader roughens it with the speed. Shots `falls_brink`, `falls_close`.
- **Fourth look** (Tom: "square blocks", mist on the sides): the generator stores each node's distance past the lip's
  smooth curve (`ReachField.LipDist`, cached); within 40 m of the lip which water drops comes from that curve too.
  River-surface cells across the drop are drawn twice, at the brink level and at the pool level, and the water shader
  clips the upper copy exactly at the curve (`lip_tex`, `lip_mid`): the edge no longer follows the 2 m grid. (The
  copies first took their levels 3 m along the flow; on the horseshoe's flanks that point was on the same side of the
  lip, leaving vertices half-way: dark wedges rising from the pool. They now use the brink and pool levels.) The sheet
  starts on the same curve (`FallsSpec.LipAt`) instead of searching the grid; it is thrown at least clear of the face;
  it receives no shadows; the finest streaks fade where they would alias. Lip ledges removed (their gaps showed as
  dark wedges; they read as logs). Spray and mist rise from the foot of the sheet, as wide as it. Shots `falls_side`,
  `falls_edge`; `RIVER_FALLS_DEBUG=1` logs skipped columns.
- **Fifth look** (Tom: still odd from the upper water near an end; no mist or spray at the foot): each strip of the
  sheet falls at right angles to its own bit of lip (the horseshoe's sides fall inwards, not straight downstream,
  where they left the face bare); the horseshoe is shallower (25 % of the half-width, ≤ 8 m); spray and mist come
  from an emitter every ~6 m along the foot, facing the way the water there falls, the mist hugging the pool; the
  streak noise is warped and turned (value noise showed its grid as rectangular blotches); the glassy tongue takes
  the river's colour instead of near-black. A top-down `falls_map` shot shows the shape; from the upper water near an
  end the plunge pool's side is a steep rock wall, as intended. Shots `falls_flank`, `falls_map`.
- **Sixth look** (Tom: from above the pool the sheet's lower half vanished; no mist at the foot; wants mist at the top
  too): a draw-order bug. All of it is see-through; the sheet didn't write depth, so the river (drawn after it) painted
  the pool over it, and over the spray and mist. Now the sheet writes depth (`depth_draw_always`) and the order is
  sheet 0, river 1, spray and mist 2, veil 3. Spray and mist also at the brink (fine droplets thrown off the edge, a
  thin mist lying over it). Shot `falls_over`.
- **Rocky horseshoe** (Tom's reference photos; his pick of four styles): the falls are now made of rock
  (`FallsBuilder.Stones`, `FallsRock.gdshader`): stacked slabs of dark layered stone (faces as 4 x 4 grids pushed in
  by noise, so chipped rather than crates) forming the face behind the water (undercut lower down), 2-3 outcrops on
  the lip that split the fall into separate streams (thinner towards their edges; they collide), walls 8 m into the
  banks with their tops at the ground, and 4 rounded boulders half sunk in the pool (Rock shader, collide). The stone
  is wet near the falls, banded, with moss on upward faces and water films on steep ones. The sheet is more
  see-through so the rock shows. Other styles (tiered cascades, tall chutes in gorges, ledge falls in rapids) are
  possible later.
