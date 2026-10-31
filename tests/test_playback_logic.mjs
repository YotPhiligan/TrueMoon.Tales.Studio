import test from 'node:test';
import assert from 'node:assert/strict';
import {timelineDuration,placementAt,musicSchedule,audibleContextTime,criticalSignature} from '../static/playback-logic.js';

function project(){
  return {id:'p1',revision:1,blocks:[{id:'b1',label:'Shot',takes:[{id:'take',video_asset_id:'video'}]}],assets:{},
    timeline:{fps:24,segments:[{id:'s',start_frame:0,duration_frames:96}],markers:[],clip_audio:false,
      clips:[{id:'c1',segment_id:'s',block_id:'b1',take_id:'take',start_frame:24,duration_frames:24,source_in_frame:12},
        {id:'c2',segment_id:'s',block_id:'b1',take_id:null,start_frame:48,duration_frames:24,source_in_frame:0},
        {id:'c3',segment_id:'s',block_id:'b1',take_id:'missing',start_frame:72,duration_frames:24,source_in_frame:0}],music:null}};
}
const close=(actual,expected)=>assert.ok(Math.abs(actual-expected)<1e-9,`${actual} != ${expected}`);

test('montage duration includes segment clip and music ends but excludes markers',()=>{
  assert.equal(timelineDuration({fps:24,segments:[],clips:[],markers:[{frame:9999}],music:null}),0);
  const p=project();
  assert.equal(timelineDuration(p.timeline),4);
  p.timeline.music={start_frame:240,duration_frames:48,muted:true};
  assert.equal(timelineDuration(p.timeline),12);
  p.timeline.clips[2].start_frame=288;
  assert.equal(timelineDuration(p.timeline),13);
});

test('placement uses half open clip windows with source offset gap placeholder and missing take',()=>{
  const p=project();
  assert.equal(placementAt(p,-1).kind,'gap');
  assert.equal(placementAt(p,23/24).kind,'gap');
  let placed=placementAt(p,1);
  assert.equal(placed.kind,'video');assert.equal(placed.clip.id,'c1');assert.equal(placed.take.video_asset_id,'video');
  assert.equal(placed.source_seconds,.5);
  assert.equal(placementAt(p,1.5).source_seconds,1);
  assert.equal(placementAt(p,2).kind,'placeholder');
  assert.equal(placementAt(p,3).kind,'missing');
  assert.equal(placementAt(p,4).kind,'gap');
});

test('music schedule accounts for delayed start source in seek trim and decoded buffer end',()=>{
  const t={fps:24,music:{asset_id:'song',start_frame:48,duration_frames:96,source_in_frame:12,gain:.8,muted:false}};
  assert.deepEqual(musicSchedule(t,0,10),{delay:2,offset:.5,duration:4,gain:.8});
  assert.deepEqual(musicSchedule(t,2,10),{delay:0,offset:.5,duration:4,gain:.8});
  assert.deepEqual(musicSchedule(t,3,10),{delay:0,offset:1.5,duration:3,gain:.8});
  assert.deepEqual(musicSchedule(t,3,2),{delay:0,offset:1.5,duration:.5,gain:.8});
  assert.equal(musicSchedule(t,6,10),null);
  assert.equal(musicSchedule(t,7,10),null);
  assert.equal(musicSchedule(t,3,1.5),null);
  assert.equal(musicSchedule({...t,music:null},0,10),null);
  assert.equal(musicSchedule({...t,music:{...t.music,muted:true}},0,10).gain,0);
});

test('audible clock prefers output timestamp compensates latency and clamps invalid estimates',()=>{
  const ctx={currentTime:10.5,baseLatency:.02,outputLatency:.03,getOutputTimestamp:()=>({contextTime:9.8,performanceTime:1000})};
  close(audibleContextTime(ctx,1100),9.9);
  close(audibleContextTime(ctx,900),10.45);
  close(audibleContextTime({...ctx,getOutputTimestamp:()=>({contextTime:1,performanceTime:1000})},1100),10.45);
  close(audibleContextTime({...ctx,getOutputTimestamp:()=>({contextTime:10.6,performanceTime:1000})},1100),10.5);
  close(audibleContextTime({...ctx,getOutputTimestamp:()=>{throw new Error('unsupported');}},1100),10.45);
  assert.equal(audibleContextTime({currentTime:.01,baseLatency:.02,outputLatency:.03},1100),0);
});

test('critical signature ignores markers volume mute clip audio and notes but detects placements and project',()=>{
  const p=project();p.timeline.music={asset_id:'song',start_frame:0,duration_frames:48,source_in_frame:0,gain:.8,muted:false};
  const initial=criticalSignature(p),irrelevant=structuredClone(p);
  irrelevant.revision=99;irrelevant.blocks[0].notes='Author note';irrelevant.timeline.markers=[{frame:50,name:'Beat'}];
  irrelevant.timeline.music.gain=.1;irrelevant.timeline.music.muted=true;irrelevant.timeline.clip_audio=true;
  assert.equal(criticalSignature(irrelevant),initial);
  for(const change of [q=>q.id='p2',q=>q.timeline.fps=30,q=>q.timeline.segments[0].duration_frames=120,
    q=>q.timeline.clips[0].source_in_frame=13,q=>q.timeline.clips[0].take_id='another',q=>q.timeline.music.start_frame=24,
    q=>q.timeline.music.source_in_frame=12,q=>q.timeline.music.asset_id='other']){
    const changed=structuredClone(p);change(changed);assert.notEqual(criticalSignature(changed),initial);
  }
});
