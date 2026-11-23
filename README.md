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
