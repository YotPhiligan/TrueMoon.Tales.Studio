import test from 'node:test';
import assert from 'node:assert/strict';
import {exportSummary,ExportsDesk} from '../static/exports-ui.js';

function deferred(){let resolve,reject;const promise=new Promise((yes,no)=>{resolve=yes;reject=no;});return {promise,resolve,reject};}
function project(){return {id:'p1',revision:7,output_size:[1280,720],timeline:{fps:24,segments:[{start_frame:0,duration_frames:96}],clips:[],markers:[],music:null}};}
function row(patch={}){return {id:'export-0000000000000001',state:'running',revision:2,project_revision:7,size:[896,504],seconds:4,created_at:'2026-10-31T10:30:00Z',fps:24,error:'',...patch};}
function list(){return {html:'',buttons:new Map(),writes:0,
  set innerHTML(html){this.html=html;this.buttons.clear();this.writes++;},get innerHTML(){return this.html;},
  querySelectorAll(selector){
    if(!this.buttons.has(selector)){
      const attribute=selector.slice(1,-1),name=attribute.slice(5).replace(/-([a-z])/g,(_,s)=>s.toUpperCase());
      this.buttons.set(selector,[...this.html.matchAll(new RegExp(attribute+'="([^"]*)"','g'))].map(m=>({dataset:{[name]:m[1]}})));
    }return this.buttons.get(selector);
  }};}
function setup(t){
  const env={p:project(),root:null,calls:[],pending:[],waiters:[],modals:0,uuid:0,errors:[],dialogOpen:true};
  const originals=[];
  function global(name,value){const old=Object.getOwnPropertyDescriptor(globalThis,name);originals.push(()=>old?Object.defineProperty(globalThis,name,old):delete globalThis[name]);Object.defineProperty(globalThis,name,{configurable:true,value});}
  global('document',{querySelector:selector=>selector==='#exportsDesk'?env.root:null});
  global('crypto',{randomUUID:()=>`export-client-${String(++env.uuid).padStart(16,'0')}`});
  t.after(()=>{for(const restore of originals.reverse())restore();});
  env.flush=async()=>{};
  env.waitCall=n=>{if(env.calls.length>=n)return Promise.resolve();const gate=deferred();env.waiters.push([n,gate]);return gate.promise;};
  env.desk=new ExportsDesk({getProject:()=>env.p,flush:()=>env.flush(),onError:e=>env.errors.push(e),
    modal:()=>{
      env.modals++;const form={elements:{profile:{value:'draft'}},button:{disabled:false},querySelector(){return this.button;}};
      env.root={isConnected:true,form,list:list(),error:{textContent:''},viewer:{hidden:true,innerHTML:'',videos:[],querySelectorAll(){return this.videos;}},
        closest:()=>({open:env.dialogOpen}),querySelector(selector){return selector==='#exportForm'?this.form:selector==='#exportsList'?this.list:selector==='#exportViewer'?this.viewer:this.error;}};
    },api:(...args)=>{env.calls.push(args);const gate=deferred();env.pending.push(gate);for(const [n,w] of env.waiters)if(n<=env.calls.length)w.resolve();return gate.promise;}});
  return env;
}
async function open(env,rows=[]){const pending=env.desk.open();await env.waitCall(env.calls.length+1);env.pending.at(-1).resolve(rows);await pending;return env.root;}
const submit=root=>root.form.onsubmit({preventDefault(){}});

test('export dialog opens for legacy project without timeline without writing a migration',async t=>{
  const env=setup(t);delete env.p.timeline;const before=structuredClone(env.p);
  await open(env);
  assert.equal(env.modals,1);assert.deepEqual(env.p,before);
  assert.deepEqual(env.calls,[['/api/projects/p1/exports']]);
});

test('export summary pins state revision warning output size and duration precision',()=>{
  assert.equal(exportSummary(row(),7),'Экспортируется · снимок v7 · 896 × 504 · 4.000 с');
  assert.equal(exportSummary(row({state:'completed',seconds:2.333333}),8),'Готово · снимок v7 · монтаж уже изменён · 896 × 504 · 2.333 с');
  assert.match(exportSummary(row({profile:'final'}),7),/финальный/);
  assert.equal(exportSummary(row({state:'interrupted'}),7),'Прерван · снимок v7 · 896 × 504 · 4.000 с');
});

test('export form performs no automatic POST and keeps retry key on network failure renewing only after success',async t=>{
  const env=setup(t),root=await open(env),before=structuredClone(env.p);
  assert.deepEqual(env.calls,[['/api/projects/p1/exports']]);assert.equal(env.desk.active(),true);
  root.form.elements.profile.value='final';env.flush=async()=>{env.p.revision=8;};
  const first=submit(root);await env.waitCall(2);await submit(root);
  assert.equal(env.calls.length,2);assert.equal(root.form.button.disabled,true);
  const body=env.calls[1][2];assert.deepEqual(body,{expected_revision:8,profile:'final',actor:'author',client_key:'export-client-0000000000000001'});
  env.pending[1].reject(new Error('Network interrupted'));await first;
  assert.equal(root.error.textContent,'Network interrupted');assert.equal(root.form.button.disabled,false);
  env.flush=async()=>{env.p.revision=9;};const retry=submit(root);await env.waitCall(3);
  assert.equal(env.calls[2][2].client_key,body.client_key);assert.equal(env.calls[2][2].expected_revision,9);
  env.pending[2].resolve(row());await env.waitCall(4);env.pending[3].resolve([]);await retry;
  assert.equal(root.error.textContent,'');assert.equal(root.form.button.disabled,false);
  const next=submit(root);await env.waitCall(5);assert.notEqual(env.calls[4][2].client_key,body.client_key);
  env.pending[4].resolve(row());await env.waitCall(6);env.pending[5].resolve([]);await next;
  assert.deepEqual({...env.p,revision:7},before);assert.equal(env.calls.every(([url])=>url==='/api/projects/p1/exports'),true);
});

test('export open and submit ignore project changes during flush and late success cannot replace modal',async t=>{
  const env=setup(t),gate=deferred();env.flush=()=>gate.promise;
  const opening=env.desk.open();env.p={...env.p,id:'p2'};gate.resolve();await opening;
  assert.equal(env.modals,0);assert.deepEqual(env.calls,[]);
  env.p=project();env.flush=async()=>{};const root=await open(env);
  const pending=submit(root);await env.waitCall(2);
  const replacement={isConnected:true,closest:()=>({open:true})};env.root=replacement;
  env.pending[1].resolve(row());await pending;
  assert.equal(env.root,replacement);assert.equal(env.calls.length,2);assert.equal(root.form.button.disabled,false);
  env.root=root;const gate2=deferred();env.flush=()=>gate2.promise;
  const switched=submit(root);env.p={...env.p,id:'p2'};gate2.resolve();await switched;
  assert.equal(env.calls.length,2);assert.equal(root.form.button.disabled,false);
});

test('export list refresh ignores stale project closed dialog and old response while preserving viewer',async t=>{
  const completed=row({state:'completed',video_url:'/api/projects/p1/exports/export-0000000000000001/video',result:{video_info:{frames:96},elapsed_seconds:1.2}});
  const env=setup(t),root=await open(env,[completed]);
  root.list.querySelectorAll('[data-export-view]')[0].onclick();assert.equal(root.viewer.hidden,false);assert.match(root.viewer.innerHTML,/<video controls/);
  const viewer=root.viewer.innerHTML;
  const older=env.desk.refresh(),newer=env.desk.refresh();
  env.pending[2].resolve([row({error:'<unsafe>',progress:{percent:250,stage:'<script>',elapsed_seconds:1}})]);await newer;
  assert.match(root.list.innerHTML,/width:100%/);assert.match(root.list.innerHTML,/&lt;unsafe&gt;/);assert.match(root.list.innerHTML,/&lt;script&gt;/);
  env.pending[1].resolve([]);await older;assert.match(root.list.innerHTML,/Экспортируется/);assert.equal(root.viewer.innerHTML,viewer);
  const writes=root.list.writes,switched=env.desk.refresh();env.p={...env.p,id:'p2'};env.pending[3].resolve([]);await switched;
  assert.equal(root.list.writes,writes);
  env.p=project();const closed=env.desk.refresh();env.dialogOpen=false;env.pending[4].resolve([]);await closed;
  assert.equal(root.list.writes,writes);assert.equal(env.desk.active(root),false);
});

test('export stop reads latest status revision before POST and late project response cannot stop old task',async t=>{
  const env=setup(t),root=await open(env,[row()]);
  const stopping=root.list.querySelectorAll('[data-export-stop]')[0].onclick();
  assert.deepEqual(env.calls[1],['/api/projects/p1/exports/export-0000000000000001']);
  env.pending[1].resolve(row({revision:11}));await env.waitCall(3);
  assert.deepEqual(env.calls[2],['/api/projects/p1/exports/export-0000000000000001/stop','POST',{expected_revision:11,actor:'author'}]);
  env.pending[2].resolve(row({state:'stopping'}));await env.waitCall(4);env.pending[3].resolve([row()]);await stopping;
  const stale=root.list.querySelectorAll('[data-export-stop]')[0].onclick();env.p={...env.p,id:'p2'};
  env.pending[4].resolve(row({revision:12}));await stale;
  assert.equal(env.calls.length,5);assert.deepEqual(env.errors,[]);
});
