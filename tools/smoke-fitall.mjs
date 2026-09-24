// Сквозная проверка оснащения пачкой и своего фита у каждого корпуса (M20) без браузера: покупка корпуса
// не раздевает прежний, «снять всё» и «поставить всё» работают, корабль в ангаре раздевается на месте,
// а мастер верфи рассказывает про чужой стапель. Заводит аккаунт fitall-NNN — он останется в data/accounts.
// Нужен запущенный сервер и Node 24 (встроенный WebSocket). Идёт несколько секунд.
//   node tools/smoke-fitall.mjs [ws://localhost:5000/ws]

import { openSocket } from './wire.mjs';

const url = process.argv[2] ?? 'ws://localhost:5000/ws';
const NAME = `fitall-${Math.floor(100 + Math.random() * 900)}`;
const PASSWORD = 'smoke-pass';

class Client {
  constructor(hello) {
    this.hello = hello;
    this.welcome = null;
    this.hangar = null;
    this.cargo = null;
    this.market = null;
    this.notices = [];
    this.listeners = new Set();
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
      ws.onmessage = (e) => {
        const m = read(e.data);
        if (m.t === 'welcome') this.welcome = m;
        else if (m.t === 'hangar') this.hangar = m;
        else if (m.t === 'cargo') this.cargo = m;
        else if (m.t === 'market') this.market = m;
        else if (m.t === 'notice') this.notices.push(m.code);
        for (const listener of this.listeners) listener();
      };
    });
  }

  send(message) {
    if (this.ws.readyState === WebSocket.OPEN) this.ws.send(JSON.stringify(message));
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

  /** Послать и дождаться ответного ангара: сервер шлёт его после каждой перестановки. */
  async act(message, what) {
    const before = this.hangar;
    this.send(message);
    await this.until(() => this.hangar !== before, 4000, what);
  }

  close() {
    this.ws.close(4000, 'hidden');
  }
}

const results = [];
const check = (text, pass) => {
  results.push(pass);
  console.log(`${pass ? 'OK  ' : 'FAIL'} ${text}`);
};

/** Что стоит в слоте по присланному фиту. */
const at = (fit, slot) => {
  if (!fit) return null;
  if (slot.startsWith('w')) return fit.weapons?.[Number(slot.slice(1))] ?? null;
  if (slot.startsWith('u')) return fit.utility?.[Number(slot.slice(1))] ?? null;
  return fit[slot] ?? null;
};

const dressed = (fit) => [...(fit.weapons ?? []), fit.shield, ...(fit.utility ?? [])].filter(Boolean).length;

async function main() {
  console.log(`smoke-fitall against ${url}, pilot ${NAME}`);
  const c = new Client({ name: NAME, password: PASSWORD, create: true });
  await c.open();
  await c.until(() => c.welcome && c.hangar?.docked, 8000, 'docked after login');

  const start = c.hangar;
  const starterHull = start.hull;
  check('пилот входит в доке на стартовом корпусе с пушкой', at(start.fit, 'w0') !== null);

  // Снять всё и одеть обратно — на том корабле, который под пилотом: для этого не нужно ни кредитов,
  // ни покупок, а проверяется главное — что пачка ходит в обе стороны и ничего не теряет по пути.
  await c.act({ t: 'fitAll', mode: 'strip' }, 'ship stripped');
  check('«снять всё» снимает пушку и щит', dressed(c.hangar.fit) === 0);
  check('и оставляет то, без чего корабль не летает', ['engine', 'radar', 'generator'].every((slot) => at(c.hangar.fit, slot) !== null));
  check('снятое лежит на складе станции', Object.values(c.hangar.storage ?? {}).some((count) => count > 0));

  c.notices.length = 0;
  c.send({ t: 'fitAll', mode: 'strip' });
  await c.until(() => c.notices.includes('nothingToFit'), 4000, 'nothing to strip notice');
  check('на раздетом корабле кнопка честно говорит, что снимать нечего', true);

  await c.act({ t: 'fitAll', mode: 'fill' }, 'ship filled');
  check('«поставить все» возвращает снаряжение со склада', dressed(c.hangar.fit) > 0);
  check('энергии хватает на то, что встало', (c.hangar.power ?? 0) <= (c.hangar.powerMax ?? 0));

  // Покупка корпуса: кредитов новичку хватает не везде, поэтому проверка по средствам.
  const shop = c.welcome.shop ?? {};
  const prices = shop.hulls ?? {};
  const credits = c.cargo?.credits ?? 0;
  const target = Object.entries(prices)
    .filter(([id, cost]) => id !== starterHull && cost > 0 && cost <= credits && (!shop.stock || shop.stock.includes(id)))
    .sort((a, b) => b[1] - a[1])[0];
  if (!target) {
    console.log('SKIP: здесь не продают корпус по карману — покупку проверять не на чем');
  } else {
    const [hullId] = target;
    const before = dressed(c.hangar.fit);
    await c.act({ t: 'buy', kind: 'hull', id: hullId }, 'hull bought');
    check(`купленный «${hullId}» приходит голым`, c.hangar.hull === hullId && dressed(c.hangar.fit) === 0);
    check('на нём стоит самое дешёвое из обязательного', ['engine', 'radar', 'generator'].every((slot) => at(c.hangar.fit, slot) !== null));
    check('прежний корабль ждёт в ангаре со своим оснащением', dressed(c.hangar.fits?.[starterHull] ?? {}) === before);

    // Раздеть корабль, который стоит здесь же: за модулями больше не надо в него пересаживаться.
    await c.act({ t: 'fitAll', mode: 'strip', hull: starterHull }, 'parked ship stripped');
    check('«снять всё» в ангаре кладёт его снаряжение на склад', dressed(c.hangar.fits?.[starterHull] ?? {}) === 0);
    check('раздетый корабль всё ещё может летать', at(c.hangar.fits?.[starterHull], 'engine') !== null);
  }

  // Слух мастера верфи: он про корпус, которого у пилота нет, и про чужую систему.
  await c.until(() => c.market !== null, 6000, 'market with rumours');
  const yard = c.market?.rumours?.find((r) => r.kind === 'yard');
  if (!yard) console.log('SKIP: соседние верфи не торгуют ничем новым — слуху неоткуда взяться');
  else {
    check('мастер советует чужую систему', yard.system !== c.welcome.system && yard.hops > 0);
    check('и корпус, которого нет ни в ангаре, ни на здешней витрине', !c.hangar.hulls.includes(yard.good) && !(c.welcome.shop?.stock ?? []).includes(yard.good));
  }

  c.close();
  const failed = results.filter((ok) => !ok).length;
  console.log(failed === 0 ? `\nall ${results.length} checks passed` : `\n${failed} of ${results.length} checks FAILED`);
  process.exit(failed === 0 ? 0 : 1);
}

main().catch((e) => {
  console.error(e);
  process.exit(1);
});
