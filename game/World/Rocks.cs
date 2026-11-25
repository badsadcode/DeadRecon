using System;
using System.Collections.Generic;
using Godot;

namespace DeadRecon.World;

/// <summary>
/// Rocks: boulders, slabs and jagged outcrops, made here (no assets) and scattered over the baked world. Many on
/// steep ground and along rivers, a few on the flat, in clusters; none on roads, fields, villages or camps. Solid:
/// they stop bullets, block sight and you can hide behind them. Streamed in cells round the viewer like the trees.
/// </summary>
public partial class Rocks : Node3D
{
    public BakedWorld World = null!;
    public Node3D Viewer = null!;

    private const float CellSize = 64, Reach = 300, Step = 3.2f;
    private const int Variants = 9;
    private readonly ArrayMesh[] _meshes = new ArrayMesh[Variants];
    private readonly Shape3D[] _shapes = new Shape3D[Variants];
    private ShaderMaterial _material = null!;
    private readonly Dictionary<(int, int), Node3D> _cells = new();
    private double _since = 1;
    public int Count { get; private set; }

    /// <summary>Tests: the nearest rock at least this big (position, size).</summary>
    public (Vector3 At, float Size)? Nearest(Vector3 p, float minSize)
    {
        (Vector3, float)? best = null;
        foreach (var cell in _cells.Values)
            foreach (var shape in cell.GetChild(0).GetChildren())
                if (shape is CollisionShape3D cs && cs.Transform.Basis.Scale.X >= minSize && (best == null || cs.Transform.Origin.DistanceTo(p) < best.Value.Item1.DistanceTo(p)))
                    best = (cs.Transform.Origin, cs.Transform.Basis.Scale.X);
        return best;
    }

    public override void _Ready()
    {
        _material = new ShaderMaterial { Shader = GD.Load<Shader>("res://game/World/rock.gdshader") };
        const string Rock = "res://assets/textures/mossy_limestone.png", Moss = "res://assets/textures/creeping_groundcover.png";
        if (ResourceLoader.Exists(Rock) && ResourceLoader.Exists(Moss))
        {
            _material.SetShaderParameter("rock_tex", GD.Load<Texture2D>(Rock));
            _material.SetShaderParameter("moss_tex", GD.Load<Texture2D>(Moss));
            _material.SetShaderParameter("has_textures", true);
        }
        for (int v = 0; v < Variants; v++)
        {
            _meshes[v] = MakeRock(v);
            _meshes[v].SurfaceSetMaterial(0, _material);
            _shapes[v] = _meshes[v].CreateConvexShape(true, true);
        }
    }

    public override void _Process(double delta)
    {
        _material.SetShaderParameter("wetness", Weather.Current?.Wetness ?? 0);
        _since += delta;
        if (_since < .5) return;
        _since = 0;
        var p = Viewer.GlobalPosition;
        int r = (int)MathF.Ceiling(Reach / CellSize);
        int ci = (int)MathF.Floor((p.X + World.Half) / CellSize), cj = (int)MathF.Floor((p.Z + World.Half) / CellSize);
        var keep = new HashSet<(int, int)>();
        int built = 0;
        for (int j = cj - r; j <= cj + r; j++)
            for (int i = ci - r; i <= ci + r; i++)
            {
                float cx = (i + .5f) * CellSize - World.Half, cz = (j + .5f) * CellSize - World.Half;
                if (new Vector2(cx - p.X, cz - p.Z).Length() > Reach + CellSize) continue;
                keep.Add((i, j));
                if (!_cells.ContainsKey((i, j)) && built < 4) { _cells[(i, j)] = Plant(i, j); built++; }
            }
        var drop = new List<(int, int)>();
        foreach (var (k, node) in _cells) if (!keep.Contains(k)) { node.QueueFree(); drop.Add(k); }
        foreach (var k in drop) _cells.Remove(k);
    }

    // ================================================================= placing

    private static uint Hash(int x, int z, uint s)
    {
        uint h = (uint)x * 374761393u + (uint)z * 668265263u + s * 2246822519u;
        h = (h ^ (h >> 13)) * 1274126177u;
        return h ^ (h >> 16);
    }
    private static float Unit(uint h) => (h & 0xFFFFFF) / (float)0x1000000;

    private static float Smooth(float x, float z)
    {
        // Cheap value noise for clusters (~60 m).
        float fx = x / 60f, fz = z / 60f;
        int ix = (int)MathF.Floor(fx), iz = (int)MathF.Floor(fz);
        float u = fx - ix, v = fz - iz;
        u = u * u * (3 - 2 * u); v = v * v * (3 - 2 * v);
        float a = Unit(Hash(ix, iz, 91)), b = Unit(Hash(ix + 1, iz, 91)), c = Unit(Hash(ix, iz + 1, 91)), d = Unit(Hash(ix + 1, iz + 1, 91));
        return (a * (1 - u) + b * u) * (1 - v) + (c * (1 - u) + d * u) * v;
    }

    private Node3D Plant(int ci, int cj)
    {
        var cell = new Node3D();
        AddChild(cell);
        var body = new StaticBody3D();
        cell.AddChild(body);
        var perVariant = new List<Transform3D>[Variants];
        var colours = new List<Color>[Variants];
        for (int v = 0; v < Variants; v++) { perVariant[v] = new(); colours[v] = new(); }
        float x0 = ci * CellSize - World.Half, z0 = cj * CellSize - World.Half;
        int n = (int)(CellSize / Step);
        var towns = Settlements.Current;
        for (int b = 0; b < n; b++)
            for (int a = 0; a < n; a++)
            {
                int gxk = ci * n + a, gzk = cj * n + b;
                float x = x0 + (a + Unit(Hash(gxk, gzk, 1))) * Step, z = z0 + (b + Unit(Hash(gxk, gzk, 2))) * Step;
                if (Math.Abs(x) > World.Half - 2 || Math.Abs(z) > World.Half - 2) continue;
                var (fx, fz) = World.ToSample(x, z);
                int gx = (int)MathF.Round(fx), gz = (int)MathF.Round(fz);
                if (World.IsWet(gx, gz)) continue;
                float dx = World.Ground(gx + 1, gz) - World.Ground(gx - 1, gz), dz = World.Ground(gx, gz + 1) - World.Ground(gx, gz - 1);
                float slope = MathF.Sqrt(dx * dx + dz * dz) / (2 * World.Spacing);       // rise over run
                bool river = World.NearWater(gx, gz, 3);
                float chance = .0025f + Math.Clamp((slope - .35f) * .09f, 0, .07f) + (river ? .05f : 0);
                float cluster = Smooth(x, z);
                chance *= cluster > .68f ? 5 : cluster > .5f ? 1.4f : .5f;
                if (Unit(Hash(gxk, gzk, 3)) > chance) continue;
                if (TreeScatter.InClearing(x, z, 1.1f)) continue;
                if (towns != null && (towns.KeepClear(x, z, 3) || towns.YardAt(x, z) > .05f)) continue;
                if (DeadRecon.Base.HomeBase.Position is { } home && new Vector2(x - home.X, z - home.Z).Length() < DeadRecon.Base.HomeBase.Radius + 3) continue;
                // Size: mostly knee to waist high; on steep ground and in clusters, some big ones.
                float r = Unit(Hash(gxk, gzk, 4));
                float size = river ? .5f + 1.1f * r * r : .45f + (slope > .6f || cluster > .75f ? 3.2f : 1.6f) * r * r * r;
                int v = (int)(Hash(gxk, gzk, 5) % Variants);
                if (river && v >= 6) v %= 4;   // rivers round their stones
                float yaw = Unit(Hash(gxk, gzk, 6)) * MathF.Tau;
                float y = World.HeightAt(x, z);
                // Lean with the ground a little, and sink in so they sit, not perch.
                var up = new Vector3(-dx, 2 * World.Spacing, -dz).Normalized();
                var tilt = Vector3.Up.Lerp(up, .6f).Normalized();
                var axis = Vector3.Up.Cross(tilt);
                var basis = axis.LengthSquared() > 1e-6f ? new Basis(axis.Normalized(), Vector3.Up.AngleTo(tilt)) : Basis.Identity;
                basis = basis * new Basis(Vector3.Up, yaw) * Basis.FromScale(Vector3.One * size);
                var xf = new Transform3D(basis, new Vector3(x, y - size * .22f, z));
                perVariant[v].Add(xf);
                float tone = .85f + .3f * Unit(Hash(gxk, gzk, 7)), warm = (Unit(Hash(gxk, gzk, 8)) - .5f) * .12f;
                colours[v].Add(new Color(tone + warm, tone, tone - warm));
                body.AddChild(new CollisionShape3D { Shape = _shapes[v], Transform = xf });
            }
        int count = 0;
        for (int v = 0; v < Variants; v++)
        {
            if (perVariant[v].Count == 0) continue;
            var mm = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseColors = true, Mesh = _meshes[v], InstanceCount = perVariant[v].Count };
            for (int k = 0; k < perVariant[v].Count; k++) { mm.SetInstanceTransform(k, perVariant[v][k]); mm.SetInstanceColor(k, colours[v][k]); }
            cell.AddChild(new MultiMeshInstance3D { Multimesh = mm });
            count += perVariant[v].Count;
        }
        Count += count;
        cell.TreeExiting += () => Count -= count;
        return cell;
    }

    // ================================================================= making them

    /// <summary>A rock: a subdivided icosahedron pushed about by noise, squashed or stretched by type.
    /// 0-3 boulders, 4-5 slabs, 6-8 jagged.</summary>
    private static ArrayMesh MakeRock(int variant)
    {
        var rng = new Random(variant * 97 + 13);
        var (verts, tris) = Icosphere(2);
        Vector3 squash = variant switch
        {
            < 4 => new Vector3(1 + (float)rng.NextDouble() * .4f, .65f + (float)rng.NextDouble() * .3f, .9f + (float)rng.NextDouble() * .3f),
            < 6 => new Vector3(1.5f, .35f, 1.1f),
            _ => new Vector3(.8f, 1.15f, .75f),
        };
        float rough = variant switch { < 4 => .22f, < 6 => .15f, _ => .38f };
        float seed = variant * 13.7f;
        for (int i = 0; i < verts.Count; i++)
        {
            var p = verts[i];
            float nse = Fbm(p * 1.6f + new Vector3(seed, seed * .7f, seed * 1.3f));
            float facet = variant >= 6 ? MathF.Floor(Fbm(p * 2.4f + new Vector3(seed, 0, 0)) * 4) / 4 * .25f : 0;
            p *= 1 + (nse - .5f) * 2 * rough + facet;
            p *= squash;
            // A flatter underside so it sits on the ground.
            if (p.Y < -.15f) p.Y = -.15f + (p.Y + .15f) * .35f;
            verts[i] = p * .5f;
        }
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        foreach (var t in tris)
        {
            // Jagged rocks: flat-shaded faces; boulders: smooth.
            if (variant >= 6)
            {
                var nrm = (verts[t.Y] - verts[t.X]).Cross(verts[t.Z] - verts[t.X]).Normalized();
                foreach (int k in new[] { t.X, t.Z, t.Y }) { st.SetNormal(-nrm); st.AddVertex(verts[k]); }
            }
            else foreach (int k in new[] { t.X, t.Z, t.Y }) st.AddVertex(verts[k]);
        }
        if (variant < 6) { st.Index(); st.GenerateNormals(); }
        return st.Commit();
    }

    private static (List<Vector3>, List<Vector3I>) Icosphere(int subdivisions)
    {
        float t = (1 + MathF.Sqrt(5)) / 2;
        var v = new List<Vector3>
        {
            new(-1, t, 0), new(1, t, 0), new(-1, -t, 0), new(1, -t, 0), new(0, -1, t), new(0, 1, t),
            new(0, -1, -t), new(0, 1, -t), new(t, 0, -1), new(t, 0, 1), new(-t, 0, -1), new(-t, 0, 1),
        };
        for (int i = 0; i < v.Count; i++) v[i] = v[i].Normalized();
        var f = new List<Vector3I>
        {
            new(0, 11, 5), new(0, 5, 1), new(0, 1, 7), new(0, 7, 10), new(0, 10, 11), new(1, 5, 9), new(5, 11, 4), new(11, 10, 2),
            new(10, 7, 6), new(7, 1, 8), new(3, 9, 4), new(3, 4, 2), new(3, 2, 6), new(3, 6, 8), new(3, 8, 9), new(4, 9, 5),
            new(2, 4, 11), new(6, 2, 10), new(8, 6, 7), new(9, 8, 1),
        };
        for (int s = 0; s < subdivisions; s++)
        {
            var mid = new Dictionary<long, int>();
            int Mid(int a, int b)
            {
                long key = Math.Min(a, b) * 100000L + Math.Max(a, b);
                if (mid.TryGetValue(key, out int m)) return m;
                v.Add(((v[a] + v[b]) / 2).Normalized());
                return mid[key] = v.Count - 1;
            }
            var next = new List<Vector3I>();
            foreach (var tri in f)
            {
                int a = Mid(tri.X, tri.Y), b = Mid(tri.Y, tri.Z), c = Mid(tri.Z, tri.X);
                next.Add(new(tri.X, a, c)); next.Add(new(tri.Y, b, a)); next.Add(new(tri.Z, c, b)); next.Add(new(a, b, c));
            }
            f = next;
        }
        return (v, f);
    }

    private static float Noise3(Vector3 p)
    {
        int ix = (int)MathF.Floor(p.X), iy = (int)MathF.Floor(p.Y), iz = (int)MathF.Floor(p.Z);
        float fx = p.X - ix, fy = p.Y - iy, fz = p.Z - iz;
        fx = fx * fx * (3 - 2 * fx); fy = fy * fy * (3 - 2 * fy); fz = fz * fz * (3 - 2 * fz);
        float H(int x, int y, int z) => Unit(Hash(x + y * 157, z, 33));
        float L(float a, float b, float t) => a + (b - a) * t;
        return L(L(L(H(ix, iy, iz), H(ix + 1, iy, iz), fx), L(H(ix, iy + 1, iz), H(ix + 1, iy + 1, iz), fx), fy),
                 L(L(H(ix, iy, iz + 1), H(ix + 1, iy, iz + 1), fx), L(H(ix, iy + 1, iz + 1), H(ix + 1, iy + 1, iz + 1), fx), fy), fz);
    }

    private static float Fbm(Vector3 p) => Noise3(p) * .55f + Noise3(p * 2.1f + new Vector3(5, 3, 1)) * .3f + Noise3(p * 4.3f + new Vector3(1, 9, 4)) * .15f;
}
