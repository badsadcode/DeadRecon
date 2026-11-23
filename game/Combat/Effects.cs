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
    public static void Make(Vector3 at, float radius, Node source) => Made?.Invoke(at, radius, source);

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
