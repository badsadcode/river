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
uniform float flow_cycle = 1.4;       // seconds per flow-map phase (shorter = less stretching in shear)
uniform float ripple_strength = 0.55;
uniform vec2 wind = vec2(0.6, 0.25);  // m/s-ish, drives ripples on slow water

// Water colour (a temperate, slightly turbid river)
uniform vec3 absorption = vec3(0.40, 0.31, 0.66);    // per metre of path, rgb: blue goes first (tannins, silt)
uniform vec3 scatter_color : source_color = vec3(0.10, 0.10, 0.055);
uniform float scatter_density = 0.55;                 // per metre
uniform float turbidity = 1.0;
uniform vec3 foam_color : source_color = vec3(0.80, 0.79, 0.74);   // river foam is off-white, a little creamy
uniform float foam_bump = 6.0;     // how strongly foam lumps catch the light
uniform float foam_scale = 4.0;   // metres per foam texture tile
uniform float foam_cycle = 3.2;   // seconds per flow-map phase for foam (long: foam keeps its shape while it drifts)
uniform float foam_max = 0.6;     // at most this share of the surface is white, even in the wildest riffle

uniform float refraction_strength = 0.035;
uniform int ssr_steps = 40;
uniform float ssr_strength = 1.0;
uniform float time_offset = 0.0;
uniform float roughness_boost = 0.0;  // set by the day cycle (softer glints at night)
uniform int debug_view = 0;

// Waves (vertex displacement): the raft's physics clock and strength (see wave_height).
uniform float wave_time = 0.0;
uniform float wave_strength = 1.0;

varying vec3 v_world;
varying vec2 v_wave_grad;

vec2 field_uv(vec2 xz) {
	return (xz / field_cell + 0.5) / field_size;
}

// Mirror of Game/Core/SurfaceWaves.cs Height(): keep the two identical, the raft floats on the C# copy.
float wave_height(vec2 p, float t, vec2 v, float depth) {
	float speed = length(v);
	float fr = speed / sqrt(9.81 * max(depth, 0.08));
	float rough = smoothstep(0.35, 0.9, fr);
	float shallow_fade = smoothstep(0.05, 0.5, depth);
	float h = 0.018 * sin(p.x * 0.31 + t * 1.7) + 0.014 * sin(p.y * 0.27 - t * 1.3 + 1.3)
	        + 0.010 * sin((p.x + p.y) * 0.53 + t * 2.3);
	h += rough * (0.05 * sin(p.x * 1.1 + t * 3.1) + 0.04 * sin(p.y * 1.4 - t * 3.7));
	if (speed > 0.3) {
		vec2 d = v / speed;
		float along = dot(p, d), across = -p.x * d.y + p.y * d.x;
		float k = clamp(9.81 / (speed * speed), 6.2831853 / 8.0, 6.2831853 / 2.5);
		float amp = 0.13 * smoothstep(0.45, 1.0, fr);
		float breakup = 0.55 + 0.45 * sin(across * 0.9 + 1.7 * sin(along * 0.23));
		h += amp * breakup * sin(along * k + 0.25 * sin(t * 1.9 + across * 0.4));
	}
	return h * shallow_fade;
}

void vertex() {
	vec3 w = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	vec4 f = textureLod(flow_tex, field_uv(w.xz), 0.0);
	const float e = 0.3;
	float h = wave_height(w.xz, wave_time, f.xy, f.z);
	float hx = wave_height(w.xz + vec2(e, 0.0), wave_time, f.xy, f.z);
	float hz = wave_height(w.xz + vec2(0.0, e), wave_time, f.xy, f.z);
	v_wave_grad = vec2(hx - h, hz - h) / e * wave_strength;
	VERTEX.y += h * wave_strength;   // water meshes are built in world space (identity model matrix)
	v_world = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
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

vec2 flow_rg(sampler2D tex, vec2 xz, vec2 flow, float scale, float cycle, float t, float jitter) {
	float p0 = fract(t / cycle + jitter);
	float p1 = fract(t / cycle + jitter + 0.5);
	float w0 = 1.0 - abs(1.0 - 2.0 * p0);
	vec2 a = texture(tex, (xz - flow * p0 * cycle) / scale).rg;
	vec2 b = texture(tex, (xz - flow * p1 * cycle) / scale + vec2(0.53, 0.19)).rg;
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
	// Phase jitter must be smooth: any fine detail here turns into marbled smears once multiplied by the flow.
	float jitter = textureLod(foam_tex, xz * 0.0035, 7.0).r * 0.8;
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
	// The displaced waves' own slope (standing waves in riffles, chop, swell).
	n_ts = blend_n(n_ts, vec3(-v_wave_grad, 1.0));
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
	// How much of the surface is foam comes from the field (rocks, riffles, current against shores, eddy lines,
	// advected with the current) plus a thin lap line at the bank. The foam texture is equalised, so a coverage c
	// whitens about a fraction c of the surface.
	float foam_field = fl.w;
	float lap = (1.0 - smoothstep(0.0, 0.6, -shore)) * (0.08 + 0.22 * smoothstep(0.15, 1.0, speed));
	float coverage = foam_field * 0.7 + lap + rough_water * 0.15;
	// Large, slowly drifting patches: foam comes in clumps and rafts, never as an even sheet.
	float patch = flow_scalar(foam_tex, xz, flow, 38.0, foam_cycle * 3.0, t, jitter);
	coverage = clamp(coverage * mix(0.35, 1.3, patch), 0.0, foam_max);
	// Cloudy foam carried by the current, its edges broken up by a finer layer; bubbles in the thin parts.
	vec2 fa = flow_rg(foam_tex, xz, flow, foam_scale, foam_cycle, t, jitter);
	vec2 fb = flow_rg(foam_tex, xz, flow, foam_scale * 0.29, foam_cycle * 0.8, t, jitter + 0.37);
	float pattern = fa.x + (fb.x - 0.5) * 0.3;
	float th = 1.0 - coverage;
	// Not paint: thin foam is a see-through lace of bubbles, only the thickest cores are nearly opaque, and the
	// colour varies (bright where bubbles catch light, greyer in thin and creased foam).
	float present = smoothstep(th, th + 0.12, pattern);
	float density = smoothstep(th, th + 0.45, pattern);
	float lace = mix(0.35, 1.0, max(fb.y, density * density));
	float foam = present * (0.3 + 0.62 * density) * lace * smoothstep(0.01, 0.06, coverage);
	vec3 foam_albedo = foam_color * (0.62 + 0.38 * density) * mix(0.82, 1.0, fb.y);
	// Lumpy surface: tilt the normal along the foam's own relief so the sun shades its bumps.
	vec2 fg = vec2(dFdx(pattern), dFdy(pattern)) / (1.0 + dist * 0.08);
	NORMAL = normalize(NORMAL + vec3(-fg * foam_bump * present, 0.0));

	// ------------------------------------------------------------- compose
	float refl_w = fresnel;
	vec3 body = scatter_color * scatter_amt;
	ALBEDO = mix(body * (1.0 - refl_w), foam_albedo, foam);
	EMISSION = (through * (1.0 - refl_w) + ssr_col * refl_w * ssr_mask) * (1.0 - foam);
	SPECULAR = mix(0.35 * (1.0 - ssr_mask), 0.1, foam);
	// Far away, ripples smaller than a pixel widen the sun's reflection into a glittering path.
	float far_rough = smoothstep(15.0, 450.0, dist) * 0.1;
	ROUGHNESS = mix(mix(0.025, 0.09, rough_water) + far_rough + roughness_boost, 0.65, foam);
	METALLIC = 0.0;
	// Fade the last centimetres into the bank so the shoreline isn't a hard cut.
	ALPHA = clamp(thickness * 6.0, 0.0, 1.0);
	// Debug views (tuning): 1 = engine lighting only (sun glint, sky), 2 = emission only (bed + SSR), 3 = albedo only.
	if (debug_view == 1) { ALBEDO = vec3(0.0); EMISSION = vec3(0.0); }
	if (debug_view == 2) { ALBEDO = vec3(0.0); SPECULAR = 0.0; }
	if (debug_view == 3) { EMISSION = vec3(0.0); SPECULAR = 0.0; }
}
