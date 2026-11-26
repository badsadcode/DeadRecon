// NoorForm tree bark: the same wind as the leaves (so trunk and crown move together), the bark texture tinted per
// tree, darker and glossier when wet. See leaves.gdshader for the wind channels.
shader_type spatial;

uniform sampler2D albedo_tex : source_color, filter_linear_mipmap_anisotropic;
uniform vec4 albedo : source_color = vec4(1.0);
uniform sampler2D normal_tex : hint_normal, filter_linear_mipmap_anisotropic;
uniform bool has_normal = false;
uniform float rough = 0.9;
uniform float wind = 0.35;
uniform vec2 wind_dir = vec2(0.8, 0.6);
uniform float wet : hint_range(0.0, 1.0) = 0.0;
uniform bool has_wind = true;

#include "res://game/World/tree_wind.gdshaderinc"

void vertex() {
	if (has_wind) {
		vec3 inst = MODEL_MATRIX[3].xyz;
		VERTEX += tree_wind(VERTEX, NORMAL, UV2, CUSTOM0.xy, inst, TIME, wind, wind_dir);
	}
}

void fragment() {
	vec3 c = texture(albedo_tex, UV).rgb * albedo.rgb * COLOR.rgb;
	ALBEDO = c * mix(1.0, 0.75, wet);
	ROUGHNESS = mix(rough, 0.28, wet);
	if (has_normal) NORMAL_MAP = texture(normal_tex, UV).rgb;
}
