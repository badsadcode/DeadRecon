using System.Collections.Generic;

namespace DeadRecon.Gear;

public enum ItemKind { Magazine, Medical, Attachment, Intel, Cargo }

/// <summary>Which rail or socket an attachment goes on.</summary>
public enum Slot { None, Optic, Muzzle, Side, Underbarrel }

/// <summary>
/// One kind of thing you can carry. Size is in backpack cells (w x h, before rotating); attachments name the BadGun
/// part they come from (a file in res://assets/weapons/attachments, or a part of the rifle itself for "stock" ones).
/// </summary>
public sealed record ItemDef(
    string Id, string Name, string Description, ItemKind Kind, int W, int H, float Kg, int Stack = 1,
    int Capacity = 0, float Heal = 0, Slot Slot = Slot.None, string Model = "")
{
    /// <summary>Every item: the built-in ones below, then content/game/items.json and mods ("items" section: by id).</summary>
    public static Dictionary<string, ItemDef> All => _all ??= Load();
    private static Dictionary<string, ItemDef>? _all;

    private static Dictionary<string, ItemDef> Load()
    {
        var d = new Dictionary<string, ItemDef>();
        foreach (var i in DeadRecon.Data.Content.Get("items", Builtin)) d[i.Id] = i;
        foreach (var i in Builtin) d.TryAdd(i.Id, i);   // code still refers to these by id: never lose them
        return d;
    }

    private static readonly ItemDef[] Builtin =
    {
        new("mag20", "20-round magazine", "Steel box, twenty rounds, one bad day.", ItemKind.Magazine, 1, 2, .45f, Capacity: 20),
        new("mag30", "30-round magazine", "Longer and heavier. Fewer reloads, more swearing when it snags on a vine.", ItemKind.Magazine, 1, 2, .6f, Capacity: 30),
        new("bandage", "Field dressing", "Press firmly. Think of something nice. Heals 35 over a few seconds (H).", ItemKind.Medical, 1, 1, .1f, Stack: 5, Heal: 35),
        new("rice", "Sticky rice parcel", "Wrapped in a banana leaf by somebody's grandmother. Heals 15, and morale.", ItemKind.Medical, 1, 1, .2f, Stack: 4, Heal: 15),
        new("optic_stock", "6x field scope", "The rifle's own scope. Long, heavy, honest. Six times closer.", ItemKind.Attachment, 3, 1, .6f, Slot: Slot.Optic, Model: "rifle:Optic"),
        new("optic_scope", "4x combat scope", "Shorter and wider than the factory glass: easier to find a target, a bit less zoom.", ItemKind.Attachment, 3, 1, .5f, Slot: Slot.Optic, Model: "BadGun_optic_scope_vietnam_97232"),
        new("optic_reddot", "Red dot sight", "No zoom. Fast. Put the dot on it.", ItemKind.Attachment, 2, 1, .2f, Slot: Slot.Optic, Model: "BadGun_optic_reddot_vietnam_97232"),
        new("optic_holo", "Holographic sight", "A ring and a dot floating in a window. A little zoom, a lot of style.", ItemKind.Attachment, 2, 1, .3f, Slot: Slot.Optic, Model: "BadGun_optic_holo_vietnam_97232"),
        new("muzzle_stock", "Flash hider", "Factory fit. Hides a bit of the flash from you, none of the noise from them.", ItemKind.Attachment, 1, 1, .1f, Slot: Slot.Muzzle, Model: "rifle:Muzzle"),
        new("muzzle_suppressor", "Suppressor", "Not silent, whatever the films say. Guards hear it about a third as far, and see no flash.", ItemKind.Attachment, 2, 1, .5f, Slot: Slot.Muzzle, Model: "BadGun_muzzle_suppressor_vietnam_97232"),
        new("muzzle_compensator", "Compensator", "Throws the blast sideways and keeps the muzzle down. Less kick, more noise.", ItemKind.Attachment, 1, 1, .15f, Slot: Slot.Muzzle, Model: "BadGun_muzzle_compensator_vietnam_97232"),
        new("side_flashlight", "Weapon light", "Turns night into day, a cone at a time (L). Guards can see it too, from far away.", ItemKind.Attachment, 2, 1, .2f, Slot: Slot.Side, Model: "BadGun_side_flashlight_vietnam_97232"),
        new("side_laser", "Laser pointer", "A red line from you to them (L). Steadier shots from the hip.", ItemKind.Attachment, 1, 1, .1f, Slot: Slot.Side, Model: "BadGun_side_laser_vietnam_97232"),
        new("grip_vgrip", "Vertical foregrip", "Something to hold on to. Less recoil, steadier aim.", ItemKind.Attachment, 1, 1, .15f, Slot: Slot.Underbarrel, Model: "BadGun_underbarrel_vgrip_vietnam_97232"),
        new("rice_sacks", "Sack of rice", "Twenty kilos of somebody's harvest, for somebody else's dinner. Heavy, lumpy, important.", ItemKind.Cargo, 2, 2, 20f),
        new("field_radio", "Field radio", "Heavy, olive, crackly. Set it up somewhere quiet and that's home: operations, news and gossip come through it. (Right-click: set up camp here.)", ItemKind.Cargo, 2, 2, 6f),
        new("supplies", "Building supplies", "Planks, sandbags, tin sheet, rope, nails. Whatever wasn't nailed down at their camp. Build with it at the radio.", ItemKind.Cargo, 1, 1, 1.5f, Stack: 10),
        new("intel", "Intel", "What you came for. Don't lose it, don't read it, don't spill rice on it.", ItemKind.Intel, 2, 2, .4f),
    };

    public static ItemDef Mag20 => All["mag20"];
    public static ItemDef Mag30 => All["mag30"];
    public static ItemDef Bandage => All["bandage"];
    public static ItemDef RiceParcel => All["rice"];
    public static ItemDef OpticStock => All["optic_stock"];
    public static ItemDef OpticScope => All["optic_scope"];
    public static ItemDef OpticRedDot => All["optic_reddot"];
    public static ItemDef OpticHolo => All["optic_holo"];
    public static ItemDef MuzzleStock => All["muzzle_stock"];
    public static ItemDef Suppressor => All["muzzle_suppressor"];
    public static ItemDef Compensator => All["muzzle_compensator"];
    public static ItemDef Flashlight => All["side_flashlight"];
    public static ItemDef Laser => All["side_laser"];
    public static ItemDef Foregrip => All["grip_vgrip"];
    public static ItemDef RiceSacks => All["rice_sacks"];
    public static ItemDef FieldRadio => All["field_radio"];
    public static ItemDef Supplies => All["supplies"];
    public static ItemDef Intel => All["intel"];





}

/// <summary>One thing in a bag: where it sits, which way round, how many, and (magazines) how many rounds.</summary>
public sealed class Item
{
    public ItemDef Def;
    public int Count = 1;
    public int Rounds;
    public int X, Y;
    public bool Rotated;
    /// <summary>A name that overrides the definition's (the intel: "the codebook").</summary>
    public string? Label;

    public Item(ItemDef def, int count = 1) { Def = def; Count = count; Rounds = def.Capacity; }
    public int W => Rotated ? Def.H : Def.W;
    public int H => Rotated ? Def.W : Def.H;
    public string Name => Label ?? Def.Name;
    public float Kg => Def.Kg * Count;
}

/// <summary>What's on the rifle. Paint is cosmetic.</summary>
public sealed record Loadout(string Optic = "optic_stock", string Muzzle = "muzzle_stock", string? Side = null, string? Underbarrel = null, string Paint = "factory", string Weapon = WeaponDef.Default)
{
    public string? In(Slot s) => s switch { Slot.Optic => Optic, Slot.Muzzle => Muzzle, Slot.Side => Side, Slot.Underbarrel => Underbarrel, _ => null };
    public Loadout With(Slot s, string? id) => s switch
    {
        Slot.Optic => this with { Optic = id ?? "optic_stock" }, Slot.Muzzle => this with { Muzzle = id ?? "muzzle_stock" },
        Slot.Side => this with { Side = id }, Slot.Underbarrel => this with { Underbarrel = id }, _ => this,
    };

    public static readonly (string Id, string Name, Godot.Color? Colour)[] Paints =
    {
        ("factory", "Factory olive", null), ("black", "Night black", new Godot.Color(.035f, .035f, .04f)),
        ("tan", "Desert tan", new Godot.Color(.36f, .29f, .19f)), ("jungle", "Jungle green", new Godot.Color(.11f, .15f, .07f)),
        ("wood", "Wood and steel", new Godot.Color(.30f, .15f, .07f)),
    };
}
