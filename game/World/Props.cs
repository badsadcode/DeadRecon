using Godot;

namespace DeadRecon.World;

/// <summary>
/// Tom's prop models (assets/*.glb), placed at real-world size. The models are made at whatever size they were made
/// at; the scale here brings them to life size (table: 74 cm high; radio: about 45 cm across). If a model is missing,
/// the caller's box stand-in is used instead.
/// </summary>
public static class Props
{
    public const string Table = "res://assets/foldable_table.glb", Radio = "res://assets/ham_radio.glb";
    public const float TableScale = .62f, RadioScale = .4f;
    /// <summary>Height of the table top (after scaling).</summary>
    public const float TableTop = .745f;

    public static bool Has(string path) => ResourceLoader.Exists(path);

    /// <summary>The table, with a box of collision round it (bullets stop, people bump). Faces +Z.</summary>
    public static Node3D? MakeTable()
    {
        if (!Has(Table)) return null;
        var body = new StaticBody3D();
        var model = ResourceLoader.Load<PackedScene>(Table).Instantiate<Node3D>();
        model.Scale = Vector3.One * TableScale;
        body.AddChild(model);
        body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(2 * TableScale, TableTop, 1 * TableScale) }, Position = new Vector3(0, TableTop / 2, 0) });
        return body;
    }

    /// <summary>The radio, bottom at its origin, its front panel facing +Z.</summary>
    public static Node3D? MakeRadio()
    {
        if (!Has(Radio)) return null;
        var model = ResourceLoader.Load<PackedScene>(Radio).Instantiate<Node3D>();
        model.Scale = Vector3.One * RadioScale;
        foreach (var mi in DeadRecon.Gear.RifleBuilder.FindAll<GeometryInstance3D>(model)) mi.CastShadow = GeometryInstance3D.ShadowCastingSetting.On;
        return model;
    }
}
