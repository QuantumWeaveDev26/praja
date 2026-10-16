import {test} from 'node:test';
import assert from 'node:assert/strict';
import vm from 'node:vm';
import fs from 'node:fs';
const context={URLSearchParams};vm.runInNewContext(fs.readFileSync('src/Praja.Web/wwwroot/js/offline.js','utf8'),context);
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
