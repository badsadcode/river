// A waterfall's curtain (FallsBuilder): a sheet of falling water from the lip to the plunge pool.
// UV.x = metres across the lip, UV.y = 0 at the lip → 1 at the pool. Streaks race down the sheet, the water is
// glassy and dark at the lip and turns to white, aerated water as it falls; ragged, see-through edges.
shader_type spatial;
render_mode cull_disabled, depth_draw_opaque, specular_schlick_ggx;

// Floating origin (World/Origin.cs): scene position + world_origin = world position.
global uniform vec3 world_origin;

uniform float drop = 8.0;            // metres
uniform float fall_speed = 6.0;      // texture speed down the sheet (m/s)
uniform vec3 deep_color : source_color = vec3(0.10, 0.17, 0.18);
uniform vec3 white_color : source_color = vec3(0.92, 0.95, 0.96);

varying vec3 v_world;

float hash(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }
float vnoise(vec2 p) {
	vec2 i = floor(p), f = fract(p);
	vec2 u = f * f * (3.0 - 2.0 * f);
	return mix(mix(hash(i), hash(i + vec2(1, 0)), u.x), mix(hash(i + vec2(0, 1)), hash(i + vec2(1, 1)), u.x), u.y);
}

void vertex() {
	v_world = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz + world_origin;
}

void fragment() {
	float across = UV.x, down = UV.y;
	// Streaks: long along the fall, narrow across it, moving down; two layers at different speeds.
	vec2 p1 = vec2(across * 1.6, down * drop * 0.35 - TIME * fall_speed * 0.35);
	vec2 p2 = vec2(across * 3.7 + 7.0, down * drop * 0.8 - TIME * fall_speed * 0.8);
	float streak = vnoise(p1) * 0.6 + vnoise(p2) * 0.4;
	// Aeration: glassy at the lip, white lower down, whitest in clumps.
	float across_var = vnoise(vec2(across * 0.35, 3.0));
	float white = clamp(smoothstep(0.08 + 0.2 * across_var, 0.6, down) * (0.5 + 0.65 * streak) + 0.1 * streak, 0.0, 1.0);
	ALBEDO = mix(deep_color, white_color, white);
	ROUGHNESS = mix(0.08, 0.7, white);
	SPECULAR = 0.5;
	// See-through gaps between the streaks, thinner sheet at the top edge.
	float body = smoothstep(0.18, 0.5, streak + 0.25 * down);
	ALPHA = clamp(mix(0.55, 0.97, white) * body + 0.25, 0.0, 1.0);
	BACKLIGHT = ALBEDO * 0.4 * white;
}
