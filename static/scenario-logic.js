import {timelineOf,frameEnd,takeCapacity} from './timeline-logic.js';
import {clipSpeed,sourceOut,sourcePatch,editFields} from './clip-edit-logic.js';

export function scenarioView(project){
  const t=timelineOf(project),blocks=project?.blocks??[];
  const story=t.scenario??{version:1,segments:t.segments.map(s=>{
    let cursor=s.start_frame;
    const items=t.clips.filter(c=>c.segment_id===s.id).toSorted((a,b)=>a.start_frame-b.start_frame).map(c=>{
      const b=blocks.find(b=>b.id===c.block_id),gap=c.start_frame-cursor;cursor=frameEnd(c);
      return {...c,title:c.story_title||b?.title||b?.label||'Блок',description:'',gap_before_frames:gap};
    });return {...s,description:'',items};
  })};
  const issues=[],segments=[],projection={...t,segments:[],clips:[]};
  const issue=(code,s,message,id=null)=>issues.push({code,segment_id:s.id,item_id:id,message});
  for(const s of story.segments){
    let cursor=s.start_frame,used=0,unknown=false;const items=[];
    if(s.start_frame==null||s.duration_frames==null)issue('unplaced_segment',s,'Не задано окно сегмента.');
    else{projection.segments.push({...s});if(s.start_frame+s.duration_frames>518400)issue('range',s,'Предел таймлайна превышен.');}
    for(const i of s.items){
      used+=i.gap_before_frames;if(cursor!=null)cursor+=i.gap_before_frames;
      if(i.anchor_frame!=null){
        if(s.start_frame!=null&&i.anchor_frame<s.start_frame)issue('anchor_range',s,'Закреплённое начало раньше сегмента.',i.id);
        if(cursor!=null){if(cursor>i.anchor_frame)issue('anchor_overlap',s,'Не хватает места перед закреплённым блоком «'+i.title+'».',i.id);else{used+=i.anchor_frame-cursor;cursor=i.anchor_frame;}}
        else cursor=i.anchor_frame;
      }
      items.push({...i,start_frame:cursor,end_frame:cursor!=null&&i.duration_frames!=null?cursor+i.duration_frames:null});
      if(i.duration_frames==null){unknown=true;cursor=null;issue('unknown_duration',s,'Не задана длина «'+i.title+'».',i.id);}
      else{
        used+=i.duration_frames;
        if(cursor!=null&&s.duration_frames!=null)projection.clips.push({id:i.id,segment_id:s.id,block_id:i.block_id,take_id:i.take_id,source_in_frame:i.source_in_frame,start_frame:cursor,duration_frames:i.duration_frames,story_title:i.title,...editFields(i)});
        if(i.take_id){const take=blocks.find(b=>b.id===i.block_id)?.takes.find(t=>t.id===i.take_id),cap=takeCapacity(take,t.fps);try{if(!cap)throw new Error('Неизвестна длина выбранного дубля.');const patch=sourcePatch(i,i.source_in_frame,sourceOut(i),clipSpeed(i),cap);if(patch.duration_frames!==i.duration_frames)throw new Error('Участок и длина блока не согласованы.');}catch(e){issue(cap?'source_short':'source_metadata',s,e.message,i.id);}}
        if(cursor!=null)cursor+=i.duration_frames;
      }
    }
    const remaining=s.duration_frames!=null&&!unknown?s.duration_frames-used:null;
    if(remaining!=null&&remaining<0)issue('overflow',s,`Не хватает ${(-remaining/t.fps).toFixed(3)} с в окне сегмента.`);
    segments.push({...s,items,used_frames:unknown?null:used,remaining_frames:remaining});
  }
  const ordered=projection.segments.toSorted((a,b)=>a.start_frame-b.start_frame);
  for(let i=1;i<ordered.length;i++)if(frameEnd(ordered[i-1])>ordered[i].start_frame)issue('segment_overlap',ordered[i],'Окна сегментов пересекаются.');
  return {active:!!t.scenario,segments,issues,timeline:projection};
}

export function windowFromEdit(values,field,fps){
  const frame=v=>v===''||v==null?null:Math.round(Number(v)*fps);
  let start=frame(values.start),duration=frame(values.duration),end=frame(values.end);
  if(field==='end'){if(start!=null&&end!=null)duration=end-start;else if(end!=null&&duration!=null)start=end-duration;else duration=null;}
  else if(start!=null&&duration!=null)end=start+duration;
  else if(field==='duration'&&end!=null&&duration!=null)start=end-duration;
  else end=null;
  return {start,duration,end};
}
