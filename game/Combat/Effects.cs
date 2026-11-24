using System;
using System.Collections.Generic;
using Godot;

namespace DeadRecon.Combat;

/// <summary>Anything bullets can hurt (the guard, the player).</summary>
public interface IDamageable
{
    /// <param name="at">World hit point.</param>
    /// <param name="from">Where the shot came from (the shooter's position).</param>
    void TakeHit(float damage, Vector3 at, Vector3 from);
}

/// <summary>
/// Things the AI can hear. Anyone making noise calls <see cref="Make"/>; guards subscribe.
/// <c>radius</c> is how far away (m) it can be heard at all.
/// </summary>
public static class Hearing
{
    public static event Action<Vector3, float, Node>? Made;

    /// <summary>0..1 how much rain is covering sounds (set by Weather).</summary>
    public static float Muffle { get; set; }

    /// <summary>How far a sound of this nominal radius carries right now: rain hides footsteps far more than gunfire.</summary>
    public static float Carry(float radius) =>
        radius * (1 - Muffle * (radius >= Gunshot ? .35f : radius < 20 ? .75f : .3f));

    public static void Make(Vector3 at, float radius, Node source) => Made?.Invoke(at, Carry(radius), source);

    public const float Gunshot = 260, RunningSteps = 14, WalkingSteps = 6;
}

/// <summary>Shared placeholder effects: bullet impacts, muzzle-flash material, synthesised sounds.</summary>
public static class Effects
{
    private static readonly Queue<Node3D> Holes = new();
    private static StandardMaterial3D? _flash, _hole, _dust;
    private static AudioStreamWav? _gunshot;

    /// <summary>Bullet hole + dust puff where a shot hit the ground.</summary>
    public static void Impact(Node context, Vector3 at, Vector3 normal)
    {
        var scene = context.GetTree().CurrentScene;
        var up = normal.Normalized();
        var side = Math.Abs(up.Y) < .9f ? up.Cross(Vector3.Up).Normalized() : up.Cross(Vector3.Right).Normalized();
        var basis = new Basis(side, up, side.Cross(up));

        _hole ??= new StandardMaterial3D { AlbedoColor = new Color(.06f, .05f, .04f), Roughness = 1 };
        var hole = new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = .03f, BottomRadius = .03f, Height = .004f, RadialSegments = 8, Rings = 0 },
            MaterialOverride = _hole, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        scene.AddChild(hole);
        hole.GlobalTransform = new Transform3D(basis, at + up * .003f);
        Holes.Enqueue(hole);
        while (Holes.Count > 80) { var old = Holes.Dequeue(); if (GodotObject.IsInstanceValid(old)) old.QueueFree(); }

        _dust ??= new StandardMaterial3D
        {
            AlbedoColor = new Color(.38f, .30f, .20f, .8f), Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        };
        var dust = new CpuParticles3D
        {
            Emitting = true, OneShot = true, Amount = 14, Lifetime = .9f, Explosiveness = .95f,
            Direction = Vector3.Up, Spread = 35, InitialVelocityMin = 1.2f, InitialVelocityMax = 3f, Gravity = new Vector3(0, -3, 0),
            ScaleAmountMin = .5f, ScaleAmountMax = 1.2f, Mesh = new QuadMesh { Size = new Vector2(.09f, .09f) }, MaterialOverride = _dust,
        };
        scene.AddChild(dust);
        dust.GlobalTransform = new Transform3D(basis, at);
        context.GetTree().CreateTimer(1.5).Timeout += dust.QueueFree;
    }

    private static StandardMaterial3D? _splinter, _bark;
    private static AudioStreamWav? _thock, _crash;
    private static readonly Queue<Node3D> BarkMarks = new();

    /// <summary>A bullet hits wood: pale bark mark on the trunk, a burst of splinters, a dull "thock".</summary>
    public static void Splinters(Node context, Vector3 at, Vector3 normal, Vector3 bulletDir)
    {
        var scene = context.GetTree().CurrentScene;
        var n = normal.Normalized();
        var side = Math.Abs(n.Y) < .9f ? n.Cross(Vector3.Up).Normalized() : n.Cross(Vector3.Right).Normalized();
        var basis = new Basis(side, n, side.Cross(n));

        _bark ??= new StandardMaterial3D { AlbedoColor = new Color(.78f, .66f, .45f), Roughness = 1 };
        var mark = new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = .04f, BottomRadius = .05f, Height = .01f, RadialSegments = 7, Rings = 0 },
            MaterialOverride = _bark, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        scene.AddChild(mark);
        mark.GlobalTransform = new Transform3D(basis, at + n * .004f);
        BarkMarks.Enqueue(mark);
        while (BarkMarks.Count > 120) { var old = BarkMarks.Dequeue(); if (GodotObject.IsInstanceValid(old)) old.QueueFree(); }

        _splinter ??= new StandardMaterial3D
        {
            AlbedoColor = new Color(.72f, .58f, .38f), BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        };
        // Splinters fly back out of the hole, roughly mirrored around the trunk surface.
        var outDir = (n * 1.2f - bulletDir.Normalized() * .4f).Normalized();
        var burst = new CpuParticles3D
        {
            Emitting = true, OneShot = true, Amount = 22, Lifetime = .8f, Explosiveness = 1,
            Direction = Vector3.Up, Spread = 40, InitialVelocityMin = 2.5f, InitialVelocityMax = 6f, Gravity = new Vector3(0, -9, 0),
            ScaleAmountMin = .4f, ScaleAmountMax = 1.3f, Mesh = new QuadMesh { Size = new Vector2(.025f, .07f) },
            MaterialOverride = _splinter, AngularVelocityMin = -720, AngularVelocityMax = 720,
        };
        scene.AddChild(burst);
        var up = outDir;
        var s2 = Math.Abs(up.Y) < .9f ? up.Cross(Vector3.Up).Normalized() : up.Cross(Vector3.Right).Normalized();
        burst.GlobalTransform = new Transform3D(new Basis(s2, up, s2.Cross(up)), at);
        context.GetTree().CreateTimer(1.2).Timeout += burst.QueueFree;

        _thock ??= Thock();
        var sound = new AudioStreamPlayer3D { Stream = _thock, UnitSize = 6, MaxDistance = 120, PitchScale = (float)GD.RandRange(.85, 1.15) };
        scene.AddChild(sound);
        sound.GlobalPosition = at;
        sound.Play();
        sound.Finished += sound.QueueFree;
    }

    /// <summary>Creak, crack and a long crashing rumble: a tree coming down.</summary>
    public static AudioStreamWav TreeCrash()
    {
        if (_crash != null) return _crash;
        const int Rate = 44100;
        int n = (int)(Rate * 3.2f);
        var data = new byte[n * 2];
        var rng = new Random(1912);
        float low = 0, mid = 0;
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Rate;
            float noise = (float)rng.NextDouble() * 2 - 1;
            low += (noise - low) * .015f;
            mid += (noise - mid) * .12f;
            float creak = t < .9f ? MathF.Sin(MathF.Tau * (180 + 60 * MathF.Sin(t * 9)) * t) * .25f * (t / .9f) : 0;
            float crack = t is > .85f and < 1.05f ? mid * 2.2f * MathF.Exp(-(t - .85f) * 25) : 0;
            float crash = t > 1.9f ? (low * 9 + mid * .8f) * MathF.Exp(-(t - 1.9f) * 2.2f) : 0;
            float s = Math.Clamp(creak + crack + crash, -1, 1);
            short v = (short)(s * 28000);
            data[i * 2] = (byte)v; data[i * 2 + 1] = (byte)(v >> 8);
        }
        return _crash = Wav(data, Rate);
    }

    private static AudioStreamWav Thock()
    {
        const int Rate = 44100;
        int n = (int)(Rate * .12f);
        var data = new byte[n * 2];
        var rng = new Random(3);
        float lp = 0;
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Rate;
            lp += (((float)rng.NextDouble() * 2 - 1) - lp) * .25f;
            float s = (MathF.Sin(MathF.Tau * 320 * t) * .6f + lp * .8f) * MathF.Exp(-t * 45);
            short v = (short)(Math.Clamp(s, -1, 1) * 24000);
            data[i * 2] = (byte)v; data[i * 2 + 1] = (byte)(v >> 8);
        }
        return Wav(data, Rate);
    }

    private static StandardMaterial3D? _mist, _drops;
    private static AudioStreamWav? _thud;
    private static GradientTexture2D? _puff;

    /// <summary>
    /// A bullet hits a person: a short puff of red mist blown along the shot, a few droplets, and a dull thud.
    /// Kept brief and dark: readable feedback, not gore.
    /// </summary>
    public static void BloodHit(Node context, Vector3 at, Vector3 shotDir, bool sound = true)
    {
        var scene = context.GetTree().CurrentScene;
        var dir = shotDir.LengthSquared() > 1e-6f ? shotDir.Normalized() : Vector3.Forward;
        var side = Math.Abs(dir.Y) < .9f ? dir.Cross(Vector3.Up).Normalized() : dir.Cross(Vector3.Right).Normalized();
        var basis = new Basis(side, dir, side.Cross(dir)); // particles' "up" = along the shot

        _puff ??= SoftPuff();
        _mist ??= new StandardMaterial3D
        {
            AlbedoTexture = _puff, AlbedoColor = new Color(.42f, .03f, .03f, .55f), Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles,
            VertexColorUseAsAlbedo = true,
        };
        var fade = new Gradient();
        fade.SetColor(0, new Color(1, 1, 1, 1));
        fade.SetColor(1, new Color(1, 1, 1, 0));
        var mist = new CpuParticles3D
        {
            Emitting = true, OneShot = true, Amount = 10, Lifetime = .45f, Explosiveness = .9f,
            Direction = Vector3.Up, Spread = 28, InitialVelocityMin = .8f, InitialVelocityMax = 2.2f, Gravity = new Vector3(0, -.6f, 0),
            ScaleAmountMin = .25f, ScaleAmountMax = .6f, Mesh = new QuadMesh { Size = new Vector2(.6f, .6f) }, MaterialOverride = _mist,
            ColorRamp = fade, DampingMin = 3, DampingMax = 5,
        };
        scene.AddChild(mist);
        mist.GlobalTransform = new Transform3D(basis, at);

        _drops ??= new StandardMaterial3D
        {
            AlbedoColor = new Color(.3f, .02f, .02f), ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles,
        };
        var drops = new CpuParticles3D
        {
            Emitting = true, OneShot = true, Amount = 8, Lifetime = .6f, Explosiveness = 1,
            Direction = Vector3.Up, Spread = 22, InitialVelocityMin = 2, InitialVelocityMax = 4, Gravity = new Vector3(0, -9.8f, 0),
            ScaleAmountMin = .6f, ScaleAmountMax = 1.2f, Mesh = new QuadMesh { Size = new Vector2(.025f, .025f) }, MaterialOverride = _drops,
        };
        scene.AddChild(drops);
        drops.GlobalTransform = new Transform3D(basis, at);
        context.GetTree().CreateTimer(1).Timeout += () => { mist.QueueFree(); drops.QueueFree(); };

        if (!sound) return;
        _thud ??= Thud();
        var p = new AudioStreamPlayer3D { Stream = _thud, UnitSize = 7, MaxDistance = 90, PitchScale = (float)GD.RandRange(.85, 1.1), VolumeDb = 3 };
        scene.AddChild(p);
        p.GlobalPosition = at;
        p.Play();
        p.Finished += p.QueueFree;
    }

    /// <summary>The thud for the player's own hits (not positional).</summary>
    public static AudioStreamWav ThudSound() => _thud ??= Thud();

    /// <summary>A heavy, dull impact: a low sine knock with a muffled noise burst.</summary>
    private static AudioStreamWav Thud()
    {
        const int Rate = 44100;
        int n = (int)(Rate * .22f);
        var data = new byte[n * 2];
        var rng = new Random(55);
        float lp = 0;
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Rate;
            lp += (((float)rng.NextDouble() * 2 - 1) - lp) * .08f;
            float knock = MathF.Sin(MathF.Tau * (95 - 40 * t / .22f) * t) * MathF.Exp(-t * 22);
            float s = Math.Clamp((knock * .9f + lp * 2.2f * MathF.Exp(-t * 40)) * Math.Min(1, t / .002f), -1, 1);
            short v = (short)(s * 28000);
            data[i * 2] = (byte)v; data[i * 2 + 1] = (byte)(v >> 8);
        }
        return Wav(data, Rate);
    }

    private static GradientTexture2D SoftPuff()
    {
        var g = new Gradient();
        g.SetColor(0, new Color(1, 1, 1, 1));
        g.AddPoint(.5f, new Color(1, 1, 1, .45f));
        g.SetColor(g.GetPointCount() - 1, new Color(1, 1, 1, 0));
        return new GradientTexture2D { Gradient = g, Fill = GradientTexture2D.FillEnum.Radial, FillFrom = new Vector2(.5f, .5f), FillTo = new Vector2(1, .5f), Width = 64, Height = 64 };
    }

    /// <summary>Additive star-shaped flash, camera-facing.</summary>
    public static StandardMaterial3D FlashMaterial()
    {
        if (_flash != null) return _flash;
        const int N = 64;
        var px = new byte[N * N * 4];
        for (int y = 0; y < N; y++)
        for (int x = 0; x < N; x++)
        {
            float dx = (x + .5f) / N * 2 - 1, dy = (y + .5f) / N * 2 - 1;
            float r = MathF.Sqrt(dx * dx + dy * dy), ang = MathF.Atan2(dy, dx);
            float star = MathF.Pow(MathF.Abs(MathF.Cos(ang * 2.5f)), 6) * .6f + .4f;
            float a = Math.Clamp(1 - r / star, 0, 1);
            a *= a;
            int o = (y * N + x) * 4;
            px[o] = 255; px[o + 1] = (byte)(200 * a + 55); px[o + 2] = (byte)(120 * a); px[o + 3] = (byte)(255 * a);
        }
        var tex = ImageTexture.CreateFromImage(Image.CreateFromData(N, N, false, Image.Format.Rgba8, px));
        return _flash = new StandardMaterial3D
        {
            AlbedoTexture = tex, Transparency = BaseMaterial3D.TransparencyEnum.Alpha, BlendMode = BaseMaterial3D.BlendModeEnum.Add,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, BillboardMode = BaseMaterial3D.BillboardModeEnum.Enabled,
        };
    }

    // ---------------------------------------------------------------- synthesised placeholder sounds

    public static AudioStreamWav Gunshot()
    {
        if (_gunshot != null) return _gunshot;
        const int Rate = 44100;
        int n = (int)(Rate * .55f);
        var data = new byte[n * 2];
        var rng = new Random(97232);
        float low = 0, lp = 0;
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Rate;
            float noise = (float)rng.NextDouble() * 2 - 1;
            lp += (noise - lp) * .35f;      // crack: bright noise, very short
            low += (noise - low) * .02f;    // boom/rumble: dark noise, longer tail
            float crack = lp * MathF.Exp(-t * 55);
            float thump = MathF.Sin(MathF.Tau * (55 + 40 * MathF.Exp(-t * 30)) * t) * MathF.Exp(-t * 14);
            float tail = low * 6 * MathF.Exp(-t * 6);
            float s = Math.Clamp((crack * 1.2f + thump * .8f + tail * .7f) * (t < .002f ? t / .002f : 1), -1, 1);
            short v = (short)(s * 30000);
            data[i * 2] = (byte)v; data[i * 2 + 1] = (byte)(v >> 8);
        }
        return _gunshot = Wav(data, Rate);
    }

    public static AudioStreamWav Click(float pitch, float length)
    {
        const int Rate = 44100;
        int n = (int)(Rate * length);
        var data = new byte[n * 2];
        var rng = new Random(7);
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Rate;
            float s = (MathF.Sin(MathF.Tau * pitch * t) * .6f + ((float)rng.NextDouble() * 2 - 1) * .4f) * MathF.Exp(-t * 90);
            short v = (short)(Math.Clamp(s, -1, 1) * 22000);
            data[i * 2] = (byte)v; data[i * 2 + 1] = (byte)(v >> 8);
        }
        return Wav(data, Rate);
    }

    private static AudioStreamWav Wav(byte[] data, int rate) =>
        new() { Format = AudioStreamWav.FormatEnum.Format16Bits, MixRate = rate, Stereo = false, Data = data };
}
