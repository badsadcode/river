# The water

The river's look is driven by the same data that drives the raft: the reach field.

## Data (FieldTextures.cs, sampled at uv = (xz / cell + 0.5) / size)
| Texture | R | G | B | A |
|---|---|---|---|---|
| `flow_tex` | current x (m/s) | current z (m/s) | depth (m) | foam coverage 0..1 |
| `shape_tex` | water surface η | signed shore distance (m, − in water) | terrain height | — |

## Shader layers (Game/Shaders/River.gdshader)
1. **Ripples** — two procedural tileable normal maps (9 m and 2.3 m), flow-mapped: two phases 1.4 s apart, so the
   pattern visibly travels with the current. Strength grows with speed and with "rough water" (Froude number:
   riffles and rapids). Wind ripples (not carried by the current) dominate slow, wide water like the lake.
   The phase jitter must be smooth, or it smears the ripples into marbling.
2. **Refraction + absorption** — the bed is read from the screen behind the water, offset by the normal, and
   absorbed per metre of path: `absorption = (0.40, 0.31, 0.66)` (blue goes first: a temperate, slightly
   tannic/silty river). Rougher views of deep water use a blurrier mip.
3. **Scattering** — `scatter_color` through ALBEDO, so the water body takes sunlight *and shadows* (tree shadows
   lie on the water).
4. **Reflections** — our own screen-space ray march (40 steps + refinement) for banks and trees; where it finds
   nothing the engine's sky reflection and sun glint take over (SPECULAR masked by the SSR hit). Fresnel ≈ 0.02 at
   normal incidence. Roughness grows with distance so the sun becomes a glittering path, not a disc.
5. **Foam** — coverage from the field (rocks, riffles, current pushing into shores, eddy lines; advected with the
   current, lingering in backwaters) plus a thin lap line at the bank. The foam texture (ProceduralTextures.Foam)
   is cloudy domain-warped noise (R) plus small bubbles (G), histogram-equalised so coverage c whitens about a
   fraction c. Large drifting patches break it into clumps; `foam_max` caps it. Not paint: thin foam is a
   translucent bubbly lace, only thick cores are near-opaque, it is off-white with brighter and greyer parts, and its
   relief tilts the normal so the sun shades its lumps.
6. **Edge** — alpha fades over the last ~15 cm of thickness so shorelines aren't hard cuts.
7. **Waves** (vertex shader) — swell, chop in rough water and *standing waves* in fast water (stationary crests,
   wavelength 2πv²/g), on a 1 m mesh. Same function as the raft's buoyancy: `Core/SurfaceWaves.cs` mirrored by
   `wave_height` in the shader, clocked by `Raft.WaveTime`. Change both together. Rocks add a pillow on their
   upstream face, water drawn down beside them and a hollow behind, scaled by the velocity head v²/2g of the current
   approaching each rock (`Rock.FlowX/Z`; `SurfaceWaves.RockBump` ↔ shader `rock_bump`, rocks passed as a uniform
   array of up to 64).
   Also in the shared wave function: *wind waves* (3–6 m, a few cm, in gusts and on deeper open water) and *boils*
   (slow domes in faster deep water, drifting with the current). `Gust()` patches (hundreds of metres, drifting)
   make the surface range from glassy to ruffled, and backwaters (the eddy field, in `shape_tex.a`) stay calm.
9. **Living ripples and texture advection** — each ripple layer is two crossing wave trains that travel on their
   own (`ripple_speed`) while drifting with the current, so the pattern changes shape instead of sliding like a
   rigid sheet. Textures are carried by a current smoothed over ~3 m (`adv_smooth`) and capped (`adv_cap`):
   the exact current curls tightly around rocks and in eddies and wound the textures into whirlpool rings.
   Ripple strength varies: gust patches (wind ripples), drifting turbulence blotches (current ripples), calmer in
   backwaters.
10. **Rocks** (Rock.gdshader, RockSplash.cs) — wet, darker, glossy stone up to where the water climbs (highest on
   the upstream face), a thin water film streaming over the wetted stone along the current, a broken foam line at
   the waterline, and spray particles off the upstream face scaled by the current. Per-rock values are instance
   uniforms (`water_level`, `flow`).
8. **Riverbed** (Terrain.gdshader) — matte under water (a glossy bed showed through as a green-yellow sun path);
   caustics ~1 m (`caustic_scale`).

## Knobs worth trying
`absorption`, `scatter_color`, `turbidity` (river character: clear highland stream ↔ muddy lowland river),
`ripple_strength`, `flow_cycle`, `wind`, `refraction_strength`, `ssr_steps`, `foam_scale`, `foam_cycle`,
`foam_max`, `foam_bump`, `foam_color`, `wave_strength`.

Tuning without rebuilding: captures accept `--water=name=value;name=value` (water uniforms), `--nomist`, and
`--water=debug_view=1|2|3` (engine lighting / emission / albedo only). Foam shots: `foam_close`, `bar`, `eddy`.
