(function(root){
 'use strict';
 class OfflineQueue {
  constructor(store,request){this.store=store;this.request=request;this.running=false;}
  async add(data,id){
   if(!data.consent||data.original.trim().length<20||data.original.length>6000||data.district.trim().length<2||data.district.length>100||data.location.trim().length<2||data.location.length>200||!['English','Kannada'].includes(data.language))throw Error('Check the complaint, location and consent.');
   if((await this.store.list()).length>=20)throw Error('This browser holds 20 complaints. Remove saved receipts before adding more.');
   await this.store.put({...data,submissionKey:id,state:'queued'});
  }
  async sync(){
   if(this.running)return;this.running=true;
   try{
    for(const item of await this.store.list()){
     if(item.state==='sent')continue;
     const tokenResponse=await this.request('/OfflineSync',{cache:'no-store',credentials:'same-origin'});
     if(!tokenResponse.ok||!tokenResponse.headers.get('content-type')?.includes('application/json'))throw Error('Praja is unavailable or sign-in needs attention. Saved complaints remain on this device.');
     const {token}=await tokenResponse.json();if(!token)throw Error('Could not prepare a secure submission. Retry later.');
     const body=new URLSearchParams({...item,consent:String(item.consent),sample:String(item.sample),__RequestVerificationToken:token});
     const response=await this.request('/OfflineSync',{method:'POST',credentials:'same-origin',body});
     if(!response.ok){
      if(response.status===400){await this.store.put({...item,state:'needs-review'});continue;}
      throw Error('Sync paused. Saved complaints remain queued; retry shortly.');
     }
     const receipt=await response.json();if(!receipt.reference||!receipt.code)throw Error('Receipt was not confirmed. Retry safely with the same complaint.');
     await this.store.put({...item,state:'sent',reference:receipt.reference,code:receipt.code});
    }
   }finally{this.running=false;}
  }
 }
 root.PrajaOfflineQueue=OfflineQueue;
 if(typeof document==='undefined')return;
 const status=document.getElementById('offline-status'),form=document.getElementById('offline-form');
 if(!form)return;
 const tell=text=>{status.textContent=text;};
 function openStore(){return new Promise((resolve,reject)=>{
  const request=indexedDB.open('PrajaCitizenOffline',1);
  request.onupgradeneeded=()=>request.result.createObjectStore('complaints',{keyPath:'submissionKey'});
  request.onerror=()=>reject(Error('Device storage unavailable. Do not close an unsaved complaint.'));
  request.onsuccess=()=>{
   const db=request.result;
   function operation(mode,action){return new Promise((done,fail)=>{const tx=db.transaction('complaints',mode);const req=action(tx.objectStore('complaints'));tx.oncomplete=()=>done(req.result);tx.onerror=()=>fail(Error('Could not save on this device. Keep your complaint open.'));tx.onabort=tx.onerror;});}
   resolve({list:()=>operation('readonly',s=>s.getAll()),put:item=>operation('readwrite',s=>s.put(item)),remove:id=>operation('readwrite',s=>s.delete(id))});
  };
 });}
 (async()=>{
  try{
   const store=await openStore();
   const queue=new OfflineQueue(store,(url,options)=>fetch(url,{...options,signal:AbortSignal.timeout(20000)}));
   const items=document.getElementById('offline-items');
   async function render(){items.replaceChildren();for(const item of await store.list()){
    const card=document.createElement('section');card.className='panel form-panel';
    const title=document.createElement('h3');title.textContent=item.state==='sent'?'Received: '+item.reference:item.state==='needs-review'?'Needs review — not confirmed':'Queued — not registered';card.append(title);
    const text=document.createElement('p');text.textContent=item.original+' · '+item.district+' · '+item.location;card.append(text);
    if(item.state==='needs-review'){const warning=document.createElement('p');warning.textContent='The server rejected this submission. Keep the original here and use the online portal for help; retry after resolving the issue.';card.append(warning);}
    if(item.code){const label=document.createElement('label');label.textContent='Private tracking code (valid seven days from registration)';const code=document.createElement('textarea');code.readOnly=true;code.value=item.code;label.append(code);card.append(label);const link=document.createElement('a');link.href='/Track';link.textContent='Open citizen tracking';card.append(link);}
    const remove=document.createElement('button');remove.type='button';remove.className='button secondary';remove.textContent='Remove from this device';remove.disabled=queue.running;remove.onclick=async()=>{if(confirm('Remove this local copy? Unsent complaints will be lost. Registered cases will not be deleted.')){await store.remove(item.submissionKey);await render();}};card.append(remove);items.append(card);
   }}
   async function sync(){if(queue.running)return;document.getElementById('sync-offline').disabled=true;try{const pending=queue.sync();await render();await pending;tell('Sync checked. See each saved complaint for its receipt or queued status.');}catch(error){tell(error.message||'Connection unavailable. Complaints remain saved.');}finally{document.getElementById('sync-offline').disabled=false;await render();}}
   let saving=false;
   form.addEventListener('submit',async event=>{event.preventDefault();if(saving||!form.reportValidity())return;saving=true;document.getElementById('save-offline').disabled=true;try{await queue.add({original:form.elements.original.value,district:form.elements.district.value,location:form.elements.location.value,language:form.elements.language.value,consent:document.getElementById('consent').checked,sample:document.getElementById('sample').checked},crypto.randomUUID());form.reset();tell('Saved on this device. Waiting for a confirmed receipt.');await render();await sync();}catch(error){tell(error.message);}finally{saving=false;document.getElementById('save-offline').disabled=false;}});
   document.getElementById('sync-offline').onclick=sync;window.addEventListener('online',sync);window.addEventListener('offline',()=>tell('Offline. Saved complaints will sync when Praja is reachable.'));
   await render();
   if('serviceWorker' in navigator){try{await navigator.serviceWorker.register('/offline-worker.js');await navigator.serviceWorker.ready;tell('Offline page ready on this device.');}catch{tell('Offline reload could not be enabled. Keep this page open; queued data is still saved.');}}
   else tell('Offline reload is unsupported in this browser. Keep this page open.');
   if((await store.list()).some(i=>i.state!=='sent'))await sync();
   setInterval(async()=>{if((await store.list()).some(i=>i.state!=='sent'))await sync();},60000);
  }catch(error){tell(error.message);document.getElementById('save-offline').disabled=true;}
 })();
})(globalThis);
