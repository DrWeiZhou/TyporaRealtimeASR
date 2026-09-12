// Isolated Typora fixture: mock transport, real editor, real Markdown save.
module.exports=async function(w){
 const fs=w.reqnode('fs'),path=w.reqnode('path'),root=path.resolve(__dirname,'..'),target=path.join(root,'artifacts/retry-editor.md');
 if(w.File.bundle?.filePath!==target)return;
 w.typoraRealtimeAsr?.dispose();delete w.typoraRealtimeAsr;
 const source=path.join(root,'src/typora-plugin');for(const name of ['main','client','controller','editor-adapter','panel-controls']){const p=path.join(source,name+'.cjs');delete w.reqnode.cache[w.reqnode.resolve(p)];}
 const {ServiceClient}=w.reqnode(path.join(source,'client.cjs')),original=ServiceClient.prototype.request;
 const sid='retry-editor-session',doc='retry-editor-doc',wait=ms=>new Promise(r=>setTimeout(r,ms));let phase=0,downloaded=false,zeroReads=0;
 const events=[{seq:1,eventId:'retry-first',state:'recognized',text:'补回的完整句子。'},{seq:2,eventId:'retry-second',state:'recognized',text:'后面的完整句子。'}];
 const status=()=>({sessionId:sid,documentId:doc,path:target,recording:false,paused:false,pending:0,seconds:12,polish:{pending:phase?0:1,failed:0,completed:phase?2:1}});
 ServiceClient.prototype.request=async function(method,route,body){
  if(route==='/sessions')return [{sessionId:sid,documentId:doc,path:target}];
  if(route.endsWith('/recover')||route===`/sessions/${sid}`)return status();
  if(route.includes('/events?')){const after=Number(route.split('after=')[1]);if(after===0)zeroReads++;downloaded=true;return events.filter(e=>e.seq>after).map(e=>e.seq===1&&!phase?{...e,text:'',polishState:'failed'}:{...e,polishState:'ready'});}
  if(route.endsWith('/ack')){events.find(e=>e.eventId===body.eventId).state=body.state;return {};}
  if(route==='/devices')return [{id:-1,name:'测试麦克风'}];
  if(route==='/health')return {status:'ok',protocolVersion:2};if(route==='/model-health')return {ready:true};
  if(route==='/polish/config')return {hasKey:true,baseUrl:'https://example.invalid',model:'fixture',prompt:''};
  throw new Error('Unexpected fixture request '+route);
 };
 try{
  const app=w.reqnode(path.join(source,'main.cjs'))(w,{connectionFile:path.join(root,'artifacts/retry-state/connection.json')});await app.recover(sid);
  w.document.dispatchEvent(new w.CompositionEvent('compositionstart',{bubbles:true}));
  for(let i=0;i<40&&!downloaded;i++)await wait(50);
  phase=1;await wait(900);w.document.dispatchEvent(new w.CompositionEvent('compositionend',{bubbles:true}));
  for(let i=0;i<60&&!events.every(e=>e.state==='saved');i++)await wait(100);
  const markdown=fs.readFileSync(target,'utf8');
  const pass=events.every(e=>e.state==='saved')&&zeroReads>=2&&events.every(e=>markdown.split(e.text).length===2)&&markdown.includes('人工笔记');
  fs.writeFileSync(path.join(root,'artifacts/retry-editor-report.json'),JSON.stringify({pass,zeroReads,states:events.map(e=>e.state),markdown},null,2));app.dispose();delete w.typoraRealtimeAsr;
 }finally{ServiceClient.prototype.request=original;}
};
