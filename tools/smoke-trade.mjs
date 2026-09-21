// Сквозная проверка обмена между игроками (M16b) без браузера: A зовёт B, оба кладут на стол,
// подтверждают — груз и кредиты меняются местами. Вторая сделка рвётся обрывом связи.
// Стыковку, гибель, прыжок и разъезд проверяют серверные тесты: там для этого не нужно никуда лететь.
// Нужен запущенный сервер и Node 24 (встроенный WebSocket).
//   node tools/smoke-trade.mjs [ws://localhost:5000/ws]

import { PROTOCOL_VERSION, openSocket, undock } from './wire.mjs';

const url = process.argv[2] ?? 'ws://localhost:5000/ws';

class Client {
  constructor(name) {
    this.name = name;
    this.welcome = null;
    this.cargo = null;
    this.hangar = null;
    this.messages = [];
    this.listeners = new Set();
  }

  connect() {
    return new Promise((resolve, reject) => {
      const { ws, read } = openSocket(url);
      this.ws = ws;
      ws.onopen = () => ws.send(JSON.stringify({ t: 'hello', name: this.name, hull: 'light' }));
      ws.onerror = () => reject(new Error(`cannot connect to ${url}`));
      ws.onmessage = (e) => {
        const message = read(e.data);
        if (message.t === 'welcome') {
          this.welcome = message;
          resolve(message);
        }
        if (message.t === 'cargo') this.cargo = message;
        if (message.t === 'hangar') this.hangar = message;
        if (message.t === 'snapshot') this.snapshot = message;
        else this.messages.push(message);
        for (const listener of this.listeners) listener();
      };
    });
  }

  send(message) {
    this.ws.send(JSON.stringify(message));
  }

  last(type) {
    return this.messages.findLast((m) => m.t === type);
  }

  until(predicate, timeoutMs, what) {
    return new Promise((resolve, reject) => {
      const check = () => {
        if (!predicate()) return;
        clearTimeout(timer);
        this.listeners.delete(check);
        resolve();
      };
      const timer = setTimeout(() => {
        this.listeners.delete(check);
        reject(new Error(`timeout: ${what}`));
      }, timeoutMs);
      this.listeners.add(check);
      check();
    });
  }
}

const results = [];

function check(text, pass) {
  results.push(pass);
  console.log(`${pass ? 'OK  ' : 'FAIL'} ${text}`);
}

/** Сколько всего штук в трюме: имена товаров у разных сборок баланса свои, а счёт — общий. */
function total(cargo) {
  return Object.values(cargo?.items ?? {}).reduce((sum, n) => sum + n, 0);
}

/** Первый предмет трюма; null — трюм пуст. */
function anyItem(cargo) {
  return Object.keys(cargo?.items ?? {})[0] ?? null;
}

async function main() {
  const suffix = Math.floor(100 + Math.random() * 900);
  const a = new Client(`TradeA-${suffix}`);
  const b = new Client(`TradeB-${suffix}`);
  await a.connect();
  await undock(a, 'a');
  await b.connect();
  await undock(b, 'b');
  const idA = a.welcome.id;
  const idB = b.welcome.id;
  check(`protocol ${a.welcome.version}`, a.welcome.version === PROTOCOL_VERSION);
  check(`trade rules: range ${a.welcome.trade?.range}`, (a.welcome.trade?.range ?? 0) > 0);

  await a.until(() => a.cargo, 2000, 'A knows its hold');
  await b.until(() => b.cargo, 2000, 'B knows its hold');
  const creditsA = a.cargo.credits;
  const creditsB = b.cargo.credits;
  const item = anyItem(a.cargo);
  const startB = total(b.cargo);

  a.send({ t: 'trade', action: 'invite', id: idB });
  await b.until(() => b.last('tradeInvite'), 2000, 'B gets the offer');
  const invite = b.last('tradeInvite');
  check(`B is offered a trade by ${invite.name} for ${invite.seconds} s`, invite.from === idA);

  b.send({ t: 'trade', action: 'accept', id: idA });
  await a.until(() => a.last('tradeState')?.active, 2000, 'A sees the table');
  await b.until(() => b.last('tradeState')?.active, 2000, 'B sees the table');
  check('the table is open for both', true);

  // A кладёт кредиты и (если есть) груз, B — только кредиты: так проверяются обе половины сразу.
  const give = item ? { [item]: 1 } : {};
  a.send({ t: 'trade', action: 'offer', credits: 100, items: give });
  await b.until(() => b.last('tradeState')?.their?.credits === 100, 2000, 'B sees what A offers');
  b.send({ t: 'trade', action: 'offer', credits: 50, items: {} });
  await a.until(() => a.last('tradeState')?.their?.credits === 50, 2000, 'A sees what B offers');
  const rev = a.last('tradeState').rev;
  check(`both halves are on the table, rev ${rev}`, rev > 0);

  // Устаревшее подтверждение сервер не принимает.
  a.send({ t: 'trade', action: 'ready', rev: rev - 1 });
  await a.until(() => a.last('tradeEvent')?.code === 'stale', 2000, 'a stale confirmation is refused');
  check('a stale confirmation is refused', true);

  a.send({ t: 'trade', action: 'ready', rev });
  b.send({ t: 'trade', action: 'ready', rev });
  await a.until(() => a.last('tradeEvent')?.code === 'done', 3000, 'the deal goes through');
  await b.until(() => b.last('tradeEvent')?.code === 'done', 3000, 'B is told too');
  await a.until(() => a.cargo.credits === creditsA - 100 + 50, 2000, 'A got the credits right');
  await b.until(() => b.cargo.credits === creditsB - 50 + 100, 2000, 'B got the credits right');
  check(`credits moved both ways: A ${creditsA} → ${a.cargo.credits}, B ${creditsB} → ${b.cargo.credits}`, true);
  if (item) {
    await b.until(() => total(b.cargo) === startB + 1, 2000, 'B got the cargo');
    check(`cargo moved: B has ${total(b.cargo)} items`, true);
  } else {
    check('cargo skipped: A started with an empty hold', true);
  }
  check('the window is closed for both', !a.last('tradeState').active && !b.last('tradeState').active);

  // Вторая сделка: у B обрывается связь, и стол должен закрыться сам — иначе A дожал бы её в одиночку.
  a.send({ t: 'trade', action: 'invite', id: idB });
  await b.until(() => b.last('tradeInvite'), 2000, 'B is offered again');
  b.send({ t: 'trade', action: 'accept', id: idA });
  await a.until(() => a.last('tradeState')?.active, 2000, 'the table is open again');
  b.ws.close();
  await a.until(() => a.last('tradeEvent')?.code === 'left', 4000, 'a lost connection breaks the deal');
  check('a lost connection breaks the deal, and the table closes', !a.last('tradeState').active);

  a.ws.close();
}

main().then(
  () => process.exit(results.every(Boolean) ? 0 : 1),
  (e) => {
    console.log(`FAIL ${e.message}`);
    process.exit(1);
  },
);
