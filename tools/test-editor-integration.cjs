// Invoked only by the development loader in the copied Typora test application.
module.exports=async function(w){
 const fs=w.reqnode('fs'),path=w.reqnode('path'),root=path.resolve(__dirname,'..');
 const target=path.join(root,'artifacts/editor-probe.md');if(w.File.bundle?.filePath!==target)return;
 for(let i=0;i<100&&!w.typoraRealtimeAsr;i++)await new Promise(r=>setTimeout(r,100));
 const fixture=JSON.parse(fs.readFileSync(path.join(root,'.asr/integration-session.json'),'utf8'));
 const before=w.File.editor.getMarkdown(),marker='<!-- asr-event:'+fixture.sessionId+':';
 const had=before.includes(marker);
 for(let i=0;i<16;i++){
   await w.typoraRealtimeAsr.recover(fixture.sessionId);
   if(w.typoraRealtimeAsr.getState().session?.sessionId===fixture.sessionId)break;
   await new Promise(r=>setTimeout(r,1000));
 }
 await new Promise(r=>setTimeout(r,6500));
 const first=w.File.editor.getMarkdown(),disk=fs.readFileSync(target,'utf8');
 await w.typoraRealtimeAsr.recover(fixture.sessionId);await new Promise(r=>setTimeout(r,4000));
 const replay=w.File.editor.getMarkdown(),phrase='导出操作必须保留审计记录。';
 const insertedCount=first.split(marker).length-1;
 const contentCorrect=had?first===before:first.split(phrase).length===before.split(phrase).length+1;
 const state=w.typoraRealtimeAsr.getState();
 const pass=insertedCount===3&&contentCorrect&&first===replay&&state.pending===0&&state.toSave===0;
 fs.writeFileSync(path.join(root,'artifacts',had?'editor-reopen-test.json':'end-to-end-test-final.json'),JSON.stringify({pass,wasAlreadyInserted:had,insertedCount,manualPreserved:first.includes('人工未保存修改'),contentCorrect,replayNoDuplicate:first===replay,diskSaved:disk.split(marker).length-1===3,state},null,2));
};
