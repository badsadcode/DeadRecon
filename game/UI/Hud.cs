using System;
using System.Collections.Generic;
using DeadRecon.AI;
using DeadRecon.Missions;
using DeadRecon.Player;
using DeadRecon.World;
using Godot;

namespace DeadRecon.UI;

/// <summary>
/// The player's HUD, drawn in code: compass strip with the objective, objective line, health and stance, ammo,
/// hold-E prompt with progress, hit marker, and two stealth cues around the crosshair:
/// <list type="bullet">
/// <item>Detection arcs: one per guard who is starting to notice you, pointing at him, filling up and turning
/// white → yellow → red as his suspicion grows. Guards who haven't noticed anything show nothing.</item>
/// <item>Damage direction: a red arc toward whoever just shot you.</item>
/// </list>
/// </summary>
public partial class Hud : Control
{
    public Walker Player = null!;
    public Weapon Rifle = null!;
    public Mission Mission = null!;
    public Weather Weather = null!;

    private readonly List<(float Bearing, float Age)> _hurts = new();
    private float _hitMarker = -1;
    private bool _hitKill;
    private Font _font = null!;
    private static string _title = "", _subtitle = "";
    private static float _titleAge = 99;
    private static string _prompt = "";
    private static readonly List<(string Text, float Age)> _toasts = new();
    private static string _dialogTitle = "", _dialogBody = "";
    private static string[] _dialogOptions = Array.Empty<string>();
    private static bool _dialogOpen;

    /// <summary>An interaction prompt for this frame ("F  Talk to...").</summary>
    public static void Prompt(string text) => _prompt = text;
    /// <summary>A small line that fades after a few seconds (support changes...).</summary>
    public static void Toast(string text) { _toasts.Add((text, 0)); if (_toasts.Count > 4) _toasts.RemoveAt(0); }
    public static void ShowDialog(string title, string body, string[] options) { _dialogTitle = title; _dialogBody = body; _dialogOptions = options; _dialogOpen = true; }
    public static void CloseDialog() => _dialogOpen = false;

    /// <summary>A quiet title card (entering a village...): fades in, holds, fades out.</summary>
    public static void Announce(string title, string subtitle) { _title = title; _subtitle = subtitle; _titleAge = 0; }

    private static readonly Color Ink = new(.93f, .91f, .84f);
    private static readonly Color Shadow = new(0, 0, 0, .65f);
    private static readonly Color Dim = new(.93f, .91f, .84f, .45f);
    private static readonly Color Amber = new(1, .78f, .3f);
    private static readonly Color Danger = new(1, .25f, .2f);
    private static readonly Color JobColour = new(.55f, .85f, .45f);

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        ProcessMode = ProcessModeEnum.Always;   // keeps redrawing under the kit screen (the game is paused there)
        MouseFilter = MouseFilterEnum.Ignore;
        _font = UiStyle.Font;
        Player.Hurt += (_, from) => _hurts.Add((Bearing(from - Player.GlobalPosition), 0));
        Rifle.HitConfirmed += kill => { _hitMarker = 0; _hitKill = kill; };
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        _titleAge += dt;
        for (int i = _toasts.Count - 1; i >= 0; i--) { var (t, age) = _toasts[i]; if (age + dt > 6) _toasts.RemoveAt(i); else _toasts[i] = (t, age + dt); }
        for (int i = _hurts.Count - 1; i >= 0; i--)
        {
            var (b, age) = _hurts[i];
            age += dt;
            if (age > 1.6f) _hurts.RemoveAt(i); else _hurts[i] = (b, age);
        }
        if (_hitMarker >= 0) { _hitMarker += dt; if (_hitMarker > .3f) _hitMarker = -1; }
        QueueRedraw();
    }

    /// <summary>Compass bearing (degrees, 0 = north = -Z, 90 = east = +X) of a direction.</summary>
    private static float Bearing(Vector3 d) => (Mathf.RadToDeg(MathF.Atan2(d.X, -d.Z)) + 360) % 360;
    private float Heading => Bearing(-Player.Camera.GlobalTransform.Basis.Z);
    private static float Wrap(float a) => (a + 540) % 360 - 180;

    private void Text(Vector2 at, string s, int size, Color c, HorizontalAlignment align = HorizontalAlignment.Left, float width = -1)
    {
        DrawString(_font, at + new Vector2(1.5f, 1.5f), s, align, width, size, Shadow);
        DrawString(_font, at, s, align, width, size, c);
    }

    public override void _Draw()
    {
        {
            var view = GetViewportRect().Size;
            for (int i = 0; i < _toasts.Count; i++)
            {
                var (t, age) = _toasts[i];
                float a = Math.Clamp(age * 3, 0, 1) * Math.Clamp((6 - age) / 1.5f, 0, 1);
                Text(new Vector2(0, view.Y * .34f + i * 22), t, 16, new Color(Amber, a), HorizontalAlignment.Center, view.X);
            }
            if (_prompt != "" && !_dialogOpen) Text(new Vector2(0, view.Y * .62f), _prompt, 18, Ink, HorizontalAlignment.Center, view.X);
            _prompt = "";
            if (_dialogOpen)
            {
                float w = Math.Min(760, view.X - 60), x = (view.X - w) / 2, y = view.Y * .58f;
                float bodyH = _font.GetMultilineStringSize(_dialogBody, HorizontalAlignment.Left, w - 40, 17).Y;
                float h = 84 + bodyH + _dialogOptions.Length * 24;
                DrawRect(new Rect2(x, y, w, h), new Color(0, 0, 0, .62f));
                DrawRect(new Rect2(x, y, w, 2), new Color(Amber, .8f));
                Text(new Vector2(x + 20, y + 28), _dialogTitle, 16, Amber);
                DrawMultilineString(_font, new Vector2(x + 20, y + 56), _dialogBody, HorizontalAlignment.Left, w - 40, 17, -1, Ink);
                for (int i = 0; i < _dialogOptions.Length; i++)
                    Text(new Vector2(x + 20, y + 60 + bodyH + i * 24 + 10), _dialogOptions[i], 16, new Color(Ink, .8f));
                Text(new Vector2(x + 20, y + h - 8), "F: done", 12, Dim);
            }
        }
        if (_titleAge < 5)
        {
            float a = Math.Clamp(_titleAge * 2, 0, 1) * Math.Clamp((5 - _titleAge) / 1.5f, 0, 1);
            var view = GetViewportRect().Size;
            Text(new Vector2(0, view.Y * .24f), _title, 30, new Color(Ink, a), HorizontalAlignment.Center, view.X);
            Text(new Vector2(0, view.Y * .24f + 26), _subtitle.ToUpper(), 14, new Color(Ink, .6f * a), HorizontalAlignment.Center, view.X);
        }

        var size = GetViewportRect().Size;
        var centre = size / 2;
        if (Player.Dead || KitScreen.IsOpen) return;

        DrawCompass(new Vector2(centre.X, 34), 560);
        // Objective line under the compass.
        Text(new Vector2(0, 122), Mission.Objective(), 18, Ink, HorizontalAlignment.Center, size.X);
        if (DeadRecon.People.JobBoard.Current?.Tracked is { Active: true } job)
        {
            string where = job.Marker is { } jm ? $"   {new Vector2(jm.X - Player.GlobalPosition.X, jm.Z - Player.GlobalPosition.Z).Length():0} m" : "";
            Text(new Vector2(0, 146), $"JOB: {job.Title}{where}", 15, JobColour, HorizontalAlignment.Center, size.X);
        }

        // Health, stance, dressings (bottom left): a segmented bar, a quiet line of status under it.
        var hp = new Vector2(30, size.Y - 52);
        float frac = Player.Health / Walker.MaxHealth;
        const int Segments = 10;
        for (int s = 0; s < Segments; s++)
        {
            var seg = new Rect2(hp + new Vector2(s * 25, 0), new Vector2(22, 9));
            DrawRect(seg, new Color(0, 0, 0, .45f));
            float fill = Math.Clamp(frac * Segments - s, 0, 1);
            if (fill > 0) DrawRect(new Rect2(seg.Position, new Vector2(22 * fill, 9)), frac > .35f ? Ink : Danger);
        }
        string stance = Player.Flying ? "flying" : Player.Crouching ? "crouched" : Player.Running ? "running" : "standing";
        Text(hp + new Vector2(0, -12), $"{stance.ToUpper()}   ·   {Weather.Describe().ToUpper()}   ·   {TimeOfDay.Current?.Clock}", 14, Dim);
        if (DeadRecon.People.Squad.Current is { Team.Count: > 0 } squad)
        {
            int row = 0;
            foreach (var (key, c, picked) in squad.Status)
            {
                var y = hp + new Vector2(0, -40 - row * 20);
                string state = c.Dead ? "KIA" : c.Down ? "DOWN" : c.Orders switch { DeadRecon.People.Comrade.Order.Hold => "holding", DeadRecon.People.Comrade.Order.Move => "moving", _ => "following" };
                var col = c.Dead ? new Color(Ink, .3f) : c.Down ? Danger : picked ? Amber : new Color(.7f, .95f, .7f, .85f);
                Text(y, $"{key}  {c.Person}   {state}{(c.FireAtWill && !c.Dead ? "  ·  weapons free" : "")}", 13, col);
                row++;
            }
            if (squad.Picking) Text(hp + new Vector2(0, -40 - row * 20 - 4), "1 follow   2 hold   3 go there   4 weapons free / hold fire", 13, Amber);
        }
        if (DeadRecon.Gear.Kit.Current is { } kit)
        {
            int dressings = kit.Medical;
            Text(hp + new Vector2(0, 30), kit.Venom > 0 ? $"VENOM!  H x{dressings}" : kit.Healing ? "PATCHING UP..." : $"H   DRESSINGS x{dressings}", 13, kit.Venom > 0 ? Danger : kit.Healing ? Amber : dressings == 0 ? new Color(Danger, .8f) : Dim);
            Text(hp + new Vector2(150, 30), "TAB   KIT", 13, Dim);
        }

        // The rifle (bottom right): a picture of it as fitted, rounds, magazines, mode and what's on it.
        if (Rifle.Loaded)
        {
            var right = new Vector2(size.X - 30, size.Y - 26);
            if (DeadRecon.Gear.Kit.Current is { } k && DeadRecon.Gear.IconBaker.Current?.Get(DeadRecon.Gear.IconBaker.RifleKey(k.Loadout)) is { } pic)
            {
                var sz = pic.GetSize() * (66f / pic.GetSize().Y);
                DrawTextureRect(pic, new Rect2(right - new Vector2(sz.X, 150), sz), false, new Color(1, 1, 1, .8f));
            }
            Text(right - new Vector2(300, 8), $"{Rifle.Ammo}", 44, Rifle.Ammo == 0 ? Danger : Ink, HorizontalAlignment.Right, 210);
            Text(right - new Vector2(84, 10), $"/ {Rifle.MagazineCapacity}", 20, Dim);
            int mags = DeadRecon.Gear.Kit.Current?.Magazines ?? 0;
            Text(right - new Vector2(300, 62), $"+{mags} MAG{(mags == 1 ? "" : "S")}   ·   {(Rifle.Automatic ? "AUTO" : "SEMI")}", 13, mags == 0 ? new Color(Danger, .85f) : Dim, HorizontalAlignment.Right, 300);
            var tags = new System.Collections.Generic.List<string>();
            if (Rifle.Suppressed) tags.Add("SUPPRESSED");
            if (DeadRecon.Gear.Kit.Current is { LightOn: true } lit && Rifle.HasLight) tags.Add(lit.Loadout.Side == "side_laser" ? "LASER ON" : "LIGHT ON");
            if (tags.Count > 0) Text(right - new Vector2(300, 80), string.Join("   ", tags), 12, Amber, HorizontalAlignment.Right, 300);
            if (Rifle.Reloading) Text(right - new Vector2(300, 98), "RELOADING", 16, Amber, HorizontalAlignment.Right, 300);
            else if (Rifle.Ammo == 0) Text(right - new Vector2(300, 98), mags > 0 ? "R TO RELOAD" : "OUT OF AMMO", 16, Danger, HorizontalAlignment.Right, 300);
        }

        // Take-the-intel prompt with a progress ring.
        if (Mission.NearIntel)
        {
            var p = centre + new Vector2(0, 70);
            DrawArc(p, 18, 0, MathF.Tau, 32, new Color(0, 0, 0, .4f), 4);
            if (Mission.SearchProgress > 0) DrawArc(p, 18, -MathF.PI / 2, -MathF.PI / 2 + MathF.Tau * Mission.SearchProgress, 32, Amber, 4);
            Text(p + new Vector2(-200, 46), $"Hold F: take {Mission.IntelName}", 18, Ink, HorizontalAlignment.Center, 400);
        }

        // Breath (scope only): a thin bar under the reticle; amber while holding, red when spent.
        if (Rifle.Scoped && (Rifle.HoldingBreath || Rifle.Breath < .999f))
        {
            var b = centre + new Vector2(-60, size.Y * .3f);
            DrawRect(new Rect2(b, new Vector2(120, 5)), new Color(0, 0, 0, .5f));
            DrawRect(new Rect2(b, new Vector2(120 * Rifle.Breath, 5)), Rifle.HoldingBreath ? Amber : Rifle.Breath < .3f ? Danger : Ink);
            Text(b + new Vector2(0, -6), Rifle.HoldingBreath ? "HOLDING BREATH" : "BREATH", 12, Dim, HorizontalAlignment.Center, 120);
        }

        DrawDetection(centre);
        foreach (var (b, age) in _hurts)
        {
            float a = Mathf.DegToRad(Wrap(b - Heading)) - MathF.PI / 2;
            var c = new Color(Danger, (1 - age / 1.6f) * .85f);
            DrawArc(centre, 150, a - .35f, a + .35f, 16, c, 8);
        }

        if (_hitMarker >= 0)
        {
            float s = 9 + _hitMarker * 10;
            var c = new Color(_hitKill ? Danger : Ink, 1 - _hitMarker / .3f);
            DrawLine(centre + new Vector2(-s, -s), centre + new Vector2(-s / 2.5f, -s / 2.5f), c, 2.5f);
            DrawLine(centre + new Vector2(s, -s), centre + new Vector2(s / 2.5f, -s / 2.5f), c, 2.5f);
            DrawLine(centre + new Vector2(-s, s), centre + new Vector2(-s / 2.5f, s / 2.5f), c, 2.5f);
            DrawLine(centre + new Vector2(s, s), centre + new Vector2(s / 2.5f, s / 2.5f), c, 2.5f);
        }
    }

    private void DrawCompass(Vector2 at, float width)
    {
        const float FieldDeg = 130;
        float pxPerDeg = width / FieldDeg, h = Heading;
        DrawRect(new Rect2(at - new Vector2(width / 2, 14), new Vector2(width, 30)), new Color(0, 0, 0, .28f));
        string[] names = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };
        for (int d = 0; d < 360; d += 15)
        {
            float dx = Wrap(d - h) * pxPerDeg;
            if (Math.Abs(dx) > width / 2) continue;
            var x = at + new Vector2(dx, 0);
            if (d % 45 == 0)
            {
                string n = names[d / 45];
                Text(x + new Vector2(-30, 8), n, n.Length == 1 ? 20 : 15, n == "N" ? Amber : Ink, HorizontalAlignment.Center, 60);
            }
            else DrawLine(x + new Vector2(0, -6), x + new Vector2(0, 2), Dim, 1.5f);
        }
        DrawLine(at + new Vector2(0, -16), at + new Vector2(0, -8), Amber, 2);   // heading notch

        if (MapMemory.Current?.Waypoint is { } wp)
        {
            var to = wp - Player.GlobalPosition;
            float dx = Math.Clamp(Wrap(Bearing(to) - h) * pxPerDeg, -width / 2, width / 2);
            var p = at + new Vector2(dx, 26);
            DrawColoredPolygon(new[] { p + new Vector2(0, -7), p + new Vector2(6, 0), p + new Vector2(0, 7), p + new Vector2(-6, 0) }, Ink);
            Text(p + new Vector2(-40, 44), $"{new Vector2(to.X, to.Z).Length():0} m", 13, Ink, HorizontalAlignment.Center, 80);   // a row below the objective's
        }
        if (DeadRecon.People.JobBoard.Current?.Tracked is { Active: true, Marker: { } jobAt })
        {
            float dx = Math.Clamp(Wrap(Bearing(jobAt - Player.GlobalPosition) - h) * pxPerDeg, -width / 2, width / 2);
            var p = at + new Vector2(dx, 26);
            DrawCircle(p, 6, JobColour);
            DrawArc(p, 6, 0, MathF.Tau, 16, new Color(0, 0, 0, .5f), 1.5f);
        }
        if (Mission.Target is { } target)
        {
            var to = target - Player.GlobalPosition;
            float dx = Math.Clamp(Wrap(Bearing(to) - h) * pxPerDeg, -width / 2, width / 2);
            var p = at + new Vector2(dx, 26);
            DrawColoredPolygon(new[] { p + new Vector2(0, -8), p + new Vector2(7, 0), p + new Vector2(0, 8), p + new Vector2(-7, 0) }, Amber);
            Text(p + new Vector2(-40, 26), $"{new Vector2(to.X, to.Z).Length():0} m", 15, Amber, HorizontalAlignment.Center, 80);
        }
    }

    /// <summary>An arc per guard who has started noticing you, toward him; fills and reddens with his suspicion.</summary>
    private void DrawDetection(Vector2 centre)
    {
        foreach (var g in Guard.All)
        {
            if (g.State == Guard.Mood.Dead) continue;
            bool alert = g.State == Guard.Mood.Alert && g.CanSeeTarget;
            float s = alert ? 1 : g.Suspicion;
            if (s < .03f || (g.State == Guard.Mood.Search && !g.CanSeeTarget)) continue;
            float a = Mathf.DegToRad(Wrap(Bearing(g.GlobalPosition - Player.GlobalPosition) - Heading)) - MathF.PI / 2;
            var colour = alert ? Danger : s < .3f ? Ink : s < .7f ? Amber : Amber.Lerp(Danger, (s - .7f) / .3f);
            const float R = 112, Half = .28f;
            float fill = .35f + .65f * s;   // visible from the first flicker of suspicion
            DrawArc(centre, R, a - Half, a + Half, 14, new Color(0, 0, 0, .45f), 10);
            DrawArc(centre, R, a - Half * fill, a + Half * fill, 14, new Color(colour, .6f + .4f * s), 8);
            // A chevron pointing outward at him.
            var dir = new Vector2(MathF.Cos(a), MathF.Sin(a));
            var tip = centre + dir * (R + 16);
            var side = new Vector2(-dir.Y, dir.X) * 7;
            DrawPolyline(new[] { tip - dir * 9 + side, tip, tip - dir * 9 - side }, new Color(colour, .4f + .6f * s), 3);
        }
    }
}
