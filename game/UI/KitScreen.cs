using System;
using System.Collections.Generic;
using System.Linq;
using DeadRecon.Gear;
using DeadRecon.Player;
using Godot;

namespace DeadRecon.UI;

/// <summary>
/// Tab: your kit. Two pages:
/// <list type="bullet">
/// <item><b>Backpack</b>: the vest and backpack grids. Drag things about (R turns them while dragging), drop the same
/// kind on a stack to merge, right-click to use a dressing or fit an attachment. Details of whatever you point at.</item>
/// <item><b>Rifle</b> (the gunsmith): a 3D rifle you can turn with the mouse, and what's fitted: optic, muzzle,
/// side rail, foregrip, paint. Click a choice to fit it; stats show what it does.</item>
/// </list>
/// The game pauses while it's open (single player). Drawn in a 1600x900 frame scaled to the window.
/// </summary>
public partial class KitScreen : Control
{
    public Kit Kit = null!;
    public Walker Player = null!;

    public static bool IsOpen { get; private set; }

    private const float RefW = 1600, RefH = 900, CellPx = 58;
    private float _scale = 1;
    private Vector2 _offset;
    private int _page;              // 0 backpack, 1 rifle
    private Vector2 _mouse;         // in the reference frame

    // Backpack page.
    private Item? _hover, _selected, _drag;
    private Vector2 _dragGrab;      // where in the item (reference px) it was grabbed
    private bool _dragRot;
    private Vector2 _pressAt;
    private string _message = "";
    private float _messageAge = 99;

    // Rifle page.
    private SubViewportContainer _previewBox = null!;
    private SubViewport _preview = null!;
    private Node3D _pivot = null!;
    private Camera3D _previewCam = null!;
    private float _spin = .6f, _spinVel = .25f;
    private bool _spinning;
    private readonly List<(Rect2 Rect, Action Do, string Tip)> _buttons = new();

    private static readonly Vector2 VestAt = new(70, 170), PackAt = new(70, 340), DetailAt = new(640, 140);

    public override void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        Visible = false;
        FocusMode = FocusModeEnum.All;

        _previewBox = new SubViewportContainer { Stretch = true, MouseFilter = MouseFilterEnum.Pass, Visible = false };
        AddChild(_previewBox);
        _preview = new SubViewport { OwnWorld3D = true, TransparentBg = true, Msaa3D = Viewport.Msaa.Msaa4X };
        _previewBox.AddChild(_preview);
        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.ClearColor, AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightColor = new Color(.7f, .74f, .78f), AmbientLightEnergy = .8f, TonemapMode = Godot.Environment.ToneMapper.Aces,
        };
        _preview.AddChild(new WorldEnvironment { Environment = env });
        _preview.AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-40, -50, 0), LightEnergy = 1.7f, LightColor = new Color(1, .95f, .88f), ShadowEnabled = true });
        _preview.AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-5, 130, 0), LightEnergy = .8f, LightColor = new Color(.7f, .82f, 1f) });
        _previewCam = new Camera3D { Fov = 30, Position = new Vector3(-1.55f, .32f, 0), Current = true };
        _preview.AddChild(_previewCam);
        _previewCam.LookAtFromPosition(_previewCam.Position, Vector3.Zero, Vector3.Up);
        _pivot = new Node3D();
        _preview.AddChild(_pivot);

        Kit.Changed += QueueRedraw;
        Kit.LoadoutChanged += OnLoadout;
        if (IconBaker.Current != null) IconBaker.Current.Baked += _ => QueueRedraw();
        OnLoadout();
    }

    public override void _ExitTree()
    {
        Kit.Changed -= QueueRedraw;
        Kit.LoadoutChanged -= OnLoadout;
        if (IsOpen) IsOpen = false;
    }

    private void OnLoadout()
    {
        IconBaker.Current?.RequestRifle(Kit.Loadout);
        foreach (var c in _pivot.GetChildren()) c.QueueFree();
        if (RifleBuilder.Build(Kit.Loadout, lights: false) is { } r)
        {
            // Centre the rifle on the pivot so it turns about its middle.
            var box = RifleBuilder.Bounds(r.Model);
            r.Model.Position = -box.GetCenter();
            _pivot.AddChild(r.Model);
        }
        QueueRedraw();
    }

    // ================================================================= opening

    public override void _Input(InputEvent e)
    {
        if (e is not InputEventKey { Pressed: true, Echo: false } k) return;
        if (k.PhysicalKeycode == Key.Tab || (IsOpen && k.PhysicalKeycode == Key.Escape))
        {
            if (IsOpen) Close(); else if (!GetTree().Paused && !Player.Dead) Open();
            GetViewport().SetInputAsHandled();
            return;
        }
        if (!IsOpen) return;
        if (k.PhysicalKeycode == Key.Key1) { _page = 0; QueueRedraw(); }
        if (k.PhysicalKeycode == Key.Key2) { _page = 1; QueueRedraw(); }
        if (k.PhysicalKeycode == Key.R && _drag != null) { _dragRot = !_dragRot; QueueRedraw(); }
        GetViewport().SetInputAsHandled();
    }

    public void Open(int page = -1)
    {
        if (page >= 0) _page = page;
        Visible = true;
        IsOpen = true;
        GetTree().Paused = true;
        Input.MouseMode = Input.MouseModeEnum.Visible;
        _selected = null; _drag = null;
        QueueRedraw();
    }

    public void Close()
    {
        Visible = false;
        IsOpen = false;
        _drag = null;
        _previewBox.Visible = false;
        GetTree().Paused = false;
        Input.MouseMode = Input.MouseModeEnum.Captured;
        Player.FireLockedUntilRelease = true;
    }

    /// <summary>Tests: point at an item (details panel), switch page.</summary>
    public void TestShow(Item? item, int page) { _selected = item; _page = page; QueueRedraw(); }

    public override void _Process(double delta)
    {
        if (!IsOpen) return;
        float dt = (float)delta;
        _messageAge += dt;
        var view = GetViewportRect().Size;
        _scale = Math.Min(view.X / RefW, view.Y / RefH);
        _offset = (view - new Vector2(RefW, RefH) * _scale) / 2;
        // The 3D preview sits in the left half of the rifle page.
        _previewBox.Visible = _page == 1;
        if (_page == 1)
        {
            var at = ToScreen(new Vector2(60, 130));
            _previewBox.Position = at;
            _previewBox.Size = new Vector2(860, 560) * _scale;
            if (!_spinning) _spin += _spinVel * dt;
            _spinVel = Mathf.MoveToward(_spinVel, .25f, dt * .5f);
            _pivot.Rotation = new Vector3(0, _spin, 0);
        }
        if (_drag != null || _page == 1) QueueRedraw();
    }

    private Vector2 ToRef(Vector2 screen) => (screen - _offset) / _scale;
    private Vector2 ToScreen(Vector2 r) => r * _scale + _offset;

    // ================================================================= mouse

    public override void _GuiInput(InputEvent e)
    {
        if (e is InputEventMouseMotion mm)
        {
            _mouse = ToRef(mm.Position);
            if (_page == 0) _hover = ItemUnder(_mouse, out _);
            if (_spinning) { _spin += mm.Relative.X * .01f; _spinVel = mm.Relative.X * .3f; }
            QueueRedraw();
        }
        else if (e is InputEventMouseButton mb)
        {
            _mouse = ToRef(mb.Position);
            if (mb.ButtonIndex == MouseButton.Left && mb.Pressed)
            {
                foreach (var (rect, act, _) in _buttons) if (rect.HasPoint(_mouse)) { act(); QueueRedraw(); return; }
                if (_page == 0 && ItemUnder(_mouse, out var origin) is { } item)
                {
                    _drag = item; _dragRot = item.Rotated; _selected = item;
                    _dragGrab = _mouse - origin;
                    _pressAt = _mouse;
                }
                if (_page == 1 && new Rect2(60, 130, 860, 560).HasPoint(_mouse)) _spinning = true;
            }
            else if (mb.ButtonIndex == MouseButton.Left && !mb.Pressed)
            {
                _spinning = false;
                if (_drag != null) Drop();
            }
            else if (mb.ButtonIndex == MouseButton.Right && mb.Pressed && _page == 0 && ItemUnder(_mouse, out _) is { } quick) QuickAction(quick);
            else if (_page == 1 && mb.Pressed && mb.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown)
                _previewCam.Fov = Math.Clamp(_previewCam.Fov + (mb.ButtonIndex == MouseButton.WheelUp ? -2 : 2), 14, 40);
            QueueRedraw();
        }
        AcceptEvent();
    }

    private (Kit.Grid Grid, Vector2 At)[] Grids => new[] { (Kit.Vest, VestAt), (Kit.Pack, PackAt) };

    private Item? ItemUnder(Vector2 p, out Vector2 origin)
    {
        origin = Vector2.Zero;
        foreach (var (g, at) in Grids)
        {
            var local = (p - at) / CellPx;
            if (local.X < 0 || local.Y < 0 || local.X >= g.W || local.Y >= g.H) continue;
            var item = g.At((int)local.X, (int)local.Y);
            if (item != null) origin = at + new Vector2(item.X, item.Y) * CellPx;
            return item;
        }
        return null;
    }

    /// <summary>Where the dragged item would land: grid and top-left cell.</summary>
    private (Kit.Grid Grid, int X, int Y, bool Fits)? DropTarget()
    {
        if (_drag == null) return null;
        int w = _dragRot ? _drag.Def.H : _drag.Def.W, h = _dragRot ? _drag.Def.W : _drag.Def.H;
        var topLeft = _mouse - (_dragRot != _drag.Rotated ? new Vector2(CellPx, CellPx) * .5f : _dragGrab);
        foreach (var (g, at) in Grids)
        {
            var cell = ((topLeft - at) / CellPx).Round();
            int x = (int)cell.X, y = (int)cell.Y;
            if (x < -1 || y < -1 || x > g.W || y > g.H) continue;
            bool stackable = g.At(Math.Clamp(x, 0, g.W - 1), Math.Clamp(y, 0, g.H - 1)) is { } t && t != _drag && t.Def == _drag.Def && t.Def.Stack > 1;
            return (g, x, y, g.Fits(x, y, w, h, _drag) || stackable);
        }
        return null;
    }

    private void Drop()
    {
        var item = _drag!;
        _drag = null;
        if (_mouse.DistanceTo(_pressAt) < 4 && _dragRot == item.Rotated) return;   // a click, not a drag
        if (DropTarget() is not { } t) return;
        if (!Kit.Move(item, t.Grid, t.X, t.Y, _dragRot)) Say("Doesn't fit there.");
    }

    private void QuickAction(Item item)
    {
        _selected = item;
        switch (item.Def.Kind)
        {
            case ItemKind.Medical: Kit.UseMedical(item); break;
            case ItemKind.Attachment:
                if (Kit.Fit(item.Def.Slot, item) is { } why) Say(why); else Say($"Fitted: {item.Def.Name}");
                break;
            case ItemKind.Magazine: Say(item.Rounds > 0 ? $"{item.Rounds} rounds. Press R in the field to load the fullest one." : "Empty."); break;
            default: Say("Keep that safe."); break;
        }
    }

    private void Say(string message) { _message = message; _messageAge = 0; }

    // ================================================================= drawing

    public override void _Draw()
    {
        if (!IsOpen) return;
        var view = GetViewportRect().Size;
        DrawRect(new Rect2(Vector2.Zero, view), new Color(.02f, .03f, .02f, .82f));
        DrawSetTransform(_offset, 0, Vector2.One * _scale);
        _buttons.Clear();

        // Header: title, pages, carried weight, health.
        UiStyle.Text(this, new Vector2(70, 78), "KIT", 34, UiStyle.Ink, bold: true);
        Tab(new Rect2(170, 50, 170, 38), "1   BACKPACK", 0);
        Tab(new Rect2(350, 50, 150, 38), "2   RIFLE", 1);

        var hp = new Rect2(1180, 60, 340, 10);
        DrawRect(hp, UiStyle.Faint);
        float frac = Player.Health / Walker.MaxHealth;
        DrawRect(new Rect2(hp.Position, new Vector2(hp.Size.X * frac, hp.Size.Y)), frac > .35f ? UiStyle.Ink : UiStyle.Danger);
        UiStyle.Text(this, new Vector2(1180, 92), $"HEALTH {Player.Health:0}{(Kit.Healing ? "   healing..." : "")}", 13, UiStyle.Dim);
        UiStyle.Text(this, new Vector2(1180, 92), $"{Kit.Kg:0.0} KG CARRIED", 13, UiStyle.Dim, HorizontalAlignment.Right, 340);
        DrawLine(new Vector2(70, 108), new Vector2(1530, 108), UiStyle.Faint, 1);

        if (_page == 0) DrawBackpack(); else DrawRifle();

        if (_messageAge < 3.5f)
            UiStyle.Text(this, new Vector2(0, 820), _message, 17, new Color(UiStyle.Amber, Math.Clamp(3.5f - _messageAge, 0, 1)), HorizontalAlignment.Center, RefW);
        UiStyle.Text(this, new Vector2(70, 868),
            _page == 0 ? "Drag to move  ·  R turns while dragging  ·  Right-click: use or fit  ·  Tab: back to the jungle"
                       : "Drag the rifle to turn it  ·  Wheel zooms  ·  Click a part to fit it  ·  L in the field: light / laser  ·  Tab: back",
            13, UiStyle.Dim);
        DrawSetTransform(Vector2.Zero, 0, Vector2.One);
    }

    private void Tab(Rect2 r, string label, int page)
    {
        bool on = _page == page, hover = r.HasPoint(_mouse);
        DrawRect(r, on ? new Color(UiStyle.Olive, .55f) : hover ? UiStyle.Faint : new Color(0, 0, 0, 0));
        if (on) DrawRect(new Rect2(r.Position + new Vector2(0, r.Size.Y - 3), new Vector2(r.Size.X, 3)), UiStyle.Amber);
        UiStyle.Text(this, r.Position + new Vector2(0, 25), label, 16, on ? UiStyle.Ink : UiStyle.Dim, HorizontalAlignment.Center, r.Size.X, bold: on);
        _buttons.Add((r, () => _page = page, label));
    }

    private bool Button(Rect2 r, string label, Action act, bool enabled = true, bool primary = false)
    {
        bool hover = enabled && r.HasPoint(_mouse);
        DrawRect(r, !enabled ? new Color(1, 1, 1, .04f) : primary ? new Color(UiStyle.Olive, hover ? .9f : .65f) : hover ? new Color(1, 1, 1, .16f) : new Color(1, 1, 1, .08f));
        DrawRect(r, new Color(1, 1, 1, enabled ? .25f : .08f), false, 1);
        UiStyle.Text(this, r.Position + new Vector2(0, r.Size.Y / 2 + 6), label, 15, enabled ? UiStyle.Ink : UiStyle.Faint, HorizontalAlignment.Center, r.Size.X, bold: primary);
        if (enabled) _buttons.Add((r, act, label));
        return hover;
    }

    // ---------------------------------------------------------------- backpack page

    private void DrawBackpack()
    {
        UiStyle.Text(this, VestAt + new Vector2(0, -16), "VEST", 15, UiStyle.Amber, bold: true);
        UiStyle.Text(this, VestAt + new Vector2(60, -16), "quick access: reloads and dressings come from here first", 12, UiStyle.Dim);
        UiStyle.Text(this, PackAt + new Vector2(0, -16), "BACKPACK", 15, UiStyle.Amber, bold: true);
        foreach (var (g, at) in Grids) DrawGrid(g, at);

        // Where a dragged item would land.
        if (DropTarget() is { } t && _drag != null)
        {
            var at = t.Grid == Kit.Vest ? VestAt : PackAt;
            int w = _dragRot ? _drag.Def.H : _drag.Def.W, h = _dragRot ? _drag.Def.W : _drag.Def.H;
            DrawRect(new Rect2(at + new Vector2(t.X, t.Y) * CellPx, new Vector2(w, h) * CellPx), new Color(t.Fits ? UiStyle.Good : UiStyle.Danger, .25f));
        }
        if (_drag != null)
        {
            int w = _dragRot ? _drag.Def.H : _drag.Def.W, h = _dragRot ? _drag.Def.W : _drag.Def.H;
            var origin = _mouse - (_dragRot != _drag.Rotated ? new Vector2(CellPx, CellPx) * .5f : _dragGrab);
            DrawItem(_drag, new Rect2(origin, new Vector2(w, h) * CellPx), _dragRot, .85f);
        }
        DrawDetails(_drag ?? _hover ?? _selected);
    }

    private void DrawGrid(Kit.Grid g, Vector2 at)
    {
        for (int y = 0; y < g.H; y++)
            for (int x = 0; x < g.W; x++)
            {
                var r = new Rect2(at + new Vector2(x, y) * CellPx + Vector2.One, new Vector2(CellPx - 2, CellPx - 2));
                DrawRect(r, UiStyle.Cell);
            }
        DrawRect(new Rect2(at - Vector2.One * 3, new Vector2(g.W, g.H) * CellPx + Vector2.One * 6), UiStyle.Faint, false, 1);
        foreach (var item in g.Items)
        {
            if (item == _drag) continue;
            DrawItem(item, new Rect2(at + new Vector2(item.X, item.Y) * CellPx, new Vector2(item.W, item.H) * CellPx), item.Rotated, 1);
        }
    }

    private void DrawItem(Item item, Rect2 r, bool rotated, float alpha)
    {
        var inner = r.Grow(-3);
        var tint = UiStyle.KindColour(item.Def.Kind);
        bool hot = item == _hover || item == _selected;
        DrawRect(inner, new Color(tint, (hot ? .95f : .75f) * alpha));
        DrawRect(inner, new Color(1, 1, 1, hot ? .45f : .14f), false, 1);
        var icon = IconBaker.Current?.Get($"item:{item.Def.Id}");
        var pad = inner.Grow(-5);
        if (icon != null)
        {
            // Fit the picture (drawn lengthways along the item's long side).
            DrawIcon(icon, pad, rotated, alpha);
        }
        else UiStyle.FlatIcon(this, item.Def.Id, pad);
        // Corner info: rounds, count, the intel's name.
        if (item.Def.Kind == ItemKind.Magazine)
        {
            UiStyle.Text(this, inner.Position + new Vector2(4, inner.Size.Y - 6), $"{item.Rounds}", 14, item.Rounds == 0 ? UiStyle.Danger : UiStyle.Ink, bold: true);
            if (item.Def.Capacity == 30) UiStyle.Text(this, inner.Position + new Vector2(4, 15), "30", 11, UiStyle.Amber);
        }
        else if (item.Count > 1) UiStyle.Text(this, inner.Position + new Vector2(0, inner.Size.Y - 6), $"x{item.Count}", 14, UiStyle.Ink, HorizontalAlignment.Right, inner.Size.X - 5, bold: true);
    }

    private void DrawIcon(Texture2D icon, Rect2 box, bool rotated, float alpha)
    {
        var img = icon.GetSize();
        if (!rotated)
        {
            float k = Math.Min(box.Size.X / img.X, box.Size.Y / img.Y);
            var size = img * k;
            DrawTextureRect(icon, new Rect2(box.GetCenter() - size / 2, size), false, new Color(1, 1, 1, alpha));
            return;
        }
        // Turned a quarter: draw in a rotated frame around the box centre.
        float kr = Math.Min(box.Size.Y / img.X, box.Size.X / img.Y);
        var sz = img * kr;
        var centre = box.GetCenter();
        DrawSetTransformMatrix(new Transform2D(0, Vector2.One * _scale, 0, _offset) * new Transform2D(MathF.PI / 2, centre));
        DrawTextureRect(icon, new Rect2(-sz / 2, sz), false, new Color(1, 1, 1, alpha));
        DrawSetTransform(_offset, 0, Vector2.One * _scale);
    }

    private void DrawDetails(Item? item)
    {
        var at = DetailAt;
        DrawRect(new Rect2(at, new Vector2(880, 640)), UiStyle.Panel);
        DrawRect(new Rect2(at, new Vector2(880, 2)), new Color(UiStyle.Amber, .6f));
        if (item == null)
        {
            UiStyle.Text(this, at + new Vector2(30, 50), "Point at something to see what it is.", 17, UiStyle.Dim);
            DrawRifleSummary(at + new Vector2(30, 400));
            return;
        }
        var pic = new Rect2(at + new Vector2(30, 30), new Vector2(380, 220));
        DrawRect(pic, new Color(1, 1, 1, .04f));
        if (IconBaker.Current?.Get($"item:{item.Def.Id}") is { } icon) DrawIcon(icon, pic.Grow(-14), false, 1);
        else UiStyle.FlatIcon(this, item.Def.Id, pic.Grow(-30));
        var tx = at + new Vector2(440, 60);
        UiStyle.Text(this, tx, item.Name, 26, UiStyle.Ink, bold: true);
        string kind = item.Def.Kind switch
        {
            ItemKind.Magazine => "MAGAZINE", ItemKind.Medical => "MEDICAL", ItemKind.Intel => "INTEL",
            _ => item.Def.Slot switch { Slot.Optic => "OPTIC", Slot.Muzzle => "MUZZLE DEVICE", Slot.Side => "SIDE RAIL", _ => "UNDERBARREL" },
        };
        UiStyle.Text(this, tx + new Vector2(0, 28), $"{kind}   ·   {item.Kg:0.00} kg   ·   {item.Def.W}x{item.Def.H}", 13, UiStyle.Amber);
        DrawMultilineString(UiStyle.Font, tx + new Vector2(0, 66), item.Def.Description, HorizontalAlignment.Left, 410, 16, -1, UiStyle.Ink);
        if (item.Def.Kind == ItemKind.Magazine)
            UiStyle.Bar(this, tx + new Vector2(0, 170), 410, "ROUNDS", item.Rounds / (float)item.Def.Capacity, $"{item.Rounds} / {item.Def.Capacity}", UiStyle.Ink);
        if (item.Count > 1) UiStyle.Text(this, tx + new Vector2(0, 170), $"x{item.Count} in this stack", 15, UiStyle.Dim);

        var b = at + new Vector2(30, 280);
        if (item.Def.Kind == ItemKind.Medical)
            Button(new Rect2(b, new Vector2(200, 42)), "USE", () => Kit.UseMedical(item), Player.Health < Walker.MaxHealth, primary: true);
        if (item.Def.Kind == ItemKind.Attachment && Kit.GridOf(item) != null)
        {
            Button(new Rect2(b, new Vector2(240, 42)), "FIT TO RIFLE", () => { if (Kit.Fit(item.Def.Slot, item) is { } why) Say(why); else Say($"Fitted: {item.Def.Name}"); }, primary: true);
            Button(new Rect2(b + new Vector2(260, 0), new Vector2(220, 42)), "OPEN GUNSMITH", () => _page = 1);
        }
        DrawRifleSummary(at + new Vector2(30, 400));
    }

    private void DrawRifleSummary(Vector2 at)
    {
        UiStyle.Text(this, at, "ON THE RIFLE", 15, UiStyle.Amber, bold: true);
        var pic = new Rect2(at + new Vector2(0, 14), new Vector2(520, 195));
        if (IconBaker.Current?.Get(IconBaker.RifleKey(Kit.Loadout)) is { } rifle) DrawIcon(rifle, pic, false, 1);
        var tx = at + new Vector2(550, 40);
        int y = 0;
        foreach (var slot in new[] { Slot.Optic, Slot.Muzzle, Slot.Side, Slot.Underbarrel })
        {
            string? id = Kit.Loadout.In(slot);
            string name = id != null && ItemDef.All.TryGetValue(id, out var d) ? d.Name : "—";
            UiStyle.Text(this, tx + new Vector2(0, y), SlotName(slot), 12, UiStyle.Dim);
            UiStyle.Text(this, tx + new Vector2(0, y + 18), name, 15, UiStyle.Ink);
            y += 44;
        }
        UiStyle.Text(this, tx + new Vector2(0, y), "LOADED", 12, UiStyle.Dim);
        UiStyle.Text(this, tx + new Vector2(0, y + 18), $"{Kit.Loaded} / {Kit.LoadedMag.Capacity}   (+{Kit.Magazines} magazines)", 15, UiStyle.Ink);
    }

    private static string SlotName(Slot s) => s switch { Slot.Optic => "OPTIC", Slot.Muzzle => "MUZZLE", Slot.Side => "SIDE RAIL", _ => "UNDERBARREL" };

    // ---------------------------------------------------------------- rifle page (gunsmith)

    private void DrawRifle()
    {
        DrawRect(new Rect2(60, 130, 860, 560), new Color(1, 1, 1, .03f));
        DrawRect(new Rect2(60, 130, 860, 560), UiStyle.Faint, false, 1);
        UiStyle.Text(this, new Vector2(80, 160), "BADGUN  ·  VIETNAM-PATTERN RIFLE", 14, UiStyle.Dim);

        float x = 960, y = 150;
        foreach (var slot in new[] { Slot.Optic, Slot.Muzzle, Slot.Side, Slot.Underbarrel })
        {
            UiStyle.Text(this, new Vector2(x, y), SlotName(slot), 14, UiStyle.Amber, bold: true);
            y += 12;
            float cx = x;
            foreach (var (label, id, act) in Choices(slot))
            {
                bool current = Kit.Loadout.In(slot) == id;
                var r = new Rect2(cx, y, 140, 74);
                bool hover = r.HasPoint(_mouse);
                DrawRect(r, current ? new Color(UiStyle.Olive, .7f) : hover ? new Color(1, 1, 1, .14f) : new Color(1, 1, 1, .06f));
                DrawRect(r, current ? UiStyle.Amber : new Color(1, 1, 1, .2f), false, current ? 2 : 1);
                if (id != null && IconBaker.Current?.Get($"item:{id}") is { } icon) DrawIcon(icon, new Rect2(r.Position + new Vector2(8, 4), new Vector2(124, 44)), false, 1);
                else UiStyle.Text(this, r.Position + new Vector2(0, 32), "—", 20, UiStyle.Dim, HorizontalAlignment.Center, r.Size.X);
                UiStyle.Text(this, r.Position + new Vector2(0, 66), label, 12, current ? UiStyle.Ink : UiStyle.Dim, HorizontalAlignment.Center, r.Size.X);
                if (!current) _buttons.Add((r, act, label));
                cx += 148;
                if (cx > x + 148 * 3.5f) break;
            }
            y += 96;
        }
        // Paint.
        UiStyle.Text(this, new Vector2(x, y), "PAINT", 14, UiStyle.Amber, bold: true);
        y += 12;
        float px = x;
        foreach (var (id, name, colour) in Loadout.Paints)
        {
            var r = new Rect2(px, y, 54, 34);
            bool current = Kit.Loadout.Paint == id;
            DrawRect(r, colour ?? new Color(.29f, .31f, .2f));
            if (id == "wood") DrawRect(new Rect2(r.Position + new Vector2(27, 0), new Vector2(27, 34)), new Color(.2f, .2f, .2f));
            DrawRect(r, current ? UiStyle.Amber : new Color(1, 1, 1, .25f), false, current ? 3 : 1);
            if (r.HasPoint(_mouse)) UiStyle.Text(this, new Vector2(x, y + 56), name, 13, UiStyle.Ink);
            if (!current) _buttons.Add((r, () => Kit.Paint(id), name));
            px += 62;
        }

        // What it all does, against the factory rifle.
        var (recoil, noise, _, sway) = RifleBuilder.Handling(Kit.Loadout);
        var optic = RifleBuilder.Optic(Kit.Loadout.Optic);
        float zoom = MathF.Tan(Mathf.DegToRad(35)) / MathF.Tan(Mathf.DegToRad(optic.Fov / 2));
        var sx = new Vector2(80, 730);
        UiStyle.Bar(this, sx, 520, "ZOOM", zoom / 7, $"{zoom:0.0}x", UiStyle.Ink);
        UiStyle.Bar(this, sx + new Vector2(0, 30), 520, "RECOIL", recoil, $"{recoil * 100:0}%", recoil < 1 ? UiStyle.Good : UiStyle.Ink);
        UiStyle.Bar(this, sx + new Vector2(0, 60), 520, "NOISE", noise / 1.2f, $"{DeadRecon.Combat.Hearing.Gunshot * noise:0} m", noise < 1 ? UiStyle.Good : noise > 1 ? UiStyle.Danger : UiStyle.Ink);
        UiStyle.Bar(this, sx + new Vector2(600, 0), 520, "STEADINESS", 1.15f - sway, sway < 1 ? "better" : sway > 1 ? "worse" : "factory", sway < 1 ? UiStyle.Good : UiStyle.Ink);
        string side = Kit.Loadout.Side switch { "side_flashlight" => "Light: L in the field. Guards see it too.", "side_laser" => "Laser: L in the field. Steadier from the hip.", _ => "" };
        UiStyle.Text(this, sx + new Vector2(600, 30), side, 13, UiStyle.Dim);
        UiStyle.Text(this, sx + new Vector2(600, 60), $"Loaded {Kit.Loaded}/{Kit.LoadedMag.Capacity}  ·  {Kit.Magazines} magazines carried", 13, UiStyle.Dim);
    }

    /// <summary>For a slot: what's fitted, everything in the bags that fits, and "none" where that's allowed.</summary>
    private IEnumerable<(string Label, string? Id, Action Do)> Choices(Slot slot)
    {
        var seen = new HashSet<string?>();
        string? current = Kit.Loadout.In(slot);
        if (current != null && ItemDef.All.TryGetValue(current, out var cd)) { seen.Add(current); yield return (Short(cd), current, () => { }); }
        foreach (var item in Kit.AllItems.Where(i => i.Def.Slot == slot).ToList())
        {
            if (!seen.Add(item.Def.Id)) continue;
            var it = item;
            yield return (Short(it.Def), it.Def.Id, () => { if (Kit.Fit(slot, it) is { } why) Say(why); else Say($"Fitted: {it.Def.Name}"); });
        }
        if (slot is Slot.Side or Slot.Underbarrel && current != null) yield return ("Nothing", null, () => { if (Kit.Fit(slot, null) is { } why) Say(why); });
        if (slot is Slot.Side or Slot.Underbarrel && current == null) yield return ("Nothing", null, () => { });
    }

    private static string Short(ItemDef d) => d.Name.Length > 18 ? d.Name[..17] + "." : d.Name;
}
