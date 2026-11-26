using System;
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
        public string What = "", Label = "", Doing = "taking it";
        public float Seconds = 1.6f;
        public Action? Done;
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
        public string What = "";
        public override string Text => What;
        public override string Marker => "PRISONER";
        public override Vector3? Target => Who()?.GlobalPosition;
        public override bool ShowDistance => false;
        public override bool Tick(float dt) => Who() is not { } v || v.Following;
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
        public override bool Tick(float dt) => !M.Player.Dead && Flat(PlayerAt - At) < Radius;
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
