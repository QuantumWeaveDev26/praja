import {test} from 'node:test';
import assert from 'node:assert/strict';
import vm from 'node:vm';
import fs from 'node:fs';
const context={URLSearchParams,FormData,Blob};vm.runInNewContext(fs.readFileSync('src/Praja.Web/wwwroot/js/offline.js','utf8'),context);
const Queue=context.PrajaOfflineQueue;
const data={original:'No water supply in Ward 12 for three days.',district:'Mysuru',location:'Ward 12',language:'English',consent:true,sample:true};
function store(){const records=new Map();return {list:async()=>[...records.values()],put:async item=>records.set(item.submissionKey,{...item}),remove:async id=>records.delete(id)};}
const response=value=>({ok:true,headers:{get:()=> 'application/json'},json:async()=>value});
test('offline save survives new queue instance and reconnect sends once with consent and stable ID',async()=>{
 const db=store();const offline=new Queue(db,async()=>{throw Error('offline');});await offline.add(data,'stable-id');await assert.rejects(()=>offline.sync());assert.equal((await db.list())[0].state,'queued');
 let posts=0;const online=new Queue(db,async(url,options)=>{if(options.method){posts++;assert.equal(options.body.get('submissionKey'),'stable-id');assert.equal(options.body.get('consent'),'true');assert.equal(options.body.get('__RequestVerificationToken'),'fresh');return response({reference:'PRJ-1',code:'private-code'});}return response({token:'fresh'});});
 await online.sync();await online.sync();assert.equal(posts,1);assert.equal((await db.list())[0].code,'private-code');
});
test('lost server response retries the same key without marking receipt prematurely',async()=>{
 const db=store();let attempts=0;const keys=[];const queue=new Queue(db,async(url,o)=>{if(!o.method)return response({token:'fresh'});keys.push(o.body.get('submissionKey'));if(++attempts===1)throw Error('response lost');return response({reference:'same-case',code:'code'});});
 await queue.add(data,'same-key');await assert.rejects(()=>queue.sync());assert.equal((await db.list())[0].state,'queued');await queue.sync();assert.deepEqual(keys,['same-key','same-key']);assert.equal((await db.list())[0].state,'sent');
});
test('server rejection and throttling retain data, consent required, storage failures do not pretend save',async()=>{
 const db=store();const queue=new Queue(db,async(url,o)=>o.method?{ok:false,status:400}:response({token:'fresh'}));await assert.rejects(()=>queue.add({...data,consent:false},'a'));await queue.add(data,'b');await queue.sync();assert.equal((await db.list())[0].state,'needs-review');
 queue.request=async(url,o)=>o.method?{ok:false,status:429}:response({token:'fresh'});await assert.rejects(()=>queue.sync());assert.equal((await db.list()).length,1);
 const broken=new Queue({list:async()=>[],put:async()=>{throw Error('disk full');}},()=>{});await assert.rejects(()=>broken.add(data,'c'));
});
test('concurrent sync clicks do not create concurrent requests',async()=>{
 const db=store();let release;const wait=new Promise(r=>release=r);let posts=0;const queue=new Queue(db,async(url,o)=>{await wait;if(!o.method)return response({token:'fresh'});posts++;return response({reference:'one',code:'code'});});await queue.add(data,'id');const first=queue.sync();await queue.sync();release();await first;assert.equal(posts,1);
});
test('worker caches only public offline assets and never caches officer or tracking responses',()=>{
 const handlers={};let captured;const self={location:{origin:'https://praja.test'},addEventListener:(event,cb)=>handlers[event]=cb};
 vm.runInNewContext(fs.readFileSync('src/Praja.Web/wwwroot/offline-worker.js','utf8'),{self,URL,caches:{},fetch(){}});
 for(const path of ['/Work','/Track','/Account/Login','/OfflineSync','/AiSettings']){captured=false;handlers.fetch({request:{url:'https://praja.test'+path,method:'GET',mode:'navigate'},respondWith(){captured=true;}});assert.equal(captured,false);}
});
test('missing receipt or HTML login response never marks a complaint as sent',async()=>{
 const db=store();const queue=new Queue(db,async(url,o)=>o.method?response({}):response({token:'fresh'}));await queue.add(data,'id');await assert.rejects(()=>queue.sync());assert.equal((await db.list())[0].state,'queued');
 queue.request=async()=>({ok:true,headers:{get:()=> 'text/html'}});await assert.rejects(()=>queue.sync());assert.equal((await db.list())[0].state,'queued');
});
test('offline worker provides cached public shell when online submission navigation fails',async()=>{
 const handlers={};let result;const shell={body:'offline shell'};const self={location:{origin:'https://praja.test'},addEventListener:(event,cb)=>handlers[event]=cb};
 vm.runInNewContext(fs.readFileSync('src/Praja.Web/wwwroot/offline-worker.js','utf8'),{self,URL,caches:{open:async()=>({match:async path=>{assert.equal(path,'/offline.html');return shell;}})},fetch:async()=>{throw Error('offline');}});
 handlers.fetch({request:{url:'https://praja.test/Submit',method:'GET',mode:'navigate'},respondWith:value=>result=value});assert.equal(await result,shell);
});

const photo=new Blob(['fictional image bytes'],{type:'image/jpeg'});
const audio=new Blob(['fictional audio bytes'],{type:'audio/webm'});
const media={...data,photo,photoName:'incident.jpg',photoConsent:true,audio,audioName:'recording.webm',audioConsent:true};

test('offline photo and voice survive restart, require transcript review, then register exactly once',async()=>{
 const db=store();await new Queue(db,()=>{}).add({...media,original:''},'media-id');
 let speech=0,registrations=0;
 const request=async(url,o)=>{
  if(!o.method)return response({token:'fresh'});
  if(url.startsWith('/CitizenVoice')){speech++;assert.equal(o.body.get('voiceConsent'),'true');assert.equal(o.body.get('audio').size,audio.size);assert.equal(o.body.get('photo'),null);return response({text:data.original});}
  registrations++;assert.equal(o.body.get('submissionKey'),'media-id');assert.equal(o.body.get('source'),'Voice transcript');assert.equal(o.body.get('original'),data.original+' Please repair urgently.');assert.equal(o.body.get('photo').size,photo.size);assert.equal(o.body.get('photoConsent'),'true');assert.equal(o.body.get('audio'),null);return response({reference:'PRJ-MEDIA',code:'private'});
 };
 const queue=new Queue(db,request);await queue.sync();await queue.sync();assert.equal(speech,1);assert.equal(registrations,0);
 const saved=(await db.list())[0];assert.equal(saved.state,'needs-transcript-review');assert.equal(saved.audio.size,audio.size);assert.equal(saved.photo.size,photo.size);
 const restarted=new Queue(db,request);await restarted.sync();assert.equal(registrations,0);await assert.rejects(()=>restarted.review('media-id','short'));await restarted.review('media-id',data.original+' Please repair urgently.');await restarted.sync();await restarted.sync();
 const receipt=(await db.list())[0];assert.equal(receipt.reference,'PRJ-MEDIA');assert.equal(receipt.photo,undefined);assert.equal(receipt.audio,undefined);assert.equal(speech,1);assert.equal(registrations,1);
});

test('photo lost receipt retains identical bytes and submission key until confirmed retry',async()=>{
 const db=store();let posts=0;const queue=new Queue(db,async(url,o)=>{
  if(!o.method)return response({token:'fresh'});
  assert.equal(o.body.get('submissionKey'),'photo-id');assert.equal(await o.body.get('photo').text(),await photo.text());assert.equal(o.body.get('source'),'Text');if(++posts===1)throw Error('response lost');return response({reference:'same-case',code:'same-code'});
 });await queue.add({...data,photo,photoConsent:true},'photo-id');await assert.rejects(()=>queue.sync());assert.equal((await db.list())[0].photo.size,photo.size);await queue.sync();assert.equal(posts,2);assert.equal((await db.list())[0].photo,undefined);
});

test('speech failure and malformed transcript retain audio and photo for later retry',async()=>{
 for(const failure of [{ok:false,status:400},{ok:false,status:429},response({}),response({text:'x'.repeat(6001)})]){
  const db=store();const queue=new Queue(db,async(url,o)=>o.method?failure:response({token:'fresh'}));await queue.add(media,'voice');try{await queue.sync();}catch{}
  let saved=(await db.list())[0];assert.notEqual(saved.state,'sent');assert.equal(saved.audio.size,audio.size);assert.equal(saved.photo.size,photo.size);
  queue.request=async(url,o)=>o.method?response({text:data.original}):response({token:'fresh'});await queue.sync();saved=(await db.list())[0];assert.equal(saved.state,'needs-transcript-review');
 }
});

test('media requires explicit consent, nonempty files, supported photo and 8 MiB bounds',async()=>{
 const db=store();const queue=new Queue(db,()=>{});
 for(const invalid of [{...media,photoConsent:false},{...media,audioConsent:false},{...media,photo:new Blob([],{type:'image/jpeg'})},{...media,audio:new Blob([])},{...media,photo:new Blob(['x'],{type:'image/svg+xml'})},{...media,photo:new Blob([new Uint8Array(8*1024*1024+1)],{type:'image/jpeg'})},{...media,audio:new Blob([new Uint8Array(8*1024*1024+1)])}])await assert.rejects(()=>queue.add(invalid,'bad'));
 assert.equal((await db.list()).length,0);
});

test('20-record and 50 MiB media budgets reject without evicting prior unsent complaints',async()=>{
 const db=store();const queue=new Queue(db,()=>{});for(let i=0;i<20;i++)await queue.add(data,'text-'+i);await assert.rejects(()=>queue.add(data,'extra'));assert.equal((await db.list()).length,20);
 const large=new Blob([new Uint8Array(8*1024*1024)],{type:'image/jpeg'});const mediaDb=store();const mediaQueue=new Queue(mediaDb,()=>{});
 for(let i=0;i<6;i++)await mediaQueue.add({...data,photo:large,photoConsent:true},'large-'+i);
 await assert.rejects(()=>mediaQueue.add({...data,photo:large,photoConsent:true},'overflow'));assert.equal((await mediaDb.list()).length,6);
});

test('storage failure after server receipt preserves original media for safe same-key retry',async()=>{
 const backing=store();let failSave=false,posts=0;const db={...backing,put:async item=>{if(failSave&&item.state==='sent')throw Error('quota unavailable');return backing.put(item);}};
 const queue=new Queue(db,async(url,o)=>{if(!o.method)return response({token:'fresh'});posts++;assert.equal(o.body.get('submissionKey'),'quota');return response({reference:'same',code:'private'});});
 await queue.add({...data,photo,photoConsent:true},'quota');failSave=true;await assert.rejects(()=>queue.sync());assert.equal((await db.list())[0].photo.size,photo.size);failSave=false;await queue.sync();assert.equal(posts,2);assert.equal((await db.list())[0].state,'sent');
});

test('transcript save failure retains recording and typed details before safe retry and review',async()=>{
 const backing=store();let rejectTranscript=true;const db={...backing,put:async item=>{if(rejectTranscript&&item.state==='needs-transcript-review')throw Error('storage full');return backing.put(item);}};
 const queue=new Queue(db,async(url,o)=>o.method?response({text:'Street lights stopped working near the school.'}):response({token:'fresh'}));await queue.add(media,'typed-and-voice');await assert.rejects(()=>queue.sync());let item=(await db.list())[0];assert.equal(item.original,data.original);assert.equal(item.audio.size,audio.size);assert.equal(item.state,'queued');
 rejectTranscript=false;await queue.sync();item=(await db.list())[0];assert.equal(item.typedOriginal,data.original);assert.equal(item.state,'needs-transcript-review');assert.equal(item.audio.size,audio.size);await queue.review('typed-and-voice',item.original+' '+item.typedOriginal);assert.match((await db.list())[0].original,/Street lights.*No water supply/);
});
