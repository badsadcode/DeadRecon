using System;
using System.Collections.Generic;
using Godot;

namespace DeadRecon.World;

/// <summary>
/// Jungle undergrowth near the viewer: low ferns and big palm-like frond plants. Placeholder art made entirely in code
/// (a painted frond texture and arching frond strips) until real plant assets exist; deterministic like the trees.
/// Only cells within <see cref="Radius"/> metres exist; beyond that the canopy hides the floor anyway.
/// </summary>
public partial class GroundCover : Node3D
{
    public BakedWorld World = null!;
    public Node3D Viewer = null!;

    private const float CellSize = 32, Radius = 110;

    private sealed record Kind(string Name, float Spacing, float Fill, float MinScale, float MaxScale, uint Salt);
    private static readonly Kind[] Kinds =
    {
        new("fern", 2.2f, .55f, .7f, 1.35f, 50),
        new("frond", 5.5f, .30f, .8f, 1.4f, 60),
        new("grass", 1.1f, .9f, .6f, 1.25f, 70),   // patchy: also needs the patch noise and some sky (see Build)
    };

    private Mesh[] _meshes = Array.Empty<Mesh>();
    private readonly Dictionary<(int, int), Node3D> _cells = new();
    private readonly Dictionary<Node3D, int> _foliage = new(); // cell -> handle in Foliage (sight blocking)
    private double _sinceUpdate = 1;

    public int PlantsShown { get; private set; }
    private StandardMaterial3D _material = null!, _grassMaterial = null!;

    /// <summary>Weather: wet fronds are darker and shinier.</summary>
    public void SetWetness(float wet)
    {
        if (_material == null) return;
        _material.Roughness = Mathf.Lerp(.75f, .3f, wet);
        _material.AlbedoColor = new Color(1 - .2f * wet, 1 - .18f * wet, 1 - .15f * wet);
        _grassMaterial.Roughness = Mathf.Lerp(.9f, .5f, wet);
        _grassMaterial.AlbedoColor = new Color(1 - .2f * wet, 1 - .17f * wet, 1 - .15f * wet);
    }

    public override void _Ready()
    {
        var material = _material = new StandardMaterial3D
        {
            AlbedoTexture = FrondTexture(),
            Transparency = BaseMaterial3D.TransparencyEnum.AlphaScissor, AlphaScissorThreshold = .5f,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            VertexColorUseAsAlbedo = true, Roughness = .75f,
            BacklightEnabled = true, Backlight = new Color(.18f, .26f, .06f),
            TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic,
        };
        _grassMaterial = new StandardMaterial3D
        {
            AlbedoTexture = GrassTexture(),
            Transparency = BaseMaterial3D.TransparencyEnum.AlphaScissor, AlphaScissorThreshold = .45f,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled, VertexColorUseAsAlbedo = true, Roughness = .9f,
            BacklightEnabled = true, Backlight = new Color(.2f, .28f, .08f),
            TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic,
        };
        _meshes = new[]
        {
            Plant(material, fronds: 9, length: 1.0f, width: .42f, rise: .55f, droop: .75f, stem: 0f),
            Plant(material, fronds: 7, length: 2.4f, width: .75f, rise: 1.1f, droop: 1.0f, stem: .35f),
            Tuft(_grassMaterial),
        };
    }

    public override void _Process(double delta)
    {
        _sinceUpdate += delta;
        if (_sinceUpdate < .3) return;
        _sinceUpdate = 0;
        var p = Viewer.GlobalPosition;
        int reach = (int)MathF.Ceiling(Radius / CellSize) + 1;
        int ci = (int)MathF.Floor((p.X + World.Half) / CellSize), cj = (int)MathF.Floor((p.Z + World.Half) / CellSize);
        var wanted = new List<(float d, int i, int j)>();
        for (int j = cj - reach; j <= cj + reach; j++)
        for (int i = ci - reach; i <= ci + reach; i++)
        {
            float x0 = i * CellSize - World.Half, z0 = j * CellSize - World.Half;
            if (x0 < -World.Half || z0 < -World.Half || x0 >= World.Half || z0 >= World.Half) continue;
            float dx = Math.Max(0, Math.Max(x0 - p.X, p.X - (x0 + CellSize))), dz = Math.Max(0, Math.Max(z0 - p.Z, p.Z - (z0 + CellSize)));
            float d = MathF.Sqrt(dx * dx + dz * dz);
            if (d < Radius) wanted.Add((d, i, j));
        }
        wanted.Sort((a, b) => a.d.CompareTo(b.d));
        var keep = new HashSet<(int, int)>();
        int built = 0, shown = 0;
        foreach (var (_, i, j) in wanted)
        {
            keep.Add((i, j));
            if (!_cells.TryGetValue((i, j), out var cell))
            {
                if (built++ >= 12) continue;
                cell = Build(i, j);
                _cells[(i, j)] = cell;
            }
            foreach (var child in cell.GetChildren()) shown += ((MultiMeshInstance3D)child).Multimesh.InstanceCount;
        }
        var drop = new List<(int, int)>();
        foreach (var (key, cell) in _cells) if (!keep.Contains(key)) { Foliage.Remove(_foliage[cell]); _foliage.Remove(cell); cell.QueueFree(); drop.Add(key); }
        foreach (var key in drop) _cells.Remove(key);
        PlantsShown = shown;
    }

    private Node3D Build(int i, int j)
    {
        var cell = new Node3D();
        AddChild(cell);
        float x0 = i * CellSize - World.Half, z0 = j * CellSize - World.Half;
        var blockers = new List<Foliage.Plant>();
        for (int k = 0; k < Kinds.Length; k++)
        {
            var kind = Kinds[k];
            var xforms = new List<Transform3D>();
            var tints = new List<Color>();
            int per = (int)(CellSize / kind.Spacing);
            for (int b = 0; b < per; b++)
            for (int a = 0; a < per; a++)
            {
                uint ax = (uint)(i * per + a), az = (uint)(j * per + b), s = kind.Salt;
                if (Unit(Hash(ax, az, s + 1)) > kind.Fill) continue;
                float x = x0 + (a + Unit(Hash(ax, az, s + 2))) * kind.Spacing, z = z0 + (b + Unit(Hash(ax, az, s + 3))) * kind.Spacing;
                if (k == 2)
                {
                    // Grass grows in patches, and needs light: thick in gaps, sparse under heavy canopy.
                    float patch = Patch(x * .06f, z * .06f) * .7f + Patch(x * .25f + 40, z * .25f) * .3f;
                    float light = Foliage.SkyAbove(new Vector3(x, World.HeightAt(x, z) + .5f, z));
                    if (patch < .62f - .25f * light) continue;
                }
                var (fx, fz) = World.ToSample(x, z);
                int gx = (int)MathF.Round(fx), gz = (int)MathF.Round(fz);
                if (World.IsWet(gx, gz) || World.Ground(gx, gz) > 1500) continue;
                if (k < 2 && TreeScatter.InClearing(x, z, .85f)) continue; // camp ground is trampled; grass may stay
                float dx = World.Ground(gx + 1, gz) - World.Ground(gx - 1, gz), dz = World.Ground(gx, gz + 1) - World.Ground(gx, gz - 1);
                if ((dx * dx + dz * dz) / (4 * World.Spacing * World.Spacing) > 1.2f) continue; // steeper than ~48 degrees
                float scale = kind.MinScale + (kind.MaxScale - kind.MinScale) * Unit(Hash(ax, az, s + 4));
                var basis = new Basis(Vector3.Up, Unit(Hash(ax, az, s + 5)) * MathF.Tau).Scaled(new Vector3(scale, scale, scale));
                float baseY = World.HeightAt(x, z);
                xforms.Add(new Transform3D(basis, new Vector3(x, baseY - .05f, z)));
                // For AI sight: ferns hide a crouching body, big fronds hide most of a standing one. Grass hides nothing.
                if (k < 2) blockers.Add(k == 0
                    ? new Foliage.Plant(x, z, baseY, 0, 0, .05f, .85f * scale, 1.0f * scale, 1.3f)
                    : new Foliage.Plant(x, z, baseY, 0, 0, .25f, 1.9f * scale, 2.1f * scale, .75f));
                float bright = .7f + .45f * Unit(Hash(ax, az, s + 6)), warm = Unit(Hash(ax, az, s + 7)) - .5f;
                if (k == 2) warm += (Patch(x * .02f + 7, z * .02f) - .5f) * 1.2f; // whole patches drift yellower or bluer
                tints.Add(new Color(bright * (1 + .25f * warm), bright, bright * (1 - .3f * warm)));
            }
            if (xforms.Count == 0) continue;
            var mm = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseColors = true, Mesh = _meshes[k], InstanceCount = xforms.Count };
            for (int t = 0; t < xforms.Count; t++) { mm.SetInstanceTransform(t, xforms[t]); mm.SetInstanceColor(t, tints[t]); }
            cell.AddChild(new MultiMeshInstance3D { Multimesh = mm, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
        }
        _foliage[cell] = Foliage.Add(blockers);
        return cell;
    }

    // ---------------------------------------------------------------- placeholder art

    /// <summary>Smooth 0..1 value noise for patchiness.</summary>
    private static float Patch(float x, float z)
    {
        int ix = (int)MathF.Floor(x), iz = (int)MathF.Floor(z);
        float u = x - ix, v = z - iz;
        u = u * u * (3 - 2 * u); v = v * v * (3 - 2 * v);
        float L(int a, int b) => Unit(Hash((uint)a, (uint)b, 991));
        return Mathf.Lerp(Mathf.Lerp(L(ix, iz), L(ix + 1, iz), u), Mathf.Lerp(L(ix, iz + 1), L(ix + 1, iz + 1), u), v);
    }

    /// <summary>A grass tuft: a few crossed, slightly leaning cards of blades, ~45 cm tall.</summary>
    private static ArrayMesh Tuft(Material material)
    {
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        const int Cards = 4;
        for (int c = 0; c < Cards; c++)
        {
            float yaw = c * MathF.PI / Cards + .3f * MathF.Sin(c * 2.1f);
            var side = new Vector3(MathF.Cos(yaw), 0, MathF.Sin(yaw)) * .26f;
            var lean = new Vector3(-side.Z, 0, side.X).Normalized() * .08f * MathF.Sin(c * 1.3f + 1);
            float h = .4f + .1f * MathF.Sin(c * 2.7f);
            Vector3 a = -side, b = side, cTop = side + lean + Vector3.Up * h, d = -side + lean + Vector3.Up * h;
            var n = Vector3.Up; // soft, sky-facing normals so grass doesn't flicker dark/light as it turns
            void V(Vector3 p, float u, float v) { st.SetNormal(n); st.SetUV(new Vector2(u, v)); st.SetColor(Colors.White); st.AddVertex(p); }
            V(a, 0, 1); V(b, 1, 1); V(cTop, 1, 0);
            V(a, 0, 1); V(cTop, 1, 0); V(d, 0, 0);
        }
        var mesh = st.Commit();
        mesh.SurfaceSetMaterial(0, material);
        return mesh;
    }

    /// <summary>Paint a card of grass blades: thin tapering strands, dark at the root, lighter toward the tips.</summary>
    private static ImageTexture GrassTexture()
    {
        const int W = 128, H = 128, Blades = 26;
        var px = new byte[W * H * 4];
        var rng = new Random(70);
        for (int i = 0; i < px.Length; i += 4) { px[i] = 50; px[i + 1] = 70; px[i + 2] = 25; px[i + 3] = 0; }
        for (int bl = 0; bl < Blades; bl++)
        {
            float x0 = 8 + (float)rng.NextDouble() * (W - 16), bend = ((float)rng.NextDouble() - .5f) * 40;
            float top = (float)(.15 + rng.NextDouble() * .45) * H, width = 1.6f + (float)rng.NextDouble() * 1.8f;
            float tint = (float)rng.NextDouble();
            for (int y = (int)top; y < H; y++)
            {
                float t = (H - y) / (H - top);                        // 0 root .. 1 tip
                float x = x0 + bend * t * t;
                float w = width * (1 - t * .85f);
                for (int xx = (int)(x - w - 1); xx <= (int)(x + w + 1); xx++)
                {
                    if (xx < 0 || xx >= W) continue;
                    float cover = Math.Clamp(w + .5f - MathF.Abs(xx - x), 0, 1);
                    if (cover <= 0) continue;
                    int o = (y * W + xx) * 4;
                    float shade = .45f + .55f * t;
                    float r = (.16f + .12f * tint) * shade, g = (.30f + .12f * tint) * shade, b = .08f * shade;
                    px[o] = (byte)(r * 255); px[o + 1] = (byte)(g * 255); px[o + 2] = (byte)(b * 255);
                    px[o + 3] = (byte)Math.Max(px[o + 3], cover * 255);
                }
            }
        }
        var image = Image.CreateFromData(W, H, false, Image.Format.Rgba8, px);
        image.GenerateMipmaps();
        return ImageTexture.CreateFromImage(image);
    }

    /// <summary>A rosette of arching frond strips around a short stem. Texture v runs base (0) to tip (1).</summary>
    private static ArrayMesh Plant(Material material, int fronds, float length, float width, float rise, float droop, float stem)
    {
        const int Segments = 7;
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        for (int f = 0; f < fronds; f++)
        {
            float yaw = f * MathF.Tau / fronds + .35f * MathF.Sin(f * 2.3f);
            float tilt = .75f + .5f * (.5f + .5f * MathF.Sin(f * 1.7f));     // how far this frond reaches up before arching
            var dir = new Vector3(MathF.Cos(yaw), 0, MathF.Sin(yaw));
            var side = new Vector3(-dir.Z, 0, dir.X);
            float len = length * (.8f + .35f * (.5f + .5f * MathF.Sin(f * 3.1f)));
            Vector3 Center(float t) => dir * len * t + Vector3.Up * (stem + len * (rise * tilt * t - droop * t * t));
            for (int sgm = 0; sgm < Segments; sgm++)
            {
                float t0 = sgm / (float)Segments, t1 = (sgm + 1) / (float)Segments;
                Vector3 c0 = Center(t0), c1 = Center(t1);
                float w0 = width * .5f, w1 = width * .5f;
                // A gentle V cross-section (fronds fold slightly along the rib).
                var fold = Vector3.Up * width * .08f;
                Vector3 a = c0 - side * w0 + fold, b = c0 + side * w0 + fold, c = c1 - side * w1 + fold, d = c1 + side * w1 + fold;
                var n = (c1 - c0).Cross(side).Normalized();
                if (n.Y < 0) n = -n;
                void V(Vector3 pos, float u, float v) { st.SetNormal(n); st.SetUV(new Vector2(u, 1 - v)); st.SetColor(Colors.White); st.AddVertex(pos); }
                V(a, 0, t0); V(b, 1, t0); V(c, 0, t1);
                V(b, 1, t0); V(d, 1, t1); V(c, 0, t1);
            }
        }
        var mesh = st.Commit();
        mesh.SurfaceSetMaterial(0, material);
        return mesh;
    }

    /// <summary>Paint a fern frond: a midrib with paired leaflets angled toward the tip, alpha outside.</summary>
    private static ImageTexture FrondTexture()
    {
        const int W = 128, H = 512, Leaflets = 22;
        var px = new byte[W * H * 4];
        for (int y = 0; y < H; y++)
        for (int x = 0; x < W; x++)
        {
            float v = 1 - (y + .5f) / H;                  // 0 base .. 1 tip
            float u = (x + .5f) / W * 2 - 1;              // -1 .. 1 across
            float au = MathF.Abs(u);
            float outline = MathF.Pow(MathF.Sin(MathF.PI * Math.Clamp(v * .92f + .04f, 0, 1)), .6f) * .97f;
            float rib = .035f + .02f * (1 - v);
            bool inside = false;
            float shade = 1;
            if (au < rib && v < .97f) { inside = true; shade = .75f; }
            else if (au < outline && v > .03f)
            {
                float along = v * Leaflets - au * 1.1f;     // leaflets slant toward the tip
                float phase = along - MathF.Floor(along);
                float half = .42f * MathF.Pow(1 - au / outline, .35f);
                if (MathF.Abs(phase - .5f) < half) { inside = true; shade = .85f + .3f * phase; }
            }
            int o = (y * W + x) * 4;
            if (!inside) { px[o] = 40; px[o + 1] = 70; px[o + 2] = 25; px[o + 3] = 0; continue; }
            float tip = v;
            float r = (.17f + .10f * tip) * shade, g = (.33f + .14f * tip) * shade, b = (.09f + .04f * tip) * shade;
            px[o] = (byte)(Math.Clamp(r, 0, 1) * 255); px[o + 1] = (byte)(Math.Clamp(g, 0, 1) * 255);
            px[o + 2] = (byte)(Math.Clamp(b, 0, 1) * 255); px[o + 3] = 255;
        }
        var image = Image.CreateFromData(W, H, false, Image.Format.Rgba8, px);
        image.GenerateMipmaps();
        return ImageTexture.CreateFromImage(image);
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
