// Paddy water: shallow, still, muddy, mirror-ish. Rain turns it into a field of expanding ripple rings; without rain
// a faint breeze shiver. Driven by Villages (rain = Weather intensity).
shader_type spatial;
render_mode cull_disabled, depth_draw_always;

uniform float rain : hint_range(0.0, 1.0) = 0.0;
uniform vec3 tint : source_color = vec3(0.14, 0.15, 0.09);

varying vec3 wp;

vec2 hash2(vec2 p) {
	p = vec2(dot(p, vec2(127.1, 311.7)), dot(p, vec2(269.5, 183.3)));
	return fract(sin(p) * 43758.5453);
}

// Slope of the water surface from ripple rings in one layer of cells.
vec2 rings(vec2 p, float t, float cell, float seed) {
	vec2 g = p / cell, id = floor(g), f = fract(g);
	vec2 slope = vec2(0.0);
	for (int j = -1; j <= 1; j++)
	for (int i = -1; i <= 1; i++) {
		vec2 o = vec2(float(i), float(j));
		vec2 h = hash2(id + o + seed);
		float phase = fract(t * (0.9 + 0.4 * h.y) + h.x);
		vec2 d = f - (o + 0.15 + 0.7 * h);
		float r = length(d) + 1e-4;
		float ring = phase * 0.9;                    // radius grows (in cells)
		float x = (r - ring) * 14.0;
		float wave = sin(clamp(x, -3.14159, 3.14159) * 2.0) * step(abs(x), 3.14159);
		float fade = (1.0 - phase) * (1.0 - phase);
		slope += d / r * wave * fade;
	}
	return slope;
}

void vertex() {
	wp = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

void fragment() {
	vec2 p = wp.xz;
	vec2 slope = vec2(0.0);
	if (rain > 0.02) {
		slope += rings(p, TIME * 1.1, 0.55, 0.0) * rain;
		slope += rings(p, TIME * 1.3 + 0.5, 0.37, 17.0) * rain * smoothstep(0.3, 0.8, rain);
	}
	// Breeze shiver.
	slope += vec2(sin(p.x * 3.1 + TIME * 1.7 + sin(p.y * 2.3)), cos(p.y * 2.7 - TIME * 1.3 + sin(p.x * 1.9))) * 0.02;
	vec3 n = normalize(vec3(-slope.x * 0.35, 1.0, -slope.y * 0.35));
	NORMAL = normalize((VIEW_MATRIX * vec4(n, 0.0)).xyz);
	float fres = pow(1.0 - clamp(dot(NORMAL, VIEW), 0.0, 1.0), 4.0);
	ALBEDO = tint * (1.0 - 0.3 * rain);
	ROUGHNESS = 0.04 + 0.06 * rain;
	SPECULAR = 0.6;
	ALPHA = mix(0.82, 0.97, fres);
}
