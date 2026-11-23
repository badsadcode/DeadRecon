using System;
using System.Collections.Generic;
using DeadRecon.Combat;
using Godot;

namespace DeadRecon.Player;

/// <summary>
/// First-person test rifle built from a BadGun .glb. Uses the gun's own metadata (glTF extras "badgun"):
/// SOCKET_ADS (eye position when aiming), SOCKET_Muzzle, and parts with "motion" (Trigger, Magazine, ChargingHandle).
/// LMB fire (full auto), RMB aim, R reload. Placeholder effects and sounds come from Combat.Effects.
/// Bullets are hitscan; terrain and characters have collision, trees don't (yet), so trees don't stop bullets.
/// </summary>
public partial class Weapon : Node3D
{
    public Walker Walker = null!;
    public string ScenePath = "res://assets/weapons/BadGun_rifle_vietnam_97232.glb";

    // Test-gun numbers (BadGun doesn't ship ballistics; these are ours).
    private const float RoundsPerMinute = 600, Range = 900, HipSpread = .018f, AimSpread = .0015f;
    private const int MagazineSize = 20;
    private const float Damage = 34; // three to the body; the guard triples it for headshots
    private const float RaiseFov = 40, ScopeFov = 11, HipFov = 70, AimSpeed = 7;  // ScopeFov 11 = roughly 6x
    private static readonly Vector3 HipOffset = new(.16f, -.21f, -.36f); // where the grip sits relative to the eye

    public int Ammo { get; private set; } = MagazineSize;
    public bool Loaded { get; private set; }
    public int ShotsFired { get; private set; }
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
        _flashSprite = new MeshInstance3D { Mesh = new QuadMesh { Size = new Vector2(.28f, .28f) }, MaterialOverride = Effects.FlashMaterial(), Visible = false, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        (_muzzle ?? _model).AddChild(_flashLight);
        (_muzzle ?? _model).AddChild(_flashSprite);

        // Scope view: a 3D scope can't magnify, so at full aim the gun hides and this overlay + narrow FOV takes over.
        _scopeMaterial = new ShaderMaterial { Shader = GD.Load<Shader>("res://game/Player/scope.gdshader") };
        _scope = new ColorRect { Material = _scopeMaterial, Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore };
        _scope.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        var layer = new CanvasLayer { Layer = -1 }; // under the HUD text
        layer.AddChild(_scope);
        AddChild(layer);

        _shot = Player(Effects.Gunshot(), -2);
        _click = Player(Effects.Click(1800, .03f), -8);
        _clack = Player(Effects.Click(700, .07f), -6);
        Loaded = true;
    }

    /// <summary>For automated screenshots: hold aim fully on/off, fire one muzzle flash.</summary>
    public void ForceAim(bool on) { _forceAim = on; _aim = on ? 1 : 0; }
    public void FlashForShot() { _flash = 0; }
    public void TestFire() { _cooldown = 0; Fire(); }
    public void TestReload() { Ammo = MagazineSize; }

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
        bool trigger = Input.IsMouseButtonPressed(MouseButton.Left);
        if (!trigger) Walker.FireLockedUntilRelease = false;
        if (captured && trigger && !Walker.FireLockedUntilRelease && _cooldown <= 0 && !Reloading) Fire();
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
        if (Walker.Dead) return;
        if (Ammo <= 0) { _click.Play(); _cooldown = .25f; return; }
        Ammo--;
        ShotsFired++;
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
        Hearing.Make(cam.Origin, Hearing.Gunshot, Walker);
        // Trunks aren't physics objects; ask the foliage registry whether one is closer than whatever the ray hit.
        var end = hit.Count > 0 ? (Vector3)hit["position"] : cam.Origin + dir * Range;
        if (DeadRecon.World.Foliage.RaycastTrunk(cam.Origin, end) is { } wood)
        {
            Effects.Splinters(this, wood.Point, wood.Normal, dir);
            DeadRecon.World.Foliage.ReportHit(wood.Plant, wood.Point, dir, Damage);
            return;
        }
        if (hit.Count == 0) return;
        var at = (Vector3)hit["position"];
        var collider = hit["collider"].AsGodotObject();
        if (collider is IDamageable target) target.TakeHit(Damage, at, cam.Origin);
        else if (collider is Node { } node && node.IsInGroup("wood")) Effects.Splinters(this, at, (Vector3)hit["normal"], dir);
        else Effects.Impact(this, at, (Vector3)hit["normal"]);
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
}
