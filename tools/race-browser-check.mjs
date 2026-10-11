// npm run dev -- --port 5066 in client/, then node tools/race-browser-check.mjs
import {spawn} from 'node:child_process';
import {existsSync,mkdirSync,writeFileSync} from 'node:fs';
import assert from 'node:assert/strict';
const base=process.argv[2]??'http://127.0.0.1:5066',out='.artifacts/race';mkdirSync(out,{recursive:true});
const edge=['C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe','C:/Program Files/Microsoft/Edge/Application/msedge.exe'].find(existsSync);
assert(edge,'Edge not found'); const port=9900+Math.floor(Math.random()*100);
const proc=spawn(edge,['--headless=new','--no-first-run',`--remote-debugging-port=${port}`,`--user-data-dir=${process.env.TEMP}/sro-race-browser-${port}`,'about:blank'],{stdio:'ignore',windowsHide:true});
const sleep=ms=>new Promise(r=>setTimeout(r,ms));let ws;
try {
  let version;for(let i=0;i<50&&!version;i++){try{version=await(await fetch(`http://127.0.0.1:${port}/json/version`)).json();}catch{await sleep(200);}}
  assert(version,'Edge startup timed out');ws=new WebSocket(version.webSocketDebuggerUrl);await new Promise(r=>ws.onopen=r);
  let id=0;const pending=new Map(),errors=[];
  ws.onmessage=e=>{const m=JSON.parse(e.data);if(m.id){pending.get(m.id)?.(m);pending.delete(m.id);}if(m.method==='Runtime.exceptionThrown')errors.push(m.params.exceptionDetails);};
  const send=(method,params={},sessionId)=>new Promise(resolve=>{const n=++id;pending.set(n,resolve);ws.send(JSON.stringify({id:n,method,params,sessionId}));});
  const target=await send('Target.createTarget',{url:'about:blank'}),attached=await send('Target.attachToTarget',{targetId:target.result.targetId,flatten:true});
  const cdp=(method,params)=>send(method,params,attached.result.sessionId);
  const evaluate=async expression=>{const r=await cdp('Runtime.evaluate',{expression,returnByValue:true,awaitPromise:true});assert(!r.error&&!r.result.exceptionDetails,JSON.stringify(r));return r.result.result.value;};
  await cdp('Runtime.enable');const results=[];
  for(const [width,height]of [[360,640],[390,844],[640,360],[844,390],[1280,720]]) {
    await cdp('Emulation.setDeviceMetricsOverride',{width,height,deviceScaleFactor:1,mobile:false});
    await cdp('Emulation.setTouchEmulationEnabled',{enabled:true,maxTouchPoints:5});
    await cdp('Page.navigate',{url:base});await sleep(1000);
    await evaluate(`(async()=>{
      const {RaceScreen}=await import('/src/race/screen.ts');const pixi=performance.getEntriesByType('resource').find(e=>e.name.includes('/deps/pixi__js.js?')).name;const {Application}=await import(pixi);
      window.sent=[];window.bench=new RaceScreen({send:m=>sent.push(m),online:()=>true,resetSpace:()=>{}});
      window.raceApp=new Application();await raceApp.init({width:innerWidth,height:innerHeight,background:'#503527'});
      document.getElementById('game').replaceChildren(raceApp.canvas);raceApp.stage.addChild(bench.scene.view);
      raceApp.ticker.add(()=>bench.frame(performance.now(),innerWidth,innerHeight));bench.show();
      window.config=bench.config;window.snap={sessionId:'fixture',tick:1,ackSeq:0,state:'COUNTDOWN',position:config.track.spawn,velocity:{x:0,y:0},heading:0,omega:0,hp:100,boostState:'READY',boostRemainingMs:0,countdownMs:3000,lap:1,nextCp:0,elapsedMs:0,lapElapsedMs:0,lapTimesMs:[],boostPressId:0};
      bench.start();bench.apply({t:'raceEvent',event:'joined',requestId:sent.find(m=>m.act==='join').requestId,sessionId:'fixture',config,snapshot:snap});
    })()`);
    await sleep(100);assert(await evaluate(`bench.scene.view.visible`));
    const bounds=await evaluate(`(()=>{const box=s=>{const r=document.querySelector(s).getBoundingClientRect();return{x:r.x,y:r.y,w:r.width,h:r.height}};return {stick:box('.race-stick'),brake:box('.race-brake'),boost:box('.race-boost'),hud:box('.race-hud')}})()`);
    for(const box of Object.values(bounds)){assert(box.x>=0&&box.y>=0&&box.x+box.w<=width+1&&box.y+box.h<=height+1,JSON.stringify({width,height,box}));}
    assert(bounds.brake.w>=88&&bounds.boost.h>=44);assert(bounds.brake.y-(bounds.boost.y+bounds.boost.h)>=8);
    await cdp('Input.dispatchKeyEvent',{type:'keyDown',code:'KeyW',key:'w'});await sleep(80);
    assert.equal(await evaluate(`bench.body.position.x`),26,'countdown must not move');
    await cdp('Input.dispatchKeyEvent',{type:'keyUp',code:'KeyW',key:'w'});
    await evaluate(`bench.apply({t:'raceEvent',event:'snapshot',sessionId:'fixture',snapshot:{...snap,state:'RUNNING',tick:180,countdownMs:0}})`);
    const stick={x:bounds.stick.x+bounds.stick.w*.8,y:bounds.stick.y+bounds.stick.h*.5,id:1};
    const brake={x:bounds.brake.x+bounds.brake.w*.5,y:bounds.brake.y+bounds.brake.h*.5,id:2};
    await cdp('Input.dispatchTouchEvent',{type:'touchStart',touchPoints:[stick]});
    await cdp('Input.dispatchTouchEvent',{type:'touchStart',touchPoints:[stick,brake]});await sleep(120);
    const held=await evaluate(`({...bench.input.value})`);assert(held.brake&&held.throttle>.5,'independent two-finger brake and steering');
    await cdp('Input.dispatchTouchEvent',{type:'touchEnd',touchPoints:[brake]});await sleep(80);
    assert.equal(await evaluate(`bench.input.value.brake`),false);assert(await evaluate(`bench.input.value.throttle>.5`));
    await cdp('Input.dispatchTouchEvent',{type:'touchEnd',touchPoints:[]});
    await evaluate(`window.dispatchEvent(new Event('blur'))`);await sleep(80);
    assert(await evaluate(`!bench.input.value.brake&&bench.input.value.throttle===0&&!bench.input.value.controlActive`));
    await evaluate(`window.dispatchEvent(new Event('focus'));bench.exitMenu()`);
    assert(await evaluate(`bench.menuOpen&&bench.input.value.controlActive&&bench.input.value.throttle===0`));
    await evaluate(`bench.exitMenu()`);await sleep(100);
    const shot=await cdp('Page.captureScreenshot',{format:'png'});writeFileSync(`${out}/${width}x${height}.png`,Buffer.from(shot.result.data,'base64'));
    await evaluate(`bench.apply({t:'raceEvent',event:'result',sessionId:'fixture',validationStatus:'CONFIRMED',result:{sessionId:'fixture',playerId:null,name:'Guest',rankingKey:config.rankingKey,status:'CRASHED',reason:'WALL',elapsedMs:1534,totalMs:null,lapTimesMs:[],finishedAt:new Date().toISOString()}})`);
    await sleep(750);
    assert(await evaluate(`bench.root.textContent.includes('DNF')`));
    await evaluate(`bench.terminal();bench.close()`);assert(await evaluate(`!document.body.classList.contains('race-open')`));
    results.push({width,height,layout:'PASS',twoFingerBrake:'PASS',countdown:'PASS',focusReset:'PASS',exitMenu:'PASS'});
  }
  assert.equal(errors.length,0,JSON.stringify(errors));writeFileSync(`${out}/browser.json`,JSON.stringify(results,null,2));console.log(JSON.stringify(results,null,2));
} finally {ws?.close();proc.kill();}
