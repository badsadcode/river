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
uniform float large_scale = 13.0;     // metres per ripple_large tile
uniform float small_scale = 3.6;
uniform float flow_cycle = 2.0;       // seconds per flow-map phase (shorter = less stretching in shear)
uniform float ripple_strength = 0.55;
uniform float ripple_speed = 0.12;    // m/s: how fast small ripples travel on their own across the water
uniform float adv_smooth = 3.0;       // m: radius over which the texture-carrying current is averaged
uniform float adv_cap = 1.8;          // m/s: textures never race faster than this (fast riffles smear otherwise)
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

// The segment this mesh belongs to (docs/STREAMING.md §5): meshes are built in the segment's grid coordinates and
// placed by the node's transform, so the field is sampled with the model-space position; patterns (ripples, waves,
// foam) use world positions so they run on across segments. The field's current is in grid axes: frame_rot
// (cos, sin of the heading) turns it into world axes. Neighbours overlap; each draws only its side of the cut lines
// (clip_a: keep where dot(p − a.xy, a.zw) ≥ 0, clip_b: keep where < 0), once that neighbour is there.
uniform vec2 frame_rot = vec2(1.0, 0.0);
uniform vec4 clip_a = vec4(0.0);
uniform vec4 clip_b = vec4(0.0);
uniform float clip_a_on = 0.0;
uniform float clip_b_on = 0.0;

varying vec3 v_world;
varying vec2 v_local;
varying vec2 v_wave_grad;

vec2 field_uv(vec2 local_xz) {
	return (local_xz / field_cell + 0.5) / field_size;
}

vec2 to_world_dir(vec2 v) {
	return vec2(v.x * frame_rot.x + v.y * frame_rot.y, -v.x * frame_rot.y + v.y * frame_rot.x);
}

// Mirror of SurfaceWaves.Gust(): wind strength patches, shared by the waves and the ripples.
float gust(vec2 p, float t) {
	float g = 0.5 + 0.5 * (0.5 * sin(0.023 * p.x + 0.011 * p.y + 0.13 * t)
	                     + 0.3 * sin(-0.009 * p.x + 0.027 * p.y - 0.09 * t + 2.1)
	                     + 0.2 * sin(0.031 * p.x - 0.017 * p.y + 0.17 * t + 4.0));
	return smoothstep(0.2, 0.8, g);
}

float boil(vec2 q) {
	float b = max(0.0, sin(q.x * 0.41 + 1.1) * sin(q.y * 0.33 + 0.4) + 0.4 * sin((q.x - q.y) * 0.27));
	return b * b;
}

// Mirror of Game/Core/SurfaceWaves.cs Height(): keep the two identical, the raft floats on the C# copy.
float wave_height(vec2 p, float t, vec2 v, float depth, float calm) {
	float speed = length(v);
	float fr = speed / sqrt(9.81 * max(depth, 0.08));
	float rough = smoothstep(0.35, 0.9, fr);
	float shallow_fade = smoothstep(0.05, 0.5, depth);
	float h = 0.018 * sin(p.x * 0.31 + t * 1.7) + 0.014 * sin(p.y * 0.27 - t * 1.3 + 1.3)
	        + 0.010 * sin((p.x + p.y) * 0.53 + t * 2.3);
	h += rough * (0.05 * sin(p.x * 1.1 + t * 3.1) + 0.04 * sin(p.y * 1.4 - t * 3.7));
	float wind_amp = gust(p, t) * (1.0 - 0.75 * calm) * smoothstep(0.4, 2.5, depth);
	h += wind_amp * (0.012 * sin(p.x * 1.71 + p.y * 0.83 - t * 4.33)
	           + 0.016 * sin(p.x * 1.26 + p.y * 0.52 - t * 3.66 + 1.9)
	           + 0.014 * sin(p.x * 0.83 + p.y * 0.67 - t * 3.18 + 4.2));
	float boil_k = smoothstep(0.5, 1.4, speed) * smoothstep(1.2, 2.6, depth) * (1.0 - calm);
	if (boil_k > 0.0) {
		const float T = 9.0;
		float p0 = fract(t / T), p1 = fract(t / T + 0.5);
		float w0 = 1.0 - abs(1.0 - 2.0 * p0);
		float a = boil(p - v * p0 * T), b = boil(p - v * p1 * T + vec2(7.3, 3.1));
		h += 0.025 * boil_k * mix(b, a, w0);
	}
	if (speed > 0.3) {
		vec2 d = v / speed;
		float along = dot(p, d), across = -p.x * d.y + p.y * d.x;
		float k = clamp(9.81 / (speed * speed), 6.2831853 / 8.0, 6.2831853 / 2.5);
		float amp = 0.13 * smoothstep(0.45, 1.0, fr)
		          + 0.24 * smoothstep(1.6, 3.0, speed) * smoothstep(0.8, 1.3, fr);   // rapids: big haystacks
		float breakup = 0.55 + 0.45 * sin(across * 0.9 + 1.7 * sin(along * 0.23));
		h += amp * breakup * sin(along * k + 0.25 * sin(t * 1.9 + across * 0.4));
	}
	return h * shallow_fade;
}

// Rocks (x, z, radius, top above the surface) and the current approaching each (x, z). Set by WaterBuilder.
uniform vec4 rocks[64];
uniform vec2 rock_flow[64];
uniform int rock_count = 0;

// Mirror of SurfaceWaves.RockBump(): pillow upstream, drawn down beside, hollow behind.
float rock_bump(vec2 p, vec4 r, vec2 fl) {
	float speed = length(fl);
	vec2 dv = p - r.xy;
	float reach = r.z * 3.5;
	if (speed < 0.05 || dot(dv, dv) > reach * reach) return 0.0;
	float hv = min(0.05 + 1.6 * speed * speed / (2.0 * 9.81), 0.45);
	vec2 d = fl / speed;
	float u = dot(dv, d) / r.z, v = (-dv.x * d.y + dv.y * d.x) / r.z;
	float emerg = smoothstep(-0.6, 0.0, r.w);
	float deep = smoothstep(-1.4, -0.6, r.w);
	float pillow = exp(-((u + 1.05) * (u + 1.05) / 0.18 + v * v / 0.55));
	float sides = exp(-((u - 0.2) * (u - 0.2) / 0.5 + (abs(v) - 1.15) * (abs(v) - 1.15) / 0.12));
	float hollow = exp(-((u - 1.5) * (u - 1.5) / 0.6 + v * v / 0.35));
	float hump = exp(-((u + 0.2) * (u + 0.2) + v * v) / 0.8);
	return hv * deep * (emerg * (pillow - 0.35 * sides - 0.55 * hollow) + (1.0 - emerg) * (0.6 * hump - 0.4 * hollow));
}

// Waves at world position p (current in world axes), rock bumps at grid position q (rocks are grid-local).
float surface_height(vec2 p, vec2 q, vec2 v_world_dir, float depth, float calm) {
	float h = wave_height(p, wave_time, v_world_dir, depth, calm);
	for (int i = 0; i < rock_count; i++) h += rock_bump(q, rocks[i], rock_flow[i]);
	return h;
}

void vertex() {
	vec3 w = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	vec2 q = VERTEX.xz;
	vec4 f = textureLod(flow_tex, field_uv(q), 0.0);
	float calm = textureLod(shape_tex, field_uv(q), 0.0).a;
	vec2 vw = to_world_dir(f.xy);
	const float e = 0.3;
	// World steps of e along x and z, and the same steps in grid coordinates.
	vec2 qx = vec2(frame_rot.x, frame_rot.y) * e, qz = vec2(-frame_rot.y, frame_rot.x) * e;
	float h = surface_height(w.xz, q, vw, f.z, calm);
	float hx = surface_height(w.xz + vec2(e, 0.0), q + qx, vw, f.z, calm);
	float hz = surface_height(w.xz + vec2(0.0, e), q + qz, vw, f.z, calm);
	v_wave_grad = vec2(hx - h, hz - h) / e * wave_strength;   // world-space slope
	VERTEX.y += h * wave_strength;
	v_world = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	v_local = VERTEX.xz;
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

// Living ripples: like flow_normal, but each phase is two crossing wave trains that also travel on their own at
// speed c (real ripples move relative to the water). Their interference keeps changing shape, so the surface
// lives instead of sliding downstream like one rigid sheet.
vec3 flow_normal_live(sampler2D tex, vec2 xz, vec2 flow, float scale, float cycle, float t, float jitter, float c) {
	float p0 = fract(t / cycle + jitter);
	float p1 = fract(t / cycle + jitter + 0.5);
	float w0 = 1.0 - abs(1.0 - 2.0 * p0);
	vec2 dA = vec2(0.80, 0.60) * c * t;
	vec2 dB = vec2(-0.71, 0.70) * c * 1.13 * t;
	vec2 b0 = xz - flow * p0 * cycle, b1 = xz - flow * p1 * cycle;
	vec3 a0 = unpack_n(texture(tex, (b0 - dA) / scale).rgb);
	vec3 c0 = unpack_n(texture(tex, (b0 - dB) / (scale * 0.87) + vec2(0.41, 0.13)).rgb);
	vec3 a1 = unpack_n(texture(tex, (b1 - dA) / scale + vec2(0.37, 0.61)).rgb);
	vec3 c1 = unpack_n(texture(tex, (b1 - dB) / (scale * 0.87) + vec2(0.77, 0.29)).rgb);
	vec3 n0 = normalize(vec3((a0.xy + c0.xy) * 0.72, a0.z * c0.z));
	vec3 n1 = normalize(vec3((a1.xy + c1.xy) * 0.72, a1.z * c1.z));
	return normalize(mix(n1, n0, w0));
}

void fragment() {
	// Only this segment's side of the cut lines (the neighbour draws the rest).
	if (clip_a_on > 0.5 && dot(v_world.xz - clip_a.xy, clip_a.zw) < 0.0) discard;
	if (clip_b_on > 0.5 && dot(v_world.xz - clip_b.xy, clip_b.zw) >= 0.0) discard;
	float t = TIME + time_offset;
	vec2 xz = v_world.xz;          // patterns: world
	vec2 q = v_local;              // field lookups: this segment's grid
	vec2 fuv = field_uv(q);
	vec4 fl = texture(flow_tex, fuv);
	vec4 sh = texture(shape_tex, fuv);
	vec2 flow_true = to_world_dir(fl.xy);
	float speed = length(flow_true);
	// Textures are carried by a smoothed current: around rocks and in eddies the real current turns within a few
	// metres, and dragging a texture through that winds it into whirlpool rings. Averaged over ~3 m, and the drag per
	// flow-map cycle is capped (adv_cap) so nothing gets stretched far beyond its own size.
	vec2 flow_grid = 0.4 * fl.xy;
	flow_grid += 0.15 * texture(flow_tex, field_uv(q + vec2(adv_smooth, 0.0))).xy;
	flow_grid += 0.15 * texture(flow_tex, field_uv(q - vec2(adv_smooth, 0.0))).xy;
	flow_grid += 0.15 * texture(flow_tex, field_uv(q + vec2(0.0, adv_smooth))).xy;
	flow_grid += 0.15 * texture(flow_tex, field_uv(q - vec2(0.0, adv_smooth))).xy;
	vec2 flow = to_world_dir(flow_grid);
	float fspeed = length(flow);
	if (fspeed > adv_cap) flow *= adv_cap / fspeed;
	float depth_field = fl.z;
	float shore = sh.g;                     // negative in water: distance to the shore (m)
	float froude = speed / sqrt(9.81 * max(depth_field, 0.08));
	float rough_water = smoothstep(0.35, 0.9, froude);   // riffles and rapids

	// ------------------------------------------------------------- surface normal
	// Phase jitter must be smooth: any fine detail here turns into marbled smears once multiplied by the flow.
	float jitter = textureLod(foam_tex, xz * 0.0035, 7.0).r * 0.8;
	vec3 nL = flow_normal_live(ripple_large, xz, flow, large_scale, flow_cycle, t, jitter, ripple_speed * 1.6);
	vec3 nS = flow_normal_live(ripple_small, xz, flow * 1.1, small_scale, flow_cycle * 0.7, t, jitter + 0.25, ripple_speed);
	// Wind ripples: not carried by the current, strongest on slow wide water.
	vec2 wuv = (xz + wind * t * 1.6) / (small_scale * 0.8);
	vec3 nW = unpack_n(texture(ripple_small, wuv + vec2(0.21, 0.73)).rgb);
	vec3 nW2 = unpack_n(texture(ripple_small, (xz + wind.yx * vec2(-1.0, 1.0) * t * 1.1) / (small_scale * 1.7)).rgb);
	float slow = 1.0 - smoothstep(0.08, 0.6, speed);
	// Not even everywhere: wind gusts come in patches (same pattern as the wind waves), backwaters are calm, and
	// the current's own turbulence is patchy too (slow drifting blotches).
	float g = gust(xz, wave_time);
	float calm = sh.a;
	float turb = flow_scalar(foam_tex, xz, flow, 45.0, 6.0, t, jitter);
	float wind_k = length(wind) * (0.35 + 0.65 * slow) * mix(0.12, 1.3, g) * (1.0 - 0.7 * calm);
	float flow_k = ripple_strength * (0.45 + 0.9 * smoothstep(0.1, 1.6, speed) + 1.2 * rough_water)
	             * mix(0.5, 1.2, turb) * mix(0.7, 1.08, g) * (1.0 - 0.5 * calm);
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
	// White water: in rapids (fast and rough) the surface is mostly churned foam, so the patches can't thin it
	// below most of the field's value and the cap rises.
	float white = smoothstep(2.0, 3.0, speed) * rough_water;
	coverage = clamp(max(coverage * mix(0.35, 1.3, patch), white * foam_field * mix(0.7, 0.95, patch)),
	                 0.0, mix(foam_max, 0.85, white));
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
