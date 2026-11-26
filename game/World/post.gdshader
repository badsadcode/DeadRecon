// The final look, on a quad in front of the camera (drawn first among transparent things, so rain and smoke stay on
// top, ungraded). Works on the linear HDR picture before tonemapping:
//   1. haze glow: the air between you and far things lights up toward the sun (aerial in-scattering),
//   2. god rays: light streaming from the sun through gaps in the canopy (screen-space, from the depth buffer),
//   3. grading: split toning (cool shadows / warm highlights), saturation, contrast, vignette, a little grain.
// TimeOfDay sets the uniforms through the day.
shader_type spatial;
render_mode unshaded, fog_disabled, depth_draw_never, depth_test_disabled, cull_disabled, shadows_disabled;

uniform sampler2D screen_tex : hint_screen_texture, filter_linear_mipmap, repeat_disable;
uniform sampler2D depth_tex : hint_depth_texture, filter_nearest, repeat_disable;

uniform vec3 sun_dir = vec3(0.3, 0.6, -0.4);    // world space, toward the sun
uniform vec3 sun_color : source_color = vec3(1.0, 0.9, 0.7);
uniform float haze = 0.4;
uniform float rays = 0.6;
uniform vec3 shadow_tint = vec3(0.94, 1.0, 1.06);
uniform vec3 highlight_tint = vec3(1.05, 1.0, 0.93);
uniform float saturation = 1.08;
uniform float contrast = 1.06;
uniform float vignette = 0.2;
uniform float grain = 0.02;
uniform float exposure = 1.18;   // grading darkens a little; this gives it back
uniform float lift = 0.012;      // raises the deepest shadows so the jungle floor reads

void vertex() {
	POSITION = vec4(VERTEX.xy, 1.0, 1.0);
}

float hash(vec2 p) { vec3 p3 = fract(vec3(p.xyx) * 0.1031); p3 += dot(p3, p3.yzx + 33.33); return fract((p3.x + p3.y) * p3.z); }

bool is_sky(vec2 uv) { return texture(depth_tex, uv).r < 0.000001; }   // reversed Z: the far plane is 0

void fragment() {
	vec2 uv = SCREEN_UV;
	vec3 col = textureLod(screen_tex, uv, 0.0).rgb;
	float depth = texture(depth_tex, uv).r;
	vec4 view = INV_PROJECTION_MATRIX * vec4(uv * 2.0 - 1.0, depth, 1.0);
	view.xyz /= view.w;
	float dist = depth < 0.000001 ? 100000.0 : length(view.xyz);
	vec3 wdir = normalize((INV_VIEW_MATRIX * vec4(view.xyz, 0.0)).xyz);
	vec3 sd = normalize(sun_dir);

	// 1. Haze glow toward the sun, growing with distance.
	float mu = max(dot(wdir, sd), 0.0);
	float thick = 1.0 - exp(-dist * 0.0035);
	col += sun_color * (pow(mu, 10.0) * 0.9 + pow(mu, 3.0) * 0.18) * thick * haze;

	// 2. God rays: march from this pixel toward the sun on screen, counting how much sky shows through.
	vec3 vs = (VIEW_MATRIX * vec4(sd, 0.0)).xyz;
	if (rays > 0.001 && vs.z < 0.0) {
		vec4 clip = PROJECTION_MATRIX * vec4(vs, 1.0);
		vec2 sun_uv = clip.xy / clip.w * 0.5 + 0.5;
		vec2 to_sun = sun_uv - uv;
		float on_screen = 1.0 - smoothstep(0.6, 1.4, length(sun_uv - 0.5));
		float facing = smoothstep(0.1, 0.6, -vs.z);
		if (on_screen * facing > 0.001) {
			const int N = 40;
			vec2 stepv = to_sun / float(N);
			// A fixed offset per pixel (interleaved gradient noise) hides the banding of 40 steps. It used to change every
			// frame (+TIME): at every leaf edge against the sky the march then hit sky one frame and leaf the next, and
			// the canopy shimmered (Tom: "the trees are flashing", 27 Nov).
			vec2 px = FRAGCOORD.xy;
			float jitter = fract(52.9829189 * fract(dot(px, vec2(0.06711056, 0.00583715))));
			vec2 p = uv + stepv * jitter;
			float light = 0.0, decay = 1.0;
			for (int i = 0; i < N; i++) {
				if (p.x < 0.0 || p.y < 0.0 || p.x > 1.0 || p.y > 1.0) break;
				light += (is_sky(p) ? 1.0 : 0.0) * decay;
				decay *= 0.965;
				p += stepv;
			}
			light /= float(N) * 0.5;
			float falloff = 1.0 / (1.0 + length(to_sun * vec2(1.6, 1.0)) * 6.0);
			col += sun_color * min(light * falloff, 0.6) * rays * on_screen * facing;
		}
	}

	// 3. Grading.
	col *= exposure;
	col += lift * shadow_tint * (1.0 - smoothstep(0.0, 0.08, dot(col, vec3(0.2126, 0.7152, 0.0722))));
	float l = dot(col, vec3(0.2126, 0.7152, 0.0722));
	col *= mix(shadow_tint, highlight_tint, smoothstep(0.02, 0.6, l));
	l = dot(col, vec3(0.2126, 0.7152, 0.0722));
	col = max(mix(vec3(l), col, saturation), vec3(0.0));
	col = pow(col / 0.18, vec3(contrast)) * 0.18;
	vec2 c = uv - 0.5;
	col *= 1.0 - vignette * smoothstep(0.15, 0.75, dot(c, c) * 2.2);
	col += (hash(uv * 1777.0 + fract(TIME * 7.0)) - 0.5) * grain * (0.15 + sqrt(max(l, 0.0)));
	ALBEDO = col;
}
