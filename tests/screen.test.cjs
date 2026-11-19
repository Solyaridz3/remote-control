const {test} = require('node:test');
const assert = require('node:assert/strict');
const {Viewer, FrameRate} = require('../web/screen.js');

function setup() {
  const commands=[], sockets=[], timers=[], revoked=[];
  let time=0, nextUrl=0;
  const image={hidden:true,removeAttribute(name){delete this[name]}};
  class Socket {
    constructor(){this.readyState=0;this.sent=[];sockets.push(this)}
    send(text){this.sent.push(JSON.parse(text))}
    close(){this.readyState=3}
    open(){this.readyState=1;this.onopen?.()}
    message(data){this.onmessage?.({data:typeof data==='string'?data:JSON.stringify(data)})}
    frame(size=60000){this.onmessage?.({data:{frame:true,size}})}
  }
  const viewer=new Viewer({image,control:command=>{commands.push(command);return true},socketFactory:()=>new Socket(),size:()=>({width:390,height:681}),now:()=>time,
    schedule(fn,delay){const timer={fn,delay,cancelled:false};timers.push(timer);return timer},cancel(timer){if(timer)timer.cancelled=true},
    urls:{createObjectURL(){return 'blob:frame-'+ ++nextUrl},revokeObjectURL(url){revoked.push(url)}},isFrame:data=>data?.frame===true});
  function ready(){viewer.setSupported(true);viewer.toggle(true);viewer.accept({type:'screen_ready',id:viewer.requestId,ticket:'A'.repeat(64)});const socket=sockets.at(-1);socket.open();socket.message({type:'screen_connected'});return socket}
  function next(){const timer=timers.find(t=>!t.cancelled&&t.delay<1000);assert.ok(timer);timer.cancelled=true;time+=timer.delay;timer.fn()}
  return{viewer,commands,sockets,timers,image,revoked,ready,next,advance(ms){time+=ms}};
}
test('stream is opt-in, requests at most one frame and waits for image decode',()=>{
  const s=setup();s.viewer.setSupported(true);assert.equal(s.commands.length,0);
  const socket=s.ready();assert.deepEqual(s.commands,[{type:'screen_start',id:1}]);
  assert.deepEqual(socket.sent[0],{type:'screen_hello',ticket:'A'.repeat(64)});
  assert.deepEqual(socket.sent[1],{type:'screen_next',seq:1,width:390,height:681,quality:80,fps:60});
  s.viewer.next();assert.equal(socket.sent.length,2);
  socket.frame();s.viewer.next();assert.equal(socket.sent.length,2);assert.equal(s.image.hidden,true);
  s.advance(10);s.image.onload();assert.equal(s.image.hidden,false);assert.equal(s.viewer.running,true);
  assert.equal(socket.sent.length,2);s.next();assert.equal(socket.sent.at(-1).seq,2);
});
test('pausing closes the screen channel, revokes image data and resumes with a fresh ticket',()=>{
  const s=setup(),socket=s.ready();socket.frame();s.advance(10);s.image.onload();
  s.viewer.setVisible(false);assert.equal(socket.readyState,3);assert.equal(s.viewer.enabled,true);assert.equal(s.image.hidden,true);
  assert.equal(s.revoked.length,1);assert.equal(s.commands.at(-1).type,'screen_stop');
  s.viewer.setVisible(true);assert.equal(s.commands.at(-1).id,2);assert.equal(s.viewer.pending,true);
  s.viewer.accept({type:'screen_ready',id:1,ticket:'B'.repeat(64)});assert.equal(s.sockets.length,1);
  s.viewer.accept({type:'screen_ready',id:2,ticket:'C'.repeat(64)});assert.equal(s.sockets.length,2);
});
test('off and disconnect never restore a stream or replay a stale frame',()=>{
  const s=setup(),socket=s.ready();socket.frame();const lateLoad=s.image.onload;
  s.viewer.setSupported(false);lateLoad();assert.equal(s.viewer.running,false);assert.equal(s.viewer.enabled,false);assert.equal(s.image.hidden,true);
  s.viewer.setSupported(true);assert.equal(s.commands.filter(c=>c.type==='screen_start').length,1);
  assert.equal(s.timers.filter(t=>!t.cancelled).length,0);
});
test('failed image decode and oversized frames disable only the screen channel',()=>{
  for(const oversized of [true,false]){
    const s=setup(),socket=s.ready();socket.frame(oversized?2097153:100);
    if(!oversized)s.image.onerror();
    assert.equal(s.viewer.enabled,false);assert.equal(socket.readyState,3);assert.ok(s.viewer.message);
    assert.ok(s.commands.every(c=>['screen_start','screen_stop'].includes(c.type)));
  }
});
test('frame and handshake timeouts end the stream and cancel all scheduled work',()=>{
  const s=setup();s.viewer.setSupported(true);s.viewer.toggle(true);
  s.timers.find(t=>!t.cancelled).fn();assert.equal(s.viewer.enabled,false);
  assert.equal(s.timers.filter(t=>!t.cancelled).length,0);
  const other=setup();other.ready();other.timers.find(t=>!t.cancelled).fn();
  assert.equal(other.viewer.enabled,false);assert.equal(other.sockets[0].readyState,3);
});
test('adaptive frame rate backs off slow links and recovers up to 60 fps',()=>{
  const rate=new FrameRate();for(let i=0;i<100;i++)rate.record(60,60000);
  assert.ok(rate.fps<30);assert.ok(rate.quality<80);
  for(let i=0;i<1000;i++)rate.record(5,20000);
  assert.equal(rate.fps,60);assert.equal(rate.quality,85);
});
test('replacing decoded frames releases the previous URL without queuing frames',()=>{
  const s=setup(),socket=s.ready();socket.frame();s.advance(10);s.image.onload();s.next();
  socket.frame();s.advance(10);s.image.onload();assert.deepEqual(s.revoked,['blob:frame-1']);
  s.viewer.toggle(false);assert.deepEqual(s.revoked,['blob:frame-1','blob:frame-2']);assert.equal(s.image.src,undefined);
});
