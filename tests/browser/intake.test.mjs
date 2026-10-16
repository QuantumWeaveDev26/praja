import assert from 'node:assert/strict';
import {test} from 'node:test';
import vm from 'node:vm';
import fs from 'node:fs';

function page() {
  const state={textContent:''};
  const consent={checked:false};
  const audio={name:'Audio',type:'file',files:[{name:'recording.webm'}],disabled:false};
  const documentFile={name:'Document',type:'file',files:[],disabled:false};
  const listeners=[]; const keys=[]; const submitted=[]; const prepare={id:'prepare-draft'};
  const form={id:'intake-form',elements:[audio,documentFile],checkValidity:()=>true,getAttribute:()=>'/Grievances/New',requestSubmit:button=>submitted.push(button),addEventListener:(type,callback)=>(type==='submit'?listeners:keys).push(callback)};
  const panel={dataset:{recording:'false'}};
  const document={querySelectorAll:selector=>selector==='form[data-busy]'?[form]:[],getElementById:id=>({'media-intake-status':state,'Input_Consent':consent,'Audio':audio,'Document':documentFile,'voice-intake':panel,'prepare-draft':prepare}[id])};
  const context={document,window:{location:{href:'http://localhost/Grievances/New'}},URL,HTMLInputElement:Object};
  vm.runInNewContext(fs.readFileSync('src/Praja.Web/wwwroot/js/site.js','utf8'),context);
  function submit(handler) {
    const button={disabled:false,textContent:'Submit',formNoValidate:true,getAttribute:()=>handler?'/Grievances/New?handler='+handler:null};
    const event={submitter:button,defaultPrevented:false,preventDefault(){this.defaultPrevented=true;}};
    listeners.forEach(callback=>callback(event)); return event;
  }
  return {state,consent,audio,documentFile,panel,submit,keys,submitted,prepare};
}

test('empty document submission preserves pending recording',()=>{
  const p=page();p.consent.checked=true;
  assert.equal(p.submit('Document').defaultPrevented,true);
  assert.match(p.state.textContent,/audio|recording/i);
  assert.equal(p.audio.disabled,false);
});
test('recording in progress cannot navigate away through intake submit',()=>{
  const p=page();p.panel.dataset.recording='true';
  assert.equal(p.submit('Analyze').defaultPrevented,true);
  assert.match(p.state.textContent,/stop/i);
});
test('audio submission is reserved for the asynchronous voice handler',()=>{
  const p=page();p.consent.checked=true;
  const event=p.submit('Audio');
  assert.equal(event.submitter.disabled,false);
  assert.equal(p.audio.disabled,false);
});
test('pending document cannot be silently skipped by preparing a draft',()=>{
  const p=page();p.audio.files=[];p.documentFile.files=[{name:'petition.pdf'}];
  assert.equal(p.submit(null).defaultPrevented,true);assert.equal(p.documentFile.disabled,false);assert.match(p.state.textContent,/Extract|clear/i);
});
test('a draft cannot be submitted while transcription is pending',()=>{
  const p=page();p.audio.files=[];p.panel.dataset.processing='true';
  assert.equal(p.submit(null).defaultPrevented,true);assert.match(p.state.textContent,/Wait|transcription/i);
});
test('Enter in location inputs prepares a draft rather than submitting a hidden media action',()=>{
  const p=page();
  for(const id of ['Input_District','Input_Location','Input_FollowUpTimeline']) {const event={key:'Enter',isComposing:false,target:{id},preventDefault(){this.prevented=true;}};p.keys.forEach(cb=>cb(event));assert.equal(event.prevented,true);assert.equal(p.submitted.at(-1),p.prepare);}
  const before=p.submitted.length;
  for(const event of [{key:'Enter',target:{id:'Input_OriginalText'}},{key:'Enter',isComposing:true,target:{id:'Input_Location'}}])p.keys.forEach(cb=>cb(event));
  assert.equal(p.submitted.length,before);
});
test('held or repeated Enter cannot resubmit a pending draft',()=>{
  const p=page();p.prepare.disabled=true;
  const event={key:'Enter',target:{id:'Input_Location'},preventDefault(){this.prevented=true;}};
  p.keys.forEach(cb=>cb(event));assert.equal(event.prevented,true);assert.equal(p.submitted.length,0);
  p.prepare.disabled=false;event.repeat=true;p.keys.forEach(cb=>cb(event));assert.equal(p.submitted.length,0);
});
