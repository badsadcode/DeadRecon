// WorldBaker: generate the persistent world ONCE and save it as tiles.
//
//   dotnet run -c Release --project tools/WorldBaker -- content/world/jungle.world.json [outDir]
//
// Output (default world/<Name>/):
//   world.json            what was baked, how to decode the tiles, where the region sits in the generated landmass
//   tiles/h_<x>_<z>.r16   ground height, uint16 little-endian, (TileSamples x TileSamples), row-major, x fastest
//   tiles/w_<x>_<z>.r16   water surface, same encoding, 0 = dry
//   preview.png           hillshaded map of the region (rivers/lakes in blue)
//   overview.png          the whole generated landmass with the baked region outlined
//
// Tiles share their edge samples with their neighbours (TileSize / Spacing + 1 samples per side), so they join exactly.
// Region coordinates: (0,0) is the region's north-west corner, +x east, +z south, metres. Heights in metres.

using System.Diagnostics;
using System.Text.Json;
using DeadRecon.WorldGen;

static class Program
{
    // Height encoding shared by ground and water tiles: metres = Min + value * Step. Water value 0 means "dry".
    const double HeightMin = -100, HeightStep = 0.05; // covers -100 .. 3176.7 m in 5 cm steps

    sealed record Recipe
    {
        public required string Name { get; init; }
        public required int Seed { get; init; }
        public int RasterSize { get; init; } = 1024;
        public required double RegionSize { get; init; }
        public double Spacing { get; init; } = 2;
        public double TileSize { get; init; } = 1024;
        public double[]? RegionCenter { get; init; }
        public required WorldProfile Profile { get; init; }
        public TerrainStyle? Style { get; init; }
    }

    static int Main(string[] args)
    {
        if (args.Length < 1) { Console.Error.WriteLine("usage: WorldBaker <recipe.world.json> [outDir]"); return 2; }
        string recipePath = Path.GetFullPath(args[0]);
        var json = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip };
        var recipe = JsonSerializer.Deserialize<Recipe>(File.ReadAllText(recipePath), json) ?? throw new InvalidDataException("Empty recipe.");
        if (recipe.RegionSize % recipe.TileSize != 0 || recipe.TileSize % recipe.Spacing != 0)
            throw new InvalidDataException("RegionSize must be a multiple of TileSize, and TileSize a multiple of Spacing.");

        string contentDir = Path.GetDirectoryName(Path.GetDirectoryName(recipePath)!)!; // content/world/x.json -> content/
        BiomeRegistry.Current = BiomeRegistry.Parse(File.ReadAllText(Path.Combine(contentDir, "biomes.json")),
                                                     File.ReadAllText(Path.Combine(contentDir, "asset_sets.json")));
        string outDir = Path.GetFullPath(args.Length > 1 ? args[1] : Path.Combine("world", recipe.Name));
        Directory.CreateDirectory(Path.Combine(outDir, "tiles"));

        var total = Stopwatch.StartNew();
        Log($"Planning a {recipe.Profile.WorldSize / 1000:0} km landmass (seed {recipe.Seed}, raster {recipe.RasterSize})...");
        // Fixed world id: the id is a label, and a random one would make two bakes of the same recipe differ.
        var manifest = WorldManifest.Create(recipe.Seed, recipe.Profile, worldId: "00000000000000000000000000000001");
        var plan = new WorldPlan(manifest, recipe.RasterSize, recipe.Style ?? TerrainStyle.For(recipe.Profile),
                                 stage: i => Log($"  stage: {WorldPlan.StageNames[i]}"));
        var terrain = new TerrainSampler(plan);
        Log($"  planned in {total.Elapsed.TotalSeconds:0.0} s, {plan.Hydrology.Reaches.Count} river reaches");

        var (cx, cz) = recipe.RegionCenter is { Length: 2 } c ? (c[0], c[1]) : FindRegion(terrain, recipe.Profile.WorldSize, recipe.RegionSize);
        double x0 = cx - recipe.RegionSize / 2, z0 = cz - recipe.RegionSize / 2;
        Log($"Region {recipe.RegionSize / 1000:0.#} km centred at ({cx:0}, {cz:0}) of the landmass");

        int tilesPerSide = (int)(recipe.RegionSize / recipe.TileSize);
        int n = (int)(recipe.TileSize / recipe.Spacing) + 1;
        double minWave = recipe.Spacing * 2;
        double lowest = double.MaxValue, highest = double.MinValue;
        long wet = 0, clipped = 0;
        var bake = Stopwatch.StartNew();
        for (int tz = 0; tz < tilesPerSide; tz++)
        for (int tx = 0; tx < tilesPerSide; tx++)
        {
            var h = new ushort[n * n];
            var w = new ushort[n * n];
            double tLow = double.MaxValue, tHigh = double.MinValue; long tWet = 0, tClip = 0;
            object gate = new();
            Parallel.For(0, n, j =>
            {
                double z = z0 + tz * recipe.TileSize + j * recipe.Spacing;
                double low = double.MaxValue, high = double.MinValue; long wetRow = 0, clipRow = 0;
                for (int i = 0; i < n; i++)
                {
                    var s = terrain.Sample(x0 + tx * recipe.TileSize + i * recipe.Spacing, z, minWave);
                    h[j * n + i] = Encode(s.Height, ref clipRow);
                    low = Math.Min(low, s.Height); high = Math.Max(high, s.Height);
                    if (s.Water != WaterKind.None && double.IsFinite(s.WaterLevel))
                    {
                        w[j * n + i] = Math.Max((ushort)1, Encode(s.WaterLevel, ref clipRow));
                        wetRow++;
                    }
                }
                lock (gate) { tLow = Math.Min(tLow, low); tHigh = Math.Max(tHigh, high); tWet += wetRow; tClip += clipRow; }
            });
            WriteTile(Path.Combine(outDir, "tiles", $"h_{tx}_{tz}.r16"), h);
            WriteTile(Path.Combine(outDir, "tiles", $"w_{tx}_{tz}.r16"), w);
            lowest = Math.Min(lowest, tLow); highest = Math.Max(highest, tHigh); wet += tWet; clipped += tClip;
            Console.Write($"\r  tiles {tz * tilesPerSide + tx + 1}/{tilesPerSide * tilesPerSide}");
        }
        Console.WriteLine();
        long samples = (long)tilesPerSide * tilesPerSide * n * n;
        Log($"  baked {samples / 1e6:0.0} M samples in {bake.Elapsed.TotalSeconds:0.0} s; ground {lowest:0} .. {highest:0} m, " +
            $"{100.0 * wet / samples:0.0}% water{(clipped > 0 ? $", {clipped} samples CLIPPED to the height range" : "")}");

        WritePreview(terrain, x0, z0, recipe.RegionSize, Path.Combine(outDir, "preview.png"));
        WriteOverview(terrain, recipe.Profile.WorldSize, x0, z0, recipe.RegionSize, Path.Combine(outDir, "overview.png"));

        var info = new
        {
            format = "deadrecon.world/1",
            name = recipe.Name,
            bakedFrom = Path.GetFileName(recipePath),
            recipe,
            region = new { size = recipe.RegionSize, centreInLandmass = new[] { cx, cz }, origin = "north-west corner, +x east, +z south, metres" },
            tiles = new { size = recipe.TileSize, spacing = recipe.Spacing, samples = n, perSide = tilesPerSide,
                          height = "tiles/h_{x}_{z}.r16", water = "tiles/w_{x}_{z}.r16" },
            encoding = new { type = "uint16-le", metres = "min + value * step", min = HeightMin, step = HeightStep, waterDry = 0 },
            stats = new { lowest, highest, waterShare = (double)wet / samples, clipped },
        };
        File.WriteAllText(Path.Combine(outDir, "world.json"), JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = true }));
        Log($"Done in {total.Elapsed.TotalSeconds:0.0} s -> {outDir}");
        return 0;
    }

    static ushort Encode(double metres, ref long clipped)
    {
        if (!double.IsFinite(metres)) throw new InvalidDataException("The generator returned a non-finite height; refusing to bake a hole.");
        double v = Math.Round((metres - HeightMin) / HeightStep);
        if (v < 0 || v > ushort.MaxValue) { clipped++; v = Math.Clamp(v, 0, ushort.MaxValue); }
        return (ushort)v;
    }

    static void WriteTile(string path, ushort[] data)
    {
        var bytes = new byte[data.Length * 2];
        Buffer.BlockCopy(data, 0, bytes, 0, bytes.Length); // x86/ARM are little-endian
        if (!BitConverter.IsLittleEndian) throw new PlatformNotSupportedException("Big-endian host.");
        File.WriteAllBytes(path, bytes);
    }

    /// <summary>
    /// Pick the region: entirely dry land (no sea), with as much relief as possible and some river.
    /// Scans the landmass on a coarse grid with summed-area tables.
    /// </summary>
    static (double, double) FindRegion(TerrainSampler terrain, double world, double region)
    {
        const double cell = 512;
        int m = (int)(world / cell), k = (int)Math.Ceiling(region / cell);
        double start = -world / 2 + cell / 2;
        var sea = new double[m * m]; var hgt = new double[m * m]; var hsq = new double[m * m]; var river = new double[m * m];
        Parallel.For(0, m, j =>
        {
            for (int i = 0; i < m; i++)
            {
                var s = terrain.Sample(start + i * cell, start + j * cell, 400);
                bool isSea = s.Land <= 0 || s.Water == WaterKind.Ocean;
                sea[j * m + i] = isSea ? 1 : 0;
                hgt[j * m + i] = s.Height; hsq[j * m + i] = s.Height * s.Height;
                river[j * m + i] = s.Water is WaterKind.River or WaterKind.Lake ? 1 : 0;
            }
        });
        var (Sea, H, H2, R) = (Sat(sea, m), Sat(hgt, m), Sat(hsq, m), Sat(river, m));
        double best = double.NegativeInfinity; int bi = -1, bj = -1;
        for (int j = 0; j + k <= m; j++)
        for (int i = 0; i + k <= m; i++)
        {
            if (Box(Sea, m, i, j, k) > 0) continue;
            double count = k * k, mean = Box(H, m, i, j, k) / count;
            double std = Math.Sqrt(Math.Max(0, Box(H2, m, i, j, k) / count - mean * mean));
            double riverShare = Box(R, m, i, j, k) / count;
            // Relief matters most; a river is a big bonus; very high regions (all mountain) are penalised.
            double score = Math.Min(std, 300) + 250 * Math.Min(riverShare * 25, 1) - Math.Max(0, mean - 900) * .3;
            if (score > best) { best = score; bi = i; bj = j; }
        }
        if (bi < 0) throw new InvalidOperationException("No all-land region of that size: make the continent bigger or the region smaller.");
        return (start + (bi + k / 2.0 - .5) * cell, start + (bj + k / 2.0 - .5) * cell);
    }

    static double[] Sat(double[] a, int m)
    {
        var s = new double[(m + 1) * (m + 1)];
        for (int j = 0; j < m; j++)
        for (int i = 0; i < m; i++)
            s[(j + 1) * (m + 1) + i + 1] = a[j * m + i] + s[j * (m + 1) + i + 1] + s[(j + 1) * (m + 1) + i] - s[j * (m + 1) + i];
        return s;
    }

    static double Box(double[] s, int m, int i, int j, int k)
    {
        int w = m + 1;
        return s[(j + k) * w + i + k] - s[j * w + i + k] - s[(j + k) * w + i] + s[j * w + i];
    }

    static void WritePreview(TerrainSampler terrain, double x0, double z0, double size, string path)
    {
        int px = (int)Math.Min(2048, size / 4);
        double step = size / px;
        var rgb = new byte[px * px * 3];
        var h = new double[(px + 1) * (px + 1)];
        var water = new bool[px * px];
        Parallel.For(0, px + 1, j =>
        {
            for (int i = 0; i <= px; i++)
            {
                var s = terrain.Sample(x0 + i * step, z0 + j * step, step * 2);
                h[j * (px + 1) + i] = s.Height;
                if (i < px && j < px) water[j * px + i] = s.Water != WaterKind.None;
            }
        });
        Shade(rgb, px, px, h, step, water);
        Png.Write(path, px, px, rgb);
    }

    static void WriteOverview(TerrainSampler terrain, double world, double rx0, double rz0, double region, string path)
    {
        const int px = 1024;
        double step = world / px, x0 = -world / 2, z0 = -world / 2;
        var h = new double[(px + 1) * (px + 1)];
        var water = new bool[px * px];
        Parallel.For(0, px + 1, j =>
        {
            for (int i = 0; i <= px; i++)
            {
                var s = terrain.Sample(x0 + i * step, z0 + j * step, step * 2);
                h[j * (px + 1) + i] = s.Land > 0 ? s.Height : Math.Min(s.Height, -1);
                if (!double.IsFinite(s.Height)) Interlocked.Increment(ref _overviewBad);
                if (s.Lava > 0) Interlocked.Increment(ref _overviewLava);
                if (i < px && j < px) water[j * px + i] = s.Water != WaterKind.None;
            }
        });
        if (_overviewBad > 0 || _overviewLava > 0) Log($"  overview: {_overviewBad} non-finite heights, {_overviewLava} lava samples (coarse LOD)");
        var rgb = new byte[px * px * 3];
        Shade(rgb, px, px, h, step, water);
        int a = (int)((rx0 - x0) / step), b = (int)((rz0 - z0) / step), len = (int)(region / step);
        for (int t = 0; t <= len; t++)
            foreach (var (x, y) in new[] { (a + t, b), (a + t, b + len), (a, b + t), (a + len, b + t) })
                for (int d = -1; d <= 1; d++)
                {
                    int xx = Math.Clamp(x + (y == b || y == b + len ? 0 : d), 0, px - 1), yy = Math.Clamp(y + (y == b || y == b + len ? d : 0), 0, px - 1);
                    int o = (yy * px + xx) * 3; rgb[o] = 230; rgb[o + 1] = 40; rgb[o + 2] = 40;
                }
        Png.Write(path, px, px, rgb);
    }

    /// <summary>Simple hypsometric tint + north-west hillshade. h has (w+1) x (hgt+1) samples.</summary>
    static void Shade(byte[] rgb, int w, int hgt, double[] h, double step, bool[] water)
    {
        for (int j = 0; j < hgt; j++)
        for (int i = 0; i < w; i++)
        {
            double a = h[j * (w + 1) + i], dx = h[j * (w + 1) + i + 1] - a, dz = h[(j + 1) * (w + 1) + i] - a;
            double nx = -dx / step, nz = -dz / step, len = Math.Sqrt(nx * nx + nz * nz + 1);
            double light = Math.Clamp((nx * -.6 + nz * -.6 + 1) / len * .85 + .15, 0, 1.2);
            (double r, double g, double b) col;
            if (water[j * w + i]) col = a < 0 && !double.IsNaN(a) ? (40, 80, 130) : (60, 120, 190);
            else if (a < 0) col = (40, 80, 130);
            else if (a < 150) col = (70, 120, 55);
            else if (a < 500) col = (90, 130, 60);
            else if (a < 1000) col = (120, 125, 75);
            else if (a < 1600) col = (130, 115, 95);
            else col = (165, 160, 155);
            double f = water[j * w + i] ? 1 : light;
            int o = (j * w + i) * 3;
            rgb[o] = (byte)Math.Clamp(col.r * f, 0, 255); rgb[o + 1] = (byte)Math.Clamp(col.g * f, 0, 255); rgb[o + 2] = (byte)Math.Clamp(col.b * f, 0, 255);
        }
    }

    static long _overviewBad, _overviewLava;

    static void Log(string message) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
}
