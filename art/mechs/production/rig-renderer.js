'use strict';
window.MechRigRenderer={
 async load(rig){
  const images={};
  await Promise.all(Object.entries(rig.assets).map(async([id,asset])=>{const img=new Image();img.src=asset.source;await img.decode();images[id]=img;}));
  return {rig,images};
 },
 draw(ctx,loaded,direction,{x=0,y=0,size=512,hidden=[],guides=false}={}){
  const {rig,images}=loaded,index=rig.directions.indexOf(direction);
  if(index<0)throw Error('Unknown direction '+direction);
  const frame=rig.frames[direction];ctx.save();ctx.translate(x,y);ctx.scale(size/rig.cellSize,size/rig.cellSize);
  for(const [id,t] of Object.entries(frame.transforms).sort((a,b)=>a[1].z-b[1].z)){
   if(!t.enabled||hidden.includes(id))continue;
   const edge=rig.cellSize*t.scale;
   ctx.drawImage(images[id],...rig.assets[id].frames[index],256-edge/2+t.x,256-edge/2+t.y,edge,edge);
  }
  if(guides){for(const [name,p] of Object.entries(frame.anchors)){ctx.fillStyle=name==='muzzle'?'#e0524a':'#d9c08a';ctx.beginPath();ctx.arc(p[0],p[1],4,0,Math.PI*2);ctx.fill();}ctx.strokeStyle='#6f8aa6';ctx.beginPath();ctx.arc(...rig.groundPivot,8,0,Math.PI*2);ctx.stroke();}
  ctx.restore();
 }
};
