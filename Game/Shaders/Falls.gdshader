// A waterfall (FallsBuilder), three meshes with one shader:
//   mode 0, the sheet: UV.x = metres across the lip, UV.y = 0 at the brink → 1 at the pool. COLOR.r = how much water
//     the column carries (threads … full sheet), COLOR.g = a per-column seed. The water leaves the edge as a smooth,
//     glassy, dark tongue, then tears into white streamers that race down and stretch as they speed up; thin columns
//     break into separate threads; full ones stay a sheet.
//   mode 1, the veil: thin white spray just in front of the sheet, lower down.
//   mode 2, the churn: UV.y = 0 where the water lands → 1 nine metres out. A white boil that heaves, spreads and thins.
shader_type spatial;
render_mode cull_disabled, depth_draw_opaque, specular_schlick_ggx;

// Floating origin (World/Origin.cs): scene position + world_origin = world position.
global uniform vec3 world_origin;

uniform int mode = 0;
uniform float drop = 8.0;            // metres
uniform vec3 glass_color : source_color = vec3(0.07, 0.15, 0.14);
uniform vec3 white_color : source_color = vec3(0.93, 0.95, 0.96);

varying vec4 v_col;

float hash(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }
float vnoise(vec2 p) {
	vec2 i = floor(p), f = fract(p);
	vec2 u = f * f * (3.0 - 2.0 * f);
	return mix(mix(hash(i), hash(i + vec2(1, 0)), u.x), mix(hash(i + vec2(0, 1)), hash(i + vec2(1, 1)), u.x), u.y);
}
float fbm(vec2 p) { return vnoise(p) * 0.55 + vnoise(p * 2.1 + 3.7) * 0.3 + vnoise(p * 4.3 + 9.1) * 0.15; }

void vertex() {
	v_col = COLOR;
	if (mode < 2) {
		// The sheet isn't a flat panel: it bulges and ripples as more and less water comes over (slow waves running
		// down it), more so lower down and in fuller columns.
		float s = UV.y * drop;
		float wob = sin(UV.x * 0.9 + s * 0.8 - TIME * 3.2 + COLOR.g * 6.0) * 0.6 + sin(UV.x * 2.3 - s * 1.3 - TIME * 4.7) * 0.4;
		VERTEX += NORMAL * wob * (0.06 + 0.22 * UV.y) * (0.4 + 0.6 * COLOR.r);
	}
	if (mode == 2) {
		// The boil heaves.
		float d = UV.y;
		VERTEX.y += 0.09 * (1.0 - d) * sin(TIME * 3.1 + UV.x * 1.7 + d * 6.0) * (0.5 + 0.5 * sin(TIME * 1.3 + UV.x * 0.4));
	}
}

void fragment() {
	float across = UV.x;
	float amount = v_col.r, seed = v_col.g;
	if (mode == 2) {
		// ----------------------------------------------------------------- churn
		float d = UV.y;
		float n = fbm(vec2(across * 0.55 + seed, d * 3.0 - TIME * 0.7)) * 0.65
		        + fbm(vec2(across * 1.6 + 5.0, d * 7.5 - TIME * 1.5)) * 0.35;
		float white = smoothstep(0.25, 0.65, n + 0.55 * (1.0 - d) - 0.25 + 0.2 * amount);
		float bubbles = step(0.82, vnoise(vec2(across * 6.0, d * 30.0 - TIME * 2.0)));
		ALBEDO = white_color * (0.82 + 0.18 * n);
		ROUGHNESS = 0.75;
		SPECULAR = 0.3;
		ALPHA = clamp(white * (1.0 - smoothstep(0.35, 1.0, d)) + bubbles * 0.25 * (1.0 - d), 0.0, 0.97);
	} else {
		// ----------------------------------------------------------------- sheet and veil
		float down = UV.y;
		float s = down * drop;                               // metres fallen
		// Streaks race down and stretch as the water speeds up (distance ~ √ fallen).
		float y1 = sqrt(s + 0.4) * 2.0 - TIME * 2.6;
		float y2 = sqrt(s + 0.4) * 4.6 - TIME * 5.5;
		float big = fbm(vec2(across * 0.9 + seed * 7.0, y1));
		float fine = fbm(vec2(across * 3.8 + seed * 13.0, y2));
		float streak = big * 0.6 + fine * 0.4;
		// Glassy tongue: full columns stay smooth further down.
		float glass = 1.0 - smoothstep(0.03, 0.12 + 0.22 * amount, down);
		float white = (1.0 - glass) * clamp(smoothstep(0.35, 0.7, streak + 0.35 * down + 0.15 * (1.0 - amount)), 0.0, 1.0);
		white = max(white, (1.0 - glass) * smoothstep(0.55, 1.0, down) * 0.9);
		if (mode == 1) {
			// Veil: a thin, bright mist of spray just in front, only once the water has broken up.
			ALBEDO = white_color;
			ROUGHNESS = 0.9;
			SPECULAR = 0.2;
			ALPHA = clamp(smoothstep(0.25, 0.8, down) * smoothstep(0.4, 0.75, fine) * 0.35 * (0.5 + amount), 0.0, 0.5);
		} else {
			ALBEDO = mix(glass_color, white_color, white);
			// Highlights sliding over the glassy tongue.
			ROUGHNESS = mix(0.04, 0.65, white);
			SPECULAR = mix(0.7, 0.35, white);
			// Thin columns tear into separate threads; full ones stay a sheet. A little see-through everywhere.
			float threads = smoothstep(0.62 - 0.5 * amount, 0.8 - 0.45 * amount, streak + 0.12 * (1.0 - down));
			float body = mix(threads, 1.0, smoothstep(0.75, 1.0, amount));
			float a = mix(0.82, 0.96, white) * mix(1.0, body, smoothstep(0.08, 0.3, down));
			// Soft top and bottom edges (into the river above and the churn below).
			a *= smoothstep(0.0, 0.03, down) * (1.0 - smoothstep(0.93, 1.0, down) * 0.6);
			ALPHA = clamp(a, 0.0, 0.98);
			BACKLIGHT = ALBEDO * 0.35 * white;
		}
	}
}
