import test from 'node:test';
import assert from 'node:assert/strict';
import {TimelineMenu,timelineCommands} from '../static/timeline-menu.js';
import {TimelineView} from '../static/timeline.js';

const clip=(patch={})=>({id:'clip-a',block_id:'block-a',segment_id:'seg-a',take_id:'take-a',start_frame:12,duration_frames:24,source_in_frame:0,...patch});
function view(){
  const calls=[],errors=[],p={id:'p',revision:7,blocks:[{id:'block-a',label:'CL-A'}]},items={clip:clip(),segment:{id:'seg-a',name:'Бой',start_frame:0,duration_frames:96}};
  const v={cursor:24,pending:false,project:()=>p,lookup:(kind,id)=>items[kind]?.id===id?items[kind]:null,write:(...a)=>calls.push(['write',...a]),
    onSeek:n=>calls.push(['seek',n]),onError:e=>errors.push(e),choose:(kind,id)=>{v.selection={kind,id};},render:()=>{v.renders=(v.renders??0)+1;},onEditStart:()=>{v.paused=true;}};
  for(const name of ['editSource','editStoryClip','openBlock','openSegment','editSegment','previewSegment','addStoryBlock','addSegmentSound'])v[name]=(...args)=>calls.push([name,...args]);
  return {v,p,items,calls,errors};
}
const command=(rows,id)=>rows.find(x=>x.id===id);
const context={frame:24,revision:7};

test('clip menu commands reuse montage actions',async()=>{
  const {v,items,calls}=view(),c=items.clip,before=structuredClone(c),rows=timelineCommands(v,'clip',c,context);
  assert.deepEqual(rows.map(r=>r.id),['source','description','materials','jump','split','anchor','gap','remove']);
  for(const id of ['source','description','materials','jump','split','anchor','gap','remove'])await command(rows,id).run();
  assert.deepEqual(calls,[['editSource',c],['editStoryClip',c],['openBlock','block-a'],['seek',12],
    ['write','split_clip',{id:'clip-a',frame:24},{expectedRevision:7}],['write','set_clip_anchor',{id:'clip-a',enabled:true},{expectedRevision:7}],
    ['write','set_gap_protected',{id:'clip-a',enabled:true},{expectedRevision:7}],['write','remove_clip',{id:'clip-a'},{expectedRevision:7}]]);
  assert.deepEqual(c,before);
  const toggled=timelineCommands(v,'clip',clip({anchor_frame:12,gap_protected:true}),context);
  assert.equal(command(toggled,'anchor').checked,true);assert.equal(command(toggled,'gap').checked,true);
  await command(toggled,'anchor').run();await command(toggled,'gap').run();assert.equal(calls.at(-2)[2].enabled,false);assert.equal(calls.at(-1)[2].enabled,false);
});

test('clip menu disables missing media pending edits and split boundaries',()=>{
  const {v}=view();
  for(const frame of [0,12,36,37,NaN])assert.equal(command(timelineCommands(v,'clip',clip(),{...context,frame}),'split').disabled,true);
  for(const frame of [13,35])assert.equal(command(timelineCommands(v,'clip',clip(),{...context,frame}),'split').disabled,false);
  const rows=timelineCommands(v,'clip',clip({take_id:null}),context);assert.equal(command(rows,'source').disabled,true);assert.equal(command(rows,'split').disabled,false);
  v.pending=true;assert.equal(timelineCommands(v,'clip',clip(),context).every(c=>c.disabled),true);
  v.pending=false;delete v.editStoryClip;assert.equal(command(timelineCommands(v,'clip',clip(),context),'description').disabled,true);
});

test('segment menu commands use scoped callbacks and preserve source operations',async()=>{
  const {v,items,calls}=view(),s=items.segment,rows=timelineCommands(v,'segment',s,context);
  for(const row of rows)await row.run();
  assert.deepEqual(calls,[['openSegment','seg-a'],['editSegment','seg-a'],['previewSegment','seg-a'],['seek',0],['addStoryBlock','seg-a'],['addSegmentSound','seg-a'],
    ['write','compact_segment',{segment_id:'seg-a'},{expectedRevision:7}],['write','remove_segment',{id:'seg-a'},{expectedRevision:7}]]);
  v.planIssues=()=>[{segment_id:'elsewhere'}];assert.equal(command(timelineCommands(v,'segment',s,context),'preview').disabled,false);
  v.contextIssues=()=>[{segment_id:'seg-a'}];assert.equal(command(timelineCommands(v,'segment',s,context),'preview').disabled,true);
  assert.deepEqual(timelineCommands(v,'marker',{},context),[]);
});

class Hub{
  constructor(){this.listeners=new Map();}
  addEventListener(name,fn){if(!this.listeners.has(name))this.listeners.set(name,new Set());this.listeners.get(name).add(fn);}
  removeEventListener(name,fn){this.listeners.get(name)?.delete(fn);}
  emit(name,target){for(const fn of [...this.listeners.get(name)??[]])fn({target});}
  count(){return [...this.listeners.values()].reduce((sum,s)=>sum+s.size,0);}
}
function dom(t){
  const oldDoc=globalThis.document,oldWindow=globalThis.window,d=new Hub(),w=new Hub();d.cleanup=[];w.innerWidth=1000;w.innerHeight=720;
  class Element{
    constructor(tag){this.tagName=tag.toUpperCase();this.children=[];this.dataset={};this.attrs={};this.style={};this.disabled=false;}
    append(...rows){for(const child of rows){child.parent=this;this.children.push(child);}}
    remove(){if(this.parent)this.parent.children=this.parent.children.filter(c=>c!==this);this.parent=null;}
    contains(target){return this===target||this.children.some(c=>c.contains(target));}
    get isConnected(){return this===d.body||!!this.parent?.isConnected;}
    setAttribute(k,v){this.attrs[k]=v;}removeAttribute(k){delete this.attrs[k];}
    focus(){d.activeElement=this;}
    getBoundingClientRect(){return this.className==='tl-context-menu'?{width:270,height:340}:{left:900,top:680,bottom:720,width:80,height:40};}
  }
  d.createElement=tag=>new Element(tag);d.body=new Element('body');globalThis.document=d;globalThis.window=w;
  t.after(()=>{for(const clean of d.cleanup)clean();if(oldDoc===undefined)delete globalThis.document;else globalThis.document=oldDoc;if(oldWindow===undefined)delete globalThis.window;else globalThis.window=oldWindow;});
  return {d,w,element:(kind,id)=>{const el=d.createElement('div');el.dataset={kind,id};d.body.append(el);return el;}};
}
function event(patch={}){return {type:'contextmenu',clientX:980,clientY:710,preventDefault(){this.prevented=true;},stopPropagation(){this.stopped=true;},...patch};}
function setup(t){const env=dom(t),state=view(),owner=env.element('clip','clip-a');state.v.root={querySelector:()=>owner};const menu=new TimelineMenu(state.v);env.d.cleanup.push(()=>menu.close());return {...env,...state,owner,menu};}

test('context menu selects without moving playhead and fits the viewport',t=>{
  const {v,calls,d,w,owner,menu}=setup(t),e=event();menu.open(e,owner);const s=menu.session;
  assert.equal(e.prevented,true);assert.equal(e.stopped,true);assert.equal(v.paused,true);assert.equal(v.cursor,24);assert.equal(v.renders,1);
  assert.deepEqual(v.selection,{kind:'clip',id:'clip-a'});assert.deepEqual(calls,[]);
  assert.equal(s.menu.style.left,'722px');assert.equal(s.menu.style.top,'372px');assert.equal(d.activeElement,s.buttons[0]);
  assert.equal(s.menu.attrs.role,'menu');assert.equal(owner.attrs['aria-expanded'],'true');assert.equal(d.count(),3);assert.equal(w.count(),1);
  assert.equal(s.buttons.find(b=>b.dataset.command==='anchor').attrs['aria-checked'],'false');
});

test('context menu keyboard skips disabled items and restores focus on escape',t=>{
  const {items,owner,menu,d}=setup(t);items.clip.take_id=null;menu.open(event({type:'keydown',key:'F10',shiftKey:true,clientX:0,clientY:0}),owner);
  const s=menu.session,first=s.buttons.find(b=>!b.disabled);assert.equal(d.activeElement,first);assert.equal(first.dataset.command,'description');
  s.menu.onkeydown(event({key:'End'}));assert.equal(d.activeElement.dataset.command,'remove');
  s.menu.onkeydown(event({key:'ArrowDown'}));assert.equal(d.activeElement,first);
  s.menu.onkeydown(event({key:'ArrowUp'}));assert.equal(d.activeElement.dataset.command,'remove');
  s.menu.onkeydown(event({key:'Home'}));assert.equal(d.activeElement,first);
  const space=event({key:' ',code:'Space'});s.menu.onkeydown(space);assert.equal(space.stopped,true);assert.equal(space.prevented,undefined);
  s.menu.onkeydown(event({key:'Escape'}));assert.equal(menu.session,null);assert.equal(d.activeElement,owner);assert.equal(d.count(),0);assert.equal(owner.attrs['aria-expanded'],'false');
});

test('context menu closes outside on scroll resize tab and replacement without leaking listeners',t=>{
  const {d,w,menu,owner}=setup(t);
  for(const signal of ['pointerdown','contextmenu','scroll','resize','Tab']){
    menu.open(event(),owner);const s=menu.session;d.emit('scroll',s.menu);assert.equal(menu.session,s);
    if(signal==='resize')w.emit(signal,w);else if(signal==='Tab')s.menu.onkeydown(event({key:'Tab'}));else d.emit(signal,d.body);
    assert.equal(menu.session,null);assert.equal(d.count(),0);assert.equal(w.count(),0);
  }
  menu.open(event(),owner);const old=menu.session;menu.open(event(),owner);assert.equal(old.menu.isConnected,false);assert.equal(d.count(),3);assert.equal(w.count(),1);
});

test('context menu rejects stale project revision removed targets and pending writes',async t=>{
  const {menu,owner,p,v,items,calls,errors}=setup(t);
  for(const change of [()=>p.id='other',()=>p.revision=8,()=>delete items.clip,()=>v.pending=true]){
    p.id='p';p.revision=7;v.pending=false;items.clip=clip();menu.open(event(),owner);const button=menu.session.buttons.find(b=>b.dataset.command==='split');change();await button.onclick();
    assert.equal(menu.session,null);assert.deepEqual(calls,[]);
  }
  assert.equal(errors.length,4);
});

test('context menu dispatches one captured action and reports callback failure',async t=>{
  const {menu,owner,v,calls,errors}=setup(t);menu.open(event(),owner);const button=menu.session.buttons.find(b=>b.dataset.command==='split');v.cursor=35;
  await button.onclick();await button.onclick();assert.deepEqual(calls,[['write','split_clip',{id:'clip-a',frame:24},{expectedRevision:7}]]);
  v.editSource=async()=>{throw new Error('Cannot open source');};menu.open(event(),owner);await menu.session.buttons[0].onclick();assert.equal(errors[0].message,'Cannot open source');
});

test('timeline menu key targets only blocks and segments',()=>{
  const v=Object.create(TimelineView.prototype),seen=[];v.lookup=()=>({id:'x'});v.openContextMenu=(e,el)=>seen.push([e.key,el.dataset.kind]);
  for(const kind of ['clip','segment'])for(const props of [{key:'ContextMenu'},{key:'F10',shiftKey:true}])v.key(event(props),{dataset:{kind,id:'x'}});
  v.key(event({key:'F10',shiftKey:true}),{dataset:{kind:'marker',id:'x'}});assert.deepEqual(seen,[['ContextMenu','clip'],['F10','clip'],['ContextMenu','segment'],['F10','segment']]);
});
