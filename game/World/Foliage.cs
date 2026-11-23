using System;
using System.Collections.Generic;
using Godot;

namespace DeadRecon.World;

/// <summary>
/// What plants are in the way, for AI sight (not for physics or rendering). TreeScatter and GroundCover register
/// every plant they place; <see cref="Transmission"/> says how much of a sight line gets through.
/// <para>Each plant is a solid trunk (a vertical cylinder that blocks completely) plus a leafy volume (a vertical
/// cylinder between two heights that lets some light through, per metre travelled inside it).</para>
/// Plants are registered per streaming cell (owner) and dropped with it. Main thread only.
/// </summary>
public static class Foliage
{
    public readonly record struct Plant(
        float X, float Z, float BaseY,
        float TrunkRadius, float TrunkTop,               // metres above BaseY; 0 radius = no trunk
        float LeafBottom, float LeafTop, float LeafRadius,
        float Density);                                  // optical depth per metre inside the leaves

    private const float BucketSize = 4;
    private static readonly Dictionary<(int, int), List<(int Owner, Plant P)>> Buckets = new();
    private static readonly Dictionary<int, List<(int, int)>> OwnerBuckets = new();
    private static int _nextOwner = 1;

    public static int Count { get; private set; }

    /// <summary>Register a batch of plants (one streaming cell); returns a handle for <see cref="Remove"/>.</summary>
    public static int Add(IEnumerable<Plant> plants)
    {
        int owner = _nextOwner++;
        var keys = new HashSet<(int, int)>();
        foreach (var p in plants)
        {
            float reach = Math.Max(p.LeafRadius, p.TrunkRadius);
            int x0 = Key(p.X - reach), x1 = Key(p.X + reach), z0 = Key(p.Z - reach), z1 = Key(p.Z + reach);
            for (int bz = z0; bz <= z1; bz++)
            for (int bx = x0; bx <= x1; bx++)
            {
                if (!Buckets.TryGetValue((bx, bz), out var list)) Buckets[(bx, bz)] = list = new();
                list.Add((owner, p));
                keys.Add((bx, bz));
            }
            Count++;
        }
        OwnerBuckets[owner] = new List<(int, int)>(keys);
        return owner;
    }

    public static void Remove(int owner)
    {
        if (!OwnerBuckets.Remove(owner, out var keys)) return;
        var seen = new HashSet<Plant>();
        foreach (var key in keys)
        {
            if (!Buckets.TryGetValue(key, out var list)) continue;
            for (int i = list.Count - 1; i >= 0; i--)
                if (list[i].Owner == owner) { if (seen.Add(list[i].P)) Count--; list.RemoveAt(i); }
            if (list.Count == 0) Buckets.Remove(key);
        }
    }

    /// <summary>
    /// 0..1: how much of the straight line from <paramref name="a"/> to <paramref name="b"/> gets through the plants.
    /// 0 = a trunk is in the way (or the leaves are thick), 1 = clear.
    /// </summary>
    public static float Transmission(Vector3 a, Vector3 b)
    {
        var d = b - a;
        float len = new Vector2(d.X, d.Z).Length();
        if (len < .01f) return 1;
        // Candidate plants: every bucket the line passes over (plants are filed in all buckets they touch).
        var candidates = new HashSet<Plant>();
        int steps = (int)MathF.Ceiling(len / (BucketSize * .5f));
        var lastKey = (int.MinValue, int.MinValue);
        for (int s = 0; s <= steps; s++)
        {
            float t = s / (float)steps;
            var key = (Key(a.X + d.X * t), Key(a.Z + d.Z * t));
            if (key == lastKey) continue;
            lastKey = key;
            if (Buckets.TryGetValue(key, out var list)) foreach (var (_, p) in list) candidates.Add(p);
        }

        float depth = 0;
        foreach (var p in candidates)
        {
            // Closest approach of the line (in plan view) to the plant's axis.
            float t = Math.Clamp(((p.X - a.X) * d.X + (p.Z - a.Z) * d.Z) / (len * len), 0, 1);
            float cx = a.X + d.X * t - p.X, cz = a.Z + d.Z * t - p.Z;
            float dist = MathF.Sqrt(cx * cx + cz * cz);
            // Ignore the plants the looker and the target are standing in the middle of (their own fern, the
            // tree they lean on is still fair game because it's a trunk at > its radius).
            if (t <= 0 || t >= 1) continue;
            float h = a.Y + d.Y * t - p.BaseY; // line height above the plant's ground
            if (p.TrunkRadius > 0 && dist < p.TrunkRadius && h > 0 && h < p.TrunkTop) return 0;
            if (dist < p.LeafRadius && h > p.LeafBottom && h < p.LeafTop)
            {
                float chord = 2 * MathF.Sqrt(p.LeafRadius * p.LeafRadius - dist * dist);
                depth += chord * p.Density;
            }
        }
        return MathF.Exp(-depth);
    }

    private static int Key(float v) => (int)MathF.Floor(v / BucketSize);
}
