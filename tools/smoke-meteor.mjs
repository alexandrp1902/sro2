// Сквозная проверка метеоритов без браузера: meteors.json в welcome, камни в снапшотах, полёт по дуге,
// нулевое уклонение в шансе попадания, минералы с расстрелянного и чистое укрытие у станции.
// Перехват, а не погоня: лёгкий корпус (165) метеорит (240–300) не догонит, поэтому летим навстречу ближайшему.
// Нужен запущенный сервер и Node 24 (встроенный WebSocket). Идёт до ~2 минут: ждём подходящий камень.
//   node tools/smoke-meteor.mjs [ws://localhost:5000/ws]

import { openSocket } from './wire.mjs';

const url = process.argv[2] ?? 'ws://localhost:5000/ws';
const INPUT_INTERVAL_MS = 50;
const CLOSE_HIDDEN = 4000;
const RUN = Math.floor(100 + Math.random() * 900);
/** Центр системы — звезда: к ней тянет камни. */
const STATION = { x: 0, y: 0 };
const DT = 0.05;
/** Шанс попадания по камню без штрафа за дистанцию — точность пушки; ниже этого уклонение всё-таки вмешалось. */
const MIN_CHANCE = 60;

class Client {
  constructor(name) {
    this.name = name;
    this.token = Array.from(crypto.getRandomValues(new Uint8Array(16)), (b) => b.toString(16).padStart(2, '0')).join('');
    this.welcome = null;
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

  get meteors() {
    return this.snapshot?.meteors ?? [];
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
        } else if (message.t === 'snapshot') {
          this.snapshots.push(message);
          if (this.snapshots.length > 4000) this.snapshots.splice(0, 2000);
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

/** Шаг полёта камня — зеркало MeteorRules.Step: тяготение меняет скорость, скорость меняет положение. */
function flightStep(rules, s, dt) {
  let ax = 0;
  let ay = 0;
  const r = Math.hypot(s.x - STATION.x, s.y - STATION.y);
  if (rules.gravity > 0 && r > 1e-9) {
    const soft = Math.max(r, rules.gravityMinRadius);
    const scale = -rules.gravity / (soft * soft * soft);
    ax = (s.x - STATION.x) * scale;
    ay = (s.y - STATION.y) * scale;
  }
  const vx = s.vx + ax * dt;
  const vy = s.vy + ay * dt;
  return { x: s.x + vx * dt, y: s.y + vy * dt, vx, vy };
}

/** Ближайшая к станции точка дуги метеорита: прямая линия с тяготением уже ничего не сказала бы. */
function passDistance(rules, m) {
  let s = { x: m.x, y: m.y, vx: m.vx, vy: m.vy };
  let closest = Math.hypot(s.x - STATION.x, s.y - STATION.y);
  const limit = 4000 + (rules.despawnMargin ?? 600);
  for (let i = 0; i < 240 * 20; i++) {
    s = flightStep(rules, s, DT);
    closest = Math.min(closest, Math.hypot(s.x - STATION.x, s.y - STATION.y));
    if ((Math.abs(s.x) > limit || Math.abs(s.y) > limit) && s.x * s.vx + s.y * s.vy > 0) break;
  }
  return closest;
}

async function main() {
  const a = new Client(`Smoke-Meteor-${RUN}`);
  await a.connect();

  const rules = a.welcome.meteors;
  const sizes = Object.entries(rules?.sizes ?? {});
  check(
    `welcome carries meteors.json: ${sizes.map(([id, s]) => `${id} r${s.radius}/${s.hp}hp`).join(', ')}`,
    sizes.length > 0 && rules.maxAlive > 0,
  );
  const shelter = a.welcome.npcs?.stationSafeRadius ?? 0;
  const tracks = Object.entries(rules?.tracks ?? {});
  check(
    `trajectories carry weights: ${tracks.map(([id, t]) => `${id} ×${t.weight}`).join(', ')}`,
    tracks.length > 1 && tracks.every(([, t]) => t.weight >= 0),
  );
  check(`gravity bends the tracks: GM ${rules?.gravity}`, rules?.gravity > 0);

  a.start();
  // Радар (M7): камень виден, только когда проходит ближе радиуса радара, — ждём дольше интервала появления.
  await a.until(() => a.meteors.length > 0, 120000, 'meteors in the sky');
  check(`meteors appear: ${a.meteors.length} in the snapshot`, a.meteors.length > 0);

  // Дуга: клиентская формула должна повторить путь сервера, а курс — заметно отвернуть от прямой.
  const watched = a.meteors[0];
  const first = a.snapshot;
  await a.until(() => a.snapshot.tick - first.tick >= 40 || !a.meteors.some((m) => m.id === watched.id), 5000, 'watching a meteor');
  const later = a.meteors.find((m) => m.id === watched.id);
  if (later) {
    const ticks = a.snapshot.tick - first.tick;
    let mine = { x: watched.x, y: watched.y, vx: watched.vx, vy: watched.vy };
    for (let i = 0; i < ticks; i++) mine = flightStep(rules, mine, DT);
    const drift = Math.hypot(mine.x - later.x, mine.y - later.y);
    check(`the client repeats the server arc: ${drift.toFixed(3)} units apart after ${(ticks * DT).toFixed(2)} s`, drift < 0.5);

    const straight = Math.hypot(watched.x + watched.vx * ticks * DT - later.x, watched.y + watched.vy * ticks * DT - later.y);
    const turn = Math.abs(Math.atan2(later.vy, later.vx) - Math.atan2(watched.vy, watched.vx)) * (180 / Math.PI);
    console.log(`     track bent ${straight.toFixed(1)} units off a straight line, course turned ${turn.toFixed(2)}° in ${(ticks * DT).toFixed(1)} s`);
    check('the track is not a straight line', straight > 0.5);
  } else {
    check('the arc matches (the watched meteor left too early — rerun)', false);
  }

  // Появления — монета на тик, а не расписание: промежутки между камнями должны заметно гулять.
  const arrivals = [];
  const seenIds = new Set(a.meteors.map((m) => m.id));
  const watchArrivals = () => {
    for (const m of a.meteors) {
      if (!seenIds.has(m.id)) {
        seenIds.add(m.id);
        arrivals.push(a.snapshot.tick);
      }
    }
  };
  a.listeners.add(watchArrivals);

  // Перехват: правим курс на ближайший камень и держим огонь по нему. Заодно смотрим за укрытием.
  const seen = new Map();
  let closestPass = Infinity;
  const watchShelter = () => {
    for (const m of a.meteors) {
      if (!seen.has(m.id)) seen.set(m.id, passDistance(rules, m));
      closestPass = Math.min(closestPass, seen.get(m.id));
    }
  };
  a.listeners.add(watchShelter);

  let aimedAt = 0;
  const nearest = () => {
    const me = a.me;
    if (!me) return null;
    let best = null;
    for (const m of a.meteors) {
      if (!best || distance(m, me) < distance(best, me)) best = m;
    }
    return best;
  };
  a.control = () => {
    const me = a.me;
    const target = nearest();
    if (!me || !target) return [0, -1, 0];
    if (target.id !== aimedAt) {
      aimedAt = target.id;
      a.send({ t: 'target', id: target.id });
      a.send({ t: 'fire', on: true });
    }
    // Точка встречи на пару секунд вперёд: летим навстречу, а не вдогонку.
    const dx = target.x + target.vx * 2 - me.x;
    const dy = target.y + target.vy * 2 - me.y;
    return [dx, dy, Math.hypot(dx, dy) > 350 ? 1 : 0];
  };

  const shotsAtRocks = () =>
    a.snapshots.flatMap((s) => (s.shots ?? []).filter((shot) => shot.from === a.id && seen.has(shot.to)));
  await a.until(() => shotsAtRocks().length > 0, 120000, 'shooting at a meteor');
  const shots = shotsAtRocks();
  const chance = Math.min(...shots.map((s) => s.ch));
  check(`shots at meteors carry no evasion: ${shots.length} shots, lowest chance ${chance}%`, chance >= MIN_CHANCE);

  const myKills = () => a.snapshots.flatMap((s) => (s.kills ?? []).filter((k) => k.by === a.id && seen.has(k.id)));
  try {
    await a.until(() => myKills().length > 0, 60000, 'destroying a meteor');
    const killed = myKills()[0].id;
    // Где камень был в последний раз: обломки контейнеров лежат по всей системе, нам нужны именно его минералы.
    const wreck = a.snapshots.flatMap((s) => s.meteors ?? []).findLast((m) => m.id === killed);
    check(`a meteor was destroyed: id ${killed} at ${Math.round(wreck.x)}, ${Math.round(wreck.y)}`, true);
    const nearWreck = () => (a.snapshot.loot ?? []).filter((d) => distance(d, wreck) < 200);
    await a.until(() => nearWreck().length > 0, 3000, 'minerals from the wreck');
    const drop = nearWreck()[0];
    check(`the wreck dropped minerals: ${drop.i} x${drop.n}`, drop.n > 0);
  } catch (e) {
    check(`a meteor was destroyed (${e.message})`, false);
  }

  const rams = a.snapshots.flatMap((s) => (s.shots ?? []).filter((shot) => shot.w === 'ram'));
  console.log(`     rams seen during the run: ${rams.length}${rams.length ? ` (last one −${rams[rams.length - 1].dmg})` : ''}`);

  check(
    `no meteor arc crosses the core around the sun: ${seen.size} tracks, closest pass ${Math.round(closestPass)} vs core ${shelter}`,
    seen.size > 0 && closestPass >= shelter,
  );

  a.listeners.delete(watchArrivals);
  const gaps = arrivals.slice(1).map((tick, i) => (tick - arrivals[i]) * DT);
  if (gaps.length >= 4) {
    const mean = gaps.reduce((sum, g) => sum + g, 0) / gaps.length;
    const spread = Math.max(...gaps) - Math.min(...gaps);
    console.log(`     ${gaps.length} gaps between arrivals: ${gaps.map((g) => g.toFixed(1)).join(', ')} s (mean ${mean.toFixed(1)})`);
    check(`arrivals are irregular, not on a schedule: spread ${spread.toFixed(1)} s`, spread > mean * 0.4);
  } else {
    console.log(`     only ${gaps.length} gaps seen — skipping the irregularity check`);
  }

  a.close();
  const failed = results.filter((ok) => !ok).length;
  console.log(failed === 0 ? `\nAll ${results.length} checks passed` : `\n${failed} of ${results.length} checks FAILED`);
  process.exit(failed === 0 ? 0 : 1);
}

main().catch((e) => {
  console.error(e.message);
  process.exit(1);
});
