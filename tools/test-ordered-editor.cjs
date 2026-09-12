// Development-only probe, restricted to its disposable fixture.
module.exports=async function(w){
 const fs=w.reqnode('fs'),path=w.reqnode('path'),root=path.resolve(__dirname,'..');
 const target=path.join(root,'artifacts/ordered-editor-probe.md');if(w.File.bundle?.filePath!==target)return;
 const source=path.join(root,'src/typora-plugin/editor-adapter.cjs');delete w.reqnode.cache[w.reqnode.resolve(source)];
 const {EditorAdapter}=w.reqnode(source);const a=new EditorAdapter(w,'ordered-test');a.bind();
 if(a.contains('ordered-one')){
  const old=w.File.editor.getMarkdown();a.insert({eventId:'ordered-four',text:'第四句内容。'});
  const markdown=w.File.editor.getMarkdown(),saved=await a.save();
  fs.writeFileSync(path.join(root,'artifacts/ordered-editor-reopen.json'),JSON.stringify({pass:saved&&/^4\. /m.test(markdown)&&a.contains('ordered-two')&&a.contains('ordered-three')&&old.includes('3. 第三句内容。'),saved,markdown},null,2));a.dispose();return;
 }
 a.insert({eventId:'ordered-one',text:'第一句内容。',start:16000,end:32000});
 a.insert({eventId:'ordered-two',text:'第二句内容。'});
 const b=new EditorAdapter(w,'ordered-test');b.insert({eventId:'ordered-three',text:'第三句内容。'});
 const markdown=w.File.editor.getMarkdown();const saved=await b.save();
 const report={markdown,saved,ordered:[1,2,3].every(n=>new RegExp('^'+n+'\\. ','m').test(markdown)),html:[...w.document.querySelectorAll('#write > ol')].map(el=>({start:el.start,text:el.textContent})),manualPreserved:markdown.includes('人工记录保持原样')};
 report.pass=report.saved&&report.ordered&&report.manualPreserved&&report.html.map(x=>x.start).join(',')==='1,2,3';
 fs.writeFileSync(path.join(root,'artifacts/ordered-editor-report.json'),JSON.stringify(report,null,2));a.dispose();b.dispose();
};
