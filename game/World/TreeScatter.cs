using System;
using System.Collections.Generic;
using Godot;

namespace DeadRecon.World;

/// <summary>
/// Plants the jungle trees from assets/trees on the baked ground, deterministically (same world = same trees).
/// Two layers from the same tree models: the <b>canopy</b> (full-size trees, a few giants) and the <b>understory</b>
/// (saplings at a quarter to half size, darker, drawn one detail level lower). Every tree gets a slight colour shift.
/// The land is cut into square cells; each cell uses a few variants, one MultiMesh per (variant, layer), and the whole
/// cell switches level of detail together.
/// </summary>
public partial class TreeScatter : Node3D
{
    public BakedWorld World = null!;
    public Node3D Viewer = null!;

    private const float CellSize = 256;          // metres per streaming cell
    private const int VariantsPerCell = 3;
    private const float MaxSlopeNormalY = .74f;  // no trees on ground steeper than ~42 degrees
    private const float MaxHeight = 1450;
    /// <summary>Distance (m, viewer to cell square) up to which each tree LOD is used; beyond the last, no trees.</summary>
    private static readonly float[] LodUntil = { 35, 140, 380, 2800 };

    private sealed record Layer(string Name, float Spacing, float Fill, float MinScale, float MaxScale, float GiantChance,
                                int LodBias, float Shade, int HideFromLod, uint Salt);

    private static readonly Layer[] Layers =
    {
        new("canopy", Spacing: 8.5f, Fill: .9f, MinScale: .75f, MaxScale: 1.45f, GiantChance: .06f, LodBias: 0, Shade: 1f, HideFromLod: -1, Salt: 0),
        // Saplings are small: in billboard range (past ~380 m) they are pixels under the canopy, so they aren't drawn.
        new("understory", Spacing: 6f, Fill: .55f, MinScale: .22f, MaxScale: .45f, GiantChance: 0, LodBias: 1, Shade: .72f, HideFromLod: 3, Salt: 1000),
    };

    private readonly List<ArrayMesh[]> _variants = new(); // [variant][lod]
    private readonly Dictionary<(int, int), Cell> _cells = new();
    private double _sinceUpdate = 1;
    private int _cellsPerSide;

    public int TreesShown { get; private set; }
    public int VariantCount => _variants.Count;
    /// <summary>Cells in range not planted yet (0 = the view is complete).</summary>
    public int Pending { get; private set; } = 1;

    private sealed class Part
    {
        public int Variant;
        public Layer Layer = null!;
        public MultiMeshInstance3D Node = null!;
        public Transform3D[] Trees = Array.Empty<Transform3D>();
        public Color[] Tints = Array.Empty<Color>();
    }

    private sealed class Cell
    {
        public int Lod = -1;
        public readonly List<Part> Parts = new();
        public int Trees;
    }

    public override void _Ready()
    {
        for (int v = 1; v <= 99; v++)
        {
            var lods = new ArrayMesh[LodUntil.Length];
            bool ok = true;
            for (int l = 0; l < lods.Length && ok; l++)
            {
                string path = $"res://assets/trees/Jungle_tree_{v:00}_LOD{l}.glb";
                if (!ResourceLoader.Exists(path)) { ok = false; break; }
                lods[l] = MeshFromScene(ResourceLoader.Load<PackedScene>(path));
            }
            if (!ok) break;
            _variants.Add(lods);
        }
        if (_variants.Count == 0) GD.PushWarning("No trees found in res://assets/trees (Jungle_tree_NN_LODn.glb). The jungle will be bald.");
        _cellsPerSide = (int)MathF.Ceiling(World.Size / CellSize);
    }

    public override void _Process(double delta)
    {
        if (_variants.Count == 0) return;
        _sinceUpdate += delta;
        if (_sinceUpdate < .25) return;
        _sinceUpdate = 0;

        var p = Viewer.GlobalPosition;
        int built = 0, shown = 0;
        int reach = (int)MathF.Ceiling(LodUntil[^1] / CellSize) + 1;
        int ci = (int)MathF.Floor((p.X + World.Half) / CellSize), cj = (int)MathF.Floor((p.Z + World.Half) / CellSize);
        var keep = new HashSet<(int, int)>();
        var wanted = new List<(float d, int i, int j, int lod)>();
        for (int j = cj - reach; j <= cj + reach; j++)
        for (int i = ci - reach; i <= ci + reach; i++)
        {
            if (i < 0 || j < 0 || i >= _cellsPerSide || j >= _cellsPerSide) continue;
            float d = Distance(i, j, p);
            int lod = LodFor(d);
            if (lod < 0) continue;
            keep.Add((i, j));
            wanted.Add((d, i, j, lod));
        }
        wanted.Sort((a, b) => a.d.CompareTo(b.d)); // nearest first: plant (and upgrade) what the viewer sees up close first
        int pending = 0;
        foreach (var (_, i, j, lod) in wanted)
        {
            if (!_cells.TryGetValue((i, j), out var cell))
            {
                if (built >= 6) { pending++; continue; } // spread first-time planting over several updates
                cell = Plant(i, j);
                _cells[(i, j)] = cell;
                built++;
            }
            if (cell.Lod != lod) SetLod(cell, lod);
            shown += cell.Trees;
        }
        Pending = pending;

        var drop = new List<(int, int)>();
        foreach (var (key, cell) in _cells)
            if (!keep.Contains(key)) { foreach (var part in cell.Parts) part.Node.QueueFree(); drop.Add(key); }
        foreach (var key in drop) _cells.Remove(key);
        TreesShown = shown;
    }

    private static int LodFor(float d)
    {
        for (int l = 0; l < LodUntil.Length; l++) if (d < LodUntil[l]) return l;
        return -1;
    }

    private float Distance(int i, int j, Vector3 p)
    {
        float x0 = i * CellSize - World.Half, z0 = j * CellSize - World.Half;
        float dx = Math.Max(0, Math.Max(x0 - p.X, p.X - (x0 + CellSize)));
        float dz = Math.Max(0, Math.Max(z0 - p.Z, p.Z - (z0 + CellSize)));
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    /// <summary>
    /// Switch a cell's detail level. Godot 4.7 doesn't redraw a MultiMesh whose mesh is replaced after its instances
    /// were set, so each switch builds a fresh MultiMesh (a few hundred transforms; cheap at 4 Hz).
    /// </summary>
    private void SetLod(Cell cell, int lod)
    {
        cell.Lod = lod;
        foreach (var part in cell.Parts)
        {
            bool hidden = part.Layer.HideFromLod >= 0 && lod >= part.Layer.HideFromLod;
            part.Node.Visible = !hidden;
            if (hidden) { part.Node.Multimesh = null; continue; }
            int l = Math.Min(lod + part.Layer.LodBias, LodUntil.Length - 1);
            part.Node.Multimesh = MakeMultiMesh(_variants[part.Variant][l], part.Trees, part.Tints);
            part.Node.CastShadow = l <= 1 ? GeometryInstance3D.ShadowCastingSetting.On : GeometryInstance3D.ShadowCastingSetting.Off;
        }
    }

    private static MultiMesh MakeMultiMesh(Mesh mesh, Transform3D[] trees, Color[] tints)
    {
        var mm = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseColors = true, Mesh = mesh, InstanceCount = trees.Length };
        for (int t = 0; t < trees.Length; t++) { mm.SetInstanceTransform(t, trees[t]); mm.SetInstanceColor(t, tints[t]); }
        return mm;
    }

    private Cell Plant(int i, int j)
    {
        var cell = new Cell();
        var picks = new int[VariantsPerCell];
        for (int k = 0; k < VariantsPerCell; k++) picks[k] = (int)(Hash((uint)i, (uint)j, 11u + (uint)k) % (uint)_variants.Count);
        float x0 = i * CellSize - World.Half, z0 = j * CellSize - World.Half;

        foreach (var layer in Layers)
        {
            var trees = new List<Transform3D>[VariantsPerCell];
            var tints = new List<Color>[VariantsPerCell];
            for (int k = 0; k < VariantsPerCell; k++) { trees[k] = new(); tints[k] = new(); }
            int per = (int)(CellSize / layer.Spacing);
            for (int b = 0; b < per; b++)
            for (int a = 0; a < per; a++)
            {
                uint ax = (uint)(i * per + a), az = (uint)(j * per + b), s = layer.Salt;
                if (Unit(Hash(ax, az, s + 1)) > layer.Fill) continue;
                float x = x0 + (a + Unit(Hash(ax, az, s + 2))) * layer.Spacing;
                float z = z0 + (b + Unit(Hash(ax, az, s + 3))) * layer.Spacing;
                if (x < -World.Half || z < -World.Half || x > World.Half || z > World.Half) continue;
                var (fx, fz) = World.ToSample(x, z);
                int gx = (int)MathF.Round(fx), gz = (int)MathF.Round(fz);
                float y = World.Ground(gx, gz);
                if (y > MaxHeight || World.NearWater(gx, gz, 2)) continue;
                float dx = World.Ground(gx + 1, gz) - World.Ground(gx - 1, gz), dz = World.Ground(gx, gz + 1) - World.Ground(gx, gz - 1);
                float ny = 1 / MathF.Sqrt(1 + (dx * dx + dz * dz) / (4 * World.Spacing * World.Spacing));
                if (ny < MaxSlopeNormalY) continue;

                float scale = layer.MinScale + (layer.MaxScale - layer.MinScale) * Unit(Hash(ax, az, s + 4));
                if (Unit(Hash(ax, az, s + 7)) < layer.GiantChance) scale = 1.7f + .5f * Unit(Hash(ax, az, s + 8)); // emergent giants
                float yaw = Unit(Hash(ax, az, s + 5)) * MathF.Tau;
                var basis = new Basis(Vector3.Up, yaw).Scaled(new Vector3(scale, scale * (.9f + .25f * Unit(Hash(ax, az, s + 9))), scale));
                int k2 = (int)(Hash(ax, az, s + 6) % VariantsPerCell);
                trees[k2].Add(new Transform3D(basis, new Vector3(x, World.HeightAt(x, z) - .3f * scale, z)));
                tints[k2].Add(Tint(Hash(ax, az, s + 10), layer.Shade));
            }
            for (int k = 0; k < VariantsPerCell; k++)
            {
                if (trees[k].Count == 0) continue;
                var node = new MultiMeshInstance3D();
                AddChild(node);
                cell.Parts.Add(new Part { Variant = picks[k], Layer = layer, Node = node, Trees = trees[k].ToArray(), Tints = tints[k].ToArray() });
                cell.Trees += trees[k].Count;
            }
        }
        return cell;
    }

    /// <summary>A small random shift in brightness and hue (yellower or bluer greens), darkened by the layer's shade.</summary>
    private static Color Tint(uint h, float shade)
    {
        float bright = (.82f + .3f * Unit(h)) * shade;
        float warm = Unit(h >> 8) - .5f; // -0.5 .. 0.5
        return new Color(bright * (1 + .18f * warm), bright, bright * (1 - .22f * warm));
    }

    /// <summary>Merge every MeshInstance3D of an imported tree scene into one mesh (one surface per material).</summary>
    private static ArrayMesh MeshFromScene(PackedScene scene)
    {
        var root = scene.Instantiate<Node>();
        var result = new ArrayMesh();
        void Walk(Node node, Transform3D parent)
        {
            var xf = node is Node3D n3 ? parent * n3.Transform : parent;
            if (node is MeshInstance3D { Mesh: { } mesh } mi)
            {
                for (int s = 0; s < mesh.GetSurfaceCount(); s++)
                {
                    var arrays = mesh.SurfaceGetArrays(s);
                    // NoorForm puts wind data in CUSTOM channels; re-adding those needs their format flags. Wind
                    // isn't animated yet, so drop them (TODO: keep them when the wind shader exists).
                    for (int c = (int)Mesh.ArrayType.Custom0; c <= (int)Mesh.ArrayType.Custom3; c++) arrays[c] = default;
                    if (xf != Transform3D.Identity)
                    {
                        var v = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                        var n = arrays[(int)Mesh.ArrayType.Normal].AsVector3Array();
                        for (int k = 0; k < v.Length; k++) v[k] = xf * v[k];
                        for (int k = 0; k < n.Length; k++) n[k] = (xf.Basis * n[k]).Normalized();
                        arrays[(int)Mesh.ArrayType.Vertex] = v;
                        arrays[(int)Mesh.ArrayType.Normal] = n;
                    }
                    result.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
                    var material = mi.GetActiveMaterial(s);
                    if (material is BaseMaterial3D bm)
                    {
                        bm = (BaseMaterial3D)bm.Duplicate();
                        // Per-tree tint arrives as the instance colour, which only reaches albedo via vertex colour.
                        bm.VertexColorUseAsAlbedo = true;
                        // Leaf cards and billboards: visible from both sides.
                        if (bm.Transparency != BaseMaterial3D.TransparencyEnum.Disabled) bm.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
                        material = bm;
                    }
                    result.SurfaceSetMaterial(result.GetSurfaceCount() - 1, material);
                }
            }
            foreach (var child in node.GetChildren()) Walk(child, xf);
        }
        Walk(root, Transform3D.Identity);
        root.Free();
        return result;
    }

    private static float Unit(uint h) => (h & 0xFFFFFF) / (float)0x1000000;

    private static uint Hash(uint x, uint z, uint salt)
    {
        uint h = x * 374761393u + z * 668265263u + salt * 2246822519u;
        h = (h ^ (h >> 13)) * 1274126177u;
        h ^= h >> 16;
        h *= 2654435761u;
        return h ^ (h >> 15);
    }
}
