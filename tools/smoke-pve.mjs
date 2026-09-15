// Сквозная проверка пиратов без браузера: пираты в системе и на карте, агро на игрока у логова, огонь пирата,
// погоня и уход пирата, когда игрок спрятался в укрытии у станции.
// Нужен запущенный сервер и Node 24 (встроенный WebSocket). Идёт ~40–60 с: полёт к логову и обратно.
//   node tools/smoke-pve.mjs [ws://localhost:5000/ws]

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
      const ws = new WebSocket(url);
      this.ws = ws;
      ws.onopen = () => this.send({ t: 'hello', name: this.name, hull: 'light', weapon: 'pulse', token: this.token });
      ws.onerror = () => reject(new Error(`cannot connect to ${url}`));
      ws.onclose = () => clearInterval(this.timer);
      ws.onmessage = (e) => {
        const message = JSON.parse(e.data);
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
  const npcs = a.welcome.npcs;
  check('welcome carries npcs.json (lairs and the shelter)', Boolean(npcs?.spawns?.length) && npcs.stationSafeRadius > 0);

  const expected = npcs.spawns.reduce((sum, s) => sum + (s.count ?? 1), 0);
  await a.until(() => a.players.length > 0, 2000, 'the roster after welcome');
  const pirates = a.players.filter((p) => p.kind === 'pirate');
  check(
    `pirates in the roster: ${pirates.length} of ${expected} (${[...new Set(pirates.map((p) => p.name))].join(', ')})`,
    pirates.length === expected && pirates.every((p) => p.npc && /Ур\.\d+$/.test(p.name) && p.maxHp > 0),
  );

  a.start();
  await a.until(() => a.me && a.pirates().length === expected, 3000, 'own ship and pirates in the snapshot');
  check('pirates patrol when nobody is around', a.pirates().every((s) => s.ai === 'patrol' && !s.tg));

  // К ближайшему пирату: подлететь и зависнуть рядом, пока защита после появления кончается по дороге.
  const prey = a.pirates().reduce((best, s) => (distance(s, a.me) < distance(best, a.me) ? s : best));
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
  const shelter = { x: 0, y: 0 };
  a.control = a.flyTo(shelter, 0);
  await a.until(() => a.me && distance(a.me, shelter) <= npcs.stationSafeRadius - 50, 45000, 'back in the shelter');
  const chasedFor = shotsAtMe().length;
  await a.until(() => !a.pirates().some((s) => s.tg === a.id), 3000, 'pirates drop the target in the shelter');
  const chaser = a.ship(attacker.id);
  check(`in the shelter nobody targets us; the attacker is now "${chaser?.ai}" (${chasedFor} pirate shots on the way)`, chaser?.ai === 'return' || chaser?.ai === 'patrol');
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
