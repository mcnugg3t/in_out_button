// page side of the WebView2 bridge. without a host it logs sends and plays a sample
(function () {
  'use strict';

  const host = window.chrome && window.chrome.webview;
  const handlers = [];
  const here = document.currentScript && document.currentScript.src;

  // PostWebMessageAsJson hands over an object; a string is parsed in case the host changes
  function deliver(data) {
    let msg = data;
    if (typeof msg === 'string') {
      try { msg = JSON.parse(msg); } catch (e) { return; }
    }
    if (!msg || typeof msg !== 'object' || Array.isArray(msg) || typeof msg.type !== 'string') return;
    handlers.slice().forEach(fn => {
      try { fn(msg); } catch (e) { console.error(e); }
    });
  }

  window.Bridge = {
    send(obj) {
      if (host) host.postMessage(obj);
      else console.log('[bridge] send', JSON.stringify(obj));
    },
    on(fn) {
      if (typeof fn === 'function') handlers.push(fn);
    }
  };

  if (host) {
    host.addEventListener('message', e => deliver(e.data));
    return;
  }

  // browser fallback only: the host never loads sample-data.js
  function playSample() {
    const s = document.createElement('script');
    s.src = here ? new URL('sample-data.js', here).href : 'sample-data.js';
    s.onload = () => {
      const page = document.body && document.body.dataset.page;
      const make = window.InOutSample && window.InOutSample[page];
      if (typeof make === 'function') deliver(make());
      else console.warn('[bridge] no sample for page', page);
    };
    s.onerror = () => console.warn('[bridge] sample-data.js did not load');
    document.head.appendChild(s);
  }
  const start = () => setTimeout(playSample, 200);
  if (document.readyState === 'complete') start();
  else window.addEventListener('load', start);
})();
