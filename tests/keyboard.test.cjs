const {test}=require('node:test');
const assert=require('node:assert/strict');
const {normalize,nextEdit}=require('../web/keyboard.js');
function mirror(before,after){let current=before,steps=0;while(current!==after){const edit=nextEdit(current,after);assert.ok(edit);assert.ok(edit.deletions<=128&&edit.text.length<=128);assert.ok(Buffer.byteLength(JSON.stringify({type:'text_edit',seq:1,deletions:edit.deletions,text:edit.text}))<=1024);current=edit.result;if(++steps>300)throw new Error('Edit did not converge')}return steps}
test('typing and removing characters produces incremental edits',()=>{
  assert.deepEqual(nextEdit('При','Привет'),{deletions:0,text:'вет',result:'Привет'});
  assert.deepEqual(nextEdit('Привет','Приве'),{deletions:1,text:'',result:'Приве'});
});
test('autocorrection and edits in the middle replace only the changed suffix',()=>{
  assert.deepEqual(nextEdit('hello world','hello brave world'),{deletions:5,text:'brave world',result:'hello brave world'});
  mirror('превет','привет');mirror('one two three','one four three');
});
test('emoji and combined graphemes are deleted as complete characters',()=>{
  assert.equal(nextEdit('a😀','a').deletions,1);
  assert.equal(nextEdit('a👨‍👩‍👧','a').deletions,1);
  assert.equal(nextEdit('e\u0301','').deletions,1);
});
test('long paste is chunked without splitting surrogate pairs or exceeding frames',()=>{
  assert.equal(mirror('','😀'.repeat(256)),4);mirror('a'.repeat(500),'б'.repeat(500));
});
test('normalization removes unsupported controls and unpaired surrogates',()=>{
  assert.equal(normalize('a\r\nb\rc\t\u0000\ud800😀'),'a\nb\nc\t😀');
  assert.ok(normalize('😀'.repeat(5000)).length<=8192);
});
test('unchanged text does not generate commands and large deletions converge',()=>{
  assert.equal(nextEdit('same','same'),null);assert.equal(mirror('x'.repeat(300),''),3);
});
