using System;
using Godot;

namespace DeadRecon.World;

/// <summary>
/// Things in the air round you: dust and pollen drifting in the sunlight, leaves coming down from the canopy (more in
/// wind and rain), and low mist hanging over the ground at dawn, dusk and in the rain. Plus the final grading pass
/// (post.gdshader) on a quad in front of the camera, which TimeOfDay drives.
/// </summary>
public partial class Atmosphere : Node3D
{
    public Camera3D Camera = null!;
    public BakedWorld World = null!;

    public static Atmosphere? Current { get; private set; }
    public ShaderMaterial Post { get; private set; } = null!;

    private GpuParticles3D _dust = null!, _leaves = null!, _mist = null!;
    private StandardMaterial3D _mistMat = null!;

    public override void _Ready()
    {
        Current = this;
        // The grading quad: always in front of the camera, never culled.
        Post = new ShaderMaterial { Shader = GD.Load<Shader>("res://game/World/post.gdshader"), RenderPriority = -128 };
        var quad = new MeshInstance3D
        {
            Mesh = new QuadMesh { Size = new Vector2(2, 2), FlipFaces = true }, MaterialOverride = Post, ExtraCullMargin = 16384,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, GIMode = GeometryInstance3D.GIModeEnum.Disabled,
            Position = new Vector3(0, 0, -.5f),
        };
        Camera.AddChild(quad);

        var puff = DeadRecon.Combat.Effects.Puff();
        // Dust motes: tiny, bright where the sun catches them.
        _dust = Particles(160, 9, new ParticleProcessMaterial
        {
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Box, EmissionBoxExtents = new Vector3(9, 4, 9),
            Gravity = new Vector3(0, -.01f, 0), InitialVelocityMin = .02f, InitialVelocityMax = .12f, Spread = 180,
            TurbulenceEnabled = true, TurbulenceNoiseStrength = .6f, TurbulenceNoiseScale = 2.5f,
            ScaleMin = .4f, ScaleMax = 1f, ColorRamp = new GradientTexture1D { Gradient = FadeInOut(.6f) },
        }, new QuadMesh { Size = new Vector2(.022f, .022f) }, new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.PerPixel, Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles, VertexColorUseAsAlbedo = true, AlbedoTexture = puff,
            AlbedoColor = new Color(1, .97f, .88f), EmissionEnabled = true, Emission = new Color(.25f, .23f, .18f), BacklightEnabled = true, Backlight = new Color(.8f, .75f, .6f),
        });
        // Leaves: brown and green flakes, tumbling down.
        var leafColours = new Gradient();
        leafColours.SetColor(0, new Color(.42f, .32f, .14f));
        leafColours.AddPoint(.5f, new Color(.35f, .42f, .16f));
        leafColours.SetColor(leafColours.GetPointCount() - 1, new Color(.5f, .38f, .18f));
        _leaves = Particles(26, 9, new ParticleProcessMaterial
        {
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Box, EmissionBoxExtents = new Vector3(14, 1, 14), Direction = Vector3.Down,
            Gravity = new Vector3(.25f, -.45f, .1f), InitialVelocityMin = .1f, InitialVelocityMax = .4f, Spread = 40,
            AngularVelocityMin = -160, AngularVelocityMax = 160, AngleMin = 0, AngleMax = 360,
            TurbulenceEnabled = true, TurbulenceNoiseStrength = 1.6f, TurbulenceNoiseScale = 1.5f,
            ScaleMin = .7f, ScaleMax = 1.3f, ColorInitialRamp = new GradientTexture1D { Gradient = leafColours },
        }, new QuadMesh { Size = new Vector2(.09f, .06f) }, new StandardMaterial3D
        {
            Transparency = BaseMaterial3D.TransparencyEnum.AlphaScissor, CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles, BillboardKeepScale = true, VertexColorUseAsAlbedo = true,
            AlbedoTexture = LeafTexture(), Roughness = .8f, BacklightEnabled = true, Backlight = new Color(.3f, .35f, .1f),
        });
        _leaves.Position = new Vector3(0, 9, 0);
        // Ground mist: big soft sheets low over the ground.
        _mistMat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.PerPixel, Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles, VertexColorUseAsAlbedo = true, AlbedoTexture = puff,
            AlbedoColor = new Color(.85f, .88f, .9f, .1f), Roughness = 1, DisableReceiveShadows = false, ProximityFadeEnabled = true, ProximityFadeDistance = 1.5f,
        };
        _mist = Particles(70, 18, new ParticleProcessMaterial
        {
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Box, EmissionBoxExtents = new Vector3(40, .4f, 40),
            Gravity = Vector3.Zero, InitialVelocityMin = .05f, InitialVelocityMax = .25f, Spread = 180, Direction = Vector3.Right,
            ScaleMin = .7f, ScaleMax = 1.4f, ColorRamp = new GradientTexture1D { Gradient = FadeInOut(1) },
        }, new QuadMesh { Size = new Vector2(9, 3.2f) }, _mistMat);
    }

    public override void _ExitTree() { if (Current == this) Current = null; }

    private GpuParticles3D Particles(int amount, float life, ParticleProcessMaterial process, QuadMesh mesh, Material mat)
    {
        mesh.Material = mat;
        var p = new GpuParticles3D
        {
            Amount = amount, Lifetime = life, Preprocess = life, LocalCoords = false, ProcessMaterial = process, DrawPass1 = mesh,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, VisibilityAabb = new Aabb(new Vector3(-60, -20, -60), new Vector3(120, 40, 120)),
        };
        AddChild(p);
        return p;
    }

    private static Gradient FadeInOut(float peak)
    {
        var g = new Gradient();
        g.SetColor(0, new Color(1, 1, 1, 0));
        g.AddPoint(.25f, new Color(1, 1, 1, peak));
        g.AddPoint(.75f, new Color(1, 1, 1, peak));
        g.SetColor(g.GetPointCount() - 1, new Color(1, 1, 1, 0));
        return g;
    }

    private static ImageTexture LeafTexture()
    {
        const int S = 32;
        var img = Image.CreateEmpty(S, S, false, Image.Format.Rgba8);
        for (int y = 0; y < S; y++)
            for (int x = 0; x < S; x++)
            {
                float u = (x + .5f) / S * 2 - 1, v = (y + .5f) / S * 2 - 1;
                bool inside = u * u + (v * v) * 3.2f < 1 - .25f * MathF.Abs(u);   // a pointed oval
                float vein = MathF.Abs(v) < .05f ? .8f : 1;
                img.SetPixel(x, y, inside ? new Color(vein, vein, vein, 1) : new Color(0, 0, 0, 0));
            }
        img.GenerateMipmaps();
        return ImageTexture.CreateFromImage(img);
    }

    public override void _Process(double delta)
    {
        var c = Camera.GlobalPosition;
        float ground = World.HeightAt(c.X, c.Z);
        float dark = TimeOfDay.Current?.Darkness ?? 0, rain = Weather.Current?.Intensity ?? 0;
        float hour = TimeOfDay.Current?.Hour ?? 12;
        _dust.GlobalPosition = c;
        _dust.AmountRatio = Math.Max(.02f, (1 - dark) * (1 - rain));
        _dust.Emitting = dark < .9f && rain < .6f;
        _leaves.GlobalPosition = new Vector3(c.X, Math.Max(c.Y, ground) + 9, c.Z);
        _leaves.AmountRatio = .4f + .6f * rain;
        _leaves.SpeedScale = 1 + rain;
        // Mist: thickest around dawn and dusk, and in the rain; a little through the night.
        float dawn = Math.Clamp(1 - MathF.Abs(hour - 6.5f) / 2.2f, 0, 1), dusk = Math.Clamp(1 - MathF.Abs(hour - 19) / 2f, 0, 1);
        float mist = Math.Clamp(Math.Max(dawn, dusk) + .7f * rain + .25f * dark, 0, 1);
        _mist.GlobalPosition = new Vector3(c.X, ground + 1.2f, c.Z);
        _mist.Emitting = mist > .05f;
        _mistMat.AlbedoColor = new Color(.86f, .88f, .9f, .11f * mist);
    }
}
