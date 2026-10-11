// Run with Vite dev: node tools/mech-controls-browser-check.mjs http://127.0.0.1:5064
// Real browser checks of the battle screen, using an isolated fixture and captured commands.
import {spawn} from 'node:child_process';
import {existsSync,mkdirSync,writeFileSync} from 'node:fs';
import assert from 'node:assert/strict';
const base=process.argv[2]??'http://127.0.0.1:5064';
const out='.artifacts/mech-controls';mkdirSync(out,{recursive:true});
const edge=['C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe','C:/Program Files/Microsoft/Edge/Application/msedge.exe'].find(existsSync);
assert(edge,'Edge not found');
const port=9800+Math.floor(Math.random()*100);
const proc=spawn(edge,['--headless=new','--disable-gpu','--no-first-run',`--remote-debugging-port=${port}`,`--user-data-dir=${process.env.TEMP}/sro-controls-${port}`,'about:blank'],{stdio:'ignore'});
const sleep=ms=>new Promise(r=>setTimeout(r,ms));let ws;
try {
  let version;
  for(let i=0;i<50&&!version;i++){try{version=await(await fetch(`http://127.0.0.1:${port}/json/version`)).json();}catch{await sleep(200);}}
  assert(version,'Edge startup timed out');
  ws=new WebSocket(version.webSocketDebuggerUrl);await new Promise(r=>ws.onopen=r);
  let id=0;const pending=new Map(),errors=[];
  ws.onmessage=e=>{const m=JSON.parse(e.data);if(m.id){pending.get(m.id)?.(m);pending.delete(m.id);}if(m.method==='Runtime.exceptionThrown')errors.push(m.params.exceptionDetails);};
  const send=(method,params={},sessionId)=>new Promise(resolve=>{const n=++id;pending.set(n,resolve);ws.send(JSON.stringify({id:n,method,params,sessionId}));});
  const target=await send('Target.createTarget',{url:'about:blank'});
  const attached=await send('Target.attachToTarget',{targetId:target.result.targetId,flatten:true});
  const cdp=(method,params)=>send(method,params,attached.result.sessionId);
  const evaluate=async expression=>{const r=await cdp('Runtime.evaluate',{expression,returnByValue:true,awaitPromise:true});assert(!r.error&&!r.result.exceptionDetails,JSON.stringify(r));return r.result.result.value;};
  await cdp('Runtime.enable');
  const results=[];
  for(const [width,height] of [[390,844],[360,640],[844,390],[1440,900]]) {
    await cdp('Emulation.setDeviceMetricsOverride',{width,height,deviceScaleFactor:1,mobile:width<1000});
    await cdp('Emulation.setTouchEmulationEnabled',{enabled:width<1000});
    await cdp('Page.navigate',{url:`${base}/?demo=mech`});await sleep(1200);
    await evaluate(`(async()=>{
      const {MechScreen}=await import('/src/mech/screen.ts');
      const {DEFAULT_RULES}=await import('/src/mech/rules.ts');
      const root=document.createElement('div');document.body.append(root);root.style.zIndex='2000';
      window.sent=[];
      window.bench=new MechScreen(root,{send:m=>sent.push(m),confirm:(_,yes)=>yes()});
      const unit=(id,side,x,y)=>({id,side,x,y,unit:'prototype',name:id,dir:0,hp:[2200,1100,600,1100],max:[2200,1100,600,1100],activated:false});
      window.fixture={mission:'firstSortie',map:Array(10).fill('..........'),round:1,turn:'player',current:'p1',moved:false,steps:0,moveRange:2,units:[unit('p1','player',2,3),unit('e1','enemy',3,3)]};
      bench.apply({t:'mechState',rules:DEFAULT_RULES,battle:structuredClone(fixture)});await bench.chain;
    })()`);
    const check=await evaluate(`(async()=>{
      const root=bench.root;
      const check=(ok,msg)=>{if(!ok)throw new Error(msg)};
      const tick=()=>new Promise(r=>setTimeout(r,50));
      check([...root.querySelectorAll('details')].every(d=>!d.open),'details initially closed');
      bench.tap(2,4);bench.tap(2,4);
      check(sent.length===0,'selecting a cell twice must not move');
      check(!root.querySelector('.mech-move-button').disabled,'Move available after selecting cell');
      root.querySelector('.mech-move-button').click();
      check(sent.length===1&&sent[0].act==='move'&&sent[0].x===2&&sent[0].y===4,'Move sends selected destination');
      bench.apply({t:'mechState',rules:bench.rules,battle:structuredClone(fixture)});await bench.chain;
      bench.tap(3,3);check(!root.querySelector('.mech-fire-button').disabled,'adjacent target can be shot');
      const toggle=()=>root.querySelector('.mech-disclosure--attack > summary').click();
      toggle();await tick();
      check(root.querySelector('.mech-disclosure--attack').open,'attack settings expand');
      [...root.querySelectorAll('.mech-mode button')].find(b=>b.textContent==='Прицельный').click();await tick();
      check(root.querySelector('.mech-disclosure--attack').open,'settings persist through render');
      check(root.querySelector('.mech-fire-button').disabled,'aimed shot requires part');
      root.querySelector('.mech-parts button').click();await tick();
      check(!root.querySelector('.mech-fire-button').disabled,'chosen part allows shot');
      toggle();await tick();
      check(root.querySelector('.mech-fire-button').textContent==='Огонь','closing settings restores default fire');
      const own=root.querySelector('.mech-disclosure--own');own.querySelector('summary').click();await tick();
      check(own.open&&own.querySelector('.mech-gear').getBoundingClientRect().height>0,'mech information expands');
      own.querySelector('summary').click();await tick();
      const rect=e=>{const r=e.getBoundingClientRect();return {x:r.x,y:r.y,w:r.width,h:r.height,bottom:r.bottom}};
      const tray=rect(root.querySelector('.mech-tray')),target=rect(root.querySelector('.mech-target'));
      const buttons=[...root.querySelectorAll('.mech-tray-buttons button')].map(rect);
      check(buttons.every(r=>r.x>=0&&r.x+r.w<=innerWidth+1&&r.bottom<=innerHeight+1),'main controls fit viewport');
      check(target.bottom<=tray.y,'target information does not overlap controls: '+JSON.stringify({width:innerWidth,height:innerHeight,target,tray}));
      check(rect(root.querySelector('.mech-own')).h<=50,'mech information collapses to icon');
      root.querySelector('.mech-fire-button').click();
      check(sent.at(-1).act==='attack'&&!sent.at(-1).part,'default Fire sends ordinary attack');
      return {tray,target,buttons};
    })()`);
    results.push({width,height,...check});
    const shot=await cdp('Page.captureScreenshot',{format:'png'});writeFileSync(`${out}/${width}x${height}.png`,Buffer.from(shot.result.data,'base64'));
  }
  assert.equal(errors.length,0,JSON.stringify(errors));
  writeFileSync(`${out}/result.json`,JSON.stringify(results,null,2)+'\n');
  console.log('Mech controls passed: four viewports; movement confirmation; adjacent ordinary fire; collapsible information; aimed settings; no browser errors.');
} finally {ws?.close();proc.kill();}
