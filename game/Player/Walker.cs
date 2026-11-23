using System;
using Godot;

namespace DeadRecon.Player;

/// <summary>
/// First-person test walker. WASD move, Shift run, Space jump, mouse look (click to capture, Esc to release),
/// F toggles fly mode (no collision; Shift = fast, Space/C = up/down).
/// </summary>
public partial class Walker : CharacterBody3D
{
    public Camera3D Camera { get; private set; } = null!;
    public bool Flying { get; private set; }
    /// <summary>Mouse-look multiplier (the weapon lowers it while zoomed).</summary>
    public float SensitivityScale { get; set; } = 1;

    private const float WalkSpeed = 4.2f, RunSpeed = 7.5f, JumpSpeed = 4.8f, Gravity = 9.81f;
    private const float FlySpeed = 40f, FlyFast = 250f, EyeHeight = 1.65f, Sensitivity = .0025f;
    private float _yaw, _pitch;

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

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left })
            Input.MouseMode = Input.MouseModeEnum.Captured;
        else if (e is InputEventKey { Pressed: true, Echo: false } key)
        {
            if (key.PhysicalKeycode == Key.Escape) Input.MouseMode = Input.MouseModeEnum.Visible;
            if (key.PhysicalKeycode == Key.F) SetFlying(!Flying);
        }
        else if (e is InputEventMouseMotion motion && Input.MouseMode == Input.MouseModeEnum.Captured)
        {
            _yaw -= motion.Relative.X * Sensitivity * SensitivityScale;
            _pitch = Math.Clamp(_pitch - motion.Relative.Y * Sensitivity * SensitivityScale, -1.5f, 1.5f);
            ApplyLook();
        }
    }

    private void ApplyLook()
    {
        Rotation = new Vector3(0, _yaw, 0);
        Camera.Rotation = new Vector3(_pitch, 0, 0);
    }

    public override void _PhysicsProcess(double delta)
    {
        float dt = (float)delta;
        var input = new Vector2(
            (Input.IsPhysicalKeyPressed(Key.D) ? 1 : 0) - (Input.IsPhysicalKeyPressed(Key.A) ? 1 : 0),
            (Input.IsPhysicalKeyPressed(Key.S) ? 1 : 0) - (Input.IsPhysicalKeyPressed(Key.W) ? 1 : 0));
        bool fast = Input.IsPhysicalKeyPressed(Key.Shift);

        if (Flying)
        {
            var look = Camera.GlobalTransform.Basis;
            var move = look * new Vector3(input.X, 0, input.Y);
            if (Input.IsPhysicalKeyPressed(Key.Space)) move += Vector3.Up;
            if (Input.IsPhysicalKeyPressed(Key.C)) move += Vector3.Down;
            GlobalPosition += move.LimitLength(1) * (fast ? FlyFast : FlySpeed) * dt;
            return;
        }

        var wish = (Transform.Basis * new Vector3(input.X, 0, input.Y)).LimitLength(1) * (fast ? RunSpeed : WalkSpeed);
        var v = Velocity;
        v.X = wish.X; v.Z = wish.Z;
        if (IsOnFloor()) { if (Input.IsPhysicalKeyPressed(Key.Space)) v.Y = JumpSpeed; }
        else v.Y -= Gravity * dt;
        Velocity = v;
        MoveAndSlide();
    }
}
