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
		_hud = new Label { Position = new Vector2(12, 10) };
		_hud.AddThemeColorOverride("font_shadow_color", Colors.Black);
		var layer = new CanvasLayer();
		layer.AddChild(_hud);
		AddChild(layer);

		string dir = ProjectSettings.GlobalizePath($"res://world/{Arg("world", "jungle")}");
		if (!File.Exists(Path.Combine(dir, "world.json")))
		{
			_hud.Text = $"No baked world at {dir}\nBake it first:\n  dotnet run -c Release --project tools/WorldBaker -- content/world/jungle.world.json";
			return;
		}
		_world = BakedWorld.Load(dir);
		Foliage.Clear(); // static registry: a fresh run (new operation) re-registers what it plants

		var worldEnv = MakeEnvironment();
		AddChild(worldEnv);
		var sun = new DirectionalLight3D
		{
			RotationDegrees = new Vector3(-42, 35, 0), ShadowEnabled = true, LightEnergy = 1.3f,
			LightColor = new Color(1f, .93f, .80f), DirectionalShadowMaxDistance = 200,
		};
		AddChild(sun);

		_walker = new Walker { ProcessMode = ProcessModeEnum.Disabled };
		AddChild(_walker);
		var spawn = FindSpawn();
		_spawn = spawn;
		_walker.GlobalPosition = spawn.position;
		_walker.Look(spawn.yaw, -4);
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
		bool testing = args.Any(a => a.StartsWith("--shots") || a.StartsWith("--aitest") || a.StartsWith("--treetest") || a.StartsWith("--raintest") || a.StartsWith("--missiontest") || a.StartsWith("--squadtest") || a.StartsWith("--hudtest"));
		int seed = int.TryParse(Arg("seed", ""), out int fixedSeed) ? fixedSeed : (int)(Time.GetUnixTimeFromSystem() * 1000 % int.MaxValue);
		_mission = new Mission { World = _world, Player = _walker, Rifle = _weapon, SkipBriefing = testing };
		_mission.Plan(seed, spawn.position);

		_terrain = new TerrainStreamer { World = _world, Viewer = _walker };
		_trees = new TreeScatter { World = _world, Viewer = _walker };
		_cover = new GroundCover { World = _world, Viewer = _walker };
		AddChild(_terrain);
		AddChild(_trees);
		AddChild(_cover);

		// The camp's guards: a grumpy patroller and a grumpier sentry by the radio desk.
		Guard AddGuard(Vector3 home, bool sentry)
		{
			home.Y = _world.HeightAt(home.X, home.Z) + .2f;
			var g = new Guard { Target = _walker, Terrain = _terrain, World = _world, Home = home, Sentry = sentry, PatrolMin = 16, PatrolMax = 26 };
			AddChild(g);
			g.GlobalPosition = home;
			_guards.Add(g);
			_mission.Guards.Add(g);
			return g;
		}
		_guard = AddGuard(_mission.Camp + new Vector3(0, 0, -2), sentry: false);
		AddGuard(_mission.IntelAt + new Vector3(.8f, 0, 1.6f), sentry: true);
		_mission.Trees = _trees;
		AddChild(_mission);

		_weather = new Weather { Camera = _walker.Camera, Environment = worldEnv.Environment, Sun = sun, Terrain = _terrain, Trees = _trees, Cover = _cover };
		AddChild(_weather);

		// Player HUD (the debug text above is F3) and the Esc menu.
		layer.AddChild(new DeadRecon.UI.Hud { Player = _walker, Rifle = _weapon, Mission = _mission, Weather = _weather });
		_menu = new DeadRecon.UI.PauseMenu { Walker = _walker };
		_menu.Changed += () => _hud.Visible = DeadRecon.UI.Settings.ShowDebug;
		AddChild(_menu);
		_hud.Visible = DeadRecon.UI.Settings.ShowDebug || testing;

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
		string hudTest = Arg("hudtest", "");
		if (hudTest != "") _ = RunHudTest(hudTest);
		string squadTest = Arg("squadtest", "");
		if (squadTest != "") _ = RunSquadTest(squadTest);
	}

	public override void _Process(double delta)
	{
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
			"click: grab mouse   WASD/Shift/Space  Ctrl crouch   LMB fire  RMB aim  R reload   F: fly (Space/C up/down)   N: weather   T: textured/painted ground   Esc: release mouse";
		_hurt.Color = new Color(.6f, 0, 0, _walker.Dead ? .35f : _walker.HurtFlash * .45f);
		_deathText.Visible = false; // the mission panel says it
		_crosshair.Visible = _weapon.AimAmount < .5f;
	}

	public override void _UnhandledInput(InputEvent e)
	{
		if (e is InputEventKey { Pressed: true, Echo: false, PhysicalKeycode: Key.F3 } && _hud != null)
		{
			DeadRecon.UI.Settings.ShowDebug = !DeadRecon.UI.Settings.ShowDebug;
			DeadRecon.UI.Settings.Save();
			_hud.Visible = DeadRecon.UI.Settings.ShowDebug;
		}
		if (e is InputEventKey { Pressed: true, Echo: false, PhysicalKeycode: Key.N } && _weather != null)
			_weather.Cycle();
		if (e is InputEventKey { Pressed: true, Echo: false, PhysicalKeycode: Key.T } && _terrain != null)
			_terrain.TextureMix = _terrain.TextureMix > .5f ? 0 : 1;
		// Dead or done: Enter starts a new operation (same jungle, new camp; felled trees stay felled).
		if (e is InputEventKey { Pressed: true, Echo: false, PhysicalKeycode: Key.Enter or Key.KpEnter } && _mission != null
		    && (_walker.Dead || _mission.State == Mission.Phase.Complete))
			GetTree().ReloadCurrentScene();
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
		var sky = new ProceduralSkyMaterial
		{
			SkyTopColor = new Color(.34f, .48f, .60f), SkyHorizonColor = new Color(.78f, .80f, .74f),
			GroundHorizonColor = new Color(.62f, .66f, .58f), GroundBottomColor = new Color(.14f, .17f, .12f),
			SunAngleMax = 40,
		};
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
