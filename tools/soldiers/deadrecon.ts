/**
 * Dead Recon soldier export: soldier theme, chosen style, real-world heights (so the game never scales them,
 * which keeps ragdoll physics happy).  tsx tools/deadrecon.ts <style> <outDir> <count> [firstSeed]
 */
import { mkdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { generateCharacter } from '../src/core/character';
import { randomRecipe, StyleId } from '../src/core/recipe';
import { exportGlb } from '../src/export/gltf';

const [style = 'blocky', dir = 'out/deadrecon', count = '6', first = '3'] = process.argv.slice(2);
mkdirSync(dir, { recursive: true });
for (let i = 0; i < Number(count); i++) {
  const seed = Number(first) + i * 4;
  const rc = randomRecipe(seed, style as StyleId, 'soldier');
  rc.body.height = 1.66 + ((seed * 37) % 15) / 100; // 1.66 .. 1.80 m
  rc.rig.restPose = 'T';
  const ch = generateCharacter(rc);
  const file = join(dir, `${style}_soldier_${seed}.glb`);
  writeFileSync(file, exportGlb(ch));
  console.log(`${file}  height ${rc.body.height.toFixed(2)} m  ${ch.stats.triangles} tris`);
}
