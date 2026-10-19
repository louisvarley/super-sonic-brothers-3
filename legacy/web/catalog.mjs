import { mkdir, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { decodeArea, readObjects, RECIPES, MAPPED_OBJECTS } from './assets.mjs';

export function catalog(rom) {
  const areas=new Map(),pending=[],worlds=[];
  function add(tileset,layout,objects) {
    if(tileset<1||tileset>14||layout<0xa000||layout>=0xc000||objects<0xc000||objects>=0xe000)return null;
    const bank=rom.prg(30,0x83e9+tileset);
    const id=`ts${String(tileset).padStart(2,'0')}-${String(bank).padStart(2,'0')}-${layout.toString(16).toUpperCase()}-${objects.toString(16).toUpperCase()}`;
    if(!areas.has(id)){const area={id,tileset,bank,layout,objects};areas.set(id,area);pending.push(area);}
    return id;
  }
  for(let world=0;world<8;world++){
    const map=rom.word(12,0xa598+world*2),end=rom.word(12,0xa59a+world*2);
    const cells=rom.bytes(12,map,end-map-1);
    if(!cells.length||cells.length%144||rom.prg(12,end-1)!==255)throw new Error(`World ${world+1}: invalid map extent.`);
    const init=rom.word(12,0xb3ca+world*2),rows=rom.word(12,0xb3dc+world*2),cols=rom.word(12,0xb3ee+world*2);
    const objects=rom.word(12,0xb400+world*2),layouts=rom.word(12,0xb412+world*2),count=cols-rows,entries=[];
    if(count<=0||objects-cols!==count||layouts-objects!==count*2)throw new Error(`World ${world+1}: invalid entry tables.`);
    for(let i=0;i<count;i++){
      const rowType=rom.prg(12,rows+i),col=rom.prg(12,cols+i),screen=col>>4;
      if(screen>=cells.length/144||rom.prg(12,init+screen)>i)throw new Error(`World ${world+1}: invalid screen.`);
      const layout=rom.word(12,layouts+i*2),obj=rom.word(12,objects+i*2),tileset=rowType&15;
      entries.push({index:i,screen,row:(rowType>>4)-2,column:col&15,tileset,layout,objects:obj,areaId:add(tileset,layout,obj)});
    }
    worlds.push({number:world+1,cells,entries});
  }
  for(let i=0;i<pending.length;i++){
    const a=pending[i];add(rom.prg(a.bank,a.layout+6)&15,rom.word(a.bank,a.layout),rom.word(a.bank,a.layout+2));
  }
  return {worlds,areas:[...areas.values()].sort((a,b)=>a.id.localeCompare(b.id))};
}

export async function convertCatalog(rom,output) {
  const source=catalog(rom),areas=[],backlog=new Map();
  async function json(path,data){
    if(!output)return;
    const dir=join(output,...path.slice(0,-1));await mkdir(dir,{recursive:true});
    await writeFile(join(dir,path.at(-1)),JSON.stringify(data,null,2));
  }
  await json(['catalog.json'],source);
  for(const area of source.areas){
    const blockers=[];let objects=[],unknown=[];
    try{
      objects=readObjects(rom,area.objects);
      unknown=[...new Set(objects.filter(o=>!MAPPED_OBJECTS.has(o.id)).map(o=>o.id))].sort((a,b)=>a-b);
      for(const id of unknown){if(!backlog.has(id))backlog.set(id,[]);backlog.get(id).push(area.id);}
      const decoded=decodeArea(rom,area);
      const recipe=RECIPES.find(r=>r.tileset===area.tileset&&r.layout===area.layout&&r.objects===area.objects);
      if(decoded.header[6]&16)blockers.push('Vertical layout/camera adapter is missing.');
      if(!recipe)blockers.push('Native browser terrain/event/entrance adapters are not implemented.');
      if(unknown.length)blockers.push('Missing native object behaviors.');
      if(recipe)blockers.push('Alternate pipe routes are not implemented.');
      if(recipe?.name==='1-3')blockers.push('White-block/warp-whistle secret is not implemented.');
      if(output){
        await json(['areas',area.id,'area.json'],{...area,header:decoded.header,instructions:decoded.instructions,objects});
        await writeFile(join(output,'areas',area.id,'tiles.bin'),decoded.tiles);
      }
      areas.push({id:area.id,status:recipe?'native-partial':'decoded',unmappedObjects:unknown,blockers});
    }catch(error){
      // A single original-loader failure is a reported conversion result, never a successful level.
      if(!/Unsupported 6502 opcode|6502 instruction limit|Unterminated object/.test(error.message))throw error;
      areas.push({id:area.id,status:'failed',unmappedObjects:unknown,blockers:[error.message]});
    }
  }
  const report={version:1,complete:false,worldCount:source.worlds.length,areas,
    campaignBlockers:['Later-world gameplay and progression are not implemented.','The complete stock-ROM asset importer for the original engine is unfinished.']};
  await json(['report.json'],report);
  await json(['backlog.json'],[...backlog.entries()].map(([sourceId,affectedAreas])=>({sourceId,affectedAreas})).sort((a,b)=>b.affectedAreas.length-a.affectedAreas.length||a.sourceId-b.sourceId));
  return report;
}
