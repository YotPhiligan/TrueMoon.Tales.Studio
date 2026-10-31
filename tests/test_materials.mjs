import test from 'node:test';
import assert from 'node:assert/strict';
import {nextGuideFrame,guidePatch,removeReferencePatch,referenceOrder,guideName,MaterialsDesk} from '../static/materials.js';

function block(){return {id:'b1',settings:{seconds:2,mode:'guided'},references:[
  {id:'r1',asset_id:'a1',role:'Character'},{id:'r2',asset_id:'a2',role:'Location'},{id:'r3',asset_id:'a3',role:'Pose'}],
  guides:[{reference_id:'r1',frame:0},{reference_id:'r3',frame:-1}],prompt:'A <Picture 1>; B <Picture 2>; C <Picture 3>'};}
function deferred(){let resolve;const promise=new Promise(yes=>{resolve=yes;});return {promise,resolve};}

test('guide frame preference is first last middle interior with normalized last and four-guide cap',()=>{
  const b=block();b.guides=[];
  for(const expected of [0,-1,28,1]){assert.equal(nextGuideFrame(b),expected);Object.assign(b,guidePatch(b,'r1',expected));}
  assert.equal(nextGuideFrame(b),null);
  b.guides=[{reference_id:'r1',frame:0},{reference_id:'r2',frame:55}];assert.equal(nextGuideFrame(b),28);
  for(const [seconds,middle,last] of [[5,62,123],[8,96,191]]){
    b.settings.seconds=seconds;b.guides=[{reference_id:'r1',frame:0},{reference_id:'r2',frame:last}];
    assert.equal(nextGuideFrame(b),middle);assert.equal(guidePatch(b,'r3',middle).guides.at(-1).frame,middle);
  }
  b.settings={seconds:5,mode:'context'};b.guides=[{reference_id:'r1',frame:0},{reference_id:'r2',frame:-1}];
  assert.equal(nextGuideFrame(b),62,'guide indices use full source frames rather than trimmed context output');
  b.settings.seconds=1;assert.equal(nextGuideFrame(b),null);
  assert.deepEqual([guideName(0,56),guideName(-1,56),guideName(55,56),guideName(28,56)],['Первый кадр','Последний кадр','Последний кадр','Кадр 28']);
});

test('guide patch appends or replaces only guide data and leaves references prompt and input immutable',()=>{
  const b=block(),before=structuredClone(b);
  const added=guidePatch(b,'r2',28);assert.deepEqual(Object.keys(added),['guides']);
  assert.deepEqual(added.guides,[...before.guides,{reference_id:'r2',frame:28}]);
  const replaced=guidePatch(b,'r2',55,1);
  assert.deepEqual(replaced.guides,[{reference_id:'r1',frame:0},{reference_id:'r2',frame:55}]);
  assert.deepEqual(b,before);assert.notEqual(replaced.guides[0],b.guides[0]);
});

test('guide patch refuses duplicate last aliases invalid frame reference index and fifth guide',()=>{
  const b=block(),before=structuredClone(b);
  for(const [reference,frame,index] of [['missing',28,null],['r2',-2,null],['r2',56,null],['r2',true,null],
    ['r2',.5,null],['r2','0',null],['r2',NaN,null],['r2',55,null],['r2',-1,null],['r2',28,-1],['r2',28,2]]){
    assert.throws(()=>guidePatch(b,reference,frame,index),Error);
  }
  assert.deepEqual(b,before);
  b.guides=[{reference_id:'r1',frame:0},{reference_id:'r2',frame:-1},{reference_id:'r3',frame:28},{reference_id:'r3',frame:1}];
  assert.throws(()=>guidePatch(b,'r2',2),Error);
  assert.equal(guidePatch(b,'r2',2,3).guides[3].frame,2);
});

test('reference removal renumbers pictures and removes only linked guides without mutating input',()=>{
  const b=block();b.guides.splice(1,0,{reference_id:'r2',frame:28});const before=structuredClone(b);
  const patch=removeReferencePatch(b,'r2');
  assert.deepEqual(patch.references.map(r=>r.id),['r1','r3']);
  assert.deepEqual(patch.guides,[{reference_id:'r1',frame:0},{reference_id:'r3',frame:-1}]);
  assert.equal(patch.prompt,'A <Picture 1>; B [удалённый референс]; C <Picture 2>');
  assert.throws(()=>removeReferencePatch(b,'missing'),Error);assert.deepEqual(b,before);
  assert.equal(removeReferencePatch(b,'r1').prompt,'A [удалённый референс]; B <Picture 1>; C <Picture 2>');
  assert.equal(removeReferencePatch(b,'r3').prompt,'A <Picture 1>; B <Picture 2>; C [удалённый референс]');
});

test('reference move returns full stable-id permutation and rejects edge or invalid direction',()=>{
  const b=block(),before=structuredClone(b);
  assert.deepEqual(referenceOrder(b,'r2',-1),['r2','r1','r3']);assert.deepEqual(referenceOrder(b,'r2',1),['r1','r3','r2']);
  for(const [id,direction] of [['r1',-1],['r3',1],['missing',1],['r2',0],['r2',2],['r2','1']])assert.throws(()=>referenceOrder(b,id,direction),Error);
  assert.deepEqual(b,before);
});

function setup(){
  const env={b:block(),p:{id:'p1',revision:7,assets:{a1:{id:'a1',name:'Character.png'},a2:{id:'a2',name:'Location.png'},a3:{id:'a3',name:'Pose.png'}}},
    calls:[],applied:[],uploads:[],chooses:[],errors:[],edits:0,refreshes:0,dirty:false,apiGate:deferred(),sent:deferred()};
  const root={nodes:new Map(),html:'',classes:new Set(),
    set innerHTML(html){this.html=html;this.nodes.clear();},get innerHTML(){return this.html;},
    node(key,properties={}){if(!this.nodes.has(key))this.nodes.set(key,{dataset:{},...properties});return this.nodes.get(key);},
    querySelector(key){return this.node(key);},
    querySelectorAll(selector){
      if(selector==='[data-role]')return env.b.references.map(r=>this.node(`[data-role="${r.id}"]`,{dataset:{role:r.id},value:r.role}));
      if(selector==='[data-remove]')return env.b.references.map(r=>this.node(`[data-remove="${r.id}"]`,{dataset:{remove:r.id}}));
      if(selector==='[data-move]')return env.b.references.flatMap(r=>[-1,1].map(d=>this.node(`[data-move="${r.id}"][data-direction="${d}"]`,{dataset:{move:r.id,direction:String(d)}})));
      if(selector==='[data-guide-ref],[data-guide-frame]')return env.b.guides.flatMap((g,i)=>[
        this.node(`[data-guide-ref="${i}"]`,{dataset:{guideRef:String(i)},value:g.reference_id}),
        this.node(`[data-guide-frame="${i}"]`,{dataset:{guideFrame:String(i)},value:String(g.frame)})]);
      if(selector==='[data-delete-guide]')return env.b.guides.map((g,i)=>this.node(`[data-delete-guide="${i}"]`,{dataset:{deleteGuide:String(i)}}));
      return [];
    }};
  root.classList={add:c=>root.classes.add(c),remove:c=>root.classes.delete(c)};env.root=root;
  env.desk=new MaterialsDesk({root,getProject:()=>env.p,getBlock:()=>env.b,media:id=>'/media/'+id,
    edit:fn=>{env.edits++;fn(env.b);},refresh:()=>env.refreshes++,chooseUpload:flag=>env.chooses.push(flag),
    uploadFiles:async(files,options)=>env.uploads.push([files,options]),onError:error=>env.errors.push(error),modal:()=>{},
    flush:async()=>{},api:(...args)=>{env.calls.push(args);env.sent.resolve();return env.apiGate.promise;},
    onProject:p=>env.applied.push(p),isDirty:()=>env.dirty});
  return env;
}

test('material tabs expose separate controls and route upload or drop with explicit guide intent',()=>{
  const env=setup(),files=[{name:'fixture.png'}];env.desk.render('references');
  assert.match(env.root.innerHTML,/id="uploadReferences"/);assert.doesNotMatch(env.root.innerHTML,/id="addGuide"|id="uploadGuide"/);
  env.root.querySelector('#uploadReferences').onclick();env.root.ondrop({preventDefault(){},dataTransfer:{files}});
  assert.deepEqual(env.chooses,[false]);assert.deepEqual(env.uploads,[[files,{asGuide:false}]]);assert.equal(env.edits,0);
  env.desk.render('guides');assert.match(env.root.innerHTML,/id="addGuide"/);assert.doesNotMatch(env.root.innerHTML,/id="uploadReferences"/);
  env.root.querySelector('#uploadGuide').onclick();env.root.ondrop({preventDefault(){},dataTransfer:{files}});
  assert.deepEqual(env.chooses,[false,true]);assert.deepEqual(env.uploads[1],[files,{asGuide:true}]);assert.equal(env.edits,0);
  for(let i=4;i<=8;i++)env.b.references.push({id:'r'+i,asset_id:'a1',role:'Extra'});
  env.desk.render('references');assert.match(env.root.innerHTML,/id="uploadReferences"[^>]*disabled/);
  env.desk.render('guides');assert.match(env.root.innerHTML,/id="uploadGuide"[^>]*disabled/);assert.doesNotMatch(env.root.innerHTML,/id="addGuide"[^>]*disabled/);
  env.b.guides.push({reference_id:'r2',frame:28},{reference_id:'r4',frame:1});env.desk.render('guides');
  assert.match(env.root.innerHTML,/id="addGuide"[^>]*disabled/);
  env.b.references=[];env.b.guides=[];env.desk.render('guides');
  assert.match(env.root.innerHTML,/id="addGuide"[^>]*disabled/);assert.doesNotMatch(env.root.innerHTML,/id="uploadGuide"[^>]*disabled/);
});

test('role edits do not alter guides and explicit guide add delete do not alter references',()=>{
  const env=setup(),beforeGuides=structuredClone(env.b.guides);env.desk.render('references');
  const role=env.root.querySelector('[data-role="r1"]');role.value='Updated role';role.oninput();
  assert.equal(env.b.references[0].role,'Updated role');assert.deepEqual(env.b.guides,beforeGuides);
  const refs=structuredClone(env.b.references),prompt=env.b.prompt;env.desk.render('guides');
  env.root.querySelector('#addGuide').onclick();assert.deepEqual(env.b.guides.at(-1),{reference_id:'r3',frame:28});
  env.root.querySelector('[data-delete-guide="0"]').onclick();assert.deepEqual(env.b.guides.map(g=>g.frame),[-1,28]);
  assert.deepEqual(env.b.references,refs);assert.equal(env.b.prompt,prompt);assert.equal(env.refreshes,2);
});

test('guide row changes validate aliases before patching and render safely escaped names and roles',()=>{
  const env=setup();env.p.assets.a1.name='<script>filename</script>';env.b.references[0].role='"><img src=x>';
  env.desk.render('references');assert.doesNotMatch(env.root.innerHTML,/<script>|<img src=x>/);
  assert.match(env.root.innerHTML,/&lt;script&gt;filename/);assert.match(env.root.innerHTML,/&quot;&gt;&lt;img/);
  env.desk.render('guides');const before=structuredClone(env.b.guides);
  const frame=env.root.querySelector('[data-guide-frame="1"]');frame.value='0';frame.onchange();
  assert.equal(env.errors.length,1);assert.deepEqual(env.b.guides,before);
  const updated=env.root.querySelector('[data-guide-frame="1"]');updated.value='55';updated.onchange();
  assert.equal(env.b.guides[1].frame,55);assert.equal(env.b.references.length,3);assert.equal(env.refreshes,1);
});

test('reference remove control cleans linked guides and prompt then refreshes without deleting source assets',()=>{
  const env=setup();env.b.guides.splice(1,0,{reference_id:'r2',frame:28});const assets=structuredClone(env.p.assets);
  env.desk.render('references');env.root.querySelector('[data-remove="r2"]').onclick();
  assert.deepEqual(env.b.references.map(r=>r.id),['r1','r3']);assert.deepEqual(env.b.guides.map(g=>g.reference_id),['r1','r3']);
  assert.equal(env.b.prompt,'A <Picture 1>; B [удалённый референс]; C <Picture 2>');assert.equal(env.refreshes,1);
  assert.deepEqual(env.p.assets,assets);
});

test('reference move flushes then uses current revision and existing reorder operation without local rewrite',async()=>{
  const env=setup(),flush=deferred();env.desk.flush=()=>flush.promise;env.p.id='project space';
  const pending=env.desk.move('r2',-1);assert.deepEqual(env.calls,[]);env.p.revision=8;flush.resolve();await env.sent.promise;
  assert.deepEqual(env.calls,[['/api/projects/project%20space/operations','POST',{expected_revision:8,operation:'reorder_references',payload:{block_id:'b1',order:['r2','r1','r3']},actor:'author'}]]);
  const result={...env.p,revision:9};env.apiGate.resolve(result);await pending;
  assert.deepEqual(env.applied,[result]);assert.equal(env.refreshes,1);assert.equal(env.edits,0);
});

test('reference move cancels before send if project or block changes during flush',async()=>{
  for(const change of [env=>env.p={...env.p,id:'p2'},env=>env.b={...env.b,id:'b2'}]){
    const env=setup(),flush=deferred();env.desk.flush=()=>flush.promise;
    const pending=env.desk.move('r2',1);change(env);flush.resolve();await pending;
    assert.deepEqual(env.calls,[]);assert.deepEqual(env.applied,[]);assert.equal(env.refreshes,0);
  }
});

test('late reorder response cannot replace a switched or newly dirty block',async()=>{
  for(const kind of ['project','block','dirty']){
    const env=setup(),pending=env.desk.move('r2',1);await env.sent.promise;
    if(kind==='project')env.p={...env.p,id:'p2'};else if(kind==='block')env.b={...env.b,id:'b2'};else env.dirty=true;
    env.apiGate.resolve({id:'p1',revision:8});
    if(kind==='dirty')await assert.rejects(pending,/Перестановка сохранена/);else await pending;
    assert.deepEqual(env.applied,[]);assert.equal(env.refreshes,0);assert.equal(env.calls.length,1);
  }
});
