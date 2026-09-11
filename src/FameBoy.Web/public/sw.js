// Simple PWA service worker: network-first with cache fallback, so new
// deployments show up immediately but the emulator (and the bundled demo
// ROM) still open offline. Uploaded ROMs and .sav data never touch the
// cache: ROMs stay in memory and saves live in localStorage.
const CACHE = 'fameboy-v1';
const PRECACHE = ['./', './index.html', './site.webmanifest', './android-chrome-192x192.png', './android-chrome-512x512.png', './tobudx.gb', './PressStart2P-Regular.ttf'];

self.addEventListener('install', (e) => {
  e.waitUntil(caches.open(CACHE).then((c) => c.addAll(PRECACHE)).then(() => self.skipWaiting()));
});

self.addEventListener('activate', (e) => {
  e.waitUntil(
    caches.keys()
      .then((keys) => Promise.all(keys.filter((k) => k !== CACHE).map((k) => caches.delete(k))))
      .then(() => self.clients.claim())
  );
});

self.addEventListener('fetch', (e) => {
  const req = e.request;
  if (req.method !== 'GET' || new URL(req.url).origin !== location.origin) return;
  e.respondWith(
    fetch(req)
      .then((res) => {
        const copy = res.clone();
        caches.open(CACHE).then((c) => c.put(req, copy));
        return res;
      })
      .catch(() =>
        caches.match(req).then((hit) => {
          if (hit) return hit;
          if (req.mode === 'navigate') return caches.match('./index.html');
          return Response.error();
        }))
  );
});
