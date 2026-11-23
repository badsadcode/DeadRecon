// Full-screen scope view: black outside a circle, thin crosshair with thicker outer posts, soft edge vignette.
shader_type canvas_item;

uniform float aspect = 1.777;     // viewport width / height (set from code)
uniform float radius = 0.46;      // circle radius as a fraction of screen height

void fragment() {
	vec2 p = (UV - 0.5) * vec2(aspect, 1.0);
	float r = length(p);
	float outside = smoothstep(radius - 0.006, radius + 0.004, r);
	float vignette = smoothstep(radius * 0.55, radius, r) * 0.55;
	float thin = 0.0011, thick = 0.0045;
	float post = (abs(p.x) < thick && abs(p.y) > radius * 0.42) || (abs(p.y) < thick && abs(p.x) > radius * 0.42) ? 1.0 : 0.0;
	float hair = (abs(p.x) < thin || abs(p.y) < thin) ? 1.0 : 0.0;
	float a = max(outside, max(max(post, hair) * 0.9, vignette));
	COLOR = vec4(0.0, 0.0, 0.0, a);
}
