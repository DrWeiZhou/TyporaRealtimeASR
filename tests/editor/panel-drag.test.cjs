const {test}=require('node:test');const assert=require('node:assert/strict');
const {makeDraggable,clamp}=require('../../src/typora-plugin/panel-drag.cjs');
test('clamp keeps the panel inside the window',()=>{
 assert.deepEqual(clamp(-50,-20,340,500,1200,800),{left:4,top:4});
 assert.deepEqual(clamp(1000,700,340,500,1200,800),{left:856,top:296});
 assert.deepEqual(clamp(100,100,340,1200,1200,800),{left:100,top:4});
 assert.deepEqual(clamp(10,10,2000,100,1200,800),{left:4,top:10});
});
function fakeWindow(saved){
 const listeners={},docListeners={},store=new Map(saved?[['typora-asr-panel-position',saved]]:[]);
 const on=map=>(type,fn)=>{(map[type]||=[]).push(fn);};const off=map=>(type,fn)=>{map[type]=(map[type]||[]).filter(x=>x!==fn);};
 const panel={style:{},hidden:false,rect:{left:840,top:70,width:340,height:500},classList:{add(){},remove(){}},
  getBoundingClientRect(){const r=this.rect;return {...r,right:r.left+r.width,bottom:r.top+r.height};},
  addEventListener:on(listeners),removeEventListener:off(listeners)};
 const sync=()=>{if(panel.style.left)panel.rect.left=parseFloat(panel.style.left);if(panel.style.top)panel.rect.top=parseFloat(panel.style.top);};
 const w={innerWidth:1200,innerHeight:800,localStorage:{getItem:k=>store.get(k)??null,setItem:(k,v)=>store.set(k,v)},
  addEventListener(){},removeEventListener(){},
  document:{body:{style:{}},addEventListener:on(docListeners),removeEventListener:off(docListeners)}};
 const fire=(map,type,e)=>{for(const fn of map[type]||[])fn({button:0,preventDefault(){},target:{closest:()=>null},...e});sync();};
 return {w,panel,store,down:e=>fire(listeners,'mousedown',e),hover:e=>fire(listeners,'mousemove',e),move:e=>fire(docListeners,'mousemove',e),up:()=>fire(docListeners,'mouseup',{}),docListeners};
}
test('dragging by the top edge moves, clamps and remembers the panel',()=>{
 const f=fakeWindow();const drag=makeDraggable(f.w,f.panel);
 f.hover({clientX:900,clientY:80});assert.equal(f.panel.style.cursor,'move');
 f.hover({clientX:900,clientY:200});assert.equal(f.panel.style.cursor,'');
 f.down({clientX:900,clientY:200});f.move({clientX:100,clientY:100});
 assert.equal(f.panel.style.left,undefined,'body area must not start a drag');
 f.down({clientX:900,clientY:80});f.move({clientX:500,clientY:300});
 assert.equal(f.panel.style.left,'440px');assert.equal(f.panel.style.top,'290px');assert.equal(f.panel.style.right,'auto');
 f.move({clientX:5000,clientY:5000});f.up();
 assert.equal(f.panel.style.left,'856px');assert.equal(f.panel.style.top,'296px');
 assert.deepEqual(JSON.parse(f.store.get('typora-asr-panel-position')),{left:856,top:296});
 f.move({clientX:10,clientY:10});assert.equal(f.panel.style.left,'856px','released panel must not follow the mouse');
 drag.dispose();assert.equal((f.docListeners.mousemove||[]).length,0);
});
test('buttons in the top edge keep working and saved positions are restored inside the window',()=>{
 const f=fakeWindow(JSON.stringify({left:3000,top:-40}));makeDraggable(f.w,f.panel);
 assert.equal(f.panel.style.left,'856px');assert.equal(f.panel.style.top,'4px');
 f.down({clientX:1150,clientY:20,target:{closest:()=>({})}});f.move({clientX:100,clientY:300});
 assert.equal(f.panel.style.left,'856px');
});
