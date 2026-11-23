// River water. Everything is driven by the reach field (FieldTextures.cs): the current advects the ripples and
// the foam, depth and speed decide roughness and whitewater, the shore distance draws lap lines.
//
// Light model:
//   ALBEDO   = light scattered back by the water body (lit by the sun, so it takes shadows), thicker = more
//   EMISSION = the riverbed seen through the water (refracted screen, absorbed with path length)
//              + screen-space reflections of the banks and trees
//   SPECULAR = the engine's sky reflection and sun glint, switched off where the SSR found something
// See docs/WATER.md for the reasoning and the knobs.
shader_type spatial;
render_mode blend_mix, cull_back, depth_draw_opaque, specular_schlick_ggx;

uniform sampler2D screen_tex : hint_screen_texture, filter_linear_mipmap, repeat_disable;
uniform sampler2D depth_tex : hint_depth_texture, filter_nearest, repeat_disable;

// Reach field
uniform sampler2D flow_tex : filter_linear, repeat_disable;   // vel x, vel z, depth, foam
uniform sampler2D shape_tex : filter_linear, repeat_disable;  // eta, shore distance, terrain height
uniform vec2 field_size = vec2(601.0, 1601.0);
uniform float field_cell = 2.0;

// Surface detail
uniform sampler2D ripple_large : filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D ripple_small : filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D foam_tex : filter_linear_mipmap_anisotropic, repeat_enable;
uniform float large_scale = 9.0;      // metres per ripple_large tile
uniform float small_scale = 2.3;
uniform float flow_cycle = 2.2;       // seconds per flow-map phase
uniform float ripple_strength = 0.55;
uniform vec2 wind = vec2(0.6, 0.25);  // m/s-ish, drives ripples on slow water

// Water colour (a temperate, slightly turbid river)
uniform vec3 absorption = vec3(0.40, 0.31, 0.66);    // per metre of path, rgb: blue goes first (tannins, silt)
uniform vec3 scatter_color : source_color = vec3(0.085, 0.090, 0.050);
uniform float scatter_density = 0.55;                 // per metre
uniform float turbidity = 1.0;
uniform vec3 foam_color : source_color = vec3(0.86, 0.87, 0.84);

uniform float refraction_strength = 0.035;
uniform int ssr_steps = 40;
uniform float ssr_strength = 1.0;
uniform float time_offset = 0.0;

varying vec3 v_world;

void vertex() {
	v_world = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

vec2 field_uv(vec2 xz) {
	return (xz / field_cell + 0.5) / field_size;
}

vec3 view_pos_at(vec2 uv, mat4 inv_proj) {
	float d = textureLod(depth_tex, uv, 0.0).r;
	vec4 v = inv_proj * vec4(uv * 2.0 - 1.0, d, 1.0);
	return v.xyz / v.w;
}

vec3 unpack_n(vec3 c) { return c * 2.0 - 1.0; }

// Two-phase flow-map sample of a tangent-space normal map. Phases are jittered per pixel to hide pulsing.
vec3 flow_normal(sampler2D tex, vec2 xz, vec2 flow, float scale, float cycle, float t, float jitter) {
	float p0 = fract(t / cycle + jitter);
	float p1 = fract(t / cycle + jitter + 0.5);
	float w0 = 1.0 - abs(1.0 - 2.0 * p0);
	vec2 uv0 = (xz - flow * p0 * cycle) / scale;
	vec2 uv1 = (xz - flow * p1 * cycle) / scale + vec2(0.37, 0.61);
	vec3 n0 = unpack_n(texture(tex, uv0).rgb);
	vec3 n1 = unpack_n(texture(tex, uv1).rgb);
	return normalize(mix(n1, n0, w0));
}

float flow_scalar(sampler2D tex, vec2 xz, vec2 flow, float scale, float cycle, float t, float jitter) {
	float p0 = fract(t / cycle + jitter);
	float p1 = fract(t / cycle + jitter + 0.5);
	float w0 = 1.0 - abs(1.0 - 2.0 * p0);
	float a = texture(tex, (xz - flow * p0 * cycle) / scale).r;
	float b = texture(tex, (xz - flow * p1 * cycle) / scale + vec2(0.53, 0.19)).r;
	return mix(b, a, w0);
}

// Whiteout blend of two tangent-space normals.
vec3 blend_n(vec3 a, vec3 b) {
	return normalize(vec3(a.xy + b.xy, a.z * b.z));
}

void fragment() {
	float t = TIME + time_offset;
	vec2 xz = v_world.xz;
	vec2 fuv = field_uv(xz);
	vec4 fl = texture(flow_tex, fuv);
	vec4 sh = texture(shape_tex, fuv);
	vec2 flow = fl.xy;
	float speed = length(flow);
	float depth_field = fl.z;
	float shore = sh.g;                     // negative in water: distance to the shore (m)
	float froude = speed / sqrt(9.81 * max(depth_field, 0.08));
	float rough_water = smoothstep(0.35, 0.9, froude);   // riffles and rapids

	// ------------------------------------------------------------- surface normal
	float jitter = texture(foam_tex, xz * 0.013).r * 0.6;
	vec3 nL = flow_normal(ripple_large, xz, flow, large_scale, flow_cycle, t, jitter);
	vec3 nS = flow_normal(ripple_small, xz, flow * 1.1, small_scale, flow_cycle * 0.7, t, jitter + 0.25);
	// Wind ripples: not carried by the current, strongest on slow wide water.
	vec2 wuv = (xz + wind * t * 1.6) / (small_scale * 0.8);
	vec3 nW = unpack_n(texture(ripple_small, wuv + vec2(0.21, 0.73)).rgb);
	vec3 nW2 = unpack_n(texture(ripple_small, (xz + wind.yx * vec2(-1.0, 1.0) * t * 1.1) / (small_scale * 1.7)).rgb);
	float slow = 1.0 - smoothstep(0.08, 0.6, speed);
	float wind_k = length(wind) * (0.35 + 0.65 * slow);
	float flow_k = ripple_strength * (0.45 + 0.9 * smoothstep(0.1, 1.6, speed) + 1.2 * rough_water);
	vec3 n_ts = blend_n(vec3(nL.xy * flow_k, nL.z), vec3(nS.xy * flow_k * 0.8, nS.z));
	n_ts = blend_n(n_ts, vec3((nW.xy + nW2.xy) * 0.35 * wind_k, 1.0));
	// Calm the very edge (thin water hugging the bank is smooth).
	n_ts.xy *= mix(0.35, 1.0, smoothstep(-0.2, -3.0, shore));
	n_ts = normalize(n_ts);
	vec3 n_world = normalize(vec3(n_ts.x, n_ts.z, n_ts.y));
	vec3 n_view = normalize((VIEW_MATRIX * vec4(n_world, 0.0)).xyz);
	NORMAL = n_view;

	// ------------------------------------------------------------- geometry through the water
	vec3 vpos = VERTEX;
	vec3 vdir = normalize(-vpos);
	float cos_v = clamp(dot(n_view, vdir), 0.0, 1.0);
	float fresnel = 0.02 + 0.98 * pow(1.0 - cos_v, 5.0);

	vec3 behind = view_pos_at(SCREEN_UV, INV_PROJECTION_MATRIX);
	float thickness = max(length(behind - vpos), 0.0);
	float dist = length(vpos);
	vec2 ruv = SCREEN_UV + n_world.xz * refraction_strength * clamp(thickness, 0.0, 3.0) / (1.0 + dist * 0.06);
	vec3 behind_r = view_pos_at(ruv, INV_PROJECTION_MATRIX);
	if (behind_r.z > vpos.z - 0.02) { ruv = SCREEN_UV; behind_r = behind; } // refracted sample is above the water
	float path = max(length(behind_r - vpos), 0.0) * turbidity;
	vec3 transmit = exp(-absorption * path);
	float lod = clamp(path * 0.35, 0.0, 4.0);
	vec3 bed = textureLod(screen_tex, ruv, lod).rgb;
	vec3 through = bed * transmit;
	float scatter_amt = 1.0 - exp(-scatter_density * path);

	// ------------------------------------------------------------- screen-space reflection
	vec3 n_ssr = normalize(mix(vec3(0.0, 1.0, 0.0), n_world, 0.35));
	vec3 n_ssr_v = normalize((VIEW_MATRIX * vec4(n_ssr, 0.0)).xyz);
	vec3 r = normalize(reflect(normalize(vpos), n_ssr_v));
	vec3 ssr_col = vec3(0.0);
	float ssr_mask = 0.0;
	if (ssr_steps > 0 && r.z < 0.25) {
		float step_len = 0.3 + dist * 0.012;
		vec3 p = vpos;
		vec3 prev = vpos;
		for (int i = 0; i < ssr_steps; i++) {
			prev = p;
			p += r * step_len;
			step_len *= 1.13;
			vec4 c = PROJECTION_MATRIX * vec4(p, 1.0);
			vec2 uv = c.xy / c.w * 0.5 + 0.5;
			if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0 || c.w <= 0.0) break;
			vec3 s = view_pos_at(uv, INV_PROJECTION_MATRIX);
			float diff = s.z - p.z;  // > 0: the scene is in front of the ray point
			if (diff > 0.0 && diff < step_len * 2.5 + 0.4) {
				// Refine between prev and p.
				vec3 a = prev, b = p;
				for (int k = 0; k < 5; k++) {
					vec3 m = 0.5 * (a + b);
					vec4 cm = PROJECTION_MATRIX * vec4(m, 1.0);
					vec2 um = cm.xy / cm.w * 0.5 + 0.5;
					vec3 sm = view_pos_at(um, INV_PROJECTION_MATRIX);
					if (sm.z - m.z > 0.0) b = m; else a = m;
				}
				vec4 cb = PROJECTION_MATRIX * vec4(b, 1.0);
				uv = cb.xy / cb.w * 0.5 + 0.5;
				vec2 edge = smoothstep(vec2(0.0), vec2(0.08), uv) * smoothstep(vec2(0.0), vec2(0.08), 1.0 - uv);
				float fade = edge.x * edge.y * (1.0 - smoothstep(0.6, 1.0, float(i) / float(ssr_steps)));
				fade *= 1.0 - smoothstep(-0.1, 0.25, r.z);
				ssr_col = textureLod(screen_tex, uv, 0.0).rgb;
				ssr_mask = fade * ssr_strength;
				break;
			}
		}
	}

	// ------------------------------------------------------------- foam
	// Coverage from the field (rocks, riffles, current against shores) plus a thin lap line at the bank.
	float foam_field = fl.w;
	float lap = (1.0 - smoothstep(0.0, 0.8, -shore)) * (0.12 + 0.3 * smoothstep(0.15, 1.0, speed));
	float churn = rough_water * 0.45;
	float coverage = clamp(foam_field * 0.8 + lap + churn, 0.0, 1.0) * 0.8;   // never a solid sheet
	// Bubbly net carried by the flow, and streaks stretched along the current.
	float fpat = flow_scalar(foam_tex, xz, flow, 3.0, flow_cycle * 1.3, t, jitter);
	vec2 fdir = speed > 0.02 ? flow / speed : vec2(0.0, 1.0);
	vec2 fr = vec2(dot(xz, vec2(-fdir.y, fdir.x)), dot(xz, fdir));
	float sp0 = fract(t / (flow_cycle * 1.6) + jitter), sp1 = fract(t / (flow_cycle * 1.6) + jitter + 0.5);
	float sw = 1.0 - abs(1.0 - 2.0 * sp0);
	float s0 = texture(foam_tex, vec2(fr.x, (fr.y - speed * sp0 * flow_cycle * 1.6) * 0.22) / 4.5).r;
	float s1 = texture(foam_tex, vec2(fr.x, (fr.y - speed * sp1 * flow_cycle * 1.6) * 0.22) / 4.5 + vec2(0.31, 0.77)).r;
	float streak = mix(s1, s0, sw);
	float pattern = fpat * 0.55 + streak * 0.6;
	float th = 1.05 - coverage;
	float foam = smoothstep(th, th + 0.14, pattern) * smoothstep(0.03, 0.2, coverage);

	// ------------------------------------------------------------- compose
	float refl_w = fresnel;
	vec3 body = scatter_color * scatter_amt;
	ALBEDO = mix(body * (1.0 - refl_w), foam_color, foam);
	EMISSION = (through * (1.0 - refl_w) + ssr_col * refl_w * ssr_mask) * (1.0 - foam);
	SPECULAR = mix(0.35 * (1.0 - ssr_mask), 0.1, foam);
	// Far away, ripples smaller than a pixel widen the sun's reflection into a glittering path.
	float far_rough = smoothstep(15.0, 450.0, dist) * 0.1;
	ROUGHNESS = mix(mix(0.025, 0.09, rough_water) + far_rough, 0.65, foam);
	METALLIC = 0.0;
	// Fade the last centimetres into the bank so the shoreline isn't a hard cut.
	ALPHA = clamp(thickness * 6.0, 0.0, 1.0);
}
