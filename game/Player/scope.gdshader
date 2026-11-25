// Aiming overlay, by optic:
//   reticle 0 (scopes): black outside a circle, thin crosshair with thicker outer posts, soft edge vignette.
//   reticle 1 (red dot): just a small glowing red dot.
//   reticle 2 (holographic): a red ring with a dot in the middle.
shader_type canvas_item;

uniform float aspect = 1.777;     // viewport width / height (set from code)
uniform float radius = 0.46;      // circle radius as a fraction of screen height
uniform int reticle = 0;

void fragment() {
	vec2 p = (UV - 0.5) * vec2(aspect, 1.0);
	float r = length(p);
	if (reticle == 0) {
		float outside = smoothstep(radius - 0.006, radius + 0.004, r);
		float vignette = smoothstep(radius * 0.55, radius, r) * 0.55;
		float thin = 0.0011, thick = 0.0045;
		float post = (abs(p.x) < thick && abs(p.y) > radius * 0.42) || (abs(p.y) < thick && abs(p.x) > radius * 0.42) ? 1.0 : 0.0;
		float hair = (abs(p.x) < thin || abs(p.y) < thin) ? 1.0 : 0.0;
		float a = max(outside, max(max(post, hair) * 0.9, vignette));
		COLOR = vec4(0.0, 0.0, 0.0, a);
	} else {
		vec3 red = vec3(1.0, 0.12, 0.08);
		float dot_ = 1.0 - smoothstep(0.0026, 0.0042, r);
		float glow = exp(-r * 260.0) * 0.5;
		float ring = reticle == 2 ? (1.0 - smoothstep(0.0009, 0.0022, abs(r - 0.032))) * 0.9 : 0.0;
		float a = clamp(dot_ + glow + ring, 0.0, 1.0);
		COLOR = vec4(red * 1.4, a);
	}
}
