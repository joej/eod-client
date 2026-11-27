// SPDX-License-Identifier: GPL-3.0-or-later
//
// A weapon trail (WorldView.Trails): the style's colour (vertex colour) times its stylemap texture, alpha-blended at
// 0.99. The textures have no alpha; their rows run from bright (v 0, the newest edge) to black (v 1 and past, clamped),
// and that is how a trail fades in, trails off and fades out. Ours (inferred): the texture's brightness is used as its
// alpha, so the dark rows let the world show through; an untextured trail (no stylemap row) is the colour alone.
shader_type spatial;
render_mode unshaded, cull_disabled, depth_draw_never, blend_mix, shadows_disabled;

uniform sampler2D trail_texture : source_color, repeat_disable, filter_linear_mipmap;
uniform bool textured = true;

void fragment() {
	vec3 c = vec3(1.0);
	float a = 1.0;
	if (textured) {
		c = texture(trail_texture, UV).rgb;
		a = max(c.r, max(c.g, c.b));
		c = c / max(a, 0.001);
	}
	ALBEDO = COLOR.rgb * c;
	ALPHA = 0.99 * a;
}
