export const clipSpeed=c=>c.speed??1;
export const sourceOut=c=>c.source_out_frame??c.source_in_frame+c.duration_frames*clipSpeed(c);
export const sourceFrame=(seconds,fps)=>Math.round(Number(seconds)*fps*1e6)/1e6;
export const sourceDuration=(start,end,rate)=>Math.ceil((end-start)/rate-1e-8);
export const editFields=c=>Object.fromEntries(['speed','source_out_frame','anchor_frame','gap_protected','mute_native_audio'].filter(k=>k in c).map(k=>[k,c[k]]));
export function sourcePatch(c,start,end,rate,capacity=Infinity){
  if(!c.take_id)throw new Error('Сначала назначьте видео.');
  if(typeof end==='number'&&Number.isFinite(end)&&end>capacity+1e-7)throw new Error('Участок выходит за длину дубля.');
  if(![start,end,rate].every(n=>typeof n==='number'&&Number.isFinite(n))||start<0||end>518400||end>capacity+1e-7||end<=start||rate<.25||rate>4)throw new Error('Проверьте участок источника и скорость 0,25×–4×.');
  const duration=sourceDuration(start,end,rate);if(duration<1||duration>518400)throw new Error('Недопустимая монтажная длина.');
  return {source_in_frame:start,source_out_frame:end,speed:rate,duration_frames:duration};
}
export function trimRetimed(c,edge,frame,capacity=Infinity){
  const rate=clipSpeed(c),end=sourceOut(c),oldEnd=c.start_frame+c.duration_frames;
  if(edge==='left'){
    const delta=Math.max(-c.source_in_frame/rate,Math.min(c.duration_frames-1,Math.round(frame)-c.start_frame));
    const start=Math.max(0,c.source_in_frame+delta*rate),patch=sourcePatch(c,start,end,rate,capacity);
    return {...patch,start_frame:oldEnd-patch.duration_frames};
  }
  const n=Math.max(1,Math.round(frame)-c.start_frame),requested=Math.min(capacity,c.source_in_frame+n*rate);
  return sourcePatch(c,c.source_in_frame,Math.round(frame)===oldEnd?end:requested,rate,capacity);
}
