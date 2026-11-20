import test from 'node:test';
import assert from 'node:assert/strict';
import {MontagePlayer} from '../static/montage-player.js';
import {segmentProject} from './segment_fixtures.mjs';

const close=(actual,expected)=>assert.ok(Math.abs(actual-expected)<1e-8,`${actual} != ${expected}`);
function deferred(){let resolve,reject;const promise=new Promise((yes,no)=>{resolve=yes;reject=no;});return {promise,resolve,reject};}
function project(id='p1',music=true){return {id,revision:1,blocks:[],assets:{song:{bytes:100,modified_ns:1}},timeline:{fps:24,
  segments:[{id:'s1',start_frame:0,duration_frames:96}],clips:[],markers:[],clip_audio:false,
  music:music?{asset_id:'song',start_frame:48,duration_frames:48,source_in_frame:12,gain:.8,muted:false}:null}};}

function setup(t,{music=true}={}){
  const env={project:project('p1',music),states:[],times:[],errors:[],painted:[],frames:new Map(),fetches:[],sources:[],gains:[],
    now:1000,nextFrame:1,decodeStarted:deferred(),decodeCount:0,fetchOk:true,decodeResult:null};
  const restored=[];
  function global(name,value){const descriptor=Object.getOwnPropertyDescriptor(globalThis,name);restored.push(()=>descriptor?Object.defineProperty(globalThis,name,descriptor):delete globalThis[name]);Object.defineProperty(globalThis,name,{value,configurable:true,writable:true});}
  class Context{
    constructor(){this.currentTime=10;this.baseLatency=0;this.outputLatency=0;this.destination={};this.resumes=0;this.state='suspended';env.ctx=this;}
    async resume(){this.resumes++;this.state='running';}
    getOutputTimestamp(){return {contextTime:this.currentTime,performanceTime:env.now};}
    decodeAudioData(){env.decodeCount++;env.decodeStarted.resolve();return env.decodeResult??Promise.resolve({duration:8});}
    createGain(){const node={gain:{value:1},connect(){},disconnect(){this.disconnected=true;}};env.gains.push(node);return node;}
    createBufferSource(){const node={starts:[],stops:0,connect(){},disconnect(){this.disconnected=true;},start(...args){this.starts.push(args);},stop(){this.stops++;}};env.sources.push(node);return node;}
  }
  global('AudioContext',Context);
  global('performance',{now:()=>env.now});
  global('requestAnimationFrame',callback=>{const id=env.nextFrame++;env.frames.set(id,callback);return id;});
  global('cancelAnimationFrame',id=>env.frames.delete(id));
  global('fetch',async url=>{env.fetches.push(url);return {ok:env.fetchOk,arrayBuffer:async()=>new ArrayBuffer(1)};});
  env.player=new MontagePlayer({getProject:()=>env.project,media:(aid,pid)=>`/media/${pid}/${aid}`,
    onState:value=>{env.states.push(value);if(value.state==='playing')env.nextPlaying?.resolve(value);},
    onTime:(...value)=>env.times.push(value),onError:error=>env.errors.push(error)});
  env.player.paint=position=>env.painted.push(position);
  env.runFrame=()=>{const [id,callback]=env.frames.entries().next().value;env.frames.delete(id);callback();};
  t.after(()=>{env.player.pause();for(const restore of restored.reverse())restore();});
  return env;
}

function attachMedia(env){
  const p=env.player;
  env.project.blocks=[{id:'b1',label:'Shot',takes:[{id:'take1',video_asset_id:'video1'},{id:'take2',video_asset_id:'video2'}],references:[],guides:[]}];
  env.project.timeline.clips=[{id:'c1',segment_id:'s1',block_id:'b1',take_id:'take1',start_frame:0,duration_frames:24,source_in_frame:12},
    {id:'c2',segment_id:'s1',block_id:'b1',take_id:'take2',start_frame:48,duration_frames:24,source_in_frame:24}];
  env.messages=[];
  p.container={dataset:{}};p.badge={};p.image={hidden:true};p.message={hidden:true};
  p.showMessage=(title,detail)=>env.messages.push([title,detail]);
  p.videos=[0,1].map(()=>({dataset:{},paused:true,hidden:true,readyState:4,duration:10,currentTime:0,seeking:false,plays:0,loads:0,
    play(){this.plays++;this.paused=false;return this.playPromise??Promise.resolve();},pause(){this.paused=true;},
    load(){this.loads++;},addEventListener(){}}));
  p.paint=MontagePlayer.prototype.paint.bind(p);
}

test('master clock advances before delayed music and pause freezes position and stops source',async t=>{
  const env=setup(t),p=env.player;
  await p.play();
  assert.equal(p.playing,true);assert.equal(p.preparing,false);assert.equal(env.sources.length,1);
  const [when,offset,duration]=env.sources[0].starts[0];
  close(when-p.anchor,2);close(offset,.5);close(duration,2);
  env.ctx.currentTime=p.anchor+.5;
  close(p.time(),.5);
  p.pause();
  close(p.position,.5);assert.equal(p.playing,false);assert.equal(env.sources[0].stops,1);assert.equal(env.frames.size,0);
  env.ctx.currentTime+=100;
  close(p.time(),.5);
});

test('seek while playing stops prior source and schedules exact new source offset',async t=>{
  const env=setup(t),p=env.player;
  await p.play();
  env.ctx.currentTime=p.anchor+1;
  env.nextPlaying=deferred();
  p.seek(2.5);
  await env.nextPlaying.promise;
  assert.equal(env.sources.length,2);assert.equal(env.sources[0].stops,1);
  const [when,offset,duration]=env.sources[1].starts[0];
  close(when,p.anchor);close(offset,1);close(duration,1.5);
  close(p.position,2.5);assert.equal(p.playing,true);assert.equal(env.fetches.length,1);
});

test('pause during pending decode prevents late source creation or restart',async t=>{
  const env=setup(t),p=env.player,gate=deferred();env.decodeResult=gate.promise;
  const started=p.play();
  await env.decodeStarted.promise;
  assert.equal(p.preparing,true);
  p.pause();gate.resolve({duration:8});await started;
  assert.equal(p.playing,false);assert.equal(p.preparing,false);assert.equal(env.sources.length,0);assert.equal(env.frames.size,0);
  assert.equal(env.states.at(-1).state,'paused');
});

test('seek during pending decode stays paused at new position after response arrives',async t=>{
  const env=setup(t),p=env.player,gate=deferred();env.decodeResult=gate.promise;
  const started=p.play();await env.decodeStarted.promise;
  p.seek(2.5);gate.resolve({duration:8});await started;
  assert.equal(p.playing,false);close(p.position,2.5);assert.equal(env.sources.length,0);
  await p.play();
  assert.equal(env.fetches.length,1);assert.equal(env.decodeCount,1);assert.equal(env.sources.length,1);
  close(env.sources[0].starts[0][1],1);
});

test('project switch ignores previous project decode and fetches new project media',async t=>{
  const env=setup(t),p=env.player,gate=deferred();env.decodeResult=gate.promise;
  const old=p.play();await env.decodeStarted.promise;
  env.project=project('p2');p.syncProject();gate.resolve({duration:8});await old;
  assert.equal(p.projectId,'p2');assert.equal(p.position,0);assert.equal(p.playing,false);assert.equal(env.sources.length,0);
  env.decodeResult=Promise.resolve({duration:8});
  await p.play();
  assert.deepEqual(env.fetches,['/media/p1/song','/media/p2/song']);
  assert.equal(env.sources.length,1);assert.equal(p.playing,true);
});

test('repeated play during preparation and playback creates only one decoded source',async t=>{
  const env=setup(t),p=env.player,gate=deferred();env.decodeResult=gate.promise;
  const first=p.play(),second=p.play();
  await env.decodeStarted.promise;gate.resolve({duration:8});await Promise.all([first,second]);
  await p.play();
  assert.equal(env.fetches.length,1);assert.equal(env.decodeCount,1);assert.equal(env.sources.length,1);
  assert.equal(env.sources[0].starts.length,1);assert.equal(env.frames.size,1);
});

test('silent montage uses master clock ends exactly and stop returns to zero',async t=>{
  const env=setup(t,{music:false}),p=env.player;
  await p.play();assert.equal(env.fetches.length,0);assert.equal(env.sources.length,0);
  env.ctx.currentTime=p.anchor+3;
  close(p.time(),3);
  env.ctx.currentTime=p.anchor+5;env.runFrame();
  assert.equal(p.playing,false);assert.equal(p.position,4);assert.equal(env.states.at(-1).state,'ended');assert.equal(env.frames.size,0);
  p.stop();assert.equal(p.position,0);assert.equal(env.states.at(-1).state,'stopped');assert.equal(env.painted.at(-1),0);
});

test('volume and mute update live while placement edits pause the active source',async t=>{
  const env=setup(t),p=env.player;
  await p.play();
  env.project.timeline.music.gain=.2;p.syncProject();
  assert.equal(p.playing,true);assert.equal(env.gains[0].gain.value,.2);
  env.project.timeline.music.muted=true;p.syncProject();
  assert.equal(p.playing,true);assert.equal(env.gains[0].gain.value,0);assert.equal(env.sources.length,1);
  env.ctx.currentTime=p.anchor+.75;
  env.project.timeline.music.start_frame=24;p.syncProject();
  assert.equal(p.playing,false);assert.equal(env.sources[0].stops,1);close(p.position,.75);
});

test('failed music fetch reports error and a later play retries without rejected cache',async t=>{
  const env=setup(t),p=env.player;env.fetchOk=false;
  await p.play();
  assert.equal(p.playing,false);assert.equal(p.preparing,false);assert.equal(env.states.at(-1).state,'error');
  assert.equal(env.errors.length,1);assert.equal(env.sources.length,0);assert.equal(env.frames.size,0);
  env.fetchOk=true;await p.play();
  assert.equal(env.fetches.length,2);assert.equal(env.sources.length,1);assert.equal(p.playing,true);
});

test('video waits for audible running clock preloads next clip and recovers large drift without seek storms',async t=>{
  const env=setup(t),p=env.player;attachMedia(env);
  await p.play();
  const video=p.active,idle=p.videos.find(v=>v!==video);
  assert.equal(video.plays,0);close(video.currentTime,.5);
  assert.equal(idle.dataset.slot,'c2:video2');assert.equal(idle.plays,0);assert.equal(idle.loads,1);
  env.ctx.currentTime=p.anchor+.1;env.ctx.state='suspended';p.paint(p.time());
  assert.equal(video.plays,0);
  env.ctx.state='running';p.paint(p.time());
  assert.equal(video.plays,1);close(video.currentTime,.5);assert.equal(video.playbackRate,1);assert.equal(video.muted,true);
  video.currentTime=.64;p.paint(p.time());close(video.currentTime,.64);
  video.currentTime=.65;p.paint(p.time());close(video.currentTime,.65);
  video.currentTime=.1;p.paint(p.time());close(video.currentTime,.6);
  video.currentTime=.1;p.paint(p.time());close(video.currentTime,.1);
  env.now+=1001;p.paint(p.time());close(video.currentTime,.6);
  env.project.timeline.clip_audio=true;p.paint(p.time());assert.equal(video.muted,false);
});

test('metadata only video is asked to play so it can buffer instead of waiting forever for canplay',async t=>{
  const env=setup(t),p=env.player;attachMedia(env);await p.play();
  const video=p.active;video.readyState=1;env.ctx.currentTime=p.anchor+.1;
  p.paint(p.time());assert.equal(video.plays,1);assert.equal(video.hidden,true);
  video.readyState=4;p.paint(p.time());assert.equal(video.hidden,false);assert.equal(video.loads,1);
});

test('late video errors are ignored after clip switch pause and slot reuse while current errors are shown',async t=>{
  const env=setup(t),p=env.player;attachMedia(env);
  await p.play();
  const original=p.active,first=deferred();original.playPromise=first.promise;
  env.ctx.currentTime=p.anchor+.1;p.paint(p.time());
  env.ctx.currentTime=p.anchor+2;p.paint(p.time());
  assert.notEqual(p.active,original);
  first.reject(new Error('old clip'));await first.promise.catch(()=>{});
  assert.deepEqual(env.messages,[]);
  const active=p.active,second=deferred();active.playPromise=second.promise;active.paused=true;p.paint(p.time());
  p.pause();second.reject(new Error('paused clip'));await second.promise.catch(()=>{});
  assert.deepEqual(env.messages,[]);
  active.playPromise=Promise.resolve();await p.play();
  const third=deferred();active.playPromise=third.promise;active.paused=true;
  env.ctx.currentTime=p.anchor+.1;p.paint(p.time());
  p.setSource(active,{id:'replacement',source_in_frame:0},{video_asset_id:'replacement-video'});
  third.reject(new Error('reused slot'));await third.promise.catch(()=>{});
  assert.equal(p.active,active);assert.equal(p.playing,true);assert.deepEqual(env.messages,[]);
  for(const video of p.videos)video.playPromise=Promise.resolve();
  p.paint(p.time());
  const current=p.active,fourth=deferred();current.playPromise=fourth.promise;current.paused=true;p.paint(p.time());
  fourth.reject(new Error('current clip'));await fourth.promise.catch(()=>{});
  assert.equal(env.messages.length,1);assert.equal(env.messages[0][0],'Не удалось запустить клип');
});

test('select segment exposes absolute range seek clamps stop and deselect keeps absolute position',t=>{
  const env=setup(t),p=env.player;env.project=segmentProject();
  p.selectSegment('s1');
  assert.deepEqual([p.start(),p.end(),p.position],[2,5,2]);
  assert.deepEqual(p.project().timeline.segments.map(s=>s.id),['s1']);
  assert.equal(env.states.at(-1).duration,3);assert.equal(env.states.at(-1).range.id,'s1');
  p.seek(-100);assert.equal(p.position,2);p.seek(100);assert.equal(p.position,5);
  p.stop();assert.equal(p.position,2);p.seek(4);
  p.selectSegment(null);assert.equal(p.start(),0);assert.equal(p.end(),10);assert.equal(p.position,4);
  p.stop();assert.equal(p.position,0);
});

test('scoped playback uses absolute master clock stops at range end and restarts at range start',async t=>{
  const env=setup(t),p=env.player;env.project=segmentProject();p.selectSegment('s1');
  await p.play();
  close(env.sources[0].starts[0][1],2.5);close(env.sources[0].starts[0][2],3);
  env.ctx.currentTime=p.anchor+.5;close(p.time(),2.5);
  env.ctx.currentTime=p.anchor+4;env.runFrame();
  assert.equal(p.position,5);assert.equal(p.playing,false);assert.equal(env.states.at(-1).state,'ended');
  await p.play();assert.equal(p.position,2);close(env.sources[1].starts[0][1],2.5);close(env.sources[1].starts[0][2],3);
});

test('scoped seek reschedules only remaining range audio and stop returns selected start',async t=>{
  const env=setup(t),p=env.player;env.project=segmentProject();p.selectSegment('s1');await p.play();
  env.nextPlaying=deferred();p.seek(4.5);await env.nextPlaying.promise;
  close(env.sources[1].starts[0][1],5);close(env.sources[1].starts[0][2],.5);
  assert.equal(env.sources[0].stops,1);p.stop();assert.equal(p.position,2);assert.equal(env.sources[1].stops,1);
});

test('changing segment during decode prevents late old-scope start and next play uses new scope',async t=>{
  const env=setup(t),p=env.player;env.project=segmentProject();p.selectSegment('s1');
  const gate=deferred();env.decodeResult=gate.promise;
  const old=p.play();await env.decodeStarted.promise;
  p.selectSegment('s2');gate.resolve({duration:20});await old;
  assert.equal(p.playing,false);assert.equal(env.sources.length,0);assert.equal(p.position,8);
  await p.play();assert.equal(env.fetches.length,1);close(env.sources[0].starts[0][1],8.5);close(env.sources[0].starts[0][2],1);
});

test('deleted or invalid selected scope during sync pauses stops source clears range and reports error',async t=>{
  const env=setup(t),p=env.player;env.project=segmentProject();p.selectSegment('s1');await p.play();
  env.project=structuredClone(env.project);env.project.timeline.segments=env.project.timeline.segments.filter(s=>s.id!=='s1');
  env.project.timeline.clips=env.project.timeline.clips.filter(c=>c.segment_id!=='s1');
  p.syncProject();assert.equal(p.playing,false);assert.equal(p.segmentId,null);assert.equal(p.range,null);
  assert.equal(env.sources[0].stops,1);assert.equal(env.errors.length,1);
  env.project=segmentProject(true);p.syncProject();p.selectSegment('s1');await p.play();
  env.project=structuredClone(env.project);env.project.timeline.scenario.segments[0].items[0].duration_frames=null;
  p.syncProject();assert.equal(p.playing,false);assert.equal(p.segmentId,null);assert.equal(p.range,null);
  assert.equal(env.sources[1].stops,1);assert.equal(env.errors.length,2);
});

test('cached scope refreshes planned source while unrelated invalid segment changes do not stop playback',async t=>{
  const env=setup(t),p=env.player;env.project=segmentProject(true);p.selectSegment('s1');await p.play();
  const cached=p.project();assert.equal(p.project(),cached);assert.equal(cached.timeline.clips[0].duration_frames,48);
  env.project=structuredClone(env.project);env.project.timeline.scenario.segments[1].description='Foreign only';p.syncProject();
  assert.equal(p.playing,true);assert.equal(env.sources.length,1);assert.equal(env.errors.length,0);
  env.ctx.currentTime=p.anchor+.4;env.project=structuredClone(env.project);env.project.timeline.scenario.segments[0].items[0].duration_frames=60;
  p.syncProject();assert.notEqual(p.project(),cached);assert.equal(p.project().timeline.clips[0].duration_frames,60);
  assert.equal(env.project.timeline.clips[0].duration_frames,24);assert.equal(p.playing,false);close(p.position,2.4);
});

test('valid segment can be selected immediately after switching projects without old scope reset',t=>{
  const env=setup(t),p=env.player;env.project=segmentProject();p.selectSegment('s1');
  env.project=segmentProject();env.project.id='p2';p.selectSegment('s2');
  assert.equal(p.projectId,'p2');assert.equal(p.segmentId,'s2');assert.deepEqual([p.start(),p.end(),p.position],[8,9,8]);
  assert.deepEqual(p.project().timeline.segments.map(s=>s.id),['s2']);
});
