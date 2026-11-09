const types=new Map([
  ['', ['Без типа','film']],
  ['dialogue', ['Диалог','type-dialogue']],
  ['narration', ['Рассказ','type-narration']],
  ['visual', ['Визуальный показ','image']],
  ['action', ['Экшн','type-action']],
  ['montage', ['Монтажная последовательность','grid']],
  ['titles', ['Титры','type-titles']],
  ['transition', ['Переход','type-transition']],
  ['other', ['Другое','type-other']]
]);
const typeInfo=value=>types.get(value)??types.get('');
export const segmentTypeLabel=value=>typeInfo(value)[0];
export const segmentTypeIcon=value=>`<svg class="segment-type-icon" aria-hidden="true"><use href="#i-${typeInfo(value)[1]}"/></svg>`;
export function segmentTypeOptions(value){
  const selected=types.has(value)?value:'';
  return [...types].map(([key,[label]])=>`<option value="${key}" ${key===selected?'selected':''}>${label}</option>`).join('');
}
