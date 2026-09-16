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
test('window blocks open numbered topics and append continuation paragraphs',()=>{
 const a=Object.create(EditorAdapter.prototype);a.documentId='doc';a.safe=()=>true;
 const raw=[];a.nodes=()=>[];a.e={nodeMap:{getLast:()=>({})}};
 a.state={number:1,events:{}};a.persist=()=>{};a.transaction=(_,specs)=>raw.push(...specs);
 a.insert({eventId:'win:s:1',text:'',blocks:[{topic:'continue',title:'',paragraphs:['接着上一话题。']},{topic:'new',title:'课程*建设',paragraphs:['1. 不是列表','- 也不是','']}]});
 assert.deepEqual(raw.map(x=>x.type),['paragraph','list','paragraph','paragraph']);
 assert.equal(raw[0].text,'接着上一话题。');
 assert.equal(raw[1].start,2);assert.equal(raw[1].children[0].children[0].text,'**课程\\*建设**');
 assert.equal(raw[2].text,'1\\. 不是列表');assert.equal(raw[3].text,'\\- 也不是');
 assert.equal(a.state.number,2);assert.equal(a.contains('win:s:1'),true);
 a.insert({eventId:'win:s:9',text:'',blocks:[]});
 assert.equal(raw.length,4);assert.equal(a.contains('win:s:9'),true);
});
test('short untouched paragraph is extended by the next continuation in one step',()=>{
 const a=Object.create(EditorAdapter.prototype);a.documentId='doc';a.safe=()=>true;a.nodes=()=>[];a.state={number:0,events:{}};a.persist=()=>{};
 const node=(cid,type,text)=>({cid,get:k=>({type,text})[k]});let last=null,calls=[];
 a.e={nodeMap:{getLast:()=>last}};
 a.transaction=(anchor,specs,before,replace)=>{calls.push({anchor,specs,replace});const made=specs.map((x,i)=>node(`n${calls.length}-${i}`,x.type,x.text));last=made[made.length-1];return made;};
 a.insert({eventId:'w1',blocks:[{topic:'new',title:'话题',paragraphs:['第一句。']}]});
 a.insert({eventId:'w2',blocks:[{topic:'continue',paragraphs:['第二句。','另起一段。']}]});
 assert.equal(calls[1].replace,true);assert.equal(calls[1].anchor.cid,'n1-1');
 assert.deepEqual(calls[1].specs.map(x=>x.text),['第一句。第二句。','另起一段。']);
 a.insert({eventId:'w3',blocks:[{topic:'continue',paragraphs:['第三句。']}]});
 assert.equal(calls[2].replace,true);assert.equal(calls[2].specs[0].text,'另起一段。第三句。');
 last=node('user','paragraph','人工输入');
 a.insert({eventId:'w4',blocks:[{topic:'continue',paragraphs:['第四句。']}]});
 assert.equal(calls[3].replace,false);assert.equal(calls[3].specs[0].text,'第四句。');
 a.lastParagraph={cid:last.cid,text:'x'.repeat(200)};
 a.insert({eventId:'w5',blocks:[{topic:'continue',paragraphs:['第五句。']}]});
 assert.equal(calls[4].replace,false);
});
