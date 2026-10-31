import test from 'node:test';
import assert from 'node:assert/strict';
import {timelineJoins,transitionWindows,transitionLevel,transitionIssue} from '../static/transitions-logic.js';
import {segmentPlayback,criticalSignature} from '../static/playback-logic.js';
import {MontagePlayer} from '../static/montage-player.js';
import {TransitionsDesk} from '../static/transitions-ui.js';
import {timelineCommands} from '../static/timeline-menu.js';
import {TimelineView} from '../static/timeline.js';

const row=(patch={})=>({left_clip_id:'a',right_clip_id:'b',fade_out_frames:2,pause_frames:4,fade_in_frames:2,...patch});
function project(){return {id:'p1',revision:7,fps:8,assets:{v:{bytes:10,modified_ns:1}},
  blocks:[{id:'source',label:'Source',references:[],guides:[],takes:[{id:'take',video_asset_id:'v',video_info:{seconds:4}}]}],
  timeline:{fps:8,segments:[{id:'s1',name:'Left',color:'mint',start_frame:0,duration_frames:8},{id:'s2',name:'Right',color:'blue',start_frame:10,duration_frames:18}],
    clips:[['c',20,'s2'],['a',0,'s1'],['b',10,'s2']].map(([id,start_frame,segment_id])=>({id,start_frame,segment_id,duration_frames:8,source_in_frame:0,block_id:'source',take_id:'take'})),
    music:null,markers:[],clip_audio:false,transitions:[row()],undo:[],redo:[]}};}

test('global join windows and midpoint envelope match exact Python oracles and do not mutate timeline',()=>{
  const t=project().timeline,before=structuredClone(t),joins=timelineJoins(t);
  assert.deepEqual(joins.map(j=>[j.id,j.left.id,j.right.id,j.gap_frames,j.cross_segment]),[['b','a','b',2,true],['c','b','c',2,false]]);
  assert.deepEqual(transitionWindows(t),[{...row(),gap_frames:2,out_start_frame:4,black_start_frame:6,right_start_frame:10,in_end_frame:12}]);
  assert.deepEqual([-1,3,4,5,6,7,8,9,10,11,12,18].map(f=>transitionLevel(t,f)),[1,1,.75,.25,0,0,0,0,.25,.75,1,1]);
  assert.equal(transitionLevel(t,4.8),.75);assert.equal(transitionLevel(t,5.99),.25);
  assert.deepEqual(t,before);assert.deepEqual(timelineJoins({clips:[]}),[]);assert.equal(transitionLevel({clips:[]},0),1);
});

test('transition issue accepts exact gap budget and touching envelopes but refuses adjacent violations',()=>{
  const t=project().timeline;
  for(const valid of [row({fade_out_frames:8,pause_frames:2,fade_in_frames:8}),row({fade_out_frames:0,pause_frames:10,fade_in_frames:0}),row({fade_out_frames:0,pause_frames:2,fade_in_frames:0})])
    assert.equal(transitionIssue({...t,transitions:[valid]}),'');
  for(const invalid of [row({pause_frames:1}),row({fade_out_frames:9,pause_frames:2}),row({fade_out_frames:0,pause_frames:11}),row({fade_in_frames:9})])
    assert.notEqual(transitionIssue({...t,transitions:[invalid]}),'');
  const following=row({left_clip_id:'b',right_clip_id:'c',fade_out_frames:0,pause_frames:8,fade_in_frames:0});
  assert.equal(transitionIssue({...t,transitions:[row(),following]}),'');
  assert.notEqual(transitionIssue({...t,transitions:[row(),{...following,pause_frames:9}]}),'');
});

test('transition issue handles malformed lists rows types duplicate and non-neighbor with useful errors',()=>{
  const t=project().timeline;
  for(const invalid of [null,{},'bad',[null],[{}],[row({left_clip_id:'a',right_clip_id:'c'})],[row(),row()],
    [row({fade_out_frames:-1})],[row({fade_out_frames:true})],[row({pause_frames:4.5})],[row({fade_in_frames:NaN})]]){
    const issue=transitionIssue({...t,transitions:invalid});assert.equal(typeof issue,'string');assert.notEqual(issue,'');
  }
});

test('segment preview preserves compiled global incoming and outgoing cross-segment envelopes',()=>{
  const p=project(),before=structuredClone(p),left=segmentPlayback(p,'s1'),right=segmentPlayback(p,'s2');
  assert.deepEqual(left.project.timeline.clips.map(c=>c.id),['a']);assert.deepEqual(right.project.timeline.clips.map(c=>c.id),['c','b']);
  assert.deepEqual(left.project.timeline.transition_windows,transitionWindows(p.timeline));
  assert.deepEqual(right.project.timeline.transition_windows,transitionWindows(p.timeline));
  assert.equal(transitionLevel(left.project.timeline,4),.75);assert.equal(transitionLevel(left.project.timeline,6),0);
  assert.equal(transitionLevel(right.project.timeline,10),.25);assert.equal(transitionLevel(right.project.timeline,11),.75);
  assert.deepEqual(p,before);
  const original=criticalSignature(p),edited=structuredClone(p);edited.timeline.transitions[0].pause_frames=5;
  assert.notEqual(criticalSignature(edited),original);
  const scoped=right.project,signature=criticalSignature(scoped);scoped.timeline.transition_windows[0].black_start_frame=7;
  assert.notEqual(criticalSignature(scoped),signature);
});

function player(t){
  const p=project(),errors=[],instance=new MontagePlayer({getProject:()=>p,media:id=>'/media/'+id,onError:e=>errors.push(e)});
  instance.syncProject();instance.container={dataset:{}};instance.blackout={style:{},hidden:true};instance.badge={};instance.image={hidden:true};instance.message={hidden:true};instance.showMessage=()=>{};
  instance.videos=[0,1].map(()=>({dataset:{},hidden:true,paused:true,readyState:4,currentTime:0,duration:4,seeking:false,
    play(){this.paused=false;return Promise.resolve();},pause(){this.paused=true;},load(){},addEventListener(){}}));
  t.after(()=>instance.pause());return {instance,p,errors};
}

test('player blackout follows midpoint levels in video and gap while leaving media source rate and audio controls intact',t=>{
  const {instance:p,errors}=player(t);
  p.paint(4/8);assert.equal(p.container.dataset.transitionLevel,'0.750000');assert.equal(p.blackout.style.opacity,'0.250000');assert.equal(p.blackout.hidden,false);
  assert.equal(p.active.currentTime,.5);assert.equal(p.active.playbackRate,1);assert.equal(p.active.muted,true);
  p.paint(8/8);assert.equal(p.container.dataset.kind,'gap');assert.equal(p.blackout.style.opacity,'1.000000');assert.equal(p.active,null);
  p.selectSegment('s2');p.paint(10/8);assert.equal(p.container.dataset.transitionLevel,'0.250000');assert.equal(p.blackout.style.opacity,'0.750000');assert.equal(p.active.currentTime,0);
  p.paint(12/8);assert.equal(p.blackout.hidden,true);assert.equal(p.blackout.style.opacity,'0.000000');assert.deepEqual(errors,[]);
});

function deferred(){let resolve,reject;const promise=new Promise((yes,no)=>{resolve=yes;reject=no;});return {promise,resolve,reject};}
function formSetup(t){
  const env={p:project(),form:null,calls:[],pending:[],closed:0,modals:0};
  const old=Object.getOwnPropertyDescriptor(globalThis,'document');Object.defineProperty(globalThis,'document',{configurable:true,value:{querySelector:()=>env.form}});
  t.after(()=>old?Object.defineProperty(globalThis,'document',old):delete globalThis.document);
  env.flush=async()=>{};
  env.desk=new TransitionsDesk({getProject:()=>env.p,getTimeline:()=>env.p.timeline,flush:()=>env.flush(),
    closeModal:()=>{env.form.isConnected=false;env.form=null;env.closed++;},
    modal:(_title,html)=>{
      env.modals++;const nodes={};for(const id of ['#transitionSummary','#transitionError','button[type="submit"]','#transitionRemove'])
        nodes[id]={disabled:false,textContent:'',removeAttribute(name){if(name==='disabled')this.disabled=false;}};
      if(!html.includes('id="transitionRemove"'))delete nodes['#transitionRemove'];
      const elements={};for(const name of ['out','pause','in'])elements[name]={value:html.match(new RegExp('name="'+name+'"[^>]*value="([^"]*)"'))[1]};
      env.form={elements,isConnected:true,closest:()=>({open:true}),querySelector:s=>nodes[s]??null,
        querySelectorAll:()=>[nodes['button[type="submit"]'],nodes['#transitionRemove']].filter(Boolean)};
    },write:(...args)=>{env.calls.push(args);const gate=deferred();env.pending.push(gate);return gate.promise;}});
  env.join=()=>timelineJoins(env.p.timeline)[0];return env;
}

test('transition form validates seconds against gap and sends one captured revision without changing geometry',async t=>{
  const env=formSetup(t),before=structuredClone(env.p);env.flush=async()=>env.p.revision=8;
  await env.desk.open(env.join());const f=env.form,v=f.elements;
  assert.deepEqual(Object.values(v).map(x=>x.value),['0.25','0.5','0.25']);
  assert.match(f.querySelector('#transitionSummary').textContent,/Затемнение 0\.500–0\.750/);
  v.pause.value='.125';v.pause.oninput();assert.equal(f.querySelector('button[type="submit"]').disabled,true);
  await f.onsubmit({preventDefault(){}});assert.deepEqual(env.calls,[]);
  v.pause.value='.5';v.out.value='.5';v.in.value='.375';v.out.oninput();
  const first=f.onsubmit({preventDefault(){}});await f.onsubmit({preventDefault(){}});
  assert.deepEqual(env.calls,[['set_transition',row({fade_out_frames:4,fade_in_frames:3}),{expectedRevision:8,throwErrors:true}]]);
  assert.equal(f.querySelectorAll().every(b=>b.disabled),true);
  env.pending[0].reject(Object.assign(new Error('Revision conflict'),{status:409}));await first;
  assert.equal(env.form,f);assert.equal(v.in.value,'.375');assert.equal(f.querySelector('#transitionError').textContent,'Revision conflict');assert.equal(env.closed,0);
  env.p.revision=9;const retry=f.onsubmit({preventDefault(){}});assert.equal(env.calls[1][2].expectedRevision,8);
  env.pending[1].resolve(env.p);await retry;assert.equal(env.closed,1);
  assert.deepEqual({...env.p,revision:7},before);
});

test('transition form remove uses right stable id and ignores stale project join and replacement modal',async t=>{
  const env=formSetup(t),gate=deferred();env.flush=()=>gate.promise;
  const opening=env.desk.open(env.join());env.p={...env.p,id:'p2'};gate.resolve();await opening;assert.equal(env.modals,0);
  env.p=project();env.flush=async()=>{};
  await assert.rejects(env.desk.open({left:{id:'a'},right:{id:'c'}}),/Стык изменился/);
  await env.desk.open(env.join());const old=env.form;
  const remove=old.querySelector('#transitionRemove').onclick();
  assert.deepEqual(env.calls[0],['remove_transition',{right_clip_id:'b'},{expectedRevision:7,throwErrors:true}]);
  await env.desk.open(env.join());const replacement=env.form;
  env.pending[0].resolve(env.p);await remove;assert.equal(env.form,replacement);assert.equal(env.closed,0);
  const pending=replacement.onsubmit({preventDefault(){}});env.p={...env.p,id:'p2'};
  env.pending[1].resolve({...env.p,id:'p1'});await pending;assert.equal(env.form,replacement);assert.equal(env.closed,0);
});

test('join menu dispatches only transition remove or seek and keyboard never drags or selects clips',async()=>{
  const t=project().timeline,join=timelineJoins(t)[0],calls=[];
  const view={pending:false,editTransition:j=>calls.push(['edit',j.left.id,j.right.id]),write:(...args)=>calls.push(['write',...args]),onSeek:frame=>calls.push(['seek',frame])};
  const commands=timelineCommands(view,'join',join,{frame:4,revision:7});assert.deepEqual(commands.map(c=>c.id),['transition','remove','jump']);
  for(const command of commands)await command.run();
  assert.deepEqual(calls,[['edit','a','b'],['write','remove_transition',{right_clip_id:'b'},{expectedRevision:7}],['seek',10]]);
  assert.equal(timelineCommands(view,'join',{...join,transition:null},{revision:7}).find(c=>c.id==='remove').disabled,true);
  view.pending=true;assert.equal(timelineCommands(view,'join',join,{revision:7}).every(c=>c.disabled),true);
  const keyView={lookup:()=>join,openContextMenu:()=>calls.push(['menu']),choose:()=>assert.fail('join selected as clip'),write:()=>assert.fail('join geometry edited')};
  for(const [key,shiftKey] of [['Enter',false],[' ',false],['ContextMenu',false],['F10',true],['ArrowLeft',false],['Delete',false]])
    TimelineView.prototype.key.call(keyView,{key,shiftKey,preventDefault(){},stopPropagation(){}},{dataset:{kind:'join',id:'b'}});
  assert.equal(calls.filter(c=>c[0]==='menu').length,4);
});
