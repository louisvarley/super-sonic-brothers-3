import { Cpu6502 } from './cpu6502.mjs';
import { imageData } from './png.mjs';

const NES = [
  0x545454,0x001e74,0x081090,0x300088,0x440064,0x5c0030,0x540400,0x3c1800,
  0x202a00,0x083a00,0x004000,0x003c00,0x00323c,0,0,0,
  0x989698,0x084cc4,0x3032ec,0x5c1ee4,0x8814b0,0xa01464,0x982220,0x783c00,
  0x545a00,0x287200,0x087c00,0x007628,0x006678,0,0,0,
  0xeceeec,0x4c9aec,0x787cec,0xb062ec,0xe454ec,0xec58b4,0xec6a64,0xd48820,
  0xa0aa00,0x74c400,0x4cd020,0x38cc6c,0x38b4cc,0x3c3c3c,0,0,
  0xeceeec,0xa8ccec,0xbcbcec,0xd4b2ec,0xecaeec,0xecaed4,0xecb4b0,0xe4c490,
  0xccd278,0xb4de78,0xa8e290,0x98e2b4,0xa0d6e4,0xa0a2a0,0,0,
];
function pixel(buffer, width, x, y, color, alpha = 255) {
  if (x < 0 || x >= width || y < 0 || (y * width + x) * 4 >= buffer.length) return;
  const i = (y * width + x) * 4;
  buffer[i] = color >> 16; buffer[i + 1] = color >> 8; buffer[i + 2] = color; buffer[i + 3] = alpha;
}
function nesTile(rom, bank, id, palette, target, width, x, y, flipX = false, flipY = false) {
  const tile = rom.chr(bank, id);
  for (let py = 0; py < 8; py++) for (let px = 0; px < 8; px++) {
    const v = (tile[py] >> (7 - px) & 1) | (tile[py + 8] >> (7 - px) & 1) << 1;
    if (v) pixel(target, width, x + (flipX ? 7 - px : px), y + (flipY ? 7 - py : py), NES[palette[v] & 63]);
  }
}
function metatileAtlas(rom, bank, banks, palette) {
  const width = 256, height = 256, pixels = new Uint8Array(width * height * 4);
  for (let t = 0; t < 256; t++) for (let q = 0; q < 4; q++) {
    const chr = rom.prg(bank, 0xa000 + q * 256 + t);
    nesTile(rom, banks[chr < 128 ? 0 : 1] + ((chr & 127) >> 6), chr & 63,
      palette.slice((t >> 6) * 4, (t >> 6) * 4 + 4), pixels, width,
      (t % 16) * 16 + (q >> 1) * 8, (t >> 4) * 16 + (q & 1) * 8);
  }
  return imageData(width, height, pixels);
}

export const RECIPES = [
  { name: '1-1', tileset: 1, bank: 15, layout: 0xbb82, objects: 0xc527, sky: 0x80 },
  { name: '1-2', tileset: 3, bank: 16, layout: 0xaf2a, objects: 0xc6aa, sky: 2 },
  { name: '1-3', tileset: 1, bank: 15, layout: 0xae09, objects: 0xc2ee, sky: 0x80 },
];
export const MAPPED_OBJECTS = new Set([0x72,0x73,0x74,0x6c,0x6d,0x6e,0xa0,0xa1,0xa2,0xa3,0xa4,0xa5,0xa6,0xa7,0x82,0xc1,0x41]);

export function decodeArea(rom, recipe) {
  const header = rom.bytes(recipe.bank, recipe.layout, 9), cpu = new Cpu6502(rom);
  cpu.banks[1] = recipe.bank; cpu.banks[2] = rom.prg(30, 0x83d6 + recipe.tileset);
  cpu.ram[0x61] = recipe.layout & 255; cpu.ram[0x62] = recipe.layout >> 8; cpu.ram[0x70a] = recipe.tileset;
  cpu.ram.set(Buffer.from('F9E211D5F65DB10A6800', 'hex'), 0x781);
  cpu.call(rom.word(30, 0x9a23 + recipe.tileset * 2));
  return {header,tiles:cpu.prgRam.slice(0,6480),instructions:cpu.steps};
}

export function readObjects(rom,address) {
  const objects=[];
  for(let p=address+1;p+2<0xe000;p+=3){
    const id=rom.prg(6,p);
    if(id===255)return objects;
    objects.push({id,x:rom.prg(6,p+1)*16+8,y:rom.prg(6,p+2)*16+8});
  }
  throw new Error(`Unterminated object list at $${address.toString(16)}.`);
}

export function decodeLevel(rom, recipe) {
  const {header,tiles,instructions}=decodeArea(rom,recipe);
  if (header[6] & 16) throw new Error(`${recipe.name}: vertical native layout is unsupported.`);
  const columns = ((header[4] & 15) + 1) * 16, cells = [];
  for (let row = 0; row < 27; row++) for (let col = 0; col < columns; col++)
    cells.push(tiles[(col >> 4) * 432 + row * 16 + (col & 15)]);
  const objects = readObjects(rom,recipe.objects);
  const paletteBase = rom.word(27, 0xb7d2 + recipe.tileset * 2);
  const palette = rom.bytes(27, paletteBase + (header[5] & 7) * 16, 16);
  const spritePalette = rom.bytes(27, paletteBase + (((header[5] >> 3) & 3) | 8) * 16, 16);
  const index = header[7] & 31, banks = [rom.prg(30, 0x9762 + index), rom.prg(30, 0x9779 + index)];
  const interactive = new Set([...rom.bytes(14, 0xcb16, 9), ...rom.bytes(14, 0xcac4, 25)]);
  interactive.delete(0x40); interactive.delete(0x79);
  const blockers = objects.filter(o => !MAPPED_OBJECTS.has(o.id)).map(o => `Object $${o.id.toString(16)} is not ported.`);
  if (recipe.name === '1-3') blockers.push('White-block/warp-whistle secret is not implemented.');
  blockers.push('Alternate pipe routes are not implemented.');
  let startY = 300;
  const attrs = rom.bytes(recipe.bank, 0xa400, 8);
  for (let r = 0; r < 27; r++) {
    const t = cells[r * columns + 2];
    if (t !== recipe.sky && t >= attrs[t >> 6]) { startY = r * 16 - 19; break; }
  }
  return { ...recipe, columns, rows: 27, cells, objects, attrs, interactive: [...interactive],
    start: { x: 32, y: startY }, backdrop: `#${NES[palette[0] & 63].toString(16).padStart(6, '0')}`,
    atlases: [0, 1, 2, 3].map(f => metatileAtlas(rom, recipe.bank,
      [banks[0], banks[1] === 0x60 ? 0x60 + f * 2 : banks[1]], palette)),
    sprites: enemySprites(rom, spritePalette), blockers, instructions };
}

function enemySprites(rom, palette) {
  const banks = [0x53, 4, 0x22, 0x4f];
  const unit = (x, y, id, pal, flip = false, bank = null) => ({ x, y, id, pal, flip, bank });
  const goomba = (pal, wings) => {
    const p = rom.bytes(4, 0xa223, 8);
    const pair = f => [unit(-8, -8, p[f * 2], pal), unit(0, -8, p[f * 2 + 1], pal, true)];
    return [pair(0), pair(1), ...wings ? [0xcd, 0xcf].map(w => [...pair(0), unit(-10,-18,w,2,true),unit(2,-18,w,2)]) : []];
  };
  const turtle = (pal, wings) => {
    const p = rom.bytes(4, 0xa1f9, 14), foot = rom.bytes(4, 0xb4a2, 6), yOff = rom.bytes(4, 0xb4a0, 2);
    const walk = (f, w) => {
      const y = yOff[f] - 26;
      const pieces = [unit(-8,y-16,0xc1,3),unit(-8,y,p[f*2],3),unit(0,y,p[f*2+1],pal),
        unit(-8,y+16,foot[f*3],3),unit(0,y+16,foot[f*3+1],3)];
      if (w) pieces.push(unit(0,y-8,w,1));
      return align(pieces, 16);
    };
    return [walk(0,0),walk(1,0),...wings ? [walk(0,0xcd),walk(1,0xcf)] : [],
      align([unit(-8,-8,p[4],pal),unit(0,-8,p[5],pal,true)],8)];
  };
  function align(pieces, bottom) {
    let max = -Infinity;
    for (const p of pieces) for (let y = 0; y < 16; y++) {
      const chr = rom.chr(p.bank ?? banks[p.id >> 6], (p.id & 62) + (y >> 3));
      if (chr[y & 7] || chr[(y & 7) + 8]) max = Math.max(max, p.y + y);
    }
    return pieces.map(p => ({ ...p, y: p.y + bottom - max }));
  }
  const plant = (fire, red) => {
    const p = rom.bytes(5, fire ? 0xa1e4 : 0xa1d4, 8), y = red ? -48 : -32;
    return [0,1].map(f => {
      const a = p.slice(f * 4, f * 4 + 4);
      const pieces = [unit(-8,y,a[0],red?1:2),unit(0,y,a[1],red?1:2,a[0]===a[1]),
        unit(-8,y+16,a[2],2),unit(0,y+16,a[3],2,a[2]===a[3])];
      if (red) pieces.push(unit(-8,-16,a[2],2),unit(0,-16,a[3],2,true));
      return pieces;
    });
  };
  const bro = rom.bytes(4,0xa275,16);
  const sets = {
    goomba: goomba(3,false), paragoomba: goomba(1,true), green: turtle(2,false),
    red: turtle(1,false), winged: turtle(2,true),
    plant: plant(false,false), redPlant: plant(false,true), firePlant: plant(true,false), venus: plant(true,true),
    brother: [0,1,2,3].map(f => bro.slice(f*4,f*4+4).map((p,i) => unit(-8+(i&1)*8,-24+(i>>1)*16,p,2,false,0x4e))),
    boomerang: [0x8b,0x8f,0x89,0x8d].map(p => [unit(-4,-8,p,2,false,0x4e)]),
    fireball: [[unit(-4,-8,0x65,1)]], micro: [[unit(-4,-8,0xff,3)]],
  };
  const result = {};
  for (const [name, frames] of Object.entries(sets)) {
    const pixels = new Uint8Array(64 * 64 * frames.length * 4);
    frames.forEach((pieces, f) => {
      for (const p of pieces) for (let half = 0; half < 2; half++)
        nesTile(rom, p.bank ?? banks[p.id >> 6], (p.id & 62) + half, palette.slice(p.pal*4,p.pal*4+4),
          pixels, 64 * frames.length, f * 64 + 32 + p.x, 48 + p.y + half*8, p.flip);
    });
    result[name] = { frames: frames.length, image: imageData(64*frames.length,64,pixels) };
  }
  return result;
}

export function playerSprites(rom, name) {
  const tables = name === 'sonic' ? [0x50000,0x6fbe0,0x714e0] : [0x64320,0x739e2,0x7446c];
  const [art,map,dplc] = tables, count = rom.readUInt16BE(map) / 2;
  const palette = Array.from({length:16},(_,i) => {
    const w = rom.readUInt16BE(0x2902+i*2);
    return Math.round((w>>1&7)*255/7)<<16 | Math.round((w>>5&7)*255/7)<<8 | Math.round((w>>9&7)*255/7);
  });
  if (!Number.isInteger(count) || count < 100 || count > 300) throw new Error(`${name}: invalid stock sprite mapping table.`);
  const width = 16 * 64, height = Math.ceil(count / 16) * 64, pixels = new Uint8Array(width*height*4);
  for (let f = 0; f < count; f++) {
    const a = map + rom.readInt16BE(map + f*2), pieces = rom.readUInt16BE(a);
    const d = dplc + rom.readInt16BE(dplc + f*2), tiles = [];
    for (let i = 0; i < rom.readUInt16BE(d); i++) {
      const v = rom.readUInt16BE(d+2+i*2), start = v & 4095;
      for (let t = 0; t <= v>>12; t++) tiles.push(start+t);
    }
    if (pieces > 32) throw new Error(`${name}: invalid frame ${f}.`);
    for (let i = 0; i < pieces; i++) {
      const p = a+2+i*8, y = rom.readInt8(p), size = rom[p+1], pattern = rom.readUInt16BE(p+2), x = rom.readInt16BE(p+6);
      const w = ((size>>2&3)+1)*8, h = ((size&3)+1)*8;
      for (let py = 0; py < h; py++) for (let px = 0; px < w; px++) {
        const sx = pattern&0x800 ? w-1-px : px, sy = pattern&0x1000 ? h-1-py : py;
        const index = (pattern&0x7ff)+(sx>>3)*(h>>3)+(sy>>3), source = tiles[index];
        if (source === undefined) throw new Error(`${name}: frame ${f} references unloaded tile ${index}.`);
        const b = rom[art+source*32+(sy&7)*4+((sx&7)>>1)], color = sx&1 ? b&15 : b>>4;
        if (color) pixel(pixels,width,(f%16)*64+32+x+px,Math.floor(f/16)*64+32+y+py,palette[color]);
      }
    }
  }
  return { frames: count, image: imageData(width,height,pixels) };
}

export function worldMap(rom) {
  const palette = rom.bytes(27,0xabd2,16);
  return { cells: rom.bytes(12,0xa5aa,144), atlas: metatileAtlas(rom,12,[0x14,0x16],palette),
    panels: [{name:'1-1',position:4},{name:'1-2',position:8},{name:'1-3',position:10}] };
}
