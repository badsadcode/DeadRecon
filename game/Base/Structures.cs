using System;
using System.Collections.Generic;
using System.Linq;
using DeadRecon.Gear;
using DeadRecon.People;
using DeadRecon.Player;
using DeadRecon.UI;
using DeadRecon.World;
using Godot;

namespace DeadRecon.Base;

/// <summary>A kind of thing you can build at home ("buildings" content section; mods add more by id).
/// Kind picks how it looks and works: tent, wall, tower, crate, shelter, fire, lamp, sandbags.</summary>
public sealed record BuildingDef(string Id, string Name, string What, string Kind, int Cost, float Hours, float Radius = 2);

/// <summary>
/// Plan mode and what it makes. Choose a building at the radio and its ghost follows where you look (R turns it,
/// click puts it down, Esc or right-click stops). Placing it moves its building supplies from your bags into the
/// camp's stores. Soldiers whose job is "build" walk to the oldest unfinished one and put it up, a little at a time;
/// without builders, hold F at a blueprint to work on it yourself. Saved with the base.
/// </summary>
public partial class Structures : Node3D
{
    public static Structures? Current { get; private set; }
    public Walker Player = null!;
    public BakedWorld World = null!;

    public sealed class Placed
    {
        public string Id { get; set; } = "";
        public float X { get; set; }
        public float Z { get; set; }
        public float Yaw { get; set; }
        public float Progress { get; set; }   // 0..1 (hours of work done / hours needed)
    }

    private static readonly BuildingDef[] Builtin =
    {
        new("tent", "Tent", "Somewhere for two of your people to sleep dry. More tents, a happier camp.", "tent", 5, 1.5f, 2.2f),
        new("sandbags", "Sandbag wall", "Three metres of waist-high sandbags. Stops bullets. Place them in a line.", "wall", 3, .6f, 1.6f),
        new("tower", "Watchtower", "Five metres up, with a ladder. Guards stand watch up there.", "tower", 10, 4f, 2f),
        new("crate", "Supply crate", "Restocks magazines and dressings (F), once per operation.", "crate", 8, 1.5f, 1),
        new("shelter", "Shelter", "A tarp and a hammock: sleep through the night here (F).", "shelter", 6, 1.5f, 2.2f),
        new("fire", "Campfire", "Warmth, light, and somewhere to sit and argue about whose turn it is.", "fire", 1, .3f, 1.2f),
        new("lamp", "Lamp post", "A lantern on a pole. Lights the camp at night. Also lights you up for anyone watching.", "lamp", 2, .4f, .5f),
    };

    public static Dictionary<string, BuildingDef> Catalogue => _catalogue ??= DeadRecon.Data.Content.Get("buildings", Builtin).ToDictionary(b => b.Id);
    private static Dictionary<string, BuildingDef>? _catalogue;

    private readonly Dictionary<Placed, Node3D> _nodes = new();
    private BuildingDef? _planning;
    private Node3D? _ghost;
    private float _ghostYaw;
    private bool _ghostOk;
    private bool _rWas, _fWas, _clickWas;

    public static List<Placed> All => HomeBase.Saved?.Structures ?? new List<Placed>();
    public static int Stores { get => HomeBase.Saved?.Stores ?? 0; set { if (HomeBase.Saved is { } s) s.Stores = value; } }
    public bool Planning => _planning != null;

    public override void _Ready()
    {
        Current = this;
        HomeBase._nextSite = () => All.Where(p => p.Progress < 1).Select(p => (Vector3?)new Vector3(p.X, 0, p.Z)).FirstOrDefault();
        foreach (var p in All) Show(p);
    }

    public override void _ExitTree() { if (Current == this) Current = null; }

    // ================================================================= plan mode

    public void Plan(string id)
    {
        if (!Catalogue.TryGetValue(id, out var def)) return;
        Stop();
        _planning = def;
        _ghost = Make(def, ghost: true);
        AddChild(_ghost);
        Input.MouseMode = Input.MouseModeEnum.Captured;
        Hud.Announce($"Planning: {def.Name}", "click to place · R to turn · Esc or right-click to stop");
    }

    public void Stop()
    {
        _planning = null;
        _ghost?.QueueFree();
        _ghost = null;
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        if (HomeBase.Saved == null) return;
        if (_planning is { } def && _ghost != null) PlanTick(def);
        Work(dt);
    }

    private void PlanTick(BuildingDef def)
    {
        var home = HomeBase.Position!.Value;
        var cam = Player.Camera.GlobalTransform;
        var q = PhysicsRayQueryParameters3D.Create(cam.Origin, cam.Origin - cam.Basis.Z * 40);
        q.Exclude = new Godot.Collections.Array<Rid> { Player.GetRid() };
        var hit = Player.GetWorld3D().DirectSpaceState.IntersectRay(q);
        var at = hit.Count > 0 ? (Vector3)hit["position"] : cam.Origin - cam.Basis.Z * 8;
        at.Y = World.HeightAt(at.X, at.Z);
        bool r = Input.IsPhysicalKeyPressed(Key.R);
        if (r && !_rWas) _ghostYaw += MathF.PI / 4;
        _rWas = r;
        _ghost!.GlobalPosition = at;
        _ghost.Rotation = new Vector3(0, _ghostYaw, 0);
        // Inside the camp (it grows with what you've built), not on top of something else.
        float campRadius = CampRadius();
        bool inside = new Vector2(at.X - home.X, at.Z - home.Z).Length() < campRadius;
        bool clear = All.All(p => new Vector2(p.X - at.X, p.Z - at.Z).Length() > Catalogue.GetValueOrDefault(p.Id)?.Radius + def.Radius - .8f || def.Kind == "wall");
        int have = Supplies();
        _ghostOk = inside && clear && have >= def.Cost;
        foreach (var mi in RifleBuilder.FindAll<GeometryInstance3D>(_ghost)) mi.Transparency = .55f;
        SetGhostColour(_ghostOk ? new Color(.4f, .8f, 1f) : new Color(1f, .35f, .3f));
        Hud.Prompt(!inside ? $"Too far from the radio (the camp is {campRadius:0} m across... well, wide)" : !clear ? "Something's already there"
                 : have < def.Cost ? $"{def.Name}: needs {def.Cost} building supplies, you carry {have}" : $"Click: place {def.Name} ({def.Cost} supplies)   R: turn   Esc: stop");
        bool click = Input.IsMouseButtonPressed(MouseButton.Left);
        if (click && !_clickWas && _ghostOk)
        {
            Pay(def.Cost);
            var placed = new Placed { Id = def.Id, X = at.X, Z = at.Z, Yaw = _ghostYaw };
            All.Add(placed);
            HomeBase.Saved!.Structures = All;
            HomeBase.SaveNow();
            Show(placed);
            Player.FireLockedUntilRelease = true;
            GD.Print($"[build] planned {def.Id} at {at}");
            int builders = Squad.Roster.Count(s => s.Task == "build" && !s.Squad);
            Hud.Toast(builders > 0 ? $"{def.Name} planned. {builders} builder{(builders == 1 ? "" : "s")} on it." : $"{def.Name} planned. Nobody's on building duty: hold F at it, or give someone the job at the radio.");
        }
        _clickWas = click;
        if (Input.IsMouseButtonPressed(MouseButton.Right) || Input.IsPhysicalKeyPressed(Key.Escape)) Stop();
    }

    /// <summary>Tests: place a blueprint without paying.</summary>
    public void TestPlace(string id, Vector3 at)
    {
        var p = new Placed { Id = id, X = at.X, Z = at.Z };
        All.Add(p);
        HomeBase.Saved!.Structures = All;
        Show(p);
    }
    public static float Progress(string id) => All.FirstOrDefault(p => p.Id == id)?.Progress ?? -1;

    public static float CampRadius() => 14 + 2.5f * All.Count(p => p.Progress >= 1);

    private void SetGhostColour(Color c)
    {
        foreach (var mi in RifleBuilder.FindAll<MeshInstance3D>(_ghost!))
            if (mi.MaterialOverride is StandardMaterial3D m) m.AlbedoColor = new Color(c, .45f);
    }

    private static int Supplies() => Kit.Current?.AllItems.Where(i => i.Def == ItemDef.Supplies).Sum(i => i.Count) ?? 0;

    private static void Pay(int cost)
    {
        if (Kit.Current is not { } kit) return;
        int left = cost;
        foreach (var it in kit.AllItems.Where(x => x.Def == ItemDef.Supplies).ToList())
        {
            int take = Math.Min(left, it.Count);
            it.Count -= take; left -= take;
            if (it.Count <= 0) kit.Remove(it);
            if (left == 0) break;
        }
        kit.Save();
    }

    // ================================================================= building it

    private void Work(float dt)
    {
        var site = All.FirstOrDefault(p => p.Progress < 1);
        if (site == null || !Catalogue.TryGetValue(site.Id, out var def)) return;
        var at = new Vector3(site.X, 0, site.Z);
        float hours = dt * 24 / ((TimeOfDay.Current?.DayMinutes ?? 48) * 60);
        // Soldiers on building duty, standing at the site.
        int working = Comrade.All.Count(c => c.Task == "build" && c.Orders == Comrade.Order.Base && !c.Dead && !c.Down && new Vector2(c.GlobalPosition.X - at.X, c.GlobalPosition.Z - at.Z).Length() < def.Radius + 1.5f);
        // You, holding F at it.
        bool f = Input.IsPhysicalKeyPressed(Key.F);
        bool near = new Vector2(Player.GlobalPosition.X - at.X, Player.GlobalPosition.Z - at.Z).Length() < def.Radius + 1.5f && !Player.Dead && !Planning;
        if (near) Hud.Prompt($"{def.Name}: {site.Progress * 100:0}% built{(working > 0 ? $" ({working} working)" : "")}   ·   hold F to help");
        float rate = working + (near && f ? 1.5f : 0);
        if (rate <= 0) return;
        site.Progress = Math.Min(1, site.Progress + hours * rate / def.Hours * 2);   // x2: a tent takes one builder ~45 s, a watchtower ~2-3 minutes
        if (_nodes.TryGetValue(site, out var n) && n.GetNodeOrNull<Label3D>("Progress") is { } label) label.Text = $"{def.Name.ToUpper()}  {site.Progress * 100:0}%";
        if (site.Progress >= 1)
        {
            GD.Print($"[build] {def.Id} finished");
            Hud.Toast($"{def.Name} finished");
            Show(site);
            HomeBase.SaveNow();
            foreach (var c in Comrade.All.Where(c => c.Task == "build" && c.Orders == Comrade.Order.Base)) c.Post = HomeBase.NextSite() is { } next ? next : c.Post;
        }
        else if (Time.GetTicksMsec() % 20000 < 20) HomeBase.SaveNow();
    }

    /// <summary>(Re)draw one: a blueprint (ghostly, with its progress) or the real thing.</summary>
    private void Show(Placed p)
    {
        if (_nodes.TryGetValue(p, out var old)) old.QueueFree();
        if (!Catalogue.TryGetValue(p.Id, out var def)) return;
        bool done = p.Progress >= 1;
        var n = Make(def, ghost: !done);
        AddChild(n);
        n.GlobalPosition = new Vector3(p.X, World.HeightAt(p.X, p.Z), p.Z);
        n.Rotation = new Vector3(0, p.Yaw, 0);
        if (!done)
        {
            foreach (var mi in RifleBuilder.FindAll<GeometryInstance3D>(n)) mi.Transparency = .6f;
            foreach (var mi in RifleBuilder.FindAll<MeshInstance3D>(n)) if (mi.MaterialOverride is StandardMaterial3D m) m.AlbedoColor = new Color(.45f, .7f, 1f, .4f);
            n.AddChild(new Label3D
            {
                Name = "Progress", Text = $"{def.Name.ToUpper()}  {p.Progress * 100:0}%", FontSize = 30, OutlineSize = 8, PixelSize = .0008f, FixedSize = true,
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, Position = new Vector3(0, 2.6f, 0), Modulate = new Color(.6f, .85f, 1f),
            });
        }
        _nodes[p] = n;
        if (done && def.Kind == "tower")
        {
            var basis = new Basis(Vector3.Up, p.Yaw);
            var foot = n.GlobalPosition + basis * new Vector3(1.45f, 0, 0);
            Ladders.Add(foot with { Y = World.HeightAt(foot.X, foot.Z) }, foot with { Y = n.GlobalPosition.Y + 5.06f }, basis * Vector3.Right, n);
            n.TreeExiting += () => Ladders.RemoveOwner(n);
        }
    }

    /// <summary>The ground point of a finished building of this kind nearest to a point (crates, shelters...).</summary>
    public static Vector3? Nearest(string kind, Vector3 from, float within)
    {
        Placed? best = null;
        foreach (var p in All)
            if (p.Progress >= 1 && Catalogue.TryGetValue(p.Id, out var d) && d.Kind == kind &&
                new Vector2(p.X - from.X, p.Z - from.Z).Length() < within &&
                (best == null || new Vector2(p.X - from.X, p.Z - from.Z).Length() < new Vector2(best.X - from.X, best.Z - from.Z).Length()))
                best = p;
        return best == null ? null : new Vector3(best.X, 0, best.Z);
    }

    // ================================================================= the models

    private static StandardMaterial3D M(Color c, float rough = .9f) => new() { AlbedoColor = c, Roughness = rough, Transparency = BaseMaterial3D.TransparencyEnum.Alpha };

    private Node3D Make(BuildingDef def, bool ghost)
    {
        var root = new Node3D();
        var body = new StaticBody3D();
        if (!ghost) root.AddChild(body);
        void Box(Color c, Vector3 size, Vector3 at, float yaw = 0, bool solid = true)
        {
            root.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = size }, MaterialOverride = M(c), Position = at, Rotation = new Vector3(0, yaw, 0) });
            if (solid) body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = size }, Position = at, Rotation = new Vector3(0, yaw, 0) });
        }
        var wood = new Color(.36f, .27f, .17f); var canvas = new Color(.30f, .34f, .22f); var sand = new Color(.52f, .48f, .36f);
        switch (def.Kind)
        {
            case "tent":
            {
                var prism = new PrismMesh { Size = new Vector3(2.6f, 1.8f, 3.4f) };
                root.AddChild(new MeshInstance3D { Mesh = prism, MaterialOverride = M(canvas), Position = new Vector3(0, .9f, 0) });
                body.AddChild(new CollisionShape3D { Shape = prism.CreateConvexShape(), Position = new Vector3(0, .9f, 0) });
                break;
            }
            case "wall":
                for (int layer = 0; layer < 3; layer++)
                    for (int i = 0; i < 5; i++)
                        Box(sand, new Vector3(.62f, .24f, .38f), new Vector3((i - 2) * .6f + (layer % 2) * .3f, .12f + layer * .23f, 0), 0, false);
                body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(3, .8f, .45f) }, Position = new Vector3(0, .4f, 0) });
                break;
            case "tower":
            {
                float deck = 5, h = 1.2f;
                foreach (var (x, z) in new[] { (-h, -h), (h, -h), (-h, h), (h, h) }) Box(wood, new Vector3(.18f, deck + 1, .18f), new Vector3(x, (deck + 1) / 2 - .3f, z));
                Box(wood, new Vector3(h * 2 + .3f, .12f, h * 2 + .3f), new Vector3(0, deck, 0));
                Box(wood, new Vector3(h * 2, 1, .06f), new Vector3(0, deck + .5f, -h)); Box(wood, new Vector3(h * 2, 1, .06f), new Vector3(0, deck + .5f, h));
                Box(wood, new Vector3(.06f, 1, h * 2), new Vector3(-h, deck + .5f, 0));
                root.AddChild(new MeshInstance3D { Mesh = new PrismMesh { Size = new Vector3(h * 2 + 1, .7f, h * 2 + 1) }, MaterialOverride = M(new Color(.5f, .5f, .48f)), Position = new Vector3(0, deck + 1.6f, 0) });
                for (int r = 0; r < 14; r++) Box(wood, new Vector3(.06f, .04f, .55f), new Vector3(h + .25f, .3f + r * deck / 14, 0), 0, false);
                Box(wood, new Vector3(.08f, deck, .6f), new Vector3(h + .25f, deck / 2, 0));
                break;
            }
            case "crate":
                Box(wood, new Vector3(1.1f, .7f, .7f), new Vector3(0, .35f, 0));
                Box(new Color(.28f, .31f, .19f), new Vector3(.9f, .5f, .5f), new Vector3(0, .95f, 0), .1f);
                break;
            case "shelter":
                Box(wood, new Vector3(.12f, 2.2f, .12f), new Vector3(-1.4f, 1.1f, -1), 0); Box(wood, new Vector3(.12f, 2.2f, .12f), new Vector3(1.4f, 1.1f, -1), 0);
                root.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(3.2f, .03f, 3.2f) }, MaterialOverride = M(canvas), Position = new Vector3(0, 1.6f, .4f), Rotation = new Vector3(-.4f, 0, 0) });
                root.AddChild(new MeshInstance3D { Mesh = new CapsuleMesh { Radius = .28f, Height = 2.2f }, MaterialOverride = M(new Color(.55f, .45f, .3f)), Position = new Vector3(0, .9f, .3f), Rotation = new Vector3(0, 0, MathF.PI / 2) });
                break;
            case "fire":
                for (int i = 0; i < 8; i++) { float a = i / 8f * MathF.Tau; Box(new Color(.42f, .41f, .38f), new Vector3(.25f, .18f, .2f), new Vector3(MathF.Cos(a) * .55f, .08f, MathF.Sin(a) * .55f), -a, false); }
                if (!ghost) root.AddChild(new OmniLight3D { LightColor = new Color(1, .55f, .25f), LightEnergy = 1.6f, OmniRange = 8, Position = new Vector3(0, .5f, 0) });
                break;
            case "lamp":
                Box(wood, new Vector3(.1f, 2.4f, .1f), new Vector3(0, 1.2f, 0));
                Box(new Color(.9f, .75f, .4f), new Vector3(.2f, .25f, .2f), new Vector3(.2f, 2.2f, 0), 0, false);
                if (!ghost) root.AddChild(new OmniLight3D { LightColor = new Color(1, .75f, .45f), LightEnergy = 1.4f, OmniRange = 11, Position = new Vector3(.2f, 2.1f, 0) });
                break;
            default:
                Box(wood, new Vector3(1, 1, 1), new Vector3(0, .5f, 0));
                break;
        }
        if (ghost) body.Free();
        return root;
    }
}
