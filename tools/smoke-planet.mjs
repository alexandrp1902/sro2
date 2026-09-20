// Сквозная проверка посадки на планету (M15) без браузера: поселение приезжает в welcome вместе с системой,
// корабль садится по ключу места, док отдаёт своё место, свой рынок и свою доску, а репутация идёт в «pl:».
// Гостем и в Sol: там есть и станция, и поселение — ровно тот случай, ради которого «место» отделили от системы.
// Нужен запущенный сервер и Node 24 (встроенный WebSocket). Идёт ~40 с.
//   node tools/smoke-planet.mjs [ws://localhost:5000/ws]

import { aroundSun, openSocket, orbitAt, stationAt } from './wire.mjs';

const url = process.argv[2] ?? 'ws://localhost:5000/ws';
const INPUT_INTERVAL_MS = 50;
const CLOSE_HIDDEN = 4000;
const RUN = Math.floor(100 + Math.random() * 900);

class Client {
  constructor(name) {
    this.name = name;
    this.token = Array.from(crypto.getRandomValues(new Uint8Array(16)), (b) => b.toString(16).padStart(2, '0')).join('');
    this.welcome = null;
    this.cargo = null;
    this.market = null;
    this.hangar = null;
    this.missions = null;
    this.rep = null;
    this.notices = [];
    this.snapshots = [];
    this.seq = 0;
    this.timer = 0;
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
        } else if (message.t === 'cargo') this.cargo = message;
        else if (message.t === 'market') this.market = message;
        else if (message.t === 'hangar') this.hangar = message;
        else if (message.t === 'missions') this.missions = message;
        else if (message.t === 'rep') this.rep = message;
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
      const goal = aroundSun(this.welcome.system, me, at);
      const dx = goal.x - me.x;
      const dy = goal.y - me.y;
      return [dx, dy, Math.hypot(me.x - at.x, me.y - at.y) > stopAt ? 1 : 0];
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

async function main() {
  const a = new Client(`Smoke-Planet-${RUN}`);
  await a.connect();

  const system = a.welcome.system;
  const planets = system.planets ?? [];
  const settled = planets.filter((p) => p.settlement && p.id);
  check(`the system carries its planets: ${planets.length}, settled ${settled.length}`, settled.length > 0);
  if (settled.length === 0) return;

  const planet = settled[0];
  const key = `pl:${planet.id}`;
  const range = a.welcome.loot.stationRange + planet.size;
  console.log(`     садимся на «${planet.settlement.name ?? planet.name}» (${planet.kind}), радиус ${range}`);

  // Планета необитаемая рядом должна остаться недоступной: сесть можно не на всё.
  const wild = planets.find((p) => !p.settlement);
  check(`a planet without a settlement stays scenery: ${wild?.name ?? '—'}`, !!wild);

  a.start();
  await a.until(() => a.me, 5000, 'the first snapshot');

  // Сперва отказ: с той стороны системы садиться нельзя, и сервер обязан это сказать.
  a.notices.length = 0;
  a.send({ t: 'dock', on: true, place: key });
  await a.until(() => a.notices.includes('tooFar'), 4000, 'the refusal from far away');
  check('landing from across the system is refused', !a.hangar?.docked);

  const where = () => orbitAt(system, planet.orbit, a.snapshot.tick);
  a.control = a.flyTo(where, range - 60);
  await a.until(() => {
    const at = where();
    return a.me && Math.hypot(a.me.x - at.x, a.me.y - at.y) <= range;
  }, 90000, 'reaching the settlement');
  a.control = () => [0, -1, 0];

  a.send({ t: 'dock', on: true, place: key });
  await a.until(() => a.hangar?.docked, 5000, 'landing');
  const place = a.hangar.place;
  check(`the hangar says where we stand: ${place?.key} «${place?.name}»`, place?.key === key && place?.kind === 'pl');
  check(`the settlement says whether it has a shipyard: ${place?.shipyard}`, typeof place?.shipyard === 'boolean');

  await a.until(() => a.market !== null, 4000, 'the market of the settlement');
  check(`the market belongs to this place: ${a.market.system}`, a.market.system === key);
  check(`the settlement trades: ${a.market.items.length} goods`, a.market.items.length > 0);

  await a.until(() => a.missions !== null, 4000, 'the board of the settlement');
  const kinds = [...new Set((a.missions.offers ?? []).map((o) => o.kind))];
  check(`the settlement has its own board: ${kinds.join(', ') || '—'}`, (a.missions.offers ?? []).length > 0);
  check('the board is issued by this place', (a.missions.offers ?? []).every((o) => o.from === key));

  // Репутация места: ключ должен быть «pl:», а не «st:» — иначе поселение и станция помнят одно и то же.
  const here = a.rep?.here;
  check(`reputation here is the settlement's own: ${here?.place}`, here?.place === key);

  // Взлёт: корабль должен оказаться у планеты, а не у станции.
  a.send({ t: 'dock', on: false });
  await a.until(() => a.hangar && !a.hangar.docked, 5000, 'taking off');
  await a.until(() => a.me, 4000, 'the snapshot after take-off');
  const at = where();
  const station = stationAt(system, a.snapshot.tick);
  const toPlanet = Math.hypot(a.me.x - at.x, a.me.y - at.y);
  const toStation = Math.hypot(a.me.x - station.x, a.me.y - station.y);
  check(`take-off leaves the ship at the planet: ${toPlanet | 0} from it, ${toStation | 0} from the station`, toPlanet < toStation);

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
