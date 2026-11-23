// Terrain: grass, earth, sand and gravel bars, rock, wet banks, silty riverbed with caustics.
// Material choice reads the reach field (height above the local water surface, distance to the shore).
shader_type spatial;
render_mode cull_back, specular_schlick_ggx;

uniform sampler2D shape_tex : filter_linear, repeat_disable;  // eta, shore distance, terrain height
uniform vec2 field_size = vec2(601.0, 1601.0);
uniform float field_cell = 2.0;
uniform float far_eta_a = 25.0;   // outside the field: water level trend a + b·z
uniform float far_eta_b = -0.001;

uniform vec3 grass_a : source_color = vec3(0.20, 0.29, 0.09);
uniform vec3 grass_b : source_color = vec3(0.31, 0.36, 0.13);
uniform vec3 grass_dry : source_color = vec3(0.42, 0.40, 0.22);
uniform vec3 earth : source_color = vec3(0.27, 0.21, 0.14);
uniform vec3 sand : source_color = vec3(0.56, 0.50, 0.38);
uniform vec3 gravel : source_color = vec3(0.45, 0.42, 0.37);
uniform vec3 rock : source_color = vec3(0.38, 0.37, 0.35);
uniform vec3 silt : source_color = vec3(0.24, 0.21, 0.15);
uniform float caustic_strength = 0.9;
uniform float caustic_scale = 4.5;   // pattern frequency (the old 0.9 drew ~7 m cells)

varying vec3 v_world;
varying vec3 v_wnormal;

void vertex() {
	v_world = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	v_wnormal = normalize((MODEL_MATRIX * vec4(NORMAL, 0.0)).xyz);
}

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

// Animated caustics: two layers of folded wave interference.
float caustics(vec2 p, float t) {
	float c = 0.0;
	for (int i = 0; i < 2; i++) {
		vec2 q = p * (0.9 + float(i) * 0.6) + vec2(t * 0.21, t * 0.15) * (1.0 + float(i));
		float a = sin(q.x + sin(q.y * 1.3 + t * 0.5)) + sin(q.y * 1.1 + sin(q.x * 0.9 - t * 0.4));
		c += pow(max(0.0, 1.0 - abs(a) * 0.7), 6.0);
	}
	return c * 0.5;
}

void fragment() {
	vec2 fuv = (v_world.xz / field_cell + 0.5) / field_size;
	bool inside = fuv.x > 0.0 && fuv.x < 1.0 && fuv.y > 0.0 && fuv.y < 1.0;
	vec4 sh = texture(shape_tex, clamp(fuv, vec2(0.0), vec2(1.0)));
	float eta = inside ? sh.r : far_eta_a + far_eta_b * v_world.z;
	float shore = inside ? sh.g : 200.0;
	float above = v_world.y - eta;
	// Water is only drawn within ~5 m of the shore (WaterBuilder). Floodplain hollows further out can dip below the
	// nearby river level, but they are dry ground, not riverbed.
	if (shore > 4.5) above = max(above, 0.3);
	float slope = 1.0 - clamp(v_wnormal.y, 0.0, 1.0);

	vec2 p = v_world.xz;
	float n1 = fbm(p * 0.045);
	float n2 = fbm(p * 0.37);
	float n3 = vnoise(p * 2.7);

	// Meadow: two greens with dry patches.
	vec3 col = mix(grass_a, grass_b, smoothstep(0.35, 0.7, n1));
	col = mix(col, grass_dry, smoothstep(0.62, 0.8, fbm(p * 0.012 + 4.0)) * 0.7);
	col *= 0.85 + 0.3 * n2;

	// Bare earth on steeper ground and cut banks.
	float earth_k = smoothstep(0.18, 0.38, slope + (n2 - 0.5) * 0.15);
	col = mix(col, earth * (0.8 + 0.4 * n3), earth_k);

	// Sand and gravel bars just above the water on gentle slopes; gravel where the current is faster (rougher).
	float bar = (1.0 - smoothstep(0.25, 1.1 + n1 * 0.6, above)) * (1.0 - smoothstep(0.12, 0.3, slope)) * step(shore, 45.0);
	vec3 bar_col = mix(sand, gravel, smoothstep(0.45, 0.65, n2)) * (0.85 + 0.3 * n3);
	col = mix(col, bar_col, bar);

	// Rock on the steepest slopes (valley walls).
	float rock_k = smoothstep(0.42, 0.62, slope + (n1 - 0.5) * 0.2);
	col = mix(col, rock * (0.75 + 0.5 * n2), rock_k);

	// Riverbed: sand in the shallows to dark silt in the deep.
	float under = -above;
	if (under > 0.0) {
		vec3 bedc = mix(mix(sand, gravel, n2) * 0.9, silt, smoothstep(0.3, 2.5, under));
		col = mix(col, bedc, smoothstep(0.0, 0.25, under));
	}

	// Wet band: darker and glossier just above the water line (higher where the river laps more).
	float wet = 1.0 - smoothstep(0.02, 0.28 + n3 * 0.15, above);
	col *= mix(1.0, 0.55, wet);
	float rough = mix(0.92, 0.35, wet);

	ALBEDO = col;
	// Under water the bed has no sun glint: sediment and water have nearly the same refractive index, so there is
	// almost nothing to reflect (a glossy bed showed through the water as a green-yellow sun path).
	float submerged = smoothstep(0.0, 0.04, under);
	ROUGHNESS = mix(rough, 1.0, submerged);
	SPECULAR = mix(0.35, 0.0, submerged);

	if (under > 0.0) {
		// Real river caustics are a fine net (decimetres to ~1 m), sharpest in the shallows, fading with depth.
		float c = caustics(p * caustic_scale, TIME * 1.3) * caustic_strength * exp(-under * 0.9) * smoothstep(0.0, 0.15, under);
		EMISSION = col * c * 0.8;
	}
}
