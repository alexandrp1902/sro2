// Run against Vite: node tools/race-fx-browser-check.mjs http://127.0.0.1:5066
import { spawn } from 'node:child_process';
import { existsSync, mkdirSync, writeFileSync } from 'node:fs';
import assert from 'node:assert/strict';
const base = process.argv[2] ?? 'http://127.0.0.1:5066', out = '.artifacts/race/fx';
mkdirSync(out, { recursive: true });
const edge = ['C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe', 'C:/Program Files/Microsoft/Edge/Application/msedge.exe'].find(existsSync);
assert(edge); const port = 10100 + Math.floor(Math.random() * 100);
const proc = spawn(edge, ['--headless=new', '--no-first-run', `--remote-debugging-port=${port}`,
  `--user-data-dir=${process.env.TEMP}/sro-race-fx-${port}`, 'about:blank'], { stdio: 'ignore', windowsHide: true });
const sleep = ms => new Promise(r => setTimeout(r, ms)); let ws;
try {
  let version;
  for (let i = 0; i < 50 && !version; i++) { try { version = await (await fetch(`http://127.0.0.1:${port}/json/version`)).json(); } catch { await sleep(200); } }
  assert(version); ws = new WebSocket(version.webSocketDebuggerUrl); await new Promise(r => ws.onopen = r);
  let id = 0; const pending = new Map(), errors = [];
  ws.onmessage = e => { const m = JSON.parse(e.data); if (m.id) { pending.get(m.id)?.(m); pending.delete(m.id); }
    if (m.method === 'Runtime.exceptionThrown') errors.push(m.params.exceptionDetails); };
  const send = (method, params = {}, sessionId) => new Promise(resolve => {
    const n = ++id; pending.set(n, resolve); ws.send(JSON.stringify({ id: n, method, params, sessionId }));
  });
  const target = await send('Target.createTarget', { url: 'about:blank' });
  const attached = await send('Target.attachToTarget', { targetId: target.result.targetId, flatten: true });
  const cdp = (method, params) => send(method, params, attached.result.sessionId);
  const evaluate = async expression => {
    const r = await cdp('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true });
    assert(!r.error && !r.result.exceptionDetails, JSON.stringify(r)); return r.result.result.value;
  };
  await cdp('Runtime.enable'); await cdp('Emulation.setDeviceMetricsOverride', { width: 1280, height: 720, deviceScaleFactor: 1, mobile: false });
  await cdp('Page.navigate', { url: base }); await sleep(1200);
  await evaluate(`(async()=>{
    await import('/src/main.ts'); await import('/src/race/race.css'); document.body.classList.add('race-open');
    const {RaceScreen}=await import('/src/race/screen.ts');
    const pixi=performance.getEntriesByType('resource').find(e=>e.name.includes('/deps/pixi__js.js?')).name;
    const {Application}=await import(pixi); window.fixture=new RaceScreen({send:()=>{},online:()=>true,resetSpace:()=>{}});
    window.app=new Application(); await app.init({width:1280,height:720,background:'#503527'}); app.ticker.stop();
    document.getElementById('game').replaceChildren(app.canvas); app.stage.addChild(fixture.scene.view);
    window.fx=fixture.scene; window.b=fixture.body; window.input={throttle:1,brake:false}; window.clock=1000;
    window.draw=()=>{fx.world.scale.set(90);fx.world.position.set(400-b.position.x*90,360-b.position.y*90);app.render()};
  })()`);
  const shot = async name => {
    await evaluate('draw()'); const r = await cdp('Page.captureScreenshot', { format: 'png' });
    writeFileSync(`${out}/${name}.png`, Buffer.from(r.result.data, 'base64'));
  };
  for (const livery of ['copper', 'blue', 'yellow', 'white']) {
    await evaluate(`(async()=>{await fx.prepare('${livery}');fx.load(fixture.config);fx.frame(clock,1280,720,b,input,false)})()`);
    assert(await evaluate(`fx.textures.size===22&&fx.pilot.width>.2&&fx.flames.every(s=>s.visible&&s.width>2)&&fx.banks.children.length>50`));
    await shot(`pod-${livery}`);
  }
  await evaluate(`fx.load(fixture.config); b.hp=10;b.velocity={x:8,y:0};for(let i=0;i<30;i++){b.position.x+=8/60;fx.frame(clock+=1000/60,1280,720,b,input,false)}`);
  assert(await evaluate(`fx.particles.particles.filter(p=>p.kind==='smoke').length>12`)); await shot('damage-smoke');
  await evaluate(`fx.load(fixture.config);b.hp=100;b.velocity={x:0,y:0};fx.frame(clock+=16,1280,720,b,input,false,undefined,
    [{position:{x:b.position.x,y:b.position.y-.8},normal:{x:0,y:1},velocity:{x:8,y:-3},speed:3}]);
    fx.frame(clock+=50,1280,720,b,input,false)`);
  assert(await evaluate(`fx.particles.particles.some(p=>p.kind==='spark'&&p.y<b.position.y)`)); await shot('contact-sparks');
  await evaluate(`fx.load(fixture.config); b.hp=0; fx.frame(clock+=16,1280,720,b,input,true,{x:12,y:0});
    window.engines=fx.particles.particles.filter(p=>p.kind==='engine');window.engineStart=engines.map(p=>p.x);
    for(let i=0;i<9;i++)fx.frame(clock+=1000/60,1280,720,b,input,true,{x:12,y:0})`);
  assert(await evaluate(`engines.length===2&&engines.every((p,i)=>p.x>engineStart[i])&&!fx.vehicle.visible`)); await shot('explosion-150ms');
  await evaluate(`for(let i=0;i<27;i++)fx.frame(clock+=1000/60,1280,720,b,input,true,{x:12,y:0})`); await shot('explosion-600ms');
  await evaluate(`for(let i=0;i<480;i++)fx.frame(clock+=1000/60,1280,720,b,input,true,{x:12,y:0})`);
  assert(await evaluate(`fx.particles.particles.length===0`), 'explosion must happen once and expire');
  await evaluate('fx.close()'); assert(await evaluate('!fx.view.visible&&fx.banks.children.length===0'));
  assert.equal(errors.length, 0, JSON.stringify(errors));
  console.log('PASS: 22 assets, 4 seated pilots, twin exhaust, damage smoke, contact sparks, inertial engines, explosion cleanup');
} finally { ws?.close(); proc.kill(); }
