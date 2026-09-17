'use strict';
// macOS host polyfill: exercised with a fake Typora bridge that runs commands through the local shell.
const {test}=require('node:test');const assert=require('node:assert/strict');
const fs=require('node:fs'),os=require('node:os'),path=require('node:path'),vm=require('node:vm'),nodeCrypto=require('node:crypto');
const {execFile}=require('node:child_process');
const plugin=path.resolve(__dirname,'../../src/typora-plugin');
const {createMacHost}=require(path.join(plugin,'host-mac.cjs'));
const {ServiceClient}=require(path.join(plugin,'client.cjs'));
const {ServiceLauncher}=require(path.join(plugin,'service-launcher.cjs'));
const {pickFolder}=require(path.join(plugin,'dialogs.cjs'));
const {node,isMac}=require(path.join(plugin,'host.cjs'));
// These cases run real shell commands / POSIX paths like Typora for macOS does; Windows CI skips them.
const posixTest=process.platform==='win32'?(name,fn)=>test(name,{skip:'需要 bash 与 POSIX 路径'},fn):test;

function fakeWindow(extra={}){
 const commands=[];
 const bridge={
  callSync:(name,file)=>{assert.equal(name,'path.readText');try{return fs.readFileSync(file,'utf8')}catch{return null}},
  callHandler:(name,options,cb)=>{assert.equal(name,'controller.runCommand');commands.push(options.args);execFile('/bin/bash',['-c',options.args],{cwd:options.cwd},(error,stdout,stderr)=>cb([!error,stdout,stderr,options.args]));},
 };
 const w={bridge,crypto:globalThis.crypto,setTimeout,clearTimeout,...extra};
 w.typoraAsrHost=createMacHost(w,{homeDir:os.tmpdir()});
 return {w,commands};
}
const tmp=()=>fs.mkdtempSync(path.join(os.tmpdir(),'asr-mac-'));

posixTest('fs overlay reads its own writes at once and flushes them to disk in order',async()=>{
 const {w}=fakeWindow();const dir=tmp();const mfs=node(w,'fs');const file=path.join(dir,'nested','state.json');
 assert.equal(isMac(w),true);
 assert.equal(mfs.existsSync(file),false);assert.throws(()=>mfs.readFileSync(file,'utf8'),/ENOENT/);
 mfs.mkdirSync(path.dirname(file),{recursive:true});
 mfs.writeFileSync(file+'.tmp','{"text":"中文 \'quoted\' $HOME `x`"}');mfs.renameSync(file+'.tmp',file);
 assert.equal(mfs.existsSync(file),true);assert.equal(mfs.existsSync(file+'.tmp'),false);
 assert.match(mfs.readFileSync(file,'utf8'),/中文 'quoted' \$HOME `x`/);
 mfs.writeFileSync(file,'second');
 await w.typoraAsrHost.flush();
 assert.equal(fs.readFileSync(file,'utf8'),'second');assert.equal(fs.existsSync(file+'.tmp'),false);
 assert.equal(mfs.readFileSync(file,'utf8'),'second');
 assert.equal(mfs.statSync(file).size,6);
 assert.deepEqual(await w.typoraAsrHost.listDir(path.dirname(file)),['state.json']);
 assert.equal(await w.typoraAsrHost.exists(file),true);
 assert.equal(await w.typoraAsrHost.isDirectory(dir),true);
});

posixTest('failed writes are reported by flush',async()=>{
 const {w}=fakeWindow();const dir=tmp();fs.writeFileSync(path.join(dir,'blocker'),'x');
 node(w,'fs').writeFileSync(path.join(dir,'blocker','child.json'),'{}');
 await assert.rejects(w.typoraAsrHost.flush(),/写入失败/);
 await w.typoraAsrHost.flush();
});

test('crypto polyfill matches Node for sha256 and returns v4 UUIDs',()=>{
 const {w}=fakeWindow();const c=node(w,'crypto');
 const text='/Users/wei/记录.md|'+'x'.repeat(200);
 assert.equal(c.createHash('sha256').update(text).digest('hex'),nodeCrypto.createHash('sha256').update(text).digest('hex'));
 assert.match(c.randomUUID(),/^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/);
 assert.equal(node(w,'path').join('/a/b','..','c.md'),'/a/c.md');
 assert.throws(()=>node(w,'http'),/不提供模块/);
});

posixTest('client uses fetch with the token and falls back to curl when the WebView refuses',async()=>{
 const dir=tmp();const headerFile=path.join(dir,'curl-auth.txt');fs.writeFileSync(headerFile,'Authorization: Bearer t\n');
 const connection=path.join(dir,'connection.json');fs.writeFileSync(connection,JSON.stringify({endpoint:'http://127.0.0.1:18082',token:'t',curlHeaderFile:headerFile}));
 const calls=[];
 const ok=fakeWindow({fetch:async(url,options)=>{calls.push([url,options]);return new Response(JSON.stringify({status:'ok'}),{status:200});}});
 const client=new ServiceClient(ok.w,connection);
 assert.deepEqual(await client.request('GET','/health'),{status:'ok'});
 assert.equal(calls[0][0],'http://127.0.0.1:18082/health');assert.equal(calls[0][1].headers.Authorization,'Bearer t');assert.equal(calls[0][1].headers['X-ASR-Client'],client.id);
 const bad=fakeWindow({fetch:async()=>new Response(JSON.stringify({error:'请先保存目标 Markdown 文件'}),{status:400})});
 await assert.rejects(new ServiceClient(bad.w,connection).request('POST','/sessions',{a:1}),/Markdown/);
 // fetch refused → curl (stubbed on PATH) receives the body on stdin and the header file.
 const bin=path.join(dir,'bin');fs.mkdirSync(bin);
 fs.writeFileSync(path.join(bin,'curl'),'#!/bin/bash\nbody=$(cat)\nprintf \'{"args":"%s","body":%s}\\n201\' "$*" "$body"\n',{mode:0o755});
 const refused=fakeWindow({fetch:async()=>{throw new TypeError('Load failed');}});
 const run=refused.w.typoraAsrHost.run;refused.w.typoraAsrHost.run=(command,o)=>run(`PATH=${bin}:$PATH; `+command,o);
 const fallback=new ServiceClient(refused.w,connection);
 const result=await fallback.request('POST','/sessions/x/ack',{eventId:'e',state:'saved'});
 assert.deepEqual(result.body,{eventId:'e',state:'saved'});assert.match(result.args,/-H @.*curl-auth\.txt/);assert.doesNotMatch(result.args,/Bearer/);
 assert.equal(fallback.preferCurl,true);
 await assert.rejects(new ServiceClient(fakeWindow({fetch:async()=>{throw new TypeError('x')}}).w,connection).request('GET','/health').then(()=>{throw new Error('unexpected')}),/无法连接本地服务|unexpected/);
});

test('launcher uses the bash runner on macOS and reports its error',async()=>{
 const ran=[];let fail=false;
 const launcher=new ServiceLauncher('/p/tools/mac/start.sh',null,'/p/tools/mac/stop.sh',async script=>{ran.push(script);if(fail)throw new Error('line\n错误：找不到 llama-server');});
 await launcher.start();await launcher.stop();assert.deepEqual(ran,['/p/tools/mac/start.sh','/p/tools/mac/stop.sh']);
 fail=true;await assert.rejects(launcher.start(),/启动失败.*找不到 llama-server/);
});

test('dialogs use the AppleScript picker on macOS',async()=>{
 const {w}=fakeWindow();let seen;w.typoraAsrHost.pick=async(kind,options)=>{seen={kind,...options};return '/Users/wei/Records';};
 assert.equal(await pickFolder(w,{defaultPath:'/Users/wei'}),'/Users/wei/Records');
 assert.equal(seen.kind,'folder');assert.equal(seen.defaultPath,'/Users/wei');
 const {pickSaveJson}=require(path.join(plugin,'dialogs.cjs'));
 await pickSaveJson(w,{defaultPath:'/Users/wei/Documents/配置.json'});
 assert.equal(seen.defaultPath,'/Users/wei/Documents');assert.equal(seen.defaultName,'配置.json');
});

posixTest('bootstrap loads the CommonJS plugin through window.bridge when Node is absent',async()=>{
 const dir=tmp();
 for(const f of fs.readdirSync(plugin))fs.copyFileSync(path.join(plugin,f),path.join(dir,f));
 fs.writeFileSync(path.join(dir,'config.json'),'﻿'+JSON.stringify({platform:'mac',connectionFile:'/x/.asr/connection.json',homeDir:'/Users/wei'}));
 fs.writeFileSync(path.join(dir,'main.cjs'),"'use strict';const {isMac,node}=require('./host.cjs');module.exports=(w,config)=>{w.mounted={mac:isMac(w),home:node(w,'os').homedir(),file:__filename,config};};");
 const {w}=fakeWindow();
 w.File={editor:{nodeMap:{}}};
 const context=vm.createContext({window:w,document:{currentScript:{dataset:{asrRoot:dir}}},setInterval,clearInterval,console,TextEncoder,btoa});
 vm.runInContext(fs.readFileSync(path.join(plugin,'bootstrap.js'),'utf8'),context);
 for(let i=0;i<30&&!w.mounted;i++)await new Promise(r=>setTimeout(r,100));
 assert.deepEqual({mac:w.mounted.mac,home:w.mounted.home},{mac:true,home:'/Users/wei'});
 assert.equal(w.mounted.file,path.join(dir,'main.cjs'));
});
