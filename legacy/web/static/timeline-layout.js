// UI preferences only. Never part of the montage or undo history.
export const layoutContext=value=>value==='story'?'story':'editing';
export const densityKey=context=>'studio-timeline-density-'+layoutContext(context);
export function resolveDensity(context,value){return ['compact','full'].includes(value)?value:layoutContext(context)==='story'?'compact':'full';}
export function loadDensity(context,storage){
  try{return resolveDensity(context,(storage??globalThis.localStorage)?.getItem(densityKey(context)));}
  catch{return resolveDensity(context,null);}
}
export function saveDensity(context,value,storage){
  if(!['compact','full'].includes(value))throw new Error('Выберите компактный или полный таймлайн.');
  try{(storage??globalThis.localStorage)?.setItem(densityKey(context),value);}catch{}
}

const workspaceLayoutKey='studio-workspace-layout';
const workspaceModes=['editing','balance','preview','custom'];

function loadWorkspaceLayout(){
  try{
    const saved=JSON.parse(globalThis.localStorage.getItem(workspaceLayoutKey));
    if(workspaceModes.includes(saved?.mode)&&Number.isFinite(saved.height))return saved;
  }catch{}
  return {mode:'balance',height:40};
}

export function bindWorkspaceLayout(workspace,onExpand){
  const grip=workspace.querySelector('#workspaceLayoutGrip');
  const timeline=workspace.querySelector('#timelineRoot');
  const presets=workspace.querySelectorAll('[data-layout-preset]');
  const show=workspace.querySelector('#workspaceShowTimeline');
  let layout=loadWorkspaceLayout(),drag;
  const percent=()=>100*timeline.offsetHeight/Math.max(workspace.clientHeight,1);
  const minimum=()=>Math.min(42,19000/Math.max(workspace.clientHeight,1));
  const setHeight=height=>({mode:'custom',height:Math.round(Math.max(minimum(),Math.min(60,height))*10)/10});

  function syncGrip(){
    const min=Math.round(minimum());
    grip.setAttribute('aria-valuemin',min);
    grip.setAttribute('aria-valuenow',Math.max(min,Math.min(60,Math.round(percent()))));
  }

  function apply(next){
    layout=next;
    workspace.dataset.workspaceLayout=next.mode;
    workspace.style.setProperty('--workspace-timeline-height',next.height+'%');
    for(const button of presets)button.setAttribute('aria-pressed',String(button.dataset.layoutPreset===next.mode));
    show.hidden=next.mode!=='preview';
    syncGrip();
  }

  function save(){
    try{globalThis.localStorage.setItem(workspaceLayoutKey,JSON.stringify(layout));}catch{}
  }

  function choose(mode){
    apply({...layout,mode});
    if(mode!=='preview')onExpand();
    save();
  }

  for(const button of presets)button.onclick=()=>choose(button.dataset.layoutPreset);
  show.onclick=()=>{
    choose('balance');
    workspace.querySelector('[data-layout-preset="balance"]').focus({preventScroll:true});
  };
  grip.onpointerdown=event=>{
    if(event.button!==0||drag)return;
    drag={id:event.pointerId,y:event.clientY,height:percent(),layout};
    grip.setPointerCapture(event.pointerId);
  };
  grip.onpointermove=event=>{
    if(drag?.id!==event.pointerId||event.clientY===drag.y&&layout===drag.layout)return;
    apply(setHeight(drag.height+100*(drag.y-event.clientY)/workspace.clientHeight));
  };
  grip.onpointerup=event=>{
    if(drag?.id!==event.pointerId)return;
    if(layout!==drag.layout)save();
    drag=null;
  };
  grip.onpointercancel=grip.onlostpointercapture=event=>{
    if(drag?.id!==event.pointerId)return;
    apply(drag.layout);
    drag=null;
  };
  grip.onkeydown=event=>{
    const targets={ArrowUp:percent()+4,ArrowDown:percent()-4,Home:minimum(),End:60};
    if(!Object.hasOwn(targets,event.key))return;
    event.preventDefault();
    apply(setHeight(targets[event.key]));
    save();
  };

  apply(layout);
  const observer=new ResizeObserver(syncGrip);
  observer.observe(timeline);
  observer.observe(workspace);
}
