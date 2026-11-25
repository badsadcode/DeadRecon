/**
 * Dead Recon villager export: farmers in conical straw hats (nón lá), plain shirts and sandals, and a grey-haired
 * headman. Real heights (the game never scales characters).
 *
 *   cd <BadCharacters checkout>
 *   npx tsx <DeadRecon>/tools/soldiers/villagers.ts <style> <outDir> <count> [firstSeed]
 *
 * Runs from outside the BadCharacters repo: BADCHARACTERS points at the checkout (default E:/Claude/Projects/BadCharacters).
 */
import { mkdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';

const root = process.env.BADCHARACTERS ?? 'E:/Claude/Projects/BadCharacters';
const load = (p: string) => import(pathToFileURL(join(root, p)).href);
async function main() {
const { generateCharacter } = await load('src/core/character.ts');
const { randomRecipe, randomOutfit } = await load('src/core/recipe.ts');
const { exportGlb } = await load('src/export/gltf.ts');

const villager = {
  label: 'Jungle villager',
  genre: 'South-east Asian rural (fictional)',
  slots: {
    head: { conical: 5, none: 3, bandana: 1, headband: 1 },
    top: { longsleeve: 3, tee: 2, tunic: 2, tank: 1 },
    bottom: { pants: 4, shorts: 2, skirt: 1 },
    feet: { sandals: 3, none: 2 },
    belt: { none: 3, sash: 1 },
    neck: { none: 5, scarf: 1 },
  },
  palette: ['#2b3550', '#1f1f24', '#4a5a78', '#b9a77f', '#6b4f36', '#d8d0bc', '#55603a', '#7a3b2e'],
  accents: ['#c8b48a', '#2f2a24', '#e8e0d0'],
  slotPalette: { head: ['#d9c48a', '#cdb67a', '#e0cd96'] },
};
const skins = ['#e8b896', '#d29b74', '#b57b55', '#8d5a3b'];

const [style = 'blocky', dir = 'out/villagers', count = '8', first = '5'] = process.argv.slice(2);
mkdirSync(dir, { recursive: true });
for (let i = 0; i <= Number(count); i++) {
  const headman = i === Number(count);
  const seed = Number(first) + i * 7;
  const rc = randomRecipe(seed, style, 'casual');
  rc.outfit = randomOutfit(seed, villager);
  rc.theme = 'villager';
  rc.body.skin = skins[seed % skins.length];
  rc.body.height = 1.55 + ((seed * 29) % 18) / 100; // 1.55 .. 1.72 m
  if (headman) {
    // The headman: no hat, grey hair and beard, a pale long shirt.
    rc.outfit.head = { ...rc.outfit.head, id: 'none' };
    rc.outfit.hair = { ...rc.outfit.hair, id: 'short', color: '#b8b4ac', color2: '#b8b4ac' };
    rc.outfit.beard = { ...rc.outfit.beard, id: 'goatee', color: '#c4c0b8', color2: '#c4c0b8' };
    rc.outfit.top = { ...rc.outfit.top, id: 'longsleeve', color: '#e4dccb', color2: '#e4dccb' };
    rc.outfit.bottom = { ...rc.outfit.bottom, id: 'pants', color: '#2b3550', color2: '#2b3550' };
    rc.face.browColor = '#c4c0b8';
    rc.body.height = 1.62;
  }
  rc.rig.restPose = 'T';
  const ch = generateCharacter(rc);
  const file = join(dir, headman ? `${style}_headman.glb` : `${style}_villager_${seed}.glb`);
  writeFileSync(file, exportGlb(ch));
  console.log(`${file}  height ${rc.body.height.toFixed(2)} m  ${ch.stats.triangles} tris  hat ${rc.outfit.head.id}`);
}
}
main();
