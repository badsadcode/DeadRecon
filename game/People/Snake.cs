using System;
using DeadRecon.Combat;
using DeadRecon.Gear;
using DeadRecon.Player;
using DeadRecon.World;
using Godot;

namespace DeadRecon.People;

/// <summary>A kind of snake ("snakes" content section; mods add more). Temper: shy (slips away), warns (rears and
/// hisses, strikes if you keep coming), ambush (sits tight, bites if you step close), harmless (big, just leaves).</summary>
public sealed record SnakeDef(
    string Id, string Name, float Length, float Radius, string Temper, float Venom,
    string Base, string Pattern, string Belly, int Style, float Scale = 14, bool Hood = false, float Weight = 1, bool Night = false);

/// <summary>
/// A snake: one tube mesh bent by snake.gdshader (slithering, coiled, rearing, hooded, striking). Wanders, rests
/// coiled, and reacts to you by its temper. A bite poisons (Kit.Venom) until a dressing.
/// </summary>
public partial class Snake : Node3D, IDamageable
{
    public SnakeDef Def = null!;
    public Walker Player = null!;
    public BakedWorld World = null!;
    public int Seed;

    public bool Dead { get; private set; }

    private ShaderMaterial _mat = null!;
    private Node3D _eyes = null!, _tongue = null!;
    private AudioStreamPlayer3D _voice = null!;
    private readonly RandomNumberGenerator _rng = new();
    private float _phase, _slither, _coil = 1, _rear, _hood, _strike, _strikeT = -1, _think, _hiss, _tongueT, _flee, _health = 25, _bitCool;
    private Vector3 _goal;
    private bool _moving;

    public override void _Ready()
    {
        _rng.Seed = (ulong)Seed;
        _health = 12 + Def.Length * 8;
        _mat = new ShaderMaterial { Shader = GD.Load<Shader>("res://game/People/snake.gdshader") };
        _mat.SetShaderParameter("length", Def.Length);
        _mat.SetShaderParameter("base_color", new Color(Def.Base));
        _mat.SetShaderParameter("pattern_color", new Color(Def.Pattern));
        _mat.SetShaderParameter("belly_color", new Color(Def.Belly));
        _mat.SetShaderParameter("pattern", Def.Style);
        _mat.SetShaderParameter("pattern_scale", Def.Scale);
        AddChild(new MeshInstance3D { Mesh = Tube(Def.Length, Def.Radius), MaterialOverride = _mat, ExtraCullMargin = Def.Length });
        // Eyes and tongue ride on the head (moved with the shader's head maths each frame).
        _eyes = new Node3D();
        var black = new StandardMaterial3D { AlbedoColor = new Color(.05f, .04f, .02f), Roughness = .1f };
        foreach (float s in new[] { -1f, 1f })
            _eyes.AddChild(new MeshInstance3D { Mesh = new SphereMesh { Radius = Def.Radius * .28f, Height = Def.Radius * .56f }, MaterialOverride = black, Position = new Vector3(s * Def.Radius * .78f, Def.Radius * .35f, -Def.Radius * 1.2f) });
        AddChild(_eyes);
        _tongue = new Node3D();
        var red = new StandardMaterial3D { AlbedoColor = new Color(.75f, .08f, .12f) };
        foreach (float s in new[] { -1f, 1f })
            _tongue.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(.006f, .004f, Def.Radius * 1.4f) }, MaterialOverride = red, Position = new Vector3(s * .006f, 0, Def.Radius * .7f), Rotation = new Vector3(0, s * .25f, 0) });
        _eyes.AddChild(_tongue);
        _tongue.Position = new Vector3(0, 0, Def.Radius * .4f);
        _tongue.Visible = false;
        // Collision along the body (bullets, and you can't walk through a python).
        var body = new SnakeBody { Owner2 = this, CollisionLayer = 1, CollisionMask = 0 };
        body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(Def.Radius * 4, Def.Radius * 2.4f, Def.Length * .7f) }, Position = new Vector3(0, Def.Radius, -Def.Length * .35f) });
        AddChild(body);
        _voice = new AudioStreamPlayer3D { UnitSize = 3, MaxDistance = 25, Bus = "Effects", VolumeDb = -2, Position = new Vector3(0, .2f, 0) };
        AddChild(_voice);
        _goal = GlobalPosition;
        Hearing.Made += OnHeard;
    }

    public override void _ExitTree() => Hearing.Made -= OnHeard;

    /// <summary>The tube: head at z = 0, tail at z = -length. Fat behind the head, a neck, tapering to the tail.</summary>
    private static ArrayMesh Tube(float length, float radius)
    {
        const int Rings = 60, Sides = 10;
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        float R(float t) => t >= 1 ? 0 : t < .045f ? radius * MathF.Sqrt(MathF.Sin(t / .045f * MathF.PI * .5f))     // rounded snout to head
                         : t < .08f ? radius * (1f - .22f * MathF.Sin((t - .045f) / .035f * MathF.PI))        // neck
                         : radius * Mathf.Lerp(1.1f, .12f, MathF.Pow((t - .08f) / .92f, 1.6f));              // body to tail
        for (int i = 0; i < Rings; i++)
            for (int j = 0; j < Sides; j++)
            {
                Vector3 P(int ii, int jj)
                {
                    float t = ii / (float)(Rings - 1), a = jj / (float)Sides * MathF.Tau, r = R(t);
                    return new Vector3(MathF.Sin(a) * r, MathF.Cos(a) * r * .8f + r * .8f, -t * length);
                }
                Vector2 UV(int ii, int jj) => new(ii / (float)(Rings - 1), jj / (float)Sides);
                if (i == Rings - 1) continue;
                var quad = new[] { (i, j), (i + 1, j), (i + 1, j + 1), (i, j), (i + 1, j + 1), (i, j + 1) };
                foreach (var (ii, jj) in quad) { st.SetUV(UV(ii, jj % Sides == 0 && jj > 0 ? Sides : jj)); st.AddVertex(P(ii, jj)); }
            }
        // Cap the snout.
        st.Index();
        st.GenerateNormals();
        return st.Commit();
    }

    public override void _Process(double delta)
    {
        if (Dead) return;
        float dt = (float)delta;
        _bitCool -= dt; _flee -= dt;
        var me = GlobalPosition;
        float toPlayer = Player.Dead ? 999 : new Vector2(Player.GlobalPosition.X - me.X, Player.GlobalPosition.Z - me.Z).Length();
        float warnAt = Def.Temper switch { "warns" => 5, "ambush" => 1.8f, "harmless" => 4, _ => 6 };
        bool threatened = toPlayer < warnAt && _flee <= 0;
        float wantRear = 0, wantHood = 0;

        if (threatened && Def.Temper is "shy" or "harmless") { Flee(Player.GlobalPosition); }
        else if (threatened && Def.Temper is "warns" or "ambush")
        {
            // Stand your ground: face him, rear up, hiss; strike if he comes closer still.
            _moving = false;
            Face(Player.GlobalPosition, dt * 5);
            wantRear = Def.Temper == "warns" ? 1 : .35f;
            wantHood = Def.Hood ? 1 : 0;
            _hiss -= dt;
            if (_hiss <= 0) { _hiss = _rng.RandfRange(1, 2); Hiss(); }
            if (toPlayer < (Def.Temper == "warns" ? 1.9f : 1.3f) && _bitCool <= 0 && _strikeT < 0) { _strikeT = 0; _bitCool = 2.2f; }
            if (toPlayer > warnAt * .8f && Def.Temper == "warns" && _rng.Randf() < dt * .3f) Flee(Player.GlobalPosition);   // sometimes thinks better of it
        }
        else
        {
            _think -= dt;
            if (_think <= 0)
            {
                _think = _rng.RandfRange(4, 12);
                _moving = _rng.Randf() < .5f;
                if (_moving)
                {
                    float a = _rng.Randf() * MathF.Tau;
                    _goal = me + new Vector3(MathF.Cos(a), 0, MathF.Sin(a)) * _rng.RandfRange(3, 9);
                }
            }
        }
        // The strike: a quick lunge and back; bites if he's still in reach at the end of it.
        if (_strikeT >= 0)
        {
            _strikeT += dt;
            _strike = _strikeT < .12f ? _strikeT / .12f : Math.Max(0, 1 - (_strikeT - .12f) / .25f);
            if (_strikeT >= .12f && _strikeT - dt < .12f && toPlayer < (Def.Temper == "warns" ? 2.3f : 1.6f) && !Player.Dead) Bite();
            if (_strikeT > .4f) _strikeT = -1;
        }
        // Move.
        float speed = _flee > 0 ? .9f + Def.Length * .15f : .35f + Def.Length * .05f;
        var to = _goal - me; to.Y = 0;
        bool going = (_moving || _flee > 0) && to.Length() > .3f;
        if (going)
        {
            var dir = to.Normalized();
            var next = me + dir * speed * dt;
            var (gx, gz) = World.ToSample(next.X, next.Z);
            if (World.IsWet((int)gx, (int)gz) && Def.Id != "python") { _moving = false; _goal = me; }
            else
            {
                next = Foliage.PushOut(next, .2f);
                GlobalPosition = next with { Y = World.HeightAt(next.X, next.Z) };
                Face(me + dir, dt * 3);
                _phase += speed * dt * 9 / Def.Length * 1.6f;
            }
        }
        else { _moving = false; GlobalPosition = me with { Y = World.HeightAt(me.X, me.Z) }; }
        _slither = Mathf.MoveToward(_slither, going ? 1 : 0, dt * 2);
        _coil = Mathf.MoveToward(_coil, going || threatened ? 0 : 1, dt * .6f);
        _rear = Mathf.MoveToward(_rear, wantRear, dt * 3);
        _hood = Mathf.MoveToward(_hood, wantHood, dt * 2.5f);
        _mat.SetShaderParameter("phase", _phase);
        _mat.SetShaderParameter("slither", _slither);
        _mat.SetShaderParameter("coil", _coil);
        _mat.SetShaderParameter("rear", _rear);
        _mat.SetShaderParameter("hood", _hood);
        _mat.SetShaderParameter("strike", _strike);
        _mat.SetShaderParameter("wet", DeadRecon.World.Weather.Current?.Wetness ?? 0);
        // The head (where the shader puts it).
        var L = Def.Length;
        // Same maths as the shader's rearing, at the head (t = 0).
        const float F = .24f;
        float hu = 1 - .05f / F, lift = _rear;
        _eyes.Position = new Vector3(0, lift * hu * F * L * .85f, lift * (-F * L * (1 - .25f * hu) + .05f * L) + _strike * L * .35f);
        // The tongue flicks now and then (more when it's tasting the air for you).
        _tongueT -= dt;
        if (_tongueT <= 0) _tongueT = threatened ? _rng.RandfRange(.4f, 1) : _rng.RandfRange(2, 6);
        _tongue.Visible = _tongueT < .18f;
        _tongue.Scale = new Vector3(1, 1, _tongue.Visible ? 1 + MathF.Sin(_tongueT * 60) * .3f : 1);
    }

    /// <summary>Tests: slither over there.</summary>
    public void TestGo(Vector3 goal) { _goal = goal; _moving = true; _think = 30; }

    private void Face(Vector3 at, float rate)
    {
        var d = at - GlobalPosition; d.Y = 0;
        if (d.LengthSquared() < .001f) return;
        Rotation = new Vector3(0, Mathf.LerpAngle(Rotation.Y, MathF.Atan2(d.X, d.Z), Math.Min(1, rate)), 0);
    }

    private void Flee(Vector3 from)
    {
        var away = (GlobalPosition - from) with { Y = 0 };
        if (away.LengthSquared() < .01f) away = Vector3.Forward;
        _goal = GlobalPosition + away.Normalized().Rotated(Vector3.Up, _rng.RandfRange(-.6f, .6f)) * _rng.RandfRange(8, 14);
        _flee = _rng.RandfRange(6, 10);
        _moving = true;
    }

    private void Hiss()
    {
        _voice.Stream = DeadRecon.Audio.Synth.Animal("hiss", _rng.RandiRange(0, 2));
        _voice.PitchScale = 1.2f - Def.Length * .08f;
        _voice.Play();
    }

    private void Bite()
    {
        Player.TakeHit(6 + Def.Length * 2, Player.GlobalPosition + Vector3.Up * .3f, GlobalPosition);
        if (Def.Venom > 0 && Kit.Current is { } kit)
        {
            kit.Poison(Def.Venom);
            DeadRecon.UI.Hud.Toast($"Bitten by a {Def.Name.ToLower()}! Venom: use a dressing (H)");
        }
        else DeadRecon.UI.Hud.Toast($"Bitten by a {Def.Name.ToLower()}. Not venomous. Still rude.");
        GD.Print($"[snake] {Def.Id} bit you");
    }

    private void OnHeard(Vector3 at, float radius, Node source)
    {
        if (Dead || radius < Hearing.Gunshot * .25f || at.DistanceTo(GlobalPosition) > Math.Min(radius, 60)) return;
        if (Def.Temper != "ambush") Flee(at);
    }

    public void TakeHit(float damage, Vector3 at, Vector3 from)
    {
        if (Dead) return;
        _health -= damage;
        Effects.BloodHit(this, at, at - from);
        if (_health <= 0)
        {
            Dead = true;
            _mat.SetShaderParameter("slither", 0f); _mat.SetShaderParameter("rear", 0f); _mat.SetShaderParameter("hood", 0f); _mat.SetShaderParameter("coil", .4f);
            _tongue.Visible = false;
            GetNodeOrNull<SnakeBody>("SnakeBody")?.QueueFree();
            GetTree().CreateTimer(60).Timeout += () => { if (IsInstanceValid(this)) QueueFree(); };
            return;
        }
        Flee(from);
    }

    public partial class SnakeBody : StaticBody3D, IDamageable
    {
        public Snake Owner2 = null!;
        public override void _Ready() => Name = "SnakeBody";
        public void TakeHit(float damage, Vector3 at, Vector3 from) => Owner2.TakeHit(damage, at, from);
    }
}
