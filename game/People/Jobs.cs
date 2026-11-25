using System;
using System.Collections.Generic;
using System.Linq;
using DeadRecon.AI;
using DeadRecon.Gear;
using DeadRecon.Player;
using DeadRecon.UI;
using DeadRecon.World;
using Godot;

namespace DeadRecon.People;

/// <summary>
/// Work the headmen give you. Each village has its own list (first the checkpoint that bothers it, then the rest in
/// an order of its own) and offers one job at a time. The job you took last is "tracked": on the HUD line, the
/// compass and the map.
/// <list type="bullet">
/// <item><b>Checkpoint</b>: clear the garrison of the nearest checkpoint.</item>
/// <item><b>Patrol</b>: stop the patrol that walks through the village (a real road patrol, followed on the map).</item>
/// <item><b>Missing</b>: a farmer went into the jungle and didn't come back. Search the circle, find him (he calls
/// out when you're close), and walk him home.</item>
/// <item><b>Delivery</b>: carry a sack of rice to a neighbouring settlement (it takes room in your backpack).</item>
/// </list>
/// </summary>
public partial class JobBoard : Node
{
    public static JobBoard? Current { get; private set; }

    public Settlements Towns = null!;
    public Walker Player = null!;
    public BakedWorld World = null!;
    public VillageLife Life = null!;
    /// <summary>Garrison guards when there's no patrol system (some tests).</summary>
    public Func<Vector3, bool, Guard>? SpawnGuard;

    public enum Kind { Checkpoint, Patrol, Missing, Delivery }

    public abstract class Job
    {
        public Settlements.Site Village = null!;
        public bool Done, Failed;
        public abstract Kind Kind { get; }
        /// <summary>One line for the HUD and the map.</summary>
        public abstract string Title { get; }
        /// <summary>Where to look (null: nowhere particular), and how big an area (0: a point).</summary>
        public virtual Vector3? Marker => null;
        public virtual float MarkerRadius => 0;
        public abstract string Reminder { get; }
        /// <summary>The place the job is about (the checkpoint, the destination), if any.</summary>
        public virtual Settlements.Site? Place => null;
        public bool Active => !Done && !Failed;
    }

    private sealed class CheckpointJob : Job
    {
        public Settlements.Site Checkpoint = null!;
        public readonly List<Guard> Guards = new();
        public override Kind Kind => Kind.Checkpoint;
        public override string Title => $"Clear {Checkpoint.Name} for {Village.Name}";
        public override Vector3? Marker => Checkpoint.Centre;
        public override Settlements.Site? Place => Checkpoint;
        public override string Reminder => $"The soldiers at {Checkpoint.Name} are still there. Still eating our rice.";
    }

    private sealed class PatrolJob : Job
    {
        public object Patrol = null!;
        public Vector3 Last;
        public override Kind Kind => Kind.Patrol;
        public override string Title => $"Stop the patrol bothering {Village.Name}";
        public override Vector3? Marker => Last;
        public override float MarkerRadius => 60;
        public override string Reminder => "That patrol came through again this morning. Took a pig this time. A PIG.";
    }

    private sealed class MissingJob : Job
    {
        public Villager Lost = null!;
        public string Who = "";
        public Vector3 SearchCentre;
        public override Kind Kind => Kind.Missing;
        public override string Title => Lost.Following ? $"Bring {Who} home to {Village.Name}" : $"Find {Who}, missing from {Village.Name}";
        public override Vector3? Marker => Lost.Following ? Village.Centre : SearchCentre;
        public override float MarkerRadius => Lost.Following ? 0 : 120;
        public override string Reminder => Lost.Following ? $"You found {Who}! Bring him the rest of the way, please." : $"Still no sign of {Who}. He went out past the fields, that way. Please.";
    }

    private sealed class DeliveryJob : Job
    {
        public Settlements.Site To = null!;
        public override Kind Kind => Kind.Delivery;
        public override string Title => $"Take rice from {Village.Name} to {To.Name}";
        public override Vector3? Marker => To.Centre;
        public override Settlements.Site? Place => To;
        public override string Reminder => $"{To.Name} is waiting for that rice. Don't eat it on the way.";
    }

    private readonly List<Job> _jobs = new();
    private readonly Dictionary<int, int> _nextOf = new();     // per village: how many jobs it has given
    public Job? Tracked { get; private set; }
    public IEnumerable<Job> Active => _jobs.Where(j => j.Active);

    public override void _Ready() => Current = this;
    public override void _ExitTree() { if (Current == this) Current = null; }

    public bool CheckpointDone(int village) => _jobs.Any(j => j.Done && j is CheckpointJob c && c.Village.Index == village);

    /// <summary>The headman's answer to "Anything I can do for you?".</summary>
    public string Talk(Settlements.Site village, Support.Level level)
    {
        if (level == Support.Level.Hostile) return "Do something for US? After what you did? Ha.";
        var current = _jobs.FirstOrDefault(j => j.Village == village && j.Active);
        if (current != null) { Tracked = current; return current.Reminder; }
        int n = _nextOf.GetValueOrDefault(village.Index);
        // First the checkpoint; then the others in this village's own order.
        var order = new List<Kind> { Kind.Checkpoint };
        var rest = new List<Kind> { Kind.Patrol, Kind.Missing, Kind.Delivery };
        int shift = village.Index % rest.Count;
        for (int k = 0; k < rest.Count; k++) order.Add(rest[(k + shift) % rest.Count]);
        for (int tries = 0; tries < order.Count; tries++)
        {
            var kind = order[(n + tries) % order.Count];
            if (n + tries >= order.Count && kind == Kind.Checkpoint) continue;   // the checkpoint only once
            var (job, text) = Make(kind, village);
            if (job == null) continue;
            _nextOf[village.Index] = n + tries + 1;
            _jobs.Add(job);
            Tracked = job;
            GD.Print($"[jobs] {village.Name}: {job.Title}");
            return text;
        }
        return "Nothing today. The rice grows, the chickens are fine. Go and have a nap.";
    }

    private (Job?, string) Make(Kind kind, Settlements.Site village)
    {
        switch (kind)
        {
            case Kind.Checkpoint:
            {
                var cp = Towns.Sites.Where(s => s.Kind == "outpost" && s.Centre.DistanceTo(village.Centre) < 2500).OrderBy(s => s.Centre.DistanceTo(village.Centre)).FirstOrDefault();
                if (cp == null) return (null, "");
                var job = new CheckpointJob { Village = village, Checkpoint = cp };
                if (Patrols.Current == null && SpawnGuard != null)
                {
                    // No patrol system (some tests): the garrison turns up now.
                    var c = cp.Centre with { Y = World.HeightAt(cp.Centre.X, cp.Centre.Z) };
                    job.Guards.Add(SpawnGuard(c + new Vector3(2.5f, 0, 1.5f), true));
                    job.Guards.Add(SpawnGuard(c + new Vector3(-3, 0, -4), false));
                }
                if (Patrols.Current?.IsCleared(cp.Index) == true)
                    return (job, $"{cp.Name}? Somebody already emptied it. ...It was you, wasn't it. Thank you!");
                return (job, $"The soldiers at {cp.Name} take a sack of rice from every cart. Every cart! It's {Compass(cp.Centre - village.Centre)} of here, " +
                             $"about {Round(cp.Centre.DistanceTo(village.Centre))} metres. Make them stop, and we won't forget it.");
            }
            case Kind.Patrol:
            {
                if (Patrols.Current?.PatrolNear(village.Centre, 350) is not { } patrol) return (null, "");
                var job = new PatrolJob { Village = village, Patrol = patrol, Last = Patrols.Current.Where(patrol) ?? village.Centre };
                return (job, "Soldiers walk through here on the road, two or three, every day. They take chickens. They take EGGS. " +
                             "They laugh about it. Make them stop laughing. I've marked where they walk on your map.");
            }
            case Kind.Missing:
            {
                var rng = new RandomNumberGenerator { Seed = (ulong)(village.Index * 977 + _jobs.Count * 13 + 5) };
                Vector3 spot = village.Centre;
                for (int tries = 0; tries < 30; tries++)
                {
                    float a = rng.Randf() * MathF.Tau, d = rng.RandfRange(220, 420);
                    var p = village.Centre + new Vector3(MathF.Cos(a) * d, 0, MathF.Sin(a) * d);
                    var (gx, gz) = World.ToSample(p.X, p.Z);
                    if (Math.Abs(p.X) > World.Half - 50 || Math.Abs(p.Z) > World.Half - 50 || World.NearWater((int)gx, (int)gz, 3)) continue;
                    if (Towns.SiteAt(p.X, p.Z, 1.4f) != null) continue;
                    // Not in the middle of the operation's camp.
                    if (Life.Mission is { } op && (op.Camp.DistanceTo(p) < 220 || op.Extraction.DistanceTo(p) < 120)) continue;
                    spot = p with { Y = World.HeightAt(p.X, p.Z) };
                    break;
                }
                string[] names = DeadRecon.Data.Content.Strings("lost_villager_names", new[] { "Old Bao", "Uncle Tam", "Young Kiet", "Grandpa Lum", "Cousin Vinh", "Brother Hoa" });
                string who = names[rng.RandiRange(0, names.Length - 1)];
                var lost = Life.SpawnLost(village, spot, who);
                if (lost == null) return (null, "");
                // The circle isn't centred on him: you have to look.
                var centre = spot + new Vector3(rng.RandfRange(-70, 70), 0, rng.RandfRange(-70, 70));
                var job = new MissingJob { Village = village, Lost = lost, Who = who, SearchCentre = centre };
                return (job, $"{who} went out to look for mushrooms yesterday and didn't come back. He's done it before... but never overnight. " +
                             $"Somewhere {Compass(spot - village.Centre)} of here, past the fields. He'll shout if he hears you. Bring him home?");
            }
            default:
            {
                var to = Towns.Sites.Where(s => s.IsSettlement && s != village && s.Centre.DistanceTo(village.Centre) is > 250 and < 2600)
                                    .OrderBy(s => s.Centre.DistanceTo(village.Centre)).FirstOrDefault();
                if (to == null || Kit.Current == null) return (null, "");
                if (!Kit.Current.Add(ItemDef.RiceSacks, 1, quiet: true)) return (null, "");
                var job = new DeliveryJob { Village = village, To = to };
                return (job, $"{to.Name} lost half their harvest to the soldiers. We can spare a sack. Could you carry it? It's {Compass(to.Centre - village.Centre)}, " +
                             $"about {Round(to.Centre.DistanceTo(village.Centre))} metres. It's in your backpack now. It's heavy. Sorry.");
            }
        }
    }

    // ================================================================= progress

    private double _since;
    private bool _fWas;

    public override void _Process(double delta)
    {
        // Delivery: hand over the sack at the destination (F).
        bool f = Input.IsPhysicalKeyPressed(Key.F), fPressed = f && !_fWas;
        _fWas = f;
        foreach (var job in _jobs.OfType<DeliveryJob>().Where(j => j.Active))
            if (Flat(job.To.Centre - Player.GlobalPosition) < job.To.Radius * .8f && !Player.Dead)
            {
                Hud.Prompt($"F   Hand over the rice to {job.To.Name}");
                if (fPressed && Input.MouseMode == Input.MouseModeEnum.Captured) Finish(job, 12, $"you brought rice to {job.To.Name}", $"{job.To.Name} has rice again", () => Support.Change(job.To.Index, 10, "you brought them rice"));
            }

        _since += delta;
        if (_since < .5) return;
        _since = 0;
        foreach (var job in _jobs.Where(j => j.Active).ToList())
        {
            switch (job)
            {
                case CheckpointJob c:
                {
                    bool clear = Patrols.Current != null && c.Guards.Count == 0
                        ? Patrols.Current.IsCleared(c.Checkpoint.Index)
                        : c.Guards.Count > 0 && c.Guards.TrueForAll(g => !IsInstanceValid(g) || g.State == Guard.Mood.Dead);
                    if (clear) Finish(c, 30, $"you cleared {c.Checkpoint.Name}", $"{c.Checkpoint.Name} is clear", GiftForCheckpoint);
                    break;
                }
                case PatrolJob p:
                    if (Patrols.Current is not { } patrols) break;
                    if (patrols.Where(p.Patrol) is { } at) p.Last = at;
                    if (patrols.IsGone(p.Patrol)) Finish(p, 20, "the patrol won't bother them again", "The patrol is gone", () => Kit.Current?.Add(ItemDef.Bandage, 3, quiet: true));
                    break;
                case MissingJob m:
                    if (!IsInstanceValid(m.Lost) || m.Lost.IsDead) { Fail(m, $"{m.Who} didn't make it home", -15); break; }
                    if (m.Lost.Following && Flat(m.Lost.GlobalPosition - m.Village.Centre) < m.Village.Radius * .9f)
                    {
                        Finish(m, 20, $"you brought {m.Who} home", $"{m.Who} is home", () => { Kit.Current?.Add(ItemDef.RiceParcel, 3, quiet: true); });
                        Life.Adopt(m.Lost, m.Village);
                    }
                    break;
            }
        }
    }

    private void Finish(Job job, float support, string reason, string title, Action? gift = null)
    {
        job.Done = true;
        if (job is DeliveryJob && Kit.Current is { } kit)
        {
            var sack = kit.AllItems.FirstOrDefault(i => i.Def == ItemDef.RiceSacks);
            if (sack != null) kit.Remove(sack);
        }
        Support.Change(job.Village.Index, support, reason);
        Support.Spread(job.Village.Index, support / 6);
        gift?.Invoke();
        Hud.Announce(title, $"{job.Village.Name} won't forget it");
        GD.Print($"[jobs] done: {job.Title}");
        if (Tracked == job) Tracked = _jobs.FirstOrDefault(j => j.Active);
    }

    private void Fail(Job job, string why, float support)
    {
        job.Failed = true;
        Support.Change(job.Village.Index, support, why);
        Hud.Announce(why, job.Village.Name);
        GD.Print($"[jobs] failed: {job.Title}");
        if (Tracked == job) Tracked = _jobs.FirstOrDefault(j => j.Active);
    }

    private void GiftForCheckpoint()
    {
        if (Kit.Current is not { } kit) return;
        bool laser = !kit.AllItems.Any(i => i.Def == ItemDef.Laser) && kit.Loadout.Side != "side_laser";
        var gift = laser ? ItemDef.Laser : ItemDef.Compensator;
        kit.Add(gift, 1, quiet: true);
        kit.Add(ItemDef.Mag30, 2, quiet: true);
        Hud.Toast($"Gift: {gift.Name.ToLower()}, two long magazines");
    }

    private static float Flat(Vector3 v) => new Vector2(v.X, v.Z).Length();
    private static string Round(float metres) => $"{Mathf.Round(metres / 50) * 50:0}";
    public static string Compass(Vector3 d)
    {
        string[] names = { "north", "north-east", "east", "south-east", "south", "south-west", "west", "north-west" };
        float bearing = (Mathf.RadToDeg(MathF.Atan2(d.X, -d.Z)) + 360) % 360;
        return names[(int)MathF.Round(bearing / 45) % 8];
    }

    /// <summary>Tests: the active jobs' kinds and titles.</summary>
    public IEnumerable<(Kind Kind, string Title, bool Done)> All => _jobs.Select(j => (j.Kind, j.Title, j.Done));
    /// <summary>Tests: the lost villager of an active "missing" job.</summary>
    public Villager? LostOf(int village) => _jobs.OfType<MissingJob>().FirstOrDefault(j => j.Village.Index == village && j.Active)?.Lost;
    public object? PatrolOf(int village) => _jobs.OfType<PatrolJob>().FirstOrDefault(j => j.Village.Index == village && j.Active)?.Patrol;
}
