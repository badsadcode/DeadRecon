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
- **Helping yourself:** only when lightly wounded (a slow, uncertain field dressing). See the wound levels in section 6.

### Failing

- **Single player:** the mission fails when you bleed out (your squad can save you first). You wake up at home; your
  pack stays where you fell (already in).
- **Co-op:** the mission only fails when **every player** is down or dead at the same time. A dead player waits
  (watching a teammate) until revived, extracted, or the mission ends, then wakes up at home.

---

## 6. Decisions (Tom, 26 Nov)

### Capturing people: simple to do, hard to pull off

No interrogation minigame. Capture is physical, in the world:

- **Surrender:** enemies can surrender on their own (low morale, outnumbered, leader down), or you surprise an
  isolated one and order him to give up (sneak close, F).
- **Prisoners:** restrain (F), search (his pockets: keys, documents, a magazine), escort (he walks ahead of you; you
  tell him to stop, go, get down).
- **What officers know:** a captured officer reveals things on your map (a cache, a camp, a patrol route).
- **Prisoners are people:** they can panic in a firefight, refuse to move, or bolt when you look away or get busy. A
  shot fired nearby makes them drop flat.
- **Getting him out is the mission:** the long walk home with someone to look after is the point. A "capture"
  operation is mostly an escort operation.

### Hold the village: a fight, not a wave counter

No "WAVE 3/5", no countdown on screen.

- **The attackers:** a real force with limited men, morale and ammunition. They probe, retreat, regroup, try another
  side, or give up when it costs too much.
- **Your help is coming, for real:** friendly reinforcements actually travel through the world toward the village.
  You get occasional radio updates ("Bravo's at the river... taking fire... moving again"). They might be late,
  might get stuck in a fight of their own, might turn up early.
- **The tension:** distant gunfire, and you don't know whose.

### Dogs: yes, all kinds

| Dog | What it does |
|---|---|
| **Guard dog** | Tied up or roaming a camp: hears and smells you before the guards see you. Barks. |
| **Tracker** | Follows your scent from where you were last seen. Rain washes scent out, wind carries it, crossing a river breaks the trail. |
| **Patrol dog** | Walks with a patrol, ranging ahead. |
| **Your dog** | A friendly military dog: growls at danger you haven't seen yet. |
| **Village dogs** | Bark at strangers, which tells the village (and anyone listening) someone's coming. |
| **Strays** | Wander, scavenge, sleep in the sun. Life. |

Noor predicts adoption, feeding, personalities and a dog called **Private Sausage** within three days. Noted. Not
promised. (Probably promised.)

### Mission length: it varies, and that's fine

| Kind | About |
|---|---|
| Short recon | 10–20 min |
| Patrol, ambush | 20–35 min |
| Search and rescue | 30–50 min |
| Capture, sabotage | 30–60 min |
| Major operation | 60–90 min |
| Chains | several evenings |

The sweet spot is 35–45 minutes. Travel, run-ins, getting lost and things going wrong stretch it naturally.

**Save anywhere reasonable:** you can stop mid-operation somewhere relatively safe (not in a firefight, not seen) and
carry on tomorrow. The operation, its progress, the camp's state, your squad and where everyone was are all saved.

### Going down: it depends on the wound

No automatic pistol every time. How bad it is decides what you can do:

| Wound | What you can do |
|---|---|
| **Light (down but awake)** | Crawl, use your sidearm, try a field dressing on yourself (slow, might not work), call for help on the radio. |
| **Bad** | Crawl slowly, call for help. No shooting. |
| **Critical** | Nothing: you're out, bleeding, and only someone else can get you up. |

Squadmates and friendly NPCs (villagers who like you, the camp medic) can rescue you, just as you rescue them.
The moment we want: lying in the ferns, hearing someone come through the vegetation, not knowing whose side they're on.

(Co-op: the mission fails only when every player is out at the same time.)

---

## 7. Build order

Each step is playable on its own.

1. **The step system.** Today's five operations rebuilt on reusable steps. **Done (2026-11-26):**
   `game/Mission/Steps.cs` has the steps (hold F, kill, search a body, fuse, talk into following, go to, get out,
   set up the radio); each operation is a list of them in `Mission.Recipe()`. People you bring out are escorts: if
   one dies the operation fails. A new mission = its props + a new list.
2. **Save anywhere (safe):** save and continue an operation, with world state, squad and progress.
3. **Going down and being rescued** (wound levels, crawling, calling for help; squad, villagers and medic come for
   you). Lays the co-op rule's foundation.
4. **Ten new missions:**
   - photo recon
   - eavesdrop
   - radio mast
   - fuel dump
   - **capture alive** (surrender, restrain, search, escort, escape attempts)
   - ambush the patrol
   - downed pilot
   - supply drop
   - escort the trader
   - **hold the village** (living attack, travelling reinforcements, radio updates)
5. **Morale and surrender** (needed by capture and the village defence), radio checks and missing patrols, radio
   operators who call for help, searching in a line.
6. **Dogs:** guard and patrol dogs, scent tracking (rain, wind, rivers), village dogs and strays. Then a dog of your
   own. Then, inevitably, Private Sausage.
7. **Modifiers and bonus objectives** in the briefing.
8. **Enemy upgrade 2:** pairs that cover and move, suppression, torches and flares at night.
9. **Chains:** The Colonel, The Rice Tax, The Pilot.
10. Later: vehicles, bridges, grenades, the market town, co-op.
