using System;
using System.Collections.Generic;
using System.Linq;
using DeadRecon.AI;
using DeadRecon.People;
using DeadRecon.World;
using Godot;

namespace DeadRecon.Base;

/// <summary>
/// What comes out of the radio: HQ small talk, other units' complaints, and rumours made from what's actually
/// going on (where patrols are walking, which checkpoints are still manned, how the villages feel, the weather).
/// Kept as a log the radio screen shows.
/// </summary>
public static class Chatter
{
    public sealed record Line(string Who, string Text);
    public static readonly List<(string Clock, string Who, string Text)> Log = new();
    private static readonly Random Rng = new();

    public static readonly (string Who, string Text)[] Banter =
    {
        ("HQ", "Whoever took the last tin of peaches: we know. We are watching. We are hungry."),
        ("HQ", "Reminder: the jungle is not a toilet. Well. It is. But be discreet."),
        ("Echo Two", "Echo Two to anyone. Has anyone seen my left boot? ...Echo Two out."),
        ("HQ", "Weather office says rain. Weather office always says rain. Weather office is usually right."),
        ("Supply", "Supply run delayed. The mule has opinions."),
        ("HQ", "Morale officer reports morale is fine. Morale officer has been asked to stop writing reports in crayon."),
        ("Bravo Six", "Bravo Six here. We found a frog the size of a dog. It's ours now. His name is Kevin."),
        ("HQ", "Radio discipline, people. That means less singing."),
        ("Echo Two", "Found the boot. There was a frog in it. Not Kevin. Different frog."),
        ("HQ", "To whoever keeps knocking trees over: forestry has filed a complaint. We have filed it under 'jungle'."),
        ("Supply", "If you find building supplies at their camps, keep them. They owe us for the peaches."),
        ("HQ", "Night patrols: check your torch. Then switch it off. Then never switch it on near the enemy."),
    };

    /// <summary>A line now: often a rumour about the real world, sometimes just talk.</summary>
    public static (string Who, string Text) Next(Vector3 home)
    {
        var recent = Log.Skip(Math.Max(0, Log.Count - 6)).Select(l => l.Text).ToHashSet();
        var rumours = Rumours(home).Where(r => !recent.Contains(r.Item2)).ToList();
        if (rumours.Count > 0 && Rng.NextDouble() < .6) return rumours[Rng.Next(rumours.Count)];
        var all = DeadRecon.Data.Content.Get("radio_banter", Banter.Select(b => new Line(b.Who, b.Text)).ToArray()).Select(l => (l.Who, l.Text)).ToArray();
        var banter = all.Where(b => !recent.Contains(b.Text)).ToArray();
        return banter.Length > 0 ? banter[Rng.Next(banter.Length)] : all[Rng.Next(all.Length)];
    }

    public static void Add(string who, string text)
    {
        Log.Add((TimeOfDay.Current?.Clock ?? "--:--", who, text));
        if (Log.Count > 40) Log.RemoveAt(0);
    }

    private static List<(string, string)> Rumours(Vector3 home)
    {
        var list = new List<(string, string)>();
        string Dir(Vector3 d)
        {
            string[] names = { "north", "north-east", "east", "south-east", "south", "south-west", "west", "north-west" };
            return names[(int)MathF.Round(((Mathf.RadToDeg(MathF.Atan2(d.X, -d.Z)) + 360) % 360) / 45) % 8];
        }
        string Km(float m) => m < 1000 ? $"{Mathf.Round(m / 100) * 100:0} m" : $"{m / 1000:0.0} km";
        if (Patrols.Current is { } patrols)
        {
            var near = patrols.PatrolPositions.OrderBy(p => p.DistanceTo(home)).FirstOrDefault();
            if (near != default && near.DistanceTo(home) < 2500)
                list.Add(("Lookout", $"Patrol spotted on the road {Km(near.DistanceTo(home))} {Dir(near - home)} of you. Two or three of them. Walking like they own the place."));
            foreach (var (site, cleared) in patrols.Checkpoints.OrderBy(c => c.Site.Centre.DistanceTo(home)).Take(2))
                list.Add(("Lookout", cleared ? $"{site.Name} is still empty. Nobody's come to replace them. Yet."
                                             : $"{site.Name}, {Km(site.Centre.DistanceTo(home))} {Dir(site.Centre - home)}: still manned. They've got the kettle on."));
        }
        if (Settlements.Current is { } towns)
        {
            var village = towns.Sites.Where(s => s.IsSettlement).OrderBy(s => s.Centre.DistanceTo(home)).Take(4).OrderBy(_ => Rng.Next()).FirstOrDefault();
            if (village != null)
            {
                var level = Support.LevelAt(village.Index);
                list.Add(("Rumour", level switch
                {
                    Support.Level.Hostile => $"Word from {village.Name}: they're not happy with you. Someone painted a rude picture of your face on the well.",
                    Support.Level.Wary => $"People in {village.Name} are wary. They'd talk to you more if you did something for them. Ask the headman.",
                    Support.Level.Friendly => $"{village.Name} speaks well of you. The grandmothers say you're polite. For a soldier.",
                    _ => $"{village.Name} would do anything for you. There's talk of naming a buffalo after you.",
                }));
            }
        }
        if (DeadRecon.World.Weather.Current is { } w && w.Target != DeadRecon.World.Weather.Kind.Clear)
            list.Add(("Weather", "Rain coming in. Good cover for moving, bad for your socks."));
        return list;
    }
}
