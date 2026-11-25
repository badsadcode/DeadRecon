// Settler: the "people were here" pass over a baked world.
//
//   dotnet run -c Release --project tools/WorldBaker -- settle world/jungle
//   (also runs automatically at the end of a full bake)
//
// Reads the baked height/water tiles and:
//   1. picks settlement sites (a market town, villages, hamlets) on flat dry ground near water, away from each other,
//   2. routes roads and tracks between them (A* over the heightmap: gentle grades, few river crossings, merging into
//      roads that already exist),
//   3. places checkpoints (outposts) on the roads, away from the villages,
//   4. cuts and fills the ground: level pads for sites, smooth corridors for roads (rivers keep their channel: fords),
//   5. lays out fields round each settlement: rice paddies terraced into the slopes near water, wheat on dry ground,
//   6. writes settlements.json (sites, roads, crossings), settle_mask.png (R = road surface, G = settlement ground,
//      B = field: 255 paddy, 128 wheat; one pixel per height sample) and settlements.png (a map, for people), and rewrites the height tiles.
//
// The untouched heights are kept in tiles_raw/ so settling again starts from the generator's ground, not from the
// previous settle. A full bake deletes tiles_raw/.
//
// Coordinates written to settlements.json are the game's: region centred on the origin, +x east, +z south, y up, metres.

using System.Text.Json;

static class Settler
{
    sealed record Site(string Kind, string Name, double X, double Z, double Radius) { public double Y; }
    sealed record Field(int Site, string Kind, string Stage, double X, double Z, double Yaw, double W, double D) { public double Level; }
    sealed record Road(string Kind, double Width, List<(double X, double Z)> Points) { public double[] Profile = Array.Empty<double>(); }

    public static void Run(string dir)
    {
        using var info = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "world.json")));
        var root = info.RootElement;
        var tiles = root.GetProperty("tiles");
        var enc = root.GetProperty("encoding");
        int seed = root.GetProperty("recipe").GetProperty("Seed").GetInt32();
        double spacing = tiles.GetProperty("spacing").GetDouble();
        int n = tiles.GetProperty("samples").GetInt32(), per = tiles.GetProperty("perSide").GetInt32();
        double hMin = enc.GetProperty("min").GetDouble(), hStep = enc.GetProperty("step").GetDouble();
        int S = per * (n - 1) + 1;
        double half = (S - 1) * spacing / 2;

        // ---- load (from the untouched copy)
        string raw = Path.Combine(dir, "tiles_raw");
        if (!Directory.Exists(raw))
        {
            Directory.CreateDirectory(raw);
            foreach (var f in Directory.GetFiles(Path.Combine(dir, "tiles"), "h_*.r16")) File.Copy(f, Path.Combine(raw, Path.GetFileName(f)));
        }
        var H = new float[S * S];
        var wet = new bool[S * S];
        for (int tz = 0; tz < per; tz++)
        for (int tx = 0; tx < per; tx++)
        {
            var hb = File.ReadAllBytes(Path.Combine(raw, $"h_{tx}_{tz}.r16"));
            var wb = File.ReadAllBytes(Path.Combine(dir, "tiles", $"w_{tx}_{tz}.r16"));
            for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                int o = (j * n + i) * 2, g = (tz * (n - 1) + j) * S + tx * (n - 1) + i;
                H[g] = (float)(hMin + BitConverter.ToUInt16(hb, o) * hStep);
                wet[g] = BitConverter.ToUInt16(wb, o) != 0;
            }
        }
        Log($"Settling {dir}: {S}x{S} samples, {spacing} m apart");
        var rng = new Random(seed * 7919 + 17);

        // ---- coarse cells (16 m) for site picking
        const int C = 8;                          // samples per cell
        int G = (S - 1) / C;
        double cellM = C * spacing;
        var ch = new double[G * G];
        var cwet = new double[G * G];
        for (int j = 0; j < G; j++)
        for (int i = 0; i < G; i++)
        {
            double sum = 0; int cnt = 0, w = 0;
            for (int b = 0; b < C; b += 2)
            for (int a = 0; a < C; a += 2)
            {
                int g = (j * C + b) * S + i * C + a;
                sum += H[g]; cnt++; if (wet[g]) w++;
            }
            ch[j * G + i] = sum / cnt; cwet[j * G + i] = w > 0 ? 1 : 0;
        }
        var distWater = new int[G * G];
        Array.Fill(distWater, int.MaxValue);
        var queue = new Queue<int>();
        for (int k = 0; k < G * G; k++) if (cwet[k] > 0) { distWater[k] = 0; queue.Enqueue(k); }
        while (queue.Count > 0)
        {
            int k = queue.Dequeue(), x = k % G, z = k / G;
            foreach (var (dx, dz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
            {
                int xx = x + dx, zz = z + dz;
                if (xx < 0 || zz < 0 || xx >= G || zz >= G) continue;
                int q = zz * G + xx;
                if (distWater[q] > distWater[k] + 1) { distWater[q] = distWater[k] + 1; queue.Enqueue(q); }
            }
        }
        var satH = Sat(ch, G); var satH2 = Sat(Array.ConvertAll(ch, v => v * v), G); var satW = Sat(cwet, G);
        double Rough(int i, int j, int r)
        {
            int k = 2 * r + 1, a = i - r, b = j - r;
            if (a < 0 || b < 0 || a + k > G || b + k > G) return double.MaxValue;
            double cnt = k * k, mean = Box(satH, G, a, b, k) / cnt;
            return Math.Sqrt(Math.Max(0, Box(satH2, G, a, b, k) / cnt - mean * mean));
        }
        bool DryAround(int i, int j, int r)
        {
            int k = 2 * r + 1, a = i - r, b = j - r;
            return a >= 0 && b >= 0 && a + k <= G && b + k <= G && Box(satW, G, a, b, k) == 0;
        }
        (double X, double Z) CellCentre(int i, int j) => ((i + .5) * cellM, (j + .5) * cellM);   // region metres (NW origin)

        // ---- sites
        var sites = new List<Site>();
        var names = new HashSet<string>();
        int margin = (int)(320 / cellM);
        double centre = (S - 1) * spacing / 2;
        void Pick(string kind, int count, int r, double radius, double minGap, double minGapAny, double idealWater, double maxRough,
                  Func<double, double, bool>? where = null)
        {
            var cands = new List<(double score, int i, int j)>();
            for (int j = margin; j < G - margin; j++)
            for (int i = margin; i < G - margin; i++)
            {
                double dw = distWater[j * G + i] * cellM;
                if (dw < radius * .9 + 15 || dw > (kind == "hamlet" ? 1800 : 1300)) continue;   // far from rivers: streams and wells we don't model
                if (!DryAround(i, j, r)) continue;
                double rough = Rough(i, j, r);
                if (rough > maxRough) continue;
                var (x, z) = CellCentre(i, j);
                if (where != null && !where(x, z)) continue;
                double score = -rough * 1.5 - Math.Abs(dw - idealWater) / 60 + rng.NextDouble() * 1.5;
                cands.Add((score, i, j));
            }
            cands.Sort((a, b) => b.score.CompareTo(a.score));
            int placed = 0;
            foreach (var (_, i, j) in cands)
            {
                if (placed >= count) break;
                var (x, z) = CellCentre(i, j);
                bool ok = true;
                foreach (var s in sites)
                {
                    double d = Math.Sqrt((s.X - x) * (s.X - x) + (s.Z - z) * (s.Z - z));
                    double gap = s.Kind == kind || (s.Kind != "hamlet" && kind != "hamlet") ? minGap : 0;
                    if (d < Math.Max(gap, minGapAny + s.Radius + radius)) { ok = false; break; }
                }
                if (!ok) continue;
                sites.Add(new Site(kind, MakeName(rng, names, kind), x, z, radius));
                placed++;
            }
            Log($"  {placed}/{count} {kind} sites ({cands.Count} candidates)");
        }
        double D(double x, double z) => Math.Sqrt((x - centre) * (x - centre) + (z - centre) * (z - centre));
        Pick("town", 1, 5, 105, 0, 0, 120, 6, (x, z) => D(x, z) > 1400);
        // A village a short walk from where operations start (the middle of the map), so new players find one.
        Pick("village", 1, 3, 55, 900, 200, 140, 7, (x, z) => D(x, z) is > 500 and < 1100);
        Pick("village", 7, 3, 55, 1000, 200, 140, 7);
        Pick("hamlet", 22, 2, 30, 420, 200, 100, 8);

        // ---- roads: a spanning tree of the settlements plus some loops, routed over the ground
        var nodes = sites.ToList();
        var edges = new List<(int a, int b, double d)>();
        for (int a = 0; a < nodes.Count; a++)
        for (int b = a + 1; b < nodes.Count; b++)
        {
            double d = Math.Sqrt(Math.Pow(nodes[a].X - nodes[b].X, 2) + Math.Pow(nodes[a].Z - nodes[b].Z, 2));
            if (d < 6000) edges.Add((a, b, d));
        }
        edges.Sort((p, q) => p.d.CompareTo(q.d));
        var parent = Enumerable.Range(0, nodes.Count).ToArray();
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        var chosen = new List<(int a, int b, double d)>();
        foreach (var e in edges)
        {
            int ra = Find(e.a), rb = Find(e.b);
            if (ra == rb) continue;
            parent[ra] = rb; chosen.Add(e);
        }
        // Loops: if going round through the tree is much longer than a direct road, build the direct road too.
        foreach (var e in edges)
        {
            if (e.d > 2200 || chosen.Contains(e)) continue;
            if (nodes[e.a].Kind == "hamlet" && nodes[e.b].Kind == "hamlet") continue;
            double via = GraphDistance(nodes.Count, chosen, e.a, e.b);
            if (via > 2.0 * e.d) chosen.Add(e);
        }
        bool Major(Site s) => s.Kind is "town" or "village";
        chosen.Sort((p, q) =>
        {
            int mp = Major(nodes[p.a]) && Major(nodes[p.b]) ? 0 : 1, mq = Major(nodes[q.a]) && Major(nodes[q.b]) ? 0 : 1;
            return mp != mq ? mp.CompareTo(mq) : p.d.CompareTo(q.d);
        });

        const int R = 4;                                // samples per routing node (8 m)
        int GR = (S - 1) / R + 1;
        var used = new bool[GR * GR];
        var roads = new List<Road>();
        foreach (var e in chosen)
        {
            var A = nodes[e.a]; var B = nodes[e.b];
            bool major = Major(A) && Major(B);
            var path = Route(H, wet, S, R, GR, spacing, used, A, B);
            if (path == null) { Log($"  no route {A.Name} -> {B.Name}"); continue; }
            foreach (int k in path) used[k] = true;
            var pts = path.Select(k => ((k % GR) * R * spacing, (k / GR) * R * spacing)).ToList();
            pts = Chaikin(Chaikin(Chaikin(Simplify(pts))));
            roads.Add(new Road(major ? "road" : "track", major ? 5.5 : 3.2, Resample(pts, 4)));
        }
        Log($"  {roads.Count} roads/tracks, {roads.Sum(r => Length(r.Points)) / 1000:0.0} km");

        // ---- checkpoints on the roads, away from villages
        int outposts = 0;
        foreach (var road in roads.OrderBy(r => r.Kind == "road" ? 0 : 1).ThenBy(_ => rng.Next()))
        {
            if (outposts >= 8) break;
            var p = road.Points;
            for (int k = p.Count / 8; k < p.Count * 7 / 8; k += 3)
            {
                var (x, z) = p[k];
                if (sites.Any(s => Math.Sqrt((s.X - x) * (s.X - x) + (s.Z - z) * (s.Z - z)) < (s.Kind == "outpost" ? 1100 : s.Radius + 220))) continue;
                int i = (int)(x / cellM), j = (int)(z / cellM);
                if (Rough(i, j, 1) > 4 || !DryAround(i, j, 1)) continue;
                sites.Add(new Site("outpost", MakeName(rng, names, "outpost"), x, z, 20));
                outposts++;
                break;
            }
        }
        Log($"  {outposts} checkpoints");

        // ---- cut and fill: site pads first, then roads over the levelled ground
        var mask = new byte[S * S * 3];
        double Bil(double x, double z)
        {
            double fx = x / spacing, fz = z / spacing;
            int ix = Math.Clamp((int)fx, 0, S - 2), iz = Math.Clamp((int)fz, 0, S - 2);
            double u = fx - ix, v = fz - iz;
            return (H[iz * S + ix] * (1 - u) + H[iz * S + ix + 1] * u) * (1 - v) + (H[(iz + 1) * S + ix] * (1 - u) + H[(iz + 1) * S + ix + 1] * u) * v;
        }
        foreach (var s in sites.Where(s => s.Kind != "outpost"))
        {
            var level = new List<double>();
            Around(s.X, s.Z, s.Radius * .7, spacing, S, (g, d) => { if (!wet[g]) level.Add(H[g]); });
            level.Sort();
            s.Y = level.Count > 0 ? level[level.Count / 2] : Bil(s.X, s.Z);
            Around(s.X, s.Z, s.Radius * 1.4, spacing, S, (g, d) =>
            {
                if (wet[g]) return;
                double w = 1 - Smooth(s.Radius * .85, s.Radius * 1.4, d);
                H[g] = (float)(H[g] + (s.Y - H[g]) * w);
                double yard = 1 - Smooth(s.Radius * .22, s.Radius * .5, d);   // the plaza and its edges; grass between the houses
                mask[g * 3 + 1] = (byte)Math.Max(mask[g * 3 + 1], (int)(yard * 255));
            });
        }
        var roadW = new float[S * S];
        var roadT = new float[S * S];
        var crossings = new List<(double X, double Z, string Kind)>();
        foreach (var road in roads)
        {
            var p = road.Points;
            var prof = p.Select(q => Bil(q.X, q.Z)).ToArray();
            var orig = (double[])prof.Clone();
            for (int pass = 0; pass < 3; pass++) prof = MovingAverage(prof, 7);
            for (int k = 0; k < prof.Length; k++) prof[k] = Math.Clamp(prof[k], orig[k] - 3.5, orig[k] + 3.5);
            prof = MovingAverage(prof, 2);
            road.Profile = prof;
            // Fords: where the corridor crosses water.
            bool inWater = false; int start = 0;
            for (int k = 0; k < p.Count; k++)
            {
                int g = Math.Clamp((int)Math.Round(p[k].Z / spacing), 0, S - 1) * S + Math.Clamp((int)Math.Round(p[k].X / spacing), 0, S - 1);
                if (wet[g] && !inWater) { inWater = true; start = k; }
                if (!wet[g] && inWater)
                {
                    inWater = false;
                    var m = p[(start + k) / 2];
                    crossings.Add((m.X, m.Z, road.Kind == "road" ? "bridge" : "ford"));
                }
            }
            double hw = road.Width / 2, shoulder = road.Kind == "road" ? 6 : 4;
            for (int k = 0; k + 1 < p.Count; k++)
            {
                var (ax, az) = p[k]; var (bx, bz) = p[k + 1];
                double ext = hw + shoulder;
                int i0 = Math.Max(0, (int)((Math.Min(ax, bx) - ext) / spacing)), i1 = Math.Min(S - 1, (int)((Math.Max(ax, bx) + ext) / spacing) + 1);
                int j0 = Math.Max(0, (int)((Math.Min(az, bz) - ext) / spacing)), j1 = Math.Min(S - 1, (int)((Math.Max(az, bz) + ext) / spacing) + 1);
                double vx = bx - ax, vz = bz - az, len2 = Math.Max(1e-6, vx * vx + vz * vz);
                for (int j = j0; j <= j1; j++)
                for (int i = i0; i <= i1; i++)
                {
                    double x = i * spacing, z = j * spacing;
                    double t = Math.Clamp(((x - ax) * vx + (z - az) * vz) / len2, 0, 1);
                    double dx = x - (ax + vx * t), dz = z - (az + vz * t), d = Math.Sqrt(dx * dx + dz * dz);
                    if (d > ext) continue;
                    int g = j * S + i;
                    double w = d <= hw + .5 ? 1 : 1 - Smooth(hw + .5, ext, d);
                    if (w > roadW[g]) { roadW[g] = (float)w; roadT[g] = (float)(prof[k] + (prof[k + 1] - prof[k]) * t); }
                    // The visible surface: soft edge about a metre wide.
                    double surf = 1 - Smooth(hw - .8, hw + .8, d);
                    mask[g * 3] = (byte)Math.Max(mask[g * 3], (int)(surf * 255));
                }
            }
        }
        for (int g = 0; g < S * S; g++) if (roadW[g] > 0 && !wet[g]) H[g] += (roadT[g] - H[g]) * roadW[g];
        foreach (var s in sites.Where(s => s.Kind == "outpost"))
        {
            s.Y = Bil(s.X, s.Z);
            Around(s.X, s.Z, s.Radius * 1.5, spacing, S, (g, d) =>
            {
                if (wet[g]) return;
                double w = 1 - Smooth(s.Radius * .8, s.Radius * 1.5, d);
                H[g] += (float)((s.Y - H[g]) * w * (1 - roadW[g]));   // the road through it is already level
                double yard = 1 - Smooth(s.Radius * .6, s.Radius * 1.05, d);
                mask[g * 3 + 1] = (byte)Math.Max(mask[g * 3 + 1], (int)(yard * 200));
            });
        }
        Log($"  {crossings.Count} river crossings");

        // ---- fields: a patchwork of rectangles in a ring round each settlement
        var fields = new List<Field>();
        var occupied = new bool[S * S];
        for (int si = 0; si < sites.Count; si++)
        {
            var st = sites[si];
            if (st.Kind == "outpost") continue;
            int want = st.Kind switch { "town" => rng.Next(22, 32), "village" => rng.Next(12, 20), _ => rng.Next(5, 9) };
            int ci = Math.Clamp((int)(st.X / cellM), 0, G - 1), cj = Math.Clamp((int)(st.Z / cellM), 0, G - 1);
            bool riverside = distWater[cj * G + ci] * cellM < 700;
            var mine = new List<Field>();
            double yaw0 = rng.NextDouble() * Math.PI;
            int got = 0;
            for (int attempt = 0; attempt < want * 40 && got < want; attempt++)
            {
                double w = 20 + rng.NextDouble() * 18, dd = 14 + rng.NextDouble() * 12, cx, cz, yaw;
                if (mine.Count > 0 && rng.NextDouble() < .75)
                {
                    // Patchwork: next to a field we already have, same orientation, a bund's width apart.
                    var nb = mine[rng.Next(mine.Count)];
                    yaw = nb.Yaw;
                    double na = Math.Cos(yaw), nsn = Math.Sin(yaw);
                    int side = rng.Next(4);
                    double off = side < 2 ? (nb.W + w) / 2 + 2.6 : (nb.D + dd) / 2 + 2.6, slide = (rng.NextDouble() - .5) * 6;
                    double u = side == 0 ? off : side == 1 ? -off : slide, v = side == 2 ? off : side == 3 ? -off : slide;
                    if (side < 2) dd = nb.D; else w = nb.W;   // rows line up
                    cx = nb.X + u * na - v * nsn; cz = nb.Z + u * nsn + v * na;
                }
                else
                {
                    double ang = rng.NextDouble() * Math.Tau, rad = st.Radius * 1.45 + 10 + rng.NextDouble() * (st.Kind == "town" ? 220 : st.Kind == "village" ? 150 : 90);
                    cx = st.X + Math.Cos(ang) * rad; cz = st.Z + Math.Sin(ang) * rad;
                    yaw = yaw0 + (rng.NextDouble() < .25 ? (rng.NextDouble() - .5) * .5 : 0);
                }
                double ca = Math.Cos(yaw), sa = Math.Sin(yaw);
                // Look at every sample in the rectangle (+ a margin): dry, off roads, free, not too steep.
                double ext = Math.Sqrt(w * w + dd * dd) / 2 + 3;
                double lo = double.MaxValue, hi = double.MinValue, sum = 0; int cnt = 0; bool ok = true;
                Around(cx, cz, ext, spacing, S, (g, _) =>
                {
                    if (!ok) return;
                    double x = (g % S) * spacing - cx, z = (g / S) * spacing - cz;
                    double u = x * ca + z * sa, v = -x * sa + z * ca;
                    if (Math.Abs(u) > w / 2 + 1.2 || Math.Abs(v) > dd / 2 + 1.2) return;
                    if (wet[g] || occupied[g] || roadW[g] > 0 || mask[g * 3] > 0 || mask[g * 3 + 1] > 40) { ok = false; return; }
                    if (Math.Abs(u) <= w / 2 && Math.Abs(v) <= dd / 2) { lo = Math.Min(lo, H[g]); hi = Math.Max(hi, H[g]); sum += H[g]; cnt++; }
                });
                if (!ok || cnt == 0) continue;
                if (sites.Any(o => o != st && Math.Sqrt((o.X - cx) * (o.X - cx) + (o.Z - cz) * (o.Z - cz)) < o.Radius * 1.4 + ext)) continue;
                // A cluster keeps its crop: paddies next to paddies (they share water), wheat next to wheat.
                string near = mine.Count > 0 ? mine.MinBy(m => (m.X - cx) * (m.X - cx) + (m.Z - cz) * (m.Z - cz))!.Kind : "";
                bool wantPaddy = near != "" ? near == "paddy" : riverside && rng.NextDouble() < .8;
                string kind = wantPaddy && hi - lo < 3.5 ? "paddy" : hi - lo < 5 ? "wheat" : "";
                if (kind == "") continue;
                string stage = kind == "paddy"
                    ? (rng.NextDouble() switch { < .45 => "young", < .85 => "mature", _ => "flooded" })
                    : (rng.NextDouble() < .75 ? "ripe" : "green");
                var f = new Field(si, kind, stage, cx, cz, yaw, Math.Round(w, 1), Math.Round(dd, 1)) { Level = kind == "paddy" ? sum / cnt - .15 : sum / cnt };
                fields.Add(f); mine.Add(f); got++;
                Around(cx, cz, ext, spacing, S, (g, _) =>
                {
                    double x = (g % S) * spacing - cx, z = (g / S) * spacing - cz;
                    double u = x * ca + z * sa, v = -x * sa + z * ca;
                    double du = Math.Max(Math.Abs(u) - w / 2, 0), dv = Math.Max(Math.Abs(v) - dd / 2, 0), out_ = Math.Sqrt(du * du + dv * dv);
                    if (out_ > 3) return;
                    if (out_ <= 1.0) occupied[g] = true;
                    if (kind == "paddy")
                    {
                        // Terrace: the paddy floor is level; the ground eases back to the slope over 2.5 m outside the bund.
                        double wgt = 1 - Smooth(.3, 2.5, out_);
                        H[g] += (float)((f.Level - H[g]) * wgt);
                        if (out_ <= .4) mask[g * 3 + 2] = 255;
                    }
                    else if (out_ <= .8) mask[g * 3 + 2] = (byte)Math.Max(mask[g * 3 + 2], (int)(128 * (1 - Smooth(0, .8, out_))));
                });
            }
        }
        Log($"  {fields.Count} fields ({fields.Count(f => f.Kind == "paddy")} rice paddies, {fields.Count(f => f.Kind == "wheat")} wheat)");

        // ---- write
        for (int tz = 0; tz < per; tz++)
        for (int tx = 0; tx < per; tx++)
        {
            var bytes = new byte[n * n * 2];
            for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                double v = Math.Clamp(Math.Round((H[(tz * (n - 1) + j) * S + tx * (n - 1) + i] - hMin) / hStep), 0, ushort.MaxValue);
                BitConverter.TryWriteBytes(bytes.AsSpan((j * n + i) * 2), (ushort)v);
            }
            File.WriteAllBytes(Path.Combine(dir, "tiles", $"h_{tx}_{tz}.r16"), bytes);
        }
        Png.Write(Path.Combine(dir, "settle_mask.png"), S, S, mask);
        var json = new
        {
            format = "deadrecon.settlements/1",
            note = "Game coordinates: region centred on the origin, +x east, +z south, y up, metres. Roads: x,y,z triples.",
            sites = sites.Select(s => new { kind = s.Kind, name = s.Name, x = Math.Round(s.X - half, 2), y = Math.Round(s.Y, 2), z = Math.Round(s.Z - half, 2), radius = s.Radius }),
            roads = roads.Select(r => new
            {
                kind = r.Kind, width = r.Width,
                points = r.Points.SelectMany((q, k) => new[] { Math.Round(q.X - half, 2), Math.Round(r.Profile[k], 2), Math.Round(q.Z - half, 2) }),
            }),
            fields = fields.Select(f => new
            {
                site = f.Site, kind = f.Kind, stage = f.Stage, x = Math.Round(f.X - half, 2), z = Math.Round(f.Z - half, 2),
                // Godot rotation about +Y; the field's local x spans w, local z spans d.
                rotation = Math.Round(-f.Yaw, 4), w = f.W, d = f.D, level = Math.Round(f.Level, 2),
            }),
            crossings = crossings.Select(c => new { kind = c.Kind, x = Math.Round(c.X - half, 2), z = Math.Round(c.Z - half, 2) }),
        };
        File.WriteAllText(Path.Combine(dir, "settlements.json"), JsonSerializer.Serialize(json, new JsonSerializerOptions { WriteIndented = true }));
        WriteMap(Path.Combine(dir, "settlements.png"), H, wet, mask, S, spacing, sites);
        Log($"Settled: {sites.Count} sites ({string.Join(", ", sites.GroupBy(s => s.Kind).Select(g => $"{g.Count()} {g.Key}"))})");
    }

    // ================================================================= routing

    static readonly (int dx, int dz)[] Moves =
    {
        (1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (1, -1), (-1, 1), (-1, -1),
        (2, 1), (2, -1), (-2, 1), (-2, -1), (1, 2), (1, -2), (-1, 2), (-1, -2),
    };

    static List<int>? Route(float[] H, bool[] wet, int S, int R, int GR, double spacing, bool[] used, Site a, Site b)
    {
        double step = R * spacing;
        int Node(double x, double z) => Math.Clamp((int)Math.Round(z / step), 0, GR - 1) * GR + Math.Clamp((int)Math.Round(x / step), 0, GR - 1);
        float Hn(int k) => H[(k / GR) * R * S + (k % GR) * R];
        bool Wn(int k) => wet[(k / GR) * R * S + (k % GR) * R];
        int start = Node(a.X, a.Z), goal = Node(b.X, b.Z);
        int gx = goal % GR, gz = goal / GR;
        var cost = new float[GR * GR];
        Array.Fill(cost, float.MaxValue);
        var from = new int[GR * GR];
        var open = new PriorityQueue<int, float>();
        cost[start] = 0; from[start] = -1;
        open.Enqueue(start, 0);
        while (open.TryDequeue(out int k, out float pri))
        {
            if (k == goal) break;
            int x = k % GR, z = k / GR;
            float hk = Hn(k);
            if (pri - (float)(Math.Sqrt((x - gx) * (x - gx) + (z - gz) * (z - gz)) * step * .6) > cost[k] + 1e-3f) continue;
            foreach (var (dx, dz) in Moves)
            {
                int xx = x + dx, zz = z + dz;
                if (xx < 0 || zz < 0 || xx >= GR || zz >= GR) continue;
                int q = zz * GR + xx;
                double len = Math.Sqrt(dx * dx + dz * dz) * step;
                double slope = Math.Abs(Hn(q) - hk) / len;
                double c = len * (1 + Math.Pow(slope / .06, 2));
                if (slope > .35) c *= 8;
                if (Wn(q)) c += Wn(k) ? 60 : 300;             // entering water is the expensive part; staying in it less so
                if (used[q]) c *= .45;                        // follow an existing road
                float nc = cost[k] + (float)c;
                if (nc < cost[q])
                {
                    cost[q] = nc; from[q] = k;
                    open.Enqueue(q, nc + (float)(Math.Sqrt((xx - gx) * (xx - gx) + (zz - gz) * (zz - gz)) * step * .6));
                }
            }
        }
        if (cost[goal] == float.MaxValue) return null;
        var path = new List<int>();
        for (int k = goal; k != -1; k = from[k]) path.Add(k);
        path.Reverse();
        return path;
    }

    static double GraphDistance(int count, List<(int a, int b, double d)> edges, int from, int to)
    {
        var dist = Enumerable.Repeat(double.MaxValue, count).ToArray();
        dist[from] = 0;
        var done = new bool[count];
        for (int it = 0; it < count; it++)
        {
            int u = -1;
            for (int k = 0; k < count; k++) if (!done[k] && (u < 0 || dist[k] < dist[u])) u = k;
            if (u < 0 || dist[u] == double.MaxValue) break;
            done[u] = true;
            foreach (var e in edges)
            {
                int v = e.a == u ? e.b : e.b == u ? e.a : -1;
                if (v >= 0 && dist[u] + e.d < dist[v]) dist[v] = dist[u] + e.d;
            }
        }
        return dist[to];
    }

    static List<(double X, double Z)> Simplify(List<(double X, double Z)> p)
    {
        // Drop points that sit on a straight run; keeps the turns.
        var o = new List<(double, double)> { p[0] };
        for (int k = 1; k + 1 < p.Count; k++)
        {
            var (ax, az) = o[^1]; var (bx, bz) = p[k]; var (cx, cz) = p[k + 1];
            double cross = (bx - ax) * (cz - az) - (bz - az) * (cx - ax);
            if (Math.Abs(cross) > 1e-6 || Math.Sqrt((bx - ax) * (bx - ax) + (bz - az) * (bz - az)) > 40) o.Add(p[k]);
        }
        o.Add(p[^1]);
        return o;
    }

    static List<(double X, double Z)> Chaikin(List<(double X, double Z)> p)
    {
        if (p.Count < 3) return p;
        var o = new List<(double, double)> { p[0] };
        for (int k = 0; k + 1 < p.Count; k++)
        {
            var (ax, az) = p[k]; var (bx, bz) = p[k + 1];
            o.Add((ax * .75 + bx * .25, az * .75 + bz * .25));
            o.Add((ax * .25 + bx * .75, az * .25 + bz * .75));
        }
        o.Add(p[^1]);
        return o;
    }

    static List<(double X, double Z)> Resample(List<(double X, double Z)> p, double every)
    {
        var o = new List<(double, double)> { p[0] };
        double carry = 0;
        for (int k = 0; k + 1 < p.Count; k++)
        {
            var (ax, az) = p[k]; var (bx, bz) = p[k + 1];
            double len = Math.Sqrt((bx - ax) * (bx - ax) + (bz - az) * (bz - az)), t = every - carry;
            while (t <= len) { o.Add((ax + (bx - ax) * t / len, az + (bz - az) * t / len)); t += every; }
            carry = len - (t - every);
        }
        if (o[^1] != p[^1]) o.Add(p[^1]);
        return o;
    }

    static double Length(List<(double X, double Z)> p)
    {
        double l = 0;
        for (int k = 0; k + 1 < p.Count; k++) l += Math.Sqrt(Math.Pow(p[k + 1].X - p[k].X, 2) + Math.Pow(p[k + 1].Z - p[k].Z, 2));
        return l;
    }

    static double[] MovingAverage(double[] a, int r)
    {
        var o = new double[a.Length];
        for (int k = 0; k < a.Length; k++)
        {
            double s = 0; int c = 0;
            for (int d = -r; d <= r; d++) { int q = k + d; if (q >= 0 && q < a.Length) { s += a[q]; c++; } }
            o[k] = s / c;
        }
        // The ends stay where the sites are.
        o[0] = a[0]; o[^1] = a[^1];
        return o;
    }

    // ================================================================= helpers

    static void Around(double x, double z, double radius, double spacing, int S, Action<int, double> each)
    {
        int i0 = Math.Max(0, (int)((x - radius) / spacing)), i1 = Math.Min(S - 1, (int)((x + radius) / spacing) + 1);
        int j0 = Math.Max(0, (int)((z - radius) / spacing)), j1 = Math.Min(S - 1, (int)((z + radius) / spacing) + 1);
        for (int j = j0; j <= j1; j++)
        for (int i = i0; i <= i1; i++)
        {
            double d = Math.Sqrt(Math.Pow(i * spacing - x, 2) + Math.Pow(j * spacing - z, 2));
            if (d <= radius) each(j * S + i, d);
        }
    }

    static double Smooth(double e0, double e1, double x)
    {
        double t = Math.Clamp((x - e0) / (e1 - e0), 0, 1);
        return t * t * (3 - 2 * t);
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

    // Invented place names: two syllables from a small, soft-sounding set. Not any real language on purpose.
    static readonly string[] Onset = { "Ka", "Lo", "Mae", "Sa", "Tham", "Bo", "Ra", "Nong", "Pha", "Ke", "Su", "Dao", "Ha", "Vi", "Ton", "Mu" };
    static readonly string[] Coda = { "lan", "ret", "may", "sung", "kai", "rin", "dok", "som", "vay", "tao", "lim", "pek", "nua", "hoi", "bai", "lor" };

    static string MakeName(Random rng, HashSet<string> used, string kind)
    {
        for (int tries = 0; ; tries++)
        {
            string core = Onset[rng.Next(Onset.Length)] + Coda[rng.Next(Coda.Length)];
            string name = kind switch
            {
                "town" => core,
                "outpost" => $"Checkpoint {core}",
                _ => rng.Next(3) == 0 ? $"{core} {Onset[rng.Next(Onset.Length)]}" : core,
            };
            if (used.Add(name) || tries > 50) return name;
        }
    }

    static void WriteMap(string path, float[] H, bool[] wet, byte[] mask, int S, double spacing, List<Site> sites)
    {
        int step = 2, px = (S - 1) / step;
        var rgb = new byte[px * px * 3];
        for (int j = 0; j < px; j++)
        for (int i = 0; i < px; i++)
        {
            int g = j * step * S + i * step;
            double a = H[g], dx = H[g + step] - a, dz = H[g + step * S] - a;
            double light = Math.Clamp(.75 + (-dx - dz) / (step * spacing) * .9, .25, 1.2);
            (double r, double gg, double b) c = wet[g] ? (60, 110, 180) : (70 * light, 110 * light, 55 * light);
            double road = mask[g * 3] / 255.0, yard = mask[g * 3 + 1] / 255.0;
            c = (c.r + (200 - c.r) * yard * .6, c.gg + (170 - c.gg) * yard * .6, c.b + (120 - c.b) * yard * .6);
            double field = mask[g * 3 + 2] / 255.0;
            if (field > .9) c = (90, 140, 120); else if (field > .3) c = (200, 180, 90);
            c = (c.r + (235 - c.r) * road, c.gg + (205 - c.gg) * road, c.b + (140 - c.b) * road);
            int o = (j * px + i) * 3;
            rgb[o] = (byte)Math.Clamp(c.r, 0, 255); rgb[o + 1] = (byte)Math.Clamp(c.gg, 0, 255); rgb[o + 2] = (byte)Math.Clamp(c.b, 0, 255);
        }
        foreach (var s in sites)
        {
            var col = s.Kind switch { "town" => (255, 230, 80), "village" => (255, 150, 60), "hamlet" => (240, 240, 240), _ => (230, 40, 40) };
            int cx = (int)(s.X / spacing / step), cz = (int)(s.Z / spacing / step), r = Math.Max(4, (int)(s.Radius / spacing / step));
            for (int a = 0; a < 360; a += 2)
            for (int t = -1; t <= 1; t++)
            {
                int x = cx + (int)Math.Round((r + t) * Math.Cos(a * Math.PI / 180)), z = cz + (int)Math.Round((r + t) * Math.Sin(a * Math.PI / 180));
                if (x < 0 || z < 0 || x >= px || z >= px) continue;
                int o = (z * px + x) * 3; rgb[o] = (byte)col.Item1; rgb[o + 1] = (byte)col.Item2; rgb[o + 2] = (byte)col.Item3;
            }
        }
        Png.Write(path, px, px, rgb);
    }

    static void Log(string message) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
}
