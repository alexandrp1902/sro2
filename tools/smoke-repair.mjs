// Сквозная проверка гибели и ремонта (M15.7) без браузера: корабль сгорает в звезде, возвращается
// с десятой долей корпуса, разбитым переживает перезаход по ключу, чинится стоянкой в доке бесплатно
// и деньгами — до полного. Заодно проверяется смена пароля: старый перестаёт пускать, новый пускает.
// Заводит на сервере аккаунт smoke-fix-NNN — он останется в data/accounts.
// Нужен запущенный сервер и Node 24 (встроенный WebSocket). Идёт ~1.5 минуты.
//   node tools/smoke-repair.mjs [ws://localhost:5000/ws]

import { openSocket, stationAt, undock } from './wire.mjs';

const url = process.argv[2] ?? 'ws://localhost:5000/ws';
const INPUT_INTERVAL_MS = 50;
const CLOSE_HIDDEN = 4000;
const RUN = Math.floor(100 + Math.random() * 900);
const NAME = `smoke-fix-${RUN}`;
const PASSWORD = 'smoke-pass';
const NEW_PASSWORD = 'smoke-pass-2';

class Client {
  constructor(hello) {
    this.hello = hello;
    this.welcome = null;
    this.account = null;
    this.denied = null;
    this.hangar = null;
    this.cargo = null;
    this.notices = [];
    this.snapshots = [];
    this.closedWith = null;
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

  open() {
    return new Promise((resolve, reject) => {
      const { ws, read } = openSocket(url);
      this.ws = ws;
      ws.onopen = () => {
        this.send({ t: 'hello', ...this.hello });
        resolve();
      };
      ws.onerror = () => reject(new Error(`cannot connect to ${url}`));
      ws.onclose = (e) => {
        clearInterval(this.timer);
        this.closedWith = e.code;
        this.notify();
      };
      ws.onmessage = (e) => {
        const message = read(e.data);
        if (message.t === 'welcome') this.welcome = message;
        else if (message.t === 'account') this.account = message;
        else if (message.t === 'denied') this.denied = message.code;
        else if (message.t === 'hangar') this.hangar = message;
        else if (message.t === 'cargo') this.cargo = message;
        else if (message.t === 'notice') this.notices.push(message.code);
        else if (message.t === 'snapshot') {
          this.snapshots.push(message);
          if (this.snapshots.length > 2000) this.snapshots.splice(0, 1000);
        }
        this.notify();
      };
    });
  }

  notify() {
    for (const listener of this.listeners) listener();
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

async function main() {
  console.log(`smoke-repair against ${url}, pilot ${NAME}`);

  const a = new Client({ name: NAME, password: PASSWORD });
  await a.open();
  await a.until(() => a.welcome && a.hangar && a.cargo, 5000, 'welcome, hangar and cargo');
  const share = a.welcome.combat?.deathHullShare;
  const perMinute = a.welcome.combat?.dockRepairPerMinute;
  check(
    `combat.json in welcome: deathHullShare ${share}, dockRepairPerMinute ${perMinute}`,
    share > 0 && share < 1 && perMinute > 0,
  );

  a.send({ t: 'mission', action: 'skip' });
  await undock(a, 'pilot');
  a.start();
  await a.until(() => a.me, 5000, 'own ship in a snapshot');
  const maxHp = a.hangar.maxHp;
  const burnRadius = a.welcome.system.sun?.burnRadius ?? 0;
  if (!(burnRadius > 0)) throw new Error('the home system has no star to burn in');

  // Погибнуть проще всего в звезде: жар не спрашивает про PvP и не зависит от того, кто рядом.
  console.log('     flying into the star');
  a.control = () => {
    const me = a.me;
    if (!me) return [0, -1, 0];
    return [-me.x, -me.y, 1];
  };
  await a.until(() => a.me && a.me.hp < maxHp, 60000, 'the star starts burning the hull');
  check('the star burns the hull', true);

  // Гибель и возвращение: корпус разбит, а не полон, как было до M15.7. Появление — у дома, вне жара.
  await a.until(
    () => a.me && a.me.hp > 0 && Math.hypot(a.me.x, a.me.y) > burnRadius,
    60000,
    'respawn after burning',
  );
  a.control = () => [0, -1, 0];
  const afterDeath = a.me.hp;
  check(
    `came back broken: ${afterDeath} / ${maxHp} hull, expected about ${Math.ceil(maxHp * share)}`,
    Math.abs(afterDeath - maxHp * share) <= 1,
  );

  // Перезаход не чинит: прочность уезжает в аккаунт.
  const key = a.account.key;
  a.close();
  const back = new Client({ key });
  await back.open();
  await back.until(() => back.welcome && back.hangar, 5000, 'welcome after the reconnect by key');
  check(
    `still broken after a relogin: ${back.hangar.hp} / ${back.hangar.maxHp}`,
    Math.abs(back.hangar.hp - afterDeath) <= 1,
  );

  // В док — чиниться. Корабль после гибели появляется в космосе у дома, лететь недалеко.
  await undock(back, 'pilot');
  back.start();
  await back.until(() => back.me, 5000, 'own ship in a snapshot after the relogin');
  const station = () => stationAt(back.welcome.system, back.snapshot?.tick ?? 0);
  console.log('     flying to the station');
  back.control = () => {
    const me = back.me;
    if (!me) return [0, -1, 0];
    const at = station();
    const d = Math.hypot(at.x - me.x, at.y - me.y);
    return [at.x - me.x, at.y - me.y, d > 120 ? 1 : 0];
  };
  await back.until(
    () => back.me && Math.hypot(back.me.x - station().x, back.me.y - station().y) <= back.welcome.loot.stationRange - 30,
    60000,
    'reaching the station',
  );
  back.send({ t: 'dock', on: true });
  await back.until(() => back.hangar.docked, 3000, 'docking');

  // Стоянка чинит бесплатно и сама: ждём, пока целое число корпуса подрастёт.
  const before = back.hangar.hp;
  const credits = back.cargo.credits;
  console.log(`     standing in the dock, waiting for free mending from ${before}`);
  await back.until(() => back.hangar.hp > before, 60000, 'the dock mends the hull for free');
  check(
    `standing in the dock mends the hull for free: ${before} → ${back.hangar.hp}, credits ${back.cargo.credits}`,
    back.cargo.credits === credits,
  );

  // Платный ремонт добивает корпус до полного.
  back.send({ t: 'repair' });
  await back.until(() => back.hangar.hp === back.hangar.maxHp, 5000, 'paid repair');
  check(
    `paid repair filled the hull: ${back.hangar.hp} / ${back.hangar.maxHp}, credits ${credits} → ${back.cargo.credits}`,
    back.cargo.credits < credits,
  );

  // Смена пароля (M15.7): отказы, успех, новый ключ.
  back.send({ t: 'password', old: 'not-the-password', new: NEW_PASSWORD });
  await back.until(() => back.notices.includes('wrongPassword'), 5000, 'a wrong old password is refused');
  check('a wrong old password is refused', true);
  back.send({ t: 'password', old: PASSWORD, new: 'ab' });
  await back.until(() => back.notices.includes('badPassword'), 5000, 'a too short new password is refused');
  check('a too short new password is refused', true);

  const oldKey = back.account?.key ?? key;
  back.send({ t: 'password', old: PASSWORD, new: NEW_PASSWORD });
  await back.until(() => back.notices.includes('passwordChanged'), 5000, 'the password is changed');
  const freshKey = back.account?.key;
  check(`password changed, device got a new key: ${freshKey?.length ?? 0} chars`, !!freshKey && freshKey !== oldKey);
  back.close();

  // Старый пароль больше не пускает, новый пускает.
  const stale = new Client({ name: NAME, password: PASSWORD });
  await stale.open();
  await stale.until(() => stale.closedWith !== null, 5000, 'the old password stops working');
  check(`the old password no longer works: ${stale.denied}`, stale.denied === 'wrongPassword' && stale.welcome === null);

  const fresh = new Client({ name: NAME, password: NEW_PASSWORD });
  await fresh.open();
  await fresh.until(() => fresh.welcome && fresh.hangar, 5000, 'the new password works');
  check(`the new password works, hull ${fresh.hangar.hp} / ${fresh.hangar.maxHp}`, fresh.hangar.hp === fresh.hangar.maxHp);
  fresh.close();
}

main().then(
  () => process.exit(results.every(Boolean) ? 0 : 1),
  (e) => {
    console.log(`FAIL ${e.message}`);
    process.exit(1);
  },
);
