import {escapeHtml as esc} from './logic.js';
import {clipSpeed,sourceOut} from './clip-edit-logic.js';
import {timelineOf,frameEnd,frameCode,toFrame,takeCapacity,extentFrames,rulerStep,snapFrame,trimClip,placementError} from './timeline-logic.js';
import {TimelineMenu} from './timeline-menu.js';
import {layoutContext,loadDensity,saveDensity} from './timeline-layout.js';
import {timelineJoins,transitionWindows} from './transitions-logic.js';

const colors=['mint','blue','violet','amber','rose','teal'];
const seconds=(n,fps)=>(n/fps).toFixed(3);

export class TimelineView {
  constructor(options){
    Object.assign(this,options);this.projectId=null;this.selection=null;this.drag=null;this.pending=false;
    this.zoom=32;this.cursor=0;this.snapping=true;this.collapsed=false;this.lastSignature='';
  }
  project(){return this.getProject();}
  timelineLayout(){
    const context=layoutContext(this.getLayoutContext?.());this.densityPrefs??={};
    this.densityPrefs[context]??=loadDensity(context);
    if(this.lastLayoutContext!==context){this.compactProperties=false;this.lastLayoutContext=context;}
    const compact=this.densityPrefs[context]==='compact';return {context,compact,collapsed:!compact&&!!this.collapsed};
  }
  setDensity(value){
    const {context}=this.timelineLayout();saveDensity(context,value);this.densityPrefs[context]=value;
    this.collapsed=false;this.compactProperties=false;this.lastSignature='';this.render();
    this.root.querySelector('#tlDensity')?.focus({preventScroll:true});
  }
  syncLayoutControls(){
    const layout=this.timelineLayout(),button=this.root.querySelector('#tlPropertiesToggle');
    if(button){button.disabled=!this.item();button.setAttribute('aria-expanded',String(!!this.compactProperties));}
    this.root.classList.toggle('compact-properties-open',layout.compact&&!!this.compactProperties);
  }
  data(){return this.getTimeline?.()??timelineOf(this.project());}
  media(id){return `/api/media?project=${encodeURIComponent(this.project().id)}&asset=${encodeURIComponent(id)}`;}
  px(frame){return frame*this.zoom/this.data().fps;}
  joinName(j,t=this.data()){
    const name=c=>c.story_title??this.project().blocks.find(b=>b.id===c.block_id)?.label??'Блок';
    return j.cross_segment?`${t.segments.find(s=>s.id===j.left.segment_id)?.name??'Сегмент'} → ${t.segments.find(s=>s.id===j.right.segment_id)?.name??'Сегмент'}`:`${name(j.left)} → ${name(j.right)}`;
  }
  lookup(kind,id){
    if(kind==='join'){const t=this.data(),j=timelineJoins(t).find(j=>j.id===id);return j?{...j,name:this.joinName(j,t)}:null;}
    return kind==='music'?this.data().music:this.data()[kind+'s']?.find(x=>x.id===id);
  }
  item(){return this.selection?this.lookup(this.selection.kind,this.selection.id):null;}
  refreshClipActions(){const button=this.root.querySelector('#tlSplit'),item=this.selection?.kind==='clip'?this.item():null;if(button)button.disabled=this.pending||!item||this.cursor<=item.start_frame||this.cursor>=frameEnd(item);}
  render(){
    const p=this.project();if(!p||this.drag)return;
    if(this.projectId!==p.id){
      this.projectId=p.id;this.selection=null;this.lastSignature='';
      this.cursor=Number(localStorage.getItem('studio-cursor-'+p.id)||0);this.zoom=Number(localStorage.getItem('studio-zoom-'+p.id)||32);
      if(!Number.isFinite(this.cursor)||this.cursor<0)this.cursor=0;
      if(!Number.isFinite(this.zoom)||this.zoom<2||this.zoom>160)this.zoom=32;
    }
    const layout=this.timelineLayout(),collapsed=layout.collapsed;
    const t=this.data(),signature=JSON.stringify([p.id,p.revision,this.zoom,this.cursor,this.selection,collapsed,this.pending,this.getBlock()?.id,layout,!!this.compactProperties]);
    if(signature===this.lastSignature)return;this.lastSignature=signature;
    this.contextMenu?.close(false);
    const old=this.root.querySelector('.tl-scroll'),scroll=old?.scrollLeft??0,focused=this.root.contains(document.activeElement)?document.activeElement.dataset.id:null;
    if(this.selection&&!this.item())this.selection=null;
    if(!this.selection)this.compactProperties=false;
    this.root.className='timeline'+(collapsed?' collapsed':'')+(this.pending?' pending':'')+(layout.compact?' compact':'');
    this.root.dataset.layoutContext=layout.context;this.root.dataset.density=layout.compact?'compact':'full';
    this.root.innerHTML=`<header class="tl-toolbar"><div class="tl-title"><span class="eyebrow">МОНТАЖ ПРОЕКТА</span><strong>Общий таймлайн</strong></div>
      <output id="tlTimecode" class="tl-timecode">${frameCode(this.cursor,t.fps)}</output><span class="tl-fps">${t.fps} fps</span>
      <button id="tlUndo" class="button subtle small" title="Отменить монтажное действие · Ctrl+Z" ${!t.undo.length||this.pending?'disabled':''}>↶</button>
      <button id="tlRedo" class="button subtle small" title="Повторить монтажное действие · Ctrl+Shift+Z" ${!t.redo.length||this.pending?'disabled':''}>↷</button>
      <button id="tlDensity" class="button subtle small" title="${layout.compact?'Развернуть монтажные инструменты':'Уменьшить высоту, сохранив дорожки'}">${layout.compact?'Полный таймлайн':'Компактный вид'}</button><button id="tlPropertiesToggle" class="button subtle small" aria-controls="tlProperties" aria-expanded="${!!this.compactProperties}" ${this.item()?'':'disabled'}>Свойства</button>
      <label class="tl-snap"><input id="tlSnap" type="checkbox" ${this.snapping?'checked':''}>Привязка</label>
      <button id="tlNewSegment" class="button subtle small" ${this.pending?'disabled':''}>+ Сегмент</button>
      <button id="tlPlaceBlock" class="button small" ${!this.getBlock()||!t.segments.length||this.pending?'disabled':''}>+ Текущий блок</button>
      <button id="tlNewMarker" class="button subtle small" ${this.pending?'disabled':''}>◇ Маркер</button>
      <label class="tl-zoom" title="Пикселей на секунду"><span>−</span><input id="tlZoom" aria-label="Масштаб таймлайна" type="range" min="2" max="160" value="${this.zoom}"><span>+</span></label>
      <button id="tlFit" class="button subtle small">Уместить</button><button id="tlCollapse" class="button subtle small" aria-label="${collapsed?'Развернуть':'Свернуть'} таймлайн">${collapsed?'⌃':'⌄'}</button></header>
      <div class="tl-body"><div class="tl-scroll" aria-label="Монтажная шкала" tabindex="0"><div class="tl-gutter"><span>Время</span><span>Маркеры</span><span>Сегменты</span><span>Блоки</span></div><div class="tl-canvas" style="width:${Math.max(600,this.px(extentFrames(t)))}px">${this.canvas(t)}</div></div>
      <div id="tlProperties" class="tl-properties"></div><div id="tlStatus" class="tl-status" role="status">${t.clips.length?'Перетаскивайте клипы и края. Alt — без привязки. Плеер показывает выбранный блок.':'Создайте монтажный сегмент и перетащите блок из дерева или нажмите «Текущий блок». Источники остаются в библиотеке.'}</div></div>`;
    this.root.querySelector('.tl-scroll').scrollLeft=scroll;
    this.bind();this.properties();this.syncLayoutControls();if(focused)this.root.querySelector(`[data-id="${focused}"]`)?.focus({preventScroll:true});
  }
  canvas(t){
    const extent=extentFrames(t),major=rulerStep(this.zoom,t.fps),minor=Math.max(1,Math.round(major/5));
    let ticks='';for(let n=0;n<=extent;n+=major)ticks+=`<span class="tl-tick major" style="left:${this.px(n)}px"><b>${frameCode(n,t.fps)}</b></span>`;
    for(let n=0;n<=extent;n+=minor)if(n%major)ticks+=`<span class="tl-tick" style="left:${this.px(n)}px"></span>`;
    const active=(kind,id)=>this.selection?.kind===kind&&this.selection.id===id?' selected':'';
    const segments=t.segments.map(s=>`<div class="tl-segment ${s.color}${active('segment',s.id)}" data-kind="segment" data-id="${s.id}" tabindex="0" role="button" aria-label="Сегмент ${esc(s.name)}" title="${esc(s.name)} · ${frameCode(s.start_frame,t.fps)} — ${frameCode(frameEnd(s),t.fps)}" style="left:${this.px(s.start_frame)}px;width:${this.px(s.duration_frames)}px"><span>${esc(s.name)}</span><small>${seconds(s.duration_frames,t.fps)} с</small><i class="tl-handle right" data-edge="segment-right" aria-hidden="true"></i></div>`).join('');
    const sequence=t.clips.toSorted((a,b)=>a.start_frame-b.start_frame);
    const windows=transitionWindows(t),incoming=new Map(windows.map(w=>[w.right_clip_id,w])),outgoing=new Map(windows.map(w=>[w.left_clip_id,w]));
    const clips=t.clips.map(c=>{
      const b=this.project().blocks.find(b=>b.id===c.block_id),take=b?.takes.find(x=>x.id===c.take_id),poster=take?.last_frame_asset_id??b?.references[0]?.asset_id;
      const s=t.segments.find(s=>s.id===c.segment_id),index=sequence.findIndex(x=>x.id===c.id),previous=sequence[index-1];
      const contextWarning=b?.settings.mode==='context'&&b.context_take_id&&previous?.take_id!==b.context_take_id;
      const into=incoming.get(c.id),out=outgoing.get(c.id),px=n=>n*this.zoom/t.fps;
      const layer=(type,start,length)=>length>0?`<i class="tl-fade-band ${type}" aria-hidden="true" style="left:${px(start)}px;width:${px(length)}px"></i>`:'';
      const fades=(into?layer('fade-in',0,into.fade_in_frames):'')+(out?layer('fade-out',out.out_start_frame-c.start_frame,out.fade_out_frames)+layer('black-tail',out.black_start_frame-c.start_frame,c.start_frame+c.duration_frames-out.black_start_frame):'');
      return `<div class="tl-clip ${s?.color??'mint'}${c.take_id?'':' placeholder'}${active('clip',c.id)}" data-kind="clip" data-id="${c.id}" tabindex="0" role="button" aria-label="Клип ${esc(c.story_title??b?.label??'Блок')}" title="${esc(c.story_title??b?.label??'Блок')} · ${seconds(c.duration_frames,t.fps)} с${contextWarning?' · Проверьте стык Motion Context':''}" style="left:${this.px(c.start_frame)}px;width:${this.px(c.duration_frames)}px">${poster?`<img src="${this.media(poster)}" alt="" draggable="false" loading="lazy">`:''}${fades}<i class="tl-handle left" data-edge="left" aria-hidden="true"></i><span class="tl-clip-label">${esc(c.story_title??b?.label??'Блок')}</span><small>${c.take_id?`${frameCode(c.source_in_frame,t.fps)} · ${seconds(c.duration_frames,t.fps)} с`:'ЗАГЛУШКА'}</small>${clipSpeed(c)!==1||c.anchor_frame!=null||c.gap_protected?`<b class="tl-edit-badge">${clipSpeed(c)}×${c.anchor_frame!=null?' · фикс.':''}${c.gap_protected?' · пауза':''}</b>`:''}${contextWarning?'<span class="tl-context-note" title="Перед этим клипом не размещён дубль, из которого взят Motion Context. Проверьте стык.">↗</span>':''}<i class="tl-handle right" data-edge="right" aria-hidden="true"></i></div>`;
    }).join('');
    const boundaries=t.segments.map(s=>[s.start_frame,frameEnd(s)].map((n,i)=>`<i class="tl-boundary" data-boundary="${s.id}" data-end="${i}" style="left:${this.px(n)}px"></i>`).join('')).join('');
    const markers=t.markers.map(m=>`<button class="tl-marker${active('marker',m.id)}" data-kind="marker" data-id="${m.id}" style="left:${this.px(m.frame)}px" title="${esc(m.name)} · ${frameCode(m.frame,t.fps)}">◇ <span>${esc(m.name)}</span></button>`).join('');
    const joins=timelineJoins(t).map(j=>`<button class="tl-join${j.transition?' configured':''}" data-kind="join" data-id="${j.id}" aria-haspopup="menu" aria-expanded="false" aria-label="${j.cross_segment?'Стык сегментов':'Стык блоков'} ${esc(this.joinName(j,t))}" title="${j.cross_segment?'Стык сегментов':'Стык блоков'} · ${esc(this.joinName(j,t))} · переход через чёрный" style="left:${this.px(j.start_frame)}px">${j.transition?'◆':'◇'}</button>`).join('');
    return `<div class="tl-ruler">${ticks}</div><div class="tl-marker-lane">${markers}</div><div class="tl-segment-lane">${segments}</div><div class="tl-clip-lane">${clips}${joins}</div>${boundaries}<div class="tl-playhead" style="left:${this.px(this.cursor)}px"><i></i></div>${!t.segments.length?'<div class="tl-empty">Сначала добавьте сегмент — его границы зададут монтажное окно.</div>':''}`;
  }
  bind(){
    const q=s=>this.root.querySelector(s);
    q('#tlUndo').onclick=()=>this.write('undo',{});q('#tlRedo').onclick=()=>this.write('redo',{});
    q('#tlSnap').onchange=e=>{this.snapping=e.target.checked;};
    q('#tlZoom').onchange=e=>this.setZoom(Number(e.target.value));
    q('#tlFit').onclick=()=>this.setZoom(Math.max(2,Math.min(160,(q('.tl-scroll').clientWidth-100)/(extentFrames(this.data())/this.data().fps))));
    q('#tlCollapse').onclick=()=>{this.collapsed=!this.collapsed;this.render();};
    q('#tlDensity').onclick=()=>this.setDensity(this.timelineLayout().compact?'full':'compact');
    q('#tlPropertiesToggle').onclick=()=>{
      this.onEditStart?.();this.compactProperties=!this.compactProperties;this.render();
      (this.compactProperties?this.root.querySelector('#tlProperties input, #tlProperties select'):q('#tlPropertiesToggle'))?.focus({preventScroll:true});
    };
    q('#tlNewSegment').onclick=()=>this.newSegment();q('#tlNewMarker').onclick=()=>this.newMarker();
    q('#tlPlaceBlock').onclick=()=>this.placeBlock(this.getBlock()?.id,this.cursor,this.getTake()?.id);
    q('.tl-ruler').onpointerdown=e=>{if(e.button!==0)return;this.seek(this.frameAt(e.clientX));};
    q('.tl-clip-lane').ondblclick=e=>{if(e.target===q('.tl-clip-lane'))this.seek(this.frameAt(e.clientX));};
    this.root.querySelectorAll('[data-kind]').forEach(el=>{
      if(el.dataset.kind==='join'){
        el.onpointerdown=e=>e.stopPropagation();el.onclick=e=>this.openContextMenu(e,el);el.oncontextmenu=e=>this.openContextMenu(e,el);el.onkeydown=e=>this.key(e,el);return;
      }
      el.onpointerdown=e=>this.startDrag(e,el);
      el.onkeydown=e=>this.key(e,el);
      if(['clip','segment'].includes(el.dataset.kind)){
        el.setAttribute('aria-haspopup','menu');el.setAttribute('aria-expanded','false');
        el.oncontextmenu=e=>this.openContextMenu(e,el);
      }
      if(el.dataset.kind==='clip')el.ondblclick=e=>{e.preventDefault();e.stopPropagation();this.editSource?.(this.lookup('clip',el.dataset.id));};
      if(el.tagName==='BUTTON')el.onclick=e=>e.preventDefault();
    });
    const scroll=q('.tl-scroll');
    scroll.ondragover=e=>{if(e.dataTransfer.types.includes('application/x-studio-block')){e.preventDefault();e.dataTransfer.dropEffect='copy';scroll.classList.add('drop-active');}};
    scroll.ondragleave=()=>scroll.classList.remove('drop-active');
    scroll.ondrop=e=>{e.preventDefault();scroll.classList.remove('drop-active');const id=e.dataTransfer.getData('application/x-studio-block');if(id)this.placeBlock(id,this.frameAt(e.clientX));};
    this.root.onkeydown=e=>{
      if(['INPUT','TEXTAREA','SELECT'].includes(e.target.tagName))return;
      if((e.ctrlKey||e.metaKey)&&e.key.toLowerCase()==='z'){e.preventDefault();this.write(e.shiftKey?'redo':'undo',{});}
    };
  }
  setZoom(zoom){
    const scroll=this.root.querySelector('.tl-scroll'),width=scroll.clientWidth,middle=(scroll.scrollLeft+width/2)/this.zoom;
    this.zoom=Math.round(zoom*10)/10;localStorage.setItem('studio-zoom-'+this.project().id,this.zoom);this.render();
    this.root.querySelector('.tl-scroll').scrollLeft=Math.max(0,middle*this.zoom-width/2);
  }
  frameAt(clientX){return Math.max(0,Math.round((clientX-this.root.querySelector('.tl-canvas').getBoundingClientRect().left)/this.zoom*this.data().fps));}
  seek(frame){
    this.cursor=Math.max(0,Math.round(frame));localStorage.setItem('studio-cursor-'+this.project().id,this.cursor);
    const clip=this.data().clips.find(c=>this.cursor>=c.start_frame&&this.cursor<frameEnd(c));
    this.render();if(clip)this.onClip(clip,(clip.source_in_frame+(this.cursor-clip.start_frame)*clipSpeed(clip))/this.data().fps);
  }
  choose(kind,id){
    this.selection={kind,id};this.root.querySelectorAll('[data-kind]').forEach(e=>e.classList.toggle('selected',e.dataset.kind===kind&&e.dataset.id===id));this.properties();this.syncLayoutControls();
  }
  async write(action,payload,{expectedRevision=null,throwErrors=false}={}){
    if(this.pending){if(throwErrors)throw new Error('Дождитесь сохранения монтажа.');return;}
    this.pending=true;const pid=this.project().id;this.root.classList.add('pending');
    try{
      // A block-save conflict has its own resolution UI. Do not replace its
      // unsaved draft with a fresh timeline response or mask that dialog.
      try{await this.beforeWrite(action,payload);}catch(error){if(throwErrors)throw error;if(error.status!==409)this.onError(error);return;}
      if(this.project().id!==pid)return;
      const result=await this.api(`/api/projects/${encodeURIComponent(pid)}/timeline`,'POST',{expected_revision:expectedRevision??this.project().revision,action,payload,actor:'author'});
      if(this.project().id===pid&&['add_segment','add_clip','add_marker'].includes(action)&&!result.timeline.scenario){
        const kind=action.slice(4),item=result.timeline[kind+'s'].at(-1);this.selection={kind,id:item.id};
        this.cursor=kind==='clip'?frameEnd(item):kind==='segment'?item.start_frame:item.frame;
        localStorage.setItem('studio-cursor-'+pid,this.cursor);
      }
      if(this.project().id===pid){this.onProject(result);return result;}
    }catch(error){
      if(throwErrors)throw error;
      if(error.status===409){
        sessionStorage.setItem('studio-timeline-conflict-'+pid,JSON.stringify({action,payload}));
        try{const fresh=await this.api(`/api/projects/${encodeURIComponent(pid)}`);if(this.project().id===pid)this.onProject(fresh);}catch{}
        error.message='Таймлайн изменился в другой сессии. Загружена сохранённая версия; ваш перенос не применён. Повторите его после проверки.';
      }
      this.onError(error);
    }finally{this.pending=false;this.lastSignature='';this.render();}
  }
  newSegment(){
    const t=this.data(),start=Math.max(0,...t.segments.map(frameEnd));
    this.dialog('Новый монтажный сегмент',`<label class="field"><span>Название</span><input name="name" value="Сегмент ${t.segments.length+1}" required maxlength="160"></label><label class="field"><span>Начало, с</span><input name="start" type="number" min="0" step="0.001" value="${seconds(start,t.fps)}" required></label><label class="field"><span>Длительность, с</span><input name="duration" type="number" min="${1/t.fps}" step="any" value="8" required></label>`,form=>this.write('add_segment',{name:form.get('name'),start_frame:toFrame(form.get('start'),t.fps),duration_frames:toFrame(form.get('duration'),t.fps)}));
  }
  newMarker(){
    const fps=this.data().fps;
    this.dialog('Маркер монтажа',`<label class="field"><span>Название маркера</span><input name="name" value="Акцент" maxlength="160" required></label><label class="field"><span>Позиция, с</span><input name="frame" type="number" min="0" step="0.001" value="${seconds(this.cursor,fps)}" required></label>`,form=>this.write('add_marker',{name:form.get('name'),frame:toFrame(form.get('frame'),fps)}));
  }
  async placeBlock(blockId,frame,takeId){
    if(this.pending||this.probing)return;const p=this.project(),t=this.data(),b=p.blocks.find(b=>b.id===blockId);if(!b)return;
    const s=t.segments.find(s=>frame>=s.start_frame&&frame<frameEnd(s))??t.segments.find(s=>s.id===(this.selection?.kind==='segment'?this.selection.id:this.item()?.segment_id));
    if(!s){this.onError(new Error('Поставьте курсор внутри монтажного сегмента или сначала создайте сегмент.'));return;}
    const take=b.takes.find(x=>x.id===(takeId??b.selected_take_id));
    let start=Math.max(frame,s.start_frame),duration=toFrame(b.montage_seconds??2,t.fps);
    if(this.snapping)start=snapFrame(start,0,this.targets('clip',''),8/this.zoom*t.fps,'start');
    if(take){
      let available=takeCapacity(take,t.fps);
      if(!available){
        this.probing=true;
        try{const info=await this.api(`/api/projects/${encodeURIComponent(p.id)}/media-info?asset=${encodeURIComponent(take.video_asset_id)}`);available=takeCapacity({video_info:info},t.fps);}
        catch(error){this.onError(error);return;}
        finally{this.probing=false;}
        if(this.project().id!==p.id)return;
      }
      duration=Math.min(duration,available);
    }
    duration=Math.min(duration,frameEnd(s)-start);
    if(duration<1){this.onError(new Error('В этом месте недостаточно времени для клипа.'));return;}
    this.write('add_clip',{block_id:b.id,take_id:take?.id??null,segment_id:s.id,start_frame:start,duration_frames:duration,source_in_frame:0});
  }
  targets(kind,id){
    const t=this.data(),ownClips=kind==='segment'?t.clips.filter(c=>c.segment_id===id).map(c=>c.id):[id];
    return [0,this.cursor,...t.markers.filter(m=>!(kind==='marker'&&m.id===id)).map(m=>m.frame),...t.segments.filter(s=>!(kind==='segment'&&s.id===id)).flatMap(s=>[s.start_frame,frameEnd(s)]),...t.clips.filter(c=>!ownClips.includes(c.id)).flatMap(c=>[c.start_frame,frameEnd(c)])];
  }
  startLibraryDrag(event,blockId){
    if(event.button!==0||this.pending)return;
    const source=event.currentTarget,origin={x:event.clientX,y:event.clientY};let moved=false,ghost=null;
    source.setPointerCapture(event.pointerId);
    const clear=()=>{ghost?.remove();this.root.querySelector('.tl-scroll')?.classList.remove('drop-active');source.onpointermove=null;source.onpointerup=null;source.onpointercancel=null;};
    source.onpointermove=e=>{
      if(Math.hypot(e.clientX-origin.x,e.clientY-origin.y)<6&&!moved)return;
      moved=true;e.preventDefault();
      if(!ghost){ghost=document.createElement('div');ghost.className='tl-library-ghost';ghost.textContent=this.project().blocks.find(b=>b.id===blockId)?.label??'Блок';document.body.append(ghost);}
      ghost.style.left=e.clientX+12+'px';ghost.style.top=e.clientY+12+'px';
      const scroll=this.root.querySelector('.tl-scroll'),bounds=scroll.getBoundingClientRect(),over=e.clientY>=bounds.top&&e.clientY<=bounds.bottom&&e.clientX>=bounds.left&&e.clientX<=bounds.right;
      scroll.classList.toggle('drop-active',over);
      if(over){if(e.clientX<bounds.left+100)scroll.scrollLeft-=12;else if(e.clientX>bounds.right-28)scroll.scrollLeft+=12;}
    };
    source.onpointerup=e=>{
      const bounds=this.root.querySelector('.tl-scroll').getBoundingClientRect();clear();
      if(source.hasPointerCapture(e.pointerId))source.releasePointerCapture(e.pointerId);
      if(moved){
        e.preventDefault();this.librarySuppressUntil=performance.now()+350;
        if(e.clientY>=bounds.top&&e.clientY<=bounds.bottom&&e.clientX>=bounds.left+70&&e.clientX<=bounds.right)this.placeBlock(blockId,this.frameAt(e.clientX));
      }
    };
    source.onpointercancel=clear;
  }
  startDrag(event,el){
    if(event.button!==0||this.pending)return;event.preventDefault();event.stopPropagation();
    const kind=el.dataset.kind,id=el.dataset.id,item=this.lookup(kind,id);
    this.choose(kind,id);el.focus({preventScroll:true});
    this.drag={kind,id,item:{...item},edge:event.target.dataset.edge??'',x:event.clientX,scroll:this.root.querySelector('.tl-scroll').scrollLeft,moved:false,el,patch:{},error:''};
    el.setPointerCapture(event.pointerId);
    el.onpointermove=e=>this.moveDrag(e);el.onpointerup=e=>this.endDrag(e);el.onpointercancel=()=>{this.drag=null;this.lastSignature='';this.render();};
  }
  moveDrag(event){
    const d=this.drag;if(!d)return;const t=this.data(),scroll=this.root.querySelector('.tl-scroll'),bounds=scroll.getBoundingClientRect();
    if(event.clientX<bounds.left+100)scroll.scrollLeft-=12;else if(event.clientX>bounds.right-28)scroll.scrollLeft+=12;
    const dx=event.clientX-d.x+scroll.scrollLeft-d.scroll,delta=Math.round(dx/this.zoom*t.fps);
    if(Math.abs(dx)>3)d.moved=true;if(!d.moved)return;
    const targets=this.targets(d.kind,d.id),snap=raw=>this.snapping&&!event.altKey?snapFrame(raw,d.edge?0:d.item.duration_frames??0,targets,8/this.zoom*t.fps,d.edge?'edge':'move'):Math.max(0,Math.round(raw));
    let kind=d.kind;
    if(d.kind==='marker'){d.patch={frame:snap(d.item.frame+delta)};d.error='';}
    else if(d.kind==='segment'){
      if(d.edge){d.patch={duration_frames:Math.max(1,snap(frameEnd(d.item)+delta)-d.item.start_frame)};kind='segment-resize';}
      else d.patch={start_frame:snap(d.item.start_frame+delta)};
      d.error=this.project().timeline?.scenario?'':placementError(this.project(),t,kind,d.id,d.patch);
    }else{
      if(d.edge){
        const b=this.project().blocks.find(b=>b.id===d.item.block_id),take=b?.takes.find(x=>x.id===d.item.take_id);
        const edgeFrame=(d.edge==='left'?d.item.start_frame:frameEnd(d.item))+delta;
        d.patch=trimClip(d.item,d.edge,snap(edgeFrame),d.item.take_id?takeCapacity(take,t.fps):Infinity);
      }else{
        const start=snap(d.item.start_frame+delta),target=t.segments.find(s=>start>=s.start_frame&&start+d.item.duration_frames<=frameEnd(s));
        d.patch={start_frame:start,segment_id:target?.id??d.item.segment_id};
      }
      d.error=this.project().timeline?.scenario?'':placementError(this.project(),t,'clip',d.id,d.patch);
    }
    d.el.classList.toggle('invalid',!!d.error);const updated={...d.item,...d.patch};
    d.el.style.left=this.px(d.kind==='marker'?updated.frame:updated.start_frame)+'px';
    if(d.kind!=='marker')d.el.style.width=this.px(updated.duration_frames)+'px';
    if(d.kind==='segment'&&!d.edge){for(const c of t.clips.filter(c=>c.segment_id===d.id)){const el=this.root.querySelector(`[data-id="${c.id}"]`);if(el)el.style.left=this.px(c.start_frame+updated.start_frame-d.item.start_frame)+'px';}}
    if(d.kind==='segment')this.root.querySelectorAll(`[data-boundary="${d.id}"]`).forEach(el=>el.style.left=this.px(el.dataset.end==='1'?frameEnd(updated):updated.start_frame)+'px');
    this.root.querySelector('#tlStatus').textContent=d.error||`${frameCode(updated.frame??updated.start_frame,t.fps)}${updated.duration_frames?' · '+seconds(updated.duration_frames,t.fps)+' с':''}`;
  }
  endDrag(event){
    const d=this.drag;if(!d)return;this.drag=null;
    if(d.el.hasPointerCapture(event.pointerId))d.el.releasePointerCapture(event.pointerId);
    d.el.onpointermove=null;d.el.onpointerup=null;
    if(d.moved&&!d.error){this.write(d.kind==='marker'?'update_marker':d.kind==='segment'?(d.edge?'resize_segment':'move_segment'):d.kind==='music'?'update_music':'update_clip',{id:d.id,...d.patch});}
    else{
      this.lastSignature='';this.render();if(d.error)this.onError(new Error(d.error));
      if(!d.moved&&d.kind==='clip')this.onClip(d.item,d.item.source_in_frame/this.data().fps);
      if(!d.moved&&d.kind==='marker')this.seek(d.item.frame);
      if(!d.moved&&d.kind==='music')this.seek(this.frameAt(event.clientX));
    }
  }
  key(event,el){
    const kind=el.dataset.kind,id=el.dataset.id,item=this.lookup(kind,id);
    if(!item)return;
    if(kind==='join'){if(['Enter',' ','ContextMenu'].includes(event.key)||event.shiftKey&&event.key==='F10')this.openContextMenu(event,el);return;}
    if(['clip','segment'].includes(kind)&&(event.key==='ContextMenu'||event.shiftKey&&event.key==='F10')){this.openContextMenu(event,el);return;}
    if(event.key==='Enter'||event.key===' '){event.preventDefault();this.choose(kind,id);if(kind==='clip')this.onClip(item,item.source_in_frame/this.data().fps);return;}
    if(['ArrowLeft','ArrowRight'].includes(event.key)){
      event.preventDefault();this.choose(kind,id);const delta=(event.key==='ArrowLeft'?-1:1)*(event.shiftKey?this.data().fps:1);
      this.write(kind==='marker'?'update_marker':kind==='segment'?'move_segment':kind==='music'?'update_music':'update_clip',{id,...(kind==='marker'?{frame:Math.max(0,item.frame+delta)}:{start_frame:Math.max(0,item.start_frame+delta)})});
    }
    if(event.key==='Delete'||event.key==='Backspace'){event.preventDefault();this.write('remove_'+kind,{id});}
  }
  openContextMenu(event,element){this.contextMenu??=new TimelineMenu(this);this.contextMenu.open(event,element);}
  properties(){
    const target=this.root.querySelector('#tlProperties'),item=this.item(),t=this.data(),kind=this.selection?.kind;
    if(!item){target.innerHTML=`<span class="tl-selection-hint">${t.segments.length} сегм. · ${t.clips.length} клипов · ${t.markers.length} маркеров</span><span class="muted">Выберите элемент, чтобы задать точное время. ← → — кадр, Shift — секунда.</span>`;return;}
    const timeField=(name,label,value)=>`<label><span>${label}</span><input name="${name}" type="number" min="0" step="0.001" value="${seconds(value,t.fps)}" required></label>`;
    const b=kind==='clip'?this.project().blocks.find(b=>b.id===item.block_id):null;
    target.innerHTML=`<form id="tlEdit"><span class="tl-item-kind">${kind==='clip'?esc(b?.label??'Клип'):kind==='segment'?'СЕГМЕНТ':'МАРКЕР'}</span>${kind==='clip'?`<button id="tlSourceEdit" type="button" class="button small" ${item.take_id?'':'disabled'}>Участок и скорость</button><button id="tlSplit" type="button" class="button subtle small" ${this.cursor>item.start_frame&&this.cursor<frameEnd(item)?'':'disabled'}>Разрезать</button><label class="tl-snap"><input id="tlAnchor" type="checkbox" ${item.anchor_frame!=null?'checked':''}> Закрепить начало</label><label class="tl-snap"><input id="tlProtectGap" type="checkbox" ${item.gap_protected?'checked':''}> Защитить паузу</label>`:kind==='segment'?'<button id="tlCompact" type="button" class="button small">Убрать промежутки</button>':''}
      ${kind!=='clip'?`<label class="tl-name-field"><span>Название</span><input name="name" value="${esc(item.name)}" maxlength="160" required></label>`:''}
      ${timeField('start',kind==='marker'?'Позиция, с':'Начало, с',kind==='marker'?item.frame:item.start_frame)}
      ${kind!=='marker'?timeField('duration','Длина, с',item.duration_frames):''}
      ${kind==='clip'?`${timeField('source','Вход в дубль, с',item.source_in_frame)}<label><span>Сегмент</span><select name="segment">${t.segments.map(s=>`<option value="${s.id}" ${s.id===item.segment_id?'selected':''}>${esc(s.name)}</option>`).join('')}</select></label><label class="tl-take-field"><span>Дубль</span><select name="take"><option value="">Заглушка</option>${b.takes.map(x=>`<option value="${x.id}" ${x.id===item.take_id?'selected':''}>${esc(x.name)}</option>`).join('')}</select></label>`:''}
      ${kind==='segment'?`<label><span>Цвет</span><select name="color">${colors.map((c,i)=>`<option value="${c}" ${c===item.color?'selected':''}>${['Мята','Синий','Сирень','Янтарь','Роза','Бирюза'][i]}</option>`).join('')}</select></label>`:''}
      <button class="button small" type="submit" ${this.pending?'disabled':''}>Применить</button><button id="tlRemove" type="button" class="button subtle small" title="Убрать из монтажа. Исходные файлы сохраняются.">Убрать</button></form>`;
    target.querySelector('#tlRemove').onclick=()=>this.write('remove_'+kind,{id:item.id});
    if(kind==='clip'){
      target.querySelector('#tlSourceEdit').onclick=()=>this.editSource?.(item);
      target.querySelector('#tlSplit').onclick=()=>this.write('split_clip',{id:item.id,frame:this.cursor});
      target.querySelector('#tlAnchor').onchange=e=>this.write('set_clip_anchor',{id:item.id,enabled:e.target.checked});
      target.querySelector('#tlProtectGap').onchange=e=>this.write('set_gap_protected',{id:item.id,enabled:e.target.checked});
    }else if(kind==='segment')target.querySelector('#tlCompact').onclick=()=>this.write('compact_segment',{segment_id:item.id});
    target.querySelector('#tlEdit').onsubmit=async e=>{
      e.preventDefault();const form=new FormData(e.target),start=toFrame(form.get('start'),t.fps);
      if(kind==='clip')this.write('update_clip',{id:item.id,start_frame:start,duration_frames:toFrame(form.get('duration'),t.fps),source_in_frame:form.get('take')?toFrame(form.get('source'),t.fps):0,segment_id:form.get('segment'),take_id:form.get('take')||null});
      else if(kind==='marker')this.write('update_marker',{id:item.id,name:form.get('name'),frame:start});
      else{
        // One atomic edit moves the group and changes its window/name/color.
        this.write('edit_segment',{id:item.id,name:form.get('name'),color:form.get('color'),start_frame:start,duration_frames:toFrame(form.get('duration'),t.fps)});
      }
    };
  }
}
