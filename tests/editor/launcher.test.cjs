const {test}=require('node:test');const assert=require('node:assert/strict');
const {EventEmitter}=require('node:events');
test('service launch is hidden, uses fixed script and prevents concurrent starts',async()=>{
 const {ServiceLauncher}=require('../../src/typora-plugin/service-launcher.cjs');
 let count=0,child;const spawn=(file,args,options)=>{count++;assert.equal(options.windowsHide,true);assert.equal(options.shell,false);assert.ok(args.includes('C:\\project\\tools\\start.ps1'));child=new EventEmitter();return child;};
 const launcher=new ServiceLauncher('C:\\project\\tools\\start.ps1',spawn);const a=launcher.start(),b=launcher.start();assert.equal(count,1);child.emit('exit',0);await Promise.all([a,b]);
});
test('service stop uses fixed hidden script, deduplicates and reports failure',async()=>{
 const {ServiceLauncher}=require('../../src/typora-plugin/service-launcher.cjs');
 let child,count=0;
 const launcher=new ServiceLauncher('start.ps1',(file,args,options)=>{count++;assert.ok(args.includes('stop.ps1'));assert.equal(options.windowsHide,true);assert.equal(options.shell,false);return child=new EventEmitter();},'stop.ps1');
 const a=launcher.stop(),b=launcher.stop();assert.equal(count,1);
 child.emit('exit',1);await assert.rejects(a,/终止失败/);await assert.rejects(b,/终止失败/);
 const retry=launcher.stop();child.emit('exit',0);await retry;assert.equal(count,2);
});
test('start and stop cannot overlap',async()=>{
 const {ServiceLauncher}=require('../../src/typora-plugin/service-launcher.cjs');let child;
 const launcher=new ServiceLauncher('start.ps1',()=>child=new EventEmitter(),'stop.ps1');
 const start=launcher.start();await assert.rejects(launcher.stop(),/正在启动/);child.emit('exit',0);await start;
 const stop=launcher.stop();await assert.rejects(launcher.start(),/正在终止/);child.emit('exit',0);await stop;
});
