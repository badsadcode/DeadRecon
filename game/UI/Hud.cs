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

    private static readonly Color Ink = new(.93f, .91f, .84f);
    private static readonly Color Shadow = new(0, 0, 0, .65f);
    private static readonly Color Dim = new(.93f, .91f, .84f, .45f);
    private static readonly Color Amber = new(1, .78f, .3f);
    private static readonly Color Danger = new(1, .25f, .2f);

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
        _font = ThemeDB.FallbackFont;
        Player.Hurt += (_, from) => _hurts.Add((Bearing(from - Player.GlobalPosition), 0));
        Rifle.HitConfirmed += kill => { _hitMarker = 0; _hitKill = kill; };
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;
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
        var size = GetViewportRect().Size;
        var centre = size / 2;
        if (Player.Dead) return;

        DrawCompass(new Vector2(centre.X, 34), 560);
        // Objective line under the compass.
        Text(new Vector2(0, 122), Mission.Objective(), 18, Ink, HorizontalAlignment.Center, size.X);

        // Health and stance (bottom left).
        var hp = new Vector2(28, size.Y - 46);
        DrawRect(new Rect2(hp, new Vector2(220, 10)), new Color(0, 0, 0, .45f));
        float frac = Player.Health / Walker.MaxHealth;
        DrawRect(new Rect2(hp, new Vector2(220 * frac, 10)), frac > .35f ? Ink : Danger);
        string stance = Player.Flying ? "flying" : Player.Crouching ? "crouched" : Player.Running ? "running" : "standing";
        Text(hp + new Vector2(0, -10), $"{stance.ToUpper()}   {Weather.Describe()}", 15, Dim);

        // Ammo (bottom right).
        if (Rifle.Loaded)
        {
            Text(new Vector2(size.X - 260, size.Y - 34), $"{Rifle.Ammo}", 40, Rifle.Ammo == 0 ? Danger : Ink, HorizontalAlignment.Right, 150);
            Text(new Vector2(size.X - 104, size.Y - 34), $"/ {Rifle.MagazineCapacity}", 20, Dim);
            if (Rifle.Reloading) Text(new Vector2(size.X - 260, size.Y - 84), "RELOADING", 16, Amber, HorizontalAlignment.Right, 228);
            else if (Rifle.Ammo == 0) Text(new Vector2(size.X - 260, size.Y - 84), "R TO RELOAD", 16, Danger, HorizontalAlignment.Right, 228);
        }

        // Take-the-intel prompt with a progress ring.
        if (Mission.NearIntel)
        {
            var p = centre + new Vector2(0, 70);
            DrawArc(p, 18, 0, MathF.Tau, 32, new Color(0, 0, 0, .4f), 4);
            if (Mission.SearchProgress > 0) DrawArc(p, 18, -MathF.PI / 2, -MathF.PI / 2 + MathF.Tau * Mission.SearchProgress, 32, Amber, 4);
            Text(p + new Vector2(-200, 46), $"Hold E: take {Mission.IntelName}", 18, Ink, HorizontalAlignment.Center, 400);
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
