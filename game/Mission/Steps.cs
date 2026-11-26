using System;
using System.Collections.Generic;
using System.Linq;
using DeadRecon.AI;
using DeadRecon.Base;
using DeadRecon.People;
using Godot;

namespace DeadRecon.Missions;

/// <summary>
/// The building blocks of an operation (docs/MISSIONS_PLAN.md, section 2). An operation is a list of steps, done in
/// order: each knows its objective line, where its marker goes, and when it's done. New mission types are new lists
/// (see <c>Recipe</c> in Mission.cs); a new kind of step is a new small class here.
/// </summary>
public partial class Mission
{
    public abstract class Step
    {
        public Mission M = null!;
        /// <summary>What finishing this step changed in the world (not your kit, that's saved already): redone when a
        /// saved operation comes back past it.</summary>
        public Action? Replay;
        /// <summary>Something to set going when this step begins (a patrol sets off, a search party heads out).</summary>
        public Action? OnStart;
        /// <summary>What you're doing while the progress bar fills ("taking it", "focusing").</summary>
        public virtual string? Doing => null;
        /// <summary>The objective line (without distance and step count).</summary>
        public abstract string Text { get; }
        /// <summary>The word over the marker ("CACHE"), or "" for no marker.</summary>
        public virtual string Marker => "";
        public virtual Vector3? Target => null;
        /// <summary>How high above the target the marker floats.</summary>
        public virtual float MarkerLift => 1.4f;
        /// <summary>This step is getting out (the "Extract" phase: home or the landing zone).</summary>
        public virtual bool IsExtract => false;
        /// <summary>Whether the objective line shows the distance to the target.</summary>
        public virtual bool ShowDistance => Target != null;
        public virtual void Start() { }
        /// <summary>Called every frame while it's the current step; true when it's done.</summary>
        public abstract bool Tick(float dt);
        /// <summary>Tests: whether <see cref="Mission.TestTakeIntel"/> finishes this step for you (talking to people is
        /// left to the test, which presses F).</summary>
        public virtual bool TestSkip => true;
        /// <summary>Tests: do it now.</summary>
        public virtual void TestDo() { }

        protected Vector3 PlayerAt => M.Player.GlobalPosition;
        protected bool Pressed(Key key) => Input.IsPhysicalKeyPressed(key) && Input.MouseMode == Input.MouseModeEnum.Captured;
    }

    /// <summary>Hold F at a spot: take something, pick a lock, plant a charge, send a message.</summary>
    public sealed class Hold : Step
    {
        public Func<Vector3> At = null!;
        public string What = "", Label = "", Verb = "taking it";
        public float Seconds = 1.6f;
        public Action? Done;
        public override string? Doing => Verb;
        public override string Text => What;
        public override string Marker => Label;
        public override Vector3? Target => At();
        public bool Near => !M.Player.Dead && PlayerAt.DistanceTo(At()) < 2.4f;
        public override bool Tick(float dt)
        {
            bool holding = Near && Input.IsPhysicalKeyPressed(Key.F);
            M.SearchProgress = holding ? Math.Min(1, M.SearchProgress + dt / Seconds) : Math.Max(0, M.SearchProgress - dt);
            if (M.SearchProgress < 1) return false;
            M.SearchProgress = 0;
            Done?.Invoke();
            return true;
        }
        public override void TestDo() => Done?.Invoke();
    }

    /// <summary>Someone has to go down (or already has).</summary>
    public sealed class Kill : Step
    {
        public Func<Guard?> Who = null!;
        public string What = "", Label = "";
        public override string Text => What;
        public override string Marker => Label;
        public override float MarkerLift => 2.4f;
        public override Vector3? Target => Who() is { } g && IsInstanceValid(g) ? g.GlobalPosition : M.Camp;
        public override bool Tick(float dt) => Who() is not { } g || !IsInstanceValid(g) || g.State == Guard.Mood.Dead;
        public override void TestDo() { if (Who() is { } g && IsInstanceValid(g)) g.TakeHit(1000, g.GlobalPosition + Vector3.Up, g.GlobalPosition + Vector3.Right); }
    }

    /// <summary>Search someone's body (F): keys, orders. While he's alive, the line says what you're after.</summary>
    public sealed class SearchBody : Step
    {
        public Func<Guard?> Who = null!;
        public string What = "", Prompt = "", Label = "";
        public Action? Done;
        public override string Text => Dead ? $"{What} (F at the body)" : What;
        public override string Marker => Label;
        public override float MarkerLift => Dead ? 1f : 2.4f;
        public override Vector3? Target => Who() is { } g && IsInstanceValid(g) ? g.GlobalPosition : null;
        private bool Dead => Who() is { } g && (!IsInstanceValid(g) || g.State == Guard.Mood.Dead);
        public override bool Tick(float dt)
        {
            if (Who() is not { } g) { Done?.Invoke(); return true; }   // nobody to search (he was never there)
            if (!Dead || !IsInstanceValid(g) || M.Player.Dead || PlayerAt.DistanceTo(g.GlobalPosition) > 2.4f) return false;
            DeadRecon.UI.Hud.Prompt($"F   {Prompt}");
            if (!Pressed(Key.F)) return false;
            Done?.Invoke();
            return true;
        }
        public override void TestDo() => Done?.Invoke();
    }

    /// <summary>A countdown (a charge's fuse): get clear. Then <see cref="Boom"/>.</summary>
    public sealed class Fuse : Step
    {
        public float Seconds = 25;
        public Func<Vector3> At = null!;
        public Action? Boom;
        private float _left;
        public float Left => _left;
        public override string Text => $"get clear! The charge goes in {_left:0} s";
        public override string Marker => $"CHARGE  {_left:0} s";
        public override Vector3? Target => At();
        public override bool ShowDistance => false;
        public override void Start() => _left = Seconds;
        public override bool Tick(float dt)
        {
            _left -= dt;
            if (_left > 0) return false;
            Boom?.Invoke();
            return true;
        }
        public override void TestDo() => Boom?.Invoke();
    }

    /// <summary>Talk someone into following you (they handle the F themselves).</summary>
    public sealed class Talk : Step
    {
        public Func<Villager?> Who = null!;
        public string What = "", Label = "PRISONER";
        /// <summary>False: no marker over him (you have to find him: he's hiding, and he calls out).</summary>
        public bool Show = true;
        public Action? Done;
        public override string Text => What;
        public override string Marker => Show ? Label : "";
        public override Vector3? Target => Show ? Who()?.GlobalPosition : null;
        public override bool ShowDistance => false;
        public override bool Tick(float dt)
        {
            if (Who() is { } v && !v.Following) return false;
            Done?.Invoke();
            return true;
        }
        public override bool TestSkip => false;
    }

    /// <summary>Somewhere you have to be (with nothing to do there yet but arrive).</summary>
    public sealed class GoTo : Step
    {
        public Vector3 At;
        public float Radius = 6;
        public string What = "", Label = "";
        public override string Text => What;
        public override string Marker => Label;
        public override float MarkerLift => 3f;
        public override Vector3? Target => At;
        /// <summary>Everyone you're bringing has to be there too.</summary>
        public bool WithEscorts;
        public override bool Tick(float dt)
        {
            if (M.Player.Dead || Flat(PlayerAt - At) > Radius) return false;
            if (WithEscorts) foreach (var (v, _) in M.Escorts) if (IsInstanceValid(v) && Flat(v.GlobalPosition - At) > Radius + 5) return false;
            return true;
        }
    }

    /// <summary>Look at each thing (through your scope is easiest), close enough, and hold F: a photo.</summary>
    public sealed class Photograph : Step
    {
        public List<(string Name, Vector3 At)> Sights = new();
        public float Range = 75;
        private readonly HashSet<int> _done = new();
        private float _hold;
        private int Next => Enumerable.Range(0, Sights.Count).FirstOrDefault(i => !_done.Contains(i), -1);
        public override string Text => Next < 0 ? "photos done" : $"photograph the camp: {Sights[Next].Name} ({_done.Count}/{Sights.Count}). Look at it, hold F";
        public override string Marker => "PHOTO";
        public override Vector3? Target => Next < 0 ? null : Sights[Next].At;
        public override string? Doing => "focusing";
        public override bool Tick(float dt)
        {
            int i = Next;
            if (i < 0) return true;
            var cam = M.Player.Camera.GlobalTransform;
            var to = Sights[i].At - cam.Origin;
            float d = to.Length();
            bool aimed = !M.Player.Dead && d < Range && -cam.Basis.Z.Dot(to / d) > .996f && Clear(cam.Origin, Sights[i].At);
            bool holding = aimed && Input.IsPhysicalKeyPressed(Key.F);
            _hold = holding ? _hold + dt : Math.Max(0, _hold - dt * 2);
            M.SearchProgress = _hold / 1.5f;
            if (aimed && !holding) DeadRecon.UI.Hud.Prompt("F   Take the photo");
            if (_hold < 1.5f) return false;
            _hold = 0; M.SearchProgress = 0;
            _done.Add(i);
            DeadRecon.UI.Hud.Toast($"Click. {char.ToUpper(Sights[i].Name[0])}{Sights[i].Name[1..]}.");
            GD.Print($"[mission] photographed {Sights[i].Name}");
            return Next < 0;
        }
        private bool Clear(Vector3 from, Vector3 to)
        {
            var hit = M.GetWorld3D().DirectSpaceState.IntersectRay(PhysicsRayQueryParameters3D.Create(from, to, 1, new Godot.Collections.Array<Rid> { M.Player.GetRid() }));
            return hit.Count == 0 || ((Vector3)hit["position"]).DistanceTo(to) < 2.5f;
        }
        public override void TestDo() { for (int i = 0; i < Sights.Count; i++) _done.Add(i); }
    }

    /// <summary>Stay close to something (their radio) long enough, without the camp noticing you.</summary>
    public sealed class Listen : Step
    {
        public Func<Vector3> At = null!;
        public float Radius = 25, Seconds = 60;
        public string[] Overheard = Array.Empty<string>();
        private float _heard;
        private int _said;
        private bool In => !M.Player.Dead && Flat(PlayerAt - At()) < Radius;
        public override string Text => In ? $"listen: stay within {Radius:0} m of their radio, unseen ({_heard:0}/{Seconds:0} s)"
                                          : $"get within {Radius:0} m of their radio and listen ({_heard:0}/{Seconds:0} s)";
        public override string Marker => "RADIO";
        public override Vector3? Target => At();
        public override bool Tick(float dt)
        {
            if (M.Guards.Take(M._campGuards).Any(g => IsInstanceValid(g) && g.State == Guard.Mood.Alert))
            {
                M.Fail("They spotted you. Nobody says anything interesting on the radio after that.");
                return false;
            }
            if (!In) return false;
            _heard += dt;
            int line = (int)(_heard / Seconds * (Overheard.Length + 1));
            if (line > _said && line - 1 < Overheard.Length) { DeadRecon.UI.Hud.Toast($"Radio: \"{Overheard[line - 1]}\""); _said = line; }
            return _heard >= Seconds;
        }
        public override void TestDo() => _heard = Seconds;
    }

    /// <summary>Wait for something to happen (the patrol to walk into your ambush).</summary>
    public sealed class WaitFor : Step
    {
        public Func<bool> Until = null!;
        public Func<Vector3?>? Where;
        public string What = "", Label = "";
        public override string Text => What;
        public override string Marker => Label;
        public override Vector3? Target => Where?.Invoke();
        public override bool Tick(float dt) => Until();
    }

    /// <summary>A group has to go down (or break and run): an ambushed patrol, a punishment squad.</summary>
    public sealed class KillAll : Step
    {
        public Func<IEnumerable<Guard>> Who = null!;
        public string What = "", Label = "";
        /// <summary>Each frame (more of them on the way, the last one breaking...). Return true to finish early.</summary>
        public Func<float, bool>? Every;
        private IEnumerable<Guard> Alive => Who().Where(g => IsInstanceValid(g) && g.State != Guard.Mood.Dead && !g.Captive && !g.Out);
        public override string Text => $"{What} ({Alive.Count()} left)";
        public override string Marker => Label;
        public override float MarkerLift => 2.4f;
        public override Vector3? Target => Alive.OrderBy(g => g.GlobalPosition.DistanceTo(PlayerAt)).FirstOrDefault()?.GlobalPosition;
        public override bool Tick(float dt) => (Every?.Invoke(dt) ?? false) || !Alive.Any();
        public override void TestDo() { foreach (var g in Alive.ToList()) g.TakeHit(1000, g.GlobalPosition + Vector3.Up, g.GlobalPosition + Vector3.Right); }
    }

    /// <summary>Hold a place for a while (the landing zone until the helicopter). The clock stops when you leave it.</summary>
    public sealed class HoldOut : Step
    {
        public Func<Vector3> At = null!;
        public float Radius = 25, Seconds = 75;
        public string What = "";
        public Action? Done;
        private float _left = -1;
        private bool In => !M.Player.Dead && Flat(PlayerAt - At()) < Radius;
        public override string Text => In ? $"{What}: {_left:0} s" : $"get back to the landing zone! ({_left:0} s left to hold)";
        public override string Marker => "LZ";
        public override float MarkerLift => 4f;
        public override Vector3? Target => At();
        public override bool ShowDistance => !In;
        public override void Start() => _left = Seconds;
        public override bool Tick(float dt)
        {
            if (In) _left -= dt;
            if (_left > 0) return false;
            Done?.Invoke();
            return true;
        }
        public override void TestDo() => Done?.Invoke();
    }

    /// <summary>Take someone alive: get close without him knowing, then F. Dead, he's no use.</summary>
    public sealed class Capture : Step
    {
        public Func<Guard?> Who = null!;
        public string What = "", Label = "OFFICER";
        public Action? Done;
        public override string Text => Who() is { State: Guard.Mood.Alert } ? $"{What}. He's seen you: don't kill him!" : What;
        public override string Marker => Label;
        public override float MarkerLift => 2.4f;
        public override Vector3? Target => Who() is { } g && IsInstanceValid(g) ? g.GlobalPosition : null;
        public override bool Tick(float dt)
        {
            if (Who() is not { } g || !IsInstanceValid(g) || g.State == Guard.Mood.Dead) { M.Fail("He was worth more alive. HQ is... disappointed."); return false; }
            if (M.Player.Dead || PlayerAt.DistanceTo(g.GlobalPosition) > 2.4f || g.State == Guard.Mood.Alert) return false;
            DeadRecon.UI.Hud.Prompt(g.Out ? "F   Cuff him and get him up" : "F   \"Hands up. Quietly.\"   (or G: the hard way)");
            if (!Pressed(Key.F)) return false;
            g.Surrender();
            Done?.Invoke();
            return true;
        }
        public override void TestDo() { if (Who() is { } g) { g.Surrender(); Done?.Invoke(); } }
    }

    /// <summary>Get out: home (once you have one) or the landing zone, with everyone you're bringing.</summary>
    public sealed class GetOut : Step
    {
        public override bool IsExtract => true;
        public override string Text => (M.ExtractHome ? "get home" : "reach extraction (green smoke)") +
                                       (M.Escorts.Count > 0 ? $"  (with {string.Join(", ", M.Escorts.ConvertAll(e => e.Name))})" : "");
        public override string Marker => M.ExtractHome ? "HOME" : "EXTRACT";
        public override float MarkerLift => 4f;
        public override Vector3? Target => M.Extraction;
        public override bool Tick(float dt)
        {
            float r = M.ExtractHome ? HomeBase.Radius : ExtractRadius;
            if (M.Player.Dead || Flat(PlayerAt - M.Extraction) > r) return false;
            foreach (var (v, _) in M.Escorts) if (IsInstanceValid(v) && Flat(v.GlobalPosition - M.Extraction) > r + 5) return false;
            if (M.Captive is { } c && IsInstanceValid(c) && Flat(c.GlobalPosition - M.Extraction) > r + 5) return false;
            return true;
        }
    }

    /// <summary>The first operation's ending: set the stolen radio up somewhere quiet. That's home.</summary>
    public sealed class SetUpRadio : Step
    {
        public override bool IsExtract => true;
        public override string Text => "set up the radio somewhere quiet (Tab, right-click the radio), 250 m+ from their camp";
        public override bool Tick(float dt) => HomeBase.Saved != null;
    }
}
