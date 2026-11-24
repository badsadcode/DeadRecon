# Dead Recon

*Working title.* A co-op jungle tactics sandbox from **Bad Ideas Works**: a big persistent world, procedural operations, and plans that go wrong in interesting ways.

> The geography is permanent. The operations are procedural.

## Devlog

The devlog lives in [`site/`](site/) and is published to GitHub Pages by [`.github/workflows/pages.yml`](.github/workflows/pages.yml) on every push to `main`.

To add an entry, copy an `<article class="entry">` block in [`site/index.html`](site/index.html) and put it at the top of the Field Diary section. Plain HTML, no build step.

---

*Where bad ideas receive suspiciously serious engineering.*

## Project

Godot **4.7.2 .NET**, C#, Forward+. Open `project.godot`, press Build, run.

- `src/WorldGen/`: terrain generator (from Thornwake_Rewrite, see its `PROVENANCE.md`). Engine-free C#.
- `content/world/jungle.world.json`: the world recipe (seed, landmass, region, climate).
- `tools/WorldBaker/`: bakes the world **once** to `world/<name>/` (height + water tiles, preview maps).
  The game loads the baked tiles; it never regenerates terrain.

```
dotnet run -c Release --project tools/WorldBaker -- content/world/jungle.world.json
```

## Playing the test mission

Each run is a new operation on the same baked jungle: a camp with a patrolling guard and a sentry, intel on a
radio desk, and a landing zone marked with green smoke. `--seed=N` (after `--`) replays a specific operation.

WASD move · Shift run · C crouch (toggle) / hold Alt · Space jump · LMB fire · RMB aim (scope) · R reload · **E** (hold) take intel ·
N weather · T textured/painted ground · F fly · Esc pause menu (difficulty, mouse, debug) · F3 debug text ·
Enter new operation (after death or success).

Scripted checks (each saves screenshots to the given folder and quits):
`--missiontest=<dir>`, `--squadtest=<dir>`, `--hudtest=<dir>`, `--aitest=<dir>`, `--treetest=<dir>`, `--raintest=<dir>`, `--shots=<dir>`.
