// Summarise committed traces: tick counts, phase coverage, act transitions, framebuffer hashes, sizes.
// Usage: node tools/golden/summarize.mjs [tests/golden]
import { readFileSync, readdirSync } from 'node:fs';
import { gunzipSync } from 'node:zlib';
import { join } from 'node:path';

const dir = process.argv[2] ?? 'tests/golden';
const files = readdirSync(dir).filter(f => f.endsWith('.sb3g.gz')).sort();
let ticks = 0, bytes = 0, noPlay = [], transitions = [], perAct = new Map();
for (const file of files) {
  let b = readFileSync(join(dir, file));
  bytes += b.length;
  b = gunzipSync(b);
  const headerLen = b.readUInt32LE(8);
  const header = JSON.parse(b.toString('utf8', 12, 12 + headerLen));
  let p = 12 + headerLen, n = 0, sawPlay = false, acts = new Set(), lastAct = null;
  while (p < b.length) {
    const kind = b[p++];
    if (kind === 0) {
      n++;
      const phase = b[p + 4];
      // act is the third byte after tick/phase/zone: tick(4) phase(1) zone(1) act(1)
      const act = b[p + 6];
      if (phase === 0) sawPlay = true;
      acts.add(act);
      if (lastAct !== null && act !== lastAct) transitions.push(`${header.Scenario}:${lastAct}->${act}`);
      lastAct = act;
      p += 133;
    } else if (kind === 1) {
      const len = b.readUInt32LE(p + 4); p += 8 + len;
    } else if (kind === 2) { p += 4; } else throw new Error(`bad kind ${kind} in ${file}`);
  }
  ticks += n;
  if (!sawPlay && !header.Scenario.startsWith('frontend-')) noPlay.push(file);
  const group = header.Scenario.startsWith('campaign-') ? 'campaign'
    : header.Scenario.startsWith('smb3-') ? 'smb3'
    : header.Scenario.startsWith('sonic-') ? 'sonic' : 'frontend';
  perAct.set(group, (perAct.get(group) ?? 0) + 1);
}
console.log(`traces: ${files.length}`);
console.log(`ticks: ${ticks.toLocaleString()}  gzip: ${(bytes / 1024 / 1024).toFixed(2)} MiB`);
console.log(`never reached Playing: ${noPlay.length}${noPlay.length ? ' -> ' + noPlay.join(', ') : ''}`);
console.log(`act transitions inside a trace: ${transitions.length}`);
for (const t of transitions.slice(0, 20)) console.log(`  ${t}`);
const byPrefix = [...perAct.entries()].sort((a, b) => b[1] - a[1]);
console.log('groups: ' + byPrefix.map(([k, v]) => `${k}=${v}`).join(', '));
