// Сквозная проверка метеоритов без браузера: meteors.json в welcome, камни в снапшотах, прямолинейный полёт,
// нулевое уклонение в шансе попадания, минералы с расстрелянного и чистое укрытие у станции.
// Перехват, а не погоня: лёгкий корпус (165) метеорит (240–300) не догонит, поэтому летим навстречу ближайшему.
// Нужен запущенный сервер и Node 24 (встроенный WebSocket). Идёт до ~2 минут: ждём подходящий камень.
//   node tools/smoke-meteor.mjs [ws://localhost:5000/ws]

const url = process.argv[2] ?? 'ws://localhost:5000/ws';
const INPUT_INTERVAL_MS = 50;
const CLOSE_HIDDEN = 4000;
const RUN = Math.floor(100 + Math.random() * 900);
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

/** Ближайшая к станции точка луча метеорита: по ней видно, заденет ли он укрытие. */
function passDistance(m) {
  const speed = Math.hypot(m.vx, m.vy);
  if (speed === 0) return distance(m, STATION);
  const ux = m.vx / speed;
  const uy = m.vy / speed;
  const t = Math.max(0, (STATION.x - m.x) * ux + (STATION.y - m.y) * uy);
  return Math.hypot(m.x + ux * t - STATION.x, m.y + uy * t - STATION.y);
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
  check(`warning thresholds are set: ${rules?.warnSeconds} s, miss ×${rules?.warnMissFactor}`, rules?.warnSeconds > 0);

  a.start();
  await a.until(() => a.meteors.length > 0, 40000, 'meteors in the sky');
  check(`meteors appear: ${a.meteors.length} in the snapshot`, a.meteors.length > 0);

  // Летит по прямой: положение через несколько тиков должно совпасть с x + vx·Δt.
  const watched = a.meteors[0];
  const first = a.snapshot;
  await a.until(() => a.snapshot.tick - first.tick >= 20 || !a.meteors.some((m) => m.id === watched.id), 4000, 'watching a meteor');
  const later = a.meteors.find((m) => m.id === watched.id);
  if (later) {
    const dt = (a.snapshot.tick - first.tick) * DT;
    const drift = Math.hypot(watched.x + watched.vx * dt - later.x, watched.y + watched.vy * dt - later.y);
    check(`flies straight: ${drift.toFixed(3)} units off after ${dt.toFixed(2)} s`, drift < 0.5);
    check(`speed stays the same: ${Math.hypot(later.vx, later.vy).toFixed(1)}`, Math.abs(Math.hypot(later.vx, later.vy) - Math.hypot(watched.vx, watched.vy)) < 0.001);
  } else {
    check('flies straight (the watched meteor left too early — rerun)', false);
  }

  // Перехват: правим курс на ближайший камень и держим огонь по нему. Заодно смотрим за укрытием.
  const seen = new Map();
  let closestPass = Infinity;
  const watchShelter = () => {
    for (const m of a.meteors) {
      if (!seen.has(m.id)) seen.set(m.id, passDistance(m));
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
    `no meteor crosses the station shelter: ${seen.size} tracks, closest pass ${Math.round(closestPass)} vs shelter ${shelter}`,
    seen.size > 0 && closestPass >= shelter,
  );

  a.close();
  const failed = results.filter((ok) => !ok).length;
  console.log(failed === 0 ? `\nAll ${results.length} checks passed` : `\n${failed} of ${results.length} checks FAILED`);
  process.exit(failed === 0 ? 0 : 1);
}

main().catch((e) => {
  console.error(e.message);
  process.exit(1);
});
