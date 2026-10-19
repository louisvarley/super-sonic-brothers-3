import { spawnSync } from 'node:child_process';
import { stat, readdir, writeFile } from 'node:fs/promises';
import { resolve, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const root=fileURLToPath(new URL('../',import.meta.url));
export const runtimeDir=resolve(root,'web','runtime','wwwroot','_framework');

async function newest(directory){
  let time=0;
  for(const entry of await readdir(directory,{withFileTypes:true})){
    if(entry.name==='obj'||/^bin(?:-|$)/.test(entry.name))continue;
    const path=join(directory,entry.name);
    if(entry.isDirectory())time=Math.max(time,await newest(path));
    else if(/\.(cs|csproj|mjs)$/.test(entry.name))time=Math.max(time,(await stat(path)).mtimeMs);
  }
  return time;
}
export async function ensureBrowserBuild(){
  let present=false,built=0;
  try{await stat(join(runtimeDir,'dotnet.js'));present=true;}
  catch(error){if(error.code!=='ENOENT')throw error;}
  try{built=(await stat(resolve(runtimeDir,'..','..','.built'))).mtimeMs;}
  catch(error){if(error.code!=='ENOENT')throw error;}
  // Production containers contain published files, not the SDK or source tree.
  let changed=0;
  try{changed=await newest(resolve(root,'src'));}
  catch(error){if(error.code!=='ENOENT')throw error;}
  if(present&&(!changed||built>=changed))return;
  console.log('Building the original game engine for the browser...');
  const result=spawnSync('dotnet',['publish',resolve(root,'src','SonicBros3.Browser','SonicBros3.Browser.csproj'),
    '-c','Release','--nologo','-o',resolve(root,'web','runtime')],{stdio:'inherit'});
  if(result.error)throw new Error(`The browser build needs the .NET 10 SDK: ${result.error.message}`);
  if(result.status!==0)throw new Error(`Browser engine build failed (exit ${result.status}).`);
  await writeFile(resolve(runtimeDir,'..','..','.built'),new Date().toISOString());
}
if(process.argv[1]&&resolve(process.argv[1])===fileURLToPath(import.meta.url)){
  try{await ensureBrowserBuild();}catch(error){console.error(error.message);process.exitCode=1;}
}
