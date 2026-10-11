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
    await cdp('Emulation.setTouchEmulationEnabled',{enabled:width<1000,maxTouchPoints:5});
    await cdp('Page.navigate',{url:base});await sleep(1000);
    await evaluate(`(async()=>{
      await import('/src/main.ts');await import('/src/race/race.css');
      const {RaceScreen}=await import('/src/race/screen.ts');const pixi=performance.getEntriesByType('resource').find(e=>e.name.includes('/deps/pixi__js.js?')).name;const {Application}=await import(pixi);
      window.sent=[];window.raceOnline=true;window.bench=new RaceScreen({send:m=>sent.push(m),online:()=>window.raceOnline,resetSpace:()=>{}});
      window.raceApp=new Application();await raceApp.init({width:innerWidth,height:innerHeight,background:'#503527'});
      document.getElementById('game').replaceChildren(raceApp.canvas);raceApp.stage.addChild(bench.scene.view);
      raceApp.ticker.add(()=>bench.frame(performance.now(),innerWidth,innerHeight));bench.show();
      window.config=bench.config;window.snap={sessionId:'fixture',tick:1,ackSeq:0,state:'COUNTDOWN',position:config.track.spawn,velocity:{x:0,y:0},heading:0,omega:0,hp:100,countdownMs:3000,lap:1,nextCp:0,elapsedMs:0,lapElapsedMs:0,lapTimesMs:[]};
    })()`);
    await sleep(100);
    const menu=await evaluate(`(()=>{const c=document.querySelector('.race-card'),r=c.getBoundingClientRect();return {x:r.x,y:r.y,w:r.width,h:r.height,scroll:c.scrollHeight,client:c.clientHeight}})()`);
    assert(menu.y>=0&&menu.y+menu.h<=height+1,`menu must fit viewport: ${JSON.stringify({width,height,menu})}`);
    if(menu.scroll>menu.client && width<1000) {
      const finger={x:menu.x+menu.w*.7,y:menu.y+menu.h*.75,id:1};
      await cdp('Input.dispatchTouchEvent',{type:'touchStart',touchPoints:[finger]});
      for(let i=1;i<=8;i++){await cdp('Input.dispatchTouchEvent',{type:'touchMove',touchPoints:[{...finger,y:finger.y-i*menu.h*.06}]});await sleep(30);}
      await cdp('Input.dispatchTouchEvent',{type:'touchEnd',touchPoints:[]});await sleep(250);
      assert(await evaluate(`document.querySelector('.race-card').scrollTop>20`),'mobile menu must scroll with an actual swipe');
      await evaluate(`document.querySelector('.race-card').scrollTop=0`);
    }
    const terminalShot=await cdp('Page.captureScreenshot',{format:'png'});writeFileSync(`${out}/menu-${width}x${height}.png`,Buffer.from(terminalShot.result.data,'base64'));
    await evaluate(`(async()=>{bench.livery=${JSON.stringify(width===1280?'blue':'copper')};await bench.start();bench.apply({t:'raceEvent',event:'joined',requestId:sent.find(m=>m.act==='join').requestId,sessionId:'fixture',config});bench.apply({t:'raceEvent',event:'started',sessionId:'fixture',countdownMs:3000});})()`);
    assert(await evaluate(`bench.scene.hull.texture.width>100&&bench.scene.hull.width===3.65`),'generated bolide is loaded at double size');
    await sleep(100);assert(await evaluate(`bench.scene.view.visible`));
    const bounds=await evaluate(`(()=>{const box=s=>{const r=document.querySelector(s).getBoundingClientRect();return{x:r.x,y:r.y,w:r.width,h:r.height}};return {stick:box('.race-stick'),brake:box('.race-brake'),hud:box('.race-hud')}})()`);
    for(const box of Object.values(bounds)){assert(box.x>=0&&box.y>=0&&box.x+box.w<=width+1&&box.y+box.h<=height+1,JSON.stringify({width,height,box}));}
    assert(await evaluate(`!document.querySelector('.race-boost')&&!document.querySelector('.race-speed')`),'boost removed');
    if(width<1000) {
      assert(bounds.brake.w>=88);
    } else assert(bounds.stick.w===0&&bounds.brake.w===0,'desktop touch controls hidden');
    await cdp('Input.dispatchKeyEvent',{type:'keyDown',code:'KeyW',key:'w'});await sleep(80);
    assert.equal(await evaluate(`bench.body.position.x`),26,'countdown must not move');
    await cdp('Input.dispatchKeyEvent',{type:'keyUp',code:'KeyW',key:'w'});
    await evaluate(`bench.run.startsAt=performance.now();bench.run.lastNow=performance.now()`);
    await cdp('Input.dispatchKeyEvent',{type:'keyDown',code:'KeyW',key:'w'});await sleep(250);
    await cdp('Input.dispatchKeyEvent',{type:'keyUp',code:'KeyW',key:'w'});
    const selected=await evaluate(`bench.input.value.throttle`);assert(selected>.1);
    await sleep(100);assert.equal(await evaluate(`bench.input.value.throttle`),selected,'speed persists after W release');
    await cdp('Input.dispatchKeyEvent',{type:'keyDown',code:'Space',key:' '});await sleep(50);
    assert(await evaluate(`bench.input.value.brake`));assert.equal(await evaluate(`bench.input.value.throttle`),selected,'Space preserves selected speed');
    await cdp('Input.dispatchKeyEvent',{type:'keyUp',code:'Space',key:' '});assert(await evaluate(`!bench.input.value.brake`));
    await cdp('Input.dispatchKeyEvent',{type:'keyDown',code:'ArrowDown',key:'ArrowDown'});await sleep(120);
    await cdp('Input.dispatchKeyEvent',{type:'keyUp',code:'ArrowDown',key:'ArrowDown'});
    assert((await evaluate(`bench.input.value.throttle`))<selected,'down reduces selected speed');
    await cdp('Input.dispatchKeyEvent',{type:'keyDown',code:'KeyD',key:'d'});assert.equal(await evaluate(`bench.input.value.turn`),1);
    await cdp('Input.dispatchKeyEvent',{type:'keyUp',code:'KeyD',key:'d'});
    await cdp('Input.dispatchKeyEvent',{type:'keyDown',code:'ShiftLeft',key:'Shift'});await sleep(40);
    await cdp('Input.dispatchKeyEvent',{type:'keyUp',code:'ShiftLeft',key:'Shift'});
    if(width<1000) {
      const stick={x:bounds.stick.x+bounds.stick.w*.8,y:bounds.stick.y+bounds.stick.h*.5,id:1};
      const brake={x:bounds.brake.x+bounds.brake.w*.5,y:bounds.brake.y+bounds.brake.h*.5,id:2};
      await cdp('Input.dispatchTouchEvent',{type:'touchStart',touchPoints:[stick]});
      const chosen=await evaluate(`bench.input.value.throttle`);assert(chosen>.7);
      const knob=await evaluate(`bench.root.querySelector('.race-knob').style.transform`);
      await cdp('Input.dispatchTouchEvent',{type:'touchStart',touchPoints:[stick,brake]});await sleep(120);
      assert(await evaluate(`bench.input.value.brake&&bench.input.value.steerMode==='heading'`));
      assert.equal(await evaluate(`bench.input.value.throttle`),chosen,'stick and brake preserve selected speed');
      await cdp('Input.dispatchTouchEvent',{type:'touchEnd',touchPoints:[brake]});await sleep(80);
      assert(await evaluate(`!bench.input.value.brake`));assert.equal(await evaluate(`bench.input.value.throttle`),chosen);
      await cdp('Input.dispatchTouchEvent',{type:'touchEnd',touchPoints:[]});
      assert.equal(await evaluate(`bench.input.value.throttle`),chosen,'releasing stick preserves speed');
      assert.equal(await evaluate(`bench.root.querySelector('.race-knob').style.transform`),knob,'stick remains in selected position');
    }
    assert(await evaluate(`sent.every(m=>m.act!=='input')`),'no race input synchronization');
    const elapsed=await evaluate(`bench.run.elapsedMs`);await evaluate(`window.raceOnline=false`);await sleep(200);
    assert((await evaluate(`bench.run.elapsedMs`))>elapsed,'race continues offline');await evaluate(`window.raceOnline=true`);
    await evaluate(`window.dispatchEvent(new Event('blur'))`);await sleep(80);
    assert(await evaluate(`!bench.input.value.brake&&bench.input.value.throttle===0&&!bench.input.value.controlActive`));
    await evaluate(`window.dispatchEvent(new Event('focus'));bench.exitMenu()`);
    assert(await evaluate(`bench.menuOpen&&bench.input.value.controlActive&&bench.input.value.throttle===0`));
    await evaluate(`bench.exitMenu()`);await sleep(100);
    const shot=await cdp('Page.captureScreenshot',{format:'png'});writeFileSync(`${out}/${width}x${height}.png`,Buffer.from(shot.result.data,'base64'));
    await evaluate(`window.raceOnline=false;const p=bench.body.position;bench.config.track.obstacles.push({id:'local-crash',center:{x:p.x+4,y:p.y},radius:1,reason:'COLUMN'});bench.body.velocity={x:600,y:0};bench.body.heading=0`);
    await sleep(200);assert(await evaluate(`bench.run.state==='CRASHED'&&bench.mode==='results'`),'local physics produces crash without server');
    assert.equal(await evaluate(`sent.filter(m=>m.act==='finish').length`),0,'offline finish is queued');
    await evaluate(`window.raceOnline=true`);await sleep(100);
    assert.equal(await evaluate(`sent.filter(m=>m.act==='finish').length`),1,'reconnect submits one finish');
    await evaluate(`bench.apply({t:'raceEvent',event:'result',sessionId:'fixture',validationStatus:'CONFIRMED',result:{sessionId:'fixture',playerId:null,name:'Guest',rankingKey:config.rankingKey,status:'CRASHED',reason:'WALL',elapsedMs:1534,totalMs:null,lapTimesMs:[],finishedAt:new Date().toISOString()}})`);
    await sleep(750);
    assert(await evaluate(`bench.root.textContent.includes('DNF')`));
    await evaluate(`bench.terminal();bench.close()`);assert(await evaluate(`!document.body.classList.contains('race-open')`));
    results.push({width,height,layout:'PASS',menuScroll:menu.scroll>menu.client?'PASS':'FITS',bolideImage:'PASS',latchedStick:'PASS',localSimulation:'PASS',desktopKeyboard:'PASS',countdown:'PASS',focusReset:'PASS',exitMenu:'PASS'});
  }
  assert.equal(errors.length,0,JSON.stringify(errors));writeFileSync(`${out}/browser.json`,JSON.stringify(results,null,2));console.log(JSON.stringify(results,null,2));
} finally {ws?.close();proc.kill();}
