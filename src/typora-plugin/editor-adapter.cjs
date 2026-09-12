'use strict';
const normalize=s=>s.replace(/^\uFEFF/,'').replace(/\r\n/g,'\n');
const escapeText=s=>s.replace(/[\\`*_{}\[\]<>#]/g,'\\$&').replace(/[\r\n]+/g,' ');
class EditorAdapter {
  constructor(w,documentId) {
    this.w=w;this.e=w.File.editor;this.documentId=documentId;this.boundPath=this.path();this.composing=false;this.conflict=false;
    this.onStart=()=>{this.composing=true};this.onEnd=()=>{this.composing=false};
    w.document.addEventListener('compositionstart',this.onStart,true);w.document.addEventListener('compositionend',this.onEnd,true);
    if(w._options.appVersion!=='1.14.10')throw new Error('当前仅验证 Typora 1.14.10；已停止自动写入');
    if(!this.e.undo?.UndoManager?.addUndoForInsert || !this.e.nodeMap?.getLast || !w.File.saveUseNode)throw new Error('编辑器接口不兼容');
  }
  path(){return this.w.File.bundle?.filePath||'';}
  nodes(){return [...this.w.document.querySelectorAll('#write > [cid]')].map(el=>this.e.getNode(el.getAttribute('cid'))).filter(Boolean);}
  marker(){return `<!-- asr-insert:${this.documentId} -->`;}
  isMarker(n,text){return ['paragraph','html_block'].includes(n.get('type')) && (n.get('text')||'').trim()===text;}
  anchors(){return this.nodes().filter(n=>this.isMarker(n,this.marker()));}
  safe(){return this.path()===this.boundPath && !this.composing && !this.e.isIME && !this.conflict && !this.e.sourceView.inSourceMode && !this.w.File.isLocked && !this.w.File.isFileLoading() && this.anchors().length===1;}
  contains(id){return this.nodes().some(n=>this.isMarker(n,`<!-- asr-event:${id} -->`));}
  transaction(anchor,specs,before=true) {
    const e=this.e,U=e.undo.UndoManager;const scroll=this.w.document.querySelector('content');
    const top=scroll?.scrollTop,left=scroll?.scrollLeft;
    let cursor;try{cursor=e.selection.buildUndo()}catch{cursor=e.lastCursor}
    e.undo.endSnap(true);
    const command=U.makeEmptyCommand(cursor);let previous=anchor;
    for(const spec of specs) {
      const build=spec=>{
        const {children,...attributes}=spec;const node=new anchor.constructor(attributes);
        for(const child of children||[])build(child).set('parent',node);
        return node;
      };
      const node=build(spec);
      if(before){anchor.addBefore(node);e.findElemById(anchor.cid).before(node.toHTML());}
      else {previous.addAfter(node);e.findElemById(previous.cid).after(node.toHTML());previous=node;}
      U.addUndoForInsert(command,node);
    }
    if(cursor)command.redo.push(cursor);
    e.undo.register(command);
    if(scroll){scroll.scrollTop=top;scroll.scrollLeft=left;}
  }
  bind() {
    if(this.path()!==this.boundPath || this.composing || this.e.isIME || this.e.sourceView.inSourceMode || this.w.File.isLocked)throw new Error('请在普通编辑模式中绑定已保存的 Markdown');
    const anchors=this.anchors();if(anchors.length>1)throw new Error('插入标记重复');if(anchors.length===1)return;
    const last=this.e.nodeMap.getLast();if(!last)throw new Error('请先在文档中写入一个标题并保存');
    this.transaction(last,[{type:'heading',depth:2,text:'实时记录'},{type:'html_block',text:this.marker()}],false);
  }
  insert(event) {
    if(!this.safe())throw new Error('编辑状态改变，等待安全插入');
    if(!/^[a-zA-Z0-9:_-]+$/.test(event.eventId))throw new Error('Invalid event id');
    const prefix=`<!-- asr-number:${this.documentId}:`;
    let number=this.lastNumber||0;
    for(const node of this.nodes()){
      const text=(node.get('text')||'').trim();
      if(['paragraph','html_block'].includes(node.get('type'))&&text.startsWith(prefix)){
        const match=text.slice(prefix.length).match(/^(\d+) -->$/);if(match)number=Math.max(number,Number(match[1]));
      }
    }
    number++;
    this.transaction(this.anchors()[0],[{type:'html_block',text:`<!-- asr-event:${event.eventId} -->`},
      {type:'html_block',text:`${prefix}${number} -->`},
      {type:'list',style:'ol',start:number,isFixed:false,children:[{type:'list_item',children:[{type:'paragraph',text:escapeText(event.text)}]}]}]);
    this.lastNumber=number;
  }
  checkDisk() {
    if(this.path()!==this.boundPath)return false;
    const fs=this.w.reqnode('fs');
    const disk=normalize(fs.readFileSync(this.boundPath,'utf8'));
    const saved=normalize(this.w.File.bundle.savedContent||'');
    if(!this.w.File.inSavingProcess && disk!==saved){this.conflict=true;return false;}
    return true;
  }
  async save() {
    if(!this.safe() || !this.checkDisk())return false;
    const expected=normalize(this.e.getMarkdown());
    const result=await this.w.File.saveUseNode(false,true);
    if(result!==true || this.path()!==this.boundPath)return false;
    return normalize(this.w.reqnode('fs').readFileSync(this.boundPath,'utf8'))===expected;
  }
  dispose(){this.w.document.removeEventListener('compositionstart',this.onStart,true);this.w.document.removeEventListener('compositionend',this.onEnd,true);}
}
module.exports={EditorAdapter};
