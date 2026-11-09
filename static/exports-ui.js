import {escapeHtml as esc} from './logic.js';
import {timelineDuration} from './playback-logic.js';
import {frameCode,timelineOf} from './timeline-logic.js';

const states={queued:'В очереди',running:'Экспортируется',stopping:'Останавливается',completed:'Готово',failed:'Ошибка',cancelled:'Остановлен',interrupted:'Прерван'};
export function exportSummary(row,currentRevision){return `${states[row.state]??row.state}${row.profile==='final'?' · финальный':''} · снимок v${row.project_revision}${row.project_revision!==currentRevision?' · монтаж уже изменён':''} · ${row.size.join(' × ')} · ${row.seconds.toFixed(3)} с`;}
export class ExportsDesk{
  constructor(options){Object.assign(this,options);this.serial=0;}
  base(pid=this.getProject().id){return '/api/projects/'+encodeURIComponent(pid)+'/exports';}
  active(root=this.root){return !!root?.isConnected&&root===document.querySelector('#exportsDesk')&&root.closest?.('dialog')?.open!==false;}
  async open({check=false}={}){
    const pid=this.getProject()?.id;if(!pid)throw new Error('Сначала выберите проект.');
    await this.flush();if(this.getProject().id!==pid)return;this.pause?.();
    const p={...this.getProject(),timeline:timelineOf(this.getProject())};
    this.modal('Экспорт таймлайна',`<div id="exportsDesk"><p class="small-note">Весь проект от 0 до ${timelineDuration(p.timeline).toFixed(3)} с: выбранные дубли, участки, скорость, переходы через чёрный, паузы, титры, музыка, звук сегментов и включённый звук клипов. Экспорт сохраняет отдельный MP4 из снимка монтажа.</p><section id="exportReadiness" class="export-readiness" aria-label="Готовность монтажа"><button id="checkReadiness" type="button" class="button small">Проверить монтаж</button><p id="readinessStatus" role="status"></p><div id="readinessIssues"></div></section><form id="exportForm" class="export-form"><label class="field"><span>Профиль MP4</span><select name="profile"><option value="draft">Черновой · до 896 px</option><option value="project">Размер проекта · ${p.output_size.join(' × ')}</option><option value="final">Финальный · ${p.output_size.join(' × ')} · CRF 18</option></select></label><button type="submit" class="button primary">Экспортировать MP4</button></form><p class="small-note">CPU · до 10 минут таймлайна · лимит работы 5 минут. Неопределённое время, конфликт или блок без дубля требуют исправления. После изменения монтажа создайте новый экспорт.</p><p id="exportError" role="status"></p><div id="exportViewer" hidden></div><div id="exportsList" aria-live="polite"></div></div>`,true);
    this.root=document.querySelector('#exportsDesk');this.signature=null;const root=this.root,form=root.querySelector('#exportForm'),key={value:crypto.randomUUID()};let pending=false;
    form.onsubmit=async e=>{
      e.preventDefault();if(pending)return;pending=true;const button=form.querySelector('button');button.disabled=true;
      try{
        const profile=form.elements.profile.value;await this.flush();if(this.getProject().id!==pid||!this.active(root))return;
        await this.api(this.base(pid),'POST',{expected_revision:this.getProject().revision,profile,actor:'author',client_key:key.value});
        key.value=crypto.randomUUID();
        if(this.getProject().id===pid&&this.active(root)){root.querySelector('#exportError').textContent='';await this.refresh();}
      }catch(error){if(this.active(root))root.querySelector('#exportError').textContent=error.message;}
      finally{pending=false;button.disabled=false;}
    };
    root.querySelector('#checkReadiness').onclick=()=>this.checkReadiness(root,pid);
    await Promise.all([this.refresh(),check?this.checkReadiness(root,pid):null]);
  }
  async checkReadiness(root,pid){
    const button=root.querySelector('#checkReadiness'),status=root.querySelector('#readinessStatus'),list=root.querySelector('#readinessIssues');
    if(button.disabled)return;
    button.disabled=true;
    status.textContent='Проверяем монтаж…';
    list.replaceChildren();
    try{
      await this.flush();
      if(this.getProject().id!==pid||!this.active(root))return;
      const report=await this.api(this.base(pid)+'/readiness');
      if(this.getProject().id!==pid||!this.active(root))return;
      if(report.project_revision!==this.getProject().revision){
        status.textContent='Монтаж изменился. Повторите проверку.';
        return;
      }
      status.textContent=report.ready?'Тайминг, дубли и файлы прошли проверку.':
        'Проблем перед экспортом: '+report.issues.length+'.';
      if(report.ready)return;
      list.innerHTML='<ul>'+report.issues.map((issue,index)=>{
        const context=[issue.location,Number.isInteger(issue.frame)?frameCode(issue.frame,report.fps):null].filter(Boolean).join(' · ');
        return '<li><div><strong>'+esc(issue.message)+'</strong>'+(context?'<small>'+esc(context)+'</small>':'')+'</div>'+
          (issue.kind?'<button type="button" class="button subtle small" data-readiness-issue="'+index+'">Перейти</button>':'')+'</li>';
      }).join('')+'</ul>';
      for(const link of list.querySelectorAll('[data-readiness-issue]')){
        link.onclick=()=>{
          if(this.getProject().id!==pid||!this.active(root))return;
          if(this.getProject().revision!==report.project_revision){this.checkReadiness(root,pid);return;}
          this.openIssue(report.issues[Number(link.dataset.readinessIssue)]);
        };
      }
    }catch(error){
      if(this.active(root)&&this.getProject().id===pid)status.textContent=error.message;
    }finally{button.disabled=false;}
  }
  async refresh(){
    const root=this.root,pid=this.getProject()?.id,serial=++this.serial;if(!pid||!this.active(root))return;
    const rows=await this.api(this.base(pid));if(this.getProject().id!==pid||!this.active(root)||serial!==this.serial)return;
    const signature=JSON.stringify([rows,this.getProject().revision]);if(signature===this.signature)return;this.signature=signature;
    const list=root.querySelector('#exportsList');
    list.innerHTML=rows.slice(0,12).map(row=>{const percent=Math.min(100,Math.max(0,Number(row.progress?.percent)||0));return `<article class="export-card"><strong>${esc(exportSummary(row,this.getProject().revision))}</strong><small>${esc(row.id)} · ${esc(row.created_at.slice(0,19).replace('T',' '))}</small>${['queued','running','stopping'].includes(row.state)?`<div class="job-progress"><span style="width:${percent}%"></span></div><p>${esc(row.progress?.stage??'Подготовка')} · ${percent}% · ${Number(row.progress?.elapsed_seconds??0).toFixed(1)} с</p><button class="button subtle small" data-export-stop="${esc(row.id)}" ${row.state==='stopping'?'disabled':''}>Остановить экспорт</button>`:''}${row.error?`<p class="job-error">${esc(row.error)}</p>`:''}${row.state==='completed'?`<p>${row.result.video_info.frames} кадров · ${row.fps} fps · рендер ${row.result.elapsed_seconds.toFixed(1)} с</p><div class="export-actions"><button class="button small" data-export-view="${esc(row.id)}">Смотреть MP4</button><a class="button subtle small" href="${esc(row.video_url)}" download="${row.profile==='final'?'Final.mp4':'Draft.mp4'}">Скачать MP4</a></div>`:''}</article>`;}).join('')||'<p class="small-note">Экспортов пока нет. Готовые версии останутся здесь после перезапуска Studio.</p>';
    list.querySelectorAll('[data-export-view]').forEach(button=>button.onclick=()=>{
      if(this.getProject().id!==pid||!this.active(root))return;
      this.pause?.();
      const row=rows.find(r=>r.id===button.dataset.exportView),viewer=root.querySelector('#exportViewer');viewer.querySelectorAll('video').forEach(v=>v.pause());
      viewer.innerHTML=`<p>${esc(exportSummary(row,this.getProject().revision))}</p><video controls preload="metadata" src="${esc(row.video_url)}" aria-label="Экспортированный MP4"></video>`;viewer.hidden=false;
    });
    list.querySelectorAll('[data-export-stop]').forEach(button=>button.onclick=async()=>{
      try{
        const path=this.base(pid)+'/'+encodeURIComponent(button.dataset.exportStop),fresh=await this.api(path);
        if(this.getProject().id!==pid||!this.active(root))return;
        await this.api(path+'/stop','POST',{expected_revision:fresh.revision,actor:'author'});await this.refresh();
      }catch(e){this.onError(e);}
    });
  }
}
