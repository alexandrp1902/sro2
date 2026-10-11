// Run against Vite: node tools/race-canyon-browser-check.mjs http://127.0.0.1:5066
import { spawn } from 'node:child_process';
import { existsSync, mkdirSync, writeFileSync } from 'node:fs';
import assert from 'node:assert/strict';
const base = process.argv[2] ?? 'http://127.0.0.1:5066', out = '.artifacts/race/canyon';
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
  await evaluate(`(async()=>{await fx.prepare('copper');fx.load(fixture.config);
    window.draw=()=>app.render()})()`);
  for (const [width,height] of [[1280,720],[390,844]]) {
    await cdp('Emulation.setDeviceMetricsOverride',{width,height,deviceScaleFactor:1,mobile:false});
    await evaluate(`app.renderer.resize(${width},${height})`);
    for (const [name,index] of [['start',0],['bend',65],['narrow',160]]) {
      const position = name === 'start' ? 'spawn' : `centerline[${index}]`;
      await evaluate(`b.position={...fixture.config.track.${position}};
        b.velocity={x:0,y:0};fx.camera={...b.position};fx.frame(clock+=16,${width},${height},b,input,false)`);
      await shot(`${name}-${width}x${height}`);
    }
  }
  assert(await evaluate(`!('marker' in fx)&&fx.banks.children.length>500&&fx.floorShadow.mask===fx.floorMask`));
  await evaluate('fx.close()');
  assert(await evaluate('fx.banks.children.length===0&&fx.floorShadow.context.instructions.length===0'));
  assert.equal(errors.length,0,JSON.stringify(errors));
  console.log('PASS: canyon start, bend and narrow pass at desktop/mobile sizes; no player ring; scenery cleanup; no browser exceptions');
} finally { ws?.close(); proc.kill(); }

