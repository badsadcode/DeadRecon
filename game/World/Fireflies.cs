using System;
using Godot;

namespace DeadRecon.World;

/// <summary>
/// Fireflies around you at dusk and night (none in the rain): little green-gold lights drifting low over the
/// undergrowth and blinking. Cheap: one particle system following the camera.
/// </summary>
public partial class Fireflies : GpuParticles3D
{
    public Camera3D Camera = null!;
    public BakedWorld World = null!;

    public override void _Ready()
    {
        Amount = 260;
        Lifetime = 7;
        Preprocess = 7;
        LocalCoords = false;
        VisibilityAabb = new Aabb(new Vector3(-40, -10, -40), new Vector3(80, 20, 80));
        CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
        var blink = new Gradient();
        blink.SetColor(0, new Color(1, 1, 1, 0));
        blink.AddPoint(.12f, new Color(1, 1, 1, 1));
        blink.AddPoint(.2f, new Color(1, 1, 1, .05f));
        blink.AddPoint(.45f, new Color(1, 1, 1, 0));
        blink.AddPoint(.55f, new Color(1, 1, 1, .9f));
        blink.AddPoint(.62f, new Color(1, 1, 1, 0));
        blink.AddPoint(.85f, new Color(1, 1, 1, 1));
        blink.SetColor(blink.GetPointCount() - 1, new Color(1, 1, 1, 0));
        ProcessMaterial = new ParticleProcessMaterial
        {
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Box, EmissionBoxExtents = new Vector3(30, 1.2f, 30),
            Gravity = Vector3.Zero, InitialVelocityMin = .05f, InitialVelocityMax = .3f, Spread = 180,
            TurbulenceEnabled = true, TurbulenceNoiseStrength = 1.2f, TurbulenceNoiseScale = 3, TurbulenceInfluenceMin = .05f, TurbulenceInfluenceMax = .12f,
            ColorRamp = new GradientTexture1D { Gradient = blink },
            ScaleMin = .7f, ScaleMax = 1.2f,
        };
        DrawPass1 = new QuadMesh
        {
            Size = new Vector2(.05f, .05f),
            Material = new StandardMaterial3D
            {
                AlbedoColor = new Color(.75f, 1f, .35f), ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha, BlendMode = BaseMaterial3D.BlendModeEnum.Add,
                BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles, VertexColorUseAsAlbedo = true,
                EmissionEnabled = true, Emission = new Color(.75f, 1f, .35f), EmissionEnergyMultiplier = 4,
                AlbedoTexture = DeadRecon.Combat.Effects.Puff(),
            },
        };
    }

    public override void _Process(double delta)
    {
        float dark = TimeOfDay.Current?.Darkness ?? 0, rain = Weather.Current?.Intensity ?? 0;
        float want = Math.Clamp((dark - .35f) / .4f, 0, 1) * Math.Clamp(1 - rain * 4, 0, 1);
        Emitting = want > .02f;
        AmountRatio = Math.Max(.01f, want);
        var c = Camera.GlobalPosition;
        GlobalPosition = new Vector3(c.X, World.HeightAt(c.X, c.Z) + 1.1f, c.Z);
    }
}
