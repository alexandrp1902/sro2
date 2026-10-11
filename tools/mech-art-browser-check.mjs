// Actual Pixi renderer: 4 assemblies × 8 directions × 3 sizes × 7 states.
// Run after vite preview: node tools/mech-art-browser-check.mjs http://127.0.0.1:5063
import {spawn} from 'node:child_process';
import {existsSync,mkdirSync,writeFileSync} from 'node:fs';
const base=process.argv[2]??'http://127.0.0.1:5063';
const out='.artifacts/m22-art/browser';mkdirSync(out,{recursive:true});
const edge=['C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe','C:/Program Files/Microsoft/Edge/Application/msedge.exe'].find(existsSync);
if(!edge)throw new Error('Edge not found');
const port=9500+Math.floor(Math.random()*400);
const proc=spawn(edge,['--headless=new','--disable-gpu','--no-first-run',`--remote-debugging-port=${port}`,`--user-data-dir=${process.env.TEMP}/sro-art-${port}`,'about:blank'],{stdio:'ignore'});
const sleep=ms=>new Promise(r=>setTimeout(r,ms));let ws;
try {
  let version;
  for(let i=0;i<50&&!version;i++){try{version=await(await fetch(`http://127.0.0.1:${port}/json/version`)).json();}catch{await sleep(200);}}
  if(!version)throw new Error('Edge startup timed out');
  ws=new WebSocket(version.webSocketDebuggerUrl);await new Promise(r=>ws.onopen=r);
  let id=0;const pending=new Map(),errors=[],unrelated=[];
  ws.onmessage=event=>{
    const message=JSON.parse(event.data);
    if(message.id){pending.get(message.id)?.(message);pending.delete(message.id);}
    if(message.method==='Runtime.exceptionThrown')errors.push(message.params.exceptionDetails);
    if(message.method==='Network.responseReceived'&&message.params.response.status>=400){
      const response=message.params.response;
      (new URL(response.url).pathname==='/favicon.ico'?unrelated:errors).push(response);
    }
    if(message.method==='Network.loadingFailed')errors.push(message.params);
  };
  const send=(method,params={},sessionId)=>new Promise(resolve=>{const n=++id;pending.set(n,resolve);ws.send(JSON.stringify({id:n,method,params,sessionId}));});
  const target=await send('Target.createTarget',{url:'about:blank'});
  const attached=await send('Target.attachToTarget',{targetId:target.result.targetId,flatten:true});
  const s=(method,params)=>send(method,params,attached.result.sessionId);
  const evaluate=async expression=>{
    const response=await s('Runtime.evaluate',{expression,returnByValue:true,awaitPromise:true});
    if(response.error||response.result.exceptionDetails)throw new Error(JSON.stringify(response));
    return response.result.result.value;
  };
  await s('Runtime.enable');await s('Network.enable');
  await s('Emulation.setDeviceMetricsOverride',{width:1440,height:900,deviceScaleFactor:1,mobile:false});
  await s('Page.navigate',{url:`${base}/?demo=mech-art`});
  await evaluate(`new Promise((resolve,reject)=>{let n=0;const timer=setInterval(()=>{if(document.querySelector('[data-ready="true"]')){clearInterval(timer);resolve(true);}else if(++n>300){clearInterval(timer);reject(new Error('art load timed out'));}},100);})`);
  const states=['целый','движение','левая рука','правая рука','шасси','обе руки','остов'];
  const names=['healthy','walk','left','right','chassis','both','wreck'];const captures=[];
  for(const size of [64,96,128])for(const [index,state]of states.entries()){
    await evaluate(`(()=>{const fields=document.querySelectorAll('section select');fields[0].value='${size}';fields[0].dispatchEvent(new Event('change'));fields[1].value=${JSON.stringify(state)};})()`);
    await sleep(180);
    const shot=await s('Page.captureScreenshot',{format:'png'});
    const file=`${out}/${size}-${names[index]}.png`;writeFileSync(file,Buffer.from(shot.result.data,'base64'));captures.push(file);
  }
  const result={renderer:'Pixi / headless Edge',assemblies:4,directions:8,sizes:[64,96,128],states,captures,errors,unrelated};
  writeFileSync(`${out}/result.json`,JSON.stringify(result,null,2)+'\n');
  if(errors.length)throw new Error(`${errors.length} browser loading/render errors; see result.json`);
  console.log(`${captures.length} renderer captures; 672 assembly states; no browser errors`);
} finally {ws?.close();proc.kill();}
