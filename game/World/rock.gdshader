// Rocks: limestone, projected from the sides so nothing stretches; moss grows over the tops in patches; the base is
// darker (damp, in the shade of the ferns); rain darkens and glosses them. Instance colour varies the stone.
shader_type spatial;

uniform sampler2D rock_tex : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D moss_tex : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool has_textures = false;
uniform float wetness : hint_range(0.0, 1.0) = 0.0;
uniform float rock_metres = 1.8;

varying vec3 wpos;
varying vec3 wnorm;
varying float base;

float hash(vec3 p) { p = fract(p * 0.3183099 + 0.1); p *= 17.0; return fract(p.x * p.y * p.z * (p.x + p.y + p.z)); }
float noise(vec3 p) {
	vec3 i = floor(p), f = fract(p); f = f * f * (3.0 - 2.0 * f);
	return mix(mix(mix(hash(i), hash(i + vec3(1, 0, 0)), f.x), mix(hash(i + vec3(0, 1, 0)), hash(i + vec3(1, 1, 0)), f.x), f.y),
	           mix(mix(hash(i + vec3(0, 0, 1)), hash(i + vec3(1, 0, 1)), f.x), mix(hash(i + vec3(0, 1, 1)), hash(i + vec3(1, 1, 1)), f.x), f.y), f.z);
}

vec3 triplanar(sampler2D t, vec3 p, vec3 n) {
	vec3 w = pow(abs(n), vec3(4.0));
	w /= (w.x + w.y + w.z);
	return texture(t, p.zy).rgb * w.x + texture(t, p.xz).rgb * w.y + texture(t, p.xy).rgb * w.z;
}

void vertex() {
	wpos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	wnorm = normalize((MODEL_MATRIX * vec4(NORMAL, 0.0)).xyz);
	base = VERTEX.y;   // meshes are built around the origin, roughly -0.5 .. 0.5
}

void fragment() {
	vec3 n = normalize(wnorm);
	vec3 stone;
	if (has_textures) stone = triplanar(rock_tex, wpos / rock_metres, n) * 0.9;
	else stone = vec3(0.42, 0.41, 0.37) * (0.8 + 0.4 * noise(wpos * 3.0));
	// Desaturate the photo a touch and tint by instance colour (some warmer, some greyer).
	stone = mix(vec3(dot(stone, vec3(0.3, 0.59, 0.11))), stone, 0.45) * COLOR.rgb * vec3(1.0, 1.0, 1.04);
	// Streaks: water runs down the faces and leaves darker stains.
	float streak = noise(vec3(wpos.x * 6.0, wpos.y * 0.6, wpos.z * 6.0));
	stone *= mix(1.0, 0.82, smoothstep(0.55, 0.85, streak) * (1.0 - abs(n.y)));
	// Moss: on upward faces, in patches, more on big rocks low in the jungle (the shader can't know that: patches).
	float up = smoothstep(0.35, 0.85, n.y);
	float patches = noise(wpos * 1.3) * 0.65 + noise(wpos * 4.7) * 0.35;
	float moss = up * smoothstep(0.38, 0.62, patches);
	vec3 moss_c = has_textures ? texture(moss_tex, wpos.xz / 1.2).rgb * vec3(0.85, 1.0, 0.75) : vec3(0.18, 0.28, 0.1);
	vec3 albedo = mix(stone, moss_c, moss * 0.9);
	// Damp, shaded base.
	albedo *= mix(0.62, 1.0, smoothstep(-0.45, 0.05, base));
	// Rain.
	albedo *= 1.0 - 0.32 * wetness * (1.0 - moss * 0.5);
	ALBEDO = albedo;
	ROUGHNESS = mix(mix(0.88, 0.95, moss), 0.38, wetness * (1.0 - moss * 0.6));
	SPECULAR = 0.2 + 0.25 * wetness;
}
