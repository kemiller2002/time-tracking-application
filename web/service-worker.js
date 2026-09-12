// SDE Tier 4 (Host / External Effects): keeps the app shell loadable without
// a network connection. Owns no business meaning — it never inspects a
// request's body, only its method and origin, so GitHub sync's own network
// calls (POST/PUT/PATCH to api.github.com) pass straight through untouched.
//
// Strategy is deliberately "grow the cache from real traffic" rather than a
// hardcoded precache list for the WASM runtime: `wasm-engine-transport.js`
// dynamically imports `dotnet.js`, which in turn fetches its own supporting
// files (the native runtime, managed assemblies, a boot config) under
// `_framework/` — exact filenames depend on the current `dotnet publish`
// output and aren't this file's business to know. The first online visit
// after a deploy populates the cache from those real fetches; every visit
// after that, including fully offline ones, is served from it.

const CACHE_NAME = "ledger-shell-v1";

// The small, stable set of files worth precaching explicitly — enough for
// the shell to render and start loading the WASM runtime even on a first
// visit that happens to be offline (unlikely, but free to cover).
const SHELL_FILES = ["./", "./index.html", "./styles.css", "./main.js", "./wasm-engine-transport.js", "./dom-bindings.js", "./manifest.webmanifest", "./icon.svg"];

self.addEventListener("install", (event) => {
  event.waitUntil(caches.open(CACHE_NAME).then((cache) => cache.addAll(SHELL_FILES)).then(() => self.skipWaiting()));
});

self.addEventListener("activate", (event) => {
  event.waitUntil(
    caches
      .keys()
      .then((names) => Promise.all(names.filter((name) => name !== CACHE_NAME).map((name) => caches.delete(name))))
      .then(() => self.clients.claim()),
  );
});

self.addEventListener("fetch", (event) => {
  const request = event.request;
  if (request.method !== "GET") return; // never intercept a write — GitHub sync's PUT/POST/PATCH pass straight through
  if (new URL(request.url).origin !== self.location.origin) return; // never cache/intercept api.github.com

  event.respondWith(
    caches.match(request).then((cached) => {
      if (cached) return cached;
      return fetch(request)
        .then((response) => {
          if (response.ok) {
            const toCache = response.clone();
            caches.open(CACHE_NAME).then((cache) => cache.put(request, toCache));
          }
          return response;
        })
        .catch(() => {
          // Offline with nothing cached for this exact request — for a page
          // navigation, the shell itself is the best fallback available.
          if (request.mode === "navigate") return caches.match("./index.html");
          return Response.error();
        });
    }),
  );
});
