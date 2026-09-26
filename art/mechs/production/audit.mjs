// Reads PNG pixels without changing source images. Run from any working directory.
import fs from 'node:fs';
import path from 'node:path';
import zlib from 'node:zlib';
import {fileURLToPath} from 'node:url';
const root=path.dirname(fileURLToPath(import.meta.url));
const manifest=JSON.parse(fs.readFileSync(path.join(root,'manifest.json'),'utf8').replace(/^\uFEFF/,''));
const paeth=(a,b,c)=>{const p=a+b-c,x=Math.abs(p-a),y=Math.abs(p-b),z=Math.abs(p-c);return x<=y&&x<=z?a:y<=z?b:c;};
const results=[];
for(const item of manifest.entries.filter(x=>x.source?.startsWith('sources/'))){
  try{
    const data=fs.readFileSync(path.join(root,item.source));
    if(data.subarray(0,8).toString('hex')!=='89504e470d0a1a0a')throw Error('Invalid PNG');
    const width=data.readUInt32BE(16),height=data.readUInt32BE(20),channels=data[25]===6?4:3;
    if(data[24]!==8||![2,6].includes(data[25])||data[28]!==0)throw Error('Unsupported PNG encoding');
    const chunks=[];
    for(let p=8;p<data.length;){const n=data.readUInt32BE(p);if(data.toString('ascii',p+4,p+8)==='IDAT')chunks.push(data.subarray(p+8,p+8+n));p+=n+12;}
    const raw=zlib.inflateSync(Buffer.concat(chunks)),stride=width*channels,pixels=Buffer.alloc(stride*height);
    if(raw.length!==(stride+1)*height)throw Error('Unexpected decoded PNG length');
    let offset=0;
    for(let y=0;y<height;y++){
      const filter=raw[offset++];if(filter>4)throw Error('Invalid filter');
      for(let x=0;x<stride;x++){
        const k=y*stride+x,a=x>=channels?pixels[k-channels]:0,b=y?pixels[k-stride]:0,c=y&&x>=channels?pixels[k-stride-channels]:0;
        pixels[k]=(raw[offset++]+(filter===0?0:filter===1?a:filter===2?b:filter===3?Math.floor((a+b)/2):paeth(a,b,c)))&255;
      }
    }
    const {columns,rows}=item.layout,cellWidth=width/columns,cellHeight=height/rows;
    const frames=[];
    for(let n=0;n<columns*rows;n++){
      const left=Math.round(n%columns*cellWidth),top=Math.round(Math.floor(n/columns)*cellHeight),right=Math.round((n%columns+1)*cellWidth),bottom=Math.round((Math.floor(n/columns)+1)*cellHeight);
      let transparent=0,visible=0,minX=right,minY=bottom,maxX=-1,maxY=-1;
      for(let y=top;y<bottom;y++)for(let x=left;x<right;x++){
        const alpha=channels===4?pixels[(y*width+x)*channels+3]:255;
        if(alpha===0)transparent++;
        if(alpha>16){visible++;minX=Math.min(minX,x);minY=Math.min(minY,y);maxX=Math.max(maxX,x);maxY=Math.max(maxY,y);}
      }
      frames.push({direction:manifest.directions[n]??n,rect:[left,top,right-left,bottom-top],visiblePixels:visible,transparentPixels:transparent,bounds:visible?[minX-left,minY-top,maxX-left,maxY-top]:null,minMargin:visible?Math.min(minX-left,minY-top,right-1-maxX,bottom-1-maxY):null});
    }
    results.push({id:item.id,source:item.source,status:item.status,notes:item.notes??[],width,height,hasAlpha:channels===4,integerCells:Number.isInteger(cellWidth)&&Number.isInteger(cellHeight),squareCells:cellWidth===cellHeight,frames});
  }catch(error){results.push({id:item.id,source:item.source,error:error.message});}
}
const report={checkedAt:new Date().toISOString(),automaticAcceptance:false,results};
fs.writeFileSync(path.join(root,'audit.json'),JSON.stringify(report,null,2)+'\n');
fs.writeFileSync(path.join(root,'review-data.js'),'window.MECH_REVIEW = '+JSON.stringify(report,null,2)+';\n');
const errors=results.filter(x=>x.error);
console.log(JSON.stringify({sources:results.length,errors,withoutAlpha:results.filter(x=>!x.error&&!x.hasAlpha).map(x=>x.id),emptyFrames:results.flatMap(x=>(x.frames??[]).filter(f=>!f.visiblePixels).map(f=>x.id+':'+f.direction)),note:'Directions, identity and mounting require visual review.'},null,2));
if(errors.length)process.exitCode=1;
