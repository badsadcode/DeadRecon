using System;
using System.Collections.Generic;
using Godot;

namespace DeadRecon.Player;

/// <summary>
/// First-person test rifle built from a BadGun .glb. Uses the gun's own metadata (glTF extras "badgun"):
/// SOCKET_ADS (eye position when aiming), SOCKET_Muzzle, and parts with "motion" (Trigger, Magazine, ChargingHandle).
/// LMB fire (full auto), RMB aim, R reload. Sounds are synthesised here until real audio exists.
/// Bullets are hitscan for now; only terrain has collision, so trees don't stop them yet.
/// </summary>
public partial class Weapon : Node3D
{
    public Walker Walker = null!;
    public string ScenePath = "res://assets/weapons/BadGun_rifle_vietnam_97232.glb";

    // Test-gun numbers (BadGun doesn't ship ballistics; these are ours).
    private const float RoundsPerMinute = 600, Range = 900, HipSpread = .018f, AimSpread = .0015f;
    private const int MagazineSize = 20;
    private const float RaiseFov = 40, ScopeFov = 11, HipFov = 70, AimSpeed = 7;  // ScopeFov 11 = roughly 6x
    private static readonly Vector3 HipOffset = new(.16f, -.21f, -.36f); // where the grip sits relative to the eye

    public int Ammo { get; private set; } = MagazineSize;
    public bool Loaded { get; private set; }
    public bool Scoped => _scope?.Visible == true;
    public float AimAmount => _aim;
    public bool Reloading => _reload >= 0;

    private Node3D _model = null!;
    private Node3D? _trigger, _magazine, _charging, _muzzle;
    private Vector3 _magazineRest, _chargingRest, _adsInModel;
    private Vector3 _magazineAxis = Vector3.Down, _chargingAxis = Vector3.Back;
    private float _magazineTravel = .25f, _chargingTravel = .07f, _triggerTravel = .3f;
    private float _aim, _cooldown, _kick, _flash = -1, _reload = -1, _bob;
    private bool _forceAim;
    private OmniLight3D _flashLight = null!;
    private MeshInstance3D _flashSprite = null!;
    private AudioStreamPlayer _shot = null!, _click = null!, _clack = null!;
    private ColorRect _scope = null!;
    private ShaderMaterial _scopeMaterial = null!;
    private readonly Queue<Node3D> _holes = new();
    private readonly RandomNumberGenerator _rng = new();

    public override void _Ready()
    {
        if (!ResourceLoader.Exists(ScenePath)) { GD.PushWarning($"No weapon at {ScenePath}"); return; }
        _model = ResourceLoader.Load<PackedScene>(ScenePath).Instantiate<Node3D>();
        // BadGun faces +Z (muzzle direction); Godot cameras look down -Z.
        _model.RotationDegrees = new Vector3(0, 180, 0);
        AddChild(_model);
        foreach (var mi in FindAll<GeometryInstance3D>(_model)) mi.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;

        _trigger = _model.FindChild("Trigger", true, false) as Node3D;
        _magazine = _model.FindChild("Magazine", true, false) as Node3D;
        _charging = _model.FindChild("ChargingHandle", true, false) as Node3D;
        _muzzle = _model.FindChild("SOCKET_Muzzle", true, false) as Node3D;
        ReadMotion(_trigger, ref _triggerTravel, out _);
        if (ReadMotion(_magazine, ref _magazineTravel, out var magAxis)) _magazineAxis = magAxis;
        if (ReadMotion(_charging, ref _chargingTravel, out var chAxis)) _chargingAxis = chAxis;
        if (_magazine != null) _magazineRest = _magazine.Position;
        if (_charging != null) _chargingRest = _charging.Position;
        if (_model.FindChild("SOCKET_ADS", true, false) is Node3D ads) _adsInModel = InModel(ads).Origin;
        else _adsInModel = new Vector3(0, .1f, -.2f);

        _flashLight = new OmniLight3D { LightColor = new Color(1, .75f, .4f), LightEnergy = 0, OmniRange = 6, ShadowEnabled = false };
        _flashSprite = new MeshInstance3D { Mesh = new QuadMesh { Size = new Vector2(.28f, .28f) }, MaterialOverride = FlashMaterial(), Visible = false, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        (_muzzle ?? _model).AddChild(_flashLight);
        (_muzzle ?? _model).AddChild(_flashSprite);

        // Scope view: a 3D scope can't magnify, so at full aim the gun hides and this overlay + narrow FOV takes over.
        _scopeMaterial = new ShaderMaterial { Shader = GD.Load<Shader>("res://game/Player/scope.gdshader") };
        _scope = new ColorRect { Material = _scopeMaterial, Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore };
        _scope.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        var layer = new CanvasLayer { Layer = -1 }; // under the HUD text
        layer.AddChild(_scope);
        AddChild(layer);

        _shot = Player(Gunshot(), -2);
        _click = Player(Click(1800, .03f), -8);
        _clack = Player(Click(700, .07f), -6);
        Loaded = true;
    }

    /// <summary>For automated screenshots: hold aim fully on/off, fire one muzzle flash.</summary>
    public void ForceAim(bool on) { _forceAim = on; _aim = on ? 1 : 0; }
    public void FlashForShot() { _flash = 0; }
    public void TestFire() { _cooldown = 0; Fire(); }

    public override void _Process(double delta)
    {
        if (!Loaded) return;
        float dt = (float)delta;
        bool captured = Input.MouseMode == Input.MouseModeEnum.Captured;
        bool wantAim = _forceAim || (captured && Input.IsMouseButtonPressed(MouseButton.Right) && !Reloading);
        _aim = Mathf.MoveToward(_aim, wantAim ? 1 : 0, dt * AimSpeed * (wantAim ? 1 : 1.3f));
        float ease = _aim * _aim * (3 - 2 * _aim);
        bool scoped = _aim > .97f;
        Walker.Camera.Fov = scoped ? ScopeFov : Mathf.Lerp(HipFov, RaiseFov, ease);
        Walker.SensitivityScale = Walker.Camera.Fov / HipFov;
        _model.Visible = !scoped;
        _scope.Visible = scoped;
        if (scoped) { var vs = GetViewport().GetVisibleRect().Size; _scopeMaterial.SetShaderParameter("aspect", vs.X / vs.Y); }

        _cooldown -= dt;
        if (captured && Input.IsMouseButtonPressed(MouseButton.Left) && _cooldown <= 0 && !Reloading) Fire();
        if (captured && Input.IsPhysicalKeyPressed(Key.R) && !Reloading && Ammo < MagazineSize) { _reload = 0; _clack.Play(); }
        UpdateReload(dt);

        // Pose: lerp from the hip pose to "ADS socket on the eye"; add bob, recoil kick.
        var speed = new Vector2(Walker.Velocity.X, Walker.Velocity.Z).Length();
        _bob += dt * speed * 1.6f;
        float bobAmount = (1 - ease * .85f) * Math.Min(speed / 4f, 1.5f);
        var bob = new Vector3(MathF.Sin(_bob) * .012f, -MathF.Abs(MathF.Cos(_bob)) * .01f, 0) * bobAmount;
        var adsOffset = -(_model.Basis * _adsInModel);              // puts SOCKET_ADS at the camera origin
        _kick = Mathf.MoveToward(_kick, 0, dt * 6);
        Position = HipOffset.Lerp(adsOffset, ease) + bob + new Vector3(0, 0, _kick * .05f);
        Rotation = new Vector3(_kick * (1 - ease * .6f) * .09f, 0, 0);
        if (_trigger != null) _trigger.Rotation = new Vector3(_cooldown > 0 ? _triggerTravel : 0, 0, 0);

        if (_flash >= 0)
        {
            _flash += dt;
            bool on = _flash < .045f;
            _flashSprite.Visible = on;
            _flashLight.LightEnergy = on ? 4 : 0;
            _flashSprite.Rotation = new Vector3(0, 0, _rng.Randf() * MathF.Tau);
            if (!on) _flash = -1;
        }
    }

    private void Fire()
    {
        if (Ammo <= 0) { _click.Play(); _cooldown = .25f; return; }
        Ammo--;
        _cooldown = 60f / RoundsPerMinute;
        _kick = Math.Min(_kick + 1, 1.6f);
        _flash = 0;
        _shot.PitchScale = 1 + _rng.RandfRange(-.06f, .06f);
        _shot.Play();
        Walker.AddRecoil(Mathf.DegToRad(Mathf.Lerp(.9f, .45f, _aim)), Mathf.DegToRad(_rng.RandfRange(-.35f, .35f)));

        var cam = Walker.Camera.GlobalTransform;
        float spread = Mathf.Lerp(HipSpread, AimSpread, _aim);
        var dir = (-cam.Basis.Z + cam.Basis.X * _rng.RandfRange(-spread, spread) + cam.Basis.Y * _rng.RandfRange(-spread, spread)).Normalized();
        var query = PhysicsRayQueryParameters3D.Create(cam.Origin, cam.Origin + dir * Range);
        query.Exclude = new Godot.Collections.Array<Rid> { Walker.GetRid() };
        var hit = GetWorld3D().DirectSpaceState.IntersectRay(query);
        if (hit.Count > 0) Impact((Vector3)hit["position"], (Vector3)hit["normal"]);
    }

    private void UpdateReload(float dt)
    {
        if (_reload < 0) return;
        _reload += dt;
        // 0-0.35 mag out, 0.35-0.9 swap, 0.9-1.25 mag in, 1.35-1.7 charging handle back and forward.
        float mag = _reload < .35f ? _reload / .35f : _reload < .9f ? 1 : _reload < 1.25f ? 1 - (_reload - .9f) / .35f : 0;
        float ch = _reload < 1.35f ? 0 : _reload < 1.5f ? (_reload - 1.35f) / .15f : _reload < 1.7f ? 1 - (_reload - 1.5f) / .2f : 0;
        if (_magazine != null) _magazine.Position = _magazineRest + _magazineAxis * _magazineTravel * mag;
        if (_charging != null) _charging.Position = _chargingRest + _chargingAxis * _chargingTravel * ch;
        if (_reload >= 1.25f && _reload - dt < 1.25f) _clack.Play();
        if (_reload >= 1.5f && _reload - dt < 1.5f) _click.Play();
        if (_reload >= 1.75f) { _reload = -1; Ammo = MagazineSize; }
    }

    private void Impact(Vector3 at, Vector3 normal)
    {
        var hole = new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = .03f, BottomRadius = .03f, Height = .004f, RadialSegments = 8, Rings = 0 },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(.06f, .05f, .04f), Roughness = 1 },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        GetTree().CurrentScene.AddChild(hole);
        var up = normal.Normalized();
        var side = Math.Abs(up.Y) < .9f ? up.Cross(Vector3.Up).Normalized() : up.Cross(Vector3.Right).Normalized();
        hole.GlobalTransform = new Transform3D(new Basis(side, up, side.Cross(up)), at + up * .003f);
        _holes.Enqueue(hole);
        if (_holes.Count > 80) _holes.Dequeue().QueueFree();

        var dust = new CpuParticles3D
        {
            Emitting = true, OneShot = true, Amount = 14, Lifetime = .9f, Explosiveness = .95f,
            Direction = Vector3.Up, Spread = 35, InitialVelocityMin = 1.2f, InitialVelocityMax = 3f, Gravity = new Vector3(0, -3, 0),
            ScaleAmountMin = .5f, ScaleAmountMax = 1.2f,
            Mesh = new QuadMesh { Size = new Vector2(.09f, .09f) },
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color(.38f, .30f, .20f, .8f), Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            },
        };
        GetTree().CurrentScene.AddChild(dust);
        dust.GlobalTransform = new Transform3D(new Basis(side, up, side.Cross(up)), at);
        GetTree().CreateTimer(1.5).Timeout += dust.QueueFree;
    }

    // ---------------------------------------------------------------- helpers

    private Transform3D InModel(Node3D node)
    {
        var xf = Transform3D.Identity;
        for (Node? n = node; n != null && n != _model; n = n.GetParent())
            if (n is Node3D n3) xf = n3.Transform * xf;
        return xf;
    }

    /// <summary>Read a BadGun part's "motion" metadata (axis and range end) from its glTF extras.</summary>
    private static bool ReadMotion(Node3D? part, ref float travel, out Vector3 axis)
    {
        axis = Vector3.Zero;
        if (part == null || !part.HasMeta("extras")) return false;
        var extras = part.GetMeta("extras").AsGodotDictionary();
        if (!extras.TryGetValue("badgun", out var bg) || !bg.AsGodotDictionary().TryGetValue("motion", out var mv)) return false;
        var motion = mv.AsGodotDictionary();
        var a = motion["axis"].AsGodotArray();
        var r = motion["range"].AsGodotArray();
        axis = new Vector3((float)a[0].AsDouble(), (float)a[1].AsDouble(), (float)a[2].AsDouble());
        travel = (float)r[1].AsDouble();
        return true;
    }

    private static IEnumerable<T> FindAll<T>(Node root) where T : Node
    {
        if (root is T t) yield return t;
        foreach (var c in root.GetChildren()) foreach (var x in FindAll<T>(c)) yield return x;
    }

    private AudioStreamPlayer Player(AudioStream stream, float db)
    {
        var p = new AudioStreamPlayer { Stream = stream, VolumeDb = db, MaxPolyphony = 4 };
        AddChild(p);
        return p;
    }

    private static StandardMaterial3D FlashMaterial()
    {
        const int N = 64;
        var px = new byte[N * N * 4];
        for (int y = 0; y < N; y++)
        for (int x = 0; x < N; x++)
        {
            float dx = (x + .5f) / N * 2 - 1, dy = (y + .5f) / N * 2 - 1;
            float r = MathF.Sqrt(dx * dx + dy * dy), ang = MathF.Atan2(dy, dx);
            float star = MathF.Pow(MathF.Abs(MathF.Cos(ang * 2.5f)), 6) * .6f + .4f; // five-ish prongs
            float a = Math.Clamp(1 - r / star, 0, 1);
            a = a * a;
            int o = (y * N + x) * 4;
            px[o] = 255; px[o + 1] = (byte)(200 * a + 55); px[o + 2] = (byte)(120 * a); px[o + 3] = (byte)(255 * a);
        }
        var tex = ImageTexture.CreateFromImage(Image.CreateFromData(N, N, false, Image.Format.Rgba8, px));
        return new StandardMaterial3D
        {
            AlbedoTexture = tex, Transparency = BaseMaterial3D.TransparencyEnum.Alpha, BlendMode = BaseMaterial3D.BlendModeEnum.Add,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, BillboardMode = BaseMaterial3D.BillboardModeEnum.Enabled,
            NoDepthTest = false, AlbedoColor = new Color(1, 1, 1, 1),
        };
    }

    // ---------------------------------------------------------------- synthesised placeholder sounds

    private static AudioStreamWav Gunshot()
    {
        const int Rate = 44100;
        int n = (int)(Rate * .55f);
        var data = new byte[n * 2];
        var rng = new Random(97232);
        float low = 0, lp = 0;
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Rate;
            float noise = (float)rng.NextDouble() * 2 - 1;
            lp += (noise - lp) * .35f;                                 // crack: bright noise, very short
            low += (noise - low) * .02f;                               // boom/rumble: dark noise, longer tail
            float crack = lp * MathF.Exp(-t * 55);
            float thump = MathF.Sin(MathF.Tau * (55 + 40 * MathF.Exp(-t * 30)) * t) * MathF.Exp(-t * 14);
            float tail = low * 6 * MathF.Exp(-t * 6);
            float s = Math.Clamp((crack * 1.2f + thump * .8f + tail * .7f) * (t < .002f ? t / .002f : 1), -1, 1);
            short v = (short)(s * 30000);
            data[i * 2] = (byte)v; data[i * 2 + 1] = (byte)(v >> 8);
        }
        return new AudioStreamWav { Format = AudioStreamWav.FormatEnum.Format16Bits, MixRate = Rate, Stereo = false, Data = data };
    }

    private static AudioStreamWav Click(float pitch, float length)
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
        return new AudioStreamWav { Format = AudioStreamWav.FormatEnum.Format16Bits, MixRate = Rate, Stereo = false, Data = data };
    }
}
