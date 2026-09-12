const {test}=require('node:test');const assert=require('node:assert/strict');
const {EventEmitter}=require('node:events');
test('service launch is hidden, uses fixed script and prevents concurrent starts',async()=>{
 const {ServiceLauncher}=require('../../src/typora-plugin/service-launcher.cjs');
 let count=0,child;const spawn=(file,args,options)=>{count++;assert.equal(options.windowsHide,true);assert.equal(options.shell,false);assert.ok(args.includes('C:\\project\\tools\\start.ps1'));child=new EventEmitter();return child;};
 const launcher=new ServiceLauncher('C:\\project\\tools\\start.ps1',spawn);const a=launcher.start(),b=launcher.start();assert.equal(count,1);child.emit('exit',0);await Promise.all([a,b]);
});
