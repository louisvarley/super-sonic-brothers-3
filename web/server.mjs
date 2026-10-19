import { createServer } from 'node:http';
import { readFile, readdir, stat } from 'node:fs/promises';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { dirname, resolve } from 'node:path';
import { discoverRoms } from './roms.mjs';
import { convertCatalog } from './catalog.mjs';
import { ensureBrowserBuild, runtimeDir } from './build.mjs';

const publicDir = fileURLToPath(new URL('./public/', import.meta.url));
const files = new Map([
  ['/', ['index.html','text/html; charset=utf-8']],
  ['/app.mjs',['app.mjs','text/javascript; charset=utf-8']],
  ['/style.css',['style.css','text/css; charset=utf-8']],
]);
export function createApp(bundle, { archive, framework=new Map() }={}) {
  const data=JSON.stringify(bundle);
  return createServer(async(req,res)=>{
    res.setHeader('X-Content-Type-Options','nosniff');
    res.setHeader('Referrer-Policy','no-referrer');
    res.setHeader('Content-Security-Policy',"default-src 'self'; img-src 'self' data:; style-src 'self'; script-src 'self' 'wasm-unsafe-eval'; connect-src 'self'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'");
    try {
      if(req.method!=='GET'&&req.method!=='HEAD'){res.writeHead(405,{'Allow':'GET, HEAD'});res.end();return;}
      const path=new URL(req.url,'http://localhost').pathname;
      if(path==='/api/game'){
        res.writeHead(200,{'Content-Type':'application/json','Cache-Control':'private, max-age=0, must-revalidate'});
        res.end(req.method==='HEAD'?undefined:data);return;
      }
      if(path==='/api/assets'&&archive){
        res.writeHead(200,{'Content-Type':'application/octet-stream','Cache-Control':'no-store'});
        res.end(req.method==='HEAD'?undefined:archive);return;
      }
      if(path==='/healthz'){res.writeHead(200,{'Content-Type':'application/json'});res.end(req.method==='HEAD'?undefined:'{"status":"ready"}');return;}
      const runtime=framework.get(path);
      if(runtime){
        const content=await readFile(runtime);
        res.writeHead(200,{'Content-Type':path.endsWith('.js')?'text/javascript':path.endsWith('.wasm')?'application/wasm':'application/octet-stream','Cache-Control':'no-cache'});
        res.end(req.method==='HEAD'?undefined:content);return;
      }
      const file=files.get(path);
      if(!file){res.writeHead(404,{'Content-Type':'text/plain'});res.end('Not found');return;}
      const content=await readFile(resolve(publicDir,file[0]));
      res.writeHead(200,{'Content-Type':file[1],'Cache-Control':'no-cache'});
      res.end(req.method==='HEAD'?undefined:content);
    }catch(error){
      console.error('HTTP request failed:',error);
      if(!res.headersSent)res.writeHead(500,{'Content-Type':'text/plain'});
      res.end('Request failed. Check server logs.');
    }
  });
}
if(process.argv[1]&&import.meta.url===pathToFileURL(resolve(process.argv[1])).href){
  try{
    const port=Number(process.env.PORT??3000),host=process.env.HOST??'127.0.0.1';
    if(!Number.isInteger(port)||port<1||port>65535)throw new Error('PORT must be an integer from 1 to 65535.');
    const root=process.env.ROM_DIR??resolve(dirname(fileURLToPath(import.meta.url)),'..');
    await ensureBrowserBuild();
    const campaignPath=resolve(root,'web-data','campaign.bin');
    let campaignExists=false;
    try{campaignExists=(await stat(campaignPath)).isFile();}catch(error){if(error.code!=='ENOENT')throw error;}
    const archivePath=process.env.GAME_ARCHIVE??(campaignExists?campaignPath:resolve(root,'sonicbros3.bin'));
    let archive;
    try{archive=await readFile(archivePath);}
    catch(error){
      if(error.code!=='ENOENT')throw error;
      throw new Error(`Original-engine assets missing: ${archivePath}. This restoration temporarily requires your existing sonicbros3.bin archive; no simplified-game fallback is used.`);
    }
    if(archive[0]!==0x50||archive[1]!==0x4b)throw new Error('GAME_ARCHIVE must be an extracted game ZIP archive, not a ROM.');
    const framework=new Map((await readdir(runtimeDir,{withFileTypes:true}))
      .filter(f=>f.isFile()&&/\.(js|wasm|dat)$/.test(f.name)).map(f=>['/_framework/'+f.name,resolve(runtimeDir,f.name)]));
    console.log('Loading original game assets and the ROM conversion catalog...');
    const {nes,sources}=await discoverRoms(root);
    const bundle={sources,coverage:await convertCatalog(nes)};
    const server=createApp({...bundle,runtime:'original-csharp-wasm',assetMode:'existing-local-archive',campaignArchive:archivePath===campaignPath}, {archive,framework});
    server.on('error',error=>{console.error('Server failed:',error.message);process.exitCode=1;});
    server.listen(port,host,()=>console.log(`Sonic Bros. 3 ready at http://${host}:${port} (original engine; temporary local archive: ${archivePath}; rom.bin is not read)`));
    for(const signal of ['SIGTERM','SIGINT'])process.on(signal,()=>server.close());
  }catch(error){console.error('Startup failed:',error.message);process.exitCode=1;}
}
