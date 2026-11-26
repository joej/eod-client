// SPDX-License-Identifier: GPL-3.0-or-later
//
// Lava surface for a zone's LAVA bodies (built by `mpk water` from SECTOR.DAT, like water). The retail client draws
// lava opaque, not with water's alpha, with a material that glows orange of its own: emissive (1.0, 0.518, 0), diffuse
// white, ambient (0.218, 0.119, 0.064). So it stays bright at night. Its second texture layer moves slowly across the
// first, as the water shader's does; the speeds are ours.
shader_type spatial;
render_mode cull_disabled;

uniform sampler2D albedo_tex : source_color, repeat_enable, filter_linear_mipmap;
uniform float flow = 0.0;          // texture repeats per second along the body
uniform vec3 glow : source_color = vec3(1.0, 0.518, 0.0);

void fragment() {
	vec4 a = texture(albedo_tex, UV + vec2(0.0, TIME * flow));
	vec4 b = texture(albedo_tex, UV * 1.7 + vec2(TIME * 0.01, -TIME * 0.006));
	vec3 col = mix(a.rgb, b.rgb, 0.4);
	ALBEDO = col;
	EMISSION = col * glow;
	ROUGHNESS = 0.8;
	SPECULAR = 0.0;
	METALLIC = 0.0;
}
