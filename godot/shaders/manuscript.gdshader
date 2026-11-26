// SPDX-License-Identifier: GPL-3.0-or-later
//
// Manuscript look: the world drawn as an illuminated manuscript page. One full-screen pass over the opaque scene
// (a quad in front of the camera, WorldView.Manuscript), in three steps:
//   0. Flatten: a Kuwahara filter (the mean of whichever of four neighbouring squares varies least) turns the low-res
//      textures into flat areas with clean edges, as paint laid in strokes, before the pigments are matched.
//   1. Pigment: each pixel is repainted with the nearest of a small set of historical pigments (lapis, azurite,
//      vermilion, red lead, verdigris, green earth, ochres, orpiment, umber, folium, lead white, carbon black), each in a
//      light, pure and dark tone. Matched in Oklab, so "nearest" is by eye, not by RGB distance. The palette comes from
//      ManuscriptLook.cs.
//   2. Ink: iron-gall outlines where the depth jumps (silhouettes) or the surface turns (creases), found from the
//      scene's depth and normals. The depth test uses the raw (reversed, 1/z-like) depth, which changes linearly across
//      a flat surface on screen, so the ground seen at a grazing angle doesn't fill with lines; the lines thin out with
//      distance and their weight wavers slightly, as a pen's does.
//   Leaf: surfaces the scene marks as gold or silver (ManuscriptLook.Leaf, passed in the roughness channel by the
//      Classic lighting shaders) are laid with flat leaf instead of a pigment, with a sheen and glint that follow the view.
//   3. Vellum: a parchment colour, grain and a few faint stains multiplied over the page, darker toward its edges.
//      Fixed on the screen so it doesn't swim when the camera turns.
// Only the opaque scene is read: name plates, water, spell effects and the interface are drawn after this pass, so
// they stay readable (the quad's render priority is the lowest).

shader_type spatial;
render_mode unshaded, depth_test_disabled, depth_draw_never, cull_disabled, skip_vertex_transform, fog_disabled, shadows_disabled;

uniform sampler2D screen_tex : hint_screen_texture, filter_linear, repeat_disable;
uniform sampler2D depth_tex : hint_depth_texture, filter_nearest, repeat_disable;
uniform sampler2D normal_tex : hint_normal_roughness_texture, filter_nearest, repeat_disable;

const int PIGMENTS = 64;
const int TONES = 4;                    // per pigment: light, pure, dark, deep (ManuscriptLook.Palette)
uniform vec3 pigment_lab[PIGMENTS];     // Oklab
uniform vec3 pigment_rgb[PIGMENTS];     // linear RGB
uniform int pigment_count = 0;
uniform float pigment_strength : hint_range(0.0, 1.0) = 0.85;
uniform int neutral_count = 5;          // the palette's first entries: lead white, carbon black, the grey washes
uniform float neutral_chroma = 0.025;   // Oklab chroma below which a colour is matched among those
uniform float black_below = 0.14;       // Oklab lightness (after the lift) below which it's carbon black
uniform float tone_lift = 0.9;         // < 1: darks are lifted before matching, so shade reads as darker paint, not black

// Leaf: gold and silver laid on the surfaces the scene marks (ManuscriptLook.Leaf, in the roughness channel), shaded
// by the light the surface had, with a soft sheen and a glint that move with the view.
uniform vec3 gold_dark : source_color = vec3(0.42, 0.28, 0.09);
uniform vec3 gold_mid : source_color = vec3(0.80, 0.60, 0.24);
uniform vec3 gold_light : source_color = vec3(0.99, 0.90, 0.58);
uniform vec3 silver_dark : source_color = vec3(0.24, 0.25, 0.29);
uniform vec3 silver_mid : source_color = vec3(0.60, 0.62, 0.67);
uniform vec3 silver_light : source_color = vec3(0.94, 0.95, 0.97);
uniform bool leaf_enabled = true;

uniform float flatten_radius = 4.0;     // pixels at 1080 lines; 0 = off

uniform vec3 ink_colour : source_color = vec3(0.13, 0.09, 0.07);
uniform float ink_width = 2.0;          // pixels at 1080 lines
uniform float ink_depth = 0.12;         // relative change of the depth's slope that makes a line
uniform float ink_crease = 0.55;        // 1 - cos(angle) between normals that makes a line
uniform float ink_fade_far = 220.0;     // metres: lines are gone by here

uniform vec3 vellum_colour : source_color = vec3(0.95, 0.89, 0.75);
uniform float vellum_strength : hint_range(0.0, 1.0) = 0.55;
uniform float vellum_edge : hint_range(0.0, 1.0) = 0.35;

void vertex() {
	POSITION = vec4(VERTEX.xy * 2.0, 1.0, 1.0);
}

float hash(vec2 p) {
	p = fract(p * vec2(123.34, 456.21));
	p += dot(p, p + 45.32);
	return fract(p.x * p.y);
}

float value_noise(vec2 p) {
	vec2 i = floor(p), f = fract(p);
	vec2 u = f * f * (3.0 - 2.0 * f);
	return mix(mix(hash(i), hash(i + vec2(1, 0)), u.x), mix(hash(i + vec2(0, 1)), hash(i + vec2(1, 1)), u.x), u.y);
}

float fbm(vec2 p) {
	float v = 0.0, a = 0.5;
	for (int i = 0; i < 5; i++) {
		v += a * value_noise(p);
		p = p * 2.03 + vec2(17.1, 9.7);
		a *= 0.5;
	}
	return v;
}

vec3 to_oklab(vec3 c) {
	float l = 0.4122214708 * c.r + 0.5363325363 * c.g + 0.0514459929 * c.b;
	float m = 0.2119034982 * c.r + 0.6806995451 * c.g + 0.1073969566 * c.b;
	float s = 0.0883024619 * c.r + 0.2817188376 * c.g + 0.6299787005 * c.b;
	l = pow(max(l, 0.0), 1.0 / 3.0);
	m = pow(max(m, 0.0), 1.0 / 3.0);
	s = pow(max(s, 0.0), 1.0 / 3.0);
	return vec3(0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s,
	            1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s,
	            0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s);
}

// Two steps, as a painter picks a pot and then how much white or black to mix in: the pigment whose tones come nearest in
// colour (hue and strength, not lightness), then its light, pure, dark or deep tone by lightness. Matching every tone at
// once let dull dark greens (a forest in shade) land on umber and black, and a forest zone went nearly black. Colours with
// almost no hue take the whites, greys and black; black is otherwise kept for the very darkest.
vec3 pigment(vec3 linear) {
	vec3 lab = to_oklab(linear);
	lab.x = pow(max(lab.x, 0.0), tone_lift);
	int first = 0, last = neutral_count;
	if (length(lab.yz) >= neutral_chroma) {
		float best = 1e9;
		for (int f = neutral_count; f + TONES <= PIGMENTS; f += TONES) {
			if (f + TONES > pigment_count)
				break;
			for (int t = 0; t < TONES; t++) {
				vec2 d = lab.yz - pigment_lab[f + t].yz;
				float e = dot(d, d);
				if (e < best) {
					best = e;
					first = f;
				}
			}
		}
		last = first + TONES;
	}
	float nearest = 1e9;
	vec3 colour = linear;
	for (int i = 0; i < PIGMENTS; i++) {
		if (i >= pigment_count)
			break;
		if (i < first || i >= last)
			continue;
		float e = abs(lab.x - pigment_lab[i].x);
		if (e < nearest) {
			nearest = e;
			colour = pigment_rgb[i];
		}
	}
	if (lab.x < black_below)
		colour = pigment_rgb[1];
	return mix(linear, colour, pigment_strength);
}

vec3 flatten(vec2 uv, vec2 texel) {
	const int R = 4;
	vec2 step_ = texel * flatten_radius / float(R);
	vec3 mean[4];
	vec3 sq[4];
	for (int q = 0; q < 4; q++) {
		mean[q] = vec3(0.0);
		sq[q] = vec3(0.0);
	}
	for (int j = 0; j <= R; j++) {
		for (int i = 0; i <= R; i++) {
			float x = float(i), y = float(j);
			vec3 c;
			c = texture(screen_tex, uv + vec2(-x, -y) * step_).rgb; mean[0] += c; sq[0] += c * c;
			c = texture(screen_tex, uv + vec2(x, -y) * step_).rgb;  mean[1] += c; sq[1] += c * c;
			c = texture(screen_tex, uv + vec2(-x, y) * step_).rgb;  mean[2] += c; sq[2] += c * c;
			c = texture(screen_tex, uv + vec2(x, y) * step_).rgb;   mean[3] += c; sq[3] += c * c;
		}
	}
	float n = float((R + 1) * (R + 1));
	float best = 1e9;
	vec3 colour = vec3(0.0);
	for (int q = 0; q < 4; q++) {
		vec3 m = mean[q] / n;
		vec3 v = abs(sq[q] / n - m * m);
		float var = v.r + v.g + v.b;
		if (var < best) {
			best = var;
			colour = m;
		}
	}
	return colour;
}

float raw_depth(vec2 uv) {
	return texture(depth_tex, uv).r;
}

float metres(vec2 uv, mat4 inv_projection) {
	vec4 v = inv_projection * vec4(uv * 2.0 - 1.0, raw_depth(uv), 1.0);
	return -v.z / v.w;
}

// The leaf mark in the roughness channel (read back as written: 0.12 gold, 0.36 silver, 1.0 none).
int leaf_at(vec2 uv) {
	float r = texture(normal_tex, uv).a;
	return abs(r - 0.12) < 0.06 ? 1 : abs(r - 0.36) < 0.06 ? 2 : 0;
}

vec3 leaf(int kind, vec3 lit, vec3 n) {
	float l = to_oklab(lit).x;
	vec3 dark = kind == 1 ? gold_dark : silver_dark;
	vec3 mid = kind == 1 ? gold_mid : silver_mid;
	vec3 light = kind == 1 ? gold_light : silver_light;
	vec3 c = mix(dark, mid, smoothstep(0.15, 0.55, l));
	// A light up and to the left of the viewer, in view space: the sheen and glint follow the surface as the view turns.
	vec3 h = normalize(vec3(-0.35, 0.55, 0.75));
	float facing = max(dot(n, h), 0.0);
	float sheen = pow(facing, 3.0) * 0.45;
	float glint = smoothstep(0.93, 0.985, facing);
	return mix(c, light, clamp(sheen + glint, 0.0, 1.0));
}

vec3 view_normal(vec2 uv) {
	return normalize(texture(normal_tex, uv).xyz * 2.0 - 1.0);
}

float ink(vec2 uv, vec2 texel, mat4 inv_projection) {
	float centre = raw_depth(uv);
	if (centre <= 0.0)   // the sky: reversed depth, nothing drawn
		return 0.0;
	float dist = metres(uv, inv_projection);
	// The pen's weight wavers along the line (fixed on screen) and thins with distance.
	float waver = 0.75 + 0.5 * value_noise(uv * vec2(90.0, 60.0));
	float near = 1.0 - smoothstep(ink_fade_far * 0.35, ink_fade_far, dist);
	vec2 step_ = texel * ink_width * waver * max(near, 0.5);
	vec3 n = view_normal(uv);
	float line = 0.0;
	// Two axes and both diagonals: depth's second difference (a silhouette or a fold), normals' turn (a crease).
	vec2 dirs[4] = vec2[](vec2(1, 0), vec2(0, 1), vec2(1, 1) * 0.7071, vec2(1, -1) * 0.7071);
	for (int i = 0; i < 4; i++) {
		vec2 o = dirs[i] * step_;
		float a = raw_depth(uv + o), b = raw_depth(uv - o);
		float bend = abs(a + b - 2.0 * centre) / max(centre, 1e-6);
		line = max(line, smoothstep(ink_depth, ink_depth * 2.0, bend));
		// Against the sky: a silhouette.
		if (a <= 0.0 || b <= 0.0)
			line = 1.0;
		float turn = 1.0 - min(dot(n, view_normal(uv + o)), dot(n, view_normal(uv - o)));
		line = max(line, smoothstep(ink_crease, ink_crease * 1.6, turn) * 0.85);
	}
	return line * near;
}

float vellum(vec2 uv, float aspect) {
	vec2 p = vec2(uv.x * aspect, uv.y);
	float grain = fbm(p * 220.0) * 0.5 + fbm(p * 40.0) * 0.5;               // fibre and mottling
	float stains = smoothstep(0.62, 0.8, fbm(p * 3.0 + vec2(3.7, 1.3)));     // a few large, faint stains
	vec2 c = uv * 2.0 - 1.0;
	float edge = smoothstep(0.55, 1.45, length(c * vec2(1.0, 0.85)));         // darker, more worn toward the edges
	return 1.0 - 0.18 * (1.0 - grain) - 0.12 * stains - vellum_edge * edge;
}

void fragment() {
	vec2 uv = SCREEN_UV;
	vec2 size = vec2(textureSize(screen_tex, 0));
	vec2 texel = max(size.y / 1080.0, 1.0) / size;   // ink width is set for 1080 lines
	vec3 scene = flatten_radius > 0.0 ? flatten(uv, texel) : texture(screen_tex, uv).rgb;
	vec3 col = pigment(scene);
	int kind = leaf_enabled && raw_depth(uv) > 0.0 ? leaf_at(uv) : 0;
	if (kind != 0)
		col = leaf(kind, scene, view_normal(uv));
	col = mix(col, ink_colour, ink(uv, texel, INV_PROJECTION_MATRIX));
	float v = vellum(uv, size.x / size.y);
	// Leaf sits on the page rather than soaking into it: the vellum shows through it only a little.
	col *= mix(vec3(1.0), vellum_colour * v, vellum_strength * (kind != 0 ? 0.3 : 1.0));
	ALBEDO = col;
}
