export function titleOpacity(row,frame){
  const f=Math.floor(frame+1e-7),start=row.start_frame,end=start+row.duration_frames;
  if(f<start||f>=end)return 0;
  if(row.fade_in_frames&&f<start+row.fade_in_frames)return (f-start+.5)/row.fade_in_frames;
  if(row.fade_out_frames&&f>=end-row.fade_out_frames)return (end-f-.5)/row.fade_out_frames;
  return 1;
}
export function titleIssue(row){
  if(typeof row.text!=='string'||!row.text.trim()||row.text.length>1000||row.text.split('\n').length>12||/[\x00-\x09\x0b-\x1f]/.test(row.text))return 'Введите текст до 1000 символов и 12 строк.';
  if(typeof row.name!=='string'||!row.name.trim()||row.name.length>160)return 'Укажите название титра.';
  if(!Number.isInteger(row.start_frame)||row.start_frame<0||!Number.isInteger(row.duration_frames)||row.duration_frames<1||row.start_frame+row.duration_frames>518400)return 'Проверьте начало и длительность титра.';
  if(['fade_in_frames','fade_out_frames'].some(k=>!Number.isInteger(row[k])||row[k]<0)||row.fade_in_frames+row.fade_out_frames>row.duration_frames)return 'Появление и исчезновение должны помещаться в длительность титра.';
  if(!Number.isInteger(row.font_size)||row.font_size<8||row.font_size>256)return 'Размер шрифта должен быть от 8 до 256.';
  return '';
}
export function projectFrameBox(width,height,size){
  if(!(width>0&&height>0&&size?.[0]>0&&size?.[1]>0))return null;
  const scale=Math.min(width/size[0],height/size[1]),w=size[0]*scale,h=size[1]*scale;
  return {x:(width-w)/2,y:(height-h)/2,width:w,height:h};
}
export function placeTitleImage(img,layer){
  const [w,h]=layer.canvas_size;
  Object.assign(img.style,{left:layer.x/w*100+'%',top:layer.y/h*100+'%',width:layer.width/w*100+'%',height:layer.height/h*100+'%'});
}
