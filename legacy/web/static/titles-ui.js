import {escapeHtml as esc} from './logic.js';
import {toFrame,frameCode} from './timeline-logic.js';
import {titleIssue,placeTitleImage} from './titles-logic.js';

export class TitlesDesk{
  constructor(options){Object.assign(this,options);this.serial=0;}
  active(root,pid){return this.getProject()?.id===pid&&root?.isConnected&&root.closest('dialog')?.open!==false&&document.querySelector('#titleForm')===root;}
  async openList(){
    const pid=this.getProject().id;await this.flush();if(this.getProject().id!==pid)return;this.pause();this.serial++;
    const p=this.getProject(),t=this.getTimeline(),rows=t.titles??[];
    this.modal('Титры проекта',`<div class="titles-list"><p class="small-note">Общее время ролика · титры сохраняют свои позиции при монтаже. На чёрном фоне изображение плавно закрывается вместе с появлением текста; музыка продолжается.</p><button id="titleNew" class="button primary">+ Титр</button>${rows.length?rows.map(r=>`<button class="title-list-row" data-title="${r.id}"><strong>${esc(r.name)}</strong><span>${frameCode(r.start_frame,t.fps)} → ${frameCode(r.start_frame+r.duration_frames,t.fps)} · ${r.background==='black'?'Чёрный фон':'Поверх видео'}</span><small>${esc(r.text)}</small></button>`).join(''):'<p class="empty-state">Добавьте название, реплику или финальные титры.</p>'}</div>`);
    const root=document.querySelector('.titles-list');root.querySelector('#titleNew').onclick=()=>this.open().catch(this.onError);
    root.querySelectorAll('[data-title]').forEach(b=>b.onclick=()=>this.open(b.dataset.title).catch(this.onError));
  }
  async open(id=null){
    const pid=this.getProject().id,serial=++this.serial;await this.flush();if(this.getProject().id!==pid)return;this.pause();
    const source=document.querySelector('#modalBody')?.firstElementChild,dialog=document.querySelector('#modal'),wasOpen=dialog?.open;
    const fonts=await this.api('/api/title-fonts');if(this.serial!==serial||this.getProject()?.id!==pid||(source&&(source!==document.querySelector('#modalBody')?.firstElementChild||(wasOpen&&dialog.open===false))))return;
    if(!fonts.length)throw new Error('Нет доступных шрифтов с кириллицей.');
    const p=this.getProject(),t=this.getTimeline(),fps=t.fps,revision=p.revision,old=(t.titles??[]).find(r=>r.id===id);
    if(id&&!old)throw new Error('Титр изменился. Откройте редактор заново.');
    const row=old??{name:'Титр',text:'Сказки истинной луны',start_frame:this.getCursor?.()??0,duration_frames:fps*3,fade_in_frames:Math.round(fps*.25),fade_out_frames:Math.round(fps*.25),font_id:fonts[0].id,font_size:Math.max(8,Math.min(256,Math.round(p.output_size[1]*.07))),color:'#FFFFFF',align:'center',position:'center',background:'transparent',outline:true};
    const seconds=n=>Number((n/fps).toFixed(6)),select=(name,label,choices)=>`<label class="field"><span>${label}</span><select name="${name}">${choices.map(([v,label])=>`<option value="${v}" ${row[name]===v?'selected':''}>${esc(label)}</option>`).join('')}</select></label>`,num=(name,label,value,min=0)=>`<label class="field"><span>${label}</span><input name="${name}" type="number" min="${min}" step="any" value="${value}" required></label>`;
    this.modal(old?'Изменить титр':'Новый титр',`<form id="titleForm" class="title-form"><div class="title-editor-grid"><div class="title-fields"><label class="field"><span>Название в проекте</span><input name="name" value="${esc(row.name)}" maxlength="160" required></label><label class="field"><span>Текст · Enter для новой строки</span><textarea name="text" rows="3" maxlength="1000" required>${esc(row.text)}</textarea></label><div class="timeline-dialog-row">${select('background','Размещение',[['transparent','Поверх видео'],['black','На чёрном фоне']])}${select('font_id','Шрифт',fonts.map(f=>[f.id,f.label]))}</div><div class="timeline-dialog-row">${num('font_size','Размер, px проекта',row.font_size,8)}<label class="field title-color"><span>Цвет</span><input name="color" type="color" value="${row.color}"></label></div><div class="timeline-dialog-row">${select('align','Выравнивание',[['left','Слева'],['center','По центру'],['right','Справа']])}${select('position','Положение',[['upper','Вверху'],['center','В центре'],['lower','Внизу']])}</div><label class="title-outline"><input name="outline" type="checkbox" ${row.outline?'checked':''}> Тонкая чёрная обводка</label></div><div><div id="titleStylePreview" class="title-style-preview" style="aspect-ratio:${p.output_size[0]}/${p.output_size[1]}" aria-label="Предпросмотр оформления титра"></div><p class="small-note">Точное оформление в размере проекта ${p.output_size.join(' × ')}. Автоперенос по ширине кадра. Движение и плавность проверяются в общем монтаже.</p><div class="timeline-dialog-row">${num('start','Начало в ролике, с',seconds(row.start_frame))}${num('duration','Длительность, с',seconds(row.duration_frames),1/fps)}</div><div class="timeline-dialog-row">${num('fade_in','Появление, с',seconds(row.fade_in_frames))}${num('fade_out','Исчезновение, с',seconds(row.fade_out_frames))}</div><output id="titleTiming"></output><p class="small-note">Окно включает оба затухания. Титр не сдвигает клипы и музыкальные акценты. Поздний титр продлевает ролик.</p></div></div><p id="titleError" role="status"></p><div class="title-actions"><button type="submit" class="button primary">Сохранить титр</button>${old?'<button type="button" id="titleRemove" class="button subtle">Удалить титр</button>':''}<span id="titlePreviewStatus" role="status"></span></div></form>`,true);
    const root=document.querySelector('#titleForm'),error=root.querySelector('#titleError'),button=root.querySelector('button[type="submit"]'),status=root.querySelector('#titlePreviewStatus');let pending=false,previewSerial=0,timer;
    const values=()=>{const f=root.elements;return {name:f.namedItem('name').value.trim(),text:f.text.value.replace(/\r\n?/g,'\n').replace(/\t/g,'    '),background:f.background.value,font_id:f.font_id.value,font_size:Number(f.font_size.value),color:f.color.value,align:f.align.value,position:f.position.value,outline:f.outline.checked,start_frame:toFrame(f.start.value,fps),duration_frames:toFrame(f.duration.value,fps),fade_in_frames:toFrame(f.fade_in.value,fps),fade_out_frames:toFrame(f.fade_out.value,fps)};};
    const preview=async(r,version)=>{
      if(!this.active(root,pid)||previewSerial!==version)return;
      try{
        const data=await this.api(`/api/projects/${encodeURIComponent(pid)}/title-preview`,'POST',{expected_revision:revision,title:r});
        if(!this.active(root,pid)||previewSerial!==version)return;
        const img=document.createElement('img');img.alt='';img.src=data.layer.url;placeTitleImage(img,data.layer);if(img.decode)await img.decode();
        if(!this.active(root,pid)||previewSerial!==version)return;
        const box=root.querySelector('#titleStylePreview');box.replaceChildren(img);box.classList.toggle('black',r.background==='black');box.dataset.layer=data.layer.key;status.textContent='Оформление готово';error.textContent='';
      }catch(e){if(this.active(root,pid)&&previewSerial===version){error.textContent=e.message;status.textContent='';}}
    };
    const update=()=>{
      clearTimeout(timer);const r=values(),issue=titleIssue(r),version=++previewSerial;error.textContent=issue;button.disabled=pending||!!issue;
      root.querySelector('#titleTiming').textContent=issue?'':`${frameCode(r.start_frame,fps)} → ${frameCode(r.start_frame+r.duration_frames,fps)} · ${fps} fps`;
      status.textContent=issue?'':'Подготовка оформления…';if(!issue)timer=setTimeout(()=>preview(r,version),300);
    };
    root.oninput=update;root.onchange=update;
    const submit=async(action,payload)=>{
      if(pending||!this.active(root,pid))return;pending=true;clearTimeout(timer);previewSerial++;root.querySelectorAll('button').forEach(b=>b.disabled=true);
      try{const result=await this.write(action,payload,{expectedRevision:revision,throwErrors:true});if(result&&this.active(root,pid))this.closeModal();}
      catch(e){if(this.active(root,pid))error.textContent=e.message;}
      finally{pending=false;if(root.isConnected){button.disabled=!!titleIssue(values());root.querySelector('#titleRemove')?.removeAttribute('disabled');}}
    };
    root.onsubmit=e=>{e.preventDefault();const r=values(),issue=titleIssue(r);if(issue){error.textContent=issue;return;}return submit(old?'update_title':'add_title',{...r,...(old?{id:old.id}:{})});};
    if(old)root.querySelector('#titleRemove').onclick=()=>submit('remove_title',{id:old.id});
    update();
  }
}
