using Godot;

namespace DeadRecon.UI;

/// <summary>
/// The interface's look: one font (Bahnschrift, the DIN-style face that ships with Windows; a plain sans
/// elsewhere), a field-kit palette, and a few drawing helpers shared by the HUD and the kit screen.
/// </summary>
public static class UiStyle
{
    private static Font? _font, _bold;

    public static Font Font => _font ??= new SystemFont { FontNames = new[] { "Bahnschrift", "DIN Alternate", "Segoe UI", "Arial" }, FontWeight = 400 };
    public static Font Bold => _bold ??= new SystemFont { FontNames = new[] { "Bahnschrift", "DIN Alternate", "Segoe UI", "Arial" }, FontWeight = 700 };

    public static readonly Color Ink = new(.93f, .91f, .84f);
    public static readonly Color Dim = new(.93f, .91f, .84f, .5f);
    public static readonly Color Faint = new(.93f, .91f, .84f, .14f);
    public static readonly Color Amber = new(1, .78f, .3f);
    public static readonly Color Danger = new(1, .3f, .22f);
    public static readonly Color Good = new(.55f, .85f, .45f);
    public static readonly Color Panel = new(.045f, .06f, .045f, .9f);
    public static readonly Color Cell = new(.12f, .14f, .11f, .85f);
    public static readonly Color Olive = new(.42f, .47f, .28f);

    /// <summary>Tint behind an item, by kind.</summary>
    public static Color KindColour(DeadRecon.Gear.ItemKind k) => k switch
    {
        DeadRecon.Gear.ItemKind.Magazine => new Color(.30f, .33f, .22f),
        DeadRecon.Gear.ItemKind.Medical => new Color(.42f, .22f, .20f),
        DeadRecon.Gear.ItemKind.Attachment => new Color(.24f, .28f, .31f),
        _ => new Color(.45f, .36f, .16f),
    };

    public static void Text(CanvasItem c, Vector2 at, string s, int size, Color col, HorizontalAlignment align = HorizontalAlignment.Left, float width = -1, bool bold = false)
    {
        var f = bold ? Bold : Font;
        c.DrawString(f, at + new Vector2(1.2f, 1.2f), s, align, width, size, new Color(0, 0, 0, .55f * col.A));
        c.DrawString(f, at, s, align, width, size, col);
    }

    /// <summary>Hand-drawn icons for things that have no 3D model (dressings, rice, intel).</summary>
    public static void FlatIcon(CanvasItem c, string id, Rect2 r)
    {
        var mid = r.GetCenter();
        float s = Mathf.Min(r.Size.X, r.Size.Y);
        switch (id)
        {
            case "bandage":
            {
                var roll = new Rect2(mid - new Vector2(s * .32f, s * .26f), new Vector2(s * .64f, s * .52f));
                c.DrawRect(roll, new Color(.9f, .88f, .82f));
                c.DrawRect(new Rect2(roll.Position, new Vector2(roll.Size.X * .22f, roll.Size.Y)), new Color(.78f, .76f, .7f));
                c.DrawRect(new Rect2(mid - new Vector2(s * .05f, s * .17f) + new Vector2(s * .05f, 0), new Vector2(s * .1f, s * .34f)), Danger);
                c.DrawRect(new Rect2(mid - new Vector2(s * .17f, s * .05f) + new Vector2(s * .05f, 0), new Vector2(s * .34f, s * .1f)), Danger);
                break;
            }
            case "rice":
            {
                var pts = new[] { mid + new Vector2(0, -s * .34f), mid + new Vector2(s * .34f, 0), mid + new Vector2(0, s * .34f), mid + new Vector2(-s * .34f, 0) };
                c.DrawColoredPolygon(pts, new Color(.28f, .5f, .2f));
                c.DrawPolyline(new[] { pts[0], pts[1], pts[2], pts[3], pts[0] }, new Color(.18f, .34f, .12f), 2);
                c.DrawLine(pts[0], pts[2], new Color(.8f, .72f, .5f), 2);
                c.DrawLine(pts[1], pts[3], new Color(.8f, .72f, .5f), 2);
                break;
            }
            case "intel":
            {
                var folder = new Rect2(mid - new Vector2(s * .38f, s * .28f), new Vector2(s * .76f, s * .56f));
                c.DrawRect(new Rect2(folder.Position - new Vector2(0, s * .07f), new Vector2(s * .28f, s * .1f)), new Color(.72f, .6f, .38f));
                c.DrawRect(folder, new Color(.8f, .68f, .44f));
                c.DrawRect(new Rect2(folder.Position + new Vector2(s * .1f, s * .2f), new Vector2(s * .56f, s * .14f)), new Color(.7f, .15f, .1f, .85f));
                c.DrawString(Bold, folder.Position + new Vector2(s * .1f, s * .31f), "SECRET", HorizontalAlignment.Center, s * .56f, (int)(s * .11f), new Color(1, .9f, .85f));
                break;
            }
        }
    }

    /// <summary>A labelled bar from 0 to 1 (stats).</summary>
    public static void Bar(CanvasItem c, Vector2 at, float width, string label, float value, string note, Color col)
    {
        Text(c, at, label, 13, Dim);
        var b = new Rect2(at + new Vector2(110, -10), new Vector2(width - 200, 8));
        c.DrawRect(b, Faint);
        c.DrawRect(new Rect2(b.Position, new Vector2(b.Size.X * Mathf.Clamp(value, 0, 1), b.Size.Y)), col);
        Text(c, at + new Vector2(width - 80, 0), note, 13, Ink, HorizontalAlignment.Right, 80);
    }
}
