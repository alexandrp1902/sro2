// Сквозная проверка рынка товаров (M12) без браузера: правила рынка в welcome, живые цены в доке,
// покупка товара за кредиты, движение цены после сделки, продажа обратно себе в убыток и отказы.
// Гостем и прямо у станции: полёт и бой здесь ни при чём, их проверяют другие смоуки.
// Нужен запущенный сервер и Node 24 (встроенный WebSocket). Идёт ~15 с.
//   node tools/smoke-market.mjs [ws://localhost:5000/ws]

import { openSocket, stationAt } from './wire.mjs';

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

  /** Строка рынка по товару; undefined — им здесь не торгуют. */
  quote(item) {
    return this.market?.items.find((i) => i.id === item);
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

const total = (items) => Object.values(items ?? {}).reduce((sum, n) => sum + n, 0);

async function main() {
  const a = new Client(`Smoke-Market-${RUN}`);
  await a.connect();

  const market = a.welcome.market;
  const goods = Object.keys(market?.goods ?? {});
  check(`welcome carries market.json: ${goods.length} goods, spread ${market?.spread}`, goods.length > 0 && market.spread > 0);
  const produces = market?.station?.produces ?? [];
  check(`the station has a profile: produces ${produces.join(', ') || '—'}`, produces.length > 0);

  // К станции и в док: рынок есть только там.
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
  await a.until(() => a.market !== null, 3000, 'the market message after docking');

  const rows = a.market.items;
  check(`docking brings the prices: ${rows.length} goods`, rows.length > 0);
  check('the station never buys higher than it sells', rows.every((i) => i.buy > i.sell));

  // Слухи — подсказки из настоящих цен соседей: маршрут обязан быть выгодным, иначе это враньё.
  const rumours = a.market.rumours ?? [];
  for (const r of rumours) console.log(`     слух: ${r.kind} ${r.good} → ${r.name} (${r.hops} прыжка), ${r.price} кр`);
  check(`the trader has something to say: ${rumours.length} rumours`, rumours.length > 0);
  check(
    'every rumour points somewhere else, within reach',
    rumours.every((r) => r.system !== a.welcome.system.id && r.hops > 0 && r.hops <= 5),
  );
  const routes = rumours.filter((r) => r.kind === 'route');
  check(
    `routes are actually profitable: ${routes.map((r) => `${r.good} +${r.profit}`).join(', ') || '—'}`,
    routes.every((r) => r.profit > 0 && produces.includes(r.good)),
  );

  // Покупаем то, что станция делает сама: чужой товар она скупает, но не перепродаёт.
  const good = produces.find((id) => a.quote(id)?.stock > 0);
  check(`something produced here is in stock: ${good ?? 'nothing'}`, !!good);
  if (!good) return;

  const before = { credits: a.cargo.credits, stock: a.quote(good).stock, buy: a.quote(good).buy };
  const count = 5;
  a.send({ t: 'buyGoods', item: good, count });
  await a.until(() => (a.cargo.items[good] ?? 0) >= count, 5000, 'the goods arriving in the hold');
  await a.until(() => a.quote(good).stock < before.stock, 3000, 'the station stock going down');

  const spent = before.credits - a.cargo.credits;
  check(`buying ${count} ${good} costs credits: ${spent}`, spent >= before.buy * count);
  check(`the hold has them: ${a.cargo.items[good]} (${a.cargo.used} / ${a.cargo.max})`, a.cargo.items[good] === count);
  check(`the warehouse emptied by ${before.stock - a.quote(good).stock}`, a.quote(good).stock <= before.stock - count);
  check(`the price went up: ${before.buy} → ${a.quote(good).buy}`, a.quote(good).buy >= before.buy);

  // Продаём обратно: спред обязан съесть разницу, иначе на месте печатались бы кредиты.
  const afterBuy = { credits: a.cargo.credits, buy: a.quote(good).buy };
  a.send({ t: 'sell', item: good, count });
  await a.until(() => !a.cargo.items[good], 5000, 'the goods leaving the hold');
  check(`selling it straight back loses money: ${a.cargo.credits - afterBuy.credits} back of ${spent}`, a.cargo.credits < before.credits);
  check(`the price came back down: ${afterBuy.buy} → ${a.quote(good).buy}`, a.quote(good).buy <= afterBuy.buy);

  // Частичная продажа: было нечем проверить до M12 — сообщение sell не умело количество.
  a.send({ t: 'buyGoods', item: good, count: 4 });
  await a.until(() => (a.cargo.items[good] ?? 0) === 4, 5000, 'four in the hold');
  a.send({ t: 'sell', item: good, count: 3 });
  await a.until(() => (a.cargo.items[good] ?? 0) === 1, 5000, 'three of four sold');
  check('selling part of a stack leaves the rest', a.cargo.items[good] === 1);

  a.send({ t: 'sell' });
  await a.until(() => total(a.cargo.items) === 0, 5000, 'the hold emptying');
  check('selling everything empties the hold', total(a.cargo.items) === 0);

  // Чего станция не производит, того не продаёт.
  const notSold = a.market.items.find((i) => !produces.includes(i.id));
  if (notSold) {
    a.notices.length = 0;
    a.send({ t: 'buyGoods', item: notSold.id, count: 1 });
    await a.until(() => a.notices.includes('noGoods'), 3000, 'the refusal for goods the station only buys');
    check(`buying ${notSold.id} is refused: the station only buys it`, !a.cargo.items[notSold.id]);
  }

  // Вне дока не торгуют.
  a.notices.length = 0;
  a.send({ t: 'dock', on: false });
  await a.until(() => a.hangar && !a.hangar.docked, 5000, 'undocking');
  a.send({ t: 'buyGoods', item: good, count: 1 });
  await a.until(() => a.notices.includes('tooFar'), 3000, 'the refusal outside the dock');
  check('buying outside the dock is refused', a.notices.includes('tooFar'));

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
