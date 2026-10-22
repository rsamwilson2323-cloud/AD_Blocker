// Removes advertising structures from YouTube JSON (player / next / browse / search responses).
// Pure function, shared by the page-world script and the Node tests. Returns how many ad nodes were removed.
(function (root) {
  'use strict';
  const AD_FIELDS = ['adPlacements', 'playerAds', 'adSlots', 'adBreakHeartbeatParams'];
  const AD_ITEMS = ['adSlotRenderer', 'displayAdRenderer', 'promotedSparklesWebRenderer', 'promotedVideoRenderer',
                    'inFeedAdLayoutRenderer', 'bannerPromoRenderer', 'statementBannerRenderer', 'compactPromotedVideoRenderer'];
  const isAdItem = o => o && typeof o === 'object' && !Array.isArray(o) &&
    (AD_ITEMS.some(k => k in o) ||
     (o.richItemRenderer && o.richItemRenderer.content && AD_ITEMS.some(k => k in o.richItemRenderer.content)));

  function pruneAds(node, depth = 0) {
    if (!node || typeof node !== 'object' || depth > 60) return 0;
    let n = 0;
    if (Array.isArray(node)) {
      for (let i = node.length - 1; i >= 0; i--) { if (isAdItem(node[i])) { node.splice(i, 1); n++; } }
      for (const v of node) n += pruneAds(v, depth + 1);
      return n;
    }
    for (const k of AD_FIELDS) if (k in node) { delete node[k]; n++; }
    for (const k of Object.keys(node)) n += pruneAds(node[k], depth + 1);
    return n;
  }
  const api = { pruneAds };
  if (typeof module !== 'undefined' && module.exports) module.exports = api; else root.AdPrune = api;
})(typeof globalThis !== 'undefined' ? globalThis : this);
