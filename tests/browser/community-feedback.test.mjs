import {test} from 'node:test';
import assert from 'node:assert/strict';
import vm from 'node:vm';
import fs from 'node:fs';
const source=fs.readFileSync('src/Praja.Web/wwwroot/js/community-feedback.js','utf8');
const context={};vm.runInNewContext(source,context);const api=context.PrajaCommunityFeedback;
const position=(time,accuracy=20)=>({coords:{latitude:12.97,longitude:77.59,accuracy},timestamp:time});
test('community location is requested explicitly with fresh high accuracy settings',()=>{
 let calls=0,options,location;const controller=api.locationController({getCurrentPosition(success,error,o){calls++;options=o;success(position(1000));}},v=>location=v,()=>{},()=>1000);
 assert.equal(calls,0);assert.equal(controller.fresh(),false);controller.request();assert.equal(calls,1);assert.equal(options.maximumAge,0);assert.equal(options.enableHighAccuracy,true);assert.equal(location.lat,12.97);assert.equal(controller.fresh(),true);
});
test('location expires and clears data after five minutes',()=>{
 let now=1000,location,status;const controller=api.locationController({getCurrentPosition(success){success(position(now));}},v=>location=v,v=>status=v,()=>now);
 controller.request();now+=300000;assert.equal(controller.fresh(),false);assert.equal(location,null);assert.match(status,/expired/);
});
test('inaccurate or malformed location cannot enable feedback',()=>{
 for(const p of [position(1000,201),position(1000,0),position(1000,-1),position(1000,NaN),{coords:{latitude:91,longitude:77,accuracy:10},timestamp:1000},position(-400000),position(20000)]) {
  let location;const controller=api.locationController({getCurrentPosition(success){success(p);}},v=>location=v,()=>{},()=>1000);controller.request();assert.equal(location,null);assert.equal(controller.fresh(),false);
 }
 assert.equal(api.validCoordinates('12',77,10),false);
});
test('permission failure and repeated request clear previously captured coordinates',()=>{
 let fail=false,location,status;const controller=api.locationController({getCurrentPosition(success,error){if(fail)error({code:1});else success(position(1000));}},v=>location=v,v=>status=v,()=>1000);
 controller.request();assert.ok(location);fail=true;controller.request();assert.equal(location,null);assert.equal(controller.fresh(),false);assert.match(status,/denied/);
});
test('older pending callbacks cannot replace a newer request',()=>{
 const pending=[];let location;const controller=api.locationController({getCurrentPosition(success,error){pending.push({success,error});}},v=>location=v,()=>{},()=>1000);
 controller.request();controller.request();pending[0].success(position(1000));assert.equal(location,null);pending[1].success(position(1000));assert.ok(location);pending[0].error();assert.ok(location);
});
test('unsupported or throwing geolocation fails with no retained coordinates',()=>{
 for(const geo of [null,{getCurrentPosition(){throw Error('blocked');}}]) {let location,status;const controller=api.locationController(geo,v=>location=v,v=>status=v);controller.request();assert.equal(location,null);assert.equal(controller.fresh(),false);assert.match(status,/unavailable|failed/);}
});
test('community page uses form submission only, with no GPS storage or external request',()=>{
 assert.doesNotMatch(source,/localStorage|sessionStorage|fetch\(|sendBeacon|innerHTML/);
 const page=fs.readFileSync('src/Praja.Web/Pages/Community.cshtml','utf8');assert.match(page,/method="post"/);assert.match(page,/id="community-submit"[^>]+disabled/);assert.match(page,/Unverified community reports/);
 const map=fs.readFileSync('src/Praja.Web/wwwroot/js/public-map.js','utf8');assert.match(map,/\['CitizenConfirmation','Closed'\]\.includes\(x.stage\)/);assert.match(map,/encodeURIComponent\(x.reference\)/);
});
