const {test}=require('node:test');
const assert=require('node:assert/strict');
const {appHeight}=require('../web/viewport.js');
test('iPhone 14 installed app fills the 47px area omitted by WebKit',()=>{
  assert.equal(appHeight({standalone:true,innerHeight:797,visualHeight:797,screenWidth:390,screenHeight:844}),844);
});
test('regular Safari respects the usable viewport below browser chrome',()=>{
  assert.equal(appHeight({standalone:false,innerHeight:724,visualHeight:724,screenWidth:390,screenHeight:844}),724);
});
test('standalone app in landscape uses the short screen dimension as height',()=>{
  assert.equal(appHeight({standalone:true,landscape:true,innerHeight:369,screenWidth:390,screenHeight:844}),390);
});
test('desktop window does not grow to monitor height',()=>{
  assert.equal(appHeight({standalone:false,innerHeight:700,screenWidth:1920,screenHeight:1080}),700);
});
test('small windows do not stretch to an unrelated physical display',()=>{
  assert.equal(appHeight({standalone:true,innerHeight:500,screenWidth:1920,screenHeight:1080}),500);
});
test('invalid dimensions leave the CSS fallback intact',()=>{
  assert.equal(appHeight({}),0);assert.equal(appHeight({innerHeight:NaN,visualHeight:700}),700);
});
