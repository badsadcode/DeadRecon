// Dead Recon sky: gradient sky, a sun with a soft halo that warms the horizon around it at dawn and dusk, a moon,
// stars that come out at night, and a layer of clouds drifting with the wind: lit from the sun's side, darker
// underneath, silver-edged near the sun, thickening into overcast as rain comes. Driven entirely by TimeOfDay
// (uniforms) plus the scene's two lights (LIGHT0 = sun, LIGHT1 = moon).
shader_type sky;

uniform vec3 top_color : source_color = vec3(0.34, 0.48, 0.60);
uniform vec3 horizon_color : source_color = vec3(0.78, 0.80, 0.74);
uniform vec3 ground_color : source_color = vec3(0.14, 0.17, 0.12);
uniform vec3 sun_glow_color : source_color = vec3(1.0, 0.75, 0.45);
uniform float sun_glow = 0.4;        // how far the sun's warmth spreads across the sky (dawn/dusk high)
uniform float sun_disc = 1.0;        // 0 when clouded over
uniform float stars = 0.0;           // 0 day .. 1 clear night
uniform float overcast = 0.0;        // flattens everything toward a grey dome
uniform vec3 overcast_color : source_color = vec3(0.45, 0.48, 0.5);
uniform float flash = 0.0;           // lightning

// Clouds.
uniform float cloud_cover = 0.4;    // 0 none .. 1 a full deck
uniform vec2 cloud_offset = vec2(0.0);   // wind drift (moved by TimeOfDay)
uniform vec3 cloud_lit : source_color = vec3(1.0, 0.97, 0.92);    // the sunny side
uniform vec3 cloud_shade : source_color = vec3(0.55, 0.6, 0.68);  // the underside / shadowed side
uniform float cloud_scale = 0.65;

float hash(vec3 p) {
	p = fract(p * 0.3183099 + 0.1);
	p *= 17.0;
	return fract(p.x * p.y * p.z * (p.x + p.y + p.z));
}

// Precision-safe (fract first, small numbers throughout): the usual "p * p" hash breaks up into flat shards once
// the noise coordinates get into the hundreds, which fbm's octaves do.
float hash2(vec2 p) {
	vec3 p3 = fract(vec3(p.xyx) * 0.1031);
	p3 += dot(p3, p3.yzx + 33.33);
	return fract((p3.x + p3.y) * p3.z);
}

float noise(vec2 p) {
	vec2 i = floor(p), f = fract(p);
	vec2 u = f * f * (3.0 - 2.0 * f);
	return mix(mix(hash2(i), hash2(i + vec2(1, 0)), u.x), mix(hash2(i + vec2(0, 1)), hash2(i + vec2(1, 1)), u.x), u.y);
}

float fbm(vec2 p, int octaves) {
	float v = 0.0, a = 0.5;
	mat2 r = mat2(vec2(0.8, 0.6), vec2(-0.6, 0.8));
	for (int i = 0; i < 6; i++) {
		if (i >= octaves) break;
		v += a * noise(p);
		p = r * p * 2.03 + 1.7;
		a *= 0.5;
	}
	return v;
}

// Cloud density (0..1) at a point on the cloud plane.
float clouds(vec2 p, int octaves) {
	vec2 warp = vec2(fbm(p * 0.5 + 3.1, 3), fbm(p * 0.5 + 7.7, 3)) - 0.5;
	float n = fbm(p + warp * 0.9 + cloud_offset, octaves);
	float edge = 1.0 - cloud_cover;
	return smoothstep(edge * 0.75, edge * 0.75 + 0.2, n);
}

void sky() {
	vec3 d = EYEDIR;
	float h = d.y;
	vec3 col = mix(horizon_color, top_color, smoothstep(0.0, 0.55, h));
	col = mix(col, ground_color, smoothstep(0.0, -0.25, h));
	float cosA = 0.0;
	vec3 s = vec3(0.0, 1.0, 0.0);

	// Sun: halo spreading along the horizon (wider and warmer when the sun is low), plus the disc.
	if (LIGHT0_ENABLED) {
		s = LIGHT0_DIRECTION;
		cosA = dot(d, s);
		float near = smoothstep(0.2, 1.0, cosA);
		float horizonBand = exp(-abs(h) * 5.0);
		float glow = pow(max(cosA, 0.0), 6.0) * 0.6 + near * horizonBand * 0.9;
		col += sun_glow_color * glow * sun_glow * (1.0 - overcast * 0.85);
		float disc = smoothstep(0.9994, 0.9997, cosA);
		col += LIGHT0_COLOR * disc * 6.0 * sun_disc;
	}
	// Moon: a pale disc with a faint halo.
	float moonLight = 0.0;
	if (LIGHT1_ENABLED) {
		vec3 m = LIGHT1_DIRECTION;
		float cosM = dot(d, m);
		moonLight = pow(max(cosM, 0.0), 6.0);
		col += vec3(0.75, 0.8, 0.9) * smoothstep(0.99955, 0.9998, cosM) * 1.4 * (1.0 - overcast);
		col += vec3(0.25, 0.3, 0.45) * pow(max(cosM, 0.0), 40.0) * 0.25 * (1.0 - overcast);
	}

	// Clouds: a flat deck overhead seen in perspective, thinning to nothing at the horizon (where the haze is).
	float cover = 0.0;
	if (h > 0.0) {
		int oct = AT_CUBEMAP_PASS ? 3 : 5;
		vec2 p = d.xz / (h + 0.12) * cloud_scale;
		float dens = clouds(p, oct);
		// Light: denser toward the sun means this side is in its own shadow.
		vec2 toSun = normalize(s.xz + vec2(1e-4)) * 0.16;
		float towardSun = clouds(p + toSun, 3);
		float lit = clamp(0.55 + (dens - towardSun) * 1.8, 0.0, 1.0);
		vec3 cloud = mix(cloud_shade, cloud_lit, lit);
		// Thin edges near the sun glow (silver lining); the thick middles go darker.
		float thin = 1.0 - smoothstep(0.25, 0.9, dens);
		cloud += sun_glow_color * pow(max(cosA, 0.0), 8.0) * thin * 1.6 * sun_disc;
		cloud *= mix(1.0, 0.72, smoothstep(0.6, 1.0, dens) * cloud_cover);
		// Mottling: lumps and lighter breaks, so a full storm deck isn't a flat grey sheet.
		float lumps = fbm(p * 1.4 + cloud_offset * 1.3 + 11.0, oct - 1);
		cloud *= mix(0.78, 1.14, lumps) * mix(1.0, mix(0.85, 1.1, lumps), cloud_cover);
		cloud += vec3(0.5, 0.55, 0.65) * moonLight * thin * 0.15;
		cover = dens * smoothstep(0.0, 0.18, h);
		col = mix(col, cloud, cover * 0.92);
	}

	// Stars: a fixed field, twinkling slightly, only above the horizon, only when dark, hidden by clouds.
	if (stars > 0.01 && h > 0.0) {
		vec3 cell = floor(d * 220.0);
		float r = hash(cell);
		float star = step(0.9975, r) * (0.6 + 0.4 * sin(TIME * (2.0 + r * 5.0) + r * 40.0));
		col += vec3(0.85, 0.9, 1.0) * star * stars * smoothstep(0.0, 0.2, h) * (1.0 - overcast) * (1.0 - cover);
	}
	col = mix(col, overcast_color * (0.6 + 0.4 * smoothstep(-0.2, 0.6, h)), overcast * 0.85);
	col += vec3(0.7, 0.75, 0.9) * flash;
	COLOR = col;
}
