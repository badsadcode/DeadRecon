using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Godot;

namespace DeadRecon.World;

/// <summary>
/// Where people live and how they get there: the sites and roads the WorldBaker's settle pass wrote
/// (settlements.json + settle_mask.png next to the height tiles). Read-only after <see cref="Load"/>, so the
/// scatter threads may query it.
/// <para>The mask has one pixel per height sample: R = road surface, G = settlement ground (yards, plazas).</para>
/// </summary>
public sealed class Settlements
{
    public sealed record Site(int Index, string Kind, string Name, Vector3 Centre, float Radius)
    {
        public bool IsSettlement => Kind is "hamlet" or "village" or "town";
        public string Describe => Kind switch { "town" => "market town", "outpost" => "checkpoint", _ => Kind };
    }
    public sealed record Road(string Kind, float Width, Vector3[] Points);
    /// <summary>A rice paddy or wheat field: a rectangle (local x spans W, local z spans D) turned by Rotation about +Y.
    /// Level: the paddy floor (levelled terrace) or the field's mean ground height.</summary>
    public sealed record Field(int Site, string Kind, string Stage, Vector3 Centre, float Rotation, float W, float D, float Level);

    public static Settlements? Current { get; private set; }

    public readonly List<Site> Sites = new();
    public readonly List<Road> Roads = new();
    public readonly List<(string Kind, Vector2 At)> Crossings = new();
    public readonly List<Field> Fields = new();
    public Image? Mask { get; private set; }

    private byte[] _mask = Array.Empty<byte>();
    private int _px;
    private BakedWorld _world = null!;

    /// <summary>Loads the world's settlements, or an empty set if the world was baked before the settle pass.</summary>
    public static Settlements Load(string dir, BakedWorld world)
    {
        var s = new Settlements { _world = world };
        Current = s;
        string json = Path.Combine(dir, "settlements.json"), mask = Path.Combine(dir, "settle_mask.png");
        if (!File.Exists(json) || !File.Exists(mask))
        {
            GD.PushWarning("No settlements in this world: run `dotnet run -c Release --project tools/WorldBaker -- settle world/jungle`.");
            return s;
        }
        using var doc = JsonDocument.Parse(File.ReadAllText(json));
        int i = 0;
        foreach (var e in doc.RootElement.GetProperty("sites").EnumerateArray())
            s.Sites.Add(new Site(i++, e.GetProperty("kind").GetString()!, e.GetProperty("name").GetString()!,
                new Vector3((float)e.GetProperty("x").GetDouble(), (float)e.GetProperty("y").GetDouble(), (float)e.GetProperty("z").GetDouble()),
                (float)e.GetProperty("radius").GetDouble()));
        foreach (var e in doc.RootElement.GetProperty("roads").EnumerateArray())
        {
            var p = new List<double>();
            foreach (var v in e.GetProperty("points").EnumerateArray()) p.Add(v.GetDouble());
            var pts = new Vector3[p.Count / 3];
            for (int k = 0; k < pts.Length; k++) pts[k] = new Vector3((float)p[k * 3], (float)p[k * 3 + 1], (float)p[k * 3 + 2]);
            s.Roads.Add(new Road(e.GetProperty("kind").GetString()!, (float)e.GetProperty("width").GetDouble(), pts));
        }
        if (doc.RootElement.TryGetProperty("fields", out var fields))
            foreach (var e in fields.EnumerateArray())
            {
                float level = (float)e.GetProperty("level").GetDouble();
                s.Fields.Add(new Field(e.GetProperty("site").GetInt32(), e.GetProperty("kind").GetString()!, e.GetProperty("stage").GetString()!,
                    new Vector3((float)e.GetProperty("x").GetDouble(), level, (float)e.GetProperty("z").GetDouble()),
                    (float)e.GetProperty("rotation").GetDouble(), (float)e.GetProperty("w").GetDouble(), (float)e.GetProperty("d").GetDouble(), level));
            }
        foreach (var e in doc.RootElement.GetProperty("crossings").EnumerateArray())
            s.Crossings.Add((e.GetProperty("kind").GetString()!, new Vector2((float)e.GetProperty("x").GetDouble(), (float)e.GetProperty("z").GetDouble())));

        var img = Image.LoadFromFile(mask);
        if (img.GetFormat() != Image.Format.Rgb8) img.Convert(Image.Format.Rgb8);
        s._px = img.GetWidth();
        s._mask = img.GetData();
        img.GenerateMipmaps();
        s.Mask = img;
        GD.Print($"[world] {s.Sites.Count} sites, {s.Roads.Count} roads");
        return s;
    }

    private float Channel(float x, float z, int c)
    {
        if (_px == 0) return 0;
        var (fx, fz) = _world.ToSample(x, z);
        int gx = Math.Clamp((int)MathF.Round(fx), 0, _px - 1), gz = Math.Clamp((int)MathF.Round(fz), 0, _px - 1);
        return _mask[(gz * _px + gx) * 3 + c] / 255f;
    }

    /// <summary>0..1: how much road surface is here.</summary>
    public float RoadAt(float x, float z) => Channel(x, z, 0);
    /// <summary>0..1: how much trampled settlement ground is here.</summary>
    public float YardAt(float x, float z) => Channel(x, z, 1);

    /// <summary>0 no field, ~.5 wheat, 1 rice paddy.</summary>
    public float FieldAt(float x, float z) => Channel(x, z, 2);

    /// <summary>Road within <paramref name="margin"/> metres? (four probes and the centre: good enough for planting)</summary>
    public bool NearRoad(float x, float z, float margin)
    {
        if (RoadAt(x, z) > .05f) return true;
        return RoadAt(x + margin, z) > .05f || RoadAt(x - margin, z) > .05f || RoadAt(x, z + margin) > .05f || RoadAt(x, z - margin) > .05f;
    }

    /// <summary>The site whose ground this is (within its radius times <paramref name="scale"/>), if any.</summary>
    public Site? SiteAt(float x, float z, float scale = 1)
    {
        foreach (var s in Sites)
        {
            float dx = x - s.Centre.X, dz = z - s.Centre.Z, r = s.Radius * scale;
            if (dx * dx + dz * dz < r * r) return s;
        }
        return null;
    }

    /// <summary>Should trees stay off this spot? Roads (with a margin for the trunk) and settlement ground.</summary>
    public bool KeepClear(float x, float z, float margin) =>
        NearRoad(x, z, margin) || SiteAt(x, z, 1.15f) != null || FieldAt(x, z) > .02f ||
        FieldAt(x + margin, z) > .02f || FieldAt(x - margin, z) > .02f || FieldAt(x, z + margin) > .02f || FieldAt(x, z - margin) > .02f;
}
