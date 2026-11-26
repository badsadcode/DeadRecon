using System.Collections.Generic;
using System.Text.Json;
using Godot;

namespace DeadRecon.Missions;

/// <summary>
/// An operation in progress, saved so you can quit and carry on later (docs/MISSIONS_PLAN.md: "save anywhere,
/// safe"). The camp itself comes back from the operation's kind and seed; this keeps what changed: the step you're
/// on, who's dead (and where they fell), where you are, the time. Your pack is in kit.json already.
/// Saved only when it's safe (nobody hunting you, no fuse burning); cleared when the operation ends.
/// </summary>
public sealed class OperationSave
{
    public string Kind { get; set; } = "";
    public int Seed { get; set; }
    public int Step { get; set; }
    public double Elapsed { get; set; }
    public float Hour { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public float Yaw { get; set; }
    public float Health { get; set; } = 100;
    public float PlannedX { get; set; }
    public float PlannedY { get; set; }
    public float PlannedZ { get; set; }
    /// <summary>The camp's guards that are dead (index in the camp's list) and where they fell.</summary>
    public List<float[]> Dead { get; set; } = new();
    public bool Reinforced { get; set; }
    public bool Loud { get; set; }

    /// <summary>Handed straight to the next scene load (tests: a save that never touches the disk).</summary>
    public static OperationSave? Pending;

    /// <summary>Off in tests (they never touch your save).</summary>
    public static bool Persist { get; set; } = true;
    private const string File = "user://operation.json";

    public void Save()
    {
        if (!Persist) return;
        using var f = FileAccess.Open(File, FileAccess.ModeFlags.Write);
        f?.StoreString(JsonSerializer.Serialize(this));
    }

    public static OperationSave? Load()
    {
        if (!Persist || !FileAccess.FileExists(File)) return null;
        try
        {
            using var f = FileAccess.Open(File, FileAccess.ModeFlags.Read);
            return JsonSerializer.Deserialize<OperationSave>(f.GetAsText());
        }
        catch (System.Exception e) { GD.PushWarning($"Couldn't read the saved operation: {e.Message}"); return null; }
    }

    public static void Clear()
    {
        if (Persist && FileAccess.FileExists(File)) DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath(File));
    }
}
