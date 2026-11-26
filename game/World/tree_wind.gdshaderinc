// Tree wind from Noor Interchange wind channels (shared by leaves.gdshader and bark.gdshader).
// bend_flutter = UV2 (bend, flutter), phase_sway = CUSTOM0.xy (phase, sway). Offsets in model space (metres).
vec3 tree_wind(vec3 v, vec3 n, vec2 bend_flutter, vec2 phase_sway, vec3 inst, float t, float strength, vec2 dir) {
	float seed = inst.x * 0.137 + inst.z * 0.173;
	float bend = bend_flutter.x, flutter = bend_flutter.y, phase = phase_sway.x, sway = phase_sway.y;
	vec3 d = normalize(vec3(dir.x, 0.0, dir.y));
	float gust = 0.6 + 0.4 * sin(t * 0.37 + seed * 0.7) * sin(t * 0.21 + seed);
	float s = strength * gust;
	// Whole tree: a slow lean with the wind and a sway back and forth, more the higher you go.
	vec3 off = d * sway * (0.25 * s + 0.18 * s * sin(t * 0.85 + seed)) ;
	off += vec3(-d.z, 0.0, d.x) * sway * 0.06 * s * sin(t * 1.3 + seed * 1.7);
	// Branches: bow and bob, each at its own phase.
	float bp = t * (1.6 + 0.4 * s) + phase * 6.2832 + seed;
	off += (d * sin(bp) * 0.12 + vec3(0.0, 0.05, 0.0) * sin(bp * 1.3 + 1.0)) * bend * (0.3 + s);
	// Leaf tips: a gentle flutter, sideways in the wind. (Not along the normal: NoorForm's crown normals point out of
	// the crown, so that pushed every leaf in and out through its neighbours' shadows, and the crowns flickered.)
	float fp = t * (2.6 + 1.6 * s) + phase * 40.0 + v.y * 0.7 + seed;
	vec3 side = vec3(-d.z, 0.0, d.x);
	off += (side * sin(fp) + vec3(0.0, 0.5, 0.0) * sin(fp * 1.37 + 0.8)) * flutter * (0.004 + 0.012 * s);
	return off;
}
