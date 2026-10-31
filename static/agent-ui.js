import {escapeHtml as esc} from './logic.js';

const briefFields=[['synopsis','Замысел и цель ролика'],['visual_style','Визуальный стиль'],['continuity_rules','Что сохранять: образ, география, музыкальные опоры'],['agent_notes','Рабочие указания агенту']];
const states={pending:'Ожидает агента',running:'В работе',completed:'Завершено',failed:'Ошибка',cancelled:'Отменено'};
export function requestSummary(row){const scope=row.scope;return `${states[row.state]??row.state} · ${scope?(scope.intent==='prepare_block'?'Новый блок: '+scope.draft.title:row.block_label)+' · '+scope.segment_name:row.block_label??'Весь проект'} · ${row.mode==='apply'?(scope?scope.intent==='prepare_block'?'Подготовить':'Улучшить':'Исправить'):'Предложить'}`;}
export function shouldRefreshEditor(state,remoteRevision){return remoteRevision!==state.project?.revision&&!state.dirty&&!state.savePromise&&!state.conflict&&!state.timelinePending&&!state.drag;}

export class AgentDesk {
  constructor(options){Object.assign(this,options);this.refreshSerial=0;}
  base(pid=this.getProject().id){return '/api/projects/'+encodeURIComponent(pid);}
  draftKey(){return 'studio-agent-draft-'+this.getProject().id+'-'+(this.getBlock()?.id??'project');}
  render(){
    const root=document.querySelector('#inspectorBody'),block=this.getBlock();
    root.innerHTML=`<div class="agent-illustration">${this.icon('spark')}</div><h2>Работа вместе с ИИ</h2><p class="agent-text">Передайте задачу агенту из чата. Контекст включает <strong>замысел проекта, монтаж, опоры, дубли и исходные документы</strong>.</p><button id="agentBrief" class="button subtle full">Карточка замысла проекта</button><label class="field"><span>Область задачи</span><select id="agentScope">${block?`<option value="block">${esc(block.label)} · выбранный блок</option>`:''}<option value="project">Весь проект</option></select></label><label class="field"><span>Что нужно проработать</span><textarea id="agentRequest" placeholder="Например: сделать снижение быстрее, сохранив направление карабина…" maxlength="12000"></textarea></label><label class="field"><span>Действие агента</span><select id="agentMode"><option value="propose">Предложить варианты</option><option value="apply">Внести указанную правку</option></select></label><button id="createAgentTask" class="button primary full">Создать задание</button><button id="copyContext" class="button subtle full">Скопировать задание для ИИ</button><button id="showContext" class="button subtle full">Посмотреть JSON-контекст</button><p class="inline-note">Задания сохраняются локально. Передайте задание чату: автоматического вызова модели пока нет. Подготовка и GPU-запуск остаются отдельными действиями.</p><div id="agentContextBox"></div><div class="agent-task-heading"><strong>Задания проекта</strong><button id="refreshAgentTasks" class="subtle-link">Обновить</button></div><div id="agentTasks" aria-live="polite"></div>`;
    const goal=root.querySelector('#agentRequest');goal.value=sessionStorage.getItem(this.draftKey())??'';
    goal.oninput=()=>sessionStorage.setItem(this.draftKey(),goal.value);
    root.querySelector('#agentBrief').onclick=()=>this.editBrief().catch(this.onError);
    root.querySelector('#createAgentTask').onclick=()=>this.create().catch(this.onError);
    root.querySelector('#copyContext').onclick=()=>this.copyDraft().catch(this.onError);
    root.querySelector('#showContext').onclick=()=>this.showContext().catch(this.onError);
    root.querySelector('#refreshAgentTasks').onclick=()=>this.refresh().catch(this.onError);
    this.refresh().catch(this.onError);
  }
  scope(){return document.querySelector('#agentScope')?.value==='block'?this.getBlock()?.id:null;}
  async currentContext(bid=this.scope(),pid=this.getProject().id){
    await this.flush();return this.api(this.base(pid)+'/agent/context'+(bid?'?block='+encodeURIComponent(bid):''));
  }
  async copyDraft(){
    const goal=document.querySelector('#agentRequest').value,mode=document.querySelector('#agentMode').value;
    const context=await this.currentContext();
    await this.copyText(JSON.stringify({goal,mode,context},null,2));
  }
  async showContext(){
    const pid=this.getProject().id,bid=this.getBlock()?.id,context=await this.currentContext();
    if(this.getProject().id!==pid||this.getBlock()?.id!==bid)return;
    const target=document.querySelector('#agentContextBox');if(!target)return;
    target.innerHTML=`<textarea class="agent-context" readonly aria-label="JSON-контекст">${esc(JSON.stringify(context,null,2))}</textarea>`;
  }
  async create(){
    const goal=document.querySelector('#agentRequest').value,mode=document.querySelector('#agentMode').value,bid=this.scope(),pid=this.getProject().id;
    if(!goal.trim())throw new Error('Опишите задачу для агента.');
    const button=document.querySelector('#createAgentTask');button.disabled=true;
    try{
      await this.flush();
      if(this.getProject().id!==pid)return;
      const task=await this.api(this.base(pid)+'/agent/requests','POST',{expected_revision:this.getProject().revision,goal,mode,block_id:bid,actor:'author'});
      if(this.getProject().id!==pid)return;
      this.toast('Задание '+task.id+' сохранено. Передайте его агенту из чата.');
      await this.refresh();
    }finally{button.disabled=false;}
  }
  async refresh(){
    const target=document.querySelector('#agentTasks');if(!target)return;
    const pid=this.getProject().id,serial=++this.refreshSerial;
    const rows=await this.api(this.base(pid)+'/agent/requests');
    if(this.getProject().id!==pid||serial!==this.refreshSerial||target!==document.querySelector('#agentTasks'))return;
    target.innerHTML=rows.slice(0,12).map(row=>`<article class="agent-task ${esc(row.state)}" data-task="${esc(row.id)}"><small>${esc(requestSummary(row))}</small><p>${esc(row.goal)}</p>${row.owner?`<span class="agent-owner">${esc(row.owner)}</span>`:''}${row.result?`<p class="agent-result">${esc(row.result.slice(0,300))}${row.result.length>300?'…':''}</p>`:''}<div class="agent-task-actions"><button class="subtle-link" data-task-copy="${esc(row.id)}">Скопировать</button><button class="subtle-link" data-task-view="${esc(row.id)}">Подробнее</button>${['pending','running'].includes(row.state)?`<button class="subtle-link" data-task-cancel="${esc(row.id)}">Отменить</button>`:''}</div></article>`).join('')||'<p class="small-note">Заданий пока нет. Создайте первое или скопируйте текущий контекст в чат.</p>';
    target.querySelectorAll('[data-task-copy]').forEach(button=>button.onclick=async()=>{try{const task=await this.api(this.base(pid)+'/agent/requests/'+button.dataset.taskCopy);await this.copyText(JSON.stringify(task,null,2));}catch(e){this.onError(e);}});
    target.querySelectorAll('[data-task-view]').forEach(button=>button.onclick=async()=>{try{const task=await this.api(this.base(pid)+'/agent/requests/'+button.dataset.taskView);if(this.getProject().id!==pid)return;this.modal('Задание для ИИ',`<p class="small-note">${esc(task.id)} · v${task.revision} · проект v${task.base_project_revision}</p><p>${esc(requestSummary(task))}</p><pre class="source-text">${esc(task.goal)}</pre><p class="small-note">Снимок контекста: ${esc(task.context.snapshot_sha256)}. Перед правкой агент должен получить свежую версию проекта.</p>${task.result?`<h3>Результат</h3><pre class="source-text">${esc(task.result)}</pre>`:''}`);}catch(e){this.onError(e);}});
    target.querySelectorAll('[data-task-cancel]').forEach(button=>button.onclick=async()=>{try{const row=rows.find(row=>row.id===button.dataset.taskCancel);await this.api(this.base(pid)+'/agent/requests/'+row.id,'POST',{expected_revision:row.revision,action:'cancel',actor:'author'});await this.refresh();}catch(e){this.onError(e);}});
  }
  async editBrief(){
    await this.flush();const project=this.getProject(),pid=project.id,revision=project.revision;
    const brief=await this.api(this.base(pid)+'/brief');if(this.getProject().id!==pid)return;
    this.modal('Карточка замысла · '+project.name,`<p class="small-note">Общий контекст для человека и агента. Карточка хранится в Studio.project.json; исходный Plan.md сохраняется отдельно.</p><form id="projectBriefForm" class="modal-form">${briefFields.map(([key,label])=>`<label class="field"><span>${label}</span><textarea name="${key}" maxlength="12000" rows="3">${esc(brief[key])}</textarea></label>`).join('')}<p id="briefStatus" role="status"></p><button class="button primary" type="submit">Сохранить карточку</button></form>`);
    const form=document.querySelector('#projectBriefForm');
    form.onsubmit=async e=>{
      e.preventDefault();const mine=Object.fromEntries(new FormData(form)),patch=Object.fromEntries(Object.entries(mine).filter(([key,value])=>value!==brief[key]));
      if(!Object.keys(patch).length){this.closeModal();return;}
      const button=form.querySelector('button');button.disabled=true;
      try{const updated=await this.api(this.base(pid)+'/brief','PATCH',{expected_revision:revision,brief:patch,actor:'author'});if(this.getProject().id!==pid)return;this.onProject(updated);this.closeModal();this.toast('Карточка проекта сохранена.');}
      catch(error){form.querySelector('#briefStatus').textContent=error.status===409?'Проект уже изменился. Ваш текст остаётся в форме; скопируйте его перед перечитыванием карточки.':error.message;}
      finally{button.disabled=false;}
    };
  }
}
