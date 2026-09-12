const {test}=require('node:test');const assert=require('node:assert/strict');
const {EditorAdapter}=require('../../src/typora-plugin/editor-adapter.cjs');
test('markers survive Typora reparsing HTML comments as paragraph nodes',()=>{
 const a=Object.create(EditorAdapter.prototype);a.documentId='doc';
 const raw=[{type:'paragraph',text:'<!-- asr-insert:doc -->\n'},{type:'paragraph',text:'<!-- asr-event:e1 -->'},{type:'fences',text:'<!-- asr-insert:doc -->'}];
 a.nodes=()=>raw.map(x=>({get:key=>x[key]}));
 assert.equal(a.anchors().length,1);assert.equal(a.contains('e1'),true);
});
