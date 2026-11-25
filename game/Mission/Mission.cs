using System;
using System.Collections.Generic;
using DeadRecon.AI;
using DeadRecon.Base;
using DeadRecon.Gear;
using DeadRecon.People;
using DeadRecon.Player;
using DeadRecon.World;
using Godot;

namespace DeadRecon.Missions;

/// <summary>
/// One operation against a small enemy camp, generated from a seed on the permanent map. Kinds (<see cref="Op"/>):
/// <list type="bullet">
/// <item><b>Radio</b> (your first): take the field radio off their desk, then set it up somewhere quiet. That's home.</item>
/// <item><b>Intel</b>: take the codebook / schedule / ledger from the radio desk (hold F).</item>
/// <item><b>Cache</b>: plant a charge on their supply cache (hold F), then get clear before it goes up.</item>
/// <item><b>Officer</b>: an officer is visiting the camp. He doesn't leave.</item>
/// <item><b>Prisoner</b>: one of ours in a bamboo cage. Open it, then bring him out (he follows you).</item>
/// <item><b>None</b>: no operation: you're at home; pick one on the radio.</item>
/// </list>
/// Then get out: home to your radio once you have one, otherwise the landing zone (green smoke). Rewards come as
/// building supplies; the camp's crates can be searched for more.
/// Usage: <see cref="Plan"/> before trees are planted (it marks clearings), then add the node.
/// </summary>
public partial class Mission : Node3D
{
    /// <summary>Intel = the main objective (whatever the kind), Extract = getting out (or setting up the radio).</summary>
    public enum Phase { Briefing, Intel, Extract, Complete, Failed }
    public enum Op { None, Radio, Intel, Cache, Officer, Prisoner }

    public static Mission? Current { get; private set; }
    /// <summary>The operation the radio picked for the next time the jungle loads (kind, seed).</summary>
    public static (Op Kind, int Seed)? Next;

    public BakedWorld World = null!;
    public Walker Player = null!;
    public Weapon Rifle = null!;
    public TreeScatter Trees = null!;
    public readonly List<Guard> Guards = new();
    /// <summary>The officer (Officer operations; Main sets him when it spawns the camp).</summary>
    public Guard? Officer;
    /// <summary>The operation succeeded.</summary>
    public event Action? Completed;
    public bool SkipBriefing;
    /// <summary>Hour the operation starts (set by the scene from the seed).</summary>
    public float StartHour = 9;

    public Op Kind { get; private set; } = Op.Intel;
    public Phase State { get; private set; } = Phase.Briefing;
    public string OperationName { get; private set; } = "";
    public string IntelName { get; private set; } = "";
    public Vector3 Camp { get; private set; }
    public Vector3 IntelAt { get; private set; }
    /// <summary>The camp's radio desk (the sentry stands by it).</summary>
    public Vector3 Desk => _desk;
    public Vector3 Extraction { get; private set; }
    public float SearchProgress { get; private set; }
    public double Elapsed { get; private set; }
    /// <summary>Getting out means walking home (you have a base), not the helicopter.</summary>
    public bool ExtractHome { get; private set; }
    /// <summary>Close enough to the objective to work on it (hold F).</summary>
    public bool NearIntel => State == Phase.Intel && HoldTarget is { } t && Player.GlobalPosition.DistanceTo(t) < 2.4f && !Player.Dead;
    /// <summary>Where the current objective is, or null when there's nothing to go to.</summary>
    public Vector3? Target => State switch
    {
        Phase.Intel => Kind switch
        {
            Op.Officer => Officer is { } o && IsInstanceValid(o) ? o.GlobalPosition : Camp,
            Op.Prisoner when _cageOpen => _prisoner?.GlobalPosition,
            _ => IntelAt,
        },
        Phase.Extract when Kind == Op.Radio => null,
        Phase.Extract => Extraction,
        _ => null,
    };

    private const float CampRadius = 13, LzRadius = 11, ExtractRadius = 7;
    private RandomNumberGenerator _rng = new();
    private Label3D _marker = null!;
    private Node3D? _intelProp;
    private CanvasLayer _ui = null!;
    private Label _panel = null!;
    private ColorRect _panelBack = null!;
    private int _treesAtStart;
    private string _why = "";
    private Vector3 _desk;
    private float _fuse = -1;
    private readonly List<Node3D> _cache = new();
    private Villager? _prisoner;
    private Node3D? _cageDoor;
    private bool _cageOpen;
    private string _prisonerName = "";

    private static readonly string[] Adjectives = { "Soggy", "Silent", "Muddy", "Crimson", "Sleepy", "Iron", "Damp", "Velvet", "Grumpy", "Thunder", "Lukewarm", "Humid", "Sticky", "Rusty", "Gentle", "Midnight" };
    private static readonly string[] Nouns = { "Mango", "Heron", "Gecko", "Monsoon", "Kettle", "Buffalo", "Machete", "Mosquito", "Lantern", "Spoon", "Orchid", "Tapir", "Noodle", "Cobra", "Teapot", "Hammock" };
    private static readonly (string Item, string Why)[] Intel =
    {
        ("the codebook", "Without it they can't read their own radio messages. They'll be furious. Quietly."),
        ("the patrol schedule", "We'd like to know when they're not looking. Apparently it's most of the time."),
        ("the radio crystals", "No crystals, no radio. No radio, no reinforcements."),
        ("the supply ledger", "Somebody is ordering 400 crates of canned peaches. We need to know who."),
        ("the map case", "They've been drawing on our jungle. We'd like to see what."),
        ("the officer's diary", "Mostly complaints about the food, probably. But maybe not."),
    };
    private static readonly string[] Prisoners = { "Sergeant Okafor", "Corporal Lindqvist", "Private Santos", "Lieutenant Mbeki", "Sergeant Kowalczyk", "Corporal Tran" };

    public static string Describe(Op kind) => kind switch
    {
        Op.Radio => "Steal a radio", Op.Intel => "Steal intel", Op.Cache => "Blow up a supply cache",
        Op.Officer => "Take out an officer", Op.Prisoner => "Rescue a prisoner", _ => "No operation",
    };

    /// <summary>Building supplies the operation pays.</summary>
    public static int Reward(Op kind) => kind switch { Op.Radio => 3, Op.Intel => 5, Op.Cache => 7, Op.Officer => 6, Op.Prisoner => 8, _ => 0 };

    /// <summary>A name for an operation from its seed (the radio lists them before they're planned).</summary>
    public static string NameFor(int seed)
    {
        var rng = new RandomNumberGenerator { Seed = (ulong)seed };
        return $"Operation {Adjectives[rng.RandiRange(0, Adjectives.Length - 1)]} {Nouns[rng.RandiRange(0, Nouns.Length - 1)]}";
    }

    // ================================================================= planning (before planting)

    /// <summary>Pick the operation: name, camp and the way out. Marks clearings for the trees.</summary>
    public void Plan(int seed, Vector3 spawn, Op kind = Op.Intel)
    {
        Current = this;
        Kind = kind;
        OperationName = NameFor(seed);
        _rng = new RandomNumberGenerator { Seed = (ulong)seed * 7 + 1 };
        var intel = Intel[_rng.RandiRange(0, Intel.Length - 1)];
        IntelName = kind == Op.Radio ? "the field radio" : intel.Item;
        _why = intel.Why;
        _prisonerName = Prisoners[_rng.RandiRange(0, Prisoners.Length - 1)];
        TreeScatter.Clearings.Clear();
        if (kind == Op.None) { State = Phase.Complete; GD.Print("[mission] at home: no operation"); return; }

        var home = HomeBase.Position;
        ExtractHome = home != null && kind != Op.Radio;
        // From home the camps are a proper walk away; the first one is close.
        Camp = (home != null && kind != Op.Radio ? PickSite(spawn, 320, 620, CampRadius, avoid: null) : null)
               ?? PickSite(spawn, 170, 260, CampRadius, avoid: null) ?? spawn + new Vector3(150, 0, 0);
        if (ExtractHome) Extraction = home!.Value with { Y = World.HeightAt(home.Value.X, home.Value.Z) };
        else Extraction = PickSite(Camp, 150, 240, LzRadius, avoid: spawn) ?? spawn;
        // Known at planning time (guards are placed from it before the camp node is built).
        _desk = Ground(Camp + new Vector3(1.5f, 0, 0), .4f);
        IntelAt = kind switch
        {
            Op.Cache => Ground(Camp + new Vector3(-5.5f, 0, -4), .4f),
            Op.Prisoner => Ground(Camp + new Vector3(4.5f, 0, -6.5f), 0),
            _ => _desk + new Vector3(.35f, .43f, 0),
        };
        TreeScatter.Clearings.Add((new Vector2(Camp.X, Camp.Z), CampRadius + 3));
        if (!ExtractHome) TreeScatter.Clearings.Add((new Vector2(Extraction.X, Extraction.Z), LzRadius + 4));
        GD.Print($"[mission] {OperationName} ({Describe(kind)}): camp {Camp.DistanceTo(spawn):0} m away, then {(ExtractHome ? "home" : $"extraction {Extraction.DistanceTo(Camp):0} m beyond it")}");
    }

    /// <summary>A random flat, dry spot in a ring around <paramref name="from"/> (away from <paramref name="avoid"/>).</summary>
    private Vector3? PickSite(Vector3 from, float minR, float maxR, float radius, Vector3? avoid)
    {
        var valid = new List<Vector3>();
        for (int k = 0; k < 400 && valid.Count < 25; k++)
        {
            float a = _rng.Randf() * MathF.Tau, r = _rng.RandfRange(minR, maxR);
            var p = from + new Vector3(MathF.Cos(a) * r, 0, MathF.Sin(a) * r);
            if (Math.Abs(p.X) > World.Half - 60 || Math.Abs(p.Z) > World.Half - 60) continue;
            if (avoid is { } av && new Vector2(p.X - av.X, p.Z - av.Z).Length() < 130) continue;
            // Not in a village, not across a road, not on top of home.
            if (Settlements.Current is { } towns && (towns.NearRoad(p.X, p.Z, radius + 4) ||
                towns.Sites.Exists(s => new Vector2(p.X - s.Centre.X, p.Z - s.Centre.Z).Length() < s.Radius + radius + 60))) continue;
            if (HomeBase.Position is { } home && new Vector2(p.X - home.X, p.Z - home.Z).Length() < 250) continue;
            float lo = float.MaxValue, hi = float.MinValue; bool wet = false;
            for (int i = -2; i <= 2; i++)
            for (int j = -2; j <= 2; j++)
            {
                float x = p.X + i * radius * .5f, z = p.Z + j * radius * .5f;
                float h = World.HeightAt(x, z);
                lo = Math.Min(lo, h); hi = Math.Max(hi, h);
                var (gx, gz) = World.ToSample(x, z);
                if (World.NearWater((int)gx, (int)gz, 6)) wet = true;
            }
            if (wet || hi - lo > radius * .28f || hi > 1200) continue;
            p.Y = World.HeightAt(p.X, p.Z);
            valid.Add(p);
        }
        return valid.Count == 0 ? null : valid[_rng.RandiRange(0, valid.Count - 1)];
    }

    // ================================================================= building

    public override void _Ready()
    {
        Current = this;
        _ui = new CanvasLayer { Layer = 5 };
        AddChild(_ui);
        _panelBack = new ColorRect { Color = new Color(.05f, .07f, .05f, .82f), MouseFilter = Control.MouseFilterEnum.Ignore };
        _panelBack.SetAnchorsPreset(Control.LayoutPreset.Center);
        _panelBack.Size = new Vector2(760, 360);
        _panelBack.Position = -_panelBack.Size / 2;
        _ui.AddChild(_panelBack);
        _panel = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, Position = new Vector2(28, 22), Size = new Vector2(704, 320) };
        _panel.AddThemeFontSizeOverride("font_size", 19);
        _panelBack.AddChild(_panel);
        _marker = new Label3D
        {
            FontSize = 44, OutlineSize = 12, PixelSize = .0008f, FixedSize = true, NoDepthTest = true,
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, Modulate = new Color(1, .92f, .55f), Visible = false,
        };
        AddChild(_marker);
        _treesAtStart = Trees.TreesFelled;
        if (Kind == Op.None) { _panelBack.Visible = false; return; }

        BuildCamp();
        if (!ExtractHome) BuildLandingZone();
        Player.Died += () => { if (State is Phase.Intel or Phase.Extract) State = Phase.Failed; ShowPanel(); };
        if (SkipBriefing) Begin(); else ShowPanel();
    }

    public override void _ExitTree() { if (Current == this) Current = null; }

    private void Begin() { State = Phase.Intel; Elapsed = 0; ShowPanel(); }

    private StandardMaterial3D Mat(float r, float g, float b, float rough = .9f) => new() { AlbedoColor = new Color(r, g, b), Roughness = rough };

    /// <summary>A static prop with box collision (bullets stop, people bump).</summary>
    private Node3D Prop(Node3D parent, Mesh mesh, Material mat, Vector3 at, float yaw, Vector3 box)
    {
        var body = new StaticBody3D();
        body.AddChild(new MeshInstance3D { Mesh = mesh, MaterialOverride = mat });
        body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = box } });
        parent.AddChild(body);
        body.GlobalPosition = at;
        body.Rotation = new Vector3(0, yaw, 0);
        return body;
    }

    private Vector3 Ground(Vector3 p, float lift = 0) => new(p.X, World.HeightAt(p.X, p.Z) + lift, p.Z);

    private void BuildCamp()
    {
        var camp = new Node3D();
        AddChild(camp);
        var canvas = Mat(.33f, .36f, .22f);
        var wood = Mat(.36f, .27f, .17f);
        var crate = Mat(.40f, .33f, .20f);
        var olive = Mat(.28f, .31f, .19f, .6f);

        // Three tents facing the middle.
        for (int t = 0; t < 3; t++)
        {
            float a = t * MathF.Tau / 3 + _rng.RandfRange(-.3f, .3f);
            var at = Ground(Camp + new Vector3(MathF.Cos(a), 0, MathF.Sin(a)) * 8.5f, 1.0f);
            Prop(camp, new PrismMesh { Size = new Vector3(3.2f, 2.0f, 4.2f) }, canvas, at, -a + MathF.PI / 2, new Vector3(3.0f, 2.0f, 4.0f));
        }
        // Crates (searchable: building supplies, sometimes magazines).
        var lootAt = Vector3.Zero;
        for (int c = 0; c < 5; c++)
        {
            float a = _rng.Randf() * MathF.Tau, r = _rng.RandfRange(3.5f, 6.5f);
            var at = Ground(Camp + new Vector3(MathF.Cos(a), 0, MathF.Sin(a)) * r, .4f);
            if (Kind == Op.Cache && at.DistanceTo(IntelAt) < 3) continue;
            Prop(camp, new BoxMesh { Size = new Vector3(.8f, .8f, .8f) }, crate, at, _rng.Randf(), new Vector3(.8f, .8f, .8f));
            if (_rng.Randf() < .5f) Prop(camp, new BoxMesh { Size = new Vector3(.7f, .7f, .7f) }, crate, at + Vector3.Up * .75f, _rng.Randf(), new Vector3(.7f, .7f, .7f));
            if (c == 0) lootAt = at;
        }
        if (lootAt != Vector3.Zero)
        {
            int supplies = _rng.RandiRange(2, 4);
            Kit.AddLoot(lootAt, "Search the crates", kit =>
            {
                int got = 0;
                for (int k = 0; k < supplies; k++) if (kit.Add(ItemDef.Supplies, 1, quiet: true)) got++;
                bool mag = GD.Randf() < .5f && kit.Add(ItemDef.Mag20, 1, quiet: true);
                return got > 0 ? $"Found {got} building supplies{(mag ? " and a magazine" : "")}" : "No room in your backpack";
            });
        }
        // The radio desk.
        var desk = _desk;
        Prop(camp, new BoxMesh { Size = new Vector3(1.6f, .8f, .8f) }, wood, desk, .3f, new Vector3(1.6f, .8f, .8f));
        var radio = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(.45f, .28f, .3f) }, MaterialOverride = olive };
        camp.AddChild(radio);
        radio.GlobalPosition = desk + new Vector3(-.35f, .54f, 0);
        var antenna = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = .005f, BottomRadius = .008f, Height = 1.1f }, MaterialOverride = Mat(.1f, .1f, .1f) };
        radio.AddChild(antenna);
        antenna.Position = new Vector3(.15f, .65f, 0);
        switch (Kind)
        {
            case Op.Radio:
                _intelProp = radio;           // you take the radio itself
                IntelAt = radio.GlobalPosition;
                break;
            case Op.Intel:
            case Op.Officer:
                _intelProp = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(.24f, .05f, .32f) }, MaterialOverride = Mat(.55f, .12f, .08f) };
                camp.AddChild(_intelProp);
                _intelProp.GlobalPosition = _desk + new Vector3(.35f, .43f, 0);
                if (Kind == Op.Officer) _intelProp.Visible = false;
                break;
            case Op.Cache:
                // Stacked ammunition and fuel: what you've come to blow up. Red stencils so you know.
                var red = Mat(.55f, .14f, .1f);
                for (int i = 0; i < 6; i++)
                {
                    var at = IntelAt + new Vector3((i % 3 - 1) * .85f, (i / 3) * .7f, 0);
                    _cache.Add(Prop(camp, new BoxMesh { Size = new Vector3(.8f, .65f, .6f) }, i % 2 == 0 ? olive : red, at, 0, new Vector3(.8f, .65f, .6f)));
                }
                for (int i = 0; i < 3; i++)
                {
                    var drum = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = .3f, BottomRadius = .3f, Height = .9f }, MaterialOverride = red };
                    camp.AddChild(drum);
                    drum.GlobalPosition = Ground(IntelAt + new Vector3(-1.9f, 0, (i - 1) * .7f), .45f);
                    _cache.Add(drum);
                }
                break;
            case Op.Prisoner:
                BuildCage(camp);
                break;
        }

        // Campfire: logs, flickering light, smoke.
        var fire = Ground(Camp + new Vector3(-2.5f, 0, 1.5f), .1f);
        for (int l = 0; l < 4; l++)
        {
            var log = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = .07f, BottomRadius = .07f, Height = .9f }, MaterialOverride = Mat(.18f, .12f, .08f) };
            camp.AddChild(log);
            log.GlobalPosition = fire;
            log.Rotation = new Vector3(MathF.PI / 2 - .35f, l * MathF.PI / 2, 0);
        }
        _fireLight = new OmniLight3D { LightColor = new Color(1, .55f, .25f), LightEnergy = 2.2f, OmniRange = 9, ShadowEnabled = true };
        camp.AddChild(_fireLight);
        _fireLight.GlobalPosition = fire + Vector3.Up * .5f;
        camp.AddChild(Smoke(fire + Vector3.Up * .3f, new Color(.55f, .55f, .52f, .35f), 1.2f));
    }

    /// <summary>A bamboo cage with a prisoner sitting in it, and a door you can pick open.</summary>
    private void BuildCage(Node3D camp)
    {
        var bamboo = Mat(.62f, .55f, .32f);
        var c = IntelAt;
        float half = 1.1f, h = 1.9f;
        for (int i = 0; i < 4; i++)
            for (int k = 0; k < 7; k++)
            {
                float t = -half + k * (2 * half / 6);
                var (x, z) = i switch { 0 => (t, -half), 1 => (t, half), 2 => (-half, t), _ => (half, t) };
                if (i == 1 && Math.Abs(t) < .5f) continue;   // the door's gap
                var bar = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = .03f, BottomRadius = .035f, Height = h }, MaterialOverride = bamboo };
                camp.AddChild(bar);
                bar.GlobalPosition = Ground(c + new Vector3(x, 0, z), h / 2);
            }
        var roof = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(2.4f, .06f, 2.4f) }, MaterialOverride = bamboo };
        camp.AddChild(roof);
        roof.GlobalPosition = c with { Y = World.HeightAt(c.X, c.Z) + h };
        // Walls as collision (bars are too thin to bump into nicely), with a gap at the door.
        var walls = new StaticBody3D();
        foreach (var (pos, size) in new[] { (new Vector3(0, h / 2, -half), new Vector3(2.2f, h, .1f)), (new Vector3(-half, h / 2, 0), new Vector3(.1f, h, 2.2f)),
                                            (new Vector3(half, h / 2, 0), new Vector3(.1f, h, 2.2f)), (new Vector3(-.8f, h / 2, half), new Vector3(.6f, h, .1f)),
                                            (new Vector3(.8f, h / 2, half), new Vector3(.6f, h, .1f)) })
            walls.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = size }, Position = pos });
        camp.AddChild(walls);
        walls.GlobalPosition = c with { Y = World.HeightAt(c.X, c.Z) };
        // The door: bars lashed together, solid until opened.
        var door = new StaticBody3D();
        door.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(1, h, .1f) }, Position = new Vector3(0, h / 2, 0) });
        for (int k = 0; k < 4; k++)
            door.AddChild(new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = .03f, BottomRadius = .035f, Height = h }, MaterialOverride = bamboo, Position = new Vector3(-.36f + k * .24f, h / 2, 0) });
        camp.AddChild(door);
        door.GlobalPosition = Ground(c + new Vector3(0, 0, half));
        _cageDoor = door;
        IntelAt = Ground(c + new Vector3(0, 0, half + .4f), .9f);
        // The prisoner: one of the soldier models, without his rifle.
        var faces = SoldierBody.Catalogue();
        if (faces.Count == 0) return;
        _prisoner = new Villager
        {
            Lost = true, Player = Player, World = World, Seed = 777, Layout = new Villages.SiteLayout(),
            CharacterScene = faces[_rng.RandiRange(0, faces.Count - 1)], Name = "Prisoner",
            FoundPrompt = $"\"Come on, {_prisonerName.Split(' ')[1]}. We're leaving.\"",
            FoundLine = "About time! I'd have walked out myself, but they took my boots. ...Joking. Lead the way.",
            Calls = new[] { "Hey! HEY! Over here!", "Is that... one of ours?", "I knew you'd come. Well. I hoped.", "Psst! The lock's bamboo. Just saying." },
        };
        camp.AddChild(_prisoner);
        _prisoner.GlobalPosition = Ground(c, .3f);
    }

    private OmniLight3D _fireLight = null!;

    private void BuildLandingZone()
    {
        var at = Ground(Extraction, .05f);
        // A ring of white-painted stones and a green smoke grenade going.
        for (int s = 0; s < 10; s++)
        {
            float a = s * MathF.Tau / 10;
            var stone = new MeshInstance3D { Mesh = new SphereMesh { Radius = .22f, Height = .3f }, MaterialOverride = Mat(.85f, .85f, .8f) };
            AddChild(stone);
            stone.GlobalPosition = Ground(Extraction + new Vector3(MathF.Cos(a), 0, MathF.Sin(a)) * 5, .05f);
        }
        AddChild(Smoke(at, new Color(.35f, .75f, .35f, .45f), 2.5f));
    }

    private static CpuParticles3D Smoke(Vector3 at, Color colour, float size)
    {
        var smoke = new CpuParticles3D
        {
            Amount = 40, Lifetime = 6, Direction = Vector3.Up, Spread = 12, InitialVelocityMin = .8f, InitialVelocityMax = 1.6f,
            Gravity = new Vector3(.3f, .25f, .1f), ScaleAmountMin = size * .6f, ScaleAmountMax = size,
            Mesh = new QuadMesh { Size = new Vector2(1, 1) },
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = colour, AlbedoTexture = Puff(), Transparency = BaseMaterial3D.TransparencyEnum.Alpha, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles, VertexColorUseAsAlbedo = true,
            },
            ColorRamp = Fade(colour),
        };
        smoke.Position = at;
        return smoke;
    }

    private static GradientTexture2D? _puff;

    /// <summary>A soft round puff: opaque in the middle, fading to nothing at the edge.</summary>
    private static GradientTexture2D Puff()
    {
        if (_puff != null) return _puff;
        var g = new Gradient();
        g.SetColor(0, new Color(1, 1, 1, 1));
        g.AddPoint(.45f, new Color(1, 1, 1, .55f));
        g.SetColor(g.GetPointCount() - 1, new Color(1, 1, 1, 0));
        return _puff = new GradientTexture2D
        {
            Gradient = g, Fill = GradientTexture2D.FillEnum.Radial, FillFrom = new Vector2(.5f, .5f), FillTo = new Vector2(1f, .5f),
            Width = 64, Height = 64,
        };
    }

    private static Gradient Fade(Color c)
    {
        var g = new Gradient();
        g.SetColor(0, new Color(1, 1, 1, 0));
        g.AddPoint(.15f, new Color(1, 1, 1, 1));
        g.SetColor(g.GetPointCount() - 1, new Color(1, 1, 1, 0));
        return g;
    }

    // ================================================================= running

    /// <summary>Where you stand to hold F for the objective (null when the objective isn't a hold-F one).</summary>
    private Vector3? HoldTarget => Kind switch
    {
        Op.Officer => null,
        Op.Cache when _fuse >= 0 => null,
        Op.Prisoner when _cageOpen => null,
        Op.None => null,
        _ => IntelAt,
    };

    private float HoldTime => Kind switch { Op.Cache => 3.5f, Op.Prisoner => 2.5f, Op.Radio => 2f, _ => 1.6f };

    public override void _Process(double delta)
    {
        if (Kind == Op.None) { _marker.Visible = false; return; }
        float dt = (float)delta;
        _fireLight.LightEnergy = 2f + MathF.Sin((float)Time.GetTicksMsec() * .013f) * .3f + MathF.Sin((float)Time.GetTicksMsec() * .031f) * .2f;
        if (State is Phase.Intel or Phase.Extract) Elapsed += delta;

        if (State == Phase.Intel)
        {
            if (HoldTarget is { } hold)
            {
                bool near = Player.GlobalPosition.DistanceTo(hold) < 2.4f && !Player.Dead;
                bool holding = near && Input.IsPhysicalKeyPressed(Key.F);
                SearchProgress = holding ? Math.Min(1, SearchProgress + dt / HoldTime) : Math.Max(0, SearchProgress - dt);
                if (SearchProgress >= 1) { SearchProgress = 0; ObjectiveAction(); }
            }
            switch (Kind)
            {
                case Op.Cache when _fuse >= 0:
                    _fuse -= dt;
                    if (_fuse <= 0) Explode();
                    break;
                case Op.Officer when Officer is { } o && (!IsInstanceValid(o) || o.State == Guard.Mood.Dead):
                    Hud("The officer is down");
                    ToExtract();
                    break;
                case Op.Prisoner when _cageOpen && _prisoner is { } pr:
                    if (!IsInstanceValid(pr) || pr.IsDead) { Fail($"{_prisonerName} didn't make it."); break; }
                    if (pr.Following) ToExtract();
                    break;
            }
        }
        else if (State == Phase.Extract)
        {
            if (Kind == Op.Prisoner && _prisoner is { } pr && (!IsInstanceValid(pr) || pr.IsDead)) Fail($"{_prisonerName} didn't make it.");
            else if (Kind == Op.Radio)
            {
                if (HomeBase.Saved != null) Complete();
            }
            else if (Flat(Player.GlobalPosition - Extraction) < (ExtractHome ? HomeBase.Radius : ExtractRadius) && !Player.Dead
                     && (Kind != Op.Prisoner || _prisoner == null || Flat(_prisoner.GlobalPosition - Extraction) < (ExtractHome ? HomeBase.Radius + 4 : ExtractRadius + 6)))
                Complete();
        }

        var target = Target;
        _marker.Visible = target != null && State is Phase.Intel or Phase.Extract;
        if (target is { } t)
        {
            _marker.GlobalPosition = t + Vector3.Up * (State == Phase.Intel ? (Kind == Op.Officer ? 2.4f : 1.4f) : 4f);
            string label = State == Phase.Extract ? (ExtractHome ? "HOME" : "EXTRACT") : Kind switch
            {
                Op.Officer => "OFFICER", Op.Cache => _fuse >= 0 ? $"CHARGE  {_fuse:0} s" : "CACHE", Op.Prisoner => _cageOpen ? "PRISONER" : "CAGE",
                Op.Radio => "RADIO", _ => "INTEL",
            };
            _marker.Text = $"{label}  {Player.GlobalPosition.DistanceTo(t):0} m";
            _marker.PixelSize = .0008f * Player.Camera.Fov / 70f; // same size through the scope
        }
    }

    private static void Hud(string text) => DeadRecon.UI.Hud.Toast(text);

    /// <summary>The hold-F part of the objective is done.</summary>
    private void ObjectiveAction()
    {
        switch (Kind)
        {
            case Op.Cache:
                _fuse = 25;
                Hud("Charge set: 25 seconds. Walk away. Briskly.");
                GD.Print("[mission] charge planted");
                break;
            case Op.Prisoner:
                _cageOpen = true;
                if (_cageDoor != null) _cageDoor.QueueFree();
                Hud("The lock gives. Talk to him (F).");
                GD.Print("[mission] cage open");
                break;
            case Op.Radio:
                if (_intelProp != null) _intelProp.Visible = false;
                Kit.Current?.Add(ItemDef.FieldRadio, 1);
                Hud("Got the radio. Now find somewhere quiet to set it up.");
                GD.Print("[mission] took the radio");
                State = Phase.Extract;
                ShowPanel();
                break;
            default:
                if (_intelProp != null) _intelProp.Visible = false;
                Kit.Current?.AddIntel(IntelName);
                GD.Print($"[mission] took {IntelName}");
                ToExtract();
                break;
        }
    }

    private void ToExtract()
    {
        if (State != Phase.Intel) return;
        State = Phase.Extract;
        ShowPanel();
    }

    /// <summary>The cache goes up: a flash, a boom, smoke; anyone too close goes with it.</summary>
    private void Explode()
    {
        _fuse = -1;
        var at = IntelAt;
        foreach (var n in _cache) if (IsInstanceValid(n)) n.QueueFree();
        _cache.Clear();
        var flash = new OmniLight3D { LightColor = new Color(1, .7f, .35f), LightEnergy = 16, OmniRange = 40 };
        AddChild(flash);
        flash.GlobalPosition = at + Vector3.Up * 2;
        var tween = CreateTween();
        tween.TweenProperty(flash, "light_energy", 0f, .9f);
        tween.TweenCallback(Callable.From(flash.QueueFree));
        AddChild(Smoke(at + Vector3.Up, new Color(.18f, .17f, .16f, .7f), 4.5f));
        var fireball = new CpuParticles3D
        {
            OneShot = true, Explosiveness = .95f, Amount = 60, Lifetime = 1.2f, Direction = Vector3.Up, Spread = 70, InitialVelocityMin = 5, InitialVelocityMax = 14,
            Gravity = new Vector3(0, -4, 0), ScaleAmountMin = 1, ScaleAmountMax = 2.4f, Mesh = new QuadMesh { Size = new Vector2(1, 1) },
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color(1, .6f, .2f), AlbedoTexture = Puff(), Transparency = BaseMaterial3D.TransparencyEnum.Alpha, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles, VertexColorUseAsAlbedo = true, BlendMode = BaseMaterial3D.BlendModeEnum.Add,
            },
            ColorRamp = Fade(new Color(1, .6f, .2f)), Emitting = true,
        };
        AddChild(fireball);
        fireball.GlobalPosition = at + Vector3.Up;
        var boom = new AudioStreamPlayer3D { Stream = DeadRecon.Combat.Effects.Gunshot(), UnitSize = 60, MaxDistance = 2000, VolumeDb = 12, PitchScale = .35f, Bus = "Effects" };
        AddChild(boom);
        boom.GlobalPosition = at;
        boom.Play();
        foreach (var g in Guard.All)
            if (g.State != Guard.Mood.Dead && g.GlobalPosition.DistanceTo(at) < 12) g.TakeHit(400, g.GlobalPosition + Vector3.Up, at);
        if (Player.GlobalPosition.DistanceTo(at) < 10) Player.TakeHit(Player.GlobalPosition.DistanceTo(at) < 5 ? 200 : 60, Player.GlobalPosition, at);
        DeadRecon.Combat.Hearing.Make(at, 600, this);
        Hud("The cache is gone. Spectacularly.");
        GD.Print("[mission] cache destroyed");
        ToExtract();
    }

    private void Complete()
    {
        State = Phase.Complete;
        GD.Print($"[mission] complete in {Elapsed:0} s");
        Completed?.Invoke();
        Kit.Current?.RemoveIntel();   // handed over
        if (Kind == Op.Prisoner && _prisoner != null && IsInstanceValid(_prisoner)) _prisoner.Say("Home sweet... tarp. I'll take it. Thank you.");
        int reward = Reward(Kind);
        if (reward > 0 && Kit.Current is { } kit)
        {
            int got = 0;
            for (int k = 0; k < reward; k++) if (kit.Add(ItemDef.Supplies, 1, quiet: true)) got++;
            Hud($"Reward: {got} building supplies{(got < reward ? " (no room for the rest)" : "")}");
        }
        HomeBase.OperationDone();
        ShowPanel();
    }

    private void Fail(string why)
    {
        State = Phase.Failed;
        _failWhy = why;
        GD.Print($"[mission] failed: {why}");
        ShowPanel();
    }

    private string _failWhy = "You were shot by a grumpy man.";

    public override void _UnhandledInput(InputEvent e)
    {
        if (State == Phase.Briefing && (e is InputEventKey { Pressed: true } || e is InputEventMouseButton { Pressed: true })) Begin();
        // Debrief: Space puts it away and you carry on in the jungle.
        if (State is Phase.Complete or Phase.Failed && _panelBack.Visible && e is InputEventKey { Pressed: true, Echo: false, PhysicalKeycode: Key.Space })
        {
            _panelBack.Visible = false;
            GetViewport().SetInputAsHandled();
        }
    }

    /// <summary>For tests: do the objective now (take the intel, plant and fire the charge, open the cage...).</summary>
    public void TestTakeIntel()
    {
        if (State != Phase.Intel) return;
        if (Kind == Op.Officer) { Officer?.TakeHit(1000, Officer.GlobalPosition + Vector3.Up, Officer.GlobalPosition + Vector3.Right); return; }
        ObjectiveAction();
        if (Kind == Op.Cache) _fuse = .01f;
    }

    /// <summary>For tests: the prisoner (Prisoner operations).</summary>
    public Villager? Prisoner => _prisoner;

    /// <summary>One line for the HUD.</summary>
    public string Objective()
    {
        if (Kind == Op.None)
            return HomeBase.Position is { } home && Flat(home - Player.GlobalPosition) > 25
                ? $"No operation. Home is {Flat(home - Player.GlobalPosition):0} m {Compass(home - Player.GlobalPosition)}: pick one on the radio"
                : "Home. Use the radio (F) to pick an operation, build, or listen in";
        var target = Target ?? Camp;
        var d = target - Player.GlobalPosition;
        string dir = Compass(d);
        string hold = SearchProgress > 0 ? $"   {(Kind == Op.Cache ? "setting the charge" : Kind == Op.Prisoner ? "picking the lock" : "taking it")}... {SearchProgress * 100:0}%"
                    : NearIntel ? "   hold F" : "";
        return State switch
        {
            Phase.Briefing => $"{OperationName}: read the briefing (any key)",
            Phase.Intel => Kind switch
            {
                Op.Radio => $"OBJECTIVE: take the field radio from their camp   {d.Length():0} m {dir}{hold}",
                Op.Cache => _fuse >= 0 ? $"OBJECTIVE: get clear! The charge goes in {_fuse:0} s" : $"OBJECTIVE: plant a charge on the supply cache   {d.Length():0} m {dir}{hold}",
                Op.Officer => $"OBJECTIVE: take out the officer   {d.Length():0} m {dir}",
                Op.Prisoner => _cageOpen ? $"OBJECTIVE: tell {_prisonerName} to follow you (F)" : $"OBJECTIVE: open the cage, free {_prisonerName}   {d.Length():0} m {dir}{hold}",
                _ => $"OBJECTIVE: take {IntelName} from the camp radio desk   {d.Length():0} m {dir}{hold}",
            },
            Phase.Extract when Kind == Op.Radio => "OBJECTIVE: set up the radio somewhere quiet (Tab, right-click the radio), 250 m+ from their camp",
            Phase.Extract => (ExtractHome ? $"OBJECTIVE: get home   {Flat(d):0} m {dir}" : $"OBJECTIVE: reach extraction (green smoke)   {Flat(d):0} m {dir}") +
                             (Kind == Op.Prisoner ? $"  (with {_prisonerName})" : ""),
            Phase.Complete => HomeBase.Saved != null ? "Operation complete. Pick the next one on your radio" : "Operation complete. Enter: next operation, wherever you are",
            _ => $"{OperationName}: FAILED. Enter: try again",
        };
    }

    private static string Compass(Vector3 d)
    {
        // Map: -Z is north, +X east.
        float deg = (Mathf.RadToDeg(MathF.Atan2(d.X, -d.Z)) + 360) % 360;
        string[] names = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };
        return names[(int)MathF.Round(deg / 45) % 8];
    }

    private static float Flat(Vector3 v) => new Vector2(v.X, v.Z).Length();

    private void ShowPanel()
    {
        _panelBack.Visible = State is Phase.Briefing or Phase.Complete or Phase.Failed;
        if (State == Phase.Briefing)
        {
            float campDist = Player.GlobalPosition.DistanceTo(Camp);
            string where = $"a small enemy camp, about {campDist:0} m {Compass(Camp - Player.GlobalPosition)} of you";
            string job = Kind switch
            {
                Op.Radio => $"You're on your own out here, with no way to call anyone. There's {where}, and they have a field radio on a desk. " +
                            "Take it. Then find a quiet spot, well away from them, and set it up (Tab, right-click the radio). That's home.",
                Op.Cache => $"Their supply cache at {where}: ammunition, fuel, canned peaches. Plant a charge on it (hold F), then be somewhere else when it goes.",
                Op.Officer => $"An officer is visiting {where}. Mostly to shout at people. He's the one by the radio desk. He doesn't leave.",
                Op.Prisoner => $"{_prisonerName} was taken two days ago and is being kept in a bamboo cage at {where}. Pick the lock (hold F) and bring him out.",
                _ => $"Take {IntelName} from the radio desk at {where}. {_why}",
            };
            string after = Kind == Op.Radio ? "" : ExtractHome ? "Then come home.\n\n" : "Then get to the landing zone. Look for the green smoke.\n\n";
            _panel.Text =
                $"{OperationName.ToUpper()}   ·   {Describe(Kind).ToUpper()}\n\n{job}\n\n{after}" +
                $"Insertion at {(int)StartHour:00}:{(int)(StartHour % 1 * 60):00} ({TimeOfDay.Describe(StartHour)}). " +
                (StartHour < 5.5f || StartHour > 19.3f ? "It's dark: they won't see you far, but you won't see much either. " : "") +
                $"{Guards.Count} guards, reportedly all in a bad mood.   Pay: {Reward(Kind)} building supplies.\n\n" +
                "Tip: crouch (hold C, or Alt to toggle) in the ferns. Rain covers your footsteps.\n\n" +
                "Press any key to begin.";
        }
        else if (State == Phase.Complete)
        {
            int down = 0, alerts = 0;
            foreach (var g in Guards) { if (g.State == Guard.Mood.Dead) down++; alerts += g.TimesAlerted; }
            int trees = Trees.TreesFelled - _treesAtStart;
            string verdict = alerts == 0 ? "Nobody even knew you were there. Ghost." :
                             down == Guards.Count ? "Loud, but effective. The paperwork will be enormous." :
                             "Messy. You were spotted, but you got it done.";
            string done = Kind switch
            {
                Op.Radio => "The radio's up. It crackles. That's home now: operations, news and gossip come through it, and you can build here.",
                Op.Cache => "Their cache is a smoking hole.", Op.Officer => "The officer won't be shouting at anyone any more.",
                Op.Prisoner => $"{_prisonerName} is safe.", _ => $"You got {IntelName} out.",
            };
            _panel.Text =
                $"{OperationName.ToUpper()}: COMPLETE\n\n{done}\n\n" +
                $"Time: {(int)Elapsed / 60}:{(int)Elapsed % 60:00}    Shots fired: {Rifle.ShotsFired}    Guards down: {down}/{Guards.Count}\n" +
                $"Times spotted: {alerts}    Trees felled: {trees}{(trees > 0 ? " (the forestry department has questions)" : "")}    Paid: {Reward(Kind)} building supplies\n\n" +
                $"{verdict}\n\n" +
                (HomeBase.Saved != null ? "Space: close. The radio (F at home) has more work, and the build list."
                                        : "Enter: next operation (it starts where you're standing).\nSpace: keep exploring. Enter works any time.");
        }
        else if (State == Phase.Failed)
        {
            _panel.Text =
                $"{OperationName.ToUpper()}: FAILED\n\n{(Player.Dead ? "You were shot by a grumpy man." : _failWhy)}\n\n" +
                "The jungle stays where it is. The camp won't next time.\n\n" +
                "Enter: start again" + (HomeBase.Saved != null ? " (at home)." : ".");
        }
    }
}
