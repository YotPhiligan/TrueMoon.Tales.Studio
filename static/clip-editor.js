import {escapeHtml as esc} from './logic.js';
import {takeCapacity} from './timeline-logic.js';
import {clipSpeed,sourceOut,sourceFrame,sourcePatch} from './clip-edit-logic.js';

export class ClipEditor{
  constructor(options){Object.assign(this,options);}
  async open(clip){
    const pid=this.getProject().id;await this.flush();if(this.getProject().id!==pid)return;
    const current=this.getClip(clip.id);if(!current?.take_id)throw new Error('Сначала назначьте дубль.');
    const c={...current},fps=this.getProject().fps,revision=this.getProject().revision;
    const b=this.getProject().blocks.find(b=>b.id===c.block_id),take=b.takes.find(t=>t.id===c.take_id),cap=takeCapacity(take,fps)||Infinity;
    const value=n=>Number((n/fps).toFixed(9));
    this.modal('Участок и скорость · '+(c.story_title??b.label),`<form id="clipSourceForm" class="modal-form clip-source-form"><p class="small-note">${esc(take.name)} · ${Number.isFinite(cap)?(cap/fps).toFixed(3)+' с':'длина будет проверена сервером'}. Меняется только это использование дубля.</p><label class="field"><span>Действие</span><select name="operation"><option value="range">Участок и скорость</option><option value="slip">Сдвиг содержимого — длина сохраняется</option></select></label><div class="story-form-row"><label class="field"><span>Вход в дубль, с</span><input name="source_in" type="number" min="0" step="any" value="${value(c.source_in_frame)}" required></label><label class="field"><span>Выход из дубля, с</span><input name="source_out" type="number" min="0" step="any" value="${value(sourceOut(c))}" required></label></div><div class="story-form-row"><label class="field"><span>Скорость, ×</span><input name="speed" type="number" min="0.25" max="4" step="any" value="${clipSpeed(c)}" required></label><label class="field"><span>Готовая скорость</span><select name="preset"><option value="">Вручную</option>${[.25,.5,.75,1,1.25,1.5,2,4].map(v=>`<option value="${v}">${v}×</option>`).join('')}</select></label></div><label class="field" id="clipSlipField" hidden><span>Сдвиг внутри дубля, с</span><input name="slip" type="number" step="any" value="0"><small>Положительный — позже в источнике; положение, скорость и длина блока сохраняются.</small></label><output id="clipSourceSummary" aria-live="polite"></output><p class="small-note">Окно сегмента и музыка сохраняются. Следующие блоки сдвигаются внутри окна; закреплённые начала и защищённые паузы учитываются. Длина округляется вверх до кадра монтажа. В исходнике не создаются новые кадры.</p><p id="clipSourceError" role="status"></p><button class="button primary" type="submit">Применить к блоку</button></form>`);
    const form=document.querySelector('#clipSourceForm');let pending=false;
    const read=()=>{
      const v=form.elements,slip=v.operation.value==='slip',delta=sourceFrame(v.slip.value,fps);
      const start=slip?c.source_in_frame+delta:sourceFrame(v.source_in.value,fps),end=slip?sourceOut(c)+delta:sourceFrame(v.source_out.value,fps),rate=slip?clipSpeed(c):Number(v.speed.value);
      return {slip,delta,patch:sourcePatch(c,start,end,rate,cap)};
    };
    const paint=()=>{
      const slip=form.elements.operation.value==='slip';form.querySelector('#clipSlipField').hidden=!slip;
      for(const name of ['source_in','source_out','speed','preset'])form.elements[name].disabled=slip;
      form.elements.slip.disabled=!slip;
      try{const {patch}=read();form.querySelector('#clipSourceSummary').textContent=`Источник: ${value(patch.source_in_frame)}–${value(patch.source_out_frame)} с · ${patch.speed}× · Монтаж: ${(patch.duration_frames/fps).toFixed(3)} с (${patch.duration_frames} кадр.)`;form.querySelector('#clipSourceError').textContent='';}
      catch(e){form.querySelector('#clipSourceSummary').textContent='';form.querySelector('#clipSourceError').textContent=e.message;}
    };
    form.oninput=paint;form.onchange=paint;form.elements.preset.onchange=()=>{if(form.elements.preset.value)form.elements.speed.value=form.elements.preset.value;paint();};paint();
    form.onsubmit=async e=>{
      e.preventDefault();if(pending)return;
      try{
        if(this.getProject().id!==pid)throw new Error('Проект изменился; значения остались в форме.');
        const {slip,delta,patch}=read();pending=true;form.querySelectorAll('input,select,button').forEach(c=>c.disabled=true);
        const p=await this.write(slip?'slip_clip':'edit_clip_source',slip?{id:c.id,delta_frames:delta}:{id:c.id,source_in_frame:patch.source_in_frame,source_out_frame:patch.source_out_frame,speed:patch.speed},{expectedRevision:revision,throwErrors:true});
        if(p&&this.getProject().id===pid&&document.querySelector('#clipSourceForm')===form)this.closeModal();
      }catch(error){form.querySelector('#clipSourceError').textContent=error.message;}
      finally{pending=false;form.querySelectorAll('input,select,button').forEach(c=>c.disabled=false);const slip=form.elements.operation.value==='slip';for(const name of ['source_in','source_out','speed','preset'])form.elements[name].disabled=slip;form.elements.slip.disabled=!slip;}
    };
  }
}
