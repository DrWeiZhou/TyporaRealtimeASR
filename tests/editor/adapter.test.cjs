const {test}=require('node:test');const assert=require('node:assert/strict');
const {EditorAdapter}=require('../../src/typora-plugin/editor-adapter.cjs');
test('markers survive Typora reparsing HTML comments as paragraph nodes',()=>{
 const a=Object.create(EditorAdapter.prototype);a.documentId='doc';
 const raw=[{type:'paragraph',text:'<!-- asr-insert:doc -->\n'},{type:'paragraph',text:'<!-- asr-event:e1 -->'},{type:'fences',text:'<!-- asr-insert:doc -->'}];
 a.nodes=()=>raw.map(x=>({get:key=>x[key]}));
 assert.equal(a.anchors().length,1);assert.equal(a.contains('e1'),true);
});
test('new insertions use ordered list nodes and continue numbering after reload',()=>{
 const a=Object.create(EditorAdapter.prototype);a.documentId='doc';a.safe=()=>true;a.anchors=()=>[{}];
 const raw=[];a.nodes=()=>raw.map(x=>({get:key=>x[key]}));
 a.transaction=(_,specs)=>raw.push(...specs);
 a.insert({eventId:'one',text:'第一句',start:16000,end:32000});
 a.insert({eventId:'two',text:'第二句'});
 const lists=raw.filter(x=>x.type==='list');assert.deepEqual(lists.map(x=>x.start),[1,2]);
 assert.equal(lists[0].style,'ol');assert.equal(lists[0].children[0].children[0].text,'第一句');
 const b=Object.create(EditorAdapter.prototype);Object.assign(b,{documentId:'doc',safe:a.safe,anchors:a.anchors,nodes:a.nodes,transaction:a.transaction});
 b.insert({eventId:'three',text:'第三句'});assert.equal(raw.at(-1).start,3);
});
