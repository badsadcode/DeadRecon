# Modding Dead Recon

Simple, on purpose: JSON files, no code. Change names, lines, items, weapons.

## Where the data lives

`content/game/*.json` is the game's own data:

| File | Sections |
|---|---|
| `items.json` | `items`: magazines, dressings, attachments, cargo |
| `weapons.json` | `weapons`: every gun (model, damage, rate of fire, magazine, spread, recoil, noise, sights, how often guards carry it) |
| `names.json` | `operation_adjectives`, `operation_nouns`, `intel`, `soldier_names`, `lost_villager_names` |
| `lines.json` | `guard_lines`, `guard_chats`, `villager_lines`, `radio_banter` |
| (any file) | `animals`: BadZoo animals in villages and the wild (see `tools/animals/README.md`) |
| (any file) | `buildings`: what you can build at home (`id`, `name`, `what`, `kind`: tent, wall, tower, crate, shelter, fire, lamp; `cost` in supplies, `hours` of work, `radius`) |

You can edit these directly. To get fresh copies of everything the game knows:

    Godot --headless --path . -- --dumpcontent=some/folder

## Making a mod

Make a folder in `mods/` (next to the game, or in the project while developing), and put JSON files in it with any
of the sections above. **Mods add to the game**:

- **lists** get longer (more operation names, more lines),
- **lines by kind** (`guard_lines`, `villager_lines`) get more lines for that kind, or new kinds,
- **records with an `id`** (items, weapons) **replace the fields you give** for that id, or are new if the id is new.

File names inside a mod don't matter, only the section names. Mods load in alphabetical order. Errors in a file show
up as a message in the game and in the log; the rest still loads.

### Example: a tougher black rifle, a new gun and some new grumbling

`mods/my_first_mod/stuff.json`:

```json
{
  "weapons": [
    { "id": "rifle_4121", "damage": 38, "name": "Black rifle (rebarrelled)" },
    {
      "id": "smg_9001", "name": "Grease gun", "description": "Ugly. Reliable. Loud.",
      "model": "res://assets/weapons/guns/BadGun_smg_vietnam_9001.glb",
      "rpm": 450, "damage": 24, "capacity": 30, "sight": "irons", "aim_spread": 0.002, "guard_weight": 1
    }
  ],
  "guard_lines": {
    "mutter": ["My boots are wet. My socks are wet. My SOUL is wet."]
  },
  "operation_nouns": ["Pineapple", "Hammer"]
}
```

## Weapons from BadGun

Make a gun with BadGun (see `tools/weapons/README.md`), e.g.

    npx tsx src/cli.ts --archetype smg --style vietnam --seed 9001 --out out

copy the `.glb` to `assets/weapons/guns/`, and add a `weapons` entry pointing at it. Fields:

| Field | Meaning |
|---|---|
| `rpm` | rounds per minute; under 120 it's a bolt or pump (one shot per click) |
| `damage` | per bullet (per pellet for shotguns). The rifle does 34: three to the body; headshots do triple |
| `capacity` | rounds in the gun. Reloads take rounds from any magazines you carry |
| `auto` | can it fire full auto (V switches) |
| `hip_spread`, `aim_spread` | inaccuracy (radians-ish) from the hip and aimed |
| `recoil` | 1 = the battle rifle |
| `noise` | 1 = guards hear it 260 m away |
| `sight` | `scope` (zoom to `scope_fov` degrees) or `irons` |
| `pellets`, `pellet_spread` | more than 1 pellet: a shotgun |
| `kg` | weight |
| `guard_weight`, `guard_damage` | how often guards carry it (0 = never), and how hard they hit you with it |
