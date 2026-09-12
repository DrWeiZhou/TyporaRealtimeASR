const {test}=require('node:test');
const assert=require('node:assert/strict');
const {TranscriptController}=require('../../src/typora-plugin/controller.cjs');
function setup(){
  let content='人工修改过的内容\n';const acknowledgements=[];
  const adapter={path:()=>'/notes.md',safe:()=>true,contains:id=>content.includes(id),insert:e=>{content+=`${e.eventId}:${e.text}\n`;}};
  return {adapter,acknowledgements,get content(){return content},controller:new TranscriptController(adapter,(id,state)=>acknowledgements.push([id,state]),'/notes.md')};
}
test('unpolished text cannot enter the document even with review approval',async()=>{
 const s=setup();assert.equal(await s.controller.apply({eventId:'raw',text:'原始口语',polishState:'pending'},true),'deferred');
 assert.equal(s.acknowledgements.length,0);
});
test('duplicate final never overwrites edited history or inserts twice',async()=>{
  const s=setup(),e={eventId:'e1',text:'新一句',state:'recognized'};
  await s.controller.apply(e);await s.controller.apply(e);
  assert.equal(s.content,'人工修改过的内容\ne1:新一句\n');
});
test('IME and changed document defer without acknowledging',async()=>{
  const s=setup();s.adapter.safe=()=>false;
  assert.equal(await s.controller.apply({eventId:'e1',text:'新一句'}),'deferred');
  s.adapter.safe=()=>true;s.adapter.path=()=>'/other.md';
  assert.equal(await s.controller.apply({eventId:'e1',text:'新一句'}),'deferred');
  assert.equal(s.acknowledgements.length,0);
});
test('deleted events stay deleted and ambiguous applied events need review',async()=>{
  const s=setup();
  assert.equal(await s.controller.apply({eventId:'e1',text:'已删',state:'deleted'}),'handled');
  assert.equal(await s.controller.apply({eventId:'e2',text:'崩溃前已插入',state:'applied'}),'review');
  assert.equal(s.content,'人工修改过的内容\n');
});
test('forced boundary is not automatically inserted',async()=>{
 const s=setup();assert.equal(await s.controller.apply({eventId:'e1',text:'长句',needsReview:true}),'review');
 assert.equal(s.content,'人工修改过的内容\n');
});
test('a document switch during durable intent acknowledgement aborts insertion',async()=>{
 const s=setup();s.controller.ack=async()=>{s.adapter.path=()=>'/other.md'};
 assert.equal(await s.controller.apply({eventId:'e1',text:'新句',state:'recognized'}),'deferred');
 assert.equal(s.content,'人工修改过的内容\n');
});
test('an interrupted insertion intent is reviewed instead of replayed automatically',async()=>{
 const s=setup();assert.equal(await s.controller.apply({eventId:'e1',text:'未知是否写入',state:'applying'}),'review');
 assert.equal(s.content,'人工修改过的内容\n');
});

test('saved events never reappear after users edit or delete marker-free text',async()=>{
 const s=setup();assert.equal(await s.controller.apply({eventId:'saved',text:'旧文字',state:'saved'}),'handled');
 assert.equal(s.content,'人工修改过的内容\n');assert.equal(s.acknowledgements.length,0);
});

test('failed polish is reported without inserting raw text and later ready text proceeds',async()=>{
 const s=setup();assert.equal(await s.controller.apply({eventId:'bad',text:'原始口语',polishState:'failed'}),'failed');
 assert.equal(await s.controller.apply({eventId:'good',text:'后续润色稿',polishState:'ready'}),'inserted');
 assert.ok(!s.content.includes('原始口语'));assert.ok(s.content.includes('后续润色稿'));
});
