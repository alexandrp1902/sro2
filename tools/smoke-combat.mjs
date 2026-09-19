// Сквозная проверка боя без браузера: защита после появления, темп огня по кулдауну, щит раньше корпуса,
// смена пушки, сектор стрельбы, снятие защиты собственным выстрелом, дроны в системе.
// В стартовой системе PvP нет (M7), поэтому пилоты сначала летят к вратам и прыгают в соседнюю систему с PvP.
// Нужен запущенный сервер и Node 24 (встроенный WebSocket). Идёт ~60 с: перелёт к вратам и конец защиты.
//   node tools/smoke-combat.mjs [ws://localhost:5000/ws]

import { aroundSun, openSocket } from './wire.mjs';

const url = process.argv[2] ?? 'ws://localhost:5000/ws';
const INPUT_INTERVAL_MS = 50;
const TICK_RATE = 20;
const CLOSE_HIDDEN = 4000;
const RUN = Math.floor(100 + Math.random() * 900);

class Client {
  constructor(name) {
    this.name = name;
    this.token = Array.from(crypto.getRandomValues(new Uint8Array(16)), (b) => b.toString(16).padStart(2, '0')).join('');
    this.welcome = null;
    this.players = [];
    this.snapshots = [];
    this.seq = 0;
    this.timer = 0;
    /** Куда смотреть: () => [dx, dy]; тяга всегда 0 — корабли только разворачиваются. */
    this.aim = () => [0, -1];
    /** Полёт к точке: () => [dx, dy, тяга]; null — стоим и только разворачиваемся. */
    this.steer = null;
    this.listeners = new Set();
  }

  get id() {
    return this.welcome.id;
  }

  get snapshot() {
    return this.snapshots[this.snapshots.length - 1];
  }

  connect() {
    return new Promise((resolve, reject) => {
      const { ws, read } = openSocket(url);
      this.ws = ws;
      ws.onopen = () => this.send({ t: 'hello', name: this.name, hull: 'light', weapon: 'pulse', token: this.token });
      ws.onerror = () => reject(new Error(`cannot connect to ${url}`));
      ws.onclose = () => clearInterval(this.timer);
      ws.onmessage = (e) => {
        const message = read(e.data);
        if (message.t === 'welcome') {
          this.welcome = message;
          resolve(message);
        } else if (message.t === 'players') this.players = message.players;
        else if (message.t === 'snapshot') this.snapshots.push(message);
        for (const listener of this.listeners) listener();
      };
    });
  }

  send(message) {
    if (this.ws.readyState === WebSocket.OPEN) this.ws.send(JSON.stringify(message));
  }

  /** Шлёт вход каждые 50 мс, как настоящий клиент. */
  start() {
    this.timer = setInterval(() => {
      const [dx, dy, th] = this.steer ? this.steer() : [...this.aim(), 0];
      const length = Math.hypot(dx, dy) || 1;
      this.send({ t: 'input', seq: ++this.seq, dx: dx / length, dy: dy / length, th });
    }, INPUT_INTERVAL_MS);
  }

  ship(id) {
    return this.snapshot?.ships.find((s) => s.id === id);
  }

  /** Направление на корабль id (away — от него). */
  face(id, away = false) {
    return () => {
      const me = this.ship(this.id);
      const them = this.ship(id);
      if (!me || !them) return [0, -1];
      const k = away ? -1 : 1;
      return [(them.x - me.x) * k, (them.y - me.y) * k];
    };
  }

  /** Долететь до точки: на полной тяге, а в радиусе within — тормозить до остановки. */
  async flyTo(x, y, within, timeoutMs) {
    this.steer = () => {
      const me = this.ship(this.id);
      if (!me) return [0, -1, 0];
      // Прямо через звезду нельзя — сгорим: сначала в обход.
      const via = aroundSun(this.welcome.system, me, { x, y });
      const dx = via.x - me.x;
      const dy = via.y - me.y;
      const final = via.x === x && via.y === y;
      return [dx, dy, !final || Math.hypot(dx, dy) > within ? 1 : 0];
    };
    await this.until(
      () => {
        const me = this.ship(this.id);
        return me && Math.hypot(x - me.x, y - me.y) <= within && Math.hypot(me.vx, me.vy) < 5;
      },
      timeoutMs,
      `${this.name} reaches ${Math.round(x)}, ${Math.round(y)}`,
    );
    this.steer = null;
  }

  /** К вратам в систему to и прыжок: ждём welcome уже из той системы. */
  async jumpTo(to) {
    const gate = this.welcome.system.gates.find((g) => g.to === to);
    await this.flyTo(gate.x, gate.y, 120, 60000);
    this.send({ t: 'jump', to });
    await this.until(() => this.welcome.system?.id === to, 10000, `${this.name} jumps to ${to}`);
  }

  /** Выстрелы корабля from начиная с тика sinceTick — по снапшотам этого клиента. */
  shots(from, sinceTick = 0) {
    return this.snapshots
      .filter((s) => s.tick >= sinceTick)
      .flatMap((s) => (s.shots ?? []).filter((shot) => shot.from === from).map((shot) => ({ tick: s.tick, ...shot, ships: s.ships })));
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
    this.send({ t: 'fire', on: false });
    this.ws.close(CLOSE_HIDDEN, 'hidden');
  }
}

const results = [];

function check(text, pass) {
  results.push(pass);
  console.log(`${pass ? 'OK  ' : 'FAIL'} ${text}`);
}

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
const cooldownTicks = (weapon) => Math.max(1, Math.ceil(weapon.cooldown * TICK_RATE - 1e-9));
const gaps = (shots) => shots.slice(1).map((shot, i) => shot.tick - shots[i].tick);

async function main() {
  const a = new Client(`Smoke-A-${RUN}`);
  const b = new Client(`Smoke-B-${RUN}`);
  await a.connect();
  await b.connect();
  const { combat: rules, weapons, hulls } = a.welcome;
  const idA = a.id;
  const idB = b.id;

  const drones = a.players.filter((p) => p.npc && (p.kind ?? 'drone') === 'drone');
  check(`drones in the system: ${drones.map((d) => d.name).join(', ') || 'none'}`, drones.length === (rules.drones?.length ?? 0));

  // Бой — в системе, где PvP есть хотя бы вне станции: в стартовой его нет (GDD §34).
  const c = new Client(`Smoke-C-${RUN}`);
  await c.connect();
  const start = a.welcome.system;
  check(`start system ${start?.name}: PvP ${start?.pvp}`, start?.pvp === 'off');
  const arena = start.gates.find((g) => g.to === 'vega') ?? start.gates[0];
  for (const client of [a, b, c]) client.start();
  await Promise.all([a, b, c].map((client) => client.jumpTo(arena.to)));
  check(`all three jumped to ${a.welcome.system.name} (PvP ${a.welcome.system.pvp})`, a.welcome.system.pvp !== 'off');
  // Все трое у одних врат: разводим A вперёд, чтобы B и C целились не в точку.
  const arrival = a.ship(idA);
  await a.flyTo(arrival.x * 0.93, arrival.y * 0.93, 30, 15000);

  a.aim = a.face(idB);
  b.aim = b.face(idA);
  c.aim = c.face(idA);
  await a.until(() => a.ship(idA) && a.ship(idB), 2000, 'both ships in the snapshot');

  // Защита после прыжка у A и B могла ещё не кончиться: огонь включаем сразу, выстрелы начнутся без неё.
  a.send({ t: 'target', id: idB });
  a.send({ t: 'fire', on: true });
  if (b.ship(idB)?.pu > b.snapshot.tick) {
    await sleep(500);
    check('no shots at a protected ship', a.shots(idA).length === 0);
  }

  await a.until(() => !a.ship(idA)?.pu && !a.ship(idB)?.pu, (rules.protectionSeconds + 2) * 1000, 'protection ends');
  const pulseStart = a.snapshot.tick;
  await sleep(3300);
  const pulse = a.shots(idA, pulseStart);
  const pulseTicks = cooldownTicks(weapons.pulse);
  check(
    `pulse: ${pulse.length} shots, gaps ${gaps(pulse).join(', ')} ticks (cooldown ${pulseTicks})`,
    pulse.length >= 3 && gaps(pulse).every((gap) => gap === pulseTicks),
  );

  if (weapons.laser) {
    a.send({ t: 'weapon', id: 'laser' });
    await a.until(() => a.ship(idA)?.w === 'laser', 2000, 'weapon switched to laser');
    const laserStart = a.snapshot.tick;
    await sleep(2200);
    const laser = a.shots(idA, laserStart).filter((shot) => shot.w === 'laser');
    const laserTicks = cooldownTicks(weapons.laser);
    check(
      `laser: ${laser.length} shots, gaps ${gaps(laser).join(', ')} ticks (cooldown ${laserTicks})`,
      laser.length >= 3 && gaps(laser).every((gap) => gap === laserTicks),
    );
  }

  // Корпус получает урон только после того, как щит кончился.
  const hits = a.shots(idA).filter((shot) => shot.hit);
  const shieldFirst = hits.every((shot) => {
    const target = shot.ships.find((s) => s.id === idB);
    return shot.dmg - shot.sh === 0 || target.sh === 0;
  });
  const bNow = a.ship(idB);
  check(
    `${hits.length} hits; shield before hull: B has shield ${bNow.sh}/${hulls.light.shield}, hull ${bNow.hp}/${hulls.light.hp}`,
    hits.length > 0 && shieldFirst,
  );

  a.aim = a.face(idB, true);
  await sleep(1500); // лёгкий разворачивается на 180° за 1,2 с
  const awayStart = a.snapshot.tick;
  await sleep(1000);
  const arc = weapons[a.ship(idA).w].arc;
  // B мог и погибнуть от предыдущих попаданий — тогда стрелять не по кому, это не провал сектора.
  const bDown = (a.ship(idB)?.rt ?? 0) > 0;
  if (arc >= 180) check(`turned away: an all-around gun keeps firing${bDown ? ' (B is down)' : ''}`, bDown || a.shots(idA, awayStart).length > 0);
  else check(`turned away: no shots outside the ±${arc}° arc`, a.shots(idA, awayStart).length === 0);
  a.send({ t: 'fire', on: false });

  // Защита C после прыжка давно кончилась: прыжок туда и обратно даёт новую — её и проверяем.
  await c.jumpTo(start.id);
  await c.jumpTo(arena.to);
  c.aim = c.face(idA);
  await c.until(() => c.ship(c.id) && c.ship(idA), 2000, 'C in the snapshot');
  if (rules.protectionSeconds >= 2) {
    check('C is protected after the jump', c.ship(c.id).pu > c.snapshot.tick);
    c.send({ t: 'target', id: idA });
    c.send({ t: 'fire', on: true });
    await c.until(() => c.shots(c.id).length > 0, 4000, 'C fires at A');
    await c.until(() => !c.ship(c.id)?.pu, 1000, 'C loses protection');
    check('C loses its protection with its first shot', true);
  }

  for (const client of [a, b, c]) client.close();
  await sleep(300); // process.exit, пока сокеты ещё закрываются, роняет Node на Windows (UV_HANDLE_CLOSING)
}

main().then(
  () => process.exit(results.every(Boolean) ? 0 : 1),
  (e) => {
    console.log(`FAIL ${e.message}`);
    process.exit(1);
  },
);
