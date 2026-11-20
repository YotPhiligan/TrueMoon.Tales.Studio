import test from 'node:test';
import assert from 'node:assert/strict';
import {layoutContext,densityKey,resolveDensity,loadDensity,saveDensity} from '../static/timeline-layout.js';
import {TimelineView} from '../static/timeline.js';
import {AudioTimelineView} from '../static/audio-timeline.js';

function storage(t){
  const original=Object.getOwnPropertyDescriptor(globalThis,'localStorage'),rows=new Map(),writes=[];
  const store={getItem:k=>rows.get(k)??null,setItem:(k,v)=>{rows.set(k,v);writes.push([k,v]);}};
  Object.defineProperty(globalThis,'localStorage',{configurable:true,value:store});
  t.after(()=>original?Object.defineProperty(globalThis,'localStorage',original):delete globalThis.localStorage);
  return {store,rows,writes};
}
function view(t){
  const env=storage(t);let context='story';const v=Object.create(TimelineView.prototype),focus=[];
  Object.assign(v,{getLayoutContext:()=>context,collapsed:false,cursor:48,zoom:32,selection:{kind:'clip',id:'one'},
    root:{querySelector:id=>({focus:()=>focus.push(id)})},render:()=>{v.renders=(v.renders??0)+1;}});
  return {...env,v,focus,context:value=>context=value};
}

test('timeline density defaults to visible compact story and full editing',()=>{
  assert.equal(layoutContext('story'),'story');for(const value of ['editing',null,undefined,'else'])assert.equal(layoutContext(value),'editing');
  for(const value of [null,undefined,'',false,'auto','wide']){assert.equal(resolveDensity('story',value),'compact');assert.equal(resolveDensity('editing',value),'full');}
  assert.equal(resolveDensity('story','full'),'full');assert.equal(resolveDensity('editing','compact'),'compact');
});

test('density preferences persist independently of project in two context keys',t=>{
  const {store,rows,writes}=storage(t);assert.equal(loadDensity('story',store),'compact');assert.equal(loadDensity('editing',store),'full');assert.deepEqual(writes,[]);
  saveDensity('story','full',store);assert.equal(loadDensity('story',store),'full');assert.equal(loadDensity('editing',store),'full');
  saveDensity('editing','compact',store);assert.equal(loadDensity('editing',store),'compact');assert.equal(loadDensity('story',store),'full');
  assert.deepEqual([...rows.keys()],[densityKey('story'),densityKey('editing')]);assert.throws(()=>saveDensity('story','wide',store),/компактный/);
});

test('storage failures keep layout usable without a migration or project write',()=>{
  const bad={getItem(){throw new Error('blocked');},setItem(){throw new Error('quota');}};
  assert.equal(loadDensity('story',bad),'compact');assert.equal(loadDensity('editing',bad),'full');assert.doesNotThrow(()=>saveDensity('story','full',bad));
});

test('view restores context preference and prevents inherited collapse from hiding compact lanes',t=>{
  const {v,context,writes}=view(t);v.collapsed=true;
  assert.deepEqual(v.timelineLayout(),{context:'story',compact:true,collapsed:false});assert.deepEqual(writes,[]);
  context('editing');assert.deepEqual(v.timelineLayout(),{context:'editing',compact:false,collapsed:true});
  v.setDensity('compact');assert.equal(v.collapsed,false);assert.equal(v.timelineLayout().compact,true);
  context('story');assert.equal(v.timelineLayout().compact,true);v.setDensity('full');assert.equal(v.timelineLayout().compact,false);
  context('editing');assert.equal(v.timelineLayout().compact,true);context('story');assert.equal(v.timelineLayout().compact,false);
});

test('density change only alters UI and preserves cursor zoom selection and project',t=>{
  const {v,rows,focus}=view(t),project={revision:9,timeline:{clips:[{id:'one',duration_frames:48}],undo:[{state:'original'}]}};
  v.project=()=>project;v.collapsed=true;const before=structuredClone(project),selection=v.selection;
  v.setDensity('full');assert.deepEqual(project,before);assert.equal(v.cursor,48);assert.equal(v.zoom,32);assert.equal(v.selection,selection);
  assert.equal(v.collapsed,false);assert.equal(v.renders,1);assert.deepEqual(focus,['#tlDensity']);assert.equal(rows.get(densityKey('story')),'full');
});

test('inspector keeps pending form values through timeline density and cursor updates',t=>{
  const {v}=view(t),project={id:'p',revision:1},submit={disabled:false},target={scrollTop:0,querySelector:()=>submit};
  const rendered=[];
  Object.assign(v,{project:()=>project,propertiesRoot:target,properties:root=>rendered.push(root),refreshClipActions:()=>{}});
  v.refreshProperties();
  target.scrollTop=42;
  v.cursor=55;
  v.setDensity('full');
  v.refreshProperties();
  assert.deepEqual(rendered,[target]);
  assert.equal(target.scrollTop,42);
  v.pending=true;
  v.refreshProperties();
  assert.equal(submit.disabled,true);
  v.pending=false;
  v.refreshProperties();
  assert.equal(submit.disabled,false);
  assert.deepEqual(rendered,[target]);
  v.selection={kind:'segment',id:'s'};
  v.refreshProperties();
  assert.deepEqual(rendered,[target,target]);
  assert.equal(target.scrollTop,0);
});

test('music waveform stays visible and inside the canvas in both timeline densities',()=>{
  for(const compact of [true,false]){
    const rects=[],canvas={style:{},dataset:{},getContext:()=>({scale(){},clearRect(){},fillRect:(...a)=>rects.push(a)})},element={getBoundingClientRect:()=>({left:0,width:320}),querySelector:name=>name==='canvas'?canvas:{style:{}}};
    const v=Object.create(AudioTimelineView.prototype);Object.assign(v,{zoom:32,timelineLayout:()=>({compact}),project:()=>({id:'p'}),data:()=>({fps:24}),
      root:{querySelector:name=>name==='.tl-music'?element:name==='.tl-scroll'?{getBoundingClientRect:()=>({left:0,right:320})}:{clientWidth:0}},
      waveData:{key:'p:music',data:{peaks_per_second:100,peaks:Array(1000).fill(1)}}});
    v.drawWaveform({asset_id:'music',source_in_frame:0,muted:false});const height=compact?24:52;
    assert.equal(canvas.style.height,height+'px');assert.equal(rects.length,320);assert.equal(canvas.dataset.ready,'true');
    for(const [x,y,width,h] of rects){assert.ok(y>=0&&y+h<=height);assert.ok(h>0);assert.equal(width,1);assert.ok(x>=0&&x<320);}
  }
});
