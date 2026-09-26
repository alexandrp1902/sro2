// Local-only browser inspection using an isolated headless Chrome profile.
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import {spawn} from 'node:child_process';
import {fileURLToPath} from 'node:url';
import http from 'node:http';
const root=path.dirname(fileURLToPath(import.meta.url));
const profile=await fs.mkdtemp(path.join(os.tmpdir(),'sro-mech-browser-'));
const server=http.createServer(async(req,res)=>{
 if(req.url==='/favicon.ico'){res.writeHead(204).end();return;}
 try{const file=path.resolve(root,'.'+decodeURIComponent(new URL(req.url,'http://localhost').pathname));if(!file.startsWith(root+path.sep)){res.writeHead(403).end();return;}
 const types={'.html':'text/html; charset=utf-8','.js':'text/javascript; charset=utf-8','.json':'application/json; charset=utf-8','.png':'image/png'};
 const data=await fs.readFile(file);res.writeHead(200,{'Content-Type':types[path.extname(file)]??'application/octet-stream'});res.end(data);
 }catch{res.writeHead(404).end();}
});
await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
const chrome=spawn('C:/Program Files/Google/Chrome/Application/chrome.exe',['--headless','--disable-gpu','--no-first-run','--no-default-browser-check','--remote-debugging-port=0','--user-data-dir='+profile,'about:blank'],{windowsHide:true,stdio:'ignore'});
let socket,sequence=0;const pending=new Map();
const delay=ms=>new Promise(r=>setTimeout(r,ms));
try{
 let port;
 for(let i=0;i<100;i++){try{port=(await fs.readFile(path.join(profile,'DevToolsActivePort'),'utf8')).split('\n')[0];break;}catch{await delay(200);}}
 if(!port)throw Error('Chrome debugging port did not start');
 const tabs=await(await fetch('http://127.0.0.1:'+port+'/json/list')).json();
 socket=new WebSocket(tabs.find(t=>t.type==='page').webSocketDebuggerUrl);
 socket.onmessage=event=>{const data=JSON.parse(event.data);if(data.id){const p=pending.get(data.id);if(p){pending.delete(data.id);data.error?p.reject(Error(JSON.stringify(data.error))):p.resolve(data.result);}}else if(['Runtime.exceptionThrown','Log.entryAdded'].includes(data.method))console.log(JSON.stringify(data));};
 await new Promise((resolve,reject)=>{socket.onopen=resolve;socket.onerror=reject;});
 const send=(method,params={})=>new Promise((resolve,reject)=>{const id=++sequence;pending.set(id,{resolve,reject});socket.send(JSON.stringify({id,method,params}));});
 await send('Runtime.enable');await send('Log.enable');await send('Page.enable');
 await send('Emulation.setDeviceMetricsOverride',{width:1500,height:1600,deviceScaleFactor:1,mobile:false});
 const page=process.argv[2]??'assembly.html';
 await send('Page.navigate',{url:'http://127.0.0.1:'+server.address().port+'/'+page});
 let result;
 for(let i=0;i<100;i++){
   result=await send('Runtime.evaluate',{expression:"JSON.stringify({ready:document.body?.dataset.ready,error:document.querySelector('#error')?.textContent})",returnByValue:true});
   const state=JSON.parse(result.result.value??'{}');
   if(state.ready==='true'||state.error){console.log(state);break;}
   await delay(100);
 }
 await delay(300);
 const scenario=process.argv[3]??'healthy';
 if(!['healthy','guides','nogun','noshield','noarms'].includes(scenario))throw Error('Unknown scenario');
 if(scenario!=='healthy')await send('Runtime.evaluate',{expression:`for(const id of ${JSON.stringify(scenario==='guides'?['guides']:scenario==='nogun'?['gun']:scenario==='noshield'?['shield']:['gun','shield'])}){const el=document.getElementById(id);if(el){el.checked=true;el.dispatchEvent(new Event('change'));}}`});
 const state=await send('Runtime.evaluate',{expression:"JSON.stringify({title:document.title,ready:document.body.dataset.ready,images:[...document.images].map(i=>({src:i.src,complete:i.complete})),error:document.querySelector('#error')?.textContent})",returnByValue:true});
 console.log(state.result.value);
 const shot=await send('Page.captureScreenshot',{format:'png',captureBeyondViewport:true});
 const prefix=page==='rig-review.html'?'browser':'assembly-browser';
 const output=path.join(root,prefix+'-review-'+scenario+'.png');await fs.writeFile(output,Buffer.from(shot.data,'base64'));console.log(output);
 const pixels=await send('Runtime.evaluate',{expression:`JSON.stringify([...document.querySelectorAll('.card>canvas:first-of-type')].map(c=>{const p=c.getContext('2d').getImageData(0,0,c.width,c.height).data;let count=0,minX=c.width,minY=c.height,maxX=-1,maxY=-1;for(let y=0;y<c.height;y++)for(let x=0;x<c.width;x++)if(p[(y*c.width+x)*4+3]>16){count++;minX=Math.min(x,minX);minY=Math.min(y,minY);maxX=Math.max(x,maxX);maxY=Math.max(y,maxY);}return {direction:c.parentNode.querySelector('strong').textContent,count,bounds:[minX,minY,maxX,maxY]};}))`,returnByValue:true});
 if(pixels.exceptionDetails)throw Error(JSON.stringify(pixels.exceptionDetails));
 await fs.writeFile(path.join(root,prefix+'-pixels-'+scenario+'.json'),pixels.result.value+'\n');
 console.log(pixels.result.value);
 if(JSON.parse(state.result.value).ready!=='true'||JSON.parse(state.result.value).error)throw Error('Page did not reach clean ready state');
 await send('Browser.close');
}finally{socket?.close();chrome.kill();server.close();}
