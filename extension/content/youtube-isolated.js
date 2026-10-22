// YouTube fallback: detect ad breaks, press native skip controls immediately,
// call the player's skip hook when exposed, and accelerate/seek only while the
// player explicitly reports an ad. Normal video speed/audio are restored after the break.
// This is best-effort: YouTube changes its player frequently.
(function () {
  'use strict';
  if (window.__adBlockerYTInstalled) return;
  window.__adBlockerYTInstalled = true;

  const send = (type, n) => { try { chrome.runtime.sendMessage({ type, n }); } catch (_) {} };
  window.addEventListener('message', ev => {
    if (ev.source === window && ev.data?.source === 'adblocker' && ev.data.type === 'pruned' && Number.isFinite(ev.data.n))
      send('yt-pruned', Math.min(ev.data.n, 100));
  });

  let enabled = true;
  chrome.storage.local.get(['allow'], ({ allow = [] }) => {
    if (allow.includes(location.hostname)) { enabled = false; return; }
    start();
  });

  let adActive = false, savedMuted = false, savedRate = 1, lastSkip = 0, lastSeek = 0;
  // Keep original inline styles so the actual video is restored exactly after an ad break.
  const concealedVideos = new Map();
  const SKIP = [
    '.ytp-skip-ad-button', '.ytp-ad-skip-button', '.ytp-ad-skip-button-modern',
    '.ytp-skip-ad button', 'button.ytp-ad-skip-button-modern',
    '.ytp-ad-skip-button-container button'
  ].join(',');
  const AD_UI = [
    'ytd-ad-slot-renderer', 'ytd-in-feed-ad-layout-renderer', 'ytd-display-ad-renderer',
    'ytd-promoted-sparkles-web-renderer', 'ytd-promoted-video-renderer',
    'ytd-compact-promoted-video-renderer', 'ytd-banner-promo-renderer',
    'ytd-statement-banner-renderer', 'ytd-companion-slot-renderer',
    '#masthead-ad', '#player-ads', '.ytp-ad-overlay-container', '.ytp-ad-overlay-slot',
    '.ytp-ad-image-overlay', '.ytp-ad-text-overlay', '.ytp-ad-message-container',
    '.ytp-ad-player-overlay', '.ytp-ad-preview-container',
    'ytd-player-legacy-desktop-watch-ads-renderer',
    'ytd-engagement-panel-section-list-renderer[target-id="engagement-panel-ads"]'
  ].join(',');

  function hideAdCards(root = document) {
    try {
      if (root.nodeType === 1 && root.matches?.(AD_UI)) hideOne(root);
      root.querySelectorAll?.(AD_UI).forEach(hideOne);
    } catch (_) {}
  }
  function hideOne(el) {
    if (!el.dataset.samAdHandled) {
      el.dataset.samAdHandled = '1';
      el.style.setProperty('display', 'none', 'important');
    }
  }

  function concealVideo(video) {
    if (!concealedVideos.has(video)) {
      concealedVideos.set(video, {
        visibility: video.style.getPropertyValue('visibility'),
        visibilityPriority: video.style.getPropertyPriority('visibility'),
        opacity: video.style.getPropertyValue('opacity'),
        opacityPriority: video.style.getPropertyPriority('opacity')
      });
    }
    // Hide the ad frame immediately; the player may still need to run so native skip works.
    video.style.setProperty('visibility', 'hidden', 'important');
    video.style.setProperty('opacity', '0', 'important');
  }

  function restoreConcealedVideos() {
    for (const [video, saved] of concealedVideos) {
      try {
        if (saved.visibility) video.style.setProperty('visibility', saved.visibility, saved.visibilityPriority);
        else video.style.removeProperty('visibility');
        if (saved.opacity) video.style.setProperty('opacity', saved.opacity, saved.opacityPriority);
        else video.style.removeProperty('opacity');
      } catch (_) {}
    }
    concealedVideos.clear();
  }

  function start() {
    hideAdCards();
    const observer = new MutationObserver(records => {
      for (const r of records) {
        if (r.type === 'attributes') { hideAdCards(r.target); continue; }
        for (const node of r.addedNodes) if (node.nodeType === 1) hideAdCards(node);
      }
    });
    observer.observe(document.documentElement, { childList: true, subtree: true, attributes: true, attributeFilter: ['class'] });
    setInterval(tick, 120);
  }

  function isAdShowing(player) {
    if (!player) return false;
    try {
      const state = typeof player.getAdState === 'function' ? player.getAdState() : 0;
      if (state && state !== 0) return true;
    } catch (_) {}
    return player.classList.contains('ad-showing') || player.classList.contains('ad-interrupting') ||
      !!player.querySelector('.ytp-ad-player-overlay, .ytp-ad-preview-container, .ytp-ad-skip-button-container, .ytp-ad-text-overlay');
  }

  function tick() {
    if (!enabled) return;
    hideAdCards();
    const player = document.querySelector('.html5-video-player');
    const video = player?.querySelector('video.html5-main-video') || document.querySelector('video.html5-main-video');
    if (!player || !video) return;
    const showing = isAdShowing(player);
    if (!showing) {
      if (adActive) {
        adActive = false;
        try { video.muted = savedMuted; } catch (_) {}
        try { video.playbackRate = savedRate; } catch (_) {}
      }
      restoreConcealedVideos();
      return;
    }

    if (!adActive) {
      adActive = true;
      savedMuted = video.muted;
      savedRate = video.playbackRate || 1;
      send('yt-accelerated', 1);
    }

    const now = Date.now();
    const skip = player.querySelector(SKIP);
    if (skip && !skip.disabled && now - lastSkip > 250) {
      lastSkip = now;
      try { skip.click(); } catch (_) {}
    }
    // Some player builds expose a native skip method. Only call it after the
    // player itself reports an ad, never during ordinary content playback.
    if (now - lastSkip > 350) {
      try {
        if (typeof player.skipAd === 'function') { lastSkip = now; player.skipAd(); }
      } catch (_) {}
    }
    try { video.muted = true; } catch (_) {}
    concealVideo(video);
    try { if (video.playbackRate < 16) video.playbackRate = 16; } catch (_) {}
    // Seeking is throttled to avoid hammering the media pipeline; YouTube may reject it.
    if (now - lastSeek > 350) {
      lastSeek = now;
      try {
        if (Number.isFinite(video.duration) && video.duration > 0 && Number.isFinite(video.currentTime) && video.currentTime < video.duration - 0.12)
          video.currentTime = Math.max(video.currentTime, video.duration - 0.08);
      } catch (_) {}
    }
  }
})();
