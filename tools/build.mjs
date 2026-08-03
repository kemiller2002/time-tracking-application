import { access, mkdir, cp, rm } from 'node:fs/promises';
const required=['index.html','manifest.webmanifest','service-worker.js','src/app.js','src/api/client.js','src/styles.css'];
await Promise.all(required.map(file=>access(file)));
await rm('dist',{recursive:true,force:true});
await mkdir('dist',{recursive:true});
for(const file of ['index.html','manifest.webmanifest','service-worker.js','src']) await cp(file,`dist/${file}`,{recursive:true});
console.log('Production assets copied to dist/');
