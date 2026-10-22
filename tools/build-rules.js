#!/usr/bin/env node
// Generates extension/rules/ads.json (Chrome declarativeNetRequest static rules) from rules/*.txt.
// Same line syntax as the Windows engine: plain domain, hosts format, or ||domain^.
'use strict';
const fs = require('fs'), path = require('path');
const root = path.join(__dirname, '..');
const LABEL = /^[a-z0-9_]([a-z0-9_-]{0,61}[a-z0-9_])?$/;

function parseDomains(text) {
  const out = [];
  for (let raw of text.split(/\r?\n/)) {
    let l = raw.trim();
    if (!l || l[0] === '#' || l[0] === '!') continue;
    l = l.replace(/\s+#.*$/, '');
    if (l.startsWith('||')) { l = l.slice(2).replace(/\^$/, ''); if (/[\/*$^|:?@#=]/.test(l)) continue; }
    else { const p = l.split(/\s+/); if (p.length === 2 && ['0.0.0.0', '127.0.0.1'].includes(p[0])) l = p[1]; else if (p.length !== 1) continue; else l = p[0]; }
    l = l.replace(/\.$/, '').toLowerCase();
    const labels = l.split('.');
    if (labels.length >= 2 && l.length <= 253 && labels.every(x => LABEL.test(x))) out.push(l);
  }
  return [...new Set(out)];
}

function chunk(a, n) { const r = []; for (let i = 0; i < a.length; i += n) r.push(a.slice(i, i + n)); return r; }
const read = f => fs.existsSync(path.join(root, 'rules', f)) ? fs.readFileSync(path.join(root, 'rules', f), 'utf8') : '';

function build() {
  const popup = parseDomains(read('popup-domains.txt'));
  const popupSet = new Set(popup);
  const other = [...parseDomains(read('ads-domains.txt')), ...parseDomains(read('tracking-domains.txt'))].filter(d => !popupSet.has(d));
  const nonMain = ['sub_frame', 'script', 'image', 'stylesheet', 'font', 'object', 'xmlhttprequest', 'ping', 'media', 'websocket', 'other'];
  const rules = []; let id = 1;
  for (const c of chunk(other, 100)) rules.push({ id: id++, priority: 1, action: { type: 'block' }, condition: { requestDomains: c, resourceTypes: nonMain } });
  for (const c of chunk(popup, 100)) rules.push({ id: id++, priority: 1, action: { type: 'block' }, condition: { requestDomains: c, resourceTypes: ['main_frame', ...nonMain] } });
  // EXPERIMENTAL YouTube ad endpoints. Deliberately narrow: playback, stats/playback and videoplayback are NOT matched.
  for (const f of ['||youtube.com/pagead/', '||youtube.com/api/stats/ads', '||youtube.com/get_midroll_info', '||youtube.com/ad_data_204', '||youtube.com/pcs/activeview'])
    rules.push({ id: id++, priority: 1, action: { type: 'block' }, condition: { urlFilter: f, resourceTypes: ['xmlhttprequest', 'image', 'script', 'ping', 'sub_frame', 'other'] } });
  return rules;
}

if (require.main === module) {
  const rules = build();
  fs.writeFileSync(path.join(root, 'extension', 'rules', 'ads.json'), JSON.stringify(rules, null, 1));
  console.log(`wrote ${rules.length} rules`);
}
module.exports = { build, parseDomains };
