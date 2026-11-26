# Animals (BadZoo)

`assets/animals/*.glb` come from [BadZoo](https://github.com/badsadcode/BadZoo) (rig/export contract: its
`docs/RIG.md`: metres, +Y up, facing +Z, feet at y = 0, clips Idle/Walk/Trot/Gallop/Eat/Sit/Sleep/Attack/Hit/Death/Roar).

    cd <BadZoo checkout> && npm install
    npx tsx tools/cli.ts export dog 3 out        # <species> <seed> <outDir>

Species ids: dog breeds (labrador, beagle, boxer, doberman, husky...), chicken, duck, pig, goat, cow, boar, deer,
lizard, crocodile, elephant, tiger, leopard, and many more (see BadZoo's src/core/species.ts).

Copy the GLB to `assets/animals/` and add it to the `animals` content section (game/People/Animals.cs has the
built-in list; see docs/MODDING.md): `id`, `model`, `role` (dog, poultry, livestock, prey, predator, basker),
`where` (village, jungle, river), `health`, `walk`, `run`, `flee` (metres), `group_min`/`group_max`, `call`
(bark, cluck, quack, oink, bleat, moo, growl, trumpet), `weight` (how often it turns up in the wild).
