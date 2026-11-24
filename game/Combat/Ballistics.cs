using System;
using System.Collections.Generic;
using DeadRecon.World;
using Godot;

namespace DeadRecon.Combat;

/// <summary>
/// Bullets that actually fly: muzzle velocity, gravity, travel time. Each physics frame every bullet moves and checks
/// what it crossed (terrain/bodies via physics, trunks via the foliage registry); the hit happens on arrival.
/// Ghost Recon-style: drop and travel time, no wind, spin or air drag.
/// Each bullet is drawn as a comet: a bright head with a short fading tail.
/// </summary>
public partial class Ballistics : Node3D
{
    public static Ballistics? Current { get; private set; }

    public const float MuzzleVelocity = 750, Gravity = 9.81f, MaxRange = 1200;

    /// <summary>The upward angle that makes a sight aligned with the bore hit dead-on at <paramref name="metres"/>.</summary>
    public static float ZeroAngle(float metres)
    {
        float t = metres / MuzzleVelocity;
        return .5f * Gravity * t * t / metres;
    }

    public readonly record struct Hit(Vector3 Point, Vector3 Normal, GodotObject? Collider, Foliage.Plant? Trunk, float Distance);

    private sealed class Bullet
    {
        public Vector3 Pos, Vel, Origin;
        public float Travelled;
        public Godot.Collections.Array<Rid> Exclude = new();
        public Action<Hit?>? Done;
        public Comet? Visual;
    }

    private readonly List<Bullet> _bullets = new();

    public override void _Ready() { Current = this; }
    public override void _ExitTree() { if (Current == this) Current = null; }

    /// <summary>Fire one bullet. <paramref name="done"/> is called once: with the hit, or null if it flew out of range.</summary>
    public void Fire(Vector3 origin, Vector3 direction, Vector3 visualFrom, Rid exclude, Action<Hit?> done, bool enemy = false)
    {
        var b = new Bullet { Pos = origin, Origin = origin, Vel = direction.Normalized() * MuzzleVelocity, Done = done };
        b.Exclude.Add(exclude);
        if (DeadRecon.UI.Settings.Tracers)
        {
            b.Visual = new Comet { Enemy = enemy, Head = visualFrom, Tail = visualFrom };
            AddChild(b.Visual);
        }
        _bullets.Add(b);
        Step(b, 0.0f);
    }

    /// <summary>Just the comet, flying straight from one point to another at bullet speed (no hit logic).</summary>
    public void Visual(Vector3 from, Vector3 to, bool enemy)
    {
        if (!DeadRecon.UI.Settings.Tracers || from.DistanceTo(to) < 1) return;
        var c = new Comet { Enemy = enemy, Head = from, Tail = from };
        AddChild(c);
        _visuals.Add((c, from, to, 0));
    }

    private readonly List<(Comet C, Vector3 From, Vector3 To, float T)> _visuals = new();

    public override void _PhysicsProcess(double delta)
    {
        float dt = (float)delta;
        for (int i = _bullets.Count - 1; i >= 0; i--)
            if (!Step(_bullets[i], dt)) _bullets.RemoveAt(i);
        for (int i = _visuals.Count - 1; i >= 0; i--)
        {
            var (c, from, to, t) = _visuals[i];
            t += dt * MuzzleVelocity;
            float len = from.DistanceTo(to);
            if (!IsInstanceValid(c)) { _visuals.RemoveAt(i); continue; }
            if (t >= len) { c.Arrive(to); _visuals.RemoveAt(i); continue; }
            c.Head = from + (to - from) * (t / len);
            c.Visible = true;
            _visuals[i] = (c, from, to, t);
        }
    }

    /// <summary>Move a bullet by dt; returns false when it's finished (hit something or out of range).</summary>
    private bool Step(Bullet b, float dt)
    {
        var next = b.Pos + b.Vel * dt + Vector3.Down * (.5f * Gravity * dt * dt);
        b.Vel += Vector3.Down * Gravity * dt;
        if (dt > 0)
        {
            var q = PhysicsRayQueryParameters3D.Create(b.Pos, next);
            q.Exclude = b.Exclude;
            var hit = GetWorld3D().DirectSpaceState.IntersectRay(q);
            var end = hit.Count > 0 ? (Vector3)hit["position"] : next;
            if (Foliage.RaycastTrunk(b.Pos, end) is { } wood)
            {
                Finish(b, new Hit(wood.Point, wood.Normal, null, wood.Plant, b.Travelled + b.Pos.DistanceTo(wood.Point)));
                return false;
            }
            if (hit.Count > 0)
            {
                var at = (Vector3)hit["position"];
                Finish(b, new Hit(at, (Vector3)hit["normal"], hit["collider"].AsGodotObject(), null, b.Travelled + b.Pos.DistanceTo(at)));
                return false;
            }
        }
        b.Travelled += b.Pos.DistanceTo(next);
        b.Pos = next;
        b.Visual?.Advance(next);
        if (b.Travelled > MaxRange) { Finish(b, null); return false; }
        return true;
    }

    private static void Finish(Bullet b, Hit? hit)
    {
        if (hit is { } h) b.Visual?.Arrive(h.Point); else b.Visual?.Arrive(b.Pos);
        b.Done?.Invoke(hit);
    }

    // ================================================================= the comet

    /// <summary>A tracer drawn as a comet: bright glowing head, short tail fading behind it, ~2-3 px wide anywhere.</summary>
    public sealed partial class Comet : Node3D
    {
        public bool Enemy;
        public Vector3 Head, Tail;
        private const float TailLength = 7;
        private MeshInstance3D _tail = null!, _glow = null!;
        private float _dying = -1;
        private Vector3 _visualOffset;     // the bullet starts at the eye; the comet starts at the muzzle and merges in
        private Vector3 _start;
        private float _merge;
        private static StandardMaterial3D? _mat;
        private static ArrayMesh? _beam;

        public override void _Ready()
        {
            _mat ??= new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                BlendMode = BaseMaterial3D.BlendModeEnum.Add, VertexColorUseAsAlbedo = true, CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            };
            var c = Enemy ? new Color(1, .42f, .25f) : new Color(1, .86f, .5f);
            _beam ??= null;
            _tail = new MeshInstance3D { Mesh = Beam(new Color(c, 0), new Color(c, .95f)), MaterialOverride = _mat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            _glow = new MeshInstance3D
            {
                Mesh = new SphereMesh { Radius = .5f, Height = 1, RadialSegments = 8, Rings = 4 },
                MaterialOverride = new StandardMaterial3D
                {
                    ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, BlendMode = BaseMaterial3D.BlendModeEnum.Add,
                    AlbedoColor = new Color(c.R, c.G * .95f, c.B * .9f), Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                },
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            };
            AddChild(_tail);
            AddChild(_glow);
            _start = Head;
            Visible = false; // shown from the first real step on
        }

        /// <summary>A cross of two quads along -Z, coloured from tail (z=0) to head (z=-1).</summary>
        private static ArrayMesh Beam(Color tail, Color head)
        {
            var st = new SurfaceTool();
            st.Begin(Mesh.PrimitiveType.Triangles);
            foreach (var side in new[] { Vector3.Right, Vector3.Up })
            {
                void V(Vector3 p, Color c) { st.SetColor(c); st.AddVertex(p); }
                var a = -side * .5f; var b = side * .5f; var z = new Vector3(0, 0, -1);
                // Taper: the tail end is narrower.
                V(a * .3f, tail); V(b * .3f, tail); V(b + z, head);
                V(a * .3f, tail); V(b + z, head); V(a + z, head);
            }
            return st.Commit();
        }

        private Vector3? _first;

        /// <summary>The bullet is now at <paramref name="bulletPos"/> (on the eye line); draw the comet there.</summary>
        public void Advance(Vector3 bulletPos)
        {
            // The comet is drawn from the muzzle and converges onto the true path over the first ~15 m.
            _first ??= bulletPos;
            if (_visualOffset == Vector3.Zero && _merge == 0) _visualOffset = _start - _first.Value;
            float flown = bulletPos.DistanceTo(_first.Value);
            float k = Math.Max(0, 1 - flown / 15);
            Head = bulletPos + _visualOffset * k;
            Visible = true;
        }

        public void Arrive(Vector3 at) { Head = at; Visible = true; _dying = 0; }

        public override void _Process(double delta)
        {
            if (!Visible) return;
            var cam = GetViewport().GetCamera3D();
            float Width(Vector3 p) => Math.Max(.025f, (cam?.GlobalPosition.DistanceTo(p) ?? 10) * .004f * ((cam?.Fov ?? 70) / 70f));
            // Tail follows the head at most TailLength behind (shorter right after leaving the muzzle).
            var toTail = Tail - Head;
            if (toTail.Length() > TailLength) Tail = Head + toTail.Normalized() * TailLength;
            if (_dying >= 0)
            {
                _dying += (float)delta;
                // After impact the tail catches up with the head and the glow flares then fades.
                Tail = Tail.Lerp(Head, Math.Min(1, (float)delta * 14));
                if (_dying > .2f) { QueueFree(); return; }
            }
            var dir = Head - Tail;
            float len = dir.Length();
            float w = Width(Head);
            if (len > .01f)
            {
                var n = dir / len;
                var basis = Basis.LookingAt(n, Math.Abs(n.Y) > .99f ? Vector3.Right : Vector3.Up);
                _tail.GlobalTransform = new Transform3D(basis * Basis.FromScale(new Vector3(w, w, len)), Tail);
                _tail.Visible = true;
            }
            else _tail.Visible = false;
            // Seen from behind (the shooter's view) a comet is mostly its head: make it a clear glowing dot (~8 px).
            float glow = _dying >= 0 ? 4f * (1 - _dying / .2f) + .8f : 2.6f;
            _glow.GlobalPosition = Head;
            _glow.Scale = Vector3.One * w * glow;
        }

        // Keep the tail end where the head was last frame (continuous streak, no gaps at 60 fps).
        public override void _PhysicsProcess(double delta) { if (_dying < 0 && Visible && (Head - Tail).Length() < .001f) Tail = Head; }
    }
}
