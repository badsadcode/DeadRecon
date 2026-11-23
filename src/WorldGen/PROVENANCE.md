# WorldGen provenance

Copied from **badsadcode/Thornwake_Rewrite** `Game/WorldGen/` at commit
`8aec21859251423ef79860c0df966beec7c42614` (terrain revision 44), on 2026-11-23.

Changes since the copy:
- Namespace `Thornwake.WorldGen` → `DeadRecon.WorldGen`. Nothing else yet.

Known issue inherited from Thornwake:
- `TerrainSampler.Sample(x, z, double.PositiveInfinity)` (and so `MacroHeight`) returns non-finite heights in some
  mountain areas. The baker never asks for that LOD; `tools/WorldBaker` refuses to write a non-finite height.

This is now DeadRecon's copy. It will diverge (finer jungle hydrology, deposition, roads/sites, monsoon climate).
If a fix is useful to Thornwake too, port it back by hand.

Thornwake's design history for this code lives in that repo's `docs/DECISIONS.md` (D-numbers in comments refer to it).
