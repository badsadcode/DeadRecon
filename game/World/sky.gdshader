// Dead Recon sky: gradient sky, a sun with a soft halo that warms the horizon around it at dawn and dusk, a moon,
// and stars that come out at night. Driven entirely by TimeOfDay (uniforms) plus the scene's two lights
// (LIGHT0 = sun, LIGHT1 = moon).
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

float hash(vec3 p) {
	p = fract(p * 0.3183099 + 0.1);
	p *= 17.0;
	return fract(p.x * p.y * p.z * (p.x + p.y + p.z));
}

void sky() {
	vec3 d = EYEDIR;
	float h = d.y;
	vec3 col = mix(horizon_color, top_color, smoothstep(0.0, 0.55, h));
	col = mix(col, ground_color, smoothstep(0.0, -0.25, h));

	// Sun: halo spreading along the horizon (wider and warmer when the sun is low), plus the disc.
	if (LIGHT0_ENABLED) {
		vec3 s = LIGHT0_DIRECTION;
		float cosA = dot(d, s);
		float near = smoothstep(0.2, 1.0, cosA);
		float horizonBand = exp(-abs(h) * 5.0);
		float glow = pow(max(cosA, 0.0), 6.0) * 0.6 + near * horizonBand * 0.9;
		col += sun_glow_color * glow * sun_glow * (1.0 - overcast * 0.85);
		float disc = smoothstep(0.9994, 0.9997, cosA);
		col += LIGHT0_COLOR * disc * 6.0 * sun_disc;
	}
	// Moon: a pale disc with a faint halo.
	if (LIGHT1_ENABLED) {
		vec3 m = LIGHT1_DIRECTION;
		float cosM = dot(d, m);
		col += vec3(0.75, 0.8, 0.9) * smoothstep(0.99955, 0.9998, cosM) * 1.4 * (1.0 - overcast);
		col += vec3(0.25, 0.3, 0.45) * pow(max(cosM, 0.0), 40.0) * 0.25 * (1.0 - overcast);
	}
	// Stars: a fixed field, twinkling slightly, only above the horizon and only when dark and clear.
	if (stars > 0.01 && h > 0.0) {
		vec3 cell = floor(d * 220.0);
		float r = hash(cell);
		float star = step(0.9975, r) * (0.6 + 0.4 * sin(TIME * (2.0 + r * 5.0) + r * 40.0));
		col += vec3(0.85, 0.9, 1.0) * star * stars * smoothstep(0.0, 0.2, h) * (1.0 - overcast);
	}
	col = mix(col, overcast_color * (0.6 + 0.4 * smoothstep(-0.2, 0.6, h)), overcast * 0.85);
	col += vec3(0.7, 0.75, 0.9) * flash;
	COLOR = col;
}
