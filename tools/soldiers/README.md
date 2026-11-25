# Guard characters (BadCharacters)

Guards use BadCharacters soldiers from `assets/soldiers/<style>/*.glb` (style picked in the Esc menu: blocky or smooth).
They are exported at real height (1.66-1.80 m) and are never scaled in the game (ragdoll physics needs unscaled bones).

To regenerate: copy `deadrecon.ts` into a BadCharacters checkout's `tools/`, then

    npx tsx tools/deadrecon.ts blocky out/blocky 6 3
    npx tsx tools/deadrecon.ts stylized out/smooth 3 3
    npx tsx tools/deadrecon.ts realistic out/smooth 3 5

and copy the GLBs to `assets/soldiers/blocky` and `assets/soldiers/smooth`.

# Villagers

Village people (`game/People/Villager.cs`) use BadCharacters too, from `assets/villagers/blocky/*.glb`: farmers in
conical straw hats, plain shirts and sandals, plus `blocky_headman.glb` (grey hair and goatee). `villagers.ts` runs
from outside the BadCharacters checkout (it imports it by path, `BADCHARACTERS` env var, default
`E:/Claude/Projects/BadCharacters`):

    cd <BadCharacters checkout>
    npx tsx <DeadRecon>/tools/soldiers/villagers.ts blocky out/villagers 8 5

then copy the GLBs to `assets/villagers/blocky` and let Godot import them (`--headless --import`).
