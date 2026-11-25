// Rice and wheat: thin blades in MultiMesh tufts, swaying in rolling waves of wind (stronger in storms), darker and
// glossier when wet. Lit softly from above (normal up), with some light through the blades.
shader_type spatial;
render_mode cull_disabled;

uniform vec3 base_color : source_color = vec3(0.20, 0.30, 0.10);
uniform vec3 tip_color : source_color = vec3(0.55, 0.65, 0.25);
uniform float height = 1.0;
uniform float wind = 0.3;
uniform float wet : hint_range(0.0, 1.0) = 0.0;

varying float vh;

void vertex() {
	vh = clamp(VERTEX.y / height, 0.0, 1.0);
	vec3 root = (MODEL_MATRIX * vec4(0.0, 0.0, 0.0, 1.0)).xyz;
	// Waves rolling across the field, plus a slow gust envelope and a little per-tuft flutter.
	float wave = sin(TIME * 1.25 - root.x * 0.16 - root.z * 0.07) * 0.65 + sin(TIME * 2.6 - root.x * 0.45 + root.z * 0.31) * 0.2;
	float gust = 0.45 + 0.55 * (0.5 + 0.5 * sin(TIME * 0.33 - root.x * 0.02));
	float flutter = sin(TIME * 7.0 + root.x * 13.0 + root.z * 7.0) * 0.08;
	vec3 push = vec3(1.0, 0.0, 0.45) * (wave * gust + flutter) * wind * vh * vh * height;
	push.y = -length(push) * 0.35;                     // bending also lowers the tip
	VERTEX += inverse(mat3(MODEL_MATRIX)) * push;
	NORMAL = vec3(0.0, 1.0, 0.0);
}

void fragment() {
	vec3 c = mix(base_color, tip_color, smoothstep(0.0, 1.0, vh)) * COLOR.rgb;
	ALBEDO = c * (1.0 - 0.25 * wet);
	ROUGHNESS = mix(0.85, 0.45, wet);
	BACKLIGHT = c * 0.35;
	AO = mix(0.55, 1.0, vh);
	AO_LIGHT_AFFECT = 0.4;
}
