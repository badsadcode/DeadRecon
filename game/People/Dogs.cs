using System;
using System.Collections.Generic;
using System.Linq;
using DeadRecon.AI;
using DeadRecon.Combat;
using DeadRecon.Player;
using DeadRecon.World;
using Godot;

namespace DeadRecon.People;

/// <summary>
/// Your scent (docs/MISSIONS_PLAN.md, dogs): a mark every 1.5 m you walk, fading over about four minutes; rain washes
/// it away up to five times faster, and walking through water leaves no trail at all (a tracker loses you there).
/// </summary>
public static class Scent
{
    public sealed class Mark { public Vector3 At; public float Fresh = 1; public bool Water; }
    public static readonly List<Mark> Marks = new();
    private const int Max = 2500;

    public static void Clear() => Marks.Clear();

    public static void Update(Walker p, BakedWorld world, float dt)
    {
        float rain = Weather.Current?.Intensity ?? 0;
        float fade = dt / 240f * (1 + 4 * rain);
        foreach (var m in Marks) m.Fresh -= fade;
        if (Marks.Count > 0 && Marks[0].Fresh <= 0) Marks.RemoveAll(m => m.Fresh <= 0);
        if (p.Dead && !p.Down || p.Flying) return;
        var at = p.GlobalPosition;
        if (Marks.Count > 0 && Marks[^1].At.DistanceTo(at) < 1.5f) return;
        var (gx, gz) = world.ToSample(at.X, at.Z);
        Marks.Add(new Mark { At = at, Water = world.IsWet((int)gx, (int)gz) });
        if (Marks.Count > Max) Marks.RemoveAt(0);
    }

    /// <summary>The freshest mark of yours within <paramref name="radius"/> of a point (index), or -1.</summary>
    public static int Near(Vector3 at, float radius, float minFresh = .12f)
    {
        int best = -1;
        for (int i = Marks.Count - 1; i >= 0; i--)
            if (Marks[i].Fresh >= minFresh && !Marks[i].Water && Marks[i].At.DistanceTo(at) < radius) { best = i; break; }
        return best;
    }
}

/// <summary>Dogs with a job (roles "army" and "pet"): the brains on top of <see cref="Animal"/>.</summary>
public partial class Animal
{
    /// <summary>An army dog's camp: he roams round it, and goes back to it.</summary>
    public Vector3 Post;
    public string Name2 = "";
    private enum Dog { Idle, Alarm, Tracking, Lost }
    private Dog _dog = Dog.Idle;
    private int _trail = -1;
    private float _barkIn, _lostFor, _growlIn = 5, _hurtFor;
    private Vector3 _lastSmelt;

    /// <summary>How far he smells you: by how you move, more downwind, less in the rain.</summary>
    private float SmellRange()
    {
        float r = Player.Running ? 26 : !Player.Crouching ? 18 : Player.Velocity.LengthSquared() > .5f ? 9 : 5;
        var wind = new Vector3(.8f, 0, .6f);               // same wind as the trees
        var fromYou = (GlobalPosition - Player.GlobalPosition) with { Y = 0 };
        if (fromYou.LengthSquared() > .01f && fromYou.Normalized().Dot(wind) > .3f) r *= 1.6f;   // he's downwind of you
        return r * (1 - .5f * (Weather.Current?.Intensity ?? 0));
    }

    private bool ArmyDog(float dt, float toPlayer)
    {
        _barkIn -= dt;
        bool smells = !Player.Dead && !Player.Flying && toPlayer < SmellRange();
        float fromPost = new Vector2(GlobalPosition.X - Post.X, GlobalPosition.Z - Post.Z).Length();
        switch (_dog)
        {
            case Dog.Idle:
            {
                if (smells) { Alarm(); break; }
                // The camp's searching for someone: put his nose to the ground, from the freshest scent near him.
                if (Guard.All.Exists(g => g.State == Guard.Mood.Search && g.GlobalPosition.DistanceTo(Post) < 80))
                {
                    int i = Scent.Near(GlobalPosition, 45);
                    if (i >= 0) StartTracking(i);
                }
                return false;   // otherwise: wander his patch like any animal
            }
            case Dog.Alarm:
            {
                if (!smells) { int i = Scent.Near(_lastSmelt, 15); if (i >= 0) StartTracking(i); else { _dog = Dog.Lost; _lostFor = 8; } break; }
                // A leash: he chases you so far from the camp, then stands there barking (the guards come to that).
                if (fromPost > 70 && (Player.GlobalPosition - Post).Length() > fromPost) { _goal = GlobalPosition; _speed = 0; Face(Player.GlobalPosition, dt * 8); Bark(); break; }
                _lastSmelt = Player.GlobalPosition;
                Bark();
                // Run at you; hold off a couple of metres and bark; snap if you're right there.
                _goal = toPlayer > 2.5f ? Player.GlobalPosition : GlobalPosition;
                _speed = toPlayer > 2.5f ? Def.Run : 0;
                if (_speed == 0) { Face(Player.GlobalPosition, dt * 8); Play("Attack"); }
                if (toPlayer < 1.7f && _attackCool <= 0) { _attackCool = 1.4f; Play("Attack"); Player.TakeHit(10, Player.Chest, GlobalPosition); }
                break;
            }
            case Dog.Tracking:
            {
                if (smells) { Alarm(); break; }
                if (_trail < 0 || _trail >= Scent.Marks.Count || fromPost > 300) { _dog = Dog.Lost; _lostFor = 8; break; }
                var m = Scent.Marks[_trail];
                if (m.Water || m.Fresh < .08f)
                {
                    // The trail's gone (into the river, or washed out): he casts about, then gives up.
                    _dog = Dog.Lost; _lostFor = 10;
                    GD.Print($"[dog] {Name2} lost the trail ({(m.Water ? "water" : "faded")})");
                    break;
                }
                _goal = m.At; _speed = Def.Run * .55f;
                if (GlobalPosition.DistanceTo(m.At) < 1.6f) _trail++;
                if (_barkIn <= 0 && _rng.Randf() < .3f) Bark(); else if (_barkIn <= 0) _barkIn = 2;
                break;
            }
            case Dog.Lost:
            {
                _lostFor -= dt;
                if (smells) { Alarm(); break; }
                if (_think <= 0) { _think = 1.5f; float a = _rng.Randf() * MathF.Tau; _goal = GlobalPosition + new Vector3(MathF.Cos(a), 0, MathF.Sin(a)) * 6; _speed = Def.Walk * 1.5f; }
                _think -= dt;
                if (_lostFor <= 0) { _dog = Dog.Idle; _goal = Post; _speed = Def.Walk * 2; Home = Post; }
                break;
            }
        }
        Move(dt);
        return true;
    }

    private void Alarm()
    {
        if (_dog != Dog.Alarm) GD.Print($"[dog] {Name2} smells you ({Player.GlobalPosition.DistanceTo(GlobalPosition):0} m)");
        _dog = Dog.Alarm;
        _lastSmelt = Player.GlobalPosition;
        _barkIn = 0;
    }

    private void StartTracking(int mark)
    {
        _dog = Dog.Tracking; _trail = mark;
        GD.Print($"[dog] {Name2} picks up your trail ({Scent.Marks.Count - mark} marks behind you)");
    }

    /// <summary>A bark the guards hear (and come to).</summary>
    private void Bark()
    {
        if (_barkIn > 0) return;
        _barkIn = _rng.RandfRange(.8f, 1.6f);
        _voice.Stream = DeadRecon.Audio.Synth.Animal("bark", _rng.RandiRange(0, 3));
        _voice.PitchScale = _rng.RandfRange(.75f, .85f);   // a big dog
        _voice.Play();
        Hearing.Make(GlobalPosition, 60, this);
    }

    /// <summary>Private Sausage: follows you, keeps quiet, growls at trouble you haven't seen yet.</summary>
    private bool PetDog(float dt, float toPlayer)
    {
        _hurtFor -= dt;
        _growlIn -= dt;
        if (Player.Dead && !Player.Down) { _speed = 0; Move(dt); return true; }
        if (toPlayer > 90) { var at = Player.GlobalPosition + Player.GlobalTransform.Basis * new Vector3(1.5f, 0, 2.5f); GlobalPosition = at with { Y = World.HeightAt(at.X, at.Z) }; }
        // At your heel, a little behind and to the side.
        var heel = Player.GlobalPosition + Player.GlobalTransform.Basis * new Vector3(1.2f, 0, 2.2f);
        float d = GlobalPosition.DistanceTo(heel);
        if (_hurtFor > 0) { }   // running off (Spook set the goal)
        else if (d > 1.2f) { _goal = heel; _speed = d > 8 ? Def.Run : d > 3 ? Def.Walk * 2.2f : Def.Walk; _restLeft = 0; }
        else { _speed = 0; _rest = Player.Crouching ? "Sit" : "Idle"; _restLeft = 1; }
        // Trouble: someone armed and up within 35 m. A low growl, his nose pointing at it.
        if (_growlIn <= 0)
        {
            var threat = Guard.All.Where(g => g.State is not (Guard.Mood.Dead or Guard.Mood.Captive) && !g.Out && g.GlobalPosition.DistanceTo(GlobalPosition) < 35)
                                  .OrderBy(g => g.GlobalPosition.DistanceTo(GlobalPosition)).FirstOrDefault();
            if (threat != null)
            {
                _growlIn = 14;
                Face(threat.GlobalPosition, 1);
                _voice.Stream = DeadRecon.Audio.Synth.Animal("growl", _rng.RandiRange(0, 2));
                _voice.PitchScale = 1.6f; _voice.VolumeDb = -10;
                _voice.Play();
                DeadRecon.UI.Hud.Toast($"{Name2} growls quietly, nose toward the {Compass(threat.GlobalPosition - GlobalPosition)}.");
                GD.Print($"[dog] {Name2} growls at a guard {threat.GlobalPosition.DistanceTo(GlobalPosition):0} m away");
            }
            else _growlIn = 2;
        }
        Move(dt);
        return true;
    }

    private static string Compass(Vector3 d)
    {
        float deg = (Mathf.RadToDeg(MathF.Atan2(d.X, -d.Z)) + 360) % 360;
        string[] names = { "north", "north-east", "east", "south-east", "south", "south-west", "west", "north-west" };
        return names[(int)MathF.Round(deg / 45) % 8];
    }

    /// <summary>Tests: what the army dog is doing.</summary>
    public string DogState => _dog.ToString();
}
