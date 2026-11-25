using System;
using System.Collections.Generic;
using System.Linq;
using DeadRecon.Gear;
using DeadRecon.Missions;
using DeadRecon.Player;
using DeadRecon.UI;
using DeadRecon.World;
using Godot;

namespace DeadRecon.Base;

/// <summary>
/// The radio at home (F): three pages.
/// <list type="bullet">
/// <item><b>Operations</b>: three generated operations to choose from (a new set after each one you finish). Picking
/// one plants the jungle again with its camp and starts you at home.</item>
/// <item><b>Build</b>: spend building supplies on the shelter, supply crate, walls and watchtower.</item>
/// <item><b>Radio log</b>: what's come over the air lately.</item>
/// </list>
/// </summary>
public partial class RadioScreen : Control
{
    public static RadioScreen? Current { get; private set; }
    public static bool IsOpen { get; private set; }
    public Walker Player = null!;
    /// <summary>Start an operation (Main reloads the jungle).</summary>
    public Action<Mission.Op, int> StartOperation = null!;

    private const float RefW = 1600, RefH = 900;
    private float _scale = 1;
    private Vector2 _offset, _mouse;
    private int _page;
    private readonly List<(Rect2 Rect, Action Do)> _buttons = new();
    private string _message = "";
    private float _messageAge = 99;

    public override void _Ready()
    {
        Current = this;
        ProcessMode = ProcessModeEnum.Always;
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
        Visible = false;
    }

    public override void _ExitTree() { if (Current == this) { Current = null; IsOpen = false; } }

    public void Open(int page = 0)
    {
        _page = page;
        Visible = IsOpen = true;
        GetTree().Paused = true;
        Input.MouseMode = Input.MouseModeEnum.Visible;
        QueueRedraw();
    }

    public void Close()
    {
        Visible = IsOpen = false;
        GetTree().Paused = false;
        Input.MouseMode = Input.MouseModeEnum.Captured;
        Player.FireLockedUntilRelease = true;
    }

    /// <summary>This set of offers: three operations, from how many you've done (same set until you finish one).</summary>
    public static List<(Mission.Op Kind, int Seed)> Offers()
    {
        int done = HomeBase.Saved?.Operations ?? 0;
        var rng = new RandomNumberGenerator { Seed = (ulong)(done * 7919 + (int)(HomeBase.Saved?.X ?? 0) * 13 + 101) };
        var kinds = new List<Mission.Op> { Mission.Op.Intel, Mission.Op.Cache, Mission.Op.Officer, Mission.Op.Prisoner };
        var list = new List<(Mission.Op, int)>();
        for (int k = 0; k < 3; k++)
        {
            var kind = kinds[rng.RandiRange(0, kinds.Count - 1)];
            kinds.Remove(kind);
            list.Add((kind, (int)(rng.Randi() % 2000000000)));
        }
        return list;
    }

    public override void _Input(InputEvent e)
    {
        if (!IsOpen) return;
        if (e is InputEventKey { Pressed: true, Echo: false } k)
        {
            if (k.PhysicalKeycode is Key.Escape or Key.Tab) Close();
            if (k.PhysicalKeycode == Key.Key1) _page = 0;
            if (k.PhysicalKeycode == Key.Key2) _page = 1;
            if (k.PhysicalKeycode == Key.Key3) _page = 2;
            if (k.PhysicalKeycode == Key.Key4) _page = 3;
            QueueRedraw();
            GetViewport().SetInputAsHandled();
            return;
        }
        if (e is InputEventMouse m)
        {
            _mouse = (m.Position - _offset) / _scale;
            if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true })
                foreach (var (rect, act) in _buttons.ToList()) if (rect.HasPoint(_mouse)) { act(); break; }
            QueueRedraw();
            GetViewport().SetInputAsHandled();
        }
    }

    public override void _Process(double delta)
    {
        if (!IsOpen) return;
        _messageAge += (float)delta;
        var view = GetViewportRect().Size;
        _scale = Math.Min(view.X / RefW, view.Y / RefH);
        _offset = (view - new Vector2(RefW, RefH) * _scale) / 2;
        QueueRedraw();
    }

    private void Say(string m) { _message = m; _messageAge = 0; }

    private void Button(Rect2 r, string label, Action act, bool enabled = true, bool primary = false)
    {
        bool hover = enabled && r.HasPoint(_mouse);
        DrawRect(r, !enabled ? new Color(1, 1, 1, .04f) : primary ? new Color(UiStyle.Olive, hover ? .95f : .7f) : hover ? new Color(1, 1, 1, .16f) : new Color(1, 1, 1, .08f));
        DrawRect(r, new Color(1, 1, 1, enabled ? .25f : .08f), false, 1);
        UiStyle.Text(this, r.Position + new Vector2(0, r.Size.Y / 2 + 6), label, 15, enabled ? UiStyle.Ink : UiStyle.Faint, HorizontalAlignment.Center, r.Size.X, bold: primary);
        if (enabled) _buttons.Add((r, act));
    }

    public override void _Draw()
    {
        if (!IsOpen) return;
        _buttons.Clear();
        DrawRect(new Rect2(Vector2.Zero, GetViewportRect().Size), new Color(.02f, .03f, .02f, .84f));
        DrawSetTransform(_offset, 0, Vector2.One * _scale);
        UiStyle.Text(this, new Vector2(70, 78), "FIELD RADIO", 34, UiStyle.Ink, bold: true);
        string[] tabs = { "1   OPERATIONS", "2   BUILD", "3   RADIO LOG", "4   SOLDIERS" };
        for (int i = 0; i < 4; i++)
        {
            var r = new Rect2(330 + i * 200, 50, 190, 38);
            bool on = _page == i;
            DrawRect(r, on ? new Color(UiStyle.Olive, .55f) : r.HasPoint(_mouse) ? UiStyle.Faint : new Color(0, 0, 0, 0));
            if (on) DrawRect(new Rect2(r.Position + new Vector2(0, 35), new Vector2(r.Size.X, 3)), UiStyle.Amber);
            UiStyle.Text(this, r.Position + new Vector2(0, 25), tabs[i], 16, on ? UiStyle.Ink : UiStyle.Dim, HorizontalAlignment.Center, r.Size.X, bold: on);
            int page = i;
            _buttons.Add((r, () => _page = page));
        }
        int supplies = Kit.Current?.AllItems.Where(it => it.Def == ItemDef.Supplies).Sum(it => it.Count) ?? 0;
        UiStyle.Text(this, new Vector2(930, 78), $"BUILDING SUPPLIES: {supplies}   ·   OPERATIONS DONE: {HomeBase.Saved?.Operations ?? 0}", 14, UiStyle.Dim, HorizontalAlignment.Right, 600);
        DrawLine(new Vector2(70, 108), new Vector2(1530, 108), UiStyle.Faint, 1);
        if (_page == 0) DrawOperations(); else if (_page == 1) DrawBuild(supplies); else if (_page == 2) DrawLog(); else DrawSoldiers();
        if (_messageAge < 3.5f) UiStyle.Text(this, new Vector2(0, 820), _message, 17, new Color(UiStyle.Amber, Math.Clamp(3.5f - _messageAge, 0, 1)), HorizontalAlignment.Center, RefW);
        UiStyle.Text(this, new Vector2(70, 868), "Click to choose  ·  1-4: pages  ·  Tab or Esc: close", 13, UiStyle.Dim);
        DrawSetTransform(Vector2.Zero, 0, Vector2.One);
    }

    private void DrawOperations()
    {
        var current = Mission.Current;
        bool busy = current != null && current.Kind != Mission.Op.None && current.State is Mission.Phase.Intel or Mission.Phase.Extract or Mission.Phase.Briefing;
        UiStyle.Text(this, new Vector2(70, 150), busy ? $"You're on {current!.OperationName}. Picking another calls it off." : "HQ has three jobs for you. Pick one; you'll set off from here.", 16, UiStyle.Dim);
        int i = 0;
        foreach (var (kind, seed) in Offers())
        {
            var r = new Rect2(70 + i * 490, 190, 460, 560);
            DrawRect(r, UiStyle.Panel);
            DrawRect(new Rect2(r.Position, new Vector2(r.Size.X, 3)), new Color(UiStyle.Amber, .7f));
            UiStyle.Text(this, r.Position + new Vector2(24, 48), Mission.NameFor(seed).ToUpper(), 20, UiStyle.Ink, bold: true);
            UiStyle.Text(this, r.Position + new Vector2(24, 78), Mission.Describe(kind).ToUpper(), 14, UiStyle.Amber, bold: true);
            string blurb = kind switch
            {
                Mission.Op.Cache => "Their supply cache: ammunition, fuel, canned peaches. Plant a charge on it, then be elsewhere when it goes up. Loud. Very satisfying.",
                Mission.Op.Officer => "An officer is visiting one of their camps, mostly to shout at people. He sits by the radio desk. He doesn't get to leave.",
                Mission.Op.Prisoner => "One of ours is in a bamboo cage at their camp. Pick the lock, then bring him home. He walks slower than you. He also complains.",
                _ => "Their camp has something on the radio desk we'd love to read. Codebooks, schedules, peach orders. Take it and come home.",
            };
            DrawMultilineString(UiStyle.Font, r.Position + new Vector2(24, 120), blurb, HorizontalAlignment.Left, r.Size.X - 48, 16, -1, UiStyle.Ink);
            float hour = TimeOfDay.StartHourFor(seed);
            UiStyle.Text(this, r.Position + new Vector2(24, 300), $"SETS OFF   {(int)hour:00}:{(int)(hour % 1 * 60):00}  ({TimeOfDay.Describe(hour)})", 14, UiStyle.Dim);
            UiStyle.Text(this, r.Position + new Vector2(24, 326), "WHERE      a camp 300-600 m from home", 14, UiStyle.Dim);
            UiStyle.Text(this, r.Position + new Vector2(24, 352), $"PAY        {Mission.Reward(kind)} building supplies", 14, UiStyle.Dim);
            var (k2, s2) = (kind, seed);
            Button(new Rect2(r.Position + new Vector2(24, r.Size.Y - 70), new Vector2(r.Size.X - 48, 46)), "TAKE THIS ONE", () => { Close(); StartOperation(k2, s2); }, primary: true);
            i++;
        }
    }

    private void DrawBuild(int supplies)
    {
        int builders = DeadRecon.People.Squad.Roster.Count(x => x.Task == "build" && !x.Squad);
        UiStyle.Text(this, new Vector2(70, 150), $"Pick something, then place it at camp (plan mode). Builders: {builders}. Supplies come from enemy crates and as pay.", 16, UiStyle.Dim);
        int i = 0;
        foreach (var b in Structures.Catalogue.Values)
        {
            var r = new Rect2(70 + (i % 2) * 735, 185 + (i / 2) * 120, 715, 104);
            DrawRect(r, UiStyle.Panel);
            UiStyle.Text(this, r.Position + new Vector2(20, 36), b.Name.ToUpper(), 18, UiStyle.Ink, bold: true);
            DrawMultilineString(UiStyle.Font, r.Position + new Vector2(20, 60), b.What, HorizontalAlignment.Left, 470, 14, -1, UiStyle.Dim);
            int count = Structures.All.Count(p => p.Id == b.Id);
            UiStyle.Text(this, r.Position + new Vector2(500, 36), $"{b.Cost} supplies{(count > 0 ? $"  ·  {count} at camp" : "")}", 14, supplies >= b.Cost ? UiStyle.Ink : UiStyle.Dim);
            var bb = b;
            Button(new Rect2(r.Position + new Vector2(500, 52), new Vector2(195, 40)), "PLAN", () => { Close(); Structures.Current?.Plan(bb.Id); }, enabled: supplies >= b.Cost, primary: true);
            i++;
        }
    }

    private void DrawSoldiers()
    {
        var roster = DeadRecon.People.Squad.Roster.ToList();
        if (roster.Count == 0)
        {
            UiStyle.Text(this, new Vector2(70, 160), "Nobody here but you. Rescue prisoners (an operation on page 1) and they'll join your camp.", 16, UiStyle.Dim);
            return;
        }
        int going = roster.Count(x => x.Squad);
        UiStyle.Text(this, new Vector2(70, 150), $"Your people. Up to {DeadRecon.People.Squad.Max} come with you on the next operation ({going} picked). The rest do their jobs here.", 16, UiStyle.Dim);
        var armory = HomeBase.Saved?.Armory ?? new List<string>();
        int i = 0;
        foreach (var s in roster)
        {
            var r = new Rect2(70, 185 + i * 76, 1460, 66);
            DrawRect(r, s.Squad ? new Color(UiStyle.Olive, .35f) : UiStyle.Panel);
            UiStyle.Text(this, r.Position + new Vector2(20, 40), s.Name, 18, UiStyle.Ink, bold: true);
            var soldier = s;
            int row = i;
            Button(new Rect2(r.Position + new Vector2(360, 13), new Vector2(240, 40)), s.Squad ? "COMING WITH YOU" : "STAYS AT CAMP", () =>
            {
                if (!soldier.Squad && roster.Count(x => x.Squad) >= DeadRecon.People.Squad.Max) { Say($"The squad's full ({DeadRecon.People.Squad.Max})."); return; }
                soldier.Squad = !soldier.Squad; HomeBase.SaveNow();
            }, primary: s.Squad);
            string[] tasks = { "rest", "guard", "build" };
            Button(new Rect2(r.Position + new Vector2(620, 13), new Vector2(220, 40)), $"JOB: {s.Task.ToUpper()}", () =>
            {
                soldier.Task = tasks[(Array.IndexOf(tasks, soldier.Task) + 1) % tasks.Length]; HomeBase.SaveNow();
                foreach (var c in DeadRecon.People.Comrade.All.Where(c => c.Person == soldier.Name && c.Orders == DeadRecon.People.Comrade.Order.Base))
                { c.Task = soldier.Task; c.Post = DeadRecon.People.Squad.PostFor(soldier, row); }
            });
            Button(new Rect2(r.Position + new Vector2(860, 13), new Vector2(420, 40)), $"GUN: {WeaponDef.Get(s.Weapon).Name.ToUpper()}", () =>
            {
                int at = armory.IndexOf(soldier.Weapon);
                soldier.Weapon = armory.Count > 0 ? armory[(at + 1) % armory.Count] : soldier.Weapon; HomeBase.SaveNow();
            }, enabled: armory.Count > 1);
            i++;
        }
        UiStyle.Text(this, new Vector2(70, 185 + i * 76 + 30), "Guns: any gun you've carried home is in the armory. Squad orders in the field: F1-F3 one, F4 all, then 1 follow · 2 hold · 3 go there · 4 weapons free/hold fire.", 14, UiStyle.Dim);
    }

    private void DrawLog()
    {
        var r = new Rect2(70, 140, 1460, 640);
        DrawRect(r, UiStyle.Panel);
        if (Chatter.Log.Count == 0) { UiStyle.Text(this, r.Position + new Vector2(24, 44), "Just static so far. Stay near the radio and it'll start talking.", 16, UiStyle.Dim); return; }
        float y = r.Position.Y + 40;
        foreach (var (clock, who, text) in Enumerable.Reverse(Chatter.Log).Take(14))
        {
            UiStyle.Text(this, new Vector2(r.Position.X + 24, y), clock, 14, UiStyle.Dim);
            UiStyle.Text(this, new Vector2(r.Position.X + 100, y), who.ToUpper(), 14, UiStyle.Amber, bold: true);
            UiStyle.Text(this, new Vector2(r.Position.X + 230, y), text, 15, UiStyle.Ink);
            y += 42;
        }
    }
}
