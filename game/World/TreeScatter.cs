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
    private const float TreeHeight = 17; // the NoorForm jungle trees are 16-17.5 m at scale 1
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
    private readonly List<float> _trunkRadius = new();    // per variant, measured from the model at body height (scale 1)
    private readonly List<float> _footRadius = new();     // per variant, the root flare near the ground (scale 1)
    private static readonly List<(BaseMaterial3D Mat, float Rough, Color Albedo)> WetMaterials = new();
    /// <summary>The trees' own shaders (leaves.gdshader, bark.gdshader): wet and wind are uniforms.</summary>
    private static readonly List<ShaderMaterial> TreeShaders = new();
    private static Shader? _leafShader, _barkShader;

    /// <summary>Places with no trees (mission camp, landing zone): centre (x, z) and radius. Set before planting.</summary>
    public static readonly List<(Vector2 Centre, float Radius)> Clearings = new();
    public static bool InClearing(float x, float z, float scale = 1)
    {
        foreach (var (c, r) in Clearings) if (new Vector2(x - c.X, z - c.Y).Length() < r * scale) return true;
        return false;
    }

    /// <summary>Weather: wet bark and leaves are darker and glossier.</summary>
    /// <summary>Other materials that should get darker and shinier in the rain (village walls, roofs...).</summary>
    public static void RegisterWet(BaseMaterial3D m) => WetMaterials.Add((m, m.Roughness, m.AlbedoColor));

    /// <summary>Weather: 0 calm .. 1 storm. Trees sway, branches bow, leaves shiver (Noor wind channels).</summary>
    public void SetWind(float strength)
    {
        foreach (var m in TreeShaders) m.SetShaderParameter("wind", strength);
    }

    public void SetWetness(float wet)
    {
        foreach (var m in TreeShaders) m.SetShaderParameter("wet", wet);
        foreach (var (mat, rough, albedo) in WetMaterials)
        {
            mat.Roughness = Mathf.Lerp(rough, .28f, wet);
            mat.AlbedoColor = albedo * new Color(1 - .22f * wet, 1 - .2f * wet, 1 - .18f * wet, 1);
        }
    }
    private readonly Dictionary<(int, int), Cell> _cells = new();
    private double _sinceUpdate = 1;
    private int _cellsPerSide;

    public int TreesShown { get; private set; }
    public int VariantCount => _variants.Count;
    /// <summary>Cells in range not planted yet (0 = the view is complete).</summary>
    public int Pending { get; private set; } = 1;

    /// <summary>One tree that bullets can hurt: where it lives in the MultiMesh, and how much it has left.</summary>
    private sealed class TreeRef
    {
        public (uint, uint, uint) Key;          // stable identity (grid cell + layer salt): survives unloading
        public Part? Part;
        public int Index;
        public Foliage.Plant Plant;
        public float Toughness, Scale, HeightScale, Yaw, TrunkTop;
        public Vector3 Base;
        public bool Felled;
        /// <summary>Damage per 0.5 m height band of the trunk: grouped shots cut, scattered shots only scar.</summary>
        public readonly Dictionary<int, float> Bands = new();
    }

    // Felled trees stay felled (and keep their stump) when their cell is unloaded and planted again: key -> cut height.
    private static readonly Dictionary<(uint, uint, uint), float> FelledTrees = new();
    private const float BandHeight = .5f, Spill = .45f;   // a hit also weakens the bands just above and below by Spill
    private const float WoodToughness = 1100;              // per band: toughness * scale^2 -> sapling ~3-7 grouped hits, big tree ~20-70

    private sealed class Part
    {
        public int Variant;
        public Layer Layer = null!;
        public MultiMeshInstance3D Node = null!;
        public Transform3D[] Trees = Array.Empty<Transform3D>();
        public Color[] Tints = Array.Empty<Color>();
    }

    public static TreeScatter? Current { get; private set; }

    /// <summary>Clear trees from a circle now (home being set up): the cells it touches are replanted without them.</summary>
    public void ClearArea(Vector2 centre, float radius)
    {
        Clearings.Add((centre, radius));
        var drop = new List<(int, int)>();
        foreach (var (key, cell) in _cells)
        {
            var min = new Vector2(key.Item1 * CellSize - World.Half, key.Item2 * CellSize - World.Half);
            var nearest = new Vector2(Math.Clamp(centre.X, min.X, min.X + CellSize), Math.Clamp(centre.Y, min.Y, min.Y + CellSize));
            if (nearest.DistanceTo(centre) > radius) continue;
            foreach (var part in cell.Parts) part.Node.QueueFree();
            foreach (var st in cell.Stumps) { Foliage.Remove((int)st.GetMeta("foliage")); st.QueueFree(); }
            DeadRecon.World.Foliage.Remove(cell.Foliage);
            drop.Add(key);
        }
        foreach (var key in drop) _cells.Remove(key);
        _sinceUpdate = 1;
    }

    private sealed class Cell
    {
        public int Lod = -1;
        public readonly List<Part> Parts = new();
        public int Trees;
        public int Foliage;   // handle in World.Foliage (sight blocking)
        public readonly List<Node3D> Stumps = new();
    }

    public override void _Ready()
    {
        Current = this;
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
            _trunkRadius.Add(MeasureTrunk(lods[0], .4f, 1.8f, .85f));
            _footRadius.Add(MeasureTrunk(lods[0], .02f, .45f, .7f));
        }
        WetMaterials.Clear();
        TreeShaders.Clear();
        Foliage.TrunkHit += OnTrunkHit;
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
            if (!keep.Contains(key)) { foreach (var part in cell.Parts) part.Node.QueueFree(); foreach (var st in cell.Stumps) { Foliage.Remove((int)st.GetMeta("foliage")); st.QueueFree(); } DeadRecon.World.Foliage.Remove(cell.Foliage); drop.Add(key); }
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
        var blockers = new List<Foliage.Plant>();
        var refs = new List<(int Slot, TreeRef Ref)>();
        var stumps = new List<(Transform3D Xf, int Variant, float Cut)>();
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
                if (y > MaxHeight || World.NearWater(gx, gz, 2) || InClearing(x, z)) continue;
                if (Settlements.Current?.KeepClear(x, z, 2.5f) == true) continue;   // roads and villages
                float dx = World.Ground(gx + 1, gz) - World.Ground(gx - 1, gz), dz = World.Ground(gx, gz + 1) - World.Ground(gx, gz - 1);
                float ny = 1 / MathF.Sqrt(1 + (dx * dx + dz * dz) / (4 * World.Spacing * World.Spacing));
                if (ny < MaxSlopeNormalY) continue;

                float scale = layer.MinScale + (layer.MaxScale - layer.MinScale) * Unit(Hash(ax, az, s + 4));
                if (Unit(Hash(ax, az, s + 7)) < layer.GiantChance) scale = 1.7f + .5f * Unit(Hash(ax, az, s + 8)); // emergent giants
                float yaw = Unit(Hash(ax, az, s + 5)) * MathF.Tau;
                var basis = new Basis(Vector3.Up, yaw).Scaled(new Vector3(scale, scale * (.9f + .25f * Unit(Hash(ax, az, s + 9))), scale));
                int k2 = (int)(Hash(ax, az, s + 6) % VariantsPerCell);
                float baseY = World.HeightAt(x, z);
                var key = (ax, az, s);
                if (FelledTrees.TryGetValue(key, out float cutAt))
                {
                    stumps.Add((new Transform3D(basis, new Vector3(x, baseY - .3f * scale, z)), picks[k2], cutAt));
                    continue;
                }
                var treeRef = new TreeRef
                {
                    Key = key, Index = trees[k2].Count, Toughness = WoodToughness * scale * scale, Scale = scale,
                    HeightScale = basis.Y.Length(), Yaw = yaw, Base = new Vector3(x, baseY, z), TrunkTop = TreeHeight * scale * .62f,
                };
                trees[k2].Add(new Transform3D(basis, new Vector3(x, baseY - .3f * scale, z)));
                // For AI sight and bullets: trunk up to where the crown starts, crown as a leafy cylinder. Model is ~17 m tall.
                float height = TreeHeight * scale, trunkR = _trunkRadius[picks[k2]] * scale;
                treeRef.Plant = new Foliage.Plant(x, z, baseY, Math.Max(trunkR, .06f), height * .62f,
                                                  height * .55f, height, 3.2f * scale, layer.LodBias > 0 ? .55f : .35f, treeRef, FootRadius: _footRadius[picks[k2]] * scale);
                blockers.Add(treeRef.Plant);
                refs.Add((k2, treeRef));
                tints[k2].Add(Tint(Hash(ax, az, s + 10), layer.Shade));
            }
            for (int k = 0; k < VariantsPerCell; k++)
            {
                if (trees[k].Count == 0) continue;
                var node = new MultiMeshInstance3D();
                AddChild(node);
                var part = new Part { Variant = picks[k], Layer = layer, Node = node, Trees = trees[k].ToArray(), Tints = tints[k].ToArray() };
                cell.Parts.Add(part);
                cell.Trees += trees[k].Count;
                foreach (var (slot, r) in refs) if (slot == k) r.Part = part;
            }
            refs.Clear();
        }
        cell.Foliage = DeadRecon.World.Foliage.Add(blockers);
        foreach (var (xf, variant, cut) in stumps) cell.Stumps.Add(Stump(xf, variant, cut));
        return cell;
    }

    // ================================================================= bullets vs trees

    public override void _ExitTree() => Foliage.TrunkHit -= OnTrunkHit;

    /// <summary>Raised when a tree comes down: (base position, falling direction).</summary>
    public event Action<Vector3, Vector3>? TreeFelled;
    public int TreesFelled { get; private set; }

    private void OnTrunkHit(Foliage.Plant plant, Vector3 at, Vector3 direction, float damage)
    {
        if (plant.Tag is not TreeRef r || r.Felled || r.Part == null) return;
        float h = at.Y - r.Base.Y;
        int band = (int)MathF.Floor(h / BandHeight);
        void Add(int b, float d) => r.Bands[b] = (r.Bands.TryGetValue(b, out var v) ? v : 0) + d;
        Add(band, damage);
        Add(band - 1, damage * Spill);
        Add(band + 1, damage * Spill);
        if (r.Bands[band] >= r.Toughness)
        {
            // Cut through the middle of the band, but leave at least a knee-high stump and some log to fall.
            float cut = Math.Clamp((band + .5f) * BandHeight, .35f, r.TrunkTop - 1f);
            Fell(r, direction, cut);
        }
    }

    /// <summary>For tests: the most damaged band of a trunk as a share of what cuts it (0..1), or -1.</summary>
    public float WorstBand(Foliage.Plant plant)
    {
        if (plant.Tag is not TreeRef r || r.Bands.Count == 0) return plant.Tag is TreeRef ? 0 : -1;
        float worst = 0;
        foreach (var v in r.Bands.Values) worst = Math.Max(worst, v);
        return worst / r.Toughness;
    }

    private void Fell(TreeRef r, Vector3 direction, float cutHeight)
    {
        r.Felled = true;
        FelledTrees[r.Key] = cutHeight;
        TreesFelled++;
        var part = r.Part!;
        // Hide it in the MultiMesh (now and for every later LOD rebuild).
        var gone = new Transform3D(Basis.Identity.Scaled(Vector3.One * .0001f), r.Base - Vector3.Up * 50);
        part.Trees[r.Index] = gone;
        part.Node.Multimesh?.SetInstanceTransform(r.Index, gone);
        Foliage.RemovePlant(r.Plant);

        var scene = GetTree().CurrentScene;
        float s = r.Scale, height = TreeHeight * r.HeightScale, cut = cutHeight;
        var treeXf = new Transform3D(new Basis(Vector3.Up, r.Yaw).Scaled(new Vector3(s, r.HeightScale, s)), r.Base - Vector3.Up * .3f * s);
        float modelCut = (cut + .3f * s) / r.HeightScale;   // the cut height in the tree model's own space
        var dir = new Vector3(direction.X, 0, direction.Z);
        dir = dir.LengthSquared() > 1e-4f ? dir.Normalized() : Vector3.Forward;

        // The falling part: the tree's own full-detail mesh on a rigid body, cut just above the stump.
        var body = new RigidBody3D
        {
            Mass = 40 + 260 * s * s * s, ContinuousCd = true, CanSleep = true,
            CenterOfMassMode = RigidBody3D.CenterOfMassModeEnum.Custom, CenterOfMass = new Vector3(0, height * .55f, 0),
            PhysicsMaterialOverride = new PhysicsMaterial { Friction = .9f, Bounce = .05f },
            ContactMonitor = true, MaxContactsReported = 4,
        };
        body.AddToGroup("wood");
        var mesh = new MeshInstance3D
        {
            Mesh = Sliced(part.Variant, modelCut, above: true),
            Transform = new Transform3D(treeXf.Basis, new Vector3(0, -cut - .3f * s, 0)),
        };
        body.AddChild(mesh);
        float trunkLen = Math.Max(height * .62f - cut, .5f), trunkR = Math.Max(.28f * s, .07f);
        body.AddChild(new CollisionShape3D { Shape = new CapsuleShape3D { Radius = trunkR, Height = Math.Max(trunkLen, trunkR * 2.2f) }, Position = new Vector3(0, trunkLen * .5f, 0) });
        body.AddChild(new CollisionShape3D { Shape = new SphereShape3D { Radius = 2.4f * s }, Position = new Vector3(0, height * .8f - cut, 0) });
        scene.AddChild(body);
        body.GlobalPosition = r.Base + Vector3.Up * (cut + .05f);
        // Start it tipping toward where the bullets were going (plus a little sideways luck); gravity does the rest.
        // Not an impulse: in its first frame the body's mass isn't computed yet (Godot treats it as 1 kg), which once
        // turned a 19 m tree into a 246 m/s rocket. A starting spin doesn't care about mass.
        var fallDir = (dir + dir.Cross(Vector3.Up) * (float)GD.RandRange(-.25, .25)).Normalized();
        body.AngularVelocity = Vector3.Up.Cross(fallDir) * .45f; // top moves toward fallDir

        // Falling trees hurt whatever they land on (while they're still moving fast).
        double born = Time.GetTicksMsec() / 1000.0;
        body.BodyEntered += other =>
        {
            if (Time.GetTicksMsec() / 1000.0 - born > 6 || body.LinearVelocity.Length() + body.AngularVelocity.Length() * height * .5f < 3) return;
            if (other is DeadRecon.Combat.IDamageable victim)
                victim.TakeHit(60 + 140 * s, other is Node3D n3 ? n3.GlobalPosition + Vector3.Up * 1.7f : body.GlobalPosition, body.GlobalPosition);
        };
        // After it settles, stop simulating it (it stays where it fell).
        GetTree().CreateTimer(20).Timeout += () => { if (IsInstanceValid(body)) body.Freeze = true; };

        var stump = Stump(treeXf, part.Variant, cut);
        foreach (var c in _cells.Values) if (c.Parts.Contains(part)) { c.Stumps.Add(stump); break; }

        var crash = new AudioStreamPlayer3D { Stream = DeadRecon.Combat.Effects.TreeCrash(), UnitSize = 18, MaxDistance = 500, VolumeDb = 4 };
        scene.AddChild(crash);
        crash.GlobalPosition = r.Base + Vector3.Up * 2;
        crash.Play();
        crash.Finished += crash.QueueFree;
        GetTree().CreateTimer(1.9).Timeout += () => DeadRecon.Combat.Hearing.Make(r.Base, 140, this); // the crash, not the creak
        GD.Print($"[trees] felled a tree (scale {s:0.00}, {height:0} m) toward {dir}");
        TreeFelled?.Invoke(r.Base, dir);
    }

    private static StandardMaterial3D? _freshWood;
    private readonly Dictionary<(int, int, bool), ArrayMesh> _slices = new();

    /// <summary>The tree model cut at a height (model space), above or below, with a fresh-wood cap. Cached per 5 cm.</summary>
    private ArrayMesh Sliced(int variant, float modelCut, bool above)
    {
        var key = (variant, (int)MathF.Round(modelCut * 20), above);
        if (_slices.TryGetValue(key, out var mesh)) return mesh;
        _freshWood ??= new StandardMaterial3D { AlbedoColor = new Color(.86f, .74f, .52f), Roughness = .95f };
        return _slices[key] = MeshSlicer.Slice(_variants[variant][0], key.Item2 / 20f, above, _freshWood);
    }

    /// <summary>What's left standing: the model below the cut, where the tree stood. Still a trunk for bullets and eyes.</summary>
    private Node3D Stump(Transform3D treeXf, int variant, float cut)
    {
        float s = treeXf.Basis.X.Length(), hs = treeXf.Basis.Y.Length();
        var stump = new MeshInstance3D { Mesh = Sliced(variant, (cut + .3f * s) / hs, above: false) };
        AddChild(stump);
        stump.GlobalTransform = treeXf;
        var foot = treeXf.Origin + Vector3.Up * .3f * s;
        stump.SetMeta("foliage", Foliage.Add(new[] { new Foliage.Plant(foot.X, foot.Z, foot.Y, Math.Max(.32f * s, .06f), cut, 0, 0, 0, 0) }));
        return stump;
    }

    /// <summary>
    /// How thick the trunk really is where people bump into it: the trunk surface's vertices between 0.4 and 1.8 m,
    /// 85th percentile of their distance from the axis (so the root flare counts, a stray root doesn't).
    /// </summary>
    private static float MeasureTrunk(ArrayMesh mesh, float y0, float y1, float percentile)
    {
        var d = new List<float>();
        for (int s = 0; s < mesh.GetSurfaceCount(); s++)
        {
            var mat = mesh.SurfaceGetMaterial(s);
            if (mat is BaseMaterial3D bm && bm.Transparency != BaseMaterial3D.TransparencyEnum.Disabled) continue; // leaves
            if (mat is ShaderMaterial sm && sm.Shader == _leafShader) continue;
            foreach (var v in mesh.SurfaceGetArrays(s)[(int)Mesh.ArrayType.Vertex].AsVector3Array())
                if (v.Y > y0 && v.Y < y1) d.Add(new Vector2(v.X, v.Z).Length());
        }
        if (d.Count < 8) return .45f;
        d.Sort();
        float r = d[(int)(d.Count * percentile)];
        return Math.Clamp(r, .15f, 2.5f);
    }

    /// <summary>A small random shift in brightness and hue (yellower or bluer greens), darkened by the layer's shade.</summary>
    private static Color Tint(uint h, float shade)
    {
        float bright = (.82f + .3f * Unit(h)) * shade;
        float warm = Unit(h >> 8) - .5f; // -0.5 .. 0.5
        return new Color(bright * (1 + .18f * warm), bright, bright * (1 - .22f * warm));
    }

    /// <summary>NoorForm's standard material as our tree shader: leaves (cut-out cards) get leaves.gdshader with its
    /// wind and light through the leaves; bark gets bark.gdshader. Textures and colours carried over.</summary>
    private static ShaderMaterial? TreeShader(BaseMaterial3D m)
    {
        _leafShader ??= GD.Load<Shader>("res://game/World/leaves.gdshader");
        _barkShader ??= GD.Load<Shader>("res://game/World/bark.gdshader");
        if (_leafShader == null || _barkShader == null) return null;
        bool leaves = m.Transparency != BaseMaterial3D.TransparencyEnum.Disabled;
        var sm = new ShaderMaterial { Shader = leaves ? _leafShader : _barkShader };
        if (m.AlbedoTexture != null) sm.SetShaderParameter("albedo_tex", m.AlbedoTexture);
        sm.SetShaderParameter("albedo", m.AlbedoColor);
        if (leaves) sm.SetShaderParameter("alpha_cut", m.Transparency == BaseMaterial3D.TransparencyEnum.AlphaScissor ? m.AlphaScissorThreshold : .45f);
        else
        {
            sm.SetShaderParameter("rough", m.Roughness);
            if (m.NormalEnabled && m.NormalTexture != null) { sm.SetShaderParameter("normal_tex", m.NormalTexture); sm.SetShaderParameter("has_normal", true); }
        }
        TreeShaders.Add(sm);
        return sm;
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
                    // NoorForm's wind data: TEXCOORD_1 arrives as UV2, TEXCOORD_2 as CUSTOM0. Re-adding a CUSTOM
                    // channel needs its format flags, so they're carried over from the imported surface.
                    long customFlags = 0;
                    if (mesh is ArrayMesh am)
                    {
                        long fmt = (long)am.SurfaceGetFormat(s);
                        int baseShift = (int)Mesh.ArrayFormat.FormatCustomBase, bits = (int)Mesh.ArrayFormat.FormatCustomBits;
                        for (int c = 0; c < 4; c++) customFlags |= fmt & ((long)Mesh.ArrayFormat.FormatCustomMask << (baseShift + c * bits));
                    }
                    for (int c = 0; c < 4; c++)
                        if ((customFlags & ((long)Mesh.ArrayFormat.FormatCustomMask << ((int)Mesh.ArrayFormat.FormatCustomBase + c * (int)Mesh.ArrayFormat.FormatCustomBits))) == 0)
                            arrays[(int)Mesh.ArrayType.Custom0 + c] = default;
                    if (xf != Transform3D.Identity)
                    {
                        var v = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                        var n = arrays[(int)Mesh.ArrayType.Normal].AsVector3Array();
                        for (int k = 0; k < v.Length; k++) v[k] = xf * v[k];
                        for (int k = 0; k < n.Length; k++) n[k] = (xf.Basis * n[k]).Normalized();
                        arrays[(int)Mesh.ArrayType.Vertex] = v;
                        arrays[(int)Mesh.ArrayType.Normal] = n;
                    }
                    result.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays, null, null, (Mesh.ArrayFormat)customFlags);
                    var material = mi.GetActiveMaterial(s);
                    if (material is BaseMaterial3D std && TreeShader(std) is { } shaded) material = shaded;
                    else if (material is BaseMaterial3D bm)
                    {
                        bm = (BaseMaterial3D)bm.Duplicate();
                        // Per-tree tint arrives as the instance colour, which only reaches albedo via vertex colour.
                        bm.VertexColorUseAsAlbedo = true;
                        // Leaf cards and billboards: visible from both sides.
                        if (bm.Transparency != BaseMaterial3D.TransparencyEnum.Disabled) bm.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
                        WetMaterials.Add((bm, bm.Roughness, bm.AlbedoColor));
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
