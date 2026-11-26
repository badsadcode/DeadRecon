using System;
using System.Collections.Generic;
using DeadRecon.Combat;
using DeadRecon.Gear;
using Godot;

namespace DeadRecon.Player;

/// <summary>
/// First-person test rifle built from a BadGun .glb. Uses the gun's own metadata (glTF extras "badgun"):
/// SOCKET_ADS (eye position when aiming), SOCKET_Muzzle, and parts with "motion" (Trigger, Magazine, ChargingHandle).
/// LMB fire, RMB aim (scope), R reload, V semi/auto, Shift while scoped holds your breath.
/// Scope sway follows your breathing and stance; recoil springs mostly back. Casings fly from SOCKET_Eject.
/// Bullets are hitscan; trunks stop them via the foliage registry.
/// <para>What's fitted (optic, muzzle device, light/laser, foregrip, paint) and the magazines come from the
/// <see cref="Kit"/>: the rifle is rebuilt by <see cref="RifleBuilder"/> whenever the loadout changes.</para>
/// </summary>
public partial class Weapon : Node3D
{
    public Walker Walker = null!;
    public string ScenePath = "res://assets/weapons/BadGun_rifle_vietnam_97232.glb";

    // Test-gun numbers (BadGun doesn't ship ballistics; these are ours).
    private const float Range = 900;
    private WeaponDef Def => Kit.Current?.Weapon ?? WeaponDef.Get(null);
    private float RoundsPerMinute => Def.Rpm;
    private float HipSpread => Def.HipSpread;
    private float AimSpread => Def.AimSpread;
    private float Damage => Def.Damage;   // rifle 34: three to the body; the guard triples it for headshots
    private const float RaiseFov = 40, ScopeFov = 11, HipFov = 70, AimSpeed = 7;  // ScopeFov 11 = roughly 6x
    private static readonly Vector3 HipOffset = new(.16f, -.21f, -.36f); // where the grip sits relative to the eye

    public int Ammo => Kit.Current?.Loaded ?? 0;
    public bool Loaded { get; private set; }
    public int ShotsFired { get; private set; }
    /// <summary>A bullet hit a person (true = that killed them). The HUD shows a hit marker.</summary>
    public event Action<bool>? HitConfirmed;
    public int MagazineCapacity => Kit.Current?.Capacity ?? 20;
    public string WeaponName => Def.Name;
    public bool Suppressed => _handling.Noise < .5f;
    public bool HasLight => _light != null || _laserFrom != null;
    public string OpticName => Kit.Current is { } k && ItemDef.All.TryGetValue(k.Loadout.Optic, out var d) ? d.Name : "";
    public bool Scoped => _scope?.Visible == true;
    public float AimAmount => _aim;
    public bool Reloading => _reload >= 0;
    public bool Automatic { get; private set; } = true;
    /// <summary>0..1 breath left for steadying the scope (Shift while scoped).</summary>
    public float Breath { get; private set; } = 1;
    public bool HoldingBreath { get; private set; }
    /// <summary>What the last bullet hit (tests).</summary>
    public string LastHit { get; private set; } = "";
    public Vector3 LastPoint { get; private set; }

    private Node3D _model = null!;
    private RifleBuilder.OpticStats _optic = RifleBuilder.Optic("optic_stock");
    private (float Recoil, float Noise, float Flash, float Sway) _handling = (1, 1, 1, 1);
    private SpotLight3D? _light;
    private Node3D? _laserFrom;
    private MeshInstance3D? _laserBeam, _laserDot;
    private AudioStreamPlayer _quietShot = null!;
    private float _reloadSpeed = 1, _noMagsToast;
    private Node3D? _trigger, _magazine, _charging, _muzzle, _eject;
    private float _swayTime, _breathShake;
    private bool _triggerWasDown, _breathSpent;
    private readonly Queue<Node3D> _casings = new();
    private static StandardMaterial3D? _brass, _smoke;
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
    private readonly RandomNumberGenerator _rng = new();

    public override void _Ready()
    {
        if (!ResourceLoader.Exists(ScenePath)) { GD.PushWarning($"No weapon at {ScenePath}"); return; }
        Rebuild();
        if (Kit.Current != null) Kit.Current.LoadoutChanged += Rebuild;

        // Scope view: a 3D scope can't magnify, so at full aim the gun hides and this overlay + narrow FOV takes over.
        _scopeMaterial = new ShaderMaterial { Shader = GD.Load<Shader>("res://game/Player/scope.gdshader") };
        _scope = new ColorRect { Material = _scopeMaterial, Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore };
        _scope.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        var layer = new CanvasLayer { Layer = -1 }; // under the HUD text
        layer.AddChild(_scope);
        AddChild(layer);

        _shot = Player(Effects.Gunshot(), -2);
        _quietShot = Player(Effects.SuppressedShot(), -6);
        _click = Player(Effects.Click(1800, .03f), -8);
        _clack = Player(Effects.Click(700, .07f), -6);
        Loaded = true;
    }

    public override void _ExitTree() { if (Kit.Current != null) Kit.Current.LoadoutChanged -= Rebuild; }

    /// <summary>(Re)build the rifle with what's fitted now.</summary>
    private void Rebuild()
    {
        var loadout = Kit.Current?.Loadout ?? new Loadout();
        if (RifleBuilder.Build(loadout) is not { } r) return;
        if (_model != null && IsInstanceValid(_model)) _model.QueueFree();
        _model = r.Model;
        // BadGun faces +Z (muzzle direction); Godot cameras look down -Z.
        _model.RotationDegrees = new Vector3(0, 180, 0);
        AddChild(_model);
        foreach (var mi in FindAll<GeometryInstance3D>(_model)) mi.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
        _trigger = r.Trigger; _magazine = r.Magazine; _charging = r.Charging; _muzzle = r.Muzzle; _eject = r.Eject;
        _light = r.Light; _laserFrom = r.Laser;
        ReadMotion(_trigger, ref _triggerTravel, out _);
        if (ReadMotion(_magazine, ref _magazineTravel, out var magAxis)) _magazineAxis = magAxis;
        if (ReadMotion(_charging, ref _chargingTravel, out var chAxis)) _chargingAxis = chAxis;
        if (_magazine != null) _magazineRest = _magazine.Position;
        if (_charging != null) _chargingRest = _charging.Position;
        _adsInModel = r.Ads;
        _optic = RifleBuilder.Optic(loadout.Optic, WeaponDef.Get(loadout.Weapon));
        Automatic = WeaponDef.Get(loadout.Weapon).Auto;
        _handling = RifleBuilder.Handling(loadout);

        _flashLight = new OmniLight3D { LightColor = new Color(1, .72f, .38f), LightEnergy = 0, OmniRange = 6, ShadowEnabled = false };
        _flashSprite = new MeshInstance3D { Mesh = new QuadMesh { Size = new Vector2(.34f, .34f) }, MaterialOverride = Effects.FlashMaterial(), Visible = false, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        (_muzzle ?? _model).AddChild(_flashLight);
        (_muzzle ?? _model).AddChild(_flashSprite);

        // Laser: a thin red line to whatever it touches, and a dot there.
        _laserBeam?.QueueFree(); _laserDot?.QueueFree(); _laserBeam = _laserDot = null;
        if (_laserFrom != null)
        {
            var red = new StandardMaterial3D
            {
                AlbedoColor = new Color(1, .1f, .08f, .55f), ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha, BlendMode = BaseMaterial3D.BlendModeEnum.Add,
                EmissionEnabled = true, Emission = new Color(1, .1f, .05f), EmissionEnergyMultiplier = 3,
            };
            _laserBeam = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = .0012f, BottomRadius = .0012f, Height = 1, RadialSegments = 4, Rings = 0 }, MaterialOverride = red, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Visible = false };
            _laserDot = new MeshInstance3D { Mesh = new SphereMesh { Radius = .02f, Height = .04f, RadialSegments = 6, Rings = 3 }, MaterialOverride = red, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Visible = false };
            GetTree().CurrentScene.CallDeferred(Node.MethodName.AddChild, _laserBeam);
            GetTree().CurrentScene.CallDeferred(Node.MethodName.AddChild, _laserDot);
        }
        Loaded = true;
    }

    /// <summary>For automated screenshots: hold aim fully on/off, fire one muzzle flash.</summary>
    public void ForceAim(bool on) { _forceAim = on; _aim = on ? 1 : 0; }
    public void FlashForShot() { _flash = 0; }
    public void TestFire() { _cooldown = 0; Fire(); }
    public void TestReload() { Kit.Current?.TestTopUp(); }

    public override void _Process(double delta)
    {
        if (!Loaded) return;
        // Down too badly to shoot: the rifle drops out of view.
        Visible = !Walker.Down || Walker.CanShoot;
        float dt = (float)delta;
        bool captured = Input.MouseMode == Input.MouseModeEnum.Captured;
        bool wantAim = _forceAim || (captured && Input.IsMouseButtonPressed(MouseButton.Right) && !Reloading);
        _aim = Mathf.MoveToward(_aim, wantAim ? 1 : 0, dt * AimSpeed * (wantAim ? 1 : 1.3f));
        float ease = _aim * _aim * (3 - 2 * _aim);
        bool scoped = _aim > .97f;
        Walker.Camera.Fov = scoped ? _optic.Fov : Mathf.Lerp(HipFov, Math.Max(RaiseFov, _optic.Fov), ease);
        Walker.SensitivityScale = Walker.Camera.Fov / HipFov;
        _model.Visible = !(scoped && _optic.HideGun);
        _scope.Visible = scoped && _optic.Reticle >= 0;
        if (scoped)
        {
            var vs = GetViewport().GetVisibleRect().Size;
            _scopeMaterial.SetShaderParameter("aspect", vs.X / vs.Y);
            _scopeMaterial.SetShaderParameter("reticle", _optic.Reticle);
        }
        UpdateLight();

        Walker.Aiming = _aim > .5f;
        UpdateCasings(dt);
        UpdateSway(dt, scoped);

        _cooldown -= dt;
        bool trigger = Input.IsMouseButtonPressed(MouseButton.Left);
        if (!trigger) Walker.FireLockedUntilRelease = false;
        bool newPull = trigger && !_triggerWasDown;
        _triggerWasDown = trigger;
        if (captured && trigger && (Automatic || newPull) && !Walker.FireLockedUntilRelease && _cooldown <= 0 && !Reloading) Fire();
        if (captured && Input.IsPhysicalKeyPressed(Key.V) && !_vWasDown && Def.Auto) { Automatic = !Automatic; _click.Play(); }
        _vWasDown = Input.IsPhysicalKeyPressed(Key.V);
        _noMagsToast -= dt;
        if (captured && Input.IsPhysicalKeyPressed(Key.R) && !Reloading && Ammo < MagazineCapacity)
        {
            if (Kit.Current?.NextMagazine() is { } next)
            {
                _reload = 0; _clack.Play();
                _reloadSpeed = next.FromPack ? .62f : 1;   // digging in the backpack takes longer than a vest pouch
            }
            else if (_noMagsToast <= 0) { DeadRecon.UI.Hud.Toast("No magazines left. Search the bodies."); _noMagsToast = 3; }
        }
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
            _flashLight.LightEnergy = on ? 2.5f : 0;
            _flashSprite.Rotation = new Vector3(0, 0, _rng.Randf() * MathF.Tau);
            _flashSprite.Scale = Vector3.One * (on ? _rng.RandfRange(.8f, 1.4f) : 1);
            if (!on) _flash = -1;
        }
    }

    private void Fire()
    {
        if (!Walker.CanShoot) return;
        Walker.SinceShot = 0;
        if (Kit.Current == null || !Kit.Current.Shoot()) { _click.Play(); _cooldown = .25f; return; }
        ShotsFired++;
        _cooldown = 60f / RoundsPerMinute;
        _kick = Math.Min(_kick + 1, 1.6f);
        if (_handling.Flash > 0) _flash = 0;
        if (Suppressed)
        {
            _quietShot.PitchScale = 1 + _rng.RandfRange(-.05f, .05f);
            _quietShot.Play();
        }
        else
        {
            _shot.PitchScale = 1 + _rng.RandfRange(-.06f, .06f);
            _shot.VolumeDb = _handling.Noise > 1 ? 0 : -2;
            _shot.Play();
            DeadRecon.Audio.Ambience.Current?.Echo(Walker.GlobalPosition);
        }
        // The bullet leaves where you were aiming *before* this shot's recoil moves the view.
        // (Applying recoil first sent every bullet ~0.5 degrees high: a metre over the head at 100 m.)
        var cam = Walker.Camera.GlobalTransform;
        float spread = Mathf.Lerp(HipSpread * (LaserOn ? .55f : 1), AimSpread, _aim);
        var dir = (-cam.Basis.Z + cam.Basis.X * _rng.RandfRange(-spread, spread) + cam.Basis.Y * _rng.RandfRange(-spread, spread)).Normalized();

        float steady = (Walker.Crouching ? .75f : 1) * _handling.Recoil;
        Walker.AddRecoil(Mathf.DegToRad(Mathf.Lerp(1.1f, .55f, _aim) * steady), Mathf.DegToRad(_rng.RandfRange(-.4f, .4f) * steady));
        EjectCasing();
        MuzzleSmoke();
        Hearing.Make(cam.Origin, Hearing.Gunshot * _handling.Noise, Walker);
        // The sight is zeroed at 100 m: the bore points a hair up so the bullet's drop brings it back onto the
        // crosshair there. Closer it's a touch high (cm), further it falls: hold over at 200 m and beyond.
        dir = dir.Rotated(cam.Basis.X, DeadRecon.Combat.Ballistics.ZeroAngle(100));
        var muzzle = Scoped || _muzzle == null ? cam.Origin - cam.Basis.Y * .06f + dir * .4f : _muzzle.GlobalPosition;
        var origin = cam.Origin;
        if (DeadRecon.Combat.Ballistics.Current is not { } ballistics) return;
        ballistics.Fire(origin, dir, muzzle, Walker.GetRid(), hit => Land(hit, origin, dir));
        // Shotguns: the rest of the pellets, in a cone round the first.
        for (int p = 1; p < Def.Pellets; p++)
        {
            var pd = (dir + cam.Basis.X * _rng.RandfRange(-Def.PelletSpread, Def.PelletSpread) + cam.Basis.Y * _rng.RandfRange(-Def.PelletSpread, Def.PelletSpread)).Normalized();
            ballistics.Fire(origin, pd, muzzle, Walker.GetRid(), hit => Land(hit, origin, pd), tracer: false);
        }
    }

    /// <summary>Where a bullet ended up (called when it arrives, a few frames after the shot).</summary>
    private void Land(DeadRecon.Combat.Ballistics.Hit? arrived, Vector3 origin, Vector3 dir)
    {
        if (arrived is not { } h) { LastHit = "nothing"; return; }
        LastPoint = h.Point;
        if (h.Trunk is { } plant)
        {
            Effects.Splinters(this, h.Point, h.Normal, dir);
            DeadRecon.World.Foliage.ReportHit(plant, h.Point, dir, Damage);
            LastHit = $"tree trunk at {h.Distance:0.0} m";
            return;
        }
        var collider = h.Collider;
        LastHit = $"{((collider as Node)?.Name.ToString() ?? collider?.GetType().Name ?? "?")} at {h.Distance:0.0} m";
        if (collider is IDamageable target)
        {
            target.TakeHit(Damage, h.Point, origin);
            HitConfirmed?.Invoke(target is DeadRecon.AI.Guard { State: DeadRecon.AI.Guard.Mood.Dead });
        }
        else if (collider is Node { } node && node.IsInGroup("wood")) Effects.Splinters(this, h.Point, h.Normal, dir);
        else Effects.Impact(this, h.Point, h.Normal);
    }

    private bool _vWasDown;

    /// <summary>
    /// Scope sway: a slow figure-eight from breathing and heartbeat, bigger standing than crouched and much bigger
    /// while moving. Shift holds your breath: steady for ~4 s, then a shaky gasp while you recover.
    /// </summary>
    private void UpdateSway(float dt, bool scoped)
    {
        _swayTime += dt;
        bool hold = scoped && Input.IsPhysicalKeyPressed(Key.Shift) && Breath > 0 && !_breathSpent;
        HoldingBreath = hold;
        if (hold) Breath = Math.Max(0, Breath - dt / 4f);
        else Breath = Math.Min(1, Breath + dt / (_breathSpent ? 5f : 3f));
        if (Breath <= 0 && !_breathSpent) { _breathSpent = true; _breathShake = 1; }
        if (_breathSpent && Breath > .6f) _breathSpent = false;
        _breathShake = Math.Max(0, _breathShake - dt / 2.5f);

        float moving = Math.Min(new Vector2(Walker.Velocity.X, Walker.Velocity.Z).Length() / 2f, 1);
        // Ghost Recon-ish: a gentle drift you can work with, not Arma's wobble.
        float amp = (Walker.Crouching ? .0009f : .0018f) * (1 + 2.5f * moving) * (1 + 2 * _breathShake);
        amp *= _handling.Sway;
        if (hold) amp *= .12f;
        float t = _swayTime;
        var sway = new Vector2(MathF.Sin(t * 1.9f) * .5f + MathF.Sin(t * .7f) * .5f, MathF.Sin(t * .95f) + MathF.Sin(t * 2.3f) * .25f) * amp;
        sway += new Vector2(MathF.Sin(t * 17) * amp * .25f * _breathShake, 0);  // gasping
        Walker.AimOffset = Walker.AimOffset.Lerp(scoped ? sway : Vector2.Zero, Math.Min(1, dt * 8));
    }

    /// <summary>One flying case: hand-made ballistics (tiny physics bodies tunnel through the terrain).</summary>
    private sealed class Casing { public MeshInstance3D Node = null!; public Vector3 Vel, Spin; public int Bounces; public float Age; }
    private readonly List<Casing> _flying = new();

    /// <summary>An empty case flies out of the ejection port to the right, bounces once or twice, tinks, and lies there.</summary>
    private void EjectCasing()
    {
        _brass ??= new StandardMaterial3D { AlbedoColor = new Color(.78f, .6f, .25f), Metallic = .9f, Roughness = .35f };
        var node = new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = .0055f, BottomRadius = .0062f, Height = .045f, RadialSegments = 8, Rings = 0 },
            MaterialOverride = _brass, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        GetTree().CurrentScene.AddChild(node);
        node.GlobalPosition = (_eject ?? _model).GlobalPosition;
        node.Rotation = new Vector3(_rng.Randf(), _rng.Randf(), MathF.PI / 2);
        var cam = Walker.Camera.GlobalTransform.Basis;
        _flying.Add(new Casing
        {
            Node = node,
            Vel = Walker.Velocity + cam.X * _rng.RandfRange(2.2f, 3.2f) + cam.Y * _rng.RandfRange(1.2f, 2f) + cam.Z * _rng.RandfRange(-.2f, .5f),
            Spin = new Vector3(_rng.RandfRange(-25, 25), _rng.RandfRange(-25, 25), _rng.RandfRange(-25, 25)),
        });
        _casings.Enqueue(node);
        while (_casings.Count > 40) { var old = _casings.Dequeue(); if (IsInstanceValid(old)) old.QueueFree(); }
    }

    private void UpdateCasings(float dt)
    {
        for (int k = _flying.Count - 1; k >= 0; k--)
        {
            var c = _flying[k];
            if (!IsInstanceValid(c.Node) || (c.Age += dt) > 4) { _flying.RemoveAt(k); continue; }
            c.Vel += Vector3.Down * 9.8f * dt;
            var from = c.Node.GlobalPosition;
            var to = from + c.Vel * dt;
            var q = PhysicsRayQueryParameters3D.Create(from, to + c.Vel.Normalized() * .01f, 1);
            var hit = GetWorld3D().DirectSpaceState.IntersectRay(q);
            if (hit.Count > 0)
            {
                var n = (Vector3)hit["normal"];
                c.Node.GlobalPosition = (Vector3)hit["position"] + n * .006f;
                if (c.Bounces == 0)
                {
                    var p = new AudioStreamPlayer3D { Stream = DeadRecon.Audio.Synth.Tink(_rng.RandiRange(0, 3)), Bus = "Effects", UnitSize = 1.5f, MaxDistance = 15, VolumeDb = -8 };
                    GetTree().CurrentScene.AddChild(p);
                    p.GlobalPosition = c.Node.GlobalPosition;
                    p.Play();
                    p.Finished += p.QueueFree;
                }
                c.Bounces++;
                c.Vel = (c.Vel - 2 * c.Vel.Dot(n) * n) * .3f;                  // bounce, losing most of its energy
                c.Spin *= .4f;
                if (c.Bounces >= 3 || c.Vel.Length() < .4f)
                {
                    // Rest: lying on its side on the ground.
                    c.Node.Rotation = new Vector3(MathF.PI / 2, c.Node.Rotation.Y, 0);
                    _flying.RemoveAt(k);
                }
                continue;
            }
            c.Node.GlobalPosition = to;
            c.Node.Rotation += c.Spin * dt;
        }
    }

    /// <summary>A small grey puff that hangs in the air at the muzzle and drifts away.</summary>
    private void MuzzleSmoke()
    {
        _smoke ??= new StandardMaterial3D
        {
            AlbedoTexture = Effects.Puff(), AlbedoColor = new Color(.75f, .75f, .72f, .18f), Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles,
            VertexColorUseAsAlbedo = true,
        };
        var fade = new Gradient();
        fade.SetColor(0, new Color(1, 1, 1, .9f));
        fade.SetColor(1, new Color(1, 1, 1, 0));
        var puff = new CpuParticles3D
        {
            Emitting = true, OneShot = true, Amount = 5, Lifetime = 1.4f, Explosiveness = .9f, LocalCoords = false,
            Direction = Vector3.Up, Spread = 40, InitialVelocityMin = .2f, InitialVelocityMax = .6f, Gravity = new Vector3(.2f, .35f, 0),
            ScaleAmountMin = .5f, ScaleAmountMax = 1f, Mesh = new QuadMesh { Size = new Vector2(.12f, .12f) }, MaterialOverride = _smoke, ColorRamp = fade,
        };
        GetTree().CurrentScene.AddChild(puff);
        // A little ahead of the barrel, so it never sits on top of the camera.
        puff.GlobalPosition = (_muzzle ?? _model).GlobalPosition - Walker.Camera.GlobalTransform.Basis.Z * .25f;
        GetTree().CreateTimer(1.6).Timeout += () => { if (IsInstanceValid(puff)) puff.QueueFree(); };
    }

    private void UpdateReload(float dt)
    {
        if (_reload < 0) return;
        _reload += dt * _reloadSpeed;
        // 0-0.35 mag out, 0.35-0.9 swap, 0.9-1.25 mag in, 1.35-1.7 charging handle back and forward.
        float mag = _reload < .35f ? _reload / .35f : _reload < .9f ? 1 : _reload < 1.25f ? 1 - (_reload - .9f) / .35f : 0;
        float ch = _reload < 1.35f ? 0 : _reload < 1.5f ? (_reload - 1.35f) / .15f : _reload < 1.7f ? 1 - (_reload - 1.5f) / .2f : 0;
        if (_magazine != null) _magazine.Position = _magazineRest + _magazineAxis * _magazineTravel * mag;
        if (_charging != null) _charging.Position = _chargingRest + _chargingAxis * _chargingTravel * ch;
        if (_reload >= 1.25f && _reload - dt < 1.25f) _clack.Play();
        if (_reload >= 1.5f && _reload - dt < 1.5f) _click.Play();
        if (_reload >= 1.75f) { _reload = -1; Kit.Current?.Reload(); }
    }

    private bool LaserOn => _laserFrom != null && Kit.Current?.LightOn == true;

    /// <summary>Weapon light on/off (L); the laser line and dot follow the gun.</summary>
    private void UpdateLight()
    {
        bool on = Kit.Current?.LightOn == true;
        if (_light != null) _light.Visible = on;
        Walker.Lit = on && _light != null;
        if (_laserBeam == null || _laserDot == null || _laserFrom == null) return;
        bool show = on && _model.Visible && IsInstanceValid(_laserBeam);
        _laserBeam.Visible = _laserDot.Visible = show;
        if (!show) return;
        var from = _laserFrom.GlobalPosition;
        var dir = -_laserFrom.GlobalTransform.Basis.Z;
        var q = PhysicsRayQueryParameters3D.Create(from, from + dir * 150);
        q.Exclude = new Godot.Collections.Array<Rid> { Walker.GetRid() };
        var hit = GetWorld3D().DirectSpaceState.IntersectRay(q);
        var end = hit.Count > 0 ? (Vector3)hit["position"] : from + dir * 150;
        if (DeadRecon.World.Foliage.RaycastTrunk(from, end) is { } wood) end = wood.Point;
        float len = from.DistanceTo(end);
        _laserBeam.GlobalTransform = new Transform3D(Basis.LookingAt(dir, Math.Abs(dir.Y) > .99f ? Vector3.Forward : Vector3.Up) * new Basis(Vector3.Right, MathF.PI / 2) * Basis.FromScale(new Vector3(1, len, 1)), from + dir * len / 2);
        _laserDot.GlobalPosition = end;
        _laserDot.Visible = hit.Count > 0;
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
        var p = new AudioStreamPlayer { Stream = stream, VolumeDb = db, MaxPolyphony = 4, Bus = "Effects" };
        AddChild(p);
        return p;
    }
}
