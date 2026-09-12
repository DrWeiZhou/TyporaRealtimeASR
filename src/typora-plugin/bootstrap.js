(()=>{
 const base=document.currentScript.dataset.asrRoot;
 let attempts=0;
 const timer=setInterval(()=>{
  if(++attempts>120){clearInterval(timer);return;}
  if(!window.File?.editor?.nodeMap || !window.reqnode)return;
  clearInterval(timer);
  try {
   const path=window.reqnode('path');
   const config=JSON.parse(window.reqnode('fs').readFileSync(path.join(base,'config.json'),'utf8').replace(/^\uFEFF/,''));
   window.reqnode(path.join(base,'main.cjs'))(window,config);
  }catch(e){console.error('Typora ASR failed to load',e);}
 },500);
})();
