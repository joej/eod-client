// SPDX-License-Identifier: GPL-3.0-or-later
//
// DAoC's sky dome from zones/sky/sky_<name>.dat (via `mpk sky` → sky.csv): a canopy gradient from the
// zenith through four bands to the horizon, separately for the east and west halves of the sky (dawn
// glows east, dusk west), two scrolling cloud layers tinted and faded by their own banded gradient, and
// the sun disk at the directional light. The band elevations and the cloud projection scale are not
// confirmed from the client: the values below are guesses to tune by eye against retail screenshots.
shader_type sky;

uniform vec4 zenith : source_color;
uniform vec4 east1 : source_color;
uniform vec4 east2 : source_color;
uniform vec4 east3 : source_color;
uniform vec4 east4 : source_color;
uniform vec4 west1 : source_color;
uniform vec4 west2 : source_color;
uniform vec4 west3 : source_color;
uniform vec4 west4 : source_color;
uniform vec4 nadir : source_color;

uniform vec4 c_zenith : source_color;
uniform vec4 c_east1 : source_color;
uniform vec4 c_east2 : source_color;
uniform vec4 c_east3 : source_color;
uniform vec4 c_east4 : source_color;
uniform vec4 c_west1 : source_color;
uniform vec4 c_west2 : source_color;
uniform vec4 c_west3 : source_color;
uniform vec4 c_west4 : source_color;

uniform sampler2D cloud_top : source_color, repeat_enable, filter_linear_mipmap;
uniform sampler2D cloud_bottom : source_color, repeat_enable, filter_linear_mipmap;
uniform float top_tile = 5.0;
uniform float top_speed = 3.0;      // as in the file; the client multiplies by 0.001
uniform float bottom_tile = 4.0;
uniform float bottom_speed = 5.0;
uniform bool has_clouds = false;

uniform sampler2D sun_disk : source_color, filter_linear;
uniform bool has_sun = false;
uniform vec4 sun_color : source_color = vec4(1.0);
uniform float sun_size = 0.035;     // angular radius, radians (guess)

// Band elevations, degrees: zenith 90, then bands 1..4 down to the horizon (guess).
const float B1 = 55.0;
const float B2 = 25.0;
const float B3 = 8.0;
const float B4 = 0.0;

vec4 band(float el, vec4 z, vec4 b1, vec4 b2, vec4 b3, vec4 b4) {
	if (el >= B1) return mix(b1, z, (el - B1) / (90.0 - B1));
	if (el >= B2) return mix(b2, b1, (el - B2) / (B1 - B2));
	if (el >= B3) return mix(b3, b2, (el - B3) / (B2 - B3));
	if (el >= B4) return mix(b4, b3, (el - B4) / (B3 - B4));
	return b4;
}

void sky() {
	vec3 d = normalize(EYEDIR);
	float el = degrees(asin(clamp(d.y, -1.0, 1.0)));
	// East is +X in our world (DAoC x runs east). 1 = due east, 0 = due west.
	float e = 0.5 + 0.5 * (length(d.xz) > 0.0001 ? normalize(d.xz).x : 0.0);
	vec4 east = band(el, zenith, east1, east2, east3, east4);
	vec4 west = band(el, zenith, west1, west2, west3, west4);
	vec3 col = mix(west, east, e).rgb;
	if (el < 0.0)
		col = mix(col, nadir.rgb, clamp(-el / 20.0, 0.0, 1.0) * nadir.a);

	if (has_clouds && d.y > 0.0) {
		// Project onto a flat cloud plane above the viewer; fade with the cloud gradient's alpha.
		vec2 p = d.xz / max(d.y, 0.06);
		vec4 cg = mix(band(el, c_zenith, c_west1, c_west2, c_west3, c_west4),
		              band(el, c_zenith, c_east1, c_east2, c_east3, c_east4), e);
		vec4 bot = texture(cloud_bottom, p * bottom_tile * 0.05 + vec2(TIME * bottom_speed * 0.001, 0.0));
		vec4 top = texture(cloud_top, p * top_tile * 0.05 + vec2(TIME * top_speed * 0.001, TIME * top_speed * 0.0004));
		col = mix(col, bot.rgb * cg.rgb, bot.a * cg.a);
		col = mix(col, top.rgb * cg.rgb, top.a * cg.a);
	}

	if (has_sun) {
		vec3 s = normalize(LIGHT0_DIRECTION);
		float a = acos(clamp(dot(d, s), -1.0, 1.0));
		if (a < sun_size) {
			// Sample the disk texture across the sun's angular extent (white disk on black: additive).
			vec3 up = abs(s.y) < 0.99 ? vec3(0.0, 1.0, 0.0) : vec3(1.0, 0.0, 0.0);
			vec3 r = normalize(cross(up, s));
			vec3 u = cross(s, r);
			vec2 uv = vec2(dot(d, r), dot(d, u)) / sin(sun_size) * 0.5 + 0.5;
			col += texture(sun_disk, uv).rgb * sun_color.rgb * sun_color.a;
		}
	}
	COLOR = col;
}
