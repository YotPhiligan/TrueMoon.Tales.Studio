import {escapeHtml as esc} from './logic.js';
import {scenarioView} from './scenario-logic.js';
import {requestSummary} from './agent-ui.js';

const parts=[['description','Описание действия'],['references','Референсы'],['guides','Опорные кадры'],['prompt','Промпт H3'],['settings','Настройки']];
export function scenarioRequest(segment,item,values,fps){
  const goal=values.goal?.trim();if(!goal||goal.length>12000)throw new Error('Введите запрос агенту: от 1 до 12 000 символов.');
  const components=parts.map(([key])=>key).filter(key=>values.components?.includes(key));
  if(!components.length)throw new Error('Выберите, что нужно подготовить или улучшить.');
  const scope={segment_id:segment.id,item_id:item?.id??null,intent:item?'improve_block':'prepare_block',components};
  if(!item){
    const title=values.title?.trim();if(!title||title.length>160)throw new Error('Укажите название нового блока, до 160 символов.');
    const seconds=values.duration===''?null:Number(values.duration),duration=seconds===null?null:Math.round(seconds*fps);
    if(duration!==null&&(!Number.isFinite(duration)||duration<1||duration>518400))throw new Error('Укажите положительную длительность или оставьте её пустой.');
    scope.draft={title,description:values.description??'',duration_frames:duration,after_item_id:values.after_item_id||null};
  }
  return {goal,mode:values.mode,scope};
}
export function segmentRequests(rows,sid){return rows.filter(r=>r.scope?.segment_id===sid);}

export class ScenarioAgentDesk{
  constructor(options){Object.assign(this,options);this.serial=0;}
  base(pid=this.getProject().id){return '/api/projects/'+encodeURIComponent(pid)+'/agent/requests';}
  async form(segment,item=null){
    const pid=this.getProject().id;await this.flush();if(this.getProject().id!==pid)return;
    const fresh=scenarioView(this.getProject()).segments.find(s=>s.id===segment.id);
    if(!fresh||item&&!fresh.items.some(i=>i.id===item.id))throw new Error('Сегмент или блок уже изменился. Перечитайте сценарий.');
    const target=item?fresh.items.find(i=>i.id===item.id):null;
    let revision=this.getProject().revision;
    const key=crypto.randomUUID(),draftKey=`studio-scenario-agent-${pid}-${fresh.id}-${target?.id??'new'}`;
    this.modal(target?'Улучшить блок с ИИ':'Подготовить новый блок с ИИ',`<form id="scenarioAgentForm" class="modal-form scenario-agent-form"><p class="small-note">${esc(fresh.name)}${target?' → '+esc(target.title):''}. Запрос сохранится у сегмента; передайте его агенту из чата.</p>${target?'':`<label class="field"><span>Название нового блока</span><input name="title" required maxlength="160"></label><label class="field"><span>Что происходит в блоке</span><textarea name="description" maxlength="12000" rows="2"></textarea></label><div class="story-form-row"><label class="field"><span>Плановая длительность, с</span><input name="duration" type="number" min="0" step="any" placeholder="Можно уточнить позже"></label><label class="field"><span>Положение в сегменте</span><select name="after_item_id"><option value="">В конце сегмента</option>${fresh.items.map(i=>`<option value="${esc(i.id)}">После: ${esc(i.title)}</option>`).join('')}</select></label></div>`}<label class="field"><span>Запрос агенту</span><textarea name="goal" required maxlength="12000" rows="4" placeholder="Например: ускорить снижение за укрытие, сохранив направление оружия и образ Клэр…"></textarea></label><fieldset class="agent-components"><legend>Состав работы</legend>${parts.map(([key,label])=>`<label><input type="checkbox" name="components" value="${key}" checked> ${label}</label>`).join('')}</fieldset><label class="field"><span>Режим работы</span><select name="mode"><option value="apply">${target?'Внести указанные изменения':'Создать блок и подготовить материалы'}</option><option value="propose">Сначала предложить варианты</option></select></label><p class="small-note">${target?'Прежние дубли и история материалов сохраняются.':'Новый блок появится после работы агента.'} Предложения сохраняются без изменения проекта. Генерация видео запускается отдельно.</p><p id="scenarioAgentError" role="status"></p><div class="scenario-agent-actions"><button type="submit" class="button primary">Создать задание</button><button type="button" id="scenarioAgentRebase" class="button subtle" hidden>Обновить контекст</button></div></form>`);
    const form=document.querySelector('#scenarioAgentForm'),goal=form.elements.goal;
    goal.value=sessionStorage.getItem(draftKey)??'';goal.oninput=()=>sessionStorage.setItem(draftKey,goal.value);
    form.querySelector('#scenarioAgentRebase').onclick=async()=>{
      if(pending||this.getProject().id!==pid)return;pending=true;
      try{
        await this.flush();if(this.getProject().id!==pid)return;
        const current=await this.api('/api/projects/'+encodeURIComponent(pid));
        if(this.getProject().id!==pid||document.querySelector('#scenarioAgentForm')!==form)return;
        revision=current.revision;this.onProject?.(current);
        form.querySelector('#scenarioAgentError').textContent='Контекст обновлён. Проверьте запрос и отправьте его ещё раз.';
      }catch(error){form.querySelector('#scenarioAgentError').textContent=error.message;}finally{pending=false;}
    };
    let pending=false;
    form.onsubmit=async e=>{
      e.preventDefault();if(pending)return;
      const controls=[...form.querySelectorAll('input,textarea,select,button')];
      try{
        if(this.getProject().id!==pid)throw new Error('Проект изменился; запрос остался в форме.');
        const data=new FormData(form),body=scenarioRequest(fresh,target,{...Object.fromEntries(data),components:data.getAll('components')},this.getProject().fps);
        pending=true;controls.forEach(c=>c.disabled=true);await this.flush();
        if(this.getProject().id!==pid)throw new Error('Проект изменился; отправка отменена.');
        const task=await this.api(this.base(pid),'POST',{...body,expected_revision:revision,actor:'author',client_key:key});
        if(this.getProject().id!==pid||document.querySelector('#scenarioAgentForm')!==form)return;
        sessionStorage.removeItem(draftKey);this.closeModal();this.toast('Задание '+task.id+' сохранено. Скопируйте его из карточки сегмента в чат.');
        await this.refresh();
      }catch(error){
        form.querySelector('#scenarioAgentError').textContent=error.message;
        form.querySelector('#scenarioAgentRebase').hidden=error.status!==409;
      }finally{pending=false;controls.forEach(c=>c.disabled=false);}
    };
  }
  render(segment,root){
    this.target=root;this.sid=segment.id;this.serial++;this.rowsSignature=null;
    root.innerHTML='<div class="agent-task-heading"><strong>Задания агенту · этот сегмент</strong><button class="subtle-link" data-refresh-tasks>Обновить</button></div><div class="scenario-agent-list" aria-live="polite">Загружаем задания…</div>';
    root.querySelector('[data-refresh-tasks]').onclick=()=>this.refresh().catch(this.onError);
    this.refresh().catch(this.onError);
  }
  async refresh(){
    const root=this.target,pid=this.getProject().id,sid=this.sid,serial=++this.serial;
    if(!root?.isConnected||this.isActive?.()===false)return;
    const rows=segmentRequests(await this.api(this.base(pid)),sid);
    if(this.getProject().id!==pid||this.target!==root||this.sid!==sid||serial!==this.serial||!root.isConnected||this.isActive?.()===false)return;
    const signature=JSON.stringify(rows);if(this.rowsSignature===signature)return;this.rowsSignature=signature;
    const list=root.querySelector('.scenario-agent-list');
    list.innerHTML=rows.slice(0,12).map(row=>`<article class="agent-task ${esc(row.state)}"><small>${esc(requestSummary(row))}</small><p>${esc(row.goal)}</p>${row.owner?`<span class="agent-owner">${esc(row.owner)}</span>`:''}${row.result?`<p class="agent-result">${esc(row.result.slice(0,300))}${row.result.length>300?'…':''}</p>`:''}<div class="agent-task-actions"><button class="subtle-link" data-scenario-copy="${esc(row.id)}">Скопировать для чата</button><button class="subtle-link" data-scenario-view="${esc(row.id)}">Подробнее</button>${row.result_target||row.scope.item_id?`<button class="subtle-link" data-scenario-open="${esc(row.id)}">${row.result_target?'Открыть результат':'Материалы блока'}</button>`:''}${['pending','running'].includes(row.state)?`<button class="subtle-link" data-scenario-cancel="${esc(row.id)}">Отменить</button>`:''}</div></article>`).join('')||'<p class="small-note">Подготовьте новый блок или выберите «Улучшить с ИИ» у существующего. Сохраняемые задания можно передать агенту из чата.</p>';
    list.querySelectorAll('[data-scenario-copy]').forEach(b=>b.onclick=()=>this.action(pid,b.dataset.scenarioCopy,'copy').catch(this.onError));
    list.querySelectorAll('[data-scenario-view]').forEach(b=>b.onclick=()=>this.action(pid,b.dataset.scenarioView,'view').catch(this.onError));
    list.querySelectorAll('[data-scenario-cancel]').forEach(b=>b.onclick=()=>this.action(pid,b.dataset.scenarioCancel,'cancel').catch(this.onError));
    list.querySelectorAll('[data-scenario-open]').forEach(b=>b.onclick=()=>{
      const row=rows.find(r=>r.id===b.dataset.scenarioOpen),target=row.result_target??row.scope;
      const segment=scenarioView(this.getProject()).segments.find(s=>s.id===target.segment_id),item=segment?.items.find(i=>i.id===target.item_id);
      if(!item){this.onError(new Error('Блок результата уже убран из сценария; исходник остаётся в библиотеке.'));return;}
      this.openBlock(item.block_id);
    });
  }
  async action(pid,rid,action){
    const root=this.target,sid=this.sid;
    const task=await this.api(this.base(pid)+'/'+encodeURIComponent(rid));
    if(this.getProject().id!==pid||this.target!==root||this.sid!==sid||root&&!root.isConnected||this.isActive?.()===false)return;
    if(action==='copy'){await this.copyText(JSON.stringify({studio_url:location.origin,project_id:pid,request_id:rid,instructions:'Получите задание из Studio, claim с актуальной revision; перечитайте проект перед каждой правкой; finish/fail с фактическим результатом. GPU не запускать.',task},null,2));return;}
    if(action==='cancel'){await this.api(this.base(pid)+'/'+rid,'POST',{expected_revision:task.revision,action:'cancel',actor:'author'});await this.refresh();return;}
    this.modal('Задание агенту',`<p class="small-note">${esc(task.id)} · v${task.revision} · проект v${task.base_project_revision}</p><p>${esc(requestSummary(task))}</p><pre class="source-text">${esc(task.goal)}</pre>${task.scope.draft?`<p>Новый блок: ${esc(task.scope.draft.title)} · ${task.scope.draft.duration_frames==null?'длина не задана':(task.scope.draft.duration_frames/task.context.project.fps).toFixed(3)+' с'}</p>`:''}<p class="small-note">Состав: ${task.scope.components.map(key=>esc(parts.find(([k])=>k===key)?.[1]??key)).join(', ')}. Статус обновляется из сохранённого задания.</p>${task.result?`<h3>Результат</h3><pre class="source-text">${esc(task.result)}</pre>`:''}`);
  }
}
