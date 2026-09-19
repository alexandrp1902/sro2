// Сквозная проверка мультиплеера без браузера: два клиента видят друг друга с никами, обрыв связи оставляет
// корабль в космосе, возврат с той же сессией восстанавливает его, дубль сессии вытесняет старое соединение.
// Нужен запущенный сервер и Node 24 (встроенный WebSocket).
//   node tools/smoke-mp.mjs [ws://localhost:5000/ws]

import { openSocket } from './wire.mjs';

const url = process.argv[2] ?? 'ws://localhost:5000/ws';
const INPUT_INTERVAL_MS = 50;
const CLOSE_HIDDEN = 4000;
const CLOSE_REPLACED = 4001;
// Корабли прошлого прогона ещё ждут на сервере 60 с — имя каждый раз новое, чтобы проверить дедуп.
const NAME = `Smoke-${Math.floor(100 + Math.random() * 900)}`;

class Client {
  constructor(name, token) {
    this.name = name;
    this.token = token;
    this.welcome = null;
    this.players = [];
    this.snapshot = null;
    this.closeCode = null;
    this.seq = 0;
    this.timer = 0;
    this.listeners = new Set();
  }

  connect() {
    return new Promise((resolve, reject) => {
      const { ws, read } = openSocket(url);
      this.ws = ws;
      ws.onopen = () => ws.send(JSON.stringify({ t: 'hello', name: this.name, hull: 'light', token: this.token }));
      ws.onerror = () => reject(new Error(`cannot connect to ${url}`));
      ws.onclose = (e) => {
        this.closeCode = e.code;
        clearInterval(this.timer);
        this.notify();
      };
      ws.onmessage = (e) => {
        const message = read(e.data);
        if (message.t === 'welcome') {
          this.welcome = message;
          resolve(message);
        } else if (message.t === 'players') this.players = message.players;
        else if (message.t === 'snapshot') this.snapshot = message;
        this.notify();
      };
    });
  }

  fly(throttle) {
    clearInterval(this.timer);
    this.timer = setInterval(() => {
      if (this.ws.readyState !== WebSocket.OPEN) return;
      this.ws.send(JSON.stringify({ t: 'input', seq: ++this.seq, dx: 0, dy: -1, th: throttle }));
    }, INPUT_INTERVAL_MS);
  }

  /** Как вкладка, ушедшая в фон. */
  close() {
    clearInterval(this.timer);
    this.ws.close(CLOSE_HIDDEN, 'hidden');
  }

  player(id) {
    return this.players.find((p) => p.id === id);
  }

  ship(id) {
    return this.snapshot?.ships.find((s) => s.id === id);
  }

  notify() {
    for (const listener of this.listeners) listener();
  }

  /** Ждёт, пока условие станет истинным; проверяется на каждом сообщении. */
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
}

const results = [];

function check(text, pass) {
  results.push(pass);
  console.log(`${pass ? 'OK  ' : 'FAIL'} ${text}`);
}

function token() {
  return Array.from(crypto.getRandomValues(new Uint8Array(16)), (b) => b.toString(16).padStart(2, '0')).join('');
}

const speed = (ship) => Math.hypot(ship.vx, ship.vy);

async function main() {
  const tokenA = token();
  const a = new Client(NAME, tokenA);
  const b = new Client(NAME, token());
  await a.connect();
  await b.connect();
  const idA = a.welcome.id;
  const idB = b.welcome.id;

  await b.until(() => b.player(idA) && b.player(idB), 2000, 'B sees both players');
  await a.until(() => a.player(idB), 2000, 'A sees B');
  check(`names: '${b.player(idA).name}' and '${b.player(idB).name}'`, b.player(idA).name === NAME && b.player(idB).name === `${NAME} 2`);

  a.fly(1);
  await b.until(() => b.ship(idA) && speed(b.ship(idA)) > 100, 3000, 'B sees A flying');
  check(`B sees A flying at ${speed(b.ship(idA)).toFixed(0)}`, true);

  a.close();
  await b.until(() => b.player(idA)?.online === false, 2000, 'A shown without connection');
  check('A is shown without connection', true);
  await b.until(() => b.ship(idA) && speed(b.ship(idA)) === 0, 4000, 'A ghost stops');
  const parked = b.ship(idA);
  check(`A's ship braked to a stop and stays in space at y ${parked.y.toFixed(0)}`, true);

  const a2 = new Client(NAME, tokenA);
  await a2.connect();
  check(`A resumed as the same ship #${a2.welcome.id}`, a2.welcome.resumed === true && a2.welcome.id === idA);
  await b.until(() => b.player(idA)?.online === true, 2000, 'A back online');
  check(`A is online again as '${b.player(idA).name}'`, b.player(idA).name === NAME);
  await a2.until(() => a2.ship(idA), 2000, 'own ship after resume');
  const resumed = a2.ship(idA);
  check(
    `position kept: ${resumed.x.toFixed(1)}, ${resumed.y.toFixed(1)}`,
    // Чужой корабль B видит во float32, свой A получает во float64 (M7): сравниваем с точностью float32.
    Math.hypot(resumed.x - parked.x, resumed.y - parked.y) < 0.01,
  );

  const duplicate = new Client(NAME, tokenA);
  await duplicate.connect();
  await a2.until(() => a2.closeCode !== null, 2000, 'old connection closed');
  check(
    `duplicate session takes the ship over, old connection closed with ${a2.closeCode}`,
    a2.closeCode === CLOSE_REPLACED && duplicate.welcome.resumed && duplicate.welcome.id === idA,
  );

  duplicate.close();
  b.close();
}

main().then(
  () => process.exit(results.every(Boolean) ? 0 : 1),
  (e) => {
    console.log(`FAIL ${e.message}`);
    process.exit(1);
  },
);
