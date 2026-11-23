using System;
using System.IO;
using System.Text.Json;

namespace DeadRecon.World;

/// <summary>
/// The baked world as written by tools/WorldBaker: height and water tiles on one global sample grid.
/// Read-only after <see cref="Load"/>, so any thread may sample it.
/// <para>Godot coordinates: the region is centred on the origin, +X east, +Z south, Y up, metres.
/// "Global sample" (gx, gz) counts samples from the region's north-west corner.</para>
/// </summary>
public sealed class BakedWorld
{
    public string Name { get; private init; } = "";
    public float Spacing { get; private init; }        // metres between samples
    public int TileSamples { get; private init; }      // samples per tile side (tiles share edges)
    public int TilesPerSide { get; private init; }
    public int SamplesPerSide => TilesPerSide * (TileSamples - 1) + 1;
    public float Size => (SamplesPerSide - 1) * Spacing;
    public float Half => Size * .5f;

    private float _min, _step;
    private ushort[][] _height = Array.Empty<ushort[]>(), _water = Array.Empty<ushort[]>();

    public static BakedWorld Load(string dir)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "world.json")));
        var root = doc.RootElement;
        if (root.GetProperty("format").GetString() != "deadrecon.world/1") throw new InvalidDataException("Unknown world format.");
        var tiles = root.GetProperty("tiles");
        var enc = root.GetProperty("encoding");
        var w = new BakedWorld
        {
            Name = root.GetProperty("name").GetString() ?? "",
            Spacing = (float)tiles.GetProperty("spacing").GetDouble(),
            TileSamples = tiles.GetProperty("samples").GetInt32(),
            TilesPerSide = tiles.GetProperty("perSide").GetInt32(),
        };
        w._min = (float)enc.GetProperty("min").GetDouble();
        w._step = (float)enc.GetProperty("step").GetDouble();
        int count = w.TilesPerSide * w.TilesPerSide;
        w._height = new ushort[count][];
        w._water = new ushort[count][];
        for (int tz = 0; tz < w.TilesPerSide; tz++)
        for (int tx = 0; tx < w.TilesPerSide; tx++)
        {
            w._height[tz * w.TilesPerSide + tx] = ReadTile(Path.Combine(dir, "tiles", $"h_{tx}_{tz}.r16"), w.TileSamples);
            w._water[tz * w.TilesPerSide + tx] = ReadTile(Path.Combine(dir, "tiles", $"w_{tx}_{tz}.r16"), w.TileSamples);
        }
        return w;
    }

    private static ushort[] ReadTile(string path, int samples)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length != samples * samples * 2) throw new InvalidDataException($"Tile has the wrong size: {path}");
        var data = new ushort[samples * samples];
        Buffer.BlockCopy(bytes, 0, data, 0, bytes.Length);
        return data;
    }

    private ushort Raw(ushort[][] layer, int gx, int gz)
    {
        int last = SamplesPerSide - 1, cells = TileSamples - 1;
        gx = Math.Clamp(gx, 0, last); gz = Math.Clamp(gz, 0, last);
        int tx = Math.Min(gx / cells, TilesPerSide - 1), tz = Math.Min(gz / cells, TilesPerSide - 1);
        return layer[tz * TilesPerSide + tx][(gz - tz * cells) * TileSamples + gx - tx * cells];
    }

    /// <summary>Ground height (m) at a global sample (clamped to the region).</summary>
    public float Ground(int gx, int gz) => _min + Raw(_height, gx, gz) * _step;

    /// <summary>Water surface (m) at a global sample, or NaN where dry.</summary>
    public float Water(int gx, int gz)
    {
        ushort v = Raw(_water, gx, gz);
        return v == 0 ? float.NaN : _min + v * _step;
    }

    public bool IsWet(int gx, int gz) => Raw(_water, gx, gz) != 0;

    /// <summary>Global sample coordinate (fractional) of a Godot world position.</summary>
    public (float gx, float gz) ToSample(float x, float z) => ((x + Half) / Spacing, (z + Half) / Spacing);

    public float SampleToWorld(int g) => g * Spacing - Half;

    /// <summary>Bilinear ground height at a Godot world position.</summary>
    public float HeightAt(float x, float z)
    {
        var (fx, fz) = ToSample(x, z);
        int ix = (int)MathF.Floor(fx), iz = (int)MathF.Floor(fz);
        float u = fx - ix, v = fz - iz;
        float a = Ground(ix, iz), b = Ground(ix + 1, iz), c = Ground(ix, iz + 1), d = Ground(ix + 1, iz + 1);
        return (a * (1 - u) + b * u) * (1 - v) + (c * (1 - u) + d * u) * v;
    }

    /// <summary>True if any sample within <paramref name="radius"/> samples is water.</summary>
    public bool NearWater(int gx, int gz, int radius)
    {
        for (int dz = -radius; dz <= radius; dz += Math.Max(1, radius))
        for (int dx = -radius; dx <= radius; dx += Math.Max(1, radius))
            if (IsWet(gx + dx, gz + dz)) return true;
        return false;
    }
}
