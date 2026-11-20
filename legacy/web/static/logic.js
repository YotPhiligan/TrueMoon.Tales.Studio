export const fields=['subject_definitions','summary','retention_analysis','detailed_description','overall_soundscape','non_diegetic_music'];
export const sectionLabels=['Персонажи','Задача','Сохранить','Действие','Звуки','Музыка'];
export function splitPrompt(text){const out=Object.fromEntries(fields.map(k=>[k,'']));const matches=[...text.matchAll(new RegExp('^('+fields.join('|')+'):\\s*','gm'))];matches.forEach((m,i)=>out[m[1]]=text.slice(m.index+m[0].length,matches[i+1]?.index??text.length).trim());return out;}
export function joinPrompt(parts){return fields.map(k=>`${k}:\n${(parts[k]??'').trim()}`).join('\n\n');}
export function timing(seconds,mode){const value=Number(seconds);if(!Number.isFinite(value)||value<2||value>8)return null;const raw=value*24,floor=Math.floor(raw);let n=raw-floor===.5?(floor%2?floor+1:floor):Math.round(raw);n+=(5-n%17+17)%17;const trim=mode==='context'?22:0;return {frames:n,trim,newFrames:n-trim,source:n/24,usable:(n-trim)/24};}
export function removePicture(text,index){return text.replace(/<Picture (\d+)>/g,(_,n)=>Number(n)===index?'[удалённый референс]':`<Picture ${Number(n)>index?Number(n)-1:n}>`);}
export function escapeHtml(value){return String(value??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));}
export function timecode(value){const s=Math.max(0,Number(value)||0);return `${String(Math.floor(s/60)).padStart(2,'0')}:${(s%60).toFixed(3).padStart(6,'0')}`;}
