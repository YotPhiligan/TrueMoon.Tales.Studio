// Timeline positions use the project's frame grid; retimed source positions can be fractional.
import {clipSpeed,sourceOut,sourcePatch,trimRetimed} from './clip-edit-logic.js';
import {placedSounds} from './sound-logic.js';
export const frameEnd = item => item.start_frame + item.duration_frames;
export const blankTimeline = (fps=24) => ({version:1,fps,segments:[],clips:[],markers:[],music:null,clip_audio:false,undo:[],redo:[]});
export const timelineOf = project => project?.timeline ?? blankTimeline(project?.fps??24);
export const toFrame = (seconds,fps=24) => Math.round(Number(seconds)*fps);
export function frameCode(frame,fps=24){
  const n=Math.max(0,Math.round(frame)),whole=Math.floor(n/fps);
  return `${String(Math.floor(whole/60)).padStart(2,'0')}:${String(whole%60).padStart(2,'0')}:${String(n%fps).padStart(2,'0')}`;
}
export function takeCapacity(take,fps=24){
  const info=take?.video_info??{};
  if(typeof info!=='object'||Array.isArray(info))return 0;
  const valid=n=>typeof n==='number'&&Number.isFinite(n)&&n>0;
  const seconds=info.seconds??(valid(info.frames)&&valid(info.fps)?info.frames/info.fps:NaN);
  return Number.isFinite(seconds)&&seconds>0?Math.floor(seconds*fps+1e-6):0;
}
export function extentFrames(t){return Math.min(518400+4*t.fps,Math.max(20*t.fps,...t.segments.map(frameEnd).map(n=>n+4*t.fps),...(t.clips??[]).map(frameEnd).map(n=>n+4*t.fps),...placedSounds(t).map(frameEnd).map(n=>n+4*t.fps),...(t.titles??[]).map(frameEnd).map(n=>n+4*t.fps),...t.markers.map(m=>m.frame+4*t.fps),t.music?frameEnd(t.music)+4*t.fps:0));}
export function rulerStep(zoom,fps=24){return Math.round(([.1,.25,.5,1,2,5,10,15,30,60,120,300].find(s=>s*zoom>=65)??600)*fps);}
export function snapPosition(raw,duration,targets,threshold,edge='move'){
  const frame=Math.max(0,Math.round(raw));
  let best={frame,target:null,offset:0},distance=threshold+1e-8;
  for(const target of targets){
    const position=typeof target==='number'?target:target.frame;
    for(const offset of edge==='move'?[0,duration]:[0]){
      const candidate=position-offset,d=Math.abs(candidate-frame);
      if(candidate>=0&&d<distance){best={frame:Math.round(candidate),target,offset};distance=d;}
    }
  }
  return best;
}
export function snapFrame(...args){return snapPosition(...args).frame;}
export function trimClip(clip,edge,frame,capacity=Infinity){
  if(clip.take_id&&('speed' in clip||'source_out_frame' in clip))return trimRetimed(clip,edge,frame,capacity);
  if(edge==='left'){
    const delta=Math.max(clip.take_id?-clip.source_in_frame:-clip.start_frame,Math.min(clip.duration_frames-1,Math.round(frame)-clip.start_frame));
    return {start_frame:clip.start_frame+delta,duration_frames:clip.duration_frames-delta,source_in_frame:clip.take_id?clip.source_in_frame+delta:0};
  }
  return {duration_frames:Math.max(1,Math.min(Math.round(frame)-clip.start_frame,capacity-clip.source_in_frame))};
}
export function placementError(project,t,kind,id,patch){
  const segments=t.segments.map(s=>({...s})),clips=t.clips.map(c=>({...c}));
  if(kind==='segment'){
    const s=segments.find(s=>s.id===id);if(!s)return 'Сегмент не найден.';
    const delta=patch.start_frame-s.start_frame;Object.assign(s,patch);
    for(const c of clips)if(c.segment_id===id)c.start_frame+=delta;
  }else if(kind==='segment-resize'){
    const s=segments.find(s=>s.id===id);if(!s)return 'Сегмент не найден.';Object.assign(s,patch);
  }else if(kind==='clip'){
    const c=clips.find(c=>c.id===id);if(!c)return 'Клип не найден.';Object.assign(c,patch);
  }
  for(const s of segments)if(s.start_frame<0||s.duration_frames<1)return 'Сегмент должен иметь положительную длину.';
  const ordered=segments.toSorted((a,b)=>a.start_frame-b.start_frame);
  if(ordered.some((s,i)=>i&&frameEnd(ordered[i-1])>s.start_frame))return 'Сегменты пересекаются.';
  for(const c of clips){
    const s=segments.find(s=>s.id===c.segment_id);
    if(!s||c.start_frame<s.start_frame||frameEnd(c)>frameEnd(s))return 'Клип выходит за границы сегмента.';
    if(c.source_in_frame<0||c.duration_frames<1)return 'Недопустимый участок дубля.';
    if(c.take_id){
      const take=project.blocks.find(b=>b.id===c.block_id)?.takes.find(x=>x.id===c.take_id);
      try{const patch=sourcePatch(c,c.source_in_frame,sourceOut(c),clipSpeed(c),takeCapacity(take,t.fps));if(patch.duration_frames!==c.duration_frames)return 'Участок и монтажная длина не согласованы.';}catch(e){return e.message;}
    }
    if(c.anchor_frame!=null&&c.anchor_frame!==c.start_frame)return 'Начало блока закреплено.';
  }
  const sequence=clips.toSorted((a,b)=>a.start_frame-b.start_frame);
  if(sequence.some((c,i)=>i&&frameEnd(sequence[i-1])>c.start_frame))return 'Клипы пересекаются.';
  return '';
}
