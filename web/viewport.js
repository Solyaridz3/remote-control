(function (root) {
  'use strict';
  function appHeight(viewport) {
    const valid = n => Number.isFinite(n) && n > 0;
    const inner = valid(viewport.innerHeight) ? viewport.innerHeight : viewport.visualHeight;
    if (!valid(inner)) return 0;
    // iOS standalone WebKit can report a viewport excluding the status-bar
    // area even with viewport-fit=cover. Use the full screen only in that mode.
    // A regular Safari tab must continue to respect its browser chrome.
    if (viewport.standalone && valid(viewport.screenWidth) && valid(viewport.screenHeight)) {
      const screen = viewport.landscape
        ? Math.min(viewport.screenWidth, viewport.screenHeight)
        : Math.max(viewport.screenWidth, viewport.screenHeight);
      // Avoid treating a small window or an unrelated display as full screen.
      if (screen >= inner && screen <= inner * 1.3) return Math.round(screen);
    }
    return Math.round(valid(viewport.visualHeight) ? Math.min(inner, viewport.visualHeight) : inner);
  }
  if (typeof module !== 'undefined' && module.exports) module.exports = { appHeight };
  else root.RemoteViewport = { appHeight };
})(globalThis);
