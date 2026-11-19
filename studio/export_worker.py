"""Bounded, streaming CPU renderer. Invoked only by the explicit export action."""
from pathlib import Path
import fractions,hashlib,json,math,stat,subprocess,sys,time,traceback

sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
from studio.model import atomic,digest
from studio.export_plan import DRAFT_ENCODING,parts,source_position
from studio.transitions import brightness
from studio.titles import TitleCompositor
from studio.paths import check_export_path


class DraftRenderer:
    def __init__(self,folder,*,request_data=None):
        self.folder=Path(folder).resolve()
        self.request=json.loads(request_data if request_data is not None else (self.folder/'Request.json').read_text(encoding='utf-8'))
        self.backend_mode=request_data is not None
        self.book=Path(self.request['book'])  # Logical legacy namespace; the frozen policy guards actual IO.
        check_export_path(self.request,self.folder,write=True)
        self.started=time.monotonic();self.deadline=self.started+self.request['timeout_seconds'];self.last_progress=0
        self.encoding = self.request.get('encoding')
        if self.encoding is None:
            if self.request.get('profile') == 'final':
                raise ValueError('В снимке финального экспорта нет параметров кодирования.')
            self.encoding = DRAFT_ENCODING
        self.fps=self.request['fps'];self.frames=self.request['frames'];self.size=self.request['size']
        self.start_frame=self.request.get('start_frame',0)
        self.start_sample=self.sample(self.start_frame)
        self.samples=self.sample(self.start_frame+self.frames)-self.start_sample
    def sample(self,frame):
        return round(frame/self.fps*48000)
    def check(self):
        if time.monotonic()>self.deadline:raise TimeoutError('Экспорт остановлен по лимиту времени.')
    def progress(self,stage,percent,force=False):
        self.check()
        if force or time.monotonic()-self.last_progress>.5:
            atomic(self.folder/'Progress.json',{'stage':stage,'percent':round(percent,1),'elapsed_seconds':round(time.monotonic()-self.started,2)})
            self.last_progress=time.monotonic()
    def inputs(self):
        result={}
        by_path={}
        for aid,a in self.request['assets'].items():
            self.check();path=self.input_path(a)
            if not path.is_file():raise ValueError('Материал экспорта недоступен: '+str(path))
            if path not in by_path:by_path[path]=digest(path)
            result[aid]=by_path[path]
            expected=a.get('expected_sha256')
            if (self.backend_mode or expected) and result[aid]!=expected:
                raise ValueError('Материал изменился после подготовки экспорта.' if self.backend_mode
                                 else 'Слой титра изменился после подготовки экспорта.')
        return result
    def input_path(self,asset):
        path=Path(asset['path'])
        if self.backend_mode:
            if not path.is_absolute():raise ValueError('Expected a resolved export input.')
            for entry in (path,*path.parents):
                metadata=entry.lstat()
                if stat.S_ISLNK(metadata.st_mode) or getattr(metadata,'st_file_attributes',0)&stat.FILE_ATTRIBUTE_REPARSE_POINT:
                    raise ValueError('Linked export inputs are forbidden.')
        return check_export_path(self.request,path)
    def input_format(self,asset):
        if not self.backend_mode:return None,{}
        container=asset.get('format')
        if container not in {'mov','matroska','avi','wav','mp3','flac','ogg','aac'}:
            raise ValueError('Unsupported export input container.')
        options={'protocol_whitelist':'file,pipe'}
        if container=='mov':options.update(enable_drefs='0',use_absolute_path='0')
        return container,options
    def input_arguments(self,asset):
        container,options=self.input_format(asset)
        args=['-f',container] if container else []
        for name,value in options.items():args.extend(['-'+name,value])
        return [*args,'-i',str(self.input_path(asset))]
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
                clip=part['clip'];decoder=None
                if clip:
                    asset=self.request['assets'][clip['asset_id']]
                    container,options=self.input_format(asset)
                    decoder=SourceFrames(str(self.input_path(asset)),source_position(clip,part['start_frame'],self.fps),
                                         container=container,options=options)
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
                        frame=av.VideoFrame.from_ndarray(display,format='rgb24');frame.pts=index-self.start_frame;frame.time_base=fractions.Fraction(1,self.fps)
                        for packet in stream.encode(frame):output.mux(packet)
                        self.progress('Изображение',5+70*(index-self.start_frame+1)/self.frames)
                finally:
                    if decoder:decoder.close()
            for packet in stream.encode(None):output.mux(packet)
        finally:output.close()
    def audio_parts(self):
        import av
        files=[];all_parts=parts(self.request)
        for index,part in enumerate(all_parts):
            self.progress('Звук клипов',75+15*index/max(1,len(all_parts)),True)
            clip=part['clip'];name=f'Audio-{index:04}.wav';files.append(name)
            start=self.sample(part['start_frame'])
            target=self.sample(part['start_frame']+part['duration_frames'])-start
            asset=self.request['assets'][clip['asset_id']] if clip and not clip.get('mute_native_audio') else None
            has_audio=False
            if asset:
                container,options=self.input_format(asset)
                with av.open(str(self.input_path(asset)),format=container,options=options) as source:
                    has_audio=bool(source.streams.audio)
            if has_audio:
                rate=clip['speed'];tempos=[]
                while rate<.5:tempos.append('atempo=0.5');rate/=.5
                while rate>2:tempos.append('atempo=2');rate/=2
                if rate!=1 or not self.backend_mode:tempos.append(f'atempo={rate:.12g}')
                offset=start-self.sample(clip['start_frame'])
                crop=(f'atrim=start_sample={offset}:end_sample={offset+target},asetpts=PTS-STARTPTS'
                      if offset else f'atrim=end_sample={target}')
                filters=','.join([f"atrim=start={clip['source_in_frame']/self.fps:.12f}:end={clip['source_out_frame']/self.fps:.12f}",
                                  'asetpts=PTS-STARTPTS',*tempos,'aresample=48000','apad',crop])
                args=['-threads','2',*self.input_arguments(asset),'-map','0:a:0','-vn','-af',filters]
            else:args=['-f','lavfi','-i','anullsrc=r=48000:cl=stereo','-af',f'atrim=end_sample={target}']
            self.command([*args,'-ar','48000','-ac','2','-c:a','pcm_s16le','-n',str(self.folder/name)])
        (self.folder/'Audio.concat.txt').write_text(''.join("file '"+name+"'\n" for name in files),encoding='utf-8')
    def mix_track(self,track,input_label,output_label):
        offset=self.sample(track['start_frame'])-self.start_sample
        crop=f'atrim=start_sample={-offset},asetpts=PTS-STARTPTS,' if offset<0 else ''
        return (f"{input_label}atrim=start={track['source_in_frame']/self.fps:.12f}:duration={track['duration_frames']/self.fps:.12f},"
                f"asetpts=PTS-STARTPTS,aresample=48000,aformat=channel_layouts=stereo,volume={track['gain']:.12g},"
                f"{crop}adelay={max(0,offset)}S:all=1,"
                f"apad,atrim=end_sample={self.samples}{output_label}")
    def mux(self):
        args=['-threads','2','-i',str(self.folder/'Video.mp4')];labels=[];filters=[]
        native=self.request['clip_audio'];music=self.request['music'];input_index=1
        if native:
            args+=['-f','concat','-safe','1','-i',str(self.folder/'Audio.concat.txt')]
            filters.append(f'[{input_index}:a]apad,atrim=end_sample={self.samples},asetpts=PTS-STARTPTS[native]');labels.append('[native]');input_index+=1
        if music and not music['muted'] and music['gain']>0:
            args+=self.input_arguments(self.request['assets'][music['asset_id']])
            filters.append(self.mix_track(music,f'[{input_index}:a]','[music]'))
            labels.append('[music]')
            input_index+=1
        for i,e in enumerate(self.request.get('sounds',[])):
            if e['muted'] or e['gain']==0:continue
            args+=self.input_arguments(self.request['assets'][e['asset_id']])
            label=f'event{i}'
            filters.append(self.mix_track(e,f"[{input_index}:a:{e['stream']}]",f'[{label}]'))
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
    def __init__(self,path,start,*,container=None,options=None):
        import av
        self.container=av.open(path,format=container,options=options);self.stream=self.container.streams.video[0];self.stream.thread_type='SLICE';self.stream.codec_context.thread_count=2
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
