using System;
using System.Collections.Generic;
using DeadRecon.AI;
using DeadRecon.Combat;
using DeadRecon.Gear;
using DeadRecon.Player;
using DeadRecon.World;
using Godot;

namespace DeadRecon.People;

/// <summary>
/// One of yours: a soldier you rescued. At home he does his job (rests at the fire, stands guard, builds); on an
/// operation he follows you, holds where you tell him, goes where you point, and shoots enemies he can see when
/// he's allowed to (hold fire by default: stealth stays stealthy). Guards who see him shoot at him. Wounded to 0
/// he goes down and bleeds out unless you patch him up (F, uses a dressing).
/// </summary>
public partial class Comrade : CharacterBody3D, IDamageable
{
    public enum Order { Base, Follow, Hold, Move }

    public static readonly List<Comrade> All = new();

    public string Person = "";
    public string CharacterScene = "";
    public string WeaponId = WeaponDef.Default;
    public string Task = "rest";
    public int Slot;                              // formation position (0, 1, 2)
    public Walker Player = null!;
    public BakedWorld World = null!;
    /// <summary>Where his job is at home (the fire, a lookout spot, a building site).</summary>
    public Vector3 Post;

    public Order Orders { get; private set; } = Order.Base;
    public bool FireAtWill { get; private set; }
    public float Health { get; private set; } = 100;
    public bool Down => Health <= 0 && !Dead;
    public bool Dead { get; private set; }
    public Vector3 Chest => GlobalPosition + Vector3.Up * (_crouch > .5f ? .8f : 1.3f);
    /// <summary>He died (the camp remembers).</summary>
    public event Action<Comrade>? Died;

    private SoldierBody? _body;
    private Label3D _bubble = null!, _tag = null!;
    private AudioStreamPlayer3D _voice = null!, _shot = null!;
    private float _bubbleLeft, _cooldown, _scan, _crouch, _bleed = 90, _stuck, _sideLeft, _voicePitch;
    private Vector3 _hold, _side;
    private Guard? _foe;
    private readonly RandomNumberGenerator _rng = new();
    private bool _fWas;
    private const float Gravity = 9.8f, Range = 85;

    public override void _Ready()
    {
        All.Add(this);
        _rng.Seed = (ulong)Person.GetHashCode();
        AddChild(new CollisionShape3D { Shape = new CapsuleShape3D { Radius = .32f, Height = 1.8f }, Position = new Vector3(0, .9f, 0) });
        FloorMaxAngle = Mathf.DegToRad(50);
        CollisionLayer = 1; CollisionMask = 1;
        var def = WeaponDef.Get(WeaponId);
        if (ResourceLoader.Exists(CharacterScene))
        {
            _body = new SoldierBody { CharacterScene = CharacterScene, GunScene = def.Model, WeaponId = def.Id };
            AddChild(_body);
        }
        _voicePitch = 95 + 45 * _rng.Randf();
        _voice = new AudioStreamPlayer3D { UnitSize = 6, MaxDistance = 60, Bus = "Voices", Position = new Vector3(0, 1.5f, 0) };
        _shot = new AudioStreamPlayer3D { Stream = def.Noise < .5f ? Effects.SuppressedShot() : Effects.Gunshot(), UnitSize = 12, MaxDistance = 900, VolumeDb = -2, Bus = "Effects", MaxPolyphony = 3 };
        AddChild(_voice); AddChild(_shot);
        _bubble = Label(2.4f, 36, new Color(.8f, 1f, .8f));
        _tag = Label(2.1f, 26, new Color(.6f, .9f, .6f, .8f));
        _tag.Text = Person;
        _hold = GlobalPosition;
    }

    private Label3D Label(float y, int size, Color c)
    {
        var l = new Label3D
        {
            FontSize = size, OutlineSize = 8, PixelSize = .0007f, FixedSize = true, Position = new Vector3(0, y, 0), Width = 900,
            AutowrapMode = TextServer.AutowrapMode.WordSmart, Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, NoDepthTest = true, Modulate = c,
        };
        AddChild(l);
        return l;
    }

    public override void _ExitTree() => All.Remove(this);

    // ================================================================= orders

    public void Command(Order o, Vector3? at = null)
    {
        if (Down || Dead) return;
        Orders = o;
        if (o == Order.Hold) _hold = GlobalPosition;
        if (o == Order.Move && at is { } p) _hold = p;
        string[] lines = o switch
        {
            Order.Follow => new[] { "Right behind you.", "With you.", "Moving." },
            Order.Hold => new[] { "Holding here.", "I'll stay put.", "Not moving. Not even for snacks." },
            Order.Move => new[] { "On my way.", "Going.", "There? Alright." },
            _ => new[] { "Back to it." },
        };
        Say(lines[_rng.RandiRange(0, lines.Length - 1)]);
    }

    public void SetFire(bool atWill)
    {
        if (Down || Dead) return;
        FireAtWill = atWill;
        Say(atWill ? "Weapons free." : "Holding fire.");
    }

    // ================================================================= per frame

    public override void _PhysicsProcess(double delta)
    {
        if (Dead) return;
        float dt = (float)delta;
        UpdateLabels(dt);
        if (Down) { BleedOut(dt); return; }

        // Who's there to shoot?
        _scan -= dt;
        if (_scan <= 0) { _scan = .35f; _foe = Orders == Order.Base ? null : SpotFoe(); }

        Vector3 goal = GlobalPosition;
        float speed = 0;
        Vector3? look = null;
        bool crouch = false;
        switch (Orders)
        {
            case Order.Follow:
            {
                // Loose file behind you: left, right, further back.
                var basis = Player.GlobalTransform.Basis;
                var offset = Slot switch { 0 => new Vector3(-2.2f, 0, 2.6f), 1 => new Vector3(2.2f, 0, 2.8f), _ => new Vector3(0, 0, 4.6f) };
                goal = Player.GlobalPosition + basis * offset;
                float d = Flat(goal - GlobalPosition);
                speed = d > 9 ? 4.6f : d > 1.2f ? (Player.Running ? 4.4f : Player.Crouching ? 1.6f : 2.3f) : 0;
                crouch = Player.Crouching;
                if (Flat(Player.GlobalPosition - GlobalPosition) > 120) GlobalPosition = goal with { Y = World.HeightAt(goal.X, goal.Z) + .3f };
                break;
            }
            case Order.Hold:
            case Order.Move:
            {
                goal = _hold;
                float d = Flat(goal - GlobalPosition);
                speed = d > 1 ? (d > 12 ? 4.2f : 2.4f) : 0;
                crouch = d <= 1 && Orders == Order.Hold;
                if (d <= 1 && Orders == Order.Move) Orders = Order.Hold;
                break;
            }
            default:
                (goal, speed, look, crouch) = AtHome(dt);
                break;
        }
        // Fighting: face him; stop to shoot if you're not running somewhere important.
        if (_foe != null)
        {
            look = _foe.GlobalPosition;
            if (Orders == Order.Follow && Flat(goal - GlobalPosition) < 6) speed = 0;
            if (FireAtWill)
            {
                _cooldown -= dt;
                if (_cooldown <= 0 && speed < 3) Shoot(_foe);
            }
        }
        Move(goal, speed, look, dt);
        _crouch = Mathf.MoveToward(_crouch, crouch || (_foe != null && speed == 0) ? 1 : 0, dt * 4);
        if (_body != null)
        {
            _body.Speed = new Vector2(Velocity.X, Velocity.Z).Length();
            _body.Crouch = _crouch;
            _body.Ready = Mathf.MoveToward(_body.Ready, _foe != null || Orders != Order.Base ? 1 : 0, dt * 3);
            _body.AimPitch = _foe != null ? MathF.Atan2(_foe.GlobalPosition.Y + 1.2f - (GlobalPosition.Y + 1.5f), Flat(_foe.GlobalPosition - GlobalPosition)) : 0;
        }
    }

    /// <summary>His job at home.</summary>
    private (Vector3, float, Vector3?, bool) AtHome(float dt)
    {
        float d = Flat(Post - GlobalPosition);
        float speed = d > .8f ? 1.4f : 0;
        bool sit = Task == "rest" && d <= .8f;
        Vector3? look = Task == "guard" ? GlobalPosition + (GlobalPosition - (DeadRecon.Base.HomeBase.Current?.Radio ?? GlobalPosition)).Normalized() * 5 : null;
        if (Task == "build" && d <= .8f) look = Post + Vector3.Forward;
        return (Post, speed, look, sit || (Task == "build" && d <= .8f && (Time.GetTicksMsec() / 700) % 2 == 0));
    }

    private void Move(Vector3 goal, float speed, Vector3? look, float dt)
    {
        var to = goal - GlobalPosition; to.Y = 0;
        var v = Velocity;
        if (speed > 0 && to.Length() > .2f)
        {
            var dir = to.Normalized();
            if (_sideLeft > 0) { _sideLeft -= dt; dir = (dir + _side).Normalized(); }
            v.X = dir.X * speed; v.Z = dir.Z * speed;
            look ??= GlobalPosition + dir;
        }
        else { v.X = 0; v.Z = 0; }
        v.Y = IsOnFloor() ? -.5f : v.Y - Gravity * dt;
        Velocity = v;
        var before = GlobalPosition;
        MoveAndSlide();
        GlobalPosition = Foliage.PushOut(GlobalPosition, .35f);
        float ground = World.HeightAt(GlobalPosition.X, GlobalPosition.Z);
        if (GlobalPosition.Y < ground - .25f) { GlobalPosition = GlobalPosition with { Y = ground + .05f }; Velocity = Vector3.Zero; }
        if (speed > 0 && Flat(GlobalPosition - before) < speed * dt * .3f) _stuck += dt; else _stuck = Math.Max(0, _stuck - dt);
        if (_stuck > 1) { _stuck = 0; _side = to.Normalized().Cross(Vector3.Up) * (_rng.Randf() < .5f ? 1 : -1); _sideLeft = 1; }
        if (look is { } l)
        {
            var d = l - GlobalPosition; d.Y = 0;
            if (d.LengthSquared() > .01f) Rotation = new Vector3(0, Mathf.LerpAngle(Rotation.Y, MathF.Atan2(-d.X, -d.Z), Math.Min(1, dt * 6)), 0);
        }
    }

    // ================================================================= fighting

    private Vector3 Eye => GlobalPosition + Vector3.Up * (_crouch > .5f ? 1.05f : 1.6f);

    private Guard? SpotFoe()
    {
        Guard? best = null;
        float bestD = Range;
        foreach (var g in Guard.All)
        {
            if (g.State == Guard.Mood.Dead) continue;
            float d = g.GlobalPosition.DistanceTo(GlobalPosition);
            if (d > bestD) continue;
            // He fights back the moment the enemy's onto us; otherwise only what he can actually see.
            if (!Sees(g.GlobalPosition + Vector3.Up * 1.3f)) continue;
            best = g; bestD = d;
        }
        return best;
    }

    public bool Sees(Vector3 p)
    {
        var eye = Eye;
        if (Foliage.RaycastTrunk(eye, p) != null) return false;
        var q = PhysicsRayQueryParameters3D.Create(eye, p);
        q.Exclude = new Godot.Collections.Array<Rid> { GetRid(), Player.GetRid() };
        var hit = GetWorld3D().DirectSpaceState.IntersectRay(q);
        if (hit.Count > 0 && hit["collider"].AsGodotObject() is not Guard) return false;
        // Leaves: thick undergrowth hides people (same rule the guards use).
        return Foliage.Transmission(eye, p) > .35f;
    }

    private void Shoot(Guard foe)
    {
        var def = WeaponDef.Get(WeaponId);
        _cooldown = Math.Max(60f / def.Rpm, .12f) + (def.Auto ? (_rng.Randf() < .25f ? _rng.RandfRange(.6f, 1.2f) : 0) : _rng.RandfRange(.3f, .8f));
        _shot.PitchScale = .95f + .1f * _rng.Randf();
        _shot.Play();
        Hearing.Make(GlobalPosition, Hearing.Gunshot * def.Noise, this);
        var muzzle = _body?.Muzzle?.GlobalPosition ?? Eye;
        var target = foe.GlobalPosition + Vector3.Up * 1.25f;
        float dist = Eye.DistanceTo(target);
        float chance = Math.Clamp(.6f * (1 - dist / Range) + .15f, .08f, .65f) * (def.AimSpread < .001f ? 1.15f : 1);
        if (_rng.Randf() < chance)
        {
            Effects.Tracer(this, muzzle, target, enemy: false);
            foe.TakeHit(def.Damage * def.Pellets * (dist > 25 && def.Pellets > 1 ? .3f : 1), target, Eye);
        }
        else
        {
            var miss = target + new Vector3(_rng.RandfRange(-1.5f, 1.5f), _rng.RandfRange(-.6f, 1f), _rng.RandfRange(-1.5f, 1.5f));
            Effects.Tracer(this, muzzle, miss, enemy: false);
        }
    }

    public void TakeHit(float damage, Vector3 at, Vector3 from)
    {
        if (Dead || Down) return;
        Health -= damage;
        Effects.BloodHit(this, at, at - from);
        if (!FireAtWill) { FireAtWill = true; Say("They're shooting at us! Weapons free!"); }
        if (Health <= 0)
        {
            Health = 0;
            _bleed = 90;
            Say("I'm hit! I'm down!");
            GD.Print($"[squad] {Person} is down");
        }
        else if (_rng.Randf() < .4f) Say("Ow! That one stung.");
    }

    private void BleedOut(float dt)
    {
        _bleed -= dt;
        _crouch = Mathf.MoveToward(_crouch, 1, dt * 4);
        if (_body != null) { _body.Crouch = 1; _body.Speed = 0; _body.Ready = 0; }
        Velocity = new Vector3(0, IsOnFloor() ? -.5f : Velocity.Y - Gravity * dt, 0);
        MoveAndSlide();
        _tag.Text = $"{Person}  DOWN  {_bleed:0} s";
        bool f = Input.IsPhysicalKeyPressed(Key.F), fPressed = f && !_fWas;
        _fWas = f;
        if (Player.GlobalPosition.DistanceTo(GlobalPosition) < 2.2f && !Player.Dead)
        {
            bool have = (Kit.Current?.Medical ?? 0) > 0;
            DeadRecon.UI.Hud.Prompt(have ? $"F   Patch up {Person}" : $"{Person} needs a dressing. You have none.");
            if (fPressed && have && Input.MouseMode == Input.MouseModeEnum.Captured)
            {
                var kit = Kit.Current!;
                var item = kit.AllItems.FirstOrDefaultMedical();
                if (item != null) { item.Count--; if (item.Count <= 0) kit.Remove(item); else kit.Save(); }
                Health = 45;
                _tag.Text = Person;
                Say("Thanks. I owe you. Again.");
                return;
            }
        }
        if (_bleed <= 0)
        {
            Dead = true;
            _tag.Visible = false;
            _body?.Ragdoll(GlobalPosition + Vector3.Up, Vector3.Down, .3f);
            GetChild<CollisionShape3D>(0).SetDeferred(CollisionShape3D.PropertyName.Disabled, true);
            DeadRecon.UI.Hud.Announce($"{Person} is gone", "the camp will remember");
            GD.Print($"[squad] {Person} died");
            Died?.Invoke(this);
        }
    }

    // ================================================================= talking

    public void Say(string line)
    {
        _bubble.Text = line;
        _bubbleLeft = 3.5f;
        if (Player.GlobalPosition.DistanceTo(GlobalPosition) < 60)
        {
            _voice.Stream = DeadRecon.Audio.Synth.Voice(line, _voicePitch, Person.GetHashCode());
            _voice.Play();
        }
    }

    private void UpdateLabels(float dt)
    {
        _bubbleLeft -= dt;
        float dist = Player.GlobalPosition.DistanceTo(GlobalPosition), zoom = Player.Camera.Fov / 70f;
        _bubble.Visible = _bubbleLeft > 0 && dist < 50;
        _bubble.Modulate = new Color(.8f, 1f, .8f, Math.Clamp(_bubbleLeft, 0, 1));
        _tag.Visible = !Dead && (dist < 30 || Down);
        _bubble.PixelSize = _tag.PixelSize = .0007f * zoom;
    }

    private static float Flat(Vector3 v) => new Vector2(v.X, v.Z).Length();
}

internal static class MedicalExt
{
    public static Item? FirstOrDefaultMedical(this IEnumerable<Item> items)
    {
        foreach (var i in items) if (i.Def == ItemDef.Bandage) return i;
        foreach (var i in items) if (i.Def.Kind == ItemKind.Medical) return i;
        return null;
    }
}
