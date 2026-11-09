"""Bounded, streaming CPU renderer. Invoked only by the explicit export action."""
from pathlib import Path
import fractions,hashlib,json,math,subprocess,sys,time,traceback

sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
from studio.model import atomic,digest
from studio.export_plan import DRAFT_ENCODING,parts,source_position
from studio.transitions import brightness
from studio.titles import TitleCompositor
from studio.paths import check_export_path


class DraftRenderer:
    def __init__(self,folder):
        self.folder=Path(folder).resolve();self.request=json.loads((self.folder/'Request.json').read_text(encoding='utf-8'))
        self.book=Path(self.request['book'])  # Logical legacy namespace; the frozen policy guards actual IO.
        check_export_path(self.request,self.folder,write=True)
        self.started=time.monotonic();self.deadline=self.started+self.request['timeout_seconds'];self.last_progress=0
        self.encoding = self.request.get('encoding')
        if self.encoding is None:
            if self.request.get('profile') == 'final':
                raise ValueError('В снимке финального экспорта нет параметров кодирования.')
            self.encoding = DRAFT_ENCODING
        self.fps=self.request['fps'];self.frames=self.request['frames'];self.size=self.request['size'];self.samples=round(self.frames/self.fps*48000)
    def check(self):
        if time.monotonic()>self.deadline:raise TimeoutError('Экспорт остановлен по лимиту времени.')
    def progress(self,stage,percent,force=False):
        self.check()
        if force or time.monotonic()-self.last_progress>.5:
            atomic(self.folder/'Progress.json',{'stage':stage,'percent':round(percent,1),'elapsed_seconds':round(time.monotonic()-self.started,2)})
            self.last_progress=time.monotonic()
    def inputs(self):
        result={}
        for aid,a in self.request['assets'].items():
            self.check();path=Path(a['path']).resolve()
            check_export_path(self.request,path)
            if not path.is_file():raise ValueError('Материал экспорта недоступен: '+str(path))
            result[aid]=digest(path)
            if a.get('expected_sha256') and result[aid]!=a['expected_sha256']:raise ValueError('Слой титра изменился после подготовки экспорта.')
        return result
    def command(self,args):
        self.check()
        with (self.folder/'FFmpeg.log').open('ab') as log:
            p=subprocess.run([self.ffmpeg,'-hide_banner','-loglevel','error','-nostdin','-filter_threads','1',*args],stdout=log,stderr=log,
                             timeout=max(.1,self.deadline-time.monotonic()),creationflags=getattr(subprocess,'CREATE_NO_WINDOW',0))
        if p.returncode:raise RuntimeError('Ошибка FFmpeg. Подробности сохранены в FFmpeg.log.')
    def video(self):
        import av,numpy as np
        from PIL import Image
        output=av.open(str(self.folder/'Video.mp4'),'w');stream=output.add_stream(self.encoding['video_codec'],rate=self.fps)
        stream.width,stream.height=self.size;stream.pix_fmt=self.encoding['pixel_format'];stream.codec_context.thread_count=2
        stream.codec_context.max_b_frames=0;stream.options={'preset':self.encoding['preset'],'crf':str(self.encoding['crf'])}
        black=np.zeros((self.size[1],self.size[0],3),dtype=np.uint8)
        titles=TitleCompositor(self.request)
        try:
            for part in parts(self.request):
                clip=part['clip'];decoder=SourceFrames(self.request['assets'][clip['asset_id']]['path'],clip['source_in_frame']/self.fps) if clip else None
                try:
                    cached=None;pixels=black
                    for index in range(part['start_frame'],part['start_frame']+part['duration_frames']):
                        self.check()
                        if decoder:
                            source=decoder.at(source_position(clip,index,self.fps));key=(source.pts,source.time_base)
                            if key!=cached:
                                image=source.to_image();scale=min(self.size[0]/image.width,self.size[1]/image.height)
                                w=max(1,round(image.width*scale));h=max(1,round(image.height*scale));image=image.resize((w,h),Image.Resampling.BILINEAR)
                                canvas=Image.new('RGB',tuple(self.size));canvas.paste(image,((self.size[0]-w)//2,(self.size[1]-h)//2));pixels=np.asarray(canvas);cached=key
                        level=brightness(index,self.request.get('transition_windows',[]))
                        display=pixels if level==1 else black if level==0 else np.floor(pixels.astype(np.float32)*level+.5).clip(0,255).astype(np.uint8)
                        display=titles.compose(display,index)
                        frame=av.VideoFrame.from_ndarray(display,format='rgb24');frame.pts=index;frame.time_base=fractions.Fraction(1,self.fps)
                        for packet in stream.encode(frame):output.mux(packet)
                        self.progress('Изображение',5+70*(index+1)/self.frames)
                finally:
                    if decoder:decoder.close()
            for packet in stream.encode(None):output.mux(packet)
        finally:output.close()
    def audio_parts(self):
        import av
        files=[];all_parts=parts(self.request)
        for index,part in enumerate(all_parts):
            self.progress('Звук клипов',75+15*index/max(1,len(all_parts)),True)
            clip=part['clip'];name=f'Audio-{index:04}.wav';files.append(name);target=round((part['start_frame']+part['duration_frames'])/self.fps*48000)-round(part['start_frame']/self.fps*48000)
            path=self.request['assets'][clip['asset_id']]['path'] if clip and not clip.get('mute_native_audio') else None
            has_audio=False
            if path:
                with av.open(path) as source:has_audio=bool(source.streams.audio)
            if has_audio:
                rate=clip['speed'];tempos=[]
                while rate<.5:tempos.append('atempo=0.5');rate/=.5
                while rate>2:tempos.append('atempo=2');rate/=2
                tempos.append(f'atempo={rate:.12g}')
                filters=f"atrim=start={clip['source_in_frame']/self.fps:.12f}:end={clip['source_out_frame']/self.fps:.12f},asetpts=PTS-STARTPTS,"+','.join(tempos)+f',aresample=48000,apad,atrim=end_sample={target}'
                args=['-threads','2','-i',path,'-map','0:a:0','-vn','-af',filters]
            else:args=['-f','lavfi','-i','anullsrc=r=48000:cl=stereo','-af',f'atrim=end_sample={target}']
            self.command([*args,'-ar','48000','-ac','2','-c:a','pcm_s16le','-n',str(self.folder/name)])
        (self.folder/'Audio.concat.txt').write_text(''.join("file '"+name+"'\n" for name in files),encoding='utf-8')
    def mux(self):
        args=['-threads','2','-i',str(self.folder/'Video.mp4')];labels=[];filters=[]
        native=self.request['clip_audio'];music=self.request['music'];input_index=1
        if native:
            args+=['-f','concat','-safe','1','-i',str(self.folder/'Audio.concat.txt')]
            filters.append(f'[{input_index}:a]apad,atrim=end_sample={self.samples},asetpts=PTS-STARTPTS[native]');labels.append('[native]');input_index+=1
        if music and not music['muted'] and music['gain']>0:
            args+=['-i',self.request['assets'][music['asset_id']]['path']]
            filters.append(f"[{input_index}:a]atrim=start={music['source_in_frame']/self.fps:.12f}:duration={music['duration_frames']/self.fps:.12f},asetpts=PTS-STARTPTS,aresample=48000,aformat=channel_layouts=stereo,volume={music['gain']:.12g},adelay={round(music['start_frame']/self.fps*48000)}S:all=1,apad,atrim=end_sample={self.samples}[music]")
            labels.append('[music]')
            input_index+=1
        for i,e in enumerate(self.request.get('sounds',[])):
            if e['muted'] or e['gain']==0:continue
            args+=['-i',self.request['assets'][e['asset_id']]['path']]
            label=f'event{i}'
            filters.append(f"[{input_index}:a:{e['stream']}]atrim=start={e['source_in_frame']/self.fps:.12f}:duration={e['duration_frames']/self.fps:.12f},asetpts=PTS-STARTPTS,aresample=48000,aformat=channel_layouts=stereo,volume={e['gain']:.12g},adelay={round(e['start_frame']/self.fps*48000)}S:all=1,apad,atrim=end_sample={self.samples}[{label}]")
            labels.append(f'[{label}]');input_index+=1
        if not labels:filters.append(f'anullsrc=r=48000:cl=stereo,atrim=end_sample={self.samples}[mixed]')
        elif len(labels)==1:filters.append(labels[0]+'anull[mixed]')
        else:filters.append(''.join(labels)+f'amix=inputs={len(labels)}:duration=longest:normalize=0[mixed]')
        # The mix already contains all delays and padding. Timestamp its PCM samples continuously.
        filters.append('[mixed]asetpts=N/SR/TB[sound]')
        self.command([*args,'-filter_complex_threads','1','-filter_complex',';'.join(filters),'-map','0:v:0','-map','[sound]',
                      '-c:v','copy','-c:a',self.encoding['audio_codec'],'-b:a',f"{self.encoding['audio_bitrate_kbps']}k",'-ar','48000','-ac','2','-t',f'{self.frames/self.fps:.12f}',
                      '-movflags','+faststart','-n',str(self.folder/'Draft.mp4')])
    def run(self):
        import av,imageio_ffmpeg
        self.ffmpeg=imageio_ffmpeg.get_ffmpeg_exe();self.progress('Проверка материалов',0,True);hashes=self.inputs()
        atomic(self.folder/'InputHashes.json',hashes)
        self.video()
        if self.request['clip_audio']:self.audio_parts()
        self.progress('Сборка MP4',91,True);self.mux();self.progress('Проверка результата',97,True)
        if self.inputs()!=hashes:raise ValueError('Материалы изменились во время экспорта. Результат не утверждён.')
        with av.open(str(self.folder/'Draft.mp4')) as result:
            stream=result.streams.video[0];count=sum(1 for _ in result.decode(video=0))
            if count!=self.frames or float(stream.average_rate)!=self.fps:raise ValueError('Число кадров или FPS результата не совпадает с таймлайном.')
            info={'frames':count,'fps':self.fps,'seconds':count/self.fps,'width':stream.width,'height':stream.height,'audio':bool(result.streams.audio)}
        self.progress('Готово',100,True)
        return {'state':'completed','output':'Draft.mp4','sha256':digest(self.folder/'Draft.mp4'),'video_info':info,'elapsed_seconds':round(time.monotonic()-self.started,3)}


class SourceFrames:
    """Keep only the current and next decoded frames; select the frame containing t."""
    def __init__(self,path,start):
        import av
        self.container=av.open(path);self.stream=self.container.streams.video[0];self.stream.thread_type='SLICE';self.stream.codec_context.thread_count=2
        self.origin=(self.container.start_time or 0)/av.time_base
        if start>0:self.container.seek(max(0,int((start+self.origin)/self.stream.time_base)),stream=self.stream,backward=True)
        self.decoder=iter(self.container.decode(self.stream));self.current=next(self.decoder);self.next=next(self.decoder,None)
    def at(self,seconds):
        while self.next is not None and float(self.next.time)-self.origin<=seconds+1e-8:
            self.current=self.next;self.next=next(self.decoder,None)
        return self.current
    def close(self):self.container.close()


if __name__=='__main__':
    folder=Path(sys.argv[1]).resolve()
    renderer=DraftRenderer(folder)  # Validate write policy before even writing an error result.
    try:atomic(folder/'Result.json',renderer.run())
    except Exception as error:
        traceback.print_exc();atomic(folder/'Result.json',{'state':'failed','error':str(error) or type(error).__name__});sys.exit(1)
