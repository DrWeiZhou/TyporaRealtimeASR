// Development-only probe; only operates on the isolated fixture document.
module.exports=async function(w){
 const fs=w.reqnode('fs'),path=w.reqnode('path'),root=path.resolve(__dirname,'..');
 const target=path.join(root,'artifacts/editor-polish-probe.md');if(w.File.bundle?.filePath!==target)return;
 const fixture=JSON.parse(fs.readFileSync(path.join(root,'artifacts/feature-test/integration-session.json'),'utf8'));
 const wait=ms=>new Promise(r=>setTimeout(r,ms));
 for(let i=0;i<100&&!w.typoraRealtimeAsr;i++)await wait(100);
 w.typoraRealtimeAsr?.dispose();delete w.typoraRealtimeAsr;
 const source=path.join(root,'src/typora-plugin');for(const file of ['main.cjs','client.cjs','controller.cjs','editor-adapter.cjs','panel-controls.cjs','service-launcher.cjs']){const resolved=w.reqnode.resolve(path.join(source,file));delete w.reqnode.cache[resolved];}
 w.reqnode(path.join(source,'main.cjs'))(w,{connectionFile:path.join(root,'artifacts/feature-test/connection.json')});
 const {EditorAdapter}=w.reqnode(path.join(root,'src/typora-plugin/editor-adapter.cjs'));
 const adapter=new EditorAdapter(w,fixture.documentId);adapter.transaction(adapter.anchors()[0],[{type:'paragraph',text:'人工未保存改写必须保留'}]);
 for(let attempt=0;attempt<16;attempt++){await w.typoraRealtimeAsr.recover(fixture.sessionId);if(w.typoraRealtimeAsr.getState().session?.sessionId===fixture.sessionId)break;await wait(1000);}
 for(let i=0;i<60;i++){await wait(250);if(w.File.editor.getMarkdown().includes('已润色的第二句话。')&&w.typoraRealtimeAsr.getState().toSave===0&&fs.readFileSync(target,'utf8')===w.File.editor.getMarkdown())break;}
 const first=w.File.editor.getMarkdown(),disk=fs.readFileSync(target,'utf8');
 await w.typoraRealtimeAsr.recover(fixture.sessionId);await wait(1800);
 const after=w.File.editor.getMarkdown();
 await w.document.querySelector('#asr-raw').onclick();
 const raw=w.document.querySelector('#asr-raw-text').textContent;
 const report={manualPreserved:first.includes('人工未保存改写必须保留'),polishedInserted:first.includes('已润色的第二句话。'),rawBlocked:!first.includes('原始口语')&&!first.includes('不能入文'),orderedList:/^1\. /m.test(first)&&/^2\. /m.test(first)&&!first.includes('00:00:00–00:00:01'),saved:disk===first,replayNoDuplicate:first===after,rawTimestamp:raw.includes('10:00:00')&&raw.includes('原始口语一'),controlsPresent:['asr-pause','asr-level','asr-service-start','asr-api-key','asr-file-save'].every(id=>!!w.document.getElementById(id)),saveState:w.document.querySelector('#asr-file-save').textContent};
 // Block the background GET and verify that critical controls still issue their request.
 const {ServiceClient}=w.reqnode(path.join(source,'client.cjs'));const original=ServiceClient.prototype.request;
 for(const action of ['pause','stop']){
  let release,started,called=false;const startedPromise=new Promise(r=>started=r);const held=new Promise(r=>release=r);let intercepted=false;
  const snapshot=w.typoraRealtimeAsr.getState().session;
  ServiceClient.prototype.request=function(method,route,body){if(method==='GET'&&route===`/sessions/${fixture.sessionId}`&&!intercepted){intercepted=true;started();return held;}if(method==='POST'&&route===`/sessions/${fixture.sessionId}/${action}`){called=true;return Promise.resolve({...snapshot,paused:action==='pause',recording:false});}return original.call(this,method,route,body);};
  try{await Promise.race([startedPromise,wait(4000)]);await w.typoraRealtimeAsr[action]();report[action+'DuringRefresh']=called;}finally{ServiceClient.prototype.request=original;release(snapshot);await wait(80);report[action+'IgnoresStaleStatus']=w.typoraRealtimeAsr.getState().session.paused===(action==='pause');await wait(900);}
 }
 report.pass=Object.entries(report).filter(([key])=>key!=='saveState').every(([,value])=>value===true);
 fs.writeFileSync(path.join(root,'artifacts/polish-editor-report.json'),JSON.stringify(report,null,2));adapter.dispose();
};
