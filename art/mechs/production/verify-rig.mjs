import fs from 'node:fs';
import path from 'node:path';
import {fileURLToPath} from 'node:url';
import assert from 'node:assert/strict';
const root=path.dirname(fileURLToPath(import.meta.url));
const read=name=>JSON.parse(fs.readFileSync(path.join(root,name),'utf8'));
const rig=read('medium-rig.json');
const scenarios=Object.fromEntries(['healthy','nogun','noshield','noarms'].map(s=>[s,read('browser-pixels-'+s+'.json')]));
for(const [name,frames] of Object.entries(scenarios)){
 assert.deepEqual(frames.map(f=>f.direction),rig.directions,name+': missing direction');
 for(const f of frames){assert(f.count>1000,name+': empty sprite');assert(f.bounds[0]>0&&f.bounds[1]>0&&f.bounds[2]<299&&f.bounds[3]<299,name+': clipped sprite');}
}
for(let i=0;i<8;i++){
 const h=scenarios.healthy[i].count,g=scenarios.nogun[i].count,s=scenarios.noshield[i].count,b=scenarios.noarms[i].count;
 assert(g<h&&s<h&&b<g&&b<s,rig.directions[i]+': destroyed component still visible');
}
for(const a of Object.values(rig.assets))assert(fs.existsSync(path.join(root,a.source)),'Missing PNG');
console.log('PASS: 32 rendered states, eight headings, no clipping, both arms independently removable.');
