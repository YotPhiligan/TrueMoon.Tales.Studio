import {escapeHtml as esc} from './logic.js';
import {clipSpeed,sourceOut,sourcePatch} from './clip-edit-logic.js';
import {frameCode,takeCapacity} from './timeline-logic.js';
import {bindTakeComparison,takeComparisonMarkup} from './take-compare.js';

export function replacementInterval(clip,takeId,start,capacity){
  const speed=clipSpeed(clip),end=start+(sourceOut(clip)-clip.source_in_frame);
  if(!Number.isFinite(start)||start<0)throw new Error('Укажите вход в кандидат.');
  if(end>capacity+1e-7){
    const missing=Math.min(clip.duration_frames,Math.ceil((end-capacity)/speed-1e-8));
    throw new Error('До конца монтажного участка не хватает кадров: '+missing+'. Сдвиньте вход или выберите другой дубль.');
  }
  const patch=sourcePatch({take_id:takeId},start,end,speed,capacity);
  if(patch.duration_frames!==clip.duration_frames)throw new Error('Выбранный участок меняет длину клипа.');
  return {id:clip.id,take_id:takeId,source_in_frame:patch.source_in_frame,
    source_out_frame:patch.source_out_frame,speed:patch.speed};
}

export class ClipReplacement{
  constructor(options){Object.assign(this,options);}
  async open(id){
    const pid=this.getProject().id;
    await this.flush();
    const project=this.getProject();
    if(project.id!==pid)return;
    const clip=this.getClip(id);
    if(!clip)return;
    const block=project.blocks.find(row=>row.id===clip.block_id);
    const original=block?.takes.find(take=>take.id===clip.take_id)??null;
    const takes=block?.takes.filter(take=>take.id!==clip.take_id)??[];
    if(!takes.length)throw new Error('У блока нет другого дубля.');
    const fps=project.fps,rate=clipSpeed(clip),duration=clip.duration_frames/fps;
    const start=clip.source_in_frame/fps,end=sourceOut(clip)/fps,span=end-start;
    const dialog=this.modal('Сравнить и заменить · '+(clip.story_title??block.label),
      '<div id="clipReplacement" class="clip-replacement">'+
      '<p class="small-note">'+frameCode(clip.start_frame,fps)+'–'+frameCode(clip.start_frame+clip.duration_frames,fps)+
      ' · '+duration.toFixed(3)+' с · '+rate+'×</p>'+
      '<div class="clip-replace-controls"><label class="field"><span>Кандидат</span><select id="replacementTake">'+
      takes.map(take=>'<option value="'+esc(take.id)+'">'+esc(take.name)+'</option>').join('')+'</select></label>'+
      '<label class="field"><span>Вход в кандидат, с</span><input id="replacementIn" type="number" min="0" step="any" value="'+start+'" required></label></div>'+
      '<div class="clip-replace-actions"><p id="replacementStatus" role="status"></p>'+
      '<button class="button primary" id="replacementApply" type="button" disabled>Назначить этот дубль клипу</button></div>'+
      '<div id="replacementComparison"></div></div>');
    const root=dialog.querySelector('#clipReplacement'),view=root.querySelector('#replacementComparison');
    const select=root.querySelector('#replacementTake'),input=root.querySelector('#replacementIn');
    const status=root.querySelector('#replacementStatus'),apply=root.querySelector('#replacementApply');
    let player,choice,pending=false;
    const active=()=>root.isConnected&&dialog.open&&this.getProject().id===pid;
    const ranges=()=>{
      const offset=input.valueAsNumber;
      const candidate=Number.isFinite(offset)&&offset>=0?{start:offset,end:offset+span}:{start:0,end:0};
      return [{start,end},candidate];
    };
    const getPatch=()=>{
      if(choice.error)throw new Error(choice.error);
      const video=choice.video;
      if(video?.error)throw new Error('Видео кандидата недоступно.');
      if(!choice.capacity||!Number.isFinite(video?.duration)||video.duration<=0)throw new Error('Проверяем длительность кандидата…');
      const cap=Math.min(choice.capacity,Math.floor((video.duration+1e-6)*fps));
      return replacementInterval(clip,choice.take.id,input.valueAsNumber*fps,cap);
    };
    function paint(){
      apply.disabled=true;
      try{
        const patch=getPatch();
        status.textContent='Участок кандидата: '+(patch.source_in_frame/fps).toFixed(3)+'–'+
          (patch.source_out_frame/fps).toFixed(3)+' с. Длина клипа сохранится.';
        apply.disabled=pending;
      }catch(error){status.textContent=error.message;}
    }
    const choose=()=>{
      player?.dispose();
      const take=takes.find(row=>row.id===select.value);
      const selected=choice={take,capacity:takeCapacity(take,fps)};
      const pair=[original,take];
      view.innerHTML=takeComparisonMarkup(pair,this.media,{montage:true});
      player=bindTakeComparison(dialog,pair,{projectId:pid,fps,window:{duration,rate,ranges:ranges()},
        onMetadata:video=>{selected.video=video;paint();}});
      if(!selected.capacity){
        const url='/api/projects/'+encodeURIComponent(pid)+'/media-info?asset='+encodeURIComponent(take.video_asset_id);
        this.api(url).then(info=>{
          if(!active()||choice!==selected)return;
          selected.capacity=takeCapacity({video_info:info},fps);
          if(!selected.capacity)selected.error='Не удалось определить длину кандидата.';
          paint();
        }).catch(error=>{
          if(!active()||choice!==selected)return;
          selected.error=error.message;
          paint();
        });
      }
    };
    this.onDispose(()=>player?.dispose());
    select.onchange=choose;
    input.oninput=paint;
    input.onchange=()=>{player.setRanges(ranges());paint();};
    apply.onclick=async()=>{
      if(pending||!active())return;
      let failure;
      try{
        const patch=getPatch();
        pending=true;
        apply.disabled=true;select.disabled=true;input.disabled=true;
        const result=await this.write('update_clip',patch,{expectedRevision:project.revision,throwErrors:true});
        if(result&&active())this.finish();
      }catch(error){
        failure=error;
      }finally{
        pending=false;
        if(active()){
          select.disabled=false;input.disabled=false;
          paint();
          if(failure)status.textContent=failure.message;
        }
      }
    };
    select.value=takes.find(take=>take.id===block.selected_take_id)?.id??takes[0].id;
    choose();
  }
}
