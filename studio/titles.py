"""Shared Cyrillic raster layers and project-frame title composition. No video generation."""
import hashlib,json,math,os,re
from collections import OrderedDict
from pathlib import Path
from . import timeline as tl
from .model import atomic,digest,read,uid,StudioError

FIELDS=('name','text','start_frame','duration_frames','fade_in_frames','fade_out_frames','font_id','font_size','color','align','position','background','outline')
RASTER_FIELDS=('text','font_id','font_size','color','align','position','outline')
RASTER_VERSION=1

def font_paths():
    base=Path(os.environ.get('WINDIR','C:/Windows'))/'Fonts'
    specs=[('arial','Arial','arial.ttf'),('arial-bold','Arial · полужирный','arialbd.ttf'),('georgia','Georgia','georgia.ttf'),
           ('times','Times New Roman','times.ttf'),('consolas','Consolas','consola.ttf'),('segoe','Segoe UI','segoeui.ttf')]
    rows=[{'id':i,'label':label,'path':base/name} for i,label,name in specs if (base/name).is_file()]
    fallback=Path('/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf')
    if not rows and fallback.is_file():rows=[{'id':'dejavu','label':'DejaVu Sans','path':fallback}]
    return rows

def catalog():return [{k:r[k] for k in ['id','label']} for r in font_paths()]

def resolve_font(font_id):
    if not isinstance(font_id,str):raise tl.TimelineError('Выберите доступный шрифт.')
    row=next((r for r in font_paths() if r['id']==font_id),None)
    if not row:raise tl.TimelineError('Выбранный шрифт недоступен.')
    return row['path']

def defaults(fps=24):
    fonts=catalog()
    return {'name':'Титр','text':'Название','start_frame':0,'duration_frames':fps*3,'fade_in_frames':round(fps*.25),'fade_out_frames':round(fps*.25),
            'font_id':fonts[0]['id'] if fonts else 'arial','font_size':56,'color':'#FFFFFF','align':'center','position':'center','background':'transparent','outline':True}

def new_row(payload,fps=24,old=None,identifier=None):
    row={**(old or defaults(fps)),**{k:v for k,v in payload.items() if k in FIELDS},'id':old['id'] if old else identifier or tl.identifier('title-')}
    if isinstance(row['text'],str):row['text']=row['text'].replace('\r\n','\n').replace('\r','\n').replace('\t','    ')
    return row

def validate(project,t):
    rows=t.get('titles',[])
    if not isinstance(rows,list) or len(rows)>64:raise tl.TimelineError('Допускается до 64 титров.')
    seen=set();edges=[]
    for row in rows:
        if not isinstance(row,dict) or not set(('id',*FIELDS))<=row.keys():raise tl.TimelineError('Некорректные поля титра.')
        if not isinstance(row['id'],str) or not re.fullmatch(r'[A-Za-z0-9_-]{1,80}',row['id']) or row['id'] in seen:raise tl.TimelineError('Некорректный или повторяющийся ID титра.')
        seen.add(row['id']);tl.text(row['name'],'название титра')
        text=row['text']
        if not isinstance(text,str) or not text.strip() or len(text)>1000 or text.count('\n')>11 or any(ord(c)<32 and c!='\n' for c in text):raise tl.TimelineError('Текст титра: до 1000 символов и 12 строк, без управляющих символов.')
        tl.integer(row['start_frame'],'Начало титра');tl.integer(row['duration_frames'],'Длина титра',1)
        for key in ['fade_in_frames','fade_out_frames']:tl.integer(row[key],'Плавность титра')
        if tl.end(row)>tl.MAX_FRAME or row['fade_in_frames']+row['fade_out_frames']>row['duration_frames']:raise tl.TimelineError('Появление и исчезновение должны помещаться в длительность титра.')
        if type(row['font_size']) is not int or not 8<=row['font_size']<=256:raise tl.TimelineError('Размер шрифта должен быть от 8 до 256.')
        resolve_font(row['font_id'])
        if not isinstance(row['color'],str) or not re.fullmatch(r'#[0-9A-Fa-f]{6}',row['color']):raise tl.TimelineError('Некорректный цвет титра.')
        for key,allowed in [('align',['left','center','right']),('position',['upper','center','lower']),('background',['transparent','black'])]:
            if not isinstance(row[key],str) or row[key] not in allowed:raise tl.TimelineError('Некорректное оформление титра.')
        if type(row['outline']) is not bool:raise tl.TimelineError('Обводка задаётся переключателем.')
        edges.extend([(row['start_frame'],1),(tl.end(row),-1)])
    count=0
    for _,delta in sorted(edges):
        count+=delta
        if count>4:raise tl.TimelineError('Одновременно допускается до четырёх титров.')

def apply(t,action,payload):
    rows=list(t.get('titles',[]))
    if action=='add_title':rows.append(new_row(payload,t['fps']))
    else:
        row=tl.find(rows,payload.get('id'),'Титр');index=rows.index(row)
        if action=='remove_title':rows.pop(index)
        else:rows[index]=new_row(payload,t['fps'],row)
    t['titles']=rows

def opacity(row,frame):
    start=row['start_frame'];end=tl.end(row)
    if not start<=frame<end:return 0
    if row['fade_in_frames'] and frame<start+row['fade_in_frames']:return (frame-start+.5)/row['fade_in_frames']
    if row['fade_out_frames'] and frame>=end-row['fade_out_frames']:return (end-frame-.5)/row['fade_out_frames']
    return 1

def _wrap(text,font,draw,width,stroke):
    lines=[]
    for hard in text.split('\n'):
        if not hard:lines.append('');continue
        line=''
        for word in hard.split(' '):
            candidate=(line+' '+word) if line else word
            if draw.textlength(candidate,font=font)+2*stroke<=width:line=candidate;continue
            if line:lines.append(line);line=''
            for letter in word:
                if line and draw.textlength(line+letter,font=font)+2*stroke>width:lines.append(line);line=''
                line+=letter
        lines.append(line)
    return '\n'.join(lines)

def render_layer(store,project,row):
    from PIL import Image,ImageDraw,ImageFont
    validate(project,{'titles':[row]})
    size=project['output_size']
    if not isinstance(size,list) or len(size)!=2 or any(type(n) is not int or not 2<=n<=4096 for n in size):raise tl.TimelineError('Некорректный размер кадра проекта.')
    font_path=resolve_font(row['font_id']);fingerprint=digest(font_path)
    descriptor={'version':RASTER_VERSION,'canvas_size':size,'font_sha256':fingerprint,**{k:row[k] for k in RASTER_FIELDS}}
    key=hashlib.sha256(json.dumps(descriptor,sort_keys=True,ensure_ascii=False).encode('utf-8')).hexdigest()
    folder=store.cache_dir(project['id'],'Titles');png=folder/(key+'.png');meta=folder/(key+'.json')
    with store.title_lock:
        layer=None
        if png.is_file() and meta.is_file():
            try:
                cached=read(meta)
                if cached.get('key')==key and digest(png)==cached.get('sha256'):layer=cached
            except (OSError,ValueError):pass
        if layer is None:
            font=ImageFont.truetype(str(font_path),row['font_size']);draw=ImageDraw.Draw(Image.new('RGBA',(1,1)));stroke=max(1,round(row['font_size']/28)) if row['outline'] else 0
            wrapped=_wrap(row['text'],font,draw,size[0]*.84,stroke);spacing=max(1,round(row['font_size']*.25))
            box=draw.multiline_textbbox((0,0),wrapped,font=font,spacing=spacing,align=row['align'],stroke_width=stroke)
            box=tuple(math.floor(v) if i<2 else math.ceil(v) for i,v in enumerate(box))
            width=max(1,box[2]-box[0]);height=max(1,box[3]-box[1])
            if width>size[0]*.9 or height>size[1]*.84:raise tl.TimelineError('Текст не помещается в кадр. Уменьшите шрифт или сократите текст.')
            x=round(size[0]*.08) if row['align']=='left' else round(size[0]*.92-width) if row['align']=='right' else round((size[0]-width)/2)
            y=round(size[1]*.08) if row['position']=='upper' else round(size[1]*.92-height) if row['position']=='lower' else round((size[1]-height)/2)
            image=Image.new('RGBA',(width,height));ImageDraw.Draw(image).multiline_text((-box[0],-box[1]),wrapped,font=font,spacing=spacing,align=row['align'],fill=row['color'],stroke_width=stroke,stroke_fill='#000000')
            folder.mkdir(parents=True,exist_ok=True);temp=folder/(uid('title-')+'.pending');image.save(temp,format='PNG')
            if digest(font_path)!=fingerprint:raise tl.TimelineError('Шрифт изменился во время подготовки титра.')
            temp.replace(png);layer={'key':key,'sha256':digest(png),'canvas_size':size,'x':x,'y':y,'width':width,'height':height,'font_id':row['font_id'],'font_sha256':fingerprint,'wrapped_text':wrapped}
            atomic(meta,layer)
    return {**layer,'id':row['id'],'background':row['background'],'path':str(png),'url':f"/api/projects/{project['id']}/title-images/{key}"}

class TitleCompositor:
    def __init__(self,request):self.request=request;self.layers={r['id']:r for r in request.get('title_layers',[])};self.cache=OrderedDict()
    def _image(self,row):
        import numpy as np
        from PIL import Image
        layer=self.layers[row['id']];key=row['id']
        if key not in self.cache:
            path=Path(self.request['assets'][layer['asset_id']]['path']).resolve()
            from .paths import check_export_path
            check_export_path(self.request,path)
            cw,ch=layer['canvas_size'];ow,oh=self.request['size'];scale=min(ow/cw,oh/ch)
            with Image.open(path) as im:rgba=np.asarray(im.convert('RGBA').resize((max(1,round(layer['width']*scale)),max(1,round(layer['height']*scale))),Image.Resampling.BILINEAR)).astype(np.float32)
            x=(ow-round(cw*scale))//2+round(layer['x']*scale);y=(oh-round(ch*scale))//2+round(layer['y']*scale)
            self.cache[key]=(rgba,x,y)
            while len(self.cache)>4:self.cache.popitem(last=False)
        self.cache.move_to_end(key);return self.cache[key]
    def compose(self,pixels,frame):
        import numpy as np
        active=[(r,opacity(r,frame)) for r in self.request.get('titles',[]) if r['start_frame']<=frame<tl.end(r)]
        if not active:return pixels
        result=pixels.astype(np.float32)
        for row,level in active:
            rgba,x,y=self._image(row);h,w=rgba.shape[:2];area=result[y:y+h,x:x+w];alpha=rgba[:,:,3:4]/255*level
            if row['background']=='black':result*=1-level;result[y:y+h,x:x+w]+=rgba[:,:,:3]*alpha
            else:result[y:y+h,x:x+w]=area*(1-alpha)+rgba[:,:,:3]*alpha
        return np.floor(result+.5).clip(0,255).astype(np.uint8)
