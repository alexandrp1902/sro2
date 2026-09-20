// Сквозная проверка галактики без браузера (M7): система и карта в welcome, врата далеко — «подлетите ближе»,
// подготовка прыжка видна в снапшоте, прыжок в соседнюю систему (тот же корабль, топливо списано, корабль у ответных
// врат), ростер старой системы его теряет, радар не присылает далёкие корабли, прыжок назад, заправка в доке.
// Нужен запущенный сервер и Node 24 (встроенный WebSocket). Идёт ~70 с: два перелёта к вратам и к станции.
//   node tools/smoke-jump.mjs [ws://localhost:5000/ws]

import { aroundSun, openSocket, stationAt } from './wire.mjs';

const url = process.argv[2] ?? 'ws://localhost:5000/ws';
const INPUT_INTERVAL_MS = 50;
const CLOSE_HIDDEN = 4000;
const RUN = Math.floor(100 + Math.random() * 900);

class Client {
  constructor(name) {
    this.name = name;
    this.token = Array.from(crypto.getRandomValues(new Uint8Array(16)), (b) => b.toString(16).padStart(2, '0')).join('');
    this.welcome = null;
    this.welcomes = [];
    this.players = null;
    this.hangar = null;
    this.cargo = null;
    this.notices = [];
    this.snapshots = [];
    this.seq = 0;
    this.timer = 0;
    /** () => [dx, dy, тяга] */
    this.steer = () => [0, -1, 0];
    this.listeners = new Set();
  }

  get id() {
    return this.welcome.id;
  }

  get snapshot() {
    return this.snapshots[this.snapshots.length - 1];
  }

  get me() {
    return this.ship(this.id);
  }

  ship(id) {
    return this.snapshot?.ships.find((s) => s.id === id);
  }

  connect() {
    return new Promise((resolve, reject) => {
      const { ws, read } = openSocket(url);
      this.ws = ws;
      // Новое соединение — входы с 1. После прыжка (welcome по тому же сокету) нумерация продолжается, как в клиенте.
      this.seq = 0;
      ws.onopen = () => this.send({ t: 'hello', name: this.name, hull: 'light', weapon: 'pulse', token: this.token });
      ws.onerror = () => reject(new Error(`cannot connect to ${url}`));
      ws.onclose = () => clearInterval(this.timer);
      ws.onmessage = (e) => {
        const message = read(e.data);
        if (message.t === 'welcome') {
          this.welcome = message;
          this.welcomes.push(message);
          // Новая система — снапшоты старой больше ни о чём не говорят (клиент так же сбрасывает мир).
          this.snapshots = [];
          resolve(message);
        } else if (message.t === 'players') this.players = message;
        else if (message.t === 'hangar') this.hangar = message;
        else if (message.t === 'cargo') this.cargo = message;
        else if (message.t === 'notice') this.notices.push(message.code);
        else if (message.t === 'snapshot') {
          this.snapshots.push(message);
          if (this.snapshots.length > 2000) this.snapshots.splice(0, 1000);
        }
        for (const listener of this.listeners) listener();
      };
    });
  }

  send(message) {
    if (this.ws.readyState === WebSocket.OPEN) this.ws.send(JSON.stringify(message));
  }

  start() {
    this.timer = setInterval(() => {
      const [dx, dy, th] = this.steer();
      const length = Math.hypot(dx, dy) || 1;
      this.send({ t: 'input', seq: ++this.seq, dx: dx / length, dy: dy / length, th });
    }, INPUT_INTERVAL_MS);
  }

  /** Долететь до точки: на полной тяге, а в радиусе within — тормозить до остановки. */
  async flyTo(x, y, within, timeoutMs) {
    this.steer = () => {
      const me = this.me;
      if (!me) return [0, -1, 0];
      // Прямо через звезду нельзя — сгорим: сначала в обход.
      const via = aroundSun(this.welcome.system, me, { x, y });
      const final = via.x === x && via.y === y;
      return [via.x - me.x, via.y - me.y, !final || Math.hypot(via.x - me.x, via.y - me.y) > within ? 1 : 0];
    };
    await this.until(
      () => this.me && Math.hypot(x - this.me.x, y - this.me.y) <= within && Math.hypot(this.me.vx, this.me.vy) < 5,
      timeoutMs,
      `reach ${Math.round(x)}, ${Math.round(y)}`,
    );
    this.steer = () => [0, -1, 0];
  }

  until(predicate, timeoutMs, what) {
    return new Promise((resolve, reject) => {
      const cleanup = () => {
        clearTimeout(timer);
        this.listeners.delete(check);
      };
      const check = () => {
        if (!predicate()) return;
        cleanup();
        resolve();
      };
      const timer = setTimeout(() => {
        cleanup();
        reject(new Error(`timeout: ${what}`));
      }, timeoutMs);
      this.listeners.add(check);
      check();
    });
  }

  close() {
    clearInterval(this.timer);
    this.ws.close(CLOSE_HIDDEN, 'hidden');
  }
}

const results = [];

function check(text, pass) {
  results.push(pass);
  console.log(`${pass ? 'OK  ' : 'FAIL'} ${text}`);
}

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

async function main() {
  const a = new Client(`Smoke-J-${RUN}`);
  const b = new Client(`Smoke-W-${RUN}`);
  await a.connect();
  await b.connect();
  a.start();
  b.start();
  await a.until(() => a.hangar && a.me, 3000, 'hangar and own ship');

  const start = a.welcome.system;
  const galaxy = a.welcome.galaxy;
  check(`start system ${start?.name}, danger ${start?.danger}, PvP ${start?.pvp}, station ${start?.station}`, start?.station === true);
  check(`galaxy map: ${galaxy?.systems.length} systems, ${galaxy?.links.length} links`, galaxy?.systems.length >= 2);
  check(`gates: ${start.gates.map((g) => g.name).join(', ')}`, start.gates.length > 0);

  const gate = start.gates[0];
  a.send({ t: 'jump', to: gate.to });
  await a.until(() => a.notices.includes('gateFar'), 2000, 'gateFar notice');
  check('jump far from the gate: "gateFar"', true);

  await a.flyTo(gate.x, gate.y, 120, 60000);
  // Радар (GDD §10): наблюдатель у станции корабль у врат не видит.
  const bDistance = Math.hypot(a.me.x - b.me.x, a.me.y - b.me.y);
  const radar = b.welcome.hulls.light.radar;
  check(`radar ${radar}: the watcher ${Math.round(bDistance)} away does not see the ship`, bDistance > radar + 200 && !b.ship(a.id));

  const creditsBefore = a.cargo.credits;
  a.send({ t: 'jump', to: gate.to });
  await a.until(() => (a.me?.j ?? 0) > 0, 2000, 'jump charging in the snapshot');
  const chargeTicks = a.me.j - a.snapshot.tick;
  check(`charging: jump in ${chargeTicks} ticks (${start.jumpSeconds} s)`, Math.abs(chargeTicks - start.jumpSeconds * 20) <= 2);

  const id = a.id;
  await a.until(() => a.welcome.system.id === gate.to, (start.jumpSeconds + 3) * 1000, `welcome from ${gate.to}`);
  const there = a.welcome.system;
  check(`arrived in ${there.name}: resumed ${a.welcome.resumed}, same id ${a.welcome.id === id}`, a.welcome.resumed && a.welcome.id === id);
  // Топливо отменено (M15.6): прыжок бесплатен, и ни бака, ни счёта он не касается.
  check(`the jump is free: ${creditsBefore} → ${a.cargo.credits} credits`, a.cargo.credits === creditsBefore);
  check('no fuel in the hangar message', a.hangar.fuel === undefined && a.hangar.maxFuel === undefined);
  await a.until(() => a.me, 2000, 'own ship in the new system');
  const back = there.gates.find((g) => g.to === start.id);
  const fromGate = Math.hypot(a.me.x - back.x, a.me.y - back.y);
  check(
    `ship at the answer gate: ${Math.round(fromGate)} from it, speed ${Math.round(Math.hypot(a.me.vx, a.me.vy))}, protected ${a.me.pu > a.snapshot.tick}`,
    fromGate < there.gateRange + 100 && a.me.pu > a.snapshot.tick,
  );
  await b.until(() => b.players && !b.players.players.some((p) => p.id === id), 2000, 'the watcher loses the jumper');
  check(`the watcher's roster lost the ship, galaxy online ${b.players.total}`, b.players.total === 2);

  await a.flyTo(back.x, back.y, 120, 30000);
  a.send({ t: 'jump', to: start.id });
  await a.until(() => a.welcome.system.id === start.id, (start.jumpSeconds + 3) * 1000, 'jump back');
  check(`back in ${start.name}, still ${a.cargo.credits} credits`, a.cargo.credits === creditsBefore);

  // И ещё круг туда-обратно, уже без всяких проверок: без топлива прыгать можно сколько угодно.
  await a.flyTo(gate.x, gate.y, 120, 60000);
  a.send({ t: 'jump', to: gate.to });
  await a.until(() => a.welcome.system.id === gate.to, (start.jumpSeconds + 3) * 1000, 'second jump out');
  await a.until(() => a.me, 2000, 'own ship after the second jump');
  await a.flyTo(back.x, back.y, 120, 30000);
  a.send({ t: 'jump', to: start.id });
  await a.until(() => a.welcome.system.id === start.id, (start.jumpSeconds + 3) * 1000, 'second jump back');
  check(`two round trips on no fuel: ${a.cargo.credits} credits, unchanged`, a.cargo.credits === creditsBefore);

  // Станция на орбите: летим туда, где она сейчас, — за время полёта она уйдёт меньше, чем на круг стыковки.
  await a.until(() => a.snapshot, 2000, 'a snapshot after the jump back');
  const station = stationAt(a.welcome.system, a.snapshot.tick);
  await a.flyTo(station.x, station.y, 60, 60000);
  a.send({ t: 'dock', on: true });
  await a.until(() => a.hangar.docked, 2000, 'docked');
  check(`docked: home ${a.hangar.home}, no fuel price in the shop`, a.hangar.home === start.id && a.welcome.shop?.fuelPrice === undefined);

  a.close();
  b.close();
  await sleep(300); // process.exit, пока сокеты ещё закрываются, роняет Node на Windows (UV_HANDLE_CLOSING)
}

main().then(
  () => process.exit(results.every(Boolean) ? 0 : 1),
  (e) => {
    console.log(`FAIL ${e.message}`);
    process.exit(1);
  },
);
