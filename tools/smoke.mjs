// Сквозная проверка полёта без браузера: подключается к серверу, 2,5 с летит вверх на полной тяге,
// потом стоп, и сверяет снапшоты с моделью. Нужен запущенный сервер и Node 24 (встроенный WebSocket).
//   node tools/smoke.mjs [ws://localhost:5000/ws]

import { openSocket } from './wire.mjs';

const url = process.argv[2] ?? 'ws://localhost:5000/ws';
const HULL = 'light';
const THRUST_MS = 2500;
const TOTAL_MS = 5500;
const INPUT_INTERVAL_MS = 50;

const { ws, read } = openSocket(url);
const own = [];
let playerId = 0;
let hull = null;
let seq = 0;
let started = 0;
let timer = 0;

ws.onopen = () => ws.send(JSON.stringify({ t: 'hello', name: 'smoke', hull: HULL }));
ws.onerror = () => fail(`cannot connect to ${url}`);
ws.onmessage = (e) => {
  const message = read(e.data);
  if (message.t === 'welcome') {
    playerId = message.id;
    hull = message.hulls[HULL];
    // Вход с M15.6 всегда в доке, а мерить разгон надо в космосе.
    ws.send(JSON.stringify({ t: 'dock', on: false }));
    started = performance.now();
    timer = setInterval(sendInput, INPUT_INTERVAL_MS);
    setTimeout(finish, TOTAL_MS);
  } else if (message.t === 'snapshot') {
    const ship = message.ships.find((s) => s.id === playerId);
    if (ship) own.push({ time: performance.now() - started, tick: message.tick, ...ship });
  }
};

function sendInput() {
  const thrusting = performance.now() - started < THRUST_MS;
  seq++;
  ws.send(JSON.stringify({ t: 'input', seq, dx: 0, dy: -1, th: thrusting ? 1 : 0 }));
}

function finish() {
  clearInterval(timer);
  ws.close();
  if (own.length < 10) fail('too few snapshots of our ship');

  const first = own[0];
  const last = own[own.length - 1];
  const speed = (s) => Math.hypot(s.vx, s.vy);
  const tickRate = ((last.tick - first.tick) * 1000) / (last.time - first.time);
  const topSpeed = Math.max(...own.map(speed));
  const acksGrow = own.every((s, i) => i === 0 || s.ack >= own[i - 1].ack);

  const checks = [
    [`tick rate ${tickRate.toFixed(2)}/s ≈ 20`, Math.abs(tickRate - 20) < 0.5],
    [`hull ${last.hull}`, last.hull === HULL],
    [`top speed ${topSpeed.toFixed(3)} = maxSpeed ${hull.maxSpeed}`, Math.abs(topSpeed - hull.maxSpeed) < 1e-6],
    [`stopped after throttle 0: speed ${speed(last)}`, speed(last) === 0],
    [`flew up: y ${first.y.toFixed(0)} → ${last.y.toFixed(0)}`, last.y < first.y - 300],
    [`ack only grows: last ack ${last.ack} of seq ${seq}`, acksGrow && seq - last.ack <= 6],
  ];
  let ok = true;
  for (const [text, pass] of checks) {
    console.log(`${pass ? 'OK  ' : 'FAIL'} ${text}`);
    ok &&= pass;
  }
  process.exit(ok ? 0 : 1);
}

function fail(reason) {
  console.log(`FAIL ${reason}`);
  process.exit(1);
}
