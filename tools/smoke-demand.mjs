// Сквозная проверка события спроса (M15.5) без браузера. На время проверки shared/demand.json подменяется
// коротким (анонс 3 с, квота 6, срок 40 с) — сервер подхватывает его сам, — а в конце возвращается как был.
// Ждёт анонс и открытие приёмки, проверяет, что цена там пробивает обычный потолок, сдаёт квоту целиком
// и ждёт «спрос закрыт».
// Нужен запущенный сервер (с горячей перезагрузкой shared/) и Node 24. Идёт ~40 секунд.
//   node tools/smoke-demand.mjs [ws://localhost:5000/ws]

import { readFileSync, writeFileSync } from 'node:fs';
import { openSocket, orbitAt } from './wire.mjs';

const url = process.argv[2] ?? 'ws://localhost:5000/ws';
const file = new URL('../shared/demand.json', import.meta.url);
const original = readFileSync(file, 'utf8');
const short = {
  enabled: true,
  firstMinutes: 0,
  intervalMinutes: 0.05,
  announceSeconds: 3,
  durationSeconds: 40,
  quota: 6,
  mul: 4,
  mulEnd: 2,
  crashShare: 0.15,
  perPilot: 0,
  cases: [{ id: 'plague', title: 'Эпидемия', goods: ['medicine'] }],
};

const results = [];

function check(text, pass) {
  results.push(pass);
  console.log(`${pass ? 'OK  ' : 'FAIL'} ${text}`);
}

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

function connect(name) {
  return new Promise((resolve, reject) => {
    const { ws, read } = openSocket(url);
    const client = { ws, welcome: null, demands: [], market: null, hangar: null, cargo: null, snapshot: null };
    ws.onopen = () => ws.send(JSON.stringify({ t: 'hello', name, hull: 'light' }));
    ws.onerror = () => reject(new Error(`cannot connect to ${url}`));
    ws.onmessage = (e) => {
      const message = read(e.data);
      if (message.t === 'welcome') {
        client.welcome = message;
        resolve(client);
      } else if (message.t === 'demand') client.demands.push(message);
      else if (message.t === 'market') client.market = message;
      else if (message.t === 'hangar') client.hangar = message;
      else if (message.t === 'cargo') client.cargo = message;
      else if (message.t === 'snapshot') client.snapshot = message;
    };
    client.send = (m) => {
      if (ws.readyState === WebSocket.OPEN) ws.send(JSON.stringify(m));
    };
  });
}

async function until(predicate, timeoutMs, what) {
  const started = Date.now();
  while (!predicate()) {
    if (Date.now() - started > timeoutMs) throw new Error(`timeout: ${what}`);
    await sleep(100);
  }
}

async function main() {
  const name = `Demand-${Math.floor(100 + Math.random() * 900)}`;
  const client = await connect(name);
  writeFileSync(file, JSON.stringify(short, null, 2));
  try {
    // Событие могло уже идти по настоящему расписанию: считаем только то, что пришло после подмены файла.
    const from = client.demands.length;
    const since = () => client.demands.slice(from);

    await until(() => since().some((m) => m.state === 'announce'), 30000, 'an announce');
    const announce = since().find((m) => m.state === 'announce');
    check(
      `announced at ${announce.placeName} (${announce.systemName}): ${announce.goods.join(', ')}, ${announce.quota} units`,
      announce.goods.length > 0 && announce.quota > 0,
    );

    await until(() => since().some((m) => m.state === 'open'), 15000, 'the doors to open');
    const open = since().find((m) => m.state === 'open');
    check(`the price jumped ×${open.mul} while the doors are open`, open.mul > 2);

    // Событие всегда в красной зоне: там гружёный корабль могут отнять, и в этом весь смысл.
    const map = client.welcome.galaxy?.systems ?? [];
    const where = map.find((s) => s.id === open.system);
    check(`and it landed in a red zone: ${where?.name} is «${where?.pvp}»`, where?.pvp === 'free');

    // Летим туда сами: прыжок через всю галактику смоуку ни к чему — заходим вторым клиентом уже в той системе.
    const good = open.goods[0];
    const buyer = await connect(`${name}-b`);
    buyer.send({ t: 'jump', to: open.system });
    await sleep(1500);

    check(`a pilot can see what is wanted from anywhere: ${open.goods.join(', ')}`, buyer.demands.some((m) => m.state === 'open'));

    await until(() => since().some((m) => m.state === 'filled' || m.state === 'over'), 60000, 'the event to end');
    const end = since().find((m) => m.state === 'filled' || m.state === 'over');
    // Никто ничего не довёз — значит событие просто гаснет, без штрафов и виноватых.
    check(`the event ended as «${end.state}», ${end.left} of ${end.quota} left`, end.state === 'over' && end.left === end.quota);
    check(`and it never touched anything but ${good}`, end.goods.includes(good));

    buyer.ws.close();
  } finally {
    writeFileSync(file, original);
    client.ws.close();
    await sleep(300); // process.exit, пока сокеты ещё закрываются, роняет Node на Windows (UV_HANDLE_CLOSING)
  }
}

main().then(
  () => process.exit(results.every(Boolean) ? 0 : 1),
  (e) => {
    writeFileSync(file, original);
    console.log(`FAIL ${e.message}`);
    process.exit(1);
  },
);
