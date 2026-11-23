using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DeadRecon.AI;
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
	private ColorRect _hurt = null!;
	private Label _deathText = null!;
	private (Vector3 position, float yaw) _spawn;
	private bool _waitingForGround = true;

	public override void _Ready()
	{
		var args = OS.GetCmdlineUserArgs();
		string Arg(string name, string fallback) =>
			args.FirstOrDefault(a => a.StartsWith($"--{name}="))?.Split('=', 2)[1] ?? fallback;

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

		AddChild(MakeEnvironment());
		AddChild(new DirectionalLight3D
		{
			RotationDegrees = new Vector3(-42, 35, 0), ShadowEnabled = true, LightEnergy = 1.3f,
			LightColor = new Color(1f, .93f, .80f), DirectionalShadowMaxDistance = 200,
		});

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
			Text = "You were shot by a grumpy man.\nPress Enter to try again.",
		};
		_deathText.AddThemeFontSizeOverride("font_size", 34);
		_deathText.AddThemeColorOverride("font_shadow_color", Colors.Black);
		_deathText.SetAnchorsPreset(Control.LayoutPreset.FullRect);
		layer.AddChild(_deathText);

		_terrain = new TerrainStreamer { World = _world, Viewer = _walker };
		_trees = new TreeScatter { World = _world, Viewer = _walker };
		_cover = new GroundCover { World = _world, Viewer = _walker };
		AddChild(_terrain);
		AddChild(_trees);
		AddChild(_cover);

		// One grumpy guard, ~90 m into the forest behind the spawn point (you spawn facing the river), out of sight range.
		float backYaw = Mathf.DegToRad(spawn.yaw + 180);
		var home = spawn.position + new Vector3(-MathF.Sin(backYaw), 0, -MathF.Cos(backYaw)) * 90;
		home.Y = _world.HeightAt(home.X, home.Z) + .2f;
		_guard = new Guard { Target = _walker, Terrain = _terrain, World = _world, Home = home };
		AddChild(_guard);
		_guard.GlobalPosition = home;

		string shots = Arg("shots", "");
		if (shots != "") _ = TakeShots(shots);
		string aiTest = Arg("aitest", "");
		if (aiTest != "") _ = RunAiTest(aiTest);
		string treeTest = Arg("treetest", "");
		if (treeTest != "") _ = RunTreeTest(treeTest);
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
			$"health {_walker.Health:0}   guard: {GuardStatus()}\n" +
			$"{CoverStatus()}\n" +
			(_waitingForGround ? "loading ground...\n" : "") +
			"click: grab mouse   WASD/Shift/Space  Ctrl crouch   LMB fire  RMB aim  R reload   F: fly (Space/C up/down)   T: textured/painted ground   Esc: release mouse";
		_hurt.Color = new Color(.6f, 0, 0, _walker.Dead ? .35f : _walker.HurtFlash * .45f);
		_deathText.Visible = _walker.Dead;
		_crosshair.Visible = _weapon.AimAmount < .5f;
	}

	public override void _UnhandledInput(InputEvent e)
	{
		if (e is InputEventKey { Pressed: true, Echo: false, PhysicalKeycode: Key.T } && _terrain != null)
			_terrain.TextureMix = _terrain.TextureMix > .5f ? 0 : 1;
		if (e is InputEventKey { Pressed: true, Echo: false, PhysicalKeycode: Key.Enter or Key.KpEnter } && _walker is { Dead: true })
		{
			_walker.Respawn(_spawn.position, _spawn.yaw);
			_guard.Forget();
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
		if (_guard.State == Guard.Mood.Dead) return "cover: nobody left to hide from";
		var eye = _guard.GlobalPosition + Vector3.Up * 1.6f;
		if (eye.DistanceTo(_walker.GlobalPosition) > 90) return "cover: out of his sight range";
		float through = Math.Max(Foliage.Transmission(eye, _walker.Chest), Foliage.Transmission(eye, _walker.Camera.GlobalPosition) * .7f);
		string sees = _guard.Exposure > 0 ? $"he sees {_guard.Exposure * 100:0}% of you" : "he isn't looking at you";
		return $"cover from plants: {(1 - through) * 100:0}%   {sees}";
	}

	private string GuardStatus()
	{
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
