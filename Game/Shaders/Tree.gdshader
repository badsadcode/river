// Trees (MultiMesh): vertex-coloured low-poly crowns, per-instance tint, gentle wind sway.
shader_type spatial;
render_mode cull_disabled, specular_schlick_ggx;

uniform float sway = 0.035;
uniform float translucency = 0.35;

varying vec3 v_tint;

void vertex() {
	vec3 base = MODEL_MATRIX[3].xyz;
	float phase = base.x * 0.13 + base.z * 0.07;
	float h = max(VERTEX.y, 0.0);
	float s = sin(TIME * 1.1 + phase) * 0.6 + sin(TIME * 2.3 + phase * 1.7) * 0.4;
	VERTEX.x += s * sway * h * h * 0.08;
	VERTEX.z += s * sway * h * h * 0.05;
	v_tint = INSTANCE_CUSTOM.rgb;
}

void fragment() {
	vec3 c = COLOR.rgb * v_tint;
	ALBEDO = c;
	ROUGHNESS = 0.88;
	SPECULAR = 0.2;
	BACKLIGHT = c * translucency * COLOR.a;
}
