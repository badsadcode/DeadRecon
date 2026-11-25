using System;
using System.Collections.Generic;
using Godot;

namespace DeadRecon.Audio;

/// <summary>
/// Every sound in Dead Recon is made here, from maths (no audio files yet). Each generator returns an
/// <see cref="AudioStreamWav"/>; results are cached. Mono 22.05 kHz unless noted: plenty for a jungle.
/// </summary>
public static class Synth
{
    public const int Rate = 22050;
    private static readonly Dictionary<string, AudioStreamWav> Cache = new();

    // ================================================================= building blocks

    /// <summary>Simple biquad filter (RBJ cookbook): band-pass, low-pass or high-pass.</summary>
    public sealed class Biquad
    {
        private float b0, b1, b2, a1, a2, x1, x2, y1, y2;
        public static Biquad BandPass(float freq, float q) => Make(freq, q, 0);
        public static Biquad LowPass(float freq, float q = .707f) => Make(freq, q, 1);
        public static Biquad HighPass(float freq, float q = .707f) => Make(freq, q, 2);

        private static Biquad Make(float f, float q, int type)
        {
            float w = MathF.Tau * Math.Clamp(f, 10, Rate * .45f) / Rate, cs = MathF.Cos(w), alpha = MathF.Sin(w) / (2 * q);
            float b0, b1, b2, a0 = 1 + alpha;
            switch (type)
            {
                case 0: b0 = alpha; b1 = 0; b2 = -alpha; break;
                case 1: b0 = (1 - cs) / 2; b1 = 1 - cs; b2 = (1 - cs) / 2; break;
                default: b0 = (1 + cs) / 2; b1 = -(1 + cs); b2 = (1 + cs) / 2; break;
            }
            return new Biquad { b0 = b0 / a0, b1 = b1 / a0, b2 = b2 / a0, a1 = -2 * cs / a0, a2 = (1 - alpha) / a0 };
        }

        public float Run(float x)
        {
            float y = b0 * x + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2;
            x2 = x1; x1 = x; y2 = y1; y1 = y;
            return y;
        }
    }

    public static AudioStreamWav ToWav(float[] s, int rate = Rate, bool loop = false, float gain = 1)
    {
        float peak = 1e-6f;
        foreach (var v in s) peak = Math.Max(peak, Math.Abs(v));
        float k = gain * .9f / peak;                       // normalise, then the caller's gain
        var data = new byte[s.Length * 2];
        for (int i = 0; i < s.Length; i++)
        {
            short v = (short)(Math.Clamp(s[i] * k, -1, 1) * 32000);
            data[i * 2] = (byte)v; data[i * 2 + 1] = (byte)(v >> 8);
        }
        var w = new AudioStreamWav { Format = AudioStreamWav.FormatEnum.Format16Bits, MixRate = rate, Stereo = false, Data = data };
        if (loop) { w.LoopMode = AudioStreamWav.LoopModeEnum.Forward; w.LoopBegin = 0; w.LoopEnd = s.Length; }
        return w;
    }

    /// <summary>Make a buffer loop seamlessly: cross-fade (equal power) the last <paramref name="fade"/> samples over the start.</summary>
    private static float[] LoopSeamless(float[] s, int fade)
    {
        int n = s.Length - fade;
        var o = new float[n];
        for (int i = 0; i < n; i++)
        {
            o[i] = s[i];
            if (i < fade) { float a = i / (float)fade; o[i] = s[i] * MathF.Sin(a * MathF.PI / 2) + s[n + i] * MathF.Cos(a * MathF.PI / 2); }
        }
        return o;
    }

    private static AudioStreamWav Cached(string key, Func<AudioStreamWav> make)
    {
        if (!Cache.TryGetValue(key, out var w)) Cache[key] = w = make();
        return w;
    }

    // ================================================================= footsteps

    public enum Surface { Leaves, Mud, Water, Dirt, Wood }

    /// <summary>One footstep (several variants per surface so a walk doesn't sound like a metronome).</summary>
    public static AudioStreamWav Step(Surface surface, int variant) => Cached($"step{surface}{variant}", () =>
    {
        var rng = new Random(1000 * (int)surface + variant);
        int n = (int)(Rate * (surface == Surface.Water ? .32f : .2f));
        var s = new float[n];
        switch (surface)
        {
            case Surface.Leaves:
            {
                // Dry litter: a soft heel thud, then a scatter of tiny crackles (leaves breaking) over ~120 ms.
                var crackle = Biquad.BandPass(2600 + rng.Next(1200), 1.2f);
                var thud = Biquad.LowPass(240);
                var crackTimes = new List<int>();
                for (int k = 0; k < 9 + rng.Next(6); k++) crackTimes.Add((int)(Rate * (.005 + rng.NextDouble() * .12)));
                for (int i = 0; i < n; i++)
                {
                    float t = i / (float)Rate, noise = (float)rng.NextDouble() * 2 - 1;
                    float c = 0;
                    foreach (int ct in crackTimes) { int d = i - ct; if (d >= 0 && d < Rate * .006f) c += MathF.Exp(-d / (Rate * .0012f)); }
                    // Close-up: a solid heel/sole thump carries the "it's my foot" feeling; the crackle sits on top.
                    s[i] = crackle.Run(noise) * (c * 1.2f + .2f * MathF.Exp(-t * 25)) + thud.Run(noise) * 6 * MathF.Exp(-t * 32);
                }
                break;
            }
            case Surface.Mud:
            {
                // Wet ground: a dull low squelch, a bit of suction.
                var lp = Biquad.LowPass(500);
                float ph = 0;
                for (int i = 0; i < n; i++)
                {
                    float t = i / (float)Rate, noise = (float)rng.NextDouble() * 2 - 1;
                    ph += MathF.Tau * (220 - 120 * t / .2f + 25 * MathF.Sin(t * 90)) / Rate;
                    s[i] = (MathF.Sin(ph) * .6f + lp.Run(noise) * 1.6f) * MathF.Exp(-t * 18) * Math.Min(1, t * 300);
                }
                break;
            }
            case Surface.Dirt:
            {
                // Packed earth: a firm thump with a little grit scuffing under the sole.
                var thud = Biquad.LowPass(320); var grit = Biquad.BandPass(3800, .9f);
                for (int i = 0; i < n; i++)
                {
                    float t = i / (float)Rate, noise = (float)rng.NextDouble() * 2 - 1;
                    s[i] = thud.Run(noise) * 6.5f * MathF.Exp(-t * 38) + grit.Run(noise) * .5f * MathF.Exp(-t * 22) * Math.Min(1, t * 200);
                }
                break;
            }
            case Surface.Wood:
            {
                // Planks: a hollow knock (two resonances) and a soft creak.
                float f1 = 180 + rng.Next(40), f2 = 410 + rng.Next(80), ph1 = 0, ph2 = 0;
                var lp = Biquad.LowPass(900);
                for (int i = 0; i < n; i++)
                {
                    float t = i / (float)Rate, noise = (float)rng.NextDouble() * 2 - 1;
                    ph1 += MathF.Tau * f1 / Rate; ph2 += MathF.Tau * f2 / Rate;
                    s[i] = (MathF.Sin(ph1) * .9f * MathF.Exp(-t * 30) + MathF.Sin(ph2) * .45f * MathF.Exp(-t * 45)) * Math.Min(1, t * 400)
                         + lp.Run(noise) * 2.5f * MathF.Exp(-t * 60);
                }
                break;
            }
            default:
            {
                // Water: a splash (bright noise) with a gurgling tail.
                var hp = Biquad.HighPass(900); var bp = Biquad.BandPass(650, 2);
                for (int i = 0; i < n; i++)
                {
                    float t = i / (float)Rate, noise = (float)rng.NextDouble() * 2 - 1;
                    s[i] = hp.Run(noise) * MathF.Exp(-t * 20) + bp.Run(noise) * 2 * MathF.Exp(-t * 9) * (.6f + .4f * MathF.Sin(t * 70));
                }
                break;
            }
        }
        return ToWav(s);
    });

    // ================================================================= voices

    /// <summary>
    /// Grumpy gibberish for a line of text: syllables of buzzy vowel sounds (a voice source through two formant
    /// filters) with consonant puffs between them. Same text + same voice = same mumble. Shouted lines (capitals,
    /// "!") are higher and louder; the pitch falls toward the end of each line, like a sigh.
    /// </summary>
    public static AudioStreamWav Voice(string text, float pitch, int voiceSeed)
    {
        int syllables = Math.Clamp(text.Length / 4, 1, 14);
        bool shout = text.Contains('!') && (text.ToUpperInvariant() == text || text.Contains("!!") || text.Split(' ').Length < 5);
        float basePitch = pitch * (shout ? 1.35f : 1);
        var rng = new Random(text.GetHashCode() ^ voiceSeed);
        // Formant pairs (F1, F2) for a, e, i, o, u, and "uh".
        var vowels = new (float f1, float f2)[] { (750, 1200), (500, 1750), (320, 2200), (480, 850), (330, 800), (600, 1150) };
        var buf = new List<float>();
        float phase = 0;
        for (int k = 0; k < syllables; k++)
        {
            var (f1, f2) = vowels[rng.Next(vowels.Length)];
            float len = .07f + (float)rng.NextDouble() * .09f + (k == syllables - 1 ? .08f : 0);
            int n = (int)(Rate * len);
            var b1 = Biquad.BandPass(f1, 4); var b2 = Biquad.BandPass(f2, 5); var cons = Biquad.BandPass(2500 + rng.Next(3000), 2);
            float progress = k / (float)Math.Max(1, syllables - 1);
            float f0 = basePitch * (1.08f - .22f * progress) * (.94f + .12f * (float)rng.NextDouble());
            bool consonant = rng.NextDouble() < .7;
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)n;
                float f = f0 * (1 + .04f * MathF.Sin(i / (float)Rate * 30));
                phase += f / Rate;
                if (phase > 1) phase -= 1;
                float source = (2 * phase - 1) * .8f;           // sawtooth: buzzy like vocal folds
                float env = MathF.Sin(MathF.PI * Math.Min(1, t * 1.3f)) * (t < .1f ? t / .1f : 1);
                float v = (b1.Run(source) * 1.0f + b2.Run(source) * .6f) * env;
                if (consonant && t < .18f) v += cons.Run((float)rng.NextDouble() * 2 - 1) * (1 - t / .18f) * .5f;
                buf.Add(v);
            }
            int gap = (int)(Rate * (.015f + (float)rng.NextDouble() * .04f));
            for (int i = 0; i < gap; i++) buf.Add(0);
        }
        return ToWav(buf.ToArray(), gain: shout ? 1 : .8f);
    }

    // ================================================================= bullets

    /// <summary>A bullet into dirt: a short, dull "thup" with a spray of grit.</summary>
    public static AudioStreamWav DirtHit(int variant) => Cached($"dirt{variant}", () =>
    {
        var rng = new Random(300 + variant);
        int n = (int)(Rate * .14f);
        var s = new float[n];
        var lp = Biquad.LowPass(380); var grit = Biquad.BandPass(3200 + rng.Next(1500), 1.5f);
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Rate, noise = (float)rng.NextDouble() * 2 - 1;
            s[i] = lp.Run(noise) * 3 * MathF.Exp(-t * 55) + grit.Run(noise) * MathF.Exp(-t * 30) * (t > .01f ? 1 : 0);
        }
        return ToWav(s, gain: .8f);
    });

    /// <summary>A bullet passing close by: the sharp supersonic "snap" (an N-wave) and a short whizz after it.</summary>
    public static AudioStreamWav Crack(int variant) => Cached($"crack{variant}", () =>
    {
        var rng = new Random(500 + variant);
        int n = (int)(Rate * .18f);
        var s = new float[n];
        var hp = Biquad.HighPass(1800); var bp = Biquad.BandPass(2400 + rng.Next(1600), 3);
        int wave = (int)(Rate * .0011f);
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Rate, noise = (float)rng.NextDouble() * 2 - 1;
            float nwave = i < wave ? 1 - 2f * i / wave : 0;                       // the snap
            float whizz = bp.Run(noise) * MathF.Exp(-t * 28) * (t > .004f ? 1 : 0);
            s[i] = nwave * 1.2f + hp.Run(noise) * MathF.Exp(-t * 300) * .6f + whizz * .7f;
        }
        return ToWav(s);
    });

    /// <summary>An empty brass case landing: a couple of high metallic tinks.</summary>
    public static AudioStreamWav Tink(int variant) => Cached($"tink{variant}", () =>
    {
        var rng = new Random(700 + variant);
        int n = (int)(Rate * .25f);
        var s = new float[n];
        float f1 = 3800 + rng.Next(1400), f2 = f1 * 2.71f;
        int second = (int)(Rate * (.06f + (float)rng.NextDouble() * .05f));
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Rate, t2 = (i - second) / (float)Rate;
            float a = MathF.Sin(MathF.Tau * f1 * t) * MathF.Exp(-t * 60) + MathF.Sin(MathF.Tau * f2 * t) * .4f * MathF.Exp(-t * 90);
            float b = i > second ? (MathF.Sin(MathF.Tau * f1 * 1.03f * t2) * MathF.Exp(-t2 * 70)) * .5f : 0;
            s[i] = a + b;
        }
        return ToWav(s, gain: .5f);
    });

    // ================================================================= ambience

    /// <summary>Cicadas: a band of hiss pulsing ~45 times a second, swelling and fading. Seamless 11 s loop.</summary>
    public static AudioStreamWav Cicadas() => Cached("cicadas", () =>
    {
        int fade = Rate, n = Rate * 11 + fade;
        var s = new float[n];
        var rng = new Random(9);
        var bp1 = Biquad.BandPass(4800, 6); var bp2 = Biquad.BandPass(6100, 8);
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Rate, noise = (float)rng.NextDouble() * 2 - 1;
            float pulse = .55f + .45f * MathF.Sin(MathF.Tau * 46 * t);
            float swell = .45f + .55f * MathF.Pow(.5f + .5f * MathF.Sin(t * .7f) * MathF.Sin(t * .23f + 1), 2);
            s[i] = (bp1.Run(noise) * pulse + bp2.Run(noise) * (.5f + .5f * MathF.Sin(MathF.Tau * 39 * t)) * .6f) * swell;
        }
        return ToWav(LoopSeamless(s, fade), loop: true);
    });

    /// <summary>A soft, low jungle bed (distant everything): dark filtered noise. Seamless 9 s loop.</summary>
    public static AudioStreamWav JungleBed() => Cached("bed", () =>
    {
        int fade = Rate, n = Rate * 9 + fade;
        var s = new float[n];
        var rng = new Random(4);
        var lp = Biquad.LowPass(420); var bp = Biquad.BandPass(1300, .8f);
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Rate, noise = (float)rng.NextDouble() * 2 - 1;
            s[i] = lp.Run(noise) * 1.2f + bp.Run(noise) * .25f * (.6f + .4f * MathF.Sin(t * .5f));
        }
        return ToWav(LoopSeamless(s, fade), loop: true);
    });

    /// <summary>A bird call: one of several species-ish patterns (whistle, trill, chirps, a two-note call).</summary>
    public static AudioStreamWav Bird(int kind, int variant) => Cached($"bird{kind}_{variant}", () =>
    {
        var rng = new Random(kind * 100 + variant);
        var buf = new List<float>();
        float phase = 0;
        void Tone(float seconds, Func<float, float> freq, Func<float, float> amp)
        {
            int n = (int)(Rate * seconds);
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)n;
                phase += freq(t) / Rate;
                buf.Add(MathF.Sin(MathF.Tau * phase) * amp(t));
            }
        }
        void Rest(float seconds) { for (int i = 0; i < (int)(Rate * seconds); i++) buf.Add(0); }
        float bp = 1800 + rng.Next(2200);
        switch (kind % 4)
        {
            case 0: // long falling whistle
                Tone(.55f + (float)rng.NextDouble() * .3f, t => bp * (1.3f - .5f * t), t => MathF.Sin(MathF.PI * t));
                break;
            case 1: // trill
                Tone(.6f, t => bp + 600 * MathF.Sin(t * MathF.Tau * 14), t => MathF.Sin(MathF.PI * t) * (.6f + .4f * MathF.Sin(t * 90)));
                break;
            case 2: // chirps
                for (int c = 0; c < 3 + rng.Next(4); c++) { Tone(.06f, t => bp * (1 + .6f * t), t => MathF.Sin(MathF.PI * t)); Rest(.05f + (float)rng.NextDouble() * .06f); }
                break;
            default: // two-note call, "hoo-hee"
                Tone(.28f, t => bp * .6f, t => MathF.Sin(MathF.PI * t)); Rest(.08f); Tone(.34f, t => bp * .8f * (1 + .05f * t), t => MathF.Sin(MathF.PI * t));
                break;
        }
        return ToWav(buf.ToArray(), gain: .7f);
    });

    /// <summary>The echo of a shot rolling round the hills: a long, dark, slowly fading rumble.</summary>
    public static AudioStreamWav ShotEcho() => Cached("echo", () =>
    {
        int n = (int)(Rate * 2.2f);
        var s = new float[n];
        var rng = new Random(77);
        var lp = Biquad.LowPass(900); var lp2 = Biquad.LowPass(400);
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Rate, noise = (float)rng.NextDouble() * 2 - 1;
            float bumps = .6f + .4f * MathF.Sin(t * 17) * MathF.Sin(t * 5.3f);        // reflections arriving
            s[i] = (lp.Run(noise) * .7f + lp2.Run(noise) * 1.5f) * bumps * MathF.Exp(-t * 1.7f) * Math.Min(1, t * 8);
        }
        return ToWav(s, gain: .9f);
    });
}
