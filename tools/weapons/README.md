# Rifle and attachments (BadGun)

The rifle is Tom's `assets/weapons/BadGun_rifle_vietnam_97232.glb`. Attachments in `assets/weapons/attachments/` come
from BadGun's standalone attachment export (BadGun devlog #010, "Snap It On"), made for the same style and seed so
they are the right size for this rifle:

    cd <BadGun checkout>
    for a in "optic reddot" "optic holo" "optic scope" "muzzle suppressor" "muzzle compensator" "muzzle flash" \
             "side flashlight" "side laser" "underbarrel vgrip"; do
      set -- $a
      npx tsx src/cli.ts --attachment $1 --variant $2 --style vietnam --seed 97232 --out out/$1_$2
    done

(BadGun has no runtime dependencies; any `tsx` works, e.g. the one in a BadCharacters checkout.) Copy the GLBs into
`assets/weapons/attachments/` and let Godot import them (`--headless --import`).

The rifle predates BadGun's attachment sockets, so `game/Gear/RifleBuilder.cs` mounts side-rail and underbarrel
parts on the handguard's measured skin. Regenerating the rifle with today's BadGun gives a different gun (longer
barrel), so we keep this one.
