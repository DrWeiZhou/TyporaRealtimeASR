'use strict';
class ServiceLauncher {
 constructor(script,spawn,stopScript){this.script=script;this.stopScript=stopScript;this.spawn=spawn;this.pending=null;this.action=null;}
 start(){return this.run('start',this.script);}
 stop(){return this.run('stop',this.stopScript);}
 run(action,script){
  if(this.pending)return this.action===action?this.pending:Promise.reject(new Error(this.action==='start'?'正在启动，请稍后终止':'正在终止，请稍后启动'));
  if(!script)return Promise.reject(new Error('未配置服务控制脚本'));
  this.action=action;
  this.pending=new Promise((resolve,reject)=>{
   const child=this.spawn('powershell.exe',['-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',script],{windowsHide:true,shell:false,stdio:'ignore'});
   child.once('error',()=>reject(new Error('无法启动 PowerShell，请检查服务控制脚本')));
   child.once('exit',code=>code===0?resolve():reject(new Error(action==='start'?'启动失败，请检查项目 artifacts 下的服务日志':'终止失败，请检查 artifacts/stop.stderr.log；旧版服务需先升级')));
  }).finally(()=>{this.pending=null;this.action=null});return this.pending;
 }
}
module.exports={ServiceLauncher};
