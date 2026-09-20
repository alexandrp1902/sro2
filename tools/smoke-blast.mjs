// Сквозная проверка урона по площади (M15.5): плазма рвётся в точке попадания и задевает соседа,
// сама цель получает только прямой урон, а промах не взрывается.
// В стартовой системе PvP нет (M7), поэтому все трое прыгают в соседнюю систему с PvP.
// Нужен запущенный сервер и Node 24 (встроенный WebSocket). Идёт ~1 мин: перелёт к вратам и конец защиты.
//   node tools/smoke-blast.mjs [ws://localhost:5000/ws]

import { aroundSun, openSocket, undock } from './wire.mjs';

const url = process.argv[2] ?? 'ws://localhost:5000/ws';
const INPUT_INTERVAL_MS = 50;
const CLOSE_HIDDEN = 4000;
const SPLASH_WEAPON = 'splash';
const RUN = Math.floor(100 + Math.random() * 900);

class Client {
  constructor(name, weapon = 'pulse', hull = 'light') {
    this.name = name;
    this.weapon = weapon;
    this.hull = hull;
    this.token = Array.from(crypto.getRandomValues(new Uint8Array(16)), (b) => b.toString(16).padStart(2, '0')).join('');
    this.welcome = null;
    this.snapshots = [];
    this.seq = 0;
    this.timer = 0;
    this.aim = () => [0, -1];
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
      ws.onopen = () => this.send({ t: 'hello', name: this.name, hull: this.hull, weapon: this.weapon, token: this.token });
      ws.onerror = () => reject(new Error(`cannot connect to ${url}`));
      ws.onclose = () => clearInterval(this.timer);
      ws.onmessage = (e) => {
        const message = read(e.data);
        if (message.t === 'welcome') {
          this.welcome = message;
          resolve(message);
        } else if (message.t === 'snapshot') this.snapshots.push(message);
        for (const listener of this.listeners) listener();
      };
    });
  }

  send(message) {
    if (this.ws.readyState === WebSocket.OPEN) this.ws.send(JSON.stringify(message));
  }

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

  face(id) {
    return () => {
      const me = this.ship(this.id);
      const them = this.ship(id);
      if (!me || !them) return [0, -1];
      return [them.x - me.x, them.y - me.y];
    };
  }

  async flyTo(x, y, within, timeoutMs) {
    this.steer = () => {
      const me = this.ship(this.id);
      if (!me) return [0, -1, 0];
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

  async jumpTo(to) {
    const gate = this.welcome.system.gates.find((g) => g.to === to);
    // «Странник» медленнее лёгкого корпуса, и врата бывают на другом краю системы — запас по времени.
    await this.flyTo(gate.x, gate.y, 120, 90000);
    this.send({ t: 'jump', to });
    await this.until(() => this.welcome.system?.id === to, 10000, `${this.name} jumps to ${to}`);
  }

  /** Выстрелы корабля from начиная с тика sinceTick — по снапшотам этого клиента. */
  shots(from, sinceTick = 0) {
    return this.snapshots
      .filter((s) => s.tick >= sinceTick)
      .flatMap((s) => (s.shots ?? []).filter((shot) => shot.from === from).map((shot) => ({ tick: s.tick, ...shot })));
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

async function main() {
  // Плазма — класса M: лёгкий корпус её не возьмёт, и оснащение молча откатится к импульсной.
  const a = new Client(`Blast-A-${RUN}`, 'plasma', 'medium');
  const b = new Client(`Blast-B-${RUN}`);
  const c = new Client(`Blast-C-${RUN}`);
  await a.connect();
  await undock(a, 'a');
  await b.connect();
  await undock(b, 'b');
  await c.connect();
  await undock(c, 'c');
  const { weapons, combat: rules } = a.welcome;

  const plasma = weapons.plasma;
  check(
    `plasma carries a blast: radius ${plasma?.blastRadius ?? 0}, share ${plasma?.blastShare ?? 0}`,
    (plasma?.blastRadius ?? 0) > 0 && (plasma?.blastShare ?? 0) > 0,
  );
  check('a single-target gun has no blast: railgun', !(weapons.railgun?.blastRadius > 0));

  const start = a.welcome.system;
  const arena = start.gates.find((g) => g.to === 'vega') ?? start.gates[0];
  for (const client of [a, b, c]) client.start();
  await Promise.all([a, b, c].map((client) => client.jumpTo(arena.to)));
  check(`all three jumped to ${a.welcome.system.name} (PvP ${a.welcome.system.pvp})`, a.welcome.system.pvp !== 'off');

  // Куча: B и C рядом, ближе радиуса взрыва; A — поодаль, но в дальности плазмы.
  // Ждём свежий кадр уже из новой системы: сразу после прыжка в снапшоте ещё координаты старой.
  await sleep(700);
  const arrival = b.ship(b.id);
  const base = { x: arrival.x * 0.92, y: arrival.y * 0.92 };
  const gap = Math.round(plasma.blastRadius * 0.5);
  await Promise.all([
    b.flyTo(base.x, base.y, 70, 40000),
    c.flyTo(base.x + gap, base.y, 70, 40000),
    a.flyTo(base.x, base.y + 450, 70, 40000),
  ]);

  const idA = a.id;
  const idB = b.id;
  const idC = c.id;
  a.aim = a.face(idB);
  await a.until(() => a.ship(idB) && a.ship(idC), 4000, 'both targets in the snapshot');
  check(`A carries ${a.ship(idA)?.w}`, a.ship(idA)?.w === 'plasma');
  await a.until(
    () => !a.ship(idA)?.pu && !a.ship(idB)?.pu && !a.ship(idC)?.pu,
    (rules.protectionSeconds + 3) * 1000,
    'protection ends for all three',
  );

  const since = a.snapshot.tick;
  a.send({ t: 'target', id: idB });
  a.send({ t: 'fire', on: true });
  // Плазма мажет часто (точность 60 против уклонения): ждём именно попадания, а не выстрела, —
  // взрыв считается от попадания по цели, и по промахам проверять нечего.
  await a.until(
    () => a.shots(idA, since).some((s) => s.hit && s.w === 'plasma'),
    30000,
    'plasma lands a hit on B',
  );
  await sleep(400); // взрыв уезжает тем же кадром, но дадим дойти снапшоту
  a.send({ t: 'fire', on: false });

  const mine = a.shots(idA, since);
  const direct = mine.filter((s) => s.w === 'plasma');
  const splash = mine.filter((s) => s.w === SPLASH_WEAPON);
  const hits = direct.filter((s) => s.hit);
  check(`A fired ${direct.length} times with plasma, ${hits.length} on target`, hits.length > 0);
  // Рядом могут оказаться и пираты: считаем именно записи по соседу, а не все подряд.
  const onNeighbour = splash.filter((s) => s.to === idC);
  check(
    `the blast reached the neighbour ${gap} away: ${onNeighbour.length} of ${splash.length} splash records`,
    onNeighbour.length > 0,
  );
  check('the target itself gets no splash record, only the direct hit', !splash.some((s) => s.to === idB));
  check('the shooter is never splashed by its own blast', !splash.some((s) => s.to === idA));

  // Промах не взрывается: у каждого взрыва есть попадание в том же тике.
  const hitTicks = new Set(direct.filter((s) => s.hit).map((s) => s.tick));
  check('every blast comes from a hit, never from a miss', splash.every((s) => hitTicks.has(s.tick)));

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
