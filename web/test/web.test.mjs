import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, writeFile, readFile, readdir, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { once } from 'node:events';
import { createHash } from 'node:crypto';
import { spawnSync } from 'node:child_process';
import { discoverRoms, isSonic2, NesRom } from '../roms.mjs';
import { convert } from '../convert.mjs';
import { createApp } from '../server.mjs';

const root=resolve(import.meta.dirname,'../..');

test('ROM discovery ignores Markdown text and rejects missing/unsupported inputs',async()=>{
  const dir=await mkdtemp(join(tmpdir(),'sonic-web-'));
  try{
    await writeFile(join(dir,'notes.md'),'# Sonic 2 ROM notes\nSEGA GENESIS');
    await assert.rejects(discoverRoms(dir),/found 0/);
    assert.equal(isSonic2(Buffer.from('# SEGA GENESIS')),false);
    const fake=Buffer.alloc(0x200);
    fake.write('SEGA GENESIS',0x100);fake.write('SONIC THE             HEDGEHOG 2',0x120);fake.write('GM 00001051-01',0x180);
    assert.equal(isSonic2(fake),true);
    await writeFile(join(dir,'unknown.md'),fake);
    await assert.rejects(discoverRoms(dir),/unsupported revision/);
    assert.throws(()=>new NesRom(Buffer.alloc(12)),/iNES/);
  }finally{await rm(dir,{recursive:true});}
});

test('stock-ROM conversion exports all areas independently of legacy archives',async t=>{
  let roms;
  try{roms=await discoverRoms(root);}catch(error){
    if(error.code==='ENOENT'||/found 0/.test(error.message)){t.skip('Local ROMs are not mounted.');return;}
    throw error;
  }
  const dir=await mkdtemp(join(tmpdir(),'sonic-web-roms-'));
  try{
    await writeFile(join(dir,'arbitrary title.MD'),roms.sonic);
    await writeFile(join(dir,'smb3.nes'),roms.nes.data);
    await writeFile(join(dir,'README.md'),'Ordinary Markdown is not a ROM.');
    const output=join(dir,'converted'),bundle=await convert(dir,output);
    assert.equal(bundle.sources.sonic,'arbitrary title.MD');
    assert.equal(bundle.players.sonic.frames,214);
    assert.ok(bundle.players.tails.frames>100);
    assert.deepEqual(bundle.levels.map(l=>[l.name,l.columns,l.instructions]),[['1-1',176,51925],['1-2',176,60540],['1-3',160,44347]]);
    const hashes=bundle.levels.map(l=>createHash('sha256').update(Buffer.from(l.cells)).digest('hex'));
    assert.deepEqual(hashes,[
      'b75500015f32e8b77980502174eff4bd764aa866299202e1b56e9a151df3cb3a',
      '551637d5c89e93b4ca40bdc3242221cfa91644652187516bf9927690cafe178d',
      '0c230c7422299b7980dee38c32956535ae14c3f1b37e2758413bc4dd66683f33',
    ]);
    assert.equal(bundle.coverage.worldCount,8);assert.equal(bundle.coverage.areas.length,248);
    assert.equal(bundle.coverage.areas.filter(a=>a.status==='failed').length,1);
    assert.equal(bundle.coverage.complete,false);
    const report=JSON.parse(await readFile(join(output,'report.json'),'utf8'));
    assert.deepEqual(report,bundle.coverage);
    assert.equal((await readdir(join(output,'areas'))).length,247);
    assert.equal((await readFile(join(output,'areas','ts01-15-BB82-C527','tiles.bin'))).length,6480);
    const strict=spawnSync(process.execPath,[join(root,'web','convert.mjs'),'--strict'],{env:{...process.env,ROM_DIR:dir},encoding:'utf8'});
    assert.equal(strict.status,2,strict.stderr);
    assert.equal(JSON.parse(strict.stdout).coverage.areas,248);
  }finally{await rm(dir,{recursive:true});}
});

test('conversion CLI rejects unknown arguments and missing output directories',()=>{
  for(const args of [['--unknown'],['--output'],['--output','--strict'],['unexpected','--strict']]){
    const result=spawnSync(process.execPath,[join(root,'web','convert.mjs'),...args],{encoding:'utf8'});
    assert.equal(result.status,1);assert.match(result.stderr,/Usage:|requires a directory/);
  }
});

test('server exposes only game assets and explicitly allowlisted WebAssembly resources',async()=>{
  const bundle={runtime:'original-csharp-wasm',sources:{sonic:'local.md'}};
  const archive=Buffer.from('PK-test-local-archive');
  const framework=new Map([['/_framework/test.wasm',join(root,'package.json')]]);
  const server=createApp(bundle,{archive,framework});
  server.listen(0,'127.0.0.1');await once(server,'listening');
  const url=`http://127.0.0.1:${server.address().port}`;
  try{
    assert.equal((await fetch(url+'/healthz')).status,200);
    const html=await fetch(url);assert.equal(html.status,200);assert.match(await html.text(),/SONIC/);
    assert.match(html.headers.get('content-security-policy'),/'wasm-unsafe-eval'/);
    for(const path of ['/rom.bin','/smb3.nes','/sonicbros3.bin','/local.md','/.env','/package.json','/web/server.mjs',
      '/%2e%2e%2fpackage.json','/_framework/../package.json','/_framework/unknown.wasm','/engine.mjs','/render.mjs'])
      assert.equal((await fetch(url+path)).status,404,path);
    assert.equal((await fetch(url+'/api/game',{method:'POST'})).status,405);
    assert.deepEqual(await(await fetch(url+'/api/game')).json(),bundle);
    assert.deepEqual(Buffer.from(await(await fetch(url+'/api/assets')).arrayBuffer()),archive);
    assert.equal((await fetch(url+'/api/assets',{method:'HEAD'})).status,200);
    const wasm=await fetch(url+'/_framework/test.wasm');
    assert.equal(wasm.status,200);assert.equal(wasm.headers.get('content-type'),'application/wasm');
  }finally{server.close();await once(server,'close');}
});
