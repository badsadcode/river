// The rock of a waterfall (FallsBuilder.Stones): dark layered stone in stacked slabs, with fine strata, wet and glossy
// near the falls (the spray reaches a few metres above the brink), water films sliding down steep faces, and moss on
// the upward faces above the pool. COLOR.r = per-slab brightness.
shader_type spatial;
render_mode cull_back, specular_schlick_ggx;

// Floating origin (World/Origin.cs): scene position + world_origin = world position.
global uniform vec3 world_origin;

uniform float pool_level = 0.0;     // water below the falls (m, grid-local = world height)
uniform float top_level = 0.0;      // water above the falls
uniform vec3 stone : source_color = vec3(0.25, 0.24, 0.22);
uniform vec3 moss : source_color = vec3(0.24, 0.30, 0.11);

varying vec3 v_world;
varying vec3 v_n;
varying float v_y;

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
	v_world = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz + world_origin;
	v_n = normalize((MODEL_MATRIX * vec4(NORMAL, 0.0)).xyz);
	v_y = (MODEL_MATRIX * vec4(VERTEX, 1.0)).y;
}

void fragment() {
	vec3 p = v_world;
	float y = v_y;
	float h = p.x + p.z;   // a horizontal coordinate for patterns on vertical faces
	// Stone: dark, mottled, per-slab tone.
	float m = fbm(vec2(h * 0.9, p.y * 1.6) + p.xz * 0.4);
	vec3 col = stone * COLOR.r * (0.7 + 0.55 * m);
	// Fine strata: thin darker lines along the bedding.
	float band = smoothstep(0.82, 1.0, 0.5 + 0.5 * sin(p.y * 11.0 + 3.0 * fbm(p.xz * 0.6)));
	col *= 1.0 - 0.3 * band;
	// Moss on upward faces above the pool, in patches.
	float up = smoothstep(0.5, 0.85, v_n.y);
	float moss_k = up * smoothstep(0.35, 0.6, fbm(p.xz * 0.7 + 4.0)) * smoothstep(pool_level + 0.3, pool_level + 0.8, y);
	col = mix(col, moss * (0.75 + 0.5 * m), moss_k * 0.85);
	// Wet: everything the spray reaches (to a few metres above the brink), darker and glossy; driest high up.
	float wet = 1.0 - smoothstep(top_level + 0.8, top_level + 4.0, y);
	col *= mix(1.0, 0.58, wet * (1.0 - 0.5 * moss_k));
	// Water films sliding down steep faces below the brink.
	float steep = 1.0 - smoothstep(0.3, 0.6, abs(v_n.y));
	float film = smoothstep(0.55, 0.9, vnoise(vec2(h * 5.0, p.y * 1.2 + TIME * 2.2))) * steep * (1.0 - smoothstep(top_level - 0.2, top_level + 0.3, y));
	ALBEDO = col + vec3(0.06) * film;
	ROUGHNESS = mix(mix(0.9, 0.3, wet), 0.08, film);
	SPECULAR = mix(0.25, 0.5, wet);
}
