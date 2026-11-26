using System;
using System.Collections.Generic;
using System.Linq;
using DeadRecon.AI;
using DeadRecon.Combat;
using DeadRecon.Missions;
using DeadRecon.Player;
using DeadRecon.UI;
using DeadRecon.World;
using Godot;

namespace DeadRecon.People;

/// <summary>
/// The people of the settlements near you: who lives where and does what (a fixed roster per settlement), neighbours
/// chatting, the village's opinion of you (<see cref="Support"/>: gunfire in the village, soldiers dealt with nearby,
/// operations finished) and the headman: news, help, a bed for the night, and a job (the checkpoint that keeps
/// taking their rice).
/// <para>Villagers exist only within <see cref="SpawnDistance"/>; further out a settlement is just its buildings.</para>
/// </summary>
public partial class VillageLife : Node
{
    public Villages Villages = null!;
    public Settlements Towns = null!;
    public Walker Player = null!;
    public BakedWorld World = null!;
    public Mission? Mission;
    /// <summary>Puts a guard in the world (home, sentry?): Main knows how to wire one up.</summary>
    public Func<Vector3, bool, Guard>? SpawnGuard;

    private const float SpawnDistance = 380, FreeDistance = 480, TalkDistance = 2.6f;

    private sealed class Folk
    {
        public readonly List<Villager> People = new();
        public Villager? Headman;
        public string HeadmanName = "";
        public float GunfireCooldown, ChatIn = 6;
    }
    private readonly Dictionary<int, Folk> _folk = new();
    private List<string> _faces = new();
    private string _headmanFace = "";
    private double _since;
    private readonly HashSet<Guard> _countedDead = new();

    // The job: a checkpoint's guards (per village), and whether it's done.
    private sealed class Job { public Settlements.Site Checkpoint = null!; public readonly List<Guard> Guards = new(); public bool Done; }
    private readonly Dictionary<int, Job> _jobs = new();
    private readonly Dictionary<int, float> _helpedAt = new();

    // The open conversation.
    private Villager? _talking;
    private int _talkSite = -1;
    private readonly bool[] _keyWas = new bool[4];
    private bool _fWas;

    public int Population { get { int n = 0; foreach (var f in _folk.Values) n += f.People.Count; return n; } }
    public IEnumerable<Villager> PeopleOf(int site) => _folk.TryGetValue(site, out var f) ? f.People : Array.Empty<Villager>();
    public Villager? HeadmanOf(int site) => _folk.TryGetValue(site, out var f) ? f.Headman : null;

    public override void _Ready()
    {
        var all = SoldierBody.Catalogue("res://assets/villagers/blocky");
        foreach (var f in all) if (f.Contains("headman")) _headmanFace = f; else _faces.Add(f);
        if (_faces.Count == 0) GD.PushWarning("No villagers in res://assets/villagers/blocky (tools/soldiers/villagers.ts makes them).");
        Hearing.Made += OnHeard;
        if (Mission != null) Mission.Completed += OnOperationDone;
    }

    public override void _ExitTree()
    {
        Hearing.Made -= OnHeard;
        if (Mission != null) Mission.Completed -= OnOperationDone;
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        foreach (var f in _folk.Values) f.GunfireCooldown -= dt;
        Conversation();
        Chats(dt);

        _since += delta;
        if (_since < .5) return;
        _since = 0;
        var p = Player.GlobalPosition;
        foreach (var site in Towns.Sites)
        {
            if (!site.IsSettlement) continue;
            float d = new Vector2(site.Centre.X - p.X, site.Centre.Z - p.Z).Length() - site.Radius;
            if (_folk.TryGetValue(site.Index, out var folk))
            {
                if (d > FreeDistance)
                {
                    foreach (var v in folk.People) v.QueueFree();
                    Wildlife.Current?.FreeVillage(site.Index);
                    _folk.Remove(site.Index);
                }
            }
            else if (d < SpawnDistance && Villages.LayoutOf(site.Index) is { } layout && _faces.Count > 0) Populate(site, layout);
        }
        SoldiersDealtWith();
        if (JobBoard.Current == null) Jobs();
    }

    // ================================================================= who lives here

    private void Populate(Settlements.Site site, Villages.SiteLayout layout)
    {
        Wildlife.Current?.PopulateVillage(site, layout);
        var folk = new Folk();
        _folk[site.Index] = folk;
        var rng = new RandomNumberGenerator { Seed = (ulong)site.Index * 31 + 7 };
        int count = site.Kind switch { "town" => rng.RandiRange(14, 18), "village" => rng.RandiRange(7, 10), _ => rng.RandiRange(3, 4) };
        var fields = Towns.Fields.FindAll(f => f.Site == site.Index);
        int stalls = layout.Stalls.Count;
        for (int i = 0; i < count; i++)
        {
            var role = i == 0 && _headmanFace != "" ? Villager.Role.Headman
                     : i - 1 < stalls && i > 0 ? Villager.Role.Vendor
                     : fields.Count > 0 && rng.Randf() < .6f ? Villager.Role.Farmer : Villager.Role.Homebody;
            float a = i * 2.4f, r = 1.9f + (i % 3) * .7f;
            var seat = layout.Fire + new Vector3(MathF.Cos(a) * r, 0, MathF.Sin(a) * r);
            seat.Y = World.HeightAt(seat.X, seat.Z);
            var v = new Villager
            {
                Job = role, Site = site.Index, Layout = layout, Player = Player, World = World, Seed = site.Index * 1000 + i,
                Home = layout.Houses.Count > 0 ? layout.Houses[i % layout.Houses.Count] : null,
                Field = role == Villager.Role.Farmer ? fields[rng.RandiRange(0, fields.Count - 1)] : null,
                Seat = seat,
                CharacterScene = role == Villager.Role.Headman ? _headmanFace : _faces[rng.RandiRange(0, _faces.Count - 1)],
                Name = $"Villager_{site.Index}_{i}",
            };
            if (role == Villager.Role.Vendor) { v.Stall = layout.Stalls[i - 1].At; v.StallYaw = layout.Stalls[i - 1].Yaw; }
            AddChild(v);
            v.GlobalPosition = layout.Centre + Vector3.Up;
            v.Plan(snap: true);
            folk.People.Add(v);
            if (role == Villager.Role.Headman) { folk.Headman = v; folk.HeadmanName = HeadmanName(site.Index); }
        }
        GD.Print($"[village] {site.Name}: {count} people ({Support.Describe(Support.LevelAt(site.Index))})");
    }

    private static string HeadmanName(int site)
    {
        string[] first = { "Old Ka", "Grandfather Lo", "Uncle Sa", "Old Tham", "Grandfather Bo", "Uncle Ra", "Old Pha", "Grandfather Su" };
        string[] last = { "lan", "ret", "may", "sung", "kai", "rin", "dok", "som" };
        return first[site * 7 % first.Length] + last[site * 13 % last.Length];
    }

    // ================================================================= what the village notices

    private void OnHeard(Vector3 at, float radius, Node source)
    {
        if (radius < Hearing.Gunshot * .5f || source != Player) return;
        foreach (var (index, folk) in _folk)
        {
            var site = Towns.Sites[index];
            if (folk.GunfireCooldown > 0 || new Vector2(at.X - site.Centre.X, at.Z - site.Centre.Z).Length() > site.Radius + 40) continue;
            folk.GunfireCooldown = 25;
            // Shooting soldiers who are in the village is defending it; shooting at nothing is just frightening.
            bool soldiersHere = Guard.All.Exists(g => g.State != Guard.Mood.Dead && g.GlobalPosition.DistanceTo(site.Centre) < site.Radius + 60);
            if (!soldiersHere) Support.Change(index, -3, "gunfire in the village");
        }
    }

    private void SoldiersDealtWith()
    {
        foreach (var g in Guard.All)
        {
            if (g.State != Guard.Mood.Dead || !_countedDead.Add(g)) continue;
            foreach (var site in Towns.Sites)
                if (site.IsSettlement && g.GlobalPosition.DistanceTo(site.Centre) < 400)
                    Support.Change(site.Index, 3, "a soldier who bothered them is gone");
        }
    }

    private void OnOperationDone()
    {
        if (Mission == null) return;
        Settlements.Site? nearest = null;
        foreach (var s in Towns.Sites)
            if (s.IsSettlement && (nearest == null || s.Centre.DistanceTo(Mission.Camp) < nearest.Centre.DistanceTo(Mission.Camp))) nearest = s;
        if (nearest == null || nearest.Centre.DistanceTo(Mission.Camp) > 2500) return;
        Support.Change(nearest.Index, 12, "the camp near them was hit");
        Support.Spread(nearest.Index, 3);
    }

    /// <summary>Two neighbours near you, standing or sitting together: one says something, the other answers.</summary>
    private void Chats(float dt)
    {
        foreach (var (index, folk) in _folk)
        {
            folk.ChatIn -= dt;
            if (folk.ChatIn > 0) continue;
            folk.ChatIn = (float)GD.RandRange(9, 16);
            if (Player.GlobalPosition.DistanceTo(Towns.Sites[index].Centre) > Towns.Sites[index].Radius + 30) continue;
            var calm = folk.People.FindAll(v => v.State is Villager.Doing.Sitting or Villager.Doing.Standing or Villager.Doing.Sheltering
                                                && v != _talking && v.GlobalPosition.DistanceTo(Player.GlobalPosition) < 35);
            for (int a = 0; a < calm.Count; a++)
                for (int b = 0; b < calm.Count; b++)
                {
                    if (a == b || calm[a].GlobalPosition.DistanceTo(calm[b].GlobalPosition) > 7) continue;
                    calm[a].Bark("chat");
                    var other = calm[b];
                    GetTree().CreateTimer(2.6).Timeout += () => { if (IsInstanceValid(other)) other.Bark("chat"); };
                    return;
                }
        }
    }

    // ================================================================= the headman

    private void Conversation()
    {
        bool f = Input.IsPhysicalKeyPressed(Key.F), fPressed = f && !_fWas;
        _fWas = f;
        if (_talking != null)
        {
            // Walk away (or something happens to him) and the conversation is over.
            if (!IsInstanceValid(_talking) || _talking.IsDead || _talking.Indoors || Player.Dead ||
                _talking.GlobalPosition.DistanceTo(Player.GlobalPosition) > TalkDistance + 2 || fPressed)
            {
                EndTalk();
                return;
            }
            for (int k = 0; k < 3; k++)
            {
                bool down = Input.IsPhysicalKeyPressed(Key.Key1 + k);
                if (down && !_keyWas[k]) Choose(k);
                _keyWas[k] = down;
            }
            return;
        }
        foreach (var (index, folk) in _folk)
        {
            var h = folk.Headman;
            if (h == null || h.IsDead || h.Indoors || h.GlobalPosition.DistanceTo(Player.GlobalPosition) > TalkDistance) continue;
            Hud.Prompt($"F   Talk to {folk.HeadmanName}, {(Towns.Sites[index].Kind == "hamlet" ? "elder" : "headman")} of {Towns.Sites[index].Name}");
            if (fPressed) StartTalk(index, h);
            return;
        }
        // Anyone else: a few words, and where to find the one in charge.
        foreach (var (index, folk) in _folk)
            foreach (var v in folk.People)
            {
                if (v == folk.Headman || v.IsDead || v.Indoors || v.Lost || v.GlobalPosition.DistanceTo(Player.GlobalPosition) > TalkDistance) continue;
                Hud.Prompt("F   Talk");
                if (!fPressed) return;
                var level = Support.LevelAt(index);
                string title = Towns.Sites[index].Kind == "hamlet" ? "the elder" : "the headman";
                string where = folk.Headman is { IsDead: false } h2 ? (h2.Indoors ? "at home, probably asleep" : $"over there, {Compass(h2.GlobalPosition - v.GlobalPosition)}") : "...we don't have one any more";
                v.Say(level switch
                {
                    Support.Level.Hostile => $"I've got nothing to say to you. Talk to {title} if you must. {where}.",
                    Support.Level.Wary => $"Me? I just grow rice. If you want something, {folk.HeadmanName} is {title}. {where}.",
                    _ => $"Hello again! If you need anything, {folk.HeadmanName} is {title}. {where}.",
                });
                return;
            }
    }

    private void StartTalk(int site, Villager headman)
    {
        _talking = headman;
        _talkSite = site;
        for (int k = 0; k < 3; k++) _keyWas[k] = true;
        var level = Support.LevelAt(site);
        string hello = level switch
        {
            Support.Level.Hostile => "You have some nerve, walking in here. Say what you want and go.",
            Support.Level.Wary => "Soldier. We don't want trouble. Do you want something?",
            Support.Level.Friendly => "Ah, it's you. Sit, if you like. What do you need?",
            _ => "Our friend! Anything this village has is yours. Within reason.",
        };
        headman.Say(hello);
        Show(hello);
    }

    private void Show(string text)
    {
        var level = Support.LevelAt(_talkSite);
        var options = new List<string> { "1   Any news?", "2   We could use some help.", "3   Anything I can do for you?" };
        Hud.ShowDialog($"{_folk[_talkSite].HeadmanName}, {(Towns.Sites[_talkSite].Kind == "hamlet" ? "elder" : "headman")} of {Towns.Sites[_talkSite].Name}  ·  {Support.Describe(level)}", text, options.ToArray());
    }

    private void EndTalk()
    {
        _talking = null;
        _talkSite = -1;
        Hud.CloseDialog();
    }

    private void Choose(int option)
    {
        if (_talking == null) return;
        var site = Towns.Sites[_talkSite];
        var level = Support.LevelAt(_talkSite);
        string reply = option switch { 0 => News(site, level), 1 => Help(site, level), _ => Work(site, level) };
        _talking.Say(reply);
        Show(reply);
    }

    private string News(Settlements.Site site, Support.Level level)
    {
        if (level == Support.Level.Hostile) return "News? The news is you're not welcome.";
        var parts = new List<string>();
        if (Mission != null && Mission.State is Mission.Phase.Intel or Mission.Phase.Briefing)
        {
            int alive = Mission.Guards.FindAll(g => g.State != Guard.Mood.Dead).Count;
            var to = Mission.Camp - site.Centre;
            parts.Add($"There's a camp {Compass(to)} of here, maybe {Mathf.Round(to.Length() / 50) * 50:0} metres. {Count(alive)} soldiers, last time anyone looked.");
            if (level >= Support.Level.Friendly) parts.Add("One of them never leaves the radio. Sits there all day like a hen on an egg.");
        }
        var cp = NearestCheckpoint(site);
        bool quiet = JobBoard.Current?.CheckpointDone(site.Index) == true || (_jobs.TryGetValue(site.Index, out var j) && j.Done);
        if (cp != null) parts.Add($"{cp.Name} is {Compass(cp.Centre - site.Centre)}, on the road. {(quiet ? "Quiet now, thanks to you." : "Avoid it if you can.")}");
        if (parts.Count == 0) parts.Add("Nothing new. The rice grows, the rain falls, my back hurts.");
        return string.Join(" ", parts);
    }

    private string Help(Settlements.Site site, Support.Level level)
    {
        if (level < Support.Level.Friendly) return level == Support.Level.Hostile ? "Help YOU? Ha." : "Help is for friends. We'll see what you are.";
        float now = Time.GetTicksMsec() / 1000f;
        if (_helpedAt.TryGetValue(site.Index, out var t) && now - t < 300) return "We already patched you up. Try not to get shot for five minutes.";
        _helpedAt[site.Index] = now;
        Player.Heal(Walker.MaxHealth);
        // And something for the road.
        if (DeadRecon.Gear.Kit.Current is { } kit)
        {
            kit.Add(DeadRecon.Gear.ItemDef.Bandage, 2, quiet: true);
            kit.Add(DeadRecon.Gear.ItemDef.RiceParcel, 1, quiet: true);
            Hud.Toast("Got 2 dressings and a rice parcel");
        }
        float hour = TimeOfDay.Current?.Hour ?? 12;
        if (level == Support.Level.Loyal && (hour >= 19 || hour < 5) && TimeOfDay.Current is { } time)
        {
            time.Hour = 6.2f;
            return "You're dead on your feet. Sleep here tonight... There. Morning. Rice for breakfast, and you're patched up.";
        }
        return "Eat. Drink. Bandage. In that order. ...There. Good as new. Nearly.";
    }

    private string Work(Settlements.Site site, Support.Level level) => JobBoard.Current?.Talk(site, level) ?? OldWork(site, level);

    /// <summary>A villager alone in the jungle (the "missing" job).</summary>
    public Villager? SpawnLost(Settlements.Site site, Vector3 at, string name)
    {
        var layout = Villages.LayoutOf(site.Index);
        if (layout == null || _faces.Count == 0) return null;
        var v = new Villager
        {
            Job = Villager.Role.Homebody, Site = site.Index, Layout = layout, Player = Player, World = World, Seed = site.Index * 1000 + 900 + _faces.Count,
            Home = layout.Houses.Count > 0 ? layout.Houses[0] : null, Seat = layout.Fire, Lost = true,
            CharacterScene = _faces[Math.Abs(name.GetHashCode()) % _faces.Count], Name = $"Lost_{name.Replace(' ', '_')}",
        };
        AddChild(v);
        v.GlobalPosition = at + Vector3.Up * .3f;
        return v;
    }

    /// <summary>A found villager joins his village's people.</summary>
    public void Adopt(Villager v, Settlements.Site site)
    {
        if (Villages.LayoutOf(site.Index) is { } layout) v.Settle(layout);
        if (_folk.TryGetValue(site.Index, out var folk)) folk.People.Add(v);
    }

    private string OldWork(Settlements.Site site, Support.Level level)
    {
        if (level == Support.Level.Hostile) return "Do something for US? After what you did? Ha.";
        if (_jobs.TryGetValue(site.Index, out var job))
            return job.Done ? "You already did more than enough. The rice is staying in our sacks now." : $"The soldiers at {job.Checkpoint.Name} are still there. Still eating our rice.";
        var cp = NearestCheckpoint(site);
        if (cp == null || (SpawnGuard == null && Patrols.Current == null)) return "Not today. Just... don't let anybody shoot at the chickens.";
        job = new Job { Checkpoint = cp };
        _jobs[site.Index] = job;
        if (Patrols.Current != null)
        {
            // The checkpoint's own garrison (it's there whether you take the job or not).
            if (Patrols.Current.IsCleared(cp.Index)) return $"{cp.Name}? Somebody already emptied it. ...It was you, wasn't it. Thank you!";
            return $"The soldiers at {cp.Name} take a sack of rice from every cart. Every cart! It's {Compass(cp.Centre - site.Centre)} of here, " +
                   $"about {Mathf.Round(cp.Centre.DistanceTo(site.Centre) / 50) * 50:0} metres. Make them stop, and we won't forget it.";
        }
        // No patrol system (some tests): the garrison turns up now.
        var c = cp.Centre with { Y = World.HeightAt(cp.Centre.X, cp.Centre.Z) };
        job.Guards.Add(SpawnGuard!(c + new Vector3(2.5f, 0, 1.5f), true));
        job.Guards.Add(SpawnGuard!(c + new Vector3(-3, 0, -4), false));
        if (site.Kind == "town" || Support.Get(site.Index) > 30) job.Guards.Add(SpawnGuard!(c + new Vector3(5, 0, -6), false));
        return $"The soldiers at {cp.Name} take a sack of rice from every cart. Every cart! It's {Compass(cp.Centre - site.Centre)} of here, " +
               $"about {Mathf.Round(cp.Centre.DistanceTo(site.Centre) / 50) * 50:0} metres. Make them stop, and we won't forget it.";
    }

    private void Jobs()
    {
        foreach (var (site, job) in _jobs)
        {
            bool clear = Patrols.Current != null && job.Guards.Count == 0
                ? Patrols.Current.IsCleared(job.Checkpoint.Index)
                : job.Guards.Count > 0 && job.Guards.TrueForAll(g => !IsInstanceValid(g) || g.State == Guard.Mood.Dead);
            if (job.Done || !clear) continue;
            job.Done = true;
            Support.Change(site, 30, $"you cleared {job.Checkpoint.Name}");
            // A thank-you: whatever the village could find that a soldier might like.
            if (DeadRecon.Gear.Kit.Current is { } kit)
            {
                bool laser = !kit.AllItems.Any(i => i.Def == DeadRecon.Gear.ItemDef.Laser) && kit.Loadout.Side != "side_laser";
                var gift = laser ? DeadRecon.Gear.ItemDef.Laser : DeadRecon.Gear.ItemDef.Compensator;
                kit.Add(gift, 1, quiet: true);
                kit.Add(DeadRecon.Gear.ItemDef.Mag30, 2, quiet: true);
                Hud.Toast($"{Towns.Sites[site].Name} gave you: {gift.Name.ToLower()}, two long magazines");
            }
            Support.Spread(site, 5);
            Hud.Announce($"{job.Checkpoint.Name} is clear", $"{Towns.Sites[site].Name} won't forget it");
        }
    }

    private Settlements.Site? NearestCheckpoint(Settlements.Site site)
    {
        Settlements.Site? best = null;
        foreach (var s in Towns.Sites)
            if (s.Kind == "outpost" && s.Centre.DistanceTo(site.Centre) < 2500 && (best == null || s.Centre.DistanceTo(site.Centre) < best.Centre.DistanceTo(site.Centre))) best = s;
        return best;
    }

    private static string Compass(Vector3 d)
    {
        string[] names = { "north", "north-east", "east", "south-east", "south", "south-west", "west", "north-west" };
        float bearing = (Mathf.RadToDeg(MathF.Atan2(d.X, -d.Z)) + 360) % 360;
        return names[(int)MathF.Round(bearing / 45) % 8];
    }

    private static string Count(int n) => n switch { 0 => "No", 1 => "One", 2 => "Two", 3 => "Three", 4 => "Four", 5 => "Five", _ => "Lots of" };

    /// <summary>For tests: open the headman conversation directly and pick options.</summary>
    public string TestTalk(int site, int option)
    {
        if (!_folk.TryGetValue(site, out var folk) || folk.Headman == null) return "(no headman)";
        StartTalk(site, folk.Headman);
        var s = Towns.Sites[site];
        var level = Support.LevelAt(site);
        string reply = option switch { 0 => News(s, level), 1 => Help(s, level), _ => Work(s, level) };
        Show(reply);
        return reply;
    }
}
