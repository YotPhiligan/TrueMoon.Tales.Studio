// Visual-only envelopes on the existing project frame grid.
export function timelineJoins(t){
  const clips=t.clips.toSorted((a,b)=>a.start_frame-b.start_frame||a.id.localeCompare(b.id));
  return clips.slice(1).map((right,i)=>{const left=clips[i];return {id:right.id,left,right,start_frame:right.start_frame,gap_frames:right.start_frame-left.start_frame-left.duration_frames,
    cross_segment:left.segment_id!==right.segment_id,transition:(t.transitions??[]).find(r=>r.left_clip_id===left.id&&r.right_clip_id===right.id)??null};});
}
export function transitionWindows(t){
  if(t.transition_windows)return t.transition_windows;
  return timelineJoins(t).filter(j=>j.transition).map(j=>{const r=j.transition,start=j.right.start_frame,black=start-r.pause_frames;return {...r,gap_frames:j.gap_frames,
    out_start_frame:black-r.fade_out_frames,black_start_frame:black,right_start_frame:start,in_end_frame:start+r.fade_in_frames};}).toSorted((a,b)=>a.out_start_frame-b.out_start_frame);
}
export function transitionLevel(t,frame){
  const index=Math.floor(frame+1e-7);
  for(const w of transitionWindows(t)){
    if(index>=w.out_start_frame&&index<w.black_start_frame)return (w.black_start_frame-index-.5)/w.fade_out_frames;
    if(index>=w.black_start_frame&&index<w.right_start_frame)return 0;
    if(index>=w.right_start_frame&&index<w.in_end_frame)return (index-w.right_start_frame+.5)/w.fade_in_frames;
  }
  return 1;
}
export function transitionIssue(t){
  const rows=t.transitions===undefined?[]:t.transitions;if(!Array.isArray(rows)||rows.length>2000)return 'Некорректный список переходов.';
  const joins=timelineJoins({...t,transitions:[]}),seen=new Set();
  for(const row of rows){
    if(!row||typeof row!=='object'||Array.isArray(row))return 'Некорректные поля перехода.';
    const j=joins.find(j=>j.left.id===row.left_clip_id&&j.right.id===row.right_clip_id);
    if(!j)return 'Переход должен соединять соседние блоки.';
    if(seen.has(row.right_clip_id))return 'На одном стыке может быть только один переход.';seen.add(row.right_clip_id);
    if(['fade_out_frames','pause_frames','fade_in_frames'].some(k=>!Number.isInteger(row[k])||row[k]<0||row[k]>518400))return 'Длительности перехода должны быть неотрицательными целыми кадрами.';
    if(row.pause_frames<j.gap_frames)return 'Пауза короче существующего промежутка.';
    if(row.fade_out_frames+row.pause_frames-j.gap_frames>j.left.duration_frames)return 'Затемнение и пауза не помещаются в предыдущий блок.';
    if(row.fade_in_frames>j.right.duration_frames)return 'Появление не помещается в следующий блок.';
  }
  const wins=transitionWindows({...t,transition_windows:undefined});
  for(const a of wins)for(const b of wins)if(a.right_clip_id===b.left_clip_id&&a.in_end_frame>b.out_start_frame)return 'Появление и следующий переход перекрываются в одном блоке.';
  return '';
}
