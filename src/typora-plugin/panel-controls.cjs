'use strict';
const {ServiceLauncher}=require('./service-launcher.cjs');
const {pickFolder,pickOpenJson,pickSaveJson}=require('./dialogs.cjs');
const {node,hostOf,secretStoreLabel}=require('./host.cjs');
module.exports=function controls(w,config,client,panel,hooks){
 const d=w.document,fs=node(w,'fs'),path=node(w,'path'),host=hostOf(w),secretStore=secretStoreLabel(w);
 const tools=path.resolve(path.dirname(config.connectionFile),'..','tools');
 // Windows: hidden PowerShell scripts. macOS: bash scripts run through Typora's command bridge.
 const launcher=host
  ? new ServiceLauncher(path.join(tools,'mac','start.sh'),null,path.join(tools,'mac','stop.sh'),script=>host.runScript(script))
  : new ServiceLauncher(path.join(tools,'start.ps1'),node(w,'child_process').spawn,path.join(tools,'stop.ps1'));
 const $=id=>panel.querySelector('#'+id);let checking=false,starting=false,stopping=false,online=false,modelReady=false,configured=false,quietSince=0,disposed=false;
 const service=d.createElement('div');service.innerHTML=`<div class="asr-service"><button id="asr-service-start">启动服务</button><button id="asr-service-stop" hidden>终止服务</button><span id="asr-service-state">检查服务…</span></div>`;panel.querySelector('h3').after(service);
 const more=d.createElement('div');more.innerHTML=`<div class="asr-meter-row"><meter id="asr-level" min="0" max="100" value="0" aria-label="麦克风电平"></meter><span id="asr-level-label">未录音</span></div><p id="asr-audio-save"></p><p id="asr-file-save"></p><p id="asr-polish-state"></p><button id="asr-raw">查看逐字稿</button><button id="asr-retry">重试润色</button><details id="asr-settings"><summary>在线润色设置</summary><p><small>只发送确认的逐字稿片段到所配置的在线服务。API Key 使用${secretStore}加密保存。</small></p><label>API Base URL<input id="asr-api-url" type="url" placeholder="https://服务地址/v1" autocomplete="off"></label><label>模型名称<input id="asr-api-model" autocomplete="off"></label><label>API Key<input id="asr-api-key" type="password" autocomplete="new-password" placeholder="填写 API Key"></label><label>润色提示词<textarea id="asr-api-prompt" rows="5"></textarea></label><button id="asr-config-save">保存配置</button><button id="asr-config-test">测试已保存配置</button><p id="asr-config-state"></p></details><details id="asr-online"><summary>在线 ASR 模型设置</summary><p><small>启用后，录音片段会发送到所配置的在线服务识别，不再需要本地模型；本地仍保存完整录音。API Key 使用${secretStore}加密保存。</small></p><p id="asr-online-mode"></p><label>接口类型<select id="asr-online-protocol"><option value="chat">Chat Completions（input_audio，如 Qwen3-ASR）</option><option value="transcriptions">Audio Transcriptions（如 Whisper、SenseVoice）</option></select></label><label>API Base URL<input id="asr-online-url" type="url" placeholder="https://服务地址/v1" autocomplete="off"></label><label>模型名称<input id="asr-online-model" autocomplete="off"></label><label>API Key<input id="asr-online-key" type="password" autocomplete="new-password" placeholder="填写 API Key"></label><button id="asr-online-enable">保存并启用</button><button id="asr-online-disable">停用，改回本地模型</button><button id="asr-online-test">测试已保存配置</button><p id="asr-online-state"></p></details><details id="asr-manage"><summary>配置管理</summary><div class="asr-group"><b>资料记录目录</b><p><small>新录音的 WAV 音频和原始逐字稿保存在此目录，不放在 Markdown 文档旁边。选择后立即保存，只对之后开始的录音生效，已有录音不会移动。</small></p><label>当前目录<input id="asr-storage-dir" readonly spellcheck="false" placeholder="启动服务后显示"></label><button id="asr-storage-pick">选择目录</button><button id="asr-storage-default">恢复默认</button><p id="asr-storage-state"></p></div><div class="asr-group"><b>配置导出 / 导入</b><p><small>导出或恢复：在线润色设置、在线 ASR 模型设置、资料记录目录。导出文件包含明文 API Key，请妥善保管。</small></p><button id="asr-config-export">配置导出</button><button id="asr-config-import">配置导入</button><p id="asr-transfer-state"></p></div></details><details id="asr-raw-view"><summary>原始逐字稿（只读）</summary><button id="asr-raw-open">打开逐字稿</button><pre id="asr-raw-text"></pre></details>`;
 panel.querySelector('#asr-status').after(more);
 const style=d.createElement('style');style.textContent=`#asr-panel{width:380px}#asr-panel label{display:block;margin:8px 0;font-size:12px}#asr-panel input,#asr-panel textarea,#asr-panel label select{display:block;box-sizing:border-box;width:100%;padding:8px;border:1px solid #8885;border-radius:5px;background:var(--bg-color,#fff);color:inherit}#asr-panel details{margin-top:12px;border-top:1px solid #8884;padding-top:10px}#asr-panel summary{cursor:pointer}#asr-panel .asr-group{margin-top:10px}#asr-panel .asr-group+.asr-group{border-top:1px dashed #8884;padding-top:8px}#asr-panel .asr-group>b{font-size:13px}#asr-level{width:130px;height:18px;margin-right:8px}.asr-meter-row{display:flex;align-items:center;margin-top:10px}#asr-level-label,#asr-service-state,#asr-file-save,#asr-audio-save,#asr-polish-state,#asr-config-state,#asr-online-state,#asr-storage-state,#asr-transfer-state,#asr-online-mode{font-size:12px}#asr-raw-text{white-space:pre-wrap;overflow-wrap:anywhere;max-height:300px;overflow:auto;font-size:12px}#asr-polish-state{color:#947029}`;d.head.append(style);
 async function loadConfig(){const c=await client.request('GET','/polish/config');configured=c.hasKey;$('asr-api-url').value=c.baseUrl;$('asr-api-model').value=c.model;$('asr-api-prompt').value=c.prompt;$('asr-api-key').value='';$('asr-api-key').placeholder=c.hasKey?'已保存，留空保留原密钥':'填写 API Key';return c;}
 let storage=null;
 async function loadStorage(){storage=await client.request('GET','/storage/config');$('asr-storage-dir').value=storage.recordDirectory;$('asr-storage-state').textContent=storage.isDefault?'当前使用默认目录':`默认目录：${storage.defaultDirectory}`;return storage;}
 async function saveStorage(directory){
  for(const id of ['asr-storage-pick','asr-storage-default'])$(id).disabled=true;
  try{await client.request('POST','/storage/config',{recordDirectory:directory});await loadStorage();$('asr-storage-state').textContent=`已保存：${storage.recordDirectory}。之后开始的录音将保存到这里。`;}
  catch(e){$('asr-storage-state').textContent=e.message;}
  finally{for(const id of ['asr-storage-pick','asr-storage-default'])$(id).disabled=false;}
 }
 let onlineAsr=false;
 async function loadAsrConfig(){const c=await client.request('GET','/asr/config');onlineAsr=c.enabled;$('asr-online-protocol').value=c.protocol||'chat';$('asr-online-url').value=c.baseUrl;$('asr-online-model').value=c.model;$('asr-online-key').value='';$('asr-online-key').placeholder=c.hasKey?'已保存，留空保留原密钥':'填写 API Key';$('asr-online-mode').textContent=c.enabled?`当前使用在线 ASR：${c.model}`:'当前使用本地模型';$('asr-online-disable').disabled=!c.enabled;return c;}
 function asrForm(enabled){return {enabled,protocol:$('asr-online-protocol').value,baseUrl:$('asr-online-url').value.trim(),model:$('asr-online-model').value.trim(),apiKey:$('asr-online-key').value.trim()};}
 function asrBusy(on){for(const id of ['asr-online-enable','asr-online-disable','asr-online-test'])$(id).disabled=on;}
 function serviceButtons(){
  $('asr-service-start').disabled=starting||stopping||(online&&modelReady);
  $('asr-service-stop').hidden=!online&&!stopping;
  $('asr-service-stop').disabled=starting||stopping;
 }
 async function check(){
  if(checking||disposed||starting||stopping)return;checking=true;
  try{
   const health=await client.request('GET','/health');online=true;serviceButtons();
   if(health.protocolVersion<2){$('asr-service-state').textContent='请重启服务以加载新版';return;}
   const m=await client.request('GET','/model-health');modelReady=m.ready;
   $('asr-mode').textContent=(m.online?'在线识别':'本地识别')+' · 在线润色后自动补充';
   if(!starting&&!stopping)$('asr-service-state').textContent=m.online?'转写服务就绪 · 在线 ASR':m.ready?'转写服务与模型就绪':'转写服务就绪 · 模型未就绪';
  }catch{online=false;if(!starting&&!stopping)$('asr-service-state').textContent='服务未启动';}
  finally{checking=false;serviceButtons();}
 }
 $('asr-service-start').onclick=async()=>{
  if(starting||stopping)return;starting=true;serviceButtons();$('asr-service-state').textContent='正在启动模型与服务…';
  try{await launcher.start();await hooks.refreshDevices();await loadConfig();await loadAsrConfig().catch(()=>{});await loadStorage().catch(()=>{});}
  catch(e){hooks.status(e.message);}
  finally{starting=false;await check();serviceButtons();}
 };
 $('asr-service-stop').onclick=async()=>{
  if(starting||stopping)return;stopping=true;serviceButtons();$('asr-service-state').textContent='正在终止服务…';
  try{await hooks.terminateService(()=>launcher.stop());online=false;}
  catch(e){hooks.status(e.message);}
  finally{stopping=false;await check();serviceButtons();}
 };
 $('asr-settings').addEventListener('toggle',()=>{if($('asr-settings').open)loadConfig().catch(e=>{$('asr-config-state').textContent=e.message});});
 $('asr-config-save').onclick=async()=>{const button=$('asr-config-save');button.disabled=true;try{await client.request('POST','/polish/config',{baseUrl:$('asr-api-url').value.trim(),model:$('asr-api-model').value.trim(),apiKey:$('asr-api-key').value.trim(),prompt:$('asr-api-prompt').value});await loadConfig();$('asr-config-state').textContent='已加密保存。修改配置后，可点击重试润色处理失败片段。';}catch(e){$('asr-config-state').textContent=e.message;}finally{button.disabled=false;}};
 $('asr-manage').addEventListener('toggle',()=>{if($('asr-manage').open)loadStorage().catch(e=>{$('asr-storage-state').textContent=e.message});});
 // Choosing a folder saves it immediately as the record-data directory.
 $('asr-storage-pick').onclick=async()=>{
  const button=$('asr-storage-pick');if(button.disabled)return;button.disabled=true;
  try{
   const current=(storage||await loadStorage().catch(()=>null))?.recordDirectory||'';
   $('asr-storage-state').textContent='请在弹出的窗口中选择目录…';
   const chosen=await pickFolder(w,{title:'选择资料记录目录',defaultPath:current&&(host?await host.isDirectory(current):fs.existsSync(current))?current:''});
   if(!chosen){$('asr-storage-state').textContent='未更改目录。';return;}
   await saveStorage(chosen);
  }catch(e){$('asr-storage-state').textContent=e.message;}
  finally{button.disabled=false;}
 };
 $('asr-storage-default').onclick=()=>saveStorage('');
 // Settings transfer: the exported JSON contains the API keys in plain text.
 function transferBusy(on){for(const id of ['asr-config-export','asr-config-import'])$(id).disabled=on;}
 $('asr-config-export').onclick=async()=>{
  if($('asr-config-export').disabled)return;transferBusy(true);const state=$('asr-transfer-state');
  try{
   const data=await client.request('GET','/config/export');
   const now=new Date(),pad=n=>String(n).padStart(2,'0');
   const name=`TyporaASR-配置-${now.getFullYear()}${pad(now.getMonth()+1)}${pad(now.getDate())}-${pad(now.getHours())}${pad(now.getMinutes())}.json`;
   state.textContent='请在弹出的窗口中选择保存位置…';
   let file=await pickSaveJson(w,{title:'导出配置',defaultPath:path.join(node(w,'os').homedir(),'Documents',name)});
   if(!file){state.textContent='已取消导出。';return;}
   if(!/\.json$/i.test(file))file+='.json';
   fs.writeFileSync(file+'.tmp',JSON.stringify(data,null,2)+'\n','utf8');fs.renameSync(file+'.tmp',file);if(host)await host.flush();
   state.textContent=`已导出到 ${file}。文件包含明文 API Key，请妥善保管。`;
  }catch(e){state.textContent='导出失败：'+e.message;}
  finally{transferBusy(false);}
 };
 $('asr-config-import').onclick=async()=>{
  if($('asr-config-import').disabled)return;transferBusy(true);const state=$('asr-transfer-state');
  try{
   state.textContent='请在弹出的窗口中选择配置文件…';
   const file=await pickOpenJson(w,{title:'导入配置',defaultPath:path.join(node(w,'os').homedir(),'Documents')+path.sep});
   if(!file){state.textContent='已取消导入。';return;}
   if(fs.statSync(file).size>1024*1024)throw new Error('配置文件过大');
   let data;try{data=JSON.parse(fs.readFileSync(file,'utf8').replace(/^\uFEFF/,''));}catch(_){throw new Error('文件不是有效的 JSON');}
   state.textContent='正在导入（启用在线 ASR 时会先做连接测试，最多约 30 秒）…';
   const result=await client.request('POST','/config/import',data);
   await Promise.all([loadConfig(),loadAsrConfig(),loadStorage()].map(p=>p.catch(()=>{})));check();
   state.textContent=(result?.messages||['已导入。']).join(' ');
  }catch(e){state.textContent='导入失败：'+e.message;}
  finally{transferBusy(false);}
 };
 $('asr-online').addEventListener('toggle',()=>{if($('asr-online').open)loadAsrConfig().catch(e=>{$('asr-online-state').textContent=e.message});});
 $('asr-online-enable').onclick=async()=>{asrBusy(true);$('asr-online-state').textContent='正在保存，并用 1 秒静音测试在线 ASR…';try{await client.request('POST','/asr/config',asrForm(true));$('asr-online-state').textContent='已启用在线 ASR，后续录音片段将由在线模型识别。';}catch(e){$('asr-online-state').textContent=e.message;}finally{await loadAsrConfig().catch(()=>{});asrBusy(false);$('asr-online-disable').disabled=!onlineAsr;check();}};
 $('asr-online-disable').onclick=async()=>{asrBusy(true);try{await client.request('POST','/asr/config',asrForm(false));$('asr-online-state').textContent='已停用在线 ASR。本地模型未运行时，请点击“启动服务”启动本地模型。';}catch(e){$('asr-online-state').textContent=e.message;}finally{await loadAsrConfig().catch(()=>{});asrBusy(false);$('asr-online-disable').disabled=!onlineAsr;check();}};
 $('asr-online-test').onclick=async()=>{asrBusy(true);$('asr-online-state').textContent='正在发送 1 秒静音测试…';try{await client.request('POST','/asr/test',{});$('asr-online-state').textContent='在线 ASR 连接测试通过';}catch(e){$('asr-online-state').textContent=e.message;}finally{asrBusy(false);$('asr-online-disable').disabled=!onlineAsr;}};
 $('asr-config-test').onclick=async()=>{const button=$('asr-config-test');button.disabled=true;$('asr-config-state').textContent='正在发送固定测试文本…';try{await client.request('POST','/polish/test',{});$('asr-config-state').textContent='连接测试通过';}catch(e){$('asr-config-state').textContent=e.message;}finally{button.disabled=false;}};
 $('asr-retry').onclick=async()=>{const s=hooks.session();if(!s)return;try{hooks.status('等待当前润色请求完成，再重试未完成片段…');await client.request('POST',`/sessions/${s.sessionId}/polish/retry`,{});hooks.replayFailed?.(s.sessionId);hooks.status('未完成片段已使用当前配置重新排队，已完成结果保持不变');}catch(e){hooks.status(e.message);}};
 $('asr-raw').onclick=async()=>{const s=hooks.session();if(!s)return;try{const raw=await client.request('GET',`/sessions/${s.sessionId}/transcript`);$('asr-raw-text').textContent=raw.text;$('asr-raw-view').open=true;}catch(e){hooks.status(e.message);}}; async function resolveTranscriptFile(){
  const doc=w.File&&w.File.bundle&&w.File.bundle.filePath;
  if(!doc)throw new Error('请先保存并打开 Markdown 文档');
  const prefix=path.basename(doc,path.extname(doc))+'.逐字稿-';const dirs=[];
  try{dirs.push((storage||await loadStorage()).recordDirectory);}catch(_){/* service unavailable: only the legacy location */}
  dirs.push(path.dirname(doc));
  for(const dir of dirs){
   let names=[];try{names=(host?await host.listDir(dir):fs.readdirSync(dir)).filter(n=>n.startsWith(prefix)&&n.endsWith('.md'));}catch(_){continue;}
   if(names.length){names.sort();return path.join(dir,names[names.length-1]);}
  }
  throw new Error('资料记录目录中没有当前文档的逐字稿文件');
 }
 async function openTranscriptFile(){ try{ hooks.status('\u6b63\u5728\u6253\u5f00\u9010\u5b57\u7a3f\u2026'); let file=null; const s=hooks.session(); if(s&&s.sessionId){ try{ const raw=await client.request('GET',`/sessions/${s.sessionId}/transcript`); if(raw&&raw.text)$('asr-raw-text').textContent=raw.text; $('asr-raw-view').open=true; if(raw&&raw.path&&(host?await host.exists(raw.path):fs.existsSync(raw.path)))file=raw.path; }catch(_){/* fall back to searching the record directory */} } if(!file)file=await resolveTranscriptFile(); $('asr-raw-view').open=true; const abs=path.resolve(file); if(host){ await host.openPath(abs); }else{ let electron=null; try{ electron=node(w,'electron'); }catch(_){/* no electron module */} let err=''; if(electron&&electron.shell&&electron.shell.openPath){ err=await electron.shell.openPath(abs); }else{ err='no shell.openPath'; } if(err){ node(w,'child_process').execFileSync('cmd.exe',['/c','start','',abs],{windowsHide:true,stdio:'ignore'}); } } hooks.status('\u5df2\u6253\u5f00\uff1a'+path.basename(abs)); }catch(e){hooks.status(e.message||String(e));} } more.addEventListener('click',function(ev){ const t=ev.target; if(!t||!t.closest)return; if(t.closest('#asr-raw-open')){ev.preventDefault();openTranscriptFile();} }); 
 const normalize=s=>(s||'').replace(/^\uFEFF/,'').replace(/\r\n/g,'\n');
 function render(s,pending,toSave){
  $('asr-raw').disabled=$('asr-retry').disabled=!s;
  const rms=s?.recording?s.rms||0:0,peak=s?.recording?s.peak||0:0;
  $('asr-level').value=rms?Math.max(0,100+20*Math.log10(rms)*100/60):0;
  if(s?.recording && rms<0.002){quietSince ||= Date.now();}else quietSince=0;
  $('asr-level-label').textContent=!s?.recording?(s?.paused?'已暂停':'未录音'):quietSince&&Date.now()-quietSince>5000?'暂未检测到声音':`峰值 ${peak?Math.round(20*Math.log10(peak)):'−∞'} dBFS`;
  $('asr-audio-save').textContent=s?`音频已落盘 ${Math.floor((s.audioSavedSamples||0)/16000)} 秒 · 待识别 ${s.pending} 段`:'音频：尚未开始';
  $('asr-polish-state').textContent=!configured?'等待在线润色配置 · 原始逐字稿仍会保存':s?`待润色 ${s.polish?.pending||0} 段 · 失败 ${s.polish?.failed||0} 段 · 待入文/确认 ${pending} 段${s.polish?.requestSeconds!=null?` · 上次润色 ${s.polish.requestSeconds} 秒，说话到出结果 ${s.polish.delaySeconds??'-'} 秒`:''}${s.polish?.error?' · '+s.polish.error:''}`:'在线润色已配置';
  const bundle=w.File.bundle,file=bundle?.filePath;let save;
  try{if(!file)save='Markdown：尚未保存文件';else if(w.File.inSavingProcess)save='Markdown：保存中…';else{const disk=normalize(fs.readFileSync(file,'utf8'));const current=normalize(w.File.editor.getMarkdown());if(disk===current)save=`Markdown：已保存 ${fs.statSync(file).mtime.toLocaleTimeString()}${toSave?' · 等待确认':''}`;else if(disk!==normalize(bundle.savedContent))save='Markdown：磁盘有外部修改，请处理冲突';else save=`Markdown：有未保存修改${toSave?' · '+toSave+' 段转写待保存':''}`;}}catch{save='Markdown：无法核验磁盘保存状态';}
  $('asr-file-save').textContent=save;
 }
 check();loadConfig().catch(()=>{});loadAsrConfig().catch(()=>{});loadStorage().catch(()=>{});const timer=w.setInterval(()=>{check();render(hooks.session(),hooks.pending(),hooks.toSave());},1500);
 return {render,dispose(){disposed=true;w.clearInterval(timer);style.remove();}};
};
