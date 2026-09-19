// Сквозная проверка аккаунтов и станции (M6) без браузера: вход по нику и паролю, отказы во входе,
// стартовые кредиты, стыковка, покупка в доке, вылет, возврат по ключу устройства и перехват корабля
// вторым устройством. Заводит на сервере аккаунт smoke-NNN — он останется в data/accounts.
// Нужен запущенный сервер и Node 24 (встроенный WebSocket). Идёт ~20 секунд.
//   node tools/smoke-account.mjs [ws://localhost:5000/ws]

import { openSocket, stationAt } from './wire.mjs';

const url = process.argv[2] ?? 'ws://localhost:5000/ws';
const INPUT_INTERVAL_MS = 50;
const CLOSE_HIDDEN = 4000;
const CLOSE_REPLACED = 4001;
const CLOSE_DENIED = 4003;
const RUN = Math.floor(100 + Math.random() * 900);
const NAME = `smoke-${RUN}`;
const PASSWORD = 'smoke-pass';

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

  /** Открывает сокет и шлёт hello; ждать ответа — через until. */
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

/** Отказ во входе: код и закрытие соединения. */
async function expectDenied(hello, code) {
  const c = new Client(hello);
  await c.open();
  await c.until(() => c.closedWith !== null, 5000, `refusal ${code}`);
  check(`login refused with ${code}`, c.denied === code && c.closedWith === CLOSE_DENIED && c.welcome === null);
}

async function main() {
  console.log(`smoke-account against ${url}, pilot ${NAME}`);

  // Новый ник с паролем заводит аккаунт; устройство получает ключ.
  const a = new Client({ name: NAME, password: PASSWORD });
  await a.open();
  await a.until(() => a.welcome && a.hangar && a.cargo, 5000, 'welcome, hangar and cargo');
  const shop = a.welcome.shop;
  check(`account created: ${a.account?.name}, device key ${a.account?.key?.length ?? 0} chars`, a.account?.name === NAME && !!a.account?.key);
  check(`shop.json in welcome: start ${shop?.startCredits} credits`, !!shop);
  check(`start credits: ${a.cargo.credits}`, a.cargo.credits === shop.startCredits);
  const guns = (hangar) => hangar.fit.weapons.filter(Boolean);
  check(
    `starter ship: ${a.hangar.hull} + ${guns(a.hangar).join(',')}, engine ${a.hangar.fit.engine}, energy ${a.hangar.power}/${a.hangar.powerMax}, hangar ${a.hangar.hulls.join(',')}`,
    a.hangar.hulls.length === 1 && guns(a.hangar).length === 1 && Object.keys(a.hangar.storage).length === 0,
  );
  // Новый пилот начинает в доке: первый шаг обучения — вылететь (M8). Обучение здесь не проверяем — его пропускаем.
  check(`new pilot starts in the dock: ${a.hangar.docked}`, a.hangar.docked);
  a.send({ t: 'mission', action: 'skip' });
  a.send({ t: 'dock', on: false });
  await a.until(() => !a.hangar.docked, 3000, 'undocking after skipping the tutorial');

  await expectDenied({ name: NAME, password: 'not-the-password' }, 'wrongPassword');
  await expectDenied({ name: `smoke-new-${RUN}`, password: 'abc' }, 'badPassword');
  await expectDenied({ name: 'ab', password: PASSWORD }, 'badName');
  await expectDenied({ key: 'no-such-key' }, 'badKey');

  // Наблюдатель-гость: видит ли он корабль в доке.
  const observer = new Client({ name: `watch-${RUN}` });
  await observer.open();
  await observer.until(() => observer.welcome, 5000, 'observer welcome');

  // Вне круга станции док не открывается.
  a.start();
  await a.until(() => a.me, 3000, 'own ship in a snapshot');
  const station = () => stationAt(a.welcome.system, a.snapshot?.tick ?? 0);
  const far = Math.hypot(a.me.x - station().x, a.me.y - station().y) > a.welcome.loot.stationRange;
  if (far) {
    a.send({ t: 'dock', on: true });
    await a.until(() => a.notices.includes('tooFar'), 3000, 'docking far away is refused');
    check('docking outside the station circle is refused', !a.hangar.docked);
  }

  console.log('     flying to the station');
  a.control = () => {
    const me = a.me;
    if (!me) return [0, -1, 0];
    const at = station();
    const d = Math.hypot(at.x - me.x, at.y - me.y);
    return [at.x - me.x, at.y - me.y, d > 120 ? 1 : 0];
  };
  await a.until(
    () => a.me && Math.hypot(a.me.x - station().x, a.me.y - station().y) <= a.welcome.loot.stationRange - 30,
    30000,
    'reaching the station',
  );
  a.send({ t: 'dock', on: true });
  await a.until(() => a.hangar.docked, 3000, 'docking');
  await observer.until(() => observer.snapshot && !observer.snapshot.ships.some((s) => s.id === a.id), 3000, 'ship leaves space');
  check('docked ship is gone from space for others', true);

  // Покупка (M9): самая дешёвая пушка по карману встаёт во второй, свободный слот; самый дорогой корпус — не по карману.
  // С M11 у станции свой ассортимент (shop.stock): чего здесь не продают, то и не купить ни за какие деньги.
  const credits = a.cargo.credits;
  const sold = (id) => !shop.stock || shop.stock.includes(id);
  const weapons = Object.entries(shop.items ?? {}).filter(([id]) => a.welcome.weapons[id] && sold(id)).sort((x, y) => x[1] - y[1]);
  const hulls = Object.entries(shop.hulls ?? {}).filter(([id]) => !a.hangar.hulls.includes(id) && sold(id)).sort((x, y) => y[1] - x[1]);
  let bought = null;
  if (weapons.length > 0 && weapons[0][1] <= credits) {
    const [id, price] = weapons[0];
    a.send({ t: 'buy', kind: 'item', id });
    await a.until(() => guns(a.hangar).length === 2, 3000, `buying ${id} into the free slot`);
    await a.until(() => a.cargo.credits === credits - price, 3000, 'credits after the purchase');
    check(`bought ${id} for ${price}: ${credits} → ${a.cargo.credits} credits, guns ${guns(a.hangar).join(',')}, energy ${a.hangar.power}/${a.hangar.powerMax}`, a.hangar.fit.weapons[1] === id);
    bought = id;
  } else check('no affordable weapon in shop.json — skipping the purchase', true);
  if (hulls.length > 0 && hulls[0][1] > a.cargo.credits) {
    a.send({ t: 'buy', kind: 'hull', id: hulls[0][0] });
    await a.until(() => a.notices.includes('noCredits'), 3000, 'not enough credits');
    check(`${hulls[0][0]} for ${hulls[0][1]} is out of reach: "noCredits"`, !a.hangar.hulls.includes(hulls[0][0]));
  }

  // Вылет: корабль там же, с новой пушкой.
  a.send({ t: 'dock', on: false });
  a.seq = 0; // сервер начал буфер входов заново — как настоящий клиент, нумеруем с 1
  await a.until(() => a.me, 3000, 'back in space after undocking');
  check(`undocked with ${guns(a.hangar).join(',')}, protected until tick ${a.me.pu ?? 0}`, (!bought || guns(a.hangar).length === 2) && (a.me.pu ?? 0) > 0);

  // Обрыв и возврат по ключу устройства — без пароля.
  const key = a.account.key;
  const shipId = a.id;
  a.close();
  const back = new Client({ key });
  await back.open();
  await back.until(() => back.welcome && back.hangar, 5000, 'welcome after the reconnect by key');
  check(`came back by device key: same ship ${back.id === shipId}, resumed ${back.welcome.resumed}`, back.id === shipId && back.welcome.resumed);
  check(`the purchase is still there: ${guns(back.hangar).join(',')}`, !bought || back.hangar.fit.weapons[1] === bought);

  // Второе устройство входит по паролю и забирает корабль; первое закрыто и само не рвётся назад.
  const phone = new Client({ name: NAME.toUpperCase(), password: PASSWORD });
  await phone.open();
  await phone.until(() => phone.welcome, 5000, 'second device welcome');
  await back.until(() => back.closedWith !== null, 3000, 'first device is closed');
  check(`second device took the ship (close ${back.closedWith})`, back.closedWith === CLOSE_REPLACED && phone.id === shipId);

  phone.close();
  observer.close();
}

main().then(
  () => process.exit(results.every(Boolean) ? 0 : 1),
  (e) => {
    console.log(`FAIL ${e.message}`);
    process.exit(1);
  },
);
