# The Living Jungle

*Design proposal, Dead Recon. Draft for Tom and Noor to mark up. Nothing here is locked.*

> The geography is permanent. The operations are procedural. **And now the people are, too.**

## The idea in one paragraph

Players don't just visit the jungle for a mission and leave. They **live in it**. Patrols move between camps whether
anyone is watching. Locals walk between villages, tend fields and go to market. There are towns, hamlets, enemy
outposts and a few friendly camps. You stumble into things. You can help people, and people can help you. What you do
stays done for a while, so the region slowly turns. Think Arma's **Antistasi**, kept simpler: no logistics spreadsheet,
just a world that notices you.

## Pillars

1. **The world goes on without you.** Patrols, convoys, villagers and traders move whether or not a player is near.
2. **Locals are the heart.** Every village has a mood toward you. Earn it and the jungle opens up.
3. **Your actions leave marks.** Cleared camps stay cleared for a while, helped villages remember, angered ones too.
4. **Cheap far away, real up close.** Off-screen, everything is a dot on a map. Bodies, AI and animation exist only
   near players. That's how it scales to 64 × 64 km.
5. **Still a mission game.** Operations (what we have now) keep working; the living world gives them context and
   gives players something to do between them.

## Settlements

| Type | Size | What's there | What it's for |
|---|---|---|---|
| Hamlet | 3–6 stilt houses | rice paddy, a few families, chickens | many of them, the texture of the map |
| Village | 10–25 buildings | well, small shop, headman | **local support hub**: talk, trade, jobs, rumours |
| Market town | 60–150 buildings | streets, market square, river dock, temple, small garrison | resupply, big rumours, busy by day, tense by night |
| Provincial town | a few hundred | old colonial buildings, the enemy's regional HQ | late-game target: "this is where the region turns" |

Placement comes from the baked world: flat ground near water and roads; towns on rivers and road junctions; hamlets
along trails. Fixed once baked, like the terrain.

## Military sites

| Site | Who | Notes |
|---|---|---|
| Outpost / checkpoint | enemy | 3–8 guards on a road or bridge; source of patrols |
| Camp | enemy | what our operations use today; radio, supplies |
| Firebase | enemy | the hard one: towers, sandbags, maybe a mortar |
| Friendly camp | allies | resupply, safe rest, sometimes a squad that can help |
| Player base (FOB) | you | home: mission board, armoury, map table |

Sites belong to a side. **Sides can change** (cleared outposts can be retaken later; a friendly camp can appear near
villages that trust you).

## People on the move

- **Patrols**: 2–4 soldiers walking a route between sites along trails and roads.
- **Convoys**: a truck or two on roads (once vehicles exist).
- **Villagers**: walking to fields, to market, to the next village; fishing boats on rivers.
- **Traders**: travel between towns, sometimes escorted.

Off-screen they're entries in a list with a route and a position. Within ~600 m of a player they become real
characters with the AI we already have.

## Locals and support

Each village (and town district) has a **support** value toward the players:

| Support | They will… |
|---|---|
| Hostile | report you to patrols, go silent |
| Wary (start) | trade a little, tell you nothing useful |
| Friendly | share rumours (patrol routes, camp locations), let you rest and resupply |
| Loyal | mark things on your map, guide you, hide you, sometimes join a fight |

**Raises it:** chasing off a patrol that's harassing them, finding a missing villager, bringing supplies, clearing a
nearby outpost, simply not causing trouble.
**Lowers it:** firefights in the village, stray bullets, civilians hurt, leading the enemy to them.

Jobs from locals are small, personal operations: *"My brother went to the river and didn't come back."* They reuse
the mission system: same director, smaller scope, a person instead of HQ giving them.

## Persistence

| Always | Usually | For a while | Not at all |
|---|---|---|---|
| terrain, rivers, roads, settlements, site locations | village support, discovered map, which side holds a site | cleared camps, felled trees, bodies, damaged buildings | individual patrol members, exact AI positions |

"For a while" means it slowly returns: a cleared outpost is re-garrisoned after some in-game days unless the region
has turned.

## Day and night

The world has a clock. Villagers sleep at night, markets open in the morning, patrols are fewer but twitchier at
night. Darkness is a stealth tool like rain.

## Build order (proposal)

1. **Atmosphere: day/night, light and mist.** Quick, transforms the mood. *(done; clouds still to come)*
2. **Roads and settlement sites in the world bake.** Fills the empty `Sites`/`Roads` slots in the generator: trails,
   dirt roads, bridges and fords, hamlet/village/town sites, outpost sites. *(done: `tools/WorldBaker/Settler.cs`;
   also rice paddies and wheat fields round every settlement. Bridges are marked but not built yet.)*
3. **Buildings.** Stilt houses, shophouses, temple, concrete outpost. Via the studio's building generator
   (BadBuilding → NoorArchitect) where possible, exported to Noor Interchange. *(placeholder procedural stilt houses,
   huts, wells, stalls and checkpoints in `game/World/Villages.cs` until NoorArchitect exports exist)*
4. **One village, alive.** Villagers with daily routines, a headman, support value, one or two local jobs.
5. **Roaming patrols** between sites, simulated cheaply off-screen, real near you.
6. **The map and its memory.** A map screen with what you've discovered, site ownership, village support; persistence
   between sessions.
7. **Towns.** Market town first, then the provincial town.
8. **Friendly camps and the region turning.**

Each step is playable on its own. Co-op stays planned for later; this design assumes several players in the same world
and nothing here should block it.

## Open questions for Tom and Noor

- Era and sides: still open (GDD). Locals need a look and a language: gibberish like the guards?
- How punishing should village anger be? Can you permanently lose a village?
- Should the player have a home base from day one, or earn it?
- Day length: we're starting at 24 real minutes per day. Too short? Too long?
- Civilians in firefights: how careful does the player have to be?
