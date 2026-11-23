// River mist: a thin layer hugging the water and the low banks, drifting slowly downstream and with the wind.
// Used by a FogVolume covering the reach (EnvironmentRig.cs); strength comes from the time-of-day preset.
shader_type fog;

// Floating origin (World/Origin.cs): scene position + world_origin = world position.
global uniform vec3 world_origin;

uniform sampler2D shape_tex : filter_linear, repeat_disable;  // eta, shore distance, terrain height
uniform vec2 field_size = vec2(601.0, 1601.0);
uniform float field_cell = 2.0;
uniform sampler3D noise3d : repeat_enable;
uniform float density = 0.06;
uniform float layer_height = 2.5;     // metres of mist above the water (e-folding)
uniform vec3 albedo : source_color = vec3(0.92, 0.93, 0.95);
uniform vec2 drift = vec2(0.25, 0.6);
// The segment's grid: world → grid = rotate(world − origin) by −heading (frame_rot = cos, sin of the heading).
uniform vec2 frame_origin = vec2(0.0);
uniform vec2 frame_rot = vec2(1.0, 0.0);

void fog() {
	vec3 wp = WORLD_POSITION + world_origin;   // scene → world (floating origin)
	vec2 d = wp.xz - frame_origin;
	vec2 local = vec2(d.x * frame_rot.x - d.y * frame_rot.y, d.x * frame_rot.y + d.y * frame_rot.x);
	vec2 fuv = (local / field_cell + 0.5) / field_size;
	vec4 sh = texture(shape_tex, fuv);
	float h = wp.y - sh.r;
	float near_water = 1.0 - smoothstep(-10.0, 70.0, sh.g);
	float layer = exp(-max(h, 0.0) / layer_height) * step(-0.2, h);
	vec3 q = wp * vec3(0.018, 0.08, 0.018) + vec3(drift.x, 0.02, drift.y) * TIME * 0.05;
	float n = texture(noise3d, q).r;
	float n2 = texture(noise3d, q * 3.1 + 0.5).r;
	float wisps = smoothstep(0.3, 0.8, n * 0.75 + n2 * 0.35);
	DENSITY = density * layer * mix(0.15, 1.0, near_water) * wisps;
	ALBEDO = albedo;
}
