import {escapeHtml as esc} from './logic.js';
import {segmentTypeIcon,segmentTypeLabel} from './segment-types.js';

const labels={segment:'Сегмент',block:'Блок'};
const terms=value=>value.toLocaleLowerCase().trim().split(/\s+/).filter(Boolean);

export class ProjectNavigation{
  constructor(root,actions){
    this.root=root;
    this.actions=actions;
    this.toggles={};
    this.panels={};
    for(const kind of Object.keys(labels)){
      const toggle=root.querySelector('[data-nav-toggle="'+kind+'"]');
      const panel=root.querySelector('#'+toggle.getAttribute('aria-controls'));
      this.toggles[kind]=toggle;
      this.panels[kind]=panel;
      toggle.onclick=()=>this.kind===kind?this.close(true):this.open(kind);
      panel.querySelector('input').oninput=()=>this.renderRows();
    }
    root.querySelector('#navScenario').onclick=()=>this.run(actions.scenario);
    root.querySelector('#navAddSegment').onclick=()=>this.run(actions.addSegment);
    root.querySelector('#navSourceOverview').onclick=()=>this.run(actions.sourceOverview);
    root.querySelector('#navBlockScope').onclick=()=>{
      this.allSources=!this.allSources;
      this.renderRows();
      this.panels.block.querySelector('input').focus();
    };
    root.addEventListener('keydown',event=>this.key(event));
    for(const type of ['pointerdown','focusin']){
      document.addEventListener(type,event=>{if(!root.contains(event.target))this.close();});
    }
  }

  update(data){
    const previous=this.data;
    this.data=data;
    if(previous?.project.id!==data.project.id)this.close();
    for(const kind of Object.keys(labels)){
      const button=this.toggles[kind],name=kind==='segment'?data.segment?.name:data.item?.title??data.block?.label;
      button.disabled=false;
      button.querySelector('span').textContent=name??labels[kind];
      button.title=name?labels[kind]+' · '+name:labels[kind];
      const selectLabel=kind==='segment'?'Выбрать сегмент':'Выбрать блок';
      button.setAttribute('aria-label',name?selectLabel+' · '+name:selectLabel);
    }
    const scenario=this.root.querySelector('#navScenario');
    scenario.disabled=false;
    if(data.overview)scenario.setAttribute('aria-current','page');
    else scenario.removeAttribute('aria-current');
    if(this.kind&&(data.project!==previous?.project||data.segment?.id!==previous.segment?.id||
      data.item?.id!==previous.item?.id||data.block?.id!==previous.block?.id))this.renderRows();
  }

  open(kind){
    if(!this.data)return;
    this.close();
    this.kind=kind;
    this.allSources=!this.data.segment;
    const panel=this.panels[kind],search=panel.querySelector('input');
    search.value='';
    panel.hidden=false;
    this.toggles[kind].setAttribute('aria-expanded','true');
    this.renderRows();
    search.focus();
  }

  close(restoreFocus=false){
    if(!this.kind)return;
    const kind=this.kind;
    this.kind=null;
    this.panels[kind].hidden=true;
    this.toggles[kind].setAttribute('aria-expanded','false');
    if(restoreFocus)this.toggles[kind].focus({preventScroll:true});
  }

  run(action){
    this.close(true);
    action();
  }

  key(event){
    if(!this.kind||event.isComposing)return;
    const panel=this.panels[this.kind],search=panel.querySelector('input');
    if(event.key==='Escape'){
      event.preventDefault();
      this.close(true);
      return;
    }
    if(!panel.contains(event.target))return;
    const rows=[...panel.querySelectorAll('[data-nav-result]')],index=rows.indexOf(event.target);
    if(event.key==='Enter'&&event.target===search){
      event.preventDefault();
      rows[0]?.click();
    }else if(['ArrowDown','ArrowUp'].includes(event.key)&&(event.target===search||index>=0)){
      event.preventDefault();
      if(event.key==='ArrowDown')rows[Math.min(index+1,rows.length-1)]?.focus();
      else if(index>0)rows[index-1].focus();
      else search.focus();
    }
  }

  renderRows(){
    const {project,view,segment,item,block}=this.data;
    const kind=this.kind,panel=this.panels[kind],list=panel.querySelector('.project-nav-results');
    const query=terms(panel.querySelector('input').value);
    const focusId=list.contains(document.activeElement)?document.activeElement.dataset.navResult:null;
    const scroll=list.scrollTop;
    const scoped=kind==='block'&&segment&&!this.allSources;
    const blocks=scoped?new Map(project.blocks.map(row=>[row.id,row])):null;
    const issues=new Set(view.issues.map(issue=>issue.segment_id));
    const rows=kind==='segment'?view.segments.map(row=>({
      id:row.id,name:row.name,icon:segmentTypeIcon(row.segment_type),
      detail:segmentTypeLabel(row.segment_type)+' · '+row.items.length+' блок.',
      search:row.description,current:row.id===segment?.id,warning:issues.has(row.id),
      action:()=>this.actions.segment(row.id)
    })):scoped?segment.items.map(row=>{
      const source=blocks.get(row.block_id);
      if(!source)return null;
      return {id:row.id,name:row.title,detail:source.label,search:row.description+' '+source.title,
        current:row.id===item?.id,action:()=>this.actions.block(source.id,segment.id,row.id)};
    }).filter(Boolean):project.blocks.map(row=>({
      id:row.id,name:row.label,detail:row.title,search:row.id,current:row.id===block?.id,sourceId:row.id,
      action:()=>this.actions.block(row.id)
    }));
    if(kind==='block'){
      const scope=panel.querySelector('#navBlockScope');
      scope.hidden=!segment;
      scope.textContent=scoped?'Все исходники':'Блоки сегмента';
    }
    const matches=rows.filter(row=>{
      const haystack=[row.name,row.detail,row.search].join(' ').toLocaleLowerCase();
      return query.every(term=>haystack.includes(term));
    });
    list.innerHTML=matches.map(row=>'<button type="button" class="project-nav-result" data-nav-result="'+esc(row.id)+'"'+
      (row.current?' aria-current="true"':'')+(row.sourceId?' data-nav-source title="Открыть исходник или перетащить на таймлайн"':'')+
      '><strong>'+(row.icon??'')+esc(row.name)+'</strong><small>'+esc(row.detail??'')+
      (row.warning?' · Проверить время / материалы':'')+'</small></button>').join('')||
      '<p class="small-note">'+(query.length?'Ничего не найдено.':kind==='segment'?'Сегментов пока нет.':'Блоков пока нет.')+'</p>';
    list.querySelectorAll('[data-nav-result]').forEach((button,index)=>{
      const row=matches[index];
      button.onclick=()=>{if(!row.sourceId||!this.actions.dragged())this.run(row.action);};
      if(row.sourceId)button.onpointerdown=event=>this.actions.drag(event,row.sourceId);
    });
    list.scrollTop=scroll;
    if(focusId){
      const target=[...list.querySelectorAll('[data-nav-result]')].find(button=>button.dataset.navResult===focusId);
      (target??panel.querySelector('input')).focus({preventScroll:true});
    }
  }
}
