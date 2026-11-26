using System;
using System.Collections.Generic;
using System.Linq;
using DeadRecon.AI;
using DeadRecon.Base;
using DeadRecon.Gear;
using DeadRecon.People;
using DeadRecon.World;
using Godot;

namespace DeadRecon.Missions;

/// <summary>
/// The kinds of operation (docs/MISSIONS_PLAN.md, section 1): what each is called, pays, and says on the radio; where
/// the ones away from an enemy camp happen (a road, a crash site, a drop, a village); their props; their steps.
/// Adding a kind: an <see cref="Op"/> value, an <see cref="Info"/> line, its places and props here, and its steps in
/// <see cref="MoreRecipe"/>.
/// </summary>
public partial class Mission
{
    /// <summary>Name, pay (building supplies), whether it's at an enemy camp, where (for the radio), the radio's pitch.</summary>
    public sealed record OpInfo(string Name, int Pay, bool Camp, string Where, string Blurb);

    private const string CampWhere = "a camp 300-600 m from home";

    public static readonly Dictionary<Op, OpInfo> Info = new()
    {
        [Op.None] = new("No operation", 0, false, "", ""),
        [Op.Radio] = new("Steal a radio", 3, true, "a camp nearby", "Take the field radio from their desk and set it up somewhere quiet. That's home."),
        [Op.Intel] = new("Steal intel", 5, true, CampWhere, "Their camp has something on the radio desk we'd love to read. Codebooks, schedules, peach orders. Take it and come home."),
        [Op.Cache] = new("Blow up a supply cache", 7, true, CampWhere, "Their supply cache: ammunition, fuel, canned peaches. Plant a charge on it, then be elsewhere when it goes up. Loud. Very satisfying."),
        [Op.Officer] = new("Take out an officer", 6, true, CampWhere, "An officer is visiting one of their camps, mostly to shout at people. He sits by the radio desk. He doesn't get to leave."),
        [Op.Prisoner] = new("Rescue a prisoner", 8, true, CampWhere, "One of ours is in a bamboo cage at their camp. Pick the lock, then bring him home. He walks slower than you. He also complains."),
        [Op.Recon] = new("Photo recon", 5, true, CampWhere, "Photograph their camp: the radio, a tent, the fire. Nothing to shoot, and better if nobody ever knows you were there (bonus for no alarm)."),
        [Op.Eavesdrop] = new("Eavesdrop", 6, true, CampWhere, "Sit within 25 m of their radio for a minute while they chat. Hold still while the patrols walk past. If they spot you, they stop talking."),
        [Op.Mast] = new("Radio mast", 6, true, CampWhere, "Blow up their radio mast. No mast, no calling for help: bring it down before the alarm and nobody comes to their rescue."),
        [Op.Fuel] = new("Fuel dump", 8, true, CampWhere, "A fuel dump. An enormous bang that wakes the whole valley, and the valley sends people to look. Have a way out."),
        [Op.Capture] = new("Capture an officer", 10, true, CampWhere, "An officer at their camp. We want him alive. Sneak up, hands up, walk him home. Take your eyes off him and he'll run."),
        [Op.Ambush] = new("Ambush the patrol", 7, false, "a road 300-650 m from home", "A patrol walks this road. Get into position first, let them come to you, spring it. Their leader carries a map case we want."),
        [Op.Pilot] = new("Downed pilot", 9, false, "jungle 350-650 m from home", "A spotter plane went down. Find the crash, find the pilot (he's hiding), get him to a clearing and hold it until the helicopter comes. They're looking for him too."),
        [Op.Drop] = new("Supply drop", 6, false, "300-550 m from home", "HQ dropped us a crate and it landed in their country. Find it before they do. What's inside is yours."),
        [Op.Trader] = new("Escort the trader", 5, false, "a village to the market town", "A trader wants to get to market without being robbed by men with rifles. Walk him there. There's a patrol on that road."),
        [Op.Defend] = new("Hold the village", 10, false, "a village that helped you", "They're sending men to punish a village that helped you. Get there first and drive them off. More come when the first lot run into trouble."),
    };

    public static bool HasCamp(Op kind) => Info[kind].Camp;

    // ================================================================= places (operations away from a camp)

    private List<Vector3> _route = new();
    private readonly List<Guard> _targets = new();
    private Guard? _leader;
    private Villager? _pilot, _trader;
    private Vector3 _pilotAt, _ambushAt;
    private Settlements.Site? _site, _market;
    private bool _secondWave, _commsCut;
    private float _bang = 1, _broken;
    private readonly List<Vector3> _tents = new();
    private Vector3 _firePos, _lootPos;
    private Node3D? _mast;

    private void PlanAway(Vector3 spawn)
    {
        var towns = Settlements.Current;
        var home = HomeBase.Position;
        ExtractHome = home != null;
        _reinforced = true;   // no camp radio to call for help; these operations send their own trouble
        switch (Kind)
        {
            case Op.Ambush:
            {
                var spots = new List<(Vector3 P, int Road, int I)>();
                if (towns != null)
                    for (int r = 0; r < towns.Roads.Count; r++)
                    {
                        var pts = towns.Roads[r].Points;
                        for (int i = 40; i < pts.Length - 14; i += 3)
                        {
                            float d = Flat(pts[i] - spawn);
                            if (d < 300 || d > 650 || towns.Sites.Exists(s => Flat(s.Centre - pts[i]) < s.Radius + 60)) continue;
                            if (home is { } h && Flat(h - pts[i]) < 200) continue;
                            spots.Add((pts[i], r, i));
                        }
                    }
                if (spots.Count > 0)
                {
                    var (p, road, at) = spots[_rng.RandiRange(0, spots.Count - 1)];
                    var pts = towns!.Roads[road].Points;
                    Camp = Ground(p);
                    for (int i = Math.Max(0, at - 40); i <= Math.Min(pts.Length - 1, at + 14); i += 2) _route.Add(Ground(pts[i]));
                    // Cover beside the road, off to one side.
                    var along = (pts[Math.Min(pts.Length - 1, at + 2)] - pts[Math.Max(0, at - 2)]) with { Y = 0 };
                    var side = new Vector3(-along.Z, 0, along.X).Normalized();
                    _ambushAt = Ground(Camp + side * 12);
                }
                else
                {
                    // No road out here: their patrol walks a jungle trail instead.
                    Camp = PickSite(spawn, 300, 600, 6, null) ?? spawn + new Vector3(300, 0, 0);
                    var dir = (Camp - spawn).Normalized();
                    for (int i = -12; i <= 6; i++) _route.Add(Ground(Camp + dir.Rotated(Vector3.Up, 1.3f) * i * 12));
                    _ambushAt = Ground(Camp + dir * -12);
                }
                IntelAt = Camp;
                break;
            }
            case Op.Pilot:
                Camp = PickSite(spawn, 350, 650, 8, null) ?? spawn + new Vector3(400, 0, 0);
                float a = _rng.Randf() * MathF.Tau;
                _pilotAt = Ground(Camp + new Vector3(MathF.Cos(a), 0, MathF.Sin(a)) * _rng.RandfRange(30, 50));
                Extraction = PickSite(Camp, 130, 220, LzRadius, avoid: spawn) ?? Camp + new Vector3(160, 0, 0);
                ExtractHome = false;
                IntelAt = Camp;
                TreeScatter.Clearings.Add((new Vector2(Camp.X, Camp.Z), 10));
                TreeScatter.Clearings.Add((new Vector2(Extraction.X, Extraction.Z), LzRadius + 4));
                break;
            case Op.Drop:
                Camp = IntelAt = PickSite(spawn, 300, 550, 4, null) ?? spawn + new Vector3(350, 0, 0);
                TreeScatter.Clearings.Add((new Vector2(Camp.X, Camp.Z), 5));
                break;
            case Op.Trader:
            {
                var pairs = towns == null ? new List<(Settlements.Site, Settlements.Site)>()
                    : towns.Sites.Where(s => s.Kind is "village" or "hamlet")
                        .Select(v => (v, m: towns.Sites.Where(t => t.Kind == "town" && t != v).OrderBy(t => Flat(t.Centre - v.Centre)).FirstOrDefault()))
                        .Where(p => p.m != null && Flat(p.m.Centre - p.v.Centre) is > 350 and < 2200 && Flat(p.v.Centre - spawn) < 1500)
                        .OrderBy(p => Flat(p.v.Centre - spawn)).Select(p => (p.v, p.m!)).ToList();
                if (pairs.Count == 0) { Kind = Op.Drop; PlanAway(spawn); return; }
                (_site, _market) = pairs[Math.Min(pairs.Count - 1, _rng.RandiRange(0, 2))];
                Camp = Ground(_site.Centre);
                IntelAt = Ground(_market.Centre);
                break;
            }
            case Op.Defend:
            {
                var near = towns?.Sites.Where(s => s.Kind is "village" or "hamlet" && Flat(s.Centre - spawn) is > 200 and < 1500).ToList() ?? new();
                if (near.Count == 0) { Kind = Op.Drop; PlanAway(spawn); return; }
                _site = near.OrderByDescending(s => Support.Get(s.Index) >= 15 ? 1 : 0).ThenBy(s => Flat(s.Centre - spawn)).First();
                Camp = IntelAt = Ground(_site.Centre);
                break;
            }
        }
        if (ExtractHome && home is { } h2) Extraction = h2 with { Y = World.HeightAt(h2.X, h2.Z) };
        else if (Kind != Op.Pilot) Extraction = PickSite(Camp, 150, 240, LzRadius, avoid: spawn) ?? spawn;
        GD.Print($"[mission] {OperationName} ({Describe(Kind)}): {Flat(Camp - spawn):0} m away{(_site != null ? $" ({_site.Name}{(_market != null ? $" to {_market.Name}" : "")})" : "")}");
    }

    /// <summary>The props of the operations away from a camp: a crashed plane, a parachuted crate, people.</summary>
    private void BuildAway()
    {
        var olive = Mat(.3f, .33f, .2f, .7f);
        switch (Kind)
        {
            case Op.Pilot:
            {
                var wreck = new Node3D();
                AddChild(wreck);
                wreck.GlobalPosition = Ground(Camp);
                wreck.Rotation = new Vector3(0, _rng.Randf() * MathF.Tau, 0);
                void Part(Vector3 size, Vector3 at, Vector3 rot, Material m)
                {
                    var b = new StaticBody3D { Position = at, Rotation = rot };
                    b.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = size }, MaterialOverride = m });
                    b.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = size } });
                    wreck.AddChild(b);
                }
                var burnt = Mat(.12f, .11f, .1f);
                Part(new Vector3(1.3f, 1.2f, 5.5f), new Vector3(0, .45f, 0), new Vector3(.12f, 0, .2f), olive);          // fuselage, nose in the dirt
                Part(new Vector3(4.2f, .12f, 1.3f), new Vector3(2.4f, .25f, .6f), new Vector3(0, .3f, -.25f), olive);     // the wing that stayed on
                Part(new Vector3(3.4f, .12f, 1.2f), new Vector3(-6, .1f, 3), new Vector3(0, 1.2f, .1f), burnt);           // the one that didn't
                Part(new Vector3(.12f, 1.1f, 1), new Vector3(0, 1.1f, -2.6f), new Vector3(0, 0, .3f), olive);             // tail fin
                AddChild(Smoke(Ground(Camp, .8f), new Color(.15f, .14f, .13f, .5f), 2.5f));
                var faces = SoldierBody.Catalogue();
                if (faces.Count > 0)
                {
                    _pilot = new Villager
                    {
                        Lost = true, Player = Player, World = World, Seed = 4242, Layout = new Villages.SiteLayout(),
                        CharacterScene = faces[_rng.RandiRange(0, faces.Count - 1)], Name = "Pilot",
                        FoundPrompt = "\"You the pilot? Come on, we're leaving.\"",
                        FoundLine = "Oh thank God. My leg's not great. Lead the way, I'll keep up. Mostly.",
                        Calls = new[] { "Psst! Over here! ...You're friendly, right?", "Help! I'm in the bushes! The thorny ones!", "Mayday... mayday... anyone?", "Don't shoot! I'm the one who crashed!" },
                    };
                    AddChild(_pilot);
                    _pilot.GlobalPosition = _pilotAt;
                }
                break;
            }
            case Op.Drop:
            {
                Prop(this, new BoxMesh { Size = new Vector3(1.1f, .8f, .8f) }, olive, Ground(IntelAt, .4f), _rng.Randf(), new Vector3(1.1f, .8f, .8f));
                var chute = new MeshInstance3D
                {
                    Mesh = new SphereMesh { Radius = 2.4f, Height = .9f, IsHemisphere = true },
                    MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(.5f, .49f, .42f), Roughness = 1, CullMode = BaseMaterial3D.CullModeEnum.Disabled },
                };
                AddChild(chute);
                chute.GlobalPosition = Ground(IntelAt + new Vector3(2.2f, 0, 1.4f), .1f);
                chute.Rotation = new Vector3(.25f, _rng.Randf(), .15f);
                chute.Scale = new Vector3(1, .35f, 1);
                break;
            }
            case Op.Trader:
            {
                var faces = SoldierBody.Catalogue("res://assets/villagers/blocky").Where(f => !f.Contains("headman")).ToList();
                if (faces.Count > 0 && _site != null)
                {
                    _trader = new Villager
                    {
                        Lost = true, Player = Player, World = World, Seed = 5151, Layout = new Villages.SiteLayout(),
                        CharacterScene = faces[_rng.RandiRange(0, faces.Count - 1)], Name = "Trader",
                        FoundPrompt = "\"Ready? Stay close to me.\"",
                        FoundLine = "Finally! The rice won't sell itself. Well, it might. But not here.",
                        Calls = new[] { "Is that my escort? You're smaller than they said.", "Over here! I'm the one with the enormous basket!", "We're going to be late. Markets wait for nobody." },
                    };
                    AddChild(_trader);
                    _trader.GlobalPosition = Ground(Camp + new Vector3(3, 0, 2), .3f);
                }
                break;
            }
        }
    }

    /// <summary>Camp props for the new camp operations: a radio mast, a fuel dump.</summary>
    private void BuildCampExtras(Node3D camp)
    {
        switch (Kind)
        {
            case Op.Mast:
            {
                _mast = new Node3D();
                camp.AddChild(_mast);
                _mast.GlobalPosition = Ground(IntelAt);
                var steel = Mat(.25f, .26f, .24f, .5f);
                for (int i = 0; i < 3; i++)
                {
                    float a = i * MathF.Tau / 3;
                    _mast.AddChild(new MeshInstance3D
                    {
                        Mesh = new CylinderMesh { TopRadius = .03f, BottomRadius = .05f, Height = 11 }, MaterialOverride = steel,
                        Position = new Vector3(MathF.Cos(a) * .25f, 5.5f, MathF.Sin(a) * .25f),
                    });
                }
                for (int k = 1; k < 11; k++)
                    _mast.AddChild(new MeshInstance3D { Mesh = new TorusMesh { InnerRadius = .2f, OuterRadius = .26f, Rings = 6, RingSegments = 6 }, MaterialOverride = steel, Position = new Vector3(0, k, 0) });
                _mast.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(.5f, .35f, .5f) }, MaterialOverride = Mat(.35f, .37f, .25f), Position = new Vector3(0, 11, 0) });
                var body = new StaticBody3D();
                body.AddChild(new CollisionShape3D { Shape = new CylinderShape3D { Radius = .35f, Height = 11 }, Position = new Vector3(0, 5.5f, 0) });
                _mast.AddChild(body);
                _cache.Add(_mast);
                break;
            }
            case Op.Fuel:
            {
                var red = Mat(.5f, .14f, .1f);
                var drab = Mat(.28f, .31f, .19f, .6f);
                for (int i = 0; i < 14; i++)
                {
                    var at = Ground(IntelAt + new Vector3((i % 5 - 2) * .7f, 0, (i / 5 - 1) * .7f), .45f + (i >= 10 ? .9f : 0));
                    var drum = Prop(camp, new CylinderMesh { TopRadius = .3f, BottomRadius = .3f, Height = .9f }, i % 3 == 0 ? red : drab, at, 0, new Vector3(.6f, .9f, .6f));
                    _cache.Add(drum);
                }
                _bang = 2.2f;
                break;
            }
        }
    }

    // ================================================================= trouble: patrols, search parties, attackers

    /// <summary>A group of n heading for <paramref name="to"/> from <paramref name="from"/> (they're coming for something).</summary>
    private List<Guard> Group(Vector3 from, Vector3 to, int n)
    {
        var list = new List<Guard>();
        if (SpawnGuard == null) return list;
        for (int k = 0; k < n; k++)
        {
            var at = from + new Vector3((k % 3 - 1) * 2.5f, 0, (k / 3) * 2.5f);
            var g = SpawnGuard(at, false, guard => guard.Home = to);
            g.Respond(to);
            Guards.Add(g); _targets.Add(g); list.Add(g);
        }
        return list;
    }

    private Vector3 FromAway(Vector3 target, float distance)
    {
        var dir = HomeBase.Position is { } home ? (target - home) with { Y = 0 } : new Vector3(_rng.RandfRange(-1, 1), 0, _rng.RandfRange(-1, 1));
        if (dir.LengthSquared() < .01f) dir = Vector3.Forward;
        var at = target + dir.Normalized().Rotated(Vector3.Up, _rng.RandfRange(-.6f, .6f)) * distance;
        return Ground(at, .2f);
    }

    /// <summary>The ambushed patrol: four men walking the road toward your spot, the leader in front.</summary>
    private void SendPatrol()
    {
        if (SpawnGuard == null || _route.Count < 2) return;
        for (int k = 0; k < 4; k++)
        {
            int start = k;
            var g = SpawnGuard(_route[0] - (_route[1] - _route[0]).Normalized() * k * 3, false, guard =>
            {
                guard.Route = _route; guard.RouteStart = 0; guard.Home = _route[0];
            });
            Guards.Add(g); _targets.Add(g);
            if (k == 0) { _leader = g; g.Leader = true; }
        }
        GD.Print($"[mission] the patrol sets off, {Flat(_route[0] - Camp):0} m up the road");
    }

    // ================================================================= the steps of the new operations

    private IEnumerable<Step> MoreRecipe()
    {
        switch (Kind)
        {
            case Op.Recon:
                yield return new Photograph
                {
                    Sights = new()
                    {
                        ("their radio", new Vector3(_desk.X, _deskTop + .25f, _desk.Z)),
                        ("a tent", _tents.Count > 0 ? _tents[0] + Vector3.Up * .6f : Camp + Vector3.Up),
                        ("the campfire", _firePos + Vector3.Up * .4f),
                        ("the supply crates", _lootPos + Vector3.Up * .4f),
                    },
                };
                yield return new GetOut();
                break;
            case Op.Eavesdrop:
                yield return new Listen
                {
                    At = () => _desk,
                    Overheard = new[] { "...no, the OTHER peaches...", "...the convoy's moved to Thursday. Again...", "...Captain wants to know who keeps stealing the radio...", "...tell the checkpoint at the river they're relieved at dawn..." },
                };
                yield return new GetOut();
                break;
            case Op.Mast:
                yield return new Hold
                {
                    At = () => IntelAt + Vector3.Up * .6f, What = "plant a charge on the radio mast", Label = "MAST", Seconds = 3.5f, Verb = "setting the charge",
                    Done = () => Hud("Charge set: 20 seconds. Somewhere else would be good."),
                };
                yield return new Fuse { Seconds = 20, At = () => IntelAt, Boom = () => { Explode(); TopplesMast(); }, Replay = () => { _commsCut = true; if (_mast != null) _mast.QueueFree(); } };
                yield return new GetOut();
                break;
            case Op.Fuel:
                yield return new Hold
                {
                    At = () => IntelAt, What = "plant a charge on the fuel dump", Label = "FUEL", Seconds = 4, Verb = "setting the charge",
                    Done = () => Hud("Charge set: 30 seconds. This one's big. Run."),
                };
                yield return new Fuse
                {
                    Seconds = 30, At = () => IntelAt, Boom = Explode,
                    Replay = () => { foreach (var n in _cache) if (IsInstanceValid(n)) n.QueueFree(); _cache.Clear(); },
                };
                yield return new GetOut();
                break;
            case Op.Capture:
                yield return new Capture
                {
                    Who = () => Officer, What = "take the officer alive: sneak up and F (or knock him out with G, then F)",
                    Done = () => { Captive = Officer; Hud("Got him. He walks in front of you. Keep your eyes on him."); GD.Print("[mission] officer captured"); },
                    Replay = () => { if (Officer != null) { Officer.Surrender(); Officer.GlobalPosition = Player.GlobalPosition + Player.GlobalTransform.Basis * new Vector3(0, 0, -3); Captive = Officer; } },
                };
                yield return new GetOut();
                break;
            case Op.Ambush:
                yield return new GoTo { At = _ambushAt, Radius = 18, What = "get into position beside the road before the patrol comes", Label = "AMBUSH" };
                yield return new WaitFor
                {
                    OnStart = SendPatrol, What = "wait for the patrol. Let them walk into it", Label = "ROAD", Where = () => Camp,
                    Until = () => _targets.Exists(g => IsInstanceValid(g) && (g.GlobalPosition.DistanceTo(Camp) < 45 || g.TimesAlerted > 0 || g.State == Guard.Mood.Dead)),
                };
                yield return new KillAll { Who = () => _targets, What = "spring the ambush", Label = "PATROL" };
                yield return new SearchBody
                {
                    Who = () => _leader, What = "take the patrol leader's map case", Prompt = "Take his map case", Label = "MAP CASE",
                    Done = () => { Kit.Current?.AddIntel("the patrol leader's map case"); Hud("Got the map case. Get it home."); },
                };
                yield return new GetOut();
                break;
            case Op.Pilot:
                yield return new GoTo { At = Camp, Radius = 14, What = "find the crash site", Label = "CRASH" };
                yield return new Talk
                {
                    Who = () => _pilot, What = "find the pilot: he's hiding somewhere near. Listen for him, then F", Show = false,
                    Done = () => { if (_pilot != null) Escorts.Add((_pilot, "the pilot")); },
                    Replay = () => { if (_pilot == null) return; _pilot.Follow(); _pilot.GlobalPosition = Player.GlobalPosition + Player.GlobalTransform.Basis * new Vector3(1.2f, 0, 2); Escorts.Add((_pilot, "the pilot")); },
                };
                yield return new GoTo { At = Extraction, Radius = LzRadius, WithEscorts = true, What = "get him to the landing zone (green smoke)", Label = "LZ" };
                yield return new HoldOut
                {
                    At = () => Extraction, Radius = 25, Seconds = 75, What = "hold the landing zone until the helicopter comes",
                    OnStart = () => { Group(FromAway(Extraction, 320), Extraction, 4); Hud("Radio: a search party's heading your way. The helicopter's 75 seconds out."); },
                    Done = () =>
                    {
                        Escorts.Clear();
                        if (_pilot != null && IsInstanceValid(_pilot)) _pilot.QueueFree();
                        Hud("Rotors over the trees. It drops, he's in, it's gone. You walk home the slow way.");
                    },
                };
                break;
            case Op.Drop:
                yield return new Hold
                {
                    At = () => IntelAt, What = "find the supply drop before they do, and open it", Label = "CRATE", Seconds = 4, Verb = "prising it open",
                    OnStart = () => Group(FromAway(IntelAt, 280), IntelAt, 3),
                    Done = () =>
                    {
                        if (Kit.Current is not { } kit) return;
                        int got = 0;
                        for (int k = 0; k < 6; k++) if (kit.Add(ItemDef.Supplies, 1, quiet: true)) got++;
                        bool mags = kit.Add(ItemDef.Mag30, 2, quiet: true), med = kit.Add(ItemDef.Bandage, 2, quiet: true);
                        Hud($"Inside: {got} building supplies{(mags ? ", two long magazines" : "")}{(med ? ", dressings" : "")}. And a note: \"Don't eat the peaches.\"");
                    },
                };
                yield return new GetOut();
                break;
            case Op.Trader:
                yield return new Talk
                {
                    Who = () => _trader, What = $"meet the trader in {_site?.Name}, then F to tell him to follow you", Label = "TRADER",
                    Done = () => { if (_trader != null) Escorts.Add((_trader, "the trader")); },
                    Replay = () => { if (_trader == null) return; _trader.Follow(); _trader.GlobalPosition = Player.GlobalPosition + Player.GlobalTransform.Basis * new Vector3(1.2f, 0, 2); Escorts.Add((_trader, "the trader")); },
                };
                yield return new GoTo
                {
                    At = IntelAt, Radius = 22, WithEscorts = true, What = $"walk him to the market in {_market?.Name}", Label = "MARKET",
                    OnStart = () =>
                    {
                        // A patrol loitering on the way, about halfway.
                        var mid = Ground(Camp.Lerp(IntelAt, _rng.RandfRange(.4f, .6f)), .2f);
                        if (Settlements.Current is { } towns)
                        {
                            var best = towns.Roads.SelectMany(r => r.Points).OrderBy(p => Flat(p - mid)).FirstOrDefault();
                            if (best != default && Flat(best - mid) < 120) mid = Ground(best, .2f);
                        }
                        for (int k = 0; k < 3 && SpawnGuard != null; k++)
                        {
                            var g = SpawnGuard(mid + new Vector3(k * 2, 0, k), false, guard => { guard.Home = mid; guard.PatrolMin = 10; guard.PatrolMax = 25; });
                            Guards.Add(g); _targets.Add(g);
                        }
                    },
                };
                break;
            case Op.Defend:
                yield return new GoTo
                {
                    At = Camp, Radius = 40, What = $"get to {_site?.Name} before they do", Label = "VILLAGE",
                    OnStart = () => { Group(FromAway(Camp, 450), Camp, 5); GD.Print("[mission] the punishment squad sets off"); },
                };
                yield return new KillAll
                {
                    Who = () => _targets, What = $"drive them out of {_site?.Name}", Label = "ENEMY",
                    Every = dt =>
                    {
                        int alive = _targets.Count(g => IsInstanceValid(g) && g.State != Guard.Mood.Dead);
                        if (!_secondWave && alive <= 2)
                        {
                            _secondWave = true;
                            Group(FromAway(Camp, 420), Camp, 4);
                            Hud("Radio: a second squad's on its way from the checkpoint. Four men.");
                            GD.Print("[mission] second wave");
                            return false;
                        }
                        // The last one standing, with his friends down and nobody else coming, runs.
                        if (_secondWave && alive == 1) { _broken += dt; if (_broken > 20) { Hud("The last one throws down his rifle and runs. They won't be back soon."); return true; } }
                        return false;
                    },
                };
                break;
        }
    }

    /// <summary>The mast leans, creaks and comes down. Their radio is a very quiet box now.</summary>
    private void TopplesMast()
    {
        _commsCut = true;
        if (_mast != null && IsInstanceValid(_mast))
        {
            var tween = CreateTween();
            tween.TweenProperty(_mast, "rotation", new Vector3(0, _mast.Rotation.Y, 1.45f), 2.2f).SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.In);
        }
        Hud(_reinforceIn > 0 && !_reinforced ? "The mast's down, but they already called for help. Someone's coming." : "The mast's down. Nobody's getting a call from this camp.");
        GD.Print("[mission] mast down");
    }

    // ================================================================= words

    private string BriefingJob(string where) => Kind switch
    {
        Op.Radio => $"You're on your own out here, with no way to call anyone. There's {where}, and they have a field radio on a desk. " +
                    "Take it. Then find a quiet spot, well away from them, and set it up (Tab, right-click the radio). That's home.",
        Op.Cache => $"Their supply cache at {where}: ammunition, fuel, canned peaches. Plant a charge on it (hold F), then be somewhere else when it goes.",
        Op.Officer => $"An officer is visiting {where}. Mostly to shout at people. He's the one by the radio desk. He doesn't leave.",
        Op.Prisoner => $"{_prisonerName} was taken two days ago and is being kept in a bamboo cage at {where}. Pick the lock (hold F) and bring him out.",
        Op.Recon => $"Photograph {where}: their radio, a tent, the fire, the crates. Look at each, hold F. Better if nobody ever knows you were there (bonus pay for no alarm).",
        Op.Eavesdrop => $"Their radio at {where} chatters all day. Get within 25 m of it and listen for a minute. If they spot you, they stop talking.",
        Op.Mast => $"A radio mast at {where}. Bring it down (hold F, then run) and the camp can't call for help. Do it before the alarm.",
        Op.Fuel => $"A fuel dump at {where}. When it goes, the whole valley will hear, and come to see. Plant the charge and have a way out.",
        Op.Capture => $"An officer at {where}. Alive, please: he knows things. Sneak right up to him and press F. He walks in front of you; if you look away, he runs.",
        Op.Ambush => $"A patrol uses the road about {Flat(Camp - Player.GlobalPosition):0} m {Compass(Camp - Player.GlobalPosition)} of you. Get into position beside it first, let them come, spring it. Their leader has a map case.",
        Op.Pilot => $"A spotter plane went down about {Flat(Camp - Player.GlobalPosition):0} m {Compass(Camp - Player.GlobalPosition)} of you. Find it, then the pilot (he'll be hiding; listen). Get him to the landing zone and hold it until the helicopter comes. They're looking for him too.",
        Op.Drop => $"HQ dropped us a crate about {Flat(Camp - Player.GlobalPosition):0} m {Compass(Camp - Player.GlobalPosition)} of you. It came down in their country, and they saw the parachute. Get there first.",
        Op.Trader => $"A trader in {_site?.Name} wants to get to the market in {_market?.Name} without being robbed. Walk him there. There's a patrol on that road.",
        Op.Defend => $"They're sending a squad to punish {_site?.Name} for helping you. It's {Flat(Camp - Player.GlobalPosition):0} m {Compass(Camp - Player.GlobalPosition)} of you. Get there first and drive them off. When the first lot run into trouble, more will come.",
        _ => $"Take {IntelName} from the radio desk at {where}. {_why}",
    };

    private string DoneText() => Kind switch
    {
        Op.Radio => "The radio's up. It crackles. That's home now: operations, news and gossip come through it, and you can build here.",
        Op.Cache => "Their cache is a smoking hole.",
        Op.Officer => "The officer won't be shouting at anyone any more.",
        Op.Prisoner => $"{_prisonerName} is safe.",
        Op.Recon => "HQ has the photos. They've already circled things on them in red.",
        Op.Eavesdrop => "HQ is very interested in Thursday's convoy. And the peaches.",
        Op.Mast => "Their mast is scrap. That camp is on its own now.",
        Op.Fuel => "The valley will be talking about that fireball for weeks.",
        Op.Capture => "The officer is in your camp, sulking. He's started talking, mostly about how unfair this is.",
        Op.Ambush => "The patrol won't be walking that road again, and HQ has the leader's map case.",
        Op.Pilot => "The pilot's on his way to a hospital and a stern talk about trees.",
        Op.Drop => "You got there first. HQ says the peaches were a joke. We're not sure.",
        Op.Trader => $"The trader made it to {_market?.Name}. He's already haggling. The village won't forget it.",
        Op.Defend => $"{_site?.Name} is still standing. They'll remember who came.",
        _ => $"You got {IntelName} out.",
    };

    /// <summary>The extras: a ghost run's bonus, a village's gratitude.</summary>
    private void Bonuses()
    {
        bool quiet = !Guards.Take(_campGuards).Any(g => IsInstanceValid(g) && g.TimesAlerted > 0);
        if (Kind == Op.Recon && quiet && Kit.Current is { } kit)
        {
            for (int k = 0; k < 3; k++) kit.Add(ItemDef.Supplies, 1, quiet: true);
            Hud("Bonus: nobody ever knew you were there. +3 building supplies.");
        }
        if (Kind == Op.Trader && _trader != null && IsInstanceValid(_trader))
        {
            Escorts.Clear();
            _trader.Rest(Ground(IntelAt + new Vector3(4, 0, 3)));
            _trader.Say("Here we are! Now, who wants rice?");
        }
        if (Kind is Op.Trader or Op.Defend && _site != null) Support.Change(_site.Index, Kind == Op.Defend ? 20 : 10, Kind == Op.Defend ? "you defended them" : "you got their trader to market");
        if (Kind == Op.Capture && Captive != null && IsInstanceValid(Captive)) { Captive.QueueFree(); Captive = null; }
    }
}
