// Snags (fallen trees in the river): vertex-coloured bark, darker where wet (baked into the colours), a little sheen
// on the wet part.
shader_type spatial;
render_mode cull_back, specular_schlick_ggx;

varying vec3 v_world;

void vertex() {
	v_world = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

void fragment() {
	// Bark grain along the trunk direction is unknown here: break the flat colour up with a fine world-space stripe.
	float grain = 0.88 + 0.12 * sin(v_world.x * 9.0 + v_world.z * 7.0 + sin(v_world.y * 13.0) * 2.0);
	ALBEDO = COLOR.rgb * grain;
	float wet = step(COLOR.r, 0.06);
	ROUGHNESS = mix(0.9, 0.45, wet);
	SPECULAR = mix(0.2, 0.4, wet);
}
