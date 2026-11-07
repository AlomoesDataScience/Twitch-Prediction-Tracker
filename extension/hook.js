// Runs inside the Twitch page itself (MAIN world), before Twitch's own scripts.
// It listens in on the data the page already receives -- the realtime websocket and
// GraphQL responses -- and hands anything that mentions a prediction to relay.js.
// Read-only: nothing is sent to Twitch and nothing on the page is changed.
(() => {
  const post = (message) => window.postMessage({ source: 'prediction-bot', ...message }, location.origin);
  const interesting = (text) =>
    typeof text === 'string' && text.includes('outcomes') && /prediction/i.test(text);

  // Any traffic on Twitch's socket proves the hook is alive. Unlike a timer, incoming network
  // messages still arrive when the tab is in the background or Firefox is minimized.
  let lastBeat = 0;
  const beat = () => {
    if (Date.now() - lastBeat < 15000) return;
    lastBeat = Date.now();
    post({ beat: true });
  };

  const NativeWebSocket = window.WebSocket;
  window.WebSocket = new Proxy(NativeWebSocket, {
    construct(target, args) {
      const socket = new target(...args);
      socket.addEventListener('message', (e) => {
        beat();
        if (interesting(e.data)) post({ raw: e.data, via: 'socket' });
      });
      return socket;
    },
  });

  const nativeFetch = window.fetch;
  window.fetch = function (...args) {
    const response = nativeFetch.apply(this, args);
    const url = String(typeof args[0] === 'string' ? args[0] : args[0]?.url ?? '');
    if (url.includes('gql.twitch.tv')) {
      response
        .then((r) => r.clone().text())
        .then((text) => { if (interesting(text)) post({ raw: text, via: 'gql' }); })
        .catch(() => {});
    }
    return response;
  };
})();
