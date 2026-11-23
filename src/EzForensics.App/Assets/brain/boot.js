// Brain start-up watchdog (a classic script, so it runs even when the 3D modules cannot load).
// brain.js sets window.brainStarted; if that never happens the app is told, and it shows a message instead of an empty canvas.
(function () {
  'use strict';
  var host = window.chrome && window.chrome.webview;
  window.brainStarted = false;
  setTimeout(function () {
    if (!window.brainStarted && host) host.postMessage({ type: 'problem', reason: 'start' });
  }, 10000);
})();
