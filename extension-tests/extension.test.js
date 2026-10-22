'use strict';
// Run: node --test extension-tests/extension.test.js
// Tests the generated rules against a small model of Chrome's declarativeNetRequest matching
// (requestDomains suffix match, urlFilter "||host/path" prefix match, resource types). It is a MODEL of Chrome's
// behaviour, not Chrome itself: the real-browser check is tests/Validate.ps1 + docs/MANUAL-TESTS.md.
const test = require('node:test'), assert = require('node:assert/strict');
const fs = require('fs'), path = require('path');
const { build, parseDomains } = require('../tools/build-rules');
const { pruneAds } = require('../extension/lib/prune');
const root = path.join(__dirname, '..');
const rules = JSON.parse(fs.readFileSync(path.join(root, 'extension/rules/ads.json'), 'utf8'));

function matches(rule, urlStr, type) {
  const u = new URL(urlStr), c = rule.condition;
  if (c.resourceTypes && !c.resourceTypes.includes(type)) return false;
  if (c.requestDomains && !c.requestDomains.some(d => u.hostname === d || u.hostname.endsWith('.' + d))) return false;
  if (c.urlFilter) {
    const m = /^\|\|([^\/]+)(\/.*)?$/.exec(c.urlFilter); if (!m) return false;
    const host = m[1], p = m[2] || '/';
    if (!(u.hostname === host || u.hostname.endsWith('.' + host))) return false;
    if (!(u.pathname + u.search).startsWith(p)) return false;
  }
  return true;
}
const blocked = (url, type = 'script') => rules.some(r => r.action.type === 'block' && matches(r, url, type));

test('rule file is structurally valid', () => {
  const ids = rules.map(r => r.id);
  assert.equal(new Set(ids).size, ids.length, 'unique ids');
  assert.ok(rules.length >= 3);
  for (const r of rules) { assert.ok(Number.isInteger(r.id) && r.id > 0 && r.id < 100000); assert.equal(r.action.type, 'block'); }
  assert.deepEqual(JSON.parse(JSON.stringify(build())), rules, 'ads.json is up to date with rules/*.txt (run tools/build-rules.js)');
});

test('ad and tracker requests are blocked', () => {
  assert.ok(blocked('https://pagead2.googlesyndication.com/pagead/js/adsbygoogle.js'));
  assert.ok(blocked('https://securepubads.g.doubleclick.net/gpt/pubads_impl.js'));
  assert.ok(blocked('https://www.google-analytics.com/g/collect', 'ping'));
  assert.ok(blocked('https://www.youtube.com/pagead/adview?x=1', 'image'));
  assert.ok(blocked('https://www.youtube.com/api/stats/ads?ver=2', 'xmlhttprequest'));
  assert.ok(blocked('https://www.youtube.com/get_midroll_info?video_id=x', 'xmlhttprequest'));
  assert.ok(blocked('https://www.youtube.com/ad_data_204?x=1', 'ping'));
  assert.ok(blocked('https://www.youtube.com/pcs/activeview?x=1', 'image'));
});

test('normal sites and YouTube playback are NOT blocked', () => {
  for (const u of ['https://example.com/', 'https://www.youtube.com/watch?v=abc', 'https://www.youtube.com/youtubei/v1/player?key=k',
                   'https://rr3---sn-abc.googlevideo.com/videoplayback?id=1', 'https://i.ytimg.com/vi/x/hq.jpg',
                   'https://www.youtube.com/api/stats/playback?x=1', 'https://www.youtube.com/s/player/base.js', 'https://www.gstatic.com/x.js'])
    assert.equal(blocked(u, 'xmlhttprequest'), false, u);
});

test('top-level navigation is only blocked for popup networks', () => {
  assert.equal(blocked('https://doubleclick.net/', 'main_frame'), false);
  assert.equal(blocked('https://popads.net/x', 'main_frame'), true);
});

test('safety net: critical domains never appear in any blocklist', () => {
  const never = ['googlevideo.com', 'youtube.com', 'google.com', 'ytimg.com', 'gstatic.com', 'googleapis.com', 'microsoft.com', 'windowsupdate.com', 'github.com'];
  for (const f of ['ads-domains.txt', 'popup-domains.txt', 'tracking-domains.txt'])
    for (const d of parseDomains(fs.readFileSync(path.join(root, 'rules', f), 'utf8'))) assert.ok(!never.includes(d), `${f}: ${d}`);
});

test('parser skips comments, accepts hosts and ||domain^, rejects unsupported syntax', () => {
  assert.deepEqual(parseDomains('# c\n0.0.0.0 a.example\n||b.example^\nc.example\n||d.example/path^\nx\n*.e.example'), ['a.example', 'b.example', 'c.example']);
  assert.deepEqual(parseDomains('0.0.0.0 ad.example.com\n||t.example.com^\n||bad.example.com/path\nnosuffix'), ['ad.example.com', 't.example.com']);
});

test('prune: removes player ad fields and feed ad items, keeps content', () => {
  const data = { videoDetails: { videoId: 'x' }, adPlacements: [{ a: 1 }], playerAds: [{}], streamingData: { formats: [1] },
    contents: [{ richItemRenderer: { content: { videoRenderer: { id: 1 } } } },
               { richItemRenderer: { content: { adSlotRenderer: {} } } },
               { adSlotRenderer: {} }, { videoRenderer: { id: 2 } }] };
  const n = pruneAds(data);
  assert.equal(n, 4);
  assert.equal(data.adPlacements, undefined); assert.equal(data.playerAds, undefined);
  assert.equal(data.contents.length, 2); assert.deepEqual(data.streamingData, { formats: [1] });
  assert.equal(pruneAds({ videoDetails: {} }), 0, 'clean data untouched');
  assert.equal(pruneAds(null), 0);
});

test('manifest is valid MV3 and referenced files exist', () => {
  const m = JSON.parse(fs.readFileSync(path.join(root, 'extension/manifest.json'), 'utf8'));
  assert.equal(m.manifest_version, 3);
  const files = [m.background.service_worker, ...m.declarative_net_request.rule_resources.map(r => r.path),
                 ...m.content_scripts.flatMap(c => c.js || [])];
  for (const f of files) assert.ok(fs.existsSync(path.join(root, 'extension', f)), f);
  assert.ok(!m.permissions.includes('webRequest') && !m.permissions.includes('cookies') && !m.permissions.includes('history'));
  assert.ok(!m.host_permissions.some(h => h.includes('<all_urls>') || h.includes('*://*/')), 'no broad host access');
});


test('YouTube ad fallback conceals the ad frame and restores original inline styles', () => {
  const source = fs.readFileSync(path.join(root, 'extension/content/youtube-isolated.js'), 'utf8');
  assert.match(source, /function concealVideo\(video\)/);
  assert.match(source, /visibility', 'hidden', 'important'/);
  assert.match(source, /opacity', '0', 'important'/);
  assert.match(source, /function restoreConcealedVideos\(\)/);
  assert.match(source, /restoreConcealedVideos\(\);/);
});
