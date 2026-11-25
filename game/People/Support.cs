using System;
using System.Collections.Generic;
using System.Text.Json;
using DeadRecon.World;
using Godot;

namespace DeadRecon.People;

/// <summary>
/// How each settlement feels about you, -100..100: Hostile, Wary, Friendly, Loyal (docs/LIVING_JUNGLE.md, "Locals and
/// support"). Remembered between sessions in user://villages.json (per world name). Changes show as a small HUD line.
/// </summary>
public static class Support
{
    public enum Level { Hostile, Wary, Friendly, Loyal }

    private static readonly Dictionary<int, float> Values = new();
    private static string _world = "";
    private const string File = "user://villages.json";

    public static event Action<int, float, string>? Changed;
    /// <summary>False in scripted tests: they must not touch the player's saved villages.</summary>
    public static bool Persist { get; set; } = true;

    public static Level LevelOf(float v) => v < -35 ? Level.Hostile : v < 15 ? Level.Wary : v < 55 ? Level.Friendly : Level.Loyal;
    public static Level LevelAt(int site) => LevelOf(Get(site));

    /// <summary>Where a village starts: a little wary of armed strangers, some more than others.</summary>
    private static float Initial(int site) => -8 + (site * 7919 % 29);

    public static float Get(int site) => Values.TryGetValue(site, out var v) ? v : Initial(site);

    public static void Change(int site, float delta, string reason)
    {
        if (delta == 0) return;
        float before = Get(site), after = Math.Clamp(before + delta, -100, 100);
        Values[site] = after;
        Changed?.Invoke(site, after - before, reason);
        var name = Settlements.Current is { } s && site < s.Sites.Count ? s.Sites[site].Name : $"site {site}";
        string shift = LevelOf(after) != LevelOf(before) ? $"  ({Describe(LevelOf(after))})" : "";
        DeadRecon.UI.Hud.Toast($"{name}: {(delta > 0 ? "+" : "")}{delta:0}  {reason}{shift}");
        GD.Print($"[support] {name} {before:0} -> {after:0} ({reason})");
        Save();
    }

    /// <summary>Word spreads: a smaller change to every other settlement.</summary>
    public static void Spread(int except, float delta)
    {
        if (Settlements.Current is not { } s) return;
        foreach (var site in s.Sites)
            if (site.IsSettlement && site.Index != except) Values[site.Index] = Math.Clamp(Get(site.Index) + delta, -100, 100);
        Save();
    }

    public static string Describe(Level l) => l switch
    {
        Level.Hostile => "hostile to you", Level.Wary => "wary of you", Level.Friendly => "friendly", _ => "loyal to you",
    };

    public static void Load(string world)
    {
        _world = world;
        Values.Clear();
        try
        {
            if (!FileAccess.FileExists(File)) return;
            using var f = FileAccess.Open(File, FileAccess.ModeFlags.Read);
            using var doc = JsonDocument.Parse(f.GetAsText());
            if (!doc.RootElement.TryGetProperty(world, out var w)) return;
            foreach (var p in w.EnumerateObject()) Values[int.Parse(p.Name)] = (float)p.Value.GetDouble();
        }
        catch (Exception e) { GD.PushWarning($"Couldn't read {File}: {e.Message}"); }
    }

    /// <summary>Forget everything (tests).</summary>
    public static void Reset() { Values.Clear(); Save(); }

    private static void Save()
    {
        if (_world == "" || !Persist) return;
        try
        {
            var all = new Dictionary<string, Dictionary<string, float>>();
            if (FileAccess.FileExists(File))
            {
                using var r = FileAccess.Open(File, FileAccess.ModeFlags.Read);
                all = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, float>>>(r.GetAsText()) ?? all;
            }
            var mine = new Dictionary<string, float>();
            foreach (var (k, v) in Values) mine[k.ToString()] = MathF.Round(v, 1);
            all[_world] = mine;
            using var f = FileAccess.Open(File, FileAccess.ModeFlags.Write);
            f.StoreString(JsonSerializer.Serialize(all));
        }
        catch (Exception e) { GD.PushWarning($"Couldn't save {File}: {e.Message}"); }
    }
}
