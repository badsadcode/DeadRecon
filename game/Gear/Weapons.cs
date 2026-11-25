using System.Collections.Generic;
using System.Linq;

namespace DeadRecon.Gear;

/// <summary>
/// A gun: its BadGun model and how it handles. Data: the "weapons" section (content/game/weapons.json, mods add more
/// by id). Generate a model with BadGun (tools/weapons/README.md), drop it in assets/weapons/guns and add an entry.
/// </summary>
/// <param name="Sight">"scope" (the model's own scope: zoom FOV <paramref name="ScopeFov"/>, gun hides) or "irons".</param>
/// <param name="Rpm">Rounds per minute. Below ~120 it's a bolt or pump: one shot per click whatever the mode.</param>
/// <param name="Pellets">More than 1: a shotgun (each pellet does <paramref name="Damage"/>, spread <paramref name="PelletSpread"/>).</param>
/// <param name="Noise">Multiplies how far guards hear it (1 = rifle, 260 m).</param>
/// <param name="GuardWeight">How often guards carry it (0 = never).</param>
public sealed record WeaponDef(
    string Id, string Name, string Description, string Model,
    float Rpm = 600, float Damage = 34, int Capacity = 20, bool Auto = true,
    float HipSpread = .014f, float AimSpread = .0006f, float Recoil = 1, float Noise = 1,
    string Sight = "scope", float ScopeFov = 11, int Pellets = 1, float PelletSpread = .03f, float Kg = 4.2f,
    float GuardWeight = 1, float GuardDamage = 22)
{
    public const string Default = "rifle_97232";

    private static readonly WeaponDef[] Builtin =
    {
        new("rifle_97232", "Scoped battle rifle", "Your old friend: a heavy rifle with a 6x scope. Accurate, loud, reliable.",
            "res://assets/weapons/BadGun_rifle_vietnam_97232.glb", 600, 34, 20, GuardWeight: .6f),
        new("rifle_4121", "Black rifle", "Light, quick and plasticky. Thirty rounds, iron sights, a bayonet you'll never use.",
            "res://assets/weapons/guns/BadGun_rifle_vietnam_4121.glb", 720, 30, 30, Sight: "irons", Recoil: .85f, AimSpread: .0011f, Kg: 3.4f, GuardWeight: 1.4f),
        new("smg_3310", "Wire-stock SMG", "Spits pistol rounds very fast. Brilliant up close, hopeless past fifty metres.",
            "res://assets/weapons/guns/BadGun_smg_vietnam_3310.glb", 880, 20, 32, Sight: "irons", HipSpread: .009f, AimSpread: .0028f, Recoil: .55f, Noise: .8f, Kg: 3f, GuardWeight: 1, GuardDamage: 16),
        new("shotgun_5507", "Pump shotgun", "Eight pellets of very strong opinion. Six shells. Pump between shots.",
            "res://assets/weapons/guns/BadGun_shotgun_vietnam_5507.glb", 70, 16, 6, false, HipSpread: .006f, AimSpread: .002f, Recoil: 2.2f, Sight: "irons", Pellets: 8, PelletSpread: .035f, Kg: 3.6f, GuardWeight: .5f, GuardDamage: 30),
        new("sniper_6633", "Suppressed marksman rifle", "Bolt action, built-in suppressor, 9x scope. One shot, one very surprised man.",
            "res://assets/weapons/guns/BadGun_sniper_vietnam_6633.glb", 50, 95, 5, false, HipSpread: .03f, AimSpread: .00015f, Recoil: 2f, Noise: .35f, ScopeFov: 7.5f, Kg: 5.4f, GuardWeight: .15f, GuardDamage: 40),
        new("lmg_7741", "Light machine gun", "A hundred rounds in a box and a bipod you can't use standing. Heavy. Very, very persuasive.",
            "res://assets/weapons/guns/BadGun_lmg_vietnam_7741.glb", 650, 34, 100, HipSpread: .02f, AimSpread: .0016f, Recoil: 1.25f, Noise: 1.15f, Sight: "irons", Kg: 9f, GuardWeight: .25f),
        new("dmr_8812", "Wooden marksman rifle", "Semi-automatic, wood and steel, a 4x scope. For people who count their shots.",
            "res://assets/weapons/guns/BadGun_rifle_vietnam_8812.glb", 320, 52, 20, false, AimSpread: .0003f, Recoil: 1.3f, ScopeFov: 15, Kg: 4.6f, GuardWeight: .3f),
    };

    private static Dictionary<string, WeaponDef>? _all;
    public static Dictionary<string, WeaponDef> All => _all ??= Load();

    private static Dictionary<string, WeaponDef> Load()
    {
        var d = new Dictionary<string, WeaponDef>();
        foreach (var w in DeadRecon.Data.Content.Get("weapons", Builtin)) d[w.Id] = w;
        d.TryAdd(Default, Builtin[0]);
        return d;
    }

    public static WeaponDef Get(string? id) => id != null && All.TryGetValue(id, out var w) ? w : All[Default];

    [System.Text.Json.Serialization.JsonIgnore] public bool Bolt => Rpm < 120;

    /// <summary>A gun for a guard, weighted (only ones whose model exists).</summary>
    public static WeaponDef ForGuard(Godot.RandomNumberGenerator rng)
    {
        var pool = All.Values.Where(w => w.GuardWeight > 0 && Godot.ResourceLoader.Exists(w.Model)).ToList();
        if (pool.Count == 0) return Get(Default);
        float total = pool.Sum(w => w.GuardWeight), r = rng.Randf() * total;
        foreach (var w in pool) { r -= w.GuardWeight; if (r <= 0) return w; }
        return pool[^1];
    }
}
