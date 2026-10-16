import assert from 'node:assert/strict';
import {test} from 'node:test';
import vm from 'node:vm';
import fs from 'node:fs';

function page(initial = 'Text') {
  const listeners = {};
  const options = ['Text','Voice','Document'].map(value => ({value,checked:value===initial,addEventListener:(type,cb)=>listeners[value]=cb}));
  const panels = ['Voice','Document'].map(value=>({dataset:{capturePanel:value},hidden:false}));
  const voice = {dataset:{recording:'false',processing:'false'}};
  const audio = {files:[]}; const petition = {files:[],value:'petition.pdf'};
  const status = {hidden:true,textContent:''}; const clear = {addEventListener:(type,cb)=>listeners.clear=cb};
  const document = {querySelectorAll:selector=>selector==='[name="CaptureMethod"]'?options:selector==='[data-capture-panel]'?panels:[],getElementById:id=>({'voice-intake':voice,Audio:audio,Document:petition,'capture-status':status,'document-clear':clear}[id])};
  vm.runInNewContext(fs.readFileSync('src/Praja.Web/wwwroot/js/capture-method.js','utf8'),{document});
  return {panels,voice,audio,petition,status,select(value){options.forEach(o=>o.checked=o.value===value);listeners[value]();return options.find(o=>o.checked).value;}};
}
test('input chooser shows only selected media and restores server selection',()=>{
  const p=page('Document'); assert.equal(p.panels[0].hidden,true); assert.equal(p.panels[1].hidden,false);
  p.select('Text');assert.ok(p.panels.every(panel=>panel.hidden));
  p.select('Voice');assert.equal(p.panels[0].hidden,false);assert.equal(p.panels[1].hidden,true);
});
test('pending recording cannot be hidden or discarded by changing input method',()=>{
  const p=page('Voice');p.audio.files=[{name:'complaint.webm'}];
  assert.equal(p.select('Text'),'Voice');assert.equal(p.panels[0].hidden,false);assert.equal(p.audio.files.length,1);assert.match(p.status.textContent,/Transcribe|clear/);
});
test('recording and transcription both block changing input method',()=>{
  for(const key of ['recording','processing']) {const p=page('Voice');p.voice.dataset[key]='true';assert.equal(p.select('Document'),'Voice');}
});
test('selected document remains visible when switching would hide it',()=>{
  const p=page('Document');p.petition.files=[{name:'petition.pdf'}];assert.equal(p.select('Text'),'Document');assert.equal(p.panels[1].hidden,false);
});
