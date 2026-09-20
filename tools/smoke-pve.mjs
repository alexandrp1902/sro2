// Сквозная проверка пиратов без браузера: пираты в системе и на карте, агро на игрока у логова, огонь пирата,
// погоня и уход пирата, когда игрок спрятался в укрытии у станции.
// Нужен запущенный сервер и Node 24 (встроенный WebSocket). Идёт ~40–60 с: полёт к логову и обратно.
//   node tools/smoke-pve.mjs [ws://localhost:5000/ws]

import { openSocket, stationAt, undock } from './wire.mjs';

const url = process.argv[2] ?? 'ws://localhost:5000/ws';
const INPUT_INTERVAL_MS = 50;
const CLOSE_HIDDEN = 4000;
const RUN = Math.floor(100 + Math.random() * 900);
/** Подлетаем к пирату на столько и тормозим: ближе радиуса агро, дальше дистанции его боя. */
const APPROACH = 450;

class Client {
  constructor(name) {
    this.name = name;
    this.token = Array.from(crypto.getRandomValues(new Uint8Array(16)), (b) => b.toString(16).padStart(2, '0')).join('');
    this.welcome = null;
    this.players = [];
    this.snapshots = [];
    this.seq = 0;
    this.timer = 0;
    /** Вход на следующий тик: () => [dx, dy, throttle]. */
    this.control = () => [0, -1, 0];
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

  /** Шлёт вход каждые 50 мс, как настоящий клиент. */
  start() {
    this.timer = setInterval(() => {
      const [dx, dy, th] = this.control();
      const length = Math.hypot(dx, dy) || 1;
      this.send({ t: 'input', seq: ++this.seq, dx: dx / length, dy: dy / length, th });
    }, INPUT_INTERVAL_MS);
  }

  ship(id) {
    return this.snapshot?.ships.find((s) => s.id === id);
  }

  get me() {
    return this.ship(this.id);
  }

  pirates() {
    const ids = new Set(this.players.filter((p) => p.kind === 'pirate').map((p) => p.id));
    return (this.snapshot?.ships ?? []).filter((s) => ids.has(s.id));
  }

  /** Лететь к точке; ближе stopAt — тяга 0. */
  flyTo(point, stopAt) {
    return () => {
      const me = this.me;
      const at = typeof point === 'function' ? point() : point;
      if (!me || !at) return [0, -1, 0];
      const dx = at.x - me.x;
      const dy = at.y - me.y;
      return [dx, dy, Math.hypot(dx, dy) > stopAt ? 1 : 0];
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

const distance = (a, b) => Math.hypot(a.x - b.x, a.y - b.y);

async function main() {
  const a = new Client(`Smoke-PvE-${RUN}`);
  await a.connect();
  await undock(a, 'a');
  const npcs = a.welcome.npcs;
  check('welcome carries the shelter radius and the sun', npcs?.stationSafeRadius > 0 && Boolean(a.welcome.system?.sun));

  // Пираты прилетают налётами: группы уже патрулируют в случайных точках, новые прилетают через врата.
  await a.until(() => a.players.length > 0, 2000, 'the roster after welcome');
  const pirates = a.players.filter((p) => p.kind === 'pirate');
  check(
    `raiders in the roster: ${pirates.length} (${[...new Set(pirates.map((p) => p.name))].join(', ')})`,
    pirates.length > 0 && pirates.every((p) => p.npc && /Ур\.\d+$/.test(p.name) && p.maxHp > 0),
  );

  a.start();
  await a.until(() => a.me, 3000, 'own ship in the snapshot');
  // Радар (M7): далёких пиратов сервер не присылает. Точки патруля — на кольце вокруг орбиты станции:
  // облетаем его по кругу, пока кто-нибудь не покажется.
  const ring = Array.from({ length: 8 }, (_, i) => ({ x: 3000 * Math.cos((i * Math.PI) / 4), y: 3000 * Math.sin((i * Math.PI) / 4) }));
  let leg = ring.reduce((best, p, i) => (distance(p, a.me) < distance(ring[best], a.me) ? i : best), 0);
  a.control = a.flyTo(() => {
    if (a.me && distance(ring[leg], a.me) < 300) leg = (leg + 1) % ring.length;
    return ring[leg];
  }, 0);
  await a.until(() => a.pirates().length > 0, 150000, 'pirates on the radar');
  // Пират может уже драться — с торговцем или рейнджером (M9), но не с нами: мы под защитой после появления.
  check(
    `pirates on the radar: ${a.pirates().length}, ${a.pirates().map((s) => s.ai).join(', ')}`,
    a.pirates().every((s) => s.tg !== a.id),
  );

  // К ближайшему свободному пирату: подлететь и зависнуть рядом, пока защита после появления кончается по дороге.
  const free = a.pirates().filter((s) => s.ai !== 'attack');
  const prey = (free.length > 0 ? free : a.pirates()).reduce((best, s) => (distance(s, a.me) < distance(best, a.me) ? s : best));
  const preyName = a.players.find((p) => p.id === prey.id).name;
  console.log(`     flying to ${preyName} #${prey.id} at ${Math.round(prey.x)}, ${Math.round(prey.y)}`);
  a.control = a.flyTo(() => a.ship(prey.id), APPROACH);
  await a.until(() => a.pirates().some((s) => s.tg === a.id), 45000, 'a pirate attacks');
  const attacker = a.pirates().find((s) => s.tg === a.id);
  check(`${a.players.find((p) => p.id === attacker.id).name} #${attacker.id} attacks at ${Math.round(distance(attacker, a.me))}`, attacker.ai === 'attack');

  const since = a.snapshot.tick;
  const pirateIds = new Set(a.pirates().map((s) => s.id));
  const shotsAtMe = () =>
    a.snapshots.filter((s) => s.tick >= since).flatMap((s) => (s.shots ?? []).filter((shot) => shot.to === a.id && pirateIds.has(shot.from)));
  await a.until(() => shotsAtMe().length > 0, 10000, 'a pirate fires');
  const shot = shotsAtMe()[0];
  check(`pirate fires: ${shot.w}, chance ${shot.ch}%, ${shot.hit ? `hit for ${shot.dmg}` : 'miss'}`, shot.ch >= 5 && shot.ch <= 95);

  // Домой, в укрытие: пират гонится и стреляет, а у станции бросает цель.
  // Станция на орбите — летим туда, где она сейчас.
  const shelter = () => stationAt(a.welcome.system, a.snapshot?.tick ?? 0);
  a.control = a.flyTo(shelter, 0);
  await a.until(() => a.me && distance(a.me, shelter()) <= npcs.stationSafeRadius - 50, 60000, 'back in the shelter');
  const chasedFor = shotsAtMe().length;
  await a.until(() => !a.pirates().some((s) => s.tg === a.id), 3000, 'pirates drop the target in the shelter');
  const chaser = a.ship(attacker.id);
  check(`in the shelter nobody targets us; the attacker is now "${chaser?.ai}" (${chasedFor} pirate shots on the way)`, !chaser || ['return', 'patrol', 'leave'].includes(chaser.ai));
  const me = a.me;
  check(`own ship alive: hull ${me.hp}, shield ${me.sh}`, !me.rt);

  a.close();
}

main().then(
  () => process.exit(results.every(Boolean) ? 0 : 1),
  (e) => {
    console.log(`FAIL ${e.message}`);
    process.exit(1);
  },
);
