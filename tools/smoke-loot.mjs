// Сквозная проверка добычи без браузера: каталог лута в welcome, контейнеры в космосе, подбор тракторным лучом,
// трюм в личном сообщении, сохранность груза при переподключении и сдача груза на станции.
// Проверяем через контейнер, а не через убийство пирата: у контейнера постоянная точка и содержимое,
// а бой в смоке зависит от TTK и от того, кто кого успеет убить. Дроп с обломков покрыт тестами комнаты.
// Нужен запущенный сервер и Node 24 (встроенный WebSocket). Идёт ~40–60 с: полёт к контейнеру и к станции.
//   node tools/smoke-loot.mjs [ws://localhost:5000/ws]

const url = process.argv[2] ?? 'ws://localhost:5000/ws';
const INPUT_INTERVAL_MS = 50;
const CLOSE_HIDDEN = 4000;
const RUN = Math.floor(100 + Math.random() * 900);
const STATION = { x: 0, y: 0 };

class Client {
  constructor(name) {
    this.name = name;
    this.token = Array.from(crypto.getRandomValues(new Uint8Array(16)), (b) => b.toString(16).padStart(2, '0')).join('');
    this.welcome = null;
    this.cargo = null;
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

  /** Предметы, лежащие в космосе, из последнего снапшота. */
  get loot() {
    return this.snapshot?.loot ?? [];
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
        } else if (message.t === 'cargo') this.cargo = message;
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

  /** Обрыв и возврат с той же сессией: корабль и трюм должны дождаться. */
  async reconnect() {
    this.ws.close(CLOSE_HIDDEN, 'hidden');
    clearInterval(this.timer);
    this.welcome = null;
    this.cargo = null;
    await new Promise((resolve) => setTimeout(resolve, 400));
    await this.connect();
    this.start();
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

const distance = (a, b) => Math.hypot(a.x - b.x, a.y - b.y);
const total = (items) => Object.values(items ?? {}).reduce((sum, n) => sum + n, 0);

async function main() {
  const a = new Client(`Smoke-Loot-${RUN}`);
  await a.connect();

  const loot = a.welcome.loot;
  const items = Object.keys(loot?.items ?? {});
  check(
    `welcome carries loot.json: ${items.length} items, pickup range ${loot?.pickupRange}`,
    items.length > 0 && loot.pickupRange > 0,
  );
  check(`loot tables exist for pirates: ${Object.keys(loot?.tables ?? {}).join(', ')}`, Object.keys(loot?.tables ?? {}).length > 0);

  await a.until(() => a.cargo !== null, 3000, 'the cargo message after welcome');
  check(`empty hold on join: ${a.cargo.used} / ${a.cargo.max}`, a.cargo.used === 0 && a.cargo.max > 0 && total(a.cargo.items) === 0);

  a.start();
  await a.until(() => a.me && a.loot.some((d) => d.c), 5000, 'containers in space');
  const boxes = a.loot.filter((d) => d.c);
  check(`containers are in the world: ${boxes.length}`, boxes.length > 0 && boxes.every((d) => d.i && d.n > 0));

  // Тап по предмету только помечает его: сервер должен принять выбор и не отвалиться.
  const box = boxes.reduce((best, d) => (distance(d, a.me) < distance(best, a.me) ? d : best));
  a.send({ t: 'loot', id: box.id });
  await new Promise((resolve) => setTimeout(resolve, 200));
  check('selecting an item keeps the connection alive', a.ws.readyState === WebSocket.OPEN);

  console.log(`     flying to ${box.i} x${box.n} at ${Math.round(box.x)}, ${Math.round(box.y)}`);
  a.control = a.flyTo({ x: box.x, y: box.y }, 0);
  await a.until(
    () => a.me && Math.hypot(a.me.x - box.x, a.me.y - box.y) <= loot.pickupRange,
    45000,
    'reaching the container',
  );

  // Подбор ручной: пока не скомандуешь, предмет так и лежит.
  a.control = () => [0, -1, 0];
  const picks = () => a.snapshots.flatMap((s) => (s.picks ?? []).filter((p) => p.by === a.id));
  await new Promise((resolve) => setTimeout(resolve, 600));
  check('hovering over the item does not take it', picks().length === 0);

  a.send({ t: 'grab' });
  await a.until(() => picks().length > 0, 5000, 'the grab command takes the item');

  const pick = picks()[0];
  check(`tractor beam took ${pick.i} x${pick.n}`, pick.id === box.id && pick.n === box.n);
  await a.until(() => a.cargo && total(a.cargo.items) > 0, 3000, 'the hold after the pickup');
  const carried = { ...a.cargo.items };
  check(`hold filled: ${a.cargo.used} / ${a.cargo.max} (${Object.entries(carried).map(([k, n]) => `${k} x${n}`).join(', ')})`, a.cargo.used > 0);
  check('the item is gone from space', !a.loot.some((d) => d.id === box.id));

  // GDD §24: груз переживает обрыв связи, и клиент видит его сразу после возвращения.
  a.control = () => [0, -1, 0];
  await a.reconnect();
  check('resumed the same ship', a.welcome.resumed === true);
  await a.until(() => a.cargo !== null, 3000, 'the cargo message after the reconnect');
  check(
    `hold survived the reconnect: ${JSON.stringify(a.cargo.items)}`,
    JSON.stringify(a.cargo.items) === JSON.stringify(carried),
  );

  if (!loot.stationUnload) {
    check('station unloading is off in loot.json — skipping the last step', true);
    a.close();
    return;
  }

  console.log('     flying to the station to sell');
  a.control = a.flyTo(STATION, 0);
  await a.until(
    () => a.me && Math.hypot(a.me.x - STATION.x, a.me.y - STATION.y) <= loot.stationRange,
    60000,
    'reaching the station',
  );

  // Продажа тоже ручная: стоять в круге мало.
  a.control = () => [0, -1, 0];
  await new Promise((resolve) => setTimeout(resolve, 600));
  check('standing at the station does not sell the cargo', total(a.cargo.items) > 0);

  a.send({ t: 'sell' });
  await a.until(() => a.notices.includes('unloaded'), 5000, 'the cargo is sold at the station');
  await a.until(() => a.cargo && total(a.cargo.items) === 0, 2000, 'the hold is empty after selling');
  check(`cargo sold for ${a.cargo.credits} credits`, a.cargo.credits > 0 && a.cargo.used === 0);
  check(`own ship alive at the station: hull ${a.me.hp}`, !a.me.rt);

  a.close();
}

main().then(
  () => process.exit(results.every(Boolean) ? 0 : 1),
  (e) => {
    console.log(`FAIL ${e.message}`);
    process.exit(1);
  },
);
