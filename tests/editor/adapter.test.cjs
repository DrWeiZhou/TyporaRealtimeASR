const {test}=require('node:test');const assert=require('node:assert/strict');
const {EditorAdapter}=require('../../src/typora-plugin/editor-adapter.cjs');
test('markers survive Typora reparsing HTML comments as paragraph nodes',()=>{
 const a=Object.create(EditorAdapter.prototype);a.documentId='doc';
 const raw=[{type:'paragraph',text:'<!-- asr-insert:doc -->\n'},{type:'paragraph',text:'<!-- asr-event:e1 -->'},{type:'fences',text:'<!-- asr-insert:doc -->'}];
 a.nodes=()=>raw.map(x=>({get:key=>x[key]}));
 assert.equal(a.anchors().length,1);assert.equal(a.contains('e1'),true);
});
test('new insertions are plain ordered lists with numbering in auxiliary state',()=>{
 const a=Object.create(EditorAdapter.prototype);a.documentId='doc';a.safe=()=>true;a.anchors=()=>[{}];
 const raw=[];a.nodes=()=>raw.map(x=>({get:key=>x[key]}));a.e={nodeMap:{getLast:()=>({})}};
 a.state={number:0,events:{}};a.persist=()=>{};a.transaction=(_,specs)=>raw.push(...specs);
 a.insert({eventId:'one',text:'第一句',start:16000,end:32000});a.insert({eventId:'two',text:'第二句'});
 assert.deepEqual(raw.map(x=>x.type),['list','list']);assert.deepEqual(raw.map(x=>x.start),[1,2]);
 assert.equal(raw[0].children[0].children[0].text,'第一句');assert.equal(a.state.number,2);
 assert.equal(a.contains('one'),true);
});
