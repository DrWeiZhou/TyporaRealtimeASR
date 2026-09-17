(()=>{
 const base=document.currentScript.dataset.asrRoot;
 let attempts=0;
 // macOS Typora has no Node require(): load the plugin's CommonJS files through window.bridge instead.
 function macRequire(root){
  const cache=new Map();
  const read=file=>{const text=window.bridge.callSync('path.readText',file);if(typeof text!=='string'||!text)throw new Error('无法读取插件文件 '+file);return text.replace(/^\uFEFF/,'');};
  const load=(file)=>{
   if(cache.has(file))return cache.get(file).exports;
   const module={exports:{}};cache.set(file,module);
   const dir=file.slice(0,file.lastIndexOf('/'));
   const local=spec=>{if(!/^\.\/[\w.-]+\.cjs$/.test(spec))throw new Error('Unsupported module '+spec);return load(dir+'/'+spec.slice(2));};
   new Function('module','exports','require','__filename','__dirname',read(file)+'\n//# sourceURL=typora-asr/'+file.slice(dir.length+1))(module,module.exports,local,file,dir);
   return module.exports;
  };
  return {load:name=>load(root+'/'+name),read};
 }
 const timer=setInterval(()=>{
  if(++attempts>120){clearInterval(timer);return;}
  if(!window.File?.editor?.nodeMap)return;
  const node=typeof window.reqnode==='function',mac=!node&&typeof window.bridge?.callSync==='function';
  if(!node&&!mac)return;
  clearInterval(timer);
  try {
   if(node){
    const path=window.reqnode('path');
    const config=JSON.parse(window.reqnode('fs').readFileSync(path.join(base,'config.json'),'utf8').replace(/^\uFEFF/,''));
    window.reqnode(path.join(base,'main.cjs'))(window,config);
   }else{
    const loader=macRequire(base.replace(/\/+$/,''));
    const config=JSON.parse(loader.read(base.replace(/\/+$/,'')+'/config.json'));
    window.typoraAsrHost=loader.load('host-mac.cjs').createMacHost(window,config);
    loader.load('main.cjs')(window,config);
   }
  }catch(e){console.error('Typora ASR failed to load',e);}
 },500);
})();
