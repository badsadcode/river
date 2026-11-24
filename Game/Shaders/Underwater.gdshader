// The underwater view (World/Underwater.cs): a full-screen quad in front of the camera while it is below the water
// surface. The picture wobbles gently, loses red light and sharpness, and fades into green-brown river water with
// distance (read from the depth buffer); darker the deeper you are and the darker the day.
shader_type spatial;
render_mode unshaded, cull_disabled, depth_test_disabled, depth_draw_never, blend_mix, shadows_disabled;

uniform sampler2D screen_tex : hint_screen_texture, filter_linear_mipmap;
uniform sampler2D depth_tex : hint_depth_texture, filter_nearest;
uniform vec3 water_color : source_color = vec3(0.13, 0.29, 0.26);
uniform float light = 1.0;          // daylight 0..1 (EnvironmentRig)
uniform float below = 0.5;          // camera depth under the surface (m)
uniform float clarity = 0.075;      // fog per metre of distance (about 13 m of sight)

void vertex() {
	POSITION = vec4(VERTEX.xy, 1.0, 1.0);   // covers the screen whatever the camera
}

void fragment() {
	vec2 uv = SCREEN_UV;
	uv += vec2(sin(uv.y * 28.0 + TIME * 1.9), cos(uv.x * 23.0 + TIME * 1.5)) * 0.0025;
	float d = texture(depth_tex, uv).r;
	vec4 v = INV_PROJECTION_MATRIX * vec4(uv * 2.0 - 1.0, d, 1.0);
	float dist = length(v.xyz / v.w);
	float fog = 1.0 - exp(-dist * clarity);
	vec3 c = textureLod(screen_tex, uv, 1.5 * smoothstep(2.0, 25.0, dist)).rgb;
	c *= vec3(0.7, 0.95, 0.9) * 1.25;                                  // red light is absorbed first
	vec3 w = water_color * light * exp(-below * 0.07);
	c = mix(c, w, fog);
	c *= 1.0 - 0.35 * smoothstep(0.35, 0.95, length(SCREEN_UV - 0.5) * 1.4);   // darker at the edges
	ALBEDO = c;
}
