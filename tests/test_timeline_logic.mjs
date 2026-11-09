import test from 'node:test';
import assert from 'node:assert/strict';
import {frameEnd,blankTimeline,timelineOf,toFrame,frameCode,takeCapacity,extentFrames,rulerStep,snapFrame,snapPosition,trimClip,placementError} from '../static/timeline-logic.js';

test('project frame conversion and clock preserve frame boundaries',()=>{
  assert.equal(toFrame(1.5,24),36);
  assert.equal(toFrame(1.5,30),45);
  assert.equal(frameCode(23,24),'00:00:23');
  assert.equal(frameCode(24,24),'00:01:00');
  assert.equal(frameCode(1465,24),'01:01:01');
  assert.equal(frameCode(-1,24),'00:00:00');
  assert.equal(frameCode(26,25),'00:01:01');
  assert.equal(frameEnd({start_frame:24,duration_frames:36}),60);
});

test('legacy timeline defaults are independent and use project fps',()=>{
  const first=timelineOf({fps:30});
  assert.deepEqual(first,{version:1,fps:30,segments:[],clips:[],markers:[],music:null,clip_audio:false,undo:[],redo:[]});
  first.markers.push({id:'local',name:'Local',frame:0});
  assert.deepEqual(blankTimeline(30).markers,[]);
  assert.deepEqual(timelineOf({fps:30}).markers,[]);
  const existing={...blankTimeline(24),markers:[{id:'cut',name:'Cut',frame:72}]};
  assert.deepEqual(timelineOf({fps:24,timeline:existing}).markers,[{id:'cut',name:'Cut',frame:72}]);
});

test('source capacity uses seconds or source fps at normal speed and rejects malformed metadata',()=>{
  for(const [video_info,frames] of [
    [{seconds:1,frames:120,fps:24},24],
    [{frames:122,fps:60},48],
    [{seconds:2.9999999999999996},72],
    [{seconds:1.001},24],
  ])assert.equal(takeCapacity({video_info},24),frames);
  for(const video_info of [{},{seconds:0},{seconds:-1},{seconds:NaN},{seconds:Infinity},{seconds:'2'},
    {frames:'60',fps:24},{frames:60,fps:'24'},{frames:true,fps:24},{frames:60,fps:true}]){
    assert.equal(takeCapacity({video_info},24),0,JSON.stringify(video_info));
  }
});

test('timeline extent includes marker and segment margins and minimum empty viewport',()=>{
  assert.equal(extentFrames(blankTimeline(24)),480);
  const t={...blankTimeline(24),segments:[{start_frame:1000,duration_frames:48}],markers:[{frame:1200}]};
  assert.equal(extentFrames(t),1296);
});

test('ruler spacing includes exact threshold and next smaller zoom',()=>{
  assert.equal(rulerStep(130,24),12);
  assert.equal(rulerStep(129.99,24),24);
  assert.equal(rulerStep(0,24),14400);
  assert.equal(rulerStep(650,24),2);
});

test('snap moves leading or trailing edge and respects inclusive threshold',()=>{
  assert.equal(snapFrame(119,24,[120],1),120);
  assert.equal(snapFrame(97,24,[120],1),96);
  assert.equal(snapFrame(116,24,[120],3),116);
  assert.equal(snapFrame(119,24,[120],1,'right'),120);
  assert.equal(snapFrame(-10,24,[0],1),0);
  assert.equal(snapFrame(90.4,24,[],2),90);
});

const clip={id:'c1',segment_id:'s1',block_id:'b1',take_id:'take',start_frame:48,duration_frames:36,source_in_frame:12};
test('left trim preserves source alignment and clip end with one frame minimum',()=>{
  const original=structuredClone(clip);
  assert.deepEqual(trimClip(clip,'left',60,60),{start_frame:60,duration_frames:24,source_in_frame:24});
  assert.deepEqual(trimClip(clip,'left',0,60),{start_frame:36,duration_frames:48,source_in_frame:0});
  assert.deepEqual(trimClip(clip,'left',200,60),{start_frame:83,duration_frames:1,source_in_frame:47});
  assert.deepEqual(trimClip({...clip,take_id:null,source_in_frame:0},'left',-50),{start_frame:0,duration_frames:84,source_in_frame:0});
  assert.deepEqual(clip,original);
});

test('right trim clamps source capacity and minimum duration without changing source in',()=>{
  assert.deepEqual(trimClip(clip,'right',72,60),{duration_frames:24});
  assert.deepEqual(trimClip(clip,'right',200,60),{duration_frames:48});
  assert.deepEqual(trimClip(clip,'right',0,60),{duration_frames:1});
  assert.equal(clip.source_in_frame,12);
});

function placement(){
  const project={blocks:[{id:'b1',takes:[{id:'take',video_info:{seconds:2.5}}]}]};
  const timeline={...blankTimeline(24),
    segments:[{id:'s1',start_frame:0,duration_frames:96},{id:'s2',start_frame:144,duration_frames:48}],
    clips:[{...clip,start_frame:12,duration_frames:24,source_in_frame:0},{...clip,id:'c2',start_frame:60,duration_frames:24,source_in_frame:24}]};
  return {project,timeline};
}

test('placement preview shifts segment group and catches overlap and resize containment',()=>{
  const {project,timeline}=placement(),original=structuredClone(timeline);
  assert.equal(placementError(project,timeline,'segment','s1',{start_frame:48,duration_frames:96}),'');
  assert.match(placementError(project,timeline,'segment','s1',{start_frame:72,duration_frames:96}),/Сегменты пересекаются/);
  assert.match(placementError(project,timeline,'segment-resize','s1',{start_frame:24,duration_frames:72}),/Клип выходит/);
  assert.deepEqual(timeline,original);
});

test('clip placement preview distinguishes touching overlap transfer and source end',()=>{
  const {project,timeline}=placement();
  assert.equal(placementError(project,timeline,'clip','c1',{start_frame:36}),'');
  assert.match(placementError(project,timeline,'clip','c1',{start_frame:37}),/Клипы пересекаются/);
  assert.equal(placementError(project,timeline,'clip','c1',{segment_id:'s2',start_frame:144}),'');
  assert.match(placementError(project,timeline,'clip','c1',{segment_id:'s2'}),/границы сегмента/);
  assert.equal(placementError(project,timeline,'clip','c1',{source_in_frame:59,duration_frames:1}),'');
  assert.match(placementError(project,timeline,'clip','c1',{source_in_frame:60,duration_frames:1}),/длину дубля/);
});

test('placement preview returns errors for stale segment and clip ids',()=>{
  const {project,timeline}=placement();
  for(const kind of ['segment','segment-resize','clip']){
    assert.match(placementError(project,timeline,kind,'missing',{start_frame:24,duration_frames:24}),/не найден/);
  }
});

test('snap result identifies the actual named target and the trailing edge offset',()=>{
  const target={frame:60,label:'Cut accent'};
  assert.deepEqual(snapPosition(34,24,[target],3),{frame:36,target,offset:24});
  assert.deepEqual(snapPosition(34,24,[target],1),{frame:34,target:null,offset:0});
});
