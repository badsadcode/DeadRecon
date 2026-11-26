using System;
using System.Collections.Generic;
using System.Linq;
using DeadRecon.Combat;
using DeadRecon.Player;
using DeadRecon.World;
using Godot;

namespace DeadRecon.AI;

/// <summary>
/// One grumpy guard (Milestone 0). Patrols a loop, notices the player gradually, investigates, fights, searches the
/// last known position and gives up. No magic tracking: he only knows where you are while he can see you, or roughly
/// where a noise came from.
/// <para>Senses: sight (view cone, range, line of sight through terrain; crouching, standing still and distance help
/// you, running hurts) and hearing (<see cref="Hearing"/>: gunshots far, footsteps close). Seeing through plants is
/// limited by <see cref="Foliage"/>: trunks block, leaves let some through (<see cref="Exposure"/>).</para>
/// <para>Placeholder body built from primitives, holding the BadGun rifle.</para>
/// </summary>
public partial class Guard : CharacterBody3D, IDamageable
{
    public enum Mood { Patrol, Suspicious, Investigate, Alert, Search, Dead, Captive, Unconscious, Restrained }

    public Walker Target = null!;
    public TerrainStreamer Terrain = null!;
    public BakedWorld World = null!;
    public Vector3 Home;
    public string GunScene = "res://assets/weapons/BadGun_rifle_vietnam_97232.glb";
    /// <summary>Which gun he carries (WeaponDef id): sets the model, his damage, and what he drops.</summary>
    public string WeaponId = DeadRecon.Gear.WeaponDef.Default;
    private float ShotDamage => DeadRecon.Gear.WeaponDef.Get(WeaponId).GuardDamage;
    /// <summary>Patrol loop radius (m). A sentry uses a tiny one: he stands his post and looks around.</summary>
    public float PatrolMin = 12, PatrolMax = 22;
    public bool Sentry;
    /// <summary>A road patrol: walk these points in order, then back again (a short smoke at each end). Set before
    /// adding him to the scene. Null: the usual loop round <see cref="Home"/>.</summary>
    public List<Vector3>? Route;
    /// <summary>Where on the route he starts, and which way he's going.</summary>
    public int RouteStart;
    public bool RouteBackwards;
    /// <summary>Where on his route he is now (the patrol system remembers it when he goes off-screen).</summary>
    public int RouteIndex => _patrolIndex;
    public bool RouteGoingBack => _routeBack;
    /// <summary>How many times he has gone from calm to alert (mission stats).</summary>
    public int TimesAlerted { get; private set; }

    public Mood State { get; private set; } = Mood.Patrol;

    /// <summary>Every guard in the scene (for alarms and spotting fallen colleagues).</summary>
    public static readonly List<Guard> All = new();
    /// <summary>True while he is crouched behind a trunk (or peeking out of it) in a fight.</summary>
    public bool InCover => _cover != null && Flat(GlobalPosition - _cover.Value) < 1.2f;
    public bool Peeking => _peeking;
    public int BodiesFound { get; private set; }
    private Vector3? _cover;
    private Vector3 _peekSpot;
    private bool _peeking, _hurry;
    private float _coverTimer, _coverCheck;
    private readonly HashSet<Guard> _bodiesSeen = new();
    /// <summary>A saved operation coming back: old bodies are old news.</summary>
    public void AlreadySaw(Guard body) => _bodiesSeen.Add(body);
    private Vector3? _flankPoint;
    private float _flankTime, _sitTimer = 20, _sitting;
    private Vector3 _lastVel;
    private readonly Queue<Vector3> _searchPlan = new();
    private static float _chatCooldown = 8;
    public static readonly (string A, string B)[] Chats =
    {
        ("Seen anything?", "Just mosquitoes. Thousands of them."),
        ("Is it my shift or yours?", "It's always your shift."),
        ("Smells like rain.", "Everything here smells like rain."),
        ("You ever think about leaving?", "Every four minutes."),
        ("Did you hear that?", "No. And neither did you."),
        ("Who's cooking tonight?", "Not Dave. Never again Dave."),
        ("The radio's been quiet.", "The radio is the only one here with any sense."),
        ("Nice day for it.", "For what?"),
    };
    /// <summary>True while he's circling round to your side.</summary>
    public bool Flanking => _flankPoint != null;
    public bool Sitting => _sitting > 0;
    public static int FlanksStarted, SuppressShots, Chats_, SitsStarted;
    private const float AlarmRadius = 75;
    /// <summary>0..1: how sure he is that someone is there. 0.3 suspicious, 0.7 investigating, 1 alerted.</summary>
    public float Suspicion { get; private set; }
    public float Health { get; private set; } = 100;
    public bool CanSeeTarget => _seen;
    public void SetHealthForTest(float h) => Health = h;
    /// <summary>0..1 how much of the player he can see right now (0 outside his view, behind terrain or trunks).</summary>
    public float Exposure { get; private set; }

    // ---- tuning
    private const float WalkSpeed = 1.5f, InvestigateSpeed = 2.0f, RunSpeed = 4.3f, Gravity = 9.81f, EyeHeight = 1.6f;
    private const float ViewRange = 55, AlertViewRange = 85, ViewCos = .5f, AlertViewCos = -.2f; // 120 deg cone, wider when alert
    private const float SearchTime = 18;
    private static float ReactionTime => .7f * DeadRecon.UI.Settings.GuardReaction;
    private const float MinExposure = .12f; // below this the player is hidden by plants

    private Node3D _rig = null!, _head = null!, _legL = null!, _legR = null!, _gunPivot = null!;
    private Node3D? _muzzle;
    private Label3D _icon = null!, _bubble = null!;
    private AudioStreamPlayer3D _shot = null!;
    private OmniLight3D _flashLight = null!;
    private MeshInstance3D _flashSprite = null!;
    private readonly List<Vector3> _patrol = new();
    private int _patrolIndex;
    private bool _routeBack;
    private float _wait, _stateTime, _sinceSeen = 999, _searchLeft, _reaction, _cooldown, _burst, _flash = -1;
    private int _warningShots;
    private float _bubbleLeft, _mutter = 12, _walkPhase, _stuck, _deathTime, _lookAround;
    private bool _seen, _wasSeen;
    private Vector3 _lastKnown, _searchPoint;
    private readonly RandomNumberGenerator _rng = new();
    private readonly Dictionary<string, int> _lastLine = new();

    // ---- the grumpiness (the most important system in the game)
    public static readonly Dictionary<string, string[]> Lines = new()
    {
        ["sentry"] = new[] { "Stand here, they said. Watch the radio, they said.", "This radio has more friends than me.",
                             "I've counted every leaf on that tree. Twice.", "Nobody ever attacks on MY shift. Nobody ever visits either." },
        ["mutter"] = new[] { "Twelve hours. In the rain. Again.", "Who even needs a radio tower out here?", "My boots are full of jungle.",
                             "Guard the trees, they said. Great career move.", "If one more mosquito bites me...", "I had a nice desk job once." },
        ["suspicious"] = new[] { "Hm?", "...what was that?", "Hello? Monkey?", "I saw that. Probably." },
        ["steps"] = new[] { "Footsteps? Wonderful.", "Who's stomping around?", "Is that you, Dave?" },
        ["investigate"] = new[] { "Fine. I'll go look. Slowly.", "If this is the monkey again...", "Coming. Don't make me run." },
        ["gunfire"] = new[] { "Gunfire?! Of course. Of course there's gunfire.", "Shots! On MY shift!", "Oh, come ON." },
        ["alert"] = new[] { "THERE you are!", "I KNEW IT!", "Contact! Ugh, the paperwork...", "Intruder! Stop being an intruder!" },
        ["lost"] = new[] { "Where'd you go?", "Come out, I just want to shoot you a little.", "Hiding? Rude." },
        ["giveup"] = new[] { "...must've been the monkey.", "Not paid enough for this.", "Nobody here. Back to being miserable." },
        ["hurt"] = new[] { "Ow!", "Rude!", "That's going in my report!", "Ow! My lunch!" },
        ["dead"] = new[] { "...told them I needed a vacation.", "...tell my mother... I was right..." },
        ["won"] = new[] { "And STAY down.", "That's what you get. Ugh, more paperwork." },
        ["flank"] = new[] { "I'll go round! Keep him busy!", "Going left! Or right! One of those!", "Cover me, I'm being clever!" },
        ["flanked"] = new[] { "Got you from the side!", "Surprise! ...wait, I'm the surprise." },
        ["alarm"] = new[] { "CONTACT! Everybody up! ...Everybody? Hello?", "Over here! OVER HERE! Ugh.", "We've got company! The rude kind!" },
        ["alarmheard"] = new[] { "What? Coming! Coming!", "Ugh, FINE, I'm coming!", "Hold on, I'm coming! Don't win without me!" },
        ["body"] = new[] { "Dave?! ...DAVE!", "Oh no. Oh, that's going to be paperwork.", "Man down! Who's going to watch the radio now?!" },
        ["cover"] = new[] { "Taking cover! Very bravely!", "Behind the tree! The tree is my friend!", "You can't hit me, I'm a tree now!" },
        ["tree"] = new[] { "Did a tree just... fall over? By itself?", "TIMBER! ...wait. Who said that?", "The jungle is falling apart. Like my career.",
                           "Trees don't just DO that." },
        ["squashed"] = new[] { "...a TREE? Seriously?", "...killed by... forestry..." },
    };

    public override void _Ready()
    {
        All.Add(this);
        _rng.Seed = (ulong)(Home.X * 73856093 + Home.Z * 19349663);
        AddChild(new CollisionShape3D { Shape = new CapsuleShape3D { Radius = .32f, Height = 1.8f }, Position = new Vector3(0, .9f, 0) });
        FloorMaxAngle = Mathf.DegToRad(50);
        BuildBody();

        if (Route is { Count: > 1 })
        {
            _patrol.AddRange(Route);
            _patrolIndex = Math.Clamp(RouteStart, 0, Route.Count - 1);
            _routeBack = RouteBackwards;
        }
        // A loop of four points around home, on the ground.
        else for (int k = 0; k < 4; k++)
        {
            float a = k * MathF.Tau / 4 + _rng.RandfRange(-.4f, .4f);
            float r = Sentry ? _rng.RandfRange(.5f, 1.5f) : _rng.RandfRange(PatrolMin, PatrolMax);
            var p = Home + new Vector3(MathF.Cos(a) * r, 0, MathF.Sin(a) * r);
            p.Y = World.HeightAt(p.X, p.Z);
            _patrol.Add(p);
        }
        Hearing.Made += OnHeard;
        Target.Died += OnTargetDied;
    }

    public override void _ExitTree()
    {
        All.Remove(this);
        Hearing.Made -= OnHeard;
        if (Target != null) Target.Died -= OnTargetDied;
    }

    // ================================================================= per frame

    public override void _PhysicsProcess(double delta)
    {
        float dt = (float)delta;
        UpdateLabels(dt);
        UpdateFlash(dt);
        if (State == Mood.Dead) { Fall(dt); return; }
        if (!Terrain.HasCollisionAt(GlobalPosition)) return; // no ground loaded under him yet: stand still (far from the player)
        if (State == Mood.Captive) { CaptiveTick(dt); return; }
        if (State is Mood.Unconscious or Mood.Restrained) { OutTick(dt); return; }

        _stateTime += dt;
        Perceive(dt);
        var (goal, speed, look) = Think(dt);
        Move(goal, speed, look, dt);
        Animate(dt);
    }

    private void Perceive(float dt)
    {
        _wasSeen = _seen;
        _seen = false;
        LookForBodies();
        if (!Target.Spottable || Target.Flying) { _sinceSeen += dt; Exposure = 0; Suspicion = Math.Max(0, Suspicion - dt * .1f); return; }

        var eye = GlobalPosition + Vector3.Up * EyeHeight;
        var to = Target.Chest - eye;
        float dist = to.Length();
        bool alert = State is Mood.Alert or Mood.Search;
        // Rain and darkness hide you; a lit weapon light undoes the darkness part.
        float sight = Target.Lit ? 1 - .4f * (Weather.Current?.Intensity ?? 0) : Weather.Current?.SightFactor ?? 1;
        float range = (alert ? AlertViewRange : ViewRange) * sight * DeadRecon.UI.Settings.GuardSight;
        var forward = -GlobalTransform.Basis.Z;
        float cos = forward.Dot(to / Math.Max(dist, .01f));
        // How much of the player he could see: terrain must not be in the way, plants let some through.
        // Chest or head, whichever shows more (a head over the ferns is a smaller target).
        Exposure = 0;
        if (dist < range && (cos > (alert ? AlertViewCos : ViewCos) || dist < 3.5f))
        {
            var head = Target.Camera.GlobalPosition;
            float chest = LineOfSight(eye, Target.Chest) ? Foliage.Transmission(eye, Target.Chest) : 0;
            float top = LineOfSight(eye, head) ? Foliage.Transmission(eye, head) * .7f : 0;
            Exposure = Math.Max(chest, top);
            if (dist < 3.5f && Exposure > 0) Exposure = Math.Max(Exposure, .4f); // that close, he'd notice the rustling
        }
        if (Exposure > MinExposure)
        {
            _seen = true;
            float near = MathF.Pow(1 - dist / range, .7f) * Exposure;
            float stance = Target.Crouching ? .45f : 1f;
            float motion = Target.Running ? 1.5f : Target.Velocity.LengthSquared() < .1f ? .55f : 1f;
            float focus = cos > .85f ? 1f : .6f;                         // corner of the eye is less sharp
            float rate = near * stance * motion * focus * 1.4f * (alert ? 4 : 1) * DeadRecon.UI.Settings.GuardSight * AlertnessFactor;
            Suspicion = Math.Min(1, Suspicion + rate * dt);
            _lastKnown = Target.GlobalPosition;
            var tv = Target.Velocity; tv.Y = 0;
            if (tv.LengthSquared() > .3f) _lastVel = tv;
            _sinceSeen = 0;
        }
        else
        {
            _sinceSeen += dt;
            if (State is Mood.Patrol or Mood.Suspicious) Suspicion = Math.Max(0, Suspicion - dt * .06f);
        }
    }

    /// <summary>
    /// The best spot within 16 m behind a trunk (as seen from where the player was last seen), not much closer to
    /// the player than he is now; null if there's no tree worth hiding behind.
    /// </summary>
    private Vector3? FindCover()
    {
        var here = GlobalPosition;
        var threat = _lastKnown + Vector3.Up * 1.3f;
        float best = float.MaxValue;
        Vector3? pick = null;
        foreach (var t in Foliage.TrunksNear(here, 16))
        {
            if (t.TrunkRadius < .18f) continue;                       // saplings don't stop bullets for long
            var trunk = new Vector3(t.X, t.BaseY, t.Z);
            var away = trunk - _lastKnown; away.Y = 0;
            if (away.LengthSquared() < 4) continue;
            away = away.Normalized();
            var spot = trunk + away * (t.TrunkRadius + .55f);
            spot.Y = World.HeightAt(spot.X, spot.Z);
            if (!Foliage.Blocked(spot + Vector3.Up * EyeHeight, threat, t)) continue;
            float cost = Flat(spot - here) + Math.Max(0, Flat(_lastKnown - here) - Flat(_lastKnown - spot)) * 1.5f;
            if (cost < best) { best = cost; pick = spot; }
        }
        return pick;
    }

    private bool CoverStillGood(Vector3 spot)
    {
        var threat = !Target.Spottable ? _lastKnown : (_seen ? Target.GlobalPosition : _lastKnown);
        return Foliage.RaycastTrunk(spot + Vector3.Up * EyeHeight, threat + Vector3.Up * 1.3f) != null;
    }

    /// <summary>A step to the side of the cover spot from which the threat is visible.</summary>
    private Vector3 PeekSpot(Vector3 spot)
    {
        var to = _lastKnown - spot; to.Y = 0; to = to.Normalized();
        var side = to.Cross(Vector3.Up);
        foreach (float sgn in _rng.Randf() < .5f ? new[] { 1f, -1f } : new[] { -1f, 1f })
        {
            var p = spot + side * sgn * .95f + to * .2f;
            p.Y = World.HeightAt(p.X, p.Z);
            if (Foliage.RaycastTrunk(p + Vector3.Up * EyeHeight, _lastKnown + Vector3.Up * 1.3f) == null) return p;
        }
        return spot + side * 1.2f;
    }

    /// <summary>A fallen colleague in view: he knows something is very wrong (but not where you are).</summary>
    private void LookForBodies()
    {
        if (State is Mood.Alert or Mood.Dead) return;
        var eye = GlobalPosition + Vector3.Up * EyeHeight;
        var forward = -GlobalTransform.Basis.Z;
        foreach (var g in All)
        {
            if (g == this || g.State is not (Mood.Dead or Mood.Unconscious or Mood.Restrained) || _bodiesSeen.Contains(g)) continue;
            var body = g.GlobalPosition + Vector3.Up * .3f;
            var to = body - eye;
            float d = to.Length();
            if (d > ViewRange * (Weather.Current?.SightFactor ?? 1) || (forward.Dot(to / d) < ViewCos && d > 4)) continue;
            if (!LineOfSight(eye, body) || Foliage.Transmission(eye, body) < .2f) continue;
            _bodiesSeen.Add(g);
            BodiesFound++;
            _lastKnown = g.GlobalPosition;
            Suspicion = Math.Max(Suspicion, .9f);
            _hurry = true;
            Enter(Mood.Investigate, "body");
            return;
        }
    }

    private bool LineOfSight(Vector3 from, Vector3 to)
    {
        var q = PhysicsRayQueryParameters3D.Create(from, to);
        q.Exclude = new Godot.Collections.Array<Rid> { GetRid() };
        var hit = GetWorld3D().DirectSpaceState.IntersectRay(q);
        return hit.Count == 0 || hit["collider"].AsGodotObject() == Target;
    }

    /// <summary>Decide where to go (goal), how fast, and what to look at (null = where he walks).</summary>
    private (Vector3 goal, float speed, Vector3? look) Think(float dt)
    {
        var here = GlobalPosition;
        switch (State)
        {
            case Mood.Patrol:
            {
                if (Suspicion >= .3f) { _sitting = 0; Enter(Mood.Suspicious, "suspicious"); return (here, 0, _lastKnown); }
                if (VisibleComrade(30) is { } stranger) { _sitting = 0; _lastKnown = stranger.GlobalPosition; EnterAlert(); Bark("alert"); return (here, 0, stranger.GlobalPosition); }
                _mutter -= dt;
                if (_mutter <= 0) { _mutter = _rng.RandfRange(16, 30); Bark(Sentry ? "sentry" : "mutter"); }
                TryChat(dt);
                // The sentry sits down now and then (and sees less: a sitting man looks at the fire, not the jungle).
                if (Sentry)
                {
                    if (_sitting > 0) { _sitting -= dt; return (here, 0, here - GlobalTransform.Basis.Z); }
                    _sitTimer -= dt;
                    if (_sitTimer <= 0) { _sitTimer = _rng.RandfRange(25, 45); _sitting = _rng.RandfRange(12, 22); SitsStarted++; }
                }
                var p = _patrol[_patrolIndex];
                if (Route != null && (Flat(here - p) < 1.5f || _stuck > 2.5f))
                {
                    // Road patrol: straight on to the next point; at either end, a pause, then turn round.
                    _stuck = 0;
                    int next = _patrolIndex + (_routeBack ? -1 : 1);
                    if (next < 0 || next >= _patrol.Count)
                    {
                        if (_wait <= 0) _wait = _rng.RandfRange(8, 14);
                        _wait -= dt;
                        if (_wait > 0) return (here, 0, null);
                        _routeBack = !_routeBack;
                        next = _patrolIndex + (_routeBack ? -1 : 1);
                    }
                    _patrolIndex = Math.Clamp(next, 0, _patrol.Count - 1);
                    p = _patrol[_patrolIndex];
                }
                else if (Flat(here - p) < 1.2f || _stuck > 2.5f)
                {
                    _stuck = 0;
                    if (_wait <= 0) _wait = _rng.RandfRange(2, 5);
                    _wait -= dt;
                    _lookAround += dt;
                    if (_wait <= 0) _patrolIndex = (_patrolIndex + 1) % _patrol.Count;
                    var glance = here + new Vector3(MathF.Sin(_lookAround * .8f), 0, MathF.Cos(_lookAround * .8f)) * 5;
                    return (here, 0, glance);
                }
                return (p, WalkSpeed, null);
            }
            case Mood.Suspicious:
                if (Suspicion >= .7f) { Enter(Mood.Investigate, "investigate"); return (_lastKnown, InvestigateSpeed, null); }
                if (_stateTime > 6 && Suspicion < .25f) { Enter(Mood.Patrol, "giveup"); Suspicion = 0; }
                return (here, 0, _lastKnown);

            case Mood.Investigate:
                if (_seen && Suspicion >= 1) { EnterAlert(); return (here, 0, _lastKnown); }
                if (Flat(here - _lastKnown) < 2.5f || _stateTime > 40 || _stuck > 3) { _hurry = false; StartSearch(); return (here, 0, null); }
                return (_lastKnown, _hurry ? RunSpeed : InvestigateSpeed, null);

            case Mood.Alert:
            {
                // Flanker: circle round to the player's side while the other guard keeps him busy.
                if (_flankPoint is { } fp)
                {
                    _flankTime += dt;
                    if (_seen && _flankTime > 2) { _flankPoint = null; Bark("flanked"); }
                    else if (Flat(here - fp) < 2.5f || _stuck > 2.5f || _flankTime > 25) _flankPoint = null;
                    else return (fp, RunSpeed, null);
                }
                // Fight from behind a trunk: hide, step out to shoot a burst, hide again.
                _coverCheck -= dt;
                if (_coverCheck <= 0)
                {
                    _coverCheck = 1;
                    if (_cover is { } c && !CoverStillGood(c)) { _cover = null; _peeking = false; }
                    if (_cover == null && _sinceSeen < 6 && FindCover() is { } found)
                    {
                        _cover = found; _peeking = false; _coverTimer = 0;
                        if (_rng.Randf() < .5f) Bark("cover");
                    }
                }
                if (_cover is { } spot)
                {
                    if (Flat(here - spot) > .9f && !_peeking) return (spot, RunSpeed, Target.GlobalPosition);
                    _coverTimer -= dt;
                    if (_coverTimer <= 0)
                    {
                        _peeking = !_peeking;
                        _coverTimer = _peeking ? _rng.RandfRange(1.4f, 2.4f) : _rng.RandfRange(1.2f, 2.6f);
                        if (_peeking) { _peekSpot = PeekSpot(spot); _reaction = Math.Min(_reaction, ReactionTime * .5f); }
                    }
                    if (_peeking)
                    {
                        if (_seen)
                        {
                            _reaction -= dt;
                            _cooldown -= dt;
                            if (_reaction <= 0 && _cooldown <= 0) Shoot();
                        }
                        else if (_sinceSeen < 5)
                        {
                            // Can't see you, knows where you were: keep your head down.
                            _cooldown -= dt;
                            if (_cooldown <= 0) Suppress();
                        }
                        return (_peekSpot, InvestigateSpeed, _lastKnown);
                    }
                    if (_sinceSeen < 10) return (spot, InvestigateSpeed, _lastKnown);   // hiding, waiting to peek
                    _cover = null;                                                       // he's been quiet too long: go look
                }
                // Can't see you, but one of your soldiers is right there: him, then.
                if (!_seen && VisibleComrade(70) is { } foe)
                {
                    _lastKnown = foe.GlobalPosition;
                    _cooldown -= dt;
                    if (_cooldown <= 0) ShootAt(foe);
                    return (here, 0, foe.GlobalPosition);
                }
                if (_seen)
                {
                    if (!_wasSeen) _reaction = ReactionTime * .6f;          // spotted again: quicker this time
                    _reaction -= dt;
                    _cooldown -= dt;
                    if (_reaction <= 0 && _cooldown <= 0) Shoot();
                    return (here, 0, Target.GlobalPosition);
                }
            }
                if (_sinceSeen > 2.5f)
                {
                    if (_sinceSeen < 2.6f) Bark("lost");
                    if (Flat(here - _lastKnown) < 2.5f || _stuck > 3) { StartSearch(); return (here, 0, null); }
                    return (_lastKnown, RunSpeed, null);
                }
                if (_sinceSeen > .6f) { _cooldown -= dt; if (_cooldown <= 0) Suppress(); }
                return (here, 0, _lastKnown);                                 // just lost sight: keep firing there a moment

            case Mood.Search:
                if (_seen) { Suspicion = 1; EnterAlert(); return (here, 0, _lastKnown); }
                _searchLeft -= dt;
                if (_searchLeft <= 0) { Enter(Mood.Patrol, "giveup"); Suspicion = 0; return (here, 0, null); }
                if (Flat(here - _searchPoint) < 1.5f || _stuck > 2.5f) { _stuck = 0; PickSearchPoint(); }
                return (_searchPoint, InvestigateSpeed, null);
        }
        return (here, 0, null);
    }

    private void Enter(Mood mood, string? line = null)
    {
        if (State != mood) GD.Print($"[guard] {State} -> {mood} (suspicion {Suspicion:0.00}, sees player {_seen})");
        State = mood;
        _stateTime = 0;
        _stuck = 0;
        if (line != null) Bark(line);
    }

    private void EnterAlert()
    {
        bool fresh = State != Mood.Alert;
        if (fresh) TimesAlerted++;
        Suspicion = 1;
        Enter(Mood.Alert, fresh ? "alert" : null);
        if (fresh) { _reaction = ReactionTime; _cooldown = 0; _burst = 0; _warningShots = 3; _cover = null; _peeking = false; RaiseAlarm(); PlanFlank(); }
    }

    /// <summary>
    /// If a colleague is already in the fight and nobody is flanking yet, go round the side: a point beside where the
    /// player was, at right angles to the line from that colleague, on whichever side is closer to us.
    /// </summary>
    private void PlanFlank()
    {
        Guard? holder = null;
        foreach (var g in All)
        {
            if (g == this || g.State != Mood.Alert) continue;
            if (g.Flanking) return;
            holder ??= g;
        }
        if (holder == null) return;
        var line = _lastKnown - holder.GlobalPosition; line.Y = 0;
        if (line.LengthSquared() < 1) return;
        line = line.Normalized();
        var side = line.Cross(Vector3.Up);
        if (side.Dot(GlobalPosition - _lastKnown) < 0) side = -side;
        var p = _lastKnown + side * 17 - line * 3;
        p.Y = World.HeightAt(p.X, p.Z);
        _flankPoint = p;
        _flankTime = 0;
        FlanksStarted++;
        Bark("flank");
    }

    /// <summary>A hidden or seated guard has a narrower world: used by perception.</summary>
    private float AlertnessFactor => _sitting > 0 ? .6f : 1;

    /// <summary>Chat with a nearby calm colleague: one line each, a few seconds apart.</summary>
    private void TryChat(float dt)
    {
        if (!Sentry) return;                          // the sentry starts conversations (he's bored)
        _chatCooldown -= dt;
        if (_chatCooldown > 0) return;
        foreach (var g in All)
        {
            if (g == this || g.State != Mood.Patrol || g.GlobalPosition.DistanceTo(GlobalPosition) > 14) continue;
            _chatCooldown = _rng.RandfRange(35, 60);
            Chats_++;
            var pairs = DeadRecon.Data.Content.Get("guard_chats", Chats.Select(c => new[] { c.A, c.B }).ToArray());
            var pick = pairs[_rng.RandiRange(0, pairs.Length - 1)];
            var (a, b) = (pick[0], pick.Length > 1 ? pick[1] : "...");
            Say(a);
            var other = g;
            GetTree().CreateTimer(2.6).Timeout += () => { if (IsInstanceValid(other) && other.State == Mood.Patrol) other.Say(b); };
            return;
        }
    }

    /// <summary>
    /// Suppressive fire: a burst at where he last saw you. Mostly it keeps your head down, but if you're still
    /// there and in his line (just hidden by leaves), it can find you.
    /// </summary>
    private void Suppress()
    {
        SuppressShots++;
        if (_burst <= 0) _burst = _rng.RandiRange(2, 4);
        _burst--;
        _cooldown = _burst > 0 ? .22f : _rng.RandfRange(1.6f, 2.8f);
        _flash = 0;
        _shot.PitchScale = .95f + _rng.Randf() * .1f;
        _shot.Play();
        DeadRecon.Audio.Ambience.Current?.Echo(GlobalPosition);

        var eye = GlobalPosition + Vector3.Up * EyeHeight;
        var from = _muzzle?.GlobalPosition ?? eye;
        var aim = _lastKnown + Vector3.Up * _rng.RandfRange(.4f, 1.6f) + new Vector3(_rng.RandfRange(-1.5f, 1.5f), 0, _rng.RandfRange(-1.5f, 1.5f));
        // Still there, and only leaves in the way (no trunk, no hill)? A small chance to hit.
        if (Target.GlobalPosition.DistanceTo(_lastKnown) < 2.5f && Target.Spottable
            && Foliage.RaycastTrunk(eye, Target.Chest) == null && LineOfSight(eye, Target.Chest)
            && _rng.Randf() < .15f * DeadRecon.UI.Settings.GuardAccuracy)
        {
            Effects.Tracer(this, from, Target.Chest, enemy: true);
            Target.TakeHit(ShotDamage * _rng.RandfRange(.6f, 1f), Target.Chest, eye);
            return;
        }
        var dir = (aim - eye).Normalized();
        float dist = eye.DistanceTo(aim) + 25;
        var q = PhysicsRayQueryParameters3D.Create(eye, eye + dir * dist);
        q.Exclude = new Godot.Collections.Array<Rid> { GetRid(), Target.GetRid() };
        var hit = GetWorld3D().DirectSpaceState.IntersectRay(q);
        var end = hit.Count > 0 ? (Vector3)hit["position"] : eye + dir * dist;
        var head = Target.Camera.GlobalPosition;
        var seg = end - eye;
        float along = Math.Clamp((head - eye).Dot(seg) / seg.LengthSquared(), 0, 1);
        if ((eye + seg * along).DistanceTo(head) < 3.5f) Target.HearCrack();
        var trunk = Foliage.RaycastTrunk(eye, end);
        Effects.Tracer(this, from, trunk?.Point ?? end, enemy: true);
        if (trunk is { } t) { Effects.Splinters(this, t.Point, t.Normal, dir); Foliage.ReportHit(t.Plant, t.Point, dir, 30); }
        else if (hit.Count > 0) Effects.Impact(this, (Vector3)hit["position"], (Vector3)hit["normal"]);
    }

    /// <summary>Shout: guards within earshot (less in rain) come running to roughly where he's looking.</summary>
    private void RaiseAlarm()
    {
        float reach = Hearing.Carry(AlarmRadius);
        bool anyone = false;
        foreach (var g in All)
        {
            if (g == this || g.State == Mood.Dead || g.GlobalPosition.DistanceTo(GlobalPosition) > reach) continue;
            anyone = true;
            g.HearAlarm(_lastKnown);
        }
        if (anyone) _bubbleLeft = 0; // let the alarm line show instead of the alert line
        if (anyone) Bark("alarm");
    }

    /// <summary>Reinforcements: run to the trouble.</summary>
    public void Respond(Vector3 where) => HearAlarm(where);

    // ================================================================= melee: knocked out, cuffed

    /// <summary>Out of the fight without being dead: knocked out (wakes up in a while) or cuffed (stays put).</summary>
    public bool Out => State is Mood.Unconscious or Mood.Restrained;
    private float _koLeft;
    private bool _fWasOut;
    private float _mumble;

    /// <summary>A rifle butt. Someone who's fighting you just gets hurt; anyone else goes down and stays down a while.</summary>
    public void Bashed(Vector3 from)
    {
        if (State is Mood.Dead or Mood.Restrained) return;
        if (State == Mood.Alert || (State == Mood.Captive && !Bolting) || State == Mood.Unconscious)
        {
            TakeHit(State == Mood.Unconscious ? 0 : 30, GlobalPosition + Vector3.Up * 1.5f, from);
            if (State == Mood.Alert) Say("OW! You—");
            return;
        }
        State = Mood.Unconscious;
        Bolting = false;
        _koLeft = 50 + Looks.Randf() * 30;
        Lie(true);
        GD.Print($"[guard] {Name} knocked out ({_koLeft:0} s)");
    }

    /// <summary>Cuffed: he isn't going anywhere, now or later.</summary>
    public void Restrain()
    {
        if (State != Mood.Unconscious) return;
        State = Mood.Restrained;
        if (_body != null) _body.GunVisible = false;
        if (_gunPivot != null) _gunPivot.Visible = false;
        GD.Print($"[guard] {Name} cuffed");
    }

    /// <summary>Lying on his back (knocked out, cuffed) or back on his feet.</summary>
    private void Lie(bool down)
    {
        var rot = new Vector3(down ? MathF.PI / 2 : 0, 0, 0);
        if (_body != null) _body.Rotation = rot; else if (_rig != null) _rig.Rotation = rot;
        var shape = GetChild<CollisionShape3D>(0);
        shape.Rotation = rot;
        shape.Position = down ? new Vector3(0, .3f, .85f) : new Vector3(0, .9f, 0);
        if (_body != null) { _body.Speed = 0; _body.Ready = 0; _body.Crouch = 0; }
    }

    private void OutTick(float dt)
    {
        Velocity = new Vector3(0, IsOnFloor() ? -.5f : Velocity.Y - 9.8f * dt, 0);
        MoveAndSlide();
        bool f = Input.IsPhysicalKeyPressed(Key.F) && Input.MouseMode == Input.MouseModeEnum.Captured, fPressed = f && !_fWasOut;
        _fWasOut = f;
        bool near = !Target.Dead && Target.GlobalPosition.DistanceTo(GlobalPosition) < 2.3f;
        if (State == Mood.Restrained)
        {
            _mumble -= dt;
            if (near && _mumble <= 0) { _mumble = 8 + Looks.Randf() * 6; string[] m = { "Mmmf!", "Mmf mmf MMF.", "...mmmf." }; Say(m[(int)(Looks.Randi() % 3)]); }
            return;
        }
        // (On a capture job the officer is the mission's business: F gets him up and walking, not cuffed to the ground.)
        if (near && !(DeadRecon.Missions.Mission.Current is { Kind: DeadRecon.Missions.Mission.Op.Capture } op && op.Officer == this))
        {
            DeadRecon.UI.Hud.Prompt("F   Cuff him");
            if (fPressed) { Restrain(); return; }
        }
        _koLeft -= dt;
        if (_koLeft > 0) return;
        // Awake, with a headache and a grudge.
        Lie(false);
        State = Mood.Patrol;
        Say("Ugh... my HEAD... WHO DID THAT?!");
        Respond(GlobalPosition);
        GD.Print($"[guard] {Name} woke up");
    }

    // ================================================================= captured (docs/MISSIONS_PLAN.md: capture is physical)

    public bool Captive => State == Mood.Captive;
    /// <summary>A captive who's legging it.</summary>
    public bool Bolting { get; private set; }
    /// <summary>A captive who got clean away.</summary>
    public bool Escaped { get; private set; }
    private float _unwatched;
    private bool _fWasCaptive;

    /// <summary>Hands up: no gun, no fight. He walks a few metres ahead of you; look away too long and he runs.</summary>
    public void Surrender()
    {
        if (State == Mood.Dead) return;
        if (Out) Lie(false);
        State = Mood.Captive;
        Bolting = false; Escaped = false; _unwatched = 0;
        if (_body != null) _body.GunVisible = false;
        if (_gunPivot != null) _gunPivot.Visible = false;
        string[] lines = { "Alright! Alright! Don't shoot!", "...I surrender. Please don't tell my mother.", "Hands up. See? Hands. Up." };
        Say(lines[(int)(Looks.Randi() % (uint)lines.Length)]);
        GD.Print($"[guard] {Name} surrendered");
    }

    private void CaptiveTick(float dt)
    {
        var p = Target.GlobalPosition;
        var flat = (GlobalPosition - p) with { Y = 0 };
        float d = flat.Length();
        bool f = Input.IsPhysicalKeyPressed(Key.F) && Input.MouseMode == Input.MouseModeEnum.Captured, fPressed = f && !_fWasCaptive;
        _fWasCaptive = f;
        if (Bolting)
        {
            if (d > 45) { Escaped = true; return; }
            if (d < 2.5f && !Target.Dead)
            {
                DeadRecon.UI.Hud.Prompt("F   \"Get back here.\"");
                if (fPressed) { Bolting = false; _unwatched = 0; Say("Alright! I was... stretching my legs."); return; }
            }
            var away = d > .1f ? flat / d : Vector3.Forward;
            Move(GlobalPosition + away * 10, 4.6f, null, dt);
            return;
        }
        // Is he being watched? (Your view, roughly toward him, and not too far.)
        var cam = Target.Camera.GlobalTransform;
        var to = (GlobalPosition + Vector3.Up * 1.2f - cam.Origin).Normalized();
        bool watched = !Target.Dead && -cam.Basis.Z.Dot(to) > .75f && d < 25;
        _unwatched = watched ? 0 : _unwatched + dt;
        if ((_unwatched > 6 && d > 4) || d > 18 || Target.Dead)
        {
            Bolting = true;
            Say("...!");
            GD.Print($"[guard] captive {Name} bolted");
            return;
        }
        // Walk ahead of you, where you can see him.
        var fwd = -Target.GlobalTransform.Basis.Z with { Y = 0 };
        var goal = p + (fwd.LengthSquared() > .01f ? fwd.Normalized() : Vector3.Forward) * 3.5f;
        float gd = new Vector2(goal.X - GlobalPosition.X, goal.Z - GlobalPosition.Z).Length();
        Move(goal, gd > 4 ? 3.2f : gd > .8f ? 1.5f : 0, gd > .8f ? goal : p, dt);
    }

    private void HearAlarm(Vector3 where)
    {
        if (State is Mood.Alert or Mood.Dead) return;
        // Head for fighting distance on his own side of the trouble, not into the middle of it.
        var back = GlobalPosition - where; back.Y = 0;
        float d = back.Length();
        var near = d > 1 ? where + back / d * Math.Min(14, d * .6f) : where;
        _lastKnown = near + new Vector3(_rng.RandfRange(-4, 4), 0, _rng.RandfRange(-4, 4));
        Suspicion = Math.Max(Suspicion, .9f);
        _hurry = true;
        Enter(Mood.Investigate, "alarmheard");
    }

    private void StartSearch()
    {
        _cover = null; _peeking = false; _flankPoint = null;
        Enter(Mood.Search, "lost");
        _searchLeft = SearchTime;
        // A plan, not random wandering: where you'd have gone (the way you were heading), then behind the nearest
        // trees (where you'd hide), then around.
        _searchPlan.Clear();
        if (_lastVel.LengthSquared() > .3f)
        {
            var heading = _lastVel.Normalized();
            _searchPlan.Enqueue(_lastKnown + heading * 7);
            _searchPlan.Enqueue(_lastKnown + heading * 15);
        }
        var trunks = Foliage.TrunksNear(_lastKnown, 12);
        trunks.Sort((a, b) => new Vector2(a.X - _lastKnown.X, a.Z - _lastKnown.Z).Length().CompareTo(new Vector2(b.X - _lastKnown.X, b.Z - _lastKnown.Z).Length()));
        int n = 0;
        foreach (var t in trunks)
        {
            if (t.TrunkRadius < .2f || n >= 3) continue;
            var away = new Vector3(t.X - GlobalPosition.X, 0, t.Z - GlobalPosition.Z).Normalized();
            _searchPlan.Enqueue(new Vector3(t.X, 0, t.Z) + away * (Math.Max(t.TrunkRadius, t.FootRadius) + 1.2f));   // its far side
            n++;
        }
        _lastVel = Vector3.Zero;
        PickSearchPoint();
    }

    private void PickSearchPoint()
    {
        if (_searchPlan.Count > 0) { _searchPoint = _searchPlan.Dequeue(); return; }
        float a = _rng.Randf() * MathF.Tau, r = _rng.RandfRange(4, 14);
        _searchPoint = _lastKnown + new Vector3(MathF.Cos(a) * r, 0, MathF.Sin(a) * r);
    }

    // ================================================================= senses: hearing, getting shot

    private void OnHeard(Vector3 at, float radius, Node source)
    {
        if (State == Mood.Dead || source == this) return;
        float d = GlobalPosition.DistanceTo(at);
        if (d > radius) return;
        if (radius >= Hearing.Gunshot)
        {
            // He knows roughly where it came from: the further away, the rougher the guess.
            var guess = at + new Vector3(_rng.RandfRange(-1, 1), 0, _rng.RandfRange(-1, 1)) * d * .15f;
            if (d < 45 && State != Mood.Alert) { _lastKnown = guess; EnterAlert(); Bark("gunfire"); }
            else if (State is Mood.Patrol or Mood.Suspicious or Mood.Search)
            {
                _lastKnown = guess;
                Suspicion = Math.Max(Suspicion, .8f);
                Enter(Mood.Investigate, "gunfire");
            }
        }
        else if (source is TreeScatter)
        {
            // A tree crashing down nearby: very suspicious, but no idea who did it.
            if (State is Mood.Alert) return;
            _lastKnown = at;
            Suspicion = Math.Max(Suspicion, .75f);
            Enter(Mood.Investigate, "tree");
        }
        else if (State is Mood.Patrol or Mood.Suspicious)
        {
            _lastKnown = at;
            Suspicion = Math.Max(Suspicion, .45f);
            if (State == Mood.Patrol) Enter(Mood.Suspicious, "steps");
        }
    }

    public void TakeHit(float damage, Vector3 at, Vector3 from)
    {
        if (State == Mood.Dead) return;
        bool headshot = at.Y - GlobalPosition.Y > 1.45f * (1 - .32f * _crouch);
        Health -= headshot ? damage * 3 : damage;
        GD.Print($"[guard] hit for {(headshot ? damage * 3 : damage):0}{(headshot ? " (headshot)" : "")}, health {Math.Max(Health, 0):0}");
        bool squashed = damage >= 60; // only falling trees hit that hard
        if (!squashed) Effects.BloodHit(this, at, at - from);
        if (Health <= 0) { Die(squashed, at, at - from); return; }
        _lastKnown = from;          // he felt where that came from
        EnterAlert();
        Bark("hurt");
    }

    private void OnTargetDied()
    {
        if (State is Mood.Alert or Mood.Search) { Bark("won"); Enter(Mood.Search); _searchLeft = 8; }
    }

    /// <summary>The player respawned: whatever he was doing, he only remembers it vaguely.</summary>
    public void Forget()
    {
        if (State == Mood.Dead) return;
        Suspicion = Math.Min(Suspicion, .5f);
        if (State is Mood.Alert or Mood.Investigate) { StartSearch(); _searchLeft = 10; }
    }

    private void Die(bool squashed, Vector3 at, Vector3 shotDir)
    {
        Enter(Mood.Dead, squashed ? "squashed" : "dead");
        _body?.Ragdoll(at, squashed ? Vector3.Down : shotDir, squashed ? 2 : 1);
        _deathTime = 0;
        _icon.Visible = false;
        GetChild<CollisionShape3D>(0).SetDeferred(CollisionShape3D.PropertyName.Disabled, true);
    }

    // ================================================================= shooting

    private float _comradeCheck;
    private DeadRecon.People.Comrade? _comradeSeen;

    /// <summary>One of the player's soldiers he can see (cheap: checked a few times a second).</summary>
    private DeadRecon.People.Comrade? VisibleComrade(float range)
    {
        _comradeCheck -= (float)GetPhysicsProcessDeltaTime();
        if (_comradeCheck > 0) return _comradeSeen is { Down: false, Dead: false } c && IsInstanceValid(c) ? c : null;
        _comradeCheck = .4f;
        _comradeSeen = null;
        var eye = GlobalPosition + Vector3.Up * EyeHeight;
        foreach (var c in DeadRecon.People.Comrade.All)
        {
            if (c.Down || c.Dead || c.Orders == DeadRecon.People.Comrade.Order.Base) continue;
            float d = eye.DistanceTo(c.Chest);
            if (d > range * (Weather.Current?.SightFactor ?? 1)) continue;
            var forward = -GlobalTransform.Basis.Z;
            if (forward.Dot((c.Chest - eye).Normalized()) < -.2f && d > 8) continue;   // not behind him
            var q = PhysicsRayQueryParameters3D.Create(eye, c.Chest);
            q.Exclude = new Godot.Collections.Array<Rid> { GetRid() };
            var hit = GetWorld3D().DirectSpaceState.IntersectRay(q);
            if (hit.Count > 0 && hit["collider"].AsGodotObject() != c) continue;
            if (Foliage.RaycastTrunk(eye, c.Chest) != null || Foliage.Transmission(eye, c.Chest) < .3f) continue;
            _comradeSeen = c;
            break;
        }
        return _comradeSeen;
    }

    private void ShootAt(DeadRecon.People.Comrade c)
    {
        if (_burst <= 0) _burst = _rng.RandiRange(2, 4);
        _burst--;
        _cooldown = _burst > 0 ? .22f : _rng.RandfRange(1.1f, 2f);
        _flash = 0;
        _shot.PitchScale = .95f + _rng.Randf() * .1f;
        _shot.Play();
        var eye = GlobalPosition + Vector3.Up * EyeHeight;
        var from = _muzzle?.GlobalPosition ?? eye;
        float chance = Math.Clamp(.5f * (1 - eye.DistanceTo(c.Chest) / 90), .05f, .5f) * DeadRecon.UI.Settings.GuardAccuracy;
        if (_rng.Randf() < chance) { Effects.Tracer(this, from, c.Chest, enemy: true); c.TakeHit(ShotDamage * _rng.RandfRange(.6f, 1f), c.Chest, eye); }
        else Effects.Tracer(this, from, c.Chest + new Vector3(_rng.RandfRange(-2, 2), _rng.RandfRange(-1, 1.5f), _rng.RandfRange(-2, 2)), enemy: true);
    }

    private void Shoot()
    {
        // Bursts of 2-4, then a pause.
        if (_burst <= 0) _burst = _rng.RandiRange(2, 4);
        _burst--;
        _cooldown = _burst > 0 ? .2f : _rng.RandfRange(1f, 1.8f);

        _flash = 0;
        _shot.PitchScale = .95f + _rng.Randf() * .1f;
        _shot.Play();
        DeadRecon.Audio.Ambience.Current?.Echo(GlobalPosition);

        var eye = GlobalPosition + Vector3.Up * EyeHeight;
        float dist = eye.DistanceTo(Target.Chest);
        float chance = Math.Clamp(.55f * (1 - dist / 110), .05f, .55f) * DeadRecon.UI.Settings.GuardAccuracy;
        if (_warningShots > 0) { _warningShots--; chance *= .3f; } // the first shots after spotting you mostly miss: a chance to dive for cover
        if (Target.Running) chance *= .5f;
        else if (Target.Velocity.LengthSquared() > .5f) chance *= .75f;
        if (Target.Crouching) chance *= .8f;
        // A trunk between us takes the bullet, hit or miss: hiding behind a tree works.
        var from = _muzzle?.GlobalPosition ?? eye;
        if (Foliage.RaycastTrunk(eye, Target.Chest) is { } wood)
        {
            Effects.Tracer(this, from, wood.Point, enemy: true);
            var shotDir = (Target.Chest - eye).Normalized();
            Effects.Splinters(this, wood.Point, wood.Normal, shotDir);
            Foliage.ReportHit(wood.Plant, wood.Point, shotDir, 30);
            return;
        }
        if (_rng.Randf() < chance)
        {
            Effects.Tracer(this, from, Target.Chest - (Target.Chest - from).Normalized() * .4f, enemy: true);
            Target.TakeHit(ShotDamage * _rng.RandfRange(.6f, 1f), Target.Chest, eye);
            return;
        }
        // Miss: kick up dirt somewhere near the player so you know you're being shot at.
        var miss = Target.GlobalPosition + new Vector3(_rng.RandfRange(-2.2f, 2.2f), 0, _rng.RandfRange(-2.2f, 2.2f));
        var dir = (miss - eye).Normalized();
        var q = PhysicsRayQueryParameters3D.Create(eye, eye + dir * (dist + 30));
        q.Exclude = new Godot.Collections.Array<Rid> { GetRid(), Target.GetRid() };
        var hit = GetWorld3D().DirectSpaceState.IntersectRay(q);
        var end = hit.Count > 0 ? (Vector3)hit["position"] : eye + dir * (dist + 30);
        // A miss that passes close to your head: you hear the supersonic crack (the best "you're being shot at" cue).
        var head = Target.Camera.GlobalPosition;
        var seg = end - eye;
        float along = Math.Clamp((head - eye).Dot(seg) / seg.LengthSquared(), 0, 1);
        if ((eye + seg * along).DistanceTo(head) < 3.5f) Target.HearCrack();
        Effects.Tracer(this, from, Foliage.RaycastTrunk(eye, end)?.Point ?? end, enemy: true);
        if (Foliage.RaycastTrunk(eye, end) is { } tree) { Effects.Splinters(this, tree.Point, tree.Normal, dir); Foliage.ReportHit(tree.Plant, tree.Point, dir, 30); }
        else if (hit.Count > 0) Effects.Impact(this, (Vector3)hit["position"], (Vector3)hit["normal"]);
    }

    // ================================================================= movement and body

    private void Move(Vector3 goal, float speed, Vector3? look, float dt)
    {
        var to = goal - GlobalPosition;
        to.Y = 0;
        var v = Velocity;
        if (speed > 0 && to.Length() > .3f)
        {
            var dir = to.Normalized();
            v.X = dir.X * speed; v.Z = dir.Z * speed;
            look ??= GlobalPosition + dir;
        }
        else { v.X = 0; v.Z = 0; }
        v.Y = IsOnFloor() ? -.5f : v.Y - Gravity * dt;
        Velocity = v;
        var before = GlobalPosition;
        MoveAndSlide();
        GlobalPosition = DeadRecon.World.Foliage.PushOut(GlobalPosition, .35f); // trunks are solid
        if (speed > 0 && Flat(GlobalPosition - before) < speed * dt * .25f) _stuck += dt; else _stuck = 0;

        if (look is { } l)
        {
            var d = l - GlobalPosition; d.Y = 0;
            if (d.LengthSquared() > .01f)
            {
                float want = MathF.Atan2(-d.X, -d.Z);
                float turn = State == Mood.Alert ? 7 : 3;
                Rotation = new Vector3(0, Mathf.LerpAngle(Rotation.Y, want, Math.Min(1, dt * turn)), 0);
            }
            // Head (and gun, when alert) tilts toward what he's looking at.
            var e = l + (State == Mood.Alert ? Vector3.Up * 1.2f : Vector3.Up * 1.6f) - (GlobalPosition + Vector3.Up * EyeHeight);
            float pitch = MathF.Atan2(e.Y, new Vector2(e.X, e.Z).Length());
            _head.Rotation = new Vector3(Mathf.Clamp(pitch, -.6f, .6f), 0, 0);
        }
    }

    private void Animate(float dt)
    {
        float speed = new Vector2(Velocity.X, Velocity.Z).Length();
        // Footsteps: one per ~0.75 m walked (shorter strides when running), heard nearby.
        _stepDistance += speed * dt;
        if (speed > .3f && _stepDistance > (speed > 3 ? 1.1f : .75f))
        {
            _stepDistance = 0;
            if (_steps != null && Target.GlobalPosition.DistanceTo(GlobalPosition) < 45)
            {
                var (gx, gz) = World.ToSample(GlobalPosition.X, GlobalPosition.Z);
                var surface = World.IsWet((int)gx, (int)gz) ? DeadRecon.Audio.Synth.Surface.Water
                            : (Weather.Current?.Wetness ?? 0) > .45f ? DeadRecon.Audio.Synth.Surface.Mud : DeadRecon.Audio.Synth.Surface.Leaves;
                _steps.Stream = DeadRecon.Audio.Synth.Step(surface, (int)(Looks.Randi() % 6));
                _steps.PitchScale = .85f + .2f * Looks.Randf();
                _steps.VolumeDb = speed > 3 ? -2 : -7;
                _steps.Play();
            }
        }
        bool crouchWanted = (State == Mood.Alert && InCover && !_peeking) || (_sitting > 0 && State == Mood.Patrol);
        if (_body != null)
        {
            // The character animates itself; tell it what we're doing.
            bool ready2 = State is Mood.Alert or Mood.Search or Mood.Investigate;
            _crouch = Mathf.MoveToward(_crouch, crouchWanted ? 1 : 0, dt * 5);
            _body.Speed = speed;
            _body.Ready = Mathf.MoveToward(_body.Ready, ready2 ? 1 : 0, dt * 3);
            _body.AimPitch = State == Mood.Alert && _seen ? _head.Rotation.X : 0;
            _body.Crouch = _crouch;
            var cap = (CapsuleShape3D)GetChild<CollisionShape3D>(0).Shape;
            cap.Height = 1.8f - .58f * _crouch;
            GetChild<CollisionShape3D>(0).Position = new Vector3(0, cap.Height * .5f, 0);
            return;
        }
        _walkPhase += dt * speed * 3.2f;
        float swing = MathF.Sin(_walkPhase) * Math.Min(speed / 2, 1) * .55f;
        _legL.Rotation = new Vector3(swing, 0, 0);
        _legR.Rotation = new Vector3(-swing, 0, 0);
        // Gun lowered while relaxed, shouldered when alert or searching.
        bool ready = State is Mood.Alert or Mood.Search or Mood.Investigate;
        float aim = State == Mood.Alert && _seen ? _head.Rotation.X : 0;
        _gunPivot.Rotation = _gunPivot.Rotation.Lerp(new Vector3(ready ? aim : -.55f, ready ? 0 : .5f, 0), Math.Min(1, dt * 6));
        _rig.Position = new Vector3(0, MathF.Abs(MathF.Sin(_walkPhase)) * .03f * Math.Min(speed, 1), 0);

        // Crouch behind cover (body and hitbox), stand to peek.
        bool crouch = State == Mood.Alert && InCover && !_peeking;
        _crouch = Mathf.MoveToward(_crouch, crouch ? 1 : 0, dt * 5);
        _rig.Scale = new Vector3(1, 1 - .32f * _crouch, 1);
        var shape = (CapsuleShape3D)GetChild<CollisionShape3D>(0).Shape;
        shape.Height = 1.8f - .58f * _crouch;
        GetChild<CollisionShape3D>(0).Position = new Vector3(0, shape.Height * .5f, 0);
    }

    private float _crouch;

    private void Fall(float dt)
    {
        if (_body is { Ragdolled: true }) return; // physics has him
        _deathTime += dt;
        float t = Math.Min(1, _deathTime / .7f);
        _rig.Rotation = new Vector3(-MathF.PI / 2 * t * t, 0, 0);   // topple backwards
        _rig.Position = new Vector3(0, .15f * t, 0);
        // No MoveAndSlide: his collision is off (so bullets and the player pass), and gravity would sink him into the ground.
    }

    private void UpdateFlash(float dt)
    {
        if (_flash < 0) return;
        _flash += dt;
        bool on = _flash < .05f;
        _flashSprite.Visible = on;
        _flashLight.LightEnergy = on ? 3 : 0;
        if (!on) _flash = -1;
    }

    // ================================================================= barks and the ?/! icon

    /// <summary>Say a specific line (chat).</summary>
    public void Say(string line)
    {
        if (State == Mood.Dead) return;
        _bubble.Text = line;
        _bubbleLeft = 3.8f;
        if (_voice != null && Target.GlobalPosition.DistanceTo(GlobalPosition) < 80)
        {
            _voice.Stream = DeadRecon.Audio.Synth.Voice(line, _voicePitch, _voiceSeed);
            _voice.Play();
        }
        GD.Print($"[guard] \"{line}\"");
    }

    private void Bark(string kind)
    {
        var lines = DeadRecon.Data.Content.LineSets("guard_lines", Lines)[kind];
        int last = _lastLine.TryGetValue(kind, out var l) ? l : -1;
        int pick = _rng.RandiRange(0, lines.Length - 1);
        if (pick == last && lines.Length > 1) pick = (pick + 1) % lines.Length;
        _lastLine[kind] = pick;
        _bubble.Text = lines[pick];
        if (_voice != null && Target.GlobalPosition.DistanceTo(GlobalPosition) < 80)
        {
            _voice.Stream = DeadRecon.Audio.Synth.Voice(lines[pick], _voicePitch, _voiceSeed);
            _voice.Play();
        }
        _bubbleLeft = 3.8f;
        GD.Print($"[guard] \"{lines[pick]}\"");
    }

    private void UpdateLabels(float dt)
    {
        _bubbleLeft -= dt;
        float dist = Target.GlobalPosition.DistanceTo(GlobalPosition);
        _bubble.Visible = _bubbleLeft > 0 && dist < 70;
        _bubble.Modulate = new Color(1, 1, 1, Math.Clamp(_bubbleLeft, 0, 1));
        _icon.Visible = State != Mood.Dead && State != Mood.Patrol && dist < 120;
        _icon.Text = State == Mood.Alert ? "!" : "?";
        _icon.Modulate = State == Mood.Alert ? new Color(1, .25f, .2f) : new Color(1, .85f, .25f, .5f + Suspicion * .5f);
        // Fixed-size labels still get magnified by a zoomed camera (the scope); undo that so they read the same.
        float zoom = Target.Camera.Fov / 70f;
        _icon.PixelSize = .0007f * zoom;
        _bubble.PixelSize = .0007f * zoom;
    }

    private static float Flat(Vector3 v) => new Vector2(v.X, v.Z).Length();

    // ================================================================= placeholder body

    private void BuildBody()
    {
        var olive = Mat(.30f, .33f, .20f);
        var dark = Mat(.22f, .24f, .15f);
        var skin = Mat(.60f, .44f, .32f);
        var boots = Mat(.12f, .10f, .08f);

        _rig = new Node3D();
        AddChild(_rig);

        // A BadCharacters soldier if there are any (assets/soldiers/<style>); the block man otherwise.
        var characters = SoldierBody.Catalogue();
        if (characters.Count > 0)
        {
            _body = new SoldierBody
            {
                // Own random source: looks must not change his patrol route or behaviour for a given seed.
                CharacterScene = characters[(int)(Looks.Randi() % (uint)characters.Count)], GunScene = DeadRecon.Gear.WeaponDef.Get(WeaponId).Model, WeaponId = WeaponId,
            };
            _rig.AddChild(_body);
            _head = new Node3D { Position = new Vector3(0, 1.52f, 0) };   // aim/look helper, invisible
            _legL = new Node3D(); _legR = new Node3D(); _gunPivot = new Node3D();
            _rig.AddChild(_head); _rig.AddChild(_legL); _rig.AddChild(_legR); _rig.AddChild(_gunPivot);
            _muzzle = _body.Muzzle;
        }
        else BuildBlockMan(olive, dark, skin, boots);

        _flashLight = new OmniLight3D { LightColor = new Color(1, .75f, .4f), LightEnergy = 0, OmniRange = 7 };
        _flashSprite = new MeshInstance3D { Mesh = new QuadMesh { Size = new Vector2(.35f, .35f) }, MaterialOverride = Effects.FlashMaterial(), Visible = false, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        (_muzzle ?? _gunPivot).AddChild(_flashLight);
        (_muzzle ?? _gunPivot).AddChild(_flashSprite);
        FinishBody();
    }

    private SoldierBody? _body;
    private AudioStreamPlayer3D? _voice, _steps;
    private float _voicePitch = 110, _stepDistance;
    private int _voiceSeed;
    public SoldierBody? Body => _body;
    private RandomNumberGenerator Looks => _looks ??= new RandomNumberGenerator { Seed = (ulong)(Home.X * 92821 + Home.Z * 68917 + 7) };
    private RandomNumberGenerator? _looks;

    /// <summary>The original placeholder: boxes and capsules holding the rifle.</summary>
    private void BuildBlockMan(Material olive, Material dark, Material skin, Material boots)
    {
        Part(_rig, new BoxMesh { Size = new Vector3(.42f, .56f, .24f) }, olive, new Vector3(0, 1.22f, 0));          // torso
        Part(_rig, new BoxMesh { Size = new Vector3(.46f, .14f, .27f) }, dark, new Vector3(0, 1.32f, 0));           // webbing
        Part(_rig, new BoxMesh { Size = new Vector3(.38f, .16f, .22f) }, dark, new Vector3(0, .9f, 0));             // belt
        _legL = Limb(_rig, new Vector3(-.1f, .92f, 0), olive, boots);
        _legR = Limb(_rig, new Vector3(.1f, .92f, 0), olive, boots);

        _head = new Node3D { Position = new Vector3(0, 1.52f, 0) };
        _rig.AddChild(_head);
        Part(_head, new SphereMesh { Radius = .11f, Height = .24f }, skin, new Vector3(0, .1f, 0));
        Part(_head, new SphereMesh { Radius = .15f, Height = .15f, IsHemisphere = true }, dark, new Vector3(0, .15f, 0)); // helmet
        Part(_head, new CylinderMesh { TopRadius = .17f, BottomRadius = .17f, Height = .015f }, dark, new Vector3(0, .15f, 0)); // brim

        // Arms: forward, holding the rifle (no IK; the gun pivot moves, the arms just point at it).
        _gunPivot = new Node3D { Position = new Vector3(.05f, 1.36f, -.05f) };
        _rig.AddChild(_gunPivot);
        Part(_gunPivot, new CapsuleMesh { Radius = .055f, Height = .5f }, olive, new Vector3(.17f, -.06f, -.12f), new Vector3(1.25f, 0, .25f));
        Part(_gunPivot, new CapsuleMesh { Radius = .055f, Height = .6f }, olive, new Vector3(-.12f, -.07f, -.24f), new Vector3(1.35f, 0, -.55f));
        if (ResourceLoader.Exists(GunScene))
        {
            var gun = ResourceLoader.Load<PackedScene>(GunScene).Instantiate<Node3D>();
            gun.RotationDegrees = new Vector3(0, 180, 0); // BadGun +Z forward -> Godot -Z forward
            gun.Position = new Vector3(.08f, -.12f, -.2f);
            _gunPivot.AddChild(gun);
            _muzzle = gun.FindChild("SOCKET_Muzzle", true, false) as Node3D;
        }
    }

    /// <summary>Sound and floating labels (same for either body).</summary>
    private void FinishBody()
    {

        _shot = new AudioStreamPlayer3D
        {
            Stream = Effects.Gunshot(), UnitSize = 12, MaxDistance = 900, VolumeDb = 2, MaxPolyphony = 4, Bus = "Effects",
            AttenuationFilterCutoffHz = 2500, AttenuationFilterDb = -18,   // far shots sound dull and boomy
        };
        _voicePitch = 92 + 40 * Looks.Randf();
        _voiceSeed = (int)Looks.Randi();
        _voice = new AudioStreamPlayer3D { UnitSize = 6, MaxDistance = 80, VolumeDb = 1, Bus = "Voices", Position = new Vector3(0, 1.6f, 0) };
        AddChild(_voice);
        _steps = new AudioStreamPlayer3D { UnitSize = 3, MaxDistance = 45, Bus = "Effects", MaxPolyphony = 2 };
        AddChild(_steps);
        AddChild(_shot);

        _icon = new Label3D
        {
            Text = "?", FontSize = 96, OutlineSize = 18, PixelSize = .0007f, Position = new Vector3(0, 2.3f, 0),
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, NoDepthTest = true, Visible = false, FixedSize = true, // same size on screen at any distance
        };
        _bubble = new Label3D
        {
            FontSize = 40, OutlineSize = 10, PixelSize = .0007f, FixedSize = true, Position = new Vector3(0, 2.95f, 0), Width = 900, AutowrapMode = TextServer.AutowrapMode.WordSmart,
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, NoDepthTest = true, Visible = false, Modulate = Colors.White,
        };
        AddChild(_icon);
        AddChild(_bubble);
    }

    private static StandardMaterial3D Mat(float r, float g, float b) => new() { AlbedoColor = new Color(r, g, b), Roughness = .9f };

    private static MeshInstance3D Part(Node3D parent, Mesh mesh, Material mat, Vector3 pos, Vector3? rot = null)
    {
        var mi = new MeshInstance3D { Mesh = mesh, MaterialOverride = mat, Position = pos, Rotation = rot ?? Vector3.Zero };
        parent.AddChild(mi);
        return mi;
    }

    private static Node3D Limb(Node3D parent, Vector3 hip, Material cloth, Material boot)
    {
        var pivot = new Node3D { Position = hip };
        parent.AddChild(pivot);
        Part(pivot, new CapsuleMesh { Radius = .08f, Height = .82f }, cloth, new Vector3(0, -.41f, 0));
        Part(pivot, new BoxMesh { Size = new Vector3(.12f, .1f, .24f) }, boot, new Vector3(0, -.86f, -.05f));
        return pivot;
    }
}
