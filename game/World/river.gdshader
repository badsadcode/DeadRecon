// Jungle rivers. Ported from badsadcode/river (Game/Shaders/River.gdshader at 719efe6, see its docs/WATER.md), with
// the raft-game parts taken out (segments, waterfalls, rocks in the current) and its flow field read from the mesh
// instead of textures: TerrainStreamer.BuildWater puts the current, depth, foam and the distance to the bank on every
// water vertex, from the baked water surface.
//
// Light model (as in River):
//   ALBEDO   = light scattered back by the water body (lit by the sun, so it takes shadows), thicker = more
//   EMISSION = the riverbed seen through the water (refracted screen, absorbed with path length)
//              + screen-space reflections of the banks and trees
//   SPECULAR = the engine's sky reflection and sun glint, switched off where the SSR found something
shader_type spatial;
render_mode blend_mix, cull_disabled, depth_draw_opaque, specular_schlick_ggx;

uniform sampler2D screen_tex : hint_screen_texture, filter_linear_mipmap, repeat_disable;
uniform sampler2D depth_tex : hint_depth_texture, filter_nearest, repeat_disable;

// Surface detail (ProceduralTextures.cs)
uniform sampler2D ripple_large : filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D ripple_small : filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D foam_tex : filter_linear_mipmap_anisotropic, repeat_enable;
uniform float large_scale = 8.0;
uniform float small_scale = 3.6;
uniform float flow_cycle = 2.0;
uniform float ripple_strength = 0.32;
uniform float ripple_speed = 0.12;
uniform float adv_cap = 1.8;
uniform vec2 wind = vec2(0.6, 0.25);

// Water colour: a silty jungle river, olive-brown (red goes last, blue first).
uniform vec3 absorption = vec3(0.55, 0.62, 1.05);
uniform vec3 scatter_color : source_color = vec3(0.16, 0.14, 0.075);
uniform float scatter_density = 0.9;
uniform float turbidity = 1.5;
uniform vec3 foam_color : source_color = vec3(0.78, 0.76, 0.68);
uniform float foam_bump = 6.0;
uniform float foam_scale = 4.0;
uniform float foam_cycle = 3.2;
uniform float foam_max = 0.6;

uniform float refraction_strength = 0.035;
uniform int ssr_steps = 40;
uniform float ssr_strength = 1.0;
uniform float wave_strength = 1.0;
/// Rain (Weather): extra roughness and a speckle of drops.
uniform float rain = 0.0;

// Cloud shadows, shared with the ground (Weather).
global uniform vec2 cloud_shift;
global uniform float cloud_cover;
global uniform float cloud_shadow;

varying vec3 v_world;
varying vec2 v_flow;      // current (m/s, world x/z)
varying float v_depth;    // m
varying float v_foam;     // 0..1
varying float v_shore;    // m, negative in the water: distance to the bank
varying vec2 v_wave_grad;

float gust(vec2 p, float t) {
	float g = 0.5 + 0.5 * (0.5 * sin(0.023 * p.x + 0.011 * p.y + 0.13 * t)
	                     + 0.3 * sin(-0.009 * p.x + 0.027 * p.y - 0.09 * t + 2.1)
	                     + 0.2 * sin(0.031 * p.x - 0.017 * p.y + 0.17 * t + 4.0));
	return smoothstep(0.2, 0.8, g);
}

float boil(vec2 q) {
	float b = max(0.0, sin(q.x * 0.41 + 1.1) * sin(q.y * 0.33 + 0.4) + 0.4 * sin((q.x - q.y) * 0.27));
	return b * b;
}

// River's wave function: swell, chop in rough water, wind waves in gusts, boils, standing waves in fast water.
float wave_height(vec2 p, float t, vec2 v, float depth) {
	float speed = length(v);
	float fr = speed / sqrt(9.81 * max(depth, 0.08));
	float rough = smoothstep(0.35, 0.9, fr);
	float shallow_fade = smoothstep(0.05, 0.5, depth);
	float h = 0.018 * sin(p.x * 0.31 + t * 1.7) + 0.014 * sin(p.y * 0.27 - t * 1.3 + 1.3)
	        + 0.010 * sin((p.x + p.y) * 0.53 + t * 2.3);
	h += rough * (0.05 * sin(p.x * 1.1 + t * 3.1) + 0.04 * sin(p.y * 1.4 - t * 3.7));
	float wind_amp = gust(p, t) * smoothstep(0.4, 2.5, depth);
	h += wind_amp * (0.012 * sin(p.x * 1.71 + p.y * 0.83 - t * 4.33)
	           + 0.016 * sin(p.x * 1.26 + p.y * 0.52 - t * 3.66 + 1.9)
	           + 0.014 * sin(p.x * 0.83 + p.y * 0.67 - t * 3.18 + 4.2));
	float boil_k = smoothstep(0.5, 1.4, speed) * smoothstep(1.2, 2.6, depth);
	if (boil_k > 0.0) {
		const float T = 9.0;
		float p0 = fract(t / T), p1 = fract(t / T + 0.5);
		float w0 = 1.0 - abs(1.0 - 2.0 * p0);
		float a = boil(p - v * p0 * T), b = boil(p - v * p1 * T + vec2(7.3, 3.1));
		h += 0.025 * boil_k * mix(b, a, w0);
	}
	if (speed > 0.3) {
		vec2 d = v / speed;
		float along = dot(p, d), across = -p.x * d.y + p.y * d.x;
		float k = clamp(9.81 / (speed * speed), 6.2831853 / 8.0, 6.2831853 / 2.5);
		float amp = 0.13 * smoothstep(0.45, 1.0, fr) + 0.24 * smoothstep(1.6, 3.0, speed) * smoothstep(0.8, 1.3, fr);
		float breakup = 0.55 + 0.45 * sin(across * 0.9 + 1.7 * sin(along * 0.23));
		h += amp * breakup * sin(along * k + 0.25 * sin(t * 1.9 + across * 0.4));
	}
	return h * shallow_fade;
}

void vertex() {
	v_flow = (COLOR.rg - 0.5) * 6.0;
	v_depth = COLOR.b * 8.0;
	v_foam = COLOR.a;
	v_shore = UV.x;
	vec3 w = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	const float e = 0.3;
	float t = TIME;
	float h = wave_height(w.xz, t, v_flow, v_depth);
	float hx = wave_height(w.xz + vec2(e, 0.0), t, v_flow, v_depth);
	float hz = wave_height(w.xz + vec2(0.0, e), t, v_flow, v_depth);
	v_wave_grad = vec2(hx - h, hz - h) / e * wave_strength;
	VERTEX.y += h * wave_strength;
	v_world = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

vec3 view_pos_at(vec2 uv, mat4 inv_proj) {
	float d = textureLod(depth_tex, uv, 0.0).r;
	vec4 v = inv_proj * vec4(uv * 2.0 - 1.0, d, 1.0);
	return v.xyz / v.w;
}

vec3 unpack_n(vec3 c) { return c * 2.0 - 1.0; }

float flow_scalar(sampler2D tex, vec2 xz, vec2 flow, float scale, float cycle, float t, float jitter) {
	float p0 = fract(t / cycle + jitter), p1 = fract(t / cycle + jitter + 0.5);
	float w0 = 1.0 - abs(1.0 - 2.0 * p0);
	float a = texture(tex, (xz - flow * p0 * cycle) / scale).r;
	float b = texture(tex, (xz - flow * p1 * cycle) / scale + vec2(0.53, 0.19)).r;
	return mix(b, a, w0);
}

vec2 flow_rg(sampler2D tex, vec2 xz, vec2 flow, float scale, float cycle, float t, float jitter) {
	float p0 = fract(t / cycle + jitter), p1 = fract(t / cycle + jitter + 0.5);
	float w0 = 1.0 - abs(1.0 - 2.0 * p0);
	vec2 a = texture(tex, (xz - flow * p0 * cycle) / scale).rg;
	vec2 b = texture(tex, (xz - flow * p1 * cycle) / scale + vec2(0.53, 0.19)).rg;
	return mix(b, a, w0);
}

vec3 blend_n(vec3 a, vec3 b) { return normalize(vec3(a.xy + b.xy, a.z * b.z)); }

// Living ripples: two crossing wave trains per phase that travel on their own while drifting with the current.
vec3 flow_normal_live(sampler2D tex, vec2 xz, vec2 flow, float scale, float cycle, float t, float jitter, float c) {
	float p0 = fract(t / cycle + jitter), p1 = fract(t / cycle + jitter + 0.5);
	float w0 = 1.0 - abs(1.0 - 2.0 * p0);
	vec2 dA = vec2(0.80, 0.60) * c * t;
	vec2 dB = vec2(-0.71, 0.70) * c * 1.13 * t;
	vec2 b0 = xz - flow * p0 * cycle, b1 = xz - flow * p1 * cycle;
	vec3 a0 = unpack_n(texture(tex, (b0 - dA) / scale).rgb);
	vec3 c0 = unpack_n(texture(tex, (b0 - dB) / (scale * 0.87) + vec2(0.41, 0.13)).rgb);
	vec3 a1 = unpack_n(texture(tex, (b1 - dA) / scale + vec2(0.37, 0.61)).rgb);
	vec3 c1 = unpack_n(texture(tex, (b1 - dB) / (scale * 0.87) + vec2(0.77, 0.29)).rgb);
	vec3 n0 = normalize(vec3((a0.xy + c0.xy) * 0.72, a0.z * c0.z));
	vec3 n1 = normalize(vec3((a1.xy + c1.xy) * 0.72, a1.z * c1.z));
	return normalize(mix(n1, n0, w0));
}

float hash(vec2 p) { vec3 p3 = fract(vec3(p.xyx) * 0.1031); p3 += dot(p3, p3.yzx + 33.33); return fract((p3.x + p3.y) * p3.z); }

// The ground's cloud-shadow noise (terrain.gdshader), so shadows run on from the bank across the water.
float chash(vec2 p) { p = fract(p * vec2(123.34, 456.21)); p += dot(p, p + 45.32); return fract(p.x * p.y); }
float cnoise(vec2 p) {
	vec2 i = floor(p), f = fract(p);
	vec2 u = f * f * (3.0 - 2.0 * f);
	return mix(mix(chash(i), chash(i + vec2(1, 0)), u.x), mix(chash(i + vec2(0, 1)), chash(i + vec2(1, 1)), u.x), u.y);
}

void fragment() {
	float t = TIME;
	vec2 xz = v_world.xz;
	vec2 flow_true = v_flow;
	float speed = length(flow_true);
	vec2 flow = flow_true;
	if (speed > adv_cap) flow *= adv_cap / speed;
	float shore = v_shore;
	float froude = speed / sqrt(9.81 * max(v_depth, 0.08));
	float rough_water = smoothstep(0.35, 0.9, froude);

	// ------------------------------------------------------------- surface normal
	float jitter = textureLod(foam_tex, xz * 0.0035, 7.0).r * 0.8;
	vec3 nL = flow_normal_live(ripple_large, xz, flow, large_scale, flow_cycle, t, jitter, ripple_speed * 1.6);
	vec3 nS = flow_normal_live(ripple_small, xz, flow * 1.1, small_scale, flow_cycle * 0.7, t, jitter + 0.25, ripple_speed);
	vec2 wuv = (xz + wind * t * 1.6) / (small_scale * 0.8);
	vec3 nW = unpack_n(texture(ripple_small, wuv + vec2(0.21, 0.73)).rgb);
	vec3 nW2 = unpack_n(texture(ripple_small, (xz + wind.yx * vec2(-1.0, 1.0) * t * 1.1) / (small_scale * 1.7)).rgb);
	float slow = 1.0 - smoothstep(0.08, 0.6, speed);
	float g = gust(xz, t);
	float turb = flow_scalar(foam_tex, xz, flow, 45.0, 6.0, t, jitter);
	float wind_k = length(wind) * (0.35 + 0.65 * slow) * mix(0.12, 1.3, g);
	float flow_k = ripple_strength * (0.45 + 0.9 * smoothstep(0.1, 1.6, speed) + 1.2 * rough_water) * mix(0.5, 1.2, turb) * mix(0.7, 1.08, g);
	vec3 n_ts = blend_n(vec3(nL.xy * flow_k, nL.z), vec3(nS.xy * flow_k * 0.8, nS.z));
	n_ts = blend_n(n_ts, vec3((nW.xy + nW2.xy) * 0.35 * wind_k, 1.0));
	n_ts = blend_n(n_ts, vec3(-v_wave_grad, 1.0));
	// Rain: drops prick the surface all over.
	if (rain > 0.01) {
		vec2 cell = floor(xz * 3.0);
		float ph = fract(t * 1.3 + hash(cell));
		vec2 c = (cell + vec2(hash(cell + 7.1), hash(cell + 3.3))) / 3.0;
		float r = length(xz - c);
		float ring = sin((r - ph * 0.25) * 70.0) * smoothstep(0.25 * ph + 0.02, 0.25 * ph, r) * (1.0 - ph);
		n_ts.xy += normalize(xz - c + 1e-4) * ring * rain * 0.6;
	}
	n_ts.xy *= mix(0.35, 1.0, smoothstep(-0.2, -3.0, shore));
	n_ts = normalize(n_ts);
	vec3 n_world = normalize(vec3(n_ts.x, n_ts.z, n_ts.y));
	vec3 n_view = normalize((VIEW_MATRIX * vec4(n_world, 0.0)).xyz);
	NORMAL = n_view;

	// ------------------------------------------------------------- geometry through the water
	vec3 vpos = VERTEX;
	vec3 vdir = normalize(-vpos);
	float cos_v = clamp(dot(n_view, vdir), 0.0, 1.0);
	float fresnel = 0.02 + 0.98 * pow(1.0 - cos_v, 5.0);
	vec3 behind = view_pos_at(SCREEN_UV, INV_PROJECTION_MATRIX);
	float thickness = max(length(behind - vpos), 0.0);
	float dist = length(vpos);
	vec2 ruv = SCREEN_UV + n_world.xz * refraction_strength * clamp(thickness, 0.0, 3.0) / (1.0 + dist * 0.06);
	vec3 behind_r = view_pos_at(ruv, INV_PROJECTION_MATRIX);
	if (behind_r.z > vpos.z - 0.02) { ruv = SCREEN_UV; behind_r = behind; }
	float path = max(length(behind_r - vpos), 0.0) * turbidity;
	vec3 transmit = exp(-absorption * path);
	float lod = clamp(path * 0.35, 0.0, 4.0);
	vec3 bed = textureLod(screen_tex, ruv, lod).rgb;
	vec3 through = bed * transmit;
	float scatter_amt = 1.0 - exp(-scatter_density * path);

	// ------------------------------------------------------------- screen-space reflection
	vec3 n_ssr = normalize(mix(vec3(0.0, 1.0, 0.0), n_world, 0.35));
	vec3 n_ssr_v = normalize((VIEW_MATRIX * vec4(n_ssr, 0.0)).xyz);
	vec3 r = normalize(reflect(normalize(vpos), n_ssr_v));
	vec3 ssr_col = vec3(0.0);
	float ssr_mask = 0.0;
	if (ssr_steps > 0 && r.z < 0.25) {
		float step_len = 0.3 + dist * 0.012;
		vec3 p = vpos;
		vec3 prev = vpos;
		for (int i = 0; i < ssr_steps; i++) {
			prev = p;
			p += r * step_len;
			step_len *= 1.13;
			vec4 c = PROJECTION_MATRIX * vec4(p, 1.0);
			vec2 uv = c.xy / c.w * 0.5 + 0.5;
			if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0 || c.w <= 0.0) break;
			vec3 s = view_pos_at(uv, INV_PROJECTION_MATRIX);
			float diff = s.z - p.z;
			if (diff > 0.0 && diff < step_len * 2.5 + 0.4) {
				vec3 a = prev, b = p;
				for (int k = 0; k < 5; k++) {
					vec3 m = 0.5 * (a + b);
					vec4 cm = PROJECTION_MATRIX * vec4(m, 1.0);
					vec2 um = cm.xy / cm.w * 0.5 + 0.5;
					vec3 sm = view_pos_at(um, INV_PROJECTION_MATRIX);
					if (sm.z - m.z > 0.0) b = m; else a = m;
				}
				vec4 cb = PROJECTION_MATRIX * vec4(b, 1.0);
				uv = cb.xy / cb.w * 0.5 + 0.5;
				vec2 edge = smoothstep(vec2(0.0), vec2(0.08), uv) * smoothstep(vec2(0.0), vec2(0.08), 1.0 - uv);
				float fade = edge.x * edge.y * (1.0 - smoothstep(0.6, 1.0, float(i) / float(ssr_steps)));
				fade *= 1.0 - smoothstep(-0.1, 0.25, r.z);
				ssr_col = textureLod(screen_tex, uv, 0.0).rgb;
				ssr_mask = fade * ssr_strength;
				break;
			}
		}
	}

	// ------------------------------------------------------------- foam
	float foam_field = v_foam;
	float lap = (1.0 - smoothstep(0.0, 0.6, -shore)) * (0.08 + 0.22 * smoothstep(0.15, 1.0, speed));
	float coverage = foam_field * 0.7 + lap + rough_water * 0.15;
	float patch = flow_scalar(foam_tex, xz, flow, 38.0, foam_cycle * 3.0, t, jitter);
	float white = max(smoothstep(2.0, 3.0, speed) * rough_water, smoothstep(0.85, 1.0, foam_field));
	coverage = clamp(max(coverage * mix(0.35, 1.3, patch), white * foam_field * mix(0.7, 0.95, patch)), 0.0, mix(foam_max, 0.85, white));
	vec2 fa = flow_rg(foam_tex, xz, flow, foam_scale, foam_cycle, t, jitter);
	vec2 fb = flow_rg(foam_tex, xz, flow, foam_scale * 0.29, foam_cycle * 0.8, t, jitter + 0.37);
	float pattern = fa.x + (fb.x - 0.5) * 0.3;
	float th = 1.0 - coverage;
	float present = smoothstep(th, th + 0.12, pattern);
	float density = smoothstep(th, th + 0.45, pattern);
	float lace = mix(0.35, 1.0, max(fb.y, density * density));
	float foam = present * (0.3 + 0.62 * density) * lace * smoothstep(0.01, 0.06, coverage);
	vec3 foam_albedo = foam_color * (0.62 + 0.38 * density) * mix(0.82, 1.0, fb.y);
	vec2 fg = vec2(dFdx(pattern), dFdy(pattern)) / (1.0 + dist * 0.08);
	NORMAL = normalize(NORMAL + vec3(-fg * foam_bump * present, 0.0));

	// ------------------------------------------------------------- falls
	// Where the water sheet tilts steeply (the river steps down), it's white water pouring over: foam streaks running
	// downhill, not a mirror-smooth chrome slide.
	float fall_glow = 0.0;
	vec3 gn = normalize(cross(dFdx(v_world), dFdy(v_world)));
	float steep = smoothstep(0.3, 0.65, 1.0 - abs(gn.y));
	if (steep > 0.001) {
		vec2 side = normalize(vec2(-gn.z, gn.x) + vec2(1e-5));
		// Long vertical streaks (stretched along the fall), sliding down fast.
		vec2 suv = vec2(dot(xz, side) * 1.4, v_world.y * 0.12 + t * 1.1);
		float streak = texture(foam_tex, suv).r * 0.65 + texture(foam_tex, suv * vec2(2.7, 1.6) + 0.31).r * 0.35;
		streak = smoothstep(0.25, 0.85, streak);
		foam = max(foam, steep * (0.75 + 0.25 * streak));
		foam_albedo = mix(foam_albedo, foam_color * (0.8 + 0.35 * streak), steep);
		fall_glow = steep * (0.08 + 0.1 * streak);   // falling white water scatters light: bright even in shade
	}

	// ------------------------------------------------------------- compose
	float refl_w = fresnel;
	vec3 body = scatter_color * scatter_amt;
	// Cloud shadows drift over the water too (they darken the lit body and the glint, not what's beneath).
	vec2 cp = (xz + cloud_shift) * 0.0016;
	float cn = cnoise(cp) * 0.6 + cnoise(cp * 2.3 + 5.0) * 0.3 + cnoise(cp * 5.1 + 9.0) * 0.1;
	float cs = smoothstep(1.0 - cloud_cover * 0.85, 1.0 - cloud_cover * 0.85 + 0.18, cn) * cloud_shadow;
	ALBEDO = mix(body * (1.0 - refl_w), foam_albedo, foam) * (1.0 - cs * 0.6);
	EMISSION = (through * (1.0 - refl_w) + ssr_col * refl_w * ssr_mask) * (1.0 - foam) + foam_color * fall_glow;
	SPECULAR = mix(0.28 * (1.0 - ssr_mask), 0.1, foam) * (1.0 - cs * 0.7);
	float far_rough = smoothstep(15.0, 450.0, dist) * 0.1;
	ROUGHNESS = mix(mix(0.025, 0.09, rough_water) + far_rough + rain * 0.12, 0.65, foam);
	METALLIC = 0.0;
	ALPHA = clamp(thickness * 6.0, 0.0, 1.0);
	// From below (wading in deep water, eyes under): a dim olive ceiling with a brighter window above.
	if (!FRONT_FACING) {
		float up_k = clamp(dot(-n_view, vdir), 0.0, 1.0);
		float window = smoothstep(0.55, 0.8, up_k);
		ALBEDO = vec3(0.0);
		EMISSION = mix(vec3(0.05, 0.06, 0.03), vec3(0.32, 0.36, 0.22) * (0.85 + 0.3 * nS.x), window) * (1.0 - foam * 0.5) + foam_albedo * foam * 0.4;
		SPECULAR = 0.0;
		ROUGHNESS = 1.0;
		ALPHA = 1.0;
	}
}
