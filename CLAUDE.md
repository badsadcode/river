# Working on RIVER

Bad Ideas Works. Tom is the founder/designer; Sage (Claude) engineers; Noor (ChatGPT) co-designs.
Studio motto #001: *code shall remain understandable to whichever AI inherits this mess.*

## Fixed choices
* Godot **4.7.2**, **C#** (.NET 8), **Forward+**. Solo game (no multiplayer).
* Design: `docs/DESIGN_PILLARS.md` (vision, pillars, decisions), `docs/SANDBOX_SETTINGS.md`.
* Tom's priorities right now: **the water/river must be perfect; raft physics realistic but not annoying.**

## How to work
* Decide technical things without asking; log notable decisions in `docs/DECISIONS.md`.
  Pause and ask Tom before significant artistic decisions (looks, palettes, tone).
* Commit small, push often. No pull requests unless Tom asks. Never rewrite pushed history.
* Keep the headless generator tests passing:
  `dotnet run --project Tests/Core.Tests -c Release` (add `-- --map out.png` for a top-down map, `--flow out.png` for the current and eddies,
  `--course out.png` for a 50 km map of the endless course, `--world out.png` for 4 stitched segments).
  The full run takes ~25 s (segments 1–4 are generated).
* Shaders: Godot does not allow `return` in `fragment()`. After shader changes, run a capture and grep the log for
  `SHADER ERROR`.

## Layout
* `Game/Core/` — pure C# (no Godot types): noise, centre lines, islands, the endless `Course` (1D journey),
  `SegmentSpec`/`Frame`/`CourseSegments` (one segment's slice), `RiverWorld` (segments as one world),
  `SegmentBake` (seam blend baked into a segment before drawing), the segment generator (`ReachGenerator`), the flow solver, eddies,
  surface waves, the field container and its cache. `PrototypeCourse` is the hand-made reach (headless tests only).
  Testable headlessly. Streaming plan: `docs/STREAMING.md`.
* `Game/World/` — Godot side of the world: `Origin` (floating origin: scene = world − origin; RiverWorld queries take
  world coordinates), `WorldStreamer` (segments around the raft, worker thread, cache),
  `SegmentView` (one segment's nodes), field → GPU textures, terrain, water meshes, vegetation, environment.
* `Game/Shaders/` — `River.gdshader` (the water), `Terrain.gdshader`, `Mist.gdshader` (fog volume), `Tree.gdshader`.
* `Game/Raft/` — raft physics (`Raft.cs`) and its placeholder model and deck colliders (`RaftModel.cs`).
* `Game/Player/` — first-person controller (walks in the raft's frame, swims, uses oar/pole/anchor).
* `Game/Debug/` — fly camera, HUD.
* `Game/Main.cs` — entry: start the streamer, raft and player; camera presets (relative to the raft or to the first
  lake/riffle/rock/confluence/split), command-line capture, streamed drift test (STREAM lines: hitches, memory).

## Physics layers
1 terrain + rocks · 2 raft hull · 3 (value 4) raft deck (walkable, follows the raft) · 4 (value 8) player.
The raft hull and the deck/player never collide with each other.

## Cloud/Linux setup (no GPU)
* `.NET 8` from apt (`dotnet-sdk-8.0`), Godot from the GitHub release zip into `/opt/godot`, `xvfb` +
  `mesa-vulkan-drivers` for software Vulkan. (Same recipe as Thornwake's `tools/setup-cloud.sh`.)
* Build: `dotnet build River.csproj`. First run: `godot --headless --import --path .`
* Screenshots: `tools/capture.sh "aerial@1,lake,raft@0" --frames=16` → `captures/*.png` (several minutes:
  lavapipe is slow). Drift test: `tools/drift_test.sh 300`.
* The reach is cached in `user://cache/` keyed by the assembly build, so any rebuild regenerates it (~35 s).

## Dev log
* `devlog/` is the public dev log (https://badsadcode.github.io/river/), an 1890s explorer's journal. **No technical
  talk** there (Tom's brief). How to add an entry: `devlog/README.md`. Pushing changes under `devlog/` publishes it.
