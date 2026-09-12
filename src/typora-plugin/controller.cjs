'use strict';
class TranscriptController {
  constructor(adapter,ack,path) { this.adapter=adapter; this.ack=ack; this.boundPath=path; this.handled=new Set(); }
  async apply(event,approveReview=false) {
    if(event.polishState && event.polishState!=='ready')return 'deferred';
    const id=event.eventId;
    if(this.handled.has(id) || event.state==='deleted') return 'handled';
    if(this.adapter.path()!==this.boundPath || !this.adapter.safe()) return 'deferred';
    if(this.adapter.contains(id)) {this.handled.add(id);await this.ack(id,'applied');return 'handled';}
    if(!approveReview && (event.needsReview || ['applying','applied','saved'].includes(event.state))) return 'review';
    await this.ack(id,'applying');
    if(this.adapter.path()!==this.boundPath || !this.adapter.safe()) return 'deferred';
    this.adapter.insert(event);this.handled.add(id);
    await this.ack(id,'applied');return 'inserted';
  }
}
module.exports={TranscriptController};
