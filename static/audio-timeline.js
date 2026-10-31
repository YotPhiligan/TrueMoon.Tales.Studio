import {TimelineView} from './timeline.js';
import {escapeHtml as esc} from './logic.js';
import {frameEnd,frameCode,toFrame,snapFrame,trimClip} from './timeline-logic.js';
import {timelineDuration} from './playback-logic.js';
import {placedSounds,soundLanes} from './sound-logic.js';
import {waveformSvg} from './sound-ui.js';

const sec=(n,fps)=>(n/fps).toFixed(3);

export class AudioTimelineView extends TimelineView {
  render(){
    super.render();if(!this.project()||this.drag||!this.root.querySelector('.tl-toolbar'))return;
    const t=this.data();this.root.classList.add('audio-timeline');
    this.root.classList.toggle('has-timeline-issues',!!this.planIssues?.().length);
    const sounds=placedSounds(t),lanes=Object.keys(soundLanes).filter(l=>sounds.some(e=>e.lane===l));this.root.style.setProperty('--sound-height',lanes.length*36+'px');this.root.style.setProperty('--compact-sound-height',lanes.length*16+'px');
    this.root.style.setProperty('--title-height',t.titles?.length?'40px':'0px');this.root.style.setProperty('--compact-title-height',t.titles?.length?'18px':'0px');
    if(!this.root.querySelector('.tl-playback-bar')){
      const bar=document.createElement('div');bar.className='tl-playback-bar';
      bar.innerHTML=`<button id="tlPlay" class="button primary small" aria-label="Воспроизвести монтаж">▶</button><button id="tlStop" class="button subtle small" aria-label="Остановить монтаж">■</button><strong>МОНТАЖ</strong><span id="tlDuration"></span><span id="tlPlaybackStatus" role="status"></span><button id="tlBeat" class="button subtle small" title="Маркер на текущем кадре · M">◇ Бит <kbd>M</kbd></button><label class="tl-clip-audio"><input id="tlClipAudio" type="checkbox" ${t.clip_audio?'checked':''}>Звук клипов</label>`;
      this.root.prepend(bar);
      bar.querySelector('#tlPlay').onclick=()=>this.onPlay();bar.querySelector('#tlStop').onclick=()=>this.onStop();
      bar.querySelector('#tlBeat').onclick=()=>this.quickMarker();
      const exportButton=document.createElement('button');exportButton.id='tlExport';exportButton.className='button subtle small';exportButton.textContent='Экспорт MP4';exportButton.onclick=()=>this.onExport?.();bar.querySelector('.tl-clip-audio').before(exportButton);
      const titlesButton=document.createElement('button');titlesButton.id='tlTitles';titlesButton.className='button subtle small';titlesButton.textContent='Тт Титры';titlesButton.onclick=()=>this.onTitles?.();exportButton.before(titlesButton);
      bar.querySelector('#tlClipAudio').onchange=e=>this.write('set_clip_audio',{enabled:e.target.checked});
      const button=document.createElement('button');button.id='tlMusic';button.className='button subtle small';button.textContent='♪ Музыка';button.onclick=()=>this.newMusic();
      this.root.querySelector('#tlNewMarker').before(button);
      const label=document.createElement('span');label.textContent='Музыка';this.root.querySelector('.tl-gutter').append(label);
      for(const lane of lanes){const label=document.createElement('span');label.className='tl-sound-label';label.textContent=soundLanes[lane];this.root.querySelector('.tl-gutter').append(label);}
      if(t.titles?.length){const label=document.createElement('span');label.className='tl-title-label';label.textContent='Титры';this.root.querySelector('.tl-gutter').append(label);}
    }
    this.root.querySelectorAll('[data-title-edit]').forEach(b=>{b.onpointerdown=e=>e.stopPropagation();b.onclick=e=>{e.stopPropagation();this.onTitle?.(b.dataset.titleEdit);};});
    this.root.querySelectorAll('[data-sound-event]').forEach(button=>{button.onpointerdown=e=>e.stopPropagation();button.onclick=()=>this.onSound?.(sounds.find(s=>s.id===button.dataset.soundEvent));});
    const p=this.project();for(const e of sounds)this.soundWave?.(e.asset_id,e.stream,p.id).then(w=>{if(this.project().id!==p.id||this.project().revision!==p.revision)return;const button=this.root.querySelector(`[data-sound-event="${e.id}"]`);if(button){button.querySelector('svg')?.remove();button.insertAdjacentHTML('afterbegin',waveformSvg(w,e,t.fps));}}).catch(()=>{});
    this.updateTransport();const scroll=this.root.querySelector('.tl-scroll');
    this.root.querySelector('#tlStatus').textContent=this.planIssues?.().length?'ПЛАН ТРЕБУЕТ ПРОВЕРКИ · '+this.planIssues().map(i=>i.message).join(' '):'Space — просмотр · M — метка бита · края — обрезка · Alt — без привязки.';
    scroll.onscroll=()=>{if(this.waveRaf)cancelAnimationFrame(this.waveRaf);this.waveRaf=requestAnimationFrame(()=>{this.waveRaf=null;this.drawWaveform();});};
    if(t.music)this.loadWaveform(t.music.asset_id).then(()=>this.drawWaveform()).catch(e=>this.waveError(e));
  }
  canvas(t){
    const m=t.music;
    const sounds=placedSounds(t),lanes=Object.keys(soundLanes).filter(l=>sounds.some(e=>e.lane===l));
    const extra=lanes.map(l=>`<div class="tl-sound-lane" aria-label="${soundLanes[l]}">${sounds.filter(e=>e.lane===l).map(e=>`<button class="tl-sound-event${e.muted?' muted':''}${e.tail_frames?' tail':''}" data-sound-event="${e.id}" aria-label="Звук ${esc(e.name)}" title="${esc(e.name)} · ${soundLanes[l]} · поток ${e.stream+1}${e.tail_frames?' · хвост за окном':''} · нажмите для изменения участка/времени" style="left:${this.px(e.start_frame)}px;width:${this.px(e.duration_frames)}px"><span>${esc(e.name)} · ${e.muted?'ВЫКЛ':Math.round(e.gain*100)+'%'}</span></button>`).join('')}</div>`).join('');
    const titles=t.titles?.length?`<div class="tl-title-lane" aria-label="Дорожка титров">${t.titles.map(r=>`<button class="tl-title-event" data-title-edit="${r.id}" title="${esc(r.name)} · ${sec(r.start_frame,t.fps)}–${sec(r.start_frame+r.duration_frames,t.fps)} с · ${r.background==='black'?'чёрный фон':'поверх видео'}" style="left:${this.px(r.start_frame)}px;width:${this.px(r.duration_frames)}px">Тт ${esc(r.name)}</button>`).join('')}</div>`:'';
    return super.canvas(t)+`<div class="tl-music-lane">${m?`<div class="tl-music${this.selection?.kind==='music'?' selected':''}" data-kind="music" data-id="${m.id}" tabindex="0" role="button" aria-label="Музыкальная дорожка" style="left:${this.px(m.start_frame)}px;width:${this.px(m.duration_frames)}px"><canvas class="tl-waveform" aria-label="Звуковая волна"></canvas><span class="tl-music-label">♪ ${esc(this.project().assets[m.asset_id]?.name??'Музыка')} · ${m.muted?'ВЫКЛ':Math.round(m.gain*100)+'%'}</span><i class="tl-handle left" data-edge="left"></i><i class="tl-handle right" data-edge="right"></i></div>`:'<span class="tl-music-empty">♪ Выберите музыкальный файл, чтобы видеть акценты и собирать монтаж под звук.</span>'}</div>`+extra+titles;
  }
  updateTransport(){
    if(!this.root.querySelector('#tlPlay'))return;const state=this.playback?.()??{},playing=state.playing||state.preparing;
    const button=this.root.querySelector('#tlPlay');button.textContent=playing?'❚❚':'▶';button.setAttribute('aria-label',playing?'Пауза монтажа':'Воспроизвести монтаж');
    button.disabled=timelineDuration(this.data())<=0||!!this.planIssues?.().length;
    this.root.querySelector('#tlDuration').textContent=state.range?'Сегмент · '+frameCode(Math.round((state.range.end-state.range.start)*this.data().fps),this.data().fps):'до '+frameCode(Math.round(timelineDuration(this.data())*this.data().fps),this.data().fps);
    this.root.querySelector('#tlPlaybackStatus').textContent=state.preparing?'Подготовка звука…':state.playing?'Воспроизведение':this.preparingMusic?'Анализ звука…':'Пауза';
    this.root.querySelector('#tlClipAudio').checked=!!this.data().clip_audio;
  }
  advance(seconds,playing){
    const t=this.data();this.cursor=Math.max(0,Math.floor(seconds*t.fps+1e-7));this.refreshClipActions();
    const code=this.root.querySelector('#tlTimecode'),head=this.root.querySelector('.tl-playhead');
    if(code)code.textContent=frameCode(this.cursor,t.fps);if(head)head.style.left=this.px(this.cursor)+'px';
    if(!playing)localStorage.setItem('studio-cursor-'+this.project().id,this.cursor);
    const scroll=this.root.querySelector('.tl-scroll');
    if(playing&&scroll){const px=this.px(this.cursor);if(px<scroll.scrollLeft||px>scroll.scrollLeft+scroll.clientWidth-130)scroll.scrollLeft=Math.max(0,px-scroll.clientWidth*.25);}
  }
  seek(frame){
    this.cursor=Math.max(0,Math.round(frame));localStorage.setItem('studio-cursor-'+this.project().id,this.cursor);this.render();this.onSeek(this.cursor);
  }
  quickMarker(){this.write('add_marker',{name:'Бит '+(this.data().markers.length+1),frame:this.cursor});}
  targets(kind,id){
    const m=this.data().music;return [...super.targets(kind,id),...(m&&kind!=='music'?[m.start_frame,frameEnd(m)]:[])];
  }
  startDrag(e,element){this.onEditStart?.();super.startDrag(e,element);}
  startLibraryDrag(e,id){this.onEditStart?.();super.startLibraryDrag(e,id);}
  moveDrag(e){
    const d=this.drag;if(d?.kind!=='music'){super.moveDrag(e);return;}
    const scroll=this.root.querySelector('.tl-scroll'),fps=this.data().fps,delta=Math.round((e.clientX-d.x+scroll.scrollLeft-d.scroll)/this.zoom*fps);
    if(Math.abs(e.clientX-d.x)>3)d.moved=true;if(!d.moved)return;
    const raw=(d.edge==='right'?frameEnd(d.item):d.item.start_frame)+delta;
    const frame=this.snapping&&!e.altKey?snapFrame(raw,d.edge?0:d.item.duration_frames,this.targets('music',d.id),8/this.zoom*fps,d.edge?'edge':'move'):Math.max(0,raw);
    const capacity=Math.floor((this.project().assets[d.item.asset_id]?.audio_info?.seconds??0)*fps+1e-6);
    d.patch=d.edge?trimClip({...d.item,take_id:d.item.asset_id},d.edge,frame,capacity):{start_frame:frame};
    const m={...d.item,...d.patch};d.error=m.start_frame<0||m.duration_frames<1||m.source_in_frame+m.duration_frames>capacity?'Участок выходит за длину музыки.':'';
    d.el.style.left=this.px(m.start_frame)+'px';d.el.style.width=this.px(m.duration_frames)+'px';d.el.classList.toggle('invalid',!!d.error);
    this.drawWaveform(m);this.root.querySelector('#tlStatus').textContent=d.error||frameCode(m.start_frame,fps)+' · '+sec(m.duration_frames,fps)+' с';
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
    const p=this.project(),fps=this.data().fps,assets=Object.values(p.assets).filter(a=>a.kind==='audio'),m=this.data().music;
    this.dialog('Музыкальная дорожка',`<label class="field"><span>Из библиотеки проекта</span><select name="asset"><option value="">Выбрать файл…</option>${assets.map(a=>`<option value="${a.id}" ${a.id===m?.asset_id?'selected':''}>${esc(a.name)}</option>`).join('')}</select></label><label class="field"><span>Или загрузить звук</span><input name="audio_file" type="file" accept=".mp3,.wav,.ogg,.flac,.m4a,.aac,.opus"></label><div class="timeline-dialog-row"><label class="field"><span>Начало в ролике, с</span><input name="start" type="number" min="0" step="any" value="${sec(m?.start_frame??this.cursor,fps)}" required></label><label class="field"><span>Вход в песню, с</span><input name="source" type="number" min="0" step="any" value="${sec(m?.source_in_frame??0,fps)}" required></label><label class="field"><span>Длина, с · пусто = до конца файла</span><input name="duration" type="number" min="${1/fps}" step="any" value="${m?sec(m.duration_frames,fps):''}"></label></div>`,async form=>{
      const pid=p.id;this.preparingMusic=true;this.updateTransport();
      try{
        const file=form.get('audio_file');let aid=form.get('asset');
        if(file?.size)aid=await this.importAudio(file);
        if(!aid)throw new Error('Выберите музыкальный файл.');
        const wave=await this.loadWaveform(aid);if(this.project().id!==pid)return;
        const source=toFrame(form.get('source'),fps),duration=form.get('duration')?toFrame(form.get('duration'),fps):Math.floor(wave.seconds*fps+1e-6)-source;
        await this.write('set_music',{asset_id:aid,start_frame:toFrame(form.get('start'),fps),source_in_frame:source,duration_frames:duration,gain:m?.gain??.8,muted:m?.muted??false});
        if(this.data().music){this.selection={kind:'music',id:this.data().music.id};this.lastSignature='';this.render();}
      }catch(error){this.onError(error);}finally{this.preparingMusic=false;this.updateTransport();}
    });
    const form=document.querySelector('#timelineDialog');
    const reset=()=>{form.elements.source.value='0';form.elements.duration.value='';};
    form.querySelector('[name="audio_file"]').onchange=reset;
    form.querySelector('[name="asset"]').onchange=e=>{if(e.target.value!==m?.asset_id)reset();};
  }
  properties(){
    if(this.selection?.kind!=='music'){super.properties();return;}
    const m=this.data().music,target=this.root.querySelector('#tlProperties'),fps=this.data().fps;if(!m){super.properties();return;}
    target.innerHTML=`<form id="tlEdit"><span class="tl-item-kind" title="${esc(this.project().assets[m.asset_id]?.name??'')}">♪ МУЗЫКА</span>${[['start','Начало, с',m.start_frame],['duration','Длина, с',m.duration_frames],['source','Вход в песню, с',m.source_in_frame]].map(([name,label,value])=>`<label><span>${label}</span><input name="${name}" type="number" min="0" step="any" value="${sec(value,fps)}" required></label>`).join('')}<label><span>Громкость, %</span><input name="gain" type="number" min="0" max="100" step="1" value="${Math.round(m.gain*100)}"></label><label class="tl-music-mute"><span>Выключить</span><input name="muted" type="checkbox" ${m.muted?'checked':''}></label><button class="button small">Применить</button><button id="tlReplaceMusic" class="button subtle small" type="button">Выбрать</button><button id="tlRemove" class="button subtle small" type="button">Убрать</button></form>`;
    target.querySelector('#tlReplaceMusic').onclick=()=>this.newMusic();target.querySelector('#tlRemove').onclick=()=>this.write('remove_music',{});
    target.querySelector('#tlEdit').onsubmit=e=>{e.preventDefault();const form=new FormData(e.target);this.write('update_music',{start_frame:toFrame(form.get('start'),fps),duration_frames:toFrame(form.get('duration'),fps),source_in_frame:toFrame(form.get('source'),fps),gain:Number(form.get('gain'))/100,muted:form.has('muted')});};
  }
}
