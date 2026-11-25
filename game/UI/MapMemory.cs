using System;
using System.Collections;
using DeadRecon.Player;
using DeadRecon.World;
using Godot;

namespace DeadRecon.UI;

/// <summary>
/// What you've seen of the region (64 m cells within 300 m of wherever you've been), and your waypoint. Hamlets and
/// checkpoints appear on the map once you've been near them; towns and villages are on any paper map. Kept between
/// sessions in user://map_seen.json (per world); not in tests.
/// </summary>
public partial class MapMemory : Node
{
    public static MapMemory? Current { get; private set; }
    public static bool Persist { get; set; } = true;

    public Walker Player = null!;
    public BakedWorld World = null!;

    public const float Cell = 64, Reveal = 300;
    public int Cells { get; private set; }
    private BitArray _seen = new(1);
    private double _since = 1, _sinceSave;
    private bool _dirty;
    private const string File = "user://map_seen.json";

    /// <summary>Your marker (click on the map). Shown on the compass; cleared when you get there.</summary>
    public Vector3? Waypoint { get; set; }
    /// <summary>Bumped when something new is seen (the map refreshes its fog).</summary>
    public int Version { get; private set; }

    public override void _Ready()
    {
        Current = this;
        Cells = (int)MathF.Ceiling(World.Size / Cell);
        _seen = new BitArray(Cells * Cells);
        Load();
    }

    public override void _ExitTree()
    {
        if (_dirty) Save();
        if (Current == this) Current = null;
    }

    public bool Seen(float x, float z)
    {
        int i = (int)((x + World.Half) / Cell), j = (int)((z + World.Half) / Cell);
        return i >= 0 && j >= 0 && i < Cells && j < Cells && _seen[j * Cells + i];
    }

    public bool SeenCell(int i, int j) => _seen[j * Cells + i];

    public override void _Process(double delta)
    {
        if (Waypoint is { } w && new Vector2(w.X - Player.GlobalPosition.X, w.Z - Player.GlobalPosition.Z).Length() < 12)
        {
            Waypoint = null;
            Hud.Toast("Waypoint reached");
        }
        _since += delta; _sinceSave += delta;
        if (_since < 1) return;
        _since = 0;
        RevealAround(Player.GlobalPosition, Reveal);
        if (_dirty && _sinceSave > 20) { Save(); _sinceSave = 0; }
    }

    public void RevealAround(Vector3 p, float radius)
    {
        int r = (int)MathF.Ceiling(radius / Cell);
        int ci = (int)((p.X + World.Half) / Cell), cj = (int)((p.Z + World.Half) / Cell);
        bool any = false;
        for (int j = cj - r; j <= cj + r; j++)
            for (int i = ci - r; i <= ci + r; i++)
            {
                if (i < 0 || j < 0 || i >= Cells || j >= Cells || _seen[j * Cells + i]) continue;
                float dx = (i + .5f) * Cell - World.Half - p.X, dz = (j + .5f) * Cell - World.Half - p.Z;
                if (dx * dx + dz * dz > radius * radius) continue;
                _seen[j * Cells + i] = true;
                any = true;
            }
        if (any) { Version++; _dirty = true; }
    }

    private void Save()
    {
        _dirty = false;
        if (!Persist) return;
        try
        {
            var bytes = new byte[(_seen.Length + 7) / 8];
            _seen.CopyTo(bytes, 0);
            var dict = new Godot.Collections.Dictionary();
            if (FileAccess.FileExists(File))
            {
                using var r = FileAccess.Open(File, FileAccess.ModeFlags.Read);
                if (Json.ParseString(r.GetAsText()).AsGodotDictionary() is { } old) dict = old;
            }
            dict[World.Name] = Convert.ToBase64String(bytes);
            using var f = FileAccess.Open(File, FileAccess.ModeFlags.Write);
            f.StoreString(Json.Stringify(dict));
        }
        catch (Exception e) { GD.PushWarning($"Couldn't save the map: {e.Message}"); }
    }

    private void Load()
    {
        if (!Persist || !FileAccess.FileExists(File)) return;
        try
        {
            using var f = FileAccess.Open(File, FileAccess.ModeFlags.Read);
            var dict = Json.ParseString(f.GetAsText()).AsGodotDictionary();
            if (dict == null || !dict.ContainsKey(World.Name)) return;
            var bytes = Convert.FromBase64String(dict[World.Name].AsString());
            var bits = new BitArray(bytes);
            for (int k = 0; k < Math.Min(bits.Length, _seen.Length); k++) _seen[k] = bits[k];
            Version++;
        }
        catch (Exception e) { GD.PushWarning($"Couldn't read the map memory: {e.Message}"); }
    }
}
