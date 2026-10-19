// Phase 1 golden-trace generator. Runs the legacy C# engine headlessly and writes deterministic .sb3g traces.
// Usage: node tools/golden/generate.mjs [--campaign] [--only name]
// Core trace set: the three SMB3 levels and the eight ported Sonic acts, with idle/right/jump/spindash plus
// goal/death/pipe/hill paths. --campaign adds every non-blocked campaign area (idle + right, shorter).
import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { mkdirSync, readFileSync, statSync, unlinkSync, writeFileSync } from 'node:fs';
import { basename, dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { gzipSync } from 'node:zlib';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const legacy = join(root, 'legacy');
const outDir = join(root, 'tests', 'golden');
const inputsDir = join(outDir, 'inputs');
mkdirSync(inputsDir, { recursive: true });

const exe = join(legacy, 'src', 'SonicBros3', 'bin', 'Debug', 'net10.0',
  process.platform === 'win32' ? 'SonicBros3.exe' : 'SonicBros3');

function run(cmd, args, label) {
  const r = spawnSync(cmd, args, { cwd: root, encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 });
  if (r.error) throw r.error;
  if (r.status !== 0) {
    console.error(`\n${label} failed (exit ${r.status})\n${r.stdout ?? ''}\n${r.stderr ?? ''}`);
    throw new Error(label);
  }
  return r.stdout ?? '';
}
const sha256 = data => createHash('sha256').update(data).digest('hex');
const fileSha = path => sha256(readFileSync(path));

const scripts = {
  idle: '0 -\n',
  right: '0 -\n201 R\n',
  hill: '0 -\n201 R\n',
  jump: '0 -\n201 R\n261 RC\n277 R\n331 RC\n347 R\n401 RC\n417 R\n',
  spindash: '0 -\n201 D\n211 DC\n217 D\n227 DC\n233 D\n239 D\n251 R\n',
  down: '0 -\n201 D\n',
  goal: '0 -\n201 R\n241 RA\n261 R\n',
  goal12: '0 -\n201 R\n219 RA\n239 R\n',
  goal13: '0 -\n401 R\n441 RA\n449 R\n',
  title: '0 -\n101 S\n102 -\n103 S\n104 -\n',
  right0: '0 R\n',
  jump0: '0 R\n60 RC\n76 R\n130 RC\n146 R\n',
  spindash0: '0 D\n10 DC\n16 D\n26 DC\n32 D\n38 D\n50 R\n',
};
for (const [name, body] of Object.entries(scripts)) writeFileSync(join(inputsDir, `${name}.txt`), body);

const CORE = [
  { name: 'smb3-1-1', level: '1-1' },
  { name: 'smb3-1-2', level: '1-2' },
  { name: 'smb3-1-3', level: '1-3' },
  { name: 'sonic-ehz-1', zone: '00', act: 1 },
  { name: 'sonic-ehz-2', zone: '00', act: 2 },
  { name: 'sonic-ghz-1', zone: '11', act: 1 },
  { name: 'sonic-ghz-2', zone: '11', act: 2 },
  { name: 'sonic-ghz-3', zone: '12', act: 1 },
  { name: 'sonic-mz-1', zone: '13', act: 1 },
  { name: 'sonic-mz-2', zone: '13', act: 2 },
  { name: 'sonic-mz-3', zone: '14', act: 1 },
];

const SPECIAL = [
  { name: 'smb3-1-1-goal', level: '1-1', debug: '2600,370', script: 'goal', frames: 1600 },
  { name: 'smb3-1-1-death', level: '1-1', script: 'idle', frames: 2400 },
  { name: 'smb3-1-2-hill', level: '1-2', script: 'hill', frames: 900 },
  { name: 'smb3-1-2-goal', level: '1-2', debug: '2670,370', script: 'goal12', frames: 1600 },
  { name: 'smb3-1-2-goomba-pipe', level: '1-2', debug: '480,360', script: 'idle', frames: 900 },
  { name: 'smb3-1-2-goomba-pipe-safe', level: '1-2', debug: '520,360', script: 'idle', frames: 900 },
  { name: 'smb3-1-3-goal', level: '1-3', debug: '2350,396', script: 'goal13', frames: 1600 },
  { name: 'smb3-1-1-pipe', zone: '40', act: 1, data: 'web-data/campaign.bin', debug: '2272,93', script: 'down', frames: 500 },
  { name: 'smb3-1-1-pipe-jump', zone: '40', act: 1, data: 'web-data/campaign.bin', debug: '2272,60', script: 'down', frames: 600 },
  { name: 'smb3-1-2-pipe', zone: '41', act: 1, data: 'web-data/campaign.bin', debug: '1200,253', script: 'down', frames: 700 },
  { name: 'smb3-1-2-pipe-jump', zone: '41', act: 1, data: 'web-data/campaign.bin', debug: '1200,220', script: 'down', frames: 800 },
  { name: 'sonic-ehz-2-boss', zone: '00', act: 2, debug: '10704,1000', script: 'jump', frames: 2200 },
  { name: 'sonic-mz-3-boss', zone: '14', act: 1, debug: '6656,700', script: 'jump', frames: 2600 },
  { name: 'frontend-title', start: 'title', frames: 600 },
  { name: 'frontend-title-map', start: 'title', script: 'title', frames: 700 },
  { name: 'frontend-map', start: 'map', frames: 400 },
  { name: 'frontend-match', start: 'match', frames: 400 },
  { name: 'smb3-1-1-playing-idle', zone: '40', act: 1, script: 'idle', frames: 600, noTitleCard: true },
  { name: 'smb3-1-1-playing-right', zone: '40', act: 1, script: 'right0', frames: 600, noTitleCard: true },
  { name: 'smb3-1-1-playing-jump', zone: '40', act: 1, script: 'jump0', frames: 600, noTitleCard: true },
  { name: 'smb3-1-1-playing-spindash', zone: '40', act: 1, script: 'spindash0', frames: 600, noTitleCard: true },
];

const only = process.argv.includes('--only') ? process.argv[process.argv.indexOf('--only') + 1] : null;
const includeCampaign = process.argv.includes('--campaign');
const keepRaw = process.argv.includes('--raw');

console.log('Building the legacy engine...');
run('dotnet', ['build', join(legacy, 'src', 'SonicBros3', 'SonicBros3.csproj'), '-c', 'Debug', '--nologo', '-v', 'q'], 'build');

const scenarios = [];
function add(s) { if (!only || s.name === only || s.name.startsWith(only + '-')) scenarios.push(s); }
for (const level of CORE) for (const script of ['idle', 'right', 'jump', 'spindash'])
  add({ ...level, name: `${level.name}-${script}`, script, frames: 1200 });
for (const s of SPECIAL) add(s);

if (includeCampaign) {
  const rows = JSON.parse(run(exe, ['--list-campaign', '--data', 'web-data/campaign.bin'], 'list campaign'));
  for (const row of rows) {
    if (row.status === 'failed' || row.status === 'blocked') continue;
    for (const script of ['idle', 'right']) add({
      name: `campaign-${String(row.act).padStart(3, '0')}-${script}`,
      campaignAct: row.act, world: row.world, script, frames: 420, data: 'web-data/campaign.bin',
    });
  }
}

console.log(`${scenarios.length} scenarios`);
let priorScenarios = [];
try {
  priorScenarios = JSON.parse(readFileSync(join(outDir, 'manifest.json'), 'utf8')).scenarios ?? [];
} catch { /* first run */ }
const manifest = {
  format: 'sb3.golden-manifest/1',
  generator: 'tools/golden/generate.mjs',
  legacyCommit: run('git', ['rev-parse', 'HEAD'], 'git rev-parse').trim(),
  archive: { path: 'sonicbros3.bin', sha256: fileSha(join(root, 'sonicbros3.bin')) },
  campaignArchive: { path: 'web-data/campaign.bin', sha256: fileSha(join(root, 'web-data', 'campaign.bin')) },
  scenarios: [],
};
let total = 0, done = 0;
function traceArgs(s, rawFile) {
  const data = s.data ?? (s.campaignAct ? 'web-data/campaign.bin' : 'sonicbros3.bin');
  const zone = typeof s.zone === 'number' ? s.zone.toString(16).padStart(2, '0') : s.zone;
  const args = ['--start', s.start ?? 'level', '--no-build', '--data', data,
    '--golden', rawFile, '--scenario', s.name, '--frames', String(s.frames ?? 1200),
    '--hash-every', '30', '--snapshot-every', '60'];
  if (s.level) args.push('--level', s.level);
  if (zone) args.push('--zone', zone, '--act', String(s.act ?? 1));
  if (s.campaignAct) args.push('--campaign-act', String(s.campaignAct), '--world', String(s.world ?? 1));
  if (s.debug) args.push('--debug-start', s.debug);
  if (s.noTitleCard) args.push('--no-title-card');
  const script = s.script ?? s.inputs;
  if (script) args.push('--inputs', join(inputsDir, `${script}.txt`));
  return args;
}
if (process.argv.includes('--verify')) {
  const entries = priorScenarios.filter(e => !only || e.name === only || e.name.startsWith(only + '-'));
  let ok = 0, bad = 0;
  for (const e of entries) {
    const raw = join(outDir, `.verify-${e.name}.sb3g`);
    run(exe, traceArgs({
      ...e, start: e.start ?? 'level', debug: e.debugStart, script: e.inputs,
      data: e.data ?? (e.campaignAct ? 'web-data/campaign.bin' : 'sonicbros3.bin'),
    }, raw), `verify ${e.name}`);
    const gz = raw + '.gz';
    writeFileSync(gz, gzipSync(readFileSync(raw), { level: 9 }));
    unlinkSync(raw);
    const hash = fileSha(gz);
    unlinkSync(gz);
    if (hash === e.sha256) ok++;
    else { bad++; console.log(`MISMATCH ${e.name}: manifest ${e.sha256}, now ${hash}`); }
  }
  console.log(`verify: ${ok} match, ${bad} differ`);
  process.exit(bad ? 1 : 0);
}
for (const s of scenarios) {
  const rawFile = join(outDir, `${s.name}.sb3g`);
  run(exe, traceArgs(s, rawFile), `trace ${s.name}`);
  let file = rawFile;
  if (!keepRaw) {
    file = rawFile + '.gz';
    writeFileSync(file, gzipSync(readFileSync(rawFile), { level: 9 }));
    unlinkSync(rawFile);
  }
  const size = statSync(file).size;
  total += size;
  manifest.scenarios.push({
    name: s.name, file: basename(file), sha256: fileSha(file), bytes: size,
    level: s.level ?? null, zone: typeof s.zone === 'number' ? s.zone : s.zone ? parseInt(s.zone, 16) : null,
    act: s.act ?? null, world: s.world ?? null, campaignAct: s.campaignAct ?? null,
    debugStart: s.debug ?? null, inputs: s.script ?? null, frames: s.frames ?? 1200,
    start: s.start ?? 'level', data: s.data ?? 'sonicbros3.bin', noTitleCard: s.noTitleCard ?? false,
  });
  if (++done % 25 === 0) console.log(`  ${done}/${scenarios.length}`);
}
const regenerated = new Set(manifest.scenarios.map(s => s.name));
manifest.scenarios = [...priorScenarios.filter(s => !regenerated.has(s.name)), ...manifest.scenarios]
  .sort((a, b) => a.name.localeCompare(b.name));
writeFileSync(join(outDir, 'manifest.json'), JSON.stringify(manifest, null, 2) + '\n');
console.log(`Wrote ${scenarios.length} traces, ${(total / 1024 / 1024).toFixed(2)} MiB; manifest now lists ${manifest.scenarios.length}.`);
