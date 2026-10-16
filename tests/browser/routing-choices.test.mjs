import assert from 'node:assert/strict';import {test} from 'node:test';import vm from 'node:vm';import fs from 'node:fs';
function page(){
  const handlers={};const option=(value,data={})=>({value,dataset:data,textContent:value,cloneNode(){return option(this.value,this.dataset);}});
  const select=(id,value,options=[])=>({value,options,replaceChildren(...next){this.options=next;},addEventListener:(type,cb)=>handlers[id]=cb});
  const department=select('department','water');const category=select('category','water_supply',[option(''),option('water_supply',{department:'water',label:'Supply'}),option('healthcare_access',{department:'health',label:'Access'})]);
  const issue=select('issue','no_supply',[option(''),option('no_supply',{category:'water_supply',label:'No supply'}),option('medicine_shortage',{category:'healthcare_access',label:'Medicines'})]);
  const reviewed={checked:true};const state={textContent:''};
  const document={querySelector:()=>({}),getElementById:id=>({Department:department,CategoryCode:category,SubcategoryCode:issue,Reviewed:reviewed,'routing-choice-state':state}[id])};
  vm.runInNewContext(fs.readFileSync('src/Praja.Web/wwwroot/js/routing-choices.js','utf8'),{document});return{department,category,issue,reviewed,state,handlers};
}
test('saved category and issue remain selected while unrelated options are filtered',()=>{const p=page();assert.equal(p.category.value,'water_supply');assert.equal(p.issue.value,'no_supply');assert.deepEqual(p.category.options.map(o=>o.value),['','water_supply']);});
test('changing service clears category, issue and previous review confirmation',()=>{const p=page();p.department.value='health';p.handlers.department();assert.equal(p.category.value,'');assert.equal(p.issue.value,'');assert.equal(p.reviewed.checked,false);assert.deepEqual(p.category.options.map(o=>o.value),['','healthcare_access']);});
test('changing category filters issues and requires fresh review',()=>{const p=page();p.department.value='health';p.handlers.department();p.reviewed.checked=true;p.category.value='healthcare_access';p.handlers.category();assert.equal(p.issue.value,'');assert.deepEqual(p.issue.options.map(o=>o.value),['','medicine_shortage']);assert.equal(p.reviewed.checked,false);});
