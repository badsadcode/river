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
