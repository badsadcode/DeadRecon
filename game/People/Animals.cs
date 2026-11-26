using System;
using System.Collections.Generic;
using System.Linq;
using DeadRecon.Combat;
using DeadRecon.Player;
using DeadRecon.World;
using Godot;

namespace DeadRecon.People;

/// <summary>A kind of animal (BadZoo model). "animals" content section; mods add more by id.
/// Role: dog (village dog: barks at strangers), poultry, livestock, prey (flees), predator (warns, then attacks if
/// you're too close), basker (lies still by water, slips away). Where: village, jungle, river.</summary>
public sealed record AnimalDef(
    string Id, string Model, string Role, string Where, float Health = 40, float Walk = .8f, float Run = 5,
    float Flee = 25, int GroupMin = 1, int GroupMax = 1, string Call = "", float Weight = 1);

/// <summary>
/// One animal: wanders, eats, sits and sleeps; runs from gunfire and from you (prey); barks at strangers (village
/// dogs: guards hear it too); warns, then charges (predators). Shootable. Moves over the ground without physics;
/// a box of collision follows it so bullets and the player hit it.
/// </summary>
public partial class Animal : Node3D, IDamageable
{
    public AnimalDef Def = null!;
    public Walker Player = null!;
    public BakedWorld World = null!;
    /// <summary>Home ground: wanders within <see cref="Range"/> of it.</summary>
    public Vector3 Home;
    public float Range = 12;
    /// <summary>Belongs to this settlement (shooting it upsets them). -1: wild.</summary>
    public int Site = -1;
    public int Seed;

    public bool Dead { get; private set; }
    public static readonly List<Animal> All = new();

    private AnimationPlayer? _anim;
    private Node3D _model = null!;
    private AudioStreamPlayer3D _voice = null!;
    private readonly RandomNumberGenerator _rng = new();
    private Vector3 _goal;
    private float _speed, _think, _health, _callIn, _fear, _angry, _clipSpeedWalk = .5f, _clipSpeedRun = 3, _attackCool;
    private string _clip = "", _rest = "Idle";
    private float _restLeft;

    public override void _Ready()
    {
        All.Add(this);
        _rng.Seed = (ulong)Seed;
        _health = Def.Health;
        _model = ResourceLoader.Load<PackedScene>(Def.Model).Instantiate<Node3D>();
        _model.RotationDegrees = new Vector3(0, 180, 0);   // BadZoo faces +Z, Godot actors face -Z
        AddChild(_model);
        foreach (var mi in DeadRecon.Gear.RifleBuilder.FindAll<GeometryInstance3D>(_model)) mi.CastShadow = GeometryInstance3D.ShadowCastingSetting.On;
        _anim = DeadRecon.Gear.RifleBuilder.FindAll<AnimationPlayer>(_model).FirstOrDefault();
        if (_anim != null)
        {
            foreach (var c in new[] { "Idle", "Walk", "Trot", "Gallop", "Eat", "Sit", "Sleep" })
                if (_anim.HasAnimation(c)) _anim.GetAnimation(c).LoopMode = Animation.LoopModeEnum.Linear;
            _clipSpeedWalk = ClipSpeed("Walk", .5f);
            _clipSpeedRun = ClipSpeed(Def.Run > 3 ? "Gallop" : "Trot", 3);
        }
        // Collision so bullets and the player hit it: a box round the model.
        var box = DeadRecon.Gear.RifleBuilder.Bounds(_model);
        var body = new AnimalBody { Owner2 = this, CollisionLayer = 1, CollisionMask = 0 };
        body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = box.Size * new Vector3(.8f, .85f, .85f) }, Position = box.GetCenter() });
        AddChild(body);
        _voice = new AudioStreamPlayer3D { UnitSize = 5, MaxDistance = 70, Bus = "Effects", VolumeDb = -4, Position = new Vector3(0, box.End.Y, 0) };
        AddChild(_voice);
        _callIn = _rng.RandfRange(4, 30);
        _goal = GlobalPosition;
        Hearing.Made += OnHeard;
        Play("Idle");
    }

    public override void _ExitTree()
    {
        All.Remove(this);
        Hearing.Made -= OnHeard;
    }

    private float ClipSpeed(string clip, float fallback)
    {
        if (_anim == null || !_anim.HasAnimation(clip)) return fallback;
        // BadZoo puts the forward speed a cycle covers in the clip's extras; Godot keeps glTF extras as meta.
        var a = _anim.GetAnimation(clip);
        if (a.HasMeta("extras") && a.GetMeta("extras").AsGodotDictionary() is { } ex && ex.TryGetValue("speed", out var sp) && (float)sp.AsDouble() > .01f) return (float)sp.AsDouble();
        return fallback;
    }

    private void Play(string clip, float scale = 1)
    {
        if (_anim == null) return;
        if (!_anim.HasAnimation(clip)) clip = "Idle";
        if (_clip != clip) { _anim.Play(clip, .25); _clip = clip; }
        _anim.SpeedScale = scale;
    }

    private void Call()
    {
        if (Def.Call == "" || Player.GlobalPosition.DistanceTo(GlobalPosition) > 70) return;
        _voice.Stream = DeadRecon.Audio.Synth.Animal(Def.Call, _rng.RandiRange(0, 3));
        _voice.PitchScale = _rng.RandfRange(.92f, 1.08f);
        _voice.Play();
    }

    // ================================================================= per frame

    public override void _Process(double delta)
    {
        if (Dead) return;
        float dt = (float)delta;
        _fear = Math.Max(0, _fear - dt);
        _attackCool -= dt;
        var me = GlobalPosition;
        float toPlayer = Player.Dead ? 999 : new Vector2(Player.GlobalPosition.X - me.X, Player.GlobalPosition.Z - me.Z).Length();
        bool sneaking = Player.Crouching && Player.Velocity.LengthSquared() < 2;

        // Reactions to you.
        switch (Def.Role)
        {
            case "prey" when toPlayer < Def.Flee * (sneaking ? .45f : 1):
            case "basker" when toPlayer < Def.Flee * (sneaking ? .5f : 1):
                Spook(Player.GlobalPosition);
                break;
            case "predator":
                if (toPlayer < 18 && _angry <= 0 && _fear <= 0) { _angry = 1; Play("Roar"); Call(); _restLeft = 1.4f; }
                if (_angry > 0 && toPlayer > 28) _angry = 0;
                break;
            case "dog":
                _callIn -= dt;
                bool stranger = Site < 0 || Support.LevelAt(Site) < Support.Level.Friendly;
                if (stranger && toPlayer < 16 && _callIn <= 0 && _fear <= 0)
                {
                    _callIn = _rng.RandfRange(1.2f, 3);
                    Call();
                    _goal = me; _restLeft = .8f; _speed = 0;
                    Hearing.Make(me, 35, this);   // guards nearby hear the dogs going off
                    Face(Player.GlobalPosition, dt * 8);
                }
                break;
        }
        if (Def.Role != "dog" && Def.Role != "predator")
        {
            _callIn -= dt;
            if (_callIn <= 0) { _callIn = _rng.RandfRange(8, 40); if (toPlayer < 40) Call(); }
        }

        // Predator charge.
        if (Def.Role == "predator" && _angry > 0 && _fear <= 0 && toPlayer < 9)
        {
            _goal = Player.GlobalPosition; _speed = Def.Run;
            if (toPlayer < 1.6f && _attackCool <= 0)
            {
                _attackCool = 1.6f;
                Play("Attack");
                Player.TakeHit(28, Player.Chest, me);
            }
        }

        // Think: where next.
        _think -= dt;
        if (_think <= 0 && _fear <= 0 && !(Def.Role == "predator" && _angry > 0 && toPlayer < 9))
        {
            _think = _rng.RandfRange(3, 9);
            if (_rng.Randf() < .45f)
            {
                // Rest a while: eat, sit, sleep (by what the animal is).
                string[] rests = Def.Role switch { "poultry" => new[] { "Eat", "Eat", "Idle" }, "dog" => new[] { "Sit", "Sleep", "Idle" }, "basker" => new[] { "Sleep", "Sleep", "Idle" }, _ => new[] { "Eat", "Idle", "Sleep" } };
                _rest = rests[_rng.RandiRange(0, rests.Length - 1)];
                _restLeft = _think;
                _speed = 0;
            }
            else
            {
                float a = _rng.Randf() * MathF.Tau, r = Range * MathF.Sqrt(_rng.Randf());
                _goal = Home + new Vector3(MathF.Cos(a) * r, 0, MathF.Sin(a) * r);
                _speed = Def.Walk;
                _restLeft = 0;
            }
        }
        Move(dt);
    }

    private void Move(float dt)
    {
        var to = _goal - GlobalPosition; to.Y = 0;
        float d = to.Length();
        if (_speed > 0 && d > .4f)
        {
            var dir = to / d;
            var next = GlobalPosition + dir * Math.Min(d, _speed * dt);
            // Don't walk into rivers (unless you're a crocodile) or off cliffs.
            var (gx, gz) = World.ToSample(next.X, next.Z);
            bool wet = World.IsWet((int)gx, (int)gz);
            if (wet && Def.Where != "river") { _goal = GlobalPosition; _speed = 0; }
            else
            {
                next = Foliage.PushOut(next, .3f);
                next.Y = World.HeightAt(next.X, next.Z);
                GlobalPosition = next;
                Face(GlobalPosition + dir, dt * 6);
                bool running = _speed > Def.Walk * 1.5f;
                Play(running ? (Def.Run > 3 ? "Gallop" : "Trot") : "Walk", _speed / (running ? _clipSpeedRun : _clipSpeedWalk));
            }
        }
        else
        {
            if (_speed > 0) { _speed = 0; _restLeft = _rng.RandfRange(2, 5); _rest = "Idle"; }
            if (_clip is not ("Roar" or "Attack" or "Hit") || !(_anim?.IsPlaying() ?? false)) Play(_restLeft > 0 ? _rest : "Idle");
            _restLeft -= dt;
            var p = GlobalPosition;
            GlobalPosition = p with { Y = World.HeightAt(p.X, p.Z) };
        }
    }

    private void Face(Vector3 at, float rate)
    {
        var d = at - GlobalPosition; d.Y = 0;
        if (d.LengthSquared() < .001f) return;
        Rotation = new Vector3(0, Mathf.LerpAngle(Rotation.Y, MathF.Atan2(-d.X, -d.Z), Math.Min(1, rate)), 0);
    }

    /// <summary>Run away from something, for a while.</summary>
    private void Spook(Vector3 from)
    {
        if (Dead) return;
        var away = (GlobalPosition - from) with { Y = 0 };
        if (away.LengthSquared() < .01f) away = Vector3.Forward;
        _goal = GlobalPosition + away.Normalized().Rotated(Vector3.Up, _rng.RandfRange(-.5f, .5f)) * _rng.RandfRange(25, 45);
        _speed = Def.Run;
        _fear = _rng.RandfRange(5, 9);
        _restLeft = 0;
        _angry = 0;
        if (Def.Role is "prey" or "poultry" or "livestock" && _rng.Randf() < .5f) Call();
        // A spooked animal settles wherever it ends up.
        Home = _goal;
    }

    private void OnHeard(Vector3 at, float radius, Node source)
    {
        if (Dead || source == this || radius < Hearing.Gunshot * .25f) return;
        if (at.DistanceTo(GlobalPosition) > Math.Min(radius, 160)) return;
        if (Def.Role == "dog" && Site >= 0) { Call(); _callIn = 1; }
        if (Def.Role is "dog" or "livestock" && Site >= 0 && at.DistanceTo(GlobalPosition) > 40) return;   // used to it
        Spook(at);
    }

    public void TakeHit(float damage, Vector3 at, Vector3 from)
    {
        if (Dead) return;
        _health -= damage;
        Effects.BloodHit(this, at, at - from);
        if (_health <= 0)
        {
            Dead = true;
            Play("Death");
            _voice.Stream = DeadRecon.Audio.Synth.Animal("yelp", 0);
            _voice.Play();
            GetNodeOrNull<AnimalBody>("AnimalBody")?.QueueFree();
            if (Site >= 0) Support.Change(Site, Def.Role == "dog" ? -12 : -6, $"you killed their {Def.Id.Split('_')[0]}");
            GetTree().CreateTimer(90).Timeout += () => { if (IsInstanceValid(this)) QueueFree(); };
            return;
        }
        if (_anim != null && _anim.HasAnimation("Hit")) { _anim.Play("Hit"); _clip = "Hit"; }
        if (Def.Role == "predator" && _health < Def.Health * .5f) { _angry = 0; Spook(from); }
        else if (Def.Role == "predator") { _angry = 1; _fear = 0; }
        else Spook(from);
        if (Site >= 0) Support.Change(Site, -3, "you shot one of their animals");
    }

    /// <summary>The collider: bullets reach the animal through it.</summary>
    public partial class AnimalBody : StaticBody3D, IDamageable
    {
        public Animal Owner2 = null!;
        public override void _Ready() => Name = "AnimalBody";
        public void TakeHit(float damage, Vector3 at, Vector3 from) => Owner2.TakeHit(damage, at, from);
    }
}

/// <summary>
/// Puts animals in the world: each settlement gets its dogs, poultry and livestock (with its people), and wildlife
/// comes and goes round you in the jungle (a handful of groups at a time, despawned when you've gone).
/// </summary>
public partial class Wildlife : Node
{
    public Walker Player = null!;
    public BakedWorld World = null!;
    public Settlements? Towns;
    public static Wildlife? Current { get; private set; }

    private static readonly AnimalDef[] Builtin =
    {
        new("dog_mutt", "res://assets/animals/dog_3.glb", "dog", "village", 30, .9f, 5, Call: "bark"),
        new("dog_brown", "res://assets/animals/dog_11.glb", "dog", "village", 30, .9f, 5, Call: "bark"),
        new("labrador", "res://assets/animals/labrador_3.glb", "dog", "village", 30, .9f, 5, Call: "bark"),
        new("beagle", "res://assets/animals/beagle_5.glb", "dog", "village", 25, .8f, 4.5f, Call: "bark"),
        new("boxer", "res://assets/animals/boxer_7.glb", "dog", "village", 32, .9f, 5, Call: "bark"),
        new("chicken", "res://assets/animals/chicken_2.glb", "poultry", "village", 8, .35f, 2.2f, 6, 3, 6, "cluck"),
        new("hen", "res://assets/animals/chicken_4.glb", "poultry", "village", 8, .35f, 2.2f, 6, 2, 4, "cluck"),
        new("duck", "res://assets/animals/duck_3.glb", "poultry", "village", 8, .3f, 1.8f, 6, 2, 4, "quack"),
        new("pig", "res://assets/animals/pig_2.glb", "livestock", "village", 60, .5f, 3, 6, 1, 3, "oink"),
        new("goat", "res://assets/animals/goat_5.glb", "livestock", "village", 40, .6f, 4, 8, 1, 3, "bleat"),
        new("cow", "res://assets/animals/cow_4.glb", "livestock", "village", 120, .5f, 3, 6, 1, 2, "moo", .6f),
        new("boar", "res://assets/animals/boar_6.glb", "prey", "jungle", 70, .7f, 5.5f, 22, 2, 4, "oink", 1.2f),
        new("deer", "res://assets/animals/deer_8.glb", "prey", "jungle", 50, .8f, 7, 35, 1, 3, Weight: 1.2f),
        new("lizard", "res://assets/animals/lizard_9.glb", "prey", "jungle", 15, .4f, 3, 10, 1, 1, Weight: 1),
        new("crocodile", "res://assets/animals/crocodile_14.glb", "basker", "river", 140, .4f, 3, 14, 1, 1, "growl", .8f),
        new("elephant", "res://assets/animals/elephant_13.glb", "prey", "jungle", 400, .7f, 3.5f, 30, 1, 3, "trumpet", .15f),
        new("tiger", "res://assets/animals/tiger_10.glb", "predator", "jungle", 150, .9f, 7, 0, 1, 1, "growl", .08f),
        new("leopard", "res://assets/animals/leopard_12.glb", "predator", "jungle", 90, .9f, 7, 0, 1, 1, "growl", .1f),
    };

    public static Dictionary<string, AnimalDef> Catalogue => _cat ??= DeadRecon.Data.Content.Get("animals", Builtin).Where(a => ResourceLoader.Exists(a.Model)).ToDictionary(a => a.Id);
    private static Dictionary<string, AnimalDef>? _cat;

    private readonly Dictionary<int, List<Animal>> _village = new();
    private readonly List<(Vector3 At, List<Animal> Pack)> _wild = new();
    private double _since;
    private readonly RandomNumberGenerator _rng = new() { Seed = 777 };
    private const int WildGroups = 7;

    public override void _Ready() { Current = this; Animal.All.Clear(); }
    public override void _ExitTree() { if (Current == this) Current = null; }

    /// <summary>A settlement's animals, round its yards (called when its people are populated).</summary>
    public void PopulateVillage(Settlements.Site site, Villages.SiteLayout layout)
    {
        if (_village.ContainsKey(site.Index) || Catalogue.Count == 0) return;
        var rng = new RandomNumberGenerator { Seed = (ulong)site.Index * 101 + 9 };
        var list = new List<Animal>();
        int scale = site.Kind switch { "town" => 3, "village" => 2, _ => 1 };
        void Add(AnimalDef def, int n, float range)
        {
            for (int k = 0; k < n; k++)
            {
                var at = layout.Centre + new Vector3(rng.RandfRange(-site.Radius * .6f, site.Radius * .6f), 0, rng.RandfRange(-site.Radius * .6f, site.Radius * .6f));
                list.Add(Spawn(def, at, range, site.Index, site.Index * 1000 + list.Count));
            }
        }
        var dogs = Catalogue.Values.Where(a => a.Role == "dog").ToList();
        var poultry = Catalogue.Values.Where(a => a.Role == "poultry").ToList();
        var stock = Catalogue.Values.Where(a => a.Role == "livestock").ToList();
        if (dogs.Count > 0) for (int k = 0; k < rng.RandiRange(1, 2) * scale; k++) Add(dogs[rng.RandiRange(0, dogs.Count - 1)], 1, site.Radius * .7f);
        if (poultry.Count > 0) for (int k = 0; k < scale; k++) { var p = poultry[rng.RandiRange(0, poultry.Count - 1)]; Add(p, rng.RandiRange(p.GroupMin, p.GroupMax), 8); }
        if (stock.Count > 0) for (int k = 0; k < scale; k++) { var s = stock[rng.RandiRange(0, stock.Count - 1)]; Add(s, rng.RandiRange(s.GroupMin, s.GroupMax), 10); }
        _village[site.Index] = list;
    }

    public void FreeVillage(int site)
    {
        if (!_village.Remove(site, out var list)) return;
        foreach (var a in list) if (IsInstanceValid(a)) a.QueueFree();
    }

    private Animal Spawn(AnimalDef def, Vector3 at, float range, int site, int seed)
    {
        var a = new Animal { Def = def, Player = Player, World = World, Home = at, Range = range, Site = site, Seed = seed, Name = $"Animal_{def.Id}_{seed}" };
        AddChild(a);
        a.GlobalPosition = at with { Y = World.HeightAt(at.X, at.Z) };
        a.Rotation = new Vector3(0, _rng.Randf() * MathF.Tau, 0);
        return a;
    }

    public override void _Process(double delta)
    {
        _since += delta;
        if (_since < 2) return;
        _since = 0;
        var p = Player.GlobalPosition;
        // Gone too far away: they go too.
        for (int i = _wild.Count - 1; i >= 0; i--)
        {
            var (at, pack) = _wild[i];
            pack.RemoveAll(a => !IsInstanceValid(a));
            var centre = pack.Count > 0 ? pack[0].GlobalPosition : at;
            if (pack.Count == 0 || Flat(centre - p) > 420) { foreach (var a in pack) if (IsInstanceValid(a)) a.QueueFree(); _wild.RemoveAt(i); }
        }
        UpdateSnakes(p);
        if (_wild.Count >= WildGroups || Catalogue.Count == 0) return;
        // A new group, out of sight, 120-300 m away, where that animal lives.
        var wild = Catalogue.Values.Where(a => a.Where is "jungle" or "river").ToList();
        if (wild.Count == 0) return;
        float total = wild.Sum(a => a.Weight), r = _rng.Randf() * total;
        var def = wild[^1];
        foreach (var a in wild) { r -= a.Weight; if (r <= 0) { def = a; break; } }
        for (int tries = 0; tries < 12; tries++)
        {
            float ang = _rng.Randf() * MathF.Tau, dist = _rng.RandfRange(120, 300);
            var at = p + new Vector3(MathF.Cos(ang) * dist, 0, MathF.Sin(ang) * dist);
            if (Math.Abs(at.X) > World.Half - 20 || Math.Abs(at.Z) > World.Half - 20) continue;
            var (gx, gz) = World.ToSample(at.X, at.Z);
            bool nearWater = World.NearWater((int)gx, (int)gz, 6);
            if (World.IsWet((int)gx, (int)gz)) continue;
            if (def.Where == "river" != nearWater) continue;
            if (Towns?.SiteAt(at.X, at.Z, 2.5f) != null) continue;
            if (DeadRecon.Base.HomeBase.Position is { } home && Flat(home - at) < 60) continue;
            int n = _rng.RandiRange(def.GroupMin, def.GroupMax);
            var pack = new List<Animal>();
            for (int k = 0; k < n; k++) pack.Add(Spawn(def, at + new Vector3(_rng.RandfRange(-4, 4), 0, _rng.RandfRange(-4, 4)), def.Where == "river" ? 10 : 35, -1, (int)_rng.Randi()));
            _wild.Add((at, pack));
            break;
        }
    }

    // ================================================================= snakes (made in code: see Snake.cs)

    private static readonly SnakeDef[] BuiltinSnakes =
    {
        new("cobra", "Cobra", 1.7f, .035f, "warns", 30, "#3b3424", "#1c1810", "#c9b98a", 0, Hood: true, Weight: 1),
        new("viper", "Green pit viper", .75f, .028f, "ambush", 22, "#4f8a2a", "#2f5c18", "#c8d46a", 0, Weight: 1.4f),
        new("krait", "Banded krait", 1.3f, .026f, "shy", 45, "#141414", "#d8b520", "#d9cfa0", 1, 9, Night: true, Weight: .8f),
        new("python", "Reticulated python", 4.2f, .085f, "harmless", 0, "#7a6440", "#2a2216", "#d6c79a", 3, 5, Weight: .5f),
        new("rat_snake", "Rat snake", 1.9f, .03f, "shy", 0, "#5b5a2e", "#2f2e18", "#e2d58c", 2, 7, Weight: 1),
    };

    /// <summary>Snake kinds ("snakes" content section; mods add more by id).</summary>
    public static Dictionary<string, SnakeDef> Snakes => _snakes ??= DeadRecon.Data.Content.Get("snakes", BuiltinSnakes).ToDictionary(a => a.Id);
    private static Dictionary<string, SnakeDef>? _snakes;
    private readonly List<Snake> _snakeList = new();
    private const int SnakeCount = 3;

    /// <summary>A few snakes near you at any time (25-80 m, never in villages or at home); gone past 150 m.</summary>
    private void UpdateSnakes(Vector3 p)
    {
        _snakeList.RemoveAll(s => !IsInstanceValid(s));
        foreach (var s in _snakeList.ToArray())
            if (Flat(s.GlobalPosition - p) > 150) { _snakeList.Remove(s); s.QueueFree(); }
        if (_snakeList.Count >= SnakeCount || Snakes.Count == 0) return;
        float hour = DeadRecon.World.TimeOfDay.Current?.Hour ?? 12;
        bool night = hour < 6 || hour > 19;
        var kinds = Snakes.Values.Where(k => !k.Night || night).ToList();
        if (kinds.Count == 0) return;
        float total = kinds.Sum(a => a.Weight), r = _rng.Randf() * total;
        var def = kinds[^1];
        foreach (var a in kinds) { r -= a.Weight; if (r <= 0) { def = a; break; } }
        for (int tries = 0; tries < 8; tries++)
        {
            float ang = _rng.Randf() * MathF.Tau, dist = _rng.RandfRange(25, 80);
            var at = p + new Vector3(MathF.Cos(ang) * dist, 0, MathF.Sin(ang) * dist);
            if (Math.Abs(at.X) > World.Half - 20 || Math.Abs(at.Z) > World.Half - 20) continue;
            var (gx, gz) = World.ToSample(at.X, at.Z);
            if (World.IsWet((int)gx, (int)gz)) continue;
            if (Towns?.SiteAt(at.X, at.Z, 1.5f) != null) continue;
            if (DeadRecon.Base.HomeBase.Position is { } home && Flat(home - at) < 50) continue;
            _snakeList.Add(SpawnSnake(def, at, (int)_rng.Randi()));
            break;
        }
    }

    private Snake SpawnSnake(SnakeDef def, Vector3 at, int seed)
    {
        var s = new Snake { Def = def, Player = Player, World = World, Seed = seed, Name = $"Snake_{def.Id}_{seed}" };
        AddChild(s);
        s.GlobalPosition = at with { Y = World.HeightAt(at.X, at.Z) };
        s.Rotation = new Vector3(0, _rng.Randf() * MathF.Tau, 0);
        return s;
    }

    /// <summary>Tests: one snake of this kind right here.</summary>
    public Snake TestSnake(string id, Vector3 at, float yaw)
    {
        var s = SpawnSnake(Snakes[id], at, 500 + _snakeList.Count);
        s.Rotation = new Vector3(0, yaw, 0);
        _snakeList.Add(s);
        return s;
    }

    /// <summary>Tests: a group of this kind right here.</summary>
    public List<Animal> TestSpawn(string id, Vector3 at, int n)
    {
        var def = Catalogue[id];
        var pack = new List<Animal>();
        for (int k = 0; k < n; k++) pack.Add(Spawn(def, at + new Vector3(k * 1.5f, 0, k), 20, -1, 100 + k));
        _wild.Add((at, pack));
        return pack;
    }

    private static float Flat(Vector3 v) => new Vector2(v.X, v.Z).Length();
}
