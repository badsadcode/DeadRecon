// NoorForm tree leaves: wind (Noor Interchange 'wind' channels) and light through the leaves.
//   UV2.x  bend     how far this part of a branch bows in the wind
//   UV2.y  flutter  how freely the leaf tip shivers
//   CUSTOM0.x phase    so neighbouring branches don't move in step
//   CUSTOM0.y sway     whole-tree sway, growing with height
// (glTF TEXCOORD_1 is Godot's UV2, TEXCOORD_2 its CUSTOM0.) Per tree: the instance colour tints, the instance
// position offsets the timing.
shader_type spatial;
render_mode cull_disabled, depth_draw_opaque;

uniform sampler2D albedo_tex : source_color, filter_linear_mipmap_anisotropic;
uniform vec4 albedo : source_color = vec4(1.0);
uniform float alpha_cut = 0.45;
uniform float wind = 0.35;          // 0 calm .. 1 storm (Weather)
uniform vec2 wind_dir = vec2(0.8, 0.6);
uniform float wet : hint_range(0.0, 1.0) = 0.0;
uniform float translucency = 0.55;  // sunlight through the leaves, from behind
uniform vec3 transmit_tint : source_color = vec3(0.75, 0.95, 0.35);
uniform bool has_wind = true;

#include "res://game/World/tree_wind.gdshaderinc"

void vertex() {
	if (has_wind) {
		vec3 inst = MODEL_MATRIX[3].xyz;
		VERTEX += tree_wind(VERTEX, NORMAL, UV2, CUSTOM0.xy, inst, TIME, wind, wind_dir);
	}
}

void fragment() {
	vec4 tex = texture(albedo_tex, UV);
	vec3 c = tex.rgb * albedo.rgb * COLOR.rgb;
	c *= mix(1.0, 0.72, wet);
	ALBEDO = c;
	ALPHA = tex.a * albedo.a;
	ALPHA_SCISSOR_THRESHOLD = alpha_cut;
	ROUGHNESS = mix(0.75, 0.3, wet);
	SPECULAR = 0.35;
	// Leaves are thin: light from behind comes through, yellow-green (the engine's backlight term).
	BACKLIGHT = c * transmit_tint * translucency * 2.2;
	// Seen from behind a card faces away: turn its normal round so both sides are lit as leaf fronts.
	if (!FRONT_FACING) NORMAL = -NORMAL;
}
