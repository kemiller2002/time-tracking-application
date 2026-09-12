import test from 'node:test';
import assert from 'node:assert/strict';
import { handleRequest, defaultBindings } from '../worker/src/handler.js';
import { MemoryLedgerStore } from '../worker/src/store.js';

const bindings=()=>({...defaultBindings,store:new MemoryLedgerStore({now:(()=>{let n=Date.parse('2026-08-02T13:00:00Z');return()=>n+=60000;})(),directories:{projects:defaultBindings.projects,activityTypes:defaultBindings.activityTypes,tags:defaultBindings.tags}})});
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

test('creating an activity with an unknown or archived project/activity-type/tag is rejected',async()=>{
  const b=bindings();
  const base={activity_type_id:'research',project_id:'general',description:'x',business_purpose:'y',started_at:'2026-08-02T09:00:00Z',ended_at:'2026-08-02T09:30:00Z'};
  const unknownProject=await call(b,'/activities',{method:'POST',payload:{...base,project_id:'does-not-exist'}});
  assert.equal(unknownProject.status,400);assert.equal(unknownProject.body.error.code,'project_not_found');
  const archivedProject=await call(b,'/activities',{method:'POST',payload:{...base,project_id:'archived-initiative'}});
  assert.equal(archivedProject.status,400);assert.equal(archivedProject.body.error.code,'project_inactive');
  const unknownType=await call(b,'/activities',{method:'POST',payload:{...base,activity_type_id:'does-not-exist'}});
  assert.equal(unknownType.status,400);assert.equal(unknownType.body.error.code,'activity_type_not_found');
  const retiredType=await call(b,'/activities',{method:'POST',payload:{...base,activity_type_id:'retired-type'}});
  assert.equal(retiredType.status,400);assert.equal(retiredType.body.error.code,'activity_type_inactive');
  const unknownTag=await call(b,'/activities',{method:'POST',payload:{...base,tags:['does-not-exist']}});
  assert.equal(unknownTag.status,400);assert.equal(unknownTag.body.error.code,'tag_not_found');
  const inactiveTag=await call(b,'/activities',{method:'POST',payload:{...base,tags:['legacy']}});
  assert.equal(inactiveTag.status,400);assert.equal(inactiveTag.body.error.code,'tag_inactive');
  const activeTag=await call(b,'/activities',{method:'POST',payload:{...base,tags:['billable']}});
  assert.equal(activeTag.status,201);
});

test('project/activity-type exemption: unchanged assignment survives archival, new assignment does not',async()=>{
  const activeProject={id:'temp-active',name:'Temp Active',active:true,version:'v1'};
  const otherArchivedProject={id:'temp-archived',name:'Temp Archived',active:false,version:'v1'};
  const activityType={id:'temp-type',name:'Temp Type',active:true,version:'v1'};
  const b={...defaultBindings,store:new MemoryLedgerStore({now:(()=>{let n=Date.parse('2026-08-02T13:00:00Z');return()=>n+=60000;})(),directories:{projects:[activeProject,otherArchivedProject],activityTypes:[activityType],tags:[]}})};
  const created=(await call(b,'/activities',{method:'POST',payload:{activity_type_id:'temp-type',project_id:'temp-active',description:'Work',business_purpose:'Purpose',started_at:'2026-08-02T09:00:00Z',ended_at:'2026-08-02T09:30:00Z'}})).body.data;
  activeProject.active=false;
  const unrelatedAmend=await call(b,`/activities/${created.activity_id}/amendments`,{method:'POST',payload:{base_version:created.version,changes:{description:'Updated description'},reason:'tweak'}});
  assert.equal(unrelatedAmend.status,200);assert.equal(unrelatedAmend.body.data.project_id,'temp-active');
  const reassign=await call(b,`/activities/${created.activity_id}/amendments`,{method:'POST',payload:{base_version:unrelatedAmend.body.data.version,changes:{project_id:'temp-archived'},reason:'move'}});
  assert.equal(reassign.status,400);assert.equal(reassign.body.error.code,'project_inactive');
});

test('tag exemption: only a newly-assigned tag must be active',async()=>{
  const legacyTag={id:'temp-legacy',name:'Temp Legacy',active:true,version:'v1'};
  const otherInactiveTag={id:'temp-other',name:'Temp Other',active:false,version:'v1'};
  const b={...defaultBindings,store:new MemoryLedgerStore({now:(()=>{let n=Date.parse('2026-08-02T13:00:00Z');return()=>n+=60000;})(),directories:{projects:defaultBindings.projects,activityTypes:defaultBindings.activityTypes,tags:[legacyTag,otherInactiveTag]}})};
  const created=(await call(b,'/activities',{method:'POST',payload:{activity_type_id:'research',project_id:'general',description:'Work',business_purpose:'Purpose',tags:['temp-legacy'],started_at:'2026-08-02T09:00:00Z',ended_at:'2026-08-02T09:30:00Z'}})).body.data;
  legacyTag.active=false;
  const keepExisting=await call(b,`/activities/${created.activity_id}/amendments`,{method:'POST',payload:{base_version:created.version,changes:{description:'Updated'},reason:'tweak'}});
  assert.equal(keepExisting.status,200);assert.deepEqual(keepExisting.body.data.tags,['temp-legacy']);
  const addInactive=await call(b,`/activities/${created.activity_id}/amendments`,{method:'POST',payload:{base_version:keepExisting.body.data.version,changes:{tags:['temp-legacy','temp-other']},reason:'add tag'}});
  assert.equal(addInactive.status,400);assert.equal(addInactive.body.error.code,'tag_inactive');
});

test('a manual entry on a past date requires reconstruction_reason',async()=>{
  const b=bindings();
  const base={activity_type_id:'research',project_id:'general',description:'x',business_purpose:'y',started_at:'2026-08-01T09:00:00Z',ended_at:'2026-08-01T09:30:00Z'};
  const missing=await call(b,'/activities',{method:'POST',payload:base});
  assert.equal(missing.status,400);assert.equal(missing.body.error.code,'validation_failed');assert.equal(missing.body.error.details.field,'reconstruction_reason');
  const withReason=await call(b,'/activities',{method:'POST',payload:{...base,reconstruction_reason:'Recorded the next day'}});
  assert.equal(withReason.status,201);
  const sameDay=await call(b,'/activities',{method:'POST',payload:{...base,started_at:'2026-08-02T09:00:00Z',ended_at:'2026-08-02T09:30:00Z'}});
  assert.equal(sameDay.status,201);
});

test('evidence type must be one of the documented values',async()=>{
  const b=bindings();
  const activity=(await call(b,'/activities',{method:'POST',payload:{activity_type_id:'research',project_id:'general',description:'x',business_purpose:'y',started_at:'2026-08-02T09:00:00Z',ended_at:'2026-08-02T09:30:00Z'}})).body.data;
  const invalid=await call(b,`/activities/${activity.activity_id}/evidence`,{method:'POST',payload:{base_version:activity.version,type:'sticky-note'}});
  assert.equal(invalid.status,400);assert.equal(invalid.body.error.code,'invalid_evidence_type');
  const valid=await call(b,`/activities/${activity.activity_id}/evidence`,{method:'POST',payload:{base_version:activity.version,type:'document',label:'Spec'}});
  assert.equal(valid.status,201);
});

test('idempotent replay preserves the original success status code',async()=>{
  const b=bindings();const id='create-1';
  const payload={activity_type_id:'research',project_id:'general',description:'x',business_purpose:'y',started_at:'2026-08-02T09:00:00Z',ended_at:'2026-08-02T09:30:00Z'};
  const first=await call(b,'/activities',{method:'POST',requestId:id,payload});
  assert.equal(first.status,201);
  const replay=await call(b,'/activities',{method:'POST',requestId:id,payload});
  assert.equal(replay.status,201);assert.equal(replay.body.data.idempotent_replay,true);assert.equal(replay.body.data.activity_id,first.body.data.activity_id);
});

test('void, restore, evidence, day and month projections reconcile',async()=>{
  const b=bindings();const activity=(await call(b,'/activities',{method:'POST',payload:{activity_type_id:'software-development',project_id:'helixnote',description:'Build service',business_purpose:'Deliver product',started_at:'2026-08-02T10:00:00Z',ended_at:'2026-08-02T11:00:00Z'}})).body.data;
  const evidence=(await call(b,`/activities/${activity.activity_id}/evidence`,{method:'POST',payload:{base_version:activity.version,type:'url',uri:'https://example.test/evidence',label:'Work reference'}})).body.data;assert.equal(evidence.activity.evidence.length,1);
  const voided=(await call(b,`/activities/${activity.activity_id}/void`,{method:'POST',payload:{base_version:evidence.activity.version,reason:'Duplicate'}})).body.data;assert.equal(voided.voided,true);assert.equal((await call(b,'/days/2026-08-02')).body.data.total_exact_ms,0);
  const evidenceOnVoided=await call(b,`/activities/${activity.activity_id}/evidence`,{method:'POST',payload:{base_version:voided.version,type:'other',note:'Still linkable while voided'}});assert.equal(evidenceOnVoided.status,201);
  const detachOnVoided=await call(b,`/activities/${activity.activity_id}/evidence/${evidence.evidence.evidence_link_id}`,{method:'DELETE',payload:{base_version:evidenceOnVoided.body.data.activity.version,reason:'try to unlink while voided'}});assert.equal(detachOnVoided.status,409);assert.equal(detachOnVoided.body.error.code,'activity_not_recorded');
  const amendOnVoided=await call(b,`/activities/${activity.activity_id}/amendments`,{method:'POST',payload:{base_version:evidenceOnVoided.body.data.activity.version,changes:{description:'try to amend while voided'},reason:'try'}});assert.equal(amendOnVoided.status,409);assert.equal(amendOnVoided.body.error.code,'activity_not_recorded');
  const restored=(await call(b,`/activities/${activity.activity_id}/restore`,{method:'POST',payload:{base_version:evidenceOnVoided.body.data.activity.version,reason:'Not a duplicate'}})).body.data;assert.equal(restored.voided,false);
  const day=(await call(b,'/days/2026-08-02')).body.data;assert.equal(day.total_exact_ms,3600000);assert.equal(day.evidence_coverage,1);
  const month=(await call(b,'/months/2026-08')).body.data;assert.equal(month.decimal_hours,1);
  const jsonReport=(await call(b,'/reports/daily/2026-08-02')).body.data;assert.equal(jsonReport.format,'json');assert.equal(jsonReport.source.total_exact_ms,3600000);
  const markdownReport=(await call(b,'/reports/daily/2026-08-02?format=markdown')).body.data;assert.equal(markdownReport.format,'markdown');assert.ok(markdownReport.content.includes('software-development'));assert.ok(markdownReport.content.includes('Build service'));
  const csvReport=(await call(b,'/reports/monthly/2026-08?format=csv')).body.data;assert.equal(csvReport.format,'csv');assert.ok(csvReport.content.startsWith('activity_id,'));assert.ok(csvReport.content.includes('Build service'));
  const badFormat=await call(b,'/reports/daily/2026-08-02?format=xml');assert.equal(badFormat.status,400);assert.equal(badFormat.body.error.code,'invalid_format');
  const attested=await call(b,'/days/2026-08-02/attest',{method:'POST',payload:{projection_version:day.projection_version,statement:'Accurate to the best of my knowledge'}});assert.equal(attested.status,200);
});

test('day review flags an activity amended after it was attested',async()=>{
  const b=bindings();
  const untouched=(await call(b,'/activities',{method:'POST',payload:{activity_type_id:'research',project_id:'general',description:'Stable',business_purpose:'Planning',started_at:'2026-08-02T09:00:00Z',ended_at:'2026-08-02T09:30:00Z'}})).body.data;
  const activity=(await call(b,'/activities',{method:'POST',payload:{activity_type_id:'research',project_id:'general',description:'Will change',business_purpose:'Planning',started_at:'2026-08-02T10:00:00Z',ended_at:'2026-08-02T10:30:00Z'}})).body.data;
  const beforeAttest=(await call(b,'/days/2026-08-02/review')).body.data;assert.deepEqual(beforeAttest.warnings,[]);
  const day=(await call(b,'/days/2026-08-02')).body.data;
  await call(b,'/days/2026-08-02/attest',{method:'POST',payload:{projection_version:day.projection_version,statement:'Accurate to the best of my knowledge'}});
  const rightAfterAttest=(await call(b,'/days/2026-08-02/review')).body.data;assert.deepEqual(rightAfterAttest.warnings,[]);
  await call(b,`/activities/${activity.activity_id}/amendments`,{method:'POST',payload:{base_version:activity.version,changes:{description:'Changed after review'},reason:'correction'}});
  const afterAmend=(await call(b,'/days/2026-08-02/review')).body.data;
  assert.deepEqual(afterAmend.warnings,[{code:'amended_after_review',activity_id:activity.activity_id}]);
  assert.equal(afterAmend.warnings.some(w=>w.activity_id===untouched.activity_id),false);
});

test('split preserves duration and merge prevents report double counting',async()=>{
  const b=bindings();const source=(await call(b,'/activities',{method:'POST',payload:{activity_type_id:'research',project_id:'general',description:'Mixed work',business_purpose:'Business development',started_at:'2026-08-02T12:00:00Z',ended_at:'2026-08-02T13:00:00Z'}})).body.data;
  const split=(await call(b,`/activities/${source.activity_id}/split`,{method:'POST',payload:{base_version:source.version,reason:'Two activities',parts:[{duration_ms:36*60000,activity_type_id:'research',project_id:'general',description:'Research',business_purpose:'Planning'},{duration_ms:24*60000,activity_type_id:'linkedin-marketing',project_id:'echelon-foundry',description:'LinkedIn',business_purpose:'Marketing'}]}})).body.data;
  assert.equal(split.replacements.length,2);assert.equal(split.source.voided,true);assert.equal(split.source.superseded,true);assert.equal((await call(b,'/days/2026-08-02')).body.data.total_exact_ms,3600000);
  const restoreSplitSource=await call(b,`/activities/${source.activity_id}/restore`,{method:'POST',payload:{base_version:split.source.version,reason:'try to undo the split'}});
  assert.equal(restoreSplitSource.status,409);assert.equal(restoreSplitSource.body.error.code,'activity_superseded');
  const splitAgain=await call(b,`/activities/${source.activity_id}/split`,{method:'POST',payload:{base_version:split.source.version,reason:'try again',parts:[{duration_ms:1800000,activity_type_id:'research',project_id:'general',description:'a',business_purpose:'b'},{duration_ms:1800000,activity_type_id:'research',project_id:'general',description:'c',business_purpose:'d'}]}});
  assert.equal(splitAgain.status,409);assert.equal(splitAgain.body.error.code,'activity_not_recorded');
  const [a,c]=split.replacements;
  const mergeWithSupersededSource=await call(b,'/activities/merge',{method:'POST',payload:{source_ids:[source.activity_id,a.activity_id],base_versions:[split.source.version,a.version],activity_type_id:'research',project_id:'general',description:'x',business_purpose:'y',reason:'try'}});
  assert.equal(mergeWithSupersededSource.status,409);assert.equal(mergeWithSupersededSource.body.error.code,'activity_not_recorded');
  const merged=(await call(b,'/activities/merge',{method:'POST',payload:{source_ids:[a.activity_id,c.activity_id],base_versions:[a.version,c.version],activity_type_id:'research',project_id:'general',description:'Combined session',business_purpose:'Planning and marketing',reason:'Should be one session'}})).body.data;
  assert.equal(merged.sources.every(x=>x.voided&&x.superseded),true);assert.equal((await call(b,'/days/2026-08-02')).body.data.total_exact_ms,3600000);
  const restoreMergeSource=await call(b,`/activities/${a.activity_id}/restore`,{method:'POST',payload:{base_version:merged.sources[0].version,reason:'try to undo the merge'}});
  assert.equal(restoreMergeSource.status,409);assert.equal(restoreMergeSource.body.error.code,'activity_superseded');
});

test('authentication seam blocks unauthenticated requests',async()=>{
  const b={...bindings(),auth:{authenticate:()=>null}};const result=await call(b,'/config');assert.equal(result.status,401);assert.equal(result.body.error.code,'unauthorized');
});
