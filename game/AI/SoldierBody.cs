using System;
using System.Collections.Generic;
using Godot;

namespace DeadRecon.AI;

/// <summary>
/// A BadCharacters soldier holding a BadGun rifle.
/// <para>The character's own clips (Idle/Walk/Run) drive the legs and body. There is no rifle-holding clip, so after the
/// animation is applied each frame the arms are re-posed with two-bone IK: the right hand to the gun's
/// SOCKET_Hand_Grip, the left to SOCKET_Hand_Support. The gun's butt (SOCKET_Shoulder) sits at the right shoulder and
/// pitches with <see cref="AimPitch"/>; <see cref="Ready"/> blends from "low ready" to shouldered. <see cref="Crouch"/>
/// lowers the hips and bends the knees with the same IK, feet staying where the animation put them.</para>
/// <para>Conventions (BadCharacters RIG.md): +Y up, character faces +Z, left is +X, every bone's rest rotation is
/// identity. BadGun: +Z is the muzzle direction, origin at the grip. So both face +Z and the gun needs no turning.</para>
/// </summary>
public partial class SoldierBody : Node3D
{
    public string CharacterScene = "";
    public string GunScene = "res://assets/weapons/BadGun_rifle_vietnam_97232.glb";

    /// <summary>Horizontal speed (m/s): picks Idle/Walk/Run and their playback speed.</summary>
    public float Speed { get; set; }
    /// <summary>Gun elevation in radians (up positive).</summary>
    public float AimPitch { get; set; }
    /// <summary>0 = gun lowered (relaxed patrol), 1 = shouldered and aiming.</summary>
    public new float Ready { get; set; }
    /// <summary>0 standing .. 1 crouched.</summary>
    public float Crouch { get; set; }
    public Node3D? Muzzle { get; private set; }
    public bool Loaded { get; private set; }

    private Skeleton3D _skel = null!;
    private AnimationPlayer? _anim;
    private Node3D _model = null!, _gun = null!;
    private string _clip = "";
    private float _scale = 1;
    private Vector3 _gunGrip, _gunSupport, _gunButt;    // socket positions in gun space
    private readonly Dictionary<string, int> _bones = new();

    public override void _Ready()
    {
        if (!ResourceLoader.Exists(CharacterScene)) { GD.PushWarning($"No character at {CharacterScene}"); return; }
        _model = ResourceLoader.Load<PackedScene>(CharacterScene).Instantiate<Node3D>();
        _model.RotationDegrees = new Vector3(0, 180, 0); // character faces +Z, Godot actors face -Z
        AddChild(_model);
        _skel = Find<Skeleton3D>(_model) ?? throw new InvalidOperationException("Character has no skeleton.");
        _anim = Find<AnimationPlayer>(_model);
        foreach (var n in new[] { "Hips", "Spine", "Chest", "UpperChest", "Head",
                                  "LeftShoulder", "LeftUpperArm", "LeftLowerArm", "LeftHand",
                                  "RightShoulder", "RightUpperArm", "RightLowerArm", "RightHand",
                                  "LeftUpperLeg", "LeftLowerLeg", "LeftFoot", "RightUpperLeg", "RightLowerLeg", "RightFoot" })
            _bones[n] = _skel.FindBone(n);
        foreach (var mi in FindAll<GeometryInstance3D>(_model)) mi.CastShadow = GeometryInstance3D.ShadowCastingSetting.On;

        // Characters are exported at real height (BadCharacters recipe body.height) and never scaled here:
        // physics bodies (the ragdoll) misbehave under a scaled parent.
        _scale = 1;

        if (_anim != null)
        {
            _anim.CallbackModeProcess = AnimationMixer.AnimationCallbackModeProcess.Manual; // we apply it, then add IK
            foreach (var clip in new[] { "Idle", "Walk", "Run" })
                if (_anim.HasAnimation(clip)) _anim.GetAnimation(clip).LoopMode = Animation.LoopModeEnum.Linear;
            Play("Idle");
        }

        if (ResourceLoader.Exists(GunScene))
        {
            _gun = ResourceLoader.Load<PackedScene>(GunScene).Instantiate<Node3D>();
            _skel.AddChild(_gun); // in skeleton space, like the bones
            Muzzle = _gun.FindChild("SOCKET_Muzzle", true, false) as Node3D;
            _gunGrip = SocketIn(_gun, "SOCKET_Hand_Grip", Vector3.Zero);
            _gunSupport = SocketIn(_gun, "SOCKET_Hand_Support", new Vector3(0, -.04f, .37f));
            _gunButt = SocketIn(_gun, "SOCKET_Shoulder", new Vector3(0, -.05f, -.3f));
        }
        Loaded = true;
    }

    private void Play(string clip)
    {
        if (_anim == null || _clip == clip || !_anim.HasAnimation(clip)) return;
        _clip = clip;
        _anim.Play(clip, .25);
    }

    // ================================================================= ragdoll

    public bool Ragdolled { get; private set; }

    /// <summary>One ragdoll part: bone, the bone it points to (for capsule length), radius, mass, joint.</summary>
    private sealed record Part(string Bone, string? Toward, float Radius, float Mass, PhysicalBone3D.JointTypeEnum Joint);
    private static readonly Part[] Parts =
    {
        new("Hips", "Spine", .14f, 12, PhysicalBone3D.JointTypeEnum.None),
        new("Spine", "UpperChest", .13f, 10, PhysicalBone3D.JointTypeEnum.Cone),
        new("UpperChest", "Neck", .15f, 12, PhysicalBone3D.JointTypeEnum.Cone),
        new("Head", null, .13f, 5, PhysicalBone3D.JointTypeEnum.Cone),
        new("LeftUpperArm", "LeftLowerArm", .055f, 2.5f, PhysicalBone3D.JointTypeEnum.Cone),
        new("LeftLowerArm", "LeftHand", .05f, 1.8f, PhysicalBone3D.JointTypeEnum.Hinge),
        new("RightUpperArm", "RightLowerArm", .055f, 2.5f, PhysicalBone3D.JointTypeEnum.Cone),
        new("RightLowerArm", "RightHand", .05f, 1.8f, PhysicalBone3D.JointTypeEnum.Hinge),
        new("LeftUpperLeg", "LeftLowerLeg", .08f, 8, PhysicalBone3D.JointTypeEnum.Cone),
        new("LeftLowerLeg", "LeftFoot", .065f, 4, PhysicalBone3D.JointTypeEnum.Hinge),
        new("RightUpperLeg", "RightLowerLeg", .08f, 8, PhysicalBone3D.JointTypeEnum.Cone),
        new("RightLowerLeg", "RightFoot", .065f, 4, PhysicalBone3D.JointTypeEnum.Hinge),
    };

    /// <summary>
    /// Go limp: stop animating, hand the bones to physics (Godot PhysicalBoneSimulator3D), drop the rifle, and give
    /// the body part nearest the hit a shove along the shot. Ragdoll parts only collide with the world (layer 1),
    /// not with each other, so overlapping limbs can't explode apart.
    /// </summary>
    public void Ragdoll(Vector3 hitPoint, Vector3 shotDirection, float push = 1)
    {
        if (!Loaded || Ragdolled) return;
        Ragdolled = true;
        _anim?.Pause();
        var sim = new PhysicalBoneSimulator3D();
        _skel.AddChild(sim);
        var bodies = new List<PhysicalBone3D>();
        foreach (var p in Parts)
        {
            int bone = _skel.FindBone(p.Bone);
            if (bone < 0) continue;
            var pb = new PhysicalBone3D
            {
                Mass = p.Mass, JointType = p.Joint, Friction = .9f, Bounce = .05f,
                LinearDamp = .3f, AngularDamp = 2.5f, CollisionLayer = 1 << 2, CollisionMask = 1,
            };
            // Capsule from this bone toward the next one (rest rotations are identity: the offset is the direction).
            Vector3 dir; float len;
            if (p.Toward != null && _skel.FindBone(p.Toward) is int to and >= 0)
            {
                var off = RestGlobal(to).Origin - RestGlobal(bone).Origin;
                dir = off.Normalized(); len = off.Length();
            }
            else { dir = Vector3.Up; len = p.Radius * 2.2f; }
            var shape = new CollisionShape3D
            {
                Shape = new CapsuleShape3D { Radius = p.Radius, Height = Math.Max(len + p.Radius, p.Radius * 2.05f) },
                Transform = new Transform3D(new Basis(Arc(Vector3.Up, dir)), dir * len * .5f),
            };
            pb.Set("bone_name", p.Bone); // not exposed as a C# property
            pb.AddChild(shape);
            if (p.Joint == PhysicalBone3D.JointTypeEnum.Cone)
            {
                pb.Set("joint_constraints/swing_span", 50f);
                pb.Set("joint_constraints/twist_span", 25f);
            }
            else if (p.Joint == PhysicalBone3D.JointTypeEnum.Hinge)
            {
                // Hinge axis = the joint's Z; turn it onto the limb's sideways axis (X) so knees and elbows bend one way.
                pb.JointRotation = new Vector3(0, MathF.PI / 2, 0);
                pb.Set("joint_constraints/angular_limit_enabled", true);
                pb.Set("joint_constraints/angular_limit_upper", p.Bone.Contains("Leg") ? 5f : 140f);
                pb.Set("joint_constraints/angular_limit_lower", p.Bone.Contains("Leg") ? -140f : -5f);
            }
            sim.AddChild(pb);
            bodies.Add(pb);
        }
        sim.PhysicalBonesStartSimulation();
        DropGun(shotDirection);

        // Shove one physics frame later: a brand-new body's mass isn't set up in its first frame (see the tree rocket).
        GetTree().CreateTimer(.05, false, true).Timeout += () =>
        {
            PhysicalBone3D? nearest = null; float best = float.MaxValue;
            foreach (var b in bodies)
            {
                if (!IsInstanceValid(b)) continue;
                float d = b.GlobalPosition.DistanceSquaredTo(hitPoint);
                if (d < best) { best = d; nearest = b; }
            }
            var dirFlat = shotDirection.Normalized();
            nearest?.ApplyImpulse(dirFlat * 55 * push, hitPoint - nearest.GlobalPosition);
            foreach (var b in bodies) if (IsInstanceValid(b)) b.ApplyCentralImpulse(dirFlat * 4 * push);
        };
    }

    private void DropGun(Vector3 shotDirection)
    {
        if (_gun == null || !IsInstanceValid(_gun)) return;
        var xf = _gun.GlobalTransform;
        var body = new RigidBody3D { Mass = 3.5f, CollisionLayer = 1 << 2, CollisionMask = 1, ContinuousCd = true };
        body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(.07f, .2f, .95f) }, Position = new Vector3(0, 0, .12f) });
        GetTree().CurrentScene.AddChild(body);
        body.GlobalTransform = xf.Orthonormalized();
        _gun.GetParent().RemoveChild(_gun);
        body.AddChild(_gun);
        _gun.Transform = Transform3D.Identity;
        GetTree().CreateTimer(.05, false, true).Timeout += () =>
        {
            if (IsInstanceValid(body)) body.ApplyImpulse(shotDirection.Normalized() * 2 + Vector3.Up, new Vector3(0, 0, .4f));
        };
        GetTree().CreateTimer(15).Timeout += () => { if (IsInstanceValid(body)) body.Freeze = true; };
    }

    public override void _Process(double delta)
    {
        if (!Loaded || Ragdolled) return;
        float dt = (float)delta;
        if (_anim != null)
        {
            float crouchSlow = 1 - .45f * Crouch;
            if (Speed < .25f) { Play("Idle"); _anim.SpeedScale = 1; }
            else if (Speed < 2.8f) { Play("Walk"); _anim.SpeedScale = Math.Clamp(Speed / 1.45f, .5f, 2f) / crouchSlow; }
            else { Play("Run"); _anim.SpeedScale = Math.Clamp(Speed / 4.3f, .6f, 1.6f); }
            _anim.Advance(dt);
        }
        PoseCrouch();
        PoseGunAndArms();
    }

    // ================================================================= posing (skeleton space)

    private void PoseCrouch()
    {
        if (Crouch <= .001f) return;
        int hips = _bones["Hips"];
        // Where the animation put the feet, before the hips go down.
        var footL = Global(_bones["LeftFoot"]).Origin;
        var footR = Global(_bones["RightFoot"]).Origin;
        float legLen = Rest(_bones["LeftLowerLeg"]).Origin.Length() + Rest(_bones["LeftFoot"]).Origin.Length();
        var hipPos = _skel.GetBonePosePosition(hips);
        _skel.SetBonePosePosition(hips, hipPos + new Vector3(0, -legLen * .38f * Crouch, .04f * Crouch));
        // Lean the upper body forward a little.
        var spine = _bones["Spine"];
        _skel.SetBonePoseRotation(spine, new Quaternion(Vector3.Right, .25f * Crouch) * _skel.GetBonePoseRotation(spine));
        // Knees bend forward (+Z).
        SolveTwoBone(_bones["LeftUpperLeg"], _bones["LeftLowerLeg"], _bones["LeftFoot"], footL, new Vector3(0, 0, 1), 0);
        SolveTwoBone(_bones["RightUpperLeg"], _bones["RightLowerLeg"], _bones["RightFoot"], footR, new Vector3(0, 0, 1), 0);
    }

    private void PoseGunAndArms()
    {
        if (_gun == null) return;
        // The gun's butt at the right shoulder, a bit forward and in toward the chest.
        var shoulder = Global(_bones["RightUpperArm"]).Origin;
        var butt = shoulder + new Vector3(.06f, -.03f, .07f);
        float pitch = Mathf.Lerp(-.75f, Math.Clamp(AimPitch, -1.1f, 1.1f), Ready);
        float yaw = Mathf.Lerp(.55f, .03f, Ready);                 // lowered: muzzle across the body
        var basis = new Basis(Vector3.Up, yaw) * new Basis(Vector3.Right, -pitch);
        float k = 1 / _scale;                                       // gun keeps its real size
        var gunBasis = basis.Scaled(Vector3.One * k);
        _gun.Transform = new Transform3D(gunBasis, butt - gunBasis * _gunButt);

        var grip = _gun.Transform * _gunGrip;
        var support = _gun.Transform * _gunSupport;
        // Elbows: right one down and out, left one down and slightly out.
        SolveTwoBone(_bones["RightUpperArm"], _bones["RightLowerArm"], _bones["RightHand"], grip, new Vector3(-.6f, -1, -.2f), HandReach(_bones["RightHand"]));
        SolveTwoBone(_bones["LeftUpperArm"], _bones["LeftLowerArm"], _bones["LeftHand"], support, new Vector3(.5f, -1, 0), HandReach(_bones["LeftHand"]));
    }

    /// <summary>Hand bone to its grip socket (the hand is held "through" to the grip centre).</summary>
    private float HandReach(int hand)
    {
        foreach (var c in _skel.GetChildren())
            if (c is Node3D n && n.Name.ToString().StartsWith("Socket_") && n is BoneAttachment3D ba && ba.BoneIdx == hand) return n.Position.Length();
        return .085f * (Rest(_bones["RightLowerArm"]).Origin.Length() / .236f);
    }

    /// <summary>
    /// Two-bone IK in skeleton space: rotate upper and lower so that the end of `end` (plus `extra` along the same
    /// rest direction) reaches `target`, bending toward `pole`.
    /// </summary>
    private void SolveTwoBone(int upper, int lower, int end, Vector3 target, Vector3 pole, float extra)
    {
        var restLower = Rest(lower).Origin;       // upper -> lower offset in rest (rest rotations are identity)
        var restEnd = Rest(end).Origin;           // lower -> end offset
        float a = restLower.Length();
        var endDirRest = restEnd.Normalized();
        float b = restEnd.Length() + extra;

        var parentGlobal = Global(_skel.GetBoneParent(upper));
        var s = (parentGlobal * Rest(upper)).Origin;  // upper bone's position (its own rotation doesn't move it)
        var toT = target - s;
        float d = Math.Clamp(toT.Length(), Math.Abs(a - b) + 1e-3f, a + b - 1e-3f);
        var dir = toT.Normalized();
        float cosA = Math.Clamp((a * a + d * d - b * b) / (2 * a * d), -1, 1);
        var bend = (pole - dir * pole.Dot(dir));
        bend = bend.LengthSquared() < 1e-6f ? Vector3.Up : bend.Normalized();
        var elbow = s + dir * (a * cosA) + bend * (a * MathF.Sqrt(1 - cosA * cosA));
        var hand = s + dir * d;

        var upperGlobal = Arc(restLower.Normalized(), (elbow - s).Normalized());
        var lowerGlobal = Arc(upperGlobal * endDirRest, (hand - elbow).Normalized()) * upperGlobal;
        _skel.SetBonePoseRotation(upper, (parentGlobal.Basis.GetRotationQuaternion().Inverse() * upperGlobal).Normalized());
        _skel.SetBonePoseRotation(lower, (upperGlobal.Inverse() * lowerGlobal).Normalized());
    }

    /// <summary>Shortest rotation taking unit vector a to unit vector b.</summary>
    private static Quaternion Arc(Vector3 a, Vector3 b)
    {
        float dot = a.Dot(b);
        if (dot > .99999f) return Quaternion.Identity;
        if (dot < -.99999f)
        {
            var axis = a.Cross(Vector3.Up);
            if (axis.LengthSquared() < 1e-6f) axis = a.Cross(Vector3.Right);
            return new Quaternion(axis.Normalized(), MathF.PI);
        }
        var c = a.Cross(b);
        return new Quaternion(c.X, c.Y, c.Z, 1 + dot).Normalized();
    }

    private Transform3D Rest(int bone) => _skel.GetBoneRest(bone);

    private Transform3D RestGlobal(int bone)
    {
        int p = _skel.GetBoneParent(bone);
        return p < 0 ? Rest(bone) : RestGlobal(p) * Rest(bone);
    }

    /// <summary>A bone's current transform in skeleton space, from the local poses (no dependence on update order).</summary>
    private Transform3D Global(int bone)
    {
        if (bone < 0) return Transform3D.Identity;
        var t = new Transform3D(new Basis(_skel.GetBonePoseRotation(bone)), _skel.GetBonePosePosition(bone));
        int p = _skel.GetBoneParent(bone);
        return p < 0 ? t : Global(p) * t;
    }

    private static Vector3 SocketIn(Node3D root, string name, Vector3 fallback)
    {
        if (root.FindChild(name, true, false) is not Node3D n) return fallback;
        var xf = Transform3D.Identity;
        for (Node? x = n; x != null && x != root; x = x.GetParent()) if (x is Node3D x3) xf = x3.Transform * xf;
        return xf.Origin;
    }

    private static T? Find<T>(Node root) where T : Node
    {
        if (root is T t) return t;
        foreach (var c in root.GetChildren()) if (Find<T>(c) is { } f) return f;
        return null;
    }

    private static IEnumerable<T> FindAll<T>(Node root) where T : Node
    {
        if (root is T t) yield return t;
        foreach (var c in root.GetChildren()) foreach (var x in FindAll<T>(c)) yield return x;
    }

    /// <summary>All character files in a folder (works in the editor and in exported builds).</summary>
    public static List<string> Catalogue(string? dir = null) // res://assets/soldiers/<style>/*.glb
    {
        // The chosen style's folder; any other style folder if that one is empty.
        if (dir == null)
        {
            const string Root = "res://assets/soldiers";
            var chosen = Catalogue($"{Root}/{DeadRecon.UI.Settings.SoldierStyle}");
            if (chosen.Count > 0) return chosen;
            if (DirAccess.DirExistsAbsolute(Root))
                foreach (var sub in DirAccess.GetDirectoriesAt(Root)) { var any = Catalogue($"{Root}/{sub}"); if (any.Count > 0) return any; }
            return Catalogue($"{Root}/.");
        }
        var list = new List<string>();
        if (!DirAccess.DirExistsAbsolute(dir)) return list;
        foreach (var f in DirAccess.GetFilesAt(dir))
        {
            var name = f.Replace(".import", "").Replace(".remap", "");
            if (name.EndsWith(".glb") && !list.Contains($"{dir}/{name}")) list.Add($"{dir}/{name}");
        }
        list.Sort();
        return list;
    }
}
