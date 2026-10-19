import { readdir, readFile } from 'node:fs/promises';
import { join } from 'node:path';
import { createHash } from 'node:crypto';

export const SMB3_SHA1 = 'a03e7e526e79df222e048ae22214bca2bc49c449';
export const SONIC2_SHA1 = '8bca5dcef1af3e00098666fd892dc1c2a76333f9';
export const sha1 = data => createHash('sha1').update(data).digest('hex');

export function isSonic2(data) {
  return data.length >= 0x200 &&
    /^SEGA (GENESIS|MEGA DRIVE)/.test(data.toString('ascii', 0x100, 0x110)) &&
    /SONIC THE\s+HEDGEHOG 2/.test(data.toString('ascii', 0x120, 0x180)) &&
    data.toString('ascii', 0x180, 0x18e).trim() === 'GM 00001051-01';
}

export class NesRom {
  constructor(data) {
    if (data.length < 16 || !data.subarray(0, 4).equals(Buffer.from([78, 69, 83, 26])))
      throw new Error('SMB3 input is not an iNES ROM.');
    this.prgSize = data[4] * 0x4000;
    if ((data[6] & 4) || (data[7] & 12) === 8 || !this.prgSize ||
        data.length !== 16 + this.prgSize + data[5] * 0x2000)
      throw new Error('Unsupported or truncated iNES image (no trainer/NES 2.0 support).');
    if (sha1(data) !== SMB3_SHA1) throw new Error(`Unsupported SMB3 revision: ${sha1(data)}.`);
    this.data = data;
  }
  prg(bank, address) {
    const offset = bank * 0x2000 + (address & 0x1fff);
    if (offset < 0 || offset >= this.prgSize) throw new Error('PRG read outside ROM.');
    return this.data[16 + offset];
  }
  word(bank, address) { return this.prg(bank, address) | this.prg(bank, address + 1) << 8; }
  bytes(bank, address, count) {
    return Array.from({ length: count }, (_, i) => this.prg(bank, address + i));
  }
  chr(bank, tile) {
    const offset = 16 + this.prgSize + bank * 1024 + tile * 16;
    if (offset < 16 + this.prgSize || offset + 16 > this.data.length) throw new Error('CHR read outside ROM.');
    return this.data.subarray(offset, offset + 16);
  }
}

export async function discoverRoms(directory) {
  const entries = await readdir(directory, { withFileTypes: true });
  const candidates = entries.filter(e => e.isFile() && /\.md$/i.test(e.name));
  const matches = [];
  for (const entry of candidates) {
    const data = await readFile(join(directory, entry.name));
    if (!isSonic2(data)) continue;
    if (sha1(data) !== SONIC2_SHA1)
      throw new Error(`${entry.name}: Sonic 2 header found, but unsupported revision (${sha1(data)}). Expected World Rev A.`);
    matches.push({ name: entry.name, data });
  }
  if (matches.length !== 1)
    throw new Error(`Expected one stock Sonic 2 World Rev A .md ROM in ${directory}; found ${matches.length}. Markdown text is ignored.`);
  const nes = new NesRom(await readFile(join(directory, 'smb3.nes')));
  return { sonic: matches[0].data, nes, sources: { sonic: matches[0].name, sonicSha1: SONIC2_SHA1, smb3Sha1: SMB3_SHA1 } };
}
