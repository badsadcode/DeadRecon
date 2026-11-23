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
			if (_guard.Suspicion < .3f && now - lastMove > 1.5)
			{
				lastMove = now;
				var f2 = -_guard.GlobalTransform.Basis.Z; f2.Y = 0; f2 = f2.Normalized();
				var p2 = _guard.GlobalPosition + f2 * 28;
				p2.Y = _world.HeightAt(p2.X, p2.Z) + .2f;
				_walker.GlobalPosition = p2;
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
