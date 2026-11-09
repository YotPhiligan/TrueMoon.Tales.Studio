import {timelineOf,frameEnd} from './timeline-logic.js';
import {scenarioView} from './scenario-logic.js';
import {clipSpeed,sourceOut} from './clip-edit-logic.js';
import {placedSounds} from './sound-logic.js';
import {timelineJoins,transitionWindows} from './transitions-logic.js';

export function segmentPlayback(project,id){
  const view=scenarioView(project),segment=view.segments.find(s=>s.id===id);
  if(!segment)throw new Error('Сегмент для просмотра не найден.');
  const issues=view.issues.filter(i=>i.segment_id===id);
  if(issues.length)throw new Error('Проверьте сегмент: '+issues.map(i=>i.message).join(' '));
  const start=segment.start_frame/project.fps,end=(segment.start_frame+segment.duration_frames)/project.fps;
  if(!Number.isFinite(start)||!Number.isFinite(end)||end<=start)throw new Error('Для просмотра нужно задать окно сегмента.');
  const t=view.active?view.timeline:timelineOf(project);
  const clips=t.clips.filter(c=>c.segment_id===id),clipIds=new Set(clips.map(c=>c.id));
  return {project:{...project,timeline:{...t,segments:t.segments.filter(s=>s.id===id),clips,...(t.sound_events?{sound_events:t.sound_events.filter(e=>e.clip_id?clipIds.has(e.clip_id):e.segment_id===id)}:{}),...(t.transitions?.length?{transition_windows:transitionWindows(t)}:{}),...(t.titles?{title_signature:t.titles,titles:t.titles.filter(r=>r.start_frame<end*t.fps&&r.start_frame+r.duration_frames>start*t.fps)}:{})}},
    range:{id,name:segment.name,start,end},items:segment.items};
}

export function animaticFrame(item,block){
  const first=block?.guides?.find(guide=>guide.frame===0);
  return item?.animatic_asset_id??block?.references?.find(ref=>ref.id===first?.reference_id)?.asset_id??null;
}

export function joinPlayback(project,id){
  const view=scenarioView(project);
  if(view.issues.length)throw new Error('Исправьте неопределённое время и конфликты сценария перед просмотром стыка.');
  const t=view.active?view.timeline:timelineOf(project),join=timelineJoins(t).find(j=>j.id===id);
  if(!join)throw new Error('Стык для просмотра не найден.');
  const {left,right}=join,fps=t.fps;
  const transition=transitionWindows(t).find(window=>window.right_clip_id===right.id);
  const start=Math.max(left.start_frame,(transition?.out_start_frame??frameEnd(left))-fps)/fps;
  const end=Math.min(frameEnd(right),(transition?.in_end_frame??right.start_frame)+fps)/fps;
  const name=clip=>clip.story_title??project.blocks.find(b=>b.id===clip.block_id)?.label??'Блок';
  return {project:{...project,timeline:t},range:{id,kind:'join',name:name(left)+' → '+name(right),start,end}};
}

export function rangePlayback(project,selection){
  const view=scenarioView(project);
  const timeline=view.active?view.timeline:timelineOf(project),fps=timeline.fps;
  const limit=Math.round(timelineDuration(timeline)*fps);
  const startFrame=selection.startFrame,endFrame=Math.min(selection.endFrame,limit);
  if(!Number.isInteger(startFrame)||!Number.isInteger(selection.endFrame)||startFrame<0||endFrame<=startFrame)
    throw new Error('Выберите диапазон хотя бы в один кадр.');
  const intersecting=new Set(view.segments.filter(segment=>segment.start_frame!=null&&segment.start_frame<endFrame&&
    (segment.duration_frames==null||frameEnd(segment)>startFrame)).map(segment=>segment.id));
  for(const clip of timeline.clips){
    if(clip.start_frame<endFrame&&frameEnd(clip)>startFrame)intersecting.add(clip.segment_id);
  }
  if(view.issues.some(issue=>intersecting.has(issue.segment_id)))
    throw new Error('Исправьте неопределённое время или конфликт сценария в выбранном диапазоне.');
  const range={id:'selection',kind:'range',name:'Выбранный диапазон',start:startFrame/fps,end:endFrame/fps};
  return {project:{...project,timeline},range};
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
