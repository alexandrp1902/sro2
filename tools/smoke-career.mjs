// Сквозная проверка выбора пути (M15.5): свободен ли ник, карточки путей, старт торговца
// (грузовой корпус, место, товар в трюме, своя ветка обучения) и отказ закрытому пути.
// Заводит на сервере аккаунты smoke-car-NNN — они останутся в data/accounts.
// Нужен запущенный сервер и Node 24 (встроенный WebSocket). Идёт ~20 секунд.
//   node tools/smoke-career.mjs [ws://localhost:5000/ws]

import { openSocket } from './wire.mjs';

const url = process.argv[2] ?? 'ws://localhost:5000/ws';
const CLOSE_HIDDEN = 4000;
const CLOSE_DENIED = 4003;
const RUN = Math.floor(100 + Math.random() * 900);
const PASSWORD = 'smoke-pass';

class Client {
  constructor(hello) {
    this.hello = hello;
    this.welcome = null;
    this.account = null;
    this.denied = null;
    this.hangar = null;
    this.cargo = null;
    this.missions = null;
    this.nameFree = null;
    this.closedWith = null;
    this.listeners = new Set();
  }

  /** Открывает сокет; hello шлётся, только если он задан, — иначе сначала спрашиваем про ник. */
  open() {
    return new Promise((resolve, reject) => {
      const { ws, read } = openSocket(url);
      this.ws = ws;
      ws.onopen = () => {
        if (this.hello) this.send({ t: 'hello', ...this.hello });
        resolve();
      };
      ws.onerror = () => reject(new Error(`cannot connect to ${url}`));
      ws.onclose = (e) => {
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
        else if (message.t === 'missions') this.missions = message;
        else if (message.t === 'nameFree') this.nameFree = message;
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
    this.ws.close(CLOSE_HIDDEN, 'hidden');
  }
}

const results = [];

function check(text, pass) {
  results.push(pass);
  console.log(`${pass ? 'OK  ' : 'FAIL'} ${text}`);
}

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

/** Спросить про ник тем же коротким сокетом, что и клиент до входа. */
async function ask(name) {
  const c = new Client(null);
  await c.open();
  c.send({ t: 'check', name });
  await c.until(() => c.nameFree !== null, 5000, `answer about ${name}`);
  c.close();
  return c.nameFree;
}

async function main() {
  console.log(`smoke-career against ${url}`);
  const fresh = `smoke-car-${RUN}`;

  const free = await ask(fresh);
  check(`a free name is free: ${fresh}`, free.free === true);
  check(
    `careers come with the answer: ${free.careers.map((c) => c.id).join(', ')}`,
    free.careers.length >= 2 && free.careers.some((c) => c.id === 'trader'),
  );
  check(`the default career is ${free.career}`, free.career === 'ranger');
  const locked = free.careers.filter((c) => !c.enabled).map((c) => c.id);
  check(`a locked career is shown but not offered: ${locked.join(', ') || 'none'}`, locked.includes('pirate'));

  // Закрытый путь отклоняется — и, что важнее, аккаунт за собой не оставляет.
  const denied = new Client({ name: `${fresh}-p`, password: PASSWORD, career: 'pirate' });
  await denied.open();
  await denied.until(() => denied.closedWith !== null, 5000, 'a locked career is refused');
  check(
    `a locked career is refused with ${denied.denied}`,
    denied.denied === 'badCareer' && denied.closedWith === CLOSE_DENIED && denied.welcome === null,
  );
  const after = await ask(`${fresh}-p`);
  check('the refused name is still free: no account was left behind', after.free === true);

  // Торговец: грузовой корпус, своё место, товар в трюме и своя ветка обучения.
  const trader = new Client({ name: fresh, password: PASSWORD, career: 'trader' });
  await trader.open();
  await trader.until(() => trader.welcome && trader.hangar && trader.cargo && trader.missions, 8000, 'the trader joins');
  const cargo = Object.entries(trader.cargo.items ?? {});
  check(`the trader flies a ${trader.hangar.hull}`, trader.hangar.hull === 'industrial');
  check(`and carries ${cargo.map(([i, n]) => `${n} ${i}`).join(', ') || 'nothing'}`, cargo.length > 0);
  check(`with ${trader.cargo.credits} credits, fewer than a ranger's 1000`, trader.cargo.credits < 1000);
  check(
    `its first step is its own: «${trader.missions.tutorial?.title ?? '—'}»`,
    trader.missions.tutorial?.id === 'undock' && /Порт/.test(trader.missions.tutorial?.title ?? ''),
  );
  check(
    `it starts docked at ${trader.hangar.place?.name ?? 'nowhere'}`,
    trader.hangar.docked === true && trader.hangar.place?.key?.startsWith('pl:') === true,
  );

  // Этот ник уже занят: карточки пути ему показывать нельзя.
  const taken = await ask(fresh);
  check('a taken name is not free any more', taken.free === false);

  // Вернувшемуся путь не меняют: он уже в профиле.
  trader.close();
  await sleep(400);
  const again = new Client({ name: fresh, password: PASSWORD, career: 'ranger' });
  await again.open();
  await again.until(() => again.hangar !== null, 8000, 'the trader comes back');
  check(`coming back as a «ranger» changes nothing: still a ${again.hangar.hull}`, again.hangar.hull === 'industrial');
  again.close();

  // Рейнджер по умолчанию — ровно тот старт, что был до M15.5.
  const ranger = new Client({ name: `${fresh}-r`, password: PASSWORD, career: 'ranger' });
  await ranger.open();
  await ranger.until(() => ranger.hangar !== null && ranger.cargo !== null, 8000, 'the ranger joins');
  check(
    `a ranger still starts with a ${ranger.hangar.hull} and ${ranger.cargo.credits} credits`,
    ranger.hangar.hull === 'light' && ranger.cargo.credits === 1000,
  );
  ranger.close();
  await sleep(300); // process.exit, пока сокеты ещё закрываются, роняет Node на Windows (UV_HANDLE_CLOSING)
}

main().then(
  () => process.exit(results.every(Boolean) ? 0 : 1),
  (e) => {
    console.log(`FAIL ${e.message}`);
    process.exit(1);
  },
);
