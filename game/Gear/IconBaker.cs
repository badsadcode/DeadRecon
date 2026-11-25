using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;

namespace DeadRecon.Gear;

/// <summary>
/// Photographs 3D models for the interface: every attachment, the magazine, and the whole rifle as fitted (for the
/// HUD and the backpack). An off-screen viewport with its own little studio (key light, rim light, transparent
/// background, orthographic side view with the muzzle to the right). One picture per frame or two, cached by key.
/// </summary>
public partial class IconBaker : Node
{
    public static IconBaker? Current { get; private set; }

    private SubViewport _vp = null!;
    private Camera3D _cam = null!;
    private Node3D _stage = null!;
    private readonly Dictionary<string, Texture2D> _done = new();
    private readonly Queue<(string Key, Func<Node3D?> Make, Vector2I Size)> _queue = new();
    private readonly HashSet<string> _queued = new();
    private bool _busy;

    /// <summary>Something new was photographed (the interface redraws).</summary>
    public event Action<string>? Baked;

    public override void _Ready()
    {
        Current = this;
        _vp = new SubViewport
        {
            Size = new Vector2I(256, 128), OwnWorld3D = true, TransparentBg = true, Msaa3D = Viewport.Msaa.Msaa4X,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled,
        };
        AddChild(_vp);
        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.ClearColor, AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightColor = new Color(.75f, .78f, .8f), AmbientLightEnergy = .9f, TonemapMode = Godot.Environment.ToneMapper.Aces,
        };
        _vp.AddChild(new WorldEnvironment { Environment = env });
        _vp.AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-35, -60, 0), LightEnergy = 1.6f, LightColor = new Color(1f, .96f, .9f) });
        _vp.AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-10, 120, 0), LightEnergy = .9f, LightColor = new Color(.75f, .85f, 1f) });
        _cam = new Camera3D { Projection = Camera3D.ProjectionType.Orthogonal, Near = .01f, Far = 20 };
        _vp.AddChild(_cam);
        _stage = new Node3D();
        _vp.AddChild(_stage);
        foreach (var def in ItemDef.All.Values)
        {
            var d = def;
            if (d.Kind is ItemKind.Attachment or ItemKind.Magazine)
                Request($"item:{d.Id}", () => RifleBuilder.ModelFor(d), d.Kind == ItemKind.Magazine ? new Vector2I(128, 256) : new Vector2I(256, 160));
        }
    }

    public override void _ExitTree() { if (Current == this) Current = null; }

    public Texture2D? Get(string key) => _done.TryGetValue(key, out var t) ? t : null;

    public void Request(string key, Func<Node3D?> make, Vector2I size, bool replace = false)
    {
        if (!replace && (_done.ContainsKey(key) || _queued.Contains(key))) return;
        _queued.Add(key);
        _queue.Enqueue((key, make, size));
        if (!_busy) _ = Pump();
    }

    /// <summary>The whole rifle with this loadout.</summary>
    public static string RifleKey(Loadout l) => $"rifle:{l.Optic}:{l.Muzzle}:{l.Side}:{l.Underbarrel}:{l.Paint}";

    public void RequestRifle(Loadout l) => Request(RifleKey(l), () => RifleBuilder.Build(l, lights: false)?.Model, new Vector2I(640, 240));

    private async Task Pump()
    {
        _busy = true;
        while (_queue.Count > 0)
        {
            var (key, make, size) = _queue.Dequeue();
            Node3D? model = null;
            try { model = make(); } catch (Exception e) { GD.PushWarning($"Icon {key}: {e.Message}"); }
            if (model == null) { _queued.Remove(key); continue; }
            _stage.AddChild(model);
            var box = RifleBuilder.Bounds(model);
            // Side view from -X: +Z (the muzzle) points right, +Y up.
            float aspect = size.X / (float)size.Y;
            _cam.Size = Math.Max(box.Size.Y, box.Size.Z / aspect) * 1.12f;
            var c = box.GetCenter();
            _cam.Position = new Vector3(c.X - 5, c.Y, c.Z);
            _cam.RotationDegrees = new Vector3(0, -90, 0);
            _vp.Size = size;
            _vp.RenderTargetUpdateMode = SubViewport.UpdateMode.Once;
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            if (!IsInstanceValid(this)) return;
            var img = _vp.GetTexture().GetImage();
            img.GenerateMipmaps();
            _done[key] = ImageTexture.CreateFromImage(img);
            _queued.Remove(key);
            model.QueueFree();
            Baked?.Invoke(key);
        }
        _busy = false;
    }
}
