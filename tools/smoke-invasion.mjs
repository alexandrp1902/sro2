// Сквозная проверка вторжения пиратов (GDD §38) без браузера. На время проверки shared/invasion.json
// подменяется коротким (анонс 3 с, одна волна из одного пирата, 20 с на всё) — сервер подхватывает его сам, —
// а в конце возвращается как был. Ждёт анонс, первую волну и итог.
// Нужен запущенный сервер (с горячей перезагрузкой shared/) и Node 24.
//   node tools/smoke-invasion.mjs [ws://localhost:5000/ws]

import { readFileSync, writeFileSync } from 'node:fs';
import { openSocket } from './wire.mjs';

const url = process.argv[2] ?? 'ws://localhost:5000/ws';
const file = new URL('../shared/invasion.json', import.meta.url);
const original = readFileSync(file, 'utf8');
const short = {
  intervalMinutes: 0.05,
  firstMinutes: 0,
  announceSeconds: 3,
  durationSeconds: 20,
  waveGapSeconds: 1,
  fund: 300,
  failShare: 0.3,
  minShare: 50,
  pointOffset: 1400,
  waves: [[{ type: 'pirate', level: 1, count: 1 }]],
};

const results = [];

function check(text, pass) {
  results.push(pass);
  console.log(`${pass ? 'OK  ' : 'FAIL'} ${text}`);
}

function connect() {
  return new Promise((resolve, reject) => {
    const { ws, read } = openSocket(url);
    const client = { ws, welcome: null, invasions: [] };
    const name = `Invasion-${Math.floor(100 + Math.random() * 900)}`;
    ws.onopen = () => ws.send(JSON.stringify({ t: 'hello', name, hull: 'light' }));
    ws.onerror = () => reject(new Error(`cannot connect to ${url}`));
    ws.onmessage = (e) => {
      const message = read(e.data);
      if (message.t === 'welcome') {
        client.welcome = message;
        resolve(client);
      } else if (message.t === 'invasion') client.invasions.push(message);
    };
  });
}

async function until(predicate, timeoutMs, what) {
  const started = Date.now();
  while (!predicate()) {
    if (Date.now() - started > timeoutMs) throw new Error(`timeout: ${what}`);
    await new Promise((resolve) => setTimeout(resolve, 100));
  }
}

async function main() {
  const client = await connect();
  writeFileSync(file, JSON.stringify(short, null, 2));
  try {
    // Вторжение могло уже идти по настоящему расписанию: считаем только то, что пришло после подмены файла.
    const from = client.invasions.length;
    const since = () => client.invasions.slice(from);
    await until(() => since().some((m) => m.state === 'announce'), 30000, 'an announce');
    const announce = since().find((m) => m.state === 'announce');
    const after = () => since().slice(since().indexOf(announce));
    // Анонс по настоящему расписанию мог прийти раньше подмены — тогда он просто сократится до 3 с.
    check(`announced in ${announce.systemName}, starts in ${announce.secondsLeft} s`, announce.secondsLeft > 0);

    await until(() => after().some((m) => m.state === 'wave'), 10000, 'the first wave');
    const wave = after().find((m) => m.state === 'wave');
    const at = `${wave.x.toFixed(0)}, ${wave.y.toFixed(0)}`;
    check(`wave ${wave.wave}/${wave.waves}: ${wave.remaining} pirate(s) gather at ${at}`, wave.waves === 1 && wave.remaining === 1);

    await until(() => after().some((m) => m.state === 'won' || m.state === 'lost'), 30000, 'the outcome');
    const end = after().find((m) => m.state === 'won' || m.state === 'lost');
    check(`the invasion ended: ${end.state}, ${end.results?.length ?? 0} participant(s)`, true);
  } finally {
    writeFileSync(file, original);
    client.ws.close();
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
