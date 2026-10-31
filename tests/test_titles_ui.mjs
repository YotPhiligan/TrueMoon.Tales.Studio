import test from 'node:test';
import assert from 'node:assert/strict';
import {titleOpacity,titleIssue,projectFrameBox,placeTitleImage} from '../static/titles-logic.js';
import {timelineDuration,segmentPlayback,criticalSignature} from '../static/playback-logic.js';
import {MontagePlayer} from '../static/montage-player.js';
import {TitlesDesk} from '../static/titles-ui.js';

const row=(patch={})=>({id:'title-one',name:'Автор',text:'Сказки\nистинной луны',start_frame:4,duration_frames:8,
  fade_in_frames:2,fade_out_frames:2,font_id:'arial',font_size:20,color:'#FFFFFF',align:'center',position:'center',background:'transparent',outline:false,...patch});

test('title midpoint opacity uses absolute frames half-open limits floor and touching fade sum',()=>{
  assert.deepEqual([3,4,5,6,9,10,11,12].map(f=>titleOpacity(row(),f)),[0,.25,.75,1,1,.75,.25,0]);
  assert.equal(titleOpacity(row(),4.9),.25);assert.equal(titleOpacity(row(),11.9),.25);
  assert.deepEqual([0,1,2,3].map(f=>titleOpacity(row({start_frame:0,duration_frames:4}),f)),[.25,.75,.75,.25]);
  assert.deepEqual([-1,0,1].map(f=>titleOpacity(row({start_frame:0,duration_frames:1,fade_in_frames:0,fade_out_frames:0}),f)),[0,1,0]);
});

test('title form issue validates text line count controls duration fade and font size boundaries',()=>{
  const good=row();assert.equal(titleIssue(good),'');
  for(const patch of [{text:'я'.repeat(1000)},{text:Array(12).fill('Я').join('\n')},{font_size:8},{font_size:256},
    {start_frame:518392},{duration_frames:4}])assert.equal(titleIssue(row(patch)),'');
  for(const patch of [{text:''},{text:' '},{text:'я'.repeat(1001)},{text:Array(13).fill('Я').join('\n')},{text:'bad\x01'},
    {name:''},{name:'x'.repeat(161)},{start_frame:-1},{start_frame:518393},{duration_frames:0},{duration_frames:true},
    {fade_in_frames:-1},{fade_out_frames:2.5},{fade_in_frames:7},{font_size:7},{font_size:257},{font_size:true}]){
    const issue=titleIssue(row(patch));assert.equal(typeof issue,'string');assert.notEqual(issue,'');
  }
});

test('canonical frame fit and cropped PNG placement use shared project percentages without changing layers',()=>{
  assert.deepEqual(projectFrameBox(640,480,[1280,720]),{x:0,y:60,width:640,height:360});
  assert.deepEqual(projectFrameBox(400,800,[320,180]),{x:0,y:287.5,width:400,height:225});
  for(const args of [[0,480,[1280,720]],[640,-1,[1280,720]],[640,480,null],[640,480,[0,720]]])assert.equal(projectFrameBox(...args),null);
  const layer={canvas_size:[320,180],x:32,y:18,width:64,height:36},before=structuredClone(layer),img={style:{opacity:'.5'}};
  placeTitleImage(img,layer);assert.deepEqual(img.style,{opacity:'.5',left:'10%',top:'10%',width:'20%',height:'20%'});assert.deepEqual(layer,before);
});

function project(){return {id:'p1',revision:7,fps:8,output_size:[320,180],blocks:[],assets:{},timeline:{version:1,fps:8,
  segments:[{id:'s1',name:'Chosen',color:'mint',start_frame:8,duration_frames:8},{id:'s2',name:'Other',color:'blue',start_frame:24,duration_frames:8}],
  clips:[],markers:[],music:null,clip_audio:false,titles:[row({duration_frames:16}),row({id:'late',start_frame:40})],undo:[],redo:[]}};}
function deferred(){let resolve,reject;const promise=new Promise((yes,no)=>{resolve=yes;reject=no;});return {promise,resolve,reject};}
function layer(id='title-one',key='a'){return {id,key,url:'/title/'+key,canvas_size:[320,180],x:32,y:18,width:64,height:36,background:'black'};}

test('title tail extends montage but scoped playback keeps absolute timing and global title signature',()=>{
  const p=project(),scope=segmentPlayback(p,'s1'),initial=criticalSignature(scope.project);
  assert.equal(timelineDuration(p.timeline),6);assert.deepEqual(scope.range,{id:'s1',name:'Chosen',start:1,end:2});
  assert.deepEqual(scope.project.timeline.titles.map(r=>r.id),['title-one']);assert.equal(scope.project.timeline.titles[0].start_frame,4);
  assert.equal(titleOpacity(scope.project.timeline.titles[0],8),1);assert.equal(scope.project.timeline.title_signature.length,2);
  const changed=structuredClone(p);changed.timeline.titles[1].text='Changed outside segment';
  assert.notEqual(criticalSignature(segmentPlayback(changed,'s1').project),initial);
});

class Element{
  constructor(tag='div'){this.tagName=tag.toUpperCase();this.style={};this.dataset={};this.children=[];this.attributes={};this.hidden=false;this.isConnected=true;this.clientWidth=640;this.clientHeight=480;}
  append(...nodes){for(const n of nodes){n.parentNode=this;this.children.push(n);}}
  replaceChildren(...nodes){this.children=[];this.append(...nodes);}
  setAttribute(k,v){this.attributes[k]=v;}removeAttribute(k){delete this.attributes[k];if(k==='disabled')this.disabled=false;}
  addEventListener(){}pause(){this.paused=true;}load(){}
}
function globals(t,values){const restores=[];for(const [name,value] of Object.entries(values)){const old=Object.getOwnPropertyDescriptor(globalThis,name);restores.push(()=>old?Object.defineProperty(globalThis,name,old):delete globalThis[name]);Object.defineProperty(globalThis,name,{configurable:true,value});}t.after(()=>{for(const restore of restores.reverse())restore();});}
function playerSetup(t){
  const env={p:project(),fetches:[],pending:[],waiters:[],errors:[],decode:null,decodeStarted:deferred()};
  env.waitFetch=n=>{if(env.fetches.length>=n)return Promise.resolve();const d=deferred();env.waiters.push([n,d]);return d.promise;};
  globals(t,{document:{createElement:tag=>{const node=new Element(tag);if(tag==='img')node.decode=()=>{env.decodeStarted.resolve();return env.decode??Promise.resolve();};return node;}},
    fetch:url=>{env.fetches.push(url);const d=deferred();env.pending.push(d);for(const [n,w] of env.waiters)if(n<=env.fetches.length)w.resolve();return d.promise;}});
  env.player=new MontagePlayer({getProject:()=>env.p,onError:e=>env.errors.push(e)});env.player.syncProject();
  env.player.container=new Element();env.player.titleStage=new Element();env.player.image=new Element('img');env.player.blackout=new Element();env.player.videos=[new Element('video'),new Element('video')];
  t.after(()=>env.player.pause());return env;
}
const response=layers=>({ok:true,json:async()=>({project_revision:7,layers})});

test('player shared title PNG uses canonical box group opacity and one fetch while resize refits all layers',async t=>{
  const env=playerSetup(t),p=env.player,loading=p.loadTitleLayers(),same=p.loadTitleLayers();
  assert.deepEqual(env.fetches,['/api/projects/p1/title-layers']);env.pending[0].resolve(response([layer()]));await Promise.all([loading,same]);
  const node=p.titleNodes[0];assert.match(node.wrapper.className,/ black/);assert.deepEqual(node.img.style,{left:'10%',top:'10%',width:'20%',height:'20%'});
  p.paintTitles(4);assert.equal(node.wrapper.style.opacity,'0.250000');assert.equal(node.wrapper.hidden,false);
  p.paintTitles(20);assert.equal(node.wrapper.hidden,true);
  for(const n of [...p.videos,p.image,p.blackout,p.titleStage])assert.deepEqual(n.style,{left:'0px',top:'60px',width:'640px',height:'360px',right:'auto',bottom:'auto'});
  p.container.clientWidth=400;p.container.clientHeight=800;p.fitProjectFrame();assert.equal(p.titleStage.style.top,'287.5px');
  env.p.timeline.titles=[];p.fitProjectFrame();assert.equal(p.titleStage.style.width,'');assert.deepEqual(env.errors,[]);
});

test('player stale PNG decode cannot replace new project or stage and paused preload cannot autoplay',async t=>{
  const env=playerSetup(t),p=env.player,oldGate=deferred();env.decode=oldGate.promise;
  const old=p.loadTitleLayers(),oldStage=p.titleStage;
  env.pending[0].resolve(response([layer('title-one','old')]));await env.decodeStarted.promise;
  env.decode=null;env.p={...env.p,id:'p2'};p.titleStage=new Element();const fresh=p.loadTitleLayers();
  env.pending[1].resolve(response([layer('title-one','new')]));await fresh;
  oldGate.resolve();await old;
  assert.equal(p.titleNodes[0].img.src,'/title/new');assert.equal(oldStage.children.length,0);
  env.p.timeline.titles[0].text='Await decode';env.decode=deferred();const gate=env.decode;env.decode=gate.promise;env.decodeStarted=deferred();
  p.paint=()=>{};p.context=()=>p.ctx={currentTime:10,state:'running',resume:async()=>{}};p.musicBuffer=async()=>null;p.soundBuffers=async()=>[];p.tick=()=>assert.fail('late preload autoplay');
  const playing=p.play();await env.waitFetch(3);assert.equal(p.preparing,true);assert.equal(p.playing,false);
  env.pending[2].resolve(response([layer()]));await env.decodeStarted.promise;p.pause();gate.resolve();await playing;
  assert.equal(p.playing,false);assert.equal(p.preparing,false);assert.deepEqual(env.errors,[]);
});

function deskSetup(t){
  const env={p:project(),form:null,calls:[],pending:[],waiters:[],writes:[],writePending:[],timers:new Map(),next:1,closed:0,paused:0};
  const createElement=tag=>{const node=new Element(tag);node.decode=async()=>{};return node;};
  globals(t,{document:{querySelector:()=>env.form,createElement},setTimeout:fn=>{const id=env.next++;env.timers.set(id,fn);return id;},clearTimeout:id=>env.timers.delete(id)});
  env.waitCall=n=>{if(env.calls.length>=n)return Promise.resolve();const d=deferred();env.waiters.push([n,d]);return d.promise;};
  env.runTimer=()=>{const [id,fn]=env.timers.entries().next().value;env.timers.delete(id);return fn();};env.flush=async()=>{};
  env.desk=new TitlesDesk({getProject:()=>env.p,getTimeline:()=>env.p.timeline,flush:()=>env.flush(),pause:()=>env.paused++,
    closeModal:()=>{env.form.isConnected=false;env.form=null;env.closed++;},
    api:(...args)=>{env.calls.push(args);const d=deferred();env.pending.push(d);for(const [n,w] of env.waiters)if(n<=env.calls.length)w.resolve();return d.promise;},
    write:(...args)=>{env.writes.push(args);const d=deferred();env.writePending.push(d);return d.promise;},
    modal:(_title,html)=>{
      const elements={};for(const name of ['name','font_size','color','start','duration','fade_in','fade_out'])elements[name]={value:html.match(new RegExp('<input[^>]*name="'+name+'"[^>]*value="([^"]*)"'))[1]};
      elements.text={value:html.match(/<textarea[^>]*>([\s\S]*?)<\/textarea>/)[1]};elements.outline={checked:/name="outline"[^>]*checked/.test(html)};
      for(const name of ['background','font_id','align','position']){const options=html.match(new RegExp('<select name="'+name+'">([\\s\\S]*?)<\\/select>'))[1];elements[name]={value:options.match(/<option value="([^"]*)" selected/)[1]};}
      elements.namedItem=name=>elements[name];const nodes=new Map();
      const get=s=>{if(!nodes.has(s)){const node=new Element();node.disabled=false;node.textContent='';node.classList={toggle(){}};nodes.set(s,node);}return nodes.get(s);};
      env.form={elements,isConnected:true,closest:()=>({open:true}),querySelector:get,querySelectorAll:()=>[get('button[type="submit"]'),get('#titleRemove')]};
    }});
  env.open=async()=>{const opened=env.desk.open('title-one');await env.waitCall(env.calls.length+1);env.pending.at(-1).resolve([{id:'arial',label:'Arial'}]);await opened;return env.form;};return env;
}

test('title editor normalizes Cyrillic multiline and writes exact captured timing revision with pending and conflict guards',async t=>{
  const env=deskSetup(t),form=await env.open(),f=form.elements;assert.equal(env.paused,1);assert.equal(env.writes.length,0);
  f.name.value=' Автор ';f.text.value='Клэр\r\nПринято\tда';f.start.value='2';f.duration.value='1';f.fade_in.value='.25';f.fade_out.value='.25';f.font_size.value='20';form.oninput();
  const first=form.onsubmit({preventDefault(){}});await form.onsubmit({preventDefault(){}});
  assert.equal(env.writes.length,1);assert.deepEqual(env.writes[0],['update_title',row({name:'Автор',text:'Клэр\nПринято    да',start_frame:16}),{expectedRevision:7,throwErrors:true}]);
  assert.equal(env.timers.size,0);env.writePending[0].reject(Object.assign(new Error('Conflict'),{status:409}));await first;
  assert.equal(env.form,form);assert.equal(f.text.value,'Клэр\r\nПринято\tда');assert.equal(form.querySelector('#titleError').textContent,'Conflict');assert.equal(env.closed,0);
  const removing=form.querySelector('#titleRemove').onclick();assert.deepEqual(env.writes[1],['remove_title',{id:'title-one'},{expectedRevision:7,throwErrors:true}]);
  env.p={...env.p,id:'p2'};env.writePending[1].resolve(env.p);await removing;assert.equal(env.form,form);assert.equal(env.closed,0);
});

test('title draft preview debounce ignores older responses replacement forms and changed project',async t=>{
  const env=deskSetup(t),form=await env.open();const older=env.runTimer();
  assert.deepEqual(env.calls[1].slice(0,2),['/api/projects/p1/title-preview','POST']);assert.equal(env.calls[1][2].expected_revision,7);
  form.elements.text.value='Newest';form.oninput();const newer=env.runTimer();
  env.pending[2].resolve({layer:layer('title-one','new')});await newer;
  assert.equal(form.querySelector('#titleStylePreview').dataset.layer,'new');
  env.pending[1].resolve({layer:layer('title-one','old')});await older;assert.equal(form.querySelector('#titleStylePreview').dataset.layer,'new');
  form.elements.text.value='Old form';form.oninput();const oldFormPreview=env.runTimer();
  const replacement=await env.open();env.pending[3].resolve({layer:layer('title-one','wrong-form')});await oldFormPreview;
  assert.equal(env.form,replacement);assert.equal(replacement.querySelector('#titleStylePreview').dataset.layer,undefined);
  const late=env.runTimer();env.p={...env.p,id:'p2'};
  env.pending[5].resolve({layer:layer('title-one','wrong-project')});await late;
  assert.equal(replacement.querySelector('#titleStylePreview').dataset.layer,undefined);assert.equal(env.writes.length,0);assert.equal(env.closed,0);
});

test('late font catalogue never opens editor after source modal is replaced or closed',async t=>{
  const p=project(),before=structuredClone(p),body={firstElementChild:null},dialog={open:true},calls=[];
  globals(t,{document:{querySelector:s=>s==='#modalBody'?body:s==='#modal'?dialog:null}});
  let ready,fonts,modals=0;
  const desk=new TitlesDesk({getProject:()=>p,getTimeline:()=>p.timeline,flush:async()=>{},pause:()=>{},
    api:url=>{calls.push(url);ready.resolve();return fonts.promise;},modal:()=>{modals++;}});
  for(const change of ['replace','close']){
    body.firstElementChild={source:change};dialog.open=true;ready=deferred();fonts=deferred();
    const opening=desk.open('title-one');await ready.promise;
    if(change==='replace')body.firstElementChild={source:'replacement'};else dialog.open=false;
    fonts.resolve([{id:'arial',label:'Arial'}]);await opening;
    assert.equal(modals,0);
  }
  assert.deepEqual(calls,['/api/title-fonts','/api/title-fonts']);assert.deepEqual(p,before);
});
