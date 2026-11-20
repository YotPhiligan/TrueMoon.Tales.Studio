// Existing montage commands, shared by mouse and keyboard context menus.
export function timelineCommands(view,kind,item,{frame,revision}){
  const command=(id,label,run,options={})=>({id,label,run,...options,disabled:!!view.pending||!!options.disabled});
  const write=(action,payload)=>()=>view.write(action,payload,{expectedRevision:revision});
  const jump=()=>view.onSeek?.(item.start_frame);
  if(kind==='clip')return [
    command('source','Участок и скорость',()=>view.editSource(item),{disabled:!item.take_id||!view.editSource}),
    command('description','Описание и время',()=>view.editStoryClip(item),{disabled:!view.editStoryClip}),
    command('materials','Материалы и промпт',()=>view.openBlock(item.block_id,item.segment_id),{disabled:!view.openBlock}),
    command('jump','К началу блока',jump,{disabled:!view.onSeek}),
    command('split','Разрезать по курсору',write('split_clip',{id:item.id,frame}),{group:true,disabled:!Number.isFinite(frame)||frame<=item.start_frame||frame>=item.start_frame+item.duration_frames}),
    command('anchor','Закрепить начало',write('set_clip_anchor',{id:item.id,enabled:item.anchor_frame==null}),{checked:item.anchor_frame!=null}),
    command('gap','Защитить паузу',write('set_gap_protected',{id:item.id,enabled:!item.gap_protected}),{checked:!!item.gap_protected}),
    command('remove','Убрать блок из монтажа',write('remove_clip',{id:item.id}),{group:true,danger:true})
  ];
  if(kind==='segment')return [
    command('open','Открыть сегмент',()=>view.openSegment(item.id),{disabled:!view.openSegment}),
    command('description','Описание и время',()=>view.editSegment(item.id),{disabled:!view.editSegment}),
    command('preview','Просмотреть сегмент',()=>view.previewSegment(item.id),{disabled:!view.previewSegment||(view.contextIssues?.()??view.planIssues?.()??[]).some(i=>i.segment_id===item.id)}),
    command('jump','К началу сегмента',jump,{disabled:!view.onSeek}),
    command('block','Добавить блок',()=>view.addStoryBlock(item.id),{group:true,disabled:!view.addStoryBlock}),
    command('sound','Добавить звук',()=>view.addSegmentSound(item.id),{disabled:!view.addSegmentSound}),
    command('compact','Убрать промежутки',write('compact_segment',{segment_id:item.id})),
    command('remove','Убрать сегмент из сценария',write('remove_segment',{id:item.id}),{group:true,danger:true})
  ];
  if(kind==='join')return [
    command('loop','Зациклить стык',()=>view.previewJoin(item.id),
      {disabled:!view.previewJoin||!!(view.contextIssues?.()??view.planIssues?.()??[]).length}),
    command('transition','Переход через чёрный',()=>view.editTransition(item),{disabled:!view.editTransition}),
    command('remove','Убрать переход',write('remove_transition',{right_clip_id:item.right.id}),{disabled:!item.transition}),
    command('jump','К началу следующего блока',jump,{group:true,disabled:!view.onSeek})
  ];
  return [];
}

export class TimelineMenu{
  constructor(view){this.view=view;}
  open(event,element){
    const {kind,id}=element.dataset,view=this.view,item=view.lookup(kind,id);
    if(!['clip','segment','join'].includes(kind)||!item)return;
    event.preventDefault();event.stopPropagation();this.close(false);view.onEditStart?.();
    if(kind!=='join'){view.choose(kind,id);view.render();}
    const owner=view.root.querySelector(`[data-kind="${kind}"][data-id="${id}"]`)??element;
    const p=view.project(),context={pid:p.id,revision:p.revision,frame:view.cursor,kind,id};
    const menu=document.createElement('div');menu.id='tlContextMenu';menu.className='tl-context-menu';menu.setAttribute('role','menu');menu.tabIndex=-1;
    const title=document.createElement('div');title.className='tl-context-title';title.textContent=(kind==='clip'?'Блок':kind==='join'?'Стык':'Сегмент')+' · '+(item.story_title??item.name??view.project().blocks?.find(b=>b.id===item.block_id)?.label??'');
    menu.setAttribute('aria-label',kind==='clip'?'Действия блока на таймлайне':kind==='join'?'Действия стыка на таймлайне':'Действия сегмента на таймлайне');menu.append(title);
    const session={...context,menu,owner,buttons:[]};this.session=session;
    for(const action of timelineCommands(view,kind,item,context)){
      if(action.group){const separator=document.createElement('div');separator.setAttribute('role','separator');menu.append(separator);}
      const button=document.createElement('button');button.type='button';button.className='tl-context-command'+(action.danger?' danger':'');button.dataset.command=action.id;
      button.setAttribute('role','checked' in action?'menuitemcheckbox':'menuitem');button.disabled=action.disabled;button.tabIndex=-1;
      if('checked' in action)button.setAttribute('aria-checked',String(action.checked));
      const check=document.createElement('span');check.className='tl-context-check';check.setAttribute('aria-hidden','true');check.textContent=action.checked?'✓':'';
      const label=document.createElement('span');label.textContent=action.label;button.append(check,label);menu.append(button);session.buttons.push(button);
      button.onclick=async()=>{
        if(this.session!==session||button.disabled)return;
        const fresh=view.project();this.close(true);
        if(fresh?.id!==context.pid||fresh.revision!==context.revision||!view.lookup(kind,id)){view.onError?.(new Error('Монтаж изменился. Откройте меню заново.'));return;}
        if(view.pending){view.onError?.(new Error('Дождитесь сохранения монтажа.'));return;}
        try{await action.run();}catch(error){view.onError?.(error);}
      };
    }
    document.body.append(menu);owner.setAttribute('aria-expanded','true');owner.setAttribute('aria-controls',menu.id);
    const rect=owner.getBoundingClientRect(),box=menu.getBoundingClientRect(),keyboard=event.type==='keydown'||!event.clientX&&!event.clientY;
    menu.style.left=Math.max(8,Math.min(keyboard?rect.left:event.clientX,window.innerWidth-box.width-8))+'px';
    menu.style.top=Math.max(8,Math.min(keyboard?rect.bottom:event.clientY,window.innerHeight-box.height-8))+'px';
    const available=()=>session.buttons.filter(b=>!b.disabled);
    menu.onkeydown=e=>{
      e.stopPropagation();
      if(e.key==='Escape'){e.preventDefault();e.stopPropagation();this.close(true);return;}
      if(e.key==='Tab'){this.close(true);return;}
      const rows=available();if(!rows.length)return;
      if(['ArrowDown','ArrowUp','Home','End'].includes(e.key)){
        e.preventDefault();e.stopPropagation();const at=rows.indexOf(document.activeElement);
        const index=e.key==='Home'?0:e.key==='End'?rows.length-1:e.key==='ArrowDown'?(at+1)%rows.length:(at<=0?rows.length:at)-1;rows[index].focus();
      }
    };
    this.outside=e=>{if(!menu.contains(e.target))this.close(false);};
    this.resize=()=>this.close(false);this.scroll=e=>{if(!menu.contains(e.target))this.close(false);};
    document.addEventListener('pointerdown',this.outside,true);document.addEventListener('contextmenu',this.outside,true);
    document.addEventListener('scroll',this.scroll,true);window.addEventListener('resize',this.resize);
    menu.oncontextmenu=e=>e.preventDefault();(available()[0]??menu).focus();
  }
  close(restore=false){
    const current=this.session;if(!current)return;this.session=null;current.menu.remove();
    document.removeEventListener('pointerdown',this.outside,true);document.removeEventListener('contextmenu',this.outside,true);
    document.removeEventListener('scroll',this.scroll,true);window.removeEventListener('resize',this.resize);
    current.owner.setAttribute('aria-expanded','false');current.owner.removeAttribute('aria-controls');
    if(restore&&current.owner.isConnected)current.owner.focus({preventScroll:true});
  }
}
