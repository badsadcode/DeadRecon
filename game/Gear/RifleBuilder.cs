using System;
using System.Collections.Generic;
using Godot;

namespace DeadRecon.Gear;

/// <summary>
/// Puts a rifle together from Tom's BadGun rifle plus BadGun attachments (res://assets/weapons/attachments), the same
/// way for the first-person gun, the gunsmith's preview and the HUD/backpack icons.
/// <para>BadGun attachments are re-centred on their mount point and keep +Y up, +Z forward, so fitting one is a plain
/// move to the rifle's socket. This rifle predates attachments: it has SOCKET_Optic and a Muzzle part, and the
/// underbarrel and side-rail mounts are worked out from its support-hand and barrel positions.</para>
/// </summary>
public static class RifleBuilder
{
    public const string RifleScene = "res://assets/weapons/BadGun_rifle_vietnam_97232.glb";
    private const string AttachmentDir = "res://assets/weapons/attachments/";

    public sealed class Rifle
    {
        public Node3D Model = null!;
        /// <summary>Muzzle tip (flash, smoke, where bullets start when not scoped).</summary>
        public Node3D? Muzzle;
        /// <summary>Eye position when aiming, in model space.</summary>
        public Vector3 Ads;
        public Node3D? Trigger, Magazine, Charging, Eject;
        public SpotLight3D? Light;
        /// <summary>Where the laser starts (model child) when a laser is fitted.</summary>
        public Node3D? Laser;
    }

    /// <summary>What each optic does when you aim.</summary>
    public sealed record OpticStats(float Fov, int Reticle, bool HideGun);   // Reticle: 0 scope, 1 red dot, 2 holo
    public static OpticStats Optic(string id) => id switch
    {
        "optic_scope" => new(17, 0, true),
        "optic_reddot" => new(50, 1, false),
        "optic_holo" => new(44, 2, false),
        _ => new(11, 0, true),
    };

    /// <summary>Recoil, noise (gunshot hearing radius) and flash, multipliers.</summary>
    public static (float Recoil, float Noise, float Flash, float Sway) Handling(Loadout l)
    {
        float recoil = 1, noise = 1, flash = 1, sway = 1;
        if (l.Muzzle == "muzzle_suppressor") { recoil = .9f; noise = .3f; flash = 0; }
        if (l.Muzzle == "muzzle_compensator") { recoil = .65f; noise = 1.15f; flash = 1.3f; }
        if (l.Underbarrel == "grip_vgrip") { recoil *= .85f; sway *= .85f; }
        if (l.Optic == "optic_stock") sway *= 1.05f;
        return (recoil, noise, flash, sway);
    }

    public static Rifle? Build(Loadout l, bool lights = true)
    {
        if (!ResourceLoader.Exists(RifleScene)) return null;
        var r = new Rifle { Model = ResourceLoader.Load<PackedScene>(RifleScene).Instantiate<Node3D>() };
        var m = r.Model;
        r.Trigger = m.FindChild("Trigger", true, false) as Node3D;
        r.Magazine = m.FindChild("Magazine", true, false) as Node3D;
        r.Charging = m.FindChild("ChargingHandle", true, false) as Node3D;
        r.Eject = m.FindChild("SOCKET_Eject", true, false) as Node3D;
        r.Muzzle = m.FindChild("SOCKET_Muzzle", true, false) as Node3D;
        r.Ads = m.FindChild("SOCKET_ADS", true, false) is Node3D ads ? InModel(m, ads).Origin : new Vector3(0, .06f, -.17f);

        var opticSocket = m.FindChild("SOCKET_Optic", true, false) is Node3D os ? InModel(m, os).Origin : new Vector3(0, .03f, .07f);
        var muzzlePart = m.FindChild("Muzzle", true, false) as Node3D;
        var barrelEnd = muzzlePart != null ? InModel(m, muzzlePart).Origin : new Vector3(0, 0, .36f);
        var support = m.FindChild("SOCKET_Hand_Support", true, false) is Node3D hs ? InModel(m, hs).Origin : new Vector3(0, -.04f, .14f);
        // No underbarrel/side sockets on this rifle: clamp to the handguard's measured skin instead.
        Vector3 under, side;
        if (m.FindChild("Handguard", true, false) is Node3D hg)
        {
            var box = InModel(m, hg) * Bounds(hg);
            var c = box.GetCenter();
            under = new Vector3(0, box.Position.Y, c.Z - box.Size.Z * .12f);
            side = new Vector3(box.Position.X, c.Y, c.Z + box.Size.Z * .1f);
        }
        else { under = new Vector3(0, support.Y + .012f, support.Z + .05f); side = new Vector3(-.028f, 0, (support.Z + barrelEnd.Z) / 2); }
        if (m.FindChild("SOCKET_Underbarrel", true, false) is Node3D ub) under = InModel(m, ub).Origin;
        if (m.FindChild("SOCKET_Side", true, false) is Node3D sd) side = InModel(m, sd).Origin;

        // Optic: the factory scope is part of the rifle; anything else replaces it.
        if (l.Optic != "optic_stock" && Attach(m, l.Optic, opticSocket) is { } optic)
        {
            if (m.FindChild("Optic", true, false) is Node3D stock) stock.Visible = false;
            if (optic.FindChild("SOCKET_ADS", true, false) is Node3D oa) r.Ads = opticSocket + InModel(optic, oa).Origin;
            // Red dots and holos: eye further back than BadGun's socket, so the sight is a window, not the whole view.
            if (Optic(l.Optic).Reticle != 0) r.Ads += new Vector3(0, 0, -.09f);
        }
        // Muzzle device.
        if (l.Muzzle != "muzzle_stock" && Attach(m, l.Muzzle, barrelEnd) is { } muzzle)
        {
            if (muzzlePart != null) muzzlePart.Visible = false;
            if (muzzle.FindChild("SOCKET_Muzzle", true, false) is Node3D tip) r.Muzzle = tip;
        }
        if (l.Underbarrel != null) Attach(m, l.Underbarrel, under);
        if (l.Side != null && Attach(m, l.Side, side) is { } sideNode && lights)
        {
            // The front of the attachment: the light or laser comes out there.
            var box = Bounds(sideNode);
            var front = new Vector3(box.GetCenter().X, box.GetCenter().Y, box.End.Z);
            if (l.Side == "side_flashlight")
            {
                r.Light = new SpotLight3D
                {
                    Position = front, LightColor = new Color(1f, .95f, .85f), LightEnergy = 7, SpotRange = 55, SpotAngle = 17,
                    SpotAngleAttenuation = .6f, ShadowEnabled = true, Visible = false, LightVolumetricFogEnergy = 1.5f,
                };
                r.Light.RotationDegrees = new Vector3(0, 180, 0);   // spot lights shine down -Z; the gun faces +Z
                sideNode.AddChild(r.Light);
            }
            else
            {
                r.Laser = new Node3D { Position = front, RotationDegrees = new Vector3(0, 180, 0) };
                sideNode.AddChild(r.Laser);
            }
        }
        // A long magazine sticks out further.
        Paint(m, l.Paint);
        return r;
    }

    /// <summary>Instantiate an attachment and put its mount on a model-space point.</summary>
    private static Node3D? Attach(Node3D model, string id, Vector3 at)
    {
        if (!ItemDef.All.TryGetValue(id, out var def) || def.Model.StartsWith("rifle:")) return null;
        string path = AttachmentDir + def.Model + ".glb";
        if (!ResourceLoader.Exists(path)) { GD.PushWarning($"No attachment model at {path}"); return null; }
        var node = ResourceLoader.Load<PackedScene>(path).Instantiate<Node3D>();
        node.Position = at;
        model.AddChild(node);
        return node;
    }

    /// <summary>Paint: tints the body, stock, grip and magazine (each instance gets its own materials).</summary>
    public static void Paint(Node3D model, string paint)
    {
        Color? colour = null;
        foreach (var p in Loadout.Paints) if (p.Id == paint) colour = p.Colour;
        if (colour is not { } c) return;
        foreach (var mi in FindAll<MeshInstance3D>(model))
        {
            if (mi.Mesh == null) continue;
            for (int s = 0; s < mi.Mesh.GetSurfaceCount(); s++)
            {
                if (mi.Mesh.SurfaceGetMaterial(s) is not StandardMaterial3D mat) continue;
                string name = mat.ResourceName;
                bool wood = paint == "wood";
                bool paintIt = wood ? name is "MAT_Stock" or "MAT_Grip" : name is "MAT_Body" or "MAT_Stock" or "MAT_Grip" or "MAT_Magazine";
                if (!paintIt) continue;
                var copy = (StandardMaterial3D)mat.Duplicate();
                copy.AlbedoColor = new Color(c, mat.AlbedoColor.A);
                if (wood) copy.Roughness = .7f;
                mi.SetSurfaceOverrideMaterial(s, copy);
            }
        }
    }

    /// <summary>A single part as its own model (icons for the factory scope, flash hider and magazine).</summary>
    public static Node3D? Part(string name)
    {
        if (!ResourceLoader.Exists(RifleScene)) return null;
        var m = ResourceLoader.Load<PackedScene>(RifleScene).Instantiate<Node3D>();
        if (m.FindChild(name, true, false) is not Node3D part) { m.Free(); return null; }
        // Lift the part out of the rifle (its parent part would come along otherwise), keep where it sat.
        var xf = InModel(m, part);
        part.GetParent().RemoveChild(part);
        m.Free();
        var holder = new Node3D();
        holder.AddChild(part);
        part.Transform = xf;
        return holder;
    }

    /// <summary>The model for an item (null for items drawn as flat icons).</summary>
    public static Node3D? ModelFor(ItemDef def)
    {
        if (def.Kind == ItemKind.Magazine) return Part("Magazine");
        if (def.Model.StartsWith("rifle:")) return Part(def.Model[6..]);
        if (def.Model == "") return null;
        string path = AttachmentDir + def.Model + ".glb";
        return ResourceLoader.Exists(path) ? ResourceLoader.Load<PackedScene>(path).Instantiate<Node3D>() : null;
    }

    private static bool IsAncestor(Node a, Node of) { for (var n = of.GetParent(); n != null; n = n.GetParent()) if (n == a) return true; return false; }

    public static Transform3D InModel(Node3D model, Node3D node)
    {
        var xf = Transform3D.Identity;
        for (Node? n = node; n != null && n != model; n = n.GetParent())
            if (n is Node3D n3) xf = n3.Transform * xf;
        return xf;
    }

    /// <summary>Bounding box of the visible meshes, in <paramref name="root"/>'s space.</summary>
    public static Aabb Bounds(Node3D root)
    {
        Aabb? box = null;
        foreach (var mi in FindAll<MeshInstance3D>(root))
        {
            if (mi.Mesh == null || !VisibleIn(mi, root)) continue;
            var b = InModel(root, mi) * mi.Mesh.GetAabb();
            box = box is { } x ? x.Merge(b) : b;
        }
        return box ?? new Aabb(Vector3.Zero, Vector3.One * .1f);
    }

    private static bool VisibleIn(Node3D n, Node3D root)
    {
        for (Node? p = n; p != null && p != root; p = p.GetParent()) if (p is Node3D p3 && !p3.Visible) return false;
        return true;
    }

    public static IEnumerable<T> FindAll<T>(Node root) where T : Node
    {
        if (root is T t) yield return t;
        foreach (var c in root.GetChildren()) foreach (var x in FindAll<T>(c)) yield return x;
    }
}
