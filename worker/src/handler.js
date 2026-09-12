import { MemoryLedgerStore } from './store.js';
import { ServiceError } from './errors.js';

const json=(body,status=200,requestId=crypto.randomUUID())=>new Response(JSON.stringify(body),{status,headers:{'content-type':'application/json; charset=utf-8','cache-control':'no-store','x-request-id':requestId}});
const meta=requestId=>({request_id:requestId,server_time:new Date().toISOString(),api_version:'v1'});
const success=(data,requestId,status=200)=>json({data,meta:meta(requestId)},status,requestId);
const failure=(error,requestId)=>json({error:{code:error.code??'internal_error',message:error.message??'The service could not complete the request.',retryable:error.retryable??false,details:error.details??{}},meta:meta(requestId)},error.status??500,requestId);

const projects=[{id:'echelon-foundry',name:'Echelon Foundry',active:true,version:'v1'},{id:'visual-engineering',name:'Visual Engineering',active:true,version:'v1'},{id:'helixnote',name:'HelixNote',active:true,version:'v1'},{id:'general',name:'General',active:true,version:'v1'},{id:'archived-initiative',name:'Archived Initiative',active:false,version:'v1'}];
const activityTypes=[{id:'research',name:'Research',active:true,version:'v1'},{id:'linkedin-marketing',name:'LinkedIn marketing',active:true,version:'v1'},{id:'software-development',name:'Software development',active:true,version:'v1'},{id:'administration',name:'Administration',active:true,version:'v1'},{id:'meeting',name:'Meeting',active:true,version:'v1'},{id:'retired-type',name:'Retired type',active:false,version:'v1'}];
const tags=[{id:'billable',name:'Billable',active:true,version:'v1'},{id:'client-facing',name:'Client-facing',active:true,version:'v1'},{id:'internal-only',name:'Internal only',active:true,version:'v1'},{id:'legacy',name:'Legacy',active:false,version:'v1'}];

export const defaultBindings={
  store:new MemoryLedgerStore({directories:{projects,activityTypes,tags}}),
  auth:{mode:'development',authenticate:()=>({user_id:'owner',roles:['owner']})},
  config:{schema_version:'1.0.0',projection_version:'service-config-1',timezone:'America/Indiana/Indianapolis',display:{six_minute_controls:true,theme:'system'},features:{evidence_uploads:true,offline_commands:true,reports:true}},
  projects,
  activityTypes,
  tags
};

const parseBody=async request=>{if(request.method==='GET'||request.method==='DELETE'&&!request.headers.get('content-length'))return{};try{return await request.json();}catch{throw new ServiceError('invalid_json','Request body must be valid JSON.');}};
const routeMatch=(path,pattern)=>{const names=[];const regex=new RegExp(`^${pattern.replace(/:([A-Za-z]+)/g,(_,name)=>{names.push(name);return'([^/]+)';})}$`);const match=path.match(regex);return match?Object.fromEntries(names.map((name,i)=>[name,decodeURIComponent(match[i+1])])):null;};

const REPORT_FORMATS=['json','markdown','csv'];
const REPORT_COLUMNS=['activity_id','activity_type_id','project_id','description','business_purpose','started_at','ended_at','exact_duration_ms','voided'];
const csvField=value=>{const s=String(value??'');return /[",\n]/.test(s)?`"${s.replace(/"/g,'""')}"`:s;};
const toCsv=summary=>[REPORT_COLUMNS.join(','),...summary.activities.map(a=>REPORT_COLUMNS.map(c=>csvField(a[c])).join(','))].join('\n');
const toMarkdown=summary=>{
  const identity=summary.date??summary.month;
  const rows=summary.activities.map(a=>`| ${a.activity_type_id} | ${a.project_id} | ${(a.description??'').replace(/\|/g,'\\|')} | ${Math.round(a.exact_duration_ms/60000)} |`);
  return [`# Report for ${identity}`,'',`Total: ${summary.total_minutes} minutes (${summary.decimal_hours} hours)`,'','| Activity type | Project | Description | Duration (min) |','| --- | --- | --- | --- |',...rows].join('\n');
};
const renderReport=(reportId,format,summary)=>format==='json'?{report_id:reportId,format,source:summary}:{report_id:reportId,format,content:format==='markdown'?toMarkdown(summary):toCsv(summary)};

export async function handleRequest(request,bindings=defaultBindings){
  const requestId=request.headers.get('x-request-id')||request.headers.get('idempotency-key')||crypto.randomUUID();
  try{
    const url=new URL(request.url);const path=url.pathname.replace(/^\/(api|service)\/v1/,'');
    if(path==='/health'&&request.method==='GET')return success({status:'ok'},requestId);
    const user=await bindings.auth.authenticate(request);if(!user)throw new ServiceError('unauthorized','Sign in is required.',{status:401});
    if(path==='/session'&&request.method==='GET')return success({authenticated:true,user:{id:user.user_id},csrf_required:true},requestId);
    if(path==='/config'&&request.method==='GET')return success(bindings.config,requestId);
    if(path==='/projects'&&request.method==='GET')return success(bindings.projects,requestId);
    if(path==='/activity-types'&&request.method==='GET')return success(bindings.activityTypes,requestId);
    if(path==='/tags'&&request.method==='GET')return success(bindings.tags,requestId);
    const body=await parseBody(request);const payload=body.payload??body;const commandId=body.request_id??request.headers.get('idempotency-key')??requestId;
    if(request.method!=='GET'){const replay=bindings.store.replay(commandId);if(replay)return success({...replay.result,idempotent_replay:true},requestId,replay.status);}
    let result;let status=200;
    if(path==='/timers/current'&&request.method==='GET')result={timer:bindings.store.currentTimer(user.user_id)};
    else if(path==='/timers/start'&&request.method==='POST')result=bindings.store.startTimer(user.user_id,payload);
    else if(path==='/timers/pause'&&request.method==='POST')result=bindings.store.pauseTimer(user.user_id);
    else if(path==='/timers/resume'&&request.method==='POST')result=bindings.store.resumeTimer(user.user_id);
    else if(path==='/timers/stop'&&request.method==='POST')result=bindings.store.stopTimer(user.user_id);
    else if(path==='/activities'&&request.method==='POST'){result=bindings.store.createActivity({...payload,client_timestamp:body.client_timestamp},user.user_id);status=201;}
    else if(path==='/activities/merge'&&request.method==='POST')result=bindings.store.mergeActivities(payload,user.user_id);
    else if(routeMatch(path,'/activities/:id')&&request.method==='GET'){const {id}=routeMatch(path,'/activities/:id');result=bindings.store.activity(id);if(!result)throw new ServiceError('activity_not_found','Activity was not found.',{status:404});}
    else if(routeMatch(path,'/activities/:id/amendments')&&request.method==='POST'){const {id}=routeMatch(path,'/activities/:id/amendments');result=bindings.store.amendActivity(id,payload,user.user_id);}
    else if(routeMatch(path,'/activities/:id/void')&&request.method==='POST'){const {id}=routeMatch(path,'/activities/:id/void');result=bindings.store.voidActivity(id,payload,user.user_id);}
    else if(routeMatch(path,'/activities/:id/restore')&&request.method==='POST'){const {id}=routeMatch(path,'/activities/:id/restore');result=bindings.store.restoreActivity(id,payload,user.user_id);}
    else if(routeMatch(path,'/activities/:id/split')&&request.method==='POST'){const {id}=routeMatch(path,'/activities/:id/split');result=bindings.store.splitActivity(id,payload,user.user_id);}
    else if(routeMatch(path,'/activities/:id/evidence')&&request.method==='POST'){const {id}=routeMatch(path,'/activities/:id/evidence');result=bindings.store.attachEvidence(id,payload,user.user_id);status=201;}
    else if(routeMatch(path,'/activities/:id/evidence/:evidenceId')&&request.method==='DELETE'){const {id,evidenceId}=routeMatch(path,'/activities/:id/evidence/:evidenceId');result=bindings.store.detachEvidence(id,evidenceId,payload,user.user_id);}
    else if(routeMatch(path,'/days/:date')&&request.method==='GET'){const {date}=routeMatch(path,'/days/:date');result=bindings.store.day(date);}
    else if(routeMatch(path,'/days/:date/review')&&request.method==='GET'){const {date}=routeMatch(path,'/days/:date/review');result=bindings.store.dayReview(date);}
    else if(routeMatch(path,'/days/:date/attest')&&request.method==='POST'){const {date}=routeMatch(path,'/days/:date/attest');result=bindings.store.attestDay(date,payload,user.user_id);}
    else if(routeMatch(path,'/months/:month')&&request.method==='GET'){const {month}=routeMatch(path,'/months/:month');result=bindings.store.month(month);}
    else if(routeMatch(path,'/reports/daily/:date')&&request.method==='GET'){const {date}=routeMatch(path,'/reports/daily/:date');const format=url.searchParams.get('format')??'json';if(!REPORT_FORMATS.includes(format))throw new ServiceError('invalid_format',`format must be one of: ${REPORT_FORMATS.join(', ')}.`,{details:{format,allowed:REPORT_FORMATS}});result=renderReport(`daily-${date}-${bindings.store.events.length}`,format,bindings.store.day(date));}
    else if(routeMatch(path,'/reports/monthly/:month')&&request.method==='GET'){const {month}=routeMatch(path,'/reports/monthly/:month');const format=url.searchParams.get('format')??'json';if(!REPORT_FORMATS.includes(format))throw new ServiceError('invalid_format',`format must be one of: ${REPORT_FORMATS.join(', ')}.`,{details:{format,allowed:REPORT_FORMATS}});result=renderReport(`monthly-${month}-${bindings.store.events.length}`,format,bindings.store.month(month));}
    else throw new ServiceError('not_found','The requested API resource does not exist.',{status:404});
    if(request.method!=='GET')bindings.store.remember(commandId,result,status);
    return success(result,requestId,status);
  }catch(error){return failure(error,requestId);}
}
export default{fetch:handleRequest};
