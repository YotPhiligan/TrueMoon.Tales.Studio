import {buttonHelp,buttonSelector} from './button-help.js';

const hoverDelay=480;
const gap=8;

export function bindButtonTooltips(){
  const tip=document.createElement('div');
  tip.id='studio-button-tooltip';
  tip.className='studio-button-tooltip';
  tip.setAttribute('role','tooltip');
  tip.hidden=true;
  const popover=typeof tip.showPopover==='function';
  if(popover)tip.setAttribute('popover','manual');

  let anchor=null,title=null,showTimer,hideTimer;
  const observer=new MutationObserver(()=>{
    if(anchor&&(!anchor.isConnected||anchor.closest('[hidden],dialog:not([open])')))dismiss();
  });
  const buttonFrom=target=>target instanceof Element?target.closest(buttonSelector):null;
  const within=(node,target)=>target instanceof Node&&node?.contains(target);

  function dismiss(){
    clearTimeout(showTimer);
    clearTimeout(hideTimer);
    observer.disconnect();
    if(anchor){
      const description=(anchor.getAttribute('aria-describedby')??'').split(/\s+/)
        .filter(id=>id&&id!==tip.id);
      if(description.length)anchor.setAttribute('aria-describedby',description.join(' '));
      else anchor.removeAttribute('aria-describedby');
      if(title!==null&&!anchor.hasAttribute('title'))anchor.setAttribute('title',title);
    }
    if(popover&&tip.matches(':popover-open'))tip.hidePopover();
    tip.hidden=true;
    anchor=null;
    title=null;
  }

  function position(){
    const rect=anchor.getBoundingClientRect(),size=tip.getBoundingClientRect();
    const left=Math.min(Math.max(gap,(rect.left+rect.right-size.width)/2),window.innerWidth-size.width-gap);
    const above=rect.top-size.height-gap,below=rect.bottom+gap;
    const top=above>=gap?above:Math.max(gap,Math.min(below,window.innerHeight-size.height-gap));
    tip.style.left=left+'px';
    tip.style.top=top+'px';
  }

  function show(){
    if(!anchor?.isConnected||!anchor.getClientRects().length){dismiss();return;}
    const owner=anchor.closest('dialog[open]')??document.body;
    owner.append(tip);
    tip.hidden=false;
    if(popover&&!tip.matches(':popover-open'))tip.showPopover();
    position();
    const descriptions=new Set((anchor.getAttribute('aria-describedby')??'').split(/\s+/).filter(Boolean));
    descriptions.add(tip.id);
    anchor.setAttribute('aria-describedby',[...descriptions].join(' '));
    observer.observe(document.body,{childList:true,subtree:true,attributes:true,attributeFilter:['hidden','open']});
  }

  function activate(button,keyboard){
    clearTimeout(hideTimer);
    if(button===anchor){
      if(keyboard&&tip.hidden){clearTimeout(showTimer);show();}
      return;
    }
    dismiss();
    const help=buttonHelp(button);
    if(!help)return;
    anchor=button;
    title=button.getAttribute('title');
    if(title!==null)button.removeAttribute('title');
    tip.textContent=help;
    if(keyboard)show();
    else showTimer=setTimeout(show,hoverDelay);
  }

  document.addEventListener('pointerover',event=>{
    if(event.pointerType==='touch')return;
    if(within(tip,event.target)){clearTimeout(hideTimer);return;}
    const button=buttonFrom(event.target);
    if(button)activate(button,false);
  },true);
  document.addEventListener('pointerout',event=>{
    if(!anchor||(!within(anchor,event.target)&&!within(tip,event.target)))return;
    if(within(anchor,event.relatedTarget)||within(tip,event.relatedTarget)||anchor.matches(':focus-visible'))return;
    clearTimeout(hideTimer);
    hideTimer=setTimeout(dismiss,120);
  },true);
  document.addEventListener('focusin',event=>{
    const button=buttonFrom(event.target);
    if(button?.matches(':focus-visible'))activate(button,true);
  });
  document.addEventListener('focusout',event=>{if(within(anchor,event.target))dismiss();});
  document.addEventListener('pointerdown',dismiss,true);
  document.addEventListener('scroll',()=>{
    if(anchor&&!tip.hidden&&anchor===document.activeElement&&anchor.matches(':focus-visible')){
      const rect=anchor.getBoundingClientRect();
      if(rect.bottom>0&&rect.top<innerHeight&&rect.right>0&&rect.left<innerWidth){position();return;}
    }
    dismiss();
  },true);
  document.addEventListener('close',dismiss,true);
  document.addEventListener('keydown',event=>{
    if(event.key!=='Escape'||!anchor)return;
    const shown=!tip.hidden;
    dismiss();
    if(shown){event.preventDefault();event.stopPropagation();}
  },true);
  window.addEventListener('resize',dismiss);
  window.addEventListener('blur',dismiss);
}
