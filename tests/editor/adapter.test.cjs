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
test('each new main idea becomes a numbered item with escaped text',()=>{
 const a=Object.create(EditorAdapter.prototype);a.documentId='doc';a.safe=()=>true;
 const raw=[];a.nodes=()=>[];a.e={nodeMap:{getLast:()=>({cid:'last',get:()=>'paragraph'})}};
 a.state={number:1,events:{}};a.persist=()=>{};a.transaction=(anchor,specs,before,replace)=>{assert.equal(replace,false);raw.push(...specs);return [];};
 a.insert({eventId:'win:s:1',text:'',paragraphs:['需求一：课程*建设。','1. 不是列表','']});
 assert.deepEqual(raw.map(x=>[x.type,x.start]),[['list',2],['list',3]]);
 assert.equal(raw[0].children[0].children[0].text,'需求一：课程\\*建设。');assert.equal(raw[1].children[0].children[0].text,'1\\. 不是列表');
 assert.equal(a.state.number,3);assert.equal(a.contains('win:s:1'),true);
 a.insert({eventId:'win:s:9',text:'',paragraphs:[]});
 assert.equal(raw.length,2);assert.equal(a.contains('win:s:9'),true);
});
test('a continuation extends the last untouched item in one step; otherwise it adds no new number',()=>{
 const a=Object.create(EditorAdapter.prototype);a.documentId='doc';a.safe=()=>true;a.nodes=()=>[];a.state={number:0,events:{}};a.persist=()=>{};
 const nodes=new Map();let last=null,calls=[];
 const make=(cid,type,text)=>{const n={cid,get:k=>({type,text:n.text})[k],text};nodes.set(cid,n);return n;};
 a.e={nodeMap:{getLast:()=>last},getNode:cid=>nodes.get(cid)};
 a.transaction=(anchor,specs,before,replace)=>{
  calls.push({anchor:anchor&&anchor.cid,specs,replace});const built=new Map();
  const made=specs.map((x,i)=>{const cid=`n${calls.length}-${i}`;const top=make(cid,x.type,x.text);if(x.type==='list')built.set(x.children[0].children[0],make(cid+'-p','paragraph',x.children[0].children[0].text));built.set(x,top);return top;});
  if(replace)nodes.delete(anchor.cid);
  last=made[made.length-1];made.nodeOf=spec=>built.get(spec);return made;
 };
 const text=call=>call.specs.map(x=>x.type==='list'?`${x.start}:${x.children[0].children[0].text}`:x.text);
 a.insert({eventId:'w1',paragraphs:['主题一。第一句。']});
 a.insert({eventId:'w2',continues:true,paragraphs:['第二句。','主题二。']});
 assert.equal(calls[1].replace,true);assert.equal(calls[1].anchor,'n1-0');assert.deepEqual(text(calls[1]),['1:主题一。第一句。第二句。','2:主题二。']);
 a.insert({eventId:'w3',continues:true,paragraphs:['主题二补充。']});
 assert.equal(calls[2].replace,true);assert.equal(calls[2].anchor,'n2-1');assert.deepEqual(text(calls[2]),['2:主题二。主题二补充。']);
 a.insert({eventId:'w4',continues:true,paragraphs:['再补充。']});
 assert.equal(calls[3].replace,true);assert.deepEqual(text(calls[3]),['2:主题二。主题二补充。再补充。']);
 nodes.get('n4-0-p').text='人工改过';
 a.insert({eventId:'w5',continues:true,paragraphs:['第五句。'],});
 assert.equal(calls[4].replace,false);assert.deepEqual(text(calls[4]),['第五句。']);
 a.insert({eventId:'w6',paragraphs:['主题三。']});
 assert.deepEqual(text(calls[5]),['3:主题三。']);assert.equal(a.state.number,3);
 last=make('user','paragraph','人工输入');
 a.insert({eventId:'w7',continues:true,paragraphs:['第七句。']});
 assert.equal(calls[6].replace,false);assert.deepEqual(text(calls[6]),['第七句。']);
});
