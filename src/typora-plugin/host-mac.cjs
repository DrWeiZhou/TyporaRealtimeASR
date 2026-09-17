'use strict';
// macOS host for the Typora ASR plugin.
// Typora for macOS exposes window.bridge instead of Node:
//  - bridge.callSync('path.readText', file) reads a text file synchronously;
//  - bridge.callHandler('controller.runCommand', {args, cwd}, ([ok, stdout, stderr]) => …) runs a shell command.
// Writes are therefore asynchronous: node('fs').writeFileSync/renameSync/mkdirSync update an in-memory overlay at once
// (so later reads in this window see the new content) and are flushed to disk strictly in order by a shell queue.

const quote=s=>"'"+String(s).replace(/'/g,"'\\''")+"'";

// ---- POSIX path subset ----
const path={
 sep:'/',delimiter:':',
 isAbsolute:p=>String(p).startsWith('/'),
 normalize(p){
  p=String(p);const abs=p.startsWith('/'),out=[];
  for(const part of p.split('/')){if(!part||part==='.')continue;if(part==='..'){if(out.length&&out[out.length-1]!=='..')out.pop();else if(!abs)out.push('..');}else out.push(part);}
  const joined=out.join('/');return abs?'/'+joined:(joined||'.');
 },
 join:(...parts)=>path.normalize(parts.filter(x=>x!=='').join('/')),
 resolve(...parts){let r='';for(const p of parts){if(!p)continue;r=String(p).startsWith('/')?String(p):r+'/'+p;}return path.normalize(r||'/');},
 dirname(p){const s=String(p);p=s.replace(/\/+$/,'');if(!p)return s.startsWith('/')?'/':'.';const i=p.lastIndexOf('/');return i<0?'.':i===0?'/':p.slice(0,i);},
 basename(p,ext){p=String(p).replace(/\/+$/,'');let b=p.slice(p.lastIndexOf('/')+1);if(ext&&b.endsWith(ext)&&b!==ext)b=b.slice(0,-ext.length);return b;},
 extname(p){const b=path.basename(p),i=b.lastIndexOf('.');return i>0?b.slice(i):'';},
};
path.posix=path;

// ---- SHA-256 (sync; WebCrypto digest is async) ----
function sha256Hex(text){
 const bytes=new TextEncoder().encode(text);
 const K=new Uint32Array([0x428a2f98,0x71374491,0xb5c0fbcf,0xe9b5dba5,0x3956c25b,0x59f111f1,0x923f82a4,0xab1c5ed5,0xd807aa98,0x12835b01,0x243185be,0x550c7dc3,0x72be5d74,0x80deb1fe,0x9bdc06a7,0xc19bf174,0xe49b69c1,0xefbe4786,0x0fc19dc6,0x240ca1cc,0x2de92c6f,0x4a7484aa,0x5cb0a9dc,0x76f988da,0x983e5152,0xa831c66d,0xb00327c8,0xbf597fc7,0xc6e00bf3,0xd5a79147,0x06ca6351,0x14292967,0x27b70a85,0x2e1b2138,0x4d2c6dfc,0x53380d13,0x650a7354,0x766a0abb,0x81c2c92e,0x92722c85,0xa2bfe8a1,0xa81a664b,0xc24b8b70,0xc76c51a3,0xd192e819,0xd6990624,0xf40e3585,0x106aa070,0x19a4c116,0x1e376c08,0x2748774c,0x34b0bcb5,0x391c0cb3,0x4ed8aa4a,0x5b9cca4f,0x682e6ff3,0x748f82ee,0x78a5636f,0x84c87814,0x8cc70208,0x90befffa,0xa4506ceb,0xbef9a3f7,0xc67178f2]);
 const H=new Uint32Array([0x6a09e667,0xbb67ae85,0x3c6ef372,0xa54ff53a,0x510e527f,0x9b05688c,0x1f83d9ab,0x5be0cd19]);
 const length=bytes.length,total=Math.ceil((length+9)/64)*64,data=new Uint8Array(total);
 data.set(bytes);data[length]=0x80;
 const view=new DataView(data.buffer);view.setUint32(total-8,Math.floor(length/0x20000000));view.setUint32(total-4,length*8>>>0);
 const W=new Uint32Array(64),rotr=(x,n)=>(x>>>n)|(x<<(32-n));
 for(let off=0;off<total;off+=64){
  for(let i=0;i<16;i++)W[i]=view.getUint32(off+i*4);
  for(let i=16;i<64;i++){const s0=rotr(W[i-15],7)^rotr(W[i-15],18)^(W[i-15]>>>3),s1=rotr(W[i-2],17)^rotr(W[i-2],19)^(W[i-2]>>>10);W[i]=(W[i-16]+s0+W[i-7]+s1)>>>0;}
  let [a,b,c,d,e,f,g,h]=H;
  for(let i=0;i<64;i++){
   const S1=rotr(e,6)^rotr(e,11)^rotr(e,25),ch=(e&f)^(~e&g),T1=(h+S1+ch+K[i]+W[i])>>>0;
   const S0=rotr(a,2)^rotr(a,13)^rotr(a,22),maj=(a&b)^(a&c)^(b&c);
   h=g;g=f;f=e;e=(d+T1)>>>0;d=c;c=b;b=a;a=(T1+S0+maj)>>>0;
  }
  H[0]+=a;H[1]+=b;H[2]+=c;H[3]+=d;H[4]+=e;H[5]+=f;H[6]+=g;H[7]+=h;
 }
 return [...H].map(x=>x.toString(16).padStart(8,'0')).join('');
}

const toBase64=text=>{const bytes=new TextEncoder().encode(text);let bin='';for(let i=0;i<bytes.length;i+=0x8000)bin+=String.fromCharCode(...bytes.subarray(i,i+0x8000));return btoa(bin);};

function createMacHost(w,config={}){
 const bridge=w.bridge;
 if(!bridge||typeof bridge.callSync!=='function'||typeof bridge.callHandler!=='function')throw new Error('当前 Typora 不提供 macOS bridge 接口');
 const home=config.homeDir||'';
 // Commands run through Typora with a minimal environment; keep a predictable PATH.
 const env=`export PATH="${home?home+'/.dotnet:':''}/opt/homebrew/bin:/usr/local/bin:/usr/bin:/bin:/usr/sbin:/sbin:$PATH"; export LANG="\${LANG:-en_US.UTF-8}"; `;
 function run(command,{cwd}={}){
  return new Promise(resolve=>{
   try{
    bridge.callHandler('controller.runCommand',{args:env+command,cwd:cwd||home||'/'},result=>{
     const [ok,stdout,stderr]=Array.isArray(result)?result:[false,'',String(result)];
     resolve({ok:!!ok,stdout:String(stdout??''),stderr:String(stderr??'')});
    });
   }catch(e){resolve({ok:false,stdout:'',stderr:e.message||String(e)});}
  });
 }
 async function runChecked(command,options){const r=await run(command,options);if(!r.ok)throw new Error((r.stderr||r.stdout||'命令执行失败').trim());return r.stdout;}

 // ---- fs overlay + ordered write queue ----
 const overlay=new Map();   // path -> {text} | {deleted:true}
 const stats=new Map();     // path -> {mtime,size,at}
 let queue=Promise.resolve(),generation=0,lastWriteError=null;
 function enqueue(command,paths){
  const id=++generation;
  queue=queue.then(async()=>{
   const r=await run(command);
   if(!r.ok){lastWriteError=new Error('写入失败：'+(r.stderr||r.stdout).trim());console.error('Typora ASR',lastWriteError);}
   // Drop overlay entries whose newest write has reached the disk.
   for(const p of paths){const entry=overlay.get(p);if(entry&&entry.generation===id)overlay.delete(p);}
  });
  return id;
 }
 function readDisk(p){
  let text;
  try{text=bridge.callSync('path.readText',p);}catch(_){text=null;}
  return typeof text==='string'?text:null;
 }
 const enoent=p=>Object.assign(new Error(`ENOENT: no such file or directory, open '${p}'`),{code:'ENOENT'});
 function refreshStat(p){
  const cached=stats.get(p);if(cached&&Date.now()-cached.at<1000)return;
  stats.set(p,{...(cached||{mtime:new Date(),size:0}),at:Date.now()});
  run(`stat -f '%m %z' ${quote(p)}`).then(r=>{const m=r.ok&&r.stdout.trim().match(/^(\d+)\s+(\d+)/);if(m)stats.set(p,{mtime:new Date(Number(m[1])*1000),size:Number(m[2]),at:Date.now()});});
 }
 const fs={
  readFileSync(p){
   const entry=overlay.get(p);
   if(entry){if(entry.deleted)throw enoent(p);return entry.text;}
   const text=readDisk(p);if(text===null)throw enoent(p);return text;
  },
  existsSync(p){
   const entry=overlay.get(p);if(entry)return !entry.deleted;
   const text=readDisk(p);return text!==null&&text!=='';
  },
  writeFileSync(p,data){
   const text=String(data);const id=generation+1;overlay.set(p,{text,generation:id});
   const tmp=p+'.asr-write';
   enqueue(`mkdir -p ${quote(path.dirname(p))} && printf '%s' ${quote(toBase64(text))} | base64 --decode > ${quote(tmp)} && mv -f ${quote(tmp)} ${quote(p)}`,[p]);
  },
  renameSync(from,to){
   const entry=overlay.get(from);const text=entry&&!entry.deleted?entry.text:readDisk(from);
   if(text===null)throw enoent(from);
   const id=generation+1;overlay.set(to,{text,generation:id});overlay.set(from,{deleted:true,generation:id});
   enqueue(`mv -f ${quote(from)} ${quote(to)}`,[from,to]);
  },
  mkdirSync(p){enqueue(`mkdir -p ${quote(p)}`,[]);},
  statSync(p){
   const text=fs.readFileSync(p);refreshStat(p);const s=stats.get(p);
   return {size:new TextEncoder().encode(text).length,mtime:s?.mtime||new Date(),isFile:()=>true,isDirectory:()=>false};
  },
  readdirSync(){throw new Error('macOS 版请使用异步目录读取');},
 };
 const crypto={
  randomUUID:()=>{
   if(w.crypto?.randomUUID)try{return w.crypto.randomUUID();}catch(_){/* insecure context */}
   const b=w.crypto.getRandomValues(new Uint8Array(16));b[6]=b[6]&0x0f|0x40;b[8]=b[8]&0x3f|0x80;
   const h=[...b].map(x=>x.toString(16).padStart(2,'0')).join('');
   return `${h.slice(0,8)}-${h.slice(8,12)}-${h.slice(12,16)}-${h.slice(16,20)}-${h.slice(20)}`;
  },
  createHash(algorithm){
   if(String(algorithm).toLowerCase()!=='sha256')throw new Error('仅支持 sha256');
   let text='';const hash={update:s=>{text+=String(s);return hash;},digest:encoding=>{if(encoding!=='hex')throw new Error('仅支持 hex');return sha256Hex(text);}};return hash;
  },
 };
 const os={homedir:()=>home,platform:()=>'darwin',tmpdir:()=>'/tmp'};
 const modules={fs,path,crypto,os};

 const host={
  platform:'mac',
  node(name){const m=modules[String(name).replace(/^node:/,'')];if(!m)throw new Error(`macOS 版不提供模块 ${name}`);return m;},
  run,runChecked,quote,toBase64,
  /** Resolves once every queued write has reached the disk; rejects with the last write error. */
  async flush(){await queue;if(lastWriteError){const e=lastWriteError;lastWriteError=null;throw e;}},
  async exists(p){const entry=overlay.get(p);if(entry)return !entry.deleted;return (await run(`test -e ${quote(p)}`)).ok;},
  async isDirectory(p){return (await run(`test -d ${quote(p)}`)).ok;},
  async listDir(dir){const r=await run(`ls -1A ${quote(dir)}`);if(!r.ok)throw new Error(r.stderr.trim()||'无法读取目录');return r.stdout.split('\n').map(x=>x.trim()).filter(Boolean);},
  openPath:p=>runChecked(`open ${quote(p)}`),
  // Typora for macOS saves natively (NSDocument); the page cannot save an existing file itself and may not send Apple
  // Events to its own app. main.cjs sets saveViaService, which asks the TyporaASR Service app to save the document
  // (Typora's AppleScript dictionary exposes documents by name only). Resolves true when saved.
  saveViaService:null,
  async saveDocument(file){
   if(!host.saveViaService)throw new Error('转写服务未连接，无法保存文档');
   const result=await host.saveViaService(path.basename(file));
   return !!result?.saved;
  },
  // Shell script runner for service control; stdout/stderr go to the script's own logs.
  runScript:script=>runChecked(`/bin/bash ${quote(script)}`),
  // Native pickers through AppleScript. Cancel (-128) resolves to null.
  async pick(kind,{title='',defaultPath='',defaultName=''}={}){
   const as=s=>'"'+String(s).replace(/\\/g,'\\\\').replace(/"/g,'\\"')+'"';
   let dir=defaultPath;
   if(kind!=='folder'&&dir&&!dir.endsWith('/'))dir=path.dirname(dir);
   if(dir&&!await host.isDirectory(dir))dir='';
   const location=dir?` default location (POSIX file ${as(dir)})`:'';
   const script=kind==='folder'?`POSIX path of (choose folder with prompt ${as(title)}${location})`
    :kind==='open'?`POSIX path of (choose file with prompt ${as(title)} of type {"json","public.json"}${location})`
    :`POSIX path of (choose file name with prompt ${as(title)}${defaultName?` default name ${as(defaultName)}`:''}${location})`;
   const r=await run(`osascript -e 'activate' -e ${quote(script)}`);
   if(!r.ok){if(/-128/.test(r.stderr))return null;throw new Error('无法打开选择窗口：'+r.stderr.trim());}
   let chosen=r.stdout.trim();if(!chosen)return null;
   if(kind==='folder'&&chosen.length>1)chosen=chosen.replace(/\/+$/,'');
   return chosen;
  },
 };
 return host;
}

module.exports={createMacHost,path,sha256Hex,quote,toBase64};
