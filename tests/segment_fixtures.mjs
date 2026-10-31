export function segmentProject(active=false){
  const p={id:'p1',revision:7,fps:24,assets:{song:{bytes:100,modified_ns:1},video:{bytes:200,modified_ns:1}},
    blocks:[{id:'b1',label:'Source',title:'Source',takes:[{id:'take',video_asset_id:'video',video_info:{seconds:4}}]},
      {id:'b2',label:'Future',title:'Future',takes:[]}],
    timeline:{version:1,fps:24,segments:[{id:'s1',name:'Chosen',color:'mint',start_frame:48,duration_frames:72},
      {id:'s2',name:'Other',color:'blue',start_frame:192,duration_frames:24}],
      clips:[{id:'c1',segment_id:'s1',block_id:'b1',take_id:'take',start_frame:60,duration_frames:24,source_in_frame:24},
        {id:'c2',segment_id:'s2',block_id:'b2',take_id:null,start_frame:192,duration_frames:24,source_in_frame:0}],
      music:{id:'music',asset_id:'song',start_frame:0,duration_frames:240,source_in_frame:12,gain:.8,muted:false},
      markers:[{id:'marker',name:'Beat',frame:90}],clip_audio:false,undo:[],redo:[]}};
  if(active)p.timeline.scenario={version:1,segments:[
    {...p.timeline.segments[0],description:'',items:[{id:'c1',block_id:'b1',title:'Planned source',description:'',duration_frames:48,gap_before_frames:12,take_id:'take',source_in_frame:24}]},
    {...p.timeline.segments[1],description:'',items:[{id:'c2',block_id:'b2',title:'Unknown future',description:'',duration_frames:null,gap_before_frames:0,take_id:null,source_in_frame:0}]}]};
  return p;
}
