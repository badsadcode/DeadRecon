using System.Text.Json;

/// <summary>
/// River-first rivers, the way badsadcode/river builds them (Tom: "it's nothing like in River Game"). The generator's
/// rivers reach the tiles as a 2 m grid of wet samples with a stepped surface (reach 50 by the first home drops in 52
/// steps), in narrow cuts that don't follow a bed. Here, from each reach's centre line (rivers.json):
/// <list type="bullet">
/// <item>the line is resampled every 2 m (Catmull-Rom), its surface smoothed along it (~60 m) and kept running
/// downhill, its width at least 5 m (1.4x the generator's) and depth 1-3 m;</item>
/// <item>the ground is carved to River's cross-section: a rounded bed under the water, a low bank easing up 0.5 m
/// over 3 m, then back into the land over 14 m;</item>
/// <item>water is exactly the channel (the old stepped water near the river is removed);</item>
/// <item>river_lines.json gets the processed lines, so the game builds its water ribbons from the same numbers.</item>
/// </list>
/// </summary>
static class RiverCarver
{
    public sealed record Line(int Id, float[] X, float[] Z, float[] S, float[] W, float[] D);

    const float Bank = 3, Blend = 14;

    public static List<Line> Load(string dir)
    {
        var path = Path.Combine(dir, "rivers.json");
        var lines = new List<Line>();
        if (!File.Exists(path)) return lines;
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var r in doc.RootElement.GetProperty("reaches").EnumerateArray())
        {
            var pts = r.GetProperty("pts").EnumerateArray().Select(p => p.EnumerateArray().Select(v => v.GetDouble()).ToArray()).ToList();
            if (pts.Count < 3) continue;
            lines.Add(Resample(r.GetProperty("id").GetInt32(), pts));
        }
        return lines;
    }

    /// <summary>Every 2 m along a Catmull-Rom spline through the points; surface smoothed and monotone.</summary>
    static Line Resample(int id, List<double[]> p)
    {
        var xs = new List<float>(); var zs = new List<float>(); var ss = new List<float>(); var ws = new List<float>(); var ds = new List<float>();
        double[] At(int i) => p[Math.Clamp(i, 0, p.Count - 1)];
        for (int i = 0; i < p.Count - 1; i++)
        {
            var a = At(i - 1); var b = At(i); var c = At(i + 1); var d = At(i + 2);
            double len = Math.Sqrt((c[0] - b[0]) * (c[0] - b[0]) + (c[1] - b[1]) * (c[1] - b[1]));
            int n = Math.Max(1, (int)Math.Round(len / 2));
            for (int k = 0; k < n; k++)
            {
                double t = k / (double)n, t2 = t * t, t3 = t2 * t;
                double Cr(int j) => .5 * (2 * b[j] + (-a[j] + c[j]) * t + (2 * a[j] - 5 * b[j] + 4 * c[j] - d[j]) * t2 + (-a[j] + 3 * b[j] - 3 * c[j] + d[j]) * t3);
                double Lin(int j) => b[j] + (c[j] - b[j]) * t;
                xs.Add((float)Cr(0)); zs.Add((float)Cr(1)); ss.Add((float)Lin(2));
                ws.Add((float)Math.Max(5, Lin(3) * 1.4)); ds.Add((float)Math.Clamp(Lin(4), 1, 3));
            }
        }
        var last = p[^1];
        xs.Add((float)last[0]); zs.Add((float)last[1]); ss.Add((float)last[2]); ws.Add((float)Math.Max(5, last[3] * 1.4)); ds.Add((float)Math.Clamp(last[4], 1, 3));
        // Smooth the surface over ±15 samples (30 m each way), then never let it rise.
        var s = ss.ToArray(); var sm = new float[s.Length];
        for (int i = 0; i < s.Length; i++)
        {
            double sum = 0; int cnt = 0;
            for (int j = Math.Max(0, i - 15); j <= Math.Min(s.Length - 1, i + 15); j++) { sum += s[j]; cnt++; }
            sm[i] = (float)(sum / cnt);
        }
        sm[0] = Math.Max(sm[0], s[0] - .2f);
        for (int i = 1; i < sm.Length; i++) sm[i] = Math.Min(sm[i], sm[i - 1] - .002f);
        // Width and depth change slowly too.
        float[] Smooth(List<float> v) { var o = new float[v.Count]; for (int i = 0; i < v.Count; i++) { float sum = 0; int c = 0; for (int j = Math.Max(0, i - 10); j <= Math.Min(v.Count - 1, i + 10); j++) { sum += v[j]; c++; } o[i] = sum / c; } return o; }
        return new Line(id, xs.ToArray(), zs.ToArray(), sm, Smooth(ws), Smooth(ds));
    }

    /// <summary>Nearest point of any line within reach: (line, fractional index, distance).</summary>
    sealed class Index
    {
        readonly Dictionary<(int, int), List<(int L, int I)>> _cells = new();
        readonly List<Line> _lines;
        const float Cell = 16;
        public Index(List<Line> lines)
        {
            _lines = lines;
            for (int l = 0; l < lines.Count; l++)
                for (int i = 0; i < lines[l].X.Length - 1; i++)
                {
                    var key = ((int)Math.Floor(lines[l].X[i] / Cell), (int)Math.Floor(lines[l].Z[i] / Cell));
                    if (!_cells.TryGetValue(key, out var list)) _cells[key] = list = new();
                    list.Add((l, i));
                }
        }
        public bool Nearest(float x, float z, float reach, out int line, out float at, out float dist)
        {
            line = -1; at = 0; dist = float.MaxValue;
            int r = (int)Math.Ceiling(reach / Cell) + 1, cx = (int)Math.Floor(x / Cell), cz = (int)Math.Floor(z / Cell);
            for (int j = cz - r; j <= cz + r; j++)
            for (int i = cx - r; i <= cx + r; i++)
            {
                if (!_cells.TryGetValue((i, j), out var list)) continue;
                foreach (var (l, k) in list)
                {
                    var L = _lines[l];
                    float ax = L.X[k], az = L.Z[k], bx = L.X[k + 1], bz = L.Z[k + 1];
                    float dx = bx - ax, dz = bz - az, len2 = dx * dx + dz * dz;
                    float t = len2 > 0 ? Math.Clamp(((x - ax) * dx + (z - az) * dz) / len2, 0, 1) : 0;
                    float px = ax + dx * t - x, pz = az + dz * t - z, d = MathF.Sqrt(px * px + pz * pz);
                    if (d < dist) { dist = d; line = l; at = k + t; }
                }
            }
            return line >= 0 && dist <= reach;
        }
    }

    static float Lerp(float[] v, float at) { int i = Math.Min((int)at, v.Length - 2); float t = at - i; return v[i] + (v[i + 1] - v[i]) * t; }
    static float Smooth01(float x) { x = Math.Clamp(x, 0, 1); return x * x * (3 - 2 * x); }

    /// <summary>Carve the channels into H, set the water, write river_lines.json. S x S samples, (0,0) at -half.</summary>
    public static void Carve(string dir, List<Line> lines, float[] H, bool[] wet, float[] level, int S, double spacing, double half)
    {
        if (lines.Count == 0) { Settler.Say("  rivers: no rivers.json (run: WorldBaker rivers <recipe> <dir>)"); return; }
        var index = new Index(lines);
        float maxW = lines.Max(l => l.W.Max());
        float reach = maxW / 2 + Bank + Blend;
        int carved = 0, water = 0, dried = 0;
        Parallel.For(0, S, gz =>
        {
            for (int gx = 0; gx < S; gx++)
            {
                float x = (float)(gx * spacing - half), z = (float)(gz * spacing - half);
                if (!index.Nearest(x, z, reach, out int l, out float at, out float dist)) continue;
                var L = lines[l];
                float sf = Lerp(L.S, at), hw = Lerp(L.W, at) / 2, depth = Lerp(L.D, at);
                if (dist > hw + Bank + Blend) continue;
                int k = gz * S + gx;
                float h0 = H[k], h;
                if (dist < hw)
                {
                    float u = dist / hw;
                    h = sf - .05f - depth * MathF.Pow(1 - u * u, .8f);       // River's rounded bed
                    wet[k] = true; level[k] = sf;
                    Interlocked.Increment(ref water);
                }
                else
                {
                    float e = dist - hw;
                    float bankTop = sf - .05f + .55f * Smooth01(e / Bank);
                    if (e <= Bank) h = bankTop;
                    else
                    {
                        float w = Smooth01((e - Bank) / Blend);
                        h = bankTop + (h0 - bankTop) * w;
                    }
                    // The old stepped water near the river goes (never the sea or a lake well below/above it).
                    if (wet[k] && Math.Abs(level[k] - sf) < 6) { wet[k] = false; level[k] = float.NaN; Interlocked.Increment(ref dried); }
                }
                H[k] = h;
                Interlocked.Increment(ref carved);
            }
        });
        var outLines = lines.Select(L => new { id = L.Id, x = L.X, z = L.Z, s = L.S, w = L.W, d = L.D });
        File.WriteAllText(Path.Combine(dir, "river_lines.json"), JsonSerializer.Serialize(new { format = "deadrecon.riverlines/1", note = "every 2 m: x, z (game coords), s surface, w width, d depth (m)", lines = outLines }));
        Settler.Say($"  rivers: {lines.Count} lines, {lines.Sum(l => l.X.Length) * 2 / 1000.0:0.0} km; {carved} samples carved, {water} water, {dried} old water samples removed");
    }
}
