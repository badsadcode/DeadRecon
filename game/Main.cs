using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DeadRecon.AI;
using DeadRecon.Combat;
using DeadRecon.Missions;
using DeadRecon.Player;
using DeadRecon.World;
using Godot;

namespace DeadRecon;

/// <summary>
/// Milestone-0 test scene: load the baked world, stream terrain and trees around a first-person walker.
/// User args (after "--"): --world=&lt;name&gt; (default jungle), --shots=&lt;dir&gt; to save a few screenshots and quit.
/// </summary>
public partial class Main : Node3D
{
	private BakedWorld _world = null!;
	private TerrainStreamer _terrain = null!;
	private TreeScatter _trees = null!;
	private GroundCover _cover = null!;
	private Weapon _weapon = null!;
	private Label _crosshair = null!;
	private Walker _walker = null!;
	private Label _hud = null!;
	private Guard _guard = null!;
	private readonly System.Collections.Generic.List<Guard> _guards = new();
	private Mission _mission = null!;
	private Weather _weather = null!;
	private TimeOfDay _time = null!;
	private static Vector3? _continueFrom;
	private static float _continueYaw;
	private static bool _carryOn;
	private static float? _lastHour;
	private Settlements _towns = null!;
	private Villages _villages = null!;
	private Rocks? _rocks;
	private DeadRecon.People.Squad? _squadNode;
	private DeadRecon.People.Wildlife? _wildlife;
	private DeadRecon.Gear.Kit _kit = null!;
	private DeadRecon.UI.KitScreen _kitScreen = null!;
	private DeadRecon.AI.Patrols? _patrols;
	private DeadRecon.People.VillageLife _life = null!;
	private DeadRecon.UI.PauseMenu _menu = null!;
	private ColorRect _hurt = null!;
	private Label _deathText = null!;
	private (Vector3 position, float yaw) _spawn;
	private bool _waitingForGround = true;

	public override void _Ready()
	{
		var args = OS.GetCmdlineUserArgs();
		string Arg(string name, string fallback) =>
			args.FirstOrDefault(a => a.StartsWith($"--{name}="))?.Split('=', 2)[1] ?? fallback;

		DeadRecon.UI.Settings.Load();
		DeadRecon.Audio.Ambience.SetupBuses();
		_hud = new Label { Position = new Vector2(12, 10) };
		_hud.AddThemeColorOverride("font_shadow_color", Colors.Black);
		var layer = new CanvasLayer();
		layer.AddChild(_hud);
		AddChild(layer);

		// The baked world: the project's world/ folder when run from the project, or world/ next to the EXE.
		string dir = OS.HasFeature("editor") || OS.HasFeature("template") == false
			? ProjectSettings.GlobalizePath($"res://world/{Arg("world", "jungle")}")
			: Path.Combine(Path.GetDirectoryName(OS.GetExecutablePath())!, "world", Arg("world", "jungle"));
		if (!File.Exists(Path.Combine(dir, "world.json")))
		{
			_hud.Text = $"No baked world at {dir}\nBake it first:\n  dotnet run -c Release --project tools/WorldBaker -- content/world/jungle.world.json";
			return;
		}
		_world = BakedWorld.Load(dir);
		_towns = Settlements.Load(dir, _world);
		bool testing = args.Any(a => a.StartsWith("--shots") || a.StartsWith("--aitest") || a.StartsWith("--treetest") || a.StartsWith("--raintest") || a.StartsWith("--missiontest") || a.StartsWith("--squadtest") || a.StartsWith("--hudtest") || a.StartsWith("--chartest") || a.StartsWith("--ragdolltest") || a.StartsWith("--movetest") || a.StartsWith("--guntest") || a.StartsWith("--rangetest") || a.StartsWith("--daytest") || a.StartsWith("--villagetest") || a.StartsWith("--lifetest") || a.StartsWith("--kittest") || a.StartsWith("--patroltest") || a.StartsWith("--jobtest") || a.StartsWith("--basetest") || a.StartsWith("--optest") || a.StartsWith("--squadbasetest") || a.StartsWith("--risktest") || a.StartsWith("--zootest") || a.StartsWith("--snaketest") || a.StartsWith("--rocktest"));
		DeadRecon.People.Support.Persist = !testing;   // tests neither read nor write your saved villages
		DeadRecon.People.Support.Load(_world.Name);
		Foliage.Clear(); // static registry: a fresh run (new operation) re-registers what it plants

		var worldEnv = MakeEnvironment();
		AddChild(worldEnv);
		var sun = new DirectionalLight3D
		{
			RotationDegrees = new Vector3(-42, 35, 0), ShadowEnabled = true, LightEnergy = 1.3f,
			LightColor = new Color(1f, .93f, .80f), DirectionalShadowMaxDistance = 200,
		};
		AddChild(sun);
		var moon = new DirectionalLight3D { LightColor = new Color(.62f, .72f, 1f), LightEnergy = 0, DirectionalShadowMaxDistance = 120, ShadowBlur = 2 };
		AddChild(moon);

		_walker = new Walker { ProcessMode = ProcessModeEnum.Disabled };
		AddChild(_walker);
		var spawn = FindSpawn();
		// After a finished operation the next one starts where you were standing.
		if (_continueFrom is { } here)
		{
			spawn = (here with { Y = _world.HeightAt(here.X, here.Z) + .2f }, _continueYaw);
			_continueFrom = null;
		}
		// With a home, you start there (unless you're carrying on from where an operation ended).
		DeadRecon.Base.HomeBase.Persist = !testing;
		if (!testing && !_carryOn && DeadRecon.Base.HomeBase.Saved is { } homeState)
		{
			var hb = new Basis(Vector3.Up, homeState.Yaw);
			var at = new Vector3(homeState.X, 0, homeState.Z) + hb * new Vector3(1.5f, 0, 2.5f);
			spawn = (at with { Y = _world.HeightAt(at.X, at.Z) + .3f }, Mathf.RadToDeg(homeState.Yaw));
		}
		_carryOn = false;
		DeadRecon.Gear.Kit.ClearLoot();
		Ladders.Clear();
		_spawn = spawn;
		_walker.GlobalPosition = spawn.position;
		_walker.Look(spawn.yaw, -4);
		DeadRecon.Gear.Kit.Persist = !testing;
		_kit = new DeadRecon.Gear.Kit { Player = _walker };
		AddChild(_kit);
		AddChild(new DeadRecon.Gear.IconBaker());
		_weapon = new Weapon { Walker = _walker };
		_walker.Camera.AddChild(_weapon);
		_crosshair = new Label { Text = "·", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
		_crosshair.AddThemeFontSizeOverride("font_size", 34);
		_crosshair.SetAnchorsPreset(Control.LayoutPreset.Center);
		layer.AddChild(_crosshair);
		_hurt = new ColorRect { Color = new Color(.6f, 0, 0, 0), MouseFilter = Control.MouseFilterEnum.Ignore };
		_hurt.SetAnchorsPreset(Control.LayoutPreset.FullRect);
		layer.AddChild(_hurt);
		_deathText = new Label
		{
			HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Visible = false,
			Text = "",
		};
		_deathText.AddThemeFontSizeOverride("font_size", 34);
		_deathText.AddThemeColorOverride("font_shadow_color", Colors.Black);
		_deathText.SetAnchorsPreset(Control.LayoutPreset.FullRect);
		layer.AddChild(_deathText);

		// The operation: picked before planting so the camp and landing zone are clearings.
		int seed = int.TryParse(Arg("seed", ""), out int fixedSeed) ? fixedSeed : (int)(Time.GetUnixTimeFromSystem() * 1000 % int.MaxValue);
		_mission = new Mission { World = _world, Player = _walker, Rifle = _weapon, SkipBriefing = testing };
		// Which operation: what the radio picked; otherwise (no home yet) go and steal a radio; at home, none.
		var op = Mission.Op.Intel;
		if (Mission.Next is { } next) { op = next.Kind; seed = next.Seed; Mission.Next = null; }
		else if (!testing) op = DeadRecon.Base.HomeBase.Saved != null ? Mission.Op.None : Mission.Op.Radio;
		if (Arg("op", "") is { Length: > 0 } opArg && Enum.TryParse<Mission.Op>(opArg, true, out var forced)) op = forced;
		_mission.Plan(seed, spawn.position, op);
		_mission.StartHour = op == Mission.Op.None ? (TimeOfDay.Current?.Hour ?? _lastHour ?? TimeOfDay.StartHourFor(seed)) : TimeOfDay.StartHourFor(seed);
		if (DeadRecon.Base.HomeBase.Position is { } homePos) TreeScatter.Clearings.Add((new Vector2(homePos.X, homePos.Z), DeadRecon.Base.HomeBase.Radius + 4));

		_terrain = new TerrainStreamer { World = _world, Viewer = _walker };
		_trees = new TreeScatter { World = _world, Viewer = _walker };
		_cover = new GroundCover { World = _world, Viewer = _walker };
		AddChild(_terrain);
		AddChild(_trees);
		AddChild(_cover);
		_villages = new Villages { World = _world, Towns = _towns, Viewer = _walker };
		AddChild(_villages);
		AddChild(new DeadRecon.Base.HomeBase { Player = _walker, World = _world });
		_squadNode = new DeadRecon.People.Squad { Player = _walker, World = _world, OnOperation = op != Mission.Op.None };
		AddChild(_squadNode);
		_wildlife = new DeadRecon.People.Wildlife { Player = _walker, World = _world, Towns = _towns };
		AddChild(_wildlife);
		AddChild(new DeadRecon.Base.Structures { Player = _walker, World = _world });
		_rocks = new Rocks { World = _world, Viewer = _walker };
		AddChild(_rocks);

		// The camp's guards: a grumpy patroller and a grumpier sentry by the radio desk.
		Guard AddGuard(Vector3 home, bool sentry)
		{
			home.Y = _world.HeightAt(home.X, home.Z) + .2f;
			var g = new Guard { WeaponId = DeadRecon.Gear.WeaponDef.ForGuard(new RandomNumberGenerator { Seed = (ulong)(home.X * 7919 + home.Z * 104729 + 3) }).Id, Target = _walker, Terrain = _terrain, World = _world, Home = home, Sentry = sentry, PatrolMin = 16, PatrolMax = 26 };
			AddChild(g);
			g.GlobalPosition = home;
			_guards.Add(g);
			_mission.Guards.Add(g);
			return g;
		}
		if (op != Mission.Op.None)
		{
		_guard = AddGuard(_mission.Camp + new Vector3(0, 0, -2), sentry: false);
		var sentryGuard = AddGuard(_mission.Desk + new Vector3(-.8f, 0, 1.6f), sentry: true);
		if (op == Mission.Op.Officer) _mission.Officer = sentryGuard;
		if (op == Mission.Op.Prisoner) _mission.KeyHolder = sentryGuard;
		// Bigger camps than before: a second patroller always, and a pair roving round the edge.
		var g2 = AddGuard(_mission.Camp + new Vector3(4, 0, 5), sentry: false);
		if (!testing || op == Mission.Op.Intel && Arg("squadbasetest", "") != "")
		{
			var r1 = AddGuard(_mission.Camp + new Vector3(-20, 0, 0), sentry: false); r1.PatrolMin = 32; r1.PatrolMax = 45;
			var r2 = AddGuard(_mission.Camp + new Vector3(-22, 0, 3), sentry: false); r2.PatrolMin = 32; r2.PatrolMax = 45;
		}
		// Bigger camps on higher difficulty and some operations: extra patrollers walking wider loops.
		var extra = new RandomNumberGenerator { Seed = (ulong)seed * 31 + 7 };
		int extras = DeadRecon.UI.Settings.Difficulty switch
		{
			DeadRecon.UI.Settings.Level.Recruit => 0,
			DeadRecon.UI.Settings.Level.Veteran => extra.RandiRange(1, 2),
			_ => extra.RandiRange(0, 1),
		};
		for (int k = 0; k < extras; k++)
		{
			float a = extra.Randf() * MathF.Tau;
			var g = AddGuard(_mission.Camp + new Vector3(MathF.Cos(a), 0, MathF.Sin(a)) * 12, sentry: false);
			g.PatrolMin = 24; g.PatrolMax = 38;
		}
		}
		_mission.SpawnGuard = (home, sentry) =>
		{
			home.Y = _world.HeightAt(home.X, home.Z) + .2f;
			var g = new Guard { WeaponId = DeadRecon.Gear.WeaponDef.ForGuard(new RandomNumberGenerator { Seed = (ulong)(home.X * 31 + home.Z * 17) }).Id, Target = _walker, Terrain = _terrain, World = _world, Home = _mission.Camp, Sentry = false, PatrolMin = 10, PatrolMax = 25 };
			AddChild(g);
			g.GlobalPosition = home;
			_guards.Add(g);
			return g;
		};
		_mission.Trees = _trees;
		AddChild(_mission);
		// Village people. Guards they ask you to deal with (checkpoints) are spawned through here.
		_life = new DeadRecon.People.VillageLife
		{
			Villages = _villages, Towns = _towns, Player = _walker, World = _world, Mission = _mission,
			SpawnGuard = (home, sentry) =>
			{
				home.Y = _world.HeightAt(home.X, home.Z) + .2f;
				var g = new Guard { WeaponId = DeadRecon.Gear.WeaponDef.ForGuard(new RandomNumberGenerator { Seed = (ulong)(home.X * 7919 + home.Z * 104729 + 3) }).Id, Target = _walker, Terrain = _terrain, World = _world, Home = home, Sentry = sentry, PatrolMin = 8, PatrolMax = 16 };
				AddChild(g);
				g.GlobalPosition = home;
				_guards.Add(g);
				return g;
			},
		};
		AddChild(_life);
		AddChild(new DeadRecon.People.JobBoard { Towns = _towns, Player = _walker, World = _world, Life = _life, SpawnGuard = _life.SpawnGuard });
		// Checkpoint garrisons and road patrols. Off in scripted tests (they'd wander into them), except their own.
		if (!testing || Arg("patroltest", "") != "" || Arg("jobtest", "") != "")
		{
			_patrols = new DeadRecon.AI.Patrols
			{
				Towns = _towns, Player = _walker, World = _world,
				Spawn = (home, sentry, setup) =>
				{
					home.Y = _world.HeightAt(home.X, home.Z) + .2f;
					var g = new Guard { WeaponId = DeadRecon.Gear.WeaponDef.ForGuard(new RandomNumberGenerator { Seed = (ulong)(home.X * 7919 + home.Z * 104729 + 3) }).Id, Target = _walker, Terrain = _terrain, World = _world, Home = home, Sentry = sentry };
					setup?.Invoke(g);
					AddChild(g);
					g.GlobalPosition = home;
					_guards.Add(g);
					return g;
				},
				Despawn = g => { _guards.Remove(g); g.QueueFree(); },
			};
			AddChild(_patrols);
		}

		_weather = new Weather { Camera = _walker.Camera, Environment = worldEnv.Environment, Sun = sun, Terrain = _terrain, Trees = _trees, Cover = _cover };
		AddChild(_weather);
		_time = new TimeOfDay
		{
			Environment = worldEnv.Environment, Sun = sun, Moon = moon, Sky = (ShaderMaterial)worldEnv.Environment.Sky.SkyMaterial,
			Hour = _mission.StartHour,
		};
		AddChild(_time);
		AddChild(new Fireflies { Camera = _walker.Camera, World = _world });
		AddChild(new Atmosphere { Camera = _walker.Camera, World = _world });
		AddChild(new DeadRecon.Audio.Ambience { Camera = _walker.Camera });
		AddChild(new DeadRecon.Combat.Ballistics());
		_walker.SurfaceAt = p =>
		{
			var (gx, gz) = _world.ToSample(p.X, p.Z);
			if (_world.IsWet((int)gx, (int)gz) || (_towns.FieldAt(p.X, p.Z) > .9f && p.Y < _world.HeightAt(p.X, p.Z) + .3f)) return DeadRecon.Audio.Synth.Surface.Water;
			if (p.Y > _world.HeightAt(p.X, p.Z) + .6f) return DeadRecon.Audio.Synth.Surface.Wood;   // floors, verandas, stairs
			if (_towns.RoadAt(p.X, p.Z) > .4f || _towns.YardAt(p.X, p.Z) > .5f)
				return _weather.Wetness > .45f ? DeadRecon.Audio.Synth.Surface.Mud : DeadRecon.Audio.Synth.Surface.Dirt;
			return _weather.Wetness > .45f ? DeadRecon.Audio.Synth.Surface.Mud : DeadRecon.Audio.Synth.Surface.Leaves;
		};

		// Player HUD (the debug text above is F3) and the Esc menu.
		// (--clean=1: photo runs for the devlog, no HUD or debug text)
		layer.AddChild(new DeadRecon.UI.Hud { Player = _walker, Rifle = _weapon, Mission = _mission, Weather = _weather, Visible = Arg("clean", "") == "" });
		// Tab: the kit screen (backpack and gunsmith), above the HUD.
		var kitLayer = new CanvasLayer { Layer = 5 };
		AddChild(kitLayer);
		DeadRecon.UI.MapMemory.Persist = !testing;
		AddChild(new DeadRecon.UI.MapMemory { Player = _walker, World = _world });
		DeadRecon.UI.MapPainter.Warm(_world, _towns);   // paints in the background; ready long before you press M
		kitLayer.AddChild(new DeadRecon.Base.RadioScreen
		{
			Player = _walker,
			StartOperation = (kind, opSeed) =>
			{
				Mission.Next = (kind, opSeed);
				_lastHour = null;
				GetTree().ReloadCurrentScene();
			},
		});
		_kitScreen = new DeadRecon.UI.KitScreen { Kit = _kit, Player = _walker, World = _world, Towns = _towns, Mission = _mission };
		kitLayer.AddChild(_kitScreen);
		_menu = new DeadRecon.UI.PauseMenu { Walker = _walker };
		_menu.Changed += () => _hud.Visible = DeadRecon.UI.Settings.ShowDebug;
		AddChild(_menu);
		_hud.Visible = (DeadRecon.UI.Settings.ShowDebug || testing) && Arg("clean", "") == "";

		string shots = Arg("shots", "");
		if (shots != "") _ = TakeShots(shots);
		string aiTest = Arg("aitest", "");
		if (aiTest != "") _ = RunAiTest(aiTest);
		string treeTest = Arg("treetest", "");
		if (treeTest != "") _ = RunTreeTest(treeTest);
		string rainTest = Arg("raintest", "");
		if (rainTest != "") _ = RunRainTest(rainTest);
		string missionTest = Arg("missiontest", "");
		if (missionTest != "") _ = RunMissionTest(missionTest);
		string viewModel = Arg("viewmodel", "");
		if (viewModel != "") { _viewOnly = true; _ = ViewModel(viewModel, Arg("out", "user://view")); return; }
		string dumpContent = Arg("dumpcontent", "");
		if (dumpContent != "")
		{
			// Every editable section, written out as the starting point for content/game (and for modders).
			_ = DeadRecon.Gear.ItemDef.All; _ = DeadRecon.Gear.WeaponDef.All;
			DeadRecon.Data.Content.LineSets("guard_lines", Guard.Lines);
			DeadRecon.Data.Content.Get("guard_chats", Guard.Chats.Select(c => new[] { c.A, c.B }).ToArray());
			DeadRecon.Data.Content.LineSets("villager_lines", DeadRecon.People.Villager.Lines);
			DeadRecon.Data.Content.Strings("operation_adjectives", Mission.Adjectives);
			DeadRecon.Data.Content.Strings("operation_nouns", Mission.Nouns);
			DeadRecon.Data.Content.Get("intel", Array.ConvertAll(Mission.Intel, i => new Mission.IntelDef(i.Item, i.Why)));
			DeadRecon.Data.Content.Strings("soldier_names", Mission.Prisoners);
			DeadRecon.Data.Content.Get("radio_banter", DeadRecon.Base.Chatter.Banter.Select(b => new DeadRecon.Base.Chatter.Line(b.Who, b.Text)).ToArray());
			DeadRecon.Data.Content.Strings("lost_villager_names", new[] { "Old Bao", "Uncle Tam", "Young Kiet", "Grandpa Lum", "Cousin Vinh", "Brother Hoa" });
			GD.Print($"[content] mods: {string.Join(", ", DeadRecon.Data.Content.Loaded)}; black rifle damage {DeadRecon.Gear.WeaponDef.Get("rifle_4121").Damage}; nouns {DeadRecon.Data.Content.Strings("operation_nouns", Mission.Nouns).Length}; mutter lines {DeadRecon.Data.Content.LineSets("guard_lines", Guard.Lines)["mutter"].Length}");
			DeadRecon.Data.Content.Dump(dumpContent, new()
			{
				["items.json"] = new[] { "items" }, ["weapons.json"] = new[] { "weapons" },
				["names.json"] = new[] { "operation_adjectives", "operation_nouns", "intel", "soldier_names", "lost_villager_names" },
				["lines.json"] = new[] { "guard_lines", "guard_chats", "villager_lines", "radio_banter" },
			});
			GetTree().Quit();
			return;
		}
		string soundTest = Arg("soundtest", "");
		if (soundTest != "")
		{
			// Export every synthesised sound as a WAV file, then quit.
			Directory.CreateDirectory(soundTest);
			void Save(string name, AudioStreamWav w) => w.SaveToWav(Path.Combine(soundTest, name + ".wav"));
			foreach (var surf in Enum.GetValues<DeadRecon.Audio.Synth.Surface>()) for (int v = 0; v < 3; v++) Save($"step_{surf}_{v}", DeadRecon.Audio.Synth.Step(surf, v));
			Save("voice_grumble", DeadRecon.Audio.Synth.Voice("Twelve hours. In the rain. Again.", 105, 1));
			Save("voice_shout", DeadRecon.Audio.Synth.Voice("THERE you are!", 105, 1));
			Save("voice_high", DeadRecon.Audio.Synth.Voice("Hello? Monkey?", 130, 7));
			Save("cicadas_loop", DeadRecon.Audio.Synth.Cicadas());
			Save("jungle_bed_loop", DeadRecon.Audio.Synth.JungleBed());
			for (int k = 0; k < 4; k++) Save($"bird_{k}", DeadRecon.Audio.Synth.Bird(k, 1));
			Save("shot_echo", DeadRecon.Audio.Synth.ShotEcho());
			Save("gunshot", DeadRecon.Combat.Effects.Gunshot());
			GD.Print($"[sound] saved to {soundTest}");
			GetTree().Quit();
			return;
		}
		string moveTest = Arg("movetest", "");
		if (moveTest != "") _ = RunMoveTest(moveTest);
		string rangeTest = Arg("rangetest", "");
		if (rangeTest != "") _ = RunRangeTest(rangeTest);
		string gunTest = Arg("guntest", "");
		if (gunTest != "") _ = RunGunTest(gunTest);
		string baseTest = Arg("basetest", "");
		if (baseTest != "") _ = RunBaseTest(baseTest);
		if (Arg("risktest", "") != "") _ = RunRiskTest();
		if (Arg("zootest", "") != "") _ = RunZooTest(Arg("zootest", ""));
		if (Arg("snaketest", "") != "") _ = RunSnakeTest(Arg("snaketest", ""));
		if (Arg("rocktest", "") != "") _ = RunRockTest(Arg("rocktest", ""));
		string sbTest = Arg("squadbasetest", "");
		if (sbTest != "") _ = RunSquadBaseTest(sbTest);
		string opTest = Arg("optest", "");
		if (opTest != "") _ = RunOpTest(opTest);
		string jobTest = Arg("jobtest", "");
		if (jobTest != "") _ = RunJobTest(jobTest);
		string patrolTest = Arg("patroltest", "");
		if (patrolTest != "") _ = RunPatrolTest(patrolTest);
		string kitTest = Arg("kittest", "");
		if (kitTest != "") _ = RunKitTest(kitTest);
		string lifeTest = Arg("lifetest", "");
		if (lifeTest != "") _ = RunLifeTest(lifeTest);
		string villageTest = Arg("villagetest", "");
		if (villageTest != "") _ = RunVillageTest(villageTest);
		string dayTest = Arg("daytest", "");
		if (dayTest != "") _ = RunDayTest(dayTest);
		string ragTest = Arg("ragdolltest", "");
		if (ragTest != "") _ = RunRagdollTest(ragTest);
		string charTest = Arg("chartest", "");
		if (charTest != "") _ = RunCharTest(charTest);
		string hudTest = Arg("hudtest", "");
		if (hudTest != "") _ = RunHudTest(hudTest);
		string squadTest = Arg("squadtest", "");
		if (squadTest != "") _ = RunSquadTest(squadTest);
	}

	private bool _viewOnly;

	public override void _Process(double delta)
	{
		if (_viewOnly) return;
		if (_terrain == null) return;
		if (_waitingForGround && _terrain.HasCollisionAt(_walker.GlobalPosition))
		{
			_waitingForGround = false;
			_walker.ProcessMode = ProcessModeEnum.Inherit;
		}
		var p = _walker.GlobalPosition;
		_hud.Text =
			$"DEAD RECON - terrain test   {Engine.GetFramesPerSecond()} fps\n" +
			$"pos {p.X:0} {p.Y:0} {p.Z:0}   ground {_world.HeightAt(p.X, p.Z):0} m   {(_walker.Flying ? "FLYING" : "walking")}\n" +
			$"trees {_trees.TreesShown:N0} ({_trees.VariantCount} kinds)   undergrowth {_cover.PlantsShown:N0}   ground: {(_terrain.TextureMix > .5f ? "textured" : "painted")}\n" +
			(_weapon.Loaded ? $"rifle {_weapon.Ammo}/20{(_weapon.Reloading ? "  reloading..." : "")}\n" : "") +
			$"{_mission.Objective()}\n" +
			$"health {_walker.Health:0}   nearest guard: {GuardStatus()}\n" +
			$"{CoverStatus()}\n" +
			$"weather: {_weather.Describe()}  (rain {_weather.Intensity * 100:0}%, wet {_weather.Wetness * 100:0}%, canopy overhead {_weather.UnderCanopy * 100:0}%)" +
			(_weather.Intensity > .05f ? $"   footsteps carry {Hearing.Carry(Hearing.RunningSteps):0}/{Hearing.RunningSteps:0} m, guard sees {_weather.SightFactor * 100:0}% as far\n" : "\n") +
			(_waitingForGround ? "loading ground...\n" : "") +
			"click: grab mouse   WASD/Shift/Space  C crouch (hold) / Alt toggle  Q/E lean   LMB fire  RMB aim  R reload   F: take/talk/search  Tab: kit  M: map  H: dressing  L: light  F10: fly (Space/Ctrl up/down)   N: weather  K: +2 hours   T: textured/painted ground   Esc: release mouse";
		_hurt.Color = new Color(.6f, 0, 0, _walker.Dead ? .35f : _walker.HurtFlash * .45f);
		_deathText.Visible = false; // the mission panel says it
		_crosshair.Visible = _weapon.AimAmount < .5f;
	}

	public override void _UnhandledInput(InputEvent e)
	{
		if (_viewOnly) return;
		if (e is InputEventKey { Pressed: true, Echo: false, PhysicalKeycode: Key.F9 } && _hud != null)
		{
			DeadRecon.UI.Settings.ShowDebug = !DeadRecon.UI.Settings.ShowDebug;
			DeadRecon.UI.Settings.Save();
			_hud.Visible = DeadRecon.UI.Settings.ShowDebug;
		}
		if (e is InputEventKey { Pressed: true, Echo: false, PhysicalKeycode: Key.K } && _time != null)
			_time.Hour = (_time.Hour + 2) % 24;   // debug: skip ahead two hours
		if (e is InputEventKey { Pressed: true, Echo: false, PhysicalKeycode: Key.N } && _weather != null)
			_weather.Cycle();
		if (e is InputEventKey { Pressed: true, Echo: false, PhysicalKeycode: Key.T } && _terrain != null)
			_terrain.TextureMix = _terrain.TextureMix > .5f ? 0 : 1;
		// Dead or done: Enter starts a new operation (same jungle, new camp; felled trees stay felled).
		if (e is InputEventKey { Pressed: true, Echo: false, PhysicalKeycode: Key.Enter or Key.KpEnter } && _mission != null
			&& (_walker.Dead || _mission.State == Mission.Phase.Failed || (_mission.State == Mission.Phase.Complete && DeadRecon.Base.HomeBase.Saved == null)))
		{
			// No home yet: the next operation starts where you're standing. With a home (or after dying), back home.
			if (!_walker.Dead && !_walker.Flying && DeadRecon.Base.HomeBase.Saved == null)
			{
				_continueFrom = _walker.GlobalPosition; _continueYaw = Mathf.RadToDeg(_walker.Rotation.Y); _carryOn = true;
			}
			GetTree().ReloadCurrentScene();
		}
	}

	/// <summary>
	/// Automated guard check: stand in front of him, let him notice, get shot at, shoot back. Screenshots + log, then quit.
	/// </summary>
	private async Task RunAiTest(string dir)
	{
		Directory.CreateDirectory(dir);
		async Task Frames(int n) { for (int f = 0; f < n; f++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
		async Task Seconds(double s) => await ToSignal(GetTree().CreateTimer(s), SceneTreeTimer.SignalName.Timeout);
		async Task Shot(string name)
		{
			await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
			GetViewport().GetTexture().GetImage().SavePng(Path.Combine(dir, name + ".png"));
			GD.Print($"[test] shot {name}: player health {_walker.Health:0}, guard {GuardStatus()}");
		}
		// A photo of him from 4.5 m: fly there (he ignores a flying player), snap, come back.
		async Task CloseUp(string name)
		{
			var back = _walker.GlobalPosition;
			_walker.SetFlying(true);
			var f = -_guard.GlobalTransform.Basis.Z; f.Y = 0; f = f.Normalized();
			var side = f.Cross(Vector3.Up);
			var eye = _guard.GlobalPosition + f * 4 + side * 1.5f;
			eye.Y = _world.HeightAt(eye.X, eye.Z) + .3f;
			_walker.GlobalPosition = eye;
			LookAt(_guard.GlobalPosition + Vector3.Up * (_guard.State == Guard.Mood.Dead ? .3f : 1.1f));
			await Frames(4);
			await Shot(name);
			_walker.GlobalPosition = back;
			_walker.SetFlying(false);
			await Frames(2);
		}
		void LookAt(Vector3 target)
		{
			var d = target - _walker.Camera.GlobalPosition;
			_walker.Look(Mathf.RadToDeg(MathF.Atan2(-d.X, -d.Z)), Mathf.RadToDeg(MathF.Atan2(d.Y, new Vector2(d.X, d.Z).Length())));
		}

		for (int f = 0; f < 1800 && (_waitingForGround || !_terrain.HasCollisionAt(_guard.GlobalPosition) || _trees.Pending > 0); f++) await Frames(1);
		await Seconds(2); // let him take a few patrol steps
		GD.Print($"[test] start: guard {GuardStatus()}");
		MeasureCover();
		await CloseUp("0_guard");

		// 1. Stand still 28 m in front of him, facing him.
		var g = _guard.GlobalPosition;
		var fwd = -_guard.GlobalTransform.Basis.Z; fwd.Y = 0; fwd = fwd.Normalized();
		var stand = g + fwd * 28;
		stand.Y = _world.HeightAt(stand.X, stand.Z) + .2f;
		_walker.GlobalPosition = stand;
		LookAt(_guard.GlobalPosition + Vector3.Up * 1.4f);
		double t0 = Time.GetTicksMsec() / 1000.0;
		double lastMove = t0;
		while (_guard.State != Guard.Mood.Alert && Time.GetTicksMsec() / 1000.0 - t0 < 25)
		{
			// Until he has noticed something, keep stepping back into his view (he patrols, so he turns away).
			double now = Time.GetTicksMsec() / 1000.0;
			if (_guard.Suspicion < .3f && now - lastMove > 3)
			{
				// The most visible spot 20-30 m in front of him (plants hide many spots now).
				lastMove = now;
				var f2 = -_guard.GlobalTransform.Basis.Z; f2.Y = 0; f2 = f2.Normalized();
				var eye = _guard.GlobalPosition + Vector3.Up * 1.6f;
				Vector3 best = Vector3.Zero; float bestT = -1;
				for (int c = 0; c < 32; c++)
				{
					float ang = (c % 8 - 3.5f) * .12f, r = 20 + c / 8 * 3.3f;
					var along = f2.Rotated(Vector3.Up, ang);
					var p2 = _guard.GlobalPosition + along * r;
					p2.Y = _world.HeightAt(p2.X, p2.Z) + .2f;
					float tr = Foliage.Transmission(eye, p2 + Vector3.Up * 1.3f);
					if (tr > bestT) { bestT = tr; best = p2; }
				}
				_walker.GlobalPosition = best;
				GD.Print($"[test] standing in the open-ish: plants let {bestT * 100:0}% through, {best.DistanceTo(_guard.GlobalPosition):0} m");
			}
			LookAt(_guard.GlobalPosition + Vector3.Up * 1.4f);
			await Frames(1);
		}
		GD.Print($"[test] alerted after {Time.GetTicksMsec() / 1000.0 - t0:0.0} s");
		await Seconds(.4);
		_weapon.ForceAim(true); // through the scope, so the picture shows him
		LookAt(_guard.GlobalPosition + Vector3.Up * 1.3f);
		await Frames(3);
		await Shot("a_spotted");
		_weapon.ForceAim(false);

		// 2. Get shot at for a bit.
		double h0 = _walker.Health;
		for (int f = 0; f < 150; f++) { LookAt(_guard.GlobalPosition + Vector3.Up * 1.4f); await Frames(1); }
		GD.Print($"[test] under fire: health {h0:0} -> {_walker.Health:0}");
		await Shot("b_under_fire");

		// 3. Shoot back (aimed), body shots until he goes down.
		_weapon.ForceAim(true);
		for (int k = 0; k < 8 && _guard.State != Guard.Mood.Dead && !_walker.Dead; k++)
		{
			LookAt(_guard.GlobalPosition + Vector3.Up * 1.15f);
			await Frames(2);
			_weapon.TestFire();
			await Seconds(.2);
		}
		await Seconds(1.2);
		LookAt(_guard.GlobalPosition + Vector3.Up * .5f);
		await Frames(3);
		await Shot("c_result");
		_weapon.ForceAim(false);
		await CloseUp("d_down");
		GD.Print($"[test] done: player {(_walker.Dead ? "dead" : $"alive ({_walker.Health:0})")}, guard {_guard.State}");
		GetTree().Quit();
	}

	/// <summary>Shooting range: a still guard at several distances, scope on his chest, 10 shots each; logs every hit.</summary>
	private async Task RunRangeTest(string dir)
	{
		Directory.CreateDirectory(dir);
		async Task Frames(int n) { for (int f = 0; f < n; f++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
		async Task Seconds(double t) => await ToSignal(GetTree().CreateTimer(t), SceneTreeTimer.SignalName.Timeout);
		void LookAt(Vector3 target)
		{
			var d = target - _walker.Camera.GlobalPosition;
			_walker.Look(Mathf.RadToDeg(MathF.Atan2(-d.X, -d.Z)), Mathf.RadToDeg(MathF.Atan2(d.Y, new Vector2(d.X, d.Z).Length())));
		}
		_weather.AutoChange = false;
		_walker.Invulnerable = true;
		var g = _guards.First(x => !x.Sentry);
		// Stand still. Not ProcessMode.Disabled: that also removes a body from physics, and bullets pass through.
		foreach (var x in _guards) { x.SetPhysicsProcess(false); x.SetProcess(false); }
		for (int f = 0; f < 1800 && (_waitingForGround || _trees.Pending > 0); f++) await Frames(1);
		_walker.SetFlying(true);
		_walker.GlobalPosition = g.GlobalPosition + Vector3.Up * 30;
		for (int f = 0; f < 600 && !_terrain.HasCollisionAt(g.GlobalPosition); f++) await Frames(1);
		await Seconds(1);
		var chest = g.GlobalPosition + Vector3.Up * 1.0f;   // belly: a stray headshot would kill him and spoil the count
		foreach (float dist in new[] { 25f, 50f, 80f, 120f, 200f, 300f })
		{
			// Find a spot at this distance with no trunk in the way (fly, at standing eye height above the ground).
			Vector3 eye = Vector3.Zero; bool found = false;
			for (int k = 0; k < 64 && !found; k++)
			{
				var c = g.GlobalPosition + new Vector3(MathF.Cos(k * .39f) * dist, 0, MathF.Sin(k * .39f) * dist);
				c.Y = _world.HeightAt(c.X, c.Z) + 1.65f;
				if (Foliage.RaycastTrunk(c, chest) != null) continue;
				var q = PhysicsRayQueryParameters3D.Create(c, chest);
				q.Exclude = new Godot.Collections.Array<Rid> { _walker.GetRid() };
				var h = GetWorld3D().DirectSpaceState.IntersectRay(q);
				if (h.Count > 0 && h["collider"].AsGodotObject() != g) continue;    // terrain in the way
				// Check from where the camera will really be.
				_walker.GlobalPosition = c - Vector3.Up * 1.65f;
				await Frames(1);
				var cam0 = _walker.Camera.GlobalPosition;
				var q2 = PhysicsRayQueryParameters3D.Create(cam0, chest);
				q2.Exclude = new Godot.Collections.Array<Rid> { _walker.GetRid() };
				var h2 = GetWorld3D().DirectSpaceState.IntersectRay(q2);
				if (h2.Count > 0 && h2["collider"].AsGodotObject() != g) continue;
				if (_guards.Any(o => o != g && o.GlobalPosition.DistanceTo(cam0) < dist + 2 && (o.GlobalPosition + Vector3.Up - cam0).Normalized().Dot((chest - cam0).Normalized()) > .995f)) continue; // the other guard in the way
				if (Foliage.RaycastTrunk(cam0, chest) != null) continue;
				eye = c; found = true;
			}
			if (!found) { GD.Print($"[range] {dist} m: no clear line"); continue; }
			_weapon.ForceAim(true);
			int hits = 0;
			var log = new System.Collections.Generic.Dictionary<string, int>();
			for (int k = 0; k < 10; k++)
			{
				LookAt(chest);
				await Frames(2);
				float hp = g.Health;
				_weapon.TestReload();
				_weapon.TestFire();
				await Seconds(.15 + dist / 700);   // bullets fly now: wait for it to arrive
				string what = _weapon.LastHit;
				string key = what.Contains("Character") ? (g.Health < hp ? "target" : "OTHER guard") : what.Split(" at ")[0];
				log[key] = (log.TryGetValue(key, out var cnt) ? cnt : 0) + 1;
				if (g.Health < hp) hits++;
				g.SetHealthForTest(100);
				await Seconds(.25);
			}
			if (dist == 80)
			{
				_weapon.ForceAim(false);
				await Seconds(.4);
				LookAt(chest + Vector3.Up * .3f);
				await Frames(2);
				_weapon.TestReload();
				var shooterEye = _walker.Camera.GlobalPosition;
				_weapon.TestFire();
				// Watch from the side: 6 m to the right of the line, 20 m along it, looking across.
				var lineDir = (chest - shooterEye).Normalized();
				var right = lineDir.Cross(Vector3.Up).Normalized();
				var watch = shooterEye + lineDir * 22 + right * 7 + Vector3.Up * .5f;
				_walker.GlobalPosition = watch - Vector3.Up * 1.65f;
				LookAt(shooterEye + lineDir * 34);
				_weapon.ForceAim(false); _weapon.Visible = false;
				await Seconds(.045);   // the comet is mid-flight (~34 m out)
				await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
				GetViewport().GetTexture().GetImage().SavePng(Path.Combine(dir, "tracer_80m.png"));
				await Seconds(.17);   // arriving: the spark
				await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
				GetViewport().GetTexture().GetImage().SavePng(Path.Combine(dir, "tracer_80m_impact.png"));
				_weapon.Visible = true;
				_weapon.ForceAim(true);
			}
			GD.Print($"[range] {dist} m: {hits}/10 hits on the chest; bullets hit: {string.Join(", ", log.Select(kv => $"{kv.Key} x{kv.Value}"))}; sway now {Mathf.RadToDeg(_walker.AimOffset.Length()) * 60:0.0}'");
		}
		GetTree().Quit();
	}

	/// <summary>Gun feel check: scope sway standing / crouched / holding breath, recoil recovery, casings.</summary>
	private async Task RunGunTest(string dir)
	{
		Directory.CreateDirectory(dir);
		async Task Frames(int n) { for (int f = 0; f < n; f++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
		async Task Seconds(double t) => await ToSignal(GetTree().CreateTimer(t), SceneTreeTimer.SignalName.Timeout);
		async Task Shot(string name)
		{
			await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
			GetViewport().GetTexture().GetImage().SavePng(Path.Combine(dir, name + ".png"));
		}
		void Key(Godot.Key k, bool down) => Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = k, Keycode = k, Pressed = down });
		async Task<float> SwayFor(double seconds)
		{
			float max = 0; double end = Time.GetTicksMsec() / 1000.0 + seconds;
			while (Time.GetTicksMsec() / 1000.0 < end) { max = Math.Max(max, _walker.AimOffset.Length()); await Frames(1); }
			return Mathf.RadToDeg(max) * 60; // arc-minutes
		}
		_weather.AutoChange = false;
		foreach (var g in _guards) g.ProcessMode = ProcessModeEnum.Disabled;
		for (int f = 0; f < 1800 && (_waitingForGround || _trees.Pending > 0); f++) await Frames(1);
		_weapon.ForceAim(true);
		await Seconds(1);
		GD.Print($"[gun] sway standing: {await SwayFor(3):0.0} arcmin");
		Key(Godot.Key.C, true); await Seconds(.6);
		GD.Print($"[gun] sway crouched: {await SwayFor(3):0.0} arcmin");
		Key(Godot.Key.C, false);
		await Seconds(.6);
		Key(Godot.Key.Shift, true); await Seconds(.4);
		GD.Print($"[gun] holding breath: {await SwayFor(2):0.0} arcmin, breath {_weapon.Breath:0.00}");
		for (int k = 0; k < 4; k++) { await Seconds(.5); GD.Print($"[gun]   +{(k + 1) * .5:0.0}s breath {_weapon.Breath:0.00} holding {_weapon.HoldingBreath} scoped {_weapon.Scoped} shift {Input.IsPhysicalKeyPressed(Godot.Key.Shift)}"); }
		GD.Print($"[gun] after 4.4 s held: breath {_weapon.Breath:0.00}, holding {_weapon.HoldingBreath}; gasping sway {await SwayFor(1):0.0} arcmin");
		Key(Godot.Key.Shift, false);
		await Shot("1_scope_breath");
		// Recoil: fire 5, measure how much of the climb stays.
		_weapon.ForceAim(false);
		await Seconds(1.5);
		float pitch0 = _walker.Camera.GlobalRotation.X;
		for (int k = 0; k < 5; k++) { _weapon.TestFire(); await Seconds(.1); }
		float peak = _walker.Camera.GlobalRotation.X;
		await Frames(3);
		await Shot("2_casings_flying");
		await Seconds(.6);
		float settled = _walker.Camera.GlobalRotation.X;
		GD.Print($"[gun] recoil over 5 shots: {Mathf.RadToDeg(peak - pitch0):0.0} deg at the end of the burst, {Mathf.RadToDeg(settled - pitch0):0.0} deg left after 0.6 s");
		var casings = GetTree().CurrentScene.GetChildren().OfType<MeshInstance3D>().Where(b => b.Mesh is CylinderMesh { Height: < .05f }).ToList();
		GD.Print($"[gun] casings: {casings.Count}, heights above ground: {string.Join(", ", casings.Select(c => (c.GlobalPosition.Y - _world.HeightAt(c.GlobalPosition.X, c.GlobalPosition.Z)).ToString("0.00")))}");
		_walker.Look(Mathf.RadToDeg(_walker.Rotation.Y) - 60, -50);
		await Frames(3);
		await Shot("3_casings_ground");
		GetTree().Quit();
	}

	/// <summary>Movement check with real key presses: sprint, slide, lean in the open and against a trunk.</summary>
	private async Task RunMoveTest(string dir)
	{
		Directory.CreateDirectory(dir);
		async Task Frames(int n) { for (int f = 0; f < n; f++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
		async Task Seconds(double t) => await ToSignal(GetTree().CreateTimer(t), SceneTreeTimer.SignalName.Timeout);
		async Task Shot(string name)
		{
			await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
			GetViewport().GetTexture().GetImage().SavePng(Path.Combine(dir, name + ".png"));
			GD.Print($"[test] shot {name}");
		}
		void Key(Godot.Key k, bool down) => Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = k, Keycode = k, Pressed = down });
		_hud.Visible = false;
		_weather.AutoChange = false;
		foreach (var g in _guards) g.ProcessMode = ProcessModeEnum.Disabled;
		for (int f = 0; f < 1800 && (_waitingForGround || _trees.Pending > 0 || !_terrain.Settled); f++) await Frames(1);
		await Seconds(.5);

		// Sprint 1.2 s, then C.
		var p0 = _walker.GlobalPosition;
		Key(Godot.Key.Shift, true); Key(Godot.Key.W, true);
		await Seconds(1.2);
		var p1 = _walker.GlobalPosition;
		GD.Print($"[move] sprint: {new Vector2(p1.X - p0.X, p1.Z - p0.Z).Length() / 1.2f:0.0} m/s, running {_walker.Running}");
		Key(Godot.Key.C, true); await Frames(1); Key(Godot.Key.C, false);
		await Frames(12);
		await Shot("1_sliding");
		await Seconds(1);
		var p2 = _walker.GlobalPosition;
		GD.Print($"[move] slide: {new Vector2(p2.X - p1.X, p2.Z - p1.Z).Length():0.0} m in ~1.2 s, now crouched {_walker.Crouching}, sliding {_walker.Sliding}");
		Key(Godot.Key.Shift, false); Key(Godot.Key.W, false);
		Key(Godot.Key.C, true); await Frames(1); Key(Godot.Key.C, false); // stand up
		await Seconds(.5);
		GD.Print($"[move] after C again: crouched {_walker.Crouching}");

		// Lean right in the open.
		Key(Godot.Key.E, true); await Seconds(.6);
		GD.Print($"[move] lean right in the open: {_walker.Lean:0.00}");
		await Shot("2_lean_right");
		Key(Godot.Key.E, false); Key(Godot.Key.Q, true); await Seconds(.8);
		GD.Print($"[move] lean left in the open: {_walker.Lean:0.00}");
		await Shot("3_lean_left");
		Key(Godot.Key.Q, false); await Seconds(.4);

		// Stand with a trunk right at our right shoulder, lean into it.
		var near = Foliage.TrunksNear(_walker.GlobalPosition, 30).OrderBy(t => new Vector2(t.X - _walker.GlobalPosition.X, t.Z - _walker.GlobalPosition.Z).Length()).FirstOrDefault(t => t.TrunkRadius > .2f);
		var fwd = -_walker.GlobalTransform.Basis.Z; fwd.Y = 0; fwd = fwd.Normalized();
		var right = fwd.Cross(Vector3.Up) * -1;
		right = _walker.GlobalTransform.Basis.X; right.Y = 0; right = right.Normalized();
		var spot = new Vector3(near.X, 0, near.Z) - right * (near.TrunkRadius + .45f);
		_walker.GlobalPosition = new Vector3(spot.X, _world.HeightAt(spot.X, spot.Z) + .1f, spot.Z);
		await Seconds(.3);
		Key(Godot.Key.E, true); await Seconds(.6);
		GD.Print($"[move] lean right into a trunk (r {near.TrunkRadius:0.00}): {_walker.Lean:0.00}");
		Key(Godot.Key.E, false); Key(Godot.Key.Q, true); await Seconds(.6);
		GD.Print($"[move] lean left away from it: {_walker.Lean:0.00}, camera roll {Mathf.RadToDeg(MathF.Asin(_walker.Camera.GlobalTransform.Basis.X.Y)):0} deg, eye offset {_walker.Camera.Position.X:0.00} m");
		await Shot("4_peek_from_trunk");
		Key(Godot.Key.Q, false);

		// Walk straight into trunks of different sizes; how close to the axis do we get?
		foreach (float minR in new[] { .25f, .5f, .8f })
		{
			var t = Foliage.TrunksNear(_walker.GlobalPosition, 60).Where(x => x.TrunkRadius >= minR)
				.OrderBy(x => new Vector2(x.X - _walker.GlobalPosition.X, x.Z - _walker.GlobalPosition.Z).Length()).FirstOrDefault();
			if (t.TrunkRadius <= 0) { GD.Print($"[move] no trunk >= {minR}"); continue; }
			var axis = new Vector3(t.X, 0, t.Z);
			var start = axis + new Vector3(3, 0, 0);
			_walker.GlobalPosition = new Vector3(start.X, _world.HeightAt(start.X, start.Z) + .2f, start.Z);
			_walker.Look(90, 0); // face -X, toward the trunk
			await Frames(3);
			float closest = 99;
			Key(Godot.Key.W, true);
			for (int f = 0; f < 120; f++)
			{
				await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
				closest = Math.Min(closest, new Vector2(_walker.GlobalPosition.X - t.X, _walker.GlobalPosition.Z - t.Z).Length());
			}
			Key(Godot.Key.W, false);
			GD.Print($"[move] walked into trunk r {t.TrunkRadius:0.00} (base y {t.BaseY:0.0}, top {t.TrunkTop:0.0}, me y {_walker.GlobalPosition.Y:0.0}): closest {closest:0.00} m from the axis (want >= {t.TrunkRadius + .35f:0.00})");
		}
		GetTree().Quit();
	}

	/// <summary>Atmosphere check: the same two views at seven times of day (clear weather), plus a rainy dusk.</summary>
	/// <summary>Home: steal the radio (--op=radio), set it up, build everything, look at it by day and night, and the
	/// radio screen's three pages.</summary>
	private async Task RunBaseTest(string dir)
	{
		Directory.CreateDirectory(dir);
		async Task Frames(int n) { for (int f = 0; f < n; f++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
		async Task Seconds(double t) => await ToSignal(GetTree().CreateTimer(t), SceneTreeTimer.SignalName.Timeout);
		async Task Shot(string name)
		{
			await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
			GetViewport().GetTexture().GetImage().SavePng(Path.Combine(dir, name + ".png"));
			GD.Print($"[base] shot {name}");
		}
		void LookAt(Vector3 target)
		{
			var d = target - _walker.Camera.GlobalPosition;
			_walker.Look(Mathf.RadToDeg(MathF.Atan2(-d.X, -d.Z)), Mathf.RadToDeg(MathF.Atan2(d.Y, new Vector2(d.X, d.Z).Length())));
		}
		_weather.AutoChange = false; _weather.SetNow(Weather.Kind.Clear);
		_time.Frozen = true; _time.Hour = 10;
		_walker.Invulnerable = true;
		foreach (var g in _guards) { g.SetPhysicsProcess(false); g.SetProcess(false); }
		for (int f = 0; f < 1800 && (_waitingForGround || !_terrain.Settled); f++) await Frames(1);
		GD.Print($"[base] operation: {_mission.Kind}, {_mission.Objective()}");
		_mission.TestTakeIntel();
		GD.Print($"[base] radio in the kit: {_kit.AllItems.Any(i => i.Def == DeadRecon.Gear.ItemDef.FieldRadio)}; {_mission.Objective()}");
		// Somewhere quiet: 320 m from the camp, the other side from it.
		var away = (_spawn.position - _mission.Camp) with { Y = 0 };
		var at = _mission.Camp + away.Normalized() * 330;
		string? why = "";
		for (int k = 0; k < 24 && why != null; k++)
		{
			var tryAt = _mission.Camp + away.Normalized().Rotated(Vector3.Up, k * .5f) * (330 + k * 10);
			tryAt.Y = _world.HeightAt(tryAt.X, tryAt.Z);
			why = DeadRecon.Base.HomeBase.Establish(tryAt, 0, _world);
			if (why == null) at = tryAt; else GD.Print($"[base] not there: {why}");
		}
		var radioItem = _kit.AllItems.FirstOrDefault(i => i.Def == DeadRecon.Gear.ItemDef.FieldRadio);
		if (radioItem != null) _kit.Remove(radioItem);
		await Seconds(1);
		GD.Print($"[base] home at {DeadRecon.Base.HomeBase.Position}: mission {_mission.State}; supplies {_kit.AllItems.Where(i => i.Def == DeadRecon.Gear.ItemDef.Supplies).Sum(i => i.Count)}");
		_kit.Add(DeadRecon.Gear.ItemDef.Supplies, 40, quiet: true);
		foreach (var b in DeadRecon.Base.HomeBase.Catalogue) DeadRecon.Base.HomeBase.Build(b.Id);
		var home = DeadRecon.Base.HomeBase.Position!.Value with { Y = _world.HeightAt(at.X, at.Z) };
		foreach (var h in FindChildren("*", nameof(ColorRect), true, false)) if (h is ColorRect cr && cr.Size == new Vector2(760, 360)) cr.Visible = false;   // the debrief
		_walker.SetFlying(true);
		_walker.GlobalPosition = home + new Vector3(9, 26, 9);
		for (int f = 0; f < 900 && (_trees.Pending > 0 || !_terrain.Settled); f++) await Frames(1);
		await Seconds(1);
		LookAt(home);
		await Shot("1_home_from_above");
		_walker.GlobalPosition = home + new Vector3(4, 1.7f, 6);
		LookAt(home + Vector3.Up * .8f);
		await Frames(20);
		await Shot("2_home_ground");
		_time.Hour = 22;
		await Frames(30);
		await Shot("3_home_night");
		_time.Hour = 10;
		for (int k = 0; k < 6; k++) { var (who, text) = DeadRecon.Base.Chatter.Next(home); DeadRecon.Base.Chatter.Add(who, text); GD.Print($"[base] radio: {who}: {text}"); }
		// Climb the watchtower's ladder.
		var ladder = DeadRecon.Base.HomeBase.Current is { } hbNode ? Ladders.Of(hbNode) : null;
		if (ladder != null)
		{
			_walker.SetFlying(false);
			_walker.GlobalPosition = ladder.Bottom + ladder.Out * 1.1f + Vector3.Up * .3f;
			var face = -ladder.Out;
			_walker.Look(Mathf.RadToDeg(MathF.Atan2(-face.X, -face.Z)), 10);
			await Frames(20);
			Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = Godot.Key.W, Keycode = Godot.Key.W, Pressed = true });
			bool climbed = false;
			for (int f = 0; f < 360; f++) { await Frames(1); if (_walker.Climbing) climbed = true; if (f == 90) await Shot("5_on_the_ladder"); }
			Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = Godot.Key.W, Keycode = Godot.Key.W, Pressed = false });
			await Frames(30);
			GD.Print($"[base] ladder: climbed {climbed}, feet {_walker.GlobalPosition.Y:0.0} (top {ladder.Top.Y:0.0}), still on it {_walker.Climbing}");
			_walker.Look(Mathf.RadToDeg(MathF.Atan2(face.X, face.Z)), -20);
			await Frames(10);
			await Shot("6_from_the_tower");
			// And back down: walk to the ladder edge (facing out), then S.
			_walker.Look(Mathf.RadToDeg(MathF.Atan2(-ladder.Out.X, -ladder.Out.Z)), -30);
			await Frames(5);
			Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = Godot.Key.W, Keycode = Godot.Key.W, Pressed = true });
			for (int f = 0; f < 60 && !_walker.Climbing; f++) await Frames(1);
			Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = Godot.Key.W, Keycode = Godot.Key.W, Pressed = false });
			bool grabbed = _walker.Climbing;
			Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = Godot.Key.S, Keycode = Godot.Key.S, Pressed = true });
			for (int f = 0; f < 400 && _walker.Climbing; f++) await Frames(1);
			Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = Godot.Key.S, Keycode = Godot.Key.S, Pressed = false });
			await Frames(30);
			float groundHere = _world.HeightAt(_walker.GlobalPosition.X, _walker.GlobalPosition.Z);
			GD.Print($"[base] ladder down: grabbed at the top {grabbed}, now {_walker.GlobalPosition.Y - groundHere:0.0} m above the ground, health {_walker.Health:0}");
		}
		var radio = DeadRecon.Base.RadioScreen.Current!;
		for (int page = 0; page < 3; page++) { radio.Open(page); await Frames(5); await Shot($"4_radio_page{page + 1}"); }
		radio.Close();
		foreach (var (kind, opSeed) in DeadRecon.Base.RadioScreen.Offers()) GD.Print($"[base] offer: {Mission.NameFor(opSeed)}: {Mission.Describe(kind)}");
		GetTree().Quit();
	}

	/// <summary>Photograph a model from four sides on a plain background (--viewmodel=res://... --out=dir).</summary>
	private async Task ViewModel(string path, string dir)
	{
		Directory.CreateDirectory(dir);
		var vp = new SubViewport { Size = new Vector2I(900, 700), OwnWorld3D = true, RenderTargetUpdateMode = SubViewport.UpdateMode.Always };
		AddChild(vp);
		vp.AddChild(new WorldEnvironment { Environment = new Godot.Environment { BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = new Color(.55f, .58f, .6f), AmbientLightSource = Godot.Environment.AmbientSource.Color, AmbientLightColor = Colors.White, AmbientLightEnergy = .6f } });
		vp.AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-45, -35, 0), LightEnergy = 1.3f });
		var model = ResourceLoader.Load<PackedScene>(path).Instantiate<Node3D>();
		vp.AddChild(model);
		var box = DeadRecon.Gear.RifleBuilder.Bounds(model);
		GD.Print($"[view] {path}: size {box.Size}, from {box.Position}");
		foreach (var n in DeadRecon.Gear.RifleBuilder.FindAll<MeshInstance3D>(model)) GD.Print($"[view]   {n.Name}: {DeadRecon.Gear.RifleBuilder.InModel(model, n) * n.Mesh.GetAabb()}");
		var cam = new Camera3D { Fov = 40 };
		vp.AddChild(cam);
		float r = box.Size.Length() * 1.4f;
		var c = box.GetCenter();
		int k = 0;
		foreach (var side in new[] { new Vector3(1, .5f, 1), new Vector3(-1, .4f, .3f), new Vector3(0, .3f, -1), new Vector3(.2f, 1.6f, .3f) })
		{
			cam.LookAtFromPosition(c + side.Normalized() * r, c, Vector3.Up);
			for (int f = 0; f < 4; f++) await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
			vp.GetTexture().GetImage().SavePng(Path.Combine(dir, $"view{k++}.png"));
		}
		GetTree().Quit();
	}

	/// <summary>Animals: the nearest village's dogs and livestock, then wildlife beside you, then a gunshot.</summary>
	/// <summary>Photos: the jungle floor (is it green?), then Thornwake's rock formations, wherever the nearest ones are.</summary>
	private async Task RunRockTest(string dir)
	{
		Directory.CreateDirectory(dir);
		async Task Seconds(double t) => await ToSignal(GetTree().CreateTimer(t), SceneTreeTimer.SignalName.Timeout);
		async Task Shot(string name)
		{
			await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
			GetViewport().GetTexture().GetImage().SavePng(Path.Combine(dir, name + ".png"));
			GD.Print($"[rocks] shot {name}");
		}
		async Task Settle(Vector3 at)
		{
			for (int f = 0; f < 2400 && (_trees.Pending > 0 || !_terrain.Settled || !_terrain.HasCollisionAt(at)); f++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
			await Seconds(2.5);
		}
		_weather.AutoChange = false; _weather.SetNow(Weather.Kind.Clear);
		_time.Frozen = true; _time.Hour = 10f;
		_walker.Invulnerable = true;
		foreach (var g in _guards) { g.SetPhysicsProcess(false); g.SetProcess(false); }
		var cam = new Camera3D { Fov = 60, Near = .05f };
		AddChild(cam);
		void Frame(Vector3 from, Vector3 at, float fov) { cam.GlobalPosition = from; cam.LookAt(at); cam.Fov = fov; cam.MakeCurrent(); }
		float H(float x, float z) => _world.HeightAt(x, z);
		var start = _walker.GlobalPosition;
		await Settle(start);
		// The floor, looking along it and from a little above.
		Frame(start + new Vector3(0, 1.7f, 0), new Vector3(start.X + 10, H(start.X + 10, start.Z - 14), start.Z - 14), 60);
		await Shot("1_floor");
		Frame(start + new Vector3(0, 14, 10), new Vector3(start.X, H(start.X, start.Z - 25), start.Z - 25), 60);
		await Shot("2_floor_above");
		// Formations: the nearest few, searching further out until there are some.
		int shots = 0;
		var seen = new System.Collections.Generic.List<Vector3>();
		for (int ring = 0; ring < 10 && shots < 3; ring++)
		{
			var probe = start + new Vector3(MathF.Cos(ring * 1.3f) * ring * 220, 0, MathF.Sin(ring * 1.3f) * ring * 220);
			probe.Y = H(probe.X, probe.Z) + 2;
			_walker.SetFlying(true);
			_walker.GlobalPosition = probe;
			await Settle(probe);
			GD.Print($"[rocks] probe {ring}: {_rocks!.Formations} formations, {_rocks.Count} rocks nearby");
			if (_rocks.NearestFormation(probe) is not { } f || seen.Exists(v => v.DistanceTo(f) < 30)) continue;
			seen.Add(f);
			var from = f + new Vector3(9, 0, 11);
			from.Y = Math.Max(H(from.X, from.Z), f.Y) + 3.5f;
			_walker.GlobalPosition = from;
			await Settle(from);
			Frame(from, f + Vector3.Up * 1.5f, 60);
			await Shot($"{3 + shots}_formation");
			shots++;
		}
		GetTree().Quit();
	}

	private async Task RunSnakeTest(string dir)
	{
		Directory.CreateDirectory(dir);
		async Task Seconds(double t) => await ToSignal(GetTree().CreateTimer(t), SceneTreeTimer.SignalName.Timeout);
		async Task Shot(string name)
		{
			await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
			GetViewport().GetTexture().GetImage().SavePng(Path.Combine(dir, name + ".png"));
			GD.Print($"[snake] shot {name}");
		}
		void LookAt(Vector3 target)
		{
			var d = target - _walker.Camera.GlobalPosition;
			_walker.Look(Mathf.RadToDeg(MathF.Atan2(-d.X, -d.Z)), Mathf.RadToDeg(MathF.Atan2(d.Y, new Vector2(d.X, d.Z).Length())));
		}
		_weather.AutoChange = false; _weather.SetNow(Weather.Kind.Clear);
		_time.Frozen = true; _time.Hour = 10f;
		_walker.Invulnerable = true;
		foreach (var g in _guards) { g.SetPhysicsProcess(false); g.SetProcess(false); }
		var village = _towns.Sites.Where(s => s.Kind == "village").OrderBy(s => s.Centre.DistanceTo(_walker.GlobalPosition)).First();
		var spot = village.Centre + new Vector3(80, 0, -80);
		_walker.SetFlying(true);
		_walker.GlobalPosition = spot + new Vector3(0, 30, 0);
		for (int f = 0; f < 2400 && (_trees.Pending > 0 || !_terrain.Settled || !_terrain.HasCollisionAt(spot)); f++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
		_walker.SetFlying(false);
		_walker.GlobalPosition = spot with { Y = _world.HeightAt(spot.X, spot.Z) + 1 };
		await Seconds(1);
		// A line-up, resting (the python further back: it's long).
		var kinds = new[] { "cobra", "viper", "krait", "rat_snake", "python" };
		var line = new System.Collections.Generic.List<DeadRecon.People.Snake>();
		for (int i = 0; i < kinds.Length; i++)
			line.Add(_wildlife!.TestSnake(kinds[i], spot + new Vector3(-4f + i * 2f, 0, -7 - (kinds[i] == "python" ? 2 : 0)), MathF.PI * .5f));
		await Seconds(2);
		// A camera of our own, low down, for the photos.
		var cam = new Camera3D { Fov = 40, Near = .02f };
		AddChild(cam);
		void Frame(Vector3 from, Vector3 at, float fov) { cam.GlobalPosition = from; cam.LookAt(at); cam.Fov = fov; cam.MakeCurrent(); }
		float H(Vector3 v) => _world.HeightAt(v.X, v.Z);
		var mid = spot + new Vector3(0, 0, -7.5f); mid.Y = H(mid);
		Frame(mid + new Vector3(0, 1.3f, 3.2f), mid + Vector3.Up * .05f, 55);
		await Shot("1_lineup");
		var py = line[4].GlobalPosition;
		var fwd = line[4].GlobalTransform.Basis.Z.Normalized(); var right = line[4].GlobalTransform.Basis.X.Normalized();
		Frame(py + fwd * 1.3f + right * 1.1f + Vector3.Up * .8f, py - fwd * 1.2f, 50);
		await Shot("2_python");
		// Slithering.
		line[3].TestGo(line[3].GlobalPosition + new Vector3(0, 0, 6));
		line[4].TestGo(line[4].GlobalPosition + new Vector3(-3, 0, 6));
		await Seconds(2.5);
		var rs = line[3].GlobalPosition;
		Frame(rs + new Vector3(1.2f, 1.4f, 1.8f), rs + new Vector3(0, 0, -.8f), 50);
		await Shot("3_slither");
		var vp = line[1].GlobalPosition;
		var vf = line[1].GlobalTransform.Basis.Z.Normalized();
		Frame(vp + vf * .45f + line[1].GlobalTransform.Basis.X.Normalized() * .3f + Vector3.Up * .25f, vp - vf * .15f, 40);
		await Shot("3b_viper_head");
		// Walk up to the cobra: it rears, spreads its hood and hisses.
		foreach (var s in line.Where((s, i) => i != 0)) s.QueueFree();
		var cobra = line[0];
		var c = cobra.GlobalPosition;
		_walker.SetFlying(false);
		_walker.GlobalPosition = c + new Vector3(3.2f, 1, 0);
		await Seconds(2);
		Frame(c + new Vector3(1.2f, .7f, 1.6f), c + new Vector3(0, .35f, -.2f), 45);
		await Shot("4_cobra_warns");
		_walker.GlobalPosition = c + new Vector3(1.4f, 1, 0);
		for (int f = 0; f < 240 && (DeadRecon.Gear.Kit.Current?.Venom ?? 0) <= 0; f++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
		await Shot("5_strike");
		GD.Print($"[snake] cobra bite: venom {DeadRecon.Gear.Kit.Current?.Venom ?? -1:0.0}");
		_walker.GlobalPosition = c + new Vector3(8, 1, 0);
		await Seconds(1);
		cobra.TakeHit(999, cobra.GlobalPosition, _walker.GlobalPosition);
		GD.Print($"[snake] cobra dead: {cobra.Dead}; snakes spawned naturally will appear around you");
		await Seconds(1);
		GetTree().Quit();
	}

	private async Task RunZooTest(string dir)
	{
		Directory.CreateDirectory(dir);
		async Task Seconds(double t) => await ToSignal(GetTree().CreateTimer(t), SceneTreeTimer.SignalName.Timeout);
		async Task Shot(string name)
		{
			await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
			GetViewport().GetTexture().GetImage().SavePng(Path.Combine(dir, name + ".png"));
			GD.Print($"[zoo] shot {name}");
		}
		void LookAt(Vector3 target)
		{
			var d = target - _walker.Camera.GlobalPosition;
			_walker.Look(Mathf.RadToDeg(MathF.Atan2(-d.X, -d.Z)), Mathf.RadToDeg(MathF.Atan2(d.Y, new Vector2(d.X, d.Z).Length())));
		}
		_weather.AutoChange = false; _weather.SetNow(Weather.Kind.Clear);
		_time.Frozen = true; _time.Hour = 9.5f;
		_walker.Invulnerable = true;
		foreach (var g in _guards) { g.SetPhysicsProcess(false); g.SetProcess(false); }
		var start = _walker.GlobalPosition;
		var village = _towns.Sites.Where(s => s.Kind == "village").OrderBy(s => s.Centre.DistanceTo(start)).First();
		_walker.SetFlying(true);
		_walker.GlobalPosition = village.Centre + new Vector3(-14, 6, 12);
		for (int f = 0; f < 2400 && (_life.Population == 0 || _trees.Pending > 0 || !_terrain.Settled); f++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
		await Seconds(6);
		var tame = DeadRecon.People.Animal.All.Where(a => a.Site == village.Index).ToList();
		GD.Print($"[zoo] {village.Name}: {tame.Count} animals: {string.Join(", ", tame.GroupBy(a => a.Def.Id).Select(g => $"{g.Count()} {g.Key}"))}");
		LookAt(village.Centre);
		await Shot("1_village_animals");
		var dog = tame.FirstOrDefault(a => a.Def.Role == "dog");
		if (dog != null)
		{
			_walker.SetFlying(false);
			_walker.GlobalPosition = dog.GlobalPosition + new Vector3(4, 1, 4);
			await Seconds(3);
			LookAt(dog.GlobalPosition + Vector3.Up * .4f);
			await Shot("2_dog_close");
		}
		// Wildlife, right here.
		var spot = village.Centre + new Vector3(80, 0, -80);
		_walker.SetFlying(true);
		_walker.GlobalPosition = spot + new Vector3(0, 30, 0);
		for (int f = 0; f < 900 && (_trees.Pending > 0 || !_terrain.Settled || !_terrain.HasCollisionAt(spot)); f++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
		_walker.SetFlying(false);
		_walker.GlobalPosition = spot with { Y = _world.HeightAt(spot.X, spot.Z) + 1 };
		var deer = _wildlife!.TestSpawn("deer", spot + new Vector3(0, 0, -45), 2);
		var boar = _wildlife.TestSpawn("boar", spot + new Vector3(-6, 0, -30), 3);
		var eleph = _wildlife.TestSpawn("elephant", spot + new Vector3(14, 0, -60), 1);
		var tiger = _wildlife.TestSpawn("tiger", spot + new Vector3(9, 0, -22), 1);
		_walker.Crouching.ToString();
		await Seconds(1.5);
		LookAt(deer[0].GlobalPosition + Vector3.Up * .8f);
		_walker.Camera.Fov = 40;
		await Shot("3_wildlife");
		var before = deer[0].GlobalPosition;
		DeadRecon.Combat.Hearing.Make(_walker.GlobalPosition, DeadRecon.Combat.Hearing.Gunshot, _walker);
		await Seconds(1.2);
		await Shot("4_scatter");
		await Seconds(3);
		GD.Print($"[zoo] after a gunshot the deer ran {before.DistanceTo(deer[0].GlobalPosition):0} m; tiger {(tiger[0].Dead ? "dead" : "alive")} at {tiger[0].GlobalPosition.DistanceTo(_walker.GlobalPosition):0} m");
		GD.Print($"[zoo] animals in the world: {DeadRecon.People.Animal.All.Count}");
		GetTree().Quit();
	}

	/// <summary>Risk: send the intel (alarm), reinforcements arrive; die with supplies in the pack; it's left where you
	/// fell; get it back. Prints what happened.</summary>
	private async Task RunRiskTest()
	{
		async Task Seconds(double t) => await ToSignal(GetTree().CreateTimer(t), SceneTreeTimer.SignalName.Timeout);
		_weather.AutoChange = false; _time.Frozen = true; _time.Hour = 10;
		foreach (var g in _guards) { g.SetPhysicsProcess(false); g.SetProcess(false); }
		for (int f = 0; f < 1800 && (_waitingForGround || !_terrain.Settled); f++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
		var away = (_spawn.position - _mission.Camp) with { Y = 0 };
		for (int k = 0; k < 24; k++)
		{
			var at = _mission.Camp + away.Normalized().Rotated(Vector3.Up, k * .5f) * (300 + k * 10);
			at.Y = _world.HeightAt(at.X, at.Z);
			if (DeadRecon.Base.HomeBase.Establish(at, 0, _world) == null) break;
		}
		_kit.Pack.Items.Clear();
		_kit.Add(DeadRecon.Gear.ItemDef.Supplies, 6, quiet: true);
		_kit.Pack.Place(new DeadRecon.Gear.Item(DeadRecon.Gear.ItemDef.Laser));
		_mission.TestTakeIntel();
		GD.Print($"[risk] {_mission.Objective()}; transmitted {_mission.Transmitted}");
		await Seconds(38);
		GD.Print($"[risk] reinforcements: {_mission.Reinforcements}, guards now {_mission.Guards.Count}");
		var died = _walker.GlobalPosition;
		_walker.TakeHit(1000, _walker.Chest, _walker.Chest + Vector3.Forward);
		await Seconds(.5);
		GD.Print($"[risk] died: pack now {_kit.Pack.Items.Count} items; lost pack saved {DeadRecon.Base.HomeBase.Saved?.Lost != null}, {DeadRecon.Base.HomeBase.Saved?.Lost?.Items.Length} chars");
		GD.Print($"[risk] recover: {_kit.Recover()}; pack now {_kit.Pack.Items.Count} items, supplies {_kit.AllItems.Where(i => i.Def == DeadRecon.Gear.ItemDef.Supplies).Sum(i => i.Count)}");
		GetTree().Quit();
	}

	/// <summary>Your people: set up home, three rescued soldiers move in (two in the squad, one builder), plan a tent
	/// and watch it go up, then take the squad into the enemy camp, weapons free.</summary>
	private async Task RunSquadBaseTest(string dir)
	{
		Directory.CreateDirectory(dir);
		async Task Frames(int n) { for (int f = 0; f < n; f++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
		async Task Seconds(double t) => await ToSignal(GetTree().CreateTimer(t), SceneTreeTimer.SignalName.Timeout);
		async Task Shot(string name)
		{
			await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
			GetViewport().GetTexture().GetImage().SavePng(Path.Combine(dir, name + ".png"));
			GD.Print($"[sb] shot {name}");
		}
		void LookAt(Vector3 target)
		{
			var d = target - _walker.Camera.GlobalPosition;
			_walker.Look(Mathf.RadToDeg(MathF.Atan2(-d.X, -d.Z)), Mathf.RadToDeg(MathF.Atan2(d.Y, new Vector2(d.X, d.Z).Length())));
		}
		_weather.AutoChange = false; _weather.SetNow(Weather.Kind.Clear);
		_time.Frozen = true; _time.Hour = 10;
		_walker.Invulnerable = true;
		foreach (var g in _guards) { g.SetPhysicsProcess(false); g.SetProcess(false); }
		for (int f = 0; f < 1800 && (_waitingForGround || !_terrain.Settled); f++) await Frames(1);
		// Home: 300 m from the camp, away from it.
		var away = (_spawn.position - _mission.Camp) with { Y = 0 };
		Vector3 home = _spawn.position;
		for (int k = 0; k < 24; k++)
		{
			var at = _mission.Camp + away.Normalized().Rotated(Vector3.Up, k * .5f) * (300 + k * 10);
			at.Y = _world.HeightAt(at.X, at.Z);
			if (DeadRecon.Base.HomeBase.Establish(at, 0, _world) == null) { home = at; break; }
		}
		var faces = DeadRecon.AI.SoldierBody.Catalogue();
		foreach (var n in new[] { "Sergeant Okafor", "Private Santos", "Corporal Tran" }) DeadRecon.Base.HomeBase.AddSoldier(n, faces[Math.Abs(n.GetHashCode()) % faces.Count]);
		var roster = DeadRecon.Base.HomeBase.Saved!.Soldiers;
		roster[2].Squad = false; roster[2].Task = "build";
		roster[0].Weapon = "lmg_7741"; roster[1].Weapon = "rifle_4121";
		var siteAt = home + new Vector3(5, 0, 3);
		DeadRecon.Base.Structures.Current!.TestPlace("tent", siteAt);
		_walker.GlobalPosition = home + new Vector3(0, 1, 6);
		_squadNode!.OnOperation = true;
		_squadNode.Muster();
		await Seconds(2);
		GD.Print($"[sb] home {home}; squad {_squadNode.Team.Count}, at home {_squadNode.AtHome.Count}; tent {DeadRecon.Base.Structures.Progress("tent") * 100:0}%");
		for (int f = 0; f < 900 && (_trees.Pending > 0 || !_terrain.Settled); f++) await Frames(1);
		_walker.GlobalPosition = home + new Vector3(-6, 3, 12);
		LookAt(home + new Vector3(3, 0, 3));
		await Seconds(8);
		await Shot("1_camp_building");
		GD.Print($"[sb] after 10 s: tent {DeadRecon.Base.Structures.Progress("tent") * 100:0}%");
		await Seconds(25);
		GD.Print($"[sb] after 35 s: tent {DeadRecon.Base.Structures.Progress("tent") * 100:0}%");
		await Shot("2_camp_later");
		// Off to the camp: walk the squad there (fly ahead, they catch up), then weapons free.
		foreach (var g in _guards) { g.SetPhysicsProcess(true); g.SetProcess(true); }
		var near = _mission.Camp + away.Normalized() * 45;
		_walker.SetFlying(true);
		_walker.GlobalPosition = near + Vector3.Up * 30;
		for (int f = 0; f < 900 && (_trees.Pending > 0 || !_terrain.Settled || !_terrain.HasCollisionAt(near)); f++) await Frames(1);
		_walker.SetFlying(false);
		_walker.GlobalPosition = near with { Y = _world.HeightAt(near.X, near.Z) + .5f };
		LookAt(_mission.Camp);
		await Seconds(3);
		foreach (var c in _squadNode.Team) c.Command(DeadRecon.People.Comrade.Order.Move, _mission.Camp + (near - _mission.Camp).Normalized() * 25);
		await Seconds(6);
		await Shot("3_squad_moving");
		foreach (var c in _squadNode.Team) c.SetFire(true);
		_walker.Flying.ToString();
		for (int t = 0; t < 40; t++)
		{
			await Seconds(1);
			if (t == 8) await Shot("4_firefight");
		}
		int dead = _mission.Guards.Count(g => g.State == Guard.Mood.Dead);
		GD.Print($"[sb] firefight: guards down {dead}/{_mission.Guards.Count}; squad: {string.Join(", ", _squadNode.Team.Select(c => $"{c.Person} {(c.Dead ? "dead" : c.Down ? "down" : $"{c.Health:0} hp")}"))}");
		GetTree().Quit();
	}

	/// <summary>One operation of the kind --op names (cache, officer, prisoner, intel): do the objective, get out.</summary>
	private async Task RunOpTest(string dir)
	{
		Directory.CreateDirectory(dir);
		async Task Frames(int n) { for (int f = 0; f < n; f++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
		async Task Seconds(double t) => await ToSignal(GetTree().CreateTimer(t), SceneTreeTimer.SignalName.Timeout);
		async Task Shot(string name)
		{
			await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
			GetViewport().GetTexture().GetImage().SavePng(Path.Combine(dir, name + ".png"));
			GD.Print($"[op] shot {name}");
		}
		void LookAt(Vector3 target)
		{
			var d = target - _walker.Camera.GlobalPosition;
			_walker.Look(Mathf.RadToDeg(MathF.Atan2(-d.X, -d.Z)), Mathf.RadToDeg(MathF.Atan2(d.Y, new Vector2(d.X, d.Z).Length())));
		}
		_weather.AutoChange = false; _weather.SetNow(Weather.Kind.Clear);
		_time.Frozen = true; _time.Hour = 10;
		_walker.Invulnerable = true;
		foreach (var g in _guards) { g.SetPhysicsProcess(false); g.SetProcess(false); }
		Input.MouseMode = Input.MouseModeEnum.Captured;
		for (int f = 0; f < 1800 && (_waitingForGround || !_terrain.Settled); f++) await Frames(1);
		GD.Print($"[op] {_mission.OperationName}: {_mission.Kind}; {_mission.Objective()}");
		var t = _mission.Target ?? _mission.Camp;
		_walker.SetFlying(true);
		_walker.GlobalPosition = t + new Vector3(7, 4, 7);
		for (int f = 0; f < 900 && (_trees.Pending > 0 || !_terrain.Settled || !_terrain.HasCollisionAt(t)); f++) await Frames(1);
		await Seconds(1.5);
		LookAt(t);
		await Shot("1_objective");
		_mission.TestTakeIntel();
		await Seconds(1.5);
		GD.Print($"[op] after the objective: {_mission.State}; {_mission.Objective()}");
		await Shot("2_after");
		if (_mission.Kind == Mission.Op.Prisoner && _mission.Prisoner is { } pr)
		{
			_walker.SetFlying(false);
			_walker.GlobalPosition = pr.GlobalPosition + new Vector3(0, .5f, 2.4f);
			await Seconds(1.5);
			LookAt(pr.GlobalPosition + Vector3.Up);
			await Shot("3_prisoner");
			Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = Godot.Key.F, Keycode = Godot.Key.F, Pressed = true });
			await Frames(3);
			Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = Godot.Key.F, Keycode = Godot.Key.F, Pressed = false });
			await Seconds(1);
			GD.Print($"[op] prisoner following: {pr.Following}; {_mission.State}");
		}
		_walker.SetFlying(false);
		var ex = _mission.Extraction;
		_walker.GlobalPosition = ex with { Y = _world.HeightAt(ex.X, ex.Z) + .5f };
		for (int f = 0; f < 900 && !_terrain.HasCollisionAt(ex); f++) await Frames(1);
		await Seconds(4);
		GD.Print($"[op] at the way out: {_mission.State}; supplies {_kit.AllItems.Where(i => i.Def == DeadRecon.Gear.ItemDef.Supplies).Sum(i => i.Count)}");
		if (_mission.Prisoner is { } p2)
		{
			var stop = p2.GlobalPosition;
			_walker.GlobalPosition = ex with { Y = _world.HeightAt(ex.X, ex.Z) + .5f } + new Vector3(40, 0, 0);
			await Seconds(5);
			GD.Print($"[op] after you walk away, the prisoner moved {stop.DistanceTo(p2.GlobalPosition):0.0} m (following {p2.Following})");
		}
		GetTree().Quit();
	}

	/// <summary>Village jobs: take each kind from the nearest village's headman and finish it (checkpoint, patrol,
	/// missing farmer walked home, rice delivered), with photos of the HUD line, the map and the lost farmer.</summary>
	private async Task RunJobTest(string dir)
	{
		Directory.CreateDirectory(dir);
		async Task Frames(int n) { for (int f = 0; f < n; f++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
		async Task Seconds(double t) => await ToSignal(GetTree().CreateTimer(t), SceneTreeTimer.SignalName.Timeout);
		async Task Shot(string name)
		{
			await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
			GetViewport().GetTexture().GetImage().SavePng(Path.Combine(dir, name + ".png"));
			GD.Print($"[jobtest] shot {name}");
		}
		void LookAt(Vector3 target)
		{
			var d = target - _walker.Camera.GlobalPosition;
			_walker.Look(Mathf.RadToDeg(MathF.Atan2(-d.X, -d.Z)), Mathf.RadToDeg(MathF.Atan2(d.Y, new Vector2(d.X, d.Z).Length())));
		}
		void Key(Godot.Key k, bool down) => Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = k, Keycode = k, Pressed = down });
		async Task PressF() { Key(Godot.Key.F, true); await Frames(3); Key(Godot.Key.F, false); await Frames(3); }
		void Kill(System.Collections.Generic.IEnumerable<Guard> men) { foreach (var g in men.ToList()) if (IsInstanceValid(g)) g.TakeHit(1000, g.GlobalPosition + Vector3.Up, g.GlobalPosition + new Vector3(10, 1, 0)); }
		_weather.AutoChange = false; _weather.SetNow(Weather.Kind.Clear);
		_time.Frozen = true; _time.Hour = 10;
		_walker.Invulnerable = true;
		foreach (var g in _guards) { g.SetPhysicsProcess(false); g.SetProcess(false); }
		Input.MouseMode = Input.MouseModeEnum.Captured;
		var board = DeadRecon.People.JobBoard.Current!;
		var start = _walker.GlobalPosition;
		var village = _towns.Sites.Where(s => s.Kind == "village").OrderBy(s => s.Centre.DistanceTo(start)).First();
		_walker.SetFlying(true);
		_walker.GlobalPosition = village.Centre + new Vector3(-8, 2, 6);
		for (int f = 0; f < 2400 && (_life.Population == 0 || !_terrain.HasCollisionAt(village.Centre)); f++) await Frames(1);
		await Seconds(1);
		string Support() => $"{DeadRecon.People.Support.Get(village.Index):0} ({DeadRecon.People.Support.LevelAt(village.Index)})";
		GD.Print($"[jobtest] {village.Name}, support {Support()}");
		for (int round = 0; round < 4; round++)
		{
			string reply = _life.TestTalk(village.Index, 2);
			var job = board.Tracked;
			if (job == null || !job.Active) { GD.Print($"[jobtest] no job: {reply}"); break; }
			GD.Print($"[jobtest] job {round + 1}: {job.Kind}: {job.Title}");
			await Frames(2);
			switch (job.Kind)
			{
				case DeadRecon.People.JobBoard.Kind.Checkpoint:
				{
					var cp = job.Place;
					Kill(_patrols!.GarrisonOf(cp!.Index));
					break;
				}
				case DeadRecon.People.JobBoard.Kind.Patrol:
					Kill(_patrols!.TestMuster(board.PatrolOf(village.Index)!));
					break;
				case DeadRecon.People.JobBoard.Kind.Missing:
				{
					_walker.GlobalPosition = village.Centre + new Vector3(0, 30, 0);
					_kitScreen.Open(2);
					for (int f = 0; f < 900 && DeadRecon.UI.MapPainter.Get(_world, _towns) == null; f++) await Frames(1);
					await Frames(5);
					await Shot("2_map_missing");
					_kitScreen.Close();
					Input.MouseMode = Input.MouseModeEnum.Captured;
					var lost = board.LostOf(village.Index)!;
					_walker.SetFlying(false);
					_walker.GlobalPosition = lost.GlobalPosition + new Vector3(2, 1, 1.5f);
					for (int f = 0; f < 900 && (_trees.Pending > 0 || !_terrain.HasCollisionAt(lost.GlobalPosition)); f++) await Frames(1);
					await Seconds(4);
					LookAt(lost.GlobalPosition + Vector3.Up);
					await Shot("3_lost_farmer");
					await PressF();
					GD.Print($"[jobtest] found him: following {lost.Following}");
					// Walk home (well, teleport near home; he catches up).
					_walker.GlobalPosition = village.Centre + new Vector3(4, 1, 4);
					await Seconds(3);
					break;
				}
				case DeadRecon.People.JobBoard.Kind.Delivery:
				{
					var to = job.Place;
					GD.Print($"[jobtest] rice in the backpack: {_kit.AllItems.Any(i => i.Def == DeadRecon.Gear.ItemDef.RiceSacks)}, {_kit.Kg:0} kg");
					_walker.SetFlying(false);
					_walker.GlobalPosition = to!.Centre + new Vector3(2, 1, 2);
					for (int f = 0; f < 900 && !_terrain.HasCollisionAt(to.Centre); f++) await Frames(1);
					await Seconds(1);
					await Shot("4_delivery_prompt");
					await PressF();
					break;
				}
			}
			await Seconds(2);
			GD.Print($"[jobtest] -> done {!job.Active && job.Done}, support {Support()}");
			if (round == 0) { _walker.GlobalPosition = village.Centre + new Vector3(-8, 2, 6); await Seconds(1); await Shot("1_hud_after_first"); }
			_walker.SetFlying(true);
			_walker.GlobalPosition = village.Centre + new Vector3(-8, 2, 6);
			await Seconds(1);
		}
		foreach (var j in board.All) GD.Print($"[jobtest] {j.Kind}: {(j.Done ? "done" : "open")} - {j.Title}");
		GetTree().Quit();
	}

	/// <summary>Patrols: a checkpoint's garrison appears as you come near and can be cleared; a road patrol becomes
	/// real near you, walks its road, and goes back to being a dot when you leave.</summary>
	private async Task RunPatrolTest(string dir)
	{
		Directory.CreateDirectory(dir);
		async Task Frames(int n) { for (int f = 0; f < n; f++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
		async Task Seconds(double t) => await ToSignal(GetTree().CreateTimer(t), SceneTreeTimer.SignalName.Timeout);
		async Task Shot(string name)
		{
			await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
			GetViewport().GetTexture().GetImage().SavePng(Path.Combine(dir, name + ".png"));
			GD.Print($"[patrol] shot {name}");
		}
		void LookAt(Vector3 target)
		{
			var d = target - _walker.Camera.GlobalPosition;
			_walker.Look(Mathf.RadToDeg(MathF.Atan2(-d.X, -d.Z)), Mathf.RadToDeg(MathF.Atan2(d.Y, new Vector2(d.X, d.Z).Length())));
		}
		_weather.AutoChange = false; _weather.SetNow(Weather.Kind.Clear);
		_time.Frozen = true; _time.Hour = 10;
		_walker.Invulnerable = true;
		foreach (var g in _guards) { g.SetPhysicsProcess(false); g.SetProcess(false); }
		for (int f = 0; f < 1800 && (_waitingForGround || !_terrain.Settled); f++) await Frames(1);
		var p = _patrols!;
		var start = _walker.GlobalPosition;
		var cp = _towns.Sites.Where(s => s.Kind == "outpost").OrderBy(s => s.Centre.DistanceTo(start)).First();
		int before = _guards.Count;
		_walker.SetFlying(true);
		_walker.GlobalPosition = cp.Centre + new Vector3(30, 18, 30);
		await Seconds(2);
		var garrison = p.GarrisonOf(cp.Index);
		GD.Print($"[patrol] {cp.Name}: garrison {garrison.Count} ({_guards.Count - before} new guards in the world)");
		for (int f = 0; f < 900 && (_trees.Pending > 0 || !_terrain.Settled || !_terrain.HasCollisionAt(cp.Centre)); f++) await Frames(1);
		await Seconds(2);
		LookAt(cp.Centre);
		await Shot("1_checkpoint_manned");
		foreach (var g in garrison.ToList()) g.TakeHit(1000, g.GlobalPosition + Vector3.Up, g.GlobalPosition + new Vector3(10, 1, 0));
		await Seconds(1.5);
		GD.Print($"[patrol] {cp.Name} cleared: {p.IsCleared(cp.Index)}");
		// A road patrol.
		var meet = p.TestBringPatrolNear(start + new Vector3(0, 0, 900));
		if (meet is { } m)
		{
			_walker.GlobalPosition = m + new Vector3(60, 25, 60);
			await Seconds(2);
			GD.Print($"[patrol] real patrols near {m}: {p.RealPatrols}, men {p.PatrolGuards.Count()}");
			for (int f = 0; f < 900 && (_trees.Pending > 0 || !_terrain.Settled); f++) await Frames(1);
			var lead = p.PatrolGuards.FirstOrDefault();
			if (lead != null)
			{
				var from = lead.GlobalPosition;
				_walker.GlobalPosition = lead.GlobalPosition + new Vector3(18, 9, 18);
				LookAt(lead.GlobalPosition);
				await Seconds(1);
				await Shot("2_patrol");
				await Seconds(20);
				GD.Print($"[patrol] the patrol walked {from.DistanceTo(lead.GlobalPosition):0} m in 20 s, mood {lead.State}");
				_walker.GlobalPosition = lead.GlobalPosition + new Vector3(10, 6, 10);
				LookAt(lead.GlobalPosition + Vector3.Up);
				await Seconds(.5);
				await Shot("3_patrol_later");
			}
			_walker.GlobalPosition = m + new Vector3(900, 40, 0);
			await Seconds(3);
			GD.Print($"[patrol] after leaving: real patrols {p.RealPatrols}");
		}
		GetTree().Quit();
	}

	/// <summary>The kit: HUD, both pages of the kit screen, attachments fitted (red dot, suppressor, light, laser) seen
	/// in the field, and magazines being used up by reloads.</summary>
	private async Task RunKitTest(string dir)
	{
		Directory.CreateDirectory(dir);
		async Task Frames(int n) { for (int f = 0; f < n; f++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
		async Task Shot(string name)
		{
			await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
			GetViewport().GetTexture().GetImage().SavePng(Path.Combine(dir, name + ".png"));
			GD.Print($"[kit] shot {name}");
		}
		_hud.Visible = false;
		_weather.AutoChange = false; _weather.SetNow(Weather.Kind.Clear);
		_time.Frozen = true; _time.Hour = 10;
		foreach (var g in _guards) { g.SetPhysicsProcess(false); g.SetProcess(false); }
		for (int f = 0; f < 1800 && (_waitingForGround || _trees.Pending > 0 || !_terrain.Settled); f++) await Frames(1);
		await Frames(90);
		var k = _kit;
		GD.Print($"[kit] start: loaded {k.Loaded}/{k.LoadedMag.Capacity}, {k.Magazines} magazines, {k.Bandages} dressings, {k.Kg:0.0} kg");
		// Fire five, reload: one magazine fewer, the part-used one back in the vest.
		for (int i = 0; i < 5; i++) { _weapon.TestFire(); await Frames(8); }
		int before = k.Magazines;
		k.SwapMagazine();
		GD.Print($"[kit] fired 5 and reloaded: loaded {k.Loaded}, magazines {before} -> {k.Magazines} (the old one has {k.AllItems.Where(i => i.Def.Kind == DeadRecon.Gear.ItemKind.Magazine).Min(i => i.Rounds)} rounds)");
		await Frames(20);
		await Shot("1_hud");
		_kitScreen.Open(0);
		await Frames(30);
		await Shot("2_backpack");
		// Real mouse input: drag the first backpack magazine three cells right.
		{
			var view = GetViewport().GetVisibleRect().Size;
			float sc = Math.Min(view.X / 1600, view.Y / 900);
			var off = (view - new Vector2(1600, 900) * sc) / 2;
			Vector2 Scr(Vector2 r) => r * sc + off;
			var mag = k.Pack.Items.First(i => i.Def.Kind == DeadRecon.Gear.ItemKind.Magazine);
			int x0 = mag.X;
			var from = Scr(new Vector2(70, 340) + new Vector2(mag.X + .5f, mag.Y + .5f) * 58);
			var to = from + new Vector2(0, 58 * 4) * sc;
			GetViewport().PushInput(new InputEventMouseMotion { Position = from, GlobalPosition = from });
			GetViewport().PushInput(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, Position = from, GlobalPosition = from });
			await Frames(3);
			for (int i = 1; i <= 8; i++) { var p = from.Lerp(to, i / 8f); GetViewport().PushInput(new InputEventMouseMotion { Position = p, GlobalPosition = p, ButtonMask = MouseButtonMask.Left }); await Frames(1); }
			await Shot("2b_dragging");
			GetViewport().PushInput(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, Position = to, GlobalPosition = to });
			await Frames(3);
			GD.Print($"[kit] drag test: magazine moved from ({x0},?) to ({mag.X},{mag.Y}) in {k.GridOf(mag)?.Name}");
		}
		_kitScreen.TestShow(k.Pack.Items.FirstOrDefault(i => i.Def == DeadRecon.Gear.ItemDef.Suppressor), 0);
		await Frames(5);
		await Shot("3_backpack_details");
		_kitScreen.TestShow(null, 1);
		await Frames(40);
		await Shot("4_gunsmith_factory");
		foreach (var id in new[] { "optic_reddot", "muzzle_suppressor", "side_flashlight" })
		{
			var item = k.AllItems.First(i => i.Def.Id == id);
			GD.Print($"[kit] fit {id}: {k.Fit(item.Def.Slot, item) ?? "ok"}");
		}
		k.Paint("tan");
		await Frames(60);
		await Shot("5_gunsmith_fitted");
		_kitScreen.Close();
		await Frames(30);
		await Shot("6_hud_fitted");
		GD.Print($"[kit] suppressed {_weapon.Suppressed}, light {_weapon.HasLight}");
		_weapon.ForceAim(true);
		await Frames(30);
		await Shot("7_red_dot");
		_weapon.ForceAim(false);
		_time.Hour = 22.5f;
		k.LightOn = true;
		await Frames(40);
		await Shot("8_flashlight_night");
		k.Add(DeadRecon.Gear.ItemDef.Laser);
		var laser = k.AllItems.First(i => i.Def == DeadRecon.Gear.ItemDef.Laser);
		GD.Print($"[kit] fit laser: {k.Fit(DeadRecon.Gear.Slot.Side, laser) ?? "ok"}");
		k.LightOn = true;
		await Frames(40);
		await Shot("9_laser_night");
		// Every gun in the list: swap to it, fire, reload, photograph (daylight).
		_time.Hour = 10; k.LightOn = false;
		foreach (var w in DeadRecon.Gear.WeaponDef.All.Values)
		{
			k.SwapWeapon(w.Id, w.Capacity);
			await Frames(15);
			for (int i = 0; i < Math.Min(3, w.Capacity); i++) { _weapon.TestFire(); await Frames(6); }
			int before2 = k.Loaded;
			k.Add(DeadRecon.Gear.ItemDef.Mag30, 4, quiet: true);
			k.Reload();
			GD.Print($"[kit] {w.Id}: fired to {before2}, reloaded to {k.Loaded}/{w.Capacity}, {_weapon.LastHit}");
			await Shot($"w_{w.Id}");
		}
		k.SwapWeapon(DeadRecon.Gear.WeaponDef.Default, 20);
		// The map: wait for the painting, set a waypoint, look.
		_time.Hour = 10; k.LightOn = false;
		var start = _walker.GlobalPosition;
		DeadRecon.UI.MapMemory.Current!.Waypoint = start + new Vector3(350, 0, -250);
		_kitScreen.Open(2);
		for (int f = 0; f < 900 && DeadRecon.UI.MapPainter.Get(_world, _towns) == null; f++) await Frames(1);
		await Frames(10);
		await Shot("10_map");
		_kitScreen.Close();
		await Frames(20);
		await Shot("11_compass_waypoint");
		GetTree().Quit();
	}

	/// <summary>Village life: a day in the nearest village (work, lunch, bed), gunfire panic, the headman's news,
	/// help and job (its checkpoint guards killed), and shooting a villager. Prints what everyone is doing.</summary>
	private async Task RunLifeTest(string dir)
	{
		Directory.CreateDirectory(dir);
		async Task Frames(int n) { for (int f = 0; f < n; f++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
		async Task Seconds(double t) => await ToSignal(GetTree().CreateTimer(t), SceneTreeTimer.SignalName.Timeout);
		async Task Shot(string name)
		{
			await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
			GetViewport().GetTexture().GetImage().SavePng(Path.Combine(dir, name + ".png"));
		}
		void LookAt(Vector3 target)
		{
			var d = target - _walker.Camera.GlobalPosition;
			_walker.Look(Mathf.RadToDeg(MathF.Atan2(-d.X, -d.Z)), Mathf.RadToDeg(MathF.Atan2(d.Y, new Vector2(d.X, d.Z).Length())));
		}
		_hud.Visible = false;
		_weather.AutoChange = false; _weather.SetNow(Weather.Kind.Clear);
		_time.Frozen = true; _time.Hour = 9;
		foreach (var g in _guards) { g.SetPhysicsProcess(false); g.SetProcess(false); }
		DeadRecon.People.Support.Reset();
		var start = _walker.GlobalPosition;
		Settlements.Site? village = null;
		foreach (var s in _towns.Sites)
			if (s.Kind == "village" && (village == null || s.Centre.DistanceTo(start) < village.Centre.DistanceTo(start))) village = s;
		var c = village!.Centre;
		void Census(string when)
		{
			var counts = new System.Collections.Generic.Dictionary<string, int>();
			foreach (var v in _life.PeopleOf(village.Index)) { string k = $"{v.Activity}/{v.State}"; counts[k] = (counts.TryGetValue(k, out var n) ? n : 0) + 1; }
			GD.Print($"[life] {when}: " + string.Join(", ", counts.Select(kv => $"{kv.Value} {kv.Key}")));
		}
		_walker.SetFlying(true);
		_walker.GlobalPosition = c + new Vector3(-12, 3, 10);
		for (int f = 0; f < 2400 && (_trees.Pending > 0 || !_terrain.Settled || _life.Population == 0 || !_terrain.HasCollisionAt(c)); f++) await Frames(1);
		await Seconds(2);
		GD.Print($"[life] {village.Name}: {_life.Population} people, support {DeadRecon.People.Support.Get(village.Index):0}");
		Census("09:00");
		LookAt(c); await Shot("1_morning");
		var farmer = _life.PeopleOf(village.Index).FirstOrDefault(v => v.Job == DeadRecon.People.Villager.Role.Farmer);
		if (farmer != null)
		{
			await Seconds(20);
			_walker.GlobalPosition = farmer.GlobalPosition + new Vector3(4, 2.2f, 4);
			LookAt(farmer.GlobalPosition + Vector3.Up);
			await Seconds(1); await Shot("2_farmer");
			Census("09:00 +20s");
		}
		_time.Hour = 12;
		_walker.GlobalPosition = c + new Vector3(-8, 2.5f, 7);
		await Seconds(40);
		Census("12:00 +40s");
		LookAt(_villages.LayoutOf(village.Index)!.Fire); await Shot("3_lunch");
		_time.Hour = 21.4f;
		await Seconds(45);
		Census("21:24 +45s");
		await Shot("4_night");
		_time.Hour = 9.5f;
		await Seconds(30);
		Census("09:30 +30s");
		var chief = _life.HeadmanOf(village.Index);
		if (chief != null) { _walker.GlobalPosition = chief.GlobalPosition + chief.GlobalTransform.Basis.Z * -2.2f + Vector3.Up * .2f; LookAt(chief.GlobalPosition + Vector3.Up * 1.4f); }
		GD.Print($"[life] news: {_life.TestTalk(village.Index, 0)}");
		GD.Print($"[life] help: {_life.TestTalk(village.Index, 1)}");
		GD.Print($"[life] job: {_life.TestTalk(village.Index, 2)}");
		await Frames(3); await Shot("6_dialog");
		DeadRecon.Combat.Hearing.Make(c + new Vector3(30, 1, 0), DeadRecon.Combat.Hearing.Gunshot, _walker);
		await Seconds(1);
		Census("gunshot +1s");
		await Seconds(15);
		Census("gunshot +16s");
		await Shot("5_panic");
		GD.Print($"[life] support after gunfire: {DeadRecon.People.Support.Get(village.Index):0}");
		var cpGuards = _guards.Where(g => g.Home.DistanceTo(c) > 100 && g.Home.DistanceTo(_mission.Camp) > 60).ToList();
		GD.Print($"[life] checkpoint guards: {cpGuards.Count}");
		foreach (var g in cpGuards) g.TakeHit(1000, g.GlobalPosition + Vector3.Up, g.GlobalPosition + new Vector3(10, 1, 0));
		await Seconds(2);
		GD.Print($"[life] support after the job: {DeadRecon.People.Support.Get(village.Index):0} ({DeadRecon.People.Support.LevelAt(village.Index)})");
		GD.Print($"[life] help now: {_life.TestTalk(village.Index, 1)}");
		var victim = _life.PeopleOf(village.Index).FirstOrDefault(v => !v.Indoors);
		for (int k = 0; k < 2; k++) victim?.TakeHit(34, victim.GlobalPosition + Vector3.Up * 1.2f, victim.GlobalPosition + new Vector3(8, 1.5f, 0));
		await Seconds(1);
		GD.Print($"[life] support after shooting a villager: {DeadRecon.People.Support.Get(village.Index):0} ({DeadRecon.People.Support.LevelAt(village.Index)})");
		DeadRecon.People.Support.Reset();
		GetTree().Quit();
	}

	/// <summary>Villages and roads: photos of the nearest village (noon, golden hour, night, rain), a road, and a walk up a
	/// house's stairs onto its veranda.</summary>
	private async Task RunVillageTest(string dir)
	{
		Directory.CreateDirectory(dir);
		async Task Frames(int n) { for (int f = 0; f < n; f++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
		async Task Shot(string name)
		{
			await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
			GetViewport().GetTexture().GetImage().SavePng(Path.Combine(dir, name + ".png"));
			GD.Print($"[village] {name}: {Engine.GetFramesPerSecond()} fps");
		}
		void LookAt(Vector3 target)
		{
			var d = target - _walker.Camera.GlobalPosition;
			_walker.Look(Mathf.RadToDeg(MathF.Atan2(-d.X, -d.Z)), Mathf.RadToDeg(MathF.Atan2(d.Y, new Vector2(d.X, d.Z).Length())));
		}
		void Key(Godot.Key k, bool down) => Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = k, Keycode = k, Pressed = down });
		foreach (var h in FindChildren("*", nameof(DeadRecon.UI.Hud), true, false)) ((Control)h).Visible = false;
		_hud.Visible = false;
		_weather.AutoChange = false; _weather.SetNow(Weather.Kind.Clear);
		_time.Frozen = true; _time.Hour = 12;
		foreach (var g in _guards) { g.SetPhysicsProcess(false); g.SetProcess(false); }
		_weapon.Visible = false;
		var start = _walker.GlobalPosition;
		Settlements.Site? village = null;
		foreach (var s in _towns.Sites)
			if (s.Kind == "village" && (village == null || s.Centre.DistanceTo(start) < village.Centre.DistanceTo(start))) village = s;
		if (village == null) { GD.PrintErr("[village] no village"); GetTree().Quit(1); return; }
		GD.Print($"[village] {village.Name} at {village.Centre}, {village.Centre.DistanceTo(start):0} m from spawn");
		var c = village.Centre;
		_walker.SetFlying(true);
		_walker.GlobalPosition = c + new Vector3(0, 40, 0);
		for (int f = 0; f < 2400 && (_trees.Pending > 0 || !_terrain.Settled || _villages.BuiltCount == 0 || !_terrain.HasCollisionAt(c)); f++) await Frames(1);
		await Frames(60);
		var over = c + new Vector3(-38, 26, 34);
		_walker.GlobalPosition = over; LookAt(c);
		await Frames(20); await Shot("1_overview_noon");
		var eye = c + new Vector3(-9, 0, 7); eye.Y = _world.HeightAt(eye.X, eye.Z) + .2f;
		_walker.GlobalPosition = eye; LookAt(c + new Vector3(0, 1.5f, 0) - (eye - c) * 1.5f);
		await Frames(20); await Shot("2_plaza_noon");
		_time.Hour = 17.4f; await Frames(30); await Shot("3_plaza_golden");
		_walker.GlobalPosition = over; LookAt(c); await Frames(20); await Shot("4_overview_golden");
		_time.Hour = 21.5f;
		_walker.GlobalPosition = eye; LookAt(c + new Vector3(0, 1.5f, 0) - (eye - c) * 1.5f);
		await Frames(40); await Shot("5_plaza_night");
		_walker.GlobalPosition = over; LookAt(c); await Frames(20); await Shot("6_overview_night");
		_time.Hour = 15; _weather.SetNow(Weather.Kind.Rain);
		_walker.GlobalPosition = eye; LookAt(c + new Vector3(0, 1.5f, 0) - (eye - c) * 1.5f);
		await Frames(60); await Shot("7_plaza_rain");
		_weather.SetNow(Weather.Kind.Clear); _time.Hour = 9.5f;
		// A road just outside the village.
		foreach (var road in _towns.Roads)
		{
			int i = Array.FindIndex(road.Points, q => q.DistanceTo(c) is > 90 and < 130);
			if (i < 0 || i + 3 >= road.Points.Length) continue;
			var p = road.Points[i];
			_walker.GlobalPosition = p + Vector3.Up * 1.0f;
			LookAt(road.Points[Math.Max(0, i - 6)] + Vector3.Up * 1.2f);
			for (int f = 0; f < 600 && _trees.Pending > 0; f++) await Frames(1);
			await Frames(40); await Shot("8_road");
			break;
		}
		// Fields: the nearest paddy and wheat field to spawn, by day, in the rain, at golden hour.
		foreach (var kind in new[] { "paddy", "wheat" })
		{
			Settlements.Field? best = null;
			foreach (var f in _towns.Fields)
				if (f.Kind == kind && f.Stage != "flooded" && (best == null || f.Centre.DistanceTo(start) < best.Centre.DistanceTo(start))) best = f;
			if (best == null) continue;
			var fc = best.Centre;
			var basis = new Basis(Vector3.Up, best.Rotation);
			var from = fc + basis * new Vector3(-best.W / 2 - 4, 0, best.D / 2 + 3);
			from.Y = _world.HeightAt(from.X, from.Z) + 2.2f;
			_walker.SetFlying(true);
			_walker.GlobalPosition = from;
			for (int f = 0; f < 900 && (_trees.Pending > 0 || !_terrain.Settled); f++) await Frames(1);
			await Frames(90);
			LookAt(fc + basis * new Vector3(best.W / 4, 0, -best.D / 4));
			_time.Hour = 10; _weather.SetNow(Weather.Kind.Clear);
			await Frames(30); await Shot($"f_{kind}_day");
			_time.Hour = 17.3f; await Frames(30); await Shot($"f_{kind}_golden");
			_time.Hour = 14; _weather.SetNow(Weather.Kind.Rain);
			_walker.GlobalPosition = from - new Vector3(0, 1.0f, 0);
			LookAt(fc + basis * new Vector3(-best.W / 4, 0, 0));
			await Frames(90); await Shot($"f_{kind}_rain");
			_weather.SetNow(Weather.Kind.Clear);
		}
		// The nearest checkpoint.
		Settlements.Site? cp = null;
		foreach (var s in _towns.Sites) if (s.Kind == "outpost" && (cp == null || s.Centre.DistanceTo(start) < cp.Centre.DistanceTo(start))) cp = s;
		if (cp != null)
		{
			_time.Hour = 11;
			_walker.SetFlying(true);
			_walker.GlobalPosition = cp.Centre + new Vector3(14, 7, 14);
			for (int f = 0; f < 900 && (_trees.Pending > 0 || !_terrain.Settled); f++) await Frames(1);
			await Frames(90);
			LookAt(cp.Centre + Vector3.Up);
			await Frames(20); await Shot("c_checkpoint");
		}
		_time.Hour = 9.5f;
		_walker.GlobalPosition = c + new Vector3(0, 30, 0);
		for (int f = 0; f < 600 && (_trees.Pending > 0 || !_terrain.Settled); f++) await Frames(1);
		await Frames(60);
		// Walk up some stairs.
		bool climbed = false;
		foreach (var (foot, top) in _villages.Stairs)
		{
			if (foot.DistanceTo(c) > village.Radius + 10) continue;
			_walker.SetFlying(true);
			var side = (top - foot).Cross(Vector3.Up).Normalized();
			_walker.GlobalPosition = (foot + top) / 2 + side * 4.5f + Vector3.Up * .4f;
			LookAt((foot + top) / 2);
			await Frames(20); await Shot("8b_stairs");
			_walker.SetFlying(false);
			_walker.GlobalPosition = foot + Vector3.Up * .3f;
			LookAt(new Vector3(top.X, foot.Y + 1.65f, top.Z));
			await Frames(30);
			Key(Godot.Key.W, true);
			for (int f = 0; f < 240 && _walker.GlobalPosition.Y < top.Y - .3f; f++) await Frames(1);
			await Frames(25);
			Key(Godot.Key.W, false);
			await Frames(20);
			float floorRise = _walker.GlobalPosition.Y - _world.HeightAt(_walker.GlobalPosition.X, _walker.GlobalPosition.Z);
			GD.Print($"[village] stairs: feet {_walker.GlobalPosition.Y:0.00}, veranda {top.Y:0.00}, {floorRise:0.0} m above ground");
			climbed = _walker.GlobalPosition.Y > top.Y - .4f;
			_walker.Look(Mathf.RadToDeg(_walker.Rotation.Y) + 180, -5);
			await Frames(10); await Shot("9_from_veranda");
			break;
		}
		GD.Print(climbed ? "[village] climbed the stairs: ok" : "[village] FAILED to climb the stairs");
		GetTree().Quit();
	}

	private async Task RunDayTest(string dir)
	{
		Directory.CreateDirectory(dir);
		async Task Frames(int n) { for (int f = 0; f < n; f++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
		async Task Shot(string name)
		{
			await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
			GetViewport().GetTexture().GetImage().SavePng(Path.Combine(dir, name + ".png"));
			GD.Print($"[day] {name}: {Engine.GetFramesPerSecond()} fps, darkness {_time.Darkness:0.00}");
		}
		_hud.Visible = false;
		foreach (var h in FindChildren("*", "", true, false)) if (h is DeadRecon.UI.Hud hud) hud.Visible = false;
		_weather.AutoChange = false;
		_weather.SetNow(Weather.Kind.Clear);
		_time.Frozen = true;
		foreach (var g in _guards) { g.SetPhysicsProcess(false); g.SetProcess(false); }
		_weapon.Visible = false;
		for (int f = 0; f < 1800 && (_waitingForGround || _trees.Pending > 0 || !_terrain.Settled); f++) await Frames(1);
		var start = _walker.GlobalPosition;
		float yaw = Mathf.RadToDeg(_walker.Rotation.Y);
		var forest = ForestSpot(start);
		foreach (var (hour, name) in new[] { (5.9f, "1_dawn"), (7.3f, "2_morning"), (12f, "3_noon"), (17.2f, "4_golden"), (18.4f, "5_sunset"), (19.5f, "6_dusk"), (22.5f, "7_night") })
		{
			_time.Hour = hour;
			_walker.SetFlying(true);
			_walker.GlobalPosition = start + Vector3.Up * 3;
			_walker.Look(yaw, -6);
			await Frames(30);
			await Shot($"{name}_river");
			_walker.GlobalPosition = start + forest + Vector3.Up * .2f;
			_walker.Look(yaw + 200, 4);
			await Frames(30);
			await Shot($"{name}_forest");
		}
		// The sky itself: looking up, and toward the setting sun (west is yaw 90).
		foreach (var (hour, look, name) in new[] { (11f, yaw, "s1_noon_sky"), (17.6f, 90f, "s2_golden_sky"), (18.4f, 90f, "s3_sunset_sky"), (22.5f, yaw, "s4_night_sky") })
		{
			_time.Hour = hour;
			_walker.GlobalPosition = start + Vector3.Up * 40;
			_walker.Look(look, 22);
			await Frames(30);
			await Shot(name);
		}
		// God rays: in the forest, looking at the sun through the canopy.
		foreach (var (hour, name) in new[] { (8.2f, "g1_rays_morning"), (16.6f, "g2_rays_afternoon") })
		{
			_time.Hour = hour;
			await Frames(2);
			var toSun = _time.Sun.GlobalTransform.Basis.Z;
			_walker.GlobalPosition = start + forest + Vector3.Up * .2f;
			_walker.Look(Mathf.RadToDeg(MathF.Atan2(-toSun.X, -toSun.Z)), Math.Min(Mathf.RadToDeg(MathF.Asin(toSun.Y)), 40));
			await Frames(40);
			await Shot(name);
		}
		// Tom's view: 17:54, in the forest, looking east (sun behind).
		_time.Hour = 17.9f;
		_walker.GlobalPosition = start + forest + Vector3.Up * .2f;
		_walker.Look(-90, 0);
		await Frames(40);
		await Shot("g3_evening_east");
		// Sunset sweep: brightness minute by minute through 17:40-18:20 must change smoothly (no flare).
		var sweep = new System.Text.StringBuilder("[day] sunset sweep:");
		for (float hh = 17.6f; hh <= 18.35f; hh += .05f)
		{
			_time.Hour = hh;
			await Frames(8);
			await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
			var img = GetViewport().GetTexture().GetImage();
			img.Resize(64, 36);
			float sum = 0; for (int yy = 0; yy < 36; yy++) for (int xx = 0; xx < 64; xx++) { var px = img.GetPixel(xx, yy); sum += .2126f * px.R + .7152f * px.G + .0722f * px.B; }
			sweep.Append($" {hh:0.00}={sum / (64 * 36) * 255:0}");
		}
		GD.Print(sweep.ToString());
		// Rocks, by day.
		if (_rocks is { } rocks)
		{
			_time.Hour = 10.5f;
			for (int k = 0; k < 2; k++)
				if (rocks.Nearest(start + new Vector3(k * 120, 0, 0), k == 0 ? 1.2f : .6f) is { } rk)
				{
					var side = new Vector3(1, 0, .6f).Normalized();
					_walker.GlobalPosition = rk.At + side * (rk.Size * 1.6f + 2.5f) + Vector3.Up * (1.4f + rk.Size * .3f);
					var d = rk.At - _walker.Camera.GlobalPosition;
					_walker.Look(Mathf.RadToDeg(MathF.Atan2(-d.X, -d.Z)), Mathf.RadToDeg(MathF.Atan2(d.Y, new Vector2(d.X, d.Z).Length())));
					await Frames(40);
					await Shot($"r{k + 1}_rock");
				}
			GD.Print($"[day] rocks near: {rocks.Count}");
		}
		_time.Hour = 18.6f; _weather.SetNow(Weather.Kind.Rain);
		_walker.GlobalPosition = start + Vector3.Up * 3; _walker.Look(yaw, -6);
		await Frames(40);
		await Shot("8_rainy_dusk");
		_time.Hour = 23.5f; await Frames(40); await Shot("8b_rainy_night");
		_walker.Look(yaw, 50); await Frames(10); await Shot("8c_rainy_night_up");
		_time.Hour = 14;
		_walker.GlobalPosition = start + Vector3.Up * 40; _walker.Look(yaw, 22);
		await Frames(60);
		await Shot("s5_storm_sky");
		GetTree().Quit();
	}

	/// <summary>Ragdoll check: shoot each guard from 8 m (hovering, so they stay calm), photograph hit, fall and rest.</summary>
	private async Task RunRagdollTest(string dir)
	{
		Directory.CreateDirectory(dir);
		async Task Frames(int n) { for (int f = 0; f < n; f++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
		async Task Seconds(double t) => await ToSignal(GetTree().CreateTimer(t), SceneTreeTimer.SignalName.Timeout);
		async Task Shot(string name)
		{
			await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
			GetViewport().GetTexture().GetImage().SavePng(Path.Combine(dir, name + ".png"));
			GD.Print($"[test] shot {name}");
		}
		void LookAt(Vector3 target)
		{
			var d = target - _walker.Camera.GlobalPosition;
			_walker.Look(Mathf.RadToDeg(MathF.Atan2(-d.X, -d.Z)), Mathf.RadToDeg(MathF.Atan2(d.Y, new Vector2(d.X, d.Z).Length())));
		}
		_hud.Visible = false;
		foreach (var h in FindChildren("*", nameof(DeadRecon.UI.Hud), true, false)) ((Control)h).Visible = false;
		_weather.AutoChange = false;
		_walker.SetFlying(true);
		_walker.GlobalPosition = _mission.Camp + Vector3.Up * 30;
		for (int f = 0; f < 1800 && (_trees.Pending > 0 || !_terrain.HasCollisionAt(_mission.Camp)); f++) await Frames(1);
		await Seconds(2);
		int n = 0;
		foreach (var g in _guards)
		{
			n++;
			// 8 m from him, on the side with no trunk in the way, eye height.
			var gp = g.GlobalPosition;
			Vector3 eyeAt = gp + new Vector3(8, 1.6f, 0);
			for (int k = 0; k < 16; k++)
			{
				var c = gp + new Vector3(MathF.Cos(k * .39f) * 8, 0, MathF.Sin(k * .39f) * 8);
				c.Y = _world.HeightAt(c.X, c.Z) + 1.6f;
				if (Foliage.RaycastTrunk(c, gp + Vector3.Up * 1.2f) == null) { eyeAt = c; break; }
			}
			_walker.GlobalPosition = eyeAt - Vector3.Up * 1.65f;
			bool head = n == 2;
			var aim = g.GlobalPosition + Vector3.Up * (head ? 1.58f : 1.15f);
			LookAt(aim);
			await Frames(3);
			_weapon.TestFire();
			await Frames(2);
			await Shot($"{n}_a_hit");
			for (int k = 0; k < 6 && g.State != Guard.Mood.Dead; k++) { LookAt(g.GlobalPosition + Vector3.Up * 1.15f); await Frames(2); _weapon.TestFire(); await Seconds(.12); }
			GD.Print($"[ragdoll] guard {n}: {g.State}");
			await Seconds(.25);
			LookAt(g.GlobalPosition + Vector3.Up * .9f);
			await Frames(1);
			await Shot($"{n}_b_falling");
			await Seconds(2.5);
			// Low and close, from the side.
			var flat = (g.GlobalPosition - _walker.GlobalPosition); flat.Y = 0;
			var sideDir = flat.Normalized().Cross(Vector3.Up);
			var cam = g.GlobalPosition + sideDir * 3.2f - flat.Normalized() * 1.5f;
			_walker.GlobalPosition = new Vector3(cam.X, _world.HeightAt(cam.X, cam.Z) - .7f, cam.Z);
			LookAt(g.GlobalPosition + flat.Normalized() * .6f + Vector3.Up * .25f);
			await Frames(3);
			await Shot($"{n}_c_rest");
		}
		GetTree().Quit();
	}

	/// <summary>Character lineup: BadCharacters soldiers from assets/soldiers, in four poses, photographed.</summary>
	private async Task RunCharTest(string dir)
	{
		Directory.CreateDirectory(dir);
		async Task Frames(int n) { for (int f = 0; f < n; f++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
		async Task Shot(string name)
		{
			await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
			GetViewport().GetTexture().GetImage().SavePng(Path.Combine(dir, name + ".png"));
			GD.Print($"[test] shot {name}");
		}
		_hud.Visible = false;
		_weather.AutoChange = false;
		foreach (var g in _guards) g.ProcessMode = ProcessModeEnum.Disabled;
		for (int f = 0; f < 1800 && (_waitingForGround || _trees.Pending > 0 || !_terrain.Settled); f++) await Frames(1);
		var files = DeadRecon.AI.SoldierBody.Catalogue("res://assets/soldiers/blocky");
		files.AddRange(DeadRecon.AI.SoldierBody.Catalogue("res://assets/soldiers/smooth").Take(4));
		GD.Print($"[chars] {files.Count} characters");
		// Stand them in a row in the camp clearing, facing a camera.
		var c = _mission.Camp;
		var bodies = new System.Collections.Generic.List<DeadRecon.AI.SoldierBody>();
		for (int i = 0; i < files.Count; i++)
		{
			var b = new DeadRecon.AI.SoldierBody { CharacterScene = files[i] };
			AddChild(b);
			var at = c + new Vector3((i - (files.Count - 1) / 2f) * 1.25f, 0, -3);
			b.GlobalPosition = new Vector3(at.X, _world.HeightAt(at.X, at.Z), at.Z);
			b.RotationDegrees = new Vector3(0, 180, 0); // face +Z, toward the camera
			bodies.Add(b);
		}
		_walker.SetFlying(true);
		_walker.GlobalPosition = new Vector3(c.X, _world.HeightAt(c.X, c.Z + 5) + 1.4f, c.Z + 5.5f);
		_walker.Look(0, -4);
		_weapon.Visible = false;
		var poses = new (string name, float speed, float ready, float pitch, float crouch)[]
		{
			("1_relaxed", 0, 0, 0, 0), ("2_aiming", 0, 1, 0, 0), ("3_walking", 1.5f, .2f, 0, 0), ("4_cover", 0, 1, .1f, 1),
		};
		foreach (var (name, speed, ready, pitch, crouch) in poses)
		{
			foreach (var b in bodies) { b.Speed = speed; b.Ready = ready; b.AimPitch = pitch; b.Crouch = crouch; }
			await Frames(40);
			await Shot(name);
		}
		// A close side view of one, aiming.
		foreach (var b in bodies) { b.Speed = 0; b.Ready = 1; b.Crouch = 0; }
		var one = bodies[bodies.Count / 2];
		_walker.GlobalPosition = one.GlobalPosition + new Vector3(2.2f, 1.3f, .9f);
		_walker.Look(68, -8);
		await Frames(20);
		await Shot("5_side");
		GetTree().Quit();
	}

	/// <summary>Automated HUD check: the player's view (no debug text) while being noticed, shot at, at the desk, and paused.</summary>
	private async Task RunHudTest(string dir)
	{
		Directory.CreateDirectory(dir);
		async Task Frames(int n) { for (int f = 0; f < n; f++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
		async Task Seconds(double t) => await ToSignal(GetTree().CreateTimer(t), SceneTreeTimer.SignalName.Timeout);
		async Task Shot(string name)
		{
			await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
			GetViewport().GetTexture().GetImage().SavePng(Path.Combine(dir, name + ".png"));
			GD.Print($"[test] shot {name}: {_mission.Objective()}");
		}
		void LookAt(Vector3 target)
		{
			var d = target - _walker.Camera.GlobalPosition;
			_walker.Look(Mathf.RadToDeg(MathF.Atan2(-d.X, -d.Z)), Mathf.RadToDeg(MathF.Atan2(d.Y, new Vector2(d.X, d.Z).Length())));
		}
		_hud.Visible = false;
		_weather.AutoChange = false;
		_walker.Invulnerable = true;
		var patrol = _guards.First(g => !g.Sentry);
		for (int f = 0; f < 1800 && (_waitingForGround || _trees.Pending > 0 || !_terrain.Settled); f++) await Frames(1);
		await Shot("0_start");

		// Stand where the patroller will notice us, looking a bit to his left so the arc points at him off-centre.
		_walker.SetFlying(true);
		_walker.GlobalPosition = _mission.Camp + Vector3.Up * 40;
		for (int f = 0; f < 600 && !_terrain.HasCollisionAt(_mission.Camp); f++) await Frames(1);
		await Seconds(1.5);
		_walker.SetFlying(false);
		float bestT = -1;
		void Reposition()
		{
			var eye = patrol.GlobalPosition + Vector3.Up * 1.6f;
			var fwd = -patrol.GlobalTransform.Basis.Z; fwd.Y = 0; fwd = fwd.Normalized();
			Vector3 spot = patrol.GlobalPosition + fwd * 20; bestT = -1;
			for (int c = 0; c < 48; c++)
			{
				var p = patrol.GlobalPosition + fwd.Rotated(Vector3.Up, (c % 8 - 3.5f) * .12f) * (14 + c / 8 * 3);
				p.Y = _world.HeightAt(p.X, p.Z) + .2f;
				float t = Foliage.RaycastTrunk(eye, p + Vector3.Up * 1.3f) == null ? Foliage.Transmission(eye, p + Vector3.Up * 1.3f) : 0;
				if (t > bestT) { bestT = t; spot = p; }
			}
			_walker.GlobalPosition = spot;
		}
		bool gotYellow = false;
		double moved = -10;
		for (int f = 0; f < 1500 && patrol.State != Guard.Mood.Alert; f++)
		{
			double now = Time.GetTicksMsec() / 1000.0;
			if (patrol.Suspicion < .05f && now - moved > 2.5) { moved = now; Reposition(); }
			var d = patrol.GlobalPosition - _walker.GlobalPosition;
			LookAt(_walker.Camera.GlobalPosition + d.Normalized().Rotated(Vector3.Up, .5f) * 10);
			await Frames(1);
			if (!gotYellow && patrol.Suspicion > .45f) { gotYellow = true; await Shot("1_being_noticed"); }
		}
		GD.Print($"[hud] visible {bestT * 100:0}%, patrol now {patrol.State} susp {patrol.Suspicion:0.00} exposure {patrol.Exposure:0.00} dist {patrol.GlobalPosition.DistanceTo(_walker.GlobalPosition):0}");
		for (int f = 0; f < 600 && _walker.HitsTaken < 2; f++) { LookAt(_walker.Camera.GlobalPosition + (patrol.GlobalPosition - _walker.GlobalPosition).Normalized().Rotated(Vector3.Up, -.9f) * 10); await Frames(1); }
		await Frames(2);
		await Shot("2_shot_at");

		// At the radio desk.
		_walker.GlobalPosition = _mission.IntelAt + new Vector3(-1.4f, -.4f, -.9f);
		LookAt(_mission.IntelAt);
		await Frames(6);
		await Shot("3_desk_prompt");

		_menu.Show();
		await Frames(4);
		await Shot("4_paused");
		_menu.Close();
		GetTree().Quit();
	}

	/// <summary>
	/// Automated squad check: (1) get spotted by the patroller: does the sentry hear the alarm, and how much of the
	/// fight does the patroller spend behind a trunk? (2) silently remove the sentry: does the patroller find the body?
	/// </summary>
	private async Task RunSquadTest(string dir)
	{
		Directory.CreateDirectory(dir);
		async Task Frames(int n) { for (int f = 0; f < n; f++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
		async Task Seconds(double t) => await ToSignal(GetTree().CreateTimer(t), SceneTreeTimer.SignalName.Timeout);
		async Task Shot(string name)
		{
			await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
			GetViewport().GetTexture().GetImage().SavePng(Path.Combine(dir, name + ".png"));
			GD.Print($"[test] shot {name}");
		}
		void LookAt(Vector3 target)
		{
			var d = target - _walker.Camera.GlobalPosition;
			_walker.Look(Mathf.RadToDeg(MathF.Atan2(-d.X, -d.Z)), Mathf.RadToDeg(MathF.Atan2(d.Y, new Vector2(d.X, d.Z).Length())));
		}
		double Now() => Time.GetTicksMsec() / 1000.0;
		var patrol = _guards.First(g => !g.Sentry);
		var sentry = _guards.First(g => g.Sentry);

		_weather.AutoChange = false;
		_walker.Invulnerable = true;
		for (int f = 0; f < 1800 && (_waitingForGround || _trees.Pending > 0 || !_terrain.HasCollisionAt(_mission.Camp)); f++) await Frames(1);
		// Wait at the camp edge (flying = ignored) until collision is there for everyone.
		_walker.SetFlying(true);
		_walker.GlobalPosition = _mission.Camp + Vector3.Up * 40;
		for (int f = 0; f < 600 && !_terrain.Settled; f++) await Frames(1);
		await Seconds(2);

		// ---- 0. Camp life while nobody knows we're here (hovering high = ignored).
		await Seconds(45);
		GD.Print($"[squad] calm 45 s: {Guard.Chats_} chats, {Guard.SitsStarted} sit-downs; {_guards.Count} guards in camp");

		// ---- 1. Spotted: alarm and cover
		Vector3 BestSpot()
		{
			var eye = patrol.GlobalPosition + Vector3.Up * 1.6f;
			var f2 = -patrol.GlobalTransform.Basis.Z; f2.Y = 0; f2 = f2.Normalized();
			Vector3 best = Vector3.Zero; float bestT = -1;
			for (int c = 0; c < 40; c++)
			{
				var p = patrol.GlobalPosition + f2.Rotated(Vector3.Up, (c % 8 - 3.5f) * .12f) * (24 + c / 8 * 3);
				p.Y = _world.HeightAt(p.X, p.Z) + .2f;
				float t = Foliage.RaycastTrunk(eye, p + Vector3.Up * 1.3f) == null ? Foliage.Transmission(eye, p + Vector3.Up * 1.3f) : 0;
				if (t > bestT) { bestT = t; best = p; }
			}
			return best;
		}
		_walker.SetFlying(false);
		double t0 = Now(), moved = 0;
		while (patrol.State != Guard.Mood.Alert && Now() - t0 < 30)
		{
			if (Now() - moved > 3 && patrol.Suspicion < .3f) { moved = Now(); _walker.GlobalPosition = BestSpot(); }
			LookAt(patrol.GlobalPosition + Vector3.Up * 1.4f);
			await Frames(1);
		}
		GD.Print($"[squad] patrol alerted after {Now() - t0:0.0} s at {patrol.GlobalPosition.DistanceTo(_walker.GlobalPosition):0} m; sentry is now: {sentry.State}");
		await Seconds(1);
		GD.Print($"[squad] 1 s later the sentry is: {sentry.State} ({sentry.GlobalPosition.DistanceTo(_walker.GlobalPosition):0} m from you)");

		int samples = 0, hidden = 0, peeking = 0, wood = 0, shotsAtMe = _walker.HitsTaken;
		bool hidShot = false, peekShot = false;
		_weapon.ForceAim(true);
		for (double end = Now() + 15; Now() < end;)
		{
			LookAt(patrol.GlobalPosition + Vector3.Up * 1.2f);
			await Seconds(.25);
			samples++;
			if (patrol.InCover && !patrol.Peeking) hidden++;
			if (patrol.Peeking) peeking++;
			if (Foliage.RaycastTrunk(_walker.Camera.GlobalPosition, patrol.GlobalPosition + Vector3.Up * 1.0f) != null) wood++;
			if (!hidShot && patrol.InCover && !patrol.Peeking && samples > 8) { hidShot = true; await Shot("1_hiding"); }
			if (!peekShot && patrol.Peeking && samples > 8) { peekShot = true; await Shot("2_peeking"); }
		}
		_weapon.ForceAim(false);
		GD.Print($"[squad] flanks started {Guard.FlanksStarted}, suppressive shots {Guard.SuppressShots}; sentry flanking now {sentry.Flanking}, {sentry.GlobalPosition.DistanceTo(_walker.GlobalPosition):0} m from me");
		GD.Print($"[squad] 15 s of fight: patrol hiding {hidden * 100 / samples}%, peeking {peeking * 100 / samples}%, " +
				 $"a trunk between us {wood * 100 / samples}% of the time; he hit me {_walker.HitsTaken - shotsAtMe} times; sentry: {sentry.State}");

		// ---- 2. A body
		sentry.TakeHit(500, sentry.GlobalPosition + Vector3.Up, sentry.GlobalPosition); // silently
		// Bodies in the ferns stay hidden (by design); for this check, leave him in the open middle of the camp.
		var open = _mission.Camp + new Vector3(-1, 0, -4);
		sentry.GlobalPosition = new Vector3(open.X, _world.HeightAt(open.X, open.Z) + .05f, open.Z);
		_walker.SetFlying(true);
		_walker.GlobalPosition = _mission.Camp + new Vector3(0, 60, 0);
		t0 = Now();
		bool found = false;
		while (Now() - t0 < 90)
		{
			await Seconds(.25);
			if (patrol.BodiesFound > 0) { found = true; break; }
		}
		GD.Print($"[squad] body: {(found ? $"found after {Now() - t0:0.0} s" : "not found in 90 s")}; patrol is {patrol.State}");
		if (found)
		{
			_walker.GlobalPosition = patrol.GlobalPosition + new Vector3(5, 3, 5);
			LookAt(patrol.GlobalPosition + Vector3.Up);
			await Frames(4);
			await Shot("3_found_body");
		}
		GetTree().Quit();
	}

	/// <summary>
	/// Automated mission run: overview of the camp, approach, take out both guards, take the intel, extract.
	/// Screenshots of each step and the debrief; logs the mission state. Then quit.
	/// </summary>
	private async Task RunMissionTest(string dir)
	{
		Directory.CreateDirectory(dir);
		async Task Frames(int n) { for (int f = 0; f < n; f++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
		async Task Seconds(double t) => await ToSignal(GetTree().CreateTimer(t), SceneTreeTimer.SignalName.Timeout);
		async Task Shot(string name)
		{
			await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
			GetViewport().GetTexture().GetImage().SavePng(Path.Combine(dir, name + ".png"));
			GD.Print($"[test] shot {name}: {_mission.State}, health {_walker.Health:0}, {_mission.Objective()}");
		}
		void LookAt(Vector3 target)
		{
			var d = target - _walker.Camera.GlobalPosition;
			_walker.Look(Mathf.RadToDeg(MathF.Atan2(-d.X, -d.Z)), Mathf.RadToDeg(MathF.Atan2(d.Y, new Vector2(d.X, d.Z).Length())));
		}
		async Task Settle() { for (int f = 0; f < 1800 && (_trees.Pending > 0 || !_terrain.Settled); f++) await Frames(1); await Frames(20); }
		void Put(Vector3 p, bool fly = false) { _walker.SetFlying(fly); _walker.GlobalPosition = new Vector3(p.X, _world.HeightAt(p.X, p.Z) + (fly ? p.Y : .2f), p.Z); }

		_weather.AutoChange = false;
		for (int f = 0; f < 1800 && _waitingForGround; f++) await Frames(1);
		await Settle();
		await Shot("0_start");

		// 1. The camp from the air (guards ignore a flying player).
		var camp = _mission.Camp;
		var toCamp = (camp - _spawn.position); toCamp.Y = 0; toCamp = toCamp.Normalized();
		Put(camp - toCamp * 28 + Vector3.Up * 14, fly: true);
		_walker.GlobalPosition = new Vector3(_walker.GlobalPosition.X, _world.HeightAt(camp.X, camp.Z) + 16, _walker.GlobalPosition.Z);
		LookAt(camp);
		await Settle();
		await Shot("1_camp_overview");

		// 2. Approach on foot to ~30 m, to a spot where no trunk is between us and the radio desk, and look through the scope.
		Vector3 spot = camp - toCamp * 30;
		for (int k = 0; k < 48; k++)
		{
			float ang = k * .37f, r = 24 + (k % 4) * 3;
			var c = camp - toCamp.Rotated(Vector3.Up, MathF.Sin(ang) * 1.2f) * r;
			c.Y = _world.HeightAt(c.X, c.Z) + 1.6f;
			if (Foliage.RaycastTrunk(c, _mission.IntelAt + Vector3.Up * 1.2f) == null) { spot = c; break; }
		}
		Put(spot);
		LookAt(_mission.IntelAt + Vector3.Up * .5f);
		await Settle();
		_weapon.ForceAim(true);
		await Frames(3);
		await Shot("2_recon_scope");

		// 3. Take out both guards (aimed headshots, a few tries each).
		foreach (var g in _guards)
		{
			for (int k = 0; k < 10 && g.State != Guard.Mood.Dead && !_walker.Dead; k++)
			{
				// Step to a spot with a clear line to this guard if a trunk is in the way.
				var head = g.GlobalPosition + Vector3.Up * 1.62f;
				if (Foliage.RaycastTrunk(_walker.Camera.GlobalPosition, head) != null)
					for (int m = 0; m < 24; m++)
					{
						var c = g.GlobalPosition + new Vector3(MathF.Cos(m * .9f), 0, MathF.Sin(m * .9f)) * (18 + m % 3 * 4);
						c.Y = _world.HeightAt(c.X, c.Z) + 1.6f;
						if (Foliage.RaycastTrunk(c, head) == null) { Put(c); await Frames(3); break; }
					}
				LookAt(head);
				await Frames(2);
				if (_weapon.Ammo == 0) _weapon.TestReload();
				_weapon.TestFire();
				await Seconds(.25);
			}
			GD.Print($"[test] guard {(g.Sentry ? "sentry" : "patrol")}: {g.State}");
		}
		_weapon.ForceAim(false);
		await Seconds(1);

		// 4. Walk up to the desk and take the intel (hold E is simulated).
		Put(_mission.IntelAt + new Vector3(-1.4f, 0, -.9f));
		LookAt(_mission.IntelAt);
		await Frames(10);
		await Shot("3_at_the_desk");
		_mission.TestTakeIntel();
		await Frames(5);

		// 5. Look toward the extraction smoke from the camp, then go there.
		LookAt(_mission.Extraction + Vector3.Up * 4);
		await Frames(5);
		await Shot("4_extraction_marker");
		Put(_mission.Extraction + new Vector3(12, 0, 0));
		LookAt(_mission.Extraction + Vector3.Up * 2);
		await Settle();
		await Shot("5_landing_zone");
		Put(_mission.Extraction + new Vector3(2, 0, 0));
		await Frames(10);
		await Shot("6_debrief");
		GD.Print($"[test] done: {_mission.State}, {_mission.OperationName}");
		GetTree().Quit();
	}

	/// <summary>
	/// Automated rain check: the same views dry, in rain and in a storm (with a lightning flash), plus the gameplay
	/// numbers (how far footsteps and gunshots carry, how far the guard sees). Screenshots + log, then quit.
	/// </summary>
	/// <summary>Offset from the spawn to the spot within 40 m with the thickest canopy overhead.</summary>
	private Vector3 ForestSpot(Vector3 from)
	{
		Vector3 best = Vector3.Zero; float least = 2;
		for (int k = 0; k < 120; k++)
		{
			float a = k * 2.4f, r = 6 + k * .3f;
			var o = new Vector3(MathF.Cos(a) * r, 0, MathF.Sin(a) * r);
			var p = from + o; p.Y = _world.HeightAt(p.X, p.Z) + 1.6f;
			float sky = Foliage.SkyAbove(p);
			if (sky < least) { least = sky; best = new Vector3(o.X, _world.HeightAt(p.X, p.Z) + .2f - from.Y, o.Z); }
		}
		return best;
	}

	private async Task RunRainTest(string dir)
	{
		Directory.CreateDirectory(dir);
		async Task Frames(int n) { for (int f = 0; f < n; f++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
		async Task Shot(string name)
		{
			await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
			GetViewport().GetTexture().GetImage().SavePng(Path.Combine(dir, name + ".png"));
			GD.Print($"[test] shot {name}: {Engine.GetFramesPerSecond()} fps, weather {_weather.Describe()} rain {_weather.Intensity:0.00} wet {_weather.Wetness:0.00} canopy {_weather.UnderCanopy:0.00}");
		}
		_weather.AutoChange = false;
		for (int f = 0; f < 1800 && (_waitingForGround || _trees.Pending > 0 || !_terrain.Settled); f++) await Frames(1);
		var start = _walker.GlobalPosition;
		float yaw = Mathf.RadToDeg(_walker.Rotation.Y);
		var views = new (string name, Vector3 offset, float yaw, float pitch, bool fly)[]
		{
			("river", Vector3.Zero, yaw, -8, false),            // open view toward the river: rain streaks, wet bank
			("forest", ForestSpot(start), yaw + 180, -4, false), // under the canopy: much less rain gets through
			("ground", Vector3.Zero, yaw + 150, -40, false),    // looking down: wet litter, puddles, splashes
		};
		foreach (var (kind, label) in new[] { (Weather.Kind.Clear, "dry"), (Weather.Kind.Rain, "rain"), (Weather.Kind.Storm, "storm") })
		{
			_weather.SetNow(kind);
			GD.Print($"[rain] {label}: running steps carry {Hearing.Carry(Hearing.RunningSteps):0.0} m (of {Hearing.RunningSteps}), walking {Hearing.Carry(Hearing.WalkingSteps):0.0} m, " +
					 $"gunshot {Hearing.Carry(Hearing.Gunshot):0} m, tree crash {Hearing.Carry(140):0} m; guard sight x{_weather.SightFactor:0.00}");
			foreach (var v in views)
			{
				_walker.SetFlying(v.fly);
				_walker.GlobalPosition = start + v.offset;
				_walker.Look(v.yaw, v.pitch);
				await Frames(45); // let the drops fill the air
				await Shot($"{label}_{v.name}");
			}
		}
		// A lightning flash, caught mid-flicker.
		_walker.Look(yaw, 5);
		await Frames(20);
		_weather.Strike(800);
		await Frames(2);
		await Shot("storm_lightning");
		GD.Print($"[test] done: {_weather.LightningStrikes} strikes");
		GetTree().Quit();
	}

	/// <summary>
	/// Automated tree check: shoot a sapling and a big tree until each falls. Logs hits needed and what the guard
	/// made of it; screenshots of splinters, falling and fallen. Then quit.
	/// </summary>
	private async Task RunTreeTest(string dir)
	{
		Directory.CreateDirectory(dir);
		async Task Frames(int n) { for (int f = 0; f < n; f++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
		async Task Seconds(double s) => await ToSignal(GetTree().CreateTimer(s), SceneTreeTimer.SignalName.Timeout);
		async Task Shot(string name)
		{
			await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
			GetViewport().GetTexture().GetImage().SavePng(Path.Combine(dir, name + ".png"));
			GD.Print($"[test] shot {name}: trees felled {_trees.TreesFelled}, guard {GuardStatus()}");
		}
		void LookAt(Vector3 target)
		{
			var d = target - _walker.Camera.GlobalPosition;
			_walker.Look(Mathf.RadToDeg(MathF.Atan2(-d.X, -d.Z)), Mathf.RadToDeg(MathF.Atan2(d.Y, new Vector2(d.X, d.Z).Length())));
		}
		// A trunk we can see directly (it's the first thing a shot at it would hit), within a size band.
		Foliage.Plant? Pick(float minR, float maxR, float minDist, float maxDist)
		{
			var eye = _walker.Camera.GlobalPosition;
			Foliage.Plant? best = null; float bestD = float.MaxValue;
			foreach (var p in Foliage.TrunksNear(_walker.GlobalPosition, maxDist))
			{
				if (p.TrunkRadius < minR || p.TrunkRadius > maxR) continue;
				var aim = new Vector3(p.X, p.BaseY + 1.3f, p.Z);
				float d = eye.DistanceTo(aim);
				if (d < minDist || d > bestD) continue;
				if (Foliage.RaycastTrunk(eye, aim + (aim - eye).Normalized() * 2) is not { } first || !first.Plant.Equals(p)) continue;
				best = p; bestD = d;
			}
			return best;
		}
		async Task<int> FellIt(Foliage.Plant p, string label)
		{
			int felledBefore = _trees.TreesFelled, shots = 0;
			_weapon.ForceAim(true);
			while (_trees.TreesFelled == felledBefore && shots < 400)
			{
				LookAt(new Vector3(p.X, p.BaseY + 1.2f, p.Z));
				await Frames(2);
				if (_weapon.Ammo == 0) _weapon.TestReload();
				_weapon.TestFire();
				shots++;
				if (shots == 1) { await Frames(2); await Shot($"{label}_1_splinters"); }
				await Frames(3);
			}
			_weapon.ForceAim(false);
			GD.Print($"[test] {label}: trunk radius {p.TrunkRadius:0.00} m, felled after {shots} shots");
			// Watch from the side: fly 10 m off to the side of the line of fire (the guard ignores a flying player).
			var back = _walker.GlobalPosition;
			var trunk = new Vector3(p.X, p.BaseY, p.Z);
			var line = (trunk - back); line.Y = 0; line = line.Normalized();
			var view = trunk - line * 4 + line.Cross(Vector3.Up) * 10;
			_walker.SetFlying(true);
			_walker.GlobalPosition = new Vector3(view.X, _world.HeightAt(view.X, view.Z) + .6f, view.Z);
			LookAt(trunk + line * 5 + Vector3.Up * 3.5f);
			await Seconds(1.2);
			await Shot($"{label}_2_falling");
			await Seconds(3.5);
			LookAt(trunk + line * 4 + Vector3.Up * 1f);
			await Frames(3);
			await Shot($"{label}_3_fallen");
			_walker.GlobalPosition = back;
			_walker.SetFlying(false);
			await Frames(3);
			return shots;
		}

		for (int f = 0; f < 1800 && (_waitingForGround || _trees.Pending > 0); f++) await Frames(1);
		await Seconds(1);
		var sapling = Pick(.05f, .15f, 5, 25);
		var tree = Pick(.24f, .5f, 8, 30);
		GD.Print($"[test] sapling {(sapling is { } a ? $"r {a.TrunkRadius:0.00} m" : "none")}, tree {(tree is { } b ? $"r {b.TrunkRadius:0.00} m" : "none")}");
		if (sapling is { } s1) await FellIt(s1, "sapling");
		await Seconds(2);
		GD.Print($"[test] guard after the crash: {GuardStatus()}");
		if (tree is { } t1) await FellIt(t1, "tree");
		// Scattered shots: 15 hits spread from 0.5 to 3.5 m up a big trunk should scar it, not cut it.
		if (Pick(.24f, .5f, 8, 30) is { } t2)
		{
			int before = _trees.TreesFelled;
			_weapon.ForceAim(true);
			for (int k = 0; k < 15; k++)
			{
				LookAt(new Vector3(t2.X, t2.BaseY + .5f + 3f * (k % 7) / 6f, t2.Z));
				await Frames(2);
				if (_weapon.Ammo == 0) _weapon.TestReload();
				_weapon.TestFire();
				await Frames(2);
			}
			_weapon.ForceAim(false);
			GD.Print($"[test] scattered: 15 hits over 3 m on a {t2.TrunkRadius:0.00} m trunk -> worst band at {_trees.WorstBand(t2) * 100:0}% of cutting, felled {_trees.TreesFelled > before}");
		}
		await Seconds(1);
		GD.Print($"[test] done: felled {_trees.TreesFelled}, guard {GuardStatus()}");
		GetTree().Quit();
	}

	/// <summary>
	/// Test numbers: from random spots near the player, how visible is a standing vs crouching person at several
	/// distances (plants only, same exposure formula as the guard). Prints a small table.
	/// </summary>
	private void MeasureCover()
	{
		GD.Print($"[cover] plants registered for sight: {Foliage.Count:N0}");
		var rng = new RandomNumberGenerator { Seed = 1234 };
		var centre = _walker.GlobalPosition;
		foreach (float dist in new[] { 10f, 20f, 30f, 45f })
		{
			double standSum = 0, crouchSum = 0; int standHidden = 0, crouchHidden = 0, n = 200;
			for (int k = 0; k < n; k++)
			{
				float a = rng.Randf() * MathF.Tau, b = rng.Randf() * MathF.Tau, r = rng.Randf() * 40;
				var g = centre + new Vector3(MathF.Cos(a) * r, 0, MathF.Sin(a) * r);
				var p = g + new Vector3(MathF.Cos(b) * dist, 0, MathF.Sin(b) * dist);
				g.Y = _world.HeightAt(g.X, g.Z); p.Y = _world.HeightAt(p.X, p.Z);
				var eye = g + Vector3.Up * 1.6f;
				float Exp(float chest, float head) => Math.Max(Foliage.Transmission(eye, p + Vector3.Up * chest), Foliage.Transmission(eye, p + Vector3.Up * head) * .7f);
				float stand = Exp(1.3f, 1.65f), crouch = Exp(.8f, 1.05f);
				standSum += stand; crouchSum += crouch;
				if (stand <= .12f) standHidden++;
				if (crouch <= .12f) crouchHidden++;
			}
			GD.Print($"[cover] {dist,2:0} m: standing avg exposure {standSum / n * 100:0}% (fully hidden {standHidden * 100 / n}%), " +
					 $"crouching avg {crouchSum / n * 100:0}% (fully hidden {crouchHidden * 100 / n}%)");
		}
	}

	/// <summary>How hidden the player is from the guard by plants (whether or not he's looking), and what he sees now.</summary>
	private string CoverStatus()
	{
		var _guard = NearestGuard();
		if (_guard.State == Guard.Mood.Dead) return "cover: nobody left to hide from";
		var eye = _guard.GlobalPosition + Vector3.Up * 1.6f;
		if (eye.DistanceTo(_walker.GlobalPosition) > 90) return "cover: out of his sight range";
		float through = Math.Max(Foliage.Transmission(eye, _walker.Chest), Foliage.Transmission(eye, _walker.Camera.GlobalPosition) * .7f);
		string sees = _guard.Exposure > 0 ? $"he sees {_guard.Exposure * 100:0}% of you" : "he isn't looking at you";
		return $"cover from plants: {(1 - through) * 100:0}%   {sees}";
	}

	private Guard NearestGuard()
	{
		Guard best = _guard; float bd = float.MaxValue;
		foreach (var g in _guards)
		{
			float d = g.GlobalPosition.DistanceTo(_walker.GlobalPosition) + (g.State == Guard.Mood.Dead ? 1e6f : 0);
			if (d < bd) { bd = d; best = g; }
		}
		return best;
	}

	private string GuardStatus()
	{
		var _guard = NearestGuard();
		float d = _guard.GlobalPosition.DistanceTo(_walker.GlobalPosition);
		string mood = _guard.State switch
		{
			Guard.Mood.Patrol => "patrolling, grumpily",
			Guard.Mood.Suspicious => "suspicious",
			Guard.Mood.Investigate => "coming to look",
			Guard.Mood.Alert => "ALERT",
			Guard.Mood.Search => "searching",
			_ => "has retired permanently",
		};
		return $"{mood} ({_guard.Suspicion * 100:0}%, {d:0} m, health {Math.Max(0, _guard.Health):0})";
	}

	/// <summary>Spawn on dry ground a little way from the water nearest the centre, facing the water.</summary>
	private (Vector3 position, float yaw) FindSpawn()
	{
		int mid = _world.SamplesPerSide / 2, best = int.MaxValue, bx = mid, bz = mid;
		for (int gz = 0; gz < _world.SamplesPerSide; gz += 8)
		for (int gx = 0; gx < _world.SamplesPerSide; gx += 8)
		{
			if (!_world.IsWet(gx, gz)) continue;
			int d = (gx - mid) * (gx - mid) + (gz - mid) * (gz - mid);
			if (d < best) { best = d; bx = gx; bz = gz; }
		}
		// Walk from the water back toward the centre until dry and a few metres clear.
		var dir = new Vector2(mid - bx, mid - bz);
		dir = dir.Length() > 0 ? dir.Normalized() : Vector2.Right;
		int x = bx, z = bz;
		for (int k = 0; k < 400; k++)
		{
			x = bx + (int)(dir.X * k); z = bz + (int)(dir.Y * k);
			if (!_world.NearWater(x, z, 6)) break;
		}
		float wx = _world.SampleToWorld(x), wz = _world.SampleToWorld(z);
		float yaw = Mathf.RadToDeg(MathF.Atan2(dir.X, dir.Y)); // face back toward the water (-Z forward)
		return (new Vector3(wx, _world.HeightAt(wx, wz) + .2f, wz), yaw);
	}

	/// <summary>Humid tropical daylight: hazy sky, thick aerial perspective, mist pooling in low valleys.</summary>
	private static WorldEnvironment MakeEnvironment()
	{
		var sky = new ShaderMaterial { Shader = GD.Load<Shader>("res://game/World/sky.gdshader") };
		var env = new Godot.Environment
		{
			BackgroundMode = Godot.Environment.BGMode.Sky,
			Sky = new Sky { SkyMaterial = sky },
			AmbientLightSource = Godot.Environment.AmbientSource.Sky,
			AmbientLightEnergy = .8f,
			TonemapMode = Godot.Environment.ToneMapper.Filmic,
			SsaoEnabled = true, SsaoRadius = 2.0f, SsaoIntensity = 2.6f,
			FogEnabled = true,
			FogLightColor = new Color(.64f, .71f, .64f),
			FogDensity = .0007f,
			FogAerialPerspective = .7f,
			FogSkyAffect = .5f,
			FogHeight = 220,           // metres above sea level: valleys below this fill with mist
			FogHeightDensity = .0015f,
			GlowEnabled = true,
			AdjustmentEnabled = true, AdjustmentSaturation = 1.12f,
		};
		return new WorldEnvironment { Environment = env };
	}

	/// <summary>Automated check: a few viewpoints, each saved as a PNG once streaming has settled; then quit.</summary>
	private async Task TakeShots(string dir)
	{
		Directory.CreateDirectory(dir);
		var start = _walker.GlobalPosition;
		float startYaw = Mathf.RadToDeg(_walker.Rotation.Y);
		var views = new (string name, Vector3 offset, float yaw, float pitch, bool fly)[]
		{
			("1_ground", Vector3.Zero, startYaw, -2, false),
			("2_treetops", new Vector3(0, 60, 0), startYaw, -12, true),
			("3_overview", new Vector3(0, 700, 0), startYaw, -28, true),
			("4_forest", Vector3.Zero, startYaw + 180, 0, false),
			("5_forest_painted", Vector3.Zero, startYaw + 180, 0, false),
		};
		foreach (var v in views)
		{
			_terrain.TextureMix = v.name.Contains("painted") ? 0 : 1;
			_walker.SetFlying(v.fly);
			_walker.GlobalPosition = start + v.offset;
			_walker.Look(v.yaw, v.pitch);
			for (int f = 0; f < 1800 && (f < 30 || !_terrain.Settled || _trees.Pending > 0); f++)
				await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
			for (int f = 0; f < 20; f++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
			await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
			GetViewport().GetTexture().GetImage().SavePng(Path.Combine(dir, v.name + ".png"));
			GD.Print($"shot {v.name}: fps {Engine.GetFramesPerSecond()}, trees {_trees.TreesShown}, plants {_cover.PlantsShown}, on floor {_walker.IsOnFloor()}, y {_walker.GlobalPosition.Y:0.0}");
		}
		// Gun: hip, aimed, firing (from the forest view).
		var guns = new (string name, bool aim, bool flash)[] { ("6_gun_hip", false, false), ("7_gun_aim", true, false), ("8_gun_fire", false, true) };
		_walker.SetFlying(false);
		_walker.GlobalPosition = start;
		_walker.Look(startYaw + 180, -6);
		foreach (var g in guns)
		{
			_weapon.ForceAim(g.aim);
			for (int f = 0; f < 15; f++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
			if (g.flash) { _weapon.FlashForShot(); await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
			await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
			GetViewport().GetTexture().GetImage().SavePng(Path.Combine(dir, g.name + ".png"));
			GD.Print($"shot {g.name}: weapon loaded {_weapon.Loaded}, fov {_walker.Camera.Fov:0}, scoped {_weapon.Scoped}");
		}
		// Bullets: look at the ground a few metres ahead, fire five rounds, photograph the holes.
		_weapon.ForceAim(false);
		_walker.Look(startYaw + 180, -35);
		for (int f = 0; f < 10; f++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
		for (int k = 0; k < 5; k++)
		{
			_walker.Look(startYaw + 180 + (k - 2) * 2.5f, -35 + (k % 2) * 3);
			_weapon.TestFire();
			for (int f = 0; f < 8; f++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
		}
		_walker.Look(startYaw + 180, -35);
		for (int f = 0; f < 4; f++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
		await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
		GetViewport().GetTexture().GetImage().SavePng(Path.Combine(dir, "9_impacts.png"));
		GD.Print($"shot 9_impacts: ammo {_weapon.Ammo}");
		GetTree().Quit();
	}
}
