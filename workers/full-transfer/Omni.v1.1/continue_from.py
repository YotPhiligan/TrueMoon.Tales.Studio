"""Create a continuation config from successful native AV save + lossless PNG."""
from pathlib import Path
import argparse,json,copy,hashlib

def make(previous,destination,job_id,prompt,picture):
    previous=Path(previous).resolve();destination=Path(destination).resolve()
    assert not destination.exists(),'Preserve existing continuation config'
    read=lambda n:json.loads((previous/n).read_text(encoding='utf-8-sig'))
    history=read('History.json');assert history['status']['status_str']=='success'
    cfg=read('Job.json');assert 1<=picture<=len(cfg['references'])
    def output(node,key):
        row=history['outputs'][node][key][0]
        p=(previous/'output'/row['subfolder']/row['filename']).resolve()
        assert p.is_relative_to((previous/'output').resolve()) and p.is_file()
        return p
    latent=output('200','latents');last=output('202','images')
    cfg=copy.deepcopy(cfg)
    # Resolve original relative assets before changing the config's parent directory.
    original=Path(read('Build.json')['config']).parent
    for ref in cfg['references']:ref['path']=str((original/ref['path']).resolve())
    cfg.update(id=job_id,profile='omni-context',previous_latent=str(latent),clip_index=cfg.get('clip_index',1)+1,
        prompt=str(Path(prompt).resolve()),seed=cfg['seed']+1,guides=[{'picture':picture,'frame':0}])
    cfg['references'][picture-1]={'role':'lossless previous boundary / same Picture role','path':str(last)}
    destination.parent.mkdir(parents=True,exist_ok=True)
    destination.write_text(json.dumps(cfg,ensure_ascii=False,indent=2),encoding='utf-8')
    evidence={'previous_job':str(previous),'latent':str(latent),'latent_sha256':hashlib.sha256(latent.read_bytes()).hexdigest(),
        'last_frame':str(last),'last_frame_sha256':hashlib.sha256(last.read_bytes()).hexdigest(),
        'lossless_before_MP4':True,'anchor_picture':picture,'new_config':str(destination),'generation_executed':False}
    destination.with_suffix('.Handoff.json').write_text(json.dumps(evidence,indent=2),encoding='utf-8')
    print(json.dumps(evidence))

if __name__=='__main__':
    p=argparse.ArgumentParser();p.add_argument('previous');p.add_argument('destination');p.add_argument('--id',required=True);p.add_argument('--prompt',required=True);p.add_argument('--anchor-picture',type=int,required=True);a=p.parse_args()
    make(a.previous,a.destination,a.id,a.prompt,a.anchor_picture)
