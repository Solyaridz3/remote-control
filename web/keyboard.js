(function (root) {
  'use strict';
  const segmenter = typeof Intl !== 'undefined' && Intl.Segmenter ? new Intl.Segmenter(undefined, {granularity:'grapheme'}) : null;
  function characters(text) { return segmenter ? Array.from(segmenter.segment(text), part => part.segment) : Array.from(text); }
  function chunk(text, limit) {
    let result = '';
    for (const character of text) { if (result.length + character.length > limit) break; result += character; }
    return result;
  }
  function normalize(text) {
    const clean = String(text).replace(/\r\n?/g, '\n').replace(/[\u0000-\u0008\u000B-\u001F\u007F-\u009F]/g, '');
    return chunk(Array.from(clean).filter(c => c.length === 2 || c.charCodeAt(0) < 0xD800 || c.charCodeAt(0) > 0xDFFF).join(''), 8192);
  }
  // The PC caret remains at the end of this typing segment. Replacing the
  // changed suffix also mirrors iPhone autocorrection and edits in the middle.
  function nextEdit(previous, desired) {
    if (previous === desired) return null;
    if (desired.startsWith(previous)) {
      const text = chunk(desired.slice(previous.length), 128);
      return {deletions:0, text, result:previous + text};
    }
    const before = characters(previous), after = characters(desired);
    let common = 0;
    while (common < before.length && common < after.length && before[common] === after[common]) common++;
    const suffix = before.length - common, deletions = Math.min(128, suffix);
    const text = deletions === suffix ? chunk(after.slice(common).join(''), 128) : '';
    return {deletions, text, result:before.slice(0, before.length - deletions).join('') + text};
  }
  if (typeof module !== 'undefined' && module.exports) module.exports = {normalize, nextEdit};
  else root.RemoteKeyboard = {normalize, nextEdit};
})(globalThis);
