(function(root){
 'use strict';
 const LIMIT=8*1024*1024, TOTAL=50*1024*1024;
 const mediaSize=item=>(item.photo?.size||0)+(item.audio?.size||0);
 class OfflineQueue {
  constructor(store,request){this.store=store;this.request=request;this.running=false;}
  async add(data,id){
   const original=data.original||'';
   if(!data.consent||(!data.audio&&original.trim().length<20)||original.length>6000||data.district.trim().length<2||data.district.length>100||data.location.trim().length<2||data.location.length>200||!['English','Kannada'].includes(data.language))throw Error('Check the complaint, location and consent.');
   if(data.photo&&(!data.photoConsent||!data.photo.size||data.photo.size>LIMIT||!['image/jpeg','image/png'].includes(data.photo.type)))throw Error('Choose a JPEG or PNG photo up to 8 MiB and agree to its upload.');
   if(data.audio&&(!data.audioConsent||!data.audio.size||data.audio.size>LIMIT))throw Error('Choose a nonempty recording up to 8 MiB and agree to speech processing.');
   const all=await this.store.list();
   if(all.length>=20)throw Error('This browser holds 20 complaints. Remove saved receipts before adding more.');
   if(all.reduce((sum,item)=>sum+mediaSize(item),0)+mediaSize(data)>TOTAL)throw Error('Saved media would exceed 50 MiB. Sync or remove an existing local complaint first.');
   await this.store.put({...data,original,submissionKey:id,state:'queued',audioReviewed:false});
  }
  async review(id,text){
   if(this.running)throw Error('Wait for the current sync to finish.');
   const item=(await this.store.list()).find(i=>i.submissionKey===id);
   if(!item||item.state!=='needs-transcript-review'||text.trim().length<20||text.length>6000)throw Error('Review the transcript: use 20 to 6,000 characters.');
   await this.store.put({...item,original:text,audioReviewed:true,state:'queued'});
  }
  async sync(){
   if(this.running)return;this.running=true;
   try{
    for(const item of await this.store.list()){
     if(item.state==='sent'||item.state==='needs-transcript-review')continue;
     const tokenResponse=await this.request('/OfflineSync',{cache:'no-store',credentials:'same-origin'});
     if(!tokenResponse.ok||!tokenResponse.headers.get('content-type')?.includes('application/json'))throw Error('Praja is unavailable or sign-in needs attention. Saved complaints remain on this device.');
     const {token}=await tokenResponse.json();if(!token)throw Error('Could not prepare a secure submission. Retry later.');
     if(item.audio&&!item.audioReviewed){
      const body=new FormData();body.set('audio',item.audio,item.audioName||'recording.webm');body.set('language',item.language);body.set('voiceConsent',String(item.audioConsent));body.set('__RequestVerificationToken',token);
      const response=await this.request('/CitizenVoice?handler=Audio',{method:'POST',credentials:'same-origin',body});
      if(!response.ok){if(response.status===400){await this.store.put({...item,state:'needs-review'});continue;}throw Error('Transcription paused. Recording remains saved; retry shortly.');}
      const result=await response.json();if(typeof result.text!=='string'||!result.text.trim()||result.text.length>6000)throw Error('No usable transcript returned. Recording remains saved for retry.');
      await this.store.put({...item,typedOriginal:item.typedOriginal??item.original,original:result.text,state:'needs-transcript-review'});continue;
     }
     const fields={original:item.original,district:item.district,location:item.location,language:item.language,submissionKey:item.submissionKey,consent:String(item.consent),sample:String(!!item.sample),source:item.audioReviewed?'Voice transcript':'Text',__RequestVerificationToken:token};
     const body=item.photo?new FormData():new URLSearchParams();for(const [key,value] of Object.entries(fields))body.set(key,value);
     if(item.photo){body.set('photo',item.photo,item.photoName||'incident.jpg');body.set('photoConsent',String(item.photoConsent));}
     const response=await this.request('/OfflineSync',{method:'POST',credentials:'same-origin',body});
     if(!response.ok){
      if(response.status===400){await this.store.put({...item,state:'needs-review'});continue;}
      throw Error('Sync paused. Saved complaints remain queued; retry shortly.');
     }
     const receipt=await response.json();if(!receipt.reference||!receipt.code)throw Error('Receipt was not confirmed. Retry safely with the same complaint.');
     const {photo,audio,...saved}=item;await this.store.put({...saved,state:'sent',reference:receipt.reference,code:receipt.code});
    }
   }finally{this.running=false;}
  }
 }
 root.PrajaOfflineQueue=OfflineQueue;
 if(typeof document==='undefined')return;
 const byId=id=>document.getElementById(id),status=byId('offline-status'),form=byId('offline-form');
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
 let saving=false;
 let recording=null,stream=null,timer=null,audioBlob=null,audioName='',previewUrl=null,recordingPending=false,recordingFailed=false;
 const setAudio=(blob,name)=>{if(previewUrl)URL.revokeObjectURL(previewUrl);audioBlob=blob;audioName=name;previewUrl=blob?URL.createObjectURL(blob):null;byId('audio-preview').hidden=!blob;byId('clear-voice').hidden=!blob;if(blob)byId('audio-preview').src=previewUrl;else byId('audio-preview').removeAttribute('src');};
 const release=()=>{clearTimeout(timer);stream?.getTracks().forEach(track=>track.stop());stream=null;};
 const restoreRecordingControls=()=>{recordingPending=false;byId('record-voice').disabled=false;byId('stop-voice').disabled=true;byId('save-offline').disabled=saving;byId('audio').disabled=false;byId('clear-voice').disabled=false;};
 const stop=()=>{try{if(recording&&recording.state!=='inactive')recording.stop();}catch{recordingFailed=true;restoreRecordingControls();byId('recording-status').textContent='Recording could not finish. Try again or choose an audio file.';}finally{release();}};
 byId('stop-voice').onclick=stop;
 byId('clear-voice').onclick=()=>{setAudio(null,'');byId('audio').value='';};
 byId('audio').onchange=()=>{const file=byId('audio').files[0];if(file)setAudio(file,file.name);};
 byId('record-voice').onclick=async()=>{
  if(saving||recordingPending||recording?.state==='recording')return;
  recordingPending=true;recordingFailed=false;byId('record-voice').disabled=true;byId('save-offline').disabled=true;byId('audio').disabled=true;byId('clear-voice').disabled=true;
  try{
   if(!navigator.mediaDevices?.getUserMedia||typeof MediaRecorder==='undefined')throw Error('Recording is unavailable here. Use an audio file or type the complaint.');
   stream=await navigator.mediaDevices.getUserMedia({audio:true});
   const type=['audio/webm;codecs=opus','audio/ogg;codecs=opus','audio/webm','audio/ogg'].find(t=>MediaRecorder.isTypeSupported(t));
   if(!type)throw Error('This browser cannot record a supported audio format. Choose a WAV, MP3, WebM or OGG file instead.');
   recording=new MediaRecorder(stream,{mimeType:type});const chunks=[];let bytes=0;
   recording.ondataavailable=event=>{if(event.data.size){chunks.push(event.data);bytes+=event.data.size;if(bytes>LIMIT){recordingFailed=true;stop();byId('recording-status').textContent='Recording exceeded 8 MiB. Record a shorter message.';}}};
   recording.onerror=()=>{recordingFailed=true;stop();restoreRecordingControls();byId('recording-status').textContent='Recording failed. Try again or choose an audio file.';};
   recording.onstop=()=>{release();const mime=recording.mimeType||'audio/webm';const blob=new Blob(chunks,{type:mime});if(!recordingFailed&&blob.size&&blob.size<=LIMIT){setAudio(blob,'recording.'+(mime.includes('ogg')?'ogg':'webm'));byId('audio').value='';byId('recording-status').textContent='Recording ready. Play it back, then save the complaint.';}else if(!recordingFailed)byId('recording-status').textContent='No sound file captured. Please try again.';restoreRecordingControls();};
   recording.start(1000);byId('stop-voice').disabled=false;byId('recording-status').textContent='Recording… choose Stop recording when finished. Stops automatically after two minutes.';timer=setTimeout(stop,120000);
  }catch(error){release();restoreRecordingControls();byId('recording-status').textContent=error.name==='NotAllowedError'?'Microphone permission denied. Allow microphone access or choose an audio file.':error.message;}
 };
 window.addEventListener('pagehide',()=>{stop();if(previewUrl)URL.revokeObjectURL(previewUrl);});
 (async()=>{
  try{
   const store=await openStore();
   const queue=new OfflineQueue(store,(url,options)=>fetch(url,{...options,signal:AbortSignal.timeout(60000)}));
   const items=byId('offline-items');let cardUrls=[];const transcriptEdits=new Map();
   async function render(){cardUrls.forEach(url=>URL.revokeObjectURL(url));cardUrls=[];items.replaceChildren();for(const item of await store.list()){
    const card=document.createElement('section');card.className='panel form-panel';
    const title=document.createElement('h3');title.textContent=item.state==='sent'?'Received: '+item.reference:item.state==='needs-transcript-review'?'Review voice transcript — not registered':item.state==='needs-review'?'Needs review — not confirmed':'Queued — not registered';card.append(title);
    const text=document.createElement('p');text.textContent=(item.original||'Voice complaint awaiting transcription')+' · '+item.district+' · '+item.location;card.append(text);
    for(const [key,tag] of [['photo','img'],['audio','audio']]){if(!item[key])continue;const element=document.createElement(tag),url=URL.createObjectURL(item[key]);cardUrls.push(url);element.src=url;if(tag==='img'){element.alt='Saved incident photo';element.style.maxWidth='100%';element.style.maxHeight='240px';}else element.controls=true;card.append(element);}
    if(item.state==='needs-review'){const warning=document.createElement('p');warning.textContent='Processing could not finish. The original and media remain saved. Check consent, file format and service availability, then choose Sync now. Use the online portal if the problem continues.';card.append(warning);}
    if(item.state==='needs-transcript-review'){
     if(item.typedOriginal){const original=document.createElement('p');original.textContent='Your typed description (include any important details in the final transcript): '+item.typedOriginal;card.append(original);}
     const label=document.createElement('label');label.textContent='Listen to your recording and correct the transcript';const input=document.createElement('textarea');input.value=transcriptEdits.get(item.submissionKey)??item.original;input.oninput=()=>transcriptEdits.set(item.submissionKey,input.value);input.minLength=20;input.maxLength=6000;input.rows=6;label.append(input);card.append(label);
     const approve=document.createElement('button');approve.type='button';approve.className='button primary';approve.textContent='Transcript is correct — send complaint';approve.disabled=queue.running;approve.onclick=async()=>{try{await queue.review(item.submissionKey,input.value);transcriptEdits.delete(item.submissionKey);await sync();}catch(error){tell(error.message);}};card.append(approve);
    }
    if(item.code){const label=document.createElement('label');label.textContent='Private tracking code (valid seven days from registration)';const code=document.createElement('textarea');code.readOnly=true;code.value=item.code;label.append(code);card.append(label);const link=document.createElement('a');link.href='/Track';link.textContent='Open citizen tracking';card.append(link);}
    const remove=document.createElement('button');remove.type='button';remove.className='button secondary';remove.textContent='Remove from this device';remove.disabled=queue.running;remove.onclick=async()=>{if(confirm('Remove this local copy? Unsent complaints and media will be lost. Registered cases will not be deleted.')){await store.remove(item.submissionKey);transcriptEdits.delete(item.submissionKey);await render();}};card.append(remove);items.append(card);
   }}
   async function sync(){if(queue.running)return;byId('sync-offline').disabled=true;try{const pending=queue.sync();await render();await pending;tell('Sync checked. Review voice transcripts below; only a receipt confirms registration.');}catch(error){tell(error.message||'Connection unavailable. Complaints remain saved.');}finally{byId('sync-offline').disabled=false;await render();}}
   form.addEventListener('submit',async event=>{event.preventDefault();if(saving||recordingPending||!form.reportValidity())return;saving=true;byId('save-offline').disabled=true;try{const photo=byId('photo').files[0];await queue.add({original:form.elements.original.value,district:form.elements.district.value,location:form.elements.location.value,language:form.elements.language.value,consent:byId('consent').checked,sample:byId('sample').checked,photo,photoName:photo?.name,photoConsent:byId('photo-consent').checked,audio:audioBlob,audioName,audioConsent:byId('audio-consent').checked},crypto.randomUUID());form.reset();setAudio(null,'');tell('Saved on this device. Waiting for transcription review or a confirmed receipt.');await render();await sync();}catch(error){tell(error.message);}finally{saving=false;byId('save-offline').disabled=recordingPending;}});
   byId('sync-offline').onclick=sync;window.addEventListener('online',sync);window.addEventListener('offline',()=>tell('Offline. Saved complaints and media will sync when Praja is reachable.'));
   await render();
   if('serviceWorker' in navigator){try{await navigator.serviceWorker.register('/offline-worker.js');await navigator.serviceWorker.ready;tell('Offline page ready on this device.');}catch{tell('Offline reload could not be enabled. Keep this page open; queued data is still saved.');}}
   else tell('Offline reload is unsupported in this browser. Keep this page open.');
   if((await store.list()).some(i=>i.state!=='sent'&&i.state!=='needs-transcript-review'))await sync();
   setInterval(async()=>{if((await store.list()).some(i=>i.state!=='sent'&&i.state!=='needs-transcript-review'))await sync();},60000);
  }catch(error){tell(error.message);byId('save-offline').disabled=true;}
 })();
})(globalThis);
