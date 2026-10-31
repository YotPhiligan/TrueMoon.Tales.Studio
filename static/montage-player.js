import {timelineOf,frameCode} from './timeline-logic.js';
import {timelineDuration,placementAt,musicSchedule,audibleContextTime,criticalSignature,segmentPlayback} from './playback-logic.js';
import {placedSounds,soundSchedule} from './sound-logic.js';
import {transitionLevel} from './transitions-logic.js';
import {titleOpacity,projectFrameBox,placeTitleImage} from './titles-logic.js';

export class MontagePlayer {
  constructor(options){
    Object.assign(this,options);this.position=0;this.playing=false;this.preparing=false;this.serial=0;
    this.signature='';this.projectId=null;this.buffers=new Map();this.active=null;this.raf=null;
  }
  project(){return this.scopeProject??this.getProject();}
  timeline(){return timelineOf(this.project());}
  duration(){return timelineDuration(this.timeline());}
  start(){return this.range?.start??0;}
  end(){return this.range?.end??this.duration();}
  selectSegment(id=null){
    if(this.projectId!==this.getProject()?.id)this.syncProject();
    if(id)segmentPlayback(this.getProject(),id);
    this.pause();this.segmentId=id;this.syncProject();
    this.position=id?this.start():Math.max(this.start(),Math.min(this.end(),this.position));
    this.paint(this.position);this.onTime?.(this.position,false);this.state('paused');
  }
  context(){
    if(!this.ctx){const AudioClass=globalThis.AudioContext??globalThis.webkitAudioContext;if(!AudioClass)throw new Error('В браузере недоступен Web Audio.');this.ctx=new AudioClass({latencyHint:'interactive'});}
    return this.ctx;
  }
  time(){return this.playing?Math.min(this.end(),this.position+Math.max(0,audibleContextTime(this.ctx,performance.now())-this.anchor)):this.position;}
  state(value){this.onState?.({state:value,playing:this.playing,preparing:this.preparing,time:this.time(),duration:this.end()-this.start(),start:this.start(),end:this.end(),range:this.range??null});}
  mount(stage){
    if(this.stage===stage&&this.container?.parentNode===stage)return;
    for(const video of this.videos??[])video.pause();
    this.resizeObserver?.disconnect();this.titleKey=null;this.titleReady=null;
    this.stage=stage;stage.replaceChildren();this.container=document.createElement('div');this.container.className='montage-preview';
    this.container.dataset.playback='paused';stage.append(this.container);
    this.videos=[0,1].map(()=>{
      const v=document.createElement('video');v.preload='auto';v.playsInline=true;v.muted=true;v.hidden=true;v.setAttribute('aria-label','Клип общего монтажа');this.container.append(v);
      v.addEventListener('canplay',()=>{if(v===this.active)this.paint(this.time());});
      v.addEventListener('error',()=>{if(v===this.active){this.showMessage('Клип недоступен','Проверьте исходный файл. Музыка и время продолжаются.');}});
      return v;
    });
    this.image=document.createElement('img');this.image.alt='Опорный кадр планируемого блока';this.image.hidden=true;this.container.append(this.image);
    this.message=document.createElement('div');this.message.className='montage-message';this.container.append(this.message);
    this.blackout=document.createElement('div');this.blackout.className='montage-blackout';this.blackout.setAttribute('aria-hidden','true');this.container.append(this.blackout);
    this.titleStage=document.createElement('div');this.titleStage.className='montage-titles';this.container.append(this.titleStage);
    this.badge=document.createElement('div');this.badge.className='montage-badge';this.container.append(this.badge);
    this.active=null;this.paint(this.position);
    if(globalThis.ResizeObserver){this.resizeObserver=new ResizeObserver(()=>this.fitProjectFrame());this.resizeObserver.observe(this.container);}
  }
  detach(){this.pause();this.resizeObserver?.disconnect();for(const v of this.videos??[])v.pause();this.stage=null;this.container=null;this.titleStage=null;this.titleKey=null;this.videos=[];this.active=null;}
  async loadTitleLayers(){
    if(!this.titleStage)return;
    const p=this.getProject(),rows=p?.timeline?.titles??[],stage=this.titleStage,key=JSON.stringify([p?.id,p?.output_size,rows]);
    if(key===this.titleKey)return this.titleReady;
    this.titleKey=key;stage.replaceChildren();this.titleNodes=[];
    if(!rows.length){this.titleReady=Promise.resolve();return this.titleReady;}
    this.titleReady=(async()=>{
      const response=await fetch(`/api/projects/${encodeURIComponent(p.id)}/title-layers`);
      if(!response.ok)throw new Error('Не удалось загрузить титры.');const data=await response.json();
      const nodes=await Promise.all(data.layers.map(async layer=>{
        const wrapper=document.createElement('div');wrapper.className='montage-title'+(layer.background==='black'?' black':'');wrapper.dataset.titleId=layer.id;wrapper.hidden=true;wrapper.setAttribute('role','img');wrapper.setAttribute('aria-label',rows.find(r=>r.id===layer.id)?.text??'Титр');
        const img=document.createElement('img');img.alt='';img.src=layer.url;placeTitleImage(img,layer);wrapper.append(img);
        if(img.decode)await img.decode();return {id:layer.id,wrapper,img,layer};
      }));
      if(this.titleKey!==key||this.titleStage!==stage||this.getProject()?.id!==p.id)return;
      this.titleNodes=nodes;stage.replaceChildren(...nodes.map(n=>n.wrapper));this.paintTitles(this.time()*this.timeline().fps);this.fitProjectFrame();
    })();
    // Keep failed preparation visible and block playback; changing a title retries.
    this.titleReady.catch(error=>{if(this.titleKey===key){this.pause();this.onError?.(error);}});
    return this.titleReady;
  }
  fitProjectFrame(){
    if(!this.container||!this.titleStage)return;
    const p=this.getProject(),hasTitles=!!p?.timeline?.titles?.length,box=hasTitles?projectFrameBox(this.container.clientWidth,this.container.clientHeight,p.output_size):null;
    for(const node of [...(this.videos??[]),this.image,this.blackout,this.titleStage].filter(Boolean)){
      if(box)Object.assign(node.style,{left:box.x+'px',top:box.y+'px',width:box.width+'px',height:box.height+'px',right:'auto',bottom:'auto'});
      else for(const field of ['left','top','width','height','right','bottom'])node.style[field]='';
    }
  }
  paintTitles(frame){
    const rows=this.timeline().titles??[],levels={};
    for(const node of this.titleNodes??[]){const row=rows.find(r=>r.id===node.id),level=row?titleOpacity(row,frame):0;node.wrapper.hidden=level<=0;node.wrapper.style.opacity=level.toFixed(6);levels[node.id]=level;}
    if(this.container)this.container.dataset.titleOpacities=JSON.stringify(levels);
  }
  syncProject(){
    const raw=this.getProject();if(!raw)return;
    const switched=this.projectId!==raw.id;
    if(switched&&this.projectId){this.pause();this.segmentId=null;}
    let resolved={project:raw,range:null};
    if(this.segmentId){try{resolved=segmentPlayback(raw,this.segmentId);}catch(error){this.pause();this.segmentId=null;this.onError?.(error);}}
    const p=resolved.project,signature=JSON.stringify([criticalSignature(p),resolved.range]);
    if(this.signature!==signature)this.pause();
    this.scopeProject=p;this.range=resolved.range;this.projectId=p.id;this.signature=signature;
    this.position=switched?this.start():Math.max(this.start(),Math.min(this.position,this.end()));
    if(switched)this.active=null;
    if(this.musicGain){const m=this.timeline().music;this.musicGain.gain.value=m&&!m.muted?m.gain:0;}
    if(this.container)this.paint(this.time());
  }
  async musicBuffer(){
    const p=this.project(),m=this.timeline().music;if(!m)return null;
    const a=p.assets[m.asset_id],key=p.id+':'+m.asset_id+':'+a?.modified_ns+':'+a?.bytes;
    if(!this.buffers.has(key)){
      const context=this.context();const promise=(async()=>{
        const response=await fetch(this.media(m.asset_id,p.id));if(!response.ok)throw new Error('Не удалось загрузить музыкальный файл.');
        return context.decodeAudioData(await response.arrayBuffer());
      })();
      this.buffers.set(key,promise);promise.catch(()=>this.buffers.delete(key));
      // Keep only a few decoded songs; this is a preview, not a media library.
      while(this.buffers.size>3)this.buffers.delete(this.buffers.keys().next().value);
    }
    return this.buffers.get(key);
  }
  async play(){
    if(this.playing)return;this.syncProject();if(this.end()<=this.start()){this.state('empty');return;}
    const token=++this.serial;this.preparing=true;this.state('preparing');
    try{
      const context=this.context();await context.resume();const buffer=await this.musicBuffer();
      const sounds=await this.soundBuffers();await this.loadTitleLayers();
      if(token!==this.serial)return;
      if(this.position>=this.end())this.position=this.start();
      this.anchor=context.currentTime+.08;this.playing=true;this.preparing=false;
      this.stats={video:0,buffering:0,resyncs:0,maxDrift:0,visits:[]};
      const schedule=buffer?musicSchedule(this.timeline(),this.position,buffer.duration,this.end()):null;
      if(schedule){
        this.musicGain=context.createGain();this.musicGain.gain.value=schedule.gain;this.musicGain.connect(context.destination);
        this.musicSource=context.createBufferSource();this.musicSource.buffer=buffer;this.musicSource.connect(this.musicGain);
        this.musicSource.start(this.anchor+schedule.delay,schedule.offset,schedule.duration);
      }
      this.soundNodes=[];
      for(const {event,buffer:audio} of sounds){
        const schedule=soundSchedule(event,this.timeline().fps,this.position,audio.duration,this.end());if(!schedule)continue;
        const gain=context.createGain();gain.gain.value=schedule.gain;gain.connect(context.destination);
        const source=context.createBufferSource();source.buffer=audio;source.connect(gain);source.start(this.anchor+schedule.delay,schedule.offset,schedule.duration);
        this.soundNodes.push({source,gain});
      }
      this.state('playing');this.tick();
    }catch(error){
      if(token!==this.serial)return;this.preparing=false;this.playing=false;this.stopMusic();this.state('error');this.onError?.(error);
    }
  }
  stopMusic(){
    for(const node of this.soundNodes??[]){try{node.source.stop();}catch{}node.source.disconnect();node.gain.disconnect();}this.soundNodes=[];
    try{this.musicSource?.stop();}catch{}
    this.musicSource?.disconnect();this.musicGain?.disconnect();
    this.musicSource=null;this.musicGain=null;
  }
  async soundBuffers(){
    const p=this.project(),position=this.position>=this.end()?this.start():this.position,events=placedSounds(this.timeline()).filter(e=>!e.muted&&e.gain>0&&e.start_frame/this.timeline().fps<this.end()&&(e.start_frame+e.duration_frames)/this.timeline().fps>position);
    this.soundCache??=new Map();const context=this.ctx;
    return Promise.all(events.map(async event=>{
      const a=p.assets[event.asset_id],key=JSON.stringify([p.id,event.id,event.asset_id,event.stream,event.source_in_frame,event.duration_frames,a?.modified_ns,a?.bytes]);
      if(!this.soundCache.has(key)){
        const promise=(async()=>{const response=await fetch(`/api/projects/${encodeURIComponent(p.id)}/sound-preview?event=${encodeURIComponent(event.id)}&v=${p.revision}`);if(!response.ok)throw new Error('Не удалось подготовить звук «'+event.name+'».');return context.decodeAudioData(await response.arrayBuffer());})();
        this.soundCache.set(key,promise);promise.catch(()=>this.soundCache.delete(key));while(this.soundCache.size>16)this.soundCache.delete(this.soundCache.keys().next().value);
      }
      return {event,buffer:await this.soundCache.get(key)};
    }));
  }
  pause(){
    const position=this.time();this.serial++;this.playing=false;this.preparing=false;this.position=position;
    this.stopMusic();if(this.raf!=null)cancelAnimationFrame(this.raf);this.raf=null;
    for(const video of this.videos??[])video.pause();
    if(this.container)this.container.dataset.playback='paused';
    this.onTime?.(this.position,false);this.state('paused');
  }
  stop(){this.pause();this.position=this.start();this.paint(this.position);this.onTime?.(this.position,false);this.state('stopped');}
  seek(seconds){
    const resume=this.playing;this.pause();this.position=Math.max(this.start(),Math.min(this.end(),Number(seconds)||0));
    this.paint(this.position);this.onTime?.(this.position,false);if(resume)this.play();else this.state('paused');
  }
  toggle(){if(this.playing||this.preparing)this.pause();else this.play();}
  tick(){
    if(!this.playing)return;const time=this.time();this.paint(time);this.onTime?.(time,true);
    if(time>=this.end()){
      this.pause();this.position=this.end();this.paint(this.position);this.onTime?.(this.position,false);this.state('ended');return;
    }
    this.raf=requestAnimationFrame(()=>this.tick());
  }
  showMessage(title,detail){
    if(!this.message)return;this.message.replaceChildren();const heading=document.createElement('strong');heading.textContent=title;
    const text=document.createElement('span');text.textContent=detail;this.message.append(heading,text);this.message.hidden=false;
  }
  setSource(video,clip,take){
    const key=clip.id+':'+take.video_asset_id;
    if(video.dataset.slot===key)return;
    video.pause();video.dataset.slot=key;video.dataset.clip=clip.id;video.src=this.media(take.video_asset_id,this.getProject().id);video.load();
    const apply=()=>{if(video.dataset.slot===key)video.currentTime=clip.source_in_frame/this.timeline().fps;};
    if(video.readyState)apply();else video.addEventListener('loadedmetadata',apply,{once:true});
  }
  paint(time){
    if(!this.container||!this.getProject())return;
    const p=this.project(),t=this.timeline(),placement=placementAt(p,time),{clip,block,take}=placement;
    this.loadTitleLayers().catch(()=>{});this.fitProjectFrame();this.paintTitles(time*t.fps);
    this.container.dataset.timelineTime=time.toFixed(6);this.container.dataset.playback=this.playing?'playing':'paused';
    const level=transitionLevel(t,time*t.fps);this.container.dataset.transitionLevel=level.toFixed(6);
    if(this.blackout){this.blackout.style.opacity=(1-level).toFixed(6);this.blackout.hidden=level>=1;}
    this.container.dataset.scopeSegment=this.segmentId??'';this.container.dataset.localTime=(time-this.start()).toFixed(6);
    this.container.dataset.kind=placement.kind;this.container.dataset.clip=clip?.id??'';
    if(this.playing&&this.stats){
      const visit=placement.kind+(clip?':'+clip.id:'');
      if(this.stats.visits.at(-1)!==visit){this.stats.visits.push(visit);this.container.dataset.visits=JSON.stringify(this.stats.visits);}
    }
    this.badge.textContent=frameCode(Math.floor(time*t.fps),t.fps)+' · '+(block?.label??'Пауза');
    this.image.hidden=true;this.message.hidden=true;
    if(placement.kind!=='video'){
      delete this.container.dataset.sourceTime;
      for(const v of this.videos){v.pause();v.hidden=true;}this.active=null;
      if(placement.kind==='placeholder'){
        const aid=block?.references.find(r=>block.guides.some(g=>g.reference_id===r.id&&g.frame===0))?.asset_id??block?.references[0]?.asset_id;
        if(aid){this.image.src=this.media(aid,p.id);this.image.hidden=false;}
        this.showMessage('Заглушка · '+(block?.label??'Блок'),'Видео ещё не назначено.');
      }else this.showMessage(placement.kind==='missing'?'Нет исходного дубля':'Пауза',placement.kind==='missing'?'Проверьте выбранный материал.':'');
    }else{
      const key=clip.id+':'+take.video_asset_id;
      let video=this.videos.find(v=>v.dataset.slot===key);
      if(!video)video=this.videos.find(v=>v!==this.active)??this.videos[0];
      this.setSource(video,clip,take);this.active=video;
      for(const other of this.videos)if(other!==video){other.hidden=true;other.pause();}
      video.muted=!t.clip_audio;video.playbackRate=placement.rate??1;video.preservesPitch=true;
      const clockReady=this.playing&&!placement.source_finished&&this.ctx.state==='running'&&audibleContextTime(this.ctx,performance.now())>=this.anchor;
      // play() also drives a metadata-only preload into actual decoding.
      // Waiting for canplay before requesting play can otherwise stall a cut.
      if(clockReady&&video.paused){
        const slot=video.dataset.slot;
        video.play().catch(error=>{if(error.name!=='AbortError'&&this.playing&&this.active===video&&video.dataset.slot===slot)this.showMessage('Не удалось запустить клип','Нажмите воспроизведение ещё раз.');});
      }
      if(!clockReady)video.pause();
      if(video.readyState>=2){
        const expected=Math.min(placement.source_seconds,Math.max(0,video.duration-.001));
        const drift=Math.abs(video.currentTime-expected);
        if(this.playing&&this.stats){this.stats.video++;if(!video.seeking)this.stats.maxDrift=Math.max(this.stats.maxDrift,drift);}
        // Paused scrubbing is exact. During playback leave normal decoding
        // alone; only recover a substantial stall, at most once per second.
        const recover=this.playing&&drift>.25&&(!video.dataset.alignedAt||performance.now()-Number(video.dataset.alignedAt)>1000);
        if(!video.seeking&&(((!this.playing||placement.source_finished)&&drift>.002)||recover)){video.currentTime=expected;if(recover){video.dataset.alignedAt=performance.now();if(this.stats)this.stats.resyncs++;}}
        video.hidden=false;this.container.dataset.sourceTime=expected.toFixed(6);
      }else{if(this.playing&&this.stats)this.stats.buffering++;video.hidden=true;this.showMessage('Загрузка клипа','Музыкальная шкала сохраняет своё время.');}
    }
    if(placement.kind==='gap'&&(t.titles??[]).some(r=>titleOpacity(r,time*t.fps)>0))this.message.hidden=true;
    // Preload the next selected video without rebuilding the current player.
    const next=t.clips.filter(c=>c.take_id&&c.start_frame/t.fps>time+.001).toSorted((a,b)=>a.start_frame-b.start_frame)[0];
    if(next){
      const b=p.blocks.find(b=>b.id===next.block_id),take=b?.takes.find(x=>x.id===next.take_id),idle=this.videos.find(v=>v!==this.active);
      if(take&&idle&&!this.videos.some(v=>v.dataset.slot===next.id+':'+take.video_asset_id))this.setSource(idle,next,take);
    }
    if(this.stats){this.container.dataset.maxDrift=this.stats.maxDrift.toFixed(4);this.container.dataset.videoTicks=this.stats.video;this.container.dataset.bufferTicks=this.stats.buffering;this.container.dataset.resyncs=this.stats.resyncs;}
  }
}
