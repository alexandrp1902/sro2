// Сквозная проверка защиты (M15.6) без браузера: динамическая защита отбивает кинетическое попадание,
// аэрозольная завеса — энергетическое, маневровые дюзы роняют объявленный шанс попасть, а противоракетный
// комплекс встаёт во вспомогательный слот, не занимая оружейного.
//
// Стреляет второй клиент, а не пират: тогда вид урона задаём мы сами (импульсная — кинетика, лазер —
// энергия), и ждать никого не нужно. Гостями: гостю можно ставить что угодно и где угодно, а на модули
// за кредиты смоук не заработает. В Vega, потому что в Sol PvP выключен.
// Нужен запущенный сервер и Node 24 (встроенный WebSocket). Идёт ~1 мин.
//   node tools/smoke-defense.mjs [ws://localhost:5000/ws]

import { aroundSun, openSocket, undock } from './wire.mjs';

const url = process.argv[2] ?? 'ws://localhost:5000/ws';
const INPUT_INTERVAL_MS = 50;
const CLOSE_HIDDEN = 4000;
const RUN = Math.floor(100 + Math.random() * 900);

class Client {
  constructor(name) {
    this.name = name;
    this.token = Array.from(crypto.getRandomValues(new Uint8Array(16)), (b) => b.toString(16).padStart(2, '0')).join('');
    this.welcome = null;
    this.players = [];
    this.hangar = null;
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
        else if (message.t === 'hangar') this.hangar = message;
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

async function main() {
  const shooter = new Client(`Smoke-Gun-${RUN}`);
  const target = new Client(`Smoke-Def-${RUN}`);
  await shooter.connect();
  await undock(shooter, 'shooter');
  await target.connect();
  await undock(target, 'target');
  shooter.start();
  target.start();
  await target.until(() => target.hangar, 3000, 'hangar');

  // Каталог модулей: защитные приезжают в welcome вместе с остальными (M15.6).
  const modules = target.welcome.modules ?? {};
  const utility = Object.entries(modules).filter(([, m]) => m.slot === 'utility' && m.tier === 1);
  const armor = utility.find(([, m]) => m.blockKinetic > 0);
  const cloud = utility.find(([, m]) => m.blockEnergy > 0);
  const guard = utility.find(([, m]) => m.intercept);
  const dodge = utility.find(([, m]) => m.evasion > 0);
  check(`defence modules in the catalogue: ${[armor, cloud, guard, dodge].map((m) => m?.[0]).join(', ')}`,
    !!armor && !!cloud && !!guard && !!dodge);
  // Комплекс сам себе перезарядка и урон: оружейного слота у него нет, занять их не у кого.
  check(`${guard[0]} carries its own cooldown ${guard[1].intercept.cooldown} s and damage ${guard[1].intercept.damage}`,
    guard[1].intercept.cooldown > 0 && guard[1].intercept.damage > 0);

  // В Vega: в Sol PvP выключен, друг по другу там не стреляют.
  await Promise.all([shooter, target].map((c) => c.jumpTo('vega')));
  check(`both in ${shooter.welcome.system.name}, PvP ${shooter.welcome.system.pvp}`, shooter.welcome.system.pvp !== 'off');
  const gun = shooter.id;
  const victim = target.id;
  // Никуда не летим: оба вышли у одних врат и стоят в одной точке. Это здесь и нужно — дистанция 0,
  // а у зенитки и лазера нет штрафа за стрельбу в упор (closeRange 0), так что объявленный шанс равен
  // точности минус уклонение, и прибавку дюз видно ровно.
  //
  // Цель — крейсер с большим щитом и генератором под него: блок бросается только по попаданию, и чтобы
  // блоков набралось наверняка, цель должна выдержать под огнём минуту, а не погибнуть на десятом выстреле.
  // Стрелок бьёт зениткой — самой слабой кинетикой, — и позже лазером: оба класса S и встают в лёгкий корпус.
  target.send({ t: 'hull', id: 'cruiser' });
  await target.until(() => target.hangar.hull === 'cruiser', 3000, 'the target takes a cruiser');
  target.send({ t: 'fit', slot: 'generator', id: 'generatorL' });
  await target.until(() => target.hangar.fit.generator === 'generatorL', 3000, 'a generator that powers it all');
  target.send({ t: 'fit', slot: 'shield', id: 'shieldL' });
  await target.until(() => target.hangar.fit.shield === 'shieldL', 3000, 'a big shield');
  shooter.send({ t: 'weapon', id: 'pointDefense' });
  await shooter.until(() => shooter.hangar?.fit.weapons[0] === 'pointDefense', 3000, 'the shooter takes a flak gun');
  shooter.aim = shooter.face(victim);
  target.aim = target.face(gun);
  await shooter.until(() => shooter.snapshot.ships.some((s) => s.id === victim), 3000, 'the target in the snapshot');

  /** Выстрелы стрелка по цели начиная с этого тика — по снапшотам цели: ей они и важны. */
  const incoming = (since) => target.shots(gun, since).filter((shot) => shot.to === victim);

  // Защита после прыжка кончится сама; огонь включаем и держим до конца.
  shooter.send({ t: 'target', id: victim });
  shooter.send({ t: 'fire', on: true });

  // 1. Голышом: шанс, который сервер объявляет по цели, — точка отсчёта для дюз.
  let since = target.snapshot.tick;
  await target.until(() => incoming(since).length >= 3, 40000, 'the shooter hitting at us');
  const bare = incoming(since).at(-1).ch;
  check(`the shooter is told ${bare}% to hit`, bare > 0);

  // 2. Маневровые дюзы: тот же стрелок объявляет меньший шанс.
  target.send({ t: 'fit', slot: 'u0', id: dodge[0] });
  await target.until(() => (target.hangar.fit.utility ?? [])[0] === dodge[0], 3000, `fitting ${dodge[0]}`);
  since = target.snapshot.tick;
  await target.until(() => incoming(since).length >= 3, 20000, 'shots after the thrusters');
  const dodged = incoming(since).at(-1).ch;
  check(`${dodge[0]} drops it to ${dodged}% (−${bare - dodged})`, dodged === bare - dodge[1].evasion);

  // 3. Динамическая защита против импульсной: попадание приходит отбитым — урона нет, и это не промах.
  target.send({ t: 'fit', slot: 'u0', id: armor[0] });
  await target.until(() => (target.hangar.fit.utility ?? [])[0] === armor[0], 3000, `fitting ${armor[0]}`);
  since = target.snapshot.tick;
  await target.until(() => incoming(since).some((shot) => shot.blk), 60000, 'a blocked kinetic shot');
  const blocked = incoming(since).find((shot) => shot.blk);
  check(`${armor[0]} blocked a kinetic shot: dmg ${blocked.dmg}, hit ${blocked.hit}`, blocked.dmg === 0 && blocked.hit === false);

  // 4. Она же против лазера: вид урона другой, и броня его не трогает.
  shooter.send({ t: 'weapon', id: 'laser' });
  await shooter.until(() => shooter.hangar.fit.weapons[0] === 'laser', 3000, 'the shooter switches to a laser');
  since = target.snapshot.tick;
  await target.until(() => incoming(since).some((shot) => shot.hit), 60000, 'a laser hit through kinetic armour');
  check(`${armor[0]} does not stop a laser`, incoming(since).some((shot) => shot.hit && !shot.blk));

  // 5. Завеса против лазера: теперь отбивают его.
  target.send({ t: 'fit', slot: 'u0', id: cloud[0] });
  await target.until(() => (target.hangar.fit.utility ?? [])[0] === cloud[0], 3000, `fitting ${cloud[0]}`);
  since = target.snapshot.tick;
  await target.until(() => incoming(since).some((shot) => shot.blk), 60000, 'a blocked laser shot');
  check(`${cloud[0]} scattered a laser`, incoming(since).some((shot) => shot.blk && shot.dmg === 0));

  // 6. Комплекс встаёт во вспомогательный слот, оружейные не трогает.
  target.send({ t: 'fit', slot: 'u0', id: guard[0] });
  await target.until(() => (target.hangar.fit.utility ?? [])[0] === guard[0], 3000, `fitting ${guard[0]}`);
  check(`${guard[0]} takes a utility slot, guns untouched: ${target.hangar.fit.weapons.filter(Boolean).join(', ')}`,
    target.hangar.fit.weapons.filter(Boolean).length > 0);

  shooter.close();
  target.close();
  await sleep(300); // Node на Windows роняет сокеты, закрытые на выходе
}

main().then(
  () => {
    const failed = results.filter((ok) => !ok).length;
    if (failed > 0) {
      console.log(`
${failed} of ${results.length} checks failed`);
      process.exit(1);
    }
    console.log(`
All ${results.length} checks passed`);
    process.exit(0);
  },
  (error) => {
    console.log(`FAIL ${error.message}`);
    process.exit(1);
  },
);
