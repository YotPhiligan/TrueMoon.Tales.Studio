"""Isolated Studio fixtures. Never import or start ComfyUI."""
import copy,json,struct,sys,tempfile,unittest
from pathlib import Path

UI=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(UI))
from studio.model import Store,atomic,digest,join_prompt,read
from studio.jobs import Jobs

class Fixture(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory(prefix='studio-tests-')
        self.addCleanup(self.temp.cleanup)
        self.base=Path(self.temp.name);self.book=self.base/'Book';self.amv=self.book/'TrueMoonTales/AMV';self.amv.mkdir(parents=True)
        self.store=Store(self.book,self.amv,self.base/'state')
        self.project=self.store.create('Тестовый проект',project_id='fixture')
        self.pid=self.project['id'];seg=self.project['segments'][0]['id']
        self.project=self.store.operation(self.pid,self.project['revision'],'add_block',{'segment_id':seg,'label':'CL-T01'})
        self.bid=self.project['blocks'][0]['id'];p=self.store.get(self.pid);b=p['blocks'][0]
        self.plan=self.store.location(self.pid).parents[1]/'Plan.md';self.plan.write_text('Авторский план\n',encoding='utf-8');self.plan_digest=digest(self.plan)
        self.images=[]
        for i in range(3):
            image=self.book/f'Assets/ref-{i+1}.png';image.parent.mkdir(exist_ok=True)
            # File contents are never passed to a model/decoder in contract tests.
            image.write_bytes(b'\x89PNG\r\n\x1a\nfixture'+bytes([i]));self.images.append(image)
            b['references'].append({'id':f'ref-{i+1}','asset_id':self.store.asset(p,image),'role':['character','location','pose'][i]})
        self.initial_prompt=join_prompt({'subject_definitions':'alpha <Picture 1>; beta <Picture 2>; gamma <Picture 3>',
            'summary':'[reference generation + keyframe completion] One guarded step.',
            'retention_analysis':'One character and scene fully_preserved.',
            'detailed_description':'Painterly anime. [Shot 1] Move right, keep a compact guard.',
            'overall_soundscape':'N/A','non_diegetic_music':'N/A'})
        b.update(title='Исходный блок',prompt=self.initial_prompt,guides=[{'reference_id':'ref-3','frame':0}])
        self.project=self.store.commit(p,p['revision'],'fixture','Fixture ready')
        self.core=self.base/'core';self.core.mkdir();(self.core/'prepare.py').write_text('# command seam only');(self.core/'run.ps1').write_text('# never executed')
        self.jobs=Jobs(self.store,python=Path(sys.executable),core=self.core)

    def current(self):return self.store.get(self.pid)
    def block(self,p=None):return self.store.find_block(p or self.current(),self.bid)
    def save(self,data):
        p=self.current();return self.store.save_block(self.pid,self.bid,data,p['revision'])
    def add_take(self,header=None):
        p=self.current();b=self.block(p);folder=self.book/'Results';folder.mkdir(exist_ok=True)
        video=folder/'take.mp4';video.write_bytes(bytes(range(256)))
        last=folder/'last.png';last.write_bytes(self.images[0].read_bytes())
        latent=folder/'take.safetensors';header=header or {'video':{'shape':[1,24,17,32,56]},'audio':{'shape':[1,32,2,93]}}
        encoded=json.dumps(header).encode();latent.write_bytes(struct.pack('<Q',len(encoded))+encoded)
        take={'id':'take-fixture','name':'Fixture take','video_asset_id':self.store.asset(p,video),'last_frame_asset_id':self.store.asset(p,last),
            'native_av_asset_id':self.store.asset(p,latent),'review_state':'unreviewed','notes':'','technical_status':'success','created_at':'2026-10-30T00:00:00Z'}
        b['takes'].append(take);self.store.commit(p,p['revision'],'fixture','Add take');return take,latent
    def fake_prepare(self,args,directory,log,timeout=90):
        cfg=read(args[1]);dest=Path(args[2]);dest.mkdir()
        rows=[{'Source':r['path'],'SHA256':digest(r['path'])} for r in cfg['references']]
        if cfg.get('previous_latent'):rows.append({'Source':cfg['previous_latent'],'SHA256':digest(cfg['previous_latent'])})
        atomic(dest/'Build.json',{'source_input_hashes':rows})
    def prepared(self):
        from unittest.mock import patch
        p=self.current()
        with patch.object(self.jobs,'command',side_effect=self.fake_prepare):return self.jobs.prepare(self.pid,self.bid,p['revision'])
