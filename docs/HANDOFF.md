# Handoff — current state

*Updated 2026-11-23.*

## What exists (prototype 0.1: "does drifting feel amazing?")
* **Reach generator** (`Game/Core/ReachGenerator.cs`): a 1.2 × 3.2 km reach, river first. Upper meanders with a
  gravel bar, a river-lake with Tom's sketch-#1 big island and islets, a tributary from the east, a forested island
  splitting the channel, a rocky riffle. Banks know inside/outside of bends (point bars vs cut banks), floodplain,
  valley walls. Headless tests in `Tests/Core.Tests` (conservation, speeds, determinism, map PNG).
* **Flow** (`Game/Core/FlowSolver.cs`): Manning-friction potential solve → water surface + current on a 2 m grid.
  Mean 0.66 m/s, lake 0.11 m/s, riffle 2.3 m/s; inflow = outflow.
* **Eddies** (`Game/Core/Eddies.cs`, D11): lee zones behind islands, points, lake inlet corners, the confluence and
  rocks get a blocked re-solve plus a divergence-free recirculation, so water runs back behind obstacles (~1 % of
  the river, slower than the main current). `ReachField.Eddy` marks the lee zones (0..1) for later use (mooring,
  fishing spots, foam on eddy lines). Flow map: `dotnet run --project Tests/Core.Tests -c Release -- --flow out.png`.
* **World** (`Game/World/`): terrain chunks (2 LODs) + heightmap collision + far land to the horizon, water meshes
  at the solved surface, placeholder trees/bushes/boulders, sky/sun/fog presets, river mist fog volume.
* **Water shader** (`Game/Shaders/River.gdshader`): see `docs/WATER.md`.
* **Raft** (`Game/Raft/`): 3.8 t log raft with tent, crates, lantern, sweep oar. Per-point buoyancy and current
  drag; oar, pole, anchor. Drift test: follows the current through bends unsteered, never grounds, ≤1° tilt.
* **Day/night** (`Game/Core/DayCycle.cs`, `Game/World/EnvironmentRig.cs`): game clock, sun and moon paths by
  season, lighting/mist curves, stars, lantern at dusk. T jumps ahead, Y overcast, [ ] time speed.
* **Sandbox settings** (`Game/Core/SandboxSettings.cs`): loads/validates `game_sandbox_settings.json`.
* **Player** (`Game/Player/PlayerController.cs`): walks on the moving raft, steps ashore, swims, climbs back on,
  takes the oar (E), poles (Q), anchors (G), chase camera (V).

## Known gaps / next steps (in rough priority)
1. **Eddies, next steps**: foam now gathers on eddy lines and lingers in backwaters (subtle; the 2 m grid can't draw
   sharp eddy lines, a shader-side line would). Still to judge in person: backward-running ripples and the raft's
   feel. Long lakes only get gyres near the inlet (the 6× reattachment rule).
   Shots (streamed world): `raft`, `deck`, `aerial`, `top`, `meander`, `water_close` around the raft; `lake`, `riffle`,
   `foam_close`, `rock`, `confluence`, `split` at the first such feature downstream.
2. **Generation time**: multilevel solve brought it down ~35 % (D9); multigrid-preconditioned CG would cut it further.
   The eddy step adds one fine re-solve (~1.5 s on a desktop).
3. **Endless streaming**: phases 0–3 done: the game streams the endless course around the raft ([STREAMING.md](STREAMING.md),
   D12–D15). Next: floating origin (phase 4: float precision past ~20 km), proper horizon/mist/saves (phase 5).
4. **River variety**: tier 1 done (D16: reach types, breathing widths, bars, bank character). Next tiers: oxbows,
   side channels, gorges, rapids & a waterfall with portage, snags, braided reaches, island labyrinth segment type.
5. Water: wakes around the raft and the oar blade, splash particles at rocks, underwater view when swimming,
   rain rings (Thornwake has rain rings and wakes worth porting).
6. Art: real trees (Thornwake VegetationPack), grass, reeds on slow inner banks, raft textures.
7. Fishing (Thornwake has a fishing system to adapt).

## How to see it
* Build `dotnet build River.csproj`, run the project in Godot 4.7.2 .NET. The first launch generates the reach.
* Cloud: `tools/capture.sh "raft@0,deck@1,lake@1,meander@1,riffle@2,aerial@1" --frames=16 [--seconds=40]`.
* Headless: `tools/drift_test.sh 300` (track + steering numbers).
