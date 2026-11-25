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
        Environment.VolumetricFogAnisotropy = .7f;          // forward scattering: beams glow toward the sun
        Environment.VolumetricFogLength = 90;
        Environment.VolumetricFogDetailSpread = 1.6f;
        Environment.VolumetricFogAmbientInject = .15f;
        Environment.TonemapMode = Godot.Environment.ToneMapper.Aces;
        Environment.SsaoIntensity = 1.5f;   // gentler contact shadows: under the canopy everything is already in shade
        Environment.TonemapWhite = 6;
        Environment.GlowEnabled = true;
        Environment.GlowIntensity = .55f;
        Environment.GlowBloom = .08f;
        Environment.GlowHdrThreshold = 1.1f;
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
        Sun.ShadowEnabled = elev > 2;
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
        Sky.SetShaderParameter("overcast", Math.Clamp(rain * 1.1f, 0, 1));
        Sky.SetShaderParameter("overcast_color", grey);
        Sky.SetShaderParameter("flash", flash * .9f);

        // Air: distance fog takes the sky's horizon colour; mist pools in valleys at dawn; light shafts in the trees.
        var fog = k.Fog.Lerp(grey * 1.05f, rain * .9f).Lerp(new Color(.85f, .88f, 1f), flash * .8f);
        Environment.FogLightColor = fog;
        Environment.FogDensity = k.FogDensity * (1 + 2.2f * rain);
        Environment.FogHeightDensity = .0015f + .004f * k.Mist;
        Environment.VolumetricFogEnabled = DeadRecon.UI.Settings.LightShafts;
        Environment.VolumetricFogDensity = k.Shafts * (1 + .2f * rain);
        Sun.LightVolumetricFogEnergy = 2.2f * (1 - .85f * rain);           // no sunbeams through a storm (else: red murk at dusk)
        Environment.VolumetricFogAlbedo = fog.Lerp(Colors.White, .65f).Lerp(new Color(.6f, .64f, .68f), rain * .7f);   // mostly neutral: the light colours it, not the air
        Environment.AmbientLightEnergy = k.Ambient * (1 - .12f * rain) + flash * 3;
        Environment.TonemapExposure = k.Exposure * (1 + .12f * rain);   // eyes adapt to the gloom a little
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
