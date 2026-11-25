using System.Collections.Generic;
using Godot;

namespace DeadRecon.World;

/// <summary>
/// Every climbable ladder in the world (watchtowers at checkpoints and at home): where it starts, where it ends,
/// and which way is "away from the ladder" (you climb facing the other way). The walker climbs them.
/// </summary>
public static class Ladders
{
    public sealed record Ladder(Vector3 Bottom, Vector3 Top, Vector3 Out, object Owner);

    private static readonly List<Ladder> All = new();

    public static void Add(Vector3 bottom, Vector3 top, Vector3 outward, object owner) =>
        All.Add(new Ladder(bottom, top, new Vector3(outward.X, 0, outward.Z).Normalized(), owner));

    public static void RemoveOwner(object owner) => All.RemoveAll(l => l.Owner == owner);
    public static void Clear() => All.Clear();
    public static int Count => All.Count;

    /// <summary>A ladder you're standing at (within reach of its rails, between its ends).</summary>
    public static Ladder? At(Vector3 feet)
    {
        foreach (var l in All)
        {
            if (feet.Y < l.Bottom.Y - .6f || feet.Y > l.Top.Y + .3f) continue;
            // Distance from the ladder's line, horizontally, at this height.
            float t = Mathf.Clamp((feet.Y - l.Bottom.Y) / Mathf.Max(.1f, l.Top.Y - l.Bottom.Y), 0, 1);
            var on = l.Bottom.Lerp(l.Top, t);
            var d = new Vector2(feet.X - on.X, feet.Z - on.Z);
            if (d.Length() < .85f) return l;
        }
        return null;
    }

    /// <summary>A ladder whose top you're standing at (on the deck, next to where it comes up).</summary>
    public static Ladder? TopAt(Vector3 feet)
    {
        foreach (var l in All)
        {
            if (Mathf.Abs(feet.Y - l.Top.Y) > .6f) continue;
            var inside = l.Top - l.Out * .55f;                    // a step back from the edge, on the deck
            if (new Vector2(feet.X - inside.X, feet.Z - inside.Z).Length() < .7f) return l;
        }
        return null;
    }

    /// <summary>Tests: the first ladder.</summary>
    public static Ladder? First => All.Count > 0 ? All[0] : null;
    public static Ladder? Of(object owner) => All.Find(l => l.Owner == owner);
}
