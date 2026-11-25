// Rain streaks, lit by the scene: they take the light around them (set by TimeOfDay through Weather) instead of
// glowing white, so a night storm is dark grey streaks, not a starfield. Thin, fading at the ends and the edges,
// invisible right in front of your face (a drop 20 cm away is a blur, not a white bar) and far away.
shader_type spatial;
render_mode unshaded, cull_disabled, depth_draw_never, shadows_disabled;

uniform vec3 tint : source_color = vec3(0.6, 0.65, 0.7);
uniform float opacity = 0.22;

varying float vdist;

void vertex() {
	vdist = length((MODELVIEW_MATRIX * vec4(VERTEX, 1.0)).xyz);
}

void fragment() {
	float along = sin(UV.y * 3.14159);
	float across = 1.0 - abs(UV.x * 2.0 - 1.0);
	float near = smoothstep(0.9, 3.5, vdist);
	float far = 1.0 - smoothstep(22.0, 42.0, vdist);
	ALBEDO = tint;
	ALPHA = opacity * along * along * across * near * far;
}
