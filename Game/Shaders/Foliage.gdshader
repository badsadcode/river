// NoorForm trees (TreeLibrary): bark and leaf cards with their packs' baseColor / normal / ORM maps, leaves and
// billboards cut out at alpha 0.5, a little wind, and per-tree distance hiding for billboards.
//
// Wind data (Noor Interchange, TEXCOORD_1 = Godot UV2): x = branch bend (0 on the trunk, rising along limbs),
// y = leaf flutter (0 at a card's anchor, 1 at its tip). Trunk sway grows with height.
// Billboards are drawn for every tree of a segment (static MultiMeshes); within hide_within of the LOD camera the
// real meshes draw instead (TreeLod), so the billboard collapses there. tree_camera is set by TreeLod every frame
// (the main camera, also during shadow passes, where CAMERA_POSITION_WORLD is the light's).
shader_type spatial;
render_mode cull_back, specular_schlick_ggx;

global uniform vec3 tree_camera;

uniform sampler2D albedo_tex : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform vec4 albedo_color : source_color = vec4(1.0);
uniform sampler2D normal_tex : hint_normal, filter_linear_mipmap, repeat_enable;
uniform sampler2D orm_tex : hint_default_white, filter_linear_mipmap, repeat_enable;
uniform bool has_normal = false;
uniform bool has_orm = false;
uniform bool cutout = false;
uniform float tree_height = 12.0;
uniform float hide_within = 0.0;
uniform float sway = 0.012;
uniform float flutter = 0.06;
uniform float translucency = 0.3;

void vertex() {
	vec3 origin = MODEL_MATRIX[3].xyz;
	if (hide_within > 0.0 && distance(origin.xz, tree_camera.xz) < hide_within) {
		VERTEX = vec3(0.0);   // a point: nothing rasterised
	} else {
		float phase = origin.x * 0.11 + origin.z * 0.07;
		float h = clamp(VERTEX.y / max(tree_height, 1.0), 0.0, 1.5);
		float gust = sin(TIME * 0.9 + phase) * 0.6 + sin(TIME * 2.1 + phase * 1.7) * 0.4;
		vec2 dir = vec2(0.8, 0.6);
		float bend = sway * tree_height * h * h * gust;
		VERTEX.xz += dir * bend;
		// Limbs and leaf cards (no-ops where the mesh has no wind data).
		float limb = UV2.x * 0.08 * sin(TIME * 1.7 + phase * 2.3 + VERTEX.y * 0.4);
		float leaf = UV2.y * flutter * sin(TIME * 6.0 + phase * 5.0 + dot(VERTEX, vec3(3.1, 1.7, 2.3)));
		VERTEX += vec3(limb * dir.x, leaf, limb * dir.y + leaf * 0.5);
	}
}

void fragment() {
	vec4 a = texture(albedo_tex, UV) * albedo_color;
	ALBEDO = a.rgb;
	if (cutout) {
		// Mipmapping averages coverage away: far leaves and billboards would thin out and go dark. Boost the alpha
		// with the mip level so a crown keeps its body at a distance.
		float lod = max(textureQueryLod(albedo_tex, UV).x, 0.0);
		ALPHA = a.a * (1.0 + lod * 0.3);
		if (hide_within > 0.0) {
			// Billboard cards: light them like a round crown (up and towards the viewer), not like flat cards edge-on
			// to the sun.
			vec3 up_v = normalize((VIEW_MATRIX * vec4(0.0, 1.0, 0.0, 0.0)).xyz);
			NORMAL = normalize(mix(vec3(0.0, 0.0, 1.0), up_v, 0.55));
		}
		ALPHA_SCISSOR_THRESHOLD = 0.5;
		BACKLIGHT = a.rgb * translucency;
	}
	if (has_normal) NORMAL_MAP = texture(normal_tex, UV).rgb;
	if (has_orm) {
		vec3 orm = texture(orm_tex, UV).rgb;
		AO = orm.r;
		ROUGHNESS = orm.g;
		METALLIC = orm.b;
	} else {
		ROUGHNESS = 0.85;
	}
}
