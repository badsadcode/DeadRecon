using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using DeadRecon.AI;
using DeadRecon.Player;
using DeadRecon.UI;
using Godot;

namespace DeadRecon.Gear;

/// <summary>
/// Everything you carry: a vest (quick access: reloads and bandages come from here first) and a backpack, both
/// grids of cells; the magazine in the rifle; what's fitted to the rifle (<see cref="Loadout"/>). Also: bandaging
/// (H), searching bodies (F), and keeping it all between operations (user://kit.json).
/// </summary>
public partial class Kit : Node
{
    public static Kit? Current { get; private set; }
    /// <summary>False in scripted tests: they start from the standard kit and save nothing.</summary>
    public static bool Persist { get; set; } = true;

    public Walker Player = null!;

    public sealed class Grid
    {
        public readonly string Name;
        public readonly int W, H;
        public readonly List<Item> Items = new();
        public Grid(string name, int w, int h) { Name = name; W = w; H = h; }

        public bool Fits(int x, int y, int w, int h, Item? ignore = null)
        {
            if (x < 0 || y < 0 || x + w > W || y + h > H) return false;
            foreach (var o in Items)
                if (o != ignore && x < o.X + o.W && o.X < x + w && y < o.Y + o.H && o.Y < y + h) return false;
            return true;
        }

        public Item? At(int x, int y) => Items.FirstOrDefault(o => x >= o.X && x < o.X + o.W && y >= o.Y && y < o.Y + o.H);

        /// <summary>Put it in the first place it fits (either way round).</summary>
        public bool Place(Item item)
        {
            foreach (bool rot in new[] { item.Rotated, !item.Rotated })
            {
                int w = rot ? item.Def.H : item.Def.W, h = rot ? item.Def.W : item.Def.H;
                for (int y = 0; y < H; y++)
                    for (int x = 0; x < W; x++)
                        if (Fits(x, y, w, h)) { item.X = x; item.Y = y; item.Rotated = rot; Items.Add(item); return true; }
            }
            return false;
        }
    }

    public readonly Grid Vest = new("Vest", 6, 2), Pack = new("Backpack", 8, 6);
    public Loadout Loadout { get; private set; } = new();
    /// <summary>Rounds in the rifle's magazine, and what kind of magazine that is.</summary>
    public int Loaded { get; private set; } = 20;
    public ItemDef LoadedMag { get; private set; } = ItemDef.Mag20;
    public bool LightOn { get; set; }

    public event Action? Changed, LoadoutChanged;

    private float _healLeft;
    private readonly HashSet<Guard> _searched = new();

    /// <summary>Places to search once (crates at a camp or a checkpoint): where, the prompt, what you get (returns a message).</summary>
    private static readonly List<(Vector3 At, string Prompt, Func<Kit, string> Give)> Loot = new();
    public static void AddLoot(Vector3 at, string prompt, Func<Kit, string> give, string? key = null)
    {
        if (key != null && (Looted.Contains(key) || LootKeys.Contains(key))) return;   // already there, or already taken
        if (key != null) LootKeys.Add(key);
        Loot.Add((at, prompt, kit => { if (key != null) Looted.Add(key); return give(kit); }));
    }
    private static readonly HashSet<string> LootKeys = new(), Looted = new();
    public static void ClearLoot() { Loot.Clear(); LootKeys.Clear(); }
    public static int LootCount => Loot.Count;
    private bool _fWas, _hWas, _lWas;

    public IEnumerable<Item> AllItems => Vest.Items.Concat(Pack.Items);
    public int Magazines => AllItems.Count(i => i.Def.Kind == ItemKind.Magazine && i.Rounds > 0);
    public int Medical => AllItems.Where(i => i.Def.Kind == ItemKind.Medical).Sum(i => i.Count);
    public int Bandages => AllItems.Where(i => i.Def == ItemDef.Bandage).Sum(i => i.Count);
    public float Kg => AllItems.Sum(i => i.Kg) + Weapon.Kg;
    public bool Healing => _healLeft > 0;

    public override void _Ready()
    {
        Current = this;
        if (!Load()) Standard();
    }

    public override void _ExitTree() { if (Current == this) Current = null; }

    /// <summary>The kit you start with: rifle with its factory scope, six magazines, dressings, a few toys.</summary>
    public void Standard()
    {
        Vest.Items.Clear(); Pack.Items.Clear();
        Loadout = new Loadout();
        Loaded = 20; LoadedMag = ItemDef.Mag20;
        for (int k = 0; k < 4; k++) Vest.Place(new Item(ItemDef.Mag20));
        Vest.Place(new Item(ItemDef.Bandage, 3));
        for (int k = 0; k < 2; k++) Pack.Place(new Item(ItemDef.Mag20));
        Pack.Place(new Item(ItemDef.OpticRedDot));
        Pack.Place(new Item(ItemDef.Suppressor));
        Pack.Place(new Item(ItemDef.Flashlight));
        Pack.Place(new Item(ItemDef.RiceParcel));
        Changed?.Invoke(); LoadoutChanged?.Invoke();
    }

    // ================================================================= adding and removing

    /// <summary>Into the bags: tops up stacks first, then a free spot (vest first for magazines and dressings).
    /// Returns false if there's no room.</summary>
    public bool Add(ItemDef def, int count = 1, int rounds = -1, bool quiet = false)
    {
        for (int n = 0; n < count; n++)
        {
            var stack = def.Stack > 1 ? AllItems.FirstOrDefault(i => i.Def == def && i.Count < def.Stack) : null;
            if (stack != null) { stack.Count++; continue; }
            var item = new Item(def) { Rounds = rounds >= 0 ? rounds : def.Capacity };
            bool quick = def.Kind is ItemKind.Magazine or ItemKind.Medical;
            if (!(quick && Vest.Place(item)) && !Pack.Place(item))
            {
                if (!quiet) Hud.Toast("No room in your backpack");
                Changed?.Invoke(); Save();
                return false;
            }
        }
        Changed?.Invoke(); Save();
        return true;
    }

    public void Remove(Item item)
    {
        Vest.Items.Remove(item); Pack.Items.Remove(item);
        Changed?.Invoke(); Save();
    }

    public Grid? GridOf(Item item) => Vest.Items.Contains(item) ? Vest : Pack.Items.Contains(item) ? Pack : null;

    /// <summary>Move an item (dragged in the backpack screen). Dropped on the same kind of item, stacks merge.</summary>
    public bool Move(Item item, Grid to, int x, int y, bool rotated)
    {
        int w = rotated ? item.Def.H : item.Def.W, h = rotated ? item.Def.W : item.Def.H;
        var target = to.At(x, y);
        if (target != null && target != item && target.Def == item.Def && item.Def.Stack > 1 && target.Count < item.Def.Stack)
        {
            int moved = Math.Min(item.Count, item.Def.Stack - target.Count);
            target.Count += moved; item.Count -= moved;
            if (item.Count <= 0) GridOf(item)?.Items.Remove(item);
            Changed?.Invoke(); Save();
            return true;
        }
        if (!to.Fits(x, y, w, h, item)) return false;
        GridOf(item)?.Items.Remove(item);
        item.X = x; item.Y = y; item.Rotated = rotated;
        to.Items.Add(item);
        Changed?.Invoke(); Save();
        return true;
    }

    // ================================================================= magazines

    /// <summary>The fullest magazine you carry (vest first: from the backpack, reloading takes longer).</summary>
    public (Item Mag, bool FromPack)? NextMagazine()
    {
        var v = Vest.Items.Where(i => i.Def.Kind == ItemKind.Magazine && i.Rounds > 0).OrderByDescending(i => i.Rounds).FirstOrDefault();
        if (v != null) return (v, false);
        var p = Pack.Items.Where(i => i.Def.Kind == ItemKind.Magazine && i.Rounds > 0).OrderByDescending(i => i.Rounds).FirstOrDefault();
        return p != null ? (p, true) : null;
    }

    /// <summary>The swap at the end of a reload: the old magazine goes back in the vest (if it has rounds left;
    /// empties are dropped), the new one goes in the rifle.</summary>
    /// <summary>The gun in your hands.</summary>
    public WeaponDef Weapon => WeaponDef.Get(Loadout.Weapon);
    public int Capacity => Weapon.Capacity;

    /// <summary>Top the gun up from the magazines you carry (fullest first; several if it's a big box). Empty ones
    /// are thrown away, part-used ones stay in the bag. Returns false if there was nothing to load.</summary>
    public bool Reload()
    {
        int need = Capacity - Loaded, got = 0;
        while (need > 0 && NextMagazine() is { } next)
        {
            var mag = next.Mag;
            int take = Math.Min(need, mag.Rounds);
            mag.Rounds -= take; need -= take; got += take;
            if (mag.Rounds <= 0) GridOf(mag)?.Items.Remove(mag);
        }
        Loaded += got;
        Changed?.Invoke(); Save();
        return got > 0;
    }

    /// <summary>Change guns (a dropped one you picked up): what's fitted moves over; the rounds in it come with it.</summary>
    public string SwapWeapon(string id, int rounds)
    {
        string old = Loadout.Weapon;
        Loadout = Loadout with { Weapon = id };
        Loaded = Math.Min(rounds, Capacity);
        Changed?.Invoke(); LoadoutChanged?.Invoke(); Save();
        return old;
    }

    public bool SwapMagazine()
    {
        if (NextMagazine() is not { } next) return false;
        var (mag, _) = next;
        var grid = GridOf(mag)!;
        grid.Items.Remove(mag);
        if (Loaded > 0)
        {
            var old = new Item(LoadedMag) { Rounds = Loaded };
            if (!Vest.Place(old)) Pack.Place(old);
        }
        Loaded = mag.Rounds;
        LoadedMag = mag.Def;
        Changed?.Invoke(); Save();
        return true;
    }

    public bool Shoot()
    {
        if (Loaded <= 0) return false;
        Loaded--;
        return true;
    }

    /// <summary>Tests: a full magazine in the rifle without using one up.</summary>
    public void TestTopUp() => Loaded = Capacity;

    // ================================================================= the rifle

    /// <summary>Fit an attachment from the bags; what it replaces goes into the bags. Null removes (side/underbarrel).</summary>
    public string? Fit(Slot slot, Item? fromBag)
    {
        string? current = Loadout.In(slot);
        if (fromBag != null && fromBag.Def.Slot != slot) return "That doesn't go there.";
        if (fromBag == null && slot is Slot.Optic or Slot.Muzzle) return null;
        if (fromBag != null) GridOf(fromBag)?.Items.Remove(fromBag);
        if (current != null && ItemDef.All.TryGetValue(current, out var old))
        {
            var back = new Item(old);
            if (!Pack.Place(back) && !Vest.Place(back))
            {
                if (fromBag != null) Pack.Place(fromBag);
                Changed?.Invoke();
                return "No room in the backpack for the old one.";
            }
        }
        Loadout = Loadout.With(slot, fromBag?.Def.Id);
        if (slot == Slot.Side && fromBag == null) LightOn = false;
        Changed?.Invoke(); LoadoutChanged?.Invoke(); Save();
        return null;
    }

    public void Paint(string id)
    {
        Loadout = Loadout with { Paint = id };
        LoadoutChanged?.Invoke(); Save();
    }

    // ================================================================= intel

    public void AddIntel(string name)
    {
        var item = new Item(ItemDef.Intel) { Label = char.ToUpper(name[0]) + name[1..] };
        if (!Pack.Place(item) && !Vest.Place(item))
        {
            // Make room: intel matters more than anything else in there.
            var drop = Pack.Items.OrderBy(i => i.Def.Kind == ItemKind.Attachment ? 1 : 0).First();
            Pack.Items.Remove(drop);
            Pack.Place(item);
        }
        Changed?.Invoke();
    }

    public void RemoveIntel()
    {
        Vest.Items.RemoveAll(i => i.Def == ItemDef.Intel);
        Pack.Items.RemoveAll(i => i.Def == ItemDef.Intel);
        Changed?.Invoke(); Save();
    }

    // ================================================================= per frame: bandages, bodies, light

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        if (_healLeft > 0 && !Player.Dead)
        {
            float h = Math.Min(_healLeft, 12 * dt);
            Player.Heal(h);
            _healLeft -= h;
        }
        bool captured = Input.MouseMode == Input.MouseModeEnum.Captured;
        bool hKey = Input.IsPhysicalKeyPressed(Key.H), lKey = Input.IsPhysicalKeyPressed(Key.L), fKey = Input.IsPhysicalKeyPressed(Key.F);
        if (captured && hKey && !_hWas) UseMedical(null);
        if (captured && lKey && !_lWas && Loadout.Side != null) LightOn = !LightOn;
        _hWas = hKey; _lWas = lKey;

        // Searching bodies.
        Guard? body = null;
        if (!Player.Dead)
            foreach (var g in Guard.All)
                if (g.State == Guard.Mood.Dead && !_searched.Contains(g) && g.GlobalPosition.DistanceTo(Player.GlobalPosition) < 2.4f) { body = g; break; }
        if (body != null)
        {
            Hud.Prompt("F   Search the body");
            if (fKey && !_fWas && captured) Search(body);
        }
        else if (!Player.Dead && DroppedGunNear() is { } gun)
        {
            var def = WeaponDef.Get((string)gun.GetMeta("weapon"));
            if (def.Id != Loadout.Weapon)
            {
                Hud.Prompt($"F   Swap for the {def.Name.ToLower()} ({(int)gun.GetMeta("rounds")} rounds)");
                if (fKey && !_fWas && captured) PickUp(gun);
            }
        }
        else if (!Player.Dead)
            for (int i = 0; i < Loot.Count; i++)
            {
                if (Loot[i].At.DistanceTo(Player.GlobalPosition) > 2.2f) continue;
                Hud.Prompt($"F   {Loot[i].Prompt}");
                if (fKey && !_fWas && captured) { Hud.Toast(Loot[i].Give(this)); Loot.RemoveAt(i); }
                break;
            }
        _fWas = fKey;
    }

    /// <summary>Use a dressing or a rice parcel (null: the best one for the job, vest first).</summary>
    public void UseMedical(Item? item)
    {
        if (Player.Dead) return;
        item ??= Vest.Items.FirstOrDefault(i => i.Def == ItemDef.Bandage) ?? Pack.Items.FirstOrDefault(i => i.Def == ItemDef.Bandage)
              ?? AllItems.FirstOrDefault(i => i.Def.Kind == ItemKind.Medical);
        if (item == null) { Hud.Toast("No dressings left"); return; }
        if (Player.Health >= Walker.MaxHealth - .5f) { Hud.Toast("You're fine. Mostly."); return; }
        _healLeft += item.Def.Heal;
        item.Count--;
        if (item.Count <= 0) GridOf(item)?.Items.Remove(item);
        Hud.Toast(item.Def == ItemDef.Bandage ? "Patching yourself up..." : "Rice. Grandmother was right.");
        Changed?.Invoke(); Save();
    }

    private void Search(Guard g)
    {
        _searched.Add(g);
        var rng = new RandomNumberGenerator();
        int mags = rng.RandiRange(1, 2);
        var found = new List<string>();
        int got = 0;
        for (int k = 0; k < mags; k++) if (Add(ItemDef.Mag20, 1, rng.RandiRange(5, 20), quiet: true)) got++;
        if (got > 0) found.Add(got == 1 ? "a magazine" : $"{got} magazines");
        if (rng.Randf() < .35f && Add(ItemDef.Bandage, 1, quiet: true)) found.Add("a dressing");
        if (rng.Randf() < .08f && Add(ItemDef.Mag30, 1, quiet: true)) found.Add("a long magazine");
        Hud.Toast(found.Count > 0 ? $"Found {string.Join(", ", found)}" : "Nothing useful. Or no room for it.");
    }

    private RigidBody3D? DroppedGunNear()
    {
        RigidBody3D? best = null;
        foreach (var n in GetTree().GetNodesInGroup("dropped_weapon"))
            if (n is RigidBody3D b && b.GlobalPosition.DistanceTo(Player.GlobalPosition) < 2 && (best == null || b.GlobalPosition.DistanceTo(Player.GlobalPosition) < best.GlobalPosition.DistanceTo(Player.GlobalPosition)))
                best = b;
        return best;
    }

    /// <summary>Take the dropped gun; yours goes on the ground where it was (so you can change your mind).</summary>
    public void PickUp(RigidBody3D gun)
    {
        string id = (string)gun.GetMeta("weapon");
        int rounds = (int)gun.GetMeta("rounds");
        var at = gun.GlobalTransform;
        int mine = Loaded;
        string old = SwapWeapon(id, rounds);
        gun.QueueFree();
        var oldDef = WeaponDef.Get(old);
        if (ResourceLoader.Exists(oldDef.Model))
        {
            var body = new RigidBody3D { Mass = 3.5f, CollisionLayer = 1 << 2, CollisionMask = 1, ContinuousCd = true };
            body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(.07f, .2f, .95f) }, Position = new Vector3(0, 0, .12f) });
            body.AddChild(ResourceLoader.Load<PackedScene>(oldDef.Model).Instantiate<Node3D>());
            body.SetMeta("weapon", old); body.SetMeta("rounds", mine);
            body.AddToGroup("dropped_weapon");
            GetTree().CurrentScene.AddChild(body);
            body.GlobalTransform = new Transform3D(at.Basis.Orthonormalized(), at.Origin + Vector3.Up * .3f);
        }
        Hud.Toast($"Took the {WeaponDef.Get(id).Name.ToLower()}");
    }

    // ================================================================= keeping it

    private sealed record SavedItem(string Id, int Count, int Rounds, int X, int Y, bool Rot);
    private sealed record Saved(Loadout Loadout, int Loaded, string LoadedMag, List<SavedItem> Vest, List<SavedItem> Pack);
    private const string File = "user://kit.json";

    public void Save()
    {
        if (!Persist) return;
        static List<SavedItem> Of(Grid g) => g.Items.Where(i => i.Def != ItemDef.Intel).Select(i => new SavedItem(i.Def.Id, i.Count, i.Rounds, i.X, i.Y, i.Rotated)).ToList();
        try
        {
            using var f = FileAccess.Open(File, FileAccess.ModeFlags.Write);
            f.StoreString(JsonSerializer.Serialize(new Saved(Loadout, Loaded, LoadedMag.Id, Of(Vest), Of(Pack))));
        }
        catch (Exception e) { GD.PushWarning($"Couldn't save the kit: {e.Message}"); }
    }

    private bool Load()
    {
        if (!Persist || !FileAccess.FileExists(File)) return false;
        try
        {
            using var f = FileAccess.Open(File, FileAccess.ModeFlags.Read);
            var s = JsonSerializer.Deserialize<Saved>(f.GetAsText());
            if (s == null) return false;
            Loadout = s.Loadout;
            Loaded = s.Loaded;
            LoadedMag = ItemDef.All.GetValueOrDefault(s.LoadedMag, ItemDef.Mag20);
            void Fill(Grid g, List<SavedItem> items)
            {
                foreach (var i in items)
                    if (ItemDef.All.TryGetValue(i.Id, out var def))
                    {
                        var item = new Item(def, i.Count) { Rounds = i.Rounds, X = i.X, Y = i.Y, Rotated = i.Rot };
                        if (g.Fits(item.X, item.Y, item.W, item.H)) g.Items.Add(item); else g.Place(item);
                    }
            }
            Fill(Vest, s.Vest); Fill(Pack, s.Pack);
            return true;
        }
        catch (Exception e) { GD.PushWarning($"Couldn't read the kit ({e.Message}); starting fresh."); return false; }
    }
}
