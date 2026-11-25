using System;
using System.Collections.Generic;
using DeadRecon.Missions;
using DeadRecon.People;
using DeadRecon.Player;
using DeadRecon.World;
using Godot;

namespace DeadRecon.UI;

/// <summary>
/// The field map (third page of the kit screen, or M): the painted terrain, roads drawn as lines, towns and villages
/// (with how they feel about you), hamlets and checkpoints you've been near, a 1 km grid, you, the objective and your
/// waypoint. Drag to pan, wheel to zoom, left-click to set a waypoint, right-click to clear it. Places you haven't
/// seen are faded.
/// </summary>
public partial class MapView : Control
{
    public BakedWorld World = null!;
    public Settlements? Towns;
    public Walker Player = null!;
    public Mission? Mission;

    private Vector2 _centre;          // world x, z at the middle of the view
    private float _mpp = 4;           // metres per pixel
    private bool _panning, _moved;
    private Vector2 _mouse;
    private ImageTexture? _fog;
    private int _fogVersion = -1;

    public void CentreOnPlayer() { _centre = new Vector2(Player.GlobalPosition.X, Player.GlobalPosition.Z); _mpp = 4; }

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Stop;
        ClipContents = true;
    }

    private Vector2 ToScreen(float x, float z) => Size / 2 + (new Vector2(x, z) - _centre) / _mpp;
    private Vector2 ToWorld(Vector2 s) => _centre + (s - Size / 2) * _mpp;

    /// <summary>A mouse event in this view's own coordinates (the kit screen forwards them).</summary>
    public void Handle(InputEvent e)
    {
        if (e is InputEventMouseMotion mm)
        {
            _mouse = mm.Position;
            if (_panning) { _centre -= mm.Relative * _mpp; _moved |= mm.Relative.LengthSquared() > 1; }
        }
        else if (e is InputEventMouseButton mb)
        {
            _mouse = mb.Position;
            if (mb.ButtonIndex == MouseButton.Left)
            {
                if (mb.Pressed) { _panning = true; _moved = false; }
                else
                {
                    _panning = false;
                    if (!_moved && MapMemory.Current is { } mem)
                    {
                        var w = ToWorld(mb.Position);
                        mem.Waypoint = new Vector3(w.X, World.HeightAt(w.X, w.Y), w.Y);
                    }
                }
            }
            else if (mb.ButtonIndex == MouseButton.Right && mb.Pressed && MapMemory.Current is { } mem2) mem2.Waypoint = null;
            else if (mb.Pressed && mb.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown)
            {
                // Zoom about the mouse.
                var before = ToWorld(mb.Position);
                _mpp = Math.Clamp(_mpp * (mb.ButtonIndex == MouseButton.WheelUp ? .8f : 1.25f), .6f, 9);
                _centre += before - ToWorld(mb.Position);
            }
        }
        float half = World.Half;
        _centre = new Vector2(Math.Clamp(_centre.X, -half, half), Math.Clamp(_centre.Y, -half, half));
        QueueRedraw();
    }

    /// <summary>The mouse left the map: stop panning.</summary>
    public void Release() => _panning = false;

    public override void _Process(double delta) { if (Visible) QueueRedraw(); }

    public override void _Draw()
    {
        var rect = new Rect2(Vector2.Zero, Size);
        DrawRect(rect, new Color(.80f, .76f, .64f));
        var tex = MapPainter.Get(World, Towns);
        float half = World.Half;
        if (tex == null)
        {
            UiStyle.Text(this, Size / 2 - new Vector2(200, 0), "Unfolding the map...", 20, new Color(.2f, .18f, .12f), HorizontalAlignment.Center, 400);
            return;
        }
        // The painting: the visible part of the region.
        var tl = ToScreen(-half, -half);
        var full = new Rect2(tl, new Vector2(World.Size, World.Size) / _mpp);
        DrawTextureRect(tex, full, false);
        DrawFog(full);

        var ink = new Color(.22f, .18f, .12f);
        // 1 km grid with letters along the top and numbers down the side.
        for (int k = 0; k <= (int)(World.Size / 1000); k++)
        {
            float v = -half + k * 1000;
            var a = ToScreen(v, -half); var b = ToScreen(v, half);
            DrawLine(new Vector2(a.X, 0), new Vector2(b.X, Size.Y), new Color(ink, .18f), 1);
            var c = ToScreen(-half, v); var d = ToScreen(half, v);
            DrawLine(new Vector2(0, c.Y), new Vector2(Size.X, d.Y), new Color(ink, .18f), 1);
            if (k < (int)(World.Size / 1000))
            {
                UiStyle.Text(this, new Vector2(ToScreen(v + 500, 0).X - 10, 18), ((char)('A' + k)).ToString(), 13, new Color(ink, .7f));
                UiStyle.Text(this, new Vector2(6, ToScreen(0, v + 500).Y + 5), (k + 1).ToString(), 13, new Color(ink, .7f));
            }
        }

        // Roads: a dark casing under a red-brown line; tracks thinner and dashed by their own detail.
        if (Towns != null)
        {
            foreach (var road in Towns.Roads)
            {
                var pts = new Vector2[road.Points.Length];
                for (int k = 0; k < pts.Length; k++) pts[k] = ToScreen(road.Points[k].X, road.Points[k].Z);
                bool major = road.Kind == "road";
                float w = Math.Clamp((major ? 6f : 3.5f) / _mpp, major ? 2 : 1.2f, major ? 7 : 4);
                DrawPolyline(pts, new Color(.35f, .2f, .12f, .55f), w + 1.6f, true);
                DrawPolyline(pts, major ? new Color(.78f, .36f, .2f) : new Color(.72f, .48f, .3f), w, true);
            }
            foreach (var (kind, at) in Towns.Crossings)
            {
                var p = ToScreen(at.X, at.Y);
                if (rect.HasPoint(p) && _mpp < 5) DrawCircle(p, 3, new Color(.25f, .45f, .65f));
            }
            DrawPlaces(ink);
        }

        // The operation: the camp (objective) and, once you have the intel, the landing zone.
        if (Mission != null && Mission.Target is { } target)
        {
            var p = ToScreen(target.X, target.Z);
            bool lz = Mission.State == Mission.Phase.Extract;
            var col = lz ? new Color(.2f, .55f, .25f) : new Color(.75f, .15f, .1f);
            if (lz) DrawArc(p, 11, 0, MathF.Tau, 24, col, 3);
            else { DrawLine(p - new Vector2(9, 9), p + new Vector2(9, 9), col, 3.5f); DrawLine(p + new Vector2(-9, 9), p + new Vector2(9, -9), col, 3.5f); }
            UiStyle.Text(this, p + new Vector2(14, 5), lz ? "LANDING ZONE" : "OBJECTIVE", 13, col, bold: true);
        }
        // Village jobs: a green circle (a search area) or a ring on the spot, with a label.
        if (DeadRecon.People.JobBoard.Current is { } board)
            foreach (var job in board.Active)
            {
                if (job.Marker is not { } jm) continue;
                var p = ToScreen(jm.X, jm.Z);
                var green = new Color(.2f, .55f, .2f);
                if (job.MarkerRadius > 0)
                {
                    DrawCircle(p, job.MarkerRadius / _mpp, new Color(green, .16f));
                    DrawArc(p, job.MarkerRadius / _mpp, 0, MathF.Tau, 48, green, 2);
                }
                else DrawArc(p, 9, 0, MathF.Tau, 24, green, 3);
                UiStyle.Text(this, p + new Vector2(job.MarkerRadius / _mpp + 8, -6), job.Title, 12, green, bold: board.Tracked == job);
            }
        // Your waypoint.
        if (MapMemory.Current?.Waypoint is { } wp)
        {
            var p = ToScreen(wp.X, wp.Z);
            DrawColoredPolygon(new[] { p + new Vector2(0, -9), p + new Vector2(7, 0), p + new Vector2(0, 9), p + new Vector2(-7, 0) }, new Color(1, 1, 1));
            DrawPolyline(new[] { p + new Vector2(0, -9), p + new Vector2(7, 0), p + new Vector2(0, 9), p + new Vector2(-7, 0), p + new Vector2(0, -9) }, ink, 1.5f);
            float dist = new Vector2(wp.X - Player.GlobalPosition.X, wp.Z - Player.GlobalPosition.Z).Length();
            UiStyle.Text(this, p + new Vector2(12, 5), $"{dist:0} m", 12, ink);
        }
        // You: an arrow the way you're facing.
        {
            var p = ToScreen(Player.GlobalPosition.X, Player.GlobalPosition.Z);
            var f = -Player.Camera.GlobalTransform.Basis.Z;
            var dir = new Vector2(f.X, f.Z).Normalized();
            var side = new Vector2(-dir.Y, dir.X);
            var tri = new[] { p + dir * 12, p - dir * 7 + side * 7, p - dir * 3, p - dir * 7 - side * 7 };
            DrawColoredPolygon(tri, UiStyle.Amber);
            DrawPolyline(new[] { tri[0], tri[1], tri[2], tri[3], tri[0] }, ink, 1.5f);
        }
        // Scale bar and coordinates under the mouse.
        float bar = 500 / _mpp;
        var sb = new Vector2(20, Size.Y - 24);
        DrawRect(new Rect2(sb, new Vector2(bar, 5)), ink);
        DrawRect(new Rect2(sb + new Vector2(bar / 2, 0), new Vector2(bar / 2, 5)), new Color(.9f, .86f, .74f));
        UiStyle.Text(this, sb + new Vector2(0, -6), "500 m", 12, ink);
        var mw = ToWorld(_mouse);
        if (rect.HasPoint(_mouse) && Math.Abs(mw.X) < half && Math.Abs(mw.Y) < half)
        {
            int gx = (int)((mw.X + half) / 1000), gz = (int)((mw.Y + half) / 1000);
            float h = World.HeightAt(mw.X, mw.Y);
            UiStyle.Text(this, new Vector2(Size.X - 20, Size.Y - 14), $"{(char)('A' + gx)}{gz + 1}   ·   {h:0} m above sea", 12, ink, HorizontalAlignment.Right, 0);
        }
    }

    /// <summary>Unseen ground: faded toward the paper colour (a small texture, smoothly stretched).</summary>
    private void DrawFog(Rect2 full)
    {
        if (MapMemory.Current is not { } mem) return;
        if (_fog == null || _fogVersion != mem.Version)
        {
            var img = Image.CreateEmpty(mem.Cells, mem.Cells, false, Image.Format.Rgba8);
            var fade = new Color(.84f, .80f, .68f, .55f);
            for (int j = 0; j < mem.Cells; j++)
                for (int i = 0; i < mem.Cells; i++)
                    img.SetPixel(i, j, mem.SeenCell(i, j) ? new Color(0, 0, 0, 0) : fade);
            _fog = ImageTexture.CreateFromImage(img);
            _fogVersion = mem.Version;
        }
        DrawTextureRect(_fog, full, false);
    }

    private void DrawPlaces(Color ink)
    {
        if (Towns == null) return;
        Settlements.Site? hover = null;
        foreach (var s in Towns.Sites)
        {
            bool known = s.Kind is "town" or "village" || (MapMemory.Current?.Seen(s.Centre.X, s.Centre.Z) ?? true);
            if (!known) continue;
            var p = ToScreen(s.Centre.X, s.Centre.Z);
            if (p.X < -100 || p.Y < -40 || p.X > Size.X + 100 || p.Y > Size.Y + 40) continue;
            if (s.Kind == "outpost")
            {
                var red = new Color(.75f, .15f, .1f);
                DrawColoredPolygon(new[] { p + new Vector2(0, -8), p + new Vector2(7, 5), p + new Vector2(-7, 5) }, red);
                if (_mpp < 6) UiStyle.Text(this, p + new Vector2(10, 4), s.Name.ToUpper(), 11, red, bold: true);
            }
            else
            {
                // Houses: little squares in a cluster, more for bigger places.
                int n = s.Kind switch { "town" => 7, "village" => 4, _ => 2 };
                float r = s.Kind == "town" ? 9 : 5;
                for (int k = 0; k < n; k++)
                {
                    float a = k * 2.4f, d = k == 0 ? 0 : r * (.5f + .5f * (k % 2));
                    var q = p + new Vector2(MathF.Cos(a), MathF.Sin(a)) * d;
                    DrawRect(new Rect2(q - Vector2.One * 2.6f, Vector2.One * 5.2f), ink);
                }
                // How they feel about you, as a ring.
                var level = Support.LevelAt(s.Index);
                var ring = level switch
                {
                    Support.Level.Hostile => new Color(.75f, .15f, .1f), Support.Level.Wary => new Color(.55f, .5f, .4f),
                    Support.Level.Friendly => new Color(.25f, .55f, .25f), _ => new Color(.15f, .45f, .7f),
                };
                DrawArc(p, r + 6, 0, MathF.Tau, 28, ring, 2.5f);
                int size = s.Kind switch { "town" => 17, "village" => 14, _ => 12 };
                if (s.Kind != "hamlet" || _mpp < 5)
                    UiStyle.Text(this, p + new Vector2(r + 10, 5), s.Kind == "town" ? s.Name.ToUpper() : s.Name, size, ink, bold: s.Kind != "hamlet");
            }
            if (p.DistanceTo(_mouse) < 14) hover = s;
        }
        if (hover != null)
        {
            var p = ToScreen(hover.Centre.X, hover.Centre.Z) + new Vector2(16, 16);
            string line2 = hover.Kind == "outpost" ? "enemy checkpoint" : $"{hover.Describe}  ·  {Support.Describe(Support.LevelAt(hover.Index))}";
            float dist = new Vector2(hover.Centre.X - Player.GlobalPosition.X, hover.Centre.Z - Player.GlobalPosition.Z).Length();
            var box = new Rect2(p, new Vector2(260, 64));
            DrawRect(box, new Color(.1f, .1f, .08f, .85f));
            UiStyle.Text(this, p + new Vector2(10, 22), hover.Name, 16, UiStyle.Ink, bold: true);
            UiStyle.Text(this, p + new Vector2(10, 42), line2, 12, UiStyle.Dim);
            UiStyle.Text(this, p + new Vector2(10, 58), $"{dist / 1000:0.0} km away", 12, UiStyle.Dim);
        }
    }
}
