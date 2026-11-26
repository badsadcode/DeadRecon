// A snake from a straight tube: UV.x runs head (0) to tail (1), UV.y round the body (0 = back, .5 = belly).
// The body bends sideways in a travelling wave (slithering) or a standing S (resting coil); the front lifts and the
// hood spreads when it rears up to warn you. Patterns are painted here too, so one mesh makes every kind.
shader_type spatial;

uniform float length = 1.6;
uniform float slither = 0.0;     // 0 still .. 1 full slither (moving)
uniform float phase = 0.0;       // advances while moving (set by the script, so the wave matches the speed)
uniform float coil = 1.0;        // resting S-curve amount
uniform float rear = 0.0;        // 0 flat .. 1 front raised
uniform float hood = 0.0;        // cobra hood
uniform float strike = 0.0;      // 0..1 lunge forward
uniform vec3 base_color : source_color = vec3(0.25, 0.22, 0.15);
uniform vec3 pattern_color : source_color = vec3(0.1, 0.08, 0.05);
uniform vec3 belly_color : source_color = vec3(0.75, 0.7, 0.5);
uniform int pattern = 0;          // 0 plain, 1 bands, 2 diamonds (back), 3 net (python)
uniform float pattern_scale = 14.0;
uniform float wet : hint_range(0.0, 1.0) = 0.0;
uniform float ground[16];        // ground height (relative to the head) at 16 points down the body, from the script

varying float t;
varying float around;

void vertex() {
	t = UV.x;
	around = UV.y;
	float z = VERTEX.z;                       // body runs along -Z from the head (z = 0) to the tail (z = -length)
	float body = smoothstep(0.0, 0.18, t);    // the head stays steady
	// Travelling wave (slither) and standing S (coil) side to side.
	float wave = sin(t * 9.0 - phase) * 0.11 * slither;
	float s = sin(t * 7.5 + 0.6) * 0.16 * coil * (1.0 - slither);
	VERTEX.x += (wave + s) * length * body;
	// Coiled: the tail curls back alongside, so the snake is shorter at rest.
	VERTEX.z = mix(z, z * 0.62, coil * (1.0 - slither));
	// Rearing: the front quarter stands up in a column, the head bent forward at the top, looking at you.
	const float F = 0.24;
	float u = clamp(1.0 - t / F, 0.0, 1.0);                  // 1 at the head, 0 where the body leaves the ground
	float hu = min(u, 1.0 - 0.05 / F);                       // the head itself stays level
	vec3 up = vec3(VERTEX.x, VERTEX.y + hu * F * length * 0.85, -F * length * (1.0 - 0.25 * hu) + max(0.0, 0.05 - t) * length);
	float lift = rear * smoothstep(0.0, 0.15, u);
	VERTEX = mix(VERTEX, up, lift);
	// Strike: the raised front throws itself forward.
	VERTEX.z += strike * u * length * 0.35;
	// Hood: the neck flattens out wide.
	float neck = smoothstep(0.03, 0.07, t) * (1.0 - smoothstep(0.12, 0.2, t));
	VERTEX.x *= 1.0 + hood * neck * 3.2;
	VERTEX.y *= 1.0 - hood * neck * 0.35;
	// Lie on the ground: each part of the body follows the slope under it.
	float gi = t * 15.0;
	int i0 = int(floor(gi));
	VERTEX.y += mix(ground[i0], ground[min(i0 + 1, 15)], fract(gi));
}

float hash(vec2 p) { vec3 p3 = fract(vec3(p.xyx) * 0.1031); p3 += dot(p3, p3.yzx + 33.33); return fract((p3.x + p3.y) * p3.z); }

void fragment() {
	float belly = smoothstep(0.32, 0.45, abs(around - 0.5) < 0.5 ? 1.0 - abs(around - 0.5) * 2.0 : 0.0);
	belly = smoothstep(0.55, 0.8, 1.0 - abs(around - 0.5) * 2.0);
	vec3 c = base_color;
	float u = t * length * pattern_scale;
	float side = abs(around - 0.5) * 2.0;     // 1 on the back, 0 on the belly
	if (pattern == 1) c = mix(c, pattern_color, step(0.5, fract(u * 0.5)));                                   // bands
	else if (pattern == 2) {                                                                                   // diamonds down the back
		float d = abs(fract(u * 0.5) - 0.5) * 2.0 + (1.0 - side) * 1.6;
		c = mix(c, pattern_color, smoothstep(0.75, 0.65, d));
	} else if (pattern == 3) {                                                                                 // python net
		float n = abs(sin(u * 1.3 + side * 6.0)) * abs(sin(u * 0.9 - side * 5.0));
		c = mix(c, pattern_color, smoothstep(0.25, 0.1, n));
	}
	c *= 0.85 + 0.3 * hash(floor(vec2(u * 3.0, around * 24.0)));                                              // scales
	c = mix(c, belly_color, belly);
	// Head a touch darker on top.
	c *= mix(1.0, 0.8, (1.0 - smoothstep(0.0, 0.06, t)) * side);
	ALBEDO = c;
	ROUGHNESS = mix(0.55, 0.25, wet);
	SPECULAR = 0.32;
}
