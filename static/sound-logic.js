export const soundLanes={voice:'Реплики',sfx:'Эффекты',ambience:'Атмосфера'};
export function placedSounds(t){return (t.sound_events??[]).flatMap(e=>{const s=t.segments.find(s=>s.id===e.segment_id);return s?[{...e,start_frame:s.start_frame+e.offset_frames,tail_frames:Math.max(0,e.offset_frames+e.duration_frames-s.duration_frames)}]:[];});}
export function soundSchedule(e,fps,position,bufferDuration,end=Infinity){
  const start=e.start_frame/fps,offset=Math.max(0,position-start),duration=Math.min(e.duration_frames/fps-offset,bufferDuration-offset,end-Math.max(start,position));
  return duration>1e-7&&!e.muted&&e.gain>0?{delay:Math.max(0,start-position),offset,duration,gain:e.gain}:null;
}
