using System;
using System.Collections.Generic;
using System.Linq;
using DeadRecon.Player;
using DeadRecon.UI;
using DeadRecon.World;
using Godot;

namespace DeadRecon.AI;

/// <summary>
/// The enemy outside the camps (docs/LIVING_JUNGLE.md, step 5):
/// <list type="bullet">
/// <item><b>Checkpoint garrisons</b>: a sentry at the barrier and a couple of men walking about. They appear when you
/// come within <see cref="SpawnDistance"/> and remember how many are left when you go; kill them all and the
/// checkpoint is cleared.</item>
/// <item><b>Road patrols</b>: groups of two or three walking a road end to end and back. Off-screen a patrol is a
/// point moving along its road; near you it becomes real guards (the same AI as the camps) and back again when you
/// leave. Wipe one out and a fresh one sets off somewhere else a few minutes later.</item>
/// </list>
/// </summary>
public partial class Patrols : Node
{
    public static Patrols? Current { get; private set; }

    public Settlements Towns = null!;
    public Walker Player = null!;
    public BakedWorld World = null!;
    /// <summary>Make a guard (home, sentry, set him up before he's added) — Main knows the wiring.</summary>
    public Func<Vector3, bool, Action<Guard>?, Guard> Spawn = null!;
    /// <summary>Take a guard out of the world.</summary>
    public Action<Guard> Despawn = null!;
    public int PatrolCount = 4;

    private const float SpawnDistance = 380, FreeDistance = 560, Walk = 1.3f;

    private sealed class Garrison
    {
        public Settlements.Site Site = null!;
        public int Alive = -1;                   // -1: not met yet
        public readonly List<Guard> Guards = new();
        public bool Cleared => Alive == 0;
    }

    private sealed class Patrol
    {
        public List<Vector3> Route = new();
        public float At;                          // index along the route, fractional
        public bool Back;
        public float Pause;
        public int Size;
        public readonly List<Guard> Guards = new();
        public bool Real => Guards.Count > 0;
    }

    private readonly Dictionary<int, Garrison> _garrisons = new();
    private readonly List<Patrol> _patrols = new();
    private readonly RandomNumberGenerator _rng = new() { Seed = 4242 };
    private double _since;
    private readonly List<float> _reinforcements = new();   // seconds until each replacement patrol sets off

    public int RealPatrols => _patrols.Count(p => p.Real);
    public IEnumerable<Guard> PatrolGuards => _patrols.SelectMany(p => p.Guards);
    public bool IsCleared(int site) => _garrisons.TryGetValue(site, out var g) && g.Cleared;

    public override void _Ready()
    {
        Current = this;
        foreach (var s in Towns.Sites.Where(s => s.Kind == "outpost")) _garrisons[s.Index] = new Garrison { Site = s };
        for (int k = 0; k < PatrolCount; k++) NewPatrol(awayFrom: Player.GlobalPosition);
    }

    public override void _ExitTree() { if (Current == this) Current = null; }

    /// <summary>A road, its points thinned to one every ~16 m, and a patrol somewhere along it.</summary>
    private void NewPatrol(Vector3 awayFrom)
    {
        var roads = Towns.Roads.Where(r => r.Points.Length > 25).ToList();
        if (roads.Count == 0) return;
        for (int tries = 0; tries < 10; tries++)
        {
            var road = roads[_rng.RandiRange(0, roads.Count - 1)];
            var route = new List<Vector3>();
            for (int k = 0; k < road.Points.Length; k += 4) route.Add(road.Points[k]);
            if (route[^1] != road.Points[^1]) route.Add(road.Points[^1]);
            var p = new Patrol { Route = route, At = _rng.RandfRange(0, route.Count - 1), Back = _rng.Randf() < .5f, Size = _rng.RandiRange(2, 3) };
            // Not right on top of you when it starts.
            if (Pos(p).DistanceTo(awayFrom) < SpawnDistance + 100 && tries < 9) continue;
            _patrols.Add(p);
            return;
        }
    }

    private static Vector3 Pos(Patrol p)
    {
        int i = Math.Clamp((int)p.At, 0, p.Route.Count - 1), j = Math.Min(i + 1, p.Route.Count - 1);
        return p.Route[i].Lerp(p.Route[j], p.At - i);
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        // Off-screen patrols walk on.
        foreach (var p in _patrols.Where(p => !p.Real))
        {
            if (p.Pause > 0) { p.Pause -= dt; continue; }
            int i = Math.Clamp((int)p.At, 0, p.Route.Count - 2);
            float seg = Math.Max(1, p.Route[i].DistanceTo(p.Route[i + 1]));
            p.At += (p.Back ? -1 : 1) * Walk * dt / seg;
            if (p.At <= 0 || p.At >= p.Route.Count - 1) { p.At = Math.Clamp(p.At, 0, p.Route.Count - 1); p.Back = !p.Back; p.Pause = _rng.RandfRange(8, 14); }
        }

        _since += delta;
        if (_since < .5) return;
        _since = 0;
        var me = Player.GlobalPosition;

        foreach (var g in _garrisons.Values)
        {
            float d = Flat(g.Site.Centre - me);
            if (g.Guards.Count == 0 && !g.Cleared && d < SpawnDistance) Man(g);
            else if (g.Guards.Count > 0)
            {
                int alive = g.Guards.Count(x => IsInstanceValid(x) && x.State != Guard.Mood.Dead);
                if (alive == 0 && !g.Cleared)
                {
                    g.Alive = 0;
                    Hud.Toast($"{g.Site.Name} is clear");
                }
                if (d > FreeDistance && g.Guards.All(x => !IsInstanceValid(x) || x.State is Guard.Mood.Patrol or Guard.Mood.Dead))
                {
                    g.Alive = alive;
                    foreach (var x in g.Guards) if (IsInstanceValid(x)) Despawn(x);
                    g.Guards.Clear();
                }
            }
        }

        foreach (var p in _patrols.ToList())
        {
            if (!p.Real)
            {
                if (Flat(Pos(p) - me) < SpawnDistance) Muster(p);
                continue;
            }
            var alive = p.Guards.Where(x => IsInstanceValid(x) && x.State != Guard.Mood.Dead).ToList();
            if (alive.Count == 0)
            {
                // Wiped out. Bodies stay until you leave; the enemy sends someone else, later.
                if (p.Guards.All(x => !IsInstanceValid(x) || Flat(x.GlobalPosition - me) > FreeDistance))
                {
                    foreach (var x in p.Guards) if (IsInstanceValid(x)) Despawn(x);
                    _patrols.Remove(p);
                    _reinforcements.Add(240);
                }
                continue;
            }
            var lead = alive[0];
            if (Flat(lead.GlobalPosition - me) > FreeDistance && alive.All(x => x.State == Guard.Mood.Patrol))
            {
                // Back to being a dot on a road.
                p.At = lead.RouteIndex;
                p.Back = lead.RouteGoingBack;
                p.Size = alive.Count;
                foreach (var x in p.Guards) if (IsInstanceValid(x)) Despawn(x);
                p.Guards.Clear();
            }
        }
        // A lost patrol is replaced about four minutes later, somewhere you aren't.
        for (int k = _reinforcements.Count - 1; k >= 0; k--)
        {
            _reinforcements[k] -= .5f;
            if (_reinforcements[k] <= 0) { _reinforcements.RemoveAt(k); NewPatrol(me); }
        }
    }

    /// <summary>Put men at a checkpoint: a sentry at the barrier, the rest walking about.</summary>
    private void Man(Garrison g)
    {
        var c = g.Site.Centre with { Y = World.HeightAt(g.Site.Centre.X, g.Site.Centre.Z) };
        int n = g.Alive < 0 ? (Towns.Sites.Any(s => s.Kind == "town" && s.Centre.DistanceTo(c) < 1500) ? 4 : 3) : g.Alive;
        g.Alive = n;
        for (int k = 0; k < n; k++)
        {
            var at = c + new Vector3(MathF.Cos(k * 2.1f) * (k == 0 ? 2.5f : 6), 0, MathF.Sin(k * 2.1f) * (k == 0 ? 2.5f : 6));
            g.Guards.Add(Spawn(at, k == 0, x => { x.PatrolMin = 6; x.PatrolMax = 14; }));
        }
    }

    /// <summary>A patrol comes into range: real men, in file along the road, carrying on the way they were going.</summary>
    private void Muster(Patrol p)
    {
        int idx = Math.Clamp((int)MathF.Round(p.At), 0, p.Route.Count - 1);
        var ahead = p.Route[Math.Clamp(idx + (p.Back ? -1 : 1), 0, p.Route.Count - 1)];
        var dir = Flat3(ahead - Pos(p));
        if (dir.LengthSquared() < .01f) dir = Vector3.Forward;
        dir = dir.Normalized();
        for (int k = 0; k < p.Size; k++)
        {
            var at = Pos(p) - dir * (k * 4.5f);
            int start = Math.Clamp(idx + (p.Back ? -1 : 1), 0, p.Route.Count - 1);
            p.Guards.Add(Spawn(at, false, x => { x.Route = p.Route; x.RouteStart = start; x.RouteBackwards = p.Back; }));
        }
    }

    /// <summary>A patrol whose road passes within <paramref name="within"/> m of a place (a new one if none does).</summary>
    public object? PatrolNear(Vector3 at, float within)
    {
        bool Passes(List<Vector3> route) => route.Any(q => Flat(q - at) < within);
        var found = _patrols.FirstOrDefault(p => Passes(p.Route) && !p.Real);
        if (found != null) return found;
        var road = Towns.Roads.Where(r => r.Points.Length > 25 && r.Points.Any(q => Flat(q - at) < within)).OrderBy(_ => _rng.Randi()).FirstOrDefault();
        if (road == null) return null;
        var route = new List<Vector3>();
        for (int k = 0; k < road.Points.Length; k += 4) route.Add(road.Points[k]);
        if (route[^1] != road.Points[^1]) route.Add(road.Points[^1]);
        // Start it at the far end of its road from the village, walking toward it.
        bool startEnd = Flat(route[0] - at) < Flat(route[^1] - at);
        var p2 = new Patrol { Route = route, At = startEnd ? route.Count - 1 : 0, Back = startEnd, Size = _rng.RandiRange(2, 3) };
        _patrols.Add(p2);
        return p2;
    }

    /// <summary>Where a patrol is now (its leader, or its dot on the road).</summary>
    public Vector3? Where(object patrol)
    {
        if (patrol is not Patrol p || !_patrols.Contains(p)) return null;
        var lead = p.Guards.FirstOrDefault(x => IsInstanceValid(x) && x.State != Guard.Mood.Dead);
        return lead?.GlobalPosition ?? Pos(p);
    }

    /// <summary>Every man of that patrol is down (or it's been cleaned up already).</summary>
    public bool IsGone(object patrol) =>
        patrol is not Patrol p || !_patrols.Contains(p) || (p.Real && p.Guards.All(x => !IsInstanceValid(x) || x.State == Guard.Mood.Dead));

    /// <summary>Tests: the men of a patrol, mustered now wherever it is.</summary>
    public IReadOnlyList<Guard> TestMuster(object patrol)
    {
        if (patrol is not Patrol p) return Array.Empty<Guard>();
        if (!p.Real) Muster(p);
        return p.Guards;
    }

    /// <summary>Where the patrols are now (for radio rumours).</summary>
    public IEnumerable<Vector3> PatrolPositions => _patrols.Select(p => Where(p) ?? Pos(p));
    public IEnumerable<(Settlements.Site Site, bool Cleared)> Checkpoints => _garrisons.Values.Select(g => (g.Site, g.Cleared));

    /// <summary>The checkpoint's garrison, manned now if it hasn't been met yet (the headman's job).</summary>
    public IReadOnlyList<Guard> GarrisonOf(int site)
    {
        if (!_garrisons.TryGetValue(site, out var g)) return Array.Empty<Guard>();
        if (g.Guards.Count == 0 && !g.Cleared) Man(g);
        return g.Guards;
    }

    /// <summary>Tests: bring the nearest patrol to a point on its road near you.</summary>
    public Vector3? TestBringPatrolNear(Vector3 me)
    {
        foreach (var p in _patrols)
        {
            if (p.Real) continue;
            int best = 0;
            for (int k = 0; k < p.Route.Count; k++) if (Flat(p.Route[k] - me) < Flat(p.Route[best] - me)) best = k;
            p.At = best;
            return p.Route[best];
        }
        return null;
    }

    private static float Flat(Vector3 v) => new Vector2(v.X, v.Z).Length();
    private static Vector3 Flat3(Vector3 v) => new(v.X, 0, v.Z);
}
