using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DeadRecon.Player;
using DeadRecon.World;
using Godot;

namespace DeadRecon;

/// <summary>
/// Milestone-0 test scene: load the baked world, stream terrain and trees around a first-person walker.
/// User args (after "--"): --world=&lt;name&gt; (default jungle), --shots=&lt;dir&gt; to save a few screenshots and quit.
/// </summary>
public partial class Main : Node3D
{
    private BakedWorld _world = null!;
    private TerrainStreamer _terrain = null!;
    private TreeScatter _trees = null!;
    private Walker _walker = null!;
    private Label _hud = null!;
    private bool _waitingForGround = true;

    public override void _Ready()
    {
        var args = OS.GetCmdlineUserArgs();
        string Arg(string name, string fallback) =>
            args.FirstOrDefault(a => a.StartsWith($"--{name}="))?.Split('=', 2)[1] ?? fallback;

        _hud = new Label { Position = new Vector2(12, 10) };
        _hud.AddThemeColorOverride("font_shadow_color", Colors.Black);
        var layer = new CanvasLayer();
        layer.AddChild(_hud);
        AddChild(layer);

        string dir = ProjectSettings.GlobalizePath($"res://world/{Arg("world", "jungle")}");
        if (!File.Exists(Path.Combine(dir, "world.json")))
        {
            _hud.Text = $"No baked world at {dir}\nBake it first:\n  dotnet run -c Release --project tools/WorldBaker -- content/world/jungle.world.json";
            return;
        }
        _world = BakedWorld.Load(dir);

        AddChild(MakeEnvironment());
        AddChild(new DirectionalLight3D
        {
            RotationDegrees = new Vector3(-48, 35, 0), ShadowEnabled = true, LightEnergy = 1.15f,
            LightColor = new Color(1f, .95f, .86f), DirectionalShadowMaxDistance = 220,
        });

        _walker = new Walker { ProcessMode = ProcessModeEnum.Disabled };
        AddChild(_walker);
        var spawn = FindSpawn();
        _walker.GlobalPosition = spawn.position;
        _walker.Look(spawn.yaw, -4);

        _terrain = new TerrainStreamer { World = _world, Viewer = _walker };
        _trees = new TreeScatter { World = _world, Viewer = _walker };
        AddChild(_terrain);
        AddChild(_trees);

        string shots = Arg("shots", "");
        if (shots != "") _ = TakeShots(shots);
    }

    public override void _Process(double delta)
    {
        if (_terrain == null) return;
        if (_waitingForGround && _terrain.HasCollisionAt(_walker.GlobalPosition))
        {
            _waitingForGround = false;
            _walker.ProcessMode = ProcessModeEnum.Inherit;
        }
        var p = _walker.GlobalPosition;
        _hud.Text =
            $"DEAD RECON - terrain test   {Engine.GetFramesPerSecond()} fps\n" +
            $"pos {p.X:0} {p.Y:0} {p.Z:0}   ground {_world.HeightAt(p.X, p.Z):0} m   {(_walker.Flying ? "FLYING" : "walking")}\n" +
            $"trees drawn {_trees.TreesShown:N0} ({_trees.VariantCount} kinds)   full-detail tiles {_terrain.TilesAtFullDetail}\n" +
            (_waitingForGround ? "loading ground...\n" : "") +
            "click: grab mouse   WASD/Shift/Space   F: fly (Space/C up/down)   Esc: release mouse";
    }

    /// <summary>Spawn on dry ground a little way from the water nearest the centre, facing the water.</summary>
    private (Vector3 position, float yaw) FindSpawn()
    {
        int mid = _world.SamplesPerSide / 2, best = int.MaxValue, bx = mid, bz = mid;
        for (int gz = 0; gz < _world.SamplesPerSide; gz += 8)
        for (int gx = 0; gx < _world.SamplesPerSide; gx += 8)
        {
            if (!_world.IsWet(gx, gz)) continue;
            int d = (gx - mid) * (gx - mid) + (gz - mid) * (gz - mid);
            if (d < best) { best = d; bx = gx; bz = gz; }
        }
        // Walk from the water back toward the centre until dry and a few metres clear.
        var dir = new Vector2(mid - bx, mid - bz);
        dir = dir.Length() > 0 ? dir.Normalized() : Vector2.Right;
        int x = bx, z = bz;
        for (int k = 0; k < 400; k++)
        {
            x = bx + (int)(dir.X * k); z = bz + (int)(dir.Y * k);
            if (!_world.NearWater(x, z, 6)) break;
        }
        float wx = _world.SampleToWorld(x), wz = _world.SampleToWorld(z);
        float yaw = Mathf.RadToDeg(MathF.Atan2(dir.X, dir.Y)); // face back toward the water (-Z forward)
        return (new Vector3(wx, _world.HeightAt(wx, wz) + .2f, wz), yaw);
    }

    private static WorldEnvironment MakeEnvironment()
    {
        var sky = new ProceduralSkyMaterial
        {
            SkyTopColor = new Color(.30f, .45f, .62f), SkyHorizonColor = new Color(.72f, .76f, .74f),
            GroundHorizonColor = new Color(.55f, .58f, .52f), GroundBottomColor = new Color(.15f, .18f, .14f),
        };
        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Sky,
            Sky = new Sky { SkyMaterial = sky },
            AmbientLightSource = Godot.Environment.AmbientSource.Sky,
            TonemapMode = Godot.Environment.ToneMapper.Filmic,
            SsaoEnabled = true,
            FogEnabled = true,
            FogLightColor = new Color(.66f, .72f, .70f),
            FogDensity = .00055f,
            FogAerialPerspective = .5f,
            FogSkyAffect = .4f,
            GlowEnabled = true,
        };
        return new WorldEnvironment { Environment = env };
    }

    /// <summary>Automated check: a few viewpoints, each saved as a PNG once streaming has settled; then quit.</summary>
    private async Task TakeShots(string dir)
    {
        Directory.CreateDirectory(dir);
        var start = _walker.GlobalPosition;
        float startYaw = Mathf.RadToDeg(_walker.Rotation.Y);
        var views = new (string name, Vector3 offset, float yaw, float pitch, bool fly)[]
        {
            ("1_ground", Vector3.Zero, startYaw, -2, false),
            ("2_treetops", new Vector3(0, 60, 0), startYaw, -12, true),
            ("3_overview", new Vector3(0, 700, 0), startYaw, -28, true),
        };
        foreach (var v in views)
        {
            _walker.SetFlying(v.fly);
            _walker.GlobalPosition = start + v.offset;
            _walker.Look(v.yaw, v.pitch);
            for (int f = 0; f < 1800 && (f < 30 || !_terrain.Settled || _trees.Pending > 0); f++)
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            for (int f = 0; f < 20; f++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            GetViewport().GetTexture().GetImage().SavePng(Path.Combine(dir, v.name + ".png"));
            GD.Print($"shot {v.name}: fps {Engine.GetFramesPerSecond()}, trees {_trees.TreesShown}, on floor {_walker.IsOnFloor()}, y {_walker.GlobalPosition.Y:0.0}");
        }
        GetTree().Quit();
    }
}
