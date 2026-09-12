'use strict';
class ServiceLauncher {
 constructor(script,spawn){this.script=script;this.spawn=spawn;this.pending=null;}
 start(){if(this.pending)return this.pending;this.pending=new Promise((resolve,reject)=>{const child=this.spawn('powershell.exe',['-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',this.script],{windowsHide:true,shell:false,stdio:'ignore'});child.once('error',()=>reject(new Error('无法启动 PowerShell，请检查启动脚本')));child.once('exit',code=>code===0?resolve():reject(new Error('启动失败，请检查项目 artifacts 下的服务日志')));}).finally(()=>{this.pending=null});return this.pending;}
}
module.exports={ServiceLauncher};
