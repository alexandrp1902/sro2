// Сквозная проверка сюжетной кампании (M20a) без браузера: пилот долетает до Новы, находит на доске
// Военной станции раздел «Сюжет» с первой миссией «Тихой войны», берёт её, вылетает — и видит конвой,
// метку цели и реплику Холта. Потом бросает работу и убеждается, что она вернулась на доску, а отношение
// от этого не пострадало: сюжет — не обычная работа, за отказ от истории не штрафуют.
//
// Заодно проверяется поселение «Рудник Прайм» (M20a): оно должно приехать в welcome вместе с системой.
// Нужен запущенный сервер и Node 24 (встроенный WebSocket). Заводит аккаунт smk-story-<число>. Идёт 1–3 минуты.
//   node tools/smoke-story.mjs [ws://localhost:5000/ws]

import { aroundSun, openSocket, stationAt } from './wire.mjs';

const url = process.argv[2] ?? 'ws://localhost:5000/ws';
const INPUT_INTERVAL_MS = 50;
const CLOSE_HIDDEN = 4000;
const RUN = Math.floor(10000 + Math.random() * 90000);

/** Дорога от Sol до Новы: короче неё нет, а длиннее смоуку незачем. */
const ROUTE = ['vega', 'nova'];

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

  await a.undock();
  for (const to of ROUTE) await a.jump(to);
  check(`долетели до Новы: ${a.welcome.system.name}`, a.welcome.system.id === 'nova');

  // Рудник Прайм (M20a) — правка мира: поселение должно приехать вместе с системой.
  const prime = (a.welcome.system.planets ?? []).find((p) => p.id === 'novaPrime');
  check(`Нова-Прайм стала обитаемой: «${prime?.settlement?.name ?? '—'}»`, !!prime?.settlement);

  await a.dock();
  await a.until(() => a.missions !== null, 4000, 'the board of the station');

  // Вторая половина кампании (M20b) приезжает клиенту данными: без этих типов и предметов половина
  // её сцен была бы пустым местом — курьера некому играть, а деталей прототипа не существует.
  const npcs = a.welcome.npcs?.types ?? {};
  for (const type of ['corpGuard', 'corpCourier', 'corpConvoy', 'rebelTug']) {
    check(`тип NPC «${type}» приехал: ${npcs[type]?.name ?? '—'}`, !!npcs[type]);
  }
  check(
    'корабли корпорации рисуются своими корпусами',
    ['corpGuard', 'corpCourier', 'corpConvoy'].every((t) => npcs[t]?.look === 'corp'),
  );
  const items = a.welcome.loot?.items ?? {};
  for (const item of ['blueprint', 'mechFrame', 'driveBlock', 'reactor', 'neuroLink', 'weaponModule']) {
    check(`деталь «${items[item]?.name ?? item}» есть и она сюжетная`, items[item]?.story === true);
  }
  check(`каркас крупный: ${items.mechFrame?.volume ?? 0} мест в трюме`, (items.mechFrame?.volume ?? 0) >= 30);

  const state = a.story;
  check(`доска знает о кампании: «${state?.name ?? '—'}»`, !!state);
  if (!state) return;
  check(`кампания длиннее написанного: ${state.done} из ${state.total}`, state.total >= 14);

  const offer = state.offer;
  check(`первая миссия ждёт на Военной станции: «${offer?.story?.title ?? '—'}»`, !!offer?.story);
  if (!offer) return;
  check(`у неё есть имя выдающего: ${offer.story.giver} · ${offer.story.role}`, !!offer.story.giver);
  check('сюжетная строка приехала и в общем списке доски', a.missions.offers.some((o) => o.id === offer.id));
  check(`её адрес — место, а не система: ${offer.place}`, (offer.place ?? '').startsWith('pl:'));

  const repBefore = a.rep?.places?.['st:nova'] ?? 0;
  a.dialogs.length = 0;
  a.send({ t: 'mission', action: 'accept', id: offer.id });
  await a.until(() => a.missions.active?.offer.story, 4000, 'taking the story mission');
  check(`взяли: «${a.missions.active.offer.story.objective}»`, a.missions.active.offer.story.mission === offer.story.mission);
  await a.until(() => a.dialogs.length > 0, 3000, 'the line said when taking it');
  check(`заказчик сказал своё: «${a.dialogs[0].lines[0]}»`, a.dialogs[0].who === offer.story.giver);

  await a.undock();
  // Живое задание начинается на вылете: конвой выходит вместе с пилотом, и у цели появляется метка.
  await a.until(() => a.missions.mark, 6000, 'the objective mark');
  check(`конвой вышел и у цели есть метка: #${a.missions.mark.ship}`, a.missions.mark.ship !== 0);
  const convoy = a.players?.players.find((p) => p.kind === 'convoy');
  check(`конвой виден в ростере: «${convoy?.name ?? '—'}»`, !!convoy);

  // Отказ от сюжета возвращает его на доску и не бьёт по отношению.
  a.send({ t: 'mission', action: 'abandon' });
  await a.until(() => a.missions.active === null, 4000, 'abandoning the story mission');
  await a.dock();
  await a.until(() => a.story?.offer, 5000, 'the story back on the board');
  check(`брошенная миссия вернулась на доску: «${a.story.offer.story.title}»`, a.story.offer.story.mission === offer.story.mission);
  const repAfter = a.rep?.places?.['st:nova'] ?? 0;
  check(`отказ от сюжета не стоил отношения: ${repBefore} → ${repAfter}`, repAfter >= repBefore);
  // Заглушка ждёт своего часа: до конца кампании «Ретранслятор» не показывается нигде.
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
