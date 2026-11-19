const { test } = require('node:test');
const assert = require('node:assert/strict');
const { GestureTracker, MotionFilter } = require('../web/gestures.js');
function setup() {
  const events = [];
  const tracker = new GestureTracker({ move: (x,y) => events.push(['move',x,y]), scroll: y => events.push(['scroll',y]), click: right => events.push(['click',right]), pinch: (phase,scale) => events.push(['pinch',phase,scale]) });
  return { tracker, events };
}
test('short one-finger tap emits exactly one left click', () => {
  const {tracker, events} = setup(); tracker.start(1,10,10,0); tracker.move(1,11,10.5); tracker.end(1,100);
  assert.deepEqual(events,[['click',false]]);
});
test('two-finger tap emits one right click, with no stray left click', () => {
  const {tracker, events} = setup(); tracker.start(1,10,10,0); tracker.start(2,40,10,25); tracker.end(1,100); tracker.end(2,120);
  assert.deepEqual(events,[['click',true]]);
});
test('one-finger drag moves without a click on release', () => {
  const {tracker, events} = setup(); tracker.start(1,10,10,0); tracker.move(1,30,40); tracker.move(1,32,42); tracker.end(1,100);
  assert.deepEqual(events,[['move',20,30],['move',2,2]]);
});
test('two-finger scroll uses centroid, without cursor jumps when fingers change', () => {
  const {tracker, events} = setup(); tracker.start(1,10,10,0); tracker.start(2,50,10,20);
  tracker.move(1,10,30); tracker.move(2,50,30); tracker.end(1,100); tracker.move(2,50,80); tracker.end(2,150);
  assert.deepEqual(events,[['scroll',10],['scroll',10]]);
});
test('cancelled gestures and long holds never click', () => {
  const {tracker, events} = setup(); tracker.start(1,10,10,0); tracker.cancel(); tracker.end(1,100);
  tracker.start(2,10,10,200); tracker.end(2,1000); assert.deepEqual(events,[]);
});
test('three fingers suppress commands until a fresh gesture', () => {
  const {tracker, events} = setup(); tracker.start(1,0,0,0); tracker.start(2,20,0,0); tracker.start(3,40,0,0);
  tracker.move(1,20,20); tracker.end(1,100); tracker.end(2,100); tracker.end(3,100);
  tracker.start(4,0,0,200); tracker.end(4,250); assert.deepEqual(events,[['click',false]]);
});
test('spreading two fingers emits a continuous pinch without scroll or click', () => {
  const {tracker,events}=setup();tracker.start(1,100,100,0);tracker.start(2,200,100,10);
  tracker.move(1,80,100);tracker.move(2,220,100);tracker.end(1,100);tracker.end(2,120);
  assert.deepEqual(events,[['pinch','start',1],['pinch','update',1.2],['pinch','update',1.4],['pinch','end',1]]);
});
test('bringing fingers together shrinks smoothly', () => {
  const {tracker,events}=setup();tracker.start(1,100,100,0);tracker.start(2,200,100,10);
  tracker.move(1,120,100);tracker.move(2,180,100);tracker.end(1,100);tracker.end(2,120);
  assert.deepEqual(events,[['pinch','start',1],['pinch','update',0.8],['pinch','update',0.6],['pinch','end',1]]);
});
test('cancelling a pinch sends end exactly once', () => {
  const {tracker,events}=setup();tracker.start(1,100,100,0);tracker.start(2,200,100,10);tracker.move(1,80,100);
  tracker.cancel();tracker.cancel();tracker.end(1,100);assert.equal(events.filter(e=>e[1]==='end').length,1);
});
test('third finger ends a pinch and blocks leftover input', () => {
  const {tracker,events}=setup();tracker.start(1,100,100,0);tracker.start(2,200,100,10);tracker.move(1,80,100);
  tracker.start(3,150,150,30);tracker.move(2,220,100);tracker.end(3,70);tracker.end(1,100);tracker.end(2,120);
  assert.deepEqual(events.map(e=>e.slice(0,2)),[['pinch','start'],['pinch','update'],['pinch','end']]);
});
test('vertical pinch selects scale rather than emitting wheel input', () => {
  const {tracker,events}=setup();tracker.start(1,100,100,0);tracker.start(2,100,200,10);
  tracker.move(1,100,80);tracker.move(2,100,220);
  assert.equal(events[0][1],'start');assert.equal(events.at(-1)[2],1.4);assert.ok(events.every(e=>e[0]==='pinch'));
});
test('scroll locks its mode so alternating finger events do not become zoom', () => {
  const {tracker,events}=setup();tracker.start(1,100,100,0);tracker.start(2,200,100,10);
  tracker.move(1,100,120);tracker.move(2,200,120);tracker.move(1,100,150);tracker.move(2,200,150);
  tracker.end(1,100);tracker.end(2,120);assert.ok(events.every(e=>e[0]==='scroll'));
});
test('fine cursor movement starts after two pixels without losing the initial samples',()=>{
  const {tracker,events}=setup();tracker.start(1,0,0,0);tracker.move(1,0.5,0);tracker.move(1,1,0);tracker.move(1,2,0);tracker.move(1,2.5,0);
  assert.deepEqual(events,[['move',2,0],['move',0.5,0]]);
});
test('smoothing reduces an isolated jump and preserves the final displacement',()=>{
  const filter=new MotionFilter();filter.add(40,-20);const first=filter.step(8);
  assert.ok(first.dx>0&&first.dx<40);assert.ok(first.dy<0&&first.dy>-20);
  const last=filter.step(8,true);assert.equal(first.dx+last.dx,40);assert.equal(first.dy+last.dy,-20);
  assert.deepEqual(filter.step(16),{dx:0,dy:0});
});
test('subpixel motion is retained across small samples in either direction',()=>{
  for(const sign of [-1,1]){
    const filter=new MotionFilter();let sum=0;
    for(let i=0;i<1000;i++){filter.add(0.1*sign,0);sum+=filter.step(16).dx;}
    sum+=filter.step(16,true).dx;assert.equal(sum,100*sign);
  }
});
test('smoothing response depends on elapsed time rather than screen refresh rate',()=>{
  const sixty=new MotionFilter(),oneTwenty=new MotionFilter();sixty.add(100,0);oneTwenty.add(100,0);
  sixty.step(16);sixty.step(16);for(let i=0;i<4;i++)oneTwenty.step(8);
  assert.ok(Math.abs(sixty.x-oneTwenty.x)<1e-9);
});
test('cancellation clears pending motion and fractional residue',()=>{
  const filter=new MotionFilter();filter.add(20.5,-10.5);filter.step(8);filter.clear();
  assert.deepEqual(filter.step(16,true),{dx:0,dy:0});
});
test('motion remains bounded to the server protocol range',()=>{
  const filter=new MotionFilter();filter.add(2000,-2000);assert.deepEqual(filter.step(16,true),{dx:500,dy:-500});
});

function draggingSetup(){
  const events=[],timers=[];
  const tracker=new GestureTracker({click:r=>events.push(['click',r]),move:(x,y)=>events.push(['move',x,y]),drag:p=>events.push(['drag',p])},
    {schedule(fn,delay){const timer={fn,delay,cancelled:false};timers.push(timer);return timer},cancel(timer){if(timer)timer.cancelled=true}});
  return{tracker,events,timers,expire(){const timer=timers.find(t=>!t.cancelled);assert.ok(timer);timer.fn();}};
}
test('second rapid tap holds through a short lift then releases without an extra click',()=>{
  const {tracker:t,events,expire}=draggingSetup();t.start(1,50,50,0);t.end(1,80);t.start(2,52,50,180);t.move(2,72,60);t.end(2,900);
  assert.equal(t.dragWaiting,true);assert.equal(events.filter(e=>e[1]==='end').length,0);expire();
  assert.deepEqual(events,[['click',false],['drag','start'],['move',20,10],['drag','end']]);
});
test('drag can resume anywhere without a pointer jump or another mouse down',()=>{
  const {tracker:t,events,timers}=draggingSetup();t.start(1,50,50,0);t.end(1,80);t.start(2,50,50,180);t.move(2,70,60);t.end(2,300);
  const oldTimer=timers.at(-1);assert.equal(oldTimer.delay,800);
  t.start(3,300,500,600);assert.equal(t.dragWaiting,false);assert.equal(oldTimer.cancelled,true);
  oldTimer.fn();assert.equal(t.dragging,true);
  t.move(3,301,510);t.end(3,700);t.start(4,10,10,900);t.end(4,950);
  assert.deepEqual(events,[['click',false],['drag','start'],['move',20,10],['move',1,10],['drag','end']]);
  assert.equal(timers.filter(timer=>!timer.cancelled).length,0);
});
test('late re-touch releases the lock even if its timer was delayed',()=>{
  const {tracker:t,events}=draggingSetup();t.start(1,0,0,0);t.end(1,50);t.start(2,0,0,150);t.move(2,20,0);t.end(2,250);
  t.start(3,100,100,1100);t.end(3,1150);
  assert.deepEqual(events,[['click',false],['drag','start'],['move',20,0],['drag','end'],['click',false]]);
});
test('cancelling a drag lock clears its timer and releases exactly once',()=>{
  const {tracker:t,events,timers}=draggingSetup();t.start(1,0,0,0);t.end(1,50);t.start(2,0,0,150);t.move(2,20,0);t.end(2,250);
  const timer=timers.at(-1);t.cancel();t.cancel();timer.fn();
  assert.equal(events.filter(e=>e[0]==='drag'&&e[1]==='end').length,1);assert.equal(timer.cancelled,true);assert.equal(t.dragging,false);
});
test('quick double tap is a click followed by mouse down and up',()=>{
  const {tracker:t,events}=draggingSetup();t.start(1,0,0,0);t.end(1,50);t.start(2,0,0,150);t.end(2,200);
  assert.deepEqual(events,[['click',false],['drag','start'],['drag','end']]);
});
test('late or distant taps stay independent and cancellation clears double-tap memory',()=>{
  const {tracker:t,events}=draggingSetup();t.start(1,0,0,0);t.end(1,50);t.start(2,0,0,500);t.end(2,550);
  t.start(3,100,100,600);t.end(3,650);t.cancel();t.start(4,100,100,700);t.end(4,750);
  assert.equal(events.filter(e=>e[0]==='drag').length,0);assert.equal(events.length,4);
});
test('second finger or cancellation releases a drag exactly once and suppresses stray clicks',()=>{
  for(const extraFinger of [false,true]){
    const {tracker:t,events}=draggingSetup();t.start(1,0,0,0);t.end(1,50);t.start(2,0,0,150);
    if(extraFinger){t.start(3,20,0,170);t.end(3,220);t.end(2,240)}else{t.cancel();t.cancel();t.end(2,240)}
    assert.deepEqual(events,[['click',false],['drag','start'],['drag','end']]);
  }
});
