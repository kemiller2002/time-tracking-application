import test from 'node:test';
import assert from 'node:assert/strict';
import { handleRequest, defaultBindings } from '../worker/src/handler.js';
import { MemoryLedgerStore } from '../worker/src/store.js';

const bindings=()=>({...defaultBindings,store:new MemoryLedgerStore({now:(()=>{let n=Date.parse('2026-08-02T13:00:00Z');return()=>n+=60000;})()})});
const call=async(b,path,{method='GET',payload,requestId=crypto.randomUUID()}={})=>{
  const response=await handleRequest(new Request(`https://ledger.test/api/v1${path}`,{method,headers:{'content-type':'application/json','x-request-id':requestId,'idempotency-key':requestId},body:method==='GET'?undefined:JSON.stringify({request_id:requestId,client_timestamp:'2026-08-02T13:00:00Z',payload:payload??{}})}),b);
  return {status:response.status,body:await response.json()};
};

test('timer enforces one active timer and preserves idempotency',async()=>{
  const b=bindings();const id='start-1';const first=await call(b,'/timers/start',{method:'POST',requestId:id,payload:{activity_type_id:'research',project_id:'visual-engineering'}});assert.equal(first.status,200);
  const replay=await call(b,'/timers/start',{method:'POST',requestId:id,payload:{activity_type_id:'research',project_id:'visual-engineering'}});assert.equal(replay.body.data.idempotent_replay,true);
  const conflict=await call(b,'/timers/start',{method:'POST',payload:{activity_type_id:'meeting',project_id:'general'}});assert.equal(conflict.status,409);assert.equal(conflict.body.error.code,'timer_already_running');
  assert.equal((await call(b,'/timers/pause',{method:'POST'})).body.data.status,'paused');assert.equal((await call(b,'/timers/resume',{method:'POST'})).body.data.status,'running');
  const stopped=await call(b,'/timers/stop',{method:'POST'});assert.equal(stopped.body.data.status,'stopped');assert.ok(stopped.body.data.exact_duration_ms>0);assert.equal((await call(b,'/timers/current')).body.data.timer,null);
});

test('a timer stopped under 30 seconds is discarded and must not be recorded',async()=>{
  let clock=Date.parse('2026-08-02T13:00:00Z');
  const b={...defaultBindings,store:new MemoryLedgerStore({now:()=>clock})};
  await call(b,'/timers/start',{method:'POST',payload:{activity_type_id:'research',project_id:'visual-engineering'}});
  clock+=15000;
  const stopped=await call(b,'/timers/stop',{method:'POST'});
  assert.equal(stopped.body.data.status,'discarded');assert.equal(stopped.body.data.discarded,true);assert.equal(stopped.body.data.exact_duration_ms,15000);
  assert.equal((await call(b,'/timers/current')).body.data.timer,null);
});

test('a timer stopped at 30 seconds or more is recorded normally',async()=>{
  let clock=Date.parse('2026-08-02T13:00:00Z');
  const b={...defaultBindings,store:new MemoryLedgerStore({now:()=>clock})};
  await call(b,'/timers/start',{method:'POST',payload:{activity_type_id:'research',project_id:'visual-engineering'}});
  clock+=30000;
  const stopped=await call(b,'/timers/stop',{method:'POST'});
  assert.equal(stopped.body.data.status,'stopped');assert.equal(stopped.body.data.discarded,false);assert.equal(stopped.body.data.exact_duration_ms,30000);
});

test('creating an activity that crosses midnight is rejected',async()=>{
  const b=bindings();
  const result=await call(b,'/activities',{method:'POST',payload:{activity_type_id:'research',project_id:'general',description:'Late work',business_purpose:'Deadline',started_at:'2026-08-02T23:50:00Z',ended_at:'2026-08-03T00:10:00Z'}});
  assert.equal(result.status,400);assert.equal(result.body.error.code,'crosses_midnight');
});

test('creating an activity that overlaps an existing recorded activity on the same date is rejected',async()=>{
  const b=bindings();
  await call(b,'/activities',{method:'POST',payload:{activity_type_id:'research',project_id:'general',description:'First',business_purpose:'Planning',started_at:'2026-08-02T09:00:00Z',ended_at:'2026-08-02T10:00:00Z'}});
  const overlapping=await call(b,'/activities',{method:'POST',payload:{activity_type_id:'research',project_id:'general',description:'Second',business_purpose:'Planning',started_at:'2026-08-02T09:30:00Z',ended_at:'2026-08-02T10:30:00Z'}});
  assert.equal(overlapping.status,409);assert.equal(overlapping.body.error.code,'overlapping_activity');
  const adjacent=await call(b,'/activities',{method:'POST',payload:{activity_type_id:'research',project_id:'general',description:'Third',business_purpose:'Planning',started_at:'2026-08-02T10:00:00Z',ended_at:'2026-08-02T10:30:00Z'}});
  assert.equal(adjacent.status,201);
});

test('splitting an activity shorter than two minutes is rejected',async()=>{
  const b=bindings();
  const source=(await call(b,'/activities',{method:'POST',payload:{activity_type_id:'research',project_id:'general',description:'Quick note',business_purpose:'Planning',started_at:'2026-08-02T09:00:00Z',ended_at:'2026-08-02T09:01:00Z'}})).body.data;
  const split=await call(b,`/activities/${source.activity_id}/split`,{method:'POST',payload:{base_version:source.version,reason:'try to split',parts:[{duration_ms:30000,activity_type_id:'research',project_id:'general',description:'a',business_purpose:'b'},{duration_ms:30000,activity_type_id:'research',project_id:'general',description:'c',business_purpose:'d'}]}});
  assert.equal(split.status,400);assert.equal(split.body.error.code,'too_short_to_split');
});

test('activities are append-only projections with optimistic corrections',async()=>{
  const b=bindings();const created=(await call(b,'/activities',{method:'POST',payload:{activity_type_id:'research',project_id:'visual-engineering',description:'Read research',business_purpose:'Inform product design',started_at:'2026-08-02T09:00:00Z',ended_at:'2026-08-02T09:42:00Z',entry_method:'manual',reconstruction_reason:'Recorded after completion'}})).body.data;
  assert.equal(created.exact_duration_ms,42*60000);assert.equal(created.version,'v1');
  const amended=await call(b,`/activities/${created.activity_id}/amendments`,{method:'POST',payload:{base_version:'v1',changes:{description:'Reviewed composition research'},reason:'Clarify description'}});assert.equal(amended.body.data.description,'Reviewed composition research');assert.equal(amended.body.data.history.length,2);
  const stale=await call(b,`/activities/${created.activity_id}/amendments`,{method:'POST',payload:{base_version:'v1',changes:{description:'stale'},reason:'stale'}});assert.equal(stale.status,409);assert.equal(stale.body.error.code,'stale_projection');
});

test('amendments are re-validated by the same rules as creation',async()=>{
  const b=bindings();
  const other=(await call(b,'/activities',{method:'POST',payload:{activity_type_id:'research',project_id:'general',description:'Existing',business_purpose:'Planning',started_at:'2026-08-02T14:00:00Z',ended_at:'2026-08-02T15:00:00Z'}})).body.data;
  const target=(await call(b,'/activities',{method:'POST',payload:{activity_type_id:'research',project_id:'general',description:'Target',business_purpose:'Planning',started_at:'2026-08-02T09:00:00Z',ended_at:'2026-08-02T09:30:00Z'}})).body.data;
  const blank=await call(b,`/activities/${target.activity_id}/amendments`,{method:'POST',payload:{base_version:target.version,changes:{description:''},reason:'try blank'}});
  assert.equal(blank.status,400);assert.equal(blank.body.error.code,'validation_failed');
  const midnight=await call(b,`/activities/${target.activity_id}/amendments`,{method:'POST',payload:{base_version:target.version,changes:{started_at:'2026-08-02T23:50:00Z',ended_at:'2026-08-03T00:10:00Z'},reason:'try midnight'}});
  assert.equal(midnight.status,400);assert.equal(midnight.body.error.code,'crosses_midnight');
  const overlap=await call(b,`/activities/${target.activity_id}/amendments`,{method:'POST',payload:{base_version:target.version,changes:{started_at:'2026-08-02T14:30:00Z',ended_at:'2026-08-02T15:30:00Z'},reason:'try overlap'}});
  assert.equal(overlap.status,409);assert.equal(overlap.body.error.code,'overlapping_activity');
  const selfOverlap=await call(b,`/activities/${target.activity_id}/amendments`,{method:'POST',payload:{base_version:target.version,changes:{started_at:'2026-08-02T09:05:00Z',ended_at:'2026-08-02T09:35:00Z'},reason:'shift slightly'}});
  assert.equal(selfOverlap.status,200);
});

test('void, restore, evidence, day and month projections reconcile',async()=>{
  const b=bindings();const activity=(await call(b,'/activities',{method:'POST',payload:{activity_type_id:'development',project_id:'helixnote',description:'Build service',business_purpose:'Deliver product',started_at:'2026-08-02T10:00:00Z',ended_at:'2026-08-02T11:00:00Z'}})).body.data;
  const evidence=(await call(b,`/activities/${activity.activity_id}/evidence`,{method:'POST',payload:{base_version:activity.version,type:'url',uri:'https://example.test/evidence',label:'Work reference'}})).body.data;assert.equal(evidence.activity.evidence.length,1);
  const voided=(await call(b,`/activities/${activity.activity_id}/void`,{method:'POST',payload:{base_version:evidence.activity.version,reason:'Duplicate'}})).body.data;assert.equal(voided.voided,true);assert.equal((await call(b,'/days/2026-08-02')).body.data.total_exact_ms,0);
  const restored=(await call(b,`/activities/${activity.activity_id}/restore`,{method:'POST',payload:{base_version:voided.version,reason:'Not a duplicate'}})).body.data;assert.equal(restored.voided,false);
  const day=(await call(b,'/days/2026-08-02')).body.data;assert.equal(day.total_exact_ms,3600000);assert.equal(day.evidence_coverage,1);
  const month=(await call(b,'/months/2026-08')).body.data;assert.equal(month.decimal_hours,1);
  const attested=await call(b,'/days/2026-08-02/attest',{method:'POST',payload:{projection_version:day.projection_version,statement:'Accurate to the best of my knowledge'}});assert.equal(attested.status,200);
});

test('split preserves duration and merge prevents report double counting',async()=>{
  const b=bindings();const source=(await call(b,'/activities',{method:'POST',payload:{activity_type_id:'research',project_id:'general',description:'Mixed work',business_purpose:'Business development',started_at:'2026-08-02T12:00:00Z',ended_at:'2026-08-02T13:00:00Z'}})).body.data;
  const split=(await call(b,`/activities/${source.activity_id}/split`,{method:'POST',payload:{base_version:source.version,reason:'Two activities',parts:[{duration_ms:36*60000,activity_type_id:'research',project_id:'general',description:'Research',business_purpose:'Planning'},{duration_ms:24*60000,activity_type_id:'linkedin-marketing',project_id:'echelon-foundry',description:'LinkedIn',business_purpose:'Marketing'}]}})).body.data;
  assert.equal(split.replacements.length,2);assert.equal(split.source.voided,true);assert.equal(split.source.superseded,true);assert.equal((await call(b,'/days/2026-08-02')).body.data.total_exact_ms,3600000);
  const restoreSplitSource=await call(b,`/activities/${source.activity_id}/restore`,{method:'POST',payload:{base_version:split.source.version,reason:'try to undo the split'}});
  assert.equal(restoreSplitSource.status,409);assert.equal(restoreSplitSource.body.error.code,'activity_superseded');
  const [a,c]=split.replacements;const merged=(await call(b,'/activities/merge',{method:'POST',payload:{source_ids:[a.activity_id,c.activity_id],base_versions:[a.version,c.version],activity_type_id:'research',project_id:'general',description:'Combined session',business_purpose:'Planning and marketing',reason:'Should be one session'}})).body.data;
  assert.equal(merged.sources.every(x=>x.voided&&x.superseded),true);assert.equal((await call(b,'/days/2026-08-02')).body.data.total_exact_ms,3600000);
  const restoreMergeSource=await call(b,`/activities/${a.activity_id}/restore`,{method:'POST',payload:{base_version:merged.sources[0].version,reason:'try to undo the merge'}});
  assert.equal(restoreMergeSource.status,409);assert.equal(restoreMergeSource.body.error.code,'activity_superseded');
});

test('authentication seam blocks unauthenticated requests',async()=>{
  const b={...bindings(),auth:{authenticate:()=>null}};const result=await call(b,'/config');assert.equal(result.status,401);assert.equal(result.body.error.code,'unauthorized');
});
