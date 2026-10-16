import {test} from 'node:test';
import assert from 'node:assert/strict';
import vm from 'node:vm';
import fs from 'node:fs';
const source = fs.readFileSync('src/Praja.Web/wwwroot/js/citizen-location.js', 'utf8');
function setup(available = true) {
    const nodes = Object.fromEntries(['capture','clear','status','coordinates','accuracy'].map(name => ['citizen-location-' + name, node()]));
    nodes['citizen-latitude'] = node(); nodes['citizen-longitude'] = node();
    const requests = [];
    const navigator = available ? {geolocation:{getCurrentPosition:(success,error,options)=>requests.push({success,error,options})}} : {};
    vm.runInNewContext(source,{document:{getElementById:id=>nodes[id]},navigator});
    return {nodes,requests,click:name=>nodes['citizen-location-'+name].handlers.click(),latitude:nodes['citizen-latitude'],longitude:nodes['citizen-longitude'],accuracy:nodes['citizen-location-accuracy']};
}
function node(){return {value:'',disabled:false,handlers:{},addEventListener(event,handler){this.handlers[event]=handler;}};}
const fix = {coords:{latitude:12.29581,longitude:76.63938,accuracy:24.2}};
test('location is never requested on load; explicit capture fills coordinates and accuracy',()=>{
    const app=setup();assert.equal(app.requests.length,0);app.click('capture');assert.equal(app.requests.length,1);
    assert.equal(app.requests[0].options.timeout,15000);assert.equal(app.requests[0].options.maximumAge,0);
    app.requests[0].success(fix);assert.equal(app.latitude.value,'12.295810');assert.equal(app.longitude.value,'76.639380');assert.equal(app.accuracy.value,'24.2');
    assert.equal(app.nodes['citizen-location-coordinates'].open,true);assert.equal(app.nodes['citizen-location-capture'].disabled,false);
});
test('denial and timeout retain existing location without blocking the form',()=>{
    for(const code of [1,2,3]){const app=setup();app.latitude.value='12';app.longitude.value='76';app.click('capture');app.requests[0].error({code});assert.equal(app.latitude.value,'12');assert.equal(app.longitude.value,'76');assert.equal(app.nodes['citizen-location-capture'].disabled,false);assert.match(app.nodes['citizen-location-status'].textContent,/Existing coordinates were kept/);}
});
test('clear invalidates pending fix and manual edits invalidate reported accuracy',()=>{
    const app=setup();app.click('capture');app.click('clear');app.requests[0].success(fix);assert.equal(app.latitude.value,'');assert.equal(app.accuracy.value,'');
    app.click('capture');app.requests[1].success(fix);app.latitude.value='13';app.latitude.handlers.input();assert.equal(app.accuracy.value,'');
    app.click('capture');app.longitude.value='77';app.longitude.handlers.input();app.requests[2].success(fix);assert.equal(app.longitude.value,'77');assert.equal(app.latitude.value,'13');
});
test('unsupported browser and invalid device position leave text submission available',()=>{
    const missing=setup(false);missing.click('capture');assert.match(missing.nodes['citizen-location-status'].textContent,/unavailable/);assert.equal(missing.nodes['citizen-location-capture'].disabled,false);
    for(const coords of [{latitude:91,longitude:76,accuracy:5},{latitude:12,longitude:181,accuracy:5},{latitude:12,longitude:76,accuracy:NaN}]){const app=setup();app.click('capture');app.requests[0].success({coords});assert.equal(app.latitude.value,'');assert.match(app.nodes['citizen-location-status'].textContent,/invalid/);}
});
test('script safely ignores non-citizen pages',()=>{
    vm.runInNewContext(source,{document:{getElementById:()=>null}});
});
