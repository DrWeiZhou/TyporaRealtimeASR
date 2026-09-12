const {test}=require('node:test');const assert=require('node:assert/strict');
const fs=require('node:fs'),path=require('node:path');
const {ServiceClient}=require('../../src/typora-plugin/client.cjs');
const file=path.resolve('.asr/connection.json');
test('native client reaches authenticated service; unauthenticated requests are refused',async()=>{
 const connection=JSON.parse(fs.readFileSync(file,'utf8'));
 const unauthorized=await fetch(connection.endpoint+'/health');assert.equal(unauthorized.status,401);
 const client=new ServiceClient({reqnode:require},file);
 assert.equal((await client.request('GET','/health')).status,'ok');
 await assert.rejects(()=>client.request('POST','/sessions',{sessionId:crypto.randomUUID(),documentId:crypto.randomUUID(),path:'relative.md'}),/Markdown/);
});
