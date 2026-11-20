import test from 'node:test';
import assert from 'node:assert/strict';
import {scenarioRequest,segmentRequests,ScenarioAgentDesk} from '../static/scenario-agent-ui.js';
import {requestSummary} from '../static/agent-ui.js';

function deferred(){let resolve,reject;const promise=new Promise((yes,no)=>{resolve=yes;reject=no;});return {promise,resolve,reject};}
function project(){
  const item=(id,bid)=>({id,block_id:bid,title:id,description:'',duration_frames:24,gap_before_frames:0,take_id:null,source_in_frame:0});
  return {id:'p1',revision:7,fps:24,blocks:[{id:'b1',title:'Source1',takes:[]},{id:'b2',title:'Source2',takes:[]}],
    timeline:{fps:24,segments:[],clips:[],markers:[],music:null,clip_audio:false,scenario:{version:1,segments:[
      {id:'s1',name:'Intro',description:'',start_frame:0,duration_frames:96,color:'mint',items:[item('c1','b1'),item('c2','b2')]},
      {id:'s2',name:'Other',description:'',start_frame:120,duration_frames:48,color:'blue',items:[item('c3','b1')]}]}}};
}
function row(patch={}){return {id:'req-0000000000000001',state:'pending',revision:1,base_project_revision:7,goal:'Prepare',mode:'apply',owner:null,result:'',
  scope:{segment_id:'s1',segment_name:'Intro',item_id:null,intent:'prepare_block',components:['prompt'],draft:{title:'Draft',description:'',duration_frames:24,after_item_id:null}},
  context:{project:{fps:24},snapshot_sha256:'snap'},...patch};}
class FakeForm{
  constructor(html){
    const field=(name,value='')=>({name,value,disabled:false});
    this.elements={goal:field('goal'),mode:field('mode','apply')};
    if(html.includes('name="title"'))for(const name of ['title','description','duration','after_item_id'])this.elements[name]=field(name);
    this.components=['description','references','guides','prompt','settings'].map(value=>({...field('components',value),checked:true}));
    this.rebase={hidden:true,disabled:false};this.error={textContent:''};this.submit={disabled:false};
  }
  querySelector(selector){return selector==='#scenarioAgentRebase'?this.rebase:selector==='#scenarioAgentError'?this.error:this.submit;}
  querySelectorAll(){return [...Object.values(this.elements),...this.components,this.rebase,this.submit];}
}
function listNode(){return {html:'',buttons:new Map(),writes:0,set innerHTML(value){this.html=value;this.buttons.clear();this.writes++;},get innerHTML(){return this.html;},
  querySelectorAll(selector){
    if(!this.buttons.has(selector)){
      const attribute=selector.slice(1,-1),dataset=attribute.slice(5).replace(/-([a-z])/g,(_,c)=>c.toUpperCase());
      this.buttons.set(selector,[...this.html.matchAll(new RegExp(attribute+'="([^"]*)"','g'))].map(m=>({dataset:{[dataset]:m[1]}})));
    }
    return this.buttons.get(selector);
  }};}
function setup(t){
  const env={p:project(),form:null,calls:[],pending:[],waiters:[],storage:new Map(),closed:0,toasts:[],copied:[],opened:[],errors:[],modals:[],refreshes:0,uuid:0};
  env.list=listNode();env.root={isConnected:true,querySelector:s=>s==='.scenario-agent-list'?env.list:{},innerHTML:''};
  const restores=[];
  function global(name,value){const old=Object.getOwnPropertyDescriptor(globalThis,name);restores.push(()=>old?Object.defineProperty(globalThis,name,old):delete globalThis[name]);Object.defineProperty(globalThis,name,{configurable:true,value});}
  global('document',{querySelector:s=>s==='#scenarioAgentForm'?env.form:null});
  global('sessionStorage',{getItem:k=>env.storage.get(k)??null,setItem:(k,v)=>env.storage.set(k,String(v)),removeItem:k=>env.storage.delete(k)});
  global('crypto',{randomUUID:()=>`scenario-client-${String(++env.uuid).padStart(16,'0')}`});global('location',{origin:'http://127.0.0.1:12345'});
  global('FormData',class{
    constructor(form){this.values=[...Object.values(form.elements).filter(f=>!f.disabled).map(f=>[f.name,f.value]),...form.components.filter(c=>c.checked&&!c.disabled).map(c=>[c.name,c.value])];}
    [Symbol.iterator](){return this.values[Symbol.iterator]();}getAll(name){return this.values.filter(([key])=>key===name).map(([,value])=>value);}
  });
  t.after(()=>{for(const restore of restores.reverse())restore();});
  env.flush=async()=>{};
  env.waitCall=n=>{if(env.calls.length>=n)return Promise.resolve();const gate=deferred();env.waiters.push([n,gate]);return gate.promise;};
  env.desk=new ScenarioAgentDesk({getProject:()=>env.p,flush:()=>env.flush(),
    api:(...args)=>{env.calls.push(args);const gate=deferred();env.pending.push(gate);for(const [n,w] of env.waiters)if(n<=env.calls.length)w.resolve();return gate.promise;},
    modal:(title,html)=>{env.modals.push([title,html]);env.form=html.includes('id="scenarioAgentForm"')?new FakeForm(html):null;},
    closeModal:()=>{env.closed++;env.form=null;},toast:m=>env.toasts.push(m),onProject:p=>{env.p=p;},
    copyText:async text=>env.copied.push(text),openBlock:id=>env.opened.push(id),onError:e=>env.errors.push(e)});
  env.desk.target=env.root;env.desk.sid='s1';
  return env;
}
function fill(form){form.elements.goal.value='  Faster shot  ';if(form.elements.title){form.elements.title.value='New shot';form.elements.description.value='Dodge';form.elements.duration.value='1.5';form.elements.after_item_id.value='c1';}for(const c of form.components)c.checked=c.value==='prompt';form.elements.goal.oninput();}
const submit=form=>form.onsubmit({preventDefault(){}});

test('scenario request DTO captures new draft or exact existing use with component subset and no mutation',()=>{
  const segment={id:'s1'},item={id:'c1',block_id:'b1'},values={goal:'  Faster  ',title:' New ',description:'Dodge',duration:'2.5',after_item_id:'c1',mode:'apply',components:['settings','prompt']};
  const original=structuredClone(values),body=scenarioRequest(segment,null,values,24);
  assert.deepEqual(body,{goal:'Faster',mode:'apply',scope:{segment_id:'s1',item_id:null,intent:'prepare_block',components:['prompt','settings'],draft:{title:'New',description:'Dodge',duration_frames:60,after_item_id:'c1'}}});
  assert.deepEqual(scenarioRequest(segment,item,{...values,mode:'propose'},24),{goal:'Faster',mode:'propose',scope:{segment_id:'s1',item_id:'c1',intent:'improve_block',components:['prompt','settings']}});
  assert.deepEqual(values,original);assert.equal(scenarioRequest(segment,null,{...values,duration:''},24).scope.draft.duration_frames,null);
});

test('scenario request boundaries filtering and summary keep old unscoped wording compatible',()=>{
  const values={goal:'Goal',title:'New',duration:'1',mode:'apply',components:['prompt']};
  for(const patch of [{goal:''},{goal:'x'.repeat(12001)},{title:'x'.repeat(161)},{components:[]},{duration:'0'},{duration:'bad'},{duration:'21601'}])assert.throws(()=>scenarioRequest({id:'s1'},null,{...values,...patch},24),Error);
  assert.equal(scenarioRequest({id:'s1'},null,{...values,duration:'21600'},24).scope.draft.duration_frames,518400);
  assert.equal(requestSummary(row()),'Ожидает агента · Новый блок: Draft · Intro · Подготовить');
  const improve=row({block_label:'CL-001',scope:{segment_id:'s1',segment_name:'Intro',item_id:'c1',intent:'improve_block'}});
  assert.equal(requestSummary(improve),'Ожидает агента · CL-001 · Intro · Улучшить');
  assert.equal(requestSummary({state:'running',block_label:'CL-001',mode:'apply'}),'В работе · CL-001 · Исправить');
  assert.deepEqual(segmentRequests([row(),{id:'old'},row({id:'other',scope:{segment_id:'s2'}})],'s1').map(r=>r.id),['req-0000000000000001']);
});

test('pending form submission disables controls and creates one keyed durable task without project edits',async t=>{
  const env=setup(t);env.desk.refresh=async()=>env.refreshes++;await env.desk.form({id:'s1'});const form=env.form;fill(form);
  const before=structuredClone(env.p),gate=deferred();env.flush=()=>gate.promise;
  const first=submit(form),second=submit(form);await second;
  assert.equal(form.querySelectorAll().every(c=>c.disabled),true);assert.deepEqual(env.calls,[]);
  gate.resolve();await env.waitCall(1);const body=env.calls[0][2];
  assert.equal(body.expected_revision,7);assert.equal(body.scope.draft.duration_frames,36);assert.equal(body.scope.draft.after_item_id,'c1');assert.deepEqual(body.scope.components,['prompt']);assert.match(body.client_key,/^scenario-client-/);
  env.pending[0].resolve(row());await first;
  assert.equal(env.calls.length,1);assert.equal(env.closed,1);assert.equal(env.refreshes,1);assert.deepEqual(env.p,before);
  assert.equal(form.querySelectorAll().some(c=>c.disabled),false);assert.equal(env.storage.size,0);
});

test('409 preserves complete form draft and rebase fetches fresh revision before same-key retry',async t=>{
  const env=setup(t);env.desk.refresh=async()=>env.refreshes++;await env.desk.form({id:'s1'});const form=env.form;fill(form);
  const failed=submit(form);await env.waitCall(1);const key=env.calls[0][2].client_key;
  env.pending[0].reject(Object.assign(new Error('Conflict'),{status:409}));await failed;
  assert.equal(form.error.textContent,'Conflict');assert.equal(form.rebase.hidden,false);assert.equal(form.elements.title.value,'New shot');assert.equal(form.elements.description.value,'Dodge');assert.equal(form.elements.duration.value,'1.5');
  assert.equal(env.storage.get('studio-scenario-agent-p1-s1-new'),'  Faster shot  ');
  const rebase=form.rebase.onclick();await env.waitCall(2);await form.rebase.onclick();await submit(form);
  assert.deepEqual(env.calls[1],['/api/projects/p1']);assert.equal(env.calls.length,2);
  env.pending[1].resolve({...env.p,revision:9});await rebase;
  const retry=submit(form);await env.waitCall(3);assert.equal(env.calls[2][2].expected_revision,9);assert.equal(env.calls[2][2].client_key,key);
  assert.equal(env.calls[2][2].scope.draft.title,'New shot');env.pending[2].resolve(row());await retry;assert.equal(env.closed,1);
});

test('project switch during submit flush sends nothing and keeps user draft',async t=>{
  const env=setup(t);await env.desk.form({id:'s1'});const form=env.form;fill(form);const gate=deferred();env.flush=()=>gate.promise;
  const pending=submit(form);env.p={...env.p,id:'p2'};gate.resolve();await pending;
  assert.deepEqual(env.calls,[]);assert.equal(env.closed,0);assert.equal(form.elements.goal.value,'  Faster shot  ');assert.equal(form.submit.disabled,false);
});

test('late submit success cannot close replacement form or erase its stored draft',async t=>{
  const env=setup(t);await env.desk.form({id:'s1'});const old=env.form;fill(old);const pending=submit(old);await env.waitCall(1);
  await env.desk.form({id:'s1'});const current=env.form;current.elements.goal.value='Replacement draft';current.elements.goal.oninput();
  env.pending[0].resolve(row());await pending;
  assert.equal(env.form,current);assert.equal(env.closed,0);assert.deepEqual(env.toasts,[]);assert.equal(env.storage.get('studio-scenario-agent-p1-s1-new'),'Replacement draft');
});

test('rebase response cannot replace project or clear draft after navigation',async t=>{
  const env=setup(t);await env.desk.form({id:'s1'});const form=env.form;fill(form);
  const rebase=form.rebase.onclick();await env.waitCall(1);env.p={...env.p,id:'p2',revision:2};env.pending[0].resolve({...project(),revision:99});await rebase;
  assert.equal(env.p.id,'p2');assert.equal(env.p.revision,2);assert.equal(form.elements.goal.value,'  Faster shot  ');
});

test('request refresh ignores older response and prior project response',async t=>{
  const env=setup(t);env.desk.target=env.root;env.desk.sid='s1';
  const old=env.desk.refresh(),fresh=env.desk.refresh();env.pending[1].resolve([row({goal:'NEW'})]);await fresh;
  env.pending[0].resolve([row({goal:'OLD'})]);await old;assert.match(env.list.innerHTML,/NEW/);assert.doesNotMatch(env.list.innerHTML,/OLD/);
  const late=env.desk.refresh();env.p={...env.p,id:'p2'};env.list.innerHTML='PROJECT_TWO';env.pending[2].resolve([row({goal:'OLD_PROJECT'})]);await late;assert.equal(env.list.innerHTML,'PROJECT_TWO');
});

test('segment task cards show escaped results correct live target and terminal actions',async t=>{
  const env=setup(t);env.desk.target=env.root;env.desk.sid='s1';
  const pending=env.desk.refresh();env.pending[0].resolve([row({id:'pending'}),row({id:'done',state:'completed',goal:'<script>goal</script>',result:'<b>done</b>',result_target:{segment_id:'s1',item_id:'c2',block_id:'b2'}}),row({id:'foreign',scope:{segment_id:'s2'}})]);await pending;
  assert.doesNotMatch(env.list.innerHTML,/foreign|<script>|<b>done/);assert.match(env.list.innerHTML,/&lt;b&gt;done/);
  assert.equal(env.list.querySelectorAll('[data-scenario-cancel]').length,1);
  const open=env.list.querySelectorAll('[data-scenario-open]')[0];open.onclick();assert.deepEqual(env.opened,['b2']);
  env.p.timeline.scenario.segments[0].items.pop();open.onclick();assert.equal(env.errors.length,1);assert.deepEqual(env.opened,['b2']);
});

test('copy includes full durable task and cancel re-reads latest revision without GPU actions',async t=>{
  const env=setup(t);env.desk.refresh=async()=>env.refreshes++;
  const copy=env.desk.action('p1','req-test','copy');env.pending[0].resolve(row({id:'req-test',revision:4}));await copy;
  const payload=JSON.parse(env.copied[0]);assert.equal(payload.request_id,'req-test');assert.equal(payload.task.revision,4);assert.match(payload.instructions,/GPU/);
  const cancel=env.desk.action('p1','req-test','cancel');env.pending[1].resolve(row({id:'req-test',revision:5}));await env.waitCall(3);
  assert.deepEqual(env.calls[2],['/api/projects/p1/agent/requests/req-test','POST',{expected_revision:5,action:'cancel',actor:'author'}]);env.pending[2].resolve(row({state:'cancelled'}));await cancel;assert.equal(env.refreshes,1);
});

test('late action response after project switch cannot open a modal or cancel task',async t=>{
  const env=setup(t),pending=env.desk.action('p1','req-test','view');env.p={...env.p,id:'p2'};env.pending[0].resolve(row());await pending;
  assert.deepEqual(env.modals,[]);assert.equal(env.calls.length,1);
});

test('late view action response after leaving selected segment does not open old task',async t=>{
  const env=setup(t);env.desk.target=env.root;env.desk.sid='s1';
  const pending=env.desk.action('p1','req-test','view');env.desk.sid='s2';env.pending[0].resolve(row());await pending;
  assert.deepEqual(env.modals,[]);
});
