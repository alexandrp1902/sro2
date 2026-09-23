// Read-only image checks: no image pixels are changed.
import fs from 'node:fs';
import path from 'node:path';
import zlib from 'node:zlib';
import {fileURLToPath} from 'node:url';
const root=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'..');
const manifest=JSON.parse(fs.readFileSync(path.join(root,'art/next-art-manifest.json'),'utf8').replace(/^\uFEFF/,''));
const paeth=(a,b,c)=>{const p=a+b-c,x=Math.abs(p-a),y=Math.abs(p-b),z=Math.abs(p-c);return x<=y&&x<=z?a:y<=z?b:c;};
const results=[];
for(const item of manifest.filter(a=>a.dir==='art/space/sun-corona'||a.dir==='art/mechs/tiles'&&!a.alpha)){
  const file=`${item.dir}/${item.name}.png`,full=path.join(root,file);
  if(!fs.existsSync(full))continue;
  const b=fs.readFileSync(full),w=b.readUInt32BE(16),h=b.readUInt32BE(20),channels=b[25]===6?4:3;
  if(b[24]!==8||![2,6].includes(b[25])||b[28]!==0)throw Error(`Unsupported PNG: ${file}`);
  const chunks=[];
  for(let p=8;p<b.length;){const n=b.readUInt32BE(p);if(b.toString('ascii',p+4,p+8)==='IDAT')chunks.push(b.subarray(p+8,p+8+n));p+=n+12;}
  const raw=zlib.inflateSync(Buffer.concat(chunks)),stride=w*channels,pixels=Buffer.alloc(stride*h);
  let offset=0;
  for(let y=0;y<h;y++){
    const filter=raw[offset++];
    for(let x=0;x<stride;x++){
      const k=y*stride+x,a=x>=channels?pixels[k-channels]:0,up=y?pixels[k-stride]:0,c=y&&x>=channels?pixels[k-stride-channels]:0;
      pixels[k]=(raw[offset++]+(filter===0?0:filter===1?a:filter===2?up:filter===3?Math.floor((a+up)/2):paeth(a,up,c)))&255;
    }
  }
  const at=(x,y,c)=>pixels[(y*w+x)*channels+c];
  let horizontalSeam=0,verticalSeam=0,interiorX=0,interiorY=0;
  for(let y=0;y<h;y++)for(let c=0;c<3;c++){horizontalSeam+=Math.abs(at(0,y,c)-at(w-1,y,c));interiorX+=Math.abs(at(Math.floor(w/2),y,c)-at(Math.floor(w/2)-1,y,c));}
  for(let x=0;x<w;x++)for(let c=0;c<3;c++){verticalSeam+=Math.abs(at(x,0,c)-at(x,h-1,c));interiorY+=Math.abs(at(x,Math.floor(h/2),c)-at(x,Math.floor(h/2)-1,c));}
  const result={file,horizontalSeamMAE:horizontalSeam/(h*3),verticalSeamMAE:verticalSeam/(w*3),interiorHorizontalMAE:interiorX/(h*3),interiorVerticalMAE:interiorY/(w*3)};
  if(item.alpha){
    let centerCount=0,centerOpaque=0,edgeOpaque=0;
    for(let y=0;y<h;y++)for(let x=0;x<w;x++){
      const alpha=channels===4?at(x,y,3):255;
      if(Math.hypot(x-w/2,y-h/2)<Math.min(w,h)*0.2){centerCount++;if(alpha>0)centerOpaque++;}
      if((x===0||y===0||x===w-1||y===h-1)&&alpha>0)edgeOpaque++;
    }
    result.centerAlpha=channels===4?at(Math.floor(w/2),Math.floor(h/2),3):255;
    result.centerDiskNontransparentFraction=centerOpaque/centerCount;
    result.nontransparentBoundaryPixels=edgeOpaque;
  }
  results.push(result);
}
fs.writeFileSync(path.join(root,'art/next-art-detail-audit.json'),JSON.stringify(results,null,2)+'\n');
console.log(JSON.stringify(results,null,2));
