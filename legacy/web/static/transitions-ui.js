import {escapeHtml as esc} from './logic.js';
import {toFrame} from './timeline-logic.js';
import {timelineJoins,transitionWindows,transitionIssue} from './transitions-logic.js';

export class TransitionsDesk{
  constructor(options){Object.assign(this,options);}
  active(root,pid){return this.getProject()?.id===pid&&root?.isConnected&&document.querySelector('#transitionForm')===root&&root.closest('dialog')?.open!==false;}
  async open(target){
    const pid=this.getProject().id;await this.flush();if(this.getProject().id!==pid)return;
    const p=this.getProject(),t=this.getTimeline(),join=timelineJoins(t).find(j=>j.left.id===target.left.id&&j.right.id===target.right.id);
    if(!join)throw new Error('Стык изменился. Откройте его меню заново.');
    const fps=t.fps,revision=p.revision,old=join.transition,seconds=n=>(n/fps).toFixed(6).replace(/0+$/,'').replace(/\.$/,'');
    const row=old??{fade_out_frames:Math.min(Math.round(fps*.25),join.left.duration_frames),pause_frames:join.gap_frames,fade_in_frames:Math.min(Math.round(fps*.25),join.right.duration_frames)};
    this.modal('Переход через чёрный',`<form id="transitionForm" class="transition-form"><p class="small-note">${esc(target.name??'Стык соседних блоков')} · ${fps} fps</p><div class="transition-steps"><span>Затемнение</span><b>→</b><span>Чёрная пауза</span><b>→</b><span>Появление</span></div><div class="timeline-dialog-row">${[['out','Затемнение, с',row.fade_out_frames,0],['pause','Чёрная пауза, с',row.pause_frames,join.gap_frames],['in','Появление, с',row.fade_in_frames,0]].map(([name,label,value,min])=>`<label class="field"><span>${label}</span><input type="number" name="${name}" min="${min/fps}" step="any" value="${seconds(value)}" required></label>`).join('')}</div><p class="small-note">Существующий промежуток: ${(join.gap_frames/fps).toFixed(3)} с. Дополнительная пауза закрывает конец предыдущего блока. Позиции клипов, музыка, метки и закрепления сохраняются. Звук автоматически не затухает.</p><output id="transitionSummary"></output><p id="transitionError" role="status"></p><div class="transition-actions"><button type="submit" class="button primary">Сохранить переход</button>${old?'<button id="transitionRemove" type="button" class="button subtle">Убрать переход</button>':''}</div></form>`);
    const root=document.querySelector('#transitionForm'),error=root.querySelector('#transitionError'),button=root.querySelector('button[type="submit"]');let pending=false;
    const values=()=>({left_clip_id:join.left.id,right_clip_id:join.right.id,fade_out_frames:toFrame(root.elements.out.value,fps),pause_frames:toFrame(root.elements.pause.value,fps),fade_in_frames:toFrame(root.elements.in.value,fps)});
    const trial=r=>({...t,transition_windows:undefined,transitions:[...(t.transitions??[]).filter(x=>x.right_clip_id!==r.right_clip_id),r]});
    const update=()=>{
      const r=values(),issue=transitionIssue(trial(r));error.textContent=issue;button.disabled=pending||!!issue;
      const w=issue?null:transitionWindows(trial(r)).find(w=>w.right_clip_id===r.right_clip_id),s=n=>(n/fps).toFixed(3);
      root.querySelector('#transitionSummary').textContent=w?`Затемнение ${s(w.out_start_frame)}–${s(w.black_start_frame)} с · чёрный ${s(w.black_start_frame)}–${s(w.right_start_frame)} с · появление ${s(w.right_start_frame)}–${s(w.in_end_frame)} с. Дополнительно закрыто ${s(Math.max(0,r.pause_frames-join.gap_frames))} с конца предыдущего блока.`:'Исправьте длительности перехода.';
    };
    for(const field of ['out','pause','in'])root.elements[field].oninput=update;
    const submit=async(action,payload)=>{
      if(pending||!this.active(root,pid))return;pending=true;root.querySelectorAll('button').forEach(b=>b.disabled=true);
      try{
        const result=await this.write(action,payload,{expectedRevision:revision,throwErrors:true});
        if(result&&this.active(root,pid))this.closeModal();
      }catch(err){if(this.active(root,pid))error.textContent=err.message;}
      finally{pending=false;if(root.isConnected){button.disabled=false;root.querySelector('#transitionRemove')?.removeAttribute('disabled');}}
    };
    root.onsubmit=e=>{e.preventDefault();const r=values(),issue=transitionIssue(trial(r));if(issue){error.textContent=issue;return;}return submit('set_transition',r);};
    if(old)root.querySelector('#transitionRemove').onclick=()=>submit('remove_transition',{right_clip_id:join.right.id});
    update();
  }
}
