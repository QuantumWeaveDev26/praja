import assert from 'node:assert/strict';
import {test} from 'node:test';
import vm from 'node:vm';
import fs from 'node:fs';

function page({permissionDenied=false,providerError=false,hold=false,citizen=false}={}) {
  const elements={};
  class Element {
    listeners={}; files=[]; disabled=false; hidden=true; textContent=''; dataset={}; checked=false; readOnly=false; _value='';
    addEventListener(type,callback){this.listeners[type]=callback;}
    dispatchEvent(){this.changed=true;}
    removeAttribute(){}
    getAttribute(name){return name==='action'?'/Grievances/New':null;}
    set value(value){this._value=value;if(value==='')this.files=[];}
    get value(){return this._value;}
  }
  for(const id of ['voice-intake','record-start','record-stop','record-clear','transcribe-audio','record-state','media-intake-status','record-preview','Audio','intake-form','Input_Consent','Input_OriginalText','Input_Source','AnalysisToken'])elements[id]=new Element();
  elements['voice-intake'].dataset={speechConfigured:'true',citizen:citizen?'true':'false'};
  const deletedFields=[]; let stoppedTracks=0,calls=0,timer,finish; const pending=new Promise(resolve=>{finish=resolve;});
  class Recorder {
    static isTypeSupported(){return true;}
    constructor(){this.mimeType='audio/webm';}
    start(){this.state='recording';}
    stop(){this.ondataavailable({data:{size:12}});this.state='inactive';this.onstop();}
  }
  class Transfer { files=[];items={add:file=>this.files.push(file)}; }
  const url=class extends URL { static createObjectURL(){return 'blob:test';}static revokeObjectURL(){} };
  const context={document:{getElementById:id=>elements[id],querySelector:()=>null},window:{MediaRecorder:Recorder,location:{href:'http://localhost/Grievances/New'},addEventListener(){}},navigator:{mediaDevices:{getUserMedia:async()=>{if(permissionDenied)throw {name:'NotAllowedError'};return {getTracks:()=>[{stop:()=>stoppedTracks++}]};}}},MediaRecorder:Recorder,URL:url,DataTransfer:Transfer,File:class {constructor(chunks,name){this.name=name;}},Blob:class {constructor(chunks,options){this.size=chunks.reduce((n,c)=>n+c.size,0);this.type=options.type;}},FormData:class {delete(field){deletedFields.push(field);}},Event:class {},setTimeout:callback=>{timer=callback;return 1;},clearTimeout(){},fetch:async()=>{calls++;if(hold)await pending;return {ok:!providerError,redirected:false,headers:{get:()=> 'application/json'},json:async()=>providerError?{error:'Provider unavailable'}:{text:'Water supply has stopped in Ward 12 for three days.',method:'Provider voice transcription'}};}};
  vm.runInNewContext(fs.readFileSync('src/Praja.Web/wwwroot/js/voice.js','utf8'),context);
  const submit=()=>elements['intake-form'].listeners.submit({submitter:{getAttribute:()=>'/Grievances/New?handler=Audio'},defaultPrevented:false,preventDefault(){}});
  return {elements,deletedFields,click:id=>elements[id].listeners.click(),submit,get calls(){return calls;},get stoppedTracks(){return stoppedTracks;},expire:()=>timer(),finish};
}

test('stop recording attaches WebM and releases microphone',async()=>{
  const p=page();await p.click('record-start');assert.equal(p.elements['transcribe-audio'].disabled,true);
  p.click('record-stop');assert.equal(p.elements.Audio.files[0].name,'citizen-recording.webm');assert.equal(p.stoppedTracks,1);assert.equal(p.elements['transcribe-audio'].disabled,false);
});
test('permission rejection restores controls and shows next action',async()=>{
  const p=page({permissionDenied:true});await p.click('record-start');
  assert.equal(p.elements['record-start'].disabled,false);assert.match(p.elements['media-intake-status'].textContent,/permission/i);
});
test('missing consent preserves audio without a provider request',async()=>{
  const p=page();p.elements.Audio.files=[{name:'test.webm'}];await p.submit();
  assert.equal(p.calls,0);assert.equal(p.elements.Audio.files.length,1);assert.match(p.elements['media-intake-status'].textContent,/consent/i);
});
test('provider failure preserves recording for retry',async()=>{
  const p=page({providerError:true});p.elements.Audio.files=[{name:'test.webm'}];p.elements.Input_Consent.checked=true;await p.submit();
  assert.equal(p.calls,1);assert.equal(p.elements.Audio.files.length,1);assert.equal(p.elements.Audio.disabled,false);assert.match(p.elements['media-intake-status'].textContent,/unavailable/i);
});
test('transcription fills editable text and selects voice channel',async()=>{
  const p=page();p.elements.Audio.files=[{name:'test.webm'}];p.elements.Input_Consent.checked=true;await p.submit();
  assert.equal(p.elements.Input_OriginalText.value,'Water supply has stopped in Ward 12 for three days.');assert.equal(p.elements.Input_Source.value,'Voice transcript');assert.equal(p.elements.Audio.files.length,0);assert.equal(p.elements.Input_OriginalText.changed,true);
});
test('two-minute cutoff stops and retains recording',async()=>{
  const p=page();await p.click('record-start');p.expire();assert.equal(p.elements.Audio.files.length,1);assert.equal(p.stoppedTracks,1);
});

test('pending transcription locks complaint editing and restores it afterwards',async()=>{
  const p=page({hold:true});p.elements.Audio.files=[{name:'test.webm'}];p.elements.Input_Consent.checked=true;
  const request=p.submit();assert.equal(p.elements.Input_OriginalText.readOnly,true);
  p.finish();await request;assert.equal(p.elements.Input_OriginalText.readOnly,false);
});

test('citizen voice guides submission without officer-only draft actions',async()=>{
 const p=page({citizen:true});p.elements.Audio.files=[{name:'test.webm'}];p.elements.Input_Consent.checked=true;await p.submit();
 assert.match(p.elements['record-state'].textContent,/submit your complaint/);assert.doesNotMatch(p.elements['media-intake-status'].textContent,/Suggest details|Prepare draft/);
});


test('voice transcription excludes citizen photos and coordinates from its request',async()=>{
 const p=page({citizen:true});p.elements.Audio.files=[{name:'test.webm'}];p.elements.Input_Consent.checked=true;await p.submit();
 for(const field of ['Photo','PhotoConsent','Latitude','Longitude','Accuracy'])assert.ok(p.deletedFields.includes(field));
});
