'use strict';
class ServiceClient {
  constructor(w,connectionFile){this.w=w;this.file=connectionFile;this.id=w.reqnode('crypto').randomUUID();}
  request(method,route,body){
    const fs=this.w.reqnode('fs'),http=this.w.reqnode('http');
    let config;try{config=JSON.parse(fs.readFileSync(this.file,'utf8').replace(/^\uFEFF/,''))}catch{return Promise.reject(new Error('请先运行启动服务脚本'));}
    const url=new URL(route,config.endpoint);
    if(url.hostname!=='127.0.0.1' || url.protocol!=='http:')return Promise.reject(new Error('仅允许本机服务'));
    const data=body===undefined?null:JSON.stringify(body);
    return new Promise((resolve,reject)=>{
      const req=http.request(url,{method,headers:{Authorization:`Bearer ${config.token}`,'X-ASR-Client':this.id,'Content-Type':'application/json',...(data?{'Content-Length':Buffer.byteLength(data)}:{})}},res=>{
        let text='';res.setEncoding('utf8');res.on('data',x=>{text+=x;if(text.length>2e6)res.destroy(new Error('响应过大'));});
        res.on('error',reject);res.on('end',()=>{try{const value=text?JSON.parse(text):null;res.statusCode>=200&&res.statusCode<300?resolve(value):reject(new Error(value?.error||`服务返回 ${res.statusCode}`));}catch(e){reject(e)}});
      });
      req.on('error',reject);req.setTimeout(100000,()=>req.destroy(new Error('本地服务响应超时')));if(data)req.write(data);req.end();
    });
  }
}
module.exports={ServiceClient};
