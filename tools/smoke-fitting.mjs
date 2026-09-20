// Сквозная проверка оснащения без браузера (M9): слоты, классы и энергия генератора, две пушки стреляют каждая сама,
// ракетница запускает ракету, и та попадает в учебный дрон; торговцы летают по системе.
// Гость ставит что угодно где угодно — склад и магазин проверяет smoke-account.mjs.
// Нужен запущенный сервер и Node 24 (встроенный WebSocket). Идёт около минуты.
//   node tools/smoke-fitting.mjs [ws://localhost:5000/ws]

import { aroundSun, openSocket, undock } from './wire.mjs';

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
      ws.onopen = () => this.send({ t: 'hello', ...this.hello });
      ws.onerror = () => reject(new Error(`cannot connect to ${url}`));
      ws.onclose = () => clearInterval(this.timer);
      ws.onmessage = (e) => {
        const message = read(e.data);
        if (message.t === 'welcome') {
          this.welcome = message;
          resolve(message);
        } else if (message.t === 'players') this.players = message;
        else if (message.t === 'hangar') this.hangar = message;
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

  /**
   * Держать нос на цели, не двигаясь. Ракетнице нужен сектор ±90° от носа (arc 90), и без этого проверка
   * зависела от того, куда корабль смотрел, когда добрался до дрона, — а дрон к тому же успевал погибнуть
   * и появиться в другом месте.
   */
  faceTarget(target) {
    this.steer = () => {
      const me = this.ship(this.id);
      const to = target();
      if (!me || !to) return [0, -1, 0];
      return [to.x - me.x, to.y - me.y, 0];
    };
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

  /** Выстрелы этого корабля с тика since: { tick, w, hit }. */
  shots(since) {
    return this.snapshots
      .filter((s) => s.tick >= since)
      .flatMap((s) => (s.shots ?? []).filter((shot) => shot.from === this.id).map((shot) => ({ tick: s.tick, ...shot })));
  }

  /** Поставить в слот и дождаться ангара, где это уже стоит (или уведомления об отказе). */
  async fit(slot, id) {
    const notices = this.notices.length;
    this.send({ t: 'fit', slot, id });
    await this.until(
      () => this.notices.length > notices || slotOf(this.hangar.fit, slot) === id,
      3000,
      `fit ${id} into ${slot}`,
    );
    return this.notices.length > notices ? this.notices[this.notices.length - 1] : null;
  }

  close() {
    clearInterval(this.timer);
    this.send({ t: 'fire', on: false });
    this.ws.close(CLOSE_HIDDEN, 'hidden');
  }
}

function slotOf(fit, slot) {
  const m = /^w(\d)$/.exec(slot);
  return m ? (fit.weapons[Number(m[1])] ?? null) : (fit[slot] ?? null);
}

const results = [];

function check(text, pass) {
  results.push(pass);
  console.log(`${pass ? 'OK  ' : 'FAIL'} ${text}`);
}

const guns = (hangar) => hangar.fit.weapons.map((w) => w ?? '—').join(', ');

async function main() {
  const a = new Client({ name: `Smoke-Fit-${RUN}`, hull: 'light', weapon: 'pulse', token: `smoke-fitting-${RUN}-token` });
  await a.connect();
  await undock(a, 'a');
  await a.until(() => a.hangar && a.players, 3000, 'hangar and roster');
  const { weapons, modules, hulls } = a.welcome;
  check(`modules.json in welcome: ${Object.keys(modules ?? {}).length} modules`, Object.keys(modules ?? {}).length >= 15);
  check(
    `starter fit: ${guns(a.hangar)}; ${a.hangar.fit.engine}, ${a.hangar.fit.shield}, ${a.hangar.fit.radar}, ${a.hangar.fit.generator}; energy ${a.hangar.power}/${a.hangar.powerMax}`,
    a.hangar.fit.weapons[0] === 'pulse' && a.hangar.powerMax > a.hangar.power && a.hangar.fit.tank === undefined,
  );
  const traders = a.players.players.filter((p) => p.kind === 'trader');
  check(`traders in ${a.welcome.system.name}: ${traders.length} (${traders.map((t) => t.name).join(', ')})`, traders.length > 0);

  // Слоты и классы.
  check(`second pulse into the second slot: ${(await a.fit('w1', 'pulse')) ?? guns(a.hangar)}`, a.hangar.fit.weapons[1] === 'pulse');
  const launcher = Object.keys(weapons).find((id) => weapons[id].missile);
  check(`launcher in weapons.json: ${launcher} (${weapons[launcher]?.class})`, !!launcher);
  check(`${launcher} does not fit a light S slot: ${await a.fit('w0', launcher)}`, a.notices.at(-1) === 'badClass');

  // Две пушки по учебному дрону: каждая стреляет сама — первый залп обеими сразу.
  a.start();
  await a.until(() => a.me, 3000, 'own ship in a snapshot');
  const drone = a.players.players.find((p) => p.kind === 'drone' && p.name === 'Учебный дрон') ?? a.players.players.find((p) => p.kind === 'drone');
  if (!drone) throw new Error('no drone in the start system');
  console.log('     flying to the training drone');
  await a.flyTo(() => a.ship(drone.id), 450, 60000);
  let since = a.snapshot.tick;
  a.send({ t: 'target', id: drone.id });
  a.send({ t: 'fire', on: true });
  await a.until(() => a.shots(since).length >= 2, 4000, 'the first volley');
  a.send({ t: 'fire', on: false });
  const ticks = new Map();
  for (const shot of a.shots(since)) ticks.set(shot.tick, (ticks.get(shot.tick) ?? 0) + 1);
  check(`two pulses: ${a.shots(since).length} shots, both guns in one tick: ${[...ticks.values()].some((n) => n >= 2)}`, [...ticks.values()].some((n) => n >= 2));

  // Средний корпус: слот M — ракетница встаёт, энергии впритык.
  a.send({ t: 'hull', id: 'medium' });
  await a.until(() => a.hangar.hull === 'medium', 3000, 'medium hull');
  check(`medium hull: slots ${hulls.medium.weaponSlots?.join(' ')}, fit carried over: ${guns(a.hangar)}`, a.hangar.fit.weapons[1] === 'pulse');
  const answer = await a.fit('w0', launcher);
  check(`${launcher} into the M slot: ${answer ?? guns(a.hangar)}, energy ${a.hangar.power}/${a.hangar.powerMax}`, a.hangar.fit.weapons[0] === launcher);
  const over = await a.fit('w2', 'pulse');
  check(`a third gun is over the generator: ${over}, energy ${a.hangar.power}/${a.hangar.powerMax}`, over === 'noPower' || a.hangar.power <= a.hangar.powerMax);

  // Ракетница и пушка по тому же дрону: ракета летит сама и бьёт при касании.
  await a.until(() => !a.ship(drone.id)?.rt, 15000, 'the drone is back');
  // Дрон, погибнув, появился заново — возможно, в другой точке: подлетаем снова и держим нос на нём.
  await a.flyTo(() => a.ship(drone.id), 450, 60000);
  a.faceTarget(() => a.ship(drone.id));
  await new Promise((resolve) => setTimeout(resolve, 1500)); // дать кораблю довернуть
  since = a.snapshot.tick;
  a.send({ t: 'target', id: drone.id });
  a.send({ t: 'fire', on: true });
  await a.until(() => a.snapshots.some((s) => s.tick >= since && (s.missiles ?? []).some((m) => m.o === a.id)), 8000, 'a missile in flight');
  const missile = a.snapshots.flatMap((s) => s.missiles ?? []).find((m) => m.o === a.id);
  check(`missile launched: #${missile.id} ${missile.w} → ${missile.t}`, missile.t === drone.id && missile.w === launcher);
  await a.until(() => a.shots(since).some((shot) => shot.w === launcher), 6000, 'the missile hits');
  const hit = a.shots(since).find((shot) => shot.w === launcher);
  check(`missile hit the drone for ${hit.dmg} (shield ${hit.sh})`, hit.hit && hit.dmg > 0);
  const pulses = a.shots(since).filter((shot) => shot.w === 'pulse');
  check(`the pulse in the other slot fires too: ${pulses.length} shots`, pulses.length > 0);
  a.close();
}

try {
  await main();
} catch (e) {
  check(e.message, false);
}
const failed = results.filter((ok) => !ok).length;
console.log(failed === 0 ? `\nAll ${results.length} checks passed` : `\n${failed} of ${results.length} checks failed`);
process.exit(failed === 0 ? 0 : 1);
