// Сквозная проверка групп (GDD §37) без браузера: A зовёт B, B принимает, оба видят группу из двух,
// статус приходит сам раз в секунду, A выходит — группа распадается у обоих.
// Нужен запущенный сервер и Node 24 (встроенный WebSocket).
//   node tools/smoke-party.mjs [ws://localhost:5000/ws]

import { PROTOCOL_VERSION, openSocket } from './wire.mjs';

const url = process.argv[2] ?? 'ws://localhost:5000/ws';

class Client {
  constructor(name) {
    this.name = name;
    this.welcome = null;
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
        if (message.t !== 'snapshot') this.messages.push(message);
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

  count(type) {
    return this.messages.filter((m) => m.t === type).length;
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

async function main() {
  const suffix = Math.floor(100 + Math.random() * 900);
  const a = new Client(`PartyA-${suffix}`);
  const b = new Client(`PartyB-${suffix}`);
  await a.connect();
  await b.connect();
  const idA = a.welcome.id;
  const idB = b.welcome.id;
  check(`protocol ${a.welcome.version}`, a.welcome.version === PROTOCOL_VERSION);

  a.send({ t: 'party', action: 'invite', id: idB });
  await b.until(() => b.last('partyInvite'), 2000, 'B gets the invite');
  const invite = b.last('partyInvite');
  check(`B is invited by ${invite.name} for ${invite.seconds} s`, invite.from === idA && invite.seconds > 0);
  await a.until(() => a.last('partyEvent')?.code === 'invited', 2000, 'A is told the invite went');
  check('A is told the invite went', true);

  b.send({ t: 'party', action: 'accept', id: idA });
  await a.until(() => a.last('partyState')?.members.length === 2, 2000, 'A sees a party of two');
  await b.until(() => b.last('partyState')?.members.length === 2, 2000, 'B sees a party of two');
  const state = a.last('partyState');
  const members = state.members.map((m) => `${m.name} in ${m.systemName} ${m.hp}/${m.maxHp}`).join(', ');
  check(`party: leader ${state.leader}, members ${members}`, state.leader === idA && state.members.every((m) => m.maxHp > 0 && m.online));

  const before = a.count('partyState');
  await a.until(() => a.count('partyState') > before, 2500, 'the status comes by itself');
  check('the status comes by itself every second', true);

  a.send({ t: 'party', action: 'leave' });
  await a.until(() => a.last('partyState')?.members.length === 0, 2000, 'A is out');
  await b.until(() => b.last('partyState')?.members.length === 0, 2000, 'B is out too');
  const code = b.last('partyEvent')?.code;
  check(`the party of two is disbanded for both (B got ${code})`, code === 'disbanded');

  a.ws.close();
  b.ws.close();
}

main().then(
  () => process.exit(results.every(Boolean) ? 0 : 1),
  (e) => {
    console.log(`FAIL ${e.message}`);
    process.exit(1);
  },
);
