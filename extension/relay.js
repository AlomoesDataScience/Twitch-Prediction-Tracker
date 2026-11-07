// Bridges hook.js (page world) to the extension background, which talks to the local app.
const NOT_CHANNELS = new Set(['popout', 'moderator', 'embed', 'u']);
const RESERVED = new Set(['directory', 'videos', 'settings', 'subscriptions', 'inventory', 'drops',
  'wallet', 'search', 'downloads', 'turbo', 'jobs', 'p', 'privacy', 'store', 'friends', 'following']);

function channel() {
  const parts = location.pathname.split('/').filter(Boolean);
  const name = (NOT_CHANNELS.has(parts[0]) ? parts[1] : parts[0]) ?? '';
  return RESERVED.has(name) ? '' : name.toLowerCase();
}

const version = browser.runtime.getManifest().version;
const post = (message) =>
  browser.runtime.sendMessage({ ...message, channel: channel(), v: version }).catch(() => {});
const ping = (via) => { if (channel()) post({ type: 'ping', via }); };

window.addEventListener('message', (e) => {
  if (e.source !== window || e.data?.source !== 'prediction-bot') return;
  if (e.data.beat) ping('socket');
  else post({ type: 'ingest', via: e.data.via, raw: e.data.raw });
});

// The background script asks each Twitch tab which channel it is on (see heartbeat there).
browser.runtime.onMessage.addListener((message) =>
  message?.type === 'status' ? Promise.resolve({ channel: channel() }) : undefined);

// Also announce straight away on load and whenever the tab is hidden or shown.
ping('load');
document.addEventListener('visibilitychange', () => ping(document.visibilityState));
