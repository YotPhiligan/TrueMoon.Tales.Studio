import test from 'node:test';
import assert from 'node:assert/strict';
import {clipSpeed,sourceOut,sourceFrame,sourceDuration,editFields,sourcePatch,trimRetimed} from '../static/clip-edit-logic.js';
import {placementAt,criticalSignature} from '../static/playback-logic.js';
import {scenarioView} from '../static/scenario-logic.js';
import {MontagePlayer} from '../static/montage-player.js';
import {ClipEditor} from '../static/clip-editor.js';
import {AudioTimelineView} from '../static/audio-timeline.js';

test('split availability follows the live cursor and respects both boundaries and pending state',t=>{
  const prior=Object.getOwnPropertyDescriptor(globalThis,'localStorage');
  Object.defineProperty(globalThis,'localStorage',{configurable:true,value:{setItem(){}}});
  t.after(()=>prior?Object.defineProperty(globalThis,'localStorage',prior):delete globalThis.localStorage);
  const p=project(),button={disabled:true},view=new AudioTimelineView({getProject:()=>p,root:{querySelector:s=>s==='#tlSplit'?button:null}});
  view.selection={kind:'clip',id:'c1'};
  view.advance(1,false);assert.equal(button.disabled,true,'at first frame');
  view.advance(25/24,true);assert.equal(button.disabled,false,'inside clip without a property form rerender');
  view.pending=true;view.advance(26/24,true);assert.equal(button.disabled,true,'pending write');
  view.pending=false;view.advance(31/24,false);assert.equal(button.disabled,true,'at exclusive end');
  view.selection=null;view.advance(25/24,false);assert.equal(button.disabled,true,'no selected clip');
});

const close=(actual,expected)=>assert.ok(Math.abs(actual-expected)<1e-8,`${actual} != ${expected}`);
const retimed=()=>({id:'c1',segment_id:'s1',block_id:'b1',take_id:'take',start_frame:24,
  duration_frames:7,source_in_frame:2.25,source_out_frame:10.375,speed:1.25});
function project(){return {id:'p1',revision:1,fps:24,assets:{},
  blocks:[{id:'b1',label:'Source',title:'Source',references:[],guides:[],takes:[{id:'take',video_asset_id:'video',video_info:{seconds:4}}]}],
  timeline:{version:1,fps:24,segments:[{id:'s1',name:'Test',start_frame:0,duration_frames:96,color:'mint'}],
    clips:[retimed()],markers:[],music:null,clip_audio:false,undo:[],redo:[]}};}
function setup(t){
  const env={p:project(),errors:[],messages:[]};
  const p=env.player=new MontagePlayer({getProject:()=>env.p,media:id=>'/media/'+id,onError:e=>env.errors.push(e)});
  p.syncProject();p.container={dataset:{}};p.badge={};p.image={hidden:true};p.message={hidden:true};
  p.showMessage=(...args)=>env.messages.push(args);
  p.videos=[0,1].map(()=>({dataset:{},paused:true,hidden:true,readyState:4,duration:4,seeking:false,plays:0,loads:0,writes:[],
    value:0,get currentTime(){return this.value;},set currentTime(v){this.value=v;this.writes.push(v);},
    play(){this.paused=false;this.plays++;return Promise.resolve();},pause(){this.paused=true;},load(){this.loads++;},addEventListener(){}}));
  p.ctx={state:'running',currentTime:10,getOutputTimestamp(){return {contextTime:10,performanceTime:performance.now()};}};
  p.anchor=0;t.after(()=>p.pause());return env;
}

test('source helpers preserve legacy defaults and exact fractional intervals without mutating input',()=>{
  const c=retimed(),before=structuredClone(c);
  assert.equal(clipSpeed({}),1);assert.equal(sourceOut({source_in_frame:12,duration_frames:24}),36);
  assert.equal(sourceOut(c),10.375);assert.equal(sourceFrame(1/3,24),8);
  assert.equal(sourceFrame(.100000001,24),2.4);
  assert.equal(sourceDuration(2.25,10.375,1.25),7);
  assert.equal(sourceDuration(0,1.0000000001,1),1);
  assert.equal(sourceDuration(0,1.000001,1),2);
  assert.deepEqual(editFields({...c,anchor_frame:24,gap_protected:true,notes:'ignored'}),
    {speed:1.25,source_out_frame:10.375,anchor_frame:24,gap_protected:true});
  assert.deepEqual(c,before);
});

test('source patch pins speed endpoints capacity and rounding and refuses invalid edits',()=>{
  const c=retimed(),before=structuredClone(c);
  assert.deepEqual(sourcePatch(c,1.25,6,1.5,6),{source_in_frame:1.25,source_out_frame:6,speed:1.5,duration_frames:4});
  assert.equal(sourcePatch(c,0,24,.25,24).duration_frames,96);
  assert.equal(sourcePatch(c,0,24,4,24).duration_frames,6);
  for(const [start,end,rate,cap] of [[0,6.001,1,6],[2,2,1,6],[3,2,1,6],[-.1,3,1,6],
    [0,3,.249999,6],[0,3,4.000001,6],[0,Infinity,1,6],[NaN,3,1,6],[0,3,true,6],[0,3,'1',6]]){
    assert.throws(()=>sourcePatch(c,start,end,rate,cap),Error);
  }
  assert.throws(()=>sourcePatch({...c,take_id:null},0,12,1,24),/видео/);
  assert.deepEqual(c,before);
});

test('retimed left trim keeps exclusive out and fixed montage end with bounded source start',()=>{
  const c=retimed(),before=structuredClone(c);
  assert.deepEqual(trimRetimed(c,'left',25,96),{source_in_frame:3.5,source_out_frame:10.375,speed:1.25,duration_frames:6,start_frame:25});
  assert.deepEqual(trimRetimed(c,'left',-100,96),{source_in_frame:0,source_out_frame:10.375,speed:1.25,duration_frames:9,start_frame:22});
  const minimum=trimRetimed(c,'left',100,96);
  assert.equal(minimum.duration_frames,1);assert.equal(minimum.start_frame,30);assert.equal(minimum.source_in_frame,9.75);
  assert.deepEqual(c,before);
});

test('retimed right trim preserves fractional last frame at identity and clamps extension to capacity',()=>{
  const c=retimed();
  assert.deepEqual(trimRetimed(c,'right',31,96),{source_in_frame:2.25,source_out_frame:10.375,speed:1.25,duration_frames:7});
  assert.deepEqual(trimRetimed(c,'right',33,12),{source_in_frame:2.25,source_out_frame:12,speed:1.25,duration_frames:8});
  assert.deepEqual(trimRetimed(c,'right',0,96),{source_in_frame:2.25,source_out_frame:3.5,speed:1.25,duration_frames:1});
});

test('scenario preview carries retime and locked gap then exposes anchor conflict without mutating actual montage',()=>{
  const p=project(),c=retimed(),second={id:'c2',block_id:'b1',take_id:null,title:'Locked',description:'',duration_frames:24,
    gap_before_frames:0,source_in_frame:0,anchor_frame:48,gap_protected:true};
  p.timeline.scenario={version:1,segments:[{...p.timeline.segments[0],description:'',items:[
    {...c,title:'Source',description:'',gap_before_frames:24},second]}]};
  const original=structuredClone(p),view=scenarioView(p);
  assert.deepEqual(view.issues,[]);assert.deepEqual(view.segments[0].items.map(i=>i.start_frame),[24,48]);
  assert.equal(view.segments[0].remaining_frames,24);
  assert.deepEqual(editFields(view.timeline.clips[0]),{speed:1.25,source_out_frame:10.375});
  assert.deepEqual(editFields(view.timeline.clips[1]),{anchor_frame:48,gap_protected:true});
  assert.deepEqual(p,original);
  p.timeline.scenario.segments[0].items[0]={...p.timeline.scenario.segments[0].items[0],duration_frames:25,source_out_frame:33.5};
  const conflict=scenarioView(p);
  assert.deepEqual(conflict.issues.map(i=>[i.code,i.item_id]),[['anchor_overlap','c2']]);
  assert.deepEqual(p.timeline.clips,original.timeline.clips);
});

test('placement maps montage time through speed and holds the fractional tail until half-open cut',()=>{
  const p=project();
  close(placementAt(p,1).source_seconds,2.25/24);
  const interior=placementAt(p,27/24);
  assert.equal(interior.kind,'video');assert.equal(interior.rate,1.25);assert.equal(interior.source_finished,false);
  close(interior.source_seconds,6/24);
  const tail=placementAt(p,30.75/24);
  assert.equal(tail.kind,'video');assert.equal(tail.source_finished,true);close(tail.source_seconds,10.375/24-1e-6);
  assert.equal(placementAt(p,31/24).kind,'gap');
  p.timeline.clips[0]={...p.timeline.clips[0],source_in_frame:12,duration_frames:24};
  delete p.timeline.clips[0].speed;delete p.timeline.clips[0].source_out_frame;
  const legacy=placementAt(p,1.5);
  assert.equal(legacy.source_seconds,1);assert.equal('rate' in legacy,false);assert.equal('source_finished' in legacy,false);
});

test('retime and source out changes invalidate playback signature while caption markers do not',()=>{
  const p=project(),signature=criticalSignature(p),changed=structuredClone(p);
  changed.timeline.clips[0].speed=2;assert.notEqual(criticalSignature(changed),signature);
  changed.timeline.clips[0].speed=1.25;changed.timeline.clips[0].source_out_frame=10;
  assert.notEqual(criticalSignature(changed),signature);
  changed.timeline.clips[0].source_out_frame=10.375;changed.timeline.markers.push({id:'m1',frame:27,name:'beat'});
  assert.equal(criticalSignature(changed),signature);
});

test('player paused retime scrub sets source time rate and pitch and resets legacy video to one',t=>{
  const env=setup(t),p=env.player;
  p.paint(27/24);const first=p.active;
  close(first.currentTime,6/24);assert.equal(first.playbackRate,1.25);assert.equal(first.preservesPitch,true);
  assert.equal(first.paused,true);assert.equal(first.plays,0);assert.equal(p.container.dataset.sourceTime,'0.250000');
  env.p.timeline.clips.push({id:'c2',segment_id:'s1',block_id:'b1',take_id:'take',start_frame:31,duration_frames:24,source_in_frame:24});
  p.paint(31/24);
  assert.equal(p.active.playbackRate,1);assert.equal(p.active.currentTime,1);assert.equal(first.paused,true);
  assert.deepEqual(env.errors,[]);
});

test('player holds source tail paused during remaining retimed montage frame then renders gap',t=>{
  const env=setup(t),p=env.player;p.playing=true;
  p.paint(30.75/24);const v=p.active;
  assert.equal(v.playbackRate,1.25);assert.equal(v.paused,true);assert.equal(v.plays,0);
  close(v.currentTime,10.375/24-1e-6);
  assert.equal(p.container.dataset.kind,'video');assert.equal(p.container.dataset.clip,'c1');
  p.paint(31/24);
  assert.equal(p.active,null);assert.equal(p.container.dataset.kind,'gap');assert.equal(v.hidden,true);
});

test('running retimed video keeps decoder for small drift and sync pauses when rate changes',t=>{
  const env=setup(t),p=env.player;
  p.paint(1);const video=p.active;video.currentTime=6/24-.1;video.writes=[];
  p.playing=true;p.paint(27/24);
  assert.equal(video.plays,1);assert.deepEqual(video.writes,[]);assert.equal(video.playbackRate,1.25);
  env.p.timeline.clips[0].speed=2;
  p.syncProject();
  assert.equal(p.playing,false);assert.equal(video.paused,true);
});

function deferred(){let resolve,reject;const promise=new Promise((yes,no)=>{resolve=yes;reject=no;});return {promise,resolve,reject};}
function clipEditorSetup(t){
  const env={p:project(),form:null,modals:0,closed:0,calls:[],pending:[]};
  const originalDocument=Object.getOwnPropertyDescriptor(globalThis,'document');
  Object.defineProperty(globalThis,'document',{configurable:true,value:{querySelector:()=>env.form}});
  t.after(()=>originalDocument?Object.defineProperty(globalThis,'document',originalDocument):delete globalThis.document);
  env.flush=async()=>{};
  env.editor=new ClipEditor({getProject:()=>env.p,getClip:id=>env.p.timeline.clips.find(c=>c.id===id),flush:()=>env.flush(),
    modal:(_title,html)=>{
      env.modals++;
      const field=(name,value)=>({name,value,disabled:false});
      const elements={operation:field('operation','range'),preset:field('preset',''),slip:field('slip','0')};
      for(const name of ['source_in','source_out','speed'])elements[name]=field(name,html.match(new RegExp('name="'+name+'"[^>]*value="([^"]*)"'))[1]);
      env.form={elements,summary:{textContent:''},error:{textContent:''},slip:{hidden:true},button:{disabled:false},
        querySelector(selector){return selector==='#clipSourceSummary'?this.summary:selector==='#clipSourceError'?this.error:this.slip;},
        querySelectorAll(){return [...Object.values(elements),this.button];}};
    },closeModal:()=>{env.closed++;env.form=null;},
    write:(...args)=>{env.calls.push(args);const gate=deferred();env.pending.push(gate);return gate.promise;}});
  return env;
}
const submitSource=form=>form.onsubmit({preventDefault(){}});

test('clip editor writes source or slip with captured revision and suppresses pending duplicate submit',async t=>{
  const env=clipEditorSetup(t),before=structuredClone(env.p);
  await env.editor.open(retimed());const range=env.form;
  assert.equal(range.elements.source_in.value,'0.09375');assert.equal(range.elements.source_out.value,'0.432291667');
  range.elements.source_in.value='.5';range.elements.source_out.value='1.75';range.elements.preset.value='2';
  range.elements.preset.onchange();assert.equal(range.elements.speed.value,'2');assert.match(range.summary.textContent,/15 кадр/);
  const first=submitSource(range);await submitSource(range);
  assert.equal(env.calls.length,1);assert.equal(range.querySelectorAll().every(c=>c.disabled),true);
  assert.deepEqual(env.calls[0],['edit_clip_source',{id:'c1',source_in_frame:12,source_out_frame:42,speed:2},{expectedRevision:1,throwErrors:true}]);
  env.pending[0].resolve(env.p);await first;assert.equal(env.closed,1);assert.deepEqual(env.p,before);
  env.p.revision=9;await env.editor.open(retimed());const slip=env.form;
  slip.elements.operation.value='slip';slip.elements.slip.value='.125';slip.onchange();
  assert.equal(slip.slip.hidden,false);assert.equal(slip.elements.source_in.disabled,true);assert.equal(slip.elements.speed.disabled,true);
  const second=submitSource(slip);
  assert.deepEqual(env.calls[1],['slip_clip',{id:'c1',delta_frames:3},{expectedRevision:9,throwErrors:true}]);
  env.pending[1].resolve(env.p);await second;assert.equal(env.closed,2);
});

test('clip editor keeps invalid and conflict drafts editable and retries against original revision explicitly',async t=>{
  const env=clipEditorSetup(t);await env.editor.open(retimed());const form=env.form;
  form.elements.speed.value='.2';form.oninput();assert.equal(form.summary.textContent,'');assert.match(form.error.textContent,/0,25/);
  await submitSource(form);assert.deepEqual(env.calls,[]);assert.equal(form.elements.speed.value,'.2');assert.equal(env.closed,0);
  form.elements.source_in.value='.5';form.elements.source_out.value='1.75';form.elements.speed.value='2';
  const failed=submitSource(form);env.pending[0].reject(Object.assign(new Error('Revision conflict; keep draft'),{status:409}));await failed;
  assert.equal(form.error.textContent,'Revision conflict; keep draft');assert.equal(env.form,form);assert.equal(env.closed,0);
  assert.deepEqual(['.5','1.75','2'],['source_in','source_out','speed'].map(name=>form.elements[name].value));
  assert.equal(form.elements.source_in.disabled,false);assert.equal(form.elements.slip.disabled,true);assert.equal(form.button.disabled,false);
  env.p.revision=2;const retry=submitSource(form);
  assert.deepEqual(env.calls[1],env.calls[0]);assert.equal(env.calls[1][2].expectedRevision,1);
  env.pending[1].resolve(env.p);await retry;assert.equal(env.closed,1);
});

test('clip editor rejects project changes during flush and late success never closes replacement form or project',async t=>{
  const env=clipEditorSetup(t),gate=deferred();env.flush=()=>gate.promise;
  const opening=env.editor.open(retimed());env.p={...env.p,id:'p2'};gate.resolve();await opening;
  assert.equal(env.modals,0);assert.deepEqual(env.calls,[]);
  env.p=project();env.flush=async()=>{};await env.editor.open(retimed());const old=env.form;
  const oldWrite=submitSource(old);await env.editor.open(retimed());const replacement=env.form;
  env.pending[0].resolve(env.p);await oldWrite;
  assert.equal(env.form,replacement);assert.equal(env.closed,0);
  const switched=submitSource(replacement);env.p={...env.p,id:'p2'};
  env.pending[1].resolve({...env.p,id:'p1'});await switched;
  assert.equal(env.form,replacement);assert.equal(env.closed,0);assert.equal(env.p.id,'p2');
  await submitSource(replacement);
  assert.equal(env.calls.length,2);assert.match(replacement.error.textContent,/Проект изменился/);assert.equal(replacement.button.disabled,false);
});
