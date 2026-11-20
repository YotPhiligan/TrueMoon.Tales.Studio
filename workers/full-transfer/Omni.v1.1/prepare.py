"""Prepare portable Omni UI/API job files. Never starts ComfyUI or enqueues work."""
from pathlib import Path
import argparse,copy,hashlib,json,re,shutil,uuid,struct

AMV=Path(__file__).resolve().parent.parent/'TemplateData'
ROOT=Path(__file__).resolve().parent
COMFY=Path('E:/ComfyUI_windows_portable/ComfyUI')
read=lambda p:json.loads(Path(p).read_text(encoding='utf-8-sig'))
sha=lambda p:hashlib.sha256(Path(p).read_bytes()).hexdigest()
def frames(seconds):
    n=max(5,round(seconds*24));return n+(5-n%17)%17

def prepare(config,destination,stage=False):
    config=Path(config).resolve();dest=Path(destination).resolve();cfg=read(config)
    global COMFY
    COMFY=Path(cfg.get('runtime',{}).get('comfy_root',COMFY)).resolve()
    assert cfg['profile'] in ['omni-guided','omni-context']
    assert re.fullmatch(r'[A-Za-z0-9][A-Za-z0-9._-]{0,80}',cfg['id'])
    assert not dest.exists(),'Destination exists; preserve earlier job'
    assert 1<=len(cfg['references'])<=8
    sec=float(cfg['seconds']);length=frames(sec)
    assert 2<=sec<=8,'v1 bounded source duration: 2-8 seconds'
    width,height=cfg.get('size',[1280,736]);assert width%32==0 and height%32==0
    prompt_path=(config.parent/cfg['prompt']).resolve();prompt=prompt_path.read_text(encoding='utf-8').rstrip('\n')
    fields=['subject_definitions:','summary:','retention_analysis:','detailed_description:','overall_soundscape:','non_diegetic_music:']
    assert all(f in prompt for f in fields) and [prompt.index(f) for f in fields]==sorted(prompt.index(f) for f in fields)
    labels={int(i) for i in re.findall(r'<Picture (\d+)>',prompt)}
    assert labels==set(range(1,len(cfg['references'])+1)),'Prompt Picture labels must match actual ordered inputs'
    assert not re.search(r'<(?:Video|Audio) \d+>',prompt),'v1 production core accepts image references and native AV context; video/audio reference branches remain separate experiments'
    assert prompt.split('overall_soundscape:',1)[1].split('non_diegetic_music:',1)[0].strip()=='N/A'
    assert prompt.split('non_diegetic_music:',1)[1].strip()=='N/A','v1 delivery is silent'
    api=read(AMV/'Research/Results/R-03.ST01.OmniFace.Run01/Prompt.api.json')
    ui=read(AMV/'Clair/Workflows/CL-ST01.OmniFace.v1.json')
    mc_ui=read(AMV/'Clair/Workflows/CL-MC01.B.Context.Omni.v1.json')
    definitions={n['type']:copy.deepcopy(n) for n in ui['nodes']+mc_ui['nodes']}
    retained={k:v for k,v in api.items() if v['class_type'] not in ['LoadImage','MiniMaxH3AddGuide']}
    api=retained
    api['138']['inputs']['value']=prompt;api['132']['inputs']['value']=sec
    api['129']['inputs']['noise_seed']=int(cfg['seed'])
    api['136']['inputs']={k:v for k,v in api['136']['inputs'].items() if not k.startswith('ref_') or k=='ref_image_size'}
    api['136']['inputs'].update(width=width,height=height,ref_image_size='match')
    inputs=[]
    def input_file(source,role):
        p=(config.parent/source).resolve();assert p.is_file(),str(p)
        digest=sha(p);name='TMT.'+cfg['id']+'.'+str(len(inputs)+1)+'.'+digest[:10]+p.suffix.lower()
        target=COMFY/'input'/name
        if stage:
            if target.exists():assert sha(target)==digest,'Preserve conflicting input'
            else:shutil.copy2(p,target)
        inputs.append({'role':role,'Source':str(p),'Target':str(target),'SHA256':digest,'staged':stage})
        return name
    for i,ref in enumerate(cfg['references'],1):
        nid=str(1000+i);api[nid]={'class_type':'LoadImage','inputs':{'image':input_file(ref['path'],ref['role'])}}
        api['136']['inputs'][f'ref_images.ref_image_{i-1}']=[nid,0]
    positive=['136',0]
    guides=cfg.get('guides',[])
    assert 1<=len(guides)<=4,'v1 production profiles require 1-4 scene guides'
    assert len({length-1 if int(g['frame'])==-1 else int(g['frame']) for g in guides})==len(guides),'Duplicate guide time'
    for i,g in enumerate(guides):
        pic=int(g['picture']);idx=int(g['frame']);resolved=length-1 if idx==-1 else idx
        assert 1<=pic<=len(cfg['references']) and 0<=resolved<length
        nid=str(1100+i);api[nid]={'class_type':'MiniMaxH3AddGuide','inputs':{
            'positive':positive,'latent':['136',1],'vae':['119',0],'audio_vae':['120',0],
            'image':[str(1000+pic),0],'frame_idx':idx}}
        positive=[nid,0]
    mc=cfg['profile']=='omni-context'
    if mc:
        assert length>22 and cfg.get('clip_index',2)>1
        prior=(config.parent/cfg['previous_latent']).resolve()
        with prior.open('rb') as f:
            count=struct.unpack('<Q',f.read(8))[0];header=json.loads(f.read(count))
        assert {'video','audio'}<=header.keys(),'Native paired AV latent required; MP4 or video-only latent cannot substitute'
        assert header['video']['shape'][-2:]==[height//16,width//16],'Context spatial dimensions differ from this job'
        latent=input_file(cfg['previous_latent'],'previous exact native AV latent')
        api['203']={'class_type':'MiniMaxH3MotionContextLoadLatent','inputs':{'latent_path':latent,'clip_index':1,'stock_format':False,'folder':'input'}}
        api['204']={'class_type':'MiniMaxH3MotionContext','inputs':{'conditioning':positive,'vae':['119',0],
            'latent':['136',1],'context_length':'22','audio_context_length':18,'context_latent':['203',0]}}
        positive=['204',0]
    api['126']['inputs']['conditioning']=positive
    api['200']={'class_type':'MiniMaxH3MotionContextSaveLatent','inputs':{'latent':['125',0],
        'filename_prefix':'latents/'+cfg['id'],'clip_index':int(cfg.get('clip_index',1))}}
    api['201']={'class_type':'ImageFromBatch','inputs':{'image':['122',0],'batch_index':length-1,'length':1}}
    api['202']={'class_type':'SaveImage','inputs':{'images':['201',0],'filename_prefix':'frames/'+cfg['id']+'.Last'}}
    definitions['ImageFromBatch']=next(copy.deepcopy(n) for n in read(AMV/'Clair/Workflows/CL-MC01.A.Omni.v1.json')['nodes'] if n['type']=='ImageFromBatch')
    definitions['SaveImage']=next(copy.deepcopy(n) for n in read(AMV/'Clair/Workflows/CL-MC01.A.Omni.v1.json')['nodes'] if n['type']=='SaveImage')
    api['92']['inputs']['filename_prefix']='video/'+cfg['id']+'.Raw'
    if mc:
        api['205']={'class_type':'MiniMaxH3MotionContextTrim','inputs':{'images':['122',0],'trim_frames':['204',1],'fps':24.0,'match_tail':True}}
        api['206']=copy.deepcopy(api['130']);api['206']['inputs']['images']=['205',0]
        api['207']=copy.deepcopy(api['92']);api['207']['inputs'].update(video=['206',0],filename_prefix='video/'+cfg['id']+'.New')
    # Rebuild reciprocal links from API, retaining documented native UI widget definitions.
    nodes=[]
    for key,spec in api.items():
        typ=spec['class_type'];n=copy.deepcopy(definitions[typ]);nid=int(key)
        n.update(id=nid,order=nid,mode=0,flags={})
        for p in n.get('inputs',[]):p['link']=None
        for p in n.get('outputs',[]):p['links']=[]
        if typ=='MiniMaxH3ReferenceToVideo':
            src=n['inputs'];n['inputs']=[p for p in src if not p['name'].startswith('ref_') or p['name']=='ref_image_size']
            for i in range(len(cfg['references'])):n['inputs'].insert(3+i,{'name':f'ref_images.ref_image_{i}','type':'IMAGE','link':None})
        values=spec['inputs'];n['widgets_values_named']={k:v for k,v in values.items() if type(v) in [str,int,float,bool]}
        widgets={
          'VAELoader':lambda:[values['vae_name']],
          'UNETLoader':lambda:[values['unet_name'],values['weight_dtype']],
          'CLIPLoader':lambda:[values['clip_name'],values['type'],values['device']],
          'LoadImage':lambda:[values['image'],'image'],
          'PrimitiveStringMultiline':lambda:[values['value']],
          'PrimitiveFloat':lambda:[values['value']],
          'RandomNoise':lambda:[values['noise_seed'],'fixed'],
          'MiniMaxH3ReferenceToVideo':lambda:['',width,height,length,'match'],
          'MiniMaxH3AddGuide':lambda:[values['frame_idx']],
          'MiniMaxH3MotionContextSaveLatent':lambda:[values['filename_prefix'],values['clip_index']],
          'MiniMaxH3MotionContextLoadLatent':lambda:[values['latent_path'],1,False,'input'],
          'MiniMaxH3MotionContext':lambda:['22',18],
          'MiniMaxH3MotionContextTrim':lambda:[22,24.0,True],
          'ImageFromBatch':lambda:[length-1,1],
          'SaveImage':lambda:[values['filename_prefix']],
          'SaveVideo':lambda:[values['filename_prefix'],'auto','auto','auto']}
        if typ in widgets:n['widgets_values']=widgets[typ]()
        if typ=='MiniMaxH3ReferenceToVideo':n['widgets_values_named'].update(prompt='',length=length)
        n['title']=f'{key} / {typ}'
        if typ=='LoadImage':n['title']=f'Picture {nid-1000} / '+cfg['references'][nid-1001]['role']
        nodes.append(n)
    ns={n['id']:n for n in nodes};links=[]
    for key,spec in api.items():
        for name,value in spec['inputs'].items():
            if not isinstance(value,list):continue
            dst=int(key);src,slot=int(value[0]),value[1]
            target_slot=next(i for i,p in enumerate(ns[dst]['inputs']) if p['name']==name)
            lid=len(links)+1;typ=ns[src]['outputs'][slot]['type']
            links.append([lid,src,slot,dst,target_slot,typ]);ns[src]['outputs'][slot]['links'].append(lid);ns[dst]['inputs'][target_slot]['link']=lid
    positions={127:(0,0),128:(0,150),119:(0,330),120:(0,440),138:(720,0),132:(0,600),131:(330,600),143:(0,720),129:(330,720),165:(720,700),123:(720,880),124:(1100,700),126:(1500,700),125:(1900,700),122:(2250,700),130:(2600,700),92:(2950,700),136:(720,1200),200:(2250,1050),201:(2600,1060),202:(2950,1060),203:(0,1700),204:(1100,1700),205:(2250,1450),206:(2600,1450),207:(2950,1450)}
    for nid,n in ns.items():
        n['pos']=list(positions.get(nid,((nid-1001)%4*450,2150+(nid-1001)//4*400)))
        if 1000<nid<1100:n['size']=[400,320]
        if 1100<=nid<1200:n['pos']=[1300+(nid-1100)*430,1200]
        if nid==138:n['size']=[850,620];n['title']='PROMPT / keep Picture roles in this order'
        if nid==92:n['size']=[800,360]
        if nid==207:n['size']=[800,360]
    ui.update(id=str(uuid.uuid4()),revision=0,nodes=nodes,links=links,groups=[],last_node_id=max(ns),last_link_id=len(links))
    ui['extra']={'ds':{'scale':0.45,'offset':[50,50]},'tmt_pipeline':{'version':1,'profile':cfg['profile'],'job':cfg['id'],'silent':True,'new_frames':length-(22 if mc else 0)}}
    dest.mkdir(parents=True)
    for name,data in [('Prepared.api.json',api),('Workflow.ui.json',ui),('Inputs.prepared.json',inputs),('Job.json',cfg)]:
        (dest/name).write_text(json.dumps(data,ensure_ascii=False,indent=2),encoding='utf-8')
    (dest/'Prompt.txt').write_text(prompt,encoding='utf-8')
    record={'version':1,'id':cfg['id'],'profile':cfg['profile'],'config':str(config),'frames':length,'fps':24,'duration_seconds':length/24,
        'new_frames':length-(22 if mc else 0),'context_video_frames':22 if mc else 0,'context_audio_frames':18 if mc else 0,
        'source_input_hashes':inputs,'workflow_sha256':sha(dest/'Workflow.ui.json'),'api_sha256':sha(dest/'Prepared.api.json'),
        'prompt_sha256':sha(dest/'Prompt.txt'),'models':'Omni NVFP4 QAD / Qwen NVFP4 AWQ / existing VAEs','staged':stage,'generation_executed':False}
    record['prepare_script_sha256']=sha(__file__)
    record['template_sha256']={str(p):sha(p) for p in [AMV/'Research/Results/R-03.ST01.OmniFace.Run01/Prompt.api.json',AMV/'Clair/Workflows/CL-ST01.OmniFace.v1.json',AMV/'Clair/Workflows/CL-MC01.B.Context.Omni.v1.json',AMV/'Clair/Workflows/CL-MC01.A.Omni.v1.json']}
    (dest/'Build.json').write_text(json.dumps(record,indent=2),encoding='utf-8')
    print(json.dumps({'prepared':str(dest),'frames':length,'new_frames':record['new_frames'],'staged':stage,'enqueued':False}))

if __name__=='__main__':
    p=argparse.ArgumentParser();p.add_argument('config');p.add_argument('destination');p.add_argument('--stage',action='store_true');args=p.parse_args()
    prepare(args.config,args.destination,args.stage)
