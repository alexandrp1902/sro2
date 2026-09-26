import fs from 'node:fs';
import path from 'node:path';
import {fileURLToPath} from 'node:url';
const root=path.dirname(fileURLToPath(import.meta.url));
const audit=JSON.parse(fs.readFileSync(path.join(root,'audit.json'),'utf8'));
const directions=['N','NE','E','SE','S','SW','W','NW'];
// Measured anatomical attachment locations, fractions of the source frame.
// These are not object bounding-box centers; left/right are the mech's own sides.
const bodyLeft=[[.30,.49],[.32,.43],[.47,.45],[.67,.48],[.71,.49],[.68,.55],[.48,.58],[.33,.54]];
const bodyRight=[[.71,.49],[.66,.55],[.47,.59],[.31,.56],[.30,.49],[.32,.44],[.50,.43],[.65,.43]];
const bodyWaist=[[.50,.68],[.49,.67],[.50,.66],[.51,.65],[.50,.67],[.49,.67],[.50,.67],[.51,.66]];
const hipMount=[[.50,.29],[.50,.29],[.46,.29],[.49,.29],[.50,.29],[.51,.29],[.52,.27],[.51,.29]];
const gunMount=[[.54,.47],[.48,.49],[.38,.44],[.35,.46],[.37,.38],[.56,.39],[.49,.42],[.57,.43]];
const shieldMount=[[.60,.31],[.56,.33],[.41,.34],[.54,.30],[.38,.30],[.44,.29],[.55,.30],[.65,.35]];
const muzzles=[[.70,.20],[.69,.23],[.83,.56],[.58,.77],[.36,.74],[.27,.72],[.18,.48],[.33,.21]];
const ids=['chassis-medium-biped','body-medium','arm-autocannon-right','shield-light-left'];
const rig={version:1,id:'medium-autocannon-shield',status:'validated-static-prototype',animationReady:false,cellSize:512,directions,groundPivot:[256,438],assets:{},frames:{}};
for(const id of ids){const entry=audit.results.find(x=>x.id===id);if(!entry||entry.error)throw Error('Missing audited asset '+id);rig.assets[id]={source:entry.source,frames:entry.frames.map(f=>f.rect)};}
const point=(p,t)=>p.map((v,i)=>256+(v*512-256)*t.scale+(i?t.y:t.x));
for(let i=0;i<8;i++){
 const body={x:0,y:-75,scale:.9,z:2,enabled:true};
 const fit=(mount,target,scale,z)=>{const p=point(target,body);return {x:p[0]-(256+(mount[0]*512-256)*scale),y:p[1]-(256+(mount[1]*512-256)*scale),scale,z,enabled:true};};
 const gunNear=['N','NE','E','SE','S'].includes(directions[i]);
 const shieldNear=['N','S','SW','W','NW'].includes(directions[i]);
 const transforms={
  'chassis-medium-biped':fit(hipMount[i],bodyWaist[i],.65,0),
  'body-medium':body,
  'arm-autocannon-right':fit(gunMount[i],bodyRight[i],.75,gunNear?3:1),
  'shield-light-left':fit(shieldMount[i],bodyLeft[i],.65,shieldNear?4:1)
 };
 rig.frames[directions[i]]={transforms,anchors:{waist:point(bodyWaist[i],body),leftShoulder:point(bodyLeft[i],body),rightShoulder:point(bodyRight[i],body),muzzle:point(muzzles[i],transforms['arm-autocannon-right'])},sourceAnchors:{hip:hipMount[i].map(v=>v*512),gunShoulder:gunMount[i].map(v=>v*512),shieldShoulder:shieldMount[i].map(v=>v*512)}};
}
fs.writeFileSync(path.join(root,'medium-rig.json'),JSON.stringify(rig,null,2)+'\n');
fs.writeFileSync(path.join(root,'rig-data.js'),'window.MECH_RIG = '+JSON.stringify(rig,null,2)+';\n');
console.log('Built eight-direction rig with per-component attachment data.');
