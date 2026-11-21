const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const { GestureTracker, MotionFilter } = require('../web/gestures.js');
const RemoteViewport = require('../web/viewport.js');
const RemoteKeyboard = require('../web/keyboard.js');
const RemoteScreen = require('../web/screen.js');
function setup(options = {}) {
  const nodes = new Map(), listeners = {}, intervals = [], timeouts = [], sockets = [];
  const storage = options.storage || new Map();
  function element(id) {
    if (nodes.has(id)) return nodes.get(id);
    const events = {};
    const node = { value:'',disabled:false,hidden:false,style:{},dataset:{},classList:{toggle(){}},textContent:'',open:false,
      removeAttribute(name){delete this[name]},addEventListener(type,fn){(events[type]??=[]).push(fn)},
      fire(type,data={}){for(const fn of events[type]??[]) fn({preventDefault(){},...data})},
      captures:new Set(),showModal(){this.open=true},close(){if(this.open){this.open=false;this.fire('close')}},focus(){this.focused=true},blur(){this.focused=false},setPointerCapture(id){this.captures.add(id)},hasPointerCapture(id){return this.captures.has(id)},releasePointerCapture(id){this.captures.delete(id)},getBoundingClientRect(){return{left:0,top:0,right:400,bottom:500,width:400,height:500}} };
    nodes.set(id,node); return node;
  }
  const buttons = ['down','mute','up'].map(action=>{const node=element(action);node.dataset.volume=action;return node});
  class FakeSocket {
    static OPEN=1;
    constructor(url){this.url=url;this.readyState=0;this.bufferedAmount=0;this.sent=[];sockets.push(this)}
    send(data){this.sent.push(JSON.parse(data))}
    close(){this.readyState=3}
    open(){this.readyState=1;this.onopen?.()}
    message(message){this.onmessage?.({data:JSON.stringify(message)})}
  }
  const cssProperties=new Map();
  const document={hidden:false,documentElement:{dataset:{},style:{setProperty:(name,value)=>cssProperties.set(name,value)}},getElementById:element,querySelectorAll:()=>buttons,addEventListener(type,fn){listeners[type]=fn}};
  element('themeLight').value='light';element('themeDark').value='dark';
  const window={innerHeight:options.innerHeight??844,screen:{width:390,height:844},orientation:0,addEventListener(type,fn){listeners[type]=fn},scrollTo(){}};
  const context=vm.createContext({document,window,RemoteViewport,RemoteKeyboard,RemoteScreen,navigator:{standalone:options.standalone===true,onLine:true},location:{protocol:options.protocol||'http:',host:'192.168.1.20:49732'},WebSocket:FakeSocket,
    RemoteGestures:{GestureTracker,MotionFilter},localStorage:{getItem:key=>storage.get(key)??null,setItem(key,value){if(options.failStorage)throw new Error('Storage unavailable');storage.set(key,value)},removeItem:key=>storage.delete(key)},requestAnimationFrame(fn){context.frame=fn},
    setInterval(fn){intervals.push(fn);return intervals.length},clearInterval(){},
    setTimeout(fn,delay){timeouts.push({fn,delay,cancelled:false});return timeouts.length},clearTimeout(id){if(timeouts[id-1])timeouts[id-1].cancelled=true},console,Date});
  vm.runInContext(fs.readFileSync(require.resolve('../web/app.js'),'utf8'),context);
  function begin(){element('code').value='A1B2C3D4E5F6';element('pairingForm').fire('submit');const socket=sockets.at(-1);socket.open();return socket}
  function pair(){const socket=begin();socket.message({type:'ready',version:1,name:'Laptop',pinch:true,keyboard:true,drag:true});return socket}
  function runNextTimeout(){const next=timeouts.find(t=>!t.cancelled);assert.ok(next,'Expected pending timer');next.cancelled=true;next.fn();return next.delay}
  return{element,context,sockets,listeners,document,window,cssProperties,intervals,timeouts,storage,begin,pair,runNextTimeout};
}
test('invalid pairing code cannot open a network connection',()=>{
  const s=setup();s.element('code').value='123';s.element('pairingForm').fire('submit');assert.equal(s.sockets.length,0);
});
test('theme switches locally without interrupting pairing and is restored on reopen',()=>{
  const s=setup(),socket=s.pair();
  assert.equal(s.document.documentElement.dataset.theme,'dark');
  s.element('openControls').fire('click');
  s.element('themeLight').checked=true;s.element('themeLight').fire('change');
  assert.equal(s.document.documentElement.dataset.theme,'light');
  assert.equal(s.element('themeColor').content,'#e9eefa');
  assert.equal(socket.readyState,1);
  assert.equal(s.storage.get('remote.theme'),'light');
  const reopened=setup({storage:s.storage});
  assert.equal(reopened.element('themeLight').checked,true);
  reopened.element('themeDark').checked=true;reopened.element('themeDark').fire('change');
  assert.equal(reopened.document.documentElement.dataset.theme,'dark');
  assert.equal(reopened.storage.get('remote.theme'),'dark');
});
test('appearance still changes when Safari blocks saving it',()=>{
  const s=setup({failStorage:true});
  s.element('themeLight').checked=true;s.element('themeLight').fire('change');
  assert.equal(s.document.documentElement.dataset.theme,'light');
  assert.equal(s.storage.has('remote.theme'),false);
});
test('handshake uses same laptop host, gates controls and clears code after ready',()=>{
  const s=setup();const socket=s.begin();assert.equal(socket.url,'ws://192.168.1.20:49732/ws');
  assert.deepEqual(socket.sent,[{type:'hello',version:1,code:'A1B2C3D4E5F6'}]);
  assert.equal(s.element('up').disabled,true);socket.message({type:'ready',version:1,name:'My laptop'});
  assert.equal(s.element('code').value,'');assert.equal(s.element('up').disabled,false);assert.equal(s.element('computer').textContent,'My laptop');
});
test('volume controls dispatch distinct commands only after ready',()=>{
  const s=setup();s.element('up').fire('click');const socket=s.pair();
  for(const action of ['down','mute','up'])s.element(action).fire('click');
  assert.deepEqual(socket.sent.slice(1),[{type:'volume',action:'down'},{type:'volume',action:'mute'},{type:'volume',action:'up'}]);
});
test('pointer drag is coalesced and scaled into bounded relative movement',()=>{
  const s=setup();const socket=s.pair(),pad=s.element('touchpad');
  pad.fire('pointerdown',{pointerId:1,clientX:10,clientY:10,timeStamp:0,pointerType:'touch'});
  pad.fire('pointermove',{pointerId:1,clientX:30,clientY:20,timeStamp:20});s.context.frame(40);
  assert.equal(socket.sent.at(-1).type,'move');assert.ok(socket.sent.at(-1).dx>0&&socket.sent.at(-1).dx<30);
  pad.fire('pointerup',{pointerId:1,clientX:30,clientY:20,timeStamp:100});assert.equal(socket.sent.filter(m=>m.type==='click').length,0);
  const total=socket.sent.filter(m=>m.type==='move').reduce((sum,m)=>({dx:sum.dx+m.dx,dy:sum.dy+m.dy}),{dx:0,dy:0});
  assert.deepEqual(total,{dx:30,dy:15});
});
test('backgrounding closes the connection and resets controls',()=>{
  const s=setup(),socket=s.pair();s.document.hidden=true;s.listeners.visibilitychange();
  assert.equal(socket.readyState,3);assert.equal(s.element('up').disabled,true);assert.equal(s.element('disconnect').disabled,true);
});
test('server rejection stays visible and permits retry',()=>{
  const s=setup(),socket=s.begin();socket.message({type:'error',message:'Неверный код'});
  assert.equal(s.element('pairStatus').textContent,'Неверный код');assert.equal(s.element('pairSubmit').disabled,false);assert.equal(socket.readyState,3);
});
test('send congestion disconnects without recursion or further commands',()=>{
  const s=setup(),socket=s.pair();socket.bufferedAmount=20000;s.element('up').fire('click');
  assert.equal(socket.readyState,3);assert.match(s.element('status').textContent,/перегружена/);assert.equal(socket.sent.length,1);
});
test('stale callbacks cannot authenticate a replacement connection',()=>{
  const s=setup(),old=s.begin(),staleCallback=old.onmessage;s.element('disconnect').fire('click');const next=s.begin();
  staleCallback({data:JSON.stringify({type:'ready',version:1,name:'Stale'})});assert.equal(s.element('up').disabled,true);
  next.message({type:'ready',version:1,name:'Current'});assert.equal(s.element('computer').textContent,'Current');
});
function startPinch(s) {
  const pad=s.element('touchpad');
  pad.fire('pointerdown',{pointerId:1,clientX:100,clientY:100,timeStamp:0,pointerType:'touch'});
  pad.fire('pointerdown',{pointerId:2,clientX:200,clientY:100,timeStamp:10,pointerType:'touch'});
  pad.fire('pointermove',{pointerId:1,clientX:80,clientY:100,timeStamp:20});
  pad.fire('pointermove',{pointerId:2,clientX:220,clientY:100,timeStamp:25});
  return pad;
}
test('pinch sends native gesture phases, coalesces absolute span and never sends wheel',()=>{
  const s=setup(),socket=s.pair(),pad=startPinch(s);s.context.frame(40);
  pad.fire('pointerup',{pointerId:1,clientX:80,clientY:100,timeStamp:100});
  assert.deepEqual(socket.sent.filter(m=>m.type==='pinch').slice(0,2),[{type:'pinch',phase:'start'},{type:'pinch',phase:'update',scale:1400}]);
  assert.deepEqual(socket.sent.at(-1),{type:'pinch',phase:'end'});
  assert.ok(socket.sent.every(m=>!['scroll','click','move'].includes(m.type)));
});
test('pointer cancellation ends native pinch and discards pending movement',()=>{
  const s=setup(),socket=s.pair(),pad=startPinch(s);pad.fire('pointercancel');s.context.frame(40);
  assert.deepEqual(socket.sent.at(-1),{type:'pinch',phase:'end'});
  assert.equal(socket.sent.filter(m=>m.phase==='end').length,1);
});
test('old Windows capability does not substitute Ctrl+wheel for pinch',()=>{
  const s=setup(),socket=s.begin();socket.message({type:'ready',version:1,name:'Old Windows',pinch:false});startPinch(s);s.context.frame(40);
  assert.equal(socket.sent.length,1);assert.match(s.element('status').textContent,/не поддерживает/);
});
const rememberedKey='remote.device.v1';
const credential={deviceId:'a'.repeat(32),token:'B'.repeat(64)};
const savedStorage=()=>new Map([[rememberedKey,JSON.stringify(credential)]]);
test('first pairing stores a per-device key without storing the pairing code',()=>{
  const s=setup();s.element('remember').checked=true;s.element('deviceName').value='My iPhone';const socket=s.begin();
  assert.deepEqual(socket.sent[0],{type:'hello',version:1,code:'A1B2C3D4E5F6',remember:true,deviceName:'My iPhone'});
  socket.message({type:'ready',version:1,name:'Laptop',remembered:true,...credential});
  assert.deepEqual(JSON.parse(s.storage.get(rememberedKey)),credential);assert.equal(s.element('code').value,'');
  assert.ok(!s.storage.get(rememberedKey).includes('A1B2C3D4E5F6'));
});
test('reopening the page authenticates automatically with the saved key',()=>{
  const s=setup({storage:savedStorage()});assert.equal(s.sockets.length,1);const socket=s.sockets[0];socket.open();
  assert.deepEqual(socket.sent[0],{type:'hello',version:1,...credential});assert.equal(s.element('pairing').open,false);
  socket.message({type:'ready',version:1,name:'Laptop',remembered:true,deviceId:credential.deviceId,token:null});
  assert.equal(s.element('up').disabled,false);assert.equal(s.storage.size,1);
});
test('returning from background automatically reconnects without reopening the code dialog',()=>{
  const s=setup({storage:savedStorage()}),socket=s.sockets[0];socket.open();socket.message({type:'ready',version:1,remembered:true});
  s.document.hidden=true;s.listeners.visibilitychange();assert.equal(socket.readyState,3);
  s.document.hidden=false;s.listeners.visibilitychange();assert.equal(s.sockets.length,2);
  s.sockets[1].open();assert.deepEqual(s.sockets[1].sent[0],{type:'hello',version:1,...credential});
});
test('manual disconnect prevents automatic reconnect until user connects again',()=>{
  const s=setup({storage:savedStorage()}),socket=s.sockets[0];socket.open();socket.message({type:'ready',version:1,remembered:true});
  s.element('disconnect').fire('click');s.document.hidden=true;s.listeners.visibilitychange();s.document.hidden=false;s.listeners.visibilitychange();s.listeners.pageshow();
  assert.equal(s.sockets.length,1);s.element('connect').fire('click');assert.equal(s.sockets.length,2);
});
test('offline laptop retries with saved key and bounded backoff',()=>{
  const s=setup({storage:savedStorage()}),socket=s.sockets[0];socket.onerror();
  assert.ok(s.storage.has(rememberedKey));assert.equal(s.runNextTimeout(),2000);assert.equal(s.sockets.length,2);
  s.sockets[1].open();assert.deepEqual(s.sockets[1].sent[0],{type:'hello',version:1,...credential});
});
test('busy laptop preserves the key and retries rather than asking for a code',()=>{
  const s=setup({storage:savedStorage()}),socket=s.sockets[0];socket.open();socket.message({type:'error',code:'busy',message:'Busy'});
  assert.ok(s.storage.has(rememberedKey));assert.equal(s.runNextTimeout(),15000);
  assert.equal(s.element('pairing').open,false);assert.equal(s.sockets.length,2);
});
test('revoked credential is deleted and does not enter a reconnect loop',()=>{
  const s=setup({storage:savedStorage()}),socket=s.sockets[0];socket.open();socket.message({type:'error',code:'invalid_device',message:'Revoked'});
  assert.equal(s.storage.has(rememberedKey),false);assert.equal(s.timeouts.filter(t=>!t.cancelled).length,0);
  s.listeners.pageshow();assert.equal(s.sockets.length,1);s.element('connect').fire('click');assert.equal(s.element('pairing').open,true);
});
test('browser storage failure does not claim the key was saved permanently',()=>{
  const s=setup({failStorage:true});s.element('remember').checked=true;const socket=s.begin();
  socket.message({type:'ready',version:1,name:'Laptop',remembered:true,...credential});
  assert.equal(s.element('up').disabled,false);assert.match(s.element('status').textContent,/не сохранил ключ/);assert.equal(s.storage.size,0);
});
test('malformed stored data never opens an automatic connection',()=>{
  for(const value of ['broken-json','{}','{"deviceId":"x","token":"y"}']){
    const s=setup({storage:new Map([[rememberedKey,value]])});assert.equal(s.sockets.length,0);
  }
});
test('network loss after first enrollment retries with the new key and can be stopped',()=>{
  const s=setup();s.element('remember').checked=true;const socket=s.begin();
  socket.message({type:'ready',version:1,name:'Laptop',remembered:true,...credential});socket.onerror();
  assert.equal(s.element('disconnect').disabled,false);assert.equal(s.timeouts.filter(t=>!t.cancelled).length,1);
  s.element('disconnect').fire('click');assert.equal(s.timeouts.filter(t=>!t.cancelled).length,0);
  s.listeners.pageshow();assert.equal(s.sockets.length,1);
});
test('slightly short display intervals do not skip alternating cursor frames',()=>{
  const s=setup(),socket=s.pair(),pad=s.element('touchpad');
  pad.fire('pointerdown',{pointerId:1,clientX:10,clientY:10,timeStamp:0,pointerType:'touch'});
  for(let i=1;i<=8;i++){
    pad.fire('pointermove',{pointerId:1,clientX:10+i*10,clientY:10,timeStamp:i*16.5});s.context.frame(i*16.5);
  }
  assert.equal(socket.sent.filter(m=>m.type==='move').length,8);
});
test('coalesced pointer samples are integrated before sending a frame',()=>{
  const s=setup(),socket=s.pair(),pad=s.element('touchpad');
  pad.fire('pointerdown',{pointerId:1,clientX:0,clientY:0,timeStamp:0,pointerType:'touch'});
  pad.fire('pointermove',{pointerId:1,clientX:10,clientY:4,timeStamp:20,getCoalescedEvents:()=>[{clientX:3,clientY:1},{clientX:6,clientY:2},{clientX:10,clientY:4}]});
  pad.fire('pointerup',{pointerId:1,clientX:10,clientY:4,timeStamp:80});
  assert.deepEqual(socket.sent.filter(m=>m.type==='move'),[{type:'move',dx:15,dy:6}]);
});
test('congested network drops stale motion instead of replaying a jump',()=>{
  const s=setup(),socket=s.pair(),pad=s.element('touchpad');
  pad.fire('pointerdown',{pointerId:1,clientX:0,clientY:0,timeStamp:0,pointerType:'touch'});
  pad.fire('pointermove',{pointerId:1,clientX:100,clientY:0,timeStamp:20});socket.bufferedAmount=5000;s.context.frame(20);
  socket.bufferedAmount=0;s.context.frame(40);assert.equal(socket.sent.filter(m=>m.type==='move').length,0);
});
test('smoothing can be disabled and its preference survives a page reopen',()=>{
  const s=setup(),socket=s.pair(),pad=s.element('touchpad');
  s.element('smoothing').fire('change',{target:{checked:false}});
  assert.equal(s.storage.get('remote.smoothing'),'off');
  pad.fire('pointerdown',{pointerId:1,clientX:0,clientY:0,timeStamp:0,pointerType:'touch'});
  pad.fire('pointermove',{pointerId:1,clientX:20,clientY:0,timeStamp:20});s.context.frame(20);
  assert.deepEqual(socket.sent.at(-1),{type:'move',dx:30,dy:0});
  assert.equal(setup({storage:s.storage}).element('smoothing').checked,false);
});
test('control sheet leaves the touchpad visible and preserves the authenticated connection',()=>{
  const s=setup(),socket=s.pair();
  assert.equal(s.element('touchScreen').hidden,false);assert.equal(s.element('controlScreen').open,false);
  s.element('openControls').fire('click');
  assert.equal(s.element('touchScreen').hidden,false);assert.equal(s.element('controlScreen').open,true);
  s.element('up').fire('click');assert.deepEqual(socket.sent.at(-1),{type:'volume',action:'up'});
  s.element('backToTouchpad').fire('click');
  assert.equal(s.element('touchScreen').hidden,false);assert.equal(s.element('controlScreen').open,false);
  assert.equal(s.element('openControls').focused,true);
  assert.equal(socket.readyState,1);assert.equal(s.sockets.length,1);
});
test('opening the control sheet ends a pinch and blocks touchpad input behind it',()=>{
  const s=setup(),socket=s.pair();startPinch(s);s.element('openControls').fire('click');
  assert.deepEqual(socket.sent.at(-1),{type:'pinch',phase:'end'});
  const sent=socket.sent.length;s.element('touchpad').fire('pointerdown',{pointerId:7,clientX:0,clientY:0,timeStamp:50,pointerType:'touch'});
  s.element('touchpad').fire('pointermove',{pointerId:7,clientX:50,clientY:50,timeStamp:100});s.context.frame(100);
  assert.equal(socket.sent.length,sent);
});
test('the sheet closes from its backdrop or handle without closing when a slider crosses the edge',()=>{
  const s=setup(),screen=s.element('controlScreen'),handle=s.element('controlHandle');
  s.element('openControls').fire('click');
  screen.fire('pointerdown',{target:s.element('sensitivity')});
  screen.fire('click',{target:screen});
  assert.equal(screen.open,true);
  screen.fire('pointerdown',{target:screen});screen.fire('click',{target:screen});
  assert.equal(screen.open,false);
  s.element('openControls').fire('click');
  const start={isPrimary:true,button:0,pointerId:1,clientX:100,clientY:200};
  handle.fire('pointerdown',start);handle.fire('pointerup',{...start,clientY:225});
  assert.equal(screen.open,true);
  handle.fire('pointerdown',start);handle.fire('pointerup',{...start,clientY:260});
  assert.equal(screen.open,false);
});
test('iPhone standalone uses screen height when viewport omits the status-bar area',()=>{
  const s=setup({standalone:true,innerHeight:797});assert.equal(s.cssProperties.get('--app-height'),'844px');
});
test('regular browser follows viewport resize without expanding under its browser bars',()=>{
  const s=setup({innerHeight:724});assert.equal(s.cssProperties.get('--app-height'),'724px');
  s.window.innerHeight=797;s.listeners.resize();assert.equal(s.cssProperties.get('--app-height'),'797px');
});
function keyboardSetup(){const s=setup(),socket=s.pair();s.element('openKeyboard').fire('click');return{...s,socket}}
function typeText(s,value,event={}){s.element('keyboardText').value=value;s.element('keyboardText').fire('input',event)}
function textCommands(socket){return socket.sent.filter(m=>m.type==='text_edit')}
function acknowledge(s){const command=textCommands(s.socket).at(-1);s.socket.message({type:'text_ack',seq:command.seq})}
test('keyboard button opens a native editable field synchronously',()=>{
  const s=keyboardSetup();assert.equal(s.element('keyboardDialog').open,true);assert.equal(s.element('keyboardText').focused,true);
  assert.equal(s.element('keyboardText').readOnly,false);assert.equal(textCommands(s.socket).length,0);
});
test('live keyboard shows Cyrillic locally and transmits text edits immediately',()=>{
  const s=keyboardSetup();typeText(s,'Привет');
  assert.equal(s.element('keyboardText').value,'Привет');assert.deepEqual(textCommands(s.socket),[{type:'text_edit',seq:1,deletions:0,text:'Привет'}]);
  acknowledge(s);typeText(s,'Приве');assert.deepEqual(textCommands(s.socket).at(-1),{type:'text_edit',seq:2,deletions:1,text:''});
  assert.ok([...s.storage.values()].every(v=>!v.includes('Привет')));
});
test('keyboard waits for acknowledgements and coalesces newer typing',()=>{
  const s=keyboardSetup();typeText(s,'п');typeText(s,'привет');assert.equal(textCommands(s.socket).length,1);
  acknowledge(s);assert.deepEqual(textCommands(s.socket).at(-1),{type:'text_edit',seq:2,deletions:0,text:'ривет'});
  acknowledge(s);assert.equal(textCommands(s.socket).length,2);
});
test('keyboard composition is committed once after IME finishes',()=>{
  const s=keyboardSetup();s.element('keyboardText').fire('compositionstart');typeText(s,'nihao',{isComposing:true});
  assert.equal(textCommands(s.socket).length,0);s.element('keyboardText').value='你好';s.element('keyboardText').fire('compositionend');
  s.element('keyboardText').fire('input');assert.equal(textCommands(s.socket).length,1);assert.equal(textCommands(s.socket)[0].text,'你好');
});
test('closing keyboard drains all typed text before returning to the pad',()=>{
  const s=keyboardSetup();typeText(s,'h');typeText(s,'hello');s.element('keyboardDone').fire('click');
  assert.equal(s.element('keyboardDialog').open,true);assert.equal(s.element('keyboardDone').disabled,true);
  acknowledge(s);assert.equal(s.element('keyboardDialog').open,true);acknowledge(s);
  assert.equal(s.element('keyboardDialog').open,false);assert.equal(s.element('keyboardText').focused,false);
});
test('reopening keyboard starts a new segment without erasing previous PC text',()=>{
  const s=keyboardSetup();typeText(s,'one');acknowledge(s);s.element('keyboardDone').fire('click');s.element('openKeyboard').fire('click');
  assert.equal(s.element('keyboardText').value,'');typeText(s,'two');
  assert.deepEqual(textCommands(s.socket).at(-1),{type:'text_edit',seq:2,deletions:0,text:'two'});
});
test('disconnect preserves the local draft and never replays it after reconnect',()=>{
  const s=keyboardSetup();typeText(s,'draft');s.socket.onclose();
  assert.equal(s.element('keyboardText').value,'draft');assert.equal(s.element('keyboardText').readOnly,true);
  const next=s.begin();next.message({type:'ready',version:1,keyboard:true});assert.equal(textCommands(next).length,0);
  assert.equal(s.element('keyboardText').readOnly,true);
});
test('native keyboard failure stops the typing segment but keeps other controls connected',()=>{
  const s=keyboardSetup();typeText(s,'test');s.socket.message({type:'notice',feature:'keyboard',seq:1,message:'Input blocked'});
  assert.equal(s.element('keyboardText').readOnly,true);assert.equal(s.socket.readyState,1);assert.equal(s.element('keyboardStatus').textContent,'Input blocked');
  s.element('up').fire('click');assert.deepEqual(s.socket.sent.at(-1),{type:'volume',action:'up'});
});
test('missing text acknowledgement stops the connection without replaying edits',()=>{
  const s=keyboardSetup();typeText(s,'test');assert.equal(s.runNextTimeout(),3500);assert.equal(s.socket.readyState,3);
  assert.equal(textCommands(s.socket).length,1);assert.equal(s.element('keyboardText').value,'test');
});

test('magnifier is opt-in, remembers preferences and coalesces changes until acknowledgement',()=>{
  const s=setup(),socket=s.begin();socket.message({type:'ready',version:1,magnifier:true});
  assert.equal(s.element('magnifierEnabled').checked,false);
  assert.equal(socket.sent.filter(m=>m.type==='magnifier').length,0);
  s.element('magnifierEnabled').checked=true;s.element('magnifierEnabled').fire('change');
  assert.deepEqual(socket.sent.at(-1),{type:'magnifier',enabled:true,radius:160,zoom:200,mode:'center'});
  s.element('magnifierRadius').value='240';s.element('magnifierRadius').fire('input');
  s.element('magnifierZoom').value='300';s.element('magnifierZoom').fire('input');
  s.element('magnifierMode').value='offset';s.element('magnifierMode').fire('change');
  assert.equal(socket.sent.filter(m=>m.type==='magnifier').length,1);
  socket.message({type:'magnifier_state',enabled:true});
  assert.deepEqual(socket.sent.at(-1),{type:'magnifier',enabled:true,radius:240,zoom:300,mode:'offset'});
  socket.message({type:'magnifier_state',enabled:true});
  s.element('disconnect').fire('click');assert.equal(s.element('magnifierEnabled').checked,false);
  const again=setup({storage:s.storage});assert.equal(again.element('magnifierRadius').value,240);
  assert.equal(again.element('magnifierMode').value,'offset');assert.equal(again.element('magnifierEnabled').checked,false);
  assert.equal(JSON.parse(s.storage.get('remote.magnifier')).enabled,undefined);
});
test('magnifier native error disables only the lens and remains visible',()=>{
  const s=setup(),socket=s.begin();socket.message({type:'ready',version:1,magnifier:true});
  s.element('magnifierEnabled').checked=true;s.element('magnifierEnabled').fire('change');
  socket.message({type:'notice',feature:'magnifier',message:'Ошибка лупы'});
  assert.equal(s.element('magnifierEnabled').checked,false);assert.equal(socket.readyState,1);
  assert.equal(s.element('magnifierStatus').textContent,'Ошибка лупы');
});

function fingerTap(button,id=1) {
  button.fire('pointerdown',{pointerType:'touch',pointerId:id,clientX:20,clientY:20});
  button.fire('pointerup',{pointerType:'touch',pointerId:id,clientX:23,clientY:23});
}
test('finger buttons activate without a Safari click and suppress the duplicate compatibility click',()=>{
  const s=setup(),socket=s.pair(),button=s.element('up');
  fingerTap(button);button.fire('click',{detail:1,pointerType:'touch'});
  fingerTap(button,2);button.fire('click',{detail:1,pointerType:'touch'});
  assert.equal(socket.sent.filter(m=>m.type==='volume').length,2);
  button.fire('click',{detail:0});assert.equal(socket.sent.filter(m=>m.type==='volume').length,3);
});
test('scrolling, cancellation, lifting outside and disabled buttons never activate',()=>{
  const s=setup(),socket=s.pair(),button=s.element('up');
  const start={pointerType:'touch',pointerId:1,clientX:20,clientY:20};
  button.fire('pointerdown',start);button.fire('pointermove',{...start,clientY:70});button.fire('pointerup',start);
  button.fire('pointerdown',start);button.fire('pointercancel',start);button.fire('pointerup',start);
  button.fire('pointerdown',{...start,clientX:395});button.fire('pointerup',{...start,clientX:405});
  button.disabled=true;fingerTap(button);button.fire('click',{detail:1});
  assert.equal(socket.sent.filter(m=>m.type==='volume').length,0);
});
test('finger toolbar taps release the pad capture and synchronously focus the iPhone keyboard',()=>{
  const s=setup();s.pair();const pad=s.element('touchpad');
  pad.fire('pointerdown',{pointerType:'touch',pointerId:50,clientX:80,clientY:80,timeStamp:0});
  assert.equal(pad.captures.size,1);fingerTap(s.element('openControls'));
  assert.equal(pad.captures.size,0);assert.equal(s.element('controlScreen').open,true);
  fingerTap(s.element('backToTouchpad'));assert.equal(s.element('controlScreen').open,false);
  fingerTap(s.element('openKeyboard'));assert.equal(s.element('keyboardDialog').open,true);assert.equal(s.element('keyboardText').focused,true);
  s.element('keyboardText').value='draft';s.element('openKeyboard').fire('click',{detail:1,pointerType:'touch'});
  assert.equal(s.element('keyboardText').value,'draft');
});
test('double tap and drag flushes movement before mouse release and cancels on toolbar access',()=>{
  const s=setup(),socket=s.pair(),pad=s.element('touchpad');
  pad.fire('pointerdown',{pointerType:'touch',pointerId:1,clientX:80,clientY:80,timeStamp:0});
  pad.fire('pointerup',{pointerType:'touch',pointerId:1,clientX:80,clientY:80,timeStamp:80});
  pad.fire('pointerdown',{pointerType:'touch',pointerId:2,clientX:80,clientY:80,timeStamp:180});
  pad.fire('pointermove',{pointerId:2,clientX:120,clientY:100});
  fingerTap(s.element('openControls'));
  const commands=socket.sent.slice(1);assert.deepEqual(commands.map(m=>m.type),['click','drag','move','drag']);
  assert.equal(commands[1].phase,'start');assert.equal(commands.at(-1).phase,'end');assert.equal(pad.captures.size,0);
});

test('screen uses a separate socket while touchpad input remains on the control channel',()=>{
  const s=setup(),control=s.begin();control.message({type:'ready',version:1,screen:true,drag:true});
  assert.equal(s.element('screenEnabled').disabled,false);
  s.element('screenEnabled').checked=true;s.element('screenEnabled').fire('change');
  assert.deepEqual(control.sent.at(-1),{type:'screen_start',id:1});
  control.message({type:'screen_ready',id:1,ticket:'A'.repeat(64)});
  assert.equal(s.sockets.length,2);const screen=s.sockets.at(-1);screen.open();screen.message({type:'screen_connected'});
  assert.equal(screen.sent.at(-1).type,'screen_next');assert.equal(screen.sent.at(-1).fps,60);
  const pad=s.element('touchpad');pad.fire('pointerdown',{pointerType:'touch',pointerId:1,clientX:30,clientY:30,timeStamp:0});
  pad.fire('pointermove',{pointerId:1,clientX:60,clientY:50});pad.fire('pointerup',{pointerId:1,clientX:60,clientY:50,timeStamp:200});
  assert.ok(control.sent.some(m=>m.type==='move'));assert.ok(!screen.sent.some(m=>m.type==='move'));
  s.element('openControls').fire('click');assert.equal(screen.readyState,3);assert.equal(control.sent.at(-1).type,'screen_stop');
  s.element('backToTouchpad').fire('click');assert.deepEqual(control.sent.at(-1),{type:'screen_start',id:2});
  s.element('disconnect').fire('click');assert.equal(s.element('screenEnabled').checked,false);
});
test('screen stays disabled for older servers without interrupting the touchpad',()=>{
  const s=setup(),socket=s.pair();assert.equal(s.element('screenEnabled').disabled,true);
  s.element('screenEnabled').checked=true;s.element('screenEnabled').fire('change');
  assert.equal(socket.sent.filter(m=>m.type==='screen_start').length,0);assert.equal(socket.readyState,1);
});

test('drag lock survives a finger lift, resumes relatively and ends on a short tap',()=>{
  const s=setup(),socket=s.pair(),pad=s.element('touchpad');
  pad.fire('pointerdown',{pointerType:'touch',pointerId:1,clientX:50,clientY:50,timeStamp:0});
  pad.fire('pointerup',{pointerId:1,clientX:50,clientY:50,timeStamp:60});
  pad.fire('pointerdown',{pointerType:'touch',pointerId:2,clientX:50,clientY:50,timeStamp:160});
  pad.fire('pointermove',{pointerId:2,clientX:80,clientY:60});
  pad.fire('pointerup',{pointerId:2,clientX:80,clientY:60,timeStamp:250});
  assert.equal(socket.sent.filter(m=>m.type==='drag'&&m.phase==='end').length,0);
  assert.equal(pad.captures.size,0);assert.equal(s.element('padState').textContent,'ПЕРЕСТАВЬТЕ ПАЛЕЦ');
  pad.fire('pointerdown',{pointerType:'touch',pointerId:3,clientX:300,clientY:400,timeStamp:600});
  pad.fire('pointermove',{pointerId:3,clientX:310,clientY:400});
  pad.fire('pointerup',{pointerId:3,clientX:310,clientY:400,timeStamp:700});
  pad.fire('pointerdown',{pointerType:'touch',pointerId:4,clientX:10,clientY:10,timeStamp:850});
  pad.fire('pointerup',{pointerId:4,clientX:10,clientY:10,timeStamp:900});
  assert.equal(socket.sent.filter(m=>m.type==='drag'&&m.phase==='start').length,1);
  assert.equal(socket.sent.filter(m=>m.type==='drag'&&m.phase==='end').length,1);
  assert.equal(socket.sent.filter(m=>m.type==='click').length,1);
  const movement=socket.sent.filter(m=>m.type==='move').reduce((sum,m)=>sum+m.dx,0);assert.equal(movement,60);
  assert.equal(s.timeouts.filter(t=>!t.cancelled&&t.delay===800).length,0);
});
test('opening controls or disconnecting while drag-locked cancels the pending release timer',()=>{
  for(const action of ['openControls','disconnect']){
    const s=setup(),socket=s.pair(),pad=s.element('touchpad');
    pad.fire('pointerdown',{pointerType:'touch',pointerId:1,clientX:50,clientY:50,timeStamp:0});
    pad.fire('pointerup',{pointerId:1,clientX:50,clientY:50,timeStamp:60});
    pad.fire('pointerdown',{pointerType:'touch',pointerId:2,clientX:50,clientY:50,timeStamp:160});
    pad.fire('pointerup',{pointerId:2,clientX:80,clientY:50,timeStamp:250});
    s.element(action).fire('click');
    assert.equal(s.timeouts.filter(t=>!t.cancelled&&t.delay===800).length,0);
    if(action==='openControls')assert.equal(socket.sent.filter(m=>m.type==='drag'&&m.phase==='end').length,1);
    else assert.equal(socket.readyState,3);
  }
});

test('HTTPS uses WSS for both control and screen without a plaintext fallback',()=>{
  const s=setup({protocol:'https:'}),control=s.begin();assert.equal(control.url,'wss://192.168.1.20:49732/ws');
  control.message({type:'ready',version:1,screen:true});s.element('screenEnabled').checked=true;s.element('screenEnabled').fire('change');
  control.message({type:'screen_ready',id:1,ticket:'A'.repeat(64)});
  assert.equal(s.sockets.at(-1).url,'wss://192.168.1.20:49732/ws');
});
