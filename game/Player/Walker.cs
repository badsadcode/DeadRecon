using System;
using DeadRecon.Combat;
using Godot;

namespace DeadRecon.Player;

/// <summary>
/// First-person test walker. WASD move, Shift run, hold C crouch (C while sprinting = slide), Alt toggles crouch, Q/E lean, Space jump, mouse look (click to capture, Esc to release),
/// F4 toggles fly mode (debug: no collision; Shift = fast, Space/Ctrl = up/down). Has health; guards can shoot it.
/// </summary>
public partial class Walker : CharacterBody3D, IDamageable
{
    public Camera3D Camera { get; private set; } = null!;
    public bool Flying { get; private set; }
    /// <summary>Mouse-look multiplier (the weapon lowers it while zoomed).</summary>
    public float SensitivityScale { get; set; } = 1;
    /// <summary>Set when a click captured the mouse; the weapon ignores the button until it's released.</summary>
    public bool FireLockedUntilRelease { get; set; }

    public float Health { get; private set; } = MaxHealth;
    public bool Dead => Health <= 0;
    public bool Crouching { get; private set; }
    public bool Running { get; private set; }
    /// <summary>0..1 how much the last hit still flashes on screen.</summary>
    public float HurtFlash { get; private set; }
    /// <summary>Raised when the walker is hit (damage, shooter position) and when it dies.</summary>
    public event Action<float, Vector3>? Hurt;
    public event Action? Died;

    public const float MaxHealth = 100;
    private const float WalkSpeed = 4.2f, RunSpeed = 7.5f, CrouchSpeed = 2.0f, JumpSpeed = 4.8f, Gravity = 9.81f;
    private const float FlySpeed = 40f, FlyFast = 250f, EyeHeight = 1.65f, CrouchEye = 1.05f, Sensitivity = .0025f;
    private float _yaw, _pitch, _eye = EyeHeight, _stepNoise, _deathTilt;

    /// <summary>Chest height (m above the feet): where guards aim and look.</summary>
    public Vector3 Chest => GlobalPosition + Vector3.Up * (Crouching ? .8f : 1.3f);

    public override void _Ready()
    {
        AddChild(new CollisionShape3D { Shape = new CapsuleShape3D { Radius = .35f, Height = 1.8f }, Position = new Vector3(0, .9f, 0) });
        Camera = new Camera3D { Position = new Vector3(0, EyeHeight, 0), Near = .05f, Far = 9000f, Fov = 70f };
        AddChild(Camera);
        FloorMaxAngle = Mathf.DegToRad(50);
    }

    public void Look(float yawDegrees, float pitchDegrees)
    {
        _yaw = Mathf.DegToRad(yawDegrees); _pitch = Mathf.DegToRad(pitchDegrees);
        ApplyLook();
    }

    /// <summary>
    /// Kick the view (radians): up by <paramref name="pitch"/>, sideways by <paramref name="yaw"/>. About a third
    /// stays (you have to pull down a little in long bursts); the rest springs back within a quarter second.
    /// </summary>
    public void AddRecoil(float pitch, float yaw)
    {
        _pitch = Math.Clamp(_pitch + pitch * .35f, -1.5f, 1.5f);
        _yaw += yaw * .5f;
        _recoilKick += pitch * .65f;
        _recoilYaw += yaw * .5f;
        ApplyLook();
    }

    private AudioStreamPlayer? _crack;

    /// <summary>A bullet just went past close by.</summary>
    public void HearCrack()
    {
        if (_crack == null) { _crack = new AudioStreamPlayer { Bus = "Effects", VolumeDb = -3, MaxPolyphony = 3 }; AddChild(_crack); }
        _crack.Stream = DeadRecon.Audio.Synth.Crack((int)(GD.Randi() % 4));
        _crack.PitchScale = (float)GD.RandRange(.9, 1.15);
        _crack.Play();
    }

    /// <summary>Extra aim offset in radians (x pitch, y yaw), e.g. scope sway. Doesn't change where you're facing.</summary>
    public Vector2 AimOffset { get; set; }
    /// <summary>Set by the weapon while aiming: you move carefully, no sprinting.</summary>
    public bool Aiming { get; set; }
    private float _recoilKick, _recoilYaw;

    public void SetFlying(bool flying)
    {
        Flying = flying;
        Velocity = Vector3.Zero;
        GetChild<CollisionShape3D>(0).Disabled = flying;
    }

    /// <summary>Tests only: hits still flash and count, but don't reduce health.</summary>
    public bool Invulnerable { get; set; }
    public int HitsTaken { get; private set; }
    private AudioStreamPlayer? _thud;
    private bool _crouchToggle;

    /// <summary>What's underfoot at a point (set by the scene, which knows the world and the weather).</summary>
    public Func<Vector3, DeadRecon.Audio.Synth.Surface>? SurfaceAt;
    private AudioStreamPlayer? _steps;
    private int _stepVariant;

    private void PlayStep()
    {
        if (_steps == null) { _steps = new AudioStreamPlayer { Bus = "Foley", MaxPolyphony = 3 }; AddChild(_steps); }
        var surface = SurfaceAt?.Invoke(GlobalPosition) ?? DeadRecon.Audio.Synth.Surface.Leaves;
        _stepVariant = (_stepVariant + 1 + (int)GD.Randi() % 3) % 6;
        _steps.Stream = DeadRecon.Audio.Synth.Step(surface, _stepVariant);
        _steps.VolumeDb = Crouching ? -14 : Running ? -2 : -6;
        _steps.PitchScale = (float)GD.RandRange(.9, 1.1);
        _steps.Play();
    }

    public void TakeHit(float damage, Vector3 at, Vector3 from)
    {
        if (Dead || Flying) return;
        HitsTaken++;
        if (Invulnerable) damage = 0;
        Health = Math.Max(0, Health - damage);
        HurtFlash = 1;
        if (_thud == null) { _thud = new AudioStreamPlayer { Stream = DeadRecon.Combat.Effects.ThudSound(), VolumeDb = -4 }; AddChild(_thud); }
        _thud.PitchScale = (float)GD.RandRange(.8, 1);
        _thud.Play();
        // A little flinch toward the shot.
        AddRecoil(Mathf.DegToRad(1.5f), Mathf.DegToRad((float)GD.RandRange(-1.5, 1.5)));
        Hurt?.Invoke(damage, from);
        if (Dead) Died?.Invoke();
    }

    public void Respawn(Vector3 at, float yawDegrees)
    {
        Health = MaxHealth;
        HurtFlash = 0;
        _deathTilt = 0;
        Velocity = Vector3.Zero;
        GlobalPosition = at;
        Look(yawDegrees, -4);
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } && Input.MouseMode != Input.MouseModeEnum.Captured)
        {
            Input.MouseMode = Input.MouseModeEnum.Captured;
            FireLockedUntilRelease = true; // the click that grabs the mouse must not also fire a shot
        }
        else if (e is InputEventKey { Pressed: true, Echo: false } key)
        {
            if (key.PhysicalKeycode == Key.F4 && !Dead) SetFlying(!Flying);
            // C: hold to crouch (tapped mid-sprint it's a slide). Alt: toggle crouch.
            if (key.PhysicalKeycode == Key.C && !Dead && !Flying)
            {
                // Sprinting = moving fast; "on the floor" with a little grace, because running over bumpy ground
                // leaves the floor for a split second all the time.
                var hv = new Vector2(Velocity.X, Velocity.Z).Length();
                if (hv > 5.2f && _sinceFloor < .25f && _slide <= 0) StartSlide();
            }
            if (key.PhysicalKeycode == Key.Alt && !Dead && !Flying) _crouchToggle = !_crouchToggle;
        }
        else if (e is InputEventMouseMotion motion && Input.MouseMode == Input.MouseModeEnum.Captured && !Dead)
        {
            _yaw -= motion.Relative.X * Sensitivity * SensitivityScale * DeadRecon.UI.Settings.MouseSensitivity;
            _pitch = Math.Clamp(_pitch - motion.Relative.Y * Sensitivity * SensitivityScale * DeadRecon.UI.Settings.MouseSensitivity * (DeadRecon.UI.Settings.InvertY ? -1 : 1), -1.5f, 1.5f);
            ApplyLook();
        }
    }

    private void ApplyLook()
    {
        Rotation = new Vector3(0, _yaw, 0);
        // Aim offsets on top of where you're looking: scope sway (from the weapon) and the springy part of recoil.
        Camera.Rotation = new Vector3(_pitch + AimOffset.X + _recoilKick, AimOffset.Y + _recoilYaw, _deathTilt - _lean * LeanRoll);
    }

    // ---------------------------------------------------------------- lean and slide

    /// <summary>-1 leaning left .. 1 leaning right (eased).</summary>
    public float Lean => _lean;
    public bool Sliding => _slide > 0;
    private const float LeanOffset = .42f, LeanRoll = .2f, SlideTime = .85f, SlideSpeed = 9.5f;
    private float _lean, _slide, _sinceFloor, _eyeVel, _leanVel;
    private Vector3 _slideDir;

    private void StartSlide()
    {
        _slide = SlideTime;
        var h = new Vector3(Velocity.X, 0, Velocity.Z);
        _slideDir = h.LengthSquared() > .1f ? h.Normalized() : -Transform.Basis.Z;
        Hearing.Make(GlobalPosition, 9, this);      // gravel and leaves: a slide isn't silent
    }

    /// <summary>
    /// How far we can lean toward a side (0..1) before the head would go into a trunk or a slope:
    /// lean as far as there's room, like peeking round a tree.
    /// </summary>
    private float LeanRoom(float side)
    {
        var head = GlobalPosition + Vector3.Up * _eye;
        var to = head + Transform.Basis.X * side * (LeanOffset + .15f);
        var q = PhysicsRayQueryParameters3D.Create(head, to);
        q.Exclude = new Godot.Collections.Array<Rid> { GetRid() };
        var hit = GetWorld3D().DirectSpaceState.IntersectRay(q);
        float room = hit.Count > 0 ? head.DistanceTo((Vector3)hit["position"]) - .15f : LeanOffset + .15f;
        if (DeadRecon.World.Foliage.RaycastTrunk(head, to) is { } wood) room = Math.Min(room, wood.T * (LeanOffset + .15f) - .12f);
        return Math.Clamp(room / LeanOffset, 0, 1);
    }

    public override void _PhysicsProcess(double delta)
    {
        float dt = (float)delta;
        float spring = MathF.Exp(-dt * 14);
        _recoilKick *= spring;
        _recoilYaw *= spring;
        HurtFlash = Math.Max(0, HurtFlash - dt * 1.6f);
        if (Dead)
        {
            // Slump to the ground and stay there.
            _eye = Mathf.MoveToward(_eye, .35f, dt * 2);
            _deathTilt = Mathf.MoveToward(_deathTilt, .9f, dt * 1.5f);
            Camera.Position = new Vector3(0, _eye, 0);
            ApplyLook();
            Velocity = new Vector3(0, Velocity.Y - Gravity * dt, 0);
            MoveAndSlide();
            return;
        }

        var input = new Vector2(
            (Input.IsPhysicalKeyPressed(Key.D) ? 1 : 0) - (Input.IsPhysicalKeyPressed(Key.A) ? 1 : 0),
            (Input.IsPhysicalKeyPressed(Key.S) ? 1 : 0) - (Input.IsPhysicalKeyPressed(Key.W) ? 1 : 0));
        bool fast = Input.IsPhysicalKeyPressed(Key.Shift) && !Aiming;   // Shift while aiming holds your breath instead

        if (Flying)
        {
            var look = Camera.GlobalTransform.Basis;
            var move = look * new Vector3(input.X, 0, input.Y);
            if (Input.IsPhysicalKeyPressed(Key.Space)) move += Vector3.Up;
            if (Input.IsPhysicalKeyPressed(Key.Ctrl)) move += Vector3.Down;
            GlobalPosition += move.LimitLength(1) * (fast ? FlyFast : FlySpeed) * dt;
            return;
        }

        Crouching = _crouchToggle || _slide > 0 || Input.IsPhysicalKeyPressed(Key.C);
        // Eye height on a critically damped spring: quick to start, eases into the end (a constant-speed move
        // that stops dead felt abrupt in the playtest). Faster for a slide.
        float eyeTarget = _slide > 0 ? CrouchEye - .15f : Crouching ? CrouchEye : EyeHeight;
        float w = _slide > 0 ? 16 : 10;                      // spring frequency: ~90% there in ~0.4 s
        _eyeVel += (w * w * (eyeTarget - _eye) - 2 * w * _eyeVel) * dt;
        _eye += _eyeVel * dt;

        // Lean: hold Q (left) or E (right); not while sprinting or sliding.
        float want = (Input.IsPhysicalKeyPressed(Key.E) ? 1 : 0) - (Input.IsPhysicalKeyPressed(Key.Q) ? 1 : 0);
        if (Running || _slide > 0) want = 0;
        if (want != 0) want *= LeanRoom(want);
        const float lw = 12;                                   // same spring as the crouch: eases in and out
        _leanVel += (lw * lw * (want - _lean) - 2 * lw * _leanVel) * dt;
        _lean += _leanVel * dt;
        if (Math.Abs(_lean) < .002f && want == 0) _lean = 0;
        Camera.Position = new Vector3(_lean * LeanOffset, _eye - Math.Abs(_lean) * .06f, 0);
        ApplyLook();

        float speed = Crouching ? CrouchSpeed : fast ? RunSpeed : WalkSpeed;
        if (_lean != 0) speed = Math.Min(speed, CrouchSpeed);          // you shuffle while leaning
        if (Aiming) speed = Math.Min(speed, WalkSpeed * .55f);           // aiming down the scope: careful steps
        var wish = (Transform.Basis * new Vector3(input.X, 0, input.Y)).LimitLength(1) * speed;
        Running = fast && !Crouching && wish.LengthSquared() > .1f;
        if (_slide > 0)
        {
            // Momentum carries you; it bleeds off over the slide, steering a little with the keys.
            _slide -= dt;
            float k = Math.Max(0, _slide / SlideTime);
            wish = (_slideDir * SlideSpeed * (.35f + .65f * k)) + wish * .15f;
        }
        _sinceFloor = IsOnFloor() ? 0 : _sinceFloor + dt;
        var v = Velocity;
        v.X = wish.X; v.Z = wish.Z;
        if (IsOnFloor()) { if (Input.IsPhysicalKeyPressed(Key.Space) && !Crouching) v.Y = JumpSpeed; }
        else v.Y -= Gravity * dt;
        Velocity = v;
        MoveAndSlide();
        // Trunks are solid: push out, and drop the part of our motion that goes into the bark so we glide round it.
        var pushed = DeadRecon.World.Foliage.PushOut(GlobalPosition, .35f);
        var nudge = pushed - GlobalPosition; nudge.Y = 0;
        if (nudge.LengthSquared() > 1e-8f)
        {
            GlobalPosition = pushed;
            var n = nudge.Normalized();
            var vel = Velocity;
            float into = vel.Dot(n);
            if (into < 0) Velocity = vel - n * into;
            _slide = Math.Min(_slide, .1f);   // sliding into a tree ends the slide
        }

        // Footsteps are audible to guards: running carries, sneaking barely does.
        if (IsOnFloor() && wish.LengthSquared() > .1f)
        {
            _stepNoise -= dt;
            if (_stepNoise <= 0)
            {
                _stepNoise = Running ? .32f : Crouching ? .62f : .5f;
                if (!Crouching) Hearing.Make(GlobalPosition, Running ? Hearing.RunningSteps : Hearing.WalkingSteps, this);
                PlayStep();
            }
        }
    }
}
