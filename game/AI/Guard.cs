using System;
using System.Collections.Generic;
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
    public enum Mood { Patrol, Suspicious, Investigate, Alert, Search, Dead }

    public Walker Target = null!;
    public TerrainStreamer Terrain = null!;
    public BakedWorld World = null!;
    public Vector3 Home;
    public string GunScene = "res://assets/weapons/BadGun_rifle_vietnam_97232.glb";

    public Mood State { get; private set; } = Mood.Patrol;
    /// <summary>0..1: how sure he is that someone is there. 0.3 suspicious, 0.7 investigating, 1 alerted.</summary>
    public float Suspicion { get; private set; }
    public float Health { get; private set; } = 100;
    public bool CanSeeTarget => _seen;
    /// <summary>0..1 how much of the player he can see right now (0 outside his view, behind terrain or trunks).</summary>
    public float Exposure { get; private set; }

    // ---- tuning
    private const float WalkSpeed = 1.5f, InvestigateSpeed = 2.0f, RunSpeed = 4.3f, Gravity = 9.81f, EyeHeight = 1.6f;
    private const float ViewRange = 55, AlertViewRange = 85, ViewCos = .5f, AlertViewCos = -.2f; // 120 deg cone, wider when alert
    private const float SearchTime = 18, ReactionTime = .7f;
    private const float MinExposure = .12f; // below this the player is hidden by plants

    private Node3D _rig = null!, _head = null!, _legL = null!, _legR = null!, _gunPivot = null!;
    private Node3D? _muzzle;
    private Label3D _icon = null!, _bubble = null!;
    private AudioStreamPlayer3D _shot = null!;
    private OmniLight3D _flashLight = null!;
    private MeshInstance3D _flashSprite = null!;
    private readonly List<Vector3> _patrol = new();
    private int _patrolIndex;
    private float _wait, _stateTime, _sinceSeen = 999, _searchLeft, _reaction, _cooldown, _burst, _flash = -1;
    private int _warningShots;
    private float _bubbleLeft, _mutter = 12, _walkPhase, _stuck, _deathTime, _lookAround;
    private bool _seen, _wasSeen;
    private Vector3 _lastKnown, _searchPoint;
    private readonly RandomNumberGenerator _rng = new();
    private readonly Dictionary<string, int> _lastLine = new();

    // ---- the grumpiness (the most important system in the game)
    private static readonly Dictionary<string, string[]> Lines = new()
    {
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
        ["tree"] = new[] { "Did a tree just... fall over? By itself?", "TIMBER! ...wait. Who said that?", "The jungle is falling apart. Like my career.",
                           "Trees don't just DO that." },
        ["squashed"] = new[] { "...a TREE? Seriously?", "...killed by... forestry..." },
    };

    public override void _Ready()
    {
        _rng.Seed = (ulong)(Home.X * 73856093 + Home.Z * 19349663);
        AddChild(new CollisionShape3D { Shape = new CapsuleShape3D { Radius = .32f, Height = 1.8f }, Position = new Vector3(0, .9f, 0) });
        FloorMaxAngle = Mathf.DegToRad(50);
        BuildBody();

        // A loop of four points around home, on the ground.
        for (int k = 0; k < 4; k++)
        {
            float a = k * MathF.Tau / 4 + _rng.RandfRange(-.4f, .4f);
            float r = _rng.RandfRange(12, 22);
            var p = Home + new Vector3(MathF.Cos(a) * r, 0, MathF.Sin(a) * r);
            p.Y = World.HeightAt(p.X, p.Z);
            _patrol.Add(p);
        }
        Hearing.Made += OnHeard;
        Target.Died += OnTargetDied;
    }

    public override void _ExitTree()
    {
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
        if (Target.Dead || Target.Flying) { Suspicion = Math.Max(0, Suspicion - dt * .1f); return; }

        var eye = GlobalPosition + Vector3.Up * EyeHeight;
        var to = Target.Chest - eye;
        float dist = to.Length();
        bool alert = State is Mood.Alert or Mood.Search;
        float range = (alert ? AlertViewRange : ViewRange) * (Weather.Current?.SightFactor ?? 1); // rain hides you too
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
            float rate = near * stance * motion * focus * 1.4f * (alert ? 4 : 1);
            Suspicion = Math.Min(1, Suspicion + rate * dt);
            _lastKnown = Target.GlobalPosition;
            _sinceSeen = 0;
        }
        else
        {
            _sinceSeen += dt;
            if (State is Mood.Patrol or Mood.Suspicious) Suspicion = Math.Max(0, Suspicion - dt * .06f);
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
                if (Suspicion >= .3f) { Enter(Mood.Suspicious, "suspicious"); return (here, 0, _lastKnown); }
                _mutter -= dt;
                if (_mutter <= 0) { _mutter = _rng.RandfRange(16, 30); Bark("mutter"); }
                var p = _patrol[_patrolIndex];
                if (Flat(here - p) < 1.2f || _stuck > 2.5f)
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
                if (Flat(here - _lastKnown) < 2.5f || _stateTime > 40 || _stuck > 3) { StartSearch(); return (here, 0, null); }
                return (_lastKnown, InvestigateSpeed, null);

            case Mood.Alert:
                if (_seen)
                {
                    if (!_wasSeen) _reaction = ReactionTime * .6f;          // spotted again: quicker this time
                    _reaction -= dt;
                    _cooldown -= dt;
                    if (_reaction <= 0 && _cooldown <= 0) Shoot();
                    return (here, 0, Target.GlobalPosition);
                }
                if (_sinceSeen > 2.5f)
                {
                    if (_sinceSeen < 2.6f) Bark("lost");
                    if (Flat(here - _lastKnown) < 2.5f || _stuck > 3) { StartSearch(); return (here, 0, null); }
                    return (_lastKnown, RunSpeed, null);
                }
                return (here, 0, _lastKnown);                                 // just lost sight: keep aiming there a moment

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
        Suspicion = 1;
        Enter(Mood.Alert, fresh ? "alert" : null);
        if (fresh) { _reaction = ReactionTime; _cooldown = 0; _burst = 0; _warningShots = 3; }
    }

    private void StartSearch()
    {
        Enter(Mood.Search, "lost");
        _searchLeft = SearchTime;
        PickSearchPoint();
    }

    private void PickSearchPoint()
    {
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
        bool headshot = at.Y - GlobalPosition.Y > 1.45f;
        Health -= headshot ? damage * 3 : damage;
        GD.Print($"[guard] hit for {(headshot ? damage * 3 : damage):0}{(headshot ? " (headshot)" : "")}, health {Math.Max(Health, 0):0}");
        if (Health <= 0) { Die(squashed: damage >= 60); return; } // only falling trees hit that hard
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

    private void Die(bool squashed = false)
    {
        Enter(Mood.Dead, squashed ? "squashed" : "dead");
        _deathTime = 0;
        _icon.Visible = false;
        GetChild<CollisionShape3D>(0).Disabled = true;
    }

    // ================================================================= shooting

    private void Shoot()
    {
        // Bursts of 2-4, then a pause.
        if (_burst <= 0) _burst = _rng.RandiRange(2, 4);
        _burst--;
        _cooldown = _burst > 0 ? .2f : _rng.RandfRange(1f, 1.8f);

        _flash = 0;
        _shot.PitchScale = .95f + _rng.Randf() * .1f;
        _shot.Play();

        var eye = GlobalPosition + Vector3.Up * EyeHeight;
        float dist = eye.DistanceTo(Target.Chest);
        float chance = Math.Clamp(.55f * (1 - dist / 110), .05f, .55f);
        if (_warningShots > 0) { _warningShots--; chance *= .3f; } // the first shots after spotting you mostly miss: a chance to dive for cover
        if (Target.Running) chance *= .5f;
        else if (Target.Velocity.LengthSquared() > .5f) chance *= .75f;
        if (Target.Crouching) chance *= .8f;
        // A trunk between us takes the bullet, hit or miss: hiding behind a tree works.
        if (Foliage.RaycastTrunk(eye, Target.Chest) is { } wood)
        {
            var shotDir = (Target.Chest - eye).Normalized();
            Effects.Splinters(this, wood.Point, wood.Normal, shotDir);
            Foliage.ReportHit(wood.Plant, wood.Point, shotDir, 30);
            return;
        }
        if (_rng.Randf() < chance)
        {
            Target.TakeHit(_rng.RandfRange(11, 18), Target.Chest, eye);
            return;
        }
        // Miss: kick up dirt somewhere near the player so you know you're being shot at.
        var miss = Target.GlobalPosition + new Vector3(_rng.RandfRange(-2.2f, 2.2f), 0, _rng.RandfRange(-2.2f, 2.2f));
        var dir = (miss - eye).Normalized();
        var q = PhysicsRayQueryParameters3D.Create(eye, eye + dir * (dist + 30));
        q.Exclude = new Godot.Collections.Array<Rid> { GetRid(), Target.GetRid() };
        var hit = GetWorld3D().DirectSpaceState.IntersectRay(q);
        var end = hit.Count > 0 ? (Vector3)hit["position"] : eye + dir * (dist + 30);
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
        _walkPhase += dt * speed * 3.2f;
        float swing = MathF.Sin(_walkPhase) * Math.Min(speed / 2, 1) * .55f;
        _legL.Rotation = new Vector3(swing, 0, 0);
        _legR.Rotation = new Vector3(-swing, 0, 0);
        // Gun lowered while relaxed, shouldered when alert or searching.
        bool ready = State is Mood.Alert or Mood.Search or Mood.Investigate;
        float aim = State == Mood.Alert && _seen ? _head.Rotation.X : 0;
        _gunPivot.Rotation = _gunPivot.Rotation.Lerp(new Vector3(ready ? aim : -.55f, ready ? 0 : .5f, 0), Math.Min(1, dt * 6));
        _rig.Position = new Vector3(0, MathF.Abs(MathF.Sin(_walkPhase)) * .03f * Math.Min(speed, 1), 0);
    }

    private void Fall(float dt)
    {
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

    private void Bark(string kind)
    {
        var lines = Lines[kind];
        int last = _lastLine.TryGetValue(kind, out var l) ? l : -1;
        int pick = _rng.RandiRange(0, lines.Length - 1);
        if (pick == last && lines.Length > 1) pick = (pick + 1) % lines.Length;
        _lastLine[kind] = pick;
        _bubble.Text = lines[pick];
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
        _flashLight = new OmniLight3D { LightColor = new Color(1, .75f, .4f), LightEnergy = 0, OmniRange = 7 };
        _flashSprite = new MeshInstance3D { Mesh = new QuadMesh { Size = new Vector2(.35f, .35f) }, MaterialOverride = Effects.FlashMaterial(), Visible = false, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        (_muzzle ?? _gunPivot).AddChild(_flashLight);
        (_muzzle ?? _gunPivot).AddChild(_flashSprite);

        _shot = new AudioStreamPlayer3D { Stream = Effects.Gunshot(), UnitSize = 12, MaxDistance = 700, VolumeDb = 2, MaxPolyphony = 4 };
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
