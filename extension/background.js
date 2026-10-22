'use strict';
// Service worker. Responsibilities: per-site pause (dynamic allow rules), counters, heartbeat to the local engine.
// Privacy: only three integer counters and the extension version are ever sent, and only to http://127.0.0.1.
const ENGINE = 'http://127.0.0.1:47653/api/ext/heartbeat';
const ALLOW_ID_BASE = 100000;   // dynamic allow rules live at ids >= 100000 so they can't collide with static block ids

async function syncAllowRules() {
  const { allow = [] } = await chrome.storage.local.get('allow');
  const old = await chrome.declarativeNetRequest.getDynamicRules();
  await chrome.declarativeNetRequest.updateDynamicRules({
    removeRuleIds: old.map(r => r.id),
    addRules: allow.map((host, i) => ({
      id: ALLOW_ID_BASE + i, priority: 100, action: { type: 'allowAllRequests' },
      condition: { requestDomains: [host], resourceTypes: ['main_frame', 'sub_frame'] }
    }))
  });
}

async function startHeartbeat() {
  await syncAllowRules();
  chrome.alarms.create('hb', { periodInMinutes: 0.5 }); // 30 seconds on current Chrome versions
  await heartbeat(); // report immediately instead of waiting for the first alarm
}

chrome.runtime.onInstalled.addListener(() => { startHeartbeat(); });
chrome.runtime.onStartup.addListener(() => { startHeartbeat(); });

chrome.action.onClicked.addListener(async tab => {
  let host; try { host = new URL(tab.url).hostname; } catch (_) { return; }
  if (!host) return;
  const { allow = [] } = await chrome.storage.local.get('allow');
  const next = allow.includes(host) ? allow.filter(h => h !== host) : [...allow, host];
  await chrome.storage.local.set({ allow: next });
  await syncAllowRules();
  await chrome.action.setBadgeText({ tabId: tab.id, text: next.includes(host) ? 'OFF' : '' });
  chrome.tabs.reload(tab.id);
});

chrome.runtime.onMessage.addListener(msg => {
  if (!msg || !Number.isFinite(msg.n)) return;
  const key = msg.type === 'yt-pruned' ? 'ytPruned' : msg.type === 'yt-accelerated' ? 'ytAccelerated' : null;
  if (!key) return;
  chrome.storage.local.get(key).then(o => chrome.storage.local.set({ [key]: (o[key] || 0) + msg.n }));
});

async function heartbeat() {
  const s = await chrome.storage.local.get(['lastPoll', 'ytPruned', 'ytAccelerated']);
  const since = s.lastPoll || Date.now() - 60000, now = Date.now();
  let matches = 0;
  try {
    const r = await chrome.declarativeNetRequest.getMatchedRules({ minTimeStamp: since });
    matches = r.rulesMatchedInfo.filter(m => m.rule.ruleId < ALLOW_ID_BASE).length;
  } catch (_) { /* Feedback may be unavailable; never invent a count. */ }
  try {
    const response = await fetch(ENGINE, { method: 'POST', headers: { 'Content-Type': 'text/plain' },
      body: JSON.stringify({ version: chrome.runtime.getManifest().version, ruleMatches: matches, ytPruned: s.ytPruned || 0, ytAccelerated: s.ytAccelerated || 0 }) });
    if (!response.ok) return; // keep counters if the engine rejected the report
    await chrome.storage.local.set({ lastPoll: now, ytPruned: 0, ytAccelerated: 0 });
  } catch (_) { /* engine not running: keep counters for the next heartbeat */ }
}

chrome.alarms.onAlarm.addListener(a => { if (a.name === 'hb') heartbeat(); });
