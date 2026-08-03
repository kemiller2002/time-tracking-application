const CACHE='echelon-shell-v3'; const ASSETS=['/','/index.html','/src/styles.css','/src/accessibility-fixes.css','/src/app.js','/src/domain.js','/src/state.js'];
self.addEventListener('install',event=>event.waitUntil(caches.open(CACHE).then(cache=>cache.addAll(ASSETS))));
self.addEventListener('activate',event=>event.waitUntil(self.clients.claim()));
self.addEventListener('fetch',event=>{const url=new URL(event.request.url);if(url.pathname.startsWith('/api/')||url.pathname.startsWith('/service/'))return;if(event.request.method==='GET') event.respondWith(fetch(event.request).then(response=>{const copy=response.clone();caches.open(CACHE).then(cache=>cache.put(event.request,copy));return response;}).catch(()=>caches.match(event.request)));});
