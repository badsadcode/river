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
riffles) and the current (splits around islands, fast narrows, slack bays) consistently. No inertia yet, so no
eddies or recirculation behind obstacles: planned as an added vortex layer.
The bed is defined *relative to the solved surface* (designed depth below η), so depth is exactly as designed.

## D4 — Foam is precomputed and advected
Foam sources (high Froude number, current running into a shore, rocks breaking the surface) are found on the grid
and advected downstream with decay, so foam trails off behind rocks and island tips. The shader adds fine moving
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
