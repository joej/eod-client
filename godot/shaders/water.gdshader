// SPDX-License-Identifier: GPL-3.0-or-later
//
// Water surface for a zone's rivers, lakes and swamps (built by `mpk water` from SECTOR.DAT). The
// mesh's v coordinate runs down the river, so `flow` scrolls the texture along it. DAoC names a ripple
// overlay ("ripl") that isn't in the install, so a second, slower layer of the base texture moving
// across the flow stands in for it. Flow units and the tint are guesses to tune by eye.
shader_type spatial;
render_mode blend_mix, cull_disabled, depth_draw_never;

uniform sampler2D albedo_tex : source_color, repeat_enable, filter_linear_mipmap;
uniform float flow = 0.0;          // texture repeats per second along the river
uniform float opacity = 0.75;
uniform vec3 tint : source_color = vec3(0.85, 0.92, 1.0);

void fragment() {
	vec4 a = texture(albedo_tex, UV + vec2(0.0, TIME * flow));
	vec4 b = texture(albedo_tex, UV * 1.7 + vec2(TIME * 0.015, -TIME * flow * 0.6));
	ALBEDO = mix(a.rgb, b.rgb, 0.45) * tint;
	ALPHA = opacity;
	ROUGHNESS = 0.12;
	SPECULAR = 0.7;
	METALLIC = 0.0;
}
