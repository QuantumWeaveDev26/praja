import {test} from 'node:test';
import assert from 'node:assert/strict';
import vm from 'node:vm';
import fs from 'node:fs';
function page(voices=[{lang:'en-IN'}]) {
 const elements={}; for(const id of ['citizen-help','citizen-help-status','Language','listen-guide','listen-complaint','stop-reading','Input_OriginalText'])elements[id]={value:'',textContent:'',listeners:{},addEventListener(e,cb){this.listeners[e]=cb;}};
 elements.Language.value='English'; let spoken,stopped=0;
 const context={document:{getElementById:id=>elements[id]},window:{speechSynthesis:{getVoices:()=>voices,cancel(){stopped++;},speak(value){spoken=value;}},SpeechSynthesisUtterance:class{constructor(text){this.text=text;}},addEventListener(){}}};
 vm.runInNewContext(fs.readFileSync('src/Praja.Web/wwwroot/js/citizen-help.js','utf8'),context);
 return {elements,click:id=>elements[id].listeners.click(),get spoken(){return spoken;},get stopped(){return stopped;}};
}
test('read-aloud requires a click and reads the editable complaint without changing it',()=>{
 const p=page();assert.equal(p.spoken,undefined);p.elements.Input_OriginalText.value='No water supply in Ward 12.';p.click('listen-complaint');assert.equal(p.spoken.text,'No water supply in Ward 12.');assert.equal(p.spoken.lang,'en-IN');p.click('stop-reading');assert.equal(p.stopped,2);
});
test('Kannada guide uses Kannada voice; missing voice gives a clear fallback',()=>{
 const p=page([{lang:'kn-IN'}]);p.elements.Language.value='Kannada';p.click('listen-guide');assert.equal(p.spoken.lang,'kn-IN');assert.match(p.spoken.text,/ಸಮಸ್ಯೆ/);
 const missing=page([]);missing.click('listen-guide');assert.equal(missing.spoken,undefined);assert.match(missing.elements['citizen-help-status'].textContent,/No voice/);
});
