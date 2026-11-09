import {escapeHtml as esc} from './logic.js';
import {frameCode} from './timeline-logic.js';

const kinds={video:'Видео',image:'Изображения',audio:'Аудио'};
const roles={reference:'Референс',guide:'Опорный кадр',take:'Дубль',take_frame:'Кадр из дубля',
  montage_video:'Видео в монтаже',animatic_frame:'Кадр аниматика',block_sound:'Звук блока',segment_sound:'Звук сегмента',music:'Музыка проекта'};
const pageSize=80;
export const assetSelection=asset=>asset.asset_id?{asset_id:asset.asset_id}:{file:asset.path};
export const assetQuery=asset=>new URLSearchParams(asset.asset_id?{asset:asset.asset_id}:{file:asset.path});
export function setAssetOption(select,asset){
  const value=asset.asset_id??'file:'+asset.path;
  let option=[...select.options].find(item=>item.value===value);
  if(!option){option=new Option(asset.name,value);select.add(option);}
  if(!asset.asset_id)option.dataset.catalogFile=asset.path;
  select.value=value;
}

function filtered(assets,query,kind,status){
  const terms=query.trim().toLocaleLowerCase().split(/\s+/).filter(Boolean);
  return assets.filter(asset=>{
    if(kind&&asset.kind!==kind)return false;
    if(status==='used'&&!asset.uses.length||status==='unused'&&asset.uses.length||
       status==='external'&&asset.location!=='external'||status==='discovered'&&asset.asset_id)return false;
    const haystack=(asset.name+' '+asset.path).toLocaleLowerCase();
    return terms.every(term=>haystack.includes(term));
  });
}

export class AssetLibrary{
  constructor(options){Object.assign(this,options);this.limit=pageSize;}
  pause(){this.root.querySelectorAll('video,audio').forEach(element=>element.pause());}
  url(project){return '/api/projects/'+encodeURIComponent(project.id)+'/asset-library';}
  preview(asset,project){
    return asset.asset_id
      ? '/api/media?'+new URLSearchParams({project:project.id,asset:asset.asset_id})
      : this.url(project)+'/preview?'+new URLSearchParams({file:asset.path});
  }
  open(project){
    const key=project.id+':'+project.revision;
    if(!this.root.querySelector('[data-asset-list]')){
      this.root.innerHTML='<div class="asset-library"><header class="asset-library-heading"><div><span class="eyebrow">МАТЕРИАЛЫ ПРОЕКТА</span><h1>Библиотека ассетов</h1></div><button type="button" class="button subtle" data-asset-refresh>Обновить каталог</button></header>'+
        '<div class="asset-library-filters">'+'<label>Поиск <input data-asset-search type="search" placeholder="Имя или путь"></label>'+
        '<label>Тип <select data-asset-kind><option value="">Все типы</option>'+
        Object.entries(kinds).filter(([key])=>!this.allowedKinds||this.allowedKinds.includes(key)).map(([key,name])=>'<option value="'+key+'">'+name+'</option>').join('')+
        '</select></label><label>Использование <select data-asset-status><option value="">Все материалы</option><option value="used">Есть связи</option><option value="unused">Без связей</option><option value="external">Внешние</option><option value="discovered">Новые на диске</option></select></label><output data-asset-count aria-live="polite"></output></div>'+
        '<div class="asset-library-content"><div data-asset-list class="asset-library-list" aria-label="Файлы проекта"></div><section data-asset-detail class="asset-library-detail" aria-label="Предпросмотр и связи"></section></div></div>';
      for(const control of this.root.querySelectorAll('select'))control.onchange=()=>this.filter();
      const search=this.root.querySelector('[data-asset-search]');
      if(search){
        search.oninput=()=>this.filter();
        search.onkeydown=event=>{if(event.key==='Enter')event.preventDefault();};
      }
    }
    this.root.querySelector('[data-asset-refresh]').onclick=()=>this.load(project);
    if(this.dataKey===key){this.renderRows();return;}
    if(this.loadingKey!==key)this.load(project);
  }
  async load(project){
    const key=project.id+':'+project.revision,serial=(this.serial??0)+1;
    this.serial=serial;this.loadingKey=key;this.dataKey=null;this.pause();
    if(this.projectId!==project.id){
      this.root.querySelector('[data-asset-search]').value='';
      this.selected=null;
      this.projectId=project.id;
    }
    const list=this.root.querySelector('[data-asset-list]'),detail=this.root.querySelector('[data-asset-detail]');
    list.textContent='Загружаем материалы проекта…';detail.replaceChildren();this.root.querySelector('[data-asset-count]').textContent='';
    try{
      const data=await this.api(this.url(project));
      if(serial!==this.serial||!this.active()||this.getProject().id!==project.id||this.getProject().revision!==project.revision)return;
      if(data.project_revision!==project.revision){list.textContent='Проект обновился. Каталог обновится вместе с проектом.';return;}
      this.dataKey=key;this.assets=data.assets;this.limit=pageSize;this.renderRows();
    }catch(error){
      if(serial===this.serial&&this.active()&&this.getProject().id===project.id)list.textContent=error.message;
    }finally{
      if(serial===this.serial)this.loadingKey=null;
    }
  }
  filter(){
    this.limit=pageSize;this.renderRows();
  }
  renderRows(){
    const project=this.getProject(),list=this.root.querySelector('[data-asset-list]');
    if(!list||this.dataKey!==project.id+':'+project.revision)return;
    const inventory=this.allowedKinds?this.assets.filter(asset=>asset.available&&this.allowedKinds.includes(asset.kind)):this.assets;
    const query=this.root.querySelector('[data-asset-search]').value;
    const assets=filtered(inventory,query,this.root.querySelector('[data-asset-kind]').value,this.root.querySelector('[data-asset-status]').value);
    if(!assets.some(a=>a.id===this.selected))this.selected=assets[0]?.id??null;
    const rows=assets.slice(0,this.limit);
    list.replaceChildren();
    for(const asset of rows){
      const button=document.createElement('button');
      button.className='asset-library-row';button.type='button';button.dataset.assetRow=asset.id;
      button.setAttribute('aria-pressed',String(asset.id===this.selected));
      const name=document.createElement('strong');name.textContent=asset.name;
      const meta=document.createElement('small');meta.textContent=kinds[asset.kind]+' · '+asset.path;
      const uses=document.createElement('span');uses.className='badge';uses.textContent=asset.uses.length?String(asset.uses.length):'—';
      button.append(name,meta,uses);button.onclick=()=>this.select(asset.id);list.append(button);
    }
    if(!rows.length)list.textContent='Подходящих материалов нет.';
    if(rows.length<assets.length){
      const more=document.createElement('button');more.className='button subtle';more.type='button';more.textContent='Показать ещё';
      more.onclick=()=>{this.limit+=pageSize;this.renderRows();list.querySelectorAll('[data-asset-row]')[rows.length]?.focus();};list.append(more);
    }
    this.root.querySelector('[data-asset-count]').textContent=assets.length+' из '+inventory.length;
    this.renderDetail(assets.find(a=>a.id===this.selected));
  }
  select(id){
    this.selected=id;
    for(const row of this.root.querySelectorAll('[data-asset-row]'))row.setAttribute('aria-pressed',String(row.dataset.assetRow===id));
    this.renderDetail(this.assets.find(a=>a.id===id));
  }
  renderDetail(asset){
    const detail=this.root.querySelector('[data-asset-detail]');
    const key=this.dataKey+'|'+(asset?.id??'');
    if(detail.dataset.key===key&&detail.childNodes.length)return;
    this.pause();detail.replaceChildren();detail.dataset.key=key;
    if(!asset)return;
    const p=this.getProject(),label=asset.available?(asset.asset_id?(asset.location==='external'?'Внешний файл':'В проекте'):'Найден на диске'):'Файл недоступен';
    const preview=asset.available?this.preview(asset,p):'';
    const player=preview?(asset.kind==='image'?'<img src="'+esc(preview)+'" alt="'+esc(asset.name)+'">':
      asset.kind==='video'?'<video controls playsinline preload="metadata" src="'+esc(preview)+'"></video>':
        '<audio controls preload="metadata" src="'+esc(preview)+'"></audio>'):'';
    detail.innerHTML='<h2>'+esc(asset.name)+'</h2><p class="muted">'+esc(asset.path)+' · '+esc(label)+'</p>'+
      (player?'<div class="asset-library-player">'+player+'</div>':'')+
      (this.actions?'<div data-asset-actions class="asset-library-actions"></div><p data-asset-error role="status"></p>':'')+
      '<h3>Где используется</h3><div data-asset-uses class="asset-library-uses"></div>';
    if(this.actions){
      const actions=detail.querySelector('[data-asset-actions]');
      for(const {label,run} of this.actions){
        const button=document.createElement('button');button.type='button';button.className='button primary';button.textContent=label;
        button.onclick=async()=>{
          if(this.picking)return;
          this.picking=true;for(const action of actions.children)action.disabled=true;
          const error=detail.querySelector('[data-asset-error]');error.textContent='';
          try{await run(asset);}
          catch(problem){error.textContent=problem.message;}
          finally{this.picking=false;for(const action of actions.children)action.disabled=false;}
        };
        actions.append(button);
      }
    }
    const list=detail.querySelector('[data-asset-uses]');
    if(!asset.uses.length){list.textContent='Связей в текущем проекте нет.';return;}
    for(const use of asset.uses){
      const button=document.createElement(this.openUsage?'button':'div');
      button.className='asset-library-usage';if(this.openUsage)button.type='button';
      const title=document.createElement('strong');title.textContent=(roles[use.role]??use.role)+' · '+use.label;
      const meta=document.createElement('small');
      meta.textContent=[use.detail,use.frames?.length?'Guide '+use.frames.join(', '):'',use.start_frame==null?'':frameCode(use.start_frame,p.fps)].filter(Boolean).join(' · ');
      button.append(title,meta);
      if(this.openUsage)button.onclick=()=>this.openUsage(use).catch(this.onError);
      list.append(button);
    }
  }
}
