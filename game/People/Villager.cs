using System;
using System.Collections.Generic;
using DeadRecon.AI;
using DeadRecon.Combat;
using DeadRecon.Player;
using DeadRecon.World;
using Godot;

namespace DeadRecon.People;

/// <summary>
/// Someone who lives here. Follows a day: up at dawn, to the well and the fire, to work (fields, a market stall,
/// chores round the house), lunch by the fire, work again, the fire in the evening, home to sleep. Shelters from heavy
/// rain, runs indoors at gunfire, backs away from a rifle pointed at them, greets you according to how the village
/// feels about you, and chats with the neighbours. Indoors means hidden (no body, no collision).
/// <para>Everything is decided by <see cref="Plan"/> every few seconds from the clock, the weather and fear; the rest
/// is walking to the chosen spot and doing the thing there.</para>
/// </summary>
public partial class Villager : CharacterBody3D, IDamageable
{
    public enum Role { Farmer, Vendor, Homebody, Headman }
    public enum Doing { Walking, Working, Sitting, Standing, Indoors, Sheltering, Panicking, Dead }

    public Role Job;
    public int Site;
    public string Person = "";
    public Villages.SiteLayout Layout = null!;
    public Villages.SiteLayout.House? Home;
    public Settlements.Field? Field;
    public Vector3 Stall;
    public float StallYaw;
    public Vector3 Seat;
    public string CharacterScene = "";
    public Walker Player = null!;
    public BakedWorld World = null!;
    public int Seed;
    /// <summary>Lost in the jungle (a job): sits tight and calls out until you find him (F), then follows you.</summary>
    public bool Lost;
    public bool Following { get; private set; }
    private Vector3? _restAt;

    /// <summary>Done following: walk to this spot and sit there (a rescued man at your fire).</summary>
    public void Rest(Vector3 at) { Following = false; _restAt = at; }
    /// <summary>What you say to get him to follow, his answer, and what he calls out (lost farmers by default).</summary>
    public string FoundPrompt = "\"It's alright. Follow me.\"";
    public string FoundLine = "Oh, thank goodness! I thought I'd be eaten! Lead the way, I'm right behind you.";
    public string[]? Calls;
    private float _shout = 3;
    private bool _fWas;

    public Doing State { get; private set; } = Doing.Standing;
    public string Activity { get; private set; } = "";
    public bool Indoors => State == Doing.Indoors;
    public bool IsDead => State == Doing.Dead;
    public float Health { get; private set; } = 60;

    private SoldierBody? _body;
    private Label3D _bubble = null!;
    private AudioStreamPlayer3D _voice = null!, _steps = null!;
    private float _bubbleLeft, _voicePitch, _stepDistance, _planIn, _panic, _greetCooldown, _aimedAt, _stuck, _sideLeft, _workClock, _crouch;
    private readonly List<Vector3> _route = new();
    private Vector3 _goal, _side;
    private string _arrive = "stand";          // what to do on arrival: work, sit, stand, indoors, shelter
    private Vector3? _face;
    private float _speed = 1.3f;
    private readonly RandomNumberGenerator _rng = new();
    private readonly Dictionary<string, int> _lastLine = new();
    private const float Gravity = 9.8f;

    public static readonly Dictionary<string, string[]> Lines = new()
    {
        ["hostile"] = new[] { "You again. The rice remembers what you did.", "Walk on, soldier. Walk far.", "No. Whatever it is: no.",
                              "My grandmother has a stick. She knows about you." },
        ["wary"] = new[] { "Mm. Boots off the paddy, please.", "Another man with a gun. Lovely.", "Don't scare the chickens.",
                           "Hello. Please don't explode anything." },
        ["friendly"] = new[] { "Ah! Our friend with the loud stick!", "Hello! You look hungry. You always look hungry.",
                               "Good day! Mind the buffalo. Metaphorically.", "Back again? Bring rain, not trouble." },
        ["loyal"] = new[] { "Our hero! Sit, sit, eat something!", "The children made a song about you. It's mostly about your boots.",
                            "Whatever you need. Within reason. And rice." },
        ["chat"] = new[] { "The rain is late. Or early. The rain does what it wants.", "My cousin says the soldiers are scared of frogs.",
                           "Fifty years I plant rice. Rice still doesn't say thank you.", "If it rains again I'm building a boat.",
                           "The headman says patience. The headman has a bad back.", "Somebody stole my good hat.",
                           "Who keeps leaving half a tree on the path?", "Did you sell the duck? ...You ATE the duck?!",
                           "The checkpoint boys took a whole sack again.", "My knees can tell you the weather. They say: no." },
        ["panic"] = new[] { "Inside! Everyone inside!", "Not again!", "Gunfire! Get the children!", "Why is it always during lunch?!" },
        ["aimed"] = new[] { "Please! Not the face!", "I'm just a farmer!", "Point that somewhere else!", "I have nothing! Only rice!" },
        ["hurt"] = new[] { "AAH! Why?!", "I'm hit! Over rice?!", "Help! He shot me!" },
        ["work"] = new[] { "One plant, two plant... ow, my back.", "Grow, you little green tyrants.", "Sun, rain, back pain. Farming." },
        ["rain"] = new[] { "Rain! Under the house, quick!", "Here it comes. Everybody under a roof!", "My washing!" },
        ["morning"] = new[] { "Morning. Tea. Then rice. Then more rice.", "Who let the rooster near my window again?" },
        ["night"] = new[] { "Goodnight, everyone! Lock up the chickens.", "Bed. Finally. My back is filing a complaint." },
        ["sell"] = new[] { "Fresh jars! Hardly cracked!", "Rice! Rice! Also... rice!", "Best prices in the jungle! Only prices in the jungle!" },
    };

    public override void _Ready()
    {
        _rng.Seed = (ulong)Seed;
        AddChild(new CollisionShape3D { Shape = new CapsuleShape3D { Radius = .3f, Height = 1.7f }, Position = new Vector3(0, .85f, 0) });
        FloorMaxAngle = Mathf.DegToRad(50);
        CollisionLayer = 1; CollisionMask = 1;
        if (ResourceLoader.Exists(CharacterScene))
        {
            _body = new SoldierBody { CharacterScene = CharacterScene, GunScene = "" };
            AddChild(_body);
        }
        _voicePitch = 105 + 70 * _rng.Randf();
        _voice = new AudioStreamPlayer3D { UnitSize = 6, MaxDistance = 60, VolumeDb = -1, Bus = "Voices", Position = new Vector3(0, 1.5f, 0) };
        _steps = new AudioStreamPlayer3D { UnitSize = 3, MaxDistance = 30, Bus = "Foley", VolumeDb = -9, MaxPolyphony = 2 };
        AddChild(_voice); AddChild(_steps);
        _bubble = new Label3D
        {
            FontSize = 36, OutlineSize = 10, PixelSize = .0007f, FixedSize = true, Position = new Vector3(0, 2.4f, 0), Width = 900,
            AutowrapMode = TextServer.AutowrapMode.WordSmart, Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, NoDepthTest = true,
            Visible = false, Modulate = new Color(1, .95f, .82f),
        };
        AddChild(_bubble);
        Hearing.Made += OnHeard;
        _planIn = _rng.RandfRange(0, .5f);
    }

    public override void _ExitTree() => Hearing.Made -= OnHeard;

    // ================================================================= deciding

    /// <summary>Choose what to do now. <paramref name="snap"/>: appear there at once (when the village is first
    /// populated, so it doesn't start with everyone walking out of their front door).</summary>
    public void Plan(bool snap = false)
    {
        if (State == Doing.Dead || Lost) return;
        float hour = TimeOfDay.Current?.Hour ?? 12, rain = DeadRecon.World.Weather.Current?.Intensity ?? 0;
        // A little personal offset so the whole village doesn't move on the same minute.
        hour = (hour + (Seed % 7) * .07f) % 24;
        string was = Activity;

        if (_panic > 0) Go("panic", HomeDoor(), "indoors", 3.8f);
        else if (rain > .55f)
        {
            if (Home?.Under is { } under) Go("shelter", under + Jitter(.8f), "shelter", 2.6f);
            else Go("shelter", HomeDoor(), "indoors", 2.6f);
        }
        else if (hour < 5.5f || hour >= 21) Go("sleep", HomeDoor(), "indoors", 1.3f);
        else if (hour < 7) Go("morning", Layout.Well is { } w && Seed % 2 == 0 && was != "morning-fire" ? w + Jitter(1.6f) : Seat, Layout.Well != null && Seed % 2 == 0 ? "stand" : "sit", 1.3f);
        else if (hour < 11 || (hour >= 13 && hour < 17.5f)) Work();
        else if (hour < 13) Go("lunch", Seat, "sit", 1.3f);
        else Go("evening", Seat, "sit", 1.3f);

        if (Activity != was)
        {
            if (Activity == "shelter" && _rng.Randf() < .35f) Bark("rain");
            if (Activity == "sleep" && _rng.Randf() < .2f) Bark("night");
            if (Activity == "morning" && was == "sleep" && _rng.Randf() < .25f) Bark("morning");
        }
        if (snap) Snap();
    }

    private void Work()
    {
        switch (Job)
        {
            case Role.Farmer when Field != null:
            {
                // A spot in the field; moves along the row every so often while working.
                var f = Field;
                if (Activity != "work" || _rng.Randf() < .15f)
                {
                    var p = f.Centre + new Basis(Vector3.Up, f.Rotation) * new Vector3(_rng.RandfRange(-f.W / 2 + 1.2f, f.W / 2 - 1.2f), 0, _rng.RandfRange(-f.D / 2 + 1.2f, f.D / 2 - 1.2f));
                    p.Y = f.Kind == "paddy" ? f.Level : World.HeightAt(p.X, p.Z);
                    Go("work", p, "work", 1.3f);
                }
                break;
            }
            case Role.Vendor:
                Go("sell", Stall, "stand", 1.2f);
                _face = Stall + new Basis(Vector3.Up, StallYaw) * Vector3.Back * 3;
                break;
            case Role.Headman:
                if (Activity != "plaza" || _rng.Randf() < .2f) Go("plaza", Layout.Fire + Jitter(4.5f, 2.5f), "stand", 1.0f);
                break;
            default:
                // Chores: the house, the well, the fire, back to the house.
                if (Activity != "chores" || _rng.Randf() < .3f)
                {
                    var spots = new List<Vector3> { HomeDoor() + Jitter(2.5f), Layout.Fire + Jitter(4, 2.5f) };
                    if (Layout.Well is { } well) spots.Add(well + Jitter(1.8f, 1.2f));
                    if (Home?.Under is { } u) spots.Add(u + Jitter(1));
                    Go("chores", spots[_rng.RandiRange(0, spots.Count - 1)], "stand", 1.25f);
                }
                break;
        }
    }

    private Vector3 HomeDoor() => Home?.Door ?? Layout.Centre + Jitter(6, 3);

    private Vector3 Jitter(float r, float min = 0)
    {
        float a = _rng.Randf() * MathF.Tau, d = Mathf.Lerp(min, r, _rng.Randf());
        var p = new Vector3(MathF.Cos(a) * d, 0, MathF.Sin(a) * d);
        return p;
    }

    /// <summary>Head for a spot (unless already going there for the same reason).</summary>
    private void Go(string activity, Vector3 goal, string arrive, float speed)
    {
        bool same = Activity == activity && _goal.DistanceTo(goal) < 1.5f;
        Activity = activity;
        _speed = speed;
        if (same) return;
        if (goal.Y == 0 || float.IsNaN(goal.Y)) goal.Y = World.HeightAt(goal.X, goal.Z);
        _goal = goal;
        _arrive = arrive;
        _face = null;
        _route.Clear();
        if (State == Doing.Indoors)
        {
            if (arrive == "indoors") return;                  // already home
            Show(Home?.Inside ?? HomeDoor());                 // step out of the door
            if (Home != null) { _route.Add(Home.Top); _route.Add(Home.Door); }
        }
        if (arrive == "indoors" && Home != null) { _route.Add(Home.Door); _route.Add(Home.Top); _route.Add(Home.Inside); }
        else _route.Add(goal);
        State = Doing.Walking;
    }

    /// <summary>Be there already (and doing it).</summary>
    private void Snap()
    {
        var at = _route.Count > 0 ? _route[^1] : _goal;
        if (_arrive == "indoors") { Hide(at); return; }
        Show(at);
        _route.Clear();
        Arrive();
    }

    private void Arrive()
    {
        State = _arrive switch
        {
            "work" => Doing.Working, "sit" => Doing.Sitting, "indoors" => Doing.Indoors, "shelter" => Doing.Sheltering,
            _ => _panic > 0 ? Doing.Panicking : Doing.Standing,
        };
        if (_arrive == "indoors") Hide(GlobalPosition);
        if (_arrive == "sit") _face = Layout.Fire;
    }

    private void Hide(Vector3 at)
    {
        GlobalPosition = at;
        State = Doing.Indoors;
        Visible = false;
        GetChild<CollisionShape3D>(0).Disabled = true;
        Velocity = Vector3.Zero;
    }

    private void Show(Vector3 at)
    {
        GlobalPosition = at + Vector3.Up * .05f;
        Visible = true;
        GetChild<CollisionShape3D>(0).Disabled = false;
        if (State == Doing.Indoors) State = Doing.Standing;
    }

    // ================================================================= per frame

    public override void _PhysicsProcess(double delta)
    {
        float dt = (float)delta;
        if (State == Doing.Dead) return;
        _panic = Math.Max(0, _panic - dt);
        _greetCooldown -= dt;
        _planIn -= dt;
        if (_planIn <= 0) { _planIn = _rng.RandfRange(4, 7); Plan(); }
        UpdateBubble(dt);
        if (State == Doing.Indoors) return;
        if (Lost) { BeLost(dt); return; }

        ReactToPlayer(dt);
        Vector3 target = GlobalPosition;
        float speed = 0;
        if (State == Doing.Walking && _route.Count > 0)
        {
            target = _route[0];
            speed = _speed;
            if (Flat(target - GlobalPosition) < (_route.Count > 1 ? .5f : .35f))
            {
                _route.RemoveAt(0);
                if (_route.Count == 0) { Arrive(); speed = 0; }
            }
            if (_sideLeft > 0) { _sideLeft -= dt; target = GlobalPosition + _side; }
        }
        Move(target, speed, dt);

        // Working: bend down and up (planting, weeding), a small step now and then.
        _workClock += dt;
        float crouch = State switch
        {
            Doing.Working => .55f + .45f * (.5f + .5f * MathF.Sin(_workClock * 1.4f + Seed)),
            Doing.Sitting or Doing.Sheltering => 1,
            Doing.Panicking => .8f,
            _ => 0,
        };
        _crouch = Mathf.MoveToward(_crouch, crouch, dt * 3);
        if (_body != null) { _body.Crouch = _crouch; _body.Speed = new Vector2(Velocity.X, Velocity.Z).Length(); }
        if (State == Doing.Working && _rng.Randf() < dt * .004f && Player.GlobalPosition.DistanceTo(GlobalPosition) < 25) Bark("work");
        if (State == Doing.Standing && Activity == "sell" && _rng.Randf() < dt * .006f && Player.GlobalPosition.DistanceTo(GlobalPosition) < 25) Bark("sell");
        Steps(dt);
    }

    private void Move(Vector3 goal, float speed, float dt)
    {
        var to = goal - GlobalPosition; to.Y = 0;
        var v = Velocity;
        if (speed > 0 && to.Length() > .05f)
        {
            var dir = to.Normalized();
            v.X = dir.X * speed; v.Z = dir.Z * speed;
            Turn(GlobalPosition + dir, dt, 5);
        }
        else
        {
            v.X = 0; v.Z = 0;
            if (_face is { } f) Turn(f, dt, 3);
        }
        v.Y = IsOnFloor() ? -.5f : v.Y - Gravity * dt;
        Velocity = v;
        var before = GlobalPosition;
        MoveAndSlide();
        GlobalPosition = Foliage.PushOut(GlobalPosition, .32f);
        // Fell through the world or got buried (terrain streaming in late): put back on the ground.
        float ground = World.HeightAt(GlobalPosition.X, GlobalPosition.Z);
        if (GlobalPosition.Y < ground - 1.5f) GlobalPosition = new Vector3(GlobalPosition.X, ground + .1f, GlobalPosition.Z);
        if (speed > 0 && Flat(GlobalPosition - before) < speed * dt * .3f) _stuck += dt; else _stuck = Math.Max(0, _stuck - dt);
        if (_stuck > 1.2f)
        {
            // Walk round whatever it is: sideways for a moment, left or right.
            _stuck = 0;
            var d = (goal - GlobalPosition) with { Y = 0 };
            _side = d.Normalized().Cross(Vector3.Up) * (_rng.Randf() < .5f ? 1 : -1) * 1.5f;
            _sideLeft = 1.2f;
            if (_route.Count > 0 && Flat(_route[0] - GlobalPosition) < 2.5f) { _route.RemoveAt(0); if (_route.Count == 0) Arrive(); }
        }
    }

    private void Turn(Vector3 at, float dt, float rate)
    {
        var d = at - GlobalPosition; d.Y = 0;
        if (d.LengthSquared() < .01f) return;
        Rotation = new Vector3(0, Mathf.LerpAngle(Rotation.Y, MathF.Atan2(-d.X, -d.Z), Math.Min(1, dt * rate)), 0);
    }

    private void Steps(float dt)
    {
        float speed = new Vector2(Velocity.X, Velocity.Z).Length();
        _stepDistance += speed * dt;
        if (speed < .3f || _stepDistance < (speed > 3 ? 1.1f : .7f)) return;
        _stepDistance = 0;
        if (Player.GlobalPosition.DistanceTo(GlobalPosition) > 30) return;
        var p = GlobalPosition;
        var (gx, gz) = World.ToSample(p.X, p.Z);
        var towns = Settlements.Current;
        var surface = World.IsWet((int)gx, (int)gz) || (towns?.FieldAt(p.X, p.Z) > .9f && p.Y < World.HeightAt(p.X, p.Z) + .3f) ? DeadRecon.Audio.Synth.Surface.Water
                    : p.Y > World.HeightAt(p.X, p.Z) + .6f ? DeadRecon.Audio.Synth.Surface.Wood
                    : towns != null && (towns.RoadAt(p.X, p.Z) > .4f || towns.YardAt(p.X, p.Z) > .5f) ? DeadRecon.Audio.Synth.Surface.Dirt
                    : DeadRecon.Audio.Synth.Surface.Leaves;
        _steps.Stream = DeadRecon.Audio.Synth.Step(surface, (int)(_rng.Randi() % 6));
        _steps.PitchScale = 1 + .15f * _rng.Randf();
        _steps.Play();
    }

    /// <summary>Lost: crouched where he is, calling out when he hears someone. Found: following you, hurrying when
    /// you get ahead.</summary>
    private void BeLost(float dt)
    {
        float dist = Player.GlobalPosition.DistanceTo(GlobalPosition);
        if (_restAt is { } rest)
        {
            bool there = new Vector2(rest.X - GlobalPosition.X, rest.Z - GlobalPosition.Z).Length() < .6f;
            Move(rest, there ? 0 : 1.4f, dt);
            _crouch = Mathf.MoveToward(_crouch, there ? 1 : 0, dt * 3);
            if (_body != null) { _body.Crouch = _crouch; _body.Speed = new Vector2(Velocity.X, Velocity.Z).Length(); }
            if (there) _face = rest + (rest - GlobalPosition) + Vector3.Forward;
            UpdateBubble(dt);
            Steps(dt);
            return;
        }
        bool f = Input.IsPhysicalKeyPressed(Key.F), fPressed = f && !_fWas;
        _fWas = f;
        if (!Following)
        {
            _shout -= dt;
            if (dist < 75 && _shout <= 0)
            {
                _shout = _rng.RandfRange(6, 10);
                string[] calls = Calls ?? new[] { "Hello?! Is someone there?!", "Over here! OVER HERE!", "Please don't be a tiger. Please don't be a tiger.", "Help! I'm by the big tree! ...They're all big trees!" };
                Say(calls[_rng.RandiRange(0, calls.Length - 1)]);
            }
            if (dist < 3 && !Player.Dead)
            {
                DeadRecon.UI.Hud.Prompt($"F   {FoundPrompt}");
                if (fPressed && Input.MouseMode == Input.MouseModeEnum.Captured)
                {
                    Following = true;
                    Say(FoundLine);
                }
            }
            _crouch = Mathf.MoveToward(_crouch, 1, dt * 3);
            if (_body != null) { _body.Crouch = _crouch; _body.Speed = 0; }
            Move(GlobalPosition, 0, dt);
            _face = Player.GlobalPosition;
            UpdateBubble(dt);
            return;
        }
        // Following: a couple of metres behind you, running to catch up.
        var target = Player.GlobalPosition;
        float speed = dist > 12 ? 3.8f : dist > 3 ? 1.6f : 0;
        _crouch = Mathf.MoveToward(_crouch, Player.Crouching ? .7f : 0, dt * 3);
        Move(target, speed, dt);
        if (_body != null) { _body.Crouch = _crouch; _body.Speed = new Vector2(Velocity.X, Velocity.Z).Length(); }
        // Left far behind (a cliff, a river): he finds his own way to you.
        if (dist > 60) GlobalPosition = Player.GlobalPosition - Player.GlobalTransform.Basis.Z * -3 + Vector3.Up;
        _face = Player.GlobalPosition;
        Steps(dt);
    }

    /// <summary>Home again: an ordinary villager from now on.</summary>
    public void Settle(Villages.SiteLayout layout)
    {
        Lost = false; Following = false;
        Layout = layout;
        Say("Home! I'm never picking mushrooms again. ...Until Tuesday.");
        Plan();
    }

    // ================================================================= people

    private void ReactToPlayer(float dt)
    {
        if (Player.Dead) return;
        var me = GlobalPosition + Vector3.Up * 1.2f;
        float dist = Player.GlobalPosition.DistanceTo(GlobalPosition);
        // A rifle pointed at me, close: hands up (well, crouch), back away, complain.
        var cam = Player.Camera.GlobalTransform;
        var toMe = me - cam.Origin;
        bool aimed = Player.Aiming && dist < 25 && toMe.Normalized().Dot(-cam.Basis.Z) > .995f;   // down the sights, not just looking
        _aimedAt = aimed ? _aimedAt + dt : Math.Max(0, _aimedAt - dt * 2);
        if (_aimedAt > 1.2f && _panic <= 0)
        {
            Bark("aimed");
            _panic = 8;
            _aimedAt = 0;
            Support.Change(Site, -1, "pointed a rifle at a villager");
            Plan();
            return;
        }
        // Greetings, by how the village feels about you.
        if (dist < 5 && _greetCooldown <= 0 && _panic <= 0)
        {
            _greetCooldown = _rng.RandfRange(40, 70);
            Bark(Support.LevelAt(Site).ToString().ToLowerInvariant());
        }
        if (dist < 6 && State is Doing.Standing or Doing.Working or Doing.Sitting) _face = Player.GlobalPosition;
        else if (State == Doing.Sitting) _face = Layout.Fire;
    }

    /// <summary>Gunfire nearby: everyone runs indoors. (The village's own fear is handled by VillageLife.)</summary>
    private void OnHeard(Vector3 at, float radius, Node source)
    {
        if (State == Doing.Dead || radius < Hearing.Gunshot * .5f) return;
        if (at.DistanceTo(GlobalPosition) > Math.Min(radius, 220)) return;
        bool wasCalm = _panic <= 0;
        _panic = _rng.RandfRange(45, 75);
        if (wasCalm)
        {
            if (_rng.Randf() < .4f) Bark("panic");
            Plan();
        }
    }

    public void TakeHit(float damage, Vector3 at, Vector3 from)
    {
        if (State == Doing.Dead) return;
        bool squashed = damage >= 60;
        Health -= damage;
        if (!squashed) Effects.BloodHit(this, at, at - from);
        if (Health <= 0)
        {
            State = Doing.Dead;
            Activity = "dead";
            _body?.Ragdoll(at, squashed ? Vector3.Down : (at - from).Normalized(), squashed ? 2 : 1);
            GetChild<CollisionShape3D>(0).Disabled = true;
            _bubble.Visible = false;
            if (squashed) Support.Change(Site, -10, "a villager was killed by a falling tree");
            else { Support.Change(Site, -40, "you killed a villager"); Support.Spread(Site, -8); }
            return;
        }
        Bark("hurt");
        _panic = 90;
        Support.Change(Site, -15, "you shot a villager");
        Plan();
    }

    // ================================================================= talking

    public void Say(string line)
    {
        if (State is Doing.Dead) return;
        _bubble.Text = line;
        _bubbleLeft = 4;
        if (Player.GlobalPosition.DistanceTo(GlobalPosition) < 60 && Visible)
        {
            _voice.Stream = DeadRecon.Audio.Synth.Voice(line, _voicePitch, Seed);
            _voice.Play();
        }
    }

    public void Bark(string kind)
    {
        var lines = DeadRecon.Data.Content.LineSets("villager_lines", Lines)[kind];
        int last = _lastLine.TryGetValue(kind, out var l) ? l : -1;
        int pick = _rng.RandiRange(0, lines.Length - 1);
        if (pick == last && lines.Length > 1) pick = (pick + 1) % lines.Length;
        _lastLine[kind] = pick;
        Say(lines[pick]);
    }

    private void UpdateBubble(float dt)
    {
        _bubbleLeft -= dt;
        float dist = Player.GlobalPosition.DistanceTo(GlobalPosition);
        _bubble.Visible = _bubbleLeft > 0 && dist < 40 && Visible;
        _bubble.Modulate = new Color(1, .95f, .82f, Math.Clamp(_bubbleLeft, 0, 1));
        _bubble.PixelSize = .0007f * Player.Camera.Fov / 70f;
    }

    private static float Flat(Vector3 v) => new Vector2(v.X, v.Z).Length();
}
