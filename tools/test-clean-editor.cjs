// Test-copy only: never touches user documents.
module.exports=async function(w){
 const fs=w.reqnode('fs'),path=w.reqnode('path'),root=path.resolve(__dirname,'..');
 const target=path.join(root,'artifacts/clean-editor-probe.md');if(w.File.bundle?.filePath!==target)return;
 const source=path.join(root,'src/typora-plugin/editor-adapter.cjs');delete w.reqnode.cache[w.reqnode.resolve(source)];
 const {EditorAdapter}=w.reqnode(source),a=new EditorAdapter(w,'clean-test',path.join(root,'artifacts/clean-state'));
 const reopen=a.state.events['clean-two']==='saved';a.bind();
 if(!reopen){
  a.transaction(a.e.nodeMap.getLast(),[{type:'paragraph',text:'人工未保存改写'}],false);
  a.insert({eventId:'clean-one',text:'新增第一句。'});a.insert({eventId:'clean-two',text:'新增第二句。'});
 }else a.insert({eventId:'clean-three',text:'重开后新增第三句。'});
 const markdown=a.e.getMarkdown(),saved=await a.save();
 const pass=saved&&!markdown.includes('<!-- asr-')&&markdown.includes('人工未保存改写')&&markdown.includes('原有人工记录')&&a.contains('legacy-event')&&a.contains('clean-two')&&new RegExp('^'+(reopen?6:5)+'\\. ','m').test(markdown);
 fs.writeFileSync(path.join(root,'artifacts/clean-editor-'+(reopen?'reopen':'first')+'.json'),JSON.stringify({pass,saved,markdown},null,2));a.dispose();
};
