// Сквозная проверка наземного боя мехов (M21) без браузера: пилот долетает до Нова-Прайм, садится на Рудник
// Прайм, где кончилась «Тихая война», и через ретранслятор начинает «Первую вылазку». Проверяется, что из дока
// посреди боя не улететь, что мусорная команда отвергается кодом, что после обрыва связи бой ждёт и продолжается
// с того же места, и что бой доигрывается до конца — а награда платится ровно та, что объявлена.
//
// Сервер нужно поднять с проставленной кампанией, иначе ретранслятора не будет:
//   $env:SRO_STORY_SKIP='quietWar'; dotnet run --project server/Sro.Server
// Нужен Node 24 (встроенный WebSocket). Заводит аккаунт smk-mech-<число>. Идёт 1–3 минуты.
//   node tools/smoke-mech.mjs [ws://localhost:5000/ws]

import { aroundSun, openSocket, orbitAt } from './wire.mjs';

const url = process.argv[2] ?? 'ws://localhost:5000/ws';
const INPUT_INTERVAL_MS = 50;
const CLOSE_HIDDEN = 4000;
const RUN = Math.floor(10000 + Math.random() * 90000);
const PASSWORD = 'smoke-password';
const ROUTE = ['vega', 'nova'];
const PRIME = 'pl:novaPrime';
/** Больше ходов бою не нужно: план обещает бой до пяти минут, это раундов десять-пятнадцать. */
const MAX_TURNS = 80;

class Client {
  constructor(hello) {
    this.hello = hello;
    this.welcome = null;
    this.hangar = null;
    this.cargo = null;
    this.missions = null;
    this.dialogs = [];
    this.notices = [];
    this.snapshots = [];
    this.mech = null;
    this.rules = null;
    this.mechStates = 0;
    this.refusals = [];
    this.end = null;
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
        switch (message.t) {
          case 'welcome':
            this.welcome = message;
            this.snapshots = [];
            resolve(message);
            break;
          case 'hangar':
            if (this.hangar?.docked && !message.docked) this.seq = 0;
            this.hangar = message;
            break;
          case 'cargo': this.cargo = message; break;
          case 'missions': this.missions = message; break;
          case 'dialog': this.dialogs.push(message); break;
          case 'notice': this.notices.push(message.code); break;
          case 'denied': reject(new Error(`denied: ${message.code}`)); break;
          case 'mechState':
            this.mech = message.battle;
            if (message.rules) this.rules = message.rules;
            this.mechStates++;
            break;
          case 'mechRefused': this.refusals.push(message.code); break;
          case 'mechEnd': this.end = message; break;
          case 'snapshot':
            this.snapshots.push(message);
            if (this.snapshots.length > 2000) this.snapshots.splice(0, 1000);
            break;
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

  /** Прыжок; пират по дороге может сбить зарядку (M15.7) — тогда ещё раз, до трёх попыток. */
  async jump(to) {
    const gate = this.welcome.system.gates.find((g) => g.to === to);
    for (let attempt = 1; ; attempt++) {
      await this.flyTo(gate, 120, 120000);
      this.notices.length = 0;
      this.send({ t: 'jump', to });
      try {
        await this.until(() => this.welcome.system.id === to, 12000, `jump to ${to}`);
        break;
      } catch (error) {
        if (attempt >= 3) throw new Error(`${error.message} (notices: ${this.notices.join(', ') || 'none'})`);
      }
    }
    await this.until(() => this.me, 4000, 'own ship after the jump');
  }

  /** Сесть на поселение планеты: подлететь на дальность стыковки и попросить именно это место. */
  async land(key) {
    const system = this.welcome.system;
    const planet = (system.planets ?? []).find((p) => `pl:${p.id}` === key);
    if (!planet) throw new Error(`no settlement ${key} in ${system.id}`);
    const range = this.welcome.loot.stationRange + planet.size;
    await this.until(() => this.snapshot, 4000, 'a snapshot');
    await this.flyTo(() => orbitAt(system, planet.orbit, this.snapshot.tick), range - 60, 120000);
    this.send({ t: 'dock', on: true, place: key });
    await this.until(() => this.hangar?.docked, 5000, `landing on ${key}`);
  }

  /** Действие боя и ответ на него: новое состояние, итог или отказ. */
  async act(message) {
    const states = this.mechStates;
    const refusals = this.refusals.length;
    this.send({ t: 'mechAct', ...message });
    await this.until(() => this.mechStates > states || this.refusals.length > refusals || this.end, 6000, `answer to ${message.act}`);
    return this.refusals.length > refusals ? this.refusals[this.refusals.length - 1] : null;
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

const cheb = (a, b) => Math.max(Math.abs(a.x - b.x), Math.abs(a.y - b.y));

/**
 * Один ход бота: подойти к ближайшему налётчику на оптимум автопушки и выстрелить; не вышло — по второму;
 * не вышло и так — завершить ход. Бот нарочно простой: смоук проверяет сервер, а не тактику.
 */
async function playTurn(c) {
  const s = c.mech;
  const me = s.units.find((u) => u.id === s.current);
  const enemies = s.units.filter((u) => u.side === 'enemy' && u.hp[0] > 0).sort((a, b) => cheb(me, a) - cheb(me, b));
  if (!me || enemies.length === 0) return;
  const foe = enemies[0];
  const d = cheb(me, foe);
  if (!s.moved && (d > 5 || d < 3)) {
    const sx = Math.sign(foe.x - me.x) * (d > 5 ? 1 : -1);
    const sy = Math.sign(foe.y - me.y) * (d > 5 ? 1 : -1);
    for (let k = s.moveRange; k >= 1; k--) {
      const x = me.x + sx * k;
      const y = me.y + sy * k;
      if (x < 0 || y < 0 || x >= s.map[0].length || y >= s.map.length) continue;
      if ((await c.act({ act: 'move', x, y })) === null) break;
    }
    if (c.end || c.mech.turn !== 'player') return;
  }
  for (const target of enemies) {
    if ((await c.act({ act: 'attack', target: target.id })) === null) return;
  }
  await c.act({ act: 'end' });
}

async function main() {
  const name = `smk-mech-${RUN}`;
  const a = new Client({ name, password: PASSWORD, create: true });
  await a.connect();
  a.start();
  a.send({ t: 'mission', action: 'skip' });
  await a.until(() => a.missions && !a.missions.tutorial, 4000, 'tutorial skipped');

  await a.undock();
  for (const to of ROUTE) await a.jump(to);
  await a.land(PRIME);
  await a.until(() => a.story, 4000, 'the campaign journal');
  check(`кампания пройдена (SRO_STORY_SKIP): ${a.story?.done} из ${a.story?.total}`, a.story?.done === a.story?.total);
  check('на Руднике Прайм открыт ретранслятор', a.story?.relay === true);
  if (!a.story?.relay) return;
  const creditsBefore = a.cargo?.credits ?? 0;

  // ------------------------------------------------------------------ старт
  a.dialogs.length = 0;
  await a.act({ act: 'start' });
  check(`бой начался: раунд ${a.mech?.round}, мехов ${a.mech?.units.length}`, a.mech?.units.length === 3);
  check('каталог мехов приехал со стартом', !!a.rules?.missions?.firstSortie);
  check(`первый ход — игрока: ${a.mech?.current}`, a.mech?.turn === 'player' && a.mech?.current === 'p1');
  await a.until(() => a.dialogs.length > 0, 3000, 'Eva before the battle');
  check(`Ева на связи: «${a.dialogs[0]?.lines[0] ?? '—'}»`, a.dialogs[0]?.who === 'Ева Морен');

  a.notices.length = 0;
  a.send({ t: 'dock', on: false });
  await a.until(() => a.notices.includes('mechBusy'), 3000, 'undock refused during the battle');
  check('посреди боя из дока не улететь', a.hangar?.docked === true);
  check(`мусорная команда отвергнута: ${await a.act({ act: 'teleport' })}`, a.refusals.at(-1) === 'badAct');

  // ------------------------------------------------------------------ обрыв связи
  await playTurn(a);
  const round = a.mech.round;
  const at = a.mech.units.find((u) => u.id === 'p1');
  a.close();
  const b = new Client({ name, password: PASSWORD });
  await b.connect();
  await b.until(() => b.mech, 5000, 'the battle after reconnecting');
  const back = b.mech.units.find((u) => u.id === 'p1');
  check(`после обрыва связи бой тот же: раунд ${b.mech.round}, мех на (${back.x}, ${back.y})`,
    b.mech.round === round && back.x === at.x && back.y === at.y);
  check('с возвращением снова приехал каталог', !!b.rules);

  // ------------------------------------------------------------------ до конца
  for (let turn = 0; turn < MAX_TURNS && !b.end; turn++) {
    if (b.mech.turn !== 'player') await b.until(() => b.mech.turn === 'player' || b.end, 6000, 'our turn');
    if (b.end) break;
    await playTurn(b);
  }
  check(`бой окончен за ${b.mech?.round ?? '?'} раундов: ${b.end ? (b.end.won ? 'победа' : 'поражение') : 'не окончен'}`, !!b.end);
  if (!b.end) return;
  await b.until(() => b.cargo, 3000, 'cargo');
  const gained = (b.cargo?.credits ?? 0) - creditsBefore;
  const reward = a.rules.missions.firstSortie.reward ?? 0;
  if (b.end.won) {
    check(`первая победа оплачена: +${gained} кр, объявлено ${b.end.reward}`, b.end.first && b.end.reward === reward && gained === reward);
  } else {
    check(`поражение не платит: +${gained} кр`, b.end.reward === 0 && gained === 0);
  }
  await b.until(() => b.story?.sortieWon === b.end.won, 3000, 'the journal after the battle');
  check(`журнал знает итог: sortieWon = ${b.story?.sortieWon}`, b.story?.sortieWon === b.end.won);
  b.notices.length = 0;
  b.send({ t: 'dock', on: false });
  await b.until(() => b.hangar && !b.hangar.docked, 4000, 'undocking after the battle');
  check('после боя вылет снова открыт', b.hangar.docked === false);
  b.close();
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
