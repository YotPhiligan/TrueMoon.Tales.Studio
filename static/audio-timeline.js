import {TimelineView} from './timeline.js';
import {escapeHtml as esc} from './logic.js';
import {frameEnd,frameCode,toFrame,trimClip} from './timeline-logic.js';
import {timelineDuration} from './playback-logic.js';
import {placeSound,placedSounds,soundDragPatch,soundLanes} from './sound-logic.js';
import {waveformSvg} from './sound-ui.js';
import {setAssetOption} from './asset-library.js';

const sec=(n,fps)=>(n/fps).toFixed(3);

export class AudioTimelineView extends TimelineView {
  render(){
    super.render();if(!this.project()||this.drag||!this.root.querySelector('.tl-toolbar'))return;
    const t=this.data();this.root.classList.add('audio-timeline');
    this.root.classList.toggle('has-timeline-issues',!!this.planIssues?.().length);
    const sounds=placedSounds(t),lanes=Object.keys(soundLanes).filter(l=>sounds.some(e=>e.lane===l));this.root.style.setProperty('--sound-height',lanes.length*36+'px');this.root.style.setProperty('--compact-sound-height',lanes.length*16+'px');
    this.root.style.setProperty('--title-height',t.titles?.length?'40px':'0px');this.root.style.setProperty('--compact-title-height',t.titles?.length?'18px':'0px');
    if(!this.root.querySelector('.tl-playback-bar')){
      this.followCursor??=localStorage.getItem('studio-timeline-follow')!=='off';
      const bar=document.createElement('div');bar.className='tl-playback-bar';
      bar.innerHTML=`<button id="tlPlay" class="button primary small" aria-label="Воспроизвести монтаж">▶</button><button id="tlStop" class="button subtle small" aria-label="Остановить монтаж">■</button><strong>МОНТАЖ</strong><span id="tlDuration"></span><span id="tlPlaybackStatus" role="status"></span><button id="tlFollow" type="button" class="button subtle small" title="Автоследование за курсором при воспроизведении" aria-label="Автоследование за курсором" aria-pressed="${this.followCursor}">Следить</button><button id="tlBeat" class="button subtle small" title="Маркер на текущем кадре · M">◇ Бит <kbd>M</kbd></button><label class="tl-clip-audio"><input id="tlClipAudio" type="checkbox" ${t.clip_audio?'checked':''}>Звук клипов</label>`;
      this.root.prepend(bar);
      bar.querySelector('#tlPlay').onclick=()=>this.onPlay();bar.querySelector('#tlStop').onclick=()=>this.onStop();
      const follow=bar.querySelector('#tlFollow');
      follow.onclick=()=>{
        this.followCursor=!this.followCursor;
        follow.setAttribute('aria-pressed',String(this.followCursor));
        localStorage.setItem('studio-timeline-follow',this.followCursor?'on':'off');
        if(this.followCursor&&this.playback?.().playing)this.revealCursor();
      };
      bar.querySelector('#tlBeat').onclick=()=>this.quickMarker();
      const exportButton=document.createElement('button');exportButton.id='tlExport';exportButton.className='button subtle small';exportButton.textContent='Экспорт MP4';exportButton.onclick=()=>this.onExport?.();bar.querySelector('.tl-clip-audio').before(exportButton);
      const readinessButton=document.createElement('button');
      readinessButton.id='tlReadiness';
      readinessButton.type='button';
      readinessButton.className='button subtle small';
      readinessButton.textContent='Готовность';
      readinessButton.title='Проверить монтаж перед экспортом';
      readinessButton.onclick=()=>this.onExport?.({check:true});
      exportButton.before(readinessButton);
      const titlesButton=document.createElement('button');titlesButton.id='tlTitles';titlesButton.className='button subtle small';titlesButton.textContent='Тт Титры';titlesButton.onclick=()=>this.onTitles?.();exportButton.before(titlesButton);
      bar.querySelector('#tlClipAudio').onchange=e=>this.write('set_clip_audio',{enabled:e.target.checked});
      const button=document.createElement('button');button.id='tlMusic';button.className='button subtle small';button.textContent='♪ Музыка';button.onclick=()=>this.newMusic();
      this.root.querySelector('#tlNewMarker').before(button);
      const label=document.createElement('span');label.textContent='Музыка';this.root.querySelector('.tl-gutter').append(label);
      for(const lane of lanes){const label=document.createElement('span');label.className='tl-sound-label';label.textContent=soundLanes[lane];this.root.querySelector('.tl-gutter').append(label);}
      if(t.titles?.length){const label=document.createElement('span');label.className='tl-title-label';label.textContent='Титры';this.root.querySelector('.tl-gutter').append(label);}
    }
    this.root.querySelectorAll('[data-title-edit]').forEach(b=>{b.onpointerdown=e=>e.stopPropagation();b.onclick=e=>{e.stopPropagation();this.onTitle?.(b.dataset.titleEdit);};});
    const soundsById=new Map(sounds.map(sound=>[sound.id,sound]));
    const clipsById=new Map([...this.root.querySelectorAll('[data-kind="clip"]')].map(clip=>[clip.dataset.id,clip]));
    this.root.querySelectorAll('[data-sound-event]').forEach(button=>{
      const sound=soundsById.get(button.dataset.soundEvent),clip=clipsById.get(sound?.clip_id);
      if(!sound)return;
      button.onclick=event=>{
        if(event.detail!==0||this.pending)return;
        this.choose('sound',sound.id);
      };
      if(clip){
        const line=clip.querySelector('[data-sound-line="'+sound.id+'"]');
        const highlight=()=>{
          const active=button.matches(':hover,:focus');
          line?.classList.toggle('sound-focused',active);
        };
        button.onpointerenter=button.onpointerleave=button.onfocus=button.onblur=highlight;
      }
    });
    this.syncLinkedSounds();
    this.soundWaves??=new WeakMap();
    const p=this.project();
    for(const e of sounds)this.soundWave?.(e.asset_id,e.stream,p.id).then(wave=>{
      if(this.project().id!==p.id||this.project().revision!==p.revision)return;
      const button=this.root.querySelector(`[data-sound-event="${e.id}"]`);
      if(!button)return;
      this.soundWaves.set(button,wave);
      const sound=this.drag?.el===button?this.drag.preview??e:e;
      this.drawSoundWave(button,sound,t.fps);
    }).catch(()=>{});
    this.updateTransport();const scroll=this.root.querySelector('.tl-scroll');
    this.root.querySelector('#tlStatus').textContent=this.planIssues?.().length?'ПЛАН ТРЕБУЕТ ПРОВЕРКИ · '+this.planIssues().map(i=>i.message).join(' '):'Space — просмотр · M — метка бита · края — обрезка · Alt — без привязки.';
    scroll.onscroll=()=>{if(this.waveRaf)cancelAnimationFrame(this.waveRaf);this.waveRaf=requestAnimationFrame(()=>{this.waveRaf=null;this.drawWaveform();});};
    if(t.music)this.loadWaveform(t.music.asset_id).then(()=>this.drawWaveform()).catch(e=>this.waveError(e));
  }
  choose(kind,id){
    super.choose(kind,id);
    this.syncLinkedSounds();
  }
  lookup(kind,id){
    return kind==='sound'?placedSounds(this.data()).find(sound=>sound.id===id):super.lookup(kind,id);
  }
  syncLinkedSounds(){
    const selected=this.selection?.kind==='sound'?this.item():null;
    const clipId=this.selection?.kind==='clip'?this.selection.id:null;
    for(const button of this.root.querySelectorAll('[data-sound-event]')){
      button.classList.toggle('related',button.dataset.soundClip===clipId);
      button.setAttribute('aria-pressed',String(button.dataset.soundEvent===selected?.id));
    }
    for(const row of this.root.querySelectorAll('[data-kind="clip"],[data-kind="segment"]')){
      const owner=row.dataset.kind==='clip'?selected?.clip_id:selected&&!selected.clip_id?selected.segment_id:null;
      row.classList.toggle('tl-sound-owner',row.dataset.id===owner);
    }
  }
  canvas(t){
    const m=t.music,sounds=placedSounds(t),lanes=Object.keys(soundLanes).filter(l=>sounds.some(e=>e.lane===l));
    const clipSounds=new Map();
    for(const sound of sounds){
      if(!sound.clip_id)continue;
      if(!clipSounds.has(sound.clip_id))clipSounds.set(sound.clip_id,[]);
      clipSounds.get(sound.clip_id).push(sound);
    }
    const blocks=this.project().blocks,clipNames=new Map();
    for(const clip of t.clips){
      if(clipSounds.has(clip.id))clipNames.set(clip.id,clip.story_title??blocks.find(b=>b.id===clip.block_id)?.label??'Блок');
    }
    const clipOverlay=clip=>{
      const tracks=clipSounds.get(clip.id);
      if(!tracks)return '';
      const lines=tracks.map(e=>`<i class="tl-block-sound-line${e.muted?' muted':''}" data-sound-line="${e.id}" data-audio-role="${e.lane}" style="left:${this.px(e.offset_frames)}px;width:${this.px(e.duration_frames)}px"></i>`).join('');
      return `<span class="tl-block-sounds" aria-hidden="true">${lines}</span>`;
    };
    const soundButton=e=>{
      const clipName=clipNames.get(e.clip_id),label=esc(e.name);
      const owner=clipName?' · блок «'+esc(clipName)+'»':' · сегмент «'+esc(t.segments.find(s=>s.id===e.segment_id)?.name??'')+'»';
      const selected=this.selection?.kind==='sound'&&this.selection.id===e.id?' selected':'';
      const link=clipName?` data-sound-clip="${e.clip_id}" data-audio-role="${e.lane}"`:'';
      return `<button type="button" class="tl-sound-event${selected}${e.muted?' muted':''}${e.tail_frames?' tail':''}" data-kind="sound" data-id="${e.id}" data-sound-event="${e.id}"${link} aria-label="Звук ${label}${owner}" title="${label}${owner} · ${soundLanes[e.lane]} · поток ${e.stream+1}${e.tail_frames?' · хвост за окном':''} · перетащите звук или его край, щёлкните для точного ввода" style="left:${this.px(e.start_frame)}px;width:${this.px(e.duration_frames)}px"><span>${clipName?'↳ ':''}${label} · ${e.muted?'ВЫКЛ':Math.round(e.gain*100)+'%'}</span><i class="tl-handle left" data-edge="left" aria-hidden="true"></i><i class="tl-handle right" data-edge="right" aria-hidden="true"></i></button>`;
    };
    const extra=lanes.map(l=>`<div class="tl-sound-lane" aria-label="${soundLanes[l]}">${sounds.filter(e=>e.lane===l).map(soundButton).join('')}</div>`).join('');
    const titles=t.titles?.length?`<div class="tl-title-lane" aria-label="Дорожка титров">${t.titles.map(r=>`<button class="tl-title-event" data-title-edit="${r.id}" title="${esc(r.name)} · ${sec(r.start_frame,t.fps)}–${sec(r.start_frame+r.duration_frames,t.fps)} с · ${r.background==='black'?'чёрный фон':'поверх видео'}" style="left:${this.px(r.start_frame)}px;width:${this.px(r.duration_frames)}px">Тт ${esc(r.name)}</button>`).join('')}</div>`:'';
    return super.canvas(t,clipOverlay)+`<div class="tl-music-lane">${m?`<div class="tl-music${this.selection?.kind==='music'?' selected':''}" data-kind="music" data-id="${m.id}" tabindex="0" role="button" aria-label="Музыкальная дорожка" style="left:${this.px(m.start_frame)}px;width:${this.px(m.duration_frames)}px"><canvas class="tl-waveform" aria-label="Звуковая волна"></canvas><span class="tl-music-label">♪ ${esc(this.project().assets[m.asset_id]?.name??'Музыка')} · ${m.muted?'ВЫКЛ':Math.round(m.gain*100)+'%'}</span><i class="tl-handle left" data-edge="left"></i><i class="tl-handle right" data-edge="right"></i></div>`:'<span class="tl-music-empty">♪ Выберите музыкальный файл, чтобы видеть акценты и собирать монтаж под звук.</span>'}</div>`+extra+titles+'<i class="tl-scope-window" aria-hidden="true" hidden></i>';
  }
  updateTransport(){
    if(!this.root.querySelector('#tlPlay'))return;const state=this.playback?.()??{},playing=state.playing||state.preparing;
    const button=this.root.querySelector('#tlPlay');button.textContent=playing?'❚❚':'▶';button.setAttribute('aria-label',playing?'Пауза монтажа':'Воспроизвести монтаж');
    button.disabled=timelineDuration(this.data())<=0||!!this.planIssues?.().length;
    const range=state.range,band=this.root.querySelector('.tl-scope-window');
    if(band){
      band.hidden=!range;
      if(range){
        band.style.left=this.px(range.start*this.data().fps)+'px';
        band.style.width=this.px((range.end-range.start)*this.data().fps)+'px';
        band.classList.toggle('looping',!!state.looping);
      }
    }
    this.root.querySelector('#tlDuration').textContent=range?(range.kind==='range'?'Диапазон · ':range.kind==='join'?'Стык · ':'Сегмент · ')+frameCode(Math.round((state.range.end-state.range.start)*this.data().fps),this.data().fps):'до '+frameCode(Math.round(timelineDuration(this.data())*this.data().fps),this.data().fps);
    this.root.querySelector('#tlPlaybackStatus').textContent=state.preparing?'Подготовка звука…':state.playing?(state.looping?'Повтор окна':'Воспроизведение'):this.preparingMusic?'Анализ звука…':'Пауза';
    this.root.querySelector('#tlClipAudio').checked=!!this.data().clip_audio;
  }
  advance(seconds,playing){
    this.setCursor(Math.floor(seconds*this.data().fps+1e-7));
    if(!playing&&!this.scrubbing)this.rememberCursor();
    if(playing&&this.followCursor!==false)this.revealCursor(40);
  }
  seek(frame,options){this.onSeek(Math.max(0,Math.round(frame)),options);}
  quickMarker(){this.write('add_marker',{name:'Бит '+(this.data().markers.length+1),frame:this.cursor});}
  targets(kind,id){
    const m=this.data().music;return [...super.targets(kind,id),...(m&&kind!=='music'?[{frame:m.start_frame,label:'Начало музыки'},{frame:frameEnd(m),label:'Конец музыки'}]:[])];
  }
  startDrag(e,element){
    if(e.button!==0||this.pending||this.drag||this.scrubbing)return;
    this.onEditStart?.();
    super.startDrag(e,element);
    const drag=this.drag;
    if(drag?.kind==='sound'){
      const t=this.data();
      drag.sound=t.sound_events.find(sound=>sound.id===drag.id);
      drag.owner=drag.sound.clip_id?t.clips.find(clip=>clip.id===drag.sound.clip_id):t.segments.find(segment=>segment.id===drag.sound.segment_id);
    }
  }
  startLibraryDrag(e,id){this.onEditStart?.();super.startLibraryDrag(e,id);}
  dragOperation(drag){
    if(drag.kind!=='sound')return super.dragOperation(drag);
    return Object.keys(drag.patch).length?{action:'update_sound',payload:{id:drag.id,...drag.patch}}:null;
  }
  key(event,element){
    if(element.dataset.kind!=='sound')super.key(event,element);
  }
  drawSoundWave(button,sound,fps){
    const wave=this.soundWaves?.get(button);
    if(!wave)return;
    button.querySelector('svg')?.remove();
    button.insertAdjacentHTML('afterbegin',waveformSvg(wave,sound,fps));
  }
  moveSoundDrag(event,drag){
    const {fps}=this.data(),delta=this.dragDelta(event,drag,fps);
    if(!drag.moved)return;
    const raw=(drag.edge==='right'?frameEnd(drag.item):drag.item.start_frame)+delta;
    const snap=this.dragSnap(raw,drag,event,fps);
    drag.patch=soundDragPatch(drag.sound,drag.owner,drag.edge,snap.frame,fps);
    const preview=placeSound({...drag.sound,...drag.patch},drag.owner);
    const previous=drag.preview??drag.item;
    drag.preview=preview;
    drag.el.style.left=this.px(preview.start_frame)+'px';
    drag.el.style.width=this.px(preview.duration_frames)+'px';
    drag.el.classList.toggle('tail',!!preview.tail_frames);
    const line=this.root.querySelector(`[data-sound-line="${drag.id}"]`);
    if(line){
      line.style.left=this.px(preview.offset_frames)+'px';
      line.style.width=this.px(preview.duration_frames)+'px';
    }
    if(previous.source_in_frame!==preview.source_in_frame||previous.duration_frames!==preview.duration_frames){
      this.drawSoundWave(drag.el,preview,fps);
    }
    this.showDragFeedback(event,drag,preview,snap,fps);
  }
  moveDrag(e){
    const d=this.drag;
    if(d?.kind==='sound'){
      const project=this.project();
      if(project.id===d.projectId&&project.revision===d.revision)this.moveSoundDrag(e,d);
      return;
    }
    if(d?.kind!=='music'){super.moveDrag(e);return;}
    const project=this.project();
    if(project.id!==d.projectId||project.revision!==d.revision)return;
    const scroll=this.root.querySelector('.tl-scroll'),fps=this.data().fps,delta=Math.round((e.clientX-d.x+scroll.scrollLeft-d.scroll)/this.zoom*fps);
    if(Math.abs(e.clientX-d.x)>3)d.moved=true;if(!d.moved)return;
    const raw=(d.edge==='right'?frameEnd(d.item):d.item.start_frame)+delta;
    const snap=this.dragSnap(raw,d,e,fps),frame=snap.frame;
    const capacity=Math.floor((this.project().assets[d.item.asset_id]?.audio_info?.seconds??0)*fps+1e-6);
    d.patch=d.edge?trimClip({...d.item,take_id:d.item.asset_id},d.edge,frame,capacity):{start_frame:frame};
    const m={...d.item,...d.patch};d.error=m.start_frame<0||m.duration_frames<1||m.source_in_frame+m.duration_frames>capacity?'Участок выходит за длину музыки.':'';
    d.el.style.left=this.px(m.start_frame)+'px';d.el.style.width=this.px(m.duration_frames)+'px';d.el.classList.toggle('invalid',!!d.error);
    this.drawWaveform(m);this.showDragFeedback(e,d,m,snap,fps);
  }
  async loadWaveform(id){
    this.waves??=new Map();const key=this.project().id+':'+id;
    if(!this.waves.has(key)){
      const promise=this.api(`/api/projects/${encodeURIComponent(this.project().id)}/waveform?asset=${encodeURIComponent(id)}`);
      this.waves.set(key,promise);promise.then(data=>{this.waveData={key,data};}).catch(()=>this.waves.delete(key));
    }
    const data=await this.waves.get(key);this.waveData={key,data};return data;
  }
  waveError(error){
    const element=this.root.querySelector('.tl-music');if(element)element.dataset.error='true';
    if(this.lastWaveError!==error.message){this.lastWaveError=error.message;this.onError(error);}
  }
  drawWaveform(m=this.data().music){
    if(!m||this.waveData?.key!==this.project().id+':'+m.asset_id)return;
    const element=this.root.querySelector('.tl-music'),canvas=element?.querySelector('canvas'),scroll=this.root.querySelector('.tl-scroll');if(!canvas||!scroll)return;
    const viewport=scroll.getBoundingClientRect(),box=element.getBoundingClientRect(),gutter=this.root.querySelector('.tl-gutter').clientWidth;
    const offset=Math.max(0,viewport.left+gutter-box.left),width=Math.max(0,Math.min(box.width-offset,viewport.right-Math.max(viewport.left+gutter,box.left)));
    if(!width)return;const ratio=Math.min(2,globalThis.devicePixelRatio||1),height=this.timelineLayout().compact?24:52,center=height/2;
    canvas.style.left=offset+'px';canvas.style.width=width+'px';canvas.style.height=height+'px';canvas.width=Math.ceil(width*ratio);canvas.height=height*ratio;
    const ctx=canvas.getContext('2d');ctx.scale(ratio,ratio);ctx.clearRect(0,0,width,height);
    const data=this.waveData.data,pps=data.peaks_per_second,max=data.peakMaximum??(data.peakMaximum=data.peaks.reduce((a,b)=>Math.max(a,b),.01)),fps=this.data().fps;
    ctx.fillStyle=m.muted?'#60737a':'#67c9be';
    for(let x=0;x<width;x++){
      const a=Math.max(0,Math.floor(((offset+x)/this.zoom+m.source_in_frame/fps)*pps)),b=Math.min(data.peaks.length,Math.ceil(((offset+x+1)/this.zoom+m.source_in_frame/fps)*pps));
      let peak=0;for(let i=a;i<b;i++)peak=Math.max(peak,data.peaks[i]);const amplitude=Math.max(.5,peak/max*(center-7));
      ctx.fillRect(x,center-amplitude,1,2*amplitude);
    }
    element.querySelector('.tl-music-label').style.left=offset+8+'px';canvas.dataset.ready='true';
  }
  async newMusic(){
    try{await this.flush?.();}catch(error){this.onError(error);return;}
    const p=this.project(),fps=this.data().fps,assets=Object.values(p.assets).filter(a=>a.kind==='audio'),m=this.data().music;
    let catalogAsset=null;
    this.dialog('Музыкальная дорожка',`<label class="field"><span>Из библиотеки проекта</span><select name="asset"><option value="">Выбрать файл…</option>${assets.map(a=>`<option value="${a.id}" ${a.id===m?.asset_id?'selected':''}>${esc(a.name)}</option>`).join('')}</select></label>${this.mountAssetPicker?'<button id="pickMusicAsset" class="button subtle small" type="button" aria-expanded="false">Выбрать из библиотеки ассетов</button><section id="musicAssetPicker" class="asset-picker-inline" hidden></section>':''}<label class="field"><span>Или загрузить звук</span><input name="audio_file" type="file" accept=".mp3,.wav,.ogg,.flac,.m4a,.aac,.opus"></label><div class="timeline-dialog-row"><label class="field"><span>Начало в ролике, с</span><input name="start" type="number" min="0" step="any" value="${sec(m?.start_frame??this.cursor,fps)}" required></label><label class="field"><span>Вход в песню, с</span><input name="source" type="number" min="0" step="any" value="${sec(m?.source_in_frame??0,fps)}" required></label><label class="field"><span>Длина, с · пусто = до конца файла</span><input name="duration" type="number" min="${1/fps}" step="any" value="${m?sec(m.duration_frames,fps):''}"></label></div>`,async form=>{
      const pid=p.id;this.preparingMusic=true;this.updateTransport();
      try{
        const file=form.get('audio_file');let aid=form.get('asset'),localFile=catalogAsset?.asset_id?null:catalogAsset?.path;
        let expectedRevision=p.revision;
        if(file?.size){aid=await this.importAudio(file);localFile=null;expectedRevision=this.project().revision;}
        if(!aid)throw new Error('Выберите музыкальный файл.');
        const wave=localFile
          ? await this.api('/api/projects/'+encodeURIComponent(pid)+'/waveform?'+new URLSearchParams({file:localFile}))
          : await this.loadWaveform(aid);
        if(this.project().id!==pid)return;
        const source=toFrame(form.get('source'),fps),duration=form.get('duration')?toFrame(form.get('duration'),fps):Math.floor(wave.seconds*fps+1e-6)-source;
        await this.write('set_music',{...(localFile?{asset_selection:{file:localFile}}:{asset_id:aid}),start_frame:toFrame(form.get('start'),fps),source_in_frame:source,duration_frames:duration,gain:m?.gain??.8,muted:m?.muted??false},{expectedRevision});
        if(this.data().music){this.selection={kind:'music',id:this.data().music.id};this.lastSignature='';this.render();}
      }catch(error){this.onError(error);}finally{this.preparingMusic=false;this.updateTransport();}
    });
    const form=document.querySelector('#timelineDialog');
    const reset=()=>{form.elements.source.value='0';form.elements.duration.value='';};
    form.querySelector('[name="audio_file"]').onchange=reset;
    form.querySelector('[name="asset"]').onchange=e=>{
      if(e.target.value!==m?.asset_id)reset();
      const path=e.target.selectedOptions[0]?.dataset.catalogFile;
      catalogAsset=path?{path}:null;
    };
    if(this.mountAssetPicker){
      const toggle=form.querySelector('#pickMusicAsset'),holder=form.querySelector('#musicAssetPicker');
      let picker;
      toggle.onclick=()=>{
        holder.hidden=!holder.hidden;toggle.setAttribute('aria-expanded',String(!holder.hidden));
        if(holder.hidden){picker?.pause();return;}
        picker??=this.mountAssetPicker(holder,['audio'],[{label:'Выбрать музыку',run:async asset=>{
          catalogAsset=asset;setAssetOption(form.elements.asset,asset);form.elements.audio_file.value='';reset();
          picker.pause();holder.hidden=true;toggle.setAttribute('aria-expanded','false');
        }}]);
        picker.open(this.project());
      };
    }
  }
  properties(target){
    const kind=this.selection?.kind,item=this.item(),fps=this.data().fps;
    if(!item||!['sound','music'].includes(kind)){super.properties(target);return;}
    if(kind==='sound'){
      target.innerHTML=`<strong class="tl-selection-hint">${esc(item.name)} · ${soundLanes[item.lane]}</strong>
        <dl class="tl-sound-summary"><dt>Начало на шкале</dt><dd>${sec(item.start_frame,fps)} с</dd>
        <dt>Длина на шкале</dt><dd>${sec(item.duration_frames,fps)} с</dd>
        <dt>Вход в источник</dt><dd>${sec(item.source_in_frame,fps)} с</dd>
        <dt>Громкость</dt><dd>${item.muted?'Выключено':Math.round(item.gain*100)+'%'}</dd></dl>
        <button id="tlEditSound" type="button" class="button">Точный ввод</button>`;
      target.querySelector('#tlEditSound').onclick=()=>this.onSound?.(item);
      return;
    }
    const m=item;
    target.innerHTML=`<form id="tlEdit"><span class="tl-item-kind" title="${esc(this.project().assets[m.asset_id]?.name??'')}">♪ МУЗЫКА</span>${[['start','Начало, с',m.start_frame],['duration','Длина, с',m.duration_frames],['source','Вход в песню, с',m.source_in_frame]].map(([name,label,value])=>`<label><span>${label}</span><input name="${name}" type="number" min="0" step="any" value="${sec(value,fps)}" required></label>`).join('')}<label><span>Громкость, %</span><input name="gain" type="number" min="0" max="100" step="1" value="${Math.round(m.gain*100)}"></label><label class="tl-music-mute"><span>Выключить</span><input name="muted" type="checkbox" ${m.muted?'checked':''}></label><button class="button small" type="submit">Применить</button><button id="tlReplaceMusic" class="button subtle small" type="button">Выбрать</button><button id="tlRemove" class="button subtle small" type="button">Убрать</button></form>`;
    target.querySelector('#tlReplaceMusic').onclick=()=>this.newMusic();target.querySelector('#tlRemove').onclick=()=>this.write('remove_music',{});
    target.querySelector('#tlEdit').onsubmit=e=>{e.preventDefault();const form=new FormData(e.target);this.write('update_music',{start_frame:toFrame(form.get('start'),fps),duration_frames:toFrame(form.get('duration'),fps),source_in_frame:toFrame(form.get('source'),fps),gain:Number(form.get('gain'))/100,muted:form.has('muted')});};
  }
}
