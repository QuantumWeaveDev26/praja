import {test} from 'node:test';
import assert from 'node:assert/strict';
import vm from 'node:vm';
import fs from 'node:fs';
function page() {
    let click, success, failure, requests=0;
    const button={disabled:false,addEventListener:(_,cb)=>click=cb};
    const status={textContent:''}, latitude={value:'12'},longitude={value:'76'};
    const navigator={geolocation:{getCurrentPosition:(ok,bad)=>{requests++;success=ok;failure=bad;}}};
    const document={querySelector:s=>s==='[data-capture-location]'?button:status,getElementById:id=>id==='Latitude'?latitude:longitude};
    vm.runInNewContext(fs.readFileSync('src/Praja.Web/wwwroot/js/location-capture.js','utf8'),{document,navigator});
    return {button,status,latitude,longitude,click:()=>click(),success:value=>success(value),failure:()=>failure(),requests:()=>requests};
}
test('GPS requests permission only on click and fills device location without claiming photo provenance',()=>{
    const p=page();assert.equal(p.requests(),0);p.click();assert.equal(p.requests(),1);assert.equal(p.button.disabled,true);
    p.success({coords:{latitude:12.31,longitude:76.62,accuracy:15}});assert.equal(p.latitude.value,'12.310000');assert.equal(p.longitude.value,'76.620000');assert.match(p.status.textContent,/does not establish/);assert.equal(p.button.disabled,false);
});
test('GPS denial and invalid fixes preserve existing coordinates',()=>{
    const p=page();p.click();p.failure();assert.equal(p.latitude.value,'12');assert.equal(p.button.disabled,false);
    p.click();p.success({coords:{latitude:999,longitude:NaN,accuracy:0}});assert.equal(p.longitude.value,'76');assert.match(p.status.textContent,/invalid/);
});
