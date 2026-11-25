// SPDX-License-Identifier: GPL-3.0-or-later
//
// Classic lighting: the world lit as the retail 1.130 client lights it (daoc-re RE/100 §8, Camelot Hills by
// day): fixed-function Direct3D, per vertex,
//   lit = clamp(emissive + vc x (ambient + sun x max(N.L, 0) + player x max(N.L, 0)), 0, 1)
// then multiplied with the texture in display (gamma) space; no cast shadows, no gamma ramp. vc is the vertex colour
// (it stands in for the material's ambient and diffuse, which are otherwise 1), emissive the material's (1/16 outdoors
// in retail), player a white point light the client puts at the player: diffuse 2, linear falloff 0.006 per unit, so
// 2 / (0.006 x distance), 1.0 at about 333 units. The ambient and sun colours (the sky file's colours times their amounts,
// which match retail's), the direction toward the sun and the player's position are global shader parameters
// (project.godot [shader_globals]), set by WorldView; ClassicLighting.cs turns it on.
//
// A shader includes this file once, after "#define CLASSIC_LIGHT" when Classic lighting is on (ClassicLighting.Header),
// puts CLASSIC_VERTEX(vc, emissive) in its vertex() and CLASSIC_FRAGMENT at the end of its fragment() (after ALBEDO is
// set). Without the define both are empty and the shader is lit by Godot as before.
//
// How it maps onto Godot: Godot draws ALBEDO x (the lights it adds) + EMISSION. The texture x the vertex light goes into
// EMISSION (as linear: Godot's ALBEDO is already linear, and lin(tex) x lin(light) = lin(tex x light) for the power
// curve); Godot's ambient, sky reflection and specular are off; the custom light() leaves out the sun (already in the
// vertex light) and adds other lights (street lamps, the torch) as plain diffuse light. Their sum is linear, not
// retail's (inferred: how retail mixes those with the rest isn't traced). The player light's range isn't traced: ours
// has none.

#ifdef CLASSIC_LIGHT

// No Godot ambient, sky reflection or specular: the vertex light is all of it (a reflection of the sky at low angles
// turned the Atlantis ground pink without these).
render_mode ambient_light_disabled, specular_disabled;

global uniform vec3 classic_ambient;
global uniform vec3 classic_sun;
global uniform vec3 classic_sun_dir;
global uniform vec3 classic_player;
global uniform float classic_units_per_metre;

varying vec3 classic_lit;

// Gold or silver leaf for the Manuscript look (ManuscriptLook.Leaf: 1 gold, 2 silver), passed on in the roughness the
// scene writes for every pixel: Classic lighting has no use for roughness, and the look's screen pass reads it back.
uniform int classic_leaf = 0;

vec3 classic_linear(vec3 c) {
	return mix(c / 12.92, pow((c + vec3(0.055)) / 1.055, vec3(2.4)), step(vec3(0.04045), c));
}

vec3 classic_vertex_light(vec3 normal, vec3 vertex, mat4 model, vec3 vc, vec3 emissive) {
	vec3 n = normalize((model * vec4(normal, 0.0)).xyz);
	vec3 to_player = classic_player - (model * vec4(vertex, 1.0)).xyz;
	float metres = max(length(to_player), 0.001);
	float player = 2.0 / max(0.006 * metres * classic_units_per_metre, 0.001) * max(dot(n, to_player / metres), 0.0);
	vec3 sum = classic_ambient + classic_sun * max(dot(n, classic_sun_dir), 0.0) + vec3(player);
	return clamp(emissive + vc * sum, vec3(0.0), vec3(1.0));
}

#define CLASSIC_VERTEX(vc, emissive) classic_lit = classic_vertex_light(NORMAL, VERTEX, MODEL_MATRIX, vc, emissive);
#define CLASSIC_FRAGMENT EMISSION += ALBEDO * classic_linear(classic_lit); AO = 0.0; AO_LIGHT_AFFECT = 0.0; SPECULAR = 0.0; ROUGHNESS = classic_leaf == 1 ? 0.12 : classic_leaf == 2 ? 0.36 : 1.0;

void light() {
	if (!LIGHT_IS_DIRECTIONAL)
		DIFFUSE_LIGHT += clamp(dot(NORMAL, LIGHT), 0.0, 1.0) * ATTENUATION * LIGHT_COLOR / PI;
}

#else

#define CLASSIC_VERTEX(vc, emissive)
#define CLASSIC_FRAGMENT

#endif
