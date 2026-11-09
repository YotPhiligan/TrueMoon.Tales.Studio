export const soundLanes={voice:'Реплики',sfx:'Эффекты',ambience:'Атмосфера'};
export function placeSound(event,owner){
  if(!owner)return null;
  const available=owner.duration_frames-event.offset_frames;
  const duration=event.clip_id?Math.min(event.duration_frames,available):event.duration_frames;
  return duration>0?{...event,segment_id:event.clip_id?owner.segment_id:event.segment_id,start_frame:owner.start_frame+event.offset_frames,
    duration_frames:duration,tail_frames:event.clip_id?0:Math.max(0,event.duration_frames-available)}:null;
}
export function placedSounds(t){
  const segments=new Map(t.segments.map(s=>[s.id,s])),clips=new Map((t.clips??[]).map(c=>[c.id,c]));
  return (t.sound_events??[]).map(event=>placeSound(event,event.clip_id?clips.get(event.clip_id):segments.get(event.segment_id))).filter(Boolean);
}
export function soundDragPatch(event,owner,edge,frame,fps){
  const placed=placeSound(event,owner);
  if(!placed)return {};
  const start=placed.start_frame,end=start+placed.duration_frames;
  const ownerEnd=event.clip_id?owner.start_frame+owner.duration_frames:518400;
  const clamp=(value,min,max)=>Math.max(min,Math.min(max,value));
  if(edge==='left'){
    const next=clamp(frame,Math.max(owner.start_frame,start-event.source_in_frame),end-1);
    const delta=next-start;
    return delta?{offset_frames:next-owner.start_frame,source_in_frame:event.source_in_frame+delta,duration_frames:event.duration_frames-delta}:{};
  }
  if(edge==='right'){
    const capacity=Math.floor(event.source_seconds*fps+1e-6);
    const limit=Number.isFinite(capacity)?start+capacity-event.source_in_frame:end;
    const next=clamp(frame,start+1,Math.min(ownerEnd,limit));
    return next!==end?{duration_frames:next-start}:{};
  }
  const next=clamp(frame,owner.start_frame,ownerEnd-placed.duration_frames);
  return next!==start?{offset_frames:next-owner.start_frame}:{};
}
export function soundSchedule(e,fps,position,bufferDuration,end=Infinity){
  const start=e.start_frame/fps,offset=Math.max(0,position-start),duration=Math.min(e.duration_frames/fps-offset,bufferDuration-offset,end-Math.max(start,position));
  return duration>1e-7&&!e.muted&&e.gain>0?{delay:Math.max(0,start-position),offset,duration,gain:e.gain}:null;
}
