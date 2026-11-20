import test from 'node:test';
import assert from 'node:assert/strict';
import {TimelineView} from '../static/timeline.js';

function view(){
  const calls={api:[],projects:[],errors:[],renders:0};let project={id:'alpha',revision:4};
  const v=Object.create(TimelineView.prototype);
  Object.assign(v,{pending:false,root:{classList:{add(){}}},project:()=>project,
    beforeWrite:async()=>{},api:async(...args)=>{calls.api.push(args);return {...project,revision:5};},
    onProject:p=>{calls.projects.push(p);project=p;},onError:e=>calls.errors.push(e),render:()=>calls.renders++});
  return {v,calls,current:()=>project,switchTo:p=>{project=p;}};
}

test('block save conflict keeps its unsaved draft and resolution UI without timeline refresh',async()=>{
  const {v,calls,current}=view(),draft=current();
  v.beforeWrite=async()=>{throw Object.assign(new Error('block conflict'),{status:409});};
  await v.write('move_segment',{id:'one',start_frame:24});
  assert.equal(current(),draft);assert.deepEqual(calls.api,[]);assert.deepEqual(calls.projects,[]);
  assert.deepEqual(calls.errors,[]);assert.equal(v.pending,false);assert.equal(calls.renders,1);
});

test('timeline conflict preserves the unapplied command and loads newer state once without retry',async()=>{
  const {v,calls,current}=view(),stored=new Map(),previous=globalThis.sessionStorage;
  globalThis.sessionStorage={setItem:(k,value)=>stored.set(k,value)};
  v.api=async(path,method,...args)=>{
    calls.api.push([path,method,...args]);
    if(method==='POST')throw Object.assign(new Error('timeline conflict'),{status:409});
    return {id:'alpha',revision:8};
  };
  try{
    await v.write('move_segment',{id:'one',start_frame:24});
    assert.equal(calls.api.length,2);assert.equal(calls.api[0][1],'POST');assert.equal(calls.api[1][1],undefined);
    assert.equal(current().revision,8);assert.equal(calls.projects.length,1);
    assert.deepEqual(JSON.parse(stored.get('studio-timeline-conflict-alpha')),{action:'move_segment',payload:{id:'one',start_frame:24}});
    assert.equal(calls.errors.length,1);assert.match(calls.errors[0].message,/не применён/);assert.equal(v.pending,false);
  }finally{if(previous===undefined)delete globalThis.sessionStorage;else globalThis.sessionStorage=previous;}
});

test('late add response cannot select an old project item after switching projects',async()=>{
  const {v,calls,current,switchTo}=view();let finish;
  v.api=()=>new Promise(resolve=>{finish=resolve;});
  const operation=v.write('add_clip',{block_id:'old'});await Promise.resolve();
  switchTo({id:'beta',revision:1});v.selection={kind:'segment',id:'beta-segment'};
  finish({id:'alpha',revision:5,timeline:{clips:[{id:'old-clip',start_frame:0,duration_frames:24}]}});
  await operation;
  assert.equal(current().id,'beta');assert.deepEqual(v.selection,{kind:'segment',id:'beta-segment'});
  assert.deepEqual(calls.projects,[]);assert.deepEqual(calls.errors,[]);assert.equal(v.pending,false);
});

test('navigation takes strict neighbours in either direction and accepts a marker at frame zero',()=>{
  const {v}=view();
  v.navigationFrames={join:[24,48,72],marker:[0,24,24,96]};
  v.cursor=24;
  assert.equal(v.adjacentFrame('marker',-1),0);
  assert.equal(v.adjacentFrame('marker',1),96);
  assert.equal(v.adjacentFrame('join',-1),undefined);
  assert.equal(v.adjacentFrame('join',1),48);
  v.cursor=96;
  assert.equal(v.adjacentFrame('marker',1),undefined);
});

test('fit segment uses explicit timeline selection, then open segment, then cursor',()=>{
  const {v}=view(),a={id:'a',start_frame:0,duration_frames:24},b={id:'b',start_frame:36,duration_frames:60};
  const timeline={segments:[a,b],clips:[{id:'clip',segment_id:'b'}]};
  v.cursor=12;
  assert.equal(v.segmentToFit(timeline),a);
  v.getActiveSegment=()=>b.id;
  assert.equal(v.segmentToFit(timeline),b);
  v.selection={kind:'segment',id:a.id};
  assert.equal(v.segmentToFit(timeline),a);
  v.selection={kind:'clip',id:'clip'};
  assert.equal(v.segmentToFit(timeline),b);
  v.selection=null;
  v.getActiveSegment=()=>null;
  v.cursor=24;
  assert.equal(v.segmentToFit(timeline),undefined);
});
