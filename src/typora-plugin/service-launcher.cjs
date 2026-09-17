'use strict';
// Runs the fixed service control scripts. Windows: hidden PowerShell via child_process.spawn.
// macOS: pass runner (script => Promise) — the Typora bridge runs tools/mac/*.sh with /bin/bash.
class ServiceLauncher {
 constructor(script,spawn,stopScript,runner){this.script=script;this.stopScript=stopScript;this.spawn=spawn;this.runner=runner||null;this.pending=null;this.action=null;}
 start(){return this.run('start',this.script);}
 stop(){return this.run('stop',this.stopScript);}
 failure(action,detail){
  const logs=this.runner?'artifacts/service.stderr.log 或 artifacts/start.log':'项目 artifacts 下的服务日志';
  const text=action==='start'?`启动失败，请检查${logs}`:'终止失败，请检查 artifacts/stop.stderr.log；旧版服务需先升级';
  return new Error(detail?`${text}：${detail}`:text);
 }
 run(action,script){
  if(this.pending)return this.action===action?this.pending:Promise.reject(new Error(this.action==='start'?'正在启动，请稍后终止':'正在终止，请稍后启动'));
  if(!script)return Promise.reject(new Error('未配置服务控制脚本'));
  this.action=action;
  this.pending=(this.runner
   ? Promise.resolve().then(()=>this.runner(script)).catch(e=>{throw this.failure(action,String(e?.message||e).split('\n').filter(Boolean).pop());})
   : new Promise((resolve,reject)=>{
    const child=this.spawn('powershell.exe',['-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',script],{windowsHide:true,shell:false,stdio:'ignore'});
    child.once('error',()=>reject(new Error('无法启动 PowerShell，请检查服务控制脚本')));
    child.once('exit',code=>code===0?resolve():reject(this.failure(action)));
   })).finally(()=>{this.pending=null;this.action=null});return this.pending;
 }
}
module.exports={ServiceLauncher};
