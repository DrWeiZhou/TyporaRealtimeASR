'use strict';
const {ServiceLauncher}=require('./service-launcher.cjs');
module.exports=function controls(w,config,client,panel,hooks){
 const d=w.document,fs=w.reqnode('fs'),path=w.reqnode('path'),spawn=w.reqnode('child_process').spawn;
 const launcher=new ServiceLauncher(path.resolve(path.dirname(config.connectionFile),'..','tools','start.ps1'),spawn,path.resolve(path.dirname(config.connectionFile),'..','tools','stop.ps1'));
 const $=id=>panel.querySelector('#'+id);let checking=false,starting=false,stopping=false,online=false,modelReady=false,configured=false,quietSince=0,disposed=false;
 const service=d.createElement('div');service.innerHTML=`<div class="asr-service"><button id="asr-service-start">启动服务</button><button id="asr-service-stop" hidden>终止服务</button><span id="asr-service-state">检查服务…</span></div>`;panel.querySelector('h3').after(service);
 const more=d.createElement('div');more.innerHTML=`<div class="asr-meter-row"><meter id="asr-level" min="0" max="100" value="0" aria-label="麦克风电平"></meter><span id="asr-level-label">未录音</span></div><p id="asr-audio-save"></p><p id="asr-file-save"></p><p id="asr-polish-state"></p><button id="asr-raw">查看逐字稿</button><button id="asr-retry">重试润色</button><details id="asr-settings"><summary>在线润色设置</summary><p><small>只发送确认的逐字稿片段到所配置的在线服务。API Key 使用当前 Windows 用户加密保存。</small></p><label>API Base URL<input id="asr-api-url" type="url" placeholder="https://服务地址/v1" autocomplete="off"></label><label>模型名称<input id="asr-api-model" autocomplete="off"></label><label>API Key<input id="asr-api-key" type="password" autocomplete="new-password" placeholder="填写 API Key"></label><label>润色提示词<textarea id="asr-api-prompt" rows="5"></textarea></label><button id="asr-config-save">保存配置</button><button id="asr-config-test">测试已保存配置</button><p id="asr-config-state"></p></details><details id="asr-raw-view"><summary>原始逐字稿（只读）</summary><button id="asr-raw-copy">复制逐字稿</button><pre id="asr-raw-text"></pre></details>`;
 panel.querySelector('#asr-status').after(more);
 const style=d.createElement('style');style.textContent=`#asr-panel{width:380px}#asr-panel label{display:block;margin:8px 0;font-size:12px}#asr-panel input,#asr-panel textarea{display:block;box-sizing:border-box;width:100%;padding:8px;border:1px solid #8885;border-radius:5px;background:var(--bg-color,#fff);color:inherit}#asr-panel details{margin-top:12px;border-top:1px solid #8884;padding-top:10px}#asr-panel summary{cursor:pointer}#asr-level{width:130px;height:18px;margin-right:8px}.asr-meter-row{display:flex;align-items:center;margin-top:10px}#asr-level-label,#asr-service-state,#asr-file-save,#asr-audio-save,#asr-polish-state,#asr-config-state{font-size:12px}#asr-raw-text{white-space:pre-wrap;overflow-wrap:anywhere;max-height:300px;overflow:auto;font-size:12px}#asr-polish-state{color:#947029}`;d.head.append(style);
 async function loadConfig(){const c=await client.request('GET','/polish/config');configured=c.hasKey;$('asr-api-url').value=c.baseUrl;$('asr-api-model').value=c.model;$('asr-api-prompt').value=c.prompt;$('asr-api-key').value='';$('asr-api-key').placeholder=c.hasKey?'已保存，留空保留原密钥':'填写 API Key';return c;}
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
   if(!starting&&!stopping)$('asr-service-state').textContent=m.ready?'转写服务与模型就绪':'转写服务就绪 · 模型未就绪';
  }catch{online=false;if(!starting&&!stopping)$('asr-service-state').textContent='服务未启动';}
  finally{checking=false;serviceButtons();}
 }
 $('asr-service-start').onclick=async()=>{
  if(starting||stopping)return;starting=true;serviceButtons();$('asr-service-state').textContent='正在启动模型与服务…';
  try{await launcher.start();await hooks.refreshDevices();await loadConfig();}
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
 $('asr-config-test').onclick=async()=>{const button=$('asr-config-test');button.disabled=true;$('asr-config-state').textContent='正在发送固定测试文本…';try{await client.request('POST','/polish/test',{});$('asr-config-state').textContent='连接测试通过';}catch(e){$('asr-config-state').textContent=e.message;}finally{button.disabled=false;}};
 $('asr-retry').onclick=async()=>{const s=hooks.session();if(!s)return;try{hooks.status('等待当前润色请求完成，再重试未完成片段…');await client.request('POST',`/sessions/${s.sessionId}/polish/retry`,{});hooks.replayFailed?.(s.sessionId);hooks.status('未完成片段已使用当前配置重新排队，已完成结果保持不变');}catch(e){hooks.status(e.message);}};
 $('asr-raw').onclick=async()=>{const s=hooks.session();if(!s)return;try{const raw=await client.request('GET',`/sessions/${s.sessionId}/transcript`);$('asr-raw-text').textContent=raw.text;$('asr-raw-view').open=true;}catch(e){hooks.status(e.message);}};
 $('asr-raw-copy').onclick=async()=>{try{await w.navigator.clipboard.writeText($('asr-raw-text').textContent);hooks.status('逐字稿已复制');}catch{hooks.status('无法访问剪贴板，可在只读区域选中文字复制');}};
 const normalize=s=>(s||'').replace(/^\uFEFF/,'').replace(/\r\n/g,'\n');
 function render(s,pending,toSave){
  $('asr-raw').disabled=$('asr-retry').disabled=!s;
  const rms=s?.recording?s.rms||0:0,peak=s?.recording?s.peak||0:0;
  $('asr-level').value=rms?Math.max(0,100+20*Math.log10(rms)*100/60):0;
  if(s?.recording && rms<0.002){quietSince ||= Date.now();}else quietSince=0;
  $('asr-level-label').textContent=!s?.recording?(s?.paused?'已暂停':'未录音'):quietSince&&Date.now()-quietSince>5000?'暂未检测到声音':`峰值 ${peak?Math.round(20*Math.log10(peak)):'−∞'} dBFS`;
  $('asr-audio-save').textContent=s?`音频已落盘 ${Math.floor((s.audioSavedSamples||0)/16000)} 秒 · 待识别 ${s.pending} 段`:'音频：尚未开始';
  $('asr-polish-state').textContent=!configured?'等待在线润色配置 · 原始逐字稿仍会保存':s?`待润色 ${s.polish?.pending||0} 段 · 失败 ${s.polish?.failed||0} 段 · 待入文/确认 ${pending} 段${s.polish?.error?' · '+s.polish.error:''}`:'在线润色已配置';
  const bundle=w.File.bundle,file=bundle?.filePath;let save;
  try{if(!file)save='Markdown：尚未保存文件';else if(w.File.inSavingProcess)save='Markdown：保存中…';else{const disk=normalize(fs.readFileSync(file,'utf8'));const current=normalize(w.File.editor.getMarkdown());if(disk===current)save=`Markdown：已保存 ${fs.statSync(file).mtime.toLocaleTimeString()}${toSave?' · 等待确认':''}`;else if(disk!==normalize(bundle.savedContent))save='Markdown：磁盘有外部修改，请处理冲突';else save=`Markdown：有未保存修改${toSave?' · '+toSave+' 段转写待保存':''}`;}}catch{save='Markdown：无法核验磁盘保存状态';}
  $('asr-file-save').textContent=save;
 }
 check();loadConfig().catch(()=>{});const timer=w.setInterval(()=>{check();render(hooks.session(),hooks.pending(),hooks.toSave());},1500);
 return {render,dispose(){disposed=true;w.clearInterval(timer);style.remove();}};
};
