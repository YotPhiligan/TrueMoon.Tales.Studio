import test from 'node:test';
import assert from 'node:assert/strict';
import {scenarioView,windowFromEdit,previewScenarioClip} from '../static/scenario-logic.js';
import {ScenarioDesk} from '../static/scenario-ui.js';

function project(active=false){
  const p={id:'p1',revision:7,fps:24,blocks:[{id:'b1',label:'SRC',title:'Source',takes:[{id:'take',video_info:{seconds:4}}]},{id:'unused',takes:[]}],
    timeline:{version:1,fps:24,segments:[{id:'s1',name:'Scene',description:'',color:'mint',start_frame:24,duration_frames:96}],
      clips:[{id:'c2',segment_id:'s1',block_id:'b1',take_id:'take',start_frame:72,duration_frames:24,source_in_frame:12},
        {id:'c1',segment_id:'s1',block_id:'b1',take_id:'take',start_frame:36,duration_frames:24,source_in_frame:0}],markers:[],music:null,clip_audio:false}};
  if(active)p.timeline.scenario={version:1,segments:[{id:'s1',name:'Scene',description:'',color:'mint',start_frame:24,duration_frames:96,items:[
    {id:'c1',block_id:'b1',title:'First',description:'',duration_frames:24,gap_before_frames:12,take_id:'take',source_in_frame:0},
    {id:'c2',block_id:'b1',title:'Second',description:'',duration_frames:24,gap_before_frames:12,take_id:'take',source_in_frame:12}]}]};
  return p;
}
function deferred(){let resolve,reject;const promise=new Promise((yes,no)=>{resolve=yes;reject=no;});return {promise,resolve,reject};}

test('scenario legacy view adopts only placed clips in time order with explicit gaps without mutation',()=>{
  const p=project(),before=structuredClone(p),view=scenarioView(p);
  assert.equal(view.active,false);assert.deepEqual(view.issues,[]);
  assert.deepEqual(view.segments[0].items.map(i=>[i.id,i.start_frame,i.end_frame,i.gap_before_frames]),[['c1',36,60,12],['c2',72,96,12]]);
  assert.deepEqual([view.segments[0].used_frames,view.segments[0].remaining_frames],[72,24]);
  assert.equal(view.segments[0].items.length,2);assert.deepEqual(p,before);
});

test('known story gaps and duration ripple preserve source ids and fixed segment window',()=>{
  const p=project(true),s=p.timeline.scenario.segments[0];s.items[0].duration_frames=36;s.items[0].gap_before_frames=8;
  const before=structuredClone(p),view=scenarioView(p);
  assert.deepEqual(view.segments[0].items.map(i=>[i.start_frame,i.end_frame]),[[32,68],[80,104]]);
  assert.deepEqual([view.segments[0].used_frames,view.segments[0].remaining_frames],[80,16]);
  assert.deepEqual(view.timeline.clips.map(c=>[c.id,c.take_id,c.source_in_frame,c.duration_frames]),[['c1','take',0,36],['c2','take',12,24]]);
  assert.deepEqual([view.timeline.segments[0].start_frame,view.timeline.segments[0].duration_frames],[24,96]);assert.deepEqual(p,before);
});

test('unknown duration propagates unknown placement and null window remains visibly unplaced',()=>{
  const p=project(true);p.timeline.scenario.segments[0].items[0].duration_frames=null;
  let view=scenarioView(p);
  assert.deepEqual(view.segments[0].items.map(i=>[i.start_frame,i.end_frame]),[[36,null],[null,null]]);
  assert.equal(view.segments[0].used_frames,null);assert.equal(view.segments[0].remaining_frames,null);
  assert.deepEqual(view.issues.map(i=>i.code),['unknown_duration']);
  p.timeline.scenario.segments[0].items[0].duration_frames=24;p.timeline.scenario.segments[0].start_frame=null;
  view=scenarioView(p);assert.deepEqual(view.segments[0].items.map(i=>i.start_frame),[null,null]);
  assert.deepEqual(view.issues.map(i=>i.code),['unplaced_segment']);assert.deepEqual(view.timeline.clips,[]);
  p.timeline.scenario.segments[0].start_frame=24;p.timeline.scenario.segments[0].duration_frames=null;
  view=scenarioView(p);assert.deepEqual(view.issues.map(i=>i.code),['unplaced_segment']);assert.deepEqual(view.timeline.clips,[]);
});

test('overflow source-short and missing metadata are independent visible issues without truncation',()=>{
  const p=project(true);p.timeline.scenario.segments[0].items[1].duration_frames=49;
  let view=scenarioView(p);assert.deepEqual(view.issues.map(i=>i.code),['overflow']);
  assert.equal(view.segments[0].remaining_frames,-1);assert.equal(view.timeline.clips[1].duration_frames,49);
  p.timeline.scenario.segments[0].items[1].duration_frames=24;p.timeline.scenario.segments[0].items[1].source_in_frame=73;
  view=scenarioView(p);assert.deepEqual(view.issues.map(i=>[i.code,i.item_id]),[['source_short','c2']]);
  p.timeline.scenario.segments[0].items[1].source_in_frame=72;assert.deepEqual(scenarioView(p).issues,[]);
  p.blocks[0].takes[0].video_info={};view=scenarioView(p);assert.deepEqual(view.issues.map(i=>i.code),['source_metadata','source_metadata']);
});

test('segment overlap one frame and global range are issues while exact touching is valid',()=>{
  const p=project(true),extra={id:'s2',name:'Next',description:'',color:'blue',start_frame:119,duration_frames:24,items:[]};
  p.timeline.scenario.segments.push(extra);
  assert.deepEqual(scenarioView(p).issues.map(i=>i.code),['segment_overlap']);
  extra.start_frame=120;assert.deepEqual(scenarioView(p).issues,[]);
  extra.start_frame=518377;assert.deepEqual(scenarioView(p).issues.map(i=>i.code),['range']);
  extra.start_frame=518376;assert.deepEqual(scenarioView(p).issues,[]);
});

test('window edit uses last field to derive start end or duration on project frame grid',()=>{
  assert.deepEqual(windowFromEdit({start:'2',duration:'3',end:''},'duration',24),{start:48,duration:72,end:120});
  assert.deepEqual(windowFromEdit({start:'2',duration:'3',end:'7'},'end',24),{start:48,duration:120,end:168});
  assert.deepEqual(windowFromEdit({start:'',duration:'3',end:'7'},'end',24),{start:96,duration:72,end:168});
  assert.deepEqual(windowFromEdit({start:'',duration:'3',end:'7'},'duration',24),{start:96,duration:72,end:168});
  assert.deepEqual(windowFromEdit({start:'',duration:'',end:''},'start',24),{start:null,duration:null,end:null});
  assert.deepEqual(windowFromEdit({start:'.1',duration:'.15',end:''},'duration',24),{start:2,duration:4,end:6});
  assert.deepEqual(windowFromEdit({start:'2',duration:'3',end:'1'},'end',24),{start:48,duration:-24,end:24});
});

function desk(){
  const env={p:{id:'p1',revision:7},calls:[],applied:[],pauses:0,flushed:deferred(),response:deferred(),sent:deferred()};
  env.desk=new ScenarioDesk({project:()=>env.p,flush:()=>env.flushed.promise,pause:()=>env.pauses++,
    api:(...args)=>{env.calls.push(args);env.sent.resolve();return env.response.promise;},onProject:p=>env.applied.push(p)});
  return env;
}

test('scenario write flushes first reads latest revision pauses and applies only returned project',async()=>{
  const env=desk(),payload={id:'s1',name:'New'};
  const pending=env.desk.write('update_segment',payload);assert.equal(env.desk.pending,true);assert.deepEqual(env.calls,[]);
  env.p.revision=8;env.flushed.resolve();await env.sent.promise;
  assert.deepEqual(env.calls,[['/api/projects/p1/scenario','POST',{expected_revision:8,action:'update_segment',payload,actor:'author'}]]);
  assert.equal(env.pauses,1);const updated={id:'p1',revision:9};env.response.resolve(updated);
  assert.equal(await pending,updated);assert.deepEqual(env.applied,[updated]);assert.equal(env.desk.pending,false);
});

test('scenario dialog explicit revision is retained and concurrent writes are rejected',async()=>{
  const env=desk(),pending=env.desk.write('remove_segment',{id:'s1'},5);
  await assert.rejects(env.desk.write('remove_segment',{id:'s2'}),/Дождитесь/);
  assert.equal(env.desk.pending,true);env.p.revision=8;env.flushed.resolve();await env.sent.promise;
  assert.equal(env.calls.length,1);assert.equal(env.calls[0][2].expected_revision,5);
  env.response.resolve({id:'p1',revision:9});await pending;assert.equal(env.desk.pending,false);
});

test('project change during flush aborts before API and late response never applies to new project',async()=>{
  let env=desk(),pending=env.desk.write('update_segment',{id:'s1'});
  env.p={id:'p2',revision:1};env.flushed.resolve();await assert.rejects(pending,/Проект изменился/);
  assert.deepEqual(env.calls,[]);assert.equal(env.pauses,0);assert.equal(env.desk.pending,false);
  env=desk();pending=env.desk.write('update_segment',{id:'s1'});env.flushed.resolve();await env.sent.promise;
  env.p={id:'p2',revision:1};env.response.resolve({id:'p1',revision:8});
  assert.equal(await pending,null);assert.deepEqual(env.applied,[]);assert.equal(env.desk.pending,false);
});

test('scenario write propagates conflict and flush errors and always clears pending flag',async()=>{
  let env=desk(),pending=env.desk.write('update_segment',{id:'s1'}),error=Object.assign(new Error('Conflict'),{status:409});
  env.flushed.resolve();await env.sent.promise;env.response.reject(error);
  await assert.rejects(pending,e=>e===error);assert.deepEqual(env.applied,[]);assert.equal(env.desk.pending,false);
  env=desk();pending=env.desk.write('update_segment',{id:'s1'});env.flushed.reject(new Error('Save failed'));
  await assert.rejects(pending,/Save failed/);assert.deepEqual(env.calls,[]);assert.equal(env.desk.pending,false);
});

test('drag preview projects ripple with protected gaps and reports anchor conflicts without mutation',()=>{
  const p=project(true),second=p.timeline.scenario.segments[0].items[1];
  second.gap_protected=true;
  const view=scenarioView(p),clip=view.timeline.clips[0],before=structuredClone(p);
  const shifted=previewScenarioClip(p,view.segments[0],clip,{duration_frames:30});
  assert.equal(shifted.clips[1].start_frame,78);
  assert.equal(shifted.warning,'');
  assert.deepEqual(p,before);

  second.anchor_frame=78;
  const anchored=scenarioView(p);
  const fits=previewScenarioClip(p,anchored.segments[0],clip,{duration_frames:30});
  assert.equal(fits.clips[1].start_frame,78);
  assert.equal(fits.warning,'');
  const conflicts=previewScenarioClip(p,anchored.segments[0],clip,{duration_frames:54});
  assert.match(conflicts.warning,/закреплённым/);
  assert.match(conflicts.warning,/в окне сегмента/);
});
