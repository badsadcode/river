// River rocks: dry stone above, wet stone where the river reaches, and the water itself on the stone.
//   • wet band: darker and glossy up to where the water climbs, highest on the upstream face (the pillow plus
//     splash, from the same velocity head as SurfaceWaves.RockBump), with a ragged edge
//   • water film: thin water sliding over the wetted stone in the direction of the current (moving streaks),
//     which covers low rocks completely: water flowing over the rock
//   • foam line: white, broken foam where the water meets the stone, mostly upstream
// Per rock (instance uniforms, set in Vegetation.BuildRocks): local water level and approaching current.
shader_type spatial;
render_mode cull_back, specular_schlick_ggx;

instance uniform float water_level = 0.0;              // local water surface height (m)
instance uniform vec4 flow = vec4(0.0, 1.0, 0.0, 1.0);  // current direction x, z (unit), speed (m/s), rock radius (m)

uniform vec3 stone : source_color = vec3(0.36, 0.35, 0.33);
uniform vec3 foam_color : source_color = vec3(0.82, 0.82, 0.78);

varying vec3 v_world;
varying vec3 v_centre;

float hash12(vec2 p) {
	vec3 p3 = fract(vec3(p.xyx) * 0.1031);
	p3 += dot(p3, p3.yzx + 33.33);
	return fract((p3.x + p3.y) * p3.z);
}

float vnoise(vec2 p) {
	vec2 i = floor(p), f = fract(p);
	vec2 u = f * f * (3.0 - 2.0 * f);
	return mix(mix(hash12(i), hash12(i + vec2(1, 0)), u.x), mix(hash12(i + vec2(0, 1)), hash12(i + vec2(1, 1)), u.x), u.y);
}

float fbm(vec2 p) {
	float s = 0.0, a = 0.5;
	for (int i = 0; i < 4; i++) { s += vnoise(p) * a; p = p * 2.03 + vec2(1.7, 9.2); a *= 0.5; }
	return s;
}

void vertex() {
	v_world = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	v_centre = MODEL_MATRIX[3].xyz;
}

void fragment() {
	vec2 dir = flow.xy;
	float speed = flow.z;
	vec2 perp = vec2(-dir.y, dir.x);
	vec3 p = v_world;
	float above = p.y - water_level;

	// Stone: grey with mottling and a few darker grains.
	float m = fbm(p.xz * 1.7 + p.y * 0.9);
	vec3 col = stone * (0.78 + 0.45 * m) * (0.9 + 0.1 * vnoise(p.xz * 14.0 + p.y * 9.0));

	// How far up the water reaches here: highest on the upstream face.
	vec2 rel = p.xz - v_centre.xz;
	float upstream = clamp(-dot(normalize(rel + 1e-4), dir), 0.0, 1.0);
	float hv = min(0.05 + 1.6 * speed * speed / (2.0 * 9.81), 0.45);
	float reach = 0.05 + hv * (0.35 + 1.4 * upstream) + 0.25 * hv * fbm(p.xz * 5.0 + vec2(0.0, TIME * 0.7));
	float wet = 1.0 - smoothstep(reach - 0.04, reach + 0.1, above);

	// Thin water sliding over the wet stone along the current: fine moving streaks.
	float along = dot(p.xz, dir), across = dot(p.xz, perp);
	float streak = vnoise(vec2(across * 7.0, along * 1.6 - TIME * (0.6 + 1.6 * speed)));
	streak = smoothstep(0.45, 0.85, streak) * wet * smoothstep(-0.05, 0.03, above);

	// Foam where the water meets the stone, broken up and flickering.
	float line_d = (above - 0.02 - 0.5 * hv * upstream) / (0.04 + 0.08 * hv);
	float foam_line = exp(-line_d * line_d) * (0.25 + 0.75 * upstream) * smoothstep(0.1, 0.7, speed);
	foam_line *= smoothstep(0.35, 0.7, vnoise(vec2(across * 9.0, p.y * 5.0 - TIME * 2.3)) + 0.25 * upstream);

	vec3 wet_col = col * 0.5;
	ALBEDO = mix(mix(col, wet_col, wet), foam_color, clamp(foam_line, 0.0, 1.0) * 0.85);
	ALBEDO += vec3(0.05) * streak;
	ROUGHNESS = mix(mix(0.88, 0.22, wet), 0.08, streak);
	SPECULAR = mix(0.3, 0.55, wet);
}
