"""Bounded CPU audio analysis. Derived waveform only; never changes the source."""
from __future__ import annotations
import json,math,os,subprocess,sys,time
from pathlib import Path

VERSION=1
RATE=16000
PEAKS_PER_SECOND=100
MAX_SECONDS=3600

class AudioError(ValueError):pass

def analyse(path,stream_index=0):
    import av
    import numpy as np
    started=time.monotonic();peaks=[];pending=np.empty(0,dtype=np.float32);samples=0;channels=0
    with av.open(str(path)) as container:
        if not container.streams.audio:raise AudioError('Файл не содержит звуковой дорожки.')
        if type(stream_index) is not int or not 0<=stream_index<len(container.streams.audio):raise AudioError('Звуковой поток не найден.')
        stream=container.streams.audio[stream_index];source_rate=stream.rate
        resampler=av.AudioResampler(format='fltp',layout=stream.layout,rate=RATE)
        def consume(frame):
            nonlocal pending,samples,channels
            values=frame.to_ndarray();channels=values.shape[0]
            # Max across channels retains opposite-phase stereo transients.
            envelope=np.max(np.abs(np.nan_to_num(values,nan=0,posinf=1,neginf=-1)),axis=0)
            samples+=len(envelope)
            if samples>MAX_SECONDS*RATE:raise AudioError('Для предпросмотра выберите звук длиной до одного часа.')
            pending=np.concatenate((pending,envelope));hop=RATE//PEAKS_PER_SECOND;n=len(pending)//hop*hop
            if n:peaks.extend(np.max(pending[:n].reshape(-1,hop),axis=1).clip(0,1).round(5).tolist());pending=pending[n:]
        for frame in container.decode(audio=stream_index):
            if time.monotonic()-started>40:raise AudioError('Анализ звука превысил 40 секунд.')
            for converted in resampler.resample(frame):consume(converted)
        for converted in resampler.resample(None):consume(converted)
        if len(pending):peaks.append(round(float(np.max(pending).clip(0,1)),5))
    if not samples:raise AudioError('Звуковая дорожка пуста.')
    return {'version':VERSION,'seconds':samples/RATE,'sample_rate':RATE,'source_sample_rate':source_rate,
            'channels':channels,'peaks_per_second':PEAKS_PER_SECOND,'peaks':peaks,'analysis_seconds':round(time.monotonic()-started,4)}

def analyse_bounded(path,stream_index=0):
    try:
        result=subprocess.run([sys.executable,'-s','-B',str(Path(__file__).resolve()),str(Path(path).resolve())]+([str(stream_index)] if stream_index else []),
            capture_output=True,text=True,encoding='utf-8',errors='replace',env={**os.environ,'PYTHONIOENCODING':'utf-8'},timeout=45,
            creationflags=getattr(subprocess,'CREATE_NO_WINDOW',0))
    except subprocess.TimeoutExpired:raise AudioError('Анализ звука остановлен по лимиту 45 секунд.')
    if result.returncode:raise AudioError('Не удалось прочитать звук. Проверьте формат и целостность файла.')
    try:data=json.loads(result.stdout)
    except ValueError:raise AudioError('Анализатор звука вернул некорректный результат.')
    if not valid(data):raise AudioError('Анализатор звука вернул некорректную волну.')
    return data

def valid(data):
    return (isinstance(data,dict) and type(data.get('version')) is int and data['version']==VERSION and type(data.get('seconds')) in [int,float]
        and math.isfinite(data['seconds']) and 0<data['seconds']<=MAX_SECONDS and data.get('peaks_per_second')==PEAKS_PER_SECOND
        and data.get('sample_rate')==RATE and type(data.get('channels')) is int and 1<=data['channels']<=64
        and isinstance(data.get('peaks'),list) and 0<len(data['peaks'])<=MAX_SECONDS*PEAKS_PER_SECOND+1
        and len(data['peaks'])==math.ceil(data['seconds']*PEAKS_PER_SECOND-1e-8)
        and all(type(n) in [int,float] and math.isfinite(n) and 0<=n<=1 for n in data['peaks']))

if __name__=='__main__':
    try:print(json.dumps(analyse(sys.argv[1],int(sys.argv[2]) if len(sys.argv)>2 else 0),separators=(',',':')))
    except Exception as error:print(type(error).__name__+': '+str(error),file=sys.stderr);sys.exit(1)
