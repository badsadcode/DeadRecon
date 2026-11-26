// Jungle ground.
// Two looks, blended by texture_mix (T in game): 0 = painted placeholder (vertex COLOR + noise), 1 = photo textures.
// Vertex UV carries blend weights from TerrainStreamer: x = rock (steep slope), y = wet (near water).
shader_type spatial;
render_mode cull_disabled;

uniform float texture_mix : hint_range(0.0, 1.0) = 1.0;
uniform float wetness : hint_range(0.0, 1.0) = 0.0;  // Weather: soaked ground is darker and shinier, with puddles
uniform float tile_metres = 2.6;   // one repeat of a ground texture
uniform float rock_metres = 5.0;
uniform float bump_strength = 0.012;  // metres of relief read from the textures' light/dark detail (fades with distance)

uniform sampler2D loam_tex : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D litter_tex : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D moss_tex : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D rock_tex : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D mud_tex : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D pebble_tex : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
// Roads and settlement ground from the world's settle pass: R = road surface, G = yards. One texel per height sample.
uniform sampler2D settle_mask : filter_linear_mipmap, repeat_disable;
uniform bool has_settle = false;
uniform float settle_half = 4096.0;     // region half size (m)
uniform float settle_spacing = 2.0;     // metres per texel
uniform float settle_px = 4097.0;       // texels per side

// Cloud shadows drifting across the ground (set by TimeOfDay).
global uniform vec2 cloud_shift;
global uniform float cloud_cover;
global uniform float cloud_shadow;

varying vec3 world_pos;
varying vec3 world_normal;

float hash(vec2 p) {
	p = fract(p * vec2(123.34, 456.21));
	p += dot(p, p + 45.32);
	return fract(p.x * p.y);
}

float noise(vec2 p) {
	vec2 i = floor(p), f = fract(p);
	vec2 u = f * f * (3.0 - 2.0 * f);
	return mix(mix(hash(i), hash(i + vec2(1, 0)), u.x), mix(hash(i + vec2(0, 1)), hash(i + vec2(1, 1)), u.x), u.y);
}

// Hide tiling: blend the texture with a rotated, rescaled copy of itself, switching by low-frequency noise.
vec3 untiled(sampler2D t, vec2 p) {
	vec3 a = texture(t, p).rgb;
	mat2 r = mat2(vec2(0.8, 0.6), vec2(-0.6, 0.8));
	vec3 b = texture(t, r * p * 0.71 + vec2(0.37, 0.11)).rgb;
	float k = smoothstep(0.3, 0.7, noise(p * 0.23));
	return mix(a, b, k);
}

// Side-projected for slopes: no stretching on cliffs.
vec3 triplanar(sampler2D t, vec3 p, vec3 n) {
	vec3 w = pow(abs(n), vec3(4.0));
	w /= (w.x + w.y + w.z);
	return texture(t, p.zy).rgb * w.x + texture(t, p.xz).rgb * w.y + texture(t, p.xy).rgb * w.z;
}

void vertex() {
	world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	world_normal = normalize((MODEL_MATRIX * vec4(NORMAL, 0.0)).xyz);
}

void fragment() {
	vec2 p = world_pos.xz;
	float broad = noise(p * 0.035);                 // ~30 m patches
	float mid = noise(p * 0.45);                    // ~2 m clumps
	float fine = noise(p * 3.7) * 0.6 + noise(p * 11.0) * 0.4;
	float dist = length(world_pos - (INV_VIEW_MATRIX * vec4(0, 0, 0, 1)).xyz);
	float near = 1.0 - smoothstep(40.0, 160.0, dist);
	float flatness = smoothstep(0.75, 0.95, world_normal.y);
	float rock_w = UV.x, wet_w = UV.y;
	// Roads and yards, with ragged edges (noise) so they don't look like they were cut with a ruler.
	float road = 0.0, yard = 0.0, paddy = 0.0, wheat = 0.0;
	if (has_settle) {
		vec3 sm = texture(settle_mask, ((p + vec2(settle_half)) / settle_spacing + 0.5) / settle_px).rgb;
		paddy = smoothstep(0.8, 0.95, sm.b);
		wheat = smoothstep(0.25, 0.45, sm.b) * (1.0 - paddy);
		road = smoothstep(0.3, 0.7, sm.r + (noise(p * 0.7) - 0.5) * 0.35 + (noise(p * 3.1) - 0.5) * 0.18);
		yard = smoothstep(0.25, 0.75, sm.g + (noise(p * 0.12) - 0.5) * 0.55 + (noise(p * 1.3) - 0.5) * 0.25) * (1.0 - road);
		rock_w *= 1.0 - max(max(road, yard), max(paddy, wheat));
	}

	// ---- painted placeholder (authored in sRGB)
	vec3 c = COLOR.rgb;
	vec3 litter = mix(vec3(0.17, 0.12, 0.07), vec3(0.24, 0.19, 0.10), mid);
	c = mix(c, litter, 0.35 * flatness * (0.4 + 0.6 * broad));
	c = mix(c, vec3(0.10, 0.16, 0.06), 0.25 * (1.0 - broad) * flatness);
	c *= mix(0.85, 1.12, broad);
	c *= mix(1.0, mix(0.72, 1.22, fine), near);
	c = mix(c, vec3(0.50, 0.32, 0.20) * mix(0.85, 1.1, mid), road);
	c = mix(c, vec3(0.40, 0.32, 0.22) * mix(0.85, 1.1, mid), yard * 0.8);
	vec3 painted = pow(clamp(c, 0.0, 1.0), vec3(2.2));

	// ---- photo textures (already linear via source_color)
	vec2 uv = p / tile_metres;
	vec3 floor_c = mix(untiled(loam_tex, uv), untiled(litter_tex, uv * 0.9 + 0.5), smoothstep(0.35, 0.7, broad));
	// Jungle floor is mostly green (Tom: "I always thought Vietnam would be greener, not rusty brown"): creeping
	// groundcover over most of it, bare leaf litter in patches and under the densest shade.
	float moss_mask = smoothstep(0.28, 0.6, noise(p * 0.11 + 7.0) * 0.7 + noise(p * 0.43 + 2.0) * 0.3) * mix(0.55, 1.0, flatness);
	floor_c = mix(floor_c, untiled(moss_tex, uv * 1.1), moss_mask * 0.9);
	vec3 rock_c = triplanar(rock_tex, world_pos / rock_metres, world_normal);
	vec3 mud_c = mix(untiled(mud_tex, uv * 0.8), floor_c, 0.45); // laterite, muted into the soil
	vec3 pebble_c = untiled(pebble_tex, uv * 1.4);
	vec3 t = floor_c;
	t = mix(t, mud_c, smoothstep(0.2, 0.7, wet_w) * 0.85);
	t = mix(t, pebble_c, smoothstep(0.75, 1.0, wet_w));
	t = mix(t, rock_c, rock_w);
	// Road: packed red laterite with grit and darker wheel-worn patches. Yards: trampled, dusty earth.
	vec3 laterite = untiled(mud_tex, uv * 0.6) * vec3(1.18, 0.92, 0.78);
	laterite = mix(laterite, pebble_c * vec3(1.05, 0.95, 0.85), smoothstep(0.55, 0.85, noise(p * 0.9 + 3.0)) * 0.45);
	laterite *= mix(0.8, 1.08, noise(p * 0.35 + 11.0));
	t = mix(t, laterite, road);
	vec3 trampled = mix(floor_c, untiled(mud_tex, uv * 0.8), 0.55);
	trampled = mix(vec3(dot(trampled, vec3(0.3, 0.59, 0.11))), trampled, 0.6) * 1.12;
	t = mix(t, trampled, yard * 0.85);
	// Fields: wet black mud under the paddies, tilled earth under the wheat. Far away (where the crops aren't drawn)
	// they take the crop's colour, so the patchwork still shows from a hilltop.
	float far = smoothstep(170.0, 260.0, dist);
	vec3 tilled = mix(floor_c, untiled(mud_tex, uv * 1.2), 0.6) * 0.75;
	t = mix(t, mix(tilled, vec3(0.42, 0.34, 0.15), far), wheat);
	t = mix(t, mix(untiled(mud_tex, uv) * 0.35, vec3(0.13, 0.17, 0.09), far), paddy);
	t *= mix(0.8, 1.1, broad);                      // big-scale variation so distant ground isn't uniform
	t *= 0.85;                                      // photos are brighter than a shaded jungle floor
	// Pull the orange out: the leaf-litter photo is very saturated orange-brown ("rusty bucket" in playtest).
	float luma = dot(t, vec3(0.3, 0.59, 0.11));
	t = mix(vec3(luma), t, 0.72) * vec3(0.97, 1.0, 0.98);
	t = mix(t, t * vec3(0.86, 1.04, 0.8), (1.0 - road) * (1.0 - yard) * (1.0 - rock_w));   // olive, not rust (not the red roads)

	// Hue drift: slow patches of warmer/redder and greener/yellower ground, so the floor isn't one carpet.
	float hue_a = noise(p * 0.017 + 91.0), hue_b = noise(p * 0.061 + 13.0);
	vec3 warm = t * vec3(1.05, 0.98, 0.92), green = t * vec3(0.92, 1.07, 0.88), yellow = t * vec3(1.03, 1.03, 0.88);
	t = mix(t, warm, smoothstep(0.7, 0.92, hue_a) * 0.5);
	t = mix(t, green, smoothstep(0.35, 0.8, 1.0 - hue_a) * 0.8 * flatness);
	t = mix(t, yellow, smoothstep(0.6, 0.9, hue_b) * 0.4);

	// Loft: the textures' own brightness as height. Dark gaps between leaves and stones sink in (darker, occluded)
	// and the surface normal tilts with the detail so low sun picks it out.
	// Height from a softened sample (a blurrier mip level): the shapes of leaves and clods, not pixel grain,
	// so the relief doesn't glitter.
	vec3 soft = mix(texture(loam_tex, uv, 2.5).rgb, texture(litter_tex, uv * 0.9 + 0.5, 2.5).rgb, smoothstep(0.35, 0.7, broad));
	soft = mix(soft, triplanar(rock_tex, world_pos / rock_metres, world_normal), rock_w);
	float h = smoothstep(0.02, 0.26, dot(soft, vec3(0.3, 0.55, 0.15)));
	float bump_near = 1.0 - smoothstep(25.0, 90.0, dist);
	t *= mix(1.0, mix(0.8, 1.04, h), 0.7 * bump_near * texture_mix * (1.0 - 0.6 * road));
	float bh = h * bump_strength * bump_near * texture_mix * (1.0 - 0.7 * max(road, yard));
	// Puddles: small, irregular, only where it's quite flat (the slope test is soft so mesh triangles don't show),
	// and the noise decides the shape. Water is smooth, so no relief inside them.
	// The slope only dims a puddle, gently: any sharp threshold on the (per-vertex) normal traces the mesh
	// triangles and gives square puddles. The outline comes from the noise alone.
	float flat_soft = mix(0.35, 1.0, smoothstep(0.7, 0.98, world_normal.y));
	float pshape = noise(p * 0.21 + 31.0) * 0.6 + noise(p * 0.9 + 7.0) * 0.4;
	float pth = mix(0.68, 0.56, max(road, yard * 0.5));   // roads and yards puddle first
	float puddle = smoothstep(pth, pth + 0.07, pshape) * flat_soft * smoothstep(0.45, 0.95, wetness) * smoothstep(0.5, 0.0, rock_w);
	bh *= 1.0 - puddle;
	vec3 dpdx = dFdx(VERTEX), dpdy = dFdy(VERTEX);
	float dhx = dFdx(bh), dhy = dFdy(bh);
	vec3 r1 = cross(dpdy, NORMAL), r2 = cross(NORMAL, dpdx);
	float det = dot(dpdx, r1);
	vec3 grad = sign(det) * (dhx * r1 + dhy * r2);
	NORMAL = normalize(abs(det) * NORMAL - grad);

	vec3 albedo = mix(painted, t, texture_mix);
	float rough = mix(0.95, 0.93 - 0.15 * wet_w, texture_mix);  // dry jungle floor is matte

	// Wet: darker (water fills the pores), glossier; puddles collect in flat spots, patchy.
	albedo *= mix(1.0, 0.66, wetness * (1.0 - rock_w * 0.4));
	rough = mix(rough, 0.62, wetness * (0.6 + 0.4 * h));    // wet: a soft sheen, strongest on the raised bits
	albedo = mix(albedo, albedo * 0.3 + vec3(0.008, 0.008, 0.006), puddle * 0.9);   // muddy water, not a mirror
	rough = mix(rough, 0.3, puddle);

	// Cloud shadow: big soft patches moving with the wind.
	vec2 cp = (p + cloud_shift) * 0.0016;
	float cn = noise(cp) * 0.6 + noise(cp * 2.3 + 5.0) * 0.3 + noise(cp * 5.1 + 9.0) * 0.1;
	float cs = smoothstep(1.0 - cloud_cover * 0.85, 1.0 - cloud_cover * 0.85 + 0.18, cn);
	albedo *= 1.0 - cloud_shadow * cs;

	ALBEDO = albedo;
	ROUGHNESS = rough;
	SPECULAR = mix(0.08, 0.35, puddle) + 0.05 * wetness * (1.0 - puddle);
}
