using System;
using Godot;

namespace DeadRecon.World;

/// <summary>
/// The clock and all the light in the world: sun and moon, sky, fog, light shafts (volumetric fog), ambient and
/// exposure. Valheim's lesson: simple models, beautiful light. Weather feeds in as overcast/darkening and lightning.
/// <para>A day is <see cref="DayMinutes"/> real minutes. Colours come from keyframes through the day (night, dawn,
/// golden hour, noon, sunset, dusk) blended smoothly.</para>
/// </summary>
public partial class TimeOfDay : Node
{
    public Godot.Environment Environment = null!;
    public DirectionalLight3D Sun = null!, Moon = null!;
    public ShaderMaterial Sky = null!;

    public static TimeOfDay? Current { get; private set; }

    /// <summary>Hours, 0..24.</summary>
    public float Hour { get; set; } = 7;
    public float DayMinutes { get; set; } = 48;
    public bool Frozen { get; set; }
    /// <summary>0 broad daylight .. 1 deep night (for stealth and lights).</summary>
    public float Darkness { get; private set; }
    public string Clock => $"{(int)Hour:00}:{(int)((Hour % 1) * 60):00}";

    private record struct Key(float Hour, Color Top, Color Horizon, Color Ground, Color Fog, Color SunColor, float SunEnergy,
                              float Ambient, float FogDensity, float Mist, float Shafts, float Glow, float Exposure, float Stars);

    // Colours authored by eye. Mist = extra valley/height fog; Shafts = volumetric fog density (light beams).
    private static readonly Key[] Keys =
    {
        //    hour  sky top            horizon             ground              fog                 sun colour          sunE  amb   fogD    mist shafts glow exp  stars
        new(0,    C(.05f,.08f,.17f), C(.11f,.15f,.25f), C(.03f,.04f,.05f), C(.10f,.14f,.22f), C(.5f,.6f,.9f),    0,    .50f, .0009f, .5f, .008f, 0,   1.55f, 1),
        new(4.8f, C(.07f,.10f,.20f), C(.20f,.20f,.28f), C(.04f,.05f,.06f), C(.16f,.18f,.24f), C(.6f,.5f,.6f),    0,    .52f, .0011f, .9f, .012f, .1f, 1.5f, .8f),
        new(5.8f, C(.24f,.31f,.45f), C(.80f,.60f,.50f), C(.10f,.11f,.10f), C(.56f,.53f,.53f), C(1f,.62f,.38f),   .55f, .90f, .0014f, 1f,  .020f, .9f, 1.35f, 0),
        new(7.2f, C(.32f,.45f,.60f), C(.88f,.80f,.66f), C(.14f,.16f,.12f), C(.74f,.72f,.64f), C(1f,.83f,.60f),   1.25f, 1.30f, .0010f, .7f, .016f, .5f, 1.32f, 0),
        new(10,   C(.33f,.48f,.62f), C(.78f,.82f,.76f), C(.14f,.17f,.12f), C(.66f,.72f,.66f), C(1f,.95f,.86f),   1.35f, 1.40f, .0007f, .15f, .008f, .2f, 1.28f, 0),
        new(15,   C(.33f,.48f,.62f), C(.80f,.82f,.74f), C(.14f,.17f,.12f), C(.68f,.72f,.64f), C(1f,.93f,.82f),   1.3f, 1.40f,  .0007f, .15f, .008f, .25f, 1.28f, 0),
        new(17.3f,C(.30f,.42f,.58f), C(.90f,.76f,.58f), C(.14f,.15f,.11f), C(.74f,.68f,.58f), C(1f,.76f,.48f),   1.15f, 1.20f, .0009f, .4f, .018f, .65f, 1.3f, 0),
        new(18.4f,C(.22f,.24f,.40f), C(.88f,.55f,.42f), C(.10f,.09f,.09f), C(.58f,.48f,.45f), C(1f,.52f,.28f),   .5f,  .90f, .0012f, .6f, .020f, .95f, 1.3f, 0),
        new(19.4f,C(.09f,.10f,.22f), C(.30f,.24f,.32f), C(.05f,.05f,.06f), C(.22f,.20f,.28f), C(.6f,.4f,.5f),    0,    .52f, .0010f, .5f, .012f, .3f, 1.45f, .5f),
        new(21,   C(.05f,.08f,.17f), C(.11f,.15f,.25f), C(.03f,.04f,.05f), C(.10f,.14f,.22f), C(.5f,.6f,.9f),    0,    .50f, .0009f, .5f, .008f, 0,   1.55f, 1),
        new(24,   C(.05f,.08f,.17f), C(.11f,.15f,.25f), C(.03f,.04f,.05f), C(.10f,.14f,.22f), C(.5f,.6f,.9f),    0,    .50f, .0009f, .5f, .008f, 0,   1.55f, 1),
    };
    private static Color C(float r, float g, float b) => new(r, g, b);

    public override void _Ready()
    {
        Current = this;
        Environment.VolumetricFogEnabled = DeadRecon.UI.Settings.LightShafts;
        Environment.VolumetricFogAnisotropy = .55f;          // forward scattering: beams glow toward the sun
        Environment.VolumetricFogLength = 64;          // shorter: more of the grid close by, where the shafts are
        Environment.VolumetricFogDetailSpread = 2.2f;
        Environment.VolumetricFogAmbientInject = .15f;
        Environment.TonemapMode = Godot.Environment.ToneMapper.Aces;
        Environment.SsaoIntensity = 1.5f;   // gentler contact shadows: under the canopy everything is already in shade
        Environment.TonemapWhite = 6;
        Environment.GlowEnabled = true;
        // Bloom: gentle, and from a soft threshold (a hard one makes things pop into glowing as they cross it).
        Environment.GlowIntensity = .4f;
        Environment.GlowBloom = .02f;
        Environment.GlowHdrThreshold = 1.6f;
        Environment.GlowHdrScale = 1.2f;
        Sun.LightVolumetricFogEnergy = 2.2f;
        Moon.LightVolumetricFogEnergy = 1.2f;
        Apply(0);
    }

    public override void _ExitTree() { if (Current == this) Current = null; }

    public override void _Process(double delta)
    {
        if (!Frozen) Hour = (Hour + (float)delta * 24 / (DayMinutes * 60)) % 24;
        Apply((float)delta);
    }

    private Key Sample(float h)
    {
        int i = 0;
        while (i < Keys.Length - 2 && Keys[i + 1].Hour <= h) i++;
        var a = Keys[i]; var b = Keys[i + 1];
        float t = Mathf.SmoothStep(0, 1, (h - a.Hour) / Math.Max(.01f, b.Hour - a.Hour));
        Color L(Color x, Color y) => x.Lerp(y, t);
        float F(float x, float y) => Mathf.Lerp(x, y, t);
        return new Key(h, L(a.Top, b.Top), L(a.Horizon, b.Horizon), L(a.Ground, b.Ground), L(a.Fog, b.Fog), L(a.SunColor, b.SunColor),
                       F(a.SunEnergy, b.SunEnergy), F(a.Ambient, b.Ambient), F(a.FogDensity, b.FogDensity), F(a.Mist, b.Mist),
                       F(a.Shafts, b.Shafts), F(a.Glow, b.Glow), F(a.Exposure, b.Exposure), F(a.Stars, b.Stars));
    }

    private void Apply(float dt)
    {
        var k = Sample(Hour);
        var weather = Weather.Current;
        float rain = weather?.Intensity ?? 0, flash = weather?.Flash ?? 0;
        float bright = Math.Clamp(k.Ambient / 1.4f, 0, 1);                // how lit the world is now (for the overcast grey)

        // Sun: rises in the east (+X), arcs over the north side a little (-Z), sets in the west.
        float dayT = (Hour - 6) / 12f * MathF.PI;                        // 0 at 06:00, PI at 18:00
        float elev = MathF.Sin(dayT) * 68;                                // degrees above horizon
        float azim = 90 - (Hour - 6) / 12f * 180;                         // east (90) to west (-90)
        Sun.RotationDegrees = new Vector3(-elev, azim + 180, 0);
        Sun.LightColor = k.SunColor;
        Sun.LightEnergy = k.SunEnergy * (1 - .7f * rain) * Mathf.SmoothStep(-2, 4, elev);
        Sun.Visible = elev > -4;
        // Shadows stay on until the sun is really gone (it's dim by then). Switching them off at 2 degrees, while the
        // volumetric fog was still lit, made the whole forest flare up at once: the "boom" at sunset.
        Sun.ShadowEnabled = elev > -1.5f;
        Sun.LightSpecular = 1 - .9f * rain;
        Sun.SkyMode = rain > .35f ? DirectionalLight3D.SkyModeEnum.LightOnly : DirectionalLight3D.SkyModeEnum.LightAndSky;
        // Moon: opposite the sun, cold and dim; enough to move by, not enough to read a map.
        Moon.RotationDegrees = new Vector3(elev - 8, azim, 0);
        float moonUp = Mathf.SmoothStep(0, 8, -elev);
        Moon.LightEnergy = .45f * moonUp * (1 - .8f * rain);
        Moon.Visible = moonUp > .01f;
        Moon.ShadowEnabled = moonUp > .3f;

        Darkness = Math.Clamp(1 - k.SunEnergy / .9f, 0, 1);

        // Sky.
        var grey = new Color(.42f, .45f, .47f) * Mathf.Lerp(.3f, 1, bright);
        Sky.SetShaderParameter("top_color", k.Top);
        Sky.SetShaderParameter("horizon_color", k.Horizon);
        Sky.SetShaderParameter("ground_color", k.Ground);
        Sky.SetShaderParameter("sun_glow_color", k.SunColor);
        Sky.SetShaderParameter("sun_glow", k.Glow);
        Sky.SetShaderParameter("sun_disc", 1 - Math.Clamp(rain * 2.5f, 0, 1));
        Sky.SetShaderParameter("stars", k.Stars * (1 - Math.Clamp(rain * 3, 0, 1)));
        Sky.SetShaderParameter("overcast", Math.Clamp(rain * 1.1f, 0, 1) * .55f);   // the clouds do most of the work now
        Sky.SetShaderParameter("overcast_color", grey);
        Sky.SetShaderParameter("flash", flash * .9f);
        Clouds(k, dt, rain, bright, flash);

        // Air: distance fog takes the sky's horizon colour; mist pools in valleys at dawn; light shafts in the trees.
        var fog = k.Fog.Lerp(grey * 1.05f, rain * .9f).Lerp(new Color(.85f, .88f, 1f), flash * .8f);
        Environment.FogLightColor = fog;
        weather?.SetRainLight(fog.Lerp(new Color(.9f, .92f, 1f), flash), Math.Clamp(bright + flash, 0, 1));
        Environment.FogDensity = k.FogDensity * (1 + 2.2f * rain);
        Environment.FogHeightDensity = .0015f + .004f * k.Mist;
        Environment.VolumetricFogEnabled = DeadRecon.UI.Settings.LightShafts;
        Environment.VolumetricFogDensity = k.Shafts * .7f * (1 + .2f * rain);   // thicker fog darkens everything: the god rays (post) do the beams
        // Low sun: less (a golden-hour sun lights the whole fog volume orange, even behind you).
        Sun.LightVolumetricFogEnergy = Mathf.Lerp(1.1f, 2f, Mathf.SmoothStep(10, 40, elev)) * Mathf.SmoothStep(0, 6, elev) * (1 - .85f * rain);           // no sunbeams through a storm (else: red murk at dusk)
        Environment.VolumetricFogAlbedo = fog.Lerp(Colors.White, .65f).Lerp(new Color(.6f, .64f, .68f), rain * .7f);   // mostly neutral: the light colours it, not the air
        Environment.AmbientLightEnergy = k.Ambient * (1 - .12f * rain) + flash * 3;
        Environment.TonemapExposure = k.Exposure * (1 + .12f * rain);
        Grade(k, elev, rain);   // eyes adapt to the gloom a little
    }

    /// <summary>The grading pass (post.gdshader): haze glow and god rays from the sun, split toning by time and weather.</summary>
    private void Grade(Key k, float elev, float rain)
    {
        if (Atmosphere.Current?.Post is not { } post) return;
        float dark = Darkness, low = 1 - Mathf.SmoothStep(8, 35, elev), up = Mathf.SmoothStep(-2, 4, elev);
        float wet = Math.Clamp(rain * 1.3f, 0, 1);
        post.SetShaderParameter("sun_dir", Sun.GlobalTransform.Basis.Z);
        post.SetShaderParameter("sun_color", k.SunColor * Math.Clamp(k.SunEnergy, 0, 1.4f));
        post.SetShaderParameter("haze", (.1f + .2f * low) * up * (1 - wet));
        post.SetShaderParameter("rays", (.12f + .16f * low) * up * (1 - wet) * (DeadRecon.UI.Settings.LightShafts ? 1 : 0));
        // Day: cool shadows, warm light. Golden hour: warmer. Night: blue and muted. Rain: grey and flat.
        var shadow = new Vector3(.94f, 1f, 1.07f).Lerp(new Vector3(.88f, .96f, 1.16f), dark);
        var high = new Vector3(1.04f, 1f, .93f).Lerp(new Vector3(1.1f, .99f, .82f), low * up).Lerp(new Vector3(.97f, 1f, 1.06f), dark);
        post.SetShaderParameter("shadow_tint", shadow.Lerp(new Vector3(.98f, 1f, 1.02f), wet));
        post.SetShaderParameter("highlight_tint", high.Lerp(new Vector3(1f, 1f, 1f), wet));
        post.SetShaderParameter("saturation", Mathf.Lerp(1.12f, .82f, dark) * Mathf.Lerp(1, .86f, wet));
        post.SetShaderParameter("contrast", Mathf.Lerp(1.07f, 1.02f, wet));
        post.SetShaderParameter("vignette", .18f + .1f * dark);
        post.SetShaderParameter("lift", .012f * (1 - dark * .7f));
    }

    private Vector2 _cloudShift = new(13.7f, 4.2f);

    /// <summary>
    /// The cloud deck: drifts with the wind (faster in a storm), covers more of the sky as rain comes, lit by the sun's
    /// colour (orange at dusk) or the moon's grey-blue at night. Its shadows cross the ground (global shader params).
    /// </summary>
    private void Clouds(Key k, float dt, float rain, float bright, float flash)
    {
        var wind = new Vector2(.0028f, .0011f) * (1 + 2.5f * rain);
        _cloudShift += wind * dt;
        // Fair-weather cover wanders through the day; rain brings a full deck.
        float fair = .3f + .22f * (.5f + .5f * MathF.Sin(Hour * .9f + 1.3f)) + .1f * MathF.Sin(Hour * 2.3f);
        float cover = Mathf.Lerp(fair, 1, Math.Clamp(rain * 1.3f, 0, 1));
        float day = Math.Clamp(k.SunEnergy / 1.1f, 0, 1);
        var lit = k.SunColor.Lerp(Colors.White, .35f) * (.75f + .35f * day);
        lit = new Color(.17f, .2f, .28f).Lerp(lit, Mathf.SmoothStep(0, .35f, day));
        var shade = (k.Horizon * .5f + k.Top * .3f) * (.55f + .45f * day);
        var storm = new Color(.36f, .38f, .40f) * Mathf.Lerp(.25f, 1, bright);
        lit = lit.Lerp(storm * 1.25f, rain * .85f).Lerp(new Color(.9f, .92f, 1f), flash * .8f);
        shade = shade.Lerp(storm * .7f, rain * .85f).Lerp(new Color(.7f, .75f, .9f), flash * .6f);
        Sky.SetShaderParameter("cloud_offset", _cloudShift);
        Sky.SetShaderParameter("cloud_cover", cover);
        Sky.SetShaderParameter("cloud_lit", lit);
        Sky.SetShaderParameter("cloud_shade", shade);
        RenderingServer.GlobalShaderParameterSet("cloud_shift", _cloudShift * 900);   // sky units to metres on the ground (roughly)
        RenderingServer.GlobalShaderParameterSet("cloud_cover", cover);
        // Shadows only show in sunlight, and not under a full grey deck (it's all shade then).
        RenderingServer.GlobalShaderParameterSet("cloud_shadow", .3f * Math.Clamp(k.SunEnergy, 0, 1) * (1 - Math.Clamp(rain * 1.5f, 0, 1)));
    }

    /// <summary>A starting hour for an operation, from its seed: mostly daytime, sometimes dawn, dusk or night.</summary>
    public static float StartHourFor(int seed)
    {
        var rng = new RandomNumberGenerator { Seed = (ulong)seed * 977 + 3 };
        float r = rng.Randf();
        return r < .15f ? 5.6f : r < .55f ? rng.RandfRange(8, 11) : r < .8f ? rng.RandfRange(13, 16) : r < .92f ? 17.6f : 21.5f;
    }

    public static string Describe(float hour) => hour switch
    {
        < 5 => "night", < 6.5f => "dawn", < 11 => "morning", < 14 => "midday", < 17 => "afternoon",
        < 19 => "dusk", _ => "night",
    };
}
