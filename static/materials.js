import {escapeHtml as esc,timing,removePicture} from './logic.js';

const frames=b=>timing(b.settings.seconds,b.settings.mode)?.frames??0;
export function nextGuideFrame(block){
  const n=frames(block),used=new Set(block.guides.map(g=>g.frame===-1?n-1:g.frame));
  if(!n||block.guides.length>=4)return null;
  for(const f of [0,-1,Math.floor(n/2),1,...Array.from({length:n},(_,i)=>i)])if(!used.has(f===-1?n-1:f))return f;
  return null;
}
export function guidePatch(block,referenceId,frame,index=null){
  const n=frames(block);
  if(!block.references.some(r=>r.id===referenceId))throw new Error('Выберите изображение из референсов блока.');
  if(!Number.isInteger(frame)||frame< -1||frame>=n||!n)throw new Error('Индекс опоры выходит за длину источника.');
  if(index!==null&&(!Number.isInteger(index)||index<0||index>=block.guides.length))throw new Error('Опора не найдена.');
  const guides=block.guides.map(g=>({...g})),g={reference_id:referenceId,frame};
  if(index===null){if(guides.length>=4)throw new Error('Допустимо до четырёх опор.');guides.push(g);}else guides[index]=g;
  const used=guides.map(g=>g.frame===-1?n-1:g.frame);
  if(new Set(used).size!==used.length)throw new Error('На этот кадр уже назначена опора.');
  return {guides};
}
export function removeReferencePatch(block,id){
  const index=block.references.findIndex(r=>r.id===id);if(index<0)throw new Error('Изображение не найдено.');
  return {references:block.references.filter(r=>r.id!==id),guides:block.guides.filter(g=>g.reference_id!==id),prompt:removePicture(block.prompt,index+1)};
}
export function referenceOrder(block,id,direction){
  const order=block.references.map(r=>r.id),i=order.indexOf(id),j=i+direction;
  if(![-1,1].includes(direction)||i<0||j<0||j>=order.length)throw new Error('Перестановка недоступна.');
  [order[i],order[j]]=[order[j],order[i]];return order;
}
export function guideName(frame,count){return frame===0?'Первый кадр':frame===-1||frame===count-1?'Последний кадр':'Кадр '+frame;}

export class MaterialsDesk{
  constructor(options){Object.assign(this,options);}
  render(tab){
    const b=this.getBlock(),body=this.root;body.ondragover=null;body.ondragleave=null;body.ondrop=null;
    if(tab==='guides')this.guides(b,body);else this.references(b,body);
    body.querySelectorAll('[data-lightbox]').forEach(button=>button.onclick=()=>{const a=this.getProject().assets[button.dataset.lightbox];this.modal(a.name,`<img class="lightbox" src="${this.media(button.dataset.lightbox)}" alt="Изображение блока">`,true);});
  }
  references(b,body){
    body.innerHTML=`<div class="section-label">Референсы<span>${b.references.length} / 8</span></div><p class="prompt-description">Входные изображения и их роли. Порядок задаёт Picture; назначение опор доступно в отдельной вкладке.</p>${b.references.map((r,i)=>{const a=this.getProject().assets[r.asset_id],uses=b.guides.filter(g=>g.reference_id===r.id);return `<article class="asset-card"><img class="asset-preview" src="${this.media(r.asset_id)}" alt="${esc(r.role)}" data-lightbox="${r.asset_id}"><div class="asset-body"><div class="asset-heading"><strong>Picture ${i+1}</strong><span class="asset-name">${esc(a.name)}</span><div class="asset-actions"><button class="icon-button" data-move="${r.id}" data-direction="-1" ${i===0?'disabled':''} aria-label="Выше">↑</button><button class="icon-button" data-move="${r.id}" data-direction="1" ${i===b.references.length-1?'disabled':''} aria-label="Ниже">↓</button><button class="icon-button" data-remove="${r.id}" title="Удаление также снимает связанные опоры" aria-label="Убрать из блока">×</button></div></div><input data-role="${r.id}" value="${esc(r.role)}" aria-label="Роль Picture ${i+1}">${uses.length?`<small class="material-use">Используется в опорах: ${uses.map(g=>esc(guideName(g.frame,frames(b)))).join(', ')}</small>`:''}</div></article>`;}).join('')}<button class="drop-zone" id="uploadReferences" ${b.references.length>=8?'disabled':''}>Добавить изображения · или перетащить</button>`;
    body.querySelectorAll('[data-role]').forEach(e=>e.oninput=()=>this.edit(b=>b.references.find(r=>r.id===e.dataset.role).role=e.value));
    body.querySelectorAll('[data-remove]').forEach(e=>e.onclick=()=>{this.edit(b=>Object.assign(b,removeReferencePatch(b,e.dataset.remove)));this.refresh();});
    body.querySelectorAll('[data-move]').forEach(e=>e.onclick=()=>this.move(e.dataset.move,Number(e.dataset.direction)).catch(this.onError));
    body.querySelector('#uploadReferences').onclick=()=>this.chooseUpload(false);
    this.drop(body,false);
  }
  guides(b,body){
    body.innerHTML=`<div class="section-label">Опорные кадры<span>${b.guides.length} / 4</span></div><p class="prompt-description">Композиция в заданный момент: первый, последний или промежуточный кадр. Можно использовать уже добавленное изображение или загрузить новое.</p><div class="guide-add-actions"><button id="addGuide" class="button small" ${nextGuideFrame(b)===null||!b.references.length?'disabled':''}>+ Из референсов</button><button id="uploadGuide" class="button subtle small" ${b.guides.length>=4||b.references.length>=8?'disabled':''}>+ Изображение опоры</button></div>${b.guides.map((g,i)=>{const r=b.references.find(r=>r.id===g.reference_id),picture=b.references.indexOf(r)+1;return `<article class="guide-card"><div class="guide-card-heading"><strong>${esc(guideName(g.frame,frames(b)))}</strong><span>Picture ${picture}</span><button class="icon-button" data-delete-guide="${i}" aria-label="Удалить опору">×</button></div>${r?`<img class="guide-preview" src="${this.media(r.asset_id)}" alt="${esc(guideName(g.frame,frames(b)))}" data-lightbox="${r.asset_id}">`:''}<label class="field"><span>Изображение</span><select data-guide-ref="${i}" aria-label="Изображение опоры ${i+1}">${b.references.map((r,n)=>`<option value="${r.id}" ${r.id===g.reference_id?'selected':''}>Picture ${n+1} · ${esc(r.role.slice(0,40))}</option>`).join('')}</select></label><label class="field"><span>Индекс кадра</span><input type="number" min="-1" max="${frames(b)-1}" data-guide-frame="${i}" value="${g.frame}" aria-label="Индекс опоры ${i+1}"></label></article>`;}).join('')||'<p class="small-note">Опоры пока не назначены. Обычная загрузка референса не назначает его первым кадром.</p>'}<p class="small-note">0 — первый; −1 — последний; остальные значения — индекс кадра источника. В Motion Context первые 22 кадра задаёт предыдущий AV-latent. Опору и референс можно связать с одним изображением.</p>`;
    body.querySelector('#addGuide').onclick=()=>{try{this.edit(b=>Object.assign(b,guidePatch(b,b.references.at(-1).id,nextGuideFrame(b))));this.refresh();}catch(e){this.onError(e);}};
    body.querySelector('#uploadGuide').onclick=()=>this.chooseUpload(true);
    body.querySelectorAll('[data-guide-ref],[data-guide-frame]').forEach(e=>e.onchange=()=>{try{const i=Number(e.dataset.guideRef??e.dataset.guideFrame),ref=body.querySelector(`[data-guide-ref="${i}"]`).value,frame=Number(body.querySelector(`[data-guide-frame="${i}"]`).value);this.edit(b=>Object.assign(b,guidePatch(b,ref,frame,i)));this.refresh();}catch(error){this.onError(error);this.render('guides');}});
    body.querySelectorAll('[data-delete-guide]').forEach(e=>e.onclick=()=>{this.edit(b=>b.guides.splice(Number(e.dataset.deleteGuide),1));this.refresh();});
    this.drop(body,true);
  }
  drop(body,guide){body.ondragover=e=>{e.preventDefault();body.classList.add('dragging');};body.ondragleave=()=>body.classList.remove('dragging');body.ondrop=e=>{e.preventDefault();body.classList.remove('dragging');this.uploadFiles(e.dataTransfer.files,{asGuide:guide}).catch(this.onError);};}
  async move(id,direction){
    const pid=this.getProject().id,bid=this.getBlock().id;await this.flush();
    if(this.getProject().id!==pid||this.getBlock().id!==bid)return;
    const order=referenceOrder(this.getBlock(),id,direction);
    const p=await this.api(`/api/projects/${encodeURIComponent(pid)}/operations`,'POST',{expected_revision:this.getProject().revision,operation:'reorder_references',payload:{block_id:bid,order},actor:'author'});
    if(this.getProject().id!==pid||this.getBlock().id!==bid)return;
    if(this.isDirty?.())throw new Error('Перестановка сохранена. Сохраните или разрешите конфликт текущего черновика перед обновлением.');
    this.onProject(p);this.refresh();
  }
}
