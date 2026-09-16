const {test}=require('node:test');const assert=require('node:assert/strict');
const {pickFolder,pickOpenJson,pickSaveJson}=require('../../src/typora-plugin/dialogs.cjs');
const noNode=()=>{throw new Error('unavailable');};
test('Typora dialog result is used, including cancel',async()=>{
 let options;
 const w={JSBridge:{invoke:async(name,o)=>{assert.equal(name,'dialog.showOpenDialog');options=o;return {canceled:false,filePaths:['D:\\记录']};}},reqnode:noNode};
 assert.equal(await pickFolder(w,{defaultPath:'C:\\old'}),'D:\\记录');
 assert.ok(options.properties.includes('openDirectory'));assert.equal(options.defaultPath,'C:\\old');
 w.JSBridge.invoke=async()=>({canceled:true,filePaths:[]});
 assert.equal(await pickFolder(w),null);
});
test('unsupported bridge falls back to Electron remote dialog',async()=>{
 const w={JSBridge:{invoke:async()=>undefined},reqnode:name=>{
  if(name==='@electron/remote')return {getCurrentWindow:()=>'win',dialog:{showOpenDialog:async(win,o)=>{assert.equal(win,'win');return {canceled:false,filePaths:['E:\\data']};}}};
  throw new Error('unexpected '+name);
 }};
 assert.equal(await pickFolder(w),'E:\\data');
});
test('without dialogs the Windows folder browser is used and its output trimmed',async()=>{
 let call;
 const w={reqnode:name=>{
  if(name==='child_process')return {execFile:(file,args,opts,cb)=>{call={file,args,opts};cb(null,'F:\\选择的目录\r\n');}};
  throw new Error('no '+name);
 }};
 assert.equal(await pickFolder(w,{defaultPath:'C:\\x'}),'F:\\选择的目录');
 assert.equal(call.file,'powershell.exe');assert.ok(call.args.includes('-STA'));assert.equal(call.opts.env.ASR_PICK_START,'C:\\x');
 w.reqnode=name=>{if(name==='child_process')return {execFile:(f,a,o,cb)=>cb(null,'')};throw new Error('no');};
 assert.equal(await pickFolder(w),null);
});
test('JSON file dialogs use the matching Typora dialog and filters',async()=>{
 const calls=[];
 const w={JSBridge:{invoke:async(name,o)=>{calls.push([name,o]);return name==='dialog.showSaveDialog'?{canceled:false,filePath:'C:\\out\\配置.json'}:{canceled:false,filePaths:['C:\\in\\配置.json']};}},reqnode:noNode};
 assert.equal(await pickSaveJson(w,{defaultPath:'C:\\out\\TyporaASR.json'}),'C:\\out\\配置.json');
 assert.equal(await pickOpenJson(w),'C:\\in\\配置.json');
 assert.deepEqual(calls.map(c=>c[0]),['dialog.showSaveDialog','dialog.showOpenDialog']);
 assert.deepEqual(calls[0][1].filters[0].extensions,['json']);assert.deepEqual(calls[1][1].properties,['openFile']);
 w.JSBridge.invoke=async()=>({canceled:true});
 assert.equal(await pickSaveJson(w),null);
});
test('Windows save dialog fallback receives the suggested file and JSON filter',async()=>{
 let call;
 const w={reqnode:name=>{if(name==='child_process')return {execFile:(f,a,o,cb)=>{call={a,o};cb(null,'D:\\备份\\配置.json\r\n');}};throw new Error('no');}};
 assert.equal(await pickSaveJson(w,{defaultPath:'D:\\备份\\TyporaASR.json'}),'D:\\备份\\配置.json');
 assert.ok(call.a.at(-1).includes('SaveFileDialog'));assert.equal(call.o.env.ASR_PICK_START,'D:\\备份\\TyporaASR.json');assert.ok(call.o.env.ASR_PICK_FILTER.includes('*.json'));
});
