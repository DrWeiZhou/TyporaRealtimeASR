'use strict';
const normalize=s=>s.replace(/^\uFEFF/,'').replace(/\r\n/g,'\n');
const escapeText=s=>s.replace(/[\\`*_{}\[\]<>#]/g,'\\$&').replace(/[\r\n]+/g,' ');
// Paragraphs must not be re-parsed as list items when they start with a number or bullet.
const escapeParagraph=s=>escapeText(String(s).trim()).replace(/^(\d+)([.)])/,'$1\\$2').replace(/^([-+])(\s)/,'\\$1$2');
const JOIN_BELOW=150,JOIN_MAX=350;
const orderedItem=(start,text)=>({type:'list',style:'ol',start,isFixed:false,children:[{type:'list_item',children:[{type:'paragraph',text}]}]});
class EditorAdapter {
  constructor(w,documentId,stateRoot) {
    this.w=w;this.e=w.File.editor;this.documentId=documentId;this.boundPath=this.path();this.composing=false;this.conflict=false;
    const fs=w.reqnode('fs'),path=w.reqnode('path'),crypto=w.reqnode('crypto');
    this.stateFile=path.join(stateRoot||path.join(path.dirname(this.boundPath),'.asr'),'editor-state',crypto.createHash('sha256').update(this.boundPath.toLowerCase()+'|'+documentId).digest('hex')+'.json');
    this.state={number:0,events:{}};this.inserted=new Set();
    if(fs.existsSync(this.stateFile)){this.state=JSON.parse(fs.readFileSync(this.stateFile,'utf8'));if(!Number.isSafeInteger(this.state.number)||!this.state.events)throw new Error('插入状态文件损坏，请保留文件并检查');}
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
  safe(){return this.path()===this.boundPath && !this.composing && !this.e.isIME && !this.conflict && !this.e.sourceView.inSourceMode && !this.w.File.isLocked && !this.w.File.isFileLoading() && !!this.e.nodeMap.getLast();}
  contains(id){return !!(this.state?.events[id]==='saved'||this.inserted?.has(id)) || this.nodes().some(n=>this.isMarker(n,`<!-- asr-event:${id} -->`));}
  // Document content is the source of truth for list numbering so a new/cleared file restarts at 1.
  syncNumberFromDocument() {
    let max=0;
    // Prefer the live DOM; Typora nodes do not expose iterable get('children').
    const ols=[...this.w.document.querySelectorAll('#write ol')];
    for(const ol of ols){
      const start=Number(ol.getAttribute('start')||ol.start||1);
      const begin=Number.isFinite(start)&&start>0?start:1;
      const items=ol.querySelectorAll(':scope > li').length;
      if(items>0)max=Math.max(max,begin+items-1);
    }
    this.state.number=max;
  }
  persist(){const fs=this.w.reqnode('fs'),path=this.w.reqnode('path');fs.mkdirSync(path.dirname(this.stateFile),{recursive:true});fs.writeFileSync(this.stateFile+'.tmp',JSON.stringify(this.state));fs.renameSync(this.stateFile+'.tmp',this.stateFile);}
  // Returns the inserted top-level nodes. removeAnchor replaces the anchor (used to extend the previous paragraph) in the same undo step.
  transaction(anchor,specs,before=true,removeAnchor=false) {
    const e=this.e,U=e.undo.UndoManager;const scroll=this.w.document.querySelector('content');
    const top=scroll?.scrollTop,left=scroll?.scrollLeft;
    let cursor;try{cursor=e.selection.buildUndo()}catch{cursor=e.lastCursor}
    e.undo.endSnap(true);
    const command=U.makeEmptyCommand(cursor);let previous=anchor;const inserted=[];
    for(const spec of specs) {
      const build=spec=>{
        const {children,...attributes}=spec;const node=new anchor.constructor(attributes);
        for(const child of children||[])build(child).set('parent',node);
        return node;
      };
      const node=build(spec);
      if(before){anchor.addBefore(node);e.findElemById(anchor.cid).before(node.toHTML());}
      else {previous.addAfter(node);e.findElemById(previous.cid).after(node.toHTML());previous=node;}
      U.addUndoForInsert(command,node);inserted.push(node);
    }
    if(removeAnchor){U.addUndoForRemove(command,anchor);const element=e.findElemById(anchor.cid);anchor.remove();element.remove();}
    if(cursor)command.redo.push(cursor);
    e.undo.register(command);
    if(scroll){scroll.scrollTop=top;scroll.scrollLeft=left;}
    return inserted;
  }
  bind() {
    if(this.path()!==this.boundPath || this.composing || this.e.isIME || this.e.sourceView.inSourceMode || this.w.File.isLocked)throw new Error('请在普通编辑模式中绑定已保存的 Markdown');
    // Migrate only exact plugin comments, leaving human content and unrelated HTML intact.
    const legacy=this.nodes().filter(n=>['paragraph','html_block'].includes(n.get('type'))&&/^<!-- asr-(?:insert:[a-zA-Z0-9:_-]+|event:[a-zA-Z0-9:_-]+|number:[a-zA-Z0-9:_-]+:\d+) -->$/.test((n.get('text')||'').trim()));
    const disk=this.w.reqnode('fs').readFileSync(this.boundPath,'utf8');
    for(const n of legacy){
      const text=n.get('text').trim(),event=text.match(/^<!-- asr-event:([a-zA-Z0-9:_-]+) -->$/),number=text.match(/^<!-- asr-number:.*:(\d+) -->$/);
      if(event){this.state.events[event[1]]=disk.includes(text)?'saved':'pending';this.inserted.add(event[1]);}
      if(number)this.state.number=Math.max(this.state.number,Number(number[1]));
    }
    // Prefer live ordered lists over durable counter so empty/new docs start at 1.
    this.syncNumberFromDocument();
    this.persist();
    if(legacy.length){
      const e=this.e,U=e.undo.UndoManager;let cursor;try{cursor=e.selection.buildUndo()}catch{cursor=e.lastCursor}
      e.undo.endSnap(true);const command=U.makeEmptyCommand(cursor);
      for(const node of legacy){U.addUndoForRemove(command,node);const element=e.findElemById(node.cid);node.remove();element.remove();}
      if(cursor)command.redo.push(cursor);e.undo.register(command);
    }
    if(!this.e.nodeMap.getLast())throw new Error('请先在文档中写入一个标题并保存');
  }
  // Window results: a new topic opens a numbered item (bold title); its paragraphs and continuations follow as plain paragraphs.
  // A continuation joins the previous inserted paragraph while that paragraph is short, untouched and still the last block,
  // so small low-latency windows still read as coherent paragraphs.
  insert(event) {
    if(!this.safe())throw new Error('编辑状态改变，等待安全插入');
    if(!/^[a-zA-Z0-9:_-]+$/.test(event.eventId))throw new Error('Invalid event id');
    let number=this.state.number;const specs=[];let anchor=this.e.nodeMap.getLast(),replace=false;
    if(Array.isArray(event.blocks)){
      event.blocks.forEach((block,index)=>{
        if(block.topic==='new'){number++;specs.push(orderedItem(number,`**${escapeText(String(block.title||'').trim()||'记录')}**`));}
        const paragraphs=(block.paragraphs||[]).map(p=>String(p).trim()).filter(Boolean).map(escapeParagraph);
        if(index===0&&block.topic==='continue'&&paragraphs.length&&this.canJoin(anchor,paragraphs[0])){paragraphs[0]=this.lastParagraph.text+paragraphs[0];replace=true;}
        for(const text of paragraphs)specs.push({type:'paragraph',text});
      });
    } else {number++;specs.push(orderedItem(number,escapeText(event.text)));}
    this.state.number=number;this.state.events[event.eventId]='pending';this.persist();
    if(specs.length){
      const result=this.transaction(anchor,specs,false,replace),nodes=Array.isArray(result)?result:[];
      const last=nodes[nodes.length-1];
      this.lastParagraph=last&&last.get('type')==='paragraph'?{cid:last.cid,text:last.get('text')}:null;
    }
    (this.inserted||=new Set()).add(event.eventId);
  }
  canJoin(anchor,next){
    const last=this.lastParagraph;
    return !!(last&&anchor&&anchor.cid===last.cid&&anchor.get('type')==='paragraph'&&anchor.get('text')===last.text&&last.text.length<JOIN_BELOW&&last.text.length+next.length<=JOIN_MAX);
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
    if(normalize(this.w.reqnode('fs').readFileSync(this.boundPath,'utf8'))!==expected)return false;
    for(const id of this.inserted)this.state.events[id]='saved';this.persist();return true;
  }
  dispose(){this.w.document.removeEventListener('compositionstart',this.onStart,true);this.w.document.removeEventListener('compositionend',this.onEnd,true);}
}
module.exports={EditorAdapter};
