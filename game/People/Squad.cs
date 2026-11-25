using System;
using System.Collections.Generic;
using System.Linq;
using DeadRecon.Base;
using DeadRecon.Gear;
using DeadRecon.Player;
using DeadRecon.UI;
using DeadRecon.World;
using Godot;

namespace DeadRecon.People;

/// <summary>
/// Your people: everyone you've rescued lives at home (HomeBase.State.Soldiers). Those you picked at the radio
/// (up to 3) come with you on the next operation; the rest stay and do their jobs.
/// <para>Orders: F1, F2, F3 pick one of your squad, F4 picks all of them; then 1 follow, 2 hold here, 3 go where
/// I'm looking, 4 weapons free / hold fire. (No pick: orders go to everyone.)</para>
/// </summary>
public partial class Squad : Node
{
    public static Squad? Current { get; private set; }
    public const int Max = 3;

    public Walker Player = null!;
    public BakedWorld World = null!;
    public bool OnOperation;

    public readonly List<Comrade> Team = new();
    public readonly List<Comrade> AtHome = new();
    private readonly HashSet<Comrade> _picked = new();
    private float _pickShown;

    public override void _Ready()
    {
        Current = this;
        Comrade.All.Clear();
        CallDeferred(nameof(Muster));
    }

    public override void _ExitTree() { if (Current == this) Current = null; }

    public static IEnumerable<HomeBase.Soldier> Roster => HomeBase.Saved?.Soldiers.Where(s => s.Alive) ?? Enumerable.Empty<HomeBase.Soldier>();

    /// <summary>Bring everyone into the world: the squad by you (on an operation), the rest at home at their jobs.</summary>
    public void Muster()
    {
        if (HomeBase.Saved is not { } home) return;
        int slot = 0;
        foreach (var s in Roster)
        {
            bool going = OnOperation && s.Squad && slot < Max;
            var c = new Comrade
            {
                Person = s.Name, CharacterScene = s.Character, WeaponId = s.Weapon, Task = s.Task, Player = Player, World = World,
                Slot = going ? slot : 0, Name = $"Comrade_{s.Name.Replace(' ', '_')}",
            };
            c.Post = PostFor(s, AtHome.Count);
            AddChild(c);
            if (going)
            {
                var start = Player.GlobalPosition + Player.GlobalTransform.Basis * new Vector3(-2 + slot * 2, 0, 3);
                c.GlobalPosition = start with { Y = World.HeightAt(start.X, start.Z) + .3f };
                c.Command(Comrade.Order.Follow);
                Team.Add(c);
                slot++;
            }
            else
            {
                c.GlobalPosition = c.Post with { Y = World.HeightAt(c.Post.X, c.Post.Z) + .3f };
                AtHome.Add(c);
            }
            var rec = s;
            c.Died += _ => { rec.Alive = false; HomeBase.SaveNow(); };
        }
        if (Team.Count > 0) GD.Print($"[squad] with you: {string.Join(", ", Team.Select(t => t.Person))}");
    }

    /// <summary>Where his job is at home.</summary>
    public static Vector3 PostFor(HomeBase.Soldier s, int index)
    {
        var home = HomeBase.Saved!;
        var basis = new Basis(Vector3.Up, home.Yaw);
        var c = new Vector3(home.X, 0, home.Z);
        Vector3 local = s.Task switch
        {
            "guard" => new[] { new Vector3(0, 0, HomeBase.Radius - 2), new Vector3(HomeBase.Radius - 2, 0, 0), new Vector3(-HomeBase.Radius + 2, 0, 0) }[index % 3],
            "build" when HomeBase.NextSite() is { } site => site - c,
            _ => new Vector3(-3, 0, 2) + new Vector3(MathF.Cos(index * 2.1f), 0, MathF.Sin(index * 2.1f)) * 1.8f,   // round the fire
        };
        return c + (s.Task == "build" ? local : basis * local);
    }

    /// <summary>The operation's over (you're home): your squad goes back to their jobs.</summary>
    public void StandDown()
    {
        foreach (var c in Team.ToList()) if (!c.Dead && !c.Down) { c.Command(Comrade.Order.Base); c.Post = PostFor(HomeBase.Saved!.Soldiers.First(s => s.Name == c.Person), AtHome.Count); AtHome.Add(c); }
        Team.Clear();
    }

    // ================================================================= orders

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is not InputEventKey { Pressed: true, Echo: false } k || Team.Count == 0 || Input.MouseMode != Input.MouseModeEnum.Captured) return;
        var alive = Team.Where(c => !c.Dead).ToList();
        switch (k.PhysicalKeycode)
        {
            case Key.F1: case Key.F2: case Key.F3:
            {
                int i = (int)(k.PhysicalKeycode - Key.F1);
                if (i < Team.Count) { _picked.Clear(); _picked.Add(Team[i]); _pickShown = 4; }
                break;
            }
            case Key.F4: _picked.Clear(); foreach (var c in alive) _picked.Add(c); _pickShown = 4; break;
            case Key.Key1: case Key.Key2: case Key.Key3: case Key.Key4:
            {
                if (_pickShown <= 0) return;   // number keys only mean orders right after picking someone
                var who = _picked.Count > 0 ? _picked.Where(c => !c.Dead).ToList() : alive;
                if (k.PhysicalKeycode == Key.Key1) foreach (var c in who) c.Command(Comrade.Order.Follow);
                if (k.PhysicalKeycode == Key.Key2) foreach (var c in who) c.Command(Comrade.Order.Hold);
                if (k.PhysicalKeycode == Key.Key3 && LookPoint() is { } p)
                    for (int n = 0; n < who.Count; n++) who[n].Command(Comrade.Order.Move, p + new Vector3((n - (who.Count - 1) / 2f) * 2.2f, 0, 0));
                if (k.PhysicalKeycode == Key.Key4) { bool free = !who.All(c => c.FireAtWill); foreach (var c in who) c.SetFire(free); }
                _pickShown = 0;
                break;
            }
            default: return;
        }
        GetViewport().SetInputAsHandled();
    }

    /// <summary>The ground where you're looking (up to 120 m).</summary>
    private Vector3? LookPoint()
    {
        var cam = Player.Camera.GlobalTransform;
        var q = PhysicsRayQueryParameters3D.Create(cam.Origin, cam.Origin - cam.Basis.Z * 120);
        q.Exclude = new Godot.Collections.Array<Rid> { Player.GetRid() };
        var hit = Player.GetWorld3D().DirectSpaceState.IntersectRay(q);
        return hit.Count > 0 ? (Vector3)hit["position"] : null;
    }

    public override void _Process(double delta) => _pickShown = Math.Max(0, _pickShown - (float)delta);

    /// <summary>For the HUD: the squad line by line, and the order menu while someone's picked.</summary>
    public IEnumerable<(string Key, Comrade C, bool Picked)> Status => Team.Select((c, i) => ($"F{i + 1}", c, _picked.Contains(c) && _pickShown > 0));
    public bool Picking => _pickShown > 0;
}
