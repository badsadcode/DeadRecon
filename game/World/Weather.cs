using System;
using System.Collections.Generic;
using DeadRecon.Combat;
using Godot;

namespace DeadRecon.World;

/// <summary>
/// Rain, written from scratch for Dead Recon. One number drives everything: <see cref="Intensity"/> (0 dry .. 1 storm).
/// <list type="bullet">
/// <item>Visuals: wind-slanted streaks around the camera that stop at the canopy (a particle heightfield seen from
/// above), splashes where they land, wet/puddled ground and glossy leaves (<see cref="Wetness"/> lags the rain),
/// greyer sky, thicker mist, weaker sun, lightning in storms.</item>
/// <item>Sound: synthesised patter and roar loops, thunder delayed by distance.</item>
/// <item>Gameplay: rain muffles what guards hear (<see cref="Hearing.Muffle"/>) and shortens how far they see
/// (<see cref="SightFactor"/>).</item>
/// </list>
/// N cycles clear / drizzle / rain / storm; it also changes by itself every few minutes.
/// </summary>
public partial class Weather : Node3D
{
    public enum Kind { Clear, Drizzle, Rain, Storm }
    private static readonly float[] Strength = { 0, .3f, .68f, 1 };

    public static Weather? Current { get; private set; }

    public Camera3D Camera = null!;
    public Godot.Environment Environment = null!;
    public DirectionalLight3D Sun = null!;
    public TerrainStreamer Terrain = null!;
    public TreeScatter Trees = null!;
    public GroundCover Cover = null!;

    public Kind Target { get; private set; } = Kind.Clear;
    /// <summary>How hard it's raining right now, 0..1 (eases toward the target weather over ~10 s).</summary>
    public float Intensity { get; private set; }
    /// <summary>How wet the world is, 0..1: soaks in over about a minute of rain, dries over a few minutes.</summary>
    public float Wetness { get; private set; }
    /// <summary>Multiplier on guard sight range (1 clear .. ~0.6 in a storm).</summary>
    public float SightFactor => 1 - .4f * Intensity;
    public bool AutoChange { get; set; } = true;
    /// <summary>0 open sky .. 1 thick canopy overhead (fewer drops reach you there).</summary>
    public float UnderCanopy { get; private set; }
    private float _canopyTimer, _openSky = 1, _shownOpen = 1;
    public int LightningStrikes { get; private set; }

    private GpuParticles3D _rain = null!, _splash = null!;
    private AudioStreamPlayer _patter = null!, _roar = null!;
    private ProceduralSkyMaterial? _sky;
    private Color _skyTop, _skyHorizon, _fogColor;
    private float _sunEnergy, _fogDensity, _ambient, _nextChange = 150, _nextLightning = 10, _flash = -1;
    private readonly RandomNumberGenerator _rng = new() { Seed = 4242 };
    private readonly List<(float delay, float volume)> _pendingThunder = new();
    private AudioStreamPlayer _thunder = null!;

    public override void _Ready()
    {
        Current = this;
        _sky = Environment.Sky?.SkyMaterial as ProceduralSkyMaterial;
        if (_sky != null) { _skyTop = _sky.SkyTopColor; _skyHorizon = _sky.SkyHorizonColor; }
        _fogColor = Environment.FogLightColor;
        _fogDensity = Environment.FogDensity;
        _ambient = Environment.AmbientLightEnergy;
        _sunEnergy = Sun.LightEnergy;
        BuildRain();
        _patter = Loop(RainLoop(heavy: false), -80);
        _roar = Loop(RainLoop(heavy: true), -80);
        _thunder = new AudioStreamPlayer { Stream = Thunder(), MaxPolyphony = 3 };
        AddChild(_thunder);
    }

    public override void _ExitTree() { if (Current == this) Current = null; Hearing.Muffle = 0; }

    /// <summary>Change the weather (eases in). Pauses the automatic changes for a while.</summary>
    public void Set(Kind kind) { Target = kind; _nextChange = 300; }

    /// <summary>For tests: jump straight to a weather, fully soaked (or dry).</summary>
    public void SetNow(Kind kind)
    {
        Target = kind; Intensity = Strength[(int)kind]; Wetness = Intensity > 0 ? 1 : 0; _nextChange = 600;
        Apply(0);
    }

    public void Cycle() => Set((Kind)(((int)Target + 1) % 4));

    /// <summary>Force a lightning strike now (tests); distance in metres decides the thunder delay.</summary>
    public void Strike(float distance = 600) { _flash = 0; LightningStrikes++; _pendingThunder.Add((distance / 343f, Math.Clamp(1.4f - distance / 2500, .25f, 1))); }

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        if (AutoChange)
        {
            _nextChange -= dt;
            if (_nextChange <= 0)
            {
                _nextChange = _rng.RandfRange(120, 300);
                float roll = _rng.Randf();
                Target = roll < .35f ? Kind.Clear : roll < .55f ? Kind.Drizzle : roll < .85f ? Kind.Rain : Kind.Storm;
                GD.Print($"[weather] turning to {Target}");
            }
        }
        Intensity = Mathf.MoveToward(Intensity, Strength[(int)Target], dt * .09f);
        Wetness = Intensity > .05f ? Mathf.MoveToward(Wetness, Math.Max(Wetness, Intensity), dt * Intensity / 50f)
                                   : Mathf.MoveToward(Wetness, 0, dt / 200f);

        if (Target == Kind.Storm && Intensity > .8f)
        {
            _nextLightning -= dt;
            if (_nextLightning <= 0) { _nextLightning = _rng.RandfRange(7, 25); Strike(_rng.RandfRange(250, 3500)); }
        }
        for (int i = _pendingThunder.Count - 1; i >= 0; i--)
        {
            var (d, v) = _pendingThunder[i];
            d -= dt;
            if (d <= 0) { _thunder.VolumeDb = Mathf.LinearToDb(v) + 2; _thunder.PitchScale = _rng.RandfRange(.85f, 1.1f); _thunder.Play(); _pendingThunder.RemoveAt(i); }
            else _pendingThunder[i] = (d, v);
        }
        Apply(dt);
    }

    private void Apply(float dt)
    {
        float r = Intensity;
        // Rain follows the camera; drops live in world space so moving doesn't drag them along.
        var cam = Camera.GlobalPosition;
        _rain.GlobalPosition = cam + Vector3.Up * 28; // above the canopy (trees are up to ~35 m), so leaves catch drops
        // Canopy overhead: average how much sky gets through above and around the camera (our own plant registry;
        // Godot's particle collision doesn't see the instanced trees). Under thick jungle only ~a quarter of the drops reach you.
        _canopyTimer -= dt;
        if (_canopyTimer <= 0)
        {
            _canopyTimer = .25f;
            float sky = 0;
            for (int k = 0; k < 5; k++)
            {
                var o = k == 0 ? Vector3.Zero : new Vector3(MathF.Cos(k * 1.57f) * 6, 0, MathF.Sin(k * 1.57f) * 6);
                sky += Foliage.SkyAbove(cam + o);
            }
            _openSky = sky / 5;
        }
        _shownOpen = Mathf.MoveToward(_shownOpen, _openSky, dt * .8f);
        UnderCanopy = 1 - _shownOpen;
        _rain.AmountRatio = Math.Max(r * (.22f + .78f * _shownOpen), .001f);
        _rain.Emitting = r > .01f;
        _splash.AmountRatio = Math.Max(r, .001f);

        // Mood: greyer sky, thicker mist, weaker light.
        if (_sky != null)
        {
            _sky.SkyTopColor = _skyTop.Lerp(new Color(.32f, .35f, .38f), r * .85f);
            _sky.SkyHorizonColor = _skyHorizon.Lerp(new Color(.52f, .55f, .55f), r * .8f);
        }
        Environment.FogLightColor = _fogColor.Lerp(new Color(.48f, .52f, .52f), r);
        Environment.FogDensity = _fogDensity * (1 + 2.2f * r);
        float flash = 0;
        if (_flash >= 0)
        {
            _flash += dt;
            // Two quick flickers.
            flash = _flash < .07f ? 1 : _flash < .14f ? .15f : _flash < .22f ? .8f : 0;
            if (_flash > .3f) _flash = -1;
        }
        // Under clouds: no sun disc, no sun glints on wet ground. Lightning is a cold white flash of the whole sky
        // and the air, not more sunshine.
        Sun.LightEnergy = _sunEnergy * (1 - .65f * r);
        Sun.LightSpecular = 1 - .9f * r;
        Sun.SkyMode = r > .35f ? DirectionalLight3D.SkyModeEnum.LightOnly : DirectionalLight3D.SkyModeEnum.LightAndSky;
        Environment.AmbientLightEnergy = _ambient * (1 - .3f * r) + flash * 3f;
        Environment.FogLightColor = Environment.FogLightColor.Lerp(new Color(.85f, .88f, 1f), flash * .8f);
        if (_sky != null)
        {
            _sky.SkyTopColor = _sky.SkyTopColor.Lerp(new Color(.75f, .78f, .9f), flash * .7f);
            _sky.SkyHorizonColor = _sky.SkyHorizonColor.Lerp(new Color(.85f, .87f, .95f), flash * .7f);
        }

        Terrain.SetWetness(Wetness);
        Trees.SetWetness(Wetness);
        Cover.SetWetness(Wetness);

        // Sound: light rain is patter, heavy rain is a roar.
        _patter.VolumeDb = r < .01f ? -80 : Mathf.LinearToDb(Math.Clamp(r * 2.2f, 0, 1) * (1 - r * .4f)) - 4;
        _roar.VolumeDb = r < .3f ? -80 : Mathf.LinearToDb(Math.Clamp((r - .3f) / .7f, 0, 1)) - 2;

        // Gameplay: rain covers sounds.
        Hearing.Muffle = r;
    }

    // ================================================================= particles

    private void BuildRain()
    {
        // Canopy and ground catch the drops: a heightfield rendered from above, following the camera.
        AddChild(new GpuParticlesCollisionHeightField3D
        {
            Size = new Vector3(70, 120, 70), Resolution = GpuParticlesCollisionHeightField3D.ResolutionEnum.Resolution512,
            FollowCameraEnabled = true, UpdateMode = GpuParticlesCollisionHeightField3D.UpdateModeEnum.WhenMoved,
        });

        var splashMat = new ParticleProcessMaterial
        {
            Direction = Vector3.Up, Spread = 70, InitialVelocityMin = .6f, InitialVelocityMax = 1.6f, Gravity = new Vector3(0, -7, 0),
            ScaleMin = .4f, ScaleMax = 1f,
        };
        _splash = new GpuParticles3D
        {
            Amount = 6000, Lifetime = .28f, LocalCoords = false, ProcessMaterial = splashMat, Emitting = false,
            VisibilityAabb = new Aabb(new Vector3(-50, -60, -50), new Vector3(100, 90, 100)),
            DrawPass1 = new QuadMesh
            {
                Size = new Vector2(.05f, .05f),
                Material = new StandardMaterial3D
                {
                    AlbedoColor = new Color(.85f, .9f, .95f, .45f), Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                    ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles,
                },
            },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(_splash);

        var rainMat = new ParticleProcessMaterial
        {
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Box, EmissionBoxExtents = new Vector3(30, 1.5f, 30),
            Direction = new Vector3(.12f, -1, .05f), Spread = 2, InitialVelocityMin = 18, InitialVelocityMax = 23,
            Gravity = new Vector3(1.5f, -3, .6f), // a little wind
            ParticleFlagAlignY = true,
            CollisionMode = ParticleProcessMaterial.CollisionModeEnum.HideOnContact,
            SubEmitterMode = ParticleProcessMaterial.SubEmitterModeEnum.AtCollision, SubEmitterAmountAtCollision = 1,
            ScaleMin = .7f, ScaleMax = 1.3f,
        };
        _rain = new GpuParticles3D
        {
            Amount = 26000, Lifetime = 2.2f, LocalCoords = false, ProcessMaterial = rainMat, Emitting = false,
            VisibilityAabb = new Aabb(new Vector3(-50, -80, -50), new Vector3(100, 110, 100)),
            DrawPass1 = Streak(), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            CollisionBaseSize = .02f,
        };
        AddChild(_rain);
        _rain.SubEmitter = _rain.GetPathTo(_splash);
    }

    /// <summary>A drop streak: two crossed thin quads (never seen edge-on), stretched along its fall by AlignY.</summary>
    private static ArrayMesh Streak()
    {
        const float W = .012f, H = .55f;
        var v = new[]
        {
            new Vector3(-W, -H, 0), new Vector3(W, -H, 0), new Vector3(W, H, 0), new Vector3(-W, H, 0),
            new Vector3(0, -H, -W), new Vector3(0, -H, W), new Vector3(0, H, W), new Vector3(0, H, -W),
        };
        var uv = new[] { new Vector2(0, 1), new Vector2(1, 1), new Vector2(1, 0), new Vector2(0, 0), new Vector2(0, 1), new Vector2(1, 1), new Vector2(1, 0), new Vector2(0, 0) };
        var idx = new[] { 0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7 };
        var a = new Godot.Collections.Array();
        a.Resize((int)Mesh.ArrayType.Max);
        a[(int)Mesh.ArrayType.Vertex] = v;
        a[(int)Mesh.ArrayType.TexUV] = uv;
        a[(int)Mesh.ArrayType.Index] = idx;
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, a);
        // Fade toward both ends of the streak.
        var grad = new Gradient();
        grad.SetColor(0, new Color(1, 1, 1, 0));
        grad.AddPoint(.5f, new Color(1, 1, 1, 1));
        grad.SetColor(grad.GetPointCount() - 1, new Color(1, 1, 1, 0));
        var tex = new GradientTexture2D { Gradient = grad, FillFrom = new Vector2(0, 0), FillTo = new Vector2(0, 1), Width = 4, Height = 64 };
        mesh.SurfaceSetMaterial(0, new StandardMaterial3D
        {
            AlbedoColor = new Color(.78f, .82f, .88f, .32f), AlbedoTexture = tex, Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        });
        return mesh;
    }

    // ================================================================= synthesised sound

    private AudioStreamPlayer Loop(AudioStreamWav stream, float db)
    {
        var p = new AudioStreamPlayer { Stream = stream, VolumeDb = db, Autoplay = true };
        AddChild(p);
        return p;
    }

    /// <summary>4 s seamless stereo loop: light = sparse bright drop ticks over soft hiss; heavy = dense dark roar.</summary>
    private static AudioStreamWav RainLoop(bool heavy)
    {
        const int Rate = 44100;
        int n = Rate * 4;
        var data = new byte[n * 4];
        var rng = new Random(heavy ? 77 : 11);
        float[] lp = new float[2], drop = new float[2], dropPhase = new float[2];
        for (int i = 0; i < n; i++)
        for (int ch = 0; ch < 2; ch++)
        {
            float noise = (float)rng.NextDouble() * 2 - 1;
            lp[ch] += (noise - lp[ch]) * (heavy ? .08f : .5f);
            float hiss = lp[ch] * (heavy ? 1.6f : .18f);
            // Individual drops: little decaying "tik"s at random moments.
            if (rng.NextDouble() < (heavy ? .004 : .0012)) { drop[ch] = (float)rng.NextDouble() * .7f + .3f; dropPhase[ch] = 0; }
            dropPhase[ch] += 1f / Rate;
            float tik = drop[ch] * MathF.Sin(MathF.Tau * (heavy ? 900 : 2400) * dropPhase[ch]) * MathF.Exp(-dropPhase[ch] * 260);
            float s = hiss + tik * (heavy ? .3f : .5f);
            // Cross-fade the last 0.1 s into the start so the loop has no click.
            float t = i / (float)n;
            if (t > .975f) s *= (1 - t) / .025f;
            if (t < .025f) s *= t / .025f;
            short v = (short)(Math.Clamp(s, -1, 1) * 20000);
            data[(i * 2 + ch) * 2] = (byte)v; data[(i * 2 + ch) * 2 + 1] = (byte)(v >> 8);
        }
        return new AudioStreamWav
        {
            Format = AudioStreamWav.FormatEnum.Format16Bits, MixRate = Rate, Stereo = true, Data = data,
            LoopMode = AudioStreamWav.LoopModeEnum.Forward, LoopBegin = 0, LoopEnd = n,
        };
    }

    /// <summary>A long rolling rumble with a crack at the front.</summary>
    private static AudioStreamWav Thunder()
    {
        const int Rate = 44100;
        int n = (int)(Rate * 5.5f);
        var data = new byte[n * 2];
        var rng = new Random(1337);
        float low = 0, mid = 0, roll = 0;
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Rate;
            float noise = (float)rng.NextDouble() * 2 - 1;
            low += (noise - low) * .01f;
            mid += (noise - mid) * .1f;
            if (i % 2205 == 0) roll = (float)rng.NextDouble();     // lumpy rolling every 50 ms
            float crack = mid * 1.5f * MathF.Exp(-t * 9);
            float rumble = low * 14 * (.5f + roll) * MathF.Exp(-t * .7f) * Math.Min(1, t * 6);
            short v = (short)(Math.Clamp(crack + rumble, -1, 1) * 30000);
            data[i * 2] = (byte)v; data[i * 2 + 1] = (byte)(v >> 8);
        }
        return new AudioStreamWav { Format = AudioStreamWav.FormatEnum.Format16Bits, MixRate = Rate, Stereo = false, Data = data };
    }

    public string Describe() => Intensity < .03f ? (Wetness > .05f ? "clear (still wet)" : "clear")
        : Intensity < .45f ? "drizzle" : Intensity < .85f ? "rain" : "STORM";
}
