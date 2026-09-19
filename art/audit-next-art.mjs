import fs from 'node:fs';
import path from 'node:path';
import zlib from 'node:zlib';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const manifest = JSON.parse(fs.readFileSync(path.join(root, 'art/next-art-manifest.json'), 'utf8').replace(/^\uFEFF/, ''));
const paeth = (a,b,c) => { const p=a+b-c, x=Math.abs(p-a), y=Math.abs(p-b), z=Math.abs(p-c); return x<=y && x<=z ? a : y<=z ? b : c; };
const results = manifest.map(item => {
  const file = `${item.dir}/${item.name}.png`;
  if (!fs.existsSync(path.join(root,file))) return {file, status:'missing'};
  try {
    const b=fs.readFileSync(path.join(root,file));
    if (b.subarray(0,8).toString('hex')!=='89504e470d0a1a0a') throw Error('Invalid PNG signature');
    const width=b.readUInt32BE(16), height=b.readUInt32BE(20), depth=b[24], type=b[25];
    const chunks=[];
    for(let p=8;p<b.length;) { const n=b.readUInt32BE(p); if(b.toString('ascii',p+4,p+8)==='IDAT') chunks.push(b.subarray(p+8,p+8+n)); p+=12+n; }
    let transparentPixels=null, alphaBounds=null;
    if(depth===8 && (type===6 || type===2) && b[28]===0) {
      const channels=type===6?4:3, stride=width*channels, raw=zlib.inflateSync(Buffer.concat(chunks));
      let previous=Buffer.alloc(stride), offset=0, minX=width,minY=height,maxX=-1,maxY=-1;
      transparentPixels=0;
      for(let y=0;y<height;y++) {
        const filter=raw[offset++], row=Buffer.from(raw.subarray(offset,offset+stride)); offset+=stride;
        for(let k=0;k<stride;k++) {
          const a=k>=channels?row[k-channels]:0, up=previous[k], c=k>=channels?previous[k-channels]:0;
          row[k]=(row[k]+(filter===0?0:filter===1?a:filter===2?up:filter===3?Math.floor((a+up)/2):filter===4?paeth(a,up,c):0))&255;
        }
        for(let x=0;x<width;x++) {
          const alpha=channels===4?row[x*channels+3]:255;
          if(alpha===0) transparentPixels++;
          if(alpha>16) {minX=Math.min(minX,x);minY=Math.min(minY,y);maxX=Math.max(maxX,x);maxY=Math.max(maxY,y);}
        }
        previous=row;
      }
      alphaBounds=[minX,minY,maxX,maxY];
    }
    return {file,status:'present',width,height,expectedSize:[item.w,item.h],sizeMatches:width===item.w&&height===item.h,alphaRequired:item.alpha,transparentPixels,alphaBounds};
  } catch(error) {return {file,status:'error',error:error.message};}
});
fs.writeFileSync(path.join(root,'art/next-art-audit.json'),JSON.stringify(results,null,2)+'\n');
console.log(JSON.stringify({total:results.length,present:results.filter(x=>x.status==='present').length,missing:results.filter(x=>x.status==='missing').length,errors:results.filter(x=>x.status==='error'),sizeMismatches:results.filter(x=>x.status==='present'&&!x.sizeMatches).length,missingAlpha:results.filter(x=>x.alphaRequired&&x.transparentPixels===0).map(x=>x.file)},null,2));
