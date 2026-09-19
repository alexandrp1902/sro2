// Сквозная проверка обучения и заданий без браузера (M8): новый пилот начинает в доке, проходит пять шагов обучения —
// вылет, учебный дрон, подбор его груза, продажа на станции, прыжок в Vega, — получает награды; затем берёт задание
// на станции Vega: доставку в Sol везёт и сдаёт, любое другое берёт и бросает.
// Нужен запущенный сервер и Node 24 (встроенный WebSocket). Заводит аккаунт smk-<число>. Идёт 1–3 минуты.
//   node tools/smoke-missions.mjs [ws://localhost:5000/ws]

import { aroundSun, openSocket, stationAt } from './wire.mjs';

const url = process.argv[2] ?? 'ws://localhost:5000/ws';
const INPUT_INTERVAL_MS = 50;
const CLOSE_HIDDEN = 4000;
const RUN = Math.floor(10000 + Math.random() * 90000);

class Client {
  constructor(hello) {
    this.hello = hello;
    this.welcome = null;
    this.players = null;
    this.hangar = null;
    this.cargo = null;
    this.missions = null;
    this.done = [];
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
      ws.onopen = () => this.send({ t: 'hello', ...this.hello });
      ws.onerror = () => reject(new Error(`cannot connect to ${url}`));
      ws.onclose = () => clearInterval(this.timer);
      ws.onmessage = (e) => {
        const message = read(e.data);
        if (message.t === 'welcome') {
          this.welcome = message;
          this.snapshots = [];
          resolve(message);
        } else if (message.t === 'players') this.players = message;
        else if (message.t === 'hangar') {
          // Вылет: сервер начинает буфер входов заново.
          if (this.hangar?.docked && !message.docked) this.seq = 0;
          this.hangar = message;
        } else if (message.t === 'cargo') this.cargo = message;
        else if (message.t === 'missions') {
          this.missions = message;
          if (message.done) this.done.push(message.done);
        } else if (message.t === 'notice') this.notices.push(message.code);
        else if (message.t === 'denied') reject(new Error(`denied: ${message.code}`));
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
      if (this.hangar?.docked) return; // в доке входы не нужны
      const [dx, dy, th] = this.steer();
      const length = Math.hypot(dx, dy) || 1;
      this.send({ t: 'input', seq: ++this.seq, dx: dx / length, dy: dy / length, th });
    }, INPUT_INTERVAL_MS);
  }

  /** Долететь до точки (или движущейся цели — функции): на полной тяге, в радиусе within — тормозить. */
  async flyTo(target, within, timeoutMs) {
    const at = () => (typeof target === 'function' ? target() : target);
    this.steer = () => {
      const me = this.me;
      const to = at();
      if (!me || !to) return [0, -1, 0];
      const via = aroundSun(this.welcome.system, me, to);
      const final = via.x === to.x && via.y === to.y;
      return [via.x - me.x, via.y - me.y, !final || Math.hypot(via.x - me.x, via.y - me.y) > within ? 1 : 0];
    };
    await this.until(
      () => {
        const me = this.me;
        const to = at();
        return me && to && Math.hypot(to.x - me.x, to.y - me.y) <= within && Math.hypot(me.vx, me.vy) < 5;
      },
      timeoutMs,
      'reach the target',
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

  /** Долететь до станции, пристыковаться. */
  async dock() {
    await this.until(() => this.snapshot, 3000, 'a snapshot');
    await this.flyTo(() => stationAt(this.welcome.system, this.snapshot.tick), 60, 90000);
    this.send({ t: 'dock', on: true });
    await this.until(() => this.hangar.docked, 3000, 'docked');
  }

  /** Долететь до врат в систему to и прыгнуть. */
  async jump(to) {
    const gate = this.welcome.system.gates.find((g) => g.to === to);
    await this.flyTo(gate, 120, 90000);
    this.send({ t: 'jump', to });
    await this.until(() => this.welcome.system.id === to, 8000, `jump to ${to}`);
    await this.until(() => this.me, 3000, 'own ship after the jump');
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
const step = (a) => a.missions?.tutorial?.id ?? 'done';

async function main() {
  const a = new Client({ name: `smk-${RUN}`, password: 'smoke-password' });
  await a.connect();
  a.start();
  await a.until(() => a.hangar && a.missions && a.cargo, 3000, 'hangar, missions and cargo');
  const credits = a.cargo.credits;
  check(`new pilot starts docked in ${a.welcome.system.name}: docked ${a.hangar.docked}`, a.hangar.docked);
  check(
    `tutorial ${a.missions.tutorial?.step + 1}/${a.missions.tutorial?.total}: ${a.missions.tutorial?.title}`,
    a.missions.tutorial?.id === 'undock',
  );
  check(`station board: ${a.missions.offers.length} offers`, a.missions.offers.length > 0);

  // 1. Вылет.
  a.send({ t: 'dock', on: false });
  await a.until(() => step(a) === 'drone', 3000, 'tutorial: drone');
  check(`undocked → «${a.missions.tutorial.title}»`, true);

  // 2. Учебный дрон — стоит на месте, возле станции.
  await a.until(() => a.players && a.snapshot, 3000, 'roster');
  const drone = a.players.players.find((p) => p.kind === 'drone' && p.name === 'Учебный дрон');
  await a.flyTo(() => a.ship(drone.id), 300, 60000);
  a.send({ t: 'target', id: drone.id });
  a.send({ t: 'fire', on: true });
  await a.until(() => step(a) === 'grab', 60000, 'tutorial: grab');
  a.send({ t: 'fire', on: false });
  check(`training drone destroyed → «${a.missions.tutorial.title}»`, true);

  // 3. Груз с дрона.
  await a.until(() => (a.snapshot?.loot ?? []).length > 0, 3000, 'drone loot in space');
  const me = a.me;
  const drop = [...a.snapshot.loot].sort((p, q) => Math.hypot(p.x - me.x, p.y - me.y) - Math.hypot(q.x - me.x, q.y - me.y))[0];
  a.send({ t: 'loot', id: drop.id });
  await a.flyTo({ x: drop.x, y: drop.y }, 60, 30000);
  a.send({ t: 'grab' });
  await a.until(() => step(a) === 'sell', 5000, 'tutorial: sell');
  check(`picked up ${drop.i} ×${drop.n} → «${a.missions.tutorial.title}»`, true);

  // 4. Продажа на станции.
  await a.dock();
  a.send({ t: 'sell' });
  await a.until(() => step(a) === 'jump', 3000, 'tutorial: jump');
  check(`sold at the station → «${a.missions.tutorial.title}»`, true);

  // 5. Прыжок в Vega.
  a.send({ t: 'dock', on: false });
  await a.until(() => !a.hangar.docked && a.me, 3000, 'undocked again');
  await a.jump('vega');
  await a.until(() => step(a) === 'done', 3000, 'tutorial done');
  const last = a.done[a.done.length - 1];
  const rewards = a.done.filter((d) => d.kind === 'tutorial').reduce((sum, d) => sum + d.reward, 0);
  check(`jumped to Vega: tutorial finished (last ${last?.last}), rewards ${rewards} credits`, last?.last === true && rewards > 0);
  await a.until(() => a.cargo.credits > credits + rewards - 1, 3000, 'credits');
  check(`credits ${credits} → ${a.cargo.credits}`, a.cargo.credits >= credits + rewards);

  // Задание на станции Vega.
  await a.dock();
  await a.until(() => a.missions.offers.length > 0, 3000, 'Vega board');
  const board = a.missions.offers;
  console.log(`     Vega board: ${board.map((o) => `${o.kind}${o.system ? `→${o.system}` : ''}${o.item ? `:${o.item}` : ''}×${o.count} ${o.reward} кр`).join(', ')}`);
  const deliver = board.find((o) => o.kind === 'deliver' && o.system === 'sol');
  const offer = deliver ?? board[0];
  a.send({ t: 'mission', action: 'accept', id: offer.id });
  await a.until(() => a.missions.active?.offer.id === offer.id, 3000, 'mission taken');
  check(`took ${offer.kind} for ${offer.reward} credits; board refreshed`, !a.missions.offers.some((o) => o.id === offer.id));

  if (deliver) {
    await a.until(() => a.cargo.reserved === deliver.count, 3000, 'reserved hold');
    check(`delivery cargo takes ${a.cargo.reserved} of the hold`, true);
    a.send({ t: 'dock', on: false });
    await a.until(() => !a.hangar.docked && a.me, 3000, 'undocked');
    const before = a.cargo.credits;
    await a.jump('sol');
    await a.dock();
    await a.until(() => a.missions.active === null, 3000, 'delivery done');
    check(`delivered to Sol: ${before} → ${a.cargo.credits} credits, hold reserve ${a.cargo.reserved}`,
      a.cargo.credits === before + deliver.reward && a.cargo.reserved === 0);
  } else {
    a.send({ t: 'mission', action: 'abandon' });
    await a.until(() => a.missions.active === null, 3000, 'abandoned');
    check('no delivery to Sol on the board: took another one and abandoned it', true);
  }

  a.close();
  await sleep(300); // process.exit, пока сокеты ещё закрываются, роняет Node на Windows (UV_HANDLE_CLOSING)
}

main().then(
  () => process.exit(results.every(Boolean) ? 0 : 1),
  (e) => {
    console.log(`FAIL ${e.message}`);
    process.exit(1);
  },
);
