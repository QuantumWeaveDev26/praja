import {test} from 'node:test';
import assert from 'node:assert/strict';
import vm from 'node:vm';
import fs from 'node:fs';

function page({denied=false,chunkSize=20}={}){
 const elements={};const handlers={};let recorder,tracksStopped=0,timer,timerMs,permissionCalls=0;
 class Element{disabled=false;hidden=true;value='';files=[];textContent='';removeAttribute(){} }
 for(const id of ['offline-status','offline-form','record-voice','stop-voice','save-offline','audio','clear-voice','audio-preview','recording-status'])elements[id]=new Element();
 class Recorder{
  static isTypeSupported(){return true;}
  constructor(){recorder=this;this.mimeType='audio/webm';this.state='inactive';}
  start(){this.state='recording';}
  stop(){if(this.state==='inactive')return;this.state='inactive';this.ondataavailable({data:new Blob([new Uint8Array(chunkSize)])});this.onstop();}
 }
 const context={URLSearchParams,FormData,Blob,document:{getElementById:id=>elements[id]},indexedDB:{open:()=>({})},navigator:{mediaDevices:{getUserMedia:async()=>{permissionCalls++;if(denied)throw {name:'NotAllowedError'};return {getTracks:()=>[{stop:()=>tracksStopped++}]};}}},MediaRecorder:Recorder,URL:{createObjectURL:()=> 'blob:local',revokeObjectURL(){}},window:{addEventListener:(event,callback)=>handlers[event]=callback},setTimeout:(callback,ms)=>{timer=callback;timerMs=ms;return 1;},clearTimeout(){}};
 vm.runInNewContext(fs.readFileSync('src/Praja.Web/wwwroot/js/offline.js','utf8'),context);
 return {elements,handlers,start:()=>elements['record-voice'].onclick(),stop:()=>elements['stop-voice'].onclick(),expire:()=>timer(),error:()=>recorder.onerror(),get tracksStopped(){return tracksStopped;},get timerMs(){return timerMs;},get permissionCalls(){return permissionCalls;}};
}

test('offline recorder stops with local playback and releases microphone without network',async()=>{
 const p=page();await p.start();assert.equal(p.elements['save-offline'].disabled,true);assert.equal(p.elements.audio.disabled,true);await p.start();assert.equal(p.permissionCalls,1);p.stop();assert.equal(p.tracksStopped,1);assert.equal(p.elements['audio-preview'].hidden,false);assert.equal(p.elements['save-offline'].disabled,false);assert.equal(p.elements['stop-voice'].disabled,true);
});

test('offline microphone permission denial restores form and gives actionable message',async()=>{
 const p=page({denied:true});await p.start();assert.match(p.elements['recording-status'].textContent,/permission denied/i);assert.equal(p.elements['record-voice'].disabled,false);assert.equal(p.elements['save-offline'].disabled,false);assert.equal(p.elements.audio.disabled,false);assert.equal(p.elements['audio-preview'].hidden,true);
});

test('offline recording stops automatically at two minutes and page exit releases microphone',async()=>{
 const p=page();await p.start();assert.equal(p.timerMs,120000);p.expire();assert.equal(p.tracksStopped,1);assert.equal(p.elements['audio-preview'].hidden,false);
 const leaving=page();await leaving.start();leaving.handlers.pagehide();assert.equal(leaving.tracksStopped,1);
});

test('recording error and oversized recording preserve previous chosen audio',async()=>{
 for(const oversized of [false,true]){
  const p=page({chunkSize:oversized?8*1024*1024+1:20});p.elements.audio.files=[new Blob(['prior audio'],{type:'audio/webm'})];p.elements.audio.onchange();await p.start();if(oversized)p.stop();else p.error();
  assert.equal(p.tracksStopped,1);assert.equal(p.elements['audio-preview'].hidden,false);assert.match(p.elements['recording-status'].textContent,oversized?/exceeded 8 MiB/:/Recording failed/);assert.equal(p.elements['record-voice'].disabled,false);assert.equal(p.elements['save-offline'].disabled,false);
 }
});
