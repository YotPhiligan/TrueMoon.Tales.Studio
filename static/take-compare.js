import {escapeHtml as esc,timecode} from './logic.js';

export function stepTime(time,fps,direction,end){
  const frame=time*fps;
  const next=direction>0?Math.floor(frame+1e-7)+1:Math.ceil(frame-1e-7)-1;
  return Math.min(end,Math.max(0,next/fps));
}

export function videoTime(time,duration){
  return Math.min(time,Math.max(0,duration-.001));
}

const seconds=value=>Number.isFinite(value)&&value>0?value.toFixed(3)+' с':null;
const referenceText=ref=>ref?[ref.file,ref.role,...ref.guides.map(frame=>frame===-1?'Последний кадр':'Guide '+frame)].filter(Boolean).join(' · '):null;

function referenceStatus(a,b){
  if(!a||!b)return 'unknown';
  const key=a.sha256&&b.sha256?'sha256':'source_key';
  return a[key]===b[key]&&a.role===b.role&&JSON.stringify(a.guides)===JSON.stringify(b.guides)?'same':'different';
}

export function comparisonRows(a,b){
  const make=(name,left,right,status)=>({
    name,left,right,status:status??(left==null||right==null?'unknown':left===right?'same':'different')
  });
  const row=(name,fn)=>make(name,fn(a),fn(b));
  const rows=[
    row('Seed',d=>d.seed??null),
    row('Профиль',d=>d.profile??null),
    row('Модели',d=>d.models??null),
    row('Запрошенная длительность',d=>seconds(d.requested_seconds)),
    row('Длительность видео',d=>seconds(d.video?.seconds)),
    row('Кадров',d=>Number.isInteger(d.video?.frames)&&d.video.frames>0?String(d.video.frames):null),
    row('FPS',d=>Number.isFinite(d.video?.fps)&&d.video.fps>0?String(d.video.fps):null),
  ];
  const refs=[a.references,b.references];
  const count=Math.max(...refs.map(r=>r?.length??0));
  if(!count)rows.push(row('Референсы',d=>d.references?'Без референсов':null));
  for(let i=0;i<count;i++){
    const [left,right]=refs.map(r=>r?.[i]);
    const status=refs.some(r=>!r)?'unknown':!left||!right?'different':referenceStatus(left,right);
    rows.push(make('Picture '+(i+1),referenceText(left)??(refs[0]?'—':null),referenceText(right)??(refs[1]?'—':null),status));
  }
  return rows;
}

export function takeComparisonMarkup(takes,media,{montage=false}={}){
  return [
    '<section class="take-comparison" aria-label="Синхронный просмотр">',
    '<div class="take-compare-videos">',
    ...takes.map((take,i)=>{
      const label=(montage?['В монтаже','Кандидат']:['A','B'])[i];
      return '<figure><figcaption>'+label+' · '+esc(take?.name??'Заглушка')+'</figcaption>'+
        (take?'<video muted playsinline preload="metadata" aria-label="'+label+'" src="'+esc(media(take.video_asset_id))+'"></video>':
          '<div class="take-compare-placeholder">Клип без дубля</div>')+
        '<span class="take-compare-end" hidden>Последний кадр</span></figure>';
    }),
    '</div>',
    '<div class="take-compare-transport"><button type="button" class="button'+(montage?'':' primary')+'" data-compare-play disabled>▶ Смотреть</button>'+
      '<button type="button" class="button subtle" data-compare-step="-1" aria-label="Предыдущий кадр" disabled>← Кадр</button>'+
      '<button type="button" class="button subtle" data-compare-step="1" aria-label="Следующий кадр" disabled>Кадр →</button>'+
      (montage?'<span class="small-note">Шаг по кадрам монтажа</span>':
        '<label>Шаг по <select data-compare-grid aria-label="Дубль для покадрового шага"><option value="0">A</option><option value="1">B</option></select></label>')+
      '<output data-compare-time>00:00.000</output></div>',
    '<input class="take-compare-scrub" data-compare-scrub aria-label="Общая позиция сравнения" type="range" min="0" max="0" value="0" step="any" disabled>',
    '<p class="small-note take-compare-status" data-compare-status role="status">Загружаем видео…</p>',
    '<table class="take-compare-details"><caption>Параметры дублей</caption><thead><tr><th scope="col">Параметр</th><th scope="col">Дубль A</th><th scope="col">Дубль B</th></tr></thead><tbody></tbody></table>',
    '</section>',
  ].join('');
}

export function bindTakeComparison(dialog,takes,{projectId,fps,window=null,onMetadata=()=>{}}){
  const root=dialog.querySelector('.take-comparison');
  const videos=[...root.querySelectorAll('figure')].map(figure=>figure.querySelector('video'));
  const ended=[...root.querySelectorAll('.take-compare-end')];
  const $=selector=>root.querySelector(selector),play=$('[data-compare-play]'),scrub=$('[data-compare-scrub]');
  const grid=$('[data-compare-grid]'),status=$('[data-compare-status]'),steps=[...root.querySelectorAll('[data-compare-step]')];
  const controller=new AbortController(),{signal}=controller;
  const listen=(el,event,callback)=>el.addEventListener(event,callback,{signal});
  const details=[{},{}],rate=window?.rate??1;
  let ranges=window?.ranges,limits=[0,0],position=0,duration=0,master=0,playing=false,serial=0,raf;

  function stepFps(index){
    if(window)return fps;
    const value=details[index].video?.fps;
    return Number.isFinite(value)&&value>0?value:fps;
  }
  function updateTime(){
    scrub.value=position;
    $('[data-compare-time]').textContent=timecode(position)+' / '+timecode(duration);
    ended.forEach((label,i)=>{
      label.textContent=limits[i]>0?'Последний кадр':'Участок недоступен';
      label.hidden=!videos[i]||!duration||position<limits[i];
    });
    steps[0].disabled=!duration||position<=0;
    steps[1].disabled=!duration||position>=duration;
  }
  function renderDetails(){
    const rows=videos.map((video,i)=>({
      ...details[i],video:{...details[i].video,...(Number.isFinite(video?.duration)?{seconds:video.duration}:{})}
    }));
    $('tbody').innerHTML=comparisonRows(...rows).map(row=>{
      const flag=row.status==='different'?'<small>Различается</small>':'';
      return '<tr class="'+row.status+'"><th scope="row">'+esc(row.name)+flag+'</th>'+
        '<td>'+esc(row.left??'Нет данных')+'</td><td>'+esc(row.right??'Нет данных')+'</td></tr>';
    }).join('');
    if(!window)grid.querySelectorAll('option').forEach((option,i)=>{
      option.textContent=['A','B'][i]+' · '+stepFps(i).toLocaleString('ru-RU',{maximumFractionDigits:3})+' fps';
    });
  }
  function sourceEnd(index){
    return Math.min(videos[index].duration,ranges?.[index]?.end??Infinity);
  }
  function sourceStart(index){
    return ranges?.[index]?.start??0;
  }
  function comparisonStatus(){
    return window?'Монтажный участок · '+rate+'× · сравнение без звука.':'Общая шкала по времени · сравнение без звука.';
  }
  function configure(){
    const loaded=videos.map(video=>!!video&&!video.error&&Number.isFinite(video.duration)&&video.duration>0);
    const failed=videos.some(video=>video?.error);
    limits=loaded.map((ready,index)=>ready?Math.max(0,(sourceEnd(index)-sourceStart(index))/rate):0);
    master=limits[0]>=limits[1]?0:1;
    const usable=window?limits[master]>0:loaded.every(Boolean);
    duration=usable?(window?.duration??limits[master]):0;
    scrub.max=duration;
    play.disabled=!usable;scrub.disabled=!usable;
    status.textContent=failed?'Один из дублей не открылся. Проверьте доступность видео.':
      usable?comparisonStatus():loaded.some(Boolean)&&window?'В выбранном участке нет кадров.':'Загружаем видео…';
    seek(Math.min(position,duration));
  }
  function metadata(){
    configure();
    renderDetails();
    onMetadata(videos[1]);
  }
  function pause(){
    serial++;playing=false;
    if(raf!=null)cancelAnimationFrame(raf);
    raf=null;
    videos.forEach(video=>video?.pause());
    play.textContent='▶ Смотреть';
  }
  function align(index,time){
    const video=videos[index];
    if(!video||video.error||!Number.isFinite(video.duration)||video.duration<=0)return;
    const end=sourceEnd(index);
    const sample=Math.min(sourceStart(index)+time*rate,end);
    const target=window?Math.min(sample,Math.max(0,end-1e-6)):videoTime(sample,end);
    if(Math.abs(video.currentTime-target)>.0005)video.currentTime=target;
  }
  function seek(time){
    pause();
    position=Math.max(0,Math.min(duration,time));
    videos.forEach((_,index)=>align(index,position));
    updateTime();
  }
  function livePosition(){
    return Math.max(0,(videos[master].currentTime-sourceStart(master))/rate);
  }
  function tick(){
    if(!dialog.open||!root.isConnected){dispose();return;}
    if(!playing)return;
    position=livePosition();
    const limit=Math.min(duration,limits[master]);
    if(videos[master].ended||position>=limit){
      const withinLastFrame=window&&duration-limit<1/fps;
      seek(withinLastFrame?duration:limit);
      return;
    }
    for(const [i,video] of videos.entries()){
      if(!video||i===master||video.error||!limits[i])continue;
      if(position>=limits[i]){
        video.pause();
        if(!video.seeking)align(i,position);
      }else if(!video.seeking&&Math.abs((video.currentTime-sourceStart(i))/rate-position)>1/stepFps(i)){
        align(i,position);
      }
    }
    updateTime();
    raf=requestAnimationFrame(tick);
  }
  async function toggle(){
    if(!duration)return;
    if(playing){seek(livePosition());return;}
    if(position>=Math.min(duration,limits[master]))seek(0);
    const ticket=++serial;
    status.textContent=comparisonStatus();
    playing=true;play.textContent='Ⅱ Пауза';
    try{
      const active=videos.flatMap((video,index)=>video&&position<limits[index]?[index]:[]);
      for(const index of active){
        videos[index].playbackRate=rate;
        align(index,position);
      }
      await Promise.all(active.map(index=>videos[index].play()));
      if(ticket!==serial||signal.aborted)return;
      tick();
    }catch{
      if(ticket!==serial||signal.aborted)return;
      pause();status.textContent='Воспроизведение прервалось. Попробуйте ещё раз.';
    }
  }
  function step(direction){if(duration)seek(stepTime(position,stepFps(Number(grid?.value??0)),direction,duration));}
  function dispose(){
    if(signal.aborted)return;
    controller.abort();pause();
    videos.forEach(video=>{if(video){video.removeAttribute('src');video.load();}});
  }

  listen(dialog,'close',()=>{if(!dialog.open)dispose();});
  listen(root,'keydown',event=>{
    if(event.altKey||event.ctrlKey||event.metaKey||event.target===grid)return;
    if(event.key==='ArrowLeft'||event.key==='ArrowRight'){
      event.preventDefault();step(event.key==='ArrowLeft'?-1:1);
    }else if(event.code==='Space'&&!['BUTTON','INPUT','SELECT'].includes(event.target.tagName)){
      event.preventDefault();toggle();
    }
  });
  listen(play,'click',toggle);
  listen(scrub,'input',()=>seek(Number(scrub.value)));
  steps.forEach(button=>listen(button,'click',()=>step(Number(button.dataset.compareStep))));
  videos.forEach((video,i)=>{
    if(!video)return;
    listen(video,'loadedmetadata',metadata);listen(video,'error',metadata);
    const url='/api/projects/'+encodeURIComponent(projectId)+'/takes/'+encodeURIComponent(takes[i].id)+'/comparison';
    fetch(url,{signal}).then(response=>response.ok?response.json():null).then(data=>{
      if(signal.aborted||!root.isConnected||!data)return;
      details[i]=data;renderDetails();
    }).catch(()=>{});
  });
  metadata();
  return {dispose,setRanges(next){ranges=next;position=0;configure();}};
}
