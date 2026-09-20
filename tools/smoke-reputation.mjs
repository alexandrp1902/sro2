// Сквозная проверка репутации (M13) без браузера: правила в welcome, отношение в доке, отказ магазина
// без репутации и штраф за обстрел торговца — один раз за окно, а не каждый тик.
// Нужен запущенный сервер и Node 24 (встроенный WebSocket). Идёт ~40 с.
//   node tools/smoke-reputation.mjs [ws://localhost:5000/ws]

import { PROTOCOL_VERSION, openSocket, stationAt } from './wire.mjs';

const url = process.argv[2] ?? 'ws://localhost:5000/ws';
const INPUT_INTERVAL_MS = 50;
const CLOSE_HIDDEN = 4000;
const RUN = Math.floor(100 + Math.random() * 900);

class Client {
  constructor(name) {
    this.name = name;
    this.token = Array.from(crypto.getRandomValues(new Uint8Array(16)), (b) => b.toString(16).padStart(2, '0')).join('');
    this.welcome = null;
    this.hangar = null;
    this.rep = null;
    this.changes = [];
    this.notices = [];
    this.players = [];
    this.snapshots = [];
    this.seq = 0;
    this.timer = 0;
    this.fire = false;
    this.target = 0;
    this.control = () => [0, -1, 0];
    this.listeners = new Set();
  }

  get id() {
    return this.welcome.id;
  }

  get snapshot() {
    return this.snapshots[this.snapshots.length - 1];
  }

  get me() {
    return this.snapshot?.ships.find((s) => s.id === this.id);
  }

  /** Очки системы, где стоит корабль; 0 — о ней ещё ничего не записано. */
  systemRep() {
    return this.rep?.systems?.[this.welcome.system.id] ?? 0;
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
        } else if (message.t === 'hangar') this.hangar = message;
        else if (message.t === 'rep') {
          this.rep = message;
          if (message.change) this.changes.push(message.change);
        } else if (message.t === 'notice') this.notices.push(message.code);
        else if (message.t === 'players') this.players = message.players;
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
      const [dx, dy, th] = this.control();
      const length = Math.hypot(dx, dy) || 1;
      this.send({ t: 'input', seq: ++this.seq, dx: dx / length, dy: dy / length, th });
    }, INPUT_INTERVAL_MS);
  }

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

const sleep = (ms) => new Promise((done) => setTimeout(done, ms));

async function main() {
  const a = new Client(`Smoke-Rep-${RUN}`);
  await a.connect();

  check(`the protocol matches the client: ${a.welcome.version}`, a.welcome.version === PROTOCOL_VERSION);
  const rules = a.welcome.reputation;
  const levels = rules?.levels ?? [];
  check(`welcome carries reputation.json: ${levels.length} levels`, levels.length >= 3);
  check('the scale starts at minus the limit', levels[0]?.from === -rules.limit);
  check('the gate names a level to reach', !!rules?.gate?.level);

  await a.until(() => a.rep !== null, 3000, 'the first rep message');
  check('a fresh pilot owes nobody anything', Object.keys(a.rep.systems).length === 0);

  // В док: там видно отношение станции и системы.
  a.start();
  await a.until(() => a.me, 5000, 'the first snapshot');
  const where = () => stationAt(a.welcome.system, a.snapshot.tick, a.welcome.tickRate);
  a.control = a.flyTo(where, 0);
  await a.until(() => {
    const at = where();
    return a.me && Math.hypot(a.me.x - at.x, a.me.y - at.y) <= a.welcome.loot.stationRange;
  }, 45000, 'reaching the station');
  a.control = () => [0, -1, 0];
  a.send({ t: 'dock', on: true });
  await a.until(() => a.hangar?.docked, 5000, 'docking');
  await a.until(() => a.rep?.here, 3000, 'rep.here after docking');

  const here = a.rep.here;
  console.log(`     здесь: место ${here.value} (${here.level}), система ${here.system} (${here.systemLevel}), витрина ${here.gate}`);
  check(`the dock says where you stand: ${here.place}`, here.place === `st:${a.welcome.system.id}`);
  check('a newcomer starts level with everyone', here.value === 0 && here.system === 0);

  // Гейт магазина смоуком не проверить: входим гостем, а гостю принадлежат все корпуса, и в Ядре
  // топовые всё равно не в ассортименте (M11) — отказ пришёл бы от региона раньше, чем от репутации.
  // Поэтому здесь только проверяем, что правила гейта доехали; сам отказ покрыт ReputationTests.
  const gatedHull = (rules.gate.hulls ?? []).find((id) => a.welcome.hulls[id]);
  check(`the gate names hulls this server has: ${(rules.gate.hulls ?? []).join(', ')}`, !!gatedHull);
  check(`the gate names tiers: ${(rules.gate.tiers ?? []).join(', ') || '—'}`, (rules.gate.tiers ?? []).length > 0);

  // Обстрел торговца: штраф один раз за окно нарушителя, а не каждый тик.
  a.send({ t: 'dock', on: false });
  await a.until(() => a.hangar?.docked === false, 5000, 'undocking');
  await a.until(() => a.players.some((p) => p.kind === 'trader'), 40000, 'a trader in the system');
  // Ближайший торговец из тех, что сейчас в снапшоте; он может уйти в прыжок — тогда берём следующего.
  const traderShip = () => {
    const ids = new Set(a.players.filter((p) => p.kind === 'trader').map((p) => p.id));
    const me = a.me;
    if (!me) return null;
    return (a.snapshot?.ships ?? [])
      .filter((s) => ids.has(s.id))
      .sort((x, y) => Math.hypot(x.x - me.x, x.y - me.y) - Math.hypot(y.x - me.x, y.y - me.y))[0] ?? null;
  };
  a.control = a.flyTo(traderShip, 200);
  await a.until(() => {
    const me = a.me;
    const it = traderShip();
    return me && it && Math.hypot(me.x - it.x, me.y - it.y) <= 450;
  }, 90000, 'catching up with a trader');

  a.changes.length = 0;
  a.notices.length = 0;
  a.send({ t: 'target', id: traderShip().id });
  a.send({ t: 'fire', on: true });
  await a.until(() => a.changes.some((c) => c.code === 'traderAttack'), 20000, 'the penalty for shooting a trader');
  const first = a.changes.find((c) => c.code === 'traderAttack');
  check(`shooting a trader costs reputation: ${first.delta}`, first.delta < 0);
  check(`the penalty lands on this system: ${first.key}`, first.key === `sys:${a.welcome.system.id}`);
  // Рейнджеры берут обидчика на прицел, только если они рядом: в пустой системе предупреждения не будет.
  console.log(`     рейнджеры ${a.notices.includes('rangers') ? 'откликнулись' : 'поблизости не нашлись'}`);

  // Окно нарушителя — 30 с, и всё это время огонь не прекращается: второго штрафа быть не должно.
  const before = a.changes.filter((c) => c.code === 'traderAttack').length;
  await sleep(4000);
  const after = a.changes.filter((c) => c.code === 'traderAttack').length;
  check(`the same attack is not charged every tick: ${before} → ${after}`, after === before);
  a.send({ t: 'fire', on: false });

  check(`the system now thinks worse of us: ${a.systemRep()}`, a.systemRep() < 0);
  check('the galaxy map gets the numbers too', a.welcome.system.id in a.rep.systems);

  a.close();
}

main()
  .then(() => {
    const failed = results.filter((ok) => !ok).length;
    console.log(failed === 0 ? `\nall ${results.length} checks passed` : `\n${failed} of ${results.length} checks failed`);
    process.exit(failed === 0 ? 0 : 1);
  })
  .catch((e) => {
    console.error(e.message);
    process.exit(1);
  });
