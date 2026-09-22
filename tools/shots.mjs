// Скриншот витрины (?demo=<экран>) headless Edge через CDP — для проходов по дизайну без сервера игры.
// Сначала: cd client && npm run build && npx vite preview --port 5055. Потом:
//   node tools/shots.mjs out.png "http://localhost:5055/?demo=galaxy" 1280 720
//   node tools/shots.mjs out.png "http://localhost:5055/?demo=flight" 390 844 touch        — телефон (pointer: coarse)
//   node tools/shots.mjs out.png <url> 1280 720 "" "document.title"                          — ещё и выражение JS в stdout
//   node tools/shots.mjs out.png <url> 1280 720 "" "" "1128,40,150,150,4"                    — вырезка x,y,w,h и масштаб
// Node 24+: WebSocket и fetch встроены. Edge ищется по стандартному пути установки.
import { spawn } from 'node:child_process';
import { existsSync, writeFileSync } from 'node:fs';

const [out, url, w, h, touch, expr, clipArg] = process.argv.slice(2);
if (!out || !url || !w || !h) {
  console.error('node tools/shots.mjs <out.png> <url> <width> <height> [touch] [js] [x,y,w,h,scale]');
  process.exit(2);
}
const clip = clipArg ? (([x, y, width, height, scale]) => ({ x, y, width, height, scale }))(clipArg.split(',').map(Number)) : undefined;
const edge = ['C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe', 'C:\\Program Files\\Microsoft\\Edge\\Application\\msedge.exe'].find(existsSync);
if (!edge) {
  console.error('Edge не найден');
  process.exit(2);
}
const port = 9333 + Math.floor(Math.random() * 500);
const dir = `${process.env.TEMP}\\sro-shots-${port}`;
const proc = spawn(edge, ['--headless=new', '--disable-gpu', '--no-first-run', `--remote-debugging-port=${port}`, `--user-data-dir=${dir}`, 'about:blank'], { stdio: 'ignore' });

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
let version = null;
for (let i = 0; i < 50 && !version; i++) {
  try {
    version = await (await fetch(`http://127.0.0.1:${port}/json/version`)).json();
  } catch {
    await sleep(200);
  }
}
if (!version) {
  proc.kill();
  console.error('Edge не поднял отладочный порт');
  process.exit(1);
}
const ws = new WebSocket(version.webSocketDebuggerUrl);
await new Promise((r) => (ws.onopen = r));
let id = 0;
const pending = new Map();
ws.onmessage = (e) => {
  const m = JSON.parse(e.data);
  if (m.id && pending.has(m.id)) {
    pending.get(m.id)(m);
    pending.delete(m.id);
  }
};
const send = (method, params = {}, sessionId) =>
  new Promise((r) => {
    const n = ++id;
    pending.set(n, r);
    ws.send(JSON.stringify({ id: n, method, params, sessionId }));
  });

const { result: { targetId } } = await send('Target.createTarget', { url: 'about:blank' });
const { result: { sessionId } } = await send('Target.attachToTarget', { targetId, flatten: true });
const s = (m, p) => send(m, p, sessionId);
await s('Emulation.setDeviceMetricsOverride', { width: +w, height: +h, deviceScaleFactor: 1, mobile: touch === 'touch' });
if (touch === 'touch') await s('Emulation.setTouchEmulationEnabled', { enabled: true, maxTouchPoints: 5 });
await s('Page.navigate', { url });
await sleep(2500); // шрифт и спрайты подгружаются, HUD успевает собраться
if (expr) {
  const r = await s('Runtime.evaluate', { expression: expr, returnByValue: true });
  console.log(JSON.stringify(r.result?.result?.value ?? r, null, 1));
}
const shot = await s('Page.captureScreenshot', { format: 'png', ...(clip ? { clip } : {}) });
writeFileSync(out, Buffer.from(shot.result.data, 'base64'));
ws.close();
proc.kill();
process.exit(0);
