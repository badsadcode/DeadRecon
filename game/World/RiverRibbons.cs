using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Godot;

namespace DeadRecon.World;

/// <summary>
/// River water the way badsadcode/river builds it: a ribbon of surface along each river's centre line
/// (world/&lt;name&gt;/river_lines.json, written by the WorldBaker's RiverCarver from the generator's rivers, every 2 m:
/// position, smoothed surface, width, depth), drawn with river.gdshader. Per vertex the shader gets River's data: the
/// current running along the river (Manning's law, as River's FlowSolver), the depth of the rounded bed under it,
/// foam where it's fast, and the distance to the bank. The grid water (TerrainStreamer) leaves river samples alone.
/// Built once, in ~120 m pieces so the far ones are culled.
/// </summary>
public partial class RiverRibbons : Node3D
{
    public BakedWorld World = null!;
    public Material Material = null!;

    public sealed record Line(float[] X, float[] Z, float[] S, float[] W, float[] D);
    public static List<Line> Lines { get; private set; } = new();

    /// <summary>Across the river: offsets in half-widths. The outer pair sits just under the bank so the edge
    /// meets the ground instead of floating.</summary>
    private static readonly float[] Across = { -1.25f, -1f, -.8f, -.55f, -.25f, 0, .25f, .55f, .8f, 1f, 1.25f };
    private const int Piece = 60;   // cross-sections per mesh (2 m apart)

    public static List<Line> Load(string dir)
    {
        var path = Path.Combine(dir, "river_lines.json");
        var list = new List<Line>();
        if (!File.Exists(path)) return list;
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var l in doc.RootElement.GetProperty("lines").EnumerateArray())
        {
            float[] A(string k) { var e = l.GetProperty(k); var a = new float[e.GetArrayLength()]; int i = 0; foreach (var v in e.EnumerateArray()) a[i++] = v.GetSingle(); return a; }
            list.Add(new Line(A("x"), A("z"), A("s"), A("w"), A("d")));
        }
        return Lines = list;
    }

    public override void _Ready()
    {
        int pieces = 0;
        foreach (var l in Lines)
            for (int start = 0; start < l.X.Length - 1; start += Piece)
            {
                int end = Math.Min(l.X.Length - 1, start + Piece);
                AddChild(new MeshInstance3D { Mesh = Build(l, start, end), MaterialOverride = Material, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
                pieces++;
            }
        GD.Print($"[rivers] {Lines.Count} rivers as {pieces} water ribbons");
    }

    private static ArrayMesh Build(Line l, int from, int to)
    {
        int n = Across.Length, rows = to - from + 1;
        var m = new MeshData(rows * n, (rows - 1) * (n - 1) * 6);
        for (int i = from; i <= to; i++)
        {
            int a = Math.Max(0, i - 1), b = Math.Min(l.X.Length - 1, i + 1);
            var tan = new Vector2(l.X[b] - l.X[a], l.Z[b] - l.Z[a]);
            tan = tan.LengthSquared() > 1e-6f ? tan.Normalized() : Vector2.Right;
            var side = new Vector2(-tan.Y, tan.X);
            // Slope along the river over ~40 m (the surface is already smoothed), and River's Manning current.
            int sa = Math.Max(0, i - 10), sb = Math.Min(l.X.Length - 1, i + 10);
            float run = Math.Max(2, (sb - sa) * 2f);
            float slope = Math.Clamp((l.S[sa] - l.S[sb]) / run, 0, .08f);
            float hw = l.W[i] / 2, depth = l.D[i];
            foreach (float o in Across)
            {
                float u = Math.Min(1, Math.Abs(o));
                float d = depth * MathF.Pow(1 - u * u, .8f);                       // the carved bed's depth here
                float h = MathF.Max(d, .12f);
                float speed = Math.Min(MathF.Pow(h, 2f / 3f) * MathF.Sqrt(slope) / .05f, 2.4f) * (1 - .6f * u * u);  // slower at the banks
                float froude = speed / MathF.Sqrt(9.81f * h);
                float foam = Smooth(1.0f, 1.7f, froude) * .6f * Smooth(.3f, .7f, d);
                var flow = tan * speed;
                float shore = Math.Abs(o) * hw - hw;                                    // m, negative in the water
                var p = new Vector2(l.X[i], l.Z[i]) + side * (o * hw);
                // The edge row sits under the bank (which starts at the waterline): no sliver of water above it.
                float y = l.S[i] - (Math.Abs(o) > 1 ? .45f : Math.Abs(o) >= 1 ? .08f : 0);
                m.Add(new Vector3(p.X, y, p.Y), Vector3.Up, new Color(flow.X / 6 + .5f, flow.Y / 6 + .5f, d / 8, foam), new Vector2(shore, 0));
            }
        }
        for (int r = 0; r < rows - 1; r++)
            for (int c = 0; c < n - 1; c++)
            {
                int a = r * n + c, b = a + 1, cc = a + n, d = cc + 1;
                m.Tri(a, cc, b); m.Tri(b, cc, d);
            }
        return m.ToMesh();
    }

    private static float Smooth(float a, float b, float x) { float t = Math.Clamp((x - a) / (b - a), 0, 1); return t * t * (3 - 2 * t); }
}
