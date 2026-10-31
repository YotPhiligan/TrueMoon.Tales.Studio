import {escapeHtml as esc} from './logic.js';
import {scenarioView,windowFromEdit} from './scenario-logic.js';

export class ScenarioDesk{
  constructor(options){Object.assign(this,options);this.selected=null;this.pending=false;}
  view(){return scenarioView(this.project());}
  seconds(n){return n==null?'':(n/this.project().fps).toFixed(3);}
  async write(action,payload,expected=null){
    if(this.pending)throw new Error('Дождитесь сохранения сценария.');
    this.pending=true;const pid=this.project().id;
    try{await this.flush();if(this.project().id!==pid)throw new Error('Проект изменился.');this.pause();
      const p=await this.api(`/api/projects/${encodeURIComponent(pid)}/scenario`,'POST',{expected_revision:expected??this.project().revision,action,payload,actor:'author'});
      if(this.project().id!==pid)return null;this.onProject(p);return p;
    }finally{this.pending=false;}
  }
  tree(){
    const root=document.querySelector('#projectTree'),view=this.view(),query=document.querySelector('#searchBlocks').value.trim().toLowerCase();
    root.innerHTML=`<div class="story-tree-heading"><strong>СЦЕНАРИЙ</strong><button class="subtle-link" id="storyAddSegment">+ Сегмент</button></div>${view.segments.filter(s=>(s.name+' '+s.description).toLowerCase().includes(query)).map(s=>`<button class="story-tree-row ${this.selected===s.id?'active':''}" data-story-segment="${s.id}"><strong>${esc(s.name)}</strong><span>${s.start_frame==null?'Начало не задано':this.seconds(s.start_frame)+' с'} · ${s.duration_frames==null?'Длина не задана':this.seconds(s.duration_frames)+' с'} · ${s.items.length} блок.</span>${view.issues.some(i=>i.segment_id===s.id)?'<small>Проверить время / материалы</small>':''}</button>`).join('')||'<p class="small-note story-empty">Начните с названия, действия и примерного времени сегмента.</p>'}<button class="button subtle full" id="storyLibrary">Библиотека исходников</button>`;
    root.querySelector('#storyAddSegment').onclick=()=>this.segmentForm().catch(this.onError);
    root.querySelector('#storyLibrary').onclick=()=>this.library();
    root.querySelectorAll('[data-story-segment]').forEach(button=>button.onclick=()=>this.open(button.dataset.storySegment));
  }
  open(id){this.selected=id;this.showStory();}
  render(){
    const view=this.view(),s=view.segments.find(s=>s.id===this.selected)??view.segments[0];this.selected=s?.id??null;
    const root=document.querySelector('#scenarioView');
    if(!s){root.innerHTML='<div class="story-intro"><span class="eyebrow">СЦЕНАРИЙ ПРОЕКТА</span><h1>Сначала — что происходит в кадре</h1><p>Создайте сегмент, опишите действие и добавьте блоки. Материалы и дубли можно подготовить позже.</p><button class="button primary" id="storyCreateFirst">Добавить сегмент</button></div>';root.querySelector('button').onclick=()=>this.segmentForm().catch(this.onError);return;}
    const issues=view.issues.filter(i=>i.segment_id===s.id),budget=s.remaining_frames;
    root.innerHTML=`<header class="story-header"><div><span class="eyebrow">СЦЕНАРИЙ / СЕГМЕНТ</span><h1>${esc(s.name)}</h1></div><button id="storyEditSegment" class="button subtle">Изменить сегмент</button></header><p class="story-description">${esc(s.description||'Описание пока не задано.')}</p><div class="story-budget"><span>Начало <b>${this.seconds(s.start_frame)||'—'} с</b></span><span>Конец <b>${s.start_frame!=null&&s.duration_frames!=null?this.seconds(s.start_frame+s.duration_frames):'—'} с</b></span><span>Окно <b>${this.seconds(s.duration_frames)||'—'} с</b></span><span class="${budget<0?'story-error':''}">${budget==null?'Требуется расчёт':budget<0?'Не хватает '+this.seconds(-budget)+' с':'Свободно '+this.seconds(budget)+' с'}</span></div>${issues.length?`<div class="story-alert" role="status">${issues.map(i=>esc(i.message)).join('<br>')}<small>План сохранён. Предыдущий допустимый монтаж остаётся доступен; исправьте конфликт перед воспроизведением.</small></div>`:''}<div class="story-actions"><button id="storyAddItem" class="button primary">+ Пустой блок</button><button id="storyAgentNew" class="button subtle">+ Блок с ИИ</button><button id="storyAddSource" class="button subtle">+ Из библиотеки</button><button id="storyPreviewSegment" class="button subtle">▶ Просмотр сегмента</button><button id="storyCompact" class="button subtle" title="Сдвинуть внутренние блоки вплотную, сохранив первое начало, закрепления и защищённые паузы">Убрать промежутки</button><button id="storyOpenTimeline" class="button subtle">Общий монтаж</button><span>Окно фиксировано · блоки следуют по порядку</span></div><div class="story-cards">${s.items.map((i,index)=>{
      const b=this.project().blocks.find(b=>b.id===i.block_id),take=b?.takes.find(t=>t.id===i.take_id),poster=take?.last_frame_asset_id??b?.references[0]?.asset_id;
      return `<article class="story-card"><div class="story-card-image">${poster?`<img src="${this.media(poster)}" alt="" loading="lazy">`:`<span>${String(index+1).padStart(2,'0')}</span>`}</div><div class="story-card-main"><small>${i.start_frame==null?'Позиция не определена':this.seconds(i.start_frame)+' с'} · ${this.seconds(i.duration_frames)||'Длина не задана'}${i.duration_frames==null?'':' с'} · ${i.take_id?'Выбран дубль':'Заглушка'}</small><h2>${esc(i.title)}</h2><p>${esc(i.description||'Описание действия пока не задано.')}</p><span class="story-source">Источник: ${esc(b?.label??'—')}${i.gap_before_frames?' · пауза перед блоком '+this.seconds(i.gap_before_frames)+' с':''}</span><div class="story-card-buttons"><button class="subtle-link" data-edit="${i.id}">Описание и время</button><button class="subtle-link" data-open="${i.block_id}">Материалы и промпт</button><button class="subtle-link" data-agent-improve="${i.id}">Улучшить с ИИ</button><button class="subtle-link" data-up="${i.id}" ${index===0?'disabled':''}>↑</button><button class="subtle-link" data-down="${i.id}" ${index===s.items.length-1?'disabled':''}>↓</button><button class="subtle-link" data-insert="${index+1}">+ После</button><button class="subtle-link" data-remove="${i.id}">Убрать</button></div></div></article>`;
    }).join('')||'<p class="story-empty">Пока нет блоков. Можно начать с одного предложения о действии.</p>'}</div><section id="storyAgentTasks" class="story-agent-tasks" aria-label="Задания агенту для сегмента"></section><footer class="story-footer"><button class="subtle-link" id="storyRemoveSegment">Убрать сегмент из сценария</button><span>Исходники и дубли сохраняются в библиотеке. Отмена доступна на таймлайне.</span></footer>`;
    root.querySelector('#storyEditSegment').onclick=()=>this.segmentForm(s).catch(this.onError);
    root.querySelector('#storyAddItem').onclick=()=>this.itemForm(s).catch(this.onError);
    root.querySelector('#storyCompact').onclick=()=>this.write('compact_segment',{segment_id:s.id}).catch(this.onError);
    root.querySelector('#storyAddSource').onclick=()=>this.itemForm(s,null,true).catch(this.onError);
    root.querySelector('#storyAgentNew').onclick=()=>this.agent.form(s).catch(this.onError);
    root.querySelectorAll('[data-agent-improve]').forEach(b=>b.onclick=()=>this.agent.form(s,s.items.find(i=>i.id===b.dataset.agentImprove)).catch(this.onError));
    this.agent.render(s,root.querySelector('#storyAgentTasks'));
    if(this.sounds){const sound=document.createElement('section');root.querySelector('.story-cards').after(sound);this.sounds.render(s,sound);}
    root.querySelector('#storyPreviewSegment').disabled=issues.length>0;root.querySelector('#storyPreviewSegment').onclick=()=>this.preview(s.id);root.querySelector('#storyOpenTimeline').onclick=()=>this.previewAll();
    root.querySelector('#storyRemoveSegment').onclick=()=>this.write('remove_segment',{id:s.id}).catch(this.onError);
    for(const delta of [-1,1]){
      const index=view.segments.findIndex(row=>row.id===s.id),button=document.createElement('button');
      button.className='button subtle';button.textContent=delta<0?'↑ В списке':'↓ В списке';button.title='Меняет порядок списка; временные окна сохраняются';button.disabled=index+delta<0||index+delta>=view.segments.length;
      button.onclick=()=>{const order=view.segments.map(row=>row.id);[order[index],order[index+delta]]=[order[index+delta],order[index]];this.write('reorder_segments',{order}).catch(this.onError);};root.querySelector('.story-header').append(button);
    }
    root.querySelectorAll('[data-edit]').forEach(b=>b.onclick=()=>this.itemForm(s,s.items.find(i=>i.id===b.dataset.edit)).catch(this.onError));
    root.querySelectorAll('[data-open]').forEach(b=>b.onclick=()=>this.openBlock(b.dataset.open));
    root.querySelectorAll('[data-insert]').forEach(b=>b.onclick=()=>this.itemForm(s,null,false,Number(b.dataset.insert)).catch(this.onError));
    root.querySelectorAll('[data-remove]').forEach(b=>b.onclick=()=>this.write('remove_item',{segment_id:s.id,id:b.dataset.remove}).catch(this.onError));
    for(const direction of ['up','down'])root.querySelectorAll('[data-'+direction+']').forEach(b=>b.onclick=()=>{const order=s.items.map(i=>i.id),from=order.indexOf(b.dataset[direction]),to=from+(direction==='up'?-1:1);[order[from],order[to]]=[order[to],order[from]];this.write('reorder_items',{segment_id:s.id,order}).catch(this.onError);});
  }
  async form(title,html,apply){
    await this.flush();const pid=this.project().id,revision=this.project().revision;
    this.modal(title,`<form id="storyForm" class="modal-form">${html}<p id="storyFormError" role="status"></p><button class="button primary">Сохранить</button></form>`);
    const form=document.querySelector('#storyForm');
    form.onsubmit=async e=>{e.preventDefault();const button=form.querySelector('button');button.disabled=true;
      try{if(this.project().id!==pid)throw new Error('Проект изменился.');await apply(new FormData(form),revision);this.closeModal();}
      catch(error){form.querySelector('#storyFormError').textContent=error.message;}finally{button.disabled=false;}};
    return form;
  }
  field(name,title,value='',type='text'){return `<label class="field"><span>${title}</span><input name="${name}" type="${type}" ${type==='number'?'min="0" step="any"':''} value="${esc(String(value??''))}" ${type==='text'?'required maxlength="160"':''}></label>`;}
  async segmentForm(s=null){
    const html=this.field('name','Название сегмента',s?.name??'')+`<label class="field"><span>Что происходит в сегменте</span><textarea name="description" maxlength="12000">${esc(s?.description??'')}</textarea></label><div class="story-form-row">`+this.field('start','Начало в ролике, с',this.seconds(s?.start_frame),'number')+this.field('end','Конец в ролике, с',s?.start_frame!=null&&s?.duration_frames!=null?this.seconds(s.start_frame+s.duration_frames):'','number')+this.field('duration','Окно сегмента, с',this.seconds(s?.duration_frames),'number')+'</div><p class="small-note">Неизвестное время можно оставить пустым. Изменение окна не сдвигает соседние сегменты.</p>';
    const form=await this.form(s?'Изменить сегмент':'Новый сценарный сегмент',html,async(data,revision)=>{const n=windowFromEdit(Object.fromEntries(data),'duration',this.project().fps);const p=await this.write(s?'update_segment':'add_segment',{...(s?{id:s.id}:{}),name:data.get('name'),description:data.get('description'),start_frame:n.start,duration_frames:n.duration},revision);if(p&&!s)this.selected=p.timeline.scenario.segments.at(-1).id;if(p)this.showStory();});
    for(const name of ['start','end','duration'])form.elements[name].oninput=form.elements[name].onchange=()=>{const value=windowFromEdit(Object.fromEntries(new FormData(form)),name,this.project().fps);for(const key of ['start','end','duration'])form.elements[key].value=this.seconds(value[key]);};
  }
  async itemForm(s,item=null,library=false,index=null){
    const block=item?this.project().blocks.find(b=>b.id===item.block_id):null;
    const html=(library?`<label class="field"><span>Источник из библиотеки</span><select name="block_id">${this.project().blocks.map(b=>`<option value="${b.id}">${esc(b.label+' · '+b.title)}</option>`).join('')}</select></label>`:'')+this.field('title','Название блока',item?.title??'')+`<label class="field"><span>Что происходит в блоке</span><textarea name="description" maxlength="12000">${esc(item?.description??'')}</textarea></label><div class="story-form-row">`+this.field('duration','Плановая длительность, с',this.seconds(item?.duration_frames),'number')+this.field('gap','Пауза перед блоком, с',this.seconds(item?.gap_before_frames??0),'number')+'</div>'+(item?`<label class="field"><span>Видео в этом использовании</span><select name="take_id"><option value="">Заглушка</option>${block.takes.map(t=>`<option value="${t.id}" ${t.id===item.take_id?'selected':''}>${esc(t.name)}</option>`).join('')}</select></label>`+this.field('source','Вход в дубль, с',this.seconds(item.source_in_frame),'number'):'');
    await this.form(item?'Постановка блока':'Добавить блок',html,async(data,revision)=>{const frame=name=>data.get(name)===''?null:Math.round(Number(data.get(name))*this.project().fps);await this.write(item?'update_item':'add_item',{segment_id:s.id,...(item?{id:item.id}:{...(index==null?{}:{index}),...(library?{block_id:data.get('block_id')}: {})}),title:data.get('title'),description:data.get('description'),duration_frames:frame('duration'),gap_before_frames:frame('gap')??0,...(item?{take_id:data.get('take_id')||null,source_in_frame:frame('source')??0}:{})},revision);});
  }
}
