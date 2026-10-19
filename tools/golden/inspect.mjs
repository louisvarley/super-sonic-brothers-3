// Inspect .sb3g[.gz] traces: header, phase/act transitions, Sonic range, framebuffer-hash cadence.
// Usage: node tools/golden/inspect.mjs tests/golden/foo.sb3g.gz [more...]
import { readFileSync } from 'node:fs';
import { gunzipSync } from 'node:zlib';
import { basename } from 'node:path';

const TICK_BODY = 133;
const ENTITY = 21;

function readTrace(path) {
  let b = readFileSync(path);
  if (b[0] === 0x1f && b[1] === 0x8b) b = gunzipSync(b);
  if (b.toString('ascii', 0, 8) !== 'SB3GOLD1') throw new Error(`${path}: not a trace`);
  const headerLen = b.readUInt32LE(8);
  const header = JSON.parse(b.toString('utf8', 12, 12 + headerLen));
  let p = 12 + headerLen;
  const ticks = [], snapshots = [];
  let end = null;
  while (p < b.length) {
    const kind = b[p++];
    if (kind === 0) {
      const t = {};
      const start = p;
      t.tick = b.readUInt32LE(p); p += 4;
      t.phase = b[p++]; t.zone = b[p++]; t.act = b[p++];
      t.cameraX = b.readInt32LE(p); p += 4;
      t.cameraY = b.readInt32LE(p); p += 4;
      t.sonic = readEntity(b, p); p += ENTITY;
      t.tails = readEntity(b, p); p += ENTITY;
      t.rings = b.readUInt16LE(p); p += 2;
      t.score = b.readUInt32LE(p); p += 4;
      t.timer = b.readUInt32LE(p); p += 4;
      t.lives = b[p++]; t.gameMode = b[p++];
      t.wholeRam = b.readBigUInt64LE(p); p += 8;
      t.regions = Array.from({ length: header.Regions.length }, () => { const v = b.readBigUInt64LE(p); p += 8; return v; });
      t.screenHash = b.readBigUInt64LE(p); p += 8;
      if (p - start !== TICK_BODY) throw new Error(`bad tick size ${p - start}`);
      ticks.push(t);
    } else if (kind === 1) {
      const tick = b.readUInt32LE(p); p += 4;
      const len = b.readUInt32LE(p); p += 4;
      snapshots.push({ tick, value: JSON.parse(b.toString('utf8', p, p + len)) }); p += len;
    } else if (kind === 2) {
      end = b.readUInt32LE(p); p += 4;
    } else throw new Error(`bad kind ${kind} at ${p}`);
  }
  return { header, ticks, snapshots, end };
}
function readEntity(b, p) {
  return {
    x: b.readInt32LE(p), y: b.readInt32LE(p + 4),
    xv: b.readInt16LE(p + 8), yv: b.readInt16LE(p + 10), inertia: b.readInt16LE(p + 12),
    angle: b[p + 14], status: b[p + 15], status2: b[p + 16], routine: b[p + 17],
    anim: b[p + 18], frame: b[p + 19], sub: b[p + 20],
  };
}

for (const path of process.argv.slice(2)) {
  const { header, ticks, snapshots, end } = readTrace(path);
  const phases = [];
  for (const t of ticks) if (phases.at(-1)?.phase !== t.phase) phases.push({ phase: t.phase, tick: t.tick });
  const acts = [...new Set(ticks.map(t => `${t.zone}:${t.act}`))];
  const xs = ticks.map(t => t.sonic.x), ys = ticks.map(t => t.sonic.y);
  const hashes = ticks.filter(t => t.screenHash !== 0n).length;
  console.log(`\n${basename(path)}  ${header.Scenario}`);
  console.log(`  header: level=${header.Level || '-'} zone=${header.Zone} act=${header.Act} debug=${header.DebugStart || '-'} frames=${header.Frames}`);
  console.log(`  ticks=${ticks.length} end=${end} snapshots=${snapshots.length} screenHashes=${hashes}`);
  console.log(`  phases: ${phases.map(p => `${p.phase}@${p.tick}`).join(' -> ')}`);
  console.log(`  zone:act seen: ${acts.join(', ')}`);
  console.log(`  sonic x ${Math.min(...xs)}..${Math.max(...xs)}  y ${Math.min(...ys)}..${Math.max(...ys)}`);
  console.log(`  rings ${ticks.at(-1).rings} score ${ticks.at(-1).score} lives ${ticks.at(-1).lives}`);
}
