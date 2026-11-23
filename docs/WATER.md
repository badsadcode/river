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
5. **Foam** — coverage from the field (rocks, riffles, current pushing into shores, advected downstream) plus a
   thin lap line at the bank; drawn as a bubbly net carried by the flow and streaks stretched along the current.
   Coverage is capped so it is never a solid white sheet.
6. **Edge** — alpha fades over the last ~15 cm of thickness so shorelines aren't hard cuts.

## Knobs worth trying
`absorption`, `scatter_color`, `turbidity` (river character: clear highland stream ↔ muddy lowland river),
`ripple_strength`, `flow_cycle`, `wind`, `refraction_strength`, `ssr_steps`.
