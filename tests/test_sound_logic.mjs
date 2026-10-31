import test from 'node:test';
import assert from 'node:assert/strict';
import {placedSounds,soundSchedule,soundLanes} from '../static/sound-logic.js';
import {timelineDuration,segmentPlayback,criticalSignature} from '../static/playback-logic.js';
import {MontagePlayer} from '../static/montage-player.js';
import {SoundDesk,waveformSvg} from '../static/sound-ui.js';

const close=(actual,expected)=>assert.ok(Math.abs(actual-expected)<1e-8,`${actual} != ${expected}`);
function deferred(){let resolve,reject;const promise=new Promise((yes,no)=>{resolve=yes;reject=no;});return {promise,resolve,reject};}
function event(patch={}){return {id:'e1',segment_id:'s1',asset_id:'a',name:'Radio',lane:'voice',stream:1,offset_frames:24,
  source_in_frame:12,duration_frames:48,source_seconds:5,gain:.4,muted:false,...patch};}
function project(){return {id:'p1',revision:7,fps:24,output_size:[1280,720],blocks:[],assets:{a:{id:'a',kind:'video',name:'Radio.mp4',bytes:100,modified_ns:1}},
  timeline:{version:1,fps:24,segments:[{id:'s1',name:'Selected',color:'mint',start_frame:48,duration_frames:72},
    {id:'s2',name:'Other',color:'blue',start_frame:192,duration_frames:24}],clips:[],markers:[],music:null,clip_audio:false,sound_events:[event()],undo:[],redo:[]}};}

test('placed sounds follow segment offset ignore orphan preserve inputs and show exact tail',()=>{
  const p=project();p.timeline.sound_events.push(event({id:'tail',offset_frames:60,duration_frames:48}),event({id:'orphan',segment_id:'gone'}));
  const before=structuredClone(p),placed=placedSounds(p.timeline);
  assert.deepEqual(placed.map(e=>[e.id,e.start_frame,e.tail_frames]),[['e1',72,0],['tail',108,36]]);
  p.timeline.segments[0].start_frame+=10;
  assert.deepEqual(placedSounds(p.timeline).map(e=>e.start_frame),[82,118]);assert.deepEqual(before.timeline.sound_events,p.timeline.sound_events);
  assert.deepEqual(placedSounds({segments:[]}),[]);assert.deepEqual(Object.keys(soundLanes),['voice','sfx','ambience']);
});

test('sound schedule uses cut-preview time and clamps buffer segment end mute and half-open range',()=>{
  const e={...event(),start_frame:72};
  assert.deepEqual(soundSchedule(e,24,0,2),{delay:3,offset:0,duration:2,gain:.4});
  assert.deepEqual(soundSchedule(e,24,3.5,2),{delay:0,offset:.5,duration:1.5,gain:.4});
  assert.deepEqual(soundSchedule(e,24,3.5,1),{delay:0,offset:.5,duration:.5,gain:.4});
  assert.deepEqual(soundSchedule(e,24,3.5,2,4),{delay:0,offset:.5,duration:.5,gain:.4});
  for(const args of [[e,24,5,2],[e,24,6,2],[e,24,3.5,.5],[e,24,0,2,3],
    [{...e,muted:true},24,0,2],[{...e,gain:0},24,0,2]])assert.equal(soundSchedule(...args),null);
});

test('sound tail extends full project but chosen segment keeps window and excludes other segment events',()=>{
  const p=project();p.timeline.sound_events=[event({offset_frames:60,duration_frames:168}),event({id:'other',segment_id:'s2',offset_frames:0})];
  const before=structuredClone(p),scoped=segmentPlayback(p,'s1');
  assert.equal(timelineDuration(p.timeline),11.5);assert.deepEqual(scoped.range,{id:'s1',name:'Selected',start:2,end:5});
  assert.deepEqual(scoped.project.timeline.sound_events.map(e=>e.id),['e1']);
  const placed=placedSounds(scoped.project.timeline)[0];
  assert.deepEqual(soundSchedule(placed,24,2,7,scoped.range.end),{delay:2.5,offset:0,duration:.5,gain:.4});
  assert.deepEqual(p,before);
  assert.notEqual(criticalSignature(p),criticalSignature({...p,timeline:{...p.timeline,sound_events:[event({stream:0})]}}));
});

function playerSetup(t){
  const env={p:project(),sources:[],gains:[],fetches:[],decodeStarted:deferred(),decodeResult:null,decodeCount:0,
    rafs:new Map(),next:1,now:1000,errors:[],states:[],fetchOk:true};
  const restores=[];const global=(name,value)=>{const old=Object.getOwnPropertyDescriptor(globalThis,name);restores.push(()=>old?Object.defineProperty(globalThis,name,old):delete globalThis[name]);Object.defineProperty(globalThis,name,{configurable:true,value});};
  class Context{
    constructor(){this.currentTime=10;this.state='suspended';this.destination={};env.ctx=this;}
    async resume(){this.state='running';}
    getOutputTimestamp(){return {contextTime:this.currentTime,performanceTime:env.now};}
    decodeAudioData(){env.decodeCount++;env.decodeStarted.resolve();return env.decodeResult??Promise.resolve({duration:2});}
    createGain(){const node={gain:{value:1},connect(){},disconnect(){this.disconnected=true;}};env.gains.push(node);return node;}
    createBufferSource(){const node={starts:[],stops:0,connect(){},disconnect(){this.disconnected=true;},start(...args){this.starts.push(args);},stop(){this.stops++;}};env.sources.push(node);return node;}
  }
  global('AudioContext',Context);global('performance',{now:()=>env.now});
  global('requestAnimationFrame',callback=>{const id=env.next++;env.rafs.set(id,callback);return id;});global('cancelAnimationFrame',id=>env.rafs.delete(id));
  global('fetch',async url=>{env.fetches.push(url);return {ok:env.fetchOk,arrayBuffer:async()=>new ArrayBuffer(1)};});
  env.player=new MontagePlayer({getProject:()=>env.p,onState:s=>{env.states.push(s);if(s.state==='playing')env.nextPlaying?.resolve(s);},onError:e=>env.errors.push(e)});
  env.player.paint=()=>{};
  t.after(()=>{env.player.pause();for(const restore of restores.reverse())restore();});return env;
}

test('player schedules selected cut and seek uses local offset while pause stops all event nodes',async t=>{
  const env=playerSetup(t),p=env.player;
  await p.play();assert.equal(env.fetches.length,1);assert.match(env.fetches[0],/sound-preview\?event=e1&v=7/);
  assert.deepEqual(env.sources[0].starts[0],[p.anchor+3,0,2]);assert.equal(env.gains[0].gain.value,.4);
  env.nextPlaying=deferred();p.seek(3.5);await env.nextPlaying.promise;
  assert.equal(env.sources[0].stops,1);assert.deepEqual(env.sources[1].starts[0],[p.anchor,.5,1.5]);assert.equal(env.fetches.length,1);
  p.pause();assert.equal(env.sources[1].stops,1);assert.equal(env.sources[1].disconnected,true);assert.deepEqual(p.soundNodes,[]);assert.equal(env.rafs.size,0);
});

test('player selected segment clips sound tail and end replay schedules audio again without decoding twice',async t=>{
  const env=playerSetup(t),p=env.player;env.p.timeline.sound_events[0]={...event(),offset_frames:60,duration_frames:168};
  env.decodeResult=Promise.resolve({duration:7});p.selectSegment('s1');await p.play();
  assert.deepEqual(env.sources[0].starts[0],[p.anchor+2.5,0,.5]);
  p.pause();p.position=p.end();await p.play();
  assert.equal(p.position,2);assert.equal(env.sources.length,2);assert.deepEqual(env.sources[1].starts[0],[p.anchor+2.5,0,.5]);assert.equal(env.decodeCount,1);
});

test('pause during sound decode or project switch cannot schedule stale buffers',async t=>{
  const env=playerSetup(t),p=env.player,gate=deferred();env.decodeResult=gate.promise;
  const first=p.play();await env.decodeStarted.promise;p.pause();gate.resolve({duration:2});await first;
  assert.equal(p.playing,false);assert.equal(env.sources.length,0);assert.equal(env.rafs.size,0);
  const secondGate=deferred();env.decodeStarted=deferred();env.decodeResult=secondGate.promise;env.p.timeline.sound_events[0].stream=0;
  const second=p.play();await env.decodeStarted.promise;env.p={...env.p,id:'p2'};p.syncProject();
  secondGate.resolve({duration:2});await second;
  assert.equal(p.projectId,'p2');assert.equal(env.sources.length,0);assert.equal(p.playing,false);
  env.decodeResult=null;await p.play();
  assert.equal(env.sources.length,1);assert.match(env.fetches.at(-1),/projects\/p2\/sound-preview/);assert.deepEqual(env.errors,[]);
});

test('sound buffer cache separates stream cut and changed source stamp and evicts failed preview',async t=>{
  const env=playerSetup(t),p=env.player;p.context();
  await p.soundBuffers();await p.soundBuffers();assert.equal(env.fetches.length,1);
  env.p.timeline.sound_events[0].gain=.2;env.p.timeline.sound_events[0].offset_frames=36;
  await p.soundBuffers();assert.equal(env.fetches.length,1);
  env.p.timeline.sound_events[0].stream=0;await p.soundBuffers();assert.equal(env.fetches.length,2);
  env.p.timeline.sound_events[0].source_in_frame=0;await p.soundBuffers();assert.equal(env.fetches.length,3);
  env.p.assets.a.bytes++;env.fetchOk=false;await assert.rejects(p.soundBuffers(),/подготовить звук/);
  env.fetchOk=true;await p.soundBuffers();assert.equal(env.fetches.length,5);
});

test('waveform SVG selects source interval rather than rendering whole waveform',()=>{
  assert.equal(waveformSvg(null,event(),24),'');
  const svg=waveformSvg({peaks_per_second:4,peaks:[.1,.2,.9,.1]}, {source_in_frame:6,duration_frames:12},24);
  const paths=[...svg.matchAll(/M(\d+) ([\d.]+)V([\d.]+)/g)];
  assert.equal(paths.length,120);close(Number(paths[0][2]),16.2);close(Number(paths.at(-1)[2]),2.9);assert.match(svg,/aria-label="Звуковая волна"/);
});

function deskSetup(t){
  const env={p:project(),root:null,calls:[],pending:[],waiters:[],closed:0,paused:0};
  const old=Object.getOwnPropertyDescriptor(globalThis,'document');Object.defineProperty(globalThis,'document',{configurable:true,value:{querySelector:()=>env.root}});
  t.after(()=>old?Object.defineProperty(globalThis,'document',old):delete globalThis.document);
  env.flush=async()=>{};env.waitCall=n=>{if(env.calls.length>=n)return Promise.resolve();const gate=deferred();env.waiters.push([n,gate]);return gate.promise;};
  env.desk=new SoundDesk({getProject:()=>env.p,flush:()=>env.flush(),pause:()=>env.paused++,closeModal:()=>{env.root=null;env.closed++;},onProject:p=>env.p=p,
    modal:()=>{
      const elements={};for(const [name,value] of Object.entries({name:'',lane:'voice',asset:'',stream:'',offset:'0',source:'0',duration:'1',gain:'100',muted:'',file:''}))elements[name]={value,checked:false,files:[]};
      const nodes=new Map();env.root={elements,isConnected:true,closest:()=>({open:true}),querySelector:s=>{if(!nodes.has(s))nodes.set(s,{textContent:'',innerHTML:'',disabled:false});return nodes.get(s);},querySelectorAll:()=>['submit','remove','upload'].map(s=>env.root.querySelector(s))};
    },api:(...args)=>{env.calls.push(args);const gate=deferred();env.pending.push(gate);for(const [n,w] of env.waiters)if(n<=env.calls.length)w.resolve();return gate.promise;}});
  return env;
}

test('SoundDesk form converts seconds stream gain and keeps draft on conflict with pending guard',async t=>{
  const env=deskSetup(t);await env.desk.open('s1');const form=env.root,v=form.elements;assert.equal(env.paused,1);assert.deepEqual(env.calls,[]);
  for(const [key,value] of Object.entries({name:'Radio',lane:'sfx',asset:'a',stream:'1',offset:'2',source:'.25',duration:'.5',gain:'30'}))v[key].value=value;
  const first=form.onsubmit({preventDefault(){}});await env.waitCall(1);await form.onsubmit({preventDefault(){}});
  assert.deepEqual(env.calls[0],['/api/projects/p1/timeline','POST',{expected_revision:7,actor:'author',action:'add_sound',payload:{segment_id:'s1',name:'Radio',lane:'sfx',asset_id:'a',stream:1,offset_frames:48,source_in_frame:6,duration_frames:12,gain:.3,muted:false}}]);
  assert.equal(env.calls.length,1);env.pending[0].reject(Object.assign(new Error('Revision conflict'),{status:409}));await first;
  assert.equal(env.root,form);assert.equal(v.source.value,'.25');assert.equal(form.querySelector('#soundError').textContent,'Revision conflict');assert.equal(env.closed,0);
  const gate=deferred();env.flush=()=>gate.promise;const switched=form.onsubmit({preventDefault(){}});env.p={...env.p,id:'p2'};gate.resolve();await switched;
  assert.equal(env.calls.length,1);assert.equal(env.closed,0);
});

test('SoundDesk wave cache separates stream and media stamp and retries after failed request',async t=>{
  const env=deskSetup(t),first=env.desk.wave('a',0),same=env.desk.wave('a',0);
  assert.equal(env.calls.length,1);assert.deepEqual(env.calls[0],['/api/projects/p1/waveform?asset=a&stream=0']);
  env.pending[0].resolve({peaks:[.1]});assert.deepEqual(await first,await same);
  const second=env.desk.wave('a',1);env.pending[1].reject(new Error('decode'));await assert.rejects(second,/decode/);
  const retry=env.desk.wave('a',1);env.pending[2].resolve({peaks:[.5]});await retry;
  env.p.assets.a.modified_ns++;const changed=env.desk.wave('a',1);env.pending[3].resolve({peaks:[.8]});await changed;
  assert.equal(env.calls.length,4);
});
