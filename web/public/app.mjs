import { dotnet } from '/_framework/dotnet.js';

const $=id=>document.getElementById(id),canvas=$('game'),keys=new Set(),touch=new Set();
const context=canvas.getContext('2d',{alpha:false}),image=context.createImageData(320,224);
const bindings={ArrowUp:1,ArrowDown:2,ArrowLeft:4,ArrowRight:8,KeyX:16,KeyC:32,KeyZ:64,Space:64,Enter:128};
let engine,archive,audio,nextAudio=0,paused=false,started=false,failed=false,accumulator=0,last=0,frames=0;
const sources=new Set();

function fail(error){
  failed=true;console.error(error);$('error').hidden=false;
  $('error').textContent=`Game error: ${error.message}\nThe original engine stopped. Check the browser console and Node logs.`;
}
function flushAudio(){
  for(const source of sources)source.stop();
  sources.clear();nextAudio=0;
}
async function enableAudio(){
  audio??=new AudioContext({sampleRate:48000});
  await audio.resume();$('sound').textContent='Sound: on';$('sound').setAttribute('aria-pressed','true');
}
function queueAudio(bytes){
  if(!audio||audio.state!=='running')return;
  if(nextAudio-audio.currentTime>.15)return;
  const samples=new DataView(bytes.buffer,bytes.byteOffset,bytes.byteLength),buffer=audio.createBuffer(2,800,48000);
  for(let channel=0;channel<2;channel++){
    const output=buffer.getChannelData(channel);
    for(let i=0;i<800;i++)output[i]=samples.getInt16((i*2+channel)*2,true)/32768;
  }
  const source=audio.createBufferSource();source.buffer=buffer;source.connect(audio.destination);
  sources.add(source);source.onended=()=>sources.delete(source);
  nextAudio=Math.max(nextAudio,audio.currentTime+.025);source.start(nextAudio);nextAudio+=800/48000;
}
function buttons(){
  let bits=0;for(const key of keys)bits|=key;for(const key of touch)bits|=key;
  const pad=[...navigator.getGamepads?.()??[]].find(Boolean);
  if(pad){
    if(pad.axes[0]<-.25||pad.buttons[14]?.pressed)bits|=4;
    if(pad.axes[0]>.25||pad.buttons[15]?.pressed)bits|=8;
    if(pad.axes[1]>.25||pad.buttons[13]?.pressed)bits|=2;
    if(pad.axes[1]<-.25||pad.buttons[12]?.pressed)bits|=1;
    if(pad.buttons[0]?.pressed||pad.buttons[1]?.pressed)bits|=64;
    if(pad.buttons[9]?.pressed)bits|=128;
  }
  return bits;
}
function pause(value=!paused){
  paused=value;keys.clear();touch.clear();flushAudio();accumulator=0;
  $('pause').textContent=paused?'Resume':'Pause';canvas.dataset.paused=String(paused);
}
function draw(){
  image.data.set(engine.Render());context.putImageData(image,0,0);
  canvas.dataset.state=engine.Describe();canvas.dataset.frames=String(frames);
}
function launch(screen='title',zone=0x40,campaign=null){
  if(!engine)return;
  if(campaign)engine.LoadCampaign(archive,screen,campaign.act??1,campaign.world??1,campaign.screen??0);
  else engine.Load(archive,screen,zone,-1,-1);
  started=true;failed=false;frames=0;pause(false);
  $('overlay').hidden=true;$('error').hidden=true;$('error').textContent='';canvas.focus();draw();
}
function animate(now){
  try{
    accumulator+=Math.min(now-last,100);last=now;
    if(started&&!paused&&!failed){
      while(accumulator>=1000/60){
        engine.Step(buttons());queueAudio(engine.Sound());accumulator-=1000/60;frames++;
      }
      draw();
    }else accumulator=0;
  }catch(error){fail(error);}
  requestAnimationFrame(animate);
}
window.addEventListener('keydown',event=>{
  if(event.target instanceof HTMLButtonElement||event.target instanceof HTMLSelectElement)return;
  if(bindings[event.code]){event.preventDefault();keys.add(bindings[event.code]);}
  if(!event.repeat&&(event.code==='KeyP'||event.code==='Escape'))pause();
});
window.addEventListener('keyup',event=>keys.delete(bindings[event.code]));
window.addEventListener('blur',()=>{if(started)pause(true);});
document.addEventListener('visibilitychange',()=>{if(document.hidden&&started)pause(true);});
for(const button of document.querySelectorAll('[data-key]')){
  const bit=Number(button.dataset.key);
  button.addEventListener('pointerdown',event=>{event.preventDefault();button.setPointerCapture(event.pointerId);touch.add(bit);});
  for(const name of ['pointerup','pointercancel','lostpointercapture'])button.addEventListener(name,()=>touch.delete(bit));
}
$('play').onclick=async()=>{try{await enableAudio();launch();}catch(error){fail(error);}};
$('map').onclick=()=>{try{launch('map');}catch(error){fail(error);}};
$('pause').onclick=()=>pause();
$('sound').onclick=async()=>{
  try{
    if(audio?.state==='running'){flushAudio();await audio.suspend();$('sound').textContent='Sound: off';$('sound').setAttribute('aria-pressed','false');}
    else await enableAudio();
  }catch(error){fail(error);}
};
$('fullscreen').onclick=async()=>{try{if(document.fullscreenElement)await document.exitFullscreen();else await canvas.parentElement.requestFullscreen();canvas.focus();}catch(error){fail(error);}};

try{
  const [assetResponse,metadataResponse]=await Promise.all([fetch('/api/assets'),fetch('/api/game')]);
  if(!assetResponse.ok)throw new Error(`Original-engine assets unavailable (${assetResponse.status}).`);
  if(!metadataResponse.ok)throw new Error(`Conversion metadata unavailable (${metadataResponse.status}).`);
  archive=new Uint8Array(await assetResponse.arrayBuffer());const metadata=await metadataResponse.json();
  const runtime=await dotnet.create();
  const exports=await runtime.getAssemblyExports('SonicBros3.Browser');
  engine=exports.BrowserGame;
  engine.Load(archive,'title',0x40,-1,-1);
  const campaign=JSON.parse(engine.Campaign());
  if(campaign){
    $('campaign').hidden=false;
    const areasById=new Map(campaign.areas.map(area=>[area.source.id,area]));
    const option=(value,label)=>{const item=document.createElement('option');item.value=String(value);item.textContent=label;return item;};
    for(const world of campaign.worlds)$('world').append(option(world.number,`World ${world.number}`));
    const showArea=()=>{
      const area=campaign.areas.find(a=>a.act===Number($('area').value));
      $('open-area').disabled=!area||['blocked','failed'].includes(area.status);
      $('area-status').textContent=area
        ?`${area.source.id} · ${area.width}×${area.height} · ${area.status}\n${area.blockers.join('\n')}`
        :'No ordinary source areas on this screen.';
    };
    const showScreen=()=>{
      const world=campaign.worlds.find(w=>w.number===Number($('world').value)),screen=Number($('map-screen').value);
      const ids=new Set(world.entries.filter(e=>e.screen===screen).map(e=>e.areaId));
      for(const id of ids){
        const alternate=areasById.get(id)?.alternateAreaId;
        if(alternate)ids.add(alternate);
      }
      $('area').replaceChildren();
      for(const area of campaign.areas.filter(a=>ids.has(a.source.id)))
        $('area').append(option(area.act,`${area.source.id} [${area.status}]`));
      showArea();
    };
    $('world').onchange=()=>{
      const world=campaign.worlds.find(w=>w.number===Number($('world').value));
      $('map-screen').replaceChildren();
      for(let screen=0;screen<world.map.length/144;screen++)$('map-screen').append(option(screen,screen+1));
      showScreen();
    };
    $('map-screen').onchange=showScreen;$('area').onchange=showArea;$('world').onchange();
    $('open-world').onclick=()=>{try{launch('map',0x43,{world:Number($('world').value),screen:Number($('map-screen').value)});}catch(error){fail(error);}};
    $('open-area').onclick=()=>{try{launch('level',0x43,{act:Number($('area').value)});}catch(error){fail(error);}};
  }
  $('status').textContent='Original engine, graphics, music and cards. Click Start to enable audio.';
  $('play').disabled=false;
  for(const [screen,zone,label] of [['level',0x40,'PLAY 1-1'],['level',0x41,'PLAY 1-2'],['level',0x42,'PLAY 1-3'],['match',0x40,'CARD MINIGAME']]){
    const button=document.createElement('button');button.textContent=label;
    button.onclick=()=>{try{launch(screen,zone);}catch(error){fail(error);}};$('stages').append(button);
  }
  for(const text of [
    'This website runs the original C# engine in WebAssembly, served by Node.js. No simplified JavaScript gameplay.',
    'Temporary restoration: your existing local sonicbros3.bin supplies the original assets. A complete stock-ROM engine importer is still pending. rom.bin is not read.',
    campaign?`${campaign.worlds.length} world maps / ${campaign.worlds.reduce((n,w)=>n+w.map.length/144,0)} screens / ${campaign.areas.length} source areas. ${campaign.areas.filter(a=>!['blocked','failed'].includes(a.status)).length} experimental native builds passed load/render checks. This does not mean complete gameplay: inspect each area's blockers above.`
      :`${metadata.coverage.worldCount} worlds / ${metadata.coverage.areas.length} areas discovered. Run npm run campaign to build the native campaign archive. Without it, only the original three stages are available.`,
    'Map paths, miniature Sonic, title sequence, end-card presentation, card matching minigame and synthesized-chip music use the original engine.',
  ]){const paragraph=document.createElement('p');paragraph.textContent=text;$('coverage').append(paragraph);}
  last=performance.now();requestAnimationFrame(animate);
}catch(error){fail(error);$('status').textContent='Unable to start the original engine.';}
