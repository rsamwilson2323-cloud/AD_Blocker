// Best-effort YouTube API response filtering. Do not block googlevideo.com: it also carries normal video.
(function () {
  'use strict';
  if (window.__adBlockerMainInstalled) return;
  window.__adBlockerMainInstalled = true;
  const prune = () => globalThis.AdPrune && globalThis.AdPrune.pruneAds;
  const report = n => { if (n > 0) { try { window.postMessage({ source: 'adblocker', type: 'pruned', n }, location.origin); } catch (_) {} } };
  const API = /\/youtubei\/v1\/(player|next|browse|search|reel\/reel_watch_sequence)(?:[?/#]|$)/;

  function clean(value) {
    try {
      const fn = prune();
      if (!fn || !value || typeof value !== 'object') return { value, count: 0 };
      const count = fn(value);
      report(count);
      return { value, count };
    } catch (_) { return { value, count: 0 }; }
  }

  // Intercept the initial player response before the player consumes it.
  try {
    let initial;
    Object.defineProperty(window, 'ytInitialPlayerResponse', {
      configurable: true,
      get() { return initial; },
      set(v) { initial = clean(v).value; }
    });
  } catch (_) {}

  // Filter JSON returned by fetch for player/feed API calls.
  try {
    const originalFetch = window.fetch;
    window.fetch = async function (input, init) {
      const response = await originalFetch.apply(this, arguments);
      try {
        const url = typeof input === 'string' ? input : input?.url || '';
        if (!API.test(url) || !response.ok) return response;
        const result = clean(await response.clone().json());
        if (!result.count) return response;
        const headers = new Headers(response.headers); headers.delete('content-length'); headers.delete('content-encoding');
        return new Response(JSON.stringify(result.value), { status: response.status, statusText: response.statusText, headers });
      } catch (_) { return response; }
    };
  } catch (_) {}

  // Some YouTube code paths use XMLHttpRequest instead of fetch.
  try {
    const XHR = XMLHttpRequest;
    const originalOpen = XHR.prototype.open;
    const originalSend = XHR.prototype.send;
    const originalResponse = Object.getOwnPropertyDescriptor(XHR.prototype, 'response');
    const originalResponseText = Object.getOwnPropertyDescriptor(XHR.prototype, 'responseText');
    const urls = new WeakMap(), cleaned = new WeakMap();
    XHR.prototype.open = function (method, url) { urls.set(this, String(url || '')); return originalOpen.apply(this, arguments); };
    XHR.prototype.send = function () {
      this.addEventListener('readystatechange', () => {
        if (this.readyState !== 4 || !API.test(urls.get(this) || '') || cleaned.has(this)) return;
        try {
          const type = this.responseType;
          if (type !== '' && type !== 'text' && type !== 'json') return;
          let data = type === 'json' ? originalResponse?.get?.call(this) : JSON.parse(originalResponseText?.get?.call(this) || '');
          if (!data || typeof data !== 'object') return;
          const before = JSON.stringify(data);
          const result = clean(data);
          data = result.value;
          if (result.count > 0 && JSON.stringify(data) !== before) cleaned.set(this, { data, text: JSON.stringify(data) });
        } catch (_) {}
      });
      return originalSend.apply(this, arguments);
    };
    if (originalResponse) Object.defineProperty(XHR.prototype, 'response', { configurable: true, enumerable: originalResponse.enumerable, get() { const c = cleaned.get(this); return c ? (this.responseType === 'json' ? c.data : c.text) : originalResponse.get.call(this); } });
    if (originalResponseText) Object.defineProperty(XHR.prototype, 'responseText', { configurable: true, enumerable: originalResponseText.enumerable, get() { const c = cleaned.get(this); if (c) return c.text; return originalResponseText.get.call(this); } });
  } catch (_) {}
})();
