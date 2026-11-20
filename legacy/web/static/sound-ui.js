import {escapeHtml as esc} from './logic.js';
import {timelineOf,toFrame} from './timeline-logic.js';
import {placedSounds,soundLanes} from './sound-logic.js';
import {setAssetOption} from './asset-library.js';

export function waveformSvg(wave,event,fps){
  if(!wave)return '';
  const points=[],start=event.source_in_frame/fps*wave.peaks_per_second,length=event.duration_frames/fps*wave.peaks_per_second;
  for(let i=0;i<120;i++){
    const a=Math.floor(start+length*i/120),b=Math.min(wave.peaks.length,Math.max(a+1,Math.ceil(start+length*(i+1)/120)));let peak=0;
    for(let j=Math.max(0,a);j<b;j++)peak=Math.max(peak,wave.peaks[j]);
    points.push(`M${i} ${20-peak*19}V${20+peak*19}`);
  }
  return `<svg viewBox="0 0 120 40" preserveAspectRatio="none" aria-label="Звуковая волна" role="img"><path d="${points.join('')}" fill="none" stroke="currentColor" stroke-width=".7"/></svg>`;
}

export class SoundDesk{
  constructor(options){Object.assign(this,options);this.waves=new Map();}
  base(pid=this.getProject().id){return `/api/projects/${encodeURIComponent(pid)}`;}
  async wave(aid,stream,pid=this.getProject().id){
    const key=pid+':'+aid+':'+stream,p=this.getProject(),a=p.assets[aid],stamp=JSON.stringify([a?.modified_ns,a?.bytes]);
    if(this.waves.get(key)?.stamp!==stamp){const promise=this.api(`${this.base(pid)}/waveform?asset=${encodeURIComponent(aid)}&stream=${stream}`);this.waves.set(key,{stamp,promise});promise.catch(()=>this.waves.delete(key));}
    return this.waves.get(key).promise;
  }
  render(segment,root,item=null){
    const p=this.getProject(),t=timelineOf(p),fps=p.fps;
    const placed=placedSounds(t),clip=item?t.clips.find(c=>c.id===item.id):null;
    const events=item?(t.sound_events??[]).filter(e=>e.clip_id===item.id).map(e=>placed.find(row=>row.id===e.id)??{...e,outside:true}):
      placed.filter(e=>!e.clip_id&&e.segment_id===segment.id);
    const title=item?'ЗВУК БЛОКА':'ЗВУК СЕГМЕНТА';
    const hint=item?'Выберите звук из любого дубля или добавьте аудиофайл. Дорожки следуют за блоком.':
      'Отдельные реплики, эффекты и атмосфера. '+(p.timeline?.clip_audio?'Родной звук клипов включён.':'Родной звук клипов выключен.');
    root.className='sound-section';root.innerHTML=`<header><strong>${title}</strong><button class="button subtle small" data-sound-new ${item&&!clip?'disabled':''}>+ Аудиодорожка</button></header><p class="small-note">${item&&!clip?'Сначала задайте время блока и окно сегмента.':hint}</p>${events.map(e=>`<button class="sound-row" data-sound-edit="${e.id}"><span><b>${esc(e.name)}</b><small>${soundLanes[e.lane]} · ${(e.offset_frames/fps).toFixed(3)} с от начала · ${(e.duration_frames/fps).toFixed(3)} с · ${e.muted?'ВЫКЛ':Math.round(e.gain*100)+'%'} · поток ${e.stream+1}${e.outside?' · за границей блока':''}${e.tail_frames?' · хвост '+(e.tail_frames/fps).toFixed(3)+' с':''}</small></span><span class="sound-wave" data-sound-wave="${e.id}"></span></button>`).join('')||'<p class="small-note">Звуковых событий пока нет.</p>'}`;
    root.querySelector('[data-sound-new]').onclick=()=>this.open(segment.id,null,item?.id).catch(this.onError);
    root.querySelectorAll('[data-sound-edit]').forEach(b=>b.onclick=()=>this.open(segment.id,b.dataset.soundEdit,item?.id).catch(this.onError));
    for(const e of events)this.wave(e.asset_id,e.stream,p.id).then(w=>{if(this.getProject().id===p.id&&root.isConnected)root.querySelector(`[data-sound-wave="${e.id}"]`)?.replaceChildren(this.svgElement(w,e,fps));}).catch(()=>{});
  }
  svgElement(w,e,fps){const holder=document.createElement('div');holder.innerHTML=waveformSvg(w,e,fps);return holder.firstElementChild;}
  active(root,pid){return this.getProject()?.id===pid&&root?.isConnected&&document.querySelector('#soundDialog')===root&&root.closest('dialog')?.open!==false;}
  async open(segmentId,eventId=null,clipId=null,initialAsset=null){
    const pid=this.getProject().id;await this.flush();if(this.getProject().id!==pid)return;this.pause();
    const p=this.getProject(),t=timelineOf(p),e=t.sound_events?.find(e=>e.id===eventId);
    if(eventId&&!e)throw new Error('Звуковое событие не найдено.');
    const id=e?.clip_id??clipId,clip=id?t.clips.find(c=>c.id===id):null;
    if(id&&!clip)throw new Error('Сначала задайте время блока и окно сегмента.');
    const s=t.segments.find(s=>s.id===(clip?.segment_id??segmentId)),owner=clip??s;
    if(!s)throw new Error('Сначала задайте допустимое временное окно сегмента.');
    const ownerName=clip?clip.story_title??p.blocks.find(b=>b.id===clip.block_id)?.title??'Блок':s.name;
    const sourceNames=new Map(p.blocks.flatMap(b=>b.takes.map(take=>[take.video_asset_id,b.label+' · '+take.name])));
    const fps=p.fps,seconds=n=>(n/fps).toFixed(6).replace(/0+$/,'').replace(/\.$/,''),assets=Object.values(p.assets).filter(a=>['audio','video'].includes(a.kind));let revision=p.revision,pending=false,loadSerial=0;
    this.modal(e?'Звуковое событие':clip?'Добавить аудиодорожку блока':'Добавить звук сегмента',`<form id="soundDialog" class="sound-form"><p class="small-note">${esc(ownerName)} · окно ${(owner.duration_frames/fps).toFixed(3)} с. Скорость звука 1×.${clip?' Звук обрезается по концу блока.':''}</p><label class="field"><span>Название звука</span><input name="name" value="${esc(e?.name??'')}" maxlength="160" required></label><label class="field"><span>Дорожка</span><select name="lane">${Object.entries(soundLanes).map(([id,name])=>`<option value="${id}" ${e?.lane===id?'selected':''}>${name}</option>`).join('')}</select></label><label class="field"><span>Источник — аудио или видео</span><select name="asset"><option value="">Выберите источник…</option>${assets.map(a=>`<option value="${a.id}" ${e?.asset_id===a.id?'selected':''}>${esc(sourceNames.get(a.id)??a.name)}</option>`).join('')}</select></label>${this.mountAssetPicker?'<button type="button" class="button subtle small" id="pickSoundAsset" aria-expanded="false">Выбрать из библиотеки ассетов</button><section class="asset-picker-inline" id="soundAssetPicker" hidden></section>':''}<div class="sound-upload"><input name="file" type="file" accept="audio/*,video/mp4,video/webm,video/quicktime" aria-label="Файл звука или видео"><button type="button" class="button subtle small" id="soundUpload">Добавить файл в библиотеку</button></div><label class="field"><span>Звуковой поток</span><select name="stream" aria-label="Звуковой поток"></select></label><div class="sound-wave sound-edit-wave" id="soundWave"></div><div class="timeline-dialog-row">${[['offset',clip?'От начала блока, с':'От начала сегмента, с',e?.offset_frames??0],['source','Вход в источник, с',e?.source_in_frame??0],['duration','Длина, с',e?.duration_frames??Math.min(fps,owner.duration_frames)]].map(([name,label,value])=>`<label class="field"><span>${label}</span><input type="number" name="${name}" min="${name==='duration'?1/fps:0}" step="any" value="${seconds(value)}" required></label>`).join('')}</div><div class="timeline-dialog-row"><label class="field"><span>Громкость, %</span><input name="gain" type="number" min="0" max="100" step="1" value="${Math.round((e?.gain??1)*100)}" required></label><label class="field"><span>Выключить</span><input name="muted" type="checkbox" ${e?.muted?'checked':''}></label></div><p id="soundHint" class="small-note"></p><p id="soundError" role="status"></p><div class="sound-actions"><button class="button primary" type="submit">Сохранить звук</button>${e?'<button class="button subtle" type="button" id="soundRemove">Убрать звук</button>':''}</div></form>`);
    const root=document.querySelector('#soundDialog'),field=n=>root.elements[n],error=root.querySelector('#soundError');let currentWave=null;
    const sourceFile=()=>field('asset').selectedOptions?.[0]?.dataset.catalogFile;
    const sourceQuery=()=>new URLSearchParams(sourceFile()?{file:sourceFile()}:{asset:field('asset').value});
    const update=()=>{const duration=toFrame(field('duration').value,fps),offset=toFrame(field('offset').value,fps),tail=Math.max(0,offset+duration-owner.duration_frames);root.querySelector('#soundHint').textContent=`Начало в проекте: ${((owner.start_frame+offset)/fps).toFixed(3)} с${tail?(clip?' · обрезка по границе блока: ':' · хвост за окном: ')+(tail/fps).toFixed(3)+' с':''}${currentWave?' · источник '+currentWave.seconds.toFixed(3)+' с':''}`;root.querySelector('#soundWave').innerHTML=waveformSvg(currentWave,{source_in_frame:toFrame(field('source').value,fps),duration_frames:duration},fps);};
    const loadWave=async()=>{const n=++loadSerial;currentWave=null;update();if(!field('asset').value||field('stream').value==='')return;try{const stream=Number(field('stream').value),query=sourceQuery();
      query.set('stream',String(stream));const w=sourceFile()?await this.api(this.base(pid)+'/waveform?'+query):await this.wave(field('asset').value,stream,pid);if(n===loadSerial&&this.active(root,pid)){currentWave=w;update();}}catch(err){if(n===loadSerial&&this.active(root,pid))error.textContent=err.message;}};
    const loadStreams=async(selected=0)=>{const aid=field('asset').value,n=++loadSerial;field('stream').replaceChildren();currentWave=null;update();if(!aid)return;try{const streams=await this.api(this.base(pid)+'/audio-streams?'+sourceQuery());if(n!==loadSerial||!this.active(root,pid))return;field('stream').innerHTML=streams.map(a=>`<option value="${a.index}" ${a.index===selected?'selected':''}>${a.index+1}: ${esc([a.title,a.language,a.codec,a.channels+' кан.'].filter(Boolean).join(' · '))}</option>`).join('');error.textContent=streams.length?'':'В источнике нет звукового потока.';await loadWave();}catch(err){if(this.active(root,pid))error.textContent=err.message;}};
    const selectAsset=asset=>{
      setAssetOption(field('asset'),asset);
      field('source').value='0';
      if(!field('name').value)field('name').value=asset.name.replace(/\.[^.]+$/,'').slice(0,160);
    };
    if(this.mountAssetPicker){
      const toggle=root.querySelector('#pickSoundAsset'),holder=root.querySelector('#soundAssetPicker');
      let picker;
      toggle.onclick=()=>{
        holder.hidden=!holder.hidden;toggle.setAttribute('aria-expanded',String(!holder.hidden));
        if(holder.hidden){picker?.pause();return;}
        picker??=this.mountAssetPicker(holder,['audio','video'],[{label:'Выбрать источник звука',run:async asset=>{
          selectAsset(asset);picker.pause();holder.hidden=true;toggle.setAttribute('aria-expanded','false');await loadStreams();
        }}]);
        picker.open(this.getProject());
      };
    }
    field('asset').onchange=()=>{field('source').value='0';loadStreams();};field('stream').onchange=loadWave;
    for(const name of ['offset','source','duration'])field(name).oninput=update;
    const write=async(action,payload)=>{if(pending)return;pending=true;root.querySelectorAll('button').forEach(b=>b.disabled=true);try{await this.flush();if(!this.active(root,pid))return;const result=await this.api(`${this.base(pid)}/timeline`,'POST',{expected_revision:revision,actor:'author',action,payload});if(!this.active(root,pid))return;this.closeModal();this.onProject(result);}catch(err){if(this.active(root,pid))error.textContent=err.message;}finally{pending=false;root.querySelectorAll('button').forEach(b=>b.disabled=false);}};
    root.onsubmit=ev=>{ev.preventDefault();if(field('stream').value===''){error.textContent='Выберите источник со звуком.';return;}return write(e?'update_sound':'add_sound',{...(e?{id:e.id}:{}),segment_id:s.id,...(clip?{clip_id:clip.id}:{}),name:field('name').value,lane:field('lane').value,...(sourceFile()?{asset_selection:{file:sourceFile()}}:{asset_id:field('asset').value}),stream:Number(field('stream').value),offset_frames:toFrame(field('offset').value,fps),source_in_frame:toFrame(field('source').value,fps),duration_frames:toFrame(field('duration').value,fps),gain:Number(field('gain').value)/100,muted:field('muted').checked});};
    if(e)root.querySelector('#soundRemove').onclick=()=>write('remove_sound',{id:e.id});
    root.querySelector('#soundUpload').onclick=async()=>{if(pending)return;const file=field('file').files[0];if(!file){error.textContent='Выберите файл.';return;}pending=true;try{if(this.getProject().revision!==revision)throw new Error('Проект изменился. Откройте форму заново перед загрузкой.');const aid=await this.importFile(file);if(!this.active(root,pid))return;revision=this.getProject().revision;const a=this.getProject().assets[aid],option=document.createElement('option');option.value=aid;option.textContent=a.name;field('asset').append(option);field('asset').value=aid;field('source').value='0';error.textContent='';await loadStreams();}catch(err){if(this.active(root,pid))error.textContent=err.message;}finally{pending=false;}};
    if(initialAsset){selectAsset(initialAsset);await loadStreams();}
    else{update();if(e)await loadStreams(e.stream);}
  }
}
