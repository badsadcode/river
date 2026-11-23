# Handoff — current state

*Updated 2026-11-23.*

## What exists (prototype 0.1: "does drifting feel amazing?")
* **Reach generator** (`Game/Core/ReachGenerator.cs`): a 1.2 × 3.2 km reach, river first. Upper meanders with a
  gravel bar, a river-lake with Tom's sketch-#1 big island and islets, a tributary from the east, a forested island
  splitting the channel, a rocky riffle. Banks know inside/outside of bends (point bars vs cut banks), floodplain,
  valley walls. Headless tests in `Tests/Core.Tests` (conservation, speeds, determinism, map PNG).
* **Flow** (`Game/Core/FlowSolver.cs`): Manning-friction potential solve → water surface + current on a 2 m grid.
  Mean 0.66 m/s, lake 0.11 m/s, riffle 2.3 m/s; inflow = outflow.
* **World** (`Game/World/`): terrain chunks (2 LODs) + heightmap collision + far land to the horizon, water meshes
  at the solved surface, placeholder trees/bushes/boulders, sky/sun/fog presets, river mist fog volume.
* **Water shader** (`Game/Shaders/River.gdshader`): see `docs/WATER.md`.
* **Raft** (`Game/Raft/`): 3.8 t log raft with tent, crates, lantern, sweep oar. Per-point buoyancy and current
  drag; oar, pole, anchor. Drift test: follows the current through bends unsteered, never grounds, ≤1° tilt.
* **Player** (`Game/Player/PlayerController.cs`): walks on the moving raft, steps ashore, swims, climbs back on,
  takes the oar (E), poles (Q), anchors (G), chase camera (V).

## Known gaps / next steps (in rough priority)
1. **Eddies and recirculation** behind islands, points and rocks (the potential flow has no inertia): add a
   vortex/eddy layer to the current field (affects raft, foam and ripples).
2. **Generation time** ~35 s in game (CG solve ×3): coarse-grid warm start or multigrid → a few seconds.
3. **Endless streaming**: the reach is one fixed grid. Next: segments generated ahead along the river, floating origin.
4. **Rapids & a waterfall with portage**, island labyrinth segment type.
5. Water: wakes around the raft and the oar blade, splash particles at rocks, underwater view when swimming,
   rain rings (Thornwake has rain rings and wakes worth porting).
6. Art: real trees (Thornwake VegetationPack), grass, reeds on slow inner banks, raft textures.
7. Fishing (Thornwake has a fishing system to adapt).

## How to see it
* Build `dotnet build River.csproj`, run the project in Godot 4.7.2 .NET. The first launch generates the reach.
* Cloud: `tools/capture.sh "raft@0,deck@1,lake@1,meander@1,riffle@2,aerial@1" --frames=16 [--seconds=40]`.
* Headless: `tools/drift_test.sh 300` (track + steering numbers).
