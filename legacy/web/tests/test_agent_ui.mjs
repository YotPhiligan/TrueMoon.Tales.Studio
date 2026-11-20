import test from 'node:test';
import assert from 'node:assert/strict';
import {AgentDesk,requestSummary,shouldRefreshEditor} from '../static/agent-ui.js';

function deferred(){let resolve;const promise=new Promise(yes=>{resolve=yes;});return {promise,resolve};}
function setup(t){
  const env={project:{id:'p1',revision:7},block:{id:'b1',label:'CL-001'},calls:[],pending:[],toasts:[],modals:[],apiCalled:deferred()};
  env.elements={
    '#agentTasks':{innerHTML:'INITIAL',querySelectorAll:()=>[]},
    '#agentContextBox':{innerHTML:'CONTEXT_INITIAL'},
    '#agentRequest':{value:'Improve dynamics'},'#agentMode':{value:'propose'},'#agentScope':{value:'block'},
    '#createAgentTask':{disabled:false},
  };
  const prior=Object.getOwnPropertyDescriptor(globalThis,'document');
  Object.defineProperty(globalThis,'document',{configurable:true,value:{querySelector:key=>env.elements[key]??null}});
  t.after(()=>prior?Object.defineProperty(globalThis,'document',prior):delete globalThis.document);
  env.desk=new AgentDesk({getProject:()=>env.project,getBlock:()=>env.block,flush:async()=>{},
    api:(...args)=>{env.calls.push(args);const gate=deferred();env.pending.push(gate);env.apiCalled.resolve();return gate.promise;},
    toast:message=>env.toasts.push(message),modal:(...args)=>env.modals.push(args),onError:error=>{throw error;}});
  return env;
}

test('request summary names every lifecycle state mode and project or block scope',()=>{
  const names={pending:'Ожидает агента',running:'В работе',completed:'Завершено',failed:'Ошибка',cancelled:'Отменено'};
  for(const [state,label] of Object.entries(names)){
    assert.equal(requestSummary({state,block_label:'CL-001',mode:'propose'}),`${label} · CL-001 · Предложить`);
  }
  assert.equal(requestSummary({state:'running',block_label:null,mode:'apply'}),'В работе · Весь проект · Исправить');
  assert.equal(requestSummary({state:'custom',mode:'propose'}),'custom · Весь проект · Предложить');
});

test('editor refresh requires changed revision and no dirty save conflict timeline work or drag',()=>{
  const ready={project:{revision:7},dirty:false,savePromise:null,conflict:false,timelinePending:false,drag:null};
  assert.equal(shouldRefreshEditor(ready,7),false);
  assert.equal(shouldRefreshEditor(ready,8),true);
  for(const [key,value] of [['dirty',true],['savePromise',Promise.resolve()],['conflict',true],['timelinePending',1],['drag',{id:'clip'}]]){
    assert.equal(shouldRefreshEditor({...ready,[key]:value},8),false,key);
  }
  const startedDraft={...ready,dirty:true};
  assert.equal(shouldRefreshEditor(ready,8),true);
  assert.equal(shouldRefreshEditor(startedDraft,8),false,'rechecking after fetch must preserve draft started during fetch');
});

test('request list rejects an older response that arrives after a newer refresh',async t=>{
  const env=setup(t),target=env.elements['#agentTasks'];
  const first=env.desk.refresh(),second=env.desk.refresh();
  env.pending[1].resolve([{id:'new',state:'running',goal:'NEW_RESULT',mode:'propose'}]);await second;
  assert.match(target.innerHTML,/NEW_RESULT/);
  env.pending[0].resolve([{id:'old',state:'pending',goal:'OLD_RESULT',mode:'propose'}]);await first;
  assert.match(target.innerHTML,/NEW_RESULT/);assert.doesNotMatch(target.innerHTML,/OLD_RESULT/);
  assert.deepEqual(env.calls,[['/api/projects/p1/agent/requests'],['/api/projects/p1/agent/requests']]);
});

test('request list ignores response after project switch even when target node remains',async t=>{
  const env=setup(t),target=env.elements['#agentTasks'];
  const pending=env.desk.refresh();env.project={id:'p2',revision:1};target.innerHTML='PROJECT_TWO';
  env.pending[0].resolve([{id:'old',state:'pending',goal:'OLD_PROJECT',mode:'propose'}]);await pending;
  assert.equal(target.innerHTML,'PROJECT_TWO');
});

test('request list does not write a detached target after inspector is remounted',async t=>{
  const env=setup(t),old=env.elements['#agentTasks'];
  const pending=env.desk.refresh();env.elements['#agentTasks']={innerHTML:'NEW_NODE',querySelectorAll:()=>[]};
  env.pending[0].resolve([{id:'old',state:'pending',goal:'DETACHED_RESULT',mode:'propose'}]);await pending;
  assert.equal(old.innerHTML,'INITIAL');assert.equal(env.elements['#agentTasks'].innerHTML,'NEW_NODE');
});

test('request list escapes agent supplied text attributes owner and result',async t=>{
  const env=setup(t),pending=env.desk.refresh();
  env.pending[0].resolve([{id:'\"><img src=x>',state:'pending',goal:'<script>goal</script>',mode:'apply',block_label:'<b>CL</b>',owner:'<u>owner</u>',result:'<iframe>result</iframe>'}]);
  await pending;
  const html=env.elements['#agentTasks'].innerHTML;
  assert.doesNotMatch(html,/<script>|<iframe>|<img|<u>owner/);
  assert.match(html,/&lt;script&gt;goal&lt;\/script&gt;/);assert.match(html,/&lt;u&gt;owner&lt;\/u&gt;/);
  assert.match(html,/&lt;iframe&gt;result&lt;\/iframe&gt;/);assert.match(html,/&quot;&gt;&lt;img/);
});

test('create aborted during flush on project switch sends no task and re-enables button',async t=>{
  const env=setup(t),flush=deferred();env.desk.flush=()=>flush.promise;
  const pending=env.desk.create();assert.equal(env.elements['#createAgentTask'].disabled,true);
  env.project={id:'p2',revision:1};flush.resolve();await pending;
  assert.deepEqual(env.calls,[]);assert.deepEqual(env.toasts,[]);assert.equal(env.elements['#createAgentTask'].disabled,false);
});

test('task created for prior project does not toast or refresh current project on late response',async t=>{
  const env=setup(t);let refreshed=0;env.desk.refresh=async()=>{refreshed++;};
  const pending=env.desk.create();await env.apiCalled.promise;
  assert.deepEqual(env.calls[0],['/api/projects/p1/agent/requests','POST',{expected_revision:7,goal:'Improve dynamics',mode:'propose',block_id:'b1',actor:'author'}]);
  env.project={id:'p2',revision:1};env.pending[0].resolve({id:'req-old'});await pending;
  assert.deepEqual(env.toasts,[]);assert.equal(refreshed,0);assert.equal(env.elements['#createAgentTask'].disabled,false);
});

test('context response for a previously selected block does not overwrite current inspector',async t=>{
  const env=setup(t),target=env.elements['#agentContextBox'];
  const pending=env.desk.showContext();await env.apiCalled.promise;
  env.block={id:'b2',label:'CL-002'};env.pending[0].resolve({block:{id:'b1'},brief:{synopsis:'OLD_CONTEXT'}});await pending;
  assert.equal(target.innerHTML,'CONTEXT_INITIAL');
  assert.deepEqual(env.calls,[['/api/projects/p1/agent/context?block=b1']]);
  env.apiCalled=deferred();const fresh=env.desk.showContext();await env.apiCalled.promise;
  env.pending[1].resolve({block:{id:'b2'},brief:{synopsis:'</textarea><script>data</script>'}});await fresh;
  assert.match(target.innerHTML,/b2/);assert.doesNotMatch(target.innerHTML,/OLD_CONTEXT|<script>/);
  assert.match(target.innerHTML,/&lt;\/textarea&gt;&lt;script&gt;/);
});
