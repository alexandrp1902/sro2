'use strict';
const directions=['N','NE','E','SE','S','SW','W','NW'];
const ids=['chassis-medium-biped','body-medium','arm-autocannon-right','shield-light-left'];
const labels=['Шасси','Корпус','Правая автопушка','Левый щит'];
const $=id=>document.getElementById(id);
const sourceEntries=window.MECH_REVIEW?.results??[];
const images=new Map(),entries=new Map(sourceEntries.filter(e=>!e.error).map(e=>[e.id,e]));
// Namespace by source filenames so replacement images do not inherit stale calibration.
const storageKey='sro-mech-calibration-v2:'+JSON.stringify(window.MECH_RIG);
const defaults=d=>structuredClone(window.MECH_RIG.frames[d].transforms);
const settings=Object.fromEntries(directions.map(d=>[d,defaults(d)]));
try{const saved=JSON.parse(localStorage.getItem(storageKey));for(const d of directions)for(const id of ids){const s=saved?.[d]?.[id];if(s&&Number.isFinite(s.x)&&Math.abs(s.x)<=512&&Number.isFinite(s.y)&&Math.abs(s.y)<=512&&Number.isFinite(s.scale)&&s.scale>=0.05&&s.scale<=3&&Number.isFinite(s.z)&&Math.abs(s.z)<=10&&typeof s.enabled==='boolean')settings[d][id]=s;}}catch{}
for(const d of directions){const o=document.createElement('option');o.value=o.textContent=d;$('direction').append(o);}
for(let i=0;i<ids.length;i++){const o=document.createElement('option');o.value=ids[i];o.textContent=labels[i];$('layer').append(o);}
function save(){try{localStorage.setItem(storageKey,JSON.stringify(settings));}catch{}}
function controls(){const s=settings[$('direction').value][$('layer').value];for(const key of ['x','y','scale','z'])$(key).value=s[key];$('enabled').checked=s.enabled;draw();}
function sprite(ctx,id,index,s={x:0,y:0,scale:1}){
 const entry=entries.get(id),img=images.get(id);if(!entry||!img)return;
 const rect=entry.frames[index].rect,edge=512*s.scale;
 ctx.drawImage(img,...rect,256-edge/2+s.x,256-edge/2+s.y,edge,edge);
}
function draw(){
 const index=directions.indexOf($('direction').value),layers=settings[directions[index]],ctx=$('assembly').getContext('2d'),ref=$('reference').getContext('2d');
 ctx.clearRect(0,0,512,512);ref.clearRect(0,0,512,512);
 for(const id of [...ids].sort((a,b)=>layers[a].z-layers[b].z))if(layers[id].enabled)sprite(ctx,id,index,layers[id]);
 sprite(ref,'reference-medium',index);
 // Small-size previews exclude diagnostic overlays.
 for(const canvas of document.querySelectorAll('.mini')){const c=canvas.getContext('2d');c.clearRect(0,0,canvas.width,canvas.height);c.imageSmoothingEnabled=true;c.imageSmoothingQuality='high';c.drawImage($('assembly'),0,0,canvas.width,canvas.height);}
 if($('ghost').checked){ctx.save();ctx.globalAlpha=0.35;sprite(ctx,'reference-medium',index);ctx.restore();}
 if($('guides').checked){ctx.strokeStyle='#d9c08a';ctx.lineWidth=1;ctx.setLineDash([4,4]);ctx.beginPath();ctx.moveTo(256,0);ctx.lineTo(256,512);ctx.moveTo(0,window.MECH_RIG.groundPivot[1]);ctx.lineTo(512,window.MECH_RIG.groundPivot[1]);ctx.stroke();ctx.setLineDash([]);}
}
for(const id of ['direction','layer'])$(id).onchange=controls;
for(const id of ['ghost','guides'])$(id).onchange=draw;
for(const id of ['x','y','scale','z','enabled'])$(id).oninput=()=>{
 const s=settings[$('direction').value][$('layer').value];
 if(id==='enabled')s.enabled=$(id).checked;else{const value=Number($(id).value);if(!Number.isFinite(value)||!$(id).checkValidity())return;s[id]=value;}
 save();draw();
};
$('reset').onclick=()=>{settings[$('direction').value]=defaults($('direction').value);save();controls();};
$('export').onclick=()=>{
 const data={version:1,status:'candidate-not-accepted',cellSize:512,sourceFiles:Object.fromEntries(ids.map(id=>[id,entries.get(id)?.source??null])),transforms:settings};
 const url=URL.createObjectURL(new Blob([JSON.stringify(data,null,2)+'\n'],{type:'application/json'})),link=document.createElement('a');link.href=url;link.download='mech-medium-calibration-candidate.json';link.click();setTimeout(()=>URL.revokeObjectURL(url),1000);
};
(async()=>{
 const errors=[];
 await Promise.all([...ids,'reference-medium'].map(async id=>{const entry=entries.get(id);if(!entry){errors.push(id+': источник отсутствует');return;}const img=new Image();img.src=entry.source;try{await img.decode();images.set(id,img);}catch{errors.push(id+': PNG не загрузился');}}));
 $('error').textContent=errors.join('; ');controls();document.body.dataset.ready='true';
})();
