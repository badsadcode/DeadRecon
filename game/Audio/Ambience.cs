using System;
using DeadRecon.World;
using Godot;

namespace DeadRecon.Audio;

/// <summary>
/// The jungle's sound: a low bed, cicadas, birds calling from the trees around you (fewer in rain, silent in a
/// storm), and the echo that rolls back after every gunshot. Also sets up the audio buses (Master > Effects /
/// Ambience / Voices / Foley) with a reverb on Effects so shots ring through the trees.
/// </summary>
public partial class Ambience : Node3D
{
    public Camera3D Camera = null!;
    public static Ambience? Current { get; private set; }

    private AudioStreamPlayer _bed = null!, _cicadas = null!;
    private float _nextBird = 2;
    private readonly RandomNumberGenerator _rng = new();

    public override void _Ready()
    {
        Current = this;
        SetupBuses();
        _bed = new AudioStreamPlayer { Stream = Synth.JungleBed(), Bus = "Ambience", VolumeDb = -22, Autoplay = true };
        _cicadas = new AudioStreamPlayer { Stream = Synth.Cicadas(), Bus = "Ambience", VolumeDb = -26, Autoplay = true };
        AddChild(_bed);
        AddChild(_cicadas);
    }

    public override void _ExitTree() { if (Current == this) Current = null; }

    public static void SetupBuses()
    {
        int Bus(string name)
        {
            int i = AudioServer.GetBusIndex(name);
            if (i >= 0) return i;
            AudioServer.AddBus();
            i = AudioServer.BusCount - 1;
            AudioServer.SetBusName(i, name);
            AudioServer.SetBusSend(i, "Master");
            return i;
        }
        int fx = Bus("Effects");
        Bus("Ambience");
        Bus("Voices");
        Bus("Foley");          // your own body (footsteps): dry, no reverb, so it sounds right at your feet
        if (AudioServer.GetBusEffectCount(fx) == 0)
            AudioServer.AddBusEffect(fx, new AudioEffectReverb { RoomSize = .75f, Damping = .6f, Wet = .16f, Dry = 1, Spread = .9f, Hipass = .15f });
        ApplyVolumes();
    }

    public static void ApplyVolumes()
    {
        AudioServer.SetBusVolumeDb(0, Mathf.LinearToDb(Math.Max(.0001f, DeadRecon.UI.Settings.MasterVolume)));
        int amb = AudioServer.GetBusIndex("Ambience");
        if (amb >= 0) AudioServer.SetBusVolumeDb(amb, Mathf.LinearToDb(Math.Max(.0001f, DeadRecon.UI.Settings.AmbienceVolume)));
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        float rain = Weather.Current?.Intensity ?? 0;
        // Rain drowns the insects and sends the birds to shelter.
        _cicadas.VolumeDb = Mathf.LinearToDb(Math.Max(.001f, .055f * (1 - .9f * rain)));
        _bed.VolumeDb = Mathf.LinearToDb(Math.Max(.001f, .08f * (1 - .5f * rain)));
        _nextBird -= dt * (1 - .95f * rain);
        if (_nextBird <= 0)
        {
            _nextBird = _rng.RandfRange(1.5f, 6f);
            float a = _rng.Randf() * MathF.Tau, r = _rng.RandfRange(15, 60);
            var at = Camera.GlobalPosition + new Vector3(MathF.Cos(a) * r, _rng.RandfRange(6, 20), MathF.Sin(a) * r);
            var bird = new AudioStreamPlayer3D
            {
                Stream = Synth.Bird(_rng.RandiRange(0, 3), _rng.RandiRange(0, 4)), Bus = "Ambience",
                UnitSize = 10, MaxDistance = 120, VolumeDb = _rng.RandfRange(-14, -6), PitchScale = _rng.RandfRange(.9f, 1.1f),
                AttenuationFilterCutoffHz = 9000, AttenuationFilterDb = -12,
            };
            AddChild(bird);
            bird.GlobalPosition = at;
            bird.Play();
            bird.Finished += bird.QueueFree;
        }
    }

    /// <summary>
    /// The hills answer a gunshot: a dark rumble arriving after the shot, from roughly behind the shooter, fainter
    /// the further away it happened. Called by anyone who fires.
    /// </summary>
    public void Echo(Vector3 at)
    {
        float d = Camera.GlobalPosition.DistanceTo(at);
        if (d > 900) return;
        var p = new AudioStreamPlayer
        {
            Stream = Synth.ShotEcho(), Bus = "Ambience", PitchScale = _rng.RandfRange(.85f, 1.05f),
            VolumeDb = -9 - d / 60,
        };
        AddChild(p);
        GetTree().CreateTimer(.25 + d / 700).Timeout += () => { if (IsInstanceValid(p)) p.Play(); };
        p.Finished += p.QueueFree;
    }
}
