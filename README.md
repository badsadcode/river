# River

An endless expedition down a procedurally generated river, around 1890.
Godot 4.7.2 · C# · Forward+ · by Bad Ideas Works.

- [Design pillars](docs/DESIGN_PILLARS.md) — vision, tone, river-first world generation, decisions.
- [Sandbox settings](docs/SANDBOX_SETTINGS.md) — `game_sandbox_settings.json`: presets, scale, darkness, difficulty.
- [Handoff](docs/HANDOFF.md) — what exists now and what's next. [Water](docs/WATER.md) · [Decisions](docs/DECISIONS.md)

## Run (on your PC)
1. Install the **.NET 8 SDK** and **Godot 4.7.2 .NET** (the "Mono/.NET" download).
2. `git clone https://github.com/badsadcode/river.git` and `git checkout claude/bold-archimedes-pqdrve`
3. Open `project.godot` in Godot, press **Build** (hammer, top right), then **Run** (F5).
   The first launch generates the river (~35 s, "Generating the river…"); later launches load a cache.

## Controls
WASD walk · Shift run · Space jump / climb out of the water · **E** take/release the sweep oar (A/D steer, W/S scull)
· hold **Q** to pole off the bottom (shallow water) · **G** anchor · **V** chase camera (mouse orbit, wheel zoom)
· **T** time of day · **F1** fly camera · **F3** info · **H** hide help · Esc release the mouse
