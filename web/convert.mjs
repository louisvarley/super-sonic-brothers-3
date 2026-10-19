import { pathToFileURL } from 'node:url';
import { resolve } from 'node:path';
import { discoverRoms } from './roms.mjs';
import { decodeLevel, playerSprites, worldMap, RECIPES } from './assets.mjs';
import { convertCatalog } from './catalog.mjs';

export async function convert(directory,output) {
  const { sonic, nes, sources } = await discoverRoms(directory);
  const coverage=await convertCatalog(nes,output);
  return {
    version: 1, sources, coverage, players: { sonic: playerSprites(sonic,'sonic'), tails: playerSprites(sonic,'tails') },
    map: worldMap(nes), levels: RECIPES.map(r => decodeLevel(nes,r)),
    limitations: [
      'Only World 1-1, 1-2 and the main route of 1-3 are native browser stages.',
      'Later worlds and secret/pipe transitions are not implemented in the original engine yet.',
      'The restored browser game uses the original C# engine through WebAssembly, including music and the card minigame.',
      'The stock-ROM converter does not yet produce the full asset archive needed by that engine; the existing local archive is temporarily required.',
    ],
  };
}
if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  try {
    const args=process.argv.slice(2),index=args.indexOf('--output');
    if(index>=0&&(!args[index+1]||args[index+1].startsWith('--')))throw new Error('--output requires a directory.');
    if(args.some((arg,i)=>arg!=='--output'&&arg!=='--strict'&&!(index>=0&&i===index+1)))throw new Error('Usage: npm run convert -- [--output directory] [--strict]');
    const output=index>=0?resolve(args[index+1]):undefined;
    if(output&&(output===resolve(process.env.ROM_DIR??process.cwd())||/\.(nes|md|bin)$/i.test(output)))
      throw new Error('Output must be a dedicated directory, not the ROM root or a ROM file.');
    const result = await convert(process.env.ROM_DIR ?? process.cwd(),output);
    console.log(JSON.stringify({sources:result.sources,levels:result.levels.map(l=>({name:l.name,columns:l.columns,
      instructions:l.instructions,blockers:l.blockers})),coverage:{worlds:result.coverage.worldCount,areas:result.coverage.areas.length,
      failures:result.coverage.areas.filter(a=>a.status==='failed'),complete:result.coverage.complete},output,limitations:result.limitations},null,2));
    if(args.includes('--strict')&&!result.coverage.complete)process.exitCode=2;
  } catch (error) { console.error(error.message); process.exitCode = 1; }
}
