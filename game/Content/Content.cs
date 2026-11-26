using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Godot;

namespace DeadRecon.Data;

/// <summary>
/// Game data you can edit and mod without touching code (docs/MODDING.md).
/// <para>Sources, in order: built-in defaults (the code), then res://content/game/*.json (the shipped data: replaces
/// the defaults key by key), then mods: every folder in mods/ next to the game (and in res://mods/ and user://mods/)
/// with JSON files of the same shape. Mods <b>add</b>: lists get longer, dictionaries of lists get more lines, and
/// records with an "id" replace the one with that id (or are new).</para>
/// <para>Each top-level key of every file is one section ("guard_lines", "items", "weapons"...). File names don't
/// matter; sections do.</para>
/// </summary>
public static class Content
{
    private static readonly Dictionary<string, JsonNode> Defaults = new(), Shipped = new();
    private static readonly List<(string Mod, JsonObject Data)> Mods = new();
    private static readonly Dictionary<string, JsonNode?> Merged = new();
    private static bool _loaded;
    public static readonly List<string> Loaded = new();

    /// <summary>A section's built-in default (called by the code that owns the section).</summary>
    public static void Default(string section, object value)
    {
        if (!Defaults.ContainsKey(section)) Defaults[section] = JsonSerializer.SerializeToNode(value, Json)!;
        Merged.Remove(section);
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, IncludeFields = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static void Load()
    {
        if (_loaded) return;
        _loaded = true;
        foreach (var file in Files("res://content/game"))
            if (Parse(file) is { } obj)
                foreach (var (k, v) in obj) if (v != null) Shipped[k] = v.DeepClone();
        var modRoots = new List<string> { "res://mods", "user://mods" };
        var exeDir = Path.GetDirectoryName(OS.GetExecutablePath());
        if (!OS.HasFeature("editor") && exeDir != null) modRoots.Insert(0, Path.Combine(exeDir, "mods"));
        foreach (var root in modRoots)
        {
            if (!DirAccess.DirExistsAbsolute(root)) continue;
            foreach (var mod in DirAccess.GetDirectoriesAt(root).OrderBy(d => d))
            {
                int files = 0;
                foreach (var file in Files($"{root}/{mod}"))
                    if (Parse(file) is { } obj) { Mods.Add((mod, obj)); files++; }
                if (files > 0) { Loaded.Add(mod); GD.Print($"[content] mod \"{mod}\": {files} file(s)"); }
            }
        }
    }

    private static IEnumerable<string> Files(string dir)
    {
        if (!DirAccess.DirExistsAbsolute(dir)) yield break;
        foreach (var f in DirAccess.GetFilesAt(dir).OrderBy(f => f))
            if (f.EndsWith(".json")) yield return $"{dir}/{f}";
    }

    private static JsonObject? Parse(string path)
    {
        try
        {
            using var f = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Read);
            return JsonNode.Parse(f.GetAsText(), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject;
        }
        catch (Exception e)
        {
            GD.PushWarning($"[content] can't read {path}: {e.Message}");
            DeadRecon.UI.Hud.Toast($"Mod file has an error: {Path.GetFileName(path)} (see the log)");
            return null;
        }
    }

    /// <summary>The merged section (defaults or shipped, plus every mod).</summary>
    private static JsonNode? Section(string key)
    {
        Load();
        if (Merged.TryGetValue(key, out var m)) return m;
        JsonNode? node = Shipped.TryGetValue(key, out var s) ? s.DeepClone() : Defaults.TryGetValue(key, out var d) ? d.DeepClone() : null;
        foreach (var (_, data) in Mods)
            if (data[key] is { } add) node = node == null ? add.DeepClone() : MergeInto(node, add);
        return Merged[key] = node;
    }

    private static JsonNode MergeInto(JsonNode baseNode, JsonNode add)
    {
        switch (baseNode, add)
        {
            case (JsonArray a, JsonArray b):
                foreach (var item in b)
                {
                    if (item is JsonObject o && o["id"]?.GetValue<string>() is { } id)
                    {
                        int at = a.ToList().FindIndex(x => x is JsonObject xo && xo["id"]?.GetValue<string>() == id);
                        if (at >= 0)
                        {
                            // Override only the fields the mod gives.
                            var target = (JsonObject)a[at]!;
                            foreach (var (k, v) in o) target[k] = v?.DeepClone();
                            continue;
                        }
                    }
                    a.Add(item?.DeepClone());
                }
                return a;
            case (JsonObject x, JsonObject y):
                foreach (var (k, v) in y)
                {
                    if (v == null) continue;
                    x[k] = x[k] is { } existing ? MergeInto(existing, v) : v.DeepClone();
                }
                return x;
            default:
                return add.DeepClone();
        }
    }

    public static T Get<T>(string key, T fallback)
    {
        if (fallback != null && !Defaults.ContainsKey(key)) Default(key, fallback);
        try { return Section(key) is { } n ? n.Deserialize<T>(Json) ?? fallback : fallback; }
        catch (Exception e) { GD.PushWarning($"[content] section \"{key}\" doesn't fit: {e.Message}"); return fallback; }
    }

    /// <summary>A list of lines (names, words).</summary>
    public static string[] Strings(string key, string[] fallback) => Get(key, fallback);

    /// <summary>Lines by kind ("sentry", "hurt"...).</summary>
    public static Dictionary<string, string[]> LineSets(string key, Dictionary<string, string[]> fallback)
    {
        // Kinds the data doesn't have (new in the code, or an older lines.json) keep their built-in lines.
        var got = Get(key, fallback);
        if (ReferenceEquals(got, fallback)) return fallback;
        var merged = new Dictionary<string, string[]>(fallback);
        foreach (var (kind, lines) in got) merged[kind] = lines;
        return merged;
    }

    /// <summary>Writes every section (shipped or default) to one file per section group, for editing.</summary>
    public static void Dump(string dir, Dictionary<string, string[]> files)
    {
        Directory.CreateDirectory(dir);
        foreach (var (file, sections) in files)
        {
            var obj = new JsonObject();
            foreach (var s in sections)
            {
                var n = Shipped.TryGetValue(s, out var sh) ? sh : Defaults.TryGetValue(s, out var d) ? d : null;
                if (n != null) obj[s] = n.DeepClone();
            }
            System.IO.File.WriteAllText(Path.Combine(dir, file), obj.ToJsonString(Json));
            GD.Print($"[content] wrote {file}: {string.Join(", ", sections)}");
        }
    }
}
