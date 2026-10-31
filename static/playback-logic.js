import {timelineOf,frameEnd} from './timeline-logic.js';
import {scenarioView} from './scenario-logic.js';
import {clipSpeed,sourceOut} from './clip-edit-logic.js';
import {placedSounds} from './sound-logic.js';
import {transitionWindows} from './transitions-logic.js';

export function segmentPlayback(project,id){
  const view=scenarioView(project),segment=view.segments.find(s=>s.id===id);
  if(!segment)throw new Error('Сегмент для просмотра не найден.');
  const issues=view.issues.filter(i=>i.segment_id===id);
  if(issues.length)throw new Error('Проверьте сегмент: '+issues.map(i=>i.message).join(' '));
  const start=segment.start_frame/project.fps,end=(segment.start_frame+segment.duration_frames)/project.fps;
  if(!Number.isFinite(start)||!Number.isFinite(end)||end<=start)throw new Error('Для просмотра нужно задать окно сегмента.');
  const t=view.active?view.timeline:timelineOf(project);
  return {project:{...project,timeline:{...t,segments:t.segments.filter(s=>s.id===id),clips:t.clips.filter(c=>c.segment_id===id),...(t.sound_events?{sound_events:t.sound_events.filter(e=>e.segment_id===id)}:{}),...(t.transitions?.length?{transition_windows:transitionWindows(t)}:{}),...(t.titles?{title_signature:t.titles,titles:t.titles.filter(r=>r.start_frame<end*t.fps&&r.start_frame+r.duration_frames>start*t.fps)}:{})}},
    range:{id,name:segment.name,start,end}};
}

export function timelineDuration(t){return Math.max(0,...t.segments.map(frameEnd),...t.clips.map(frameEnd),...placedSounds(t).map(frameEnd),...(t.titles??[]).map(frameEnd),t.music?frameEnd(t.music):0)/t.fps;}
export function placementAt(project,seconds){
  const t=timelineOf(project),frame=Math.max(0,seconds)*t.fps;
  const clip=t.clips.find(c=>frame+1e-7>=c.start_frame&&frame<frameEnd(c)-1e-7);
  if(!clip)return {kind:'gap',clip:null,source_seconds:0};
  const block=project.blocks.find(b=>b.id===clip.block_id),take=block?.takes.find(x=>x.id===clip.take_id);
  const rate=clipSpeed(clip),source=(clip.source_in_frame+(frame-clip.start_frame)*rate)/t.fps,end=sourceOut(clip)/t.fps;
  return {kind:clip.take_id?(take?'video':'missing'):'placeholder',clip,block,take,
    source_seconds:Math.min(source,Math.max(clip.source_in_frame/t.fps,end-1e-6)),...('speed' in clip||'source_out_frame' in clip?{source_finished:source>=end,rate}:{})};
}
export function musicSchedule(t,position,bufferDuration,playbackEnd=Infinity){
  const m=t.music;if(!m)return null;
  const start=m.start_frame/t.fps,end=frameEnd(m)/t.fps,offset=m.source_in_frame/t.fps+Math.max(0,position-start);
  const duration=Math.min(end-Math.max(position,start),bufferDuration-offset,playbackEnd-Math.max(position,start));
  return duration>1e-7?{delay:Math.max(0,start-position),offset,duration,gain:m.muted?0:m.gain}:null;
}
export function audibleContextTime(ctx,performanceMs){
  let estimate=NaN;
  try{
    const stamp=ctx.getOutputTimestamp?.();
    if(stamp&&Number.isFinite(stamp.contextTime)&&stamp.performanceTime>0&&performanceMs>=stamp.performanceTime)
      estimate=stamp.contextTime+(performanceMs-stamp.performanceTime)/1000;
  }catch{}
  if(!Number.isFinite(estimate)||Math.abs(estimate-ctx.currentTime)>2)estimate=ctx.currentTime-(ctx.baseLatency||0)-(ctx.outputLatency||0);
  return Math.max(0,Math.min(ctx.currentTime,estimate));
}
export function criticalSignature(project){
  const t=timelineOf(project),m=t.music;
  return JSON.stringify([project.id,t.fps,t.segments.map(s=>[s.start_frame,s.duration_frames]),t.clips,
    m?[m.asset_id,m.start_frame,m.duration_frames,m.source_in_frame]:null,...(t.sound_events?[t.sound_events]:[]),...(t.transitions?[t.transitions,t.transition_windows]:[]),...(t.titles?[t.title_signature??t.titles,project.output_size]:[])]);
}
