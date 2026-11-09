import test from 'node:test';
import assert from 'node:assert/strict';
import {comparisonRows,stepTime,videoTime} from '../static/take-compare.js';
import {replacementInterval} from '../static/clip-replacement.js';

test('replacement fits the exact retimed source span while keeping the montage window',()=>{
  const clip={id:'c1',take_id:'old',start_frame:96,duration_frames:7,source_in_frame:2.25,source_out_frame:10.375,speed:1.25};
  assert.deepEqual(replacementInterval(clip,'new',5,14),
    {id:'c1',take_id:'new',source_in_frame:5,source_out_frame:13.125,speed:1.25});
  assert.throws(()=>replacementInterval(clip,'new',5,12),/кадров: 1/);
  const blank={id:'c2',take_id:null,duration_frames:24,source_in_frame:0};
  assert.deepEqual(replacementInterval(blank,'new',0,24),
    {id:'c2',take_id:'new',source_in_frame:0,source_out_frame:24,speed:1});
});

test('shared frame step follows the chosen source grid from a fractional position',()=>{
  const a=stepTime(.5,24,1,4);
  assert.equal(a,13/24);
  assert.equal(stepTime(a,30,1,4),17/30);
  assert.equal(stepTime(a,30,-1,4),16/30);
  assert.equal(stepTime(a,24,-1,4),.5);
  assert.equal(stepTime(0,24,-1,4),0);
  assert.equal(videoTime(3,2),1.999);
});

test('comparison preserves exact seed text and compares reference content, roles and guides',()=>{
  const reference={file:'reference.png',role:'character',source_key:'source',sha256:'hash',guides:[0]};
  const a={seed:'18446744073709551613',references:[reference]};
  const b={seed:'18446744073709551614',references:[{...reference,guides:[-1]}]};
  assert.deepEqual(comparisonRows(a,b).find(row=>row.name==='Seed'),{
    name:'Seed',left:a.seed,right:b.seed,status:'different'
  });
  const picture=(left,right)=>comparisonRows(left,right).find(row=>row.name==='Picture 1');
  assert.equal(picture(a,b).status,'different');
  assert.equal(picture(a,{references:[{...reference,source_key:'renamed'}]}).status,'same');
  assert.equal(picture(a,{references:[]}).status,'different');
  assert.equal(picture(a,{}).status,'unknown');
});
