import { ServiceError, requireField } from './errors.js';

const clone = value => structuredClone(value);
const iso = value => new Date(value).toISOString();
const version = seq => `v${seq}`;
const MIN_RECORDABLE_TIMER_MS = 30000;
const MIN_SPLITTABLE_MS = 120000;

export class MemoryLedgerStore {
  constructor({ now = () => Date.now() } = {}) { this.now = now; this.events = []; this.timers = new Map(); this.requests = new Map(); this.attestations = []; }
  replay(requestId) { return this.requests.has(requestId) ? clone(this.requests.get(requestId)) : null; }
  remember(requestId, result) { this.requests.set(requestId, clone(result)); return result; }
  append(type, aggregateId, payload, actor = 'owner') {
    const seq = this.events.length + 1;
    const event = { event_id: crypto.randomUUID(), sequence: seq, event_type: type, aggregate_id: aggregateId, actor, occurred_at: iso(this.now()), payload: clone(payload) };
    this.events.push(event); return event;
  }
  currentTimer(owner = 'owner') { const timer = this.timers.get(owner); return timer ? clone(timer) : null; }
  startTimer(owner, input) {
    if (this.timers.has(owner)) throw new ServiceError('timer_already_running', 'A timer is already active.', { status: 409, details: { current_timer: this.currentTimer(owner) } });
    const timer = { timer_id: crypto.randomUUID(), owner, status: 'running', activity_type_id: requireField(input.activity_type_id, 'activity_type_id'), project_id: requireField(input.project_id, 'project_id'), description: input.description ?? '', started_at: iso(this.now()), paused_at: null, paused_ms: 0, version: 'v1' };
    this.timers.set(owner, timer); return clone(timer);
  }
  pauseTimer(owner) { const timer=this.#timer(owner); if(timer.status!=='running')throw new ServiceError('timer_not_running','The timer is not running.',{status:409}); timer.status='paused';timer.paused_at=iso(this.now());timer.version=version(Number(timer.version.slice(1))+1);return clone(timer); }
  resumeTimer(owner) { const timer=this.#timer(owner); if(timer.status!=='paused')throw new ServiceError('timer_not_paused','The timer is not paused.',{status:409});timer.paused_ms+=this.now()-Date.parse(timer.paused_at);timer.paused_at=null;timer.status='running';timer.version=version(Number(timer.version.slice(1))+1);return clone(timer); }
  stopTimer(owner) {
    const timer=this.#timer(owner); const stoppedAt=this.now(); const end=timer.paused_at?Date.parse(timer.paused_at):stoppedAt; const exactMs=Math.max(0,end-Date.parse(timer.started_at)-timer.paused_ms);
    this.timers.delete(owner);
    const discarded=exactMs<MIN_RECORDABLE_TIMER_MS;
    return { ...clone(timer), status:discarded?'discarded':'stopped', stopped_at:iso(stoppedAt), exact_duration_ms:exactMs, exact_minutes:Number((exactMs/60000).toFixed(4)), discarded };
  }
  #timer(owner){const timer=this.timers.get(owner);if(!timer)throw new ServiceError('timer_not_found','No active timer exists.',{status:404});return timer;}
  createActivity(input, actor='owner') {
    const id=input.activity_id??crypto.randomUUID();
    const {start,end}=this.#validateActivityFields(input,input.overlap_exclude_ids??[]);
    const record={ activity_id:id, activity_type_id:requireField(input.activity_type_id,'activity_type_id'), project_id:requireField(input.project_id,'project_id'), description:requireField(input.description,'description'), business_purpose:requireField(input.business_purpose,'business_purpose'), outcome:input.outcome??'', tags:input.tags??[], entry_method:input.entry_method??'manual', reconstruction_reason:input.reconstruction_reason??null, started_at:iso(start), ended_at:iso(end), exact_duration_ms:end-start, client_timestamp:input.client_timestamp??null, server_received_at:iso(this.now()) };
    const event=this.append('activity.created',id,record,actor); return this.activity(id,event.sequence);
  }
  #validateActivityFields(candidate,excludeIds=[]){
    requireField(candidate.activity_type_id,'activity_type_id'); requireField(candidate.project_id,'project_id');
    requireField(candidate.description,'description'); requireField(candidate.business_purpose,'business_purpose');
    const start=Date.parse(requireField(candidate.started_at,'started_at')); const end=Date.parse(requireField(candidate.ended_at,'ended_at'));
    if(!Number.isFinite(start)||!Number.isFinite(end)||end<=start)throw new ServiceError('invalid_time_range','End time must be after start time.');
    if(iso(start).slice(0,10)!==iso(end).slice(0,10))throw new ServiceError('crosses_midnight','An activity must not extend past midnight.');
    this.#assertNoOverlap(start,end,excludeIds);
    return {start,end};
  }
  #assertNoOverlap(start,end,excludeIds){
    const dateKey=iso(start).slice(0,10);
    const conflict=this.activities().find(a=>!a.voided&&!excludeIds.includes(a.activity_id)&&a.started_at.slice(0,10)===dateKey&&start<Date.parse(a.ended_at)&&Date.parse(a.started_at)<end);
    if(conflict)throw new ServiceError('overlapping_activity','This time interval overlaps another recorded activity.',{status:409,details:{conflicting_activity_id:conflict.activity_id}});
  }
  amendActivity(id, input, actor='owner') { const current=this.assertVersion(id,input.base_version); const changes=clone(requireField(input.changes,'changes')); delete changes.activity_id; delete changes.entry_method; this.#validateActivityFields({...current,...changes},[id]); const event=this.append('activity.amended',id,{changes,reason:requireField(input.reason,'reason'),prior_version:current.version},actor); return this.activity(id,event.sequence); }
  voidActivity(id,input,actor='owner'){const current=this.assertVersion(id,input.base_version);if(current.voided)throw new ServiceError('already_voided','Activity is already removed from totals.',{status:409});const event=this.append('activity.voided',id,{reason:requireField(input.reason,'reason'),prior_version:current.version},actor);return this.activity(id,event.sequence);}
  restoreActivity(id,input,actor='owner'){const current=this.assertVersion(id,input.base_version);if(current.superseded)throw new ServiceError('activity_superseded','A Superseded activity (replaced by a split or merge) cannot be restored.',{status:409});if(!current.voided)throw new ServiceError('not_voided','Activity is already included in totals.',{status:409});const event=this.append('activity.restored',id,{reason:requireField(input.reason,'reason'),prior_version:current.version},actor);return this.activity(id,event.sequence);}
  attachEvidence(id,input,actor='owner'){const current=this.assertVersion(id,input.base_version);const evidence={evidence_link_id:crypto.randomUUID(),type:requireField(input.type,'type'),uri:input.uri??null,note:input.note??null,hash:input.hash??null,label:input.label??'',attached_at:iso(this.now())};const event=this.append('evidence.attached',id,{...evidence,prior_version:current.version},actor);return {activity:this.activity(id,event.sequence),evidence};}
  detachEvidence(id,evidenceId,input,actor='owner'){const current=this.assertVersion(id,input.base_version);if(!current.evidence.some(x=>x.evidence_link_id===evidenceId))throw new ServiceError('evidence_not_found','Evidence link was not found.',{status:404});const event=this.append('evidence.detached',id,{evidence_link_id:evidenceId,reason:requireField(input.reason,'reason'),prior_version:current.version},actor);return this.activity(id,event.sequence);}
  splitActivity(id,input,actor='owner'){
    const current=this.assertVersion(id,input.base_version); const parts=requireField(input.parts,'parts'); if(!Array.isArray(parts)||parts.length<2)throw new ServiceError('invalid_split','At least two split parts are required.');
    if(current.exact_duration_ms<MIN_SPLITTABLE_MS)throw new ServiceError('too_short_to_split','Activity must be at least two minutes long to split.');
    const total=parts.reduce((sum,p)=>sum+Number(p.duration_ms),0);if(total!==current.exact_duration_ms)throw new ServiceError('duration_invariant_failed','Split durations must equal the source duration.',{details:{expected:current.exact_duration_ms,actual:total}});
    let cursor=Date.parse(current.started_at); const replacements=parts.map(part=>{const next=cursor+Number(part.duration_ms);const activity=this.createActivity({...current,...part,activity_id:crypto.randomUUID(),started_at:iso(cursor),ended_at:iso(next),entry_method:'split',overlap_exclude_ids:[id]},actor);cursor=next;return activity;});
    this.append('activity.split',id,{replacement_ids:replacements.map(x=>x.activity_id),reason:requireField(input.reason,'reason')},actor);this.append('activity.superseded',id,{reason:'Replaced by split',prior_version:this.activity(id).version},actor);return {source:this.activity(id),replacements};
  }
  mergeActivities(input,actor='owner'){
    const ids=requireField(input.source_ids,'source_ids');if(!Array.isArray(ids)||ids.length<2)throw new ServiceError('invalid_merge','At least two source activities are required.');
    const sources=ids.map((id,index)=>this.assertVersion(id,input.base_versions?.[index]));const startedAt=new Date(Math.min(...sources.map(x=>Date.parse(x.started_at))));const endedAt=new Date(Math.max(...sources.map(x=>Date.parse(x.ended_at))));
    const merged=this.createActivity({activity_type_id:requireField(input.activity_type_id,'activity_type_id'),project_id:requireField(input.project_id,'project_id'),description:requireField(input.description,'description'),business_purpose:requireField(input.business_purpose,'business_purpose'),started_at:startedAt,ended_at:endedAt,entry_method:'merge',overlap_exclude_ids:ids},actor);
    this.append('activity.merged',merged.activity_id,{source_ids:ids,reason:requireField(input.reason,'reason')},actor);for(const source of sources)this.append('activity.superseded',source.activity_id,{reason:`Merged into ${merged.activity_id}`,merged_into:merged.activity_id,prior_version:this.activity(source.activity_id).version},actor);return {merged:this.activity(merged.activity_id),sources:ids.map(id=>this.activity(id))};
  }
  assertVersion(id,base){const current=this.activity(id);if(!current)throw new ServiceError('activity_not_found','Activity was not found.',{status:404});if(base&&base!==current.version)throw new ServiceError('stale_projection','This activity changed after it was opened.',{status:409,details:{current}});return current;}
  activity(id,through=Infinity){return this.activities(through).find(x=>x.activity_id===id)??null;}
  activities(through=Infinity){
    const map=new Map();for(const event of this.events){if(event.sequence>through)break;const p=event.payload;let a=map.get(event.aggregate_id);
      if(event.event_type==='activity.created'){a={...clone(p),voided:false,superseded:false,evidence:[],history:[],relationships:{}};map.set(event.aggregate_id,a);}
      if(!a)continue;a.history.push(clone(event));a.version=version(event.sequence);a.updated_at=event.occurred_at;
      if(event.event_type==='activity.amended')Object.assign(a,p.changes);
      if(event.event_type==='activity.voided')a.voided=true;
      if(event.event_type==='activity.restored')a.voided=false;
      if(event.event_type==='activity.superseded'){a.voided=true;a.superseded=true;if(p.merged_into)a.relationships.merged_into=p.merged_into;}
      if(event.event_type==='evidence.attached')a.evidence.push(clone(p));
      if(event.event_type==='evidence.detached')a.evidence=a.evidence.filter(x=>x.evidence_link_id!==p.evidence_link_id);
      if(event.event_type==='activity.split')a.relationships.split_into=p.replacement_ids;
      if(event.event_type==='activity.merged')a.relationships.merged_from=p.source_ids;
    }return [...map.values()].map(clone);
  }
  day(date){const activities=this.activities().filter(a=>a.started_at.slice(0,10)===date);return this.#summary(activities,{date});}
  month(month){const activities=this.activities().filter(a=>a.started_at.slice(0,7)===month);return this.#summary(activities,{month});}
  #summary(activities,identity){const included=activities.filter(a=>!a.voided);const totalMs=included.reduce((s,a)=>s+a.exact_duration_ms,0);const by=(key)=>Object.fromEntries([...new Set(included.map(x=>x[key]))].map(k=>[k,included.filter(x=>x[key]===k).reduce((s,x)=>s+x.exact_duration_ms,0)]));return {...identity,projection_version:version(this.events.length),activities:included,total_exact_ms:totalMs,total_minutes:Number((totalMs/60000).toFixed(4)),decimal_hours:Number((totalMs/3600000).toFixed(4)),by_activity_type:by('activity_type_id'),by_project:by('project_id'),manual_count:included.filter(x=>x.entry_method==='manual').length,correction_count:included.filter(x=>x.history.some(e=>e.event_type==='activity.amended')).length,void_count:activities.filter(x=>x.voided).length,evidence_coverage:included.length?included.filter(x=>x.evidence.length).length/included.length:0};}
  attestDay(date,input,actor='owner'){const day=this.day(date);if(input.projection_version&&input.projection_version!==day.projection_version)throw new ServiceError('stale_projection','The day changed before attestation.',{status:409,details:{current:day}});const event=this.append('day.attested',date,{projection_version:day.projection_version,statement:requireField(input.statement,'statement')},actor);const result={attestation_id:event.event_id,date,projection_version:day.projection_version,attested_at:event.occurred_at};this.attestations.push(result);return clone(result);}
}
