// Cosmetic filters hide known ad containers. This is separate from network request blocking.
(function () {
  'use strict';
  const GENERIC = [
    'ins.adsbygoogle', '.adsbygoogle', 'iframe[src*="doubleclick.net"]',
    'iframe[src*="googlesyndication.com"]', 'div[id^="google_ads_iframe"]',
    'div[id^="div-gpt-ad"]', 'amp-ad', 'amp-embed[type="taboola"]',
    'amp-embed[type="outbrain"]', '[id^="taboola-"]', '.OUTBRAIN', '[data-ad-slot]'
  ];
  const YOUTUBE = [
    'ytd-ad-slot-renderer', 'ytd-in-feed-ad-layout-renderer', 'ytd-display-ad-renderer',
    'ytd-promoted-sparkles-web-renderer', 'ytd-promoted-video-renderer',
    'ytd-compact-promoted-video-renderer', 'ytd-banner-promo-renderer',
    'ytd-statement-banner-renderer', 'ytd-companion-slot-renderer', '#masthead-ad',
    '#player-ads', 'ytd-player-legacy-desktop-watch-ads-renderer',
    '.ytp-ad-overlay-container', '.ytp-ad-overlay-slot', '.ytp-ad-image-overlay',
    '.ytp-ad-text-overlay', '.ytp-ad-message-container',
    'ytd-engagement-panel-section-list-renderer[target-id="engagement-panel-ads"]',
    'ytd-merch-shelf-renderer', 'ytd-action-companion-ad-renderer'
  ];
  const isYT = /(^|\.)youtube\.com$/.test(location.hostname);
  chrome.storage.local.get(['allow'], ({ allow = [] }) => {
    if (allow.includes(location.hostname)) return;
    const css = (isYT ? GENERIC.concat(YOUTUBE) : GENERIC).join(',\n') + ' { display: none !important; }';
    const install = () => {
      const target = document.head || document.documentElement;
      if (!target || document.getElementById('adblocker-cosmetic-style')) return;
      const style = document.createElement('style');
      style.id = 'adblocker-cosmetic-style';
      style.textContent = css;
      target.appendChild(style);
    };
    install();
    if (!document.head) document.addEventListener('DOMContentLoaded', install, { once: true });
  });
})();
