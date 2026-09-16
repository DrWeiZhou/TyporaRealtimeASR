'use strict';
// Lets the fixed panel be dragged by its top edge (HANDLE px), kept inside the Typora window, and remembers the position.
const HANDLE=36,MARGIN=4,KEY='typora-asr-panel-position';
const INTERACTIVE='button,input,select,textarea,a,summary,label,[contenteditable="true"]';
function clamp(left,top,width,height,viewWidth,viewHeight){
  const maxLeft=Math.max(MARGIN,viewWidth-width-MARGIN),maxTop=Math.max(MARGIN,viewHeight-Math.min(height,viewHeight)-MARGIN);
  return {left:Math.round(Math.min(Math.max(left,MARGIN),maxLeft)),top:Math.round(Math.min(Math.max(top,MARGIN),maxTop))};
}
function makeDraggable(w,panel){
  const d=w.document;let drag=null,placed=false;
  const inHandle=e=>{const r=panel.getBoundingClientRect();return e.clientY>=r.top&&e.clientY<r.top+HANDLE&&e.clientX>=r.left&&e.clientX<=r.right&&!e.target?.closest?.(INTERACTIVE);};
  const place=(left,top)=>{
    const r=panel.getBoundingClientRect(),p=clamp(left,top,r.width,r.height,w.innerWidth,w.innerHeight);
    panel.style.left=p.left+'px';panel.style.top=p.top+'px';panel.style.right='auto';panel.style.bottom='auto';placed=true;return p;
  };
  const save=p=>{try{w.localStorage.setItem(KEY,JSON.stringify(p));}catch(_){/* position is a convenience only */}};
  // Keep the panel reachable after the window shrinks or the panel changes size (compact/expanded).
  const refit=()=>{if(!placed||panel.hidden)return;const r=panel.getBoundingClientRect();place(r.left,r.top);};
  const onHover=e=>{if(!drag)panel.style.cursor=inHandle(e)?'move':'';};
  const onDown=e=>{
    if(e.button!==0||!inHandle(e))return;
    const r=panel.getBoundingClientRect();drag={dx:e.clientX-r.left,dy:e.clientY-r.top};
    panel.classList.add('asr-dragging');d.body.style.userSelect='none';e.preventDefault();
  };
  const onMove=e=>{if(drag){place(e.clientX-drag.dx,e.clientY-drag.dy);e.preventDefault();}};
  const onUp=()=>{
    if(!drag)return;drag=null;panel.classList.remove('asr-dragging');d.body.style.userSelect='';
    const r=panel.getBoundingClientRect();save(place(r.left,r.top));
  };
  panel.addEventListener('mousemove',onHover);panel.addEventListener('mousedown',onDown);
  d.addEventListener('mousemove',onMove,true);d.addEventListener('mouseup',onUp,true);w.addEventListener('resize',refit);
  const observer=typeof w.ResizeObserver==='function'?new w.ResizeObserver(refit):null;observer?.observe(panel);
  try{const saved=JSON.parse(w.localStorage.getItem(KEY)||'null');if(Number.isFinite(saved?.left)&&Number.isFinite(saved?.top))place(saved.left,saved.top);}catch(_){/* default CSS position */}
  return {refit,dispose(){panel.removeEventListener('mousemove',onHover);panel.removeEventListener('mousedown',onDown);d.removeEventListener('mousemove',onMove,true);d.removeEventListener('mouseup',onUp,true);w.removeEventListener('resize',refit);observer?.disconnect();}};
}
module.exports={makeDraggable,clamp,HANDLE};
