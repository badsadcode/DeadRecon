# Guard characters (BadCharacters)

Guards use BadCharacters soldiers from `assets/soldiers/<style>/*.glb` (style picked in the Esc menu: blocky or smooth).
They are exported at real height (1.66-1.80 m) and are never scaled in the game (ragdoll physics needs unscaled bones).

To regenerate: copy `deadrecon.ts` into a BadCharacters checkout's `tools/`, then

    npx tsx tools/deadrecon.ts blocky out/blocky 6 3
    npx tsx tools/deadrecon.ts stylized out/smooth 3 3
    npx tsx tools/deadrecon.ts realistic out/smooth 3 5

and copy the GLBs to `assets/soldiers/blocky` and `assets/soldiers/smooth`.
