// Browser layout/render checks, distinct from human and physical-device playtesting.
import {spawn} from 'node:child_process';
import {existsSync,mkdirSync,writeFileSync} from 'node:fs';
const base=process.argv[2]??'http://127.0.0.1:5992',out='.artifacts/m23-browser';mkdirSync(out,{recursive:true});
const edge=['C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe','C:/Program Files/Microsoft/Edge/Application/msedge.exe'].find(existsSync);
if(!edge)throw new Error('Edge not found');
const port=9800+Math.floor(Math.random()*150),proc=spawn(edge,['--headless=new','--disable-gpu','--no-first-run',`--remote-debugging-port=${port}`,`--user-data-dir=${process.env.TEMP}/sro-m23-browser-${port}`,'about:blank'],{stdio:'ignore',windowsHide:true});
const sleep=ms=>new Promise(r=>setTimeout(r,ms));let ws;
try {
  let version;for(let i=0;i<50&&!version;i++){try{version=await(await fetch(`http://127.0.0.1:${port}/json/version`)).json();}catch{await sleep(100);}}
  ws=new WebSocket(version.webSocketDebuggerUrl);await new Promise(r=>ws.onopen=r);let id=0;const pending=new Map(),errors=[];
  ws.onmessage=e=>{const m=JSON.parse(e.data);if(m.id){pending.get(m.id)?.(m);pending.delete(m.id);}if(m.method==='Runtime.exceptionThrown')errors.push(m.params.exceptionDetails);if(m.method==='Runtime.consoleAPICalled'&&m.params.type==='error')errors.push(m.params);if(m.method==='Network.responseReceived'&&m.params.response.status>=400&&!m.params.response.url.endsWith('/favicon.ico'))errors.push(m.params.response);};
  const send=(method,params={},sessionId)=>new Promise(r=>{const n=++id;pending.set(n,r);ws.send(JSON.stringify({id:n,method,params,sessionId}));});
  const target=await send('Target.createTarget',{url:'about:blank'}),attached=await send('Target.attachToTarget',{targetId:target.result.targetId,flatten:true}),s=(method,params={})=>send(method,params,attached.result.sessionId);
  await s('Runtime.enable');await s('Network.enable');const captures=[];
  for(const [width,height]of [[1280,720],[390,844]])for(const demo of ['territory','territory-busy','territory-invite','territory-coop','territory-season','territory-saving']) {
    await s('Emulation.setDeviceMetricsOverride',{width,height,deviceScaleFactor:1,mobile:width<600});await s('Page.navigate',{url:`${base}/?demo=${demo}`});
    let rendered=false;
    for(let attempt=0;attempt<120&&!rendered;attempt++) {
      await sleep(250);
      const ready=await s('Runtime.evaluate',{expression:demo==='territory-coop'?"document.querySelector('.mech-head')?.textContent.includes('Раунд')":"!!document.querySelector('.territory-map')",returnByValue:true});
      rendered=!!ready.result?.result?.value;
    }
    if(!rendered)errors.push({demo,width,reason:'screen did not render'});
    const layout=await s('Runtime.evaluate',{expression:'({width:innerWidth,scroll:document.documentElement.scrollWidth,text:document.body.innerText})',returnByValue:true});
    if(layout.result.exceptionDetails)throw new Error(JSON.stringify(layout.result.exceptionDetails));
    if(layout.result.result.value.scroll>width+1)errors.push({demo,width,reason:'horizontal overflow',layout:layout.result.result.value});
    const shot=await s('Page.captureScreenshot',{format:'png'}),file=`${out}/${width}-${demo}.png`;writeFileSync(file,Buffer.from(shot.result.data,'base64'));captures.push(file);
    if(width===390&&demo==='territory') {
      await s('Runtime.evaluate',{expression:"document.querySelector('.territory-map g[role=button]').dispatchEvent(new MouseEvent('click',{bubbles:true}))"});
      const card=await s('Runtime.evaluate',{expression:"(()=>{const c=document.querySelector('.territory-bottom');const r=c.getBoundingClientRect();return {position:getComputedStyle(c).position,bottom:r.bottom}})()",returnByValue:true});
      if(card.result?.result?.value?.position!=='fixed'||Math.abs(card.result.result.value.bottom-height)>1)errors.push({reason:'phone card is not a bottom panel',card});
      const detail=await s('Page.captureScreenshot',{format:'png'}),name=`${out}/390-territory-card.png`;writeFileSync(name,Buffer.from(detail.result.data,'base64'));captures.push(name);
    }
  }
  writeFileSync(`${out}/result.json`,JSON.stringify({captures,errors},null,2));if(errors.length)throw new Error(`${errors.length} browser errors; see ${out}/result.json`);
  console.log(`${captures.length} captures: desktop and phone viewport, no browser errors or horizontal overflow`);
}finally {ws?.close();proc.kill();}
