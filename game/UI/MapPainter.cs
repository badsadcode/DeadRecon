using System;
using System.Threading.Tasks;
using DeadRecon.World;
using Godot;

namespace DeadRecon.UI;

/// <summary>
/// Paints the field map's base from the baked world, once, on a background thread: khaki paper, greener lowlands,
/// hill shading, brown contours every 20 m (bolder every 100 m), blue water, fields (rice teal, wheat gold) and
/// trampled village ground. Roads, places and markers are drawn on top by <see cref="MapView"/>, as vectors.
/// </summary>
public static class MapPainter
{
    public const int Px = 2048;
    private static Task<ImageTexture?>? _job;
    private static ImageTexture? _done;

    /// <summary>The painted map, or null while it's still being painted (starts the job on first call).</summary>
    public static ImageTexture? Get(BakedWorld world, Settlements? towns)
    {
        if (_done != null) return _done;
        _job ??= Task.Run(() => Paint(world, towns));
        if (!_job.IsCompleted) return null;
        _done = _job.Result;
        return _done;
    }

    /// <summary>A new world was loaded (new operation): the old painting is still right (same bake), keep it.</summary>
    public static void Warm(BakedWorld world, Settlements? towns) => Get(world, towns);

    private static ImageTexture? Paint(BakedWorld world, Settlements? towns)
    {
        try
        {
            float mpp = world.Size / Px;
            var h = new float[(Px + 1) * (Px + 1)];
            Parallel.For(0, Px + 1, j =>
            {
                for (int i = 0; i <= Px; i++) h[j * (Px + 1) + i] = world.HeightAt(-world.Half + i * mpp, -world.Half + j * mpp);
            });
            var data = new byte[Px * Px * 3];
            var paper = new Vector3(.86f, .82f, .70f);
            Parallel.For(0, Px, j =>
            {
                for (int i = 0; i < Px; i++)
                {
                    float a = h[j * (Px + 1) + i], b = h[j * (Px + 1) + i + 1], c = h[(j + 1) * (Px + 1) + i];
                    float x = -world.Half + (i + .5f) * mpp, z = -world.Half + (j + .5f) * mpp;
                    var (gx, gz) = world.ToSample(x, z);
                    bool wet = world.IsWet((int)MathF.Round(gx), (int)MathF.Round(gz));
                    // Lowlands greener, highlands drier.
                    float t = Math.Clamp((a - 150) / 900, 0, 1);
                    var col = paper.Lerp(new Vector3(.60f, .69f, .50f), .55f * (1 - t)).Lerp(new Vector3(.78f, .72f, .58f), .35f * t);
                    // Hill shading from the north-west.
                    float dx = (b - a) / mpp, dz = (c - a) / mpp;
                    float light = Math.Clamp(1 + (-dx - dz) * .9f, .62f, 1.12f);
                    col *= light;
                    // Contours: where this pixel and its neighbour sit in different 20 m bands.
                    int band = (int)MathF.Floor(a / 20), bandR = (int)MathF.Floor(b / 20), bandD = (int)MathF.Floor(c / 20);
                    if (band != bandR || band != bandD)
                    {
                        bool index = (Math.Max(band, Math.Max(bandR, bandD)) % 5) == 0;
                        col = col.Lerp(new Vector3(.52f, .37f, .22f), index ? .62f : .3f);
                    }
                    if (towns != null)
                    {
                        float field = towns.FieldAt(x, z), yard = towns.YardAt(x, z);
                        if (field > .8f) col = col.Lerp(new Vector3(.52f, .70f, .62f), .75f);
                        else if (field > .3f) col = col.Lerp(new Vector3(.88f, .76f, .42f), .75f);
                        if (yard > .3f) col = col.Lerp(new Vector3(.82f, .70f, .54f), .6f * yard);
                    }
                    if (wet) col = new Vector3(.42f, .60f, .72f);
                    int o = (j * Px + i) * 3;
                    data[o] = (byte)Math.Clamp(col.X * 255, 0, 255);
                    data[o + 1] = (byte)Math.Clamp(col.Y * 255, 0, 255);
                    data[o + 2] = (byte)Math.Clamp(col.Z * 255, 0, 255);
                }
            });
            var img = Image.CreateFromData(Px, Px, false, Image.Format.Rgb8, data);
            img.GenerateMipmaps();
            return ImageTexture.CreateFromImage(img);
        }
        catch (Exception e)
        {
            GD.PushWarning($"Map painting failed: {e.Message}");
            return null;
        }
    }
}
