using System;
using System.Collections.Generic;
using DeadRecon.AI;
using DeadRecon.Player;
using DeadRecon.World;
using Godot;

namespace DeadRecon.Missions;

/// <summary>
/// The Milestone-0 operation: approach a camp, take the intel from its radio desk (hold F), reach extraction.
/// Procedural operations, permanent geography: every run picks a new name and new places for the camp and the
/// landing zone on the same baked map, chosen for being flat, dry and a sensible walk away.
/// Usage: <see cref="Plan"/> before trees are planted (it marks clearings), then add the node.
/// </summary>
public partial class Mission : Node3D
{
    public enum Phase { Briefing, Intel, Extract, Complete, Failed }

    public BakedWorld World = null!;
    public Walker Player = null!;
    public Weapon Rifle = null!;
    public TreeScatter Trees = null!;
    public readonly List<Guard> Guards = new();
    /// <summary>The operation succeeded (you reached extraction with the intel).</summary>
    public event Action? Completed;
    public bool SkipBriefing;
    /// <summary>Hour the operation starts (set by the scene from the seed).</summary>
    public float StartHour = 9;

    public Phase State { get; private set; } = Phase.Briefing;
    public string OperationName { get; private set; } = "";
    public string IntelName { get; private set; } = "";
    public Vector3 Camp { get; private set; }
    public Vector3 IntelAt { get; private set; }
    public Vector3 Extraction { get; private set; }
    public float SearchProgress { get; private set; }
    public double Elapsed { get; private set; }
    /// <summary>Close enough to the intel to take it.</summary>
    public bool NearIntel => State == Phase.Intel && Player.GlobalPosition.DistanceTo(IntelAt) < 2.4f && !Player.Dead;
    /// <summary>Where the current objective is (intel or landing zone), or null when there's nothing to go to.</summary>
    public Vector3? Target => State switch { Phase.Intel => IntelAt, Phase.Extract => Extraction, _ => null };

    private const float CampRadius = 13, LzRadius = 11, SearchTime = 1.6f, ExtractRadius = 7;
    private RandomNumberGenerator _rng = new();
    private Label3D _marker = null!;
    private Node3D _intelProp = null!;
    private CanvasLayer _ui = null!;
    private Label _panel = null!;
    private ColorRect _panelBack = null!;
    private int _treesAtStart;

    private static readonly string[] Adjectives = { "Soggy", "Silent", "Muddy", "Crimson", "Sleepy", "Iron", "Damp", "Velvet", "Grumpy", "Thunder", "Lukewarm", "Humid" };
    private static readonly string[] Nouns = { "Mango", "Heron", "Gecko", "Monsoon", "Kettle", "Buffalo", "Machete", "Mosquito", "Lantern", "Spoon", "Orchid", "Tapir" };
    private static readonly (string Item, string Why)[] Intel =
    {
        ("the codebook", "Without it they can't read their own radio messages. They'll be furious. Quietly."),
        ("the patrol schedule", "We'd like to know when they're not looking. Apparently it's most of the time."),
        ("the radio crystals", "No crystals, no radio. No radio, no reinforcements."),
        ("the supply ledger", "Somebody is ordering 400 crates of canned peaches. We need to know who."),
    };

    // ================================================================= planning (before planting)

    /// <summary>Pick the operation: name, camp and landing zone. Marks both as clearings for the trees.</summary>
    public void Plan(int seed, Vector3 spawn)
    {
        _rng = new RandomNumberGenerator { Seed = (ulong)seed };
        OperationName = $"Operation {Adjectives[_rng.RandiRange(0, Adjectives.Length - 1)]} {Nouns[_rng.RandiRange(0, Nouns.Length - 1)]}";
        var intel = Intel[_rng.RandiRange(0, Intel.Length - 1)];
        IntelName = intel.Item;
        _why = intel.Why;

        Camp = PickSite(spawn, 170, 260, CampRadius, avoid: null) ?? spawn + new Vector3(150, 0, 0);
        Extraction = PickSite(Camp, 150, 240, LzRadius, avoid: spawn) ?? spawn;
        // Known at planning time (guards are placed from it before the camp node is built).
        _desk = Ground(Camp + new Vector3(1.5f, 0, 0), .4f);
        IntelAt = _desk + new Vector3(.35f, .43f, 0);
        TreeScatter.Clearings.Clear();
        TreeScatter.Clearings.Add((new Vector2(Camp.X, Camp.Z), CampRadius + 3));
        TreeScatter.Clearings.Add((new Vector2(Extraction.X, Extraction.Z), LzRadius + 4));
        GD.Print($"[mission] {OperationName}: get {IntelName}; camp {Camp.DistanceTo(spawn):0} m away, extraction {Extraction.DistanceTo(Camp):0} m beyond it");
    }

    private string _why = "";
    private Vector3 _desk;

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
            // Not in a village, not across a road.
            if (Settlements.Current is { } towns && (towns.NearRoad(p.X, p.Z, radius + 4) ||
                towns.Sites.Exists(s => new Vector2(p.X - s.Centre.X, p.Z - s.Centre.Z).Length() < s.Radius + radius + 60))) continue;
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
        BuildCamp();
        BuildLandingZone();
        _marker = new Label3D
        {
            FontSize = 44, OutlineSize = 12, PixelSize = .0008f, FixedSize = true, NoDepthTest = true,
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, Modulate = new Color(1, .92f, .55f),
        };
        AddChild(_marker);

        _ui = new CanvasLayer { Layer = 5 };
        AddChild(_ui);
        _panelBack = new ColorRect { Color = new Color(.05f, .07f, .05f, .82f), MouseFilter = Control.MouseFilterEnum.Ignore };
        _panelBack.SetAnchorsPreset(Control.LayoutPreset.Center);
        _panelBack.Size = new Vector2(760, 330);
        _panelBack.Position = -_panelBack.Size / 2;
        _ui.AddChild(_panelBack);
        _panel = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, Position = new Vector2(28, 22), Size = new Vector2(704, 290) };
        _panel.AddThemeFontSizeOverride("font_size", 20);
        _panelBack.AddChild(_panel);

        _treesAtStart = Trees.TreesFelled;
        Player.Died += () => { if (State is Phase.Intel or Phase.Extract) State = Phase.Failed; ShowPanel(); };
        if (SkipBriefing) Begin(); else ShowPanel();
    }

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
        // Crates.
        for (int c = 0; c < 5; c++)
        {
            float a = _rng.Randf() * MathF.Tau, r = _rng.RandfRange(3.5f, 6.5f);
            var at = Ground(Camp + new Vector3(MathF.Cos(a), 0, MathF.Sin(a)) * r, .4f);
            Prop(camp, new BoxMesh { Size = new Vector3(.8f, .8f, .8f) }, crate, at, _rng.Randf(), new Vector3(.8f, .8f, .8f));
            if (_rng.Randf() < .5f) Prop(camp, new BoxMesh { Size = new Vector3(.7f, .7f, .7f) }, crate, at + Vector3.Up * .75f, _rng.Randf(), new Vector3(.7f, .7f, .7f));
        }
        // The radio desk with the intel on it.
        var desk = _desk;
        Prop(camp, new BoxMesh { Size = new Vector3(1.6f, .8f, .8f) }, wood, desk, .3f, new Vector3(1.6f, .8f, .8f));
        var radio = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(.45f, .28f, .3f) }, MaterialOverride = olive };
        camp.AddChild(radio);
        radio.GlobalPosition = desk + new Vector3(-.35f, .54f, 0);
        var antenna = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = .005f, BottomRadius = .008f, Height = 1.1f }, MaterialOverride = Mat(.1f, .1f, .1f) };
        radio.AddChild(antenna);
        antenna.Position = new Vector3(.15f, .65f, 0);
        _intelProp = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(.24f, .05f, .32f) }, MaterialOverride = Mat(.55f, .12f, .08f) };
        camp.AddChild(_intelProp);
        _intelProp.GlobalPosition = IntelAt;

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

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        _fireLight.LightEnergy = 2f + MathF.Sin((float)Time.GetTicksMsec() * .013f) * .3f + MathF.Sin((float)Time.GetTicksMsec() * .031f) * .2f;
        if (State is Phase.Intel or Phase.Extract) Elapsed += delta;

        if (State == Phase.Intel)
        {
            bool near = Player.GlobalPosition.DistanceTo(IntelAt) < 2.4f && !Player.Dead;
            bool holding = near && Input.IsPhysicalKeyPressed(Key.F);
            SearchProgress = holding ? Math.Min(1, SearchProgress + dt / SearchTime) : Math.Max(0, SearchProgress - dt);
            if (SearchProgress >= 1) TakeIntel();
        }
        else if (State == Phase.Extract && Flat(Player.GlobalPosition - Extraction) < ExtractRadius && !Player.Dead)
        {
            State = Phase.Complete;
            GD.Print($"[mission] complete in {Elapsed:0} s");
            Completed?.Invoke();
            DeadRecon.Gear.Kit.Current?.RemoveIntel();   // handed over at the landing zone
            ShowPanel();
        }

        var target = State == Phase.Intel ? IntelAt : Extraction;
        _marker.Visible = State is Phase.Intel or Phase.Extract;
        _marker.GlobalPosition = target + Vector3.Up * (State == Phase.Intel ? 1.4f : 4f);
        _marker.Text = $"{(State == Phase.Intel ? "INTEL" : "EXTRACT")}  {Player.GlobalPosition.DistanceTo(target):0} m";
        _marker.PixelSize = .0008f * Player.Camera.Fov / 70f; // same size through the scope
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (State == Phase.Briefing && (e is InputEventKey { Pressed: true } || e is InputEventMouseButton { Pressed: true })) Begin();
    }

    private void TakeIntel()
    {
        State = Phase.Extract;
        SearchProgress = 0;
        _intelProp.Visible = false;
        DeadRecon.Gear.Kit.Current?.AddIntel(IntelName);
        GD.Print($"[mission] took {IntelName}");
        ShowPanel();
    }

    /// <summary>For tests: take the intel right now.</summary>
    public void TestTakeIntel() { if (State == Phase.Intel) TakeIntel(); }

    /// <summary>One line for the HUD.</summary>
    public string Objective()
    {
        var target = State == Phase.Intel ? IntelAt : Extraction;
        var d = target - Player.GlobalPosition;
        string dir = Compass(d);
        return State switch
        {
            Phase.Briefing => $"{OperationName}: read the briefing (any key)",
            Phase.Intel => $"OBJECTIVE: take {IntelName} from the camp radio desk   {d.Length():0} m {dir}" +
                           (SearchProgress > 0 ? $"   searching... {SearchProgress * 100:0}%" :
                            Player.GlobalPosition.DistanceTo(IntelAt) < 2.4f ? "   hold F to take it" : ""),
            Phase.Extract => $"OBJECTIVE: reach extraction (green smoke)   {Flat(d):0} m {dir}",
            Phase.Complete => $"{OperationName}: COMPLETE",
            _ => $"{OperationName}: FAILED",
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
            _panel.Text =
                $"{OperationName.ToUpper()}\n\n" +
                $"Objective: take {IntelName} from the radio desk at a small enemy camp, about {campDist:0} m {Compass(Camp - Player.GlobalPosition)} of you. {_why}\n\n" +
                $"Then get to the landing zone. Look for the green smoke.\n\n" +
                $"Insertion at {(int)StartHour:00}:{(int)(StartHour % 1 * 60):00} ({TimeOfDay.Describe(StartHour)}). " +
                (StartHour < 5.5f || StartHour > 19.3f ? "It's dark: they won't see you far, but you won't see much either.\n\n" : "\n\n") +
                (Guards.Count <= 2 ? "Intel says one guard on patrol and one sentry by the radio. Both are reported to be in a bad mood.\n\n"
                                   : $"Intel says {Guards.Count} guards: a sentry by the radio and {Guards.Count - 1} on patrol. All of them are reported to be in a bad mood.\n\n") +
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
            _panel.Text =
                $"{OperationName.ToUpper()}: COMPLETE\n\n" +
                $"You got {IntelName} out.\n\n" +
                $"Time: {(int)Elapsed / 60}:{(int)Elapsed % 60:00}    Shots fired: {Rifle.ShotsFired}    Guards down: {down}/{Guards.Count}\n" +
                $"Times spotted: {alerts}    Trees felled: {trees}{(trees > 0 ? " (the forestry department has questions)" : "")}\n\n" +
                $"{verdict}\n\n" +
                "Press Enter for a new operation.";
        }
        else if (State == Phase.Failed)
        {
            _panel.Text =
                $"{OperationName.ToUpper()}: FAILED\n\n" +
                "You were shot by a grumpy man.\n\n" +
                "The jungle stays where it is. The camp won't next time.\n\n" +
                "Press Enter for a new operation.";
        }
    }
}
