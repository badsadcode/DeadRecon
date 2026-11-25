using System;
using Godot;

namespace DeadRecon.UI;

/// <summary>Player settings, saved to user://settings.cfg. Static so any system can read them.</summary>
public static class Settings
{
    public enum Level { Recruit, Regular, Veteran }

    public static Level Difficulty { get; set; } = Level.Regular;
    public static float MouseSensitivity { get; set; } = 1;
    public static bool InvertY { get; set; }
    public static bool ShowDebug { get; set; }
    /// <summary>Which BadCharacters set the guards use: a folder under assets/soldiers (blocky, smooth).</summary>
    public static string SoldierStyle { get; set; } = "blocky";
    public static float MasterVolume { get; set; } = .8f;
    public static float AmbienceVolume { get; set; } = 1;
    public static bool Tracers { get; set; } = true;
    /// <summary>Volumetric fog: light shafts through the canopy. Costs some frame rate.</summary>
    public static bool LightShafts { get; set; } = true;

    // What difficulty does to the guards.
    /// <summary>How far and how quickly guards notice you.</summary>
    public static float GuardSight => Difficulty switch { Level.Recruit => .75f, Level.Veteran => 1.2f, _ => 1 };
    /// <summary>How often their shots hit.</summary>
    public static float GuardAccuracy => Difficulty switch { Level.Recruit => .55f, Level.Veteran => 1.3f, _ => 1 };
    /// <summary>How long they take to start shooting once they see you.</summary>
    public static float GuardReaction => Difficulty switch { Level.Recruit => 1.6f, Level.Veteran => .7f, _ => 1 };

    public static string Describe(Level l) => l switch
    {
        Level.Recruit => "Recruit: guards are short-sighted and can't hit a barn",
        Level.Veteran => "Veteran: guards notice fast and shoot straight",
        _ => "Regular: grumpy, but fair",
    };

    private const string Path = "user://settings.cfg";

    public static void Load()
    {
        var cfg = new ConfigFile();
        if (cfg.Load(Path) != Error.Ok) return;
        Difficulty = (Level)Math.Clamp((int)cfg.GetValue("game", "difficulty", (int)Level.Regular), 0, 2);
        MouseSensitivity = Math.Clamp((float)cfg.GetValue("input", "sensitivity", 1f), .2f, 3f);
        InvertY = (bool)cfg.GetValue("input", "invert_y", false);
        ShowDebug = (bool)cfg.GetValue("ui", "debug", false);
        SoldierStyle = (string)cfg.GetValue("game", "soldier_style", "blocky");
        MasterVolume = Math.Clamp((float)cfg.GetValue("audio", "master", .8f), 0, 1);
        AmbienceVolume = Math.Clamp((float)cfg.GetValue("audio", "ambience", 1f), 0, 1.5f);
        Tracers = (bool)cfg.GetValue("game", "tracers", true);
        LightShafts = (bool)cfg.GetValue("video", "light_shafts", true);
    }

    public static void Save()
    {
        var cfg = new ConfigFile();
        cfg.SetValue("game", "difficulty", (int)Difficulty);
        cfg.SetValue("input", "sensitivity", MouseSensitivity);
        cfg.SetValue("input", "invert_y", InvertY);
        cfg.SetValue("ui", "debug", ShowDebug);
        cfg.SetValue("game", "soldier_style", SoldierStyle);
        cfg.SetValue("audio", "master", MasterVolume);
        cfg.SetValue("audio", "ambience", AmbienceVolume);
        cfg.SetValue("game", "tracers", Tracers);
        cfg.SetValue("video", "light_shafts", LightShafts);
        cfg.Save(Path);
    }
}
