// Forwards messages from Twitch tabs to the Prediction Bot app on this machine.
// Fails silently when the app isn't running.
const APP = 'http://127.0.0.1:47800';

const send = (type, body) =>
  fetch(`${APP}/${type}`, { method: 'POST', body: JSON.stringify(body) }).catch(() => {});

browser.runtime.onMessage.addListener((message) => {
  if (message?.type === 'ingest' || message?.type === 'ping') send(message.type, message);
});

// Heartbeat so the app can show which channels are hooked. It is driven from here rather than
// from a timer inside the tab, because Firefox slows or pauses timers in tabs that aren't in
// front. Asking the tab directly also proves the hook in it is still alive.
async function heartbeat() {
  const v = browser.runtime.getManifest().version;
  const tabs = await browser.tabs.query({ url: '*://*.twitch.tv/*', discarded: false });
  for (const tab of tabs) {
    try {
      const reply = await browser.tabs.sendMessage(tab.id, { type: 'status' });
      if (reply?.channel) send('ping', { type: 'ping', channel: reply.channel, via: 'alarm', v });
    } catch (e) {
      // Tab has no hook in it (opened before the extension loaded): reload it to hook it.
      send('ping', { type: 'ping', channel: '', via: 'alarm', v, detail: `no hook in ${tab.url}: ${e.message}` });
    }
  }
}

// browser.alarms only exists once Firefox has re-read the manifest (Reload in about:debugging).
// Until then the tab's own socket heartbeat is the only one, so don't fall over without it.
if (browser.alarms) {
  browser.alarms.onAlarm.addListener(heartbeat);
  // This script restarts every time a tab sends something; only create the alarm if it's missing,
  // otherwise each restart would reset the countdown and it might never fire.
  browser.alarms.get('heartbeat').then((alarm) => {
    if (!alarm) browser.alarms.create('heartbeat', { periodInMinutes: 0.5 });
  });
}
heartbeat();
