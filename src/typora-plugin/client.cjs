'use strict';
const {hostOf,node}=require('./host.cjs');
class ServiceClient {
  constructor(w,connectionFile){
    this.w=w;this.file=connectionFile;this.host=hostOf(w);
    this.id=node(w,'crypto').randomUUID();this.preferCurl=false;
  }
  config(){
    const fs=node(this.w,'fs');
    let config;try{config=JSON.parse(fs.readFileSync(this.file,'utf8').replace(/^\uFEFF/,''))}catch{throw new Error('请先运行启动服务脚本');}
    return config;
  }
  request(method,route,body){
    let config;try{config=this.config()}catch(e){return Promise.reject(e)}
    const url=new URL(route,config.endpoint);
    if(url.hostname!=='127.0.0.1' || url.protocol!=='http:')return Promise.reject(new Error('仅允许本机服务'));
    const data=body===undefined?null:JSON.stringify(body);
    const timeout=route.includes('health')?5000:100000;
    if(this.host)return this.requestMac(method,url,data,config,timeout);
    const http=node(this.w,'http');
    return new Promise((resolve,reject)=>{
      const req=http.request(url,{method,headers:{Authorization:`Bearer ${config.token}`,'X-ASR-Client':this.id,'Content-Type':'application/json',...(data?{'Content-Length':Buffer.byteLength(data)}:{})}},res=>{
        let text='';res.setEncoding('utf8');res.on('data',x=>{text+=x;if(text.length>2e6)res.destroy(new Error('响应过大'));});
        res.on('error',reject);res.on('end',()=>{try{resolve(ServiceClient.parse(res.statusCode,text))}catch(e){reject(e)}});
      });
      req.on('error',reject);req.setTimeout(timeout,()=>req.destroy(new Error('本地服务响应超时')));if(data)req.write(data);req.end();
    });
  }
  static parse(status,text){
    const value=text?JSON.parse(text):null;
    if(status>=200&&status<300)return value;
    throw new Error(value?.error||`服务返回 ${status}`);
  }
  // macOS: fetch() from the Typora page (the service allows the Typora origin); if the WebView refuses the request,
  // fall back to curl through Typora's command runner with the token read from a 0600 header file written by the service.
  async requestMac(method,url,data,config,timeout){
    if(!this.preferCurl){
      const controller=new AbortController();const timer=this.w.setTimeout(()=>controller.abort(),timeout);
      let response;
      try{
        response=await this.w.fetch(url.href,{method,headers:{Authorization:`Bearer ${config.token}`,'X-ASR-Client':this.id,...(data?{'Content-Type':'application/json'}:{})},body:data??undefined,signal:controller.signal,cache:'no-store'});
      }catch(e){
        if(controller.signal.aborted)throw new Error('本地服务响应超时');
        const viaCurl=await this.requestCurl(method,url,data,config,timeout).catch(()=>null);
        if(!viaCurl)throw new Error('无法连接本地服务');
        this.preferCurl=true;return ServiceClient.parse(viaCurl.status,viaCurl.text);
      }finally{this.w.clearTimeout(timer);}
      const text=await response.text();
      if(text.length>2e6)throw new Error('响应过大');
      return ServiceClient.parse(response.status,text);
    }
    const result=await this.requestCurl(method,url,data,config,timeout);
    return ServiceClient.parse(result.status,result.text);
  }
  async requestCurl(method,url,data,config,timeout){
    const {quote,run}=this.host;
    const headerFile=config.curlHeaderFile;
    if(!headerFile)throw new Error('服务未提供 curl 认证文件');
    const input=data?`printf '%s' ${quote(this.host.toBase64(data))} | base64 --decode | `:'';
    const command=`${input}curl -sS --noproxy '*' -m ${Math.ceil(timeout/1000)} -X ${method==='POST'?'POST':'GET'} -H @${quote(headerFile)} -H ${quote('X-ASR-Client: '+this.id)} -H 'Content-Type: application/json' ${data?'--data-binary @- ':''}-w '\\n%{http_code}' ${quote(url.href)}`;
    const r=await run(command);
    if(!r.ok)throw new Error((r.stderr||'无法连接本地服务').trim());
    const cut=r.stdout.lastIndexOf('\n');
    return {status:Number(r.stdout.slice(cut+1).trim()),text:r.stdout.slice(0,cut)};
  }
}
module.exports={ServiceClient};
