# Missions, smarter enemies and reviving: the plan

Tom's brief (26 Nov 2026): lots of mission types ("the more the better"), multi-step missions, enemies that are
smarter but **fair** ("smarter doesn't mean laser aim or X-ray vision"), reviving, and for co-op: the mission only
fails when every player is dead.

What we have today: five operation kinds (steal a radio, steal intel, blow up a cache, take out an officer, rescue a
prisoner), a few with steps, reinforcements after an alarm, and a squad of up to three.

---

## 1. Mission types

Grouped by what you do. ✅ = exists, the rest are new. Each is a template; the generator picks the place, the
target, the time of day, the garrison and the modifiers (section 3).

### Quiet work (stealth, recon)

| Mission | What you do | Why it's fun |
|---|---|---|
| **Photo recon** | Get close enough to photograph 3–5 things in a camp (binoculars, hold the shot) and get out unseen. | Pure sneaking, nothing to shoot. A ghost run feels great. |
| **Eavesdrop** | Sit within 25 m of their radio tent for 60 s while they talk. | Holding still while patrols walk past you. |
| **Tag the courier** | A runner walks between two camps. Follow him without being seen and learn his route, or plant a tracker on him. | Tailing. A moving target. |
| **Map the trail** | Walk an enemy supply trail end to end and mark its waypoints. | Exploration with risk. |
| **Count them** | How many soldiers at this checkpoint? Watch it, then report on the radio. | Patience, observation. HQ trusts your count. |

### Breaking things (sabotage)

| Mission | What you do |
|---|---|
| ✅ **Supply cache** | Plant a charge, get clear. |
| **Radio mast** | Cut their comms: no reinforcements for that camp afterwards, and possibly a whole area. |
| **Fuel dump / ammo store** | A bigger bang that wakes the whole valley: you need a way out. |
| **Mortar pit** | Disable it before it shells the village. Timed. |
| **Bridge** | Blow it to cut a road (once bridges exist): patrols reroute for good. |
| **Spoil the rice tax** | Quietly swap their stolen rice back to the village carts. No shots fired = bonus. |

### Direct action

| Mission | What you do |
|---|---|
| ✅ **Take out the officer** | Then search him. |
| **Capture the officer alive** | Surprise him (sneak up, F: "hands up") and walk him home. He tries to run if you look away. |
| **Ambush the patrol** | A patrol will use this road at about 14:00. Set up, wait, spring it. Your squad shines here. |
| **Clear the checkpoint** | Partly in the village jobs already. With the squad, a proper assault. |
| **Hold the village** | The enemy comes to punish a village that helped you. Defend it in waves until they break. |

### People

| Mission | What you do |
|---|---|
| ✅ **Rescue a prisoner** | Keys, cage, home. |
| **Downed pilot** | A plane went down. Find the crash, find the pilot (hiding, injured), carry or escort him to a clearing, pop smoke, hold until the helicopter. |
| **Escort the trader** | Walk a villager and his cart to the market town. Patrols on the road. |
| **The doctor** | A village doctor is wanted by the enemy. Get him to your camp (and he stays: medic at home, heals your squad). |
| **Defector** | An enemy soldier wants out. Meet him at night, verify it's not a trap (sometimes it is), bring him in: intel about other camps. |

### Logistics and loot

| Mission | What you do |
|---|---|
| **Supply drop** | HQ dropped a crate; it landed in a tree in enemy country. Find it before they do. |
| **Steal the truck** | (When vehicles exist.) Drive it home. Loud. Glorious. |
| **Medicine run** | A village is sick; carry medicine from the market town. Time limit. |

That's about 25 kinds, plus the village jobs. Adding a template later is cheap: a mission is a sequence of steps
built from the parts below.

---

## 2. Multi-step missions

### Steps are building blocks

Every mission is a short list of **steps**. The steps we need (most exist in some form already):

`go to` · `find` (search an area) · `take` (hold F) · `photograph` · `listen` (stay near, unseen) · `plant` (charge,
tracker) · `kill` · `capture` · `search` (a body or a crate) · `free` · `escort` (someone follows) · `defend` (survive
waves) · `wait until` (a time, an event) · `signal` (flare, smoke, radio) · `get home`.

A mission is something like `find the crash → free the pilot → escort → signal → defend until pickup`. The
generator fills in the places and the people.

### Chains: missions that lead to missions

Some missions unlock the next one, and what you did matters:

- **The Colonel**
  1. Steal the intel: it names a colonel visiting the area.
  2. Tag his courier: learn which road he uses.
  3. Ambush his car on that road.
  - If you were spotted in step 1, the colonel has twice the escort.
- **The Rice Tax**
  1. Count the checkpoint's soldiers.
  2. Cut its radio.
  3. Clear it.
  4. Hold the village when the punishment squad arrives.
- **The Pilot**
  1. Find the crash.
  2. Follow the drag marks to where they took him.
  3. Rescue him from a camp.

Chains are offered over the radio with a little story ("Part 2 of 3"). Between steps you go home, rest, re-arm,
recruit. That's the "live in the world" extraction loop.

### Optional objectives

Every mission can carry one or two extras for bonus pay, usually building supplies, guns or village support:
no alarm, no kills, bring back extra documents from the officer's tent, find the hidden stash, finish before nightfall.

---

## 3. Modifiers: same mission, different evening

The generator rolls a few per mission, shown in the briefing:

- **Time and weather:** night (torches and flares), storm (sound masked, sight short), dawn mist.
- **Garrison:** light, normal, heavy. Plus a dog (hears and smells you, barks), a sniper in a tower, a machine-gun
  nest.
- **Alert level:** the area was hit recently, so patrols are doubled and guards are jumpy.
- **Time limit:** "before the convoy leaves at 16:00".
- **Gear:** "go light" (no backpack: carry less, move quieter).
- **Rewards:** what the job pays: supplies, a specific gun, an attachment, a recruit.

---

## 4. Smarter enemies, still fair

The rule: **they only know what they actually saw or heard, and they shoot like people.** No aimbot, no wallhacks.

### What they know

- **Memory, not radar.** They hunt your *last known position*, then guess where you went. That's how it works
  today; we keep it strict.
- **Clues:** an unanswered radio check ("Patrol 3, report... Patrol 3?"), a missing sentry, blood, footprints in mud
  after rain, a door left open, a body (already in).
- **Talking to each other:** a guard who sees you shouts, and only those within earshot hear. A radio operator calls
  other camps, but only if he's alive and at his radio. Cutting the radio first becomes a real tactic.

### How they fight

- **In pairs:** one covers, one moves (bounding). Today they mostly fight alone.
- **Use the ground:** cover, rocks, flanking along the ditches. Retreat to a better spot when losing.
- **Flush you out:** fire at your last cover to pin you, and send one man round the side (half exists). Use grenades
  when we have grenades, with a shouted warning you can hear.
- **Search properly:** fan out in a line through the ferns, check the obvious hiding places, give up in stages.
- **Night:** torches (you can see them coming, they light you up), flares over a contact.

### How they aim

- **Accuracy depends on the situation:** distance, your movement, their movement, light, rain, suppression.
- **The first shots after spotting you are warnings:** wide, so you have a beat to react (already partly in).
- **Being shot at ruins their aim** (suppression). Your squad's covering fire really helps.
- **Reaction time:** a moment between seeing and shooting, longer at range and in the dark.
- **Leaves are not walls, but they aren't glass either:** bullets go through leaves, but a guard can't aim at what he
  can't see; he fires at where he *thinks* you are.

### Morale

Every enemy group has a morale value. Losing their leader, getting outnumbered, or being flanked can make them
**fall back**, call for help, or even **surrender**. Prisoners can be interrogated (they point out a cache) or let go
(the village likes that).

---

## 5. Reviving

- **Down, not dead.** At 0 health you go *down*: you crawl slowly, can't shoot, and bleed out over about 60 s. A
  dressing used by someone else gets you back up with little health.
- **Who can revive you:** squad soldiers (they come to you if they're up and it's not suicide: they'll smoke or
  suppress first), co-op teammates, and later the camp medic (the rescued doctor).
- **Your squad already works this way.** It becomes two-way: they revive you too.
- **Self-revive:** no, or once per mission with a rare item, so being alone stays dangerous.

### Failing

- **Single player:** the mission fails when you bleed out (your squad can save you first). You wake up at home; your
  pack stays where you fell (already in).
- **Co-op:** the mission only fails when **every player** is down or dead at the same time. A dead player waits
  (watching a teammate) until revived, extracted, or the mission ends, then wakes up at home.

---

## 6. Build order

Each step is playable on its own.

1. **The step system.** Rebuild today's five operations on reusable steps. No visible change, but everything after
   gets cheap.
2. **Ten new missions from those steps:**
   - photo recon
   - eavesdrop
   - radio mast
   - fuel dump
   - capture the officer
   - ambush the patrol
   - downed pilot
   - supply drop
   - escort the trader
   - hold the village
3. **Modifiers and optional objectives** in the briefing.
4. **Reviving:** you go down too, and the squad revives you. This also covers the co-op rule's foundation.
5. **Enemy upgrade 1:** radio checks and missing patrols, radio operators who call for help (cut the radio first),
   searching in a line, morale and falling back.
6. **Enemy upgrade 2:** pairs that cover and move, suppression that ruins their aim, torches and flares at night,
   dogs.
7. **Chains:** The Colonel, The Rice Tax, The Pilot.
8. Later, when the pieces exist: vehicles (steal the truck, convoys), bridges, grenades, the market town, co-op.

## Open questions for Tom (and Noor)

- **Capture alive:** how hard? Simple ("hands up" from behind) or a whole interrogation scene?
- **Hold the village:** waves until they break, or a timer until help arrives?
- **Dogs:** yes or no? (They change stealth a lot.)
- **Mission length:** chains can stretch over several evenings. How long should a typical one-sitting mission take?
  20 minutes? 45?
- **When you go down in single player and nobody's around:** a short "last stand" with the pistol, or straight to
  bleeding out?
