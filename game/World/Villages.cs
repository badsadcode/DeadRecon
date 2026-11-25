using System;
using System.Collections.Generic;
using Godot;

namespace DeadRecon.World;

/// <summary>
/// Builds the settlements near you and frees the far ones: stilt houses with verandas and stairs, ground huts,
/// a well, a campfire (smoke by day, firelight at night), market stalls, laundry, and road checkpoints with a
/// watchtower. All procedural and deterministic per site (the same village every time you come back).
/// <para>Each site is one merged mesh (a surface per material) plus one static body, so a village costs a handful of
/// draw calls. Walls and roofs are real collision: they stop bullets and block sight.</para>
/// <para>Placeholder architecture until NoorArchitect exports buildings (docs/LIVING_JUNGLE.md, step 3).</para>
/// </summary>
public partial class Villages : Node3D
{
    public BakedWorld World = null!;
    public Settlements Towns = null!;
    public Node3D Viewer = null!;

    private const float BuildDistance = 750, FreeDistance = 950, LightDistance = 260;

    private sealed class Built
    {
        public Node3D Root = null!;
        public readonly List<(OmniLight3D Light, float Energy, bool Fire)> Lights = new();
        public readonly List<GpuParticles3D> Fires = new();
        public readonly List<int> Plants = new();
        public SiteLayout Layout = new();          // Foliage owners (wheat hides you), removed with the site
    }
    private readonly Dictionary<int, Built> _built = new();

    /// <summary>Where things are in a built settlement, for the people who live there.</summary>
    public sealed class SiteLayout
    {
        /// <summary>Door: where you stand outside (stairs foot / hut door). Top: the stairs' top (stilt houses; else
        /// the door). Inside: in the doorway. Under: shelter under a stilt house (null for huts).</summary>
        public sealed record House(Vector3 Door, Vector3 Top, Vector3 Inside, Vector3? Under);
        public Vector3 Fire, Centre;
        public Vector3? Well;
        public readonly List<Vector3> Seats = new();
        public readonly List<(Vector3 At, float Yaw)> Stalls = new();
        public readonly List<House> Houses = new();
    }
    private SiteLayout _layout = new();
    public SiteLayout? LayoutOf(int site) => _built.TryGetValue(site, out var b) ? b.Layout : null;
    private double _since = 1;
    private Settlements.Site? _inside;

    /// <summary>For tests: every stair built (foot on the ground, top on the veranda), in build order.</summary>
    public readonly List<(Vector3 Foot, Vector3 Top)> Stairs = new();
    public int BuiltCount => _built.Count;

    private StandardMaterial3D _wood = null!, _bamboo = null!, _thatch = null!, _stone = null!, _sandbag = null!, _tin = null!,
                               _mat = null!, _clay = null!, _red = null!, _white = null!;
    private StandardMaterial3D[] _cloth = Array.Empty<StandardMaterial3D>();
    private StandardMaterial3D _soil = null!;
    private ShaderMaterial _paddyWater = null!, _riceYoung = null!, _riceMature = null!, _wheatRipe = null!, _wheatGreen = null!;
    private ArrayMesh _riceTuft = null!, _wheatTuft = null!;

    public override void _Ready()
    {
        StandardMaterial3D M(Color c, float rough, Texture2D? tex = null, float scale = 1, float metal = 0)
        {
            var m = new StandardMaterial3D { AlbedoColor = c, Roughness = rough, Metallic = metal };
            if (tex != null) { m.AlbedoTexture = tex; m.Uv1Triplanar = true; m.Uv1WorldTriplanar = true; m.Uv1Scale = Vector3.One * scale; }
            TreeScatter.RegisterWet(m);
            return m;
        }
        _wood = M(new Color(.42f, .30f, .20f), .85f, Grain(), .9f);
        _bamboo = M(new Color(.95f, .86f, .66f), .9f, Weave(), 1.3f);
        _thatch = M(new Color(.86f, .74f, .52f), .95f, Thatch(), .75f);
        _stone = M(new Color(.46f, .45f, .41f), .95f);
        _sandbag = M(new Color(.52f, .48f, .36f), .97f, Weave(), 2.5f);
        _tin = M(new Color(.50f, .50f, .48f), .55f, Rust(), .5f, .45f);
        _mat = M(new Color(.62f, .50f, .30f), .95f, Weave(), 2.2f);
        _clay = M(new Color(.55f, .30f, .18f), .8f);
        _red = M(new Color(.62f, .12f, .10f), .7f);
        _white = M(new Color(.85f, .84f, .80f), .7f);
        _soil = M(new Color(.62f, .55f, .40f), .95f, Tex(64, (x, y) => H(x / 2, y / 2, 11) * .5f + H(x / 7, y / 7, 12) * .5f,
                  new Color(.22f, .19f, .12f), new Color(.55f, .50f, .34f)), 1.1f);
        _paddyWater = new ShaderMaterial { Shader = GD.Load<Shader>("res://game/World/paddy.gdshader") };
        ShaderMaterial Crop(Color root, Color tip, float height)
        {
            var m = new ShaderMaterial { Shader = GD.Load<Shader>("res://game/World/crop.gdshader") };
            m.SetShaderParameter("base_color", root); m.SetShaderParameter("tip_color", tip); m.SetShaderParameter("height", height);
            return m;
        }
        _riceYoung = Crop(new Color(.16f, .30f, .07f), new Color(.42f, .74f, .20f), .36f);
        _riceMature = Crop(new Color(.22f, .30f, .09f), new Color(.70f, .70f, .30f), .72f);
        _wheatRipe = Crop(new Color(.42f, .33f, .14f), new Color(.93f, .77f, .40f), 1.05f);
        _wheatGreen = Crop(new Color(.20f, .29f, .09f), new Color(.58f, .66f, .28f), .9f);
        _riceTuft = Tuft(7, .72f, .16f, .022f, ears: false, 1);
        _wheatTuft = Tuft(6, 1.0f, .1f, .018f, ears: true, 2);
        _cloth = new[]
        {
            M(new Color(.62f, .20f, .16f), .9f), M(new Color(.20f, .32f, .55f), .9f), M(new Color(.85f, .80f, .66f), .9f),
            M(new Color(.32f, .45f, .25f), .9f), M(new Color(.75f, .55f, .18f), .9f),
        };
    }

    public override void _Process(double delta)
    {
        var p = Viewer.GlobalPosition;
        float dark = TimeOfDay.Current?.Darkness ?? 0, rain = Weather.Current?.Intensity ?? 0;
        float t = Time.GetTicksMsec() / 1000f;
        _paddyWater.SetShaderParameter("rain", rain);
        float wet = Weather.Current?.Wetness ?? 0;
        foreach (var m in new[] { _riceYoung, _riceMature, _wheatRipe, _wheatGreen })
        {
            m.SetShaderParameter("wind", .22f + .7f * rain);
            m.SetShaderParameter("wet", wet);
        }
        foreach (var (index, b) in _built)
        {
            var site = Towns.Sites[index];
            bool near = Flat(site.Centre - p).Length() < LightDistance + site.Radius;
            foreach (var (light, energy, fire) in b.Lights)
            {
                // Fire: always lit (someone keeps it going), but only shows against the dark. Lamps: only at night.
                float want = fire ? (.35f + 1.65f * dark) * (1 - .6f * rain) : dark > .35f ? 1 : 0;
                light.Visible = near && want > .01f;
                if (!light.Visible) continue;
                float flicker = fire
                    ? .82f + .1f * MathF.Sin(t * 13 + light.GetInstanceId() % 7) + .08f * MathF.Sin(t * 29.7f)
                    : .95f + .05f * MathF.Sin(t * 5 + light.GetInstanceId() % 11);
                light.LightEnergy = energy * want * flicker;
            }
            foreach (var f in b.Fires) f.Emitting = rain < .7f;
        }

        // Entering a settlement: a quiet title card.
        var inside = Towns.SiteAt(p.X, p.Z, 1.25f);
        if (inside != null && inside != _inside) DeadRecon.UI.Hud.Announce(inside.Name, inside.Describe);
        _inside = inside;

        _since += delta;
        if (_since < .4) return;
        _since = 0;
        bool builtOne = false;
        foreach (var s in Towns.Sites)
        {
            float d = Flat(s.Centre - p).Length() - s.Radius;
            if (_built.TryGetValue(s.Index, out var b))
            {
                if (d > FreeDistance)
                {
                    foreach (int owner in b.Plants) Foliage.Remove(owner);
                    b.Root.QueueFree();
                    _built.Remove(s.Index);
                }
            }
            else if (d < BuildDistance && !builtOne)
            {
                _built[s.Index] = Build(s);
                builtOne = true;   // one per tick: no hitch when several come into range at once
            }
        }
    }

    private static Vector3 Flat(Vector3 v) => new(v.X, 0, v.Z);

    // ================================================================= one site

    /// <summary>Collects pieces into one mesh per material, and their collision into one body.</summary>
    private sealed class Kit
    {
        private readonly Dictionary<Material, SurfaceTool> _tools = new();
        public readonly StaticBody3D Body = new();

        public void Add(Material m, Mesh mesh, Transform3D xf)
        {
            if (!_tools.TryGetValue(m, out var st)) { st = new SurfaceTool(); st.Begin(Mesh.PrimitiveType.Triangles); _tools[m] = st; }
            st.AppendFrom(mesh, 0, xf);
        }
        public void Box(Material m, Vector3 size, Transform3D xf, bool solid = true)
        {
            Add(m, new BoxMesh { Size = size }, xf);
            if (solid) Shape(new BoxShape3D { Size = size }, xf);
        }
        public void Shape(Shape3D shape, Transform3D xf) => Body.AddChild(new CollisionShape3D { Shape = shape, Transform = xf });
        public MeshInstance3D Commit()
        {
            var mesh = new ArrayMesh();
            foreach (var (m, st) in _tools)
            {
                st.Commit(mesh);
                mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1, m);
            }
            return new MeshInstance3D { Mesh = mesh };
        }
    }

    private Built Build(Settlements.Site site)
    {
        var b = new Built { Root = new Node3D { Name = $"Site_{site.Index}" } };
        _layout = b.Layout;
        var kit = new Kit();
        var rng = new RandomNumberGenerator { Seed = (ulong)site.Index * 7919 + 12345 };
        if (site.Kind == "outpost") Checkpoint(kit, b, site, rng);
        else
        {
            Settlement(kit, b, site, rng);
            foreach (var f in Towns.Fields) if (f.Site == site.Index) Field(kit, b, f, rng);
        }
        b.Root.AddChild(kit.Commit());
        b.Root.AddChild(kit.Body);
        AddChild(b.Root);
        return b;
    }

    private void Settlement(Kit kit, Built b, Settlements.Site site, RandomNumberGenerator rng)
    {
        var c = site.Centre;
        c.Y = World.HeightAt(c.X, c.Z);
        int houses = site.Kind switch { "town" => rng.RandiRange(32, 42), "village" => rng.RandiRange(11, 17), _ => rng.RandiRange(3, 6) };
        var taken = new List<(Vector2 At, float R)> { (new Vector2(c.X, c.Z), site.Kind == "hamlet" ? 5 : 8) };

        // The middle: a well (not in hamlets) and the fire everyone sits around.
        _layout.Centre = c;
        if (site.Kind != "hamlet") { var well = Ground(c + new Vector3(2.5f, 0, -1.5f)); Well(kit, well); _layout.Well = well; }
        var fireAt = Ground(c + new Vector3(-2.5f, 0, 2));
        _layout.Fire = fireAt;
        Campfire(kit, b, fireAt, rng);

        // Market stalls round the plaza (towns have a real market).
        int stalls = site.Kind switch { "town" => rng.RandiRange(8, 12), "village" => rng.RandiRange(1, 3), _ => 0 };
        for (int k = 0; k < stalls; k++)
        {
            float a = k / (float)Math.Max(1, stalls) * MathF.Tau + rng.Randf() * .3f, r = site.Kind == "town" ? 11 : 7.5f;
            var at = c + new Vector3(MathF.Cos(a) * r, 0, MathF.Sin(a) * r);
            if (Towns.RoadAt(at.X, at.Z) > .3f) continue;
            float sy = MathF.Atan2(c.X - at.X, c.Z - at.Z);
            Stall(kit, Ground(at), sy, rng);
            _layout.Stalls.Add((Ground(at + new Basis(Vector3.Up, sy) * new Vector3(0, 0, -.85f)), sy));
            taken.Add((new Vector2(at.X, at.Z), 2.2f));
        }

        int placed = 0, lamps = 0;
        for (int attempt = 0; attempt < houses * 40 && placed < houses; attempt++)
        {
            float a = rng.Randf() * MathF.Tau, r = Mathf.Lerp(site.Kind == "hamlet" ? 8 : 12, site.Radius - 4, MathF.Sqrt(rng.Randf()));
            var at = c + new Vector3(MathF.Cos(a) * r, 0, MathF.Sin(a) * r);
            bool stilt = rng.Randf() < (site.Kind == "hamlet" ? .85f : .7f);
            float w = stilt ? rng.RandfRange(5, 7.5f) : rng.RandfRange(3, 4.2f), d = stilt ? rng.RandfRange(4, 5.5f) : rng.RandfRange(3, 4);
            float extent = .5f * MathF.Sqrt(w * w + d * d) + (stilt ? 3.2f : 1.2f);
            // Doors face the plaza.
            float yaw = MathF.Atan2(c.X - at.X, c.Z - at.Z) + rng.RandfRange(-.25f, .25f);
            var basis = new Basis(Vector3.Up, yaw);
            var centre = at + basis * new Vector3(0, 0, stilt ? 1.2f : 0);
            if (taken.Exists(o => o.At.DistanceTo(new Vector2(centre.X, centre.Z)) < o.R + extent + 1.5f)) continue;
            bool ok = true;
            foreach (var (lx, lz) in new[] { (0f, 0f), (-w / 2, -d / 2), (w / 2, -d / 2), (-w / 2, d / 2 + 3), (w / 2, d / 2 + 3), (w / 2 - .6f, d / 2 + 4.5f) })
            {
                var q = at + basis * new Vector3(lx, 0, lz);
                var (gx, gz) = World.ToSample(q.X, q.Z);
                if (Towns.RoadAt(q.X, q.Z) > .12f || World.NearWater((int)gx, (int)gz, 1)) { ok = false; break; }
            }
            if (!ok) continue;
            taken.Add((new Vector2(centre.X, centre.Z), extent));
            Vector3? lamp = stilt ? StiltHouse(kit, at, yaw, w, d, rng) : GroundHut(kit, at, yaw, w, d, rng, _thatch, _bamboo);
            if (lamp is { } l && lamps < 4 && rng.Randf() < .6f)
            {
                var light = new OmniLight3D { LightColor = new Color(1f, .66f, .36f), OmniRange = 7, LightEnergy = 0, ShadowEnabled = false, Visible = false };
                b.Root.AddChild(light);
                light.Position = l;   // the site root sits at the origin
                b.Lights.Add((light, 2.4f, false));
                lamps++;
            }
            Clutter(kit, at, yaw, w, d, stilt, rng);
            placed++;
        }
    }

    private Vector3 Ground(Vector3 p) => new(p.X, World.HeightAt(p.X, p.Z), p.Z);

    // ================================================================= buildings

    /// <summary>A house on stilts: floor ~2 m up, bamboo walls, a thatched roof over a front veranda, stairs.
    /// Returns where its lamp hangs.</summary>
    private Vector3 StiltHouse(Kit k, Vector3 at, float yaw, float w, float d, RandomNumberGenerator r)
    {
        float ver = r.RandfRange(1.4f, 2f), stilt = r.RandfRange(1.75f, 2.2f), wh = 2.15f, t = .08f;
        var basis = new Basis(Vector3.Up, yaw);
        float top = float.MinValue;
        foreach (var (lx, lz) in new[] { (-w / 2, -d / 2), (w / 2, -d / 2), (-w / 2, d / 2 + ver), (w / 2, d / 2 + ver), (0f, 0f) })
            top = Math.Max(top, World.HeightAt((at + basis * new Vector3(lx, 0, lz)).X, (at + basis * new Vector3(lx, 0, lz)).Z));
        float floorY = top + stilt;
        var H = new Transform3D(basis, new Vector3(at.X, floorY, at.Z));
        float G(float lx, float lz) { var q = H * new Vector3(lx, 0, lz); return World.HeightAt(q.X, q.Z) - floorY; }
        Transform3D L(Vector3 p, Basis? rot = null) => H * new Transform3D(rot ?? Basis.Identity, p);
        float depth = d + ver, zc = ver / 2, front = d / 2 + ver;

        // Floor, joists, stilts.
        k.Box(_wood, new Vector3(w + .2f, .14f, depth), L(new Vector3(0, -.07f, zc)));
        foreach (float z in new[] { -d / 2, d / 2, front }) k.Box(_wood, new Vector3(w + .4f, .2f, .2f), L(new Vector3(0, -.24f, z)), solid: false);
        int nx = w > 6 ? 3 : 2;
        for (int ix = 0; ix < nx; ix++)
        {
            float x = -w / 2 + w * ix / (nx - 1);
            foreach (float z in new[] { -d / 2, d / 2, front })
            {
                float g = G(x, z) - .3f;
                k.Box(_wood, new Vector3(.2f, -g, .2f), L(new Vector3(x, g / 2, z)));
            }
        }

        // Walls: a window at the back, a door at the front, open eaves under the roof.
        float wx = r.RandfRange(-w / 5, w / 5);
        k.Box(_bamboo, new Vector3(w, 1.0f, t), L(new Vector3(0, .5f, -d / 2)));
        k.Box(_bamboo, new Vector3(w, wh - 1.6f, t), L(new Vector3(0, 1.6f + (wh - 1.6f) / 2, -d / 2)));
        float lw = wx - .45f + w / 2, rw = w / 2 - wx - .45f;
        k.Box(_bamboo, new Vector3(lw, .6f, t), L(new Vector3(-w / 2 + lw / 2, 1.3f, -d / 2)));
        k.Box(_bamboo, new Vector3(rw, .6f, t), L(new Vector3(w / 2 - rw / 2, 1.3f, -d / 2)));
        foreach (float sx in new[] { -w / 2, w / 2 }) k.Box(_bamboo, new Vector3(t, wh, d), L(new Vector3(sx, wh / 2, 0)));
        float dx = r.RandfRange(-w / 4, w / 4);
        float a = dx - .55f + w / 2, b = w / 2 - dx - .55f;
        k.Box(_bamboo, new Vector3(a, wh, t), L(new Vector3(-w / 2 + a / 2, wh / 2, d / 2)));
        k.Box(_bamboo, new Vector3(b, wh, t), L(new Vector3(w / 2 - b / 2, wh / 2, d / 2)));
        k.Box(_bamboo, new Vector3(1.1f, wh - 2.0f, t), L(new Vector3(dx, 2.0f + (wh - 2.0f) / 2, d / 2)));
        // Frame posts and the veranda's posts and rails.
        foreach (float sx in new[] { -w / 2, w / 2 })
        {
            foreach (float z in new[] { -d / 2, d / 2 }) k.Box(_wood, new Vector3(.14f, wh, .14f), L(new Vector3(sx, wh / 2, z)), solid: false);
            k.Box(_wood, new Vector3(.14f, wh, .14f), L(new Vector3(sx, wh / 2, front)));
            k.Box(_wood, new Vector3(.1f, .08f, ver), L(new Vector3(sx, .9f, d / 2 + ver / 2)));
        }
        float sxs = w / 2 - .65f;
        float railL = sxs - .5f + w / 2;
        k.Box(_wood, new Vector3(railL, .08f, .1f), L(new Vector3(-w / 2 + railL / 2, .9f, front)));
        for (float x = -w / 2 + .6f; x < sxs - .5f; x += .6f) k.Box(_wood, new Vector3(.05f, .9f, .05f), L(new Vector3(x, .45f, front)), solid: false);

        // Roof: thatch over house and veranda, ridge along the long side.
        float span = depth + 1.1f, rh = span * .42f;
        var prism = new PrismMesh { Size = new Vector3(span, rh, w + 1.2f) };
        var roofXf = L(new Vector3(0, wh + rh / 2 - .05f, zc), new Basis(Vector3.Up, MathF.PI / 2));
        k.Add(_thatch, prism, roofXf);
        k.Shape(prism.CreateConvexShape(), roofXf);

        // Stairs off the veranda's front corner: a walkable ramp under visible treads.
        float run = 2.5f, rise = 0;
        for (int it = 0; it < 3; it++) { rise = -G(sxs, front + run); run = Math.Max(1.6f, rise * 1.45f); }
        float len = MathF.Sqrt(rise * rise + run * run), ang = MathF.Atan2(rise, run);
        var rot = new Basis(Vector3.Right, ang);
        var mid = new Vector3(sxs, -rise / 2, front + run / 2);
        k.Shape(new BoxShape3D { Size = new Vector3(.95f, .1f, len) }, L(mid - new Vector3(0, .07f, 0), rot));
        foreach (float side in new[] { -.47f, .47f }) k.Box(_wood, new Vector3(.06f, .22f, len), L(mid + new Vector3(side, 0, 0), rot), solid: false);
        int steps = Math.Max(2, (int)MathF.Ceiling(rise / .24f));
        for (int s = 1; s <= steps; s++)
        {
            float f = s / (steps + 1f);
            k.Box(_wood, new Vector3(.9f, .05f, .26f), L(new Vector3(sxs, -rise * f, front + run * f - .02f)), solid: false);
        }
        Stairs.Add((H * new Vector3(sxs, -rise, front + run + .6f), H * new Vector3(sxs, 0, front - .4f)));
        _layout.Houses.Add(new SiteLayout.House(H * new Vector3(sxs, -rise, front + run + .6f), H * new Vector3(sxs, .05f, front - .5f),
                                                H * new Vector3(dx, .05f, d / 2 - .7f),
                                                Ground(H * new Vector3(0, 0, 0))));

        // Inside: a sleeping mat; under the house: a firewood stack.
        k.Box(_mat, new Vector3(1.6f, .03f, 2), L(new Vector3(-w / 4, .015f, -d / 5)), solid: false);
        if (r.Randf() < .6f)
        {
            float g = G(w / 4, 0);
            k.Box(_wood, new Vector3(1.4f, .6f, .6f), L(new Vector3(w / 4, g + .3f, 0)));
        }
        return H * new Vector3(0, 1.7f, -d / 5);
    }

    /// <summary>A hut on the ground (kitchen, storage, guard hut). Walls run into the ground on slopes.</summary>
    private Vector3 GroundHut(Kit k, Vector3 at, float yaw, float w, float d, RandomNumberGenerator r, StandardMaterial3D roof, StandardMaterial3D walls)
    {
        var basis = new Basis(Vector3.Up, yaw);
        float lo = float.MaxValue, hi = float.MinValue;
        foreach (var (lx, lz) in new[] { (-w / 2, -d / 2), (w / 2, -d / 2), (-w / 2, d / 2), (w / 2, d / 2) })
        {
            var q = at + basis * new Vector3(lx, 0, lz);
            float h = World.HeightAt(q.X, q.Z);
            lo = Math.Min(lo, h); hi = Math.Max(hi, h);
        }
        float baseY = lo - .3f, wh = hi - baseY + 2.05f, t = .1f;
        var H = new Transform3D(basis, new Vector3(at.X, baseY, at.Z));
        Transform3D L(Vector3 p, Basis? rot = null) => H * new Transform3D(rot ?? Basis.Identity, p);
        k.Box(walls, new Vector3(w, wh, t), L(new Vector3(0, wh / 2, -d / 2)));
        foreach (float sx in new[] { -w / 2, w / 2 }) k.Box(walls, new Vector3(t, wh, d), L(new Vector3(sx, wh / 2, 0)));
        float a = (w - 1.0f) / 2;
        k.Box(walls, new Vector3(a, wh, t), L(new Vector3(-w / 2 + a / 2, wh / 2, d / 2)));
        k.Box(walls, new Vector3(a, wh, t), L(new Vector3(w / 2 - a / 2, wh / 2, d / 2)));
        foreach (float sx in new[] { -w / 2, w / 2 }) foreach (float z in new[] { -d / 2, d / 2 })
            k.Box(_wood, new Vector3(.14f, wh + .1f, .14f), L(new Vector3(sx, wh / 2, z)), solid: false);
        float span = d + .9f, rh = span * (roof == _tin ? .2f : .45f);
        var prism = new PrismMesh { Size = new Vector3(span, rh, w + .9f) };
        var roofXf = L(new Vector3(0, wh + rh / 2 - .05f, 0), new Basis(Vector3.Up, MathF.PI / 2));
        k.Add(roof, prism, roofXf);
        k.Shape(prism.CreateConvexShape(), roofXf);
        if (roof != _tin) { var door = Ground(H * new Vector3(0, 0, d / 2 + .9f)); _layout.Houses.Add(new SiteLayout.House(door, door, Ground(H * new Vector3(0, 0, 0)), null)); }
        return H * new Vector3(0, wh - .5f, 0);
    }

    private void Well(Kit k, Vector3 at)
    {
        for (int i = 0; i < 10; i++)
        {
            float a = i / 10f * MathF.Tau;
            k.Box(_stone, new Vector3(.55f, .75f, .28f), new Transform3D(new Basis(Vector3.Up, -a + MathF.PI / 2), at + new Vector3(MathF.Cos(a) * .85f, .3f, MathF.Sin(a) * .85f)), solid: false);
        }
        k.Shape(new CylinderShape3D { Radius = 1.0f, Height = .8f }, new Transform3D(Basis.Identity, at + new Vector3(0, .35f, 0)));
        foreach (float s in new[] { -1f, 1f }) k.Box(_wood, new Vector3(.12f, 1.9f, .12f), new Transform3D(Basis.Identity, at + new Vector3(s * .95f, .95f, 0)));
        k.Add(_wood, new CylinderMesh { TopRadius = .06f, BottomRadius = .06f, Height = 2 }, new Transform3D(new Basis(Vector3.Forward, MathF.PI / 2), at + new Vector3(0, 1.7f, 0)));
        var roof = new PrismMesh { Size = new Vector3(1.4f, .5f, 2.5f) };
        k.Add(_thatch, roof, new Transform3D(new Basis(Vector3.Up, MathF.PI / 2), at + new Vector3(0, 2.1f, 0)));
    }

    private void Campfire(Kit k, Built b, Vector3 at, RandomNumberGenerator rng)
    {
        for (int i = 0; i < 9; i++)
        {
            float a = i / 9f * MathF.Tau;
            k.Box(_stone, new Vector3(.28f, .2f, .22f), new Transform3D(new Basis(Vector3.Up, -a), at + new Vector3(MathF.Cos(a) * .6f, .06f, MathF.Sin(a) * .6f)), solid: false);
        }
        for (int i = 0; i < 3; i++)
        {
            float a = i / 3f * MathF.Tau + .4f;
            var log = new Basis(Vector3.Up, a) * new Basis(Vector3.Right, MathF.PI / 2 - .45f);
            k.Add(_wood, new CylinderMesh { TopRadius = .06f, BottomRadius = .08f, Height = .9f }, new Transform3D(log, at + new Vector3(MathF.Cos(a) * .18f, .2f, MathF.Sin(a) * .18f)));
        }
        // Logs to sit on.
        for (int i = 0; i < 3; i++)
        {
            float a = i / 3f * MathF.Tau + rng.Randf();
            var seat = Ground(at + new Vector3(MathF.Cos(a) * 2.2f, 0, MathF.Sin(a) * 2.2f));
            _layout.Seats.Add(Ground(at + new Vector3(MathF.Cos(a + .35f) * 1.7f, 0, MathF.Sin(a + .35f) * 1.7f)));
            var basis = new Basis(Vector3.Up, -a) * new Basis(Vector3.Right, MathF.PI / 2);
            k.Add(_wood, new CylinderMesh { TopRadius = .18f, BottomRadius = .2f, Height = 1.6f }, new Transform3D(basis, seat + new Vector3(0, .15f, 0)));
        }
        var puff = DeadRecon.Combat.Effects.Puff();
        var flameRamp = new Gradient();
        flameRamp.SetColor(0, new Color(1f, .75f, .35f, 1));
        flameRamp.AddPoint(.5f, new Color(1f, .35f, .08f, .8f));
        flameRamp.SetColor(flameRamp.GetPointCount() - 1, new Color(.6f, .1f, .02f, 0));
        var fire = new GpuParticles3D
        {
            Amount = 28, Lifetime = .7f, Position = at + new Vector3(0, .15f, 0),
            ProcessMaterial = new ParticleProcessMaterial
            {
                EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Sphere, EmissionSphereRadius = .2f,
                Direction = Vector3.Up, Spread = 12, InitialVelocityMin = .6f, InitialVelocityMax = 1.3f, Gravity = new Vector3(0, .8f, 0),
                ScaleMin = .5f, ScaleMax = 1f, ColorRamp = new GradientTexture1D { Gradient = flameRamp },
                ScaleCurve = new CurveTexture { Curve = Ramp(1, .25f) },
            },
            DrawPass1 = new QuadMesh
            {
                Size = new Vector2(.45f, .55f),
                Material = new StandardMaterial3D
                {
                    ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                    BlendMode = BaseMaterial3D.BlendModeEnum.Add, BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles,
                    VertexColorUseAsAlbedo = true, AlbedoTexture = puff, AlbedoColor = new Color(2.2f, 1.6f, 1.1f),
                },
            },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        var smokeRamp = new Gradient();
        smokeRamp.SetColor(0, new Color(.5f, .5f, .5f, 0));
        smokeRamp.AddPoint(.15f, new Color(.55f, .55f, .55f, .28f));
        smokeRamp.SetColor(smokeRamp.GetPointCount() - 1, new Color(.7f, .7f, .7f, 0));
        var smoke = new GpuParticles3D
        {
            Amount = 22, Lifetime = 8, Preprocess = 8, Position = at + new Vector3(0, .6f, 0),
            VisibilityAabb = new Aabb(new Vector3(-6, -1, -6), new Vector3(12, 16, 12)),
            ProcessMaterial = new ParticleProcessMaterial
            {
                Direction = Vector3.Up, Spread = 8, InitialVelocityMin = .5f, InitialVelocityMax = .8f, Gravity = new Vector3(.12f, .05f, .05f),
                ScaleMin = .8f, ScaleMax = 1.2f, ColorRamp = new GradientTexture1D { Gradient = smokeRamp },
                ScaleCurve = new CurveTexture { Curve = Ramp(.6f, 3.5f) },
                TurbulenceEnabled = true, TurbulenceNoiseStrength = .4f, TurbulenceNoiseScale = 4,
            },
            DrawPass1 = new QuadMesh
            {
                Size = new Vector2(1, 1),
                Material = new StandardMaterial3D
                {
                    Transparency = BaseMaterial3D.TransparencyEnum.Alpha, BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles,
                    VertexColorUseAsAlbedo = true, AlbedoTexture = puff, Roughness = 1,
                },
            },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        b.Root.AddChild(fire);
        b.Root.AddChild(smoke);
        b.Fires.Add(fire);
        b.Fires.Add(smoke);
        var light = new OmniLight3D
        {
            LightColor = new Color(1f, .58f, .28f), OmniRange = 15, OmniAttenuation = 1.4f, ShadowEnabled = true, Visible = false,
            Position = at + new Vector3(0, .9f, 0),
        };
        b.Root.AddChild(light);
        b.Lights.Add((light, 2.2f, true));
    }

    private static Curve Ramp(float from, float to)
    {
        var c = new Curve { MaxValue = Math.Max(from, to) };
        c.AddPoint(new Vector2(0, from));
        c.AddPoint(new Vector2(1, to));
        return c;
    }

    private void Stall(Kit k, Vector3 at, float yaw, RandomNumberGenerator rng)
    {
        var H = new Transform3D(new Basis(Vector3.Up, yaw), at);
        Transform3D L(Vector3 p, Basis? rot = null) => H * new Transform3D(rot ?? Basis.Identity, p);
        k.Box(_wood, new Vector3(1.8f, .08f, .9f), L(new Vector3(0, .8f, .3f)));
        foreach (float x in new[] { -.85f, .85f }) foreach (float z in new[] { -.4f, 1f })
            k.Box(_wood, new Vector3(.08f, z < 0 ? 2.3f : 2.0f, .08f), L(new Vector3(x, (z < 0 ? 2.3f : 2.0f) / 2, z)), solid: false);
        k.Box(_cloth[rng.RandiRange(0, _cloth.Length - 1)], new Vector3(2.1f, .03f, 1.8f), L(new Vector3(0, 2.15f, .3f), new Basis(Vector3.Right, -.17f)), solid: false);
        for (int i = 0; i < 4; i++)
            k.Add(_clay, new CylinderMesh { TopRadius = .1f, BottomRadius = .13f, Height = .25f }, L(new Vector3(-.6f + i * .4f, .97f, .3f + rng.RandfRange(-.2f, .2f))));
    }

    private void Clutter(Kit k, Vector3 at, float yaw, float w, float d, bool stilt, RandomNumberGenerator rng)
    {
        var basis = new Basis(Vector3.Up, yaw);
        // Water jars by the door.
        int jars = rng.RandiRange(0, 3);
        for (int i = 0; i < jars; i++)
        {
            var p = Ground(at + basis * new Vector3(-w / 2 + .4f + i * .55f, 0, d / 2 + (stilt ? .6f : 1.2f)));
            k.Add(_clay, new CylinderMesh { TopRadius = .16f, BottomRadius = .24f, Height = .55f }, new Transform3D(Basis.Identity, p + new Vector3(0, .27f, 0)));
        }
        // A laundry line beside some houses: two posts, a rope, cloth.
        if (rng.Randf() < .45f)
        {
            var p0 = Ground(at + basis * new Vector3(w / 2 + 1.5f, 0, -d / 2));
            var p1 = Ground(at + basis * new Vector3(w / 2 + 1.5f, 0, -d / 2 + 3.5f));
            foreach (var p in new[] { p0, p1 }) k.Box(_wood, new Vector3(.07f, 1.9f, .07f), new Transform3D(Basis.Identity, p + new Vector3(0, .95f, 0)));
            var aTop = p0 + Vector3.Up * 1.8f; var bTop = p1 + Vector3.Up * 1.8f;
            var along = bTop - aTop;
            k.Box(_wood, new Vector3(.015f, .015f, along.Length()), new Transform3D(Basis.LookingAt(along.Normalized(), Vector3.Up), (aTop + bTop) / 2), solid: false);
            int n = rng.RandiRange(2, 4);
            for (int i = 0; i < n; i++)
            {
                var q = aTop + along * ((i + .7f) / (n + .4f));
                k.Box(_cloth[rng.RandiRange(0, _cloth.Length - 1)], new Vector3(.02f, rng.RandfRange(.5f, .8f), rng.RandfRange(.4f, .7f)),
                      new Transform3D(Basis.LookingAt(along.Normalized(), Vector3.Up), q - new Vector3(0, .32f, 0)), solid: false);
            }
        }
        // A pile of crates or firewood.
        if (rng.Randf() < .4f)
        {
            var p = Ground(at + basis * new Vector3(-w / 2 - 1, 0, rng.RandfRange(-d / 2, d / 2)));
            k.Box(_wood, new Vector3(.7f, .55f, .55f), new Transform3D(new Basis(Vector3.Up, rng.Randf()), p + new Vector3(0, .27f, 0)));
            if (rng.Randf() < .5f) k.Box(_wood, new Vector3(.5f, .4f, .45f), new Transform3D(new Basis(Vector3.Up, rng.Randf()), p + new Vector3(.1f, .75f, 0)));
        }
    }

    // ================================================================= fields

    private void Field(Kit k, Built b, Settlements.Field f, RandomNumberGenerator rng)
    {
        var basis = new Basis(Vector3.Up, f.Rotation);
        var c = new Vector3(f.Centre.X, f.Level, f.Centre.Z);
        Transform3D L(Vector3 p, Basis? rot = null) => new(basis * (rot ?? Basis.Identity), c + basis * p);
        if (f.Kind == "paddy")
        {
            // Bunds: an earth wall (a terrace wall where the slope falls away) with a rounded ridge you can walk along.
            foreach (var (u, v, len, alongX) in new[]
            {
                (0f, f.D / 2 + .2f, f.W + 1.2f, true), (0f, -f.D / 2 - .2f, f.W + 1.2f, true),
                (f.W / 2 + .2f, 0f, f.D + 1.2f, false), (-f.W / 2 - .2f, 0f, f.D + 1.2f, false),
            })
            {
                var rot = alongX ? Basis.Identity : new Basis(Vector3.Up, MathF.PI / 2);
                k.Box(_soil, new Vector3(len, 2.6f, .7f), L(new Vector3(u, .05f - 1.3f, v), rot));
                var ridge = new PrismMesh { Size = new Vector3(1.5f, .38f, len) };
                var rx = L(new Vector3(u, .05f + .19f, v), rot * new Basis(Vector3.Up, MathF.PI / 2));
                k.Add(_soil, ridge, rx);
                k.Shape(ridge.CreateConvexShape(), rx);
            }
            k.Add(_paddyWater, new PlaneMesh { Size = new Vector2(f.W + .5f, f.D + .5f) }, L(new Vector3(0, .12f, 0)));
            if (f.Stage != "flooded")
                Plant(b, f, basis, c, f.Stage == "young" ? _riceYoung : _riceMature, _riceTuft, .42f, .32f, .9f, f.Stage == "young" ? .5f : 1, onFloor: true, rng);
        }
        else
        {
            Plant(b, f, basis, c, f.Stage == "ripe" ? _wheatRipe : _wheatGreen, _wheatTuft, .36f, .3f, .4f, f.Stage == "ripe" ? 1 : .85f, onFloor: false, rng);
            // Wheat hides a crouching soldier (and most of a standing one at a distance).
            var plants = new List<Foliage.Plant>();
            for (float u = -f.W / 2 + 1; u < f.W / 2; u += 2)
                for (float v = -f.D / 2 + 1; v < f.D / 2; v += 2)
                {
                    var p = c + basis * new Vector3(u, 0, v);
                    plants.Add(new Foliage.Plant(p.X, p.Z, World.HeightAt(p.X, p.Z), 0, 0, .05f, f.Stage == "ripe" ? 1.0f : .85f, 1.2f, 1.0f));
                }
            b.Plants.Add(Foliage.Add(plants));
        }
    }

    /// <summary>Rows of crop tufts as one MultiMesh (written as a raw buffer: thousands of instances, one call).</summary>
    private void Plant(Built b, Settlements.Field f, Basis basis, Vector3 c, Material mat, Mesh tuft, float du, float dv, float inset,
                       float scale, bool onFloor, RandomNumberGenerator rng)
    {
        var buf = new List<float>();
        for (float u = -f.W / 2 + inset; u <= f.W / 2 - inset; u += du)
            for (float v = -f.D / 2 + inset; v <= f.D / 2 - inset; v += dv)
            {
                var p = c + basis * new Vector3(u + rng.RandfRange(-.06f, .06f), 0, v + rng.RandfRange(-.05f, .05f));
                float y = onFloor ? f.Level : World.HeightAt(p.X, p.Z) - .03f;
                float yaw = rng.Randf() * MathF.Tau, s = scale * rng.RandfRange(.82f, 1.15f), cs = MathF.Cos(yaw) * s, sn = MathF.Sin(yaw) * s;
                // Rows of the 3x4 transform (rotation about Y, uniform scale), then the instance colour.
                buf.AddRange(new[] { cs, 0, sn, p.X, 0, s, 0, y, -sn, 0, cs, p.Z });
                float lum = rng.RandfRange(.82f, 1.1f), warm = rng.RandfRange(-.05f, .05f);
                buf.AddRange(new[] { lum + warm, lum, lum - warm, 1 });
            }
        int count = buf.Count / 16;
        if (count == 0) return;
        var mm = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseColors = true, Mesh = tuft, InstanceCount = count };
        mm.Buffer = buf.ToArray();
        b.Root.AddChild(new MultiMeshInstance3D
        {
            Multimesh = mm, MaterialOverride = mat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            VisibilityRangeEnd = 240, VisibilityRangeEndMargin = 30, VisibilityRangeFadeMode = GeometryInstance3D.VisibilityRangeFadeModeEnum.Self,
        });
    }

    /// <summary>A tuft of thin bent blades (and grain ears on top, for wheat). Normals point up: soft grass lighting.</summary>
    private static ArrayMesh Tuft(int blades, float height, float lean, float width, bool ears, int seed)
    {
        var r = new Random(seed);
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        void V(Vector3 p, float lum) { st.SetNormal(Vector3.Up); st.SetColor(new Color(lum, lum, lum)); st.AddVertex(p); }
        for (int i = 0; i < blades; i++)
        {
            float a = i / (float)blades * MathF.Tau + (float)r.NextDouble(), face = (float)r.NextDouble() * MathF.Tau;
            var off = new Vector3(MathF.Cos(a), 0, MathF.Sin(a)) * .045f * (float)r.NextDouble();
            var bend = new Vector3(MathF.Cos(a), 0, MathF.Sin(a)) * lean * (.5f + (float)r.NextDouble());
            var side = new Vector3(MathF.Cos(face), 0, MathF.Sin(face)) * width / 2;
            float h = height * (.75f + .3f * (float)r.NextDouble()), lum = .85f + .25f * (float)r.NextDouble();
            var b0 = off - side; var b1 = off + side;
            var mid = off + bend * .35f + Vector3.Up * h * .55f;
            var m0 = mid - side * .7f; var m1 = mid + side * .7f;
            var tip = off + bend + Vector3.Up * h;
            V(b0, lum * .7f); V(b1, lum * .7f); V(m1, lum);
            V(b0, lum * .7f); V(m1, lum); V(m0, lum);
            V(m0, lum); V(m1, lum); V(tip, lum * 1.1f);
            if (ears)
            {
                // The ear: a slim diamond along the top of the stalk.
                var up = (tip - mid).Normalized();
                var e0 = tip - up * .02f; var e1 = tip + up * .11f;
                var ec = tip + up * .04f;
                var ew = side.Normalized() * .011f;
                V(e0, 1.15f); V(ec + ew, 1.2f); V(e1, 1.25f);
                V(e0, 1.15f); V(e1, 1.25f); V(ec - ew, 1.2f);
            }
        }
        return st.Commit();
    }


    // ================================================================= checkpoints

    private void Checkpoint(Kit k, Built b, Settlements.Site site, RandomNumberGenerator rng)
    {
        // Which way does the road run here?
        var c = Ground(site.Centre);
        Vector3 dir = Vector3.Forward; float width = 5.5f, best = float.MaxValue;
        foreach (var road in Towns.Roads)
            for (int i = 0; i + 1 < road.Points.Length; i++)
            {
                float dd = Flat(road.Points[i] - c).LengthSquared();
                if (dd < best) { best = dd; dir = Flat(road.Points[i + 1] - road.Points[i]).Normalized(); width = road.Width; }
            }
        var side = dir.Cross(Vector3.Up).Normalized();
        float yaw = MathF.Atan2(dir.X, dir.Z);
        var along = new Basis(Vector3.Up, yaw);

        // The barrier: two posts and a striped pole across the road.
        float half = width / 2 + .7f;
        foreach (float s in new[] { -1f, 1f })
        {
            var p = Ground(c + side * s * half);
            k.Box(_wood, new Vector3(.2f, 1.2f, .2f), new Transform3D(Basis.Identity, p + new Vector3(0, .6f, 0)));
        }
        var poleBasis = new Basis(Vector3.Up, yaw + MathF.PI / 2);
        int pieces = (int)(half * 2 / .5f);
        for (int i = 0; i < pieces; i++)
        {
            var p = c + side * (-half + (i + .5f) * half * 2 / pieces);
            p.Y = c.Y + 1.05f;
            k.Box(i % 2 == 0 ? _red : _white, new Vector3(.08f, .08f, half * 2 / pieces), new Transform3D(poleBasis, p), solid: false);
        }
        k.Shape(new BoxShape3D { Size = new Vector3(.1f, .1f, half * 2) }, new Transform3D(poleBasis, c + new Vector3(0, 1.05f, 0)));

        // Sandbag walls along both sides, staggered layers.
        foreach (float s in new[] { -1f, 1f })
        {
            var mid = c + side * s * (width / 2 + 3) + dir * rng.RandfRange(-2, 2);
            for (int layer = 0; layer < 3; layer++)
                for (int i = 0; i < 7; i++)
                {
                    var p = Ground(mid + dir * ((i - 3) * .62f + (layer % 2) * .31f));
                    k.Box(_sandbag, new Vector3(.36f, .24f, .6f), new Transform3D(along * new Basis(Vector3.Up, rng.RandfRange(-.08f, .08f)), p + new Vector3(0, .12f + layer * .23f, 0)), solid: false);
                }
            k.Shape(new BoxShape3D { Size = new Vector3(.4f, .75f, 4.4f) }, new Transform3D(along, Ground(mid) + new Vector3(0, .37f, 0)));
        }

        // A guard hut on one side, a watchtower on the other.
        var hutAt = c + side * (width / 2 + 7) - dir * 4;
        GroundHut(k, hutAt, yaw - MathF.PI / 2, 3.4f, 3, rng, _tin, _wood);
        Watchtower(k, c - side * (width / 2 + 6) + dir * 5, yaw);
    }

    private void Watchtower(Kit k, Vector3 at, float yaw)
    {
        var basis = new Basis(Vector3.Up, yaw);
        float deck = 4.6f, s = 1.2f;
        float top = World.HeightAt(at.X, at.Z) + deck;
        var H = new Transform3D(basis, new Vector3(at.X, top, at.Z));
        Transform3D L(Vector3 p, Basis? rot = null) => H * new Transform3D(rot ?? Basis.Identity, p);
        foreach (float x in new[] { -s, s }) foreach (float z in new[] { -s, s })
        {
            var q = H * new Vector3(x, 0, z);
            float g = World.HeightAt(q.X, q.Z) - top - .3f;
            k.Box(_wood, new Vector3(.18f, 2.4f - g, .18f), L(new Vector3(x, (g + 2.4f) / 2, z)));
        }
        k.Box(_wood, new Vector3(s * 2 + .3f, .12f, s * 2 + .3f), L(new Vector3(0, -.06f, 0)));
        // Cross braces and a waist-high plank wall round the deck.
        foreach (float z in new[] { -s, s }) k.Box(_wood, new Vector3(.08f, .08f, MathF.Sqrt(deck * deck + 4 * s * s)), L(new Vector3(0, -deck / 2, z), new Basis(Vector3.Forward, MathF.Atan2(deck, 2 * s)) * new Basis(Vector3.Up, MathF.PI / 2)), solid: false);
        foreach (var (x, z, sx, sz) in new[] { (0f, -s, s * 2, .06f), (0f, s, s * 2, .06f), (-s, 0f, .06f, s * 2), (s, 0f, .06f, s * 2) })
            k.Box(_wood, new Vector3(sx, 1.0f, sz), L(new Vector3(x, .5f, z)));
        var roof = new PrismMesh { Size = new Vector3(s * 2 + 1, .7f, s * 2 + 1) };
        k.Add(_tin, roof, L(new Vector3(0, 2.75f, 0)));
        // Ladder (looks only).
        for (int i = 0; i < 14; i++)
            k.Box(_wood, new Vector3(.5f, .04f, .04f), L(new Vector3(0, -deck + .35f * i + .3f, s + .25f)), solid: false);
        foreach (float x in new[] { -.25f, .25f }) k.Box(_wood, new Vector3(.05f, deck + .4f, .05f), L(new Vector3(x, -deck / 2 + .2f, s + .25f)), solid: false);
    }

    // ================================================================= little textures (made here, no assets)

    private static ImageTexture Tex(int size, Func<int, int, float> value, Color dark, Color light)
    {
        var img = Image.CreateEmpty(size, size, false, Image.Format.Rgb8);
        for (int y = 0; y < size; y++) for (int x = 0; x < size; x++) img.SetPixel(x, y, dark.Lerp(light, Math.Clamp(value(x, y), 0, 1)));
        img.GenerateMipmaps();
        return ImageTexture.CreateFromImage(img);
    }

    private static float H(int x, int y, int s) { uint h = (uint)(x * 374761393 + y * 668265263 + s * 2246822519u); h = (h ^ (h >> 13)) * 1274126177u; return (h & 0xFFFF) / 65535f; }

    /// <summary>Woven bamboo: strands alternate over and under in 8-px squares.</summary>
    private static ImageTexture Weave() => Tex(64, (x, y) =>
    {
        bool horizontal = ((x / 8) + (y / 8)) % 2 == 0;
        float across = horizontal ? (y % 8) / 7f : (x % 8) / 7f;
        float edge = MathF.Sin(across * MathF.PI);
        float along = horizontal ? x : y;
        return edge * (.75f + .25f * MathF.Sin(along * .7f + (horizontal ? y / 8 : x / 8) * 2.1f)) + (H(x / 8, y / 8, 3) - .5f) * .2f;
    }, new Color(.28f, .22f, .13f), new Color(.80f, .70f, .50f));

    /// <summary>Thatch: long straw streaks in overlapping rows.</summary>
    private static ImageTexture Thatch() => Tex(128, (x, y) =>
    {
        float straw = H(x, 0, 7) * .6f + H(x / 2, 1, 8) * .4f;
        float row = (y % 32) / 31f;
        return straw * (.55f + .45f * row) - (row < .08f ? .35f : 0) + (H(x, y, 9) - .5f) * .15f;
    }, new Color(.22f, .17f, .09f), new Color(.75f, .62f, .38f));

    private static ImageTexture Grain() => Tex(64, (x, y) =>
        .55f + .3f * MathF.Sin(x * .35f + MathF.Sin(y * .05f) * 3) * H(x / 3, 0, 4) + (H(x, y / 6, 5) - .5f) * .25f,
        new Color(.35f, .26f, .18f), new Color(.85f, .72f, .55f));

    private static ImageTexture Rust() => Tex(64, (x, y) =>
        (x % 16 < 1 ? .2f : .7f) - H(x / 4, y / 4, 6) * .45f * H(x / 9, y / 9, 7),
        new Color(.35f, .20f, .12f), new Color(.85f, .85f, .82f));
}
