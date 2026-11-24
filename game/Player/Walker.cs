using System;
using DeadRecon.Combat;
using Godot;

namespace DeadRecon.Player;

/// <summary>
/// First-person test walker. WASD move, Shift run, C crouch (toggle) or hold Alt, Space jump, mouse look (click to capture, Esc to release),
/// F toggles fly mode (no collision; Shift = fast, Space/Ctrl = up/down). Has health; guards can shoot it.
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

    /// <summary>Kick the view (radians): up by <paramref name="pitch"/>, sideways by <paramref name="yaw"/>.</summary>
    public void AddRecoil(float pitch, float yaw)
    {
        _pitch = Math.Clamp(_pitch + pitch, -1.5f, 1.5f);
        _yaw += yaw;
        ApplyLook();
    }

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
            if (key.PhysicalKeycode == Key.F && !Dead) SetFlying(!Flying);
            if (key.PhysicalKeycode == Key.C && !Dead && !Flying) _crouchToggle = !_crouchToggle;
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
        Camera.Rotation = new Vector3(_pitch, 0, _deathTilt);
    }

    public override void _PhysicsProcess(double delta)
    {
        float dt = (float)delta;
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
        bool fast = Input.IsPhysicalKeyPressed(Key.Shift);

        if (Flying)
        {
            var look = Camera.GlobalTransform.Basis;
            var move = look * new Vector3(input.X, 0, input.Y);
            if (Input.IsPhysicalKeyPressed(Key.Space)) move += Vector3.Up;
            if (Input.IsPhysicalKeyPressed(Key.Ctrl)) move += Vector3.Down;
            GlobalPosition += move.LimitLength(1) * (fast ? FlyFast : FlySpeed) * dt;
            return;
        }

        Crouching = _crouchToggle || Input.IsPhysicalKeyPressed(Key.Alt);
        _eye = Mathf.MoveToward(_eye, Crouching ? CrouchEye : EyeHeight, dt * 4);
        Camera.Position = new Vector3(0, _eye, 0);
        float speed = Crouching ? CrouchSpeed : fast ? RunSpeed : WalkSpeed;
        var wish = (Transform.Basis * new Vector3(input.X, 0, input.Y)).LimitLength(1) * speed;
        Running = fast && !Crouching && wish.LengthSquared() > .1f;
        var v = Velocity;
        v.X = wish.X; v.Z = wish.Z;
        if (IsOnFloor()) { if (Input.IsPhysicalKeyPressed(Key.Space) && !Crouching) v.Y = JumpSpeed; }
        else v.Y -= Gravity * dt;
        Velocity = v;
        MoveAndSlide();

        // Footsteps are audible to guards: running carries, sneaking barely does.
        if (IsOnFloor() && wish.LengthSquared() > .1f)
        {
            _stepNoise -= dt;
            if (_stepNoise <= 0)
            {
                _stepNoise = Running ? .32f : .5f;
                if (!Crouching) Hearing.Make(GlobalPosition, Running ? Hearing.RunningSteps : Hearing.WalkingSteps, this);
            }
        }
    }
}
