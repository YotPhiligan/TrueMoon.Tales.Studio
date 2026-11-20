import {escapeHtml as esc} from './logic.js';
import {scenarioView,windowFromEdit} from './scenario-logic.js';
import {frameCode} from './timeline-logic.js';
import {animaticFrame} from './playback-logic.js';
import {assetSelection} from './asset-library.js';
import {segmentTypeIcon,segmentTypeLabel,segmentTypeOptions} from './segment-types.js';

const itemNotes=[['sound_notes','Звук и реплики'],['continuity_notes','Стык со следующим блоком']];

export class ScenarioDesk{
  constructor(options){Object.assign(this,options);this.selected=null;this.pending=false;this.overview=false;this.descriptionDrafts=new Map();}
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
  open(id){this.selected=id;this.overview=false;this.showStory();}
  showOverview(){this.overview=true;this.showStory();}
  hasDescriptionDraft(){return [...this.descriptionDrafts.values()].some(d=>d.saving||d.value!==d.original);}
  renderOverview(root,view){
    const {id:projectId,revision,fps,name}=this.project(),draft=this.descriptionDrafts.get(projectId);
    const sameProject=root.dataset.scriptProject===projectId,scroll=sameProject?root.scrollTop:0;
    const active=root.querySelector('#scriptDescription');
    const selection=sameProject&&active===document.activeElement?[active.selectionStart,active.selectionEnd,active.selectionDirection]:null;
    root.dataset.scriptProject=projectId;
    let draftRow=null;
    const text=(segment,item,field,label)=>{
      const row=item??segment,value=row[field]??'',itemId=item?.id??null;
      if(draft?.segmentId===segment.id&&draft.itemId===itemId&&draft.field===field){
        draftRow=row;
        return this.descriptionForm(draft,row);
      }
      const emptyNote=field!=='description'&&!value;
      const content=emptyNote?'+ '+label:'<span>'+label+'</span>'+
        '<span class="script-text'+(value?'':' muted')+'">'+esc(value||'Добавить описание')+'</span>';
      return '<button type="button" class="'+(emptyNote?'subtle-link script-add-note':'script-description')+
        '" data-script-edit="'+field+'" data-script-label="'+label+'" '+(draft?'disabled ':'')+
        'aria-label="'+esc(label+' · '+(item?.title??segment.name))+'">'+content+'</button>';
    };
    const timing=row=>{
      const start=row.start_frame,frames=row.duration_frames;
      const time=start==null?'<span>Время не задано</span>':
        '<button type="button" class="subtle-link script-time" data-script-seek="'+start+'" title="Перейти к началу на шкале">'+
        frameCode(start,fps)+(frames==null?'':' → '+frameCode(start+frames,fps))+'</button>';
      return '<div class="script-timing">'+time+'<span>'+(frames==null?'Длина не задана':this.seconds(frames)+' с')+'</span></div>';
    };
    const issues=new Map();
    for(const issue of view.issues){
      if(!issues.has(issue.segment_id))issues.set(issue.segment_id,[]);
      issues.get(issue.segment_id).push(issue.message);
    }
    const sections=view.segments.map(segment=>'<section class="script-segment" data-script-segment="'+esc(segment.id)+'">'+
      '<header><div><span class="story-segment-type">'+segmentTypeIcon(segment.segment_type)+segmentTypeLabel(segment.segment_type)+'</span>'+
      '<h2>'+esc(segment.name)+'</h2>'+timing(segment)+'</div>'+
      '<button type="button" class="button subtle small" data-script-open>Карточка сегмента</button></header>'+
      '<div data-script-row="'+esc(segment.id)+'">'+text(segment,null,'description','Что меняется к концу сегмента')+'</div>'+
      (issues.has(segment.id)?'<ul class="script-issues">'+issues.get(segment.id).map(message=>'<li>'+esc(message)+'</li>').join('')+'</ul>':'')+
      (segment.items.length?'<ol class="script-blocks">'+segment.items.map(item=>
        '<li data-script-row="'+esc(item.id)+'"><div class="script-block-heading"><h3>'+esc(item.title)+'</h3>'+timing(item)+'</div>'+
        text(segment,item,'description','В кадре')+'<div class="script-block-notes">'+
        itemNotes.map(([field,label])=>text(segment,item,field,label)).join('')+'</div></li>').join('')+'</ol>':'<p class="small-note">В сегменте пока нет блоков.</p>')+'</section>').join('');
    root.innerHTML='<div id="scriptOverview" class="script-overview"><header class="story-header"><div>'+
      '<span class="eyebrow">СЦЕНАРИЙ ПРОЕКТА</span><h1>'+esc(name)+'</h1></div>'+
      '<button type="button" id="scriptAddSegment" class="button subtle">+ Сегмент</button></header>'+
      '<p class="small-note">Нажмите на описание или заметку для правки. Время переносит курсор к началу строки.</p>'+
      (draft&&!draftRow?this.descriptionForm(draft,null):'')+
      (sections||'<p class="story-empty">Создайте первый сегмент, чтобы начать сценарий.</p>')+'</div>';
    const overview=root.querySelector('#scriptOverview');
    overview.onclick=event=>{
      const button=event.target.closest('button');
      if(!button)return;
      if(button.id==='scriptAddSegment'){this.segmentForm().catch(this.onError);return;}
      if(button.id==='scriptCancelDescription'){this.finishDescription(draft);return;}
      const section=button.closest('[data-script-segment]');
      if(!section)return;
      const segment=view.segments.find(s=>s.id===section.dataset.scriptSegment);
      if(button.hasAttribute('data-script-open'))this.open(segment.id);
      if(button.hasAttribute('data-script-seek')){
        this.selected=segment.id;
        this.tree();
        this.seekScriptFrame(Number(button.dataset.scriptSeek));
      }
      if(button.hasAttribute('data-script-edit')){
        const id=button.closest('[data-script-row]').dataset.scriptRow;
        const item=segment.items.find(i=>i.id===id),field=button.dataset.scriptEdit,original=(item??segment)[field]??'';
        this.descriptionDrafts.set(projectId,{projectId,segmentId:segment.id,itemId:item?.id??null,
          field,label:button.dataset.scriptLabel,original,value:original,error:'',saving:false});
        this.render();
        root.querySelector('#scriptDescription')?.focus();
      }
    };
    const form=root.querySelector('#scriptDescriptionForm'),input=root.querySelector('#scriptDescription');
    if(form){
      input.oninput=()=>{draft.value=input.value;};
      form.onsubmit=event=>{
        event.preventDefault();
        if(draftRow)this.saveDescription(draft,draftRow[draft.field]??'',revision);
      };
      if(selection){input.focus({preventScroll:true});input.setSelectionRange(...selection);}
    }
    root.scrollTop=scroll;
  }
  descriptionForm(draft,row){
    const current=row?.[draft.field]??'',conflict=row&&current!==draft.original;
    const kind=draft.field==='description'?'описание':'заметку';
    return '<form id="scriptDescriptionForm" class="script-description-form"><label for="scriptDescription">'+draft.label+'</label>'+
      '<textarea id="scriptDescription" name="'+draft.field+'" rows="4" maxlength="12000" '+(draft.saving?'readonly':'')+'>'+
      '\n'+esc(draft.value)+'</textarea>'+
      (!row?'<p class="script-conflict">Эта строка удалена из сценария. Черновик можно скопировать.</p>':
        conflict?'<div class="script-conflict"><strong>Сохранённая версия изменилась</strong><p class="script-text">'+
          esc(current||'Поле пустое.')+'</p></div>':'')+
      '<div class="script-form-actions"><button class="button primary small" '+(!row||draft.saving?'disabled':'')+'>'+
      (draft.saving?'Сохраняется…':(conflict?'Заменить ':'Сохранить ')+kind)+'</button>'+
      '<button type="button" id="scriptCancelDescription" class="button subtle small" '+(draft.saving?'disabled':'')+'>Отмена</button>'+
      '<span class="script-conflict" role="status">'+esc(draft.error)+'</span></div></form>';
  }
  finishDescription(draft){
    this.descriptionDrafts.delete(draft.projectId);
    const root=document.querySelector('#scenarioView');
    if(this.project().id!==draft.projectId||root.hidden||!this.overview)return;
    this.render();
    const id=CSS.escape(draft.itemId??draft.segmentId);
    (root.querySelector('[data-script-row="'+id+'"] [data-script-edit="'+draft.field+'"]')??root.querySelector('#scriptAddSegment'))?.focus({preventScroll:true});
  }
  async saveDescription(draft,current,revision){
    if(draft.saving)return;
    if(draft.value===current){this.finishDescription(draft);return;}
    draft.saving=true;
    draft.error='';
    this.render();
    const {segmentId,itemId,field,value}=draft;
    const payload=itemId?{segment_id:segmentId,id:itemId,[field]:value}:{id:segmentId,description:value};
    try{
      const project=await this.write(itemId?'update_item':'update_segment',payload,revision);
      if(project)this.finishDescription(draft);
    }catch(error){
      draft.error=error.status===409?'Проект обновился. Черновик сохранён в редакторе. Дождитесь обновления и повторите.':error.message;
    }finally{
      draft.saving=false;
      if(this.descriptionDrafts.get(draft.projectId)===draft&&this.project().id===draft.projectId&&
        this.overview&&!document.querySelector('#scenarioView').hidden)this.render();
    }
  }
  render(){
    const view=this.view(),s=view.segments.find(s=>s.id===this.selected)??view.segments[0];this.selected=s?.id??null;
    const root=document.querySelector('#scenarioView');
    if(this.overview){this.renderOverview(root,view);return;}
    if(root.dataset.scriptProject){delete root.dataset.scriptProject;root.scrollTop=0;}
    if(!s){root.innerHTML='<div class="story-intro"><span class="eyebrow">СЦЕНАРИЙ ПРОЕКТА</span><h1>Сначала — что происходит в кадре</h1><p>Создайте сегмент, опишите действие и добавьте блоки. Материалы и дубли можно подготовить позже.</p><button class="button primary" id="storyCreateFirst">Добавить сегмент</button></div>';root.querySelector('button').onclick=()=>this.segmentForm().catch(this.onError);return;}
    const issues=view.issues.filter(i=>i.segment_id===s.id),budget=s.remaining_frames;
    root.innerHTML=`<header class="story-header"><div><div class="story-heading-title"><h1>${esc(s.name)}</h1><button type="button" id="storyEditSegment" class="icon-button" aria-label="Изменить сегмент" title="Изменить сегмент"><svg class="icon" aria-hidden="true"><use href="#i-pencil"/></svg></button></div><span class="story-segment-type">${segmentTypeIcon(s.segment_type)}${segmentTypeLabel(s.segment_type)}</span></div></header><p class="story-description">${esc(s.description||'Описание пока не задано.')}</p><div class="story-budget"><span>Начало <b>${this.seconds(s.start_frame)||'—'} с</b></span><span>Конец <b>${s.start_frame!=null&&s.duration_frames!=null?this.seconds(s.start_frame+s.duration_frames):'—'} с</b></span><span>Окно <b>${this.seconds(s.duration_frames)||'—'} с</b></span><span class="${budget<0?'story-error':''}">${budget==null?'Требуется расчёт':budget<0?'Не хватает '+this.seconds(-budget)+' с':'Свободно '+this.seconds(budget)+' с'}</span></div>${issues.length?`<div class="story-alert" role="status">${issues.map(i=>esc(i.message)).join('<br>')}<small>План сохранён. Предыдущий допустимый монтаж остаётся доступен; исправьте конфликт перед воспроизведением.</small></div>`:''}<div class="story-actions"><button id="storyAddItem" class="button primary">+ Пустой блок</button><button id="storyAgentNew" class="button subtle">+ Блок с ИИ</button><button id="storyAgentBreakdown" class="button subtle">Предложить разбивку</button><button id="storyAddSource" class="button subtle">+ Из библиотеки</button><button id="storyPreviewSegment" class="button subtle">▶ Просмотр сегмента</button><button id="storyPreviewAnimatic" class="button subtle">▶ Аниматик</button><button id="storyCompact" class="button subtle" title="Сдвинуть внутренние блоки вплотную, сохранив первое начало, закрепления и защищённые паузы">Убрать промежутки</button><button id="storyOpenTimeline" class="button subtle">Общий монтаж</button><span>Окно фиксировано · блоки следуют по порядку</span></div><div class="story-cards">${s.items.map((i,index)=>{
      const b=this.project().blocks.find(b=>b.id===i.block_id),take=b?.takes.find(t=>t.id===i.take_id),poster=take?.last_frame_asset_id??animaticFrame(i,b)??b?.references[0]?.asset_id;
      const soundCount=(this.project().timeline?.sound_events??[]).filter(e=>e.clip_id===i.id).length;
      return `<article class="story-card" data-story-item="${esc(i.id)}"><div class="story-card-image">${poster?`<img src="${this.media(poster)}" alt="" loading="lazy">`:`<span>${String(index+1).padStart(2,'0')}</span>`}</div><div class="story-card-main"><small>${i.start_frame==null?'Позиция не определена':this.seconds(i.start_frame)+' с'} · ${this.seconds(i.duration_frames)||'Длина не задана'}${i.duration_frames==null?'':' с'} · ${i.take_id?'Выбран дубль':'Заглушка'}</small><h2>${esc(i.title)}</h2><p>${esc(i.description||'Описание действия пока не задано.')}</p>${this.blockNotesSummary(i)}<span class="story-source">Источник: ${esc(b?.label??'—')}${take?' · '+esc(take.name):''}${i.gap_before_frames?' · пауза перед блоком '+this.seconds(i.gap_before_frames)+' с':''}</span><div class="story-card-buttons"><button class="subtle-link" data-edit="${i.id}">Изменить блок</button><button class="subtle-link" data-open="${i.block_id}">Материалы и промпт</button><button class="subtle-link" data-animatic-frame="${esc(i.id)}" title="${esc(this.project().assets[i.animatic_asset_id]?.name??'Выбрать стоп-кадр')}">Кадр аниматика</button><button class="subtle-link" data-agent-improve="${i.id}">Улучшить с ИИ</button><button class="subtle-link" data-up="${i.id}" ${index===0?'disabled':''}>↑</button><button class="subtle-link" data-down="${i.id}" ${index===s.items.length-1?'disabled':''}>↓</button><button class="subtle-link" data-insert="${index+1}">+ После</button><button class="subtle-link" data-remove="${i.id}">Убрать</button></div>${this.sounds?`<details class="story-tracks" ${soundCount?'open':''}><summary>Дорожки блока · ${take?'1 видео':'без видео'} · ${soundCount} аудио</summary><section data-block-sounds="${i.id}"></section></details>`:''}</div></article>`;
    }).join('')||'<p class="story-empty">Пока нет блоков. Можно начать с одного предложения о действии.</p>'}</div><section id="storyAgentTasks" class="story-agent-tasks" aria-label="Задания агенту для сегмента"></section><footer class="story-footer"><button class="subtle-link" id="storyRemoveSegment">Убрать сегмент из сценария</button><span>Исходники и дубли сохраняются в библиотеке. Отмена доступна на таймлайне.</span></footer>`;
    root.querySelector('#storyEditSegment').onclick=()=>this.segmentForm(s).catch(this.onError);
    root.querySelector('#storyAddItem').onclick=()=>this.itemForm(s).catch(this.onError);
    root.querySelector('#storyCompact').onclick=()=>this.write('compact_segment',{segment_id:s.id}).catch(this.onError);
    root.querySelector('#storyAddSource').onclick=()=>(this.addFromLibrary?this.addFromLibrary(s):this.itemForm(s,null,true)).catch(this.onError);
    root.querySelector('#storyAgentNew').onclick=()=>this.agent.form(s).catch(this.onError);
    root.querySelector('#storyAgentBreakdown').onclick=()=>this.agent.form(s,null,{breakdown:true}).catch(this.onError);
    root.querySelectorAll('[data-agent-improve]').forEach(b=>b.onclick=()=>this.agent.form(s,s.items.find(i=>i.id===b.dataset.agentImprove)).catch(this.onError));
    this.agent.render(s,root.querySelector('#storyAgentTasks'));
    if(this.sounds){const sound=document.createElement('section');root.querySelector('.story-cards').after(sound);this.sounds.render(s,sound);root.querySelectorAll('[data-block-sounds]').forEach(node=>this.sounds.render(s,node,s.items.find(i=>i.id===node.dataset.blockSounds)));}
    root.querySelector('#storyPreviewSegment').disabled=issues.length>0;root.querySelector('#storyPreviewSegment').onclick=()=>this.preview(s.id);
    root.querySelector('#storyPreviewAnimatic').disabled=issues.length>0;root.querySelector('#storyPreviewAnimatic').onclick=()=>this.preview(s.id,{animatic:true});
    root.querySelector('#storyOpenTimeline').onclick=()=>this.previewAll();
    root.querySelector('#storyRemoveSegment').onclick=()=>this.write('remove_segment',{id:s.id}).catch(this.onError);
    for(const delta of [-1,1]){
      const index=view.segments.findIndex(row=>row.id===s.id),button=document.createElement('button');
      button.className='button subtle';button.textContent=delta<0?'↑ В списке':'↓ В списке';button.title='Меняет порядок списка; временные окна сохраняются';button.disabled=index+delta<0||index+delta>=view.segments.length;
      button.onclick=()=>{const order=view.segments.map(row=>row.id);[order[index],order[index+delta]]=[order[index+delta],order[index]];this.write('reorder_segments',{order}).catch(this.onError);};root.querySelector('.story-header').append(button);
    }
    root.querySelectorAll('[data-edit]').forEach(b=>b.onclick=()=>this.itemForm(s,s.items.find(i=>i.id===b.dataset.edit)).catch(this.onError));
    root.querySelectorAll('[data-open]').forEach(b=>b.onclick=()=>this.openBlock(b.dataset.open,s.id,b.closest('[data-story-item]').dataset.storyItem));
    root.querySelectorAll('[data-animatic-frame]').forEach(b=>b.onclick=()=>this.pickAnimaticFrame(s,s.items.find(i=>i.id===b.dataset.animaticFrame)).catch(this.onError));
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
    const html=this.field('name','Название сегмента',s?.name??'')+`<label class="field"><span>Тип сегмента</span><select name="segment_type">${segmentTypeOptions(s?.segment_type)}</select></label><label class="field"><span>Что меняется к концу сегмента</span><textarea name="description" maxlength="12000" placeholder="Какое состояние, решение или событие отличает конец сегмента от начала?">${esc(s?.description??'')}</textarea></label><div class="story-form-row">`+this.field('start','Начало в ролике, с',this.seconds(s?.start_frame),'number')+this.field('end','Конец в ролике, с',s?.start_frame!=null&&s?.duration_frames!=null?this.seconds(s.start_frame+s.duration_frames):'','number')+this.field('duration','Окно сегмента, с',this.seconds(s?.duration_frames),'number')+'</div><p class="small-note">Неизвестное время можно оставить пустым. Изменение окна не сдвигает соседние сегменты.</p>';
    const form=await this.form(s?'Изменить сегмент':'Новый сценарный сегмент',html,async(data,revision)=>{const n=windowFromEdit(Object.fromEntries(data),'duration',this.project().fps);const p=await this.write(s?'update_segment':'add_segment',{...(s?{id:s.id}:{}),name:data.get('name'),description:data.get('description'),segment_type:data.get('segment_type')||null,start_frame:n.start,duration_frames:n.duration},revision);if(p&&!s)this.open(p.timeline.scenario.segments.at(-1).id);else if(p)this.showStory();});
    for(const name of ['start','end','duration'])form.elements[name].oninput=form.elements[name].onchange=()=>{const value=windowFromEdit(Object.fromEntries(new FormData(form)),name,this.project().fps);for(const key of ['start','end','duration'])form.elements[key].value=this.seconds(value[key]);};
  }
  libraryPreview(form,block,chosen){
    const target=form.querySelector('[data-library-preview]');
    target.querySelector('video')?.pause();
    const image=chosen?.last_frame_asset_id??block.references.find(r=>block.guides.some(g=>g.reference_id===r.id&&g.frame===0))?.asset_id??block.references[0]?.asset_id;
    target.innerHTML=chosen?.video_asset_id
      ?`<video controls playsinline preload="metadata" aria-label="Предпросмотр дубля" src="${this.media(chosen.video_asset_id)}" ${image?`poster="${this.media(image)}"`:''}></video>`
      :image?`<img src="${this.media(image)}" alt="Опорный кадр блока">`:'<p class="small-note">У источника нет видео или опорного кадра.</p>';
  }
  blockNotesSummary(item){
    const notes=itemNotes.filter(([field])=>item[field]);
    return notes.length?'<dl class="story-block-notes">'+notes.map(([field,label])=>
      '<dt>'+label+'</dt><dd class="script-text">'+esc(item[field])+'</dd>').join('')+'</dl>':'';
  }
  blockNotesFields(item){
    return '<details class="story-note-fields" '+(itemNotes.some(([field])=>item?.[field])?'open':'')+'>'+
      '<summary>Звук и стык</summary>'+itemNotes.map(([field,label])=>
        '<label class="field"><span>'+label+'</span><textarea name="'+field+'" rows="3" maxlength="12000">'+
        '\n'+esc(item?.[field]??'')+'</textarea></label>').join('')+'</details>';
  }
  blockNoteValues(data){
    return Object.fromEntries(itemNotes.map(([field])=>[field,data.get(field)]));
  }
  async catalogVideoForm(s,asset,info,streams){
    const fps=this.project().fps,capacity=Math.floor(info.seconds*fps+1e-6);
    const duration=s.remaining_frames>0?Math.min(s.remaining_frames,capacity):capacity;
    const sound=streams?'<label class="field"><span>Звуковой поток</span><select name="stream">'+
      streams.map(stream=>'<option value="'+stream.index+'">'+(stream.index+1)+' · '+esc(stream.title||stream.language||stream.codec)+'</option>').join('')+'</select></label>':'';
    const html='<p class="small-note">'+esc(asset.path)+'</p>'+sound+this.field('title','Название блока',asset.name.replace(/\.[^.]+$/,'').slice(0,160))+
      '<label class="field"><span>В кадре</span><textarea name="description" maxlength="12000"></textarea></label>'+this.blockNotesFields()+'<div class="story-form-row">'+
      this.field('source','Вход в видео, с','0','number')+this.field('duration','Плановая длительность, с',this.seconds(duration),'number')+
      this.field('gap','Пауза перед блоком, с','0','number')+'</div>';
    return this.form(streams?'Добавить видео и звук':'Добавить только видео',html,async(data,revision)=>{
      const frame=name=>data.get(name)===''?null:Math.round(Number(data.get(name))*fps);
      await this.write('add_catalog_video',{asset_selection:assetSelection(asset),segment_id:s.id,title:data.get('title'),
        description:data.get('description'),...this.blockNoteValues(data),source_in_frame:frame('source')??0,duration_frames:frame('duration'),
        gap_before_frames:frame('gap')??0,...(streams?{audio_stream:Number(data.get('stream'))}:{})},revision);
    });
  }
  async itemForm(s,item=null,library=false,index=null){
    const blocks=this.project().blocks,block=item?blocks.find(b=>b.id===item.block_id):null;
    if(library&&!blocks.length)throw new Error('Добавьте видео или блок в библиотеку источников.');
    const source=library?`<label class="field"><span>Источник из библиотеки</span><select name="block_id">${blocks.map(b=>`<option value="${b.id}">${esc(b.label===b.title?b.title:b.label+' · '+b.title)}</option>`).join('')}</select></label>`:'';
    const takes=item||library?`<label class="field"><span>Видео в этом использовании</span><select name="take_id"></select></label>${library?'<div class="story-library-preview" data-library-preview></div>':''}${this.field('source','Вход в дубль, с',this.seconds(item?.source_in_frame??0),'number')}<label class="story-native-audio"><input type="checkbox" name="mute_native_audio" ${item?.mute_native_audio?'checked':''}> Отключить родной звук видео в этом блоке</label>`:'';
    const html=source+takes+this.field('title','Название блока',item?.title??'')+`<label class="field"><span>В кадре</span><textarea name="description" maxlength="12000">${esc(item?.description??'')}</textarea></label>${this.blockNotesFields(item)}<div class="story-form-row">`+this.field('duration','Плановая длительность, с',this.seconds(item?.duration_frames),'number')+this.field('gap','Пауза перед блоком, с',this.seconds(item?.gap_before_frames??0),'number')+'</div>';
    const form=await this.form(item?'Изменить блок':'Добавить блок',html,async(data,revision)=>{
      const frame=name=>data.get(name)===''?null:Math.round(Number(data.get(name))*this.project().fps);
      await this.write(item?'update_item':'add_item',{
        segment_id:s.id,...(item?{id:item.id}:{...(index==null?{}:{index}),...(library?{block_id:data.get('block_id')}: {})}),
        title:data.get('title'),description:data.get('description'),...this.blockNoteValues(data),duration_frames:frame('duration'),gap_before_frames:frame('gap')??0,
        ...(item||library?{take_id:data.get('take_id')||null,source_in_frame:data.get('take_id')?frame('source')??0:0,...(item?.mute_native_audio||data.has('mute_native_audio')?{mute_native_audio:data.has('mute_native_audio')}:{})}:{})
      },revision);
    });
    if(!item&&!library)return;
    const takeField=form.elements.take_id;
    function setTakes(b,selected){
      takeField.replaceChildren(new Option('Заглушка',''),...b.takes.map(t=>new Option(t.name,t.id)));
      takeField.value=selected??'';
    }
    if(item){setTakes(block,item.take_id);return;}
    this.pause();
    let selected,defaultTitle='',defaultDuration='';
    const preview=()=>{
      const take=selected.takes.find(t=>t.id===takeField.value);
      this.libraryPreview(form,selected,take);
      if(form.elements.duration.value===defaultDuration){
        const seconds=take?.video_info?.seconds??selected.montage_seconds;
        defaultDuration=seconds?this.seconds(Math.floor(seconds*this.project().fps+1e-6)):'';
        form.elements.duration.value=defaultDuration;
      }
    };
    const selectSource=()=>{
      selected=blocks.find(b=>b.id===form.elements.block_id.value);
      if(form.elements.title.value===defaultTitle){
        defaultTitle=(selected.title||selected.label).slice(0,160);
        form.elements.title.value=defaultTitle;
      }
      setTakes(selected,selected.selected_take_id??selected.takes[0]?.id);
      form.elements.source.value='0';
      preview();
    };
    form.elements.block_id.onchange=selectSource;
    takeField.onchange=()=>{form.elements.source.value='0';preview();};
    selectSource();
  }
}
