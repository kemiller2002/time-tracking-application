import { readFile } from 'node:fs/promises';
const files=['schemas/domain/config.schema.json','schemas/domain/project.schema.json','schemas/domain/activity-type.schema.json'];
for(const file of files){const schema=JSON.parse(await readFile(file,'utf8'));if(schema.$schema!=='https://json-schema.org/draft/2020-12/schema'||schema.type!=='object')throw new Error(`${file}: unsupported schema root`);}
console.log(`${files.length} domain schemas parsed and passed baseline structural validation`);
