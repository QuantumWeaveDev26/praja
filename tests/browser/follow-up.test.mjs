import assert from 'node:assert/strict';
import {test} from 'node:test';
import vm from 'node:vm';
import fs from 'node:fs';
function page() {
  const handlers={};
  const fields=['timeline','help'].map(id=>({id,value:'',addEventListener:(type,cb)=>handlers[id]=cb}));
  const original={addEventListener:(type,cb)=>handlers.original=cb};
  const confirmation={checked:false,required:false,addEventListener:(type,cb)=>handlers.invalid=cb};const state={textContent:''};
  const panel={querySelectorAll:()=>fields,open:false};
  const document={querySelector:()=>panel,getElementById:id=>({'Input_FollowUpConfirmed':confirmation,'follow-up-state':state,'Input_OriginalText':original}[id])};
  vm.runInNewContext(fs.readFileSync('src/Praja.Web/wwwroot/js/follow-up.js','utf8'),{document});
  return {handlers,fields,confirmation,state,panel};
}
test('known follow-up answers require confirmation, empty answers do not',()=>{
  const p=page();assert.equal(p.confirmation.required,false);p.fields[0].value='For three days';p.handlers.timeline();assert.equal(p.confirmation.required,true);
  p.fields[0].value='   ';p.handlers.timeline();assert.equal(p.confirmation.required,false);
});
test('editing an answer or replacing the complaint clears old confirmation without losing answers',()=>{
  const p=page();p.fields[1].value='Restore supply';p.confirmation.checked=true;p.handlers.help();assert.equal(p.confirmation.checked,false);
  p.confirmation.checked=true;p.handlers.original();assert.equal(p.confirmation.checked,false);assert.equal(p.fields[1].value,'Restore supply');assert.match(p.state.textContent,/Confirm/);
});
test('missing confirmation reopens a collapsed follow-up panel for correction',()=>{
  const p=page();p.fields[0].value='Since Monday';p.handlers.timeline();
  p.handlers.invalid?.();assert.equal(p.panel.open,true);
});
