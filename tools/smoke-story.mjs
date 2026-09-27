// Сквозная проверка сюжетной кампании без браузера (M20a, переделано по плейтесту 2026-09-26): сразу после
// обучения сюжет знает, куда звать, — к вербовщице в Новом Порту на Терре, первой миссии пролога. Пилот садится
// туда, берёт сюжетную миссию и работу с доски разом (у сюжета свой слот), слышит реплику, получает бумаги
// в трюм. Потом бросает сюжет — работа с доски остаётся, сюжет возвращается на доску, а отношение от этого
// не страдает: за отказ от истории не штрафуют.
//
// Заодно проверяются типы NPC и предметы второй половины кампании (M20b): они едут в welcome.
// Нужен запущенный сервер и Node 24 (встроенный WebSocket). Заводит аккаунт smk-story-<число>. Идёт 1–3 минуты.
//   node tools/smoke-story.mjs [ws://localhost:5000/ws]

import { aroundSun, openSocket, orbitAt, settlement, stationAt } from './wire.mjs';

const url = process.argv[2] ?? 'ws://localhost:5000/ws';
const INPUT_INTERVAL_MS = 50;
const CLOSE_HIDDEN = 4000;
const RUN = Math.floor(10000 + Math.random() * 90000);

/** Первая встреча пролога: вербовщица в Новом Порту на Терре. */
const FIRST = { mission: 'recruit', place: 'pl:terra', system: 'sol' };

class Client {
  constructor(hello) {
    this.hello = hello;
    this.welcome = null;
    this.hangar = null;
    this.cargo = null;
    this.missions = null;
    this.rep = null;
    this.dialogs = [];
    this.players = null;
    this.notices = [];
    this.snapshots = [];
    this.seq = 0;
    this.timer = 0;
    this.steer = () => [0, -1, 0];
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

  /** Состояние кампании из последнего сообщения о заданиях. */
  get story() {
    return this.missions?.story ?? null;
  }

  connect() {
    return new Promise((resolve, reject) => {
      const { ws, read } = openSocket(url);
      this.ws = ws;
      this.seq = 0;
      ws.onopen = () => this.send({ t: 'hello', ...this.hello });
      ws.onerror = () => reject(new Error(`cannot connect to ${url}`));
      ws.onclose = () => clearInterval(this.timer);
      ws.onmessage = (e) => {
        const message = read(e.data);
        if (message.t === 'welcome') {
          this.welcome = message;
          this.snapshots = [];
          resolve(message);
        } else if (message.t === 'hangar') {
          if (this.hangar?.docked && !message.docked) this.seq = 0;
          this.hangar = message;
        } else if (message.t === 'cargo') this.cargo = message;
        else if (message.t === 'missions') this.missions = message;
        else if (message.t === 'rep') this.rep = message;
        else if (message.t === 'dialog') this.dialogs.push(message);
        else if (message.t === 'players') this.players = message;
        else if (message.t === 'notice') this.notices.push(message.code);
        else if (message.t === 'denied') reject(new Error(`denied: ${message.code}`));
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
      if (this.hangar?.docked) return;
      const [dx, dy, th] = this.steer();
      const length = Math.hypot(dx, dy) || 1;
      this.send({ t: 'input', seq: ++this.seq, dx: dx / length, dy: dy / length, th });
    }, INPUT_INTERVAL_MS);
  }

  async flyTo(target, within, timeoutMs) {
    const at = () => (typeof target === 'function' ? target() : target);
    this.steer = () => {
      const me = this.me;
      const to = at();
      if (!me || !to) return [0, -1, 0];
      const via = aroundSun(this.welcome.system, me, to);
      const final = via.x === to.x && via.y === to.y;
      return [via.x - me.x, via.y - me.y, !final || Math.hypot(via.x - me.x, via.y - me.y) > within ? 1 : 0];
    };
    await this.until(
      () => {
        const me = this.me;
        const to = at();
        return me && to && Math.hypot(to.x - me.x, to.y - me.y) <= within && Math.hypot(me.vx, me.vy) < 5;
      },
      timeoutMs,
      'reach the target',
    );
    this.steer = () => [0, -1, 0];
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

  async undock() {
    if (!this.hangar?.docked) return;
    this.send({ t: 'dock', on: false });
    await this.until(() => this.hangar && !this.hangar.docked, 4000, 'out of the dock');
    await this.until(() => this.me, 4000, 'own ship after take-off');
  }

  async dock() {
    if (this.hangar?.docked) return;
    await this.until(() => this.snapshot, 4000, 'a snapshot');
    await this.flyTo(() => stationAt(this.welcome.system, this.snapshot.tick), 60, 120000);
    this.send({ t: 'dock', on: true });
    await this.until(() => this.hangar.docked, 4000, 'docked');
  }

  /** Сесть в поселение по ключу места: долететь до планеты на её орбите и попросить посадку. */
  async land(key) {
    if (this.hangar?.docked) return;
    await this.until(() => this.snapshot, 4000, 'a snapshot');
    const system = this.welcome.system;
    const planet = settlement(system, key);
    if (!planet) throw new Error(`no settlement ${key} in ${system.id}`);
    const range = this.welcome.loot.stationRange + planet.size;
    await this.flyTo(() => orbitAt(system, planet.orbit, this.snapshot.tick), range - 60, 120000);
    this.send({ t: 'dock', on: true, place: key });
    await this.until(() => this.hangar.docked, 4000, 'landed');
  }

  async jump(to) {
    const gate = this.welcome.system.gates.find((g) => g.to === to);
    await this.flyTo(gate, 120, 120000);
    this.send({ t: 'jump', to });
    await this.until(() => this.welcome.system.id === to, 10000, `jump to ${to}`);
    await this.until(() => this.me, 4000, 'own ship after the jump');
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

async function main() {
  const a = new Client({ name: `smk-story-${RUN}`, password: 'smoke-password', create: true });
  await a.connect();
  a.start();

  // Обучение смоуку мешает: оно держит трекер и собственные шаги, а проверяем мы сюжет.
  a.send({ t: 'mission', action: 'skip' });
  await a.until(() => a.missions && !a.missions.tutorial, 4000, 'tutorial skipped');

  // Сюжет зовёт сразу, где бы пилот ни стоял: кто, где и в какой системе даёт первую миссию.
  const next = a.story?.next;
  check(`сюжет зовёт сразу: ${next?.giver ?? '—'} — ${next?.place ?? '—'} (${next?.system ?? '—'})`,
    next?.mission === FIRST.mission && next.place === FIRST.place && next.system === FIRST.system);
  check(`пролог — часть кампании: ${a.story?.total ?? 0} миссий`, (a.story?.total ?? 0) >= 19);

  // Вторая половина кампании (M20b) приезжает клиенту данными: без этих типов и предметов половина
  // её сцен была бы пустым местом — курьера некому играть, а деталей прототипа не существует.
  const npcs = a.welcome.npcs?.types ?? {};
  for (const type of ['corpGuard', 'corpCourier', 'corpConvoy', 'rebelTug']) {
    check(`тип NPC «${type}» приехал: ${npcs[type]?.name ?? '—'}`, !!npcs[type]);
  }
  const items = a.welcome.loot?.items ?? {};
  for (const item of ['contracts', 'mailCapsule', 'passengerList', 'blueprint', 'mechFrame', 'reactor']) {
    check(`предмет «${items[item]?.name ?? item}» есть и он сюжетный`, items[item]?.story === true);
  }

  await a.undock();
  await a.land(FIRST.place);
  await a.until(() => a.story?.offer, 4000, 'the story offer at the first meeting');
  const offer = a.story.offer;
  check(`первая миссия ждёт на месте: «${offer.story.title}» от ${offer.story.giver}`, offer.story.mission === FIRST.mission);
  const board = a.missions.offers.find((o) => !o.story);
  check(`рядом и работа с доски: ${board?.kind ?? '—'}`, !!board);

  const repBefore = a.rep?.places?.[FIRST.place] ?? 0;
  a.dialogs.length = 0;
  a.send({ t: 'mission', action: 'accept', id: offer.id });
  await a.until(() => a.missions.storyActive?.offer.story, 4000, 'taking the story mission');
  check(`взяли сюжет: «${a.missions.storyActive.offer.story.objective}»`, a.missions.storyActive.offer.id === offer.id);
  await a.until(() => a.dialogs.length > 0, 3000, 'the line said when taking it');
  check(`заказчица сказала своё: «${a.dialogs[0].lines[0]}»`, a.dialogs[0].who === offer.story.giver);
  await a.until(() => (a.cargo?.items?.contracts ?? 0) > 0, 3000, 'the papers in the hold');
  check('контракты легли в трюм', a.cargo.items.contracts === 1);
  check('взятая миссия ведёт сама — указателя нет', !a.story?.next);

  if (board) {
    a.send({ t: 'mission', action: 'accept', id: board.id });
    await a.until(() => a.missions.active, 4000, 'taking the board work next to the story');
    check(`и работа с доски взята рядом с сюжетом: ${a.missions.active.offer.kind}`, a.missions.active.offer.id === board.id && !!a.missions.storyActive);
  }

  // Отказ от сюжета: работа с доски остаётся, сюжет возвращается на доску, отношение не страдает.
  a.send({ t: 'mission', action: 'abandon', id: offer.id });
  await a.until(() => !a.missions.storyActive, 4000, 'abandoning the story mission');
  if (board) check('работа с доски пережила отказ от сюжета', a.missions.active?.offer.id === board.id);
  await a.until(() => a.story?.offer, 5000, 'the story back on the board');
  check(`брошенная миссия вернулась на доску: «${a.story.offer.story.title}»`, a.story.offer.story.mission === FIRST.mission);
  const repAfter = a.rep?.places?.[FIRST.place] ?? 0;
  check(`отказ от сюжета не стоил отношения: ${repBefore} → ${repAfter}`, repAfter >= repBefore);
  check('до финала кампании ретранслятора нет', !a.story.relay);

  a.close();
}

main()
  .then(() => {
    const failed = results.filter((ok) => !ok).length;
    console.log(failed === 0 ? `\nall ${results.length} checks passed` : `\n${failed} of ${results.length} checks failed`);
    process.exit(failed === 0 ? 0 : 1);
  })
  .catch((error) => {
    console.error(`\n${error.message}`);
    process.exit(1);
  });
