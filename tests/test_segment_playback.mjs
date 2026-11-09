import test from 'node:test';
import assert from 'node:assert/strict';
import {segmentPlayback,rangePlayback,musicSchedule,placementAt} from '../static/playback-logic.js';
import {segmentProject} from './segment_fixtures.mjs';

test('segment preview uses valid selected planned clips despite foreign issues and keeps global music',()=>{
  const p=segmentProject(true),before=structuredClone(p),result=segmentPlayback(p,'s1');
  assert.deepEqual(result.range,{id:'s1',name:'Chosen',start:2,end:5});
  assert.deepEqual(result.project.timeline.segments.map(s=>s.id),['s1']);
  assert.deepEqual(result.project.timeline.clips.map(c=>[c.id,c.start_frame,c.duration_frames,c.source_in_frame]),[['c1',60,48,24]]);
  assert.deepEqual(result.project.timeline.music,p.timeline.music);
  assert.equal(placementAt(result.project,8).kind,'gap');
  assert.deepEqual(p,before);
});

test('segment preview supports legacy and empty windows without importing other clips',()=>{
  const p=segmentProject(),legacy=segmentPlayback(p,'s1');
  assert.deepEqual(legacy.range,{id:'s1',name:'Chosen',start:2,end:5});
  assert.deepEqual(legacy.project.timeline.clips.map(c=>[c.id,c.duration_frames]),[['c1',24]]);
  p.timeline.clips=p.timeline.clips.filter(c=>c.segment_id!=='s1');
  const empty=segmentPlayback(p,'s1');
  assert.deepEqual(empty.project.timeline.clips,[]);assert.equal(empty.range.end-empty.range.start,3);
  assert.deepEqual(empty.project.timeline.music,p.timeline.music);
});

test('segment preview rejects missing or selected invalid window duration source and overflow',()=>{
  assert.throws(()=>segmentPlayback(segmentProject(),'missing'),Error);
  for(const change of [s=>s.start_frame=null,s=>s.duration_frames=null,s=>s.items[0].duration_frames=null,
    s=>s.items[0].duration_frames=61,s=>s.items[0].source_in_frame=49]){
    const p=segmentProject(true);change(p.timeline.scenario.segments[0]);
    assert.throws(()=>segmentPlayback(p,'s1'),Error);
  }
  const p=segmentProject(true);p.blocks[0].takes[0].video_info={};
  assert.throws(()=>segmentPlayback(p,'s1'),Error);
});

test('music scheduling clamps to preview end while keeping absolute source offset and buffer bound',()=>{
  const t={fps:24,music:{start_frame:48,duration_frames:144,source_in_frame:24,gain:.8,muted:false}};
  assert.deepEqual(musicSchedule(t,0,20,5),{delay:2,offset:1,duration:3,gain:.8});
  assert.deepEqual(musicSchedule(t,4,20,5),{delay:0,offset:3,duration:1,gain:.8});
  assert.deepEqual(musicSchedule(t,4,3.5,5),{delay:0,offset:3,duration:.5,gain:.8});
  assert.equal(musicSchedule(t,0,20,2),null);
  assert.equal(musicSchedule(t,4,20,3),null);
  assert.deepEqual(musicSchedule(t,4,20),{delay:0,offset:3,duration:4,gain:.8});
});

test('arbitrary range keeps the absolute audio clock and isolates unrelated unfinished windows',()=>{
  const p=segmentProject(true),before=structuredClone(p);
  const {project,range}=rangePlayback(p,{startFrame:60,endFrame:90});
  assert.deepEqual([range.kind,range.start,range.end],['range',2.5,3.75]);
  assert.deepEqual(musicSchedule(project.timeline,range.start,20,range.end),{delay:0,offset:3,duration:1.25,gain:.8});
  assert.equal(placementAt(project,range.start).source_seconds,1);
  assert.throws(()=>rangePlayback(p,{startFrame:60,endFrame:60}),/один кадр/);
  assert.throws(()=>rangePlayback(p,{startFrame:192,endFrame:210}),/конфликт сценария/);
  assert.deepEqual(p,before);
});
