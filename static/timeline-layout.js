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
