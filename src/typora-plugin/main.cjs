'use strict';
const {EditorAdapter}=require('./editor-adapter.cjs');
const {TranscriptController}=require('./controller.cjs');
const {ServiceClient}=require('./client.cjs');

module.exports=function mount(w,config){
 if(w.typoraRealtimeAsr)return w.typoraRealtimeAsr;
 const d=w.document,crypto=w.reqnode('crypto'),fs=w.reqnode('fs'),path=w.reqnode('path');
 const client=new ServiceClient(w,config.connectionFile);
 const stateFile=path.join(path.dirname(config.connectionFile),'plugin-documents.json');
 let documents={};try{documents=JSON.parse(fs.readFileSync(stateFile,'utf8'))}catch{}
 let adapter,controller,session,serviceStopping=false,busy=false,controlBusy=false,controlRevision=0,replayRevision=0,after=0,lastSave=0,lastStatus='',pending=new Map(),toSave=new Map(),observed=new Set(),retrying=new Set(),lastPolishCompleted;
 const style=d.createElement('style');style.textContent=`
 #asr-toggle{position:fixed;right:22px;bottom:24px;z-index:9999;border:0;border-radius:24px;padding:12px 18px;background:#235c4b;color:white;box-shadow:0 3px 16px #0003;cursor:pointer}
 #asr-panel{position:fixed;right:20px;top:70px;width:340px;max-height:78vh;overflow:auto;z-index:9998;background:var(--bg-color,#fff);color:var(--text-color,#222);border:1px solid #8885;border-radius:14px;padding:20px;box-shadow:0 8px 35px #0002;font-family:system-ui,sans-serif;font-size:14px}
 #asr-panel h3{font-size:20px;margin:0 0 12px;padding-right:80px}#asr-panel #asr-minimize,#asr-panel #asr-close{position:absolute;top:12px;margin:0;font-size:18px;line-height:1;padding:7px;width:32px;height:32px}#asr-panel #asr-minimize{right:52px}#asr-panel #asr-close{right:14px}#asr-panel.asr-minimized{width:220px;max-height:none;overflow:hidden;padding:10px 12px;border-radius:6px}#asr-panel.asr-minimized>:not(h3):not(#asr-minimize):not(#asr-close){display:none}#asr-panel.asr-minimized h3{font-size:14px;line-height:32px;margin:0;white-space:nowrap}#asr-panel.asr-minimized #asr-minimize,#asr-panel.asr-minimized #asr-close{top:10px}#asr-preview{margin-top:10px}#asr-panel p{margin:10px 0;line-height:1.65}#asr-panel button,#asr-panel select{padding:7px 10px;margin:4px 4px 4px 0;border:1px solid #8886;border-radius:6px;background:transparent;color:inherit;cursor:pointer}#asr-panel button:disabled{opacity:.4;cursor:default}
 #asr-preview{padding:12px;border-left:3px solid #458a71;min-height:65px;background:#458a7110;white-space:pre-wrap}#asr-status{color:#697870;font-size:12px}#asr-review>div{border-top:1px solid #8884;margin-top:12px;padding-top:8px}#asr-panel small{color:#777}#asr-devices{max-width:100%}
 `;d.head.appendChild(style);
 const panel=d.createElement('section');panel.id='asr-panel';panel.innerHTML=`<h3>语音记录</h3><button id="asr-minimize" title="缩小面板" aria-label="缩小面板" aria-expanded="true">−</button><button id="asr-close" title="关闭窗口" aria-label="关闭窗口">×</button><small id="asr-mode">正在检测识别方式 · 在线润色后自动补充</small><div id="asr-preview" role="status">当前句将在这里实时显示。</div><p id="asr-target">尚未绑定文档</p><select id="asr-devices" aria-label="录音设备"><option value="-1">默认麦克风</option></select><div><button id="asr-start">开始录音</button><button id="asr-pause" disabled>暂停</button><button id="asr-stop" disabled>结束录音</button><button id="asr-recover">恢复记录</button></div><p id="asr-status">准备就绪。Ctrl+Alt+R 展开或缩小面板。</p><div id="asr-review"></div>`;
 const toggle=d.createElement('button');toggle.id='asr-toggle';toggle.textContent='语音记录';d.body.append(panel,toggle);
 const $=id=>panel.querySelector('#'+id);
 const status=text=>{$('asr-status').textContent=text;lastStatus=text};
 const showError=e=>status(e.message||String(e));
 let view='expanded';
 function setView(next){
  view=next;panel.hidden=next==='closed';panel.classList.toggle('asr-minimized',next==='compact');toggle.hidden=next!=='expanded';
  const minimized=next==='compact',button=$('asr-minimize');
  button.textContent=minimized?'□':'−';button.title=minimized?'展开面板':'缩小面板';button.setAttribute('aria-label',button.title);button.setAttribute('aria-expanded',String(next==='expanded'));
  if(next!=='closed')button.focus();
 }
 toggle.onclick=()=>setView(view==='expanded'?'compact':'expanded');
 $('asr-minimize').onclick=()=>setView(view==='compact'?'expanded':'compact');
 $('asr-close').onclick=()=>{
  if(w.confirm('关闭后，可按 Ctrl + Alt + R 恢复显示。\n录音和服务会继续运行。\n确定关闭语音记录窗口吗？'))setView('closed');
 };
 const onKeyDown=e=>{if(e.ctrlKey&&e.altKey&&e.code==='KeyR'){e.preventDefault();setView(view==='expanded'?'compact':'expanded');}};
 d.addEventListener('keydown',onKeyDown);
 const awaitingPolish=s=>Math.max(0,(s?.polish?.pending||0)-(s?.polish?.failed||0));
 const ack=(eventId,state)=>client.request('POST',`/sessions/${session.sessionId}/ack`,{eventId,state});
 function bind(documentId){adapter?.dispose();adapter=new EditorAdapter(w,documentId,path.dirname(config.connectionFile));controller=new TranscriptController(adapter,ack,adapter.path());$('asr-target').textContent='记录到：'+path.basename(adapter.path());}
 function remember(documentId){documents[adapter.path().toLowerCase()]={documentId,sessionId:session.sessionId};fs.mkdirSync(path.dirname(stateFile),{recursive:true});const tmp=stateFile+'.'+client.id+'.tmp';fs.writeFileSync(tmp,JSON.stringify(documents,null,2));fs.renameSync(tmp,stateFile);}
 async function refreshDevices(){try{const devices=await client.request('GET','/devices');const sel=$('asr-devices');sel.replaceChildren();const mic=d.createElement('optgroup');mic.label='麦克风';const sys=d.createElement('optgroup');sys.label='系统声音';for(const x of devices){const o=d.createElement('option');o.value=x.id;o.textContent=x.name;(x.kind==='system'?sys:mic).appendChild(o);}if(mic.childElementCount)sel.appendChild(mic);if(sys.childElementCount)sel.appendChild(sys);}catch(e){showError(e)}}
async function start(){
  if(serviceStopping||busy||controlBusy)return;busy=true;$('asr-start').disabled=true;
  try {
   if(session?.recording||session?.paused||awaitingPolish(session)||pending.size||toSave.size)throw new Error('请先处理上一会话的待写入或未保存内容');
   const file=w.File.bundle?.filePath;if(!file)throw new Error('请先保存 Markdown 文档');
   const documentId=documents[file.toLowerCase()]?.documentId||crypto.randomUUID();bind(documentId);adapter.bind();
   if(!await adapter.save())throw new Error('插入位置尚未保存，请回到 Typora 并保存文档后重试');
   const health=await client.request('GET','/health');if(health.protocolVersion<2)throw new Error('请重启服务以加载新版');
   status('正在检查模型并预热…');
   const newId=crypto.randomUUID();
   session=await client.request('POST','/sessions',{sessionId:newId,documentId,path:file,device:Number($('asr-devices').value)});
   after=0;pending.clear();toSave.clear();observed.clear();retrying.clear();lastPolishCompleted=undefined;remember(documentId);$('asr-stop').disabled=false;status('正在录音，你可以同时编辑已有内容。');
  }catch(e){showError(e);$('asr-start').disabled=false}finally{busy=false}
 }
 async function stop(){if(!session||controlBusy)return;controlBusy=true;controlRevision++;$('asr-pause').disabled=$('asr-stop').disabled=true;try{status('正在保存尾音…');session=await client.request('POST',`/sessions/${session.sessionId}/stop`,{});$('asr-stop').disabled=true;status('录音已停止，正在处理剩余音频。')}catch(e){showError(e)}finally{controlBusy=false;$('asr-pause').disabled=$('asr-stop').disabled=!(session?.recording||session?.paused)}}
 async function pause(){if(!session||controlBusy)return;controlBusy=true;controlRevision++;$('asr-pause').disabled=$('asr-stop').disabled=true;try{session=await client.request('POST',`/sessions/${session.sessionId}/${session.paused?'resume':'pause'}`,{device:Number($('asr-devices').value)});status(session.paused?'已暂停；已有音频继续识别和润色':'已续录');}catch(e){showError(e)}finally{controlBusy=false;$('asr-pause').disabled=$('asr-stop').disabled=!(session?.recording||session?.paused)}}
 async function recover(id){
   if(serviceStopping||busy||controlBusy)return;busy=true;try{
    if(session?.recording||session?.paused)throw new Error('请先停止当前录音');
    const options=(await client.request('GET','/sessions')).filter(s=>s.path.toLowerCase()===(w.File.bundle?.filePath||'').toLowerCase());
    if(!id){
      const area=$('asr-review');area.replaceChildren();
      if(!options.length){status('当前文件没有可恢复的会话');return;}
      for(const s of options){const button=d.createElement('button');button.textContent='恢复会话 '+s.sessionId.slice(0,8);button.onclick=()=>recover(s.sessionId);area.append(button)}
      status('选择之前的会话；无法确定是否已插入的内容会要求你确认。');return;
    }
    const target=options.find(s=>s.sessionId===id);if(!target)throw new Error('会话不属于当前文档');
    session=await client.request('POST',`/sessions/${id}/recover`,{});bind(target.documentId);
    adapter.bind();if(!await adapter.save())throw new Error('请先保存当前文档后恢复');
    after=0;pending.clear();toSave.clear();observed.clear();retrying.clear();lastPolishCompleted=undefined;remember(target.documentId);$('asr-review').replaceChildren();status('正在恢复…');
   }catch(e){showError(e)}finally{busy=false}
 }
 function review(event){
   const area=$('asr-review');if(area.querySelector(`[data-event="${event.eventId}"]`))return;
   const card=d.createElement('div');card.dataset.event=event.eventId;const text=d.createElement('p');text.textContent=event.text;
   const failed=event.polishState==='failed';
   const note=d.createElement('small');note.textContent=failed?'在线润色多次失败：可插入未润色原文、忽略，或点击“重试润色”':event.needsReview?'长句边界需要确认':'此段可能已插入或被人工删除，请核对后选择';
   const target=failed?{...event,polishState:'ready',blocks:[{topic:'continue',title:'',paragraphs:[event.text]}]}:event;
   const insert=d.createElement('button');insert.textContent=failed?'插入原文':'插入此段';insert.onclick=async()=>{if(serviceStopping||busy||controlBusy)return;busy=true;try{const result=await controller.apply(target,true);if(result!=='deferred'){pending.delete(event.eventId);toSave.set(event.eventId,event);card.remove()}}catch(e){showError(e)}finally{busy=false}};
   const ignore=d.createElement('button');ignore.textContent='忽略';ignore.onclick=async()=>{if(serviceStopping||busy||controlBusy)return;busy=true;try{await ack(event.eventId,'deleted');pending.delete(event.eventId);card.remove()}catch(e){showError(e)}finally{busy=false}};
   card.append(note,text,insert,ignore);area.append(card);
 }
 async function tick(){
   if(serviceStopping||busy||controlBusy||!session)return;busy=true;
   try {
    const revision=controlRevision,id=session.sessionId;const fresh=await client.request('GET',`/sessions/${id}`);
    if(revision!==controlRevision||id!==session.sessionId)return;session=fresh;
    if(retrying.size&&session.polish?.completed!==lastPolishCompleted)after=0;lastPolishCompleted=session.polish?.completed;
    $('asr-stop').disabled=!(session.recording||session.paused);$('asr-pause').disabled=!(session.recording||session.paused);$('asr-pause').textContent=session.paused?'继续录音':'暂停';$('asr-start').disabled=session.recording||session.paused||session.pending>0||awaitingPolish(session)>0||pending.size>0||toSave.size>0;extra.render(session,pending.size,toSave.size);
    if(session.hypothesis)$('asr-preview').textContent=session.hypothesis.text;
    else if(session.recording&&session.lastFinal)$('asr-preview').textContent=session.lastFinal;
    else $('asr-preview').textContent=session.paused?'已暂停。识别与润色继续处理已有音频。':session.recording?'等待当前句识别…':'录音已结束。';
    if(pending.size<200){const revision=replayRevision;const events=await client.request('GET',`/sessions/${session.sessionId}/events?after=${after}`);if(revision!==replayRevision)return;for(const e of events){if(e.polishState==='failed')retrying.add(e.eventId);else $('asr-review').querySelector(`[data-event="${e.eventId}"]`)?.remove();pending.set(e.eventId,e);after=Math.max(after,e.seq)}}
    if(adapter.path()!==adapter.boundPath){status('已切换文档；录音继续，自动入文已暂停。');return;}
    if(!adapter.checkDisk()){status('检测到磁盘内容变化，已暂停补写。请处理文件冲突后重新恢复会话。');return;}
    if(!adapter.safe()){status('等待中文输入完成或返回普通编辑模式，转写继续保留。');return;}
    for(const id of observed){if(!adapter.contains(id)){await ack(id,'deleted');observed.delete(id);toSave.delete(id)}}
    for(const [id,event] of pending){
      const result=await controller.apply(event);
      if(result==='failed'){retrying.add(id);pending.delete(id);if(event.text)review(event);continue;}
      retrying.delete(id);
      if(result==='deferred')break;
      if(result==='review'){review(event);break;}
      pending.delete(id);if(adapter.contains(id)){toSave.set(id,event);observed.add(id);}
    }
    if(toSave.size && Date.now()-lastSave>100){
      lastSave=Date.now();
      for(const [id] of toSave){if(!adapter.contains(id)){await ack(id,'deleted');toSave.delete(id)}}
      if(toSave.size && await adapter.save()){for(const [id] of toSave){await ack(id,'saved');toSave.delete(id)}}
    }
    status(session.error||`${session.recording?'录音中':session.paused?'已暂停':'录音已结束'} · ${Math.floor(session.seconds)} 秒 · 积累中 ${Math.floor(session.bufferedSeconds||0)} 秒 · 待识别 ${session.pending} 段 · 待润色 ${awaitingPolish(session)} 段 · 待确认 ${pending.size} 段 · 未保存 ${toSave.size} 段`);
   }catch(e){showError(e)}finally{busy=false}
 }
 async function terminateService(terminate){
  if(serviceStopping||controlBusy)throw new Error('正在处理录音操作，请稍后重试');
  serviceStopping=true;
  try {
   while(busy)await new Promise(resolve=>w.setTimeout(resolve,50));
   if(toSave.size){
    if(adapter.path()!==adapter.boundPath||!adapter.safe()||!adapter.checkDisk())throw new Error('请回到绑定文档并处理编辑或磁盘冲突，再终止服务');
    if(!await adapter.save())throw new Error('请先保存当前文档，再终止服务');
    for(const [id] of toSave){await ack(id,adapter.contains(id)?'saved':'deleted');toSave.delete(id);}
   }
   status('正在保存录音并终止服务…');await terminate();session=null;
   $('asr-pause').disabled=$('asr-stop').disabled=true;$('asr-start').disabled=false;
   $('asr-preview').textContent='服务已终止。重新启动服务后可恢复记录。';
   extra.render(null,pending.size,toSave.size);status('服务已终止；未完成片段已保留，重启后可恢复记录。');
  }finally{serviceStopping=false;}
 }
 const extra=require('./panel-controls.cjs')(w,config,client,panel,{terminateService,session:()=>session,pending:()=>pending.size,toSave:()=>toSave.size,status,refreshDevices,replayFailed:id=>{if(id===session?.sessionId){replayRevision++;after=0;}}});
 $('asr-pause').onclick=pause;$('asr-start').onclick=start;$('asr-stop').onclick=stop;$('asr-recover').onclick=()=>recover();
 refreshDevices();const timer=w.setInterval(tick,200);
 const api={start,stop,pause,recover,getState:()=>({session,busy,controlBusy,pending:pending.size,toSave:toSave.size,status:lastStatus}),dispose:()=>{d.removeEventListener('keydown',onKeyDown);w.clearInterval(timer);extra.dispose();adapter?.dispose();panel.remove();toggle.remove();style.remove()}};
 w.typoraRealtimeAsr=api;return api;
};
