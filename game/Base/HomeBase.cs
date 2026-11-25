using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using DeadRecon.Gear;
using DeadRecon.Player;
using DeadRecon.UI;
using DeadRecon.World;
using Godot;

namespace DeadRecon.Base;

/// <summary>
/// Home: wherever you set up the field radio. Remembered between sessions (user://base.json). At the radio (F):
/// operations, building and the radio log (<see cref="RadioScreen"/>). Things you can build with building supplies:
/// <list type="bullet">
/// <item><b>Shelter</b>: a tarp lean-to with a hammock. Sleep (F, at night) until morning; it's where you start.</item>
/// <item><b>Supply crate</b>: refills your magazines and dressings (F), once per operation.</item>
/// <item><b>Sandbag walls</b>: a ring round the camp with a gap to walk through.</item>
/// <item><b>Watchtower</b>: see over the jungle.</item>
/// </list>
/// Trees inside the camp are cleared the next time the jungle is planted (choosing an operation does that).
/// </summary>
public partial class HomeBase : Node3D
{
    public static HomeBase? Current { get; private set; }
    public static bool Persist { get; set; } = true;
    public const float Radius = 11;

    public Walker Player = null!;
    public BakedWorld World = null!;

    public sealed record Buildable(string Id, string Name, string What, int Cost, string? Needs = null);
    public static readonly Buildable[] Catalogue =
    {
        new("shelter", "Shelter", "A tarp, a hammock, a place to sleep. Sleep through the night (F); you start here.", 6),
        new("crate", "Supply crate", "Refills your magazines and dressings (F), once per operation.", 8),
        new("walls", "Sandbag walls", "A waist-high ring of sandbags round the camp, with a way in.", 14, "shelter"),
        new("tower", "Watchtower", "Five metres up: see over the ferns, and see them coming.", 10, "walls"),
    };

    /// <summary>The saved state: where home is, what's built, how many operations you've done.</summary>
    /// <summary>Someone you rescued: lives at home now.</summary>
    public sealed class Soldier
    {
        public string Name { get; set; } = "";
        public string Character { get; set; } = "";
        public string Weapon { get; set; } = DeadRecon.Gear.WeaponDef.Default;
        /// <summary>rest, guard, build.</summary>
        public string Task { get; set; } = "rest";
        /// <summary>Comes with you on the next operation.</summary>
        public bool Squad { get; set; }
        public bool Alive { get; set; } = true;
    }

    public sealed class State
    {
        public List<Soldier> Soldiers { get; set; } = new();
        public List<Structures.Placed> Structures { get; set; } = new();
        public int Stores { get; set; }
        /// <summary>Guns you've brought home (handed out to your soldiers at the radio).</summary>
        public List<string> Armory { get; set; } = new() { DeadRecon.Gear.WeaponDef.Default };
        public float X { get; set; }
        public float Z { get; set; }
        public float Yaw { get; set; }
        public List<string> Built { get; set; } = new();
        public int Operations { get; set; }
        public bool CrateUsed { get; set; }
    }

    private static State? _state;
    private const string File = "user://base.json";

    /// <summary>Where home is, if you have one (loaded on first use).</summary>
    public static State? Saved { get { if (_state == null && Persist) Load(); return _state; } }
    public static Vector3? Position => Saved is { } s ? new Vector3(s.X, 0, s.Z) : null;
    public static bool Has(string id) => Saved?.Built.Contains(id) == true;

    public Vector3 Radio { get; private set; }
    private Node3D _built = new();
    private bool _fWas;
    private float _chatterIn = 6;
    private AudioStreamPlayer3D? _speaker;

    public override void _Ready()
    {
        Current = this;
        Rebuild();
    }

    public override void _ExitTree() { if (Current == this) Current = null; }

    /// <summary>Set up the radio here: this is home now (moves home if you already had one).</summary>
    public static string? Establish(Vector3 at, float yaw, BakedWorld world)
    {
        if (Settlements.Current?.SiteAt(at.X, at.Z, 1.6f) is { } site) return $"Not in {site.Name}. The villagers would rather you didn't.";
        if (DeadRecon.Missions.Mission.Current is { } m && m.Camp.DistanceTo(at) < 250 && m.Kind != DeadRecon.Missions.Mission.Op.None)
            return "Too close to their camp. Find somewhere quieter (250 m away at least).";
        var (gx, gz) = world.ToSample(at.X, at.Z);
        if (world.NearWater((int)gx, (int)gz, 3)) return "Too wet here. Somewhere drier.";
        var old = _state;
        _state = new State { X = at.X, Z = at.Z, Yaw = yaw, Built = old?.Built ?? new(), Operations = old?.Operations ?? 0, Soldiers = old?.Soldiers ?? new(), Structures = new(), Armory = old?.Armory ?? new() { DeadRecon.Gear.WeaponDef.Default } };
        Save();
        Current?.Rebuild();
        TreeScatter.Current?.ClearArea(new Vector2(at.X, at.Z), Radius + 4);
        return null;
    }

    public static void Build(string id)
    {
        if (_state == null || _state.Built.Contains(id)) return;
        _state.Built.Add(id);
        Save();
        Current?.Rebuild();
    }

    public static void SaveNow() => Save();

    /// <summary>A rescued soldier moves in. Joins the squad if there's room.</summary>
    public static void AddSoldier(string name, string character)
    {
        if (_state == null || _state.Soldiers.Any(s => s.Name == name && s.Alive)) return;
        int going = _state.Soldiers.Count(s => s.Alive && s.Squad);
        _state.Soldiers.Add(new Soldier { Name = name, Character = character, Squad = going < 3 });
        Save();
    }

    public static void AddToArmory(string weapon)
    {
        if (_state == null || _state.Armory.Contains(weapon)) return;
        _state.Armory.Add(weapon);
        Save();
    }

    /// <summary>Where builders should be working (plan mode's next unfinished blueprint), if anywhere.</summary>
    public static Vector3? NextSite() => _nextSite?.Invoke();
    public static Func<Vector3?>? _nextSite;

    public static void OperationDone()
    {
        if (_state == null) return;
        _state.Operations++;
        _state.CrateUsed = false;
        Save();
    }

    /// <summary>Tests: forget home (never touches the player's save: tests don't persist).</summary>
    public static void TestReset() { _state = null; Current?.Rebuild(); }

    // ================================================================= building it

    private StandardMaterial3D M(Color c, float rough = .9f) => new() { AlbedoColor = c, Roughness = rough };

    private void Rebuild()
    {
        _built.QueueFree();
        _built = new Node3D();
        Ladders.RemoveOwner(this);
        AddChild(_built);
        if (Saved is not { } s) { Visible = false; return; }
        Visible = true;
        var c = new Vector3(s.X, World.HeightAt(s.X, s.Z), s.Z);
        var basis = new Basis(Vector3.Up, s.Yaw);
        Vector3 At(float x, float z, float lift = 0) { var p = c + basis * new Vector3(x, 0, z); return p with { Y = World.HeightAt(p.X, p.Z) + lift }; }
        var wood = M(new Color(.36f, .27f, .17f));
        var olive = M(new Color(.28f, .31f, .19f), .6f);
        var canvas = M(new Color(.30f, .34f, .22f));
        var sand = M(new Color(.52f, .48f, .36f), .97f);

        // The radio on a crate-table, a stool, and a little fire.
        var table = At(0, 0, .4f);
        Box(wood, new Vector3(1.2f, .8f, .7f), table, s.Yaw);
        Radio = table + Vector3.Up * .55f;
        var radio = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(.45f, .28f, .3f) }, MaterialOverride = olive };
        _built.AddChild(radio);
        radio.GlobalPosition = Radio;
        radio.Rotation = new Vector3(0, s.Yaw, 0);
        var antenna = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = .005f, BottomRadius = .008f, Height = 1.3f }, MaterialOverride = M(new Color(.1f, .1f, .1f)) };
        radio.AddChild(antenna);
        antenna.Position = new Vector3(.15f, .75f, 0);
        _lamp = new OmniLight3D { LightColor = new Color(.6f, 1f, .5f), LightEnergy = .25f, OmniRange = 1.2f };
        radio.AddChild(_lamp);
        _lamp.Position = new Vector3(-.1f, .1f, .16f);
        Box(wood, new Vector3(.45f, .45f, .45f), At(0, 1.2f, .22f), s.Yaw + .4f);
        var fire = At(-3, 2);
        for (int l = 0; l < 3; l++)
        {
            var log = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = .06f, BottomRadius = .07f, Height = .8f }, MaterialOverride = M(new Color(.18f, .12f, .08f)) };
            _built.AddChild(log);
            log.GlobalPosition = fire + Vector3.Up * .1f;
            log.Rotation = new Vector3(MathF.PI / 2 - .4f, l * MathF.Tau / 3, 0);
        }
        _fire = new OmniLight3D { LightColor = new Color(1, .55f, .25f), LightEnergy = 1.6f, OmniRange = 8, ShadowEnabled = true };
        _built.AddChild(_fire);
        _fire.GlobalPosition = fire + Vector3.Up * .5f;

        if (s.Built.Contains("shelter"))
        {
            // Lean-to: two posts, a ridge pole, a sloping tarp, a hammock under it.
            var front = At(4, -1); var back = At(4, -4);
            foreach (var x in new[] { 2.6f, 5.4f })
            {
                Box(wood, new Vector3(.12f, 2.2f, .12f), At(x, -1, 1.1f), s.Yaw);
                Box(wood, new Vector3(.12f, .9f, .12f), At(x, -4, .45f), s.Yaw);
            }
            var tarp = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(3.4f, .03f, 3.4f) }, MaterialOverride = canvas };
            _built.AddChild(tarp);
            tarp.GlobalPosition = (At(4, -1, 2.2f) + At(4, -4, .9f)) / 2;
            tarp.Rotation = new Vector3(0, s.Yaw, 0);
            tarp.RotateObjectLocal(Vector3.Right, -MathF.Atan2(1.3f, 3));
            var hammock = new MeshInstance3D { Mesh = new CapsuleMesh { Radius = .28f, Height = 2.2f }, MaterialOverride = M(new Color(.55f, .45f, .3f)) };
            _built.AddChild(hammock);
            hammock.GlobalPosition = At(4, -2.3f, .9f);
            hammock.Rotation = new Vector3(0, s.Yaw + MathF.PI / 2, MathF.PI / 2);
            Shelter = At(4, -2.3f);
        }
        else Shelter = null;
        if (s.Built.Contains("crate"))
        {
            Crate = At(-2.5f, -2.5f);
            Box(wood, new Vector3(1.1f, .7f, .7f), Crate.Value + Vector3.Up * .35f, s.Yaw + .2f);
            Box(olive, new Vector3(.9f, .5f, .5f), Crate.Value + Vector3.Up * .95f, s.Yaw + .25f);
        }
        else Crate = null;
        if (s.Built.Contains("walls"))
        {
            // A ring of sandbags with a gap facing +Z (the way the radio faces).
            for (int i = 0; i < 56; i++)
            {
                float a = i / 56f * MathF.Tau;
                if (Math.Abs(Mathf.Wrap(a - MathF.PI / 2, -MathF.PI, MathF.PI)) < .28f) continue;
                var p = At(MathF.Cos(a) * Radius, MathF.Sin(a) * Radius);
                for (int layer = 0; layer < 3; layer++)
                    Box(sand, new Vector3(.36f, .24f, 1.2f), p + Vector3.Up * (.12f + layer * .23f), s.Yaw - a + (layer % 2) * .05f, solid: layer == 0, solidHeight: .75f);
            }
        }
        if (s.Built.Contains("tower"))
        {
            var t = At(-Radius + 3, -Radius + 3);
            float deck = 5, half = 1.2f, top = t.Y + deck;
            foreach (var (x, z) in new[] { (-half, -half), (half, -half), (-half, half), (half, half) })
            {
                var leg = t + basis * new Vector3(x, 0, z);
                float g = World.HeightAt(leg.X, leg.Z) - .3f;
                Box(wood, new Vector3(.18f, top - g + 1, .18f), new Vector3(leg.X, (g + top + 1) / 2, leg.Z), s.Yaw);
            }
            Box(wood, new Vector3(half * 2 + .3f, .12f, half * 2 + .3f), new Vector3(t.X, top, t.Z), s.Yaw);
            foreach (var (x, z, sx, sz) in new[] { (0f, -half, half * 2, .06f), (0f, half, half * 2, .06f), (-half, 0f, .06f, half * 2) })
            {
                var p = t + basis * new Vector3(x, 0, z);
                Box(wood, new Vector3(sx, 1, sz), new Vector3(p.X, top + .5f, p.Z), s.Yaw);
            }
            // A ladder up the open side (climbable).
            Ladders.Add(new Vector3((t + basis * new Vector3(half + .3f, 0, 0)).X, World.HeightAt((t + basis * new Vector3(half + .3f, 0, 0)).X, (t + basis * new Vector3(half + .3f, 0, 0)).Z), (t + basis * new Vector3(half + .3f, 0, 0)).Z),
                        new Vector3((t + basis * new Vector3(half + .3f, 0, 0)).X, top + .06f, (t + basis * new Vector3(half + .3f, 0, 0)).Z), basis * Vector3.Right, this);
            // The climbable ladder itself: solid, so you can't walk through it.
            var lb = t + basis * new Vector3(half + .25f, 0, 0);
            var ladderBody = new StaticBody3D();
            ladderBody.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(.08f, top - World.HeightAt(lb.X, lb.Z), .6f) } });
            _built.AddChild(ladderBody);
            ladderBody.GlobalPosition = new Vector3(lb.X, (top + World.HeightAt(lb.X, lb.Z)) / 2, lb.Z);
            ladderBody.Rotation = new Vector3(0, s.Yaw, 0);
            for (int r = 0; r < 14; r++)
                Box(wood, new Vector3(.06f, .04f, .55f), new Vector3(lb.X, World.HeightAt(lb.X, lb.Z) + .3f + r * (top - World.HeightAt(lb.X, lb.Z)) / 14f, lb.Z), s.Yaw, solid: false);
        }
    }

    private OmniLight3D? _fire, _lamp;
    public Vector3? Shelter { get; private set; }
    public Vector3? Crate { get; private set; }

    private void Box(Material m, Vector3 size, Vector3 at, float yaw, bool solid = true, float solidHeight = -1)
    {
        var mesh = new MeshInstance3D { Mesh = new BoxMesh { Size = size }, MaterialOverride = m };
        if (!solid) { _built.AddChild(mesh); mesh.GlobalPosition = at; mesh.Rotation = new Vector3(0, yaw, 0); return; }
        var body = new StaticBody3D();
        body.AddChild(mesh);
        var box = solidHeight > 0 ? size with { Y = solidHeight } : size;
        body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = box }, Position = new Vector3(0, (box.Y - size.Y) / 2, 0) });
        _built.AddChild(body);
        body.GlobalPosition = at;
        body.Rotation = new Vector3(0, yaw, 0);
    }

    // ================================================================= being there

    public override void _Process(double delta)
    {
        if (Saved == null) return;
        float t = Time.GetTicksMsec() / 1000f;
        if (_fire != null) _fire.LightEnergy = (.6f + 1.4f * (TimeOfDay.Current?.Darkness ?? 0)) * (.85f + .1f * MathF.Sin(t * 13) + .05f * MathF.Sin(t * 31));
        if (_lamp != null) _lamp.LightEnergy = MathF.Sin(t * 2) > .6f ? .5f : .2f;
        // The radio talks when you're near it.
        _chatterIn -= (float)delta;
        if (_chatterIn <= 0 && Player.GlobalPosition.DistanceTo(Radio) < 30)
        {
            _chatterIn = (float)GD.RandRange(35, 80);
            var (who, text) = Chatter.Next(Radio);
            Chatter.Add(who, text);
            Hud.Toast($"RADIO · {who}: {text}");
            if (_speaker == null) { _speaker = new AudioStreamPlayer3D { UnitSize = 4, MaxDistance = 35, VolumeDb = -4, Bus = "Voices" }; AddChild(_speaker); }
            _speaker.GlobalPosition = Radio;
            _speaker.Stream = DeadRecon.Audio.Synth.Voice(text, who == "HQ" ? 92 : 120, who.GetHashCode());
            _speaker.Play();
        }
        bool f = Input.IsPhysicalKeyPressed(Key.F), fPressed = f && !_fWas && Input.MouseMode == Input.MouseModeEnum.Captured;
        _fWas = f;
        if (Player.Dead) return;
        var p = Player.GlobalPosition;
        if (p.DistanceTo(Radio) < 2.2f)
        {
            Hud.Prompt("F   Use the radio");
            if (fPressed) RadioScreen.Current?.Open();
        }
        else if ((Crate is { } c0 && p.DistanceTo(c0) < 2.2f ? c0 : Structures.Nearest("crate", p, 2.2f)) is { } crate)
        {
            Hud.Prompt(Saved.CrateUsed ? "Supply crate: empty until your next operation" : "F   Restock magazines and dressings");
            if (fPressed && !Saved.CrateUsed && Kit.Current is { } kit)
            {
                int mags = 0;
                foreach (var m in kit.AllItems.Where(i => i.Def.Kind == ItemKind.Magazine)) { if (m.Rounds < m.Def.Capacity) mags++; m.Rounds = m.Def.Capacity; }
                kit.TestTopUp();
                while (kit.Magazines < 6 && kit.Add(ItemDef.Mag20, 1, quiet: true)) mags++;
                int dressings = 0;
                while (kit.Bandages < 4 && kit.Add(ItemDef.Bandage, 1, quiet: true)) dressings++;
                Saved.CrateUsed = true;
                Save();
                Hud.Toast($"Restocked: {mags} magazines filled, {dressings} dressings");
            }
        }
        else if ((Shelter is { } s0 && p.DistanceTo(s0) < 2.4f ? s0 : Structures.Nearest("shelter", p, 2.6f)) is { } shelter)
        {
            bool night = (TimeOfDay.Current?.Darkness ?? 0) > .5f;
            Hud.Prompt(night ? "F   Sleep until morning" : "Shelter (you can sleep here at night)");
            if (fPressed && night && TimeOfDay.Current is { } time)
            {
                time.Hour = 6.3f;
                Player.Heal(Walker.MaxHealth);
                Hud.Announce("Morning", "you slept like a log. A damp log.");
            }
        }
    }

    // ================================================================= saving

    private static void Save()
    {
        if (!Persist || _state == null) return;
        try
        {
            using var f = FileAccess.Open(File, FileAccess.ModeFlags.Write);
            f.StoreString(JsonSerializer.Serialize(_state));
        }
        catch (Exception e) { GD.PushWarning($"Couldn't save the base: {e.Message}"); }
    }

    private static void Load()
    {
        if (!FileAccess.FileExists(File)) return;
        try
        {
            using var f = FileAccess.Open(File, FileAccess.ModeFlags.Read);
            _state = JsonSerializer.Deserialize<State>(f.GetAsText());
        }
        catch (Exception e) { GD.PushWarning($"Couldn't read the base: {e.Message}"); }
    }
}
