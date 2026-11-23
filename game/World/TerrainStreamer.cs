using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;

namespace DeadRecon.World;

/// <summary>
/// Shows the baked world around a viewer: one mesh per baked tile, rebuilt at a coarser sample step the further
/// away it is (built on worker threads), plus water surfaces and, close to the viewer, collision.
/// Neighbouring tiles at different steps can leave hairline cracks; each tile hangs a "skirt" below its edge to hide them.
/// </summary>
public partial class TerrainStreamer : Node3D
{
    public BakedWorld World = null!;
    public Node3D Viewer = null!;

    /// <summary>Sample step per detail level (1 = every baked sample) and the distance (m) up to which it is used.</summary>
    private static readonly (int Step, float Until)[] Levels = { (1, 350), (2, 1200), (4, 3000), (8, float.MaxValue) };
    private const float CollisionIn = 300, CollisionOut = 500;
    private const int MaxBuilding = 4;

    private sealed class Tile
    {
        public int Tx, Tz;
        public int Step = -1, Building = -1;
        public MeshInstance3D? Ground, Water;
        public StaticBody3D? Body;
    }

    private sealed record Built(Tile Tile, int Step, MeshData Ground, MeshData? Water);

    private readonly List<Tile> _tiles = new();
    private readonly ConcurrentQueue<Built> _done = new();
    private int _building;
    private double _sinceUpdate = 1;
    private StandardMaterial3D _groundMaterial = null!, _waterMaterial = null!;

    public int TilesAtFullDetail { get; private set; }
    public bool Settled => _building == 0 && _done.IsEmpty && _tiles.TrueForAll(t => t.Step == Desired(t));

    public override void _Ready()
    {
        _groundMaterial = new StandardMaterial3D
        {
            VertexColorUseAsAlbedo = true, Roughness = .95f, CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };
        _waterMaterial = new StandardMaterial3D
        {
            AlbedoColor = new Color(.10f, .17f, .13f, .82f), Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            Roughness = .06f, Metallic = .15f, CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };
        for (int tz = 0; tz < World.TilesPerSide; tz++)
        for (int tx = 0; tx < World.TilesPerSide; tx++)
            _tiles.Add(new Tile { Tx = tx, Tz = tz });
    }

    /// <summary>Is there collision under this world position yet? (the walker waits for it)</summary>
    public bool HasCollisionAt(Vector3 p)
    {
        int cells = World.TileSamples - 1;
        var (gx, gz) = World.ToSample(p.X, p.Z);
        int tx = Math.Clamp((int)gx / cells, 0, World.TilesPerSide - 1), tz = Math.Clamp((int)gz / cells, 0, World.TilesPerSide - 1);
        return _tiles[tz * World.TilesPerSide + tx].Body != null;
    }

    public override void _Process(double delta)
    {
        for (int i = 0; i < 2 && _done.TryDequeue(out var built); i++) Apply(built);

        _sinceUpdate += delta;
        if (_sinceUpdate < .2) return;
        _sinceUpdate = 0;

        var order = new List<Tile>(_tiles);
        order.Sort((a, b) => Distance(a).CompareTo(Distance(b)));
        int full = 0;
        foreach (var t in order)
        {
            int want = Desired(t);
            if (want == 1) full++;
            if (want != t.Step && t.Building < 0 && _building < MaxBuilding)
            {
                t.Building = want;
                _building++;
                var tile = t;
                Task.Run(() =>
                {
                    try { _done.Enqueue(new Built(tile, want, BuildGround(tile, want), BuildWater(tile, Math.Max(want, 2)))); }
                    catch (Exception e) { GD.PushError($"Terrain tile {tile.Tx},{tile.Tz}: {e}"); }
                });
            }
            float d = Distance(t);
            if (d < CollisionIn && t.Body == null) AddCollision(t);
            else if (d > CollisionOut && t.Body != null) { t.Body.QueueFree(); t.Body = null; }
        }
        TilesAtFullDetail = full;
    }

    private int Desired(Tile t)
    {
        float d = Distance(t);
        foreach (var (step, until) in Levels) if (d < until) return step;
        return Levels[^1].Step;
    }

    /// <summary>Horizontal distance from the viewer to the tile's square (0 inside it).</summary>
    private float Distance(Tile t)
    {
        float size = (World.TileSamples - 1) * World.Spacing;
        float x0 = t.Tx * size - World.Half, z0 = t.Tz * size - World.Half;
        var p = Viewer.GlobalPosition;
        float dx = Math.Max(0, Math.Max(x0 - p.X, p.X - (x0 + size)));
        float dz = Math.Max(0, Math.Max(z0 - p.Z, p.Z - (z0 + size)));
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    private void Apply(Built b)
    {
        _building--;
        var t = b.Tile;
        t.Building = -1;
        t.Step = b.Step;
        t.Ground ??= AddChildMesh(_groundMaterial, GeometryInstance3D.ShadowCastingSetting.On);
        t.Ground.Mesh = b.Ground.ToMesh();
        if (b.Water is { } water)
        {
            t.Water ??= AddChildMesh(_waterMaterial, GeometryInstance3D.ShadowCastingSetting.Off);
            t.Water.Mesh = water.ToMesh();
        }
        else if (t.Water != null) { t.Water.QueueFree(); t.Water = null; }
    }

    private MeshInstance3D AddChildMesh(Material material, GeometryInstance3D.ShadowCastingSetting shadows)
    {
        var mi = new MeshInstance3D { MaterialOverride = material, CastShadow = shadows };
        AddChild(mi);
        return mi;
    }

    // ---------------------------------------------------------------- mesh building (worker threads)

    private MeshData BuildGround(Tile t, int step)
    {
        int cells = World.TileSamples - 1, n = cells / step + 1;
        int gx0 = t.Tx * cells, gz0 = t.Tz * cells;
        int perimeter = 4 * (n - 1);
        var m = new MeshData(n * n + perimeter, (n - 1) * (n - 1) * 6 + perimeter * 6);
        for (int j = 0; j < n; j++)
        for (int i = 0; i < n; i++)
        {
            int gx = gx0 + i * step, gz = gz0 + j * step;
            var normal = Normal(gx, gz, step);
            m.Add(new Vector3(World.SampleToWorld(gx), World.Ground(gx, gz), World.SampleToWorld(gz)), normal, GroundColor(gx, gz, normal));
        }
        for (int j = 0; j < n - 1; j++)
        for (int i = 0; i < n - 1; i++)
        {
            int a = j * n + i, b = a + 1, c = a + n, d = c + 1;
            m.Tri(a, b, c); m.Tri(b, d, c);
        }
        // Skirt: walk the edge, drop a copy of each edge vertex, join with a vertical strip.
        var ring = new List<int>(perimeter);
        for (int i = 0; i < n - 1; i++) ring.Add(i);                         // north, west -> east
        for (int j = 0; j < n - 1; j++) ring.Add(j * n + n - 1);             // east, north -> south
        for (int i = n - 1; i > 0; i--) ring.Add((n - 1) * n + i);           // south, east -> west
        for (int j = n - 1; j > 0; j--) ring.Add(j * n);                     // west, south -> north
        float drop = 2 + step * World.Spacing * 2;
        int first = m.Count;
        foreach (int v in ring) m.Add(m.Pos[v] - new Vector3(0, drop, 0), m.Nrm[v], m.Col[v]);
        for (int k = 0; k < ring.Count; k++)
        {
            int k2 = (k + 1) % ring.Count;
            int p = ring[k], q = ring[k2], pb = first + k, qb = first + k2;
            m.Tri(p, q, pb); m.Tri(q, qb, pb);
        }
        return m;
    }

    /// <summary>Flat-ish water quads over every cell that touches water; dry corners take the wet corners' level.</summary>
    private MeshData? BuildWater(Tile t, int step)
    {
        int cells = World.TileSamples - 1, n = cells / step;
        int gx0 = t.Tx * cells, gz0 = t.Tz * cells;
        var quads = new List<(int gx, int gz, Vector4 levels)>();
        Span<float> lv = stackalloc float[4];
        for (int j = 0; j < n; j++)
        for (int i = 0; i < n; i++)
        {
            int gx = gx0 + i * step, gz = gz0 + j * step;
            lv[0] = World.Water(gx, gz); lv[1] = World.Water(gx + step, gz);
            lv[2] = World.Water(gx, gz + step); lv[3] = World.Water(gx + step, gz + step);
            float sum = 0; int wet = 0;
            foreach (float l in lv) if (!float.IsNaN(l)) { sum += l; wet++; }
            if (wet == 0) continue;
            float avg = sum / wet;
            for (int k = 0; k < 4; k++) if (float.IsNaN(lv[k])) lv[k] = avg;
            quads.Add((gx, gz, new Vector4(lv[0], lv[1], lv[2], lv[3])));
        }
        if (quads.Count == 0) return null;
        var m = new MeshData(quads.Count * 4, quads.Count * 6);
        float s = step * World.Spacing;
        var white = Colors.White;
        foreach (var (gx, gz, l) in quads)
        {
            float x = World.SampleToWorld(gx), z = World.SampleToWorld(gz);
            int a = m.Count;
            m.Add(new Vector3(x, l.X, z), Vector3.Up, white);
            m.Add(new Vector3(x + s, l.Y, z), Vector3.Up, white);
            m.Add(new Vector3(x, l.Z, z + s), Vector3.Up, white);
            m.Add(new Vector3(x + s, l.W, z + s), Vector3.Up, white);
            m.Tri(a, a + 1, a + 2); m.Tri(a + 1, a + 3, a + 2);
        }
        return m;
    }

    private Vector3 Normal(int gx, int gz, int step)
    {
        float dx = World.Ground(gx + step, gz) - World.Ground(gx - step, gz);
        float dz = World.Ground(gx, gz + step) - World.Ground(gx, gz - step);
        float span = 2 * step * World.Spacing;
        return new Vector3(-dx / span, 1, -dz / span).Normalized();
    }

    /// <summary>Placeholder ground colours: jungle floor, steep rock, mud along water, a little noise.</summary>
    private Color GroundColor(int gx, int gz, Vector3 normal)
    {
        float h = World.Ground(gx, gz);
        uint hash = Hash((uint)gx, (uint)gz);
        float noise = (hash & 1023) / 1023f;
        float blotch = ValueNoise(gx / 40f, gz / 40f) * .65f + ValueNoise(gx / 9f + 77, gz / 9f) * .35f;
        var floor = new Color(.16f, .22f, .10f).Lerp(new Color(.24f, .30f, .13f), blotch * .7f + noise * .3f);
        var rock = new Color(.36f, .34f, .30f).Lerp(new Color(.30f, .29f, .26f), noise);
        var mud = new Color(.27f, .22f, .15f);
        float steep = Mathf.SmoothStep(.82f, .62f, normal.Y);
        var c = floor.Lerp(rock, steep);
        if (World.NearWater(gx, gz, 3)) c = c.Lerp(mud, .75f);
        if (h > 1300) c = c.Lerp(rock, Mathf.Clamp((h - 1300) / 300, 0, .6f));
        return c;
    }

    /// <summary>Smooth 0..1 value noise (bilinear between hashed lattice points, smoothstep-eased).</summary>
    private static float ValueNoise(float x, float z)
    {
        int ix = (int)MathF.Floor(x), iz = (int)MathF.Floor(z);
        float u = x - ix, v = z - iz;
        u = u * u * (3 - 2 * u); v = v * v * (3 - 2 * v);
        float L(int a, int b) => (Hash((uint)a, (uint)b) & 1023) / 1023f;
        return Mathf.Lerp(Mathf.Lerp(L(ix, iz), L(ix + 1, iz), u), Mathf.Lerp(L(ix, iz + 1), L(ix + 1, iz + 1), u), v);
    }

    private static uint Hash(uint x, uint z)
    {
        uint h = x * 374761393u + z * 668265263u;
        h = (h ^ (h >> 13)) * 1274126177u;
        return h ^ (h >> 16);
    }

    // ---------------------------------------------------------------- collision (main thread)

    private void AddCollision(Tile t)
    {
        int n = World.TileSamples, cells = n - 1;
        int gx0 = t.Tx * cells, gz0 = t.Tz * cells;
        float s = World.Spacing;
        var data = new float[n * n];
        for (int j = 0; j < n; j++)
        for (int i = 0; i < n; i++)
            data[j * n + i] = World.Ground(gx0 + i, gz0 + j) / s; // the shape is scaled by the spacing below
        var shape = new HeightMapShape3D { MapWidth = n, MapDepth = n, MapData = data };
        var body = new StaticBody3D();
        var col = new CollisionShape3D { Shape = shape, Scale = new Vector3(s, s, s) };
        body.AddChild(col);
        body.Position = new Vector3(World.SampleToWorld(gx0) + cells * s * .5f, 0, World.SampleToWorld(gz0) + cells * s * .5f);
        AddChild(body);
        t.Body = body;
    }
}

/// <summary>Plain vertex/index arrays, filled on a worker thread and turned into a mesh on the main thread.</summary>
public sealed class MeshData
{
    public readonly Vector3[] Pos, Nrm;
    public readonly Color[] Col;
    public readonly int[] Idx;
    public int Count, IndexCount;

    public MeshData(int vertices, int indices)
    {
        Pos = new Vector3[vertices]; Nrm = new Vector3[vertices]; Col = new Color[vertices]; Idx = new int[indices];
    }

    public void Add(Vector3 p, Vector3 n, Color c) { Pos[Count] = p; Nrm[Count] = n; Col[Count] = c; Count++; }
    public void Tri(int a, int b, int c) { Idx[IndexCount++] = a; Idx[IndexCount++] = b; Idx[IndexCount++] = c; }

    public ArrayMesh ToMesh()
    {
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = Count == Pos.Length ? Pos : Pos[..Count];
        arrays[(int)Mesh.ArrayType.Normal] = Count == Nrm.Length ? Nrm : Nrm[..Count];
        arrays[(int)Mesh.ArrayType.Color] = Count == Col.Length ? Col : Col[..Count];
        arrays[(int)Mesh.ArrayType.Index] = IndexCount == Idx.Length ? Idx : Idx[..IndexCount];
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }
}
