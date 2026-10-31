"""Verify and list successful job outputs; make review sheets, never enqueue."""
from pathlib import Path
import argparse,json,hashlib,math,struct
import av
from PIL import Image,ImageDraw,ImageFont

def collect(job):
    job=Path(job).resolve();read=lambda n:json.loads((job/n).read_text(encoding='utf-8-sig'))
    h=read('History.json');assert h['status']['status_str']=='success'
    build=read('Build.json');rt=read('Runtime.json');tele=read('Telemetry.summary.json')
    assert read('Cleanup.json')['stopped'] and read('Submission.json')['submissions']==1
    outputs={}
    for node,values in h['outputs'].items():
        for kind in ['images','latents']:
            for i,row in enumerate(values.get(kind,[])):
                p=(job/'output'/row['subfolder']/row['filename']).resolve()
                assert p.is_relative_to((job/'output').resolve()) and p.is_file()
                outputs[node+'.'+str(i)]={'path':str(p),'bytes':p.stat().st_size,'sha256':hashlib.sha256(p.read_bytes()).hexdigest()}
    assert '200.0' in outputs and '202.0' in outputs
    with open(outputs['200.0']['path'],'rb') as f:
        count=struct.unpack('<Q',f.read(8))[0];header=json.loads(f.read(count))
    assert {'video','audio'}<=header.keys(),'Must save native paired AV latent'
    latent_shapes={k:header[k]['shape'] for k in ['video','audio']}
    font=ImageFont.truetype('C:/Windows/Fonts/consola.ttf',16)
    videos={}
    for node,want in [('92',build['frames']),('207',build['new_frames'])]:
        if node+'.0' not in outputs:continue
        path=outputs[node+'.0']['path']
        with av.open(path) as c:
            fps=float(c.streams.video[0].average_rate);assert fps==24
            assert all(s.type!='audio' for s in c.streams)
            frames=[f.to_image() for f in c.decode(video=0)]
        assert len(frames)==want,(node,len(frames),want)
        canvas=Image.new('RGB',(1920,604),'#101015');d=ImageDraw.Draw(canvas)
        indices=sorted({round(i*(len(frames)-1)/7) for i in range(8)})
        for n,i in enumerate(indices):
            x,y=n%4*480,n//4*302
            canvas.paste(frames[i].resize((480,276),Image.Resampling.LANCZOS),(x,y))
            d.text((x+5,y+278),f'{node} frame {i} / {i/24:.3f}s',font=font,fill='white')
        canvas.save(job/f'Contact.{node}.png')
        videos[node]={'path':path,'frames':len(frames),'fps':fps,'seconds':len(frames)/fps,'silent':True}
    msgs=dict(h['status']['messages']);nodes={n['id']:n for n in rt['nodes']}
    summary={'id':build['id'],'profile':build['profile'],'outputs':outputs,'videos':videos,'latent_shapes':latent_shapes,
       'server_wall_seconds':(msgs['execution_success']['timestamp']-msgs['execution_start']['timestamp'])/1000,
       'solver_seconds':rt['solver'][0]['seconds'],'conditioning_seconds':nodes['136']['seconds'],
       'rss_peak_GiB':tele['process_rss_peak_bytes']/2**30,'device_gpu_peak_GiB':tele['device_gpu_peak_MiB']/1024,
       'sage_success':rt['sage_success'],'sdpa_calls':rt['sdpa_calls'],'all_h3_finite':all(all(n['output_finite']) for n in rt['h3_forwards']),
       'UI_export_verified':(job/'UI.Request.json').exists(),'production_accepted':False}
    assert summary['sage_success']==400 and not summary['sdpa_calls'] and summary['all_h3_finite']
    (job/'Result.json').write_text(json.dumps(summary,indent=2),encoding='utf-8');print(json.dumps(summary,indent=2))

if __name__=='__main__':
    p=argparse.ArgumentParser();p.add_argument('job');a=p.parse_args();collect(a.job)
