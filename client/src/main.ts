import './style.css';
import { Application, Container } from 'pixi.js';
import { SPAWN, STATION } from './game/layout';
import { FixedLoop } from './game/loop';
import { LOOT_MOUSE_RADIUS_PX, cycle, nearest, nearestLoot, pickArrow, pickAt } from './game/targeting';
import { Controls } from './input/controls';
import { FireControl, bindCombatKeys } from './input/fire';
import { preventBrowserGestures } from './input/gestures';
import { KeyboardControls, bindKeyboard } from './input/keyboard';
import { Stick } from './input/stick';
import { TapSelect } from './input/tapSelect';
import { Zoom } from './input/zoom';
import { CombatEvents, type CombatEvent } from './net/combatEvents';
import { Connection } from './net/connection';
import { Prediction } from './net/prediction';
import type { ShipDto, SnapshotMsg } from './net/protocol';
import { RemoteShips } from './net/remoteShips';
import { Roster } from './net/roster';
import { resolveServerUrl } from './net/serverUrl';
import { Camera } from './render/camera';
import { CombatFx, type FxAnchor } from './render/combatFx';
import { LootField } from './render/lootView';
import { MeteorField } from './render/meteorView';
import { Nebula } from './render/nebulaView';
import { PlayerOverlay } from './render/playerOverlay';
import { ShipView, engineGlow } from './render/ship';
import { Starfield } from './render/starfield';
import { WeaponArc } from './render/weaponArc';
import { createWorldView } from './render/world';
import { Zones } from './render/zones';
import { DEFAULT_SECTOR_UNIT, assess, cooldownTicks, evasion } from './sim/combat';
import { DEFAULT_HULL, Hulls } from './sim/hulls';
import { NO_LOOT, lootLabel, rarityColor, type LootRules } from './sim/loot';
import { DT, directionAngle, localVelocity, type MoveInput } from './sim/movement';
import { DEFAULT_WEAPON, Weapons } from './sim/weapons';
import { CargoHud, type CargoState } from './ui/cargoHud';
import { CombatHud } from './ui/combatHud';
import { DevOverlay } from './ui/devOverlay';
import { DockScreen } from './ui/dockScreen';
import { Feed, describeKill, describeNotice } from './ui/feed';
import { FlightHud } from './ui/flightHud';
import { PilotForm, describeDenied } from './ui/pilotForm';
import { StatusHud } from './ui/statusHud';
import { account } from './util/account';

/** Id станции в прицеле: отрицательный, чтобы не совпасть с id кораблей и добычи от сервера. */
const STATION_ID = -1;
const OWN_COLOR = 0x7fd4ff;
const SKY_SEED = 0;
/** Клавиша «взять ближайший предмет» ищет его в этом радиусе — примерно экран на среднем зуме. */
const LOOT_KEY_RANGE = 1200;
/** Ниже этой скорости «корабль тормозит» в статусе не показываем. */
const STOPPED_SPEED = 1;

async function main(): Promise<void> {
  preventBrowserGestures();

  const app = new Application();
  await app.init({
    resizeTo: window,
    background: '#05060a',
    antialias: true,
    resolution: Math.min(window.devicePixelRatio || 1, 2),
    autoDensity: true,
  });
  document.getElementById('game')!.appendChild(app.canvas);
  const el = (id: string) => document.getElementById(id)!;

  const hulls = new Hulls();
  const weapons = new Weapons();
  const controls = new Controls();
  const keyboard = new KeyboardControls(controls);
  bindKeyboard(keyboard);
  const stick = new Stick(el('stick'), controls);
  const zoom = new Zoom(app.canvas);

  // Корпус и пушку сообщает сервер из аккаунта (hangar); до этого летим на стартовых.
  const prediction = new Prediction(hulls, DEFAULT_HULL, SPAWN);
  let weaponId = DEFAULT_WEAPON;
  /** Каталог лута с сервера: названия, редкость и радиус захвата. */
  let lootRules: LootRules = NO_LOOT;
  /** Единица дистанции для игрока: «цель в 1.4 сектора» вместо «в 980». */
  let sectorUnit = DEFAULT_SECTOR_UNIT;

  const serverUrl = resolveServerUrl();
  const connection = serverUrl ? new Connection(serverUrl) : null;
  const roster = connection?.roster ?? new Roster();

  // Сид неба: одна система — одно небо. В M7 у каждой системы будет своё.
  const starfield = new Starfield(SKY_SEED);
  const nebula = new Nebula(SKY_SEED);
  const world = new Container();
  const remote = new RemoteShips(hulls, roster);
  const ownShip = new ShipView(OWN_COLOR);
  const weaponArc = new WeaponArc();
  const fx = new CombatFx(weapons);
  const overlay = new PlayerOverlay();
  const zones = new Zones();
  const loot = new LootField();
  const meteors = new MeteorField();
  world.addChild(
    nebula.view,
    createWorldView(),
    zones.view,
    loot.view,
    meteors.view,
    weaponArc.view,
    remote.view,
    ownShip.view,
    fx.view,
  );
  app.stage.addChild(starfield.view, world, overlay.view);
  const camera = new Camera();

  const feed = new Feed(el('feed'));
  // Вход (GDD §61): ник и пароль один раз, дальше — ключ устройства. Выход забывает ключ.
  const pilotForm = new PilotForm(el('connect'), {
    onLogin: (name, password) => {
      account.setName(name);
      connection?.login({ name, password });
    },
    onLogout: () => {
      if (serverUrl) account.forget(serverUrl);
      connection?.logout();
    },
  });
  const showPilotForm = (error = '') =>
    pilotForm.show(
      { url: serverUrl, name: connection?.accountName || account.name(), loggedIn: connection?.hasCredentials ?? false },
      error,
    );
  // Тап по статусу: корабль забрало другое устройство — вернуть его сюда; иначе — окно «Пилот».
  const status = new StatusHud(el('status'), () => {
    if (connection?.replaced) connection.resume();
    else showPilotForm();
  });

  const isOnline = () => connection?.state === 'online';
  const ownId = () => (isOnline() ? connection!.playerId : -1);

  // Цель (GDD §9) выбирает клиент, сервер помнит последнюю присланную.
  let targetId = 0;
  const fire = new FireControl(el('fire'), el('combat-pad'));
  const setTarget = (id: number) => {
    if (id === targetId) return;
    targetId = id;
    if (isOnline()) connection!.send({ t: 'target', id });
    if (id !== 0) {
      // прицел один: навёлся на корабль — отпустил груз и станцию
      setLoot(0);
      setStation(false);
    }
    if (id === 0) fire.release(); // без цели огонь выключается: кнопка не горит впустую
  };
  const combatHud = new CombatHud(el('ship'), el('target'), el('death'), () => setTarget(0));

  // Прицел один на всё: он на противнике, на грузе или на станции. Наводка на предмет снимает цель и гасит огонь.
  // Тап по предмету только помечает его; подлетать игрок должен сам, автопилота в MVP нет (боевой документ §45).
  let selectedLootId = 0;
  const setLoot = (id: number) => {
    if (id === selectedLootId) return;
    selectedLootId = id;
    if (isOnline()) connection!.send({ t: 'loot', id });
    if (id !== 0) {
      // прицел один: навёлся на груз — отпустил противника и станцию
      setTarget(0);
      setStation(false);
    }
  };

  // Станция тоже выбирается прицелом (тап, клик, Q/E), но стрелять в неё нельзя: прицел на ней — чтобы
  // пристыковаться. Выбор живёт только на клиенте: сервер про него не знает, id станции ни с чем не совпадёт.
  let stationSelected = false;
  const stationMark = { id: STATION_ID, x: STATION.x, y: STATION.y, size: STATION.radius };
  function setStation(on: boolean): void {
    if (on === stationSelected) return;
    stationSelected = on;
    if (on) {
      setTarget(0);
      setLoot(0);
    }
  }
  const stationDistance = () => Math.hypot(prediction.curr.x - STATION.x, prediction.curr.y - STATION.y);
  const cargoHud = new CargoHud(el('cargo'), el('loot'), () => {
    setLoot(0);
    setStation(false);
  });

  // Док станции (GDD §26): корабль уходит из космоса, поверх мира — торговля, магазин и ангар.
  // Пока пристыкованы, полёт стоит: входы не шлём, при вылете их нумерация начинается заново.
  let docked = false;
  const send = (message: Parameters<Connection['send']>[0]) => {
    if (isOnline()) connection!.send(message);
  };
  const dockScreen = new DockScreen(el('dock'), hulls, weapons, {
    onSell: (item) => send({ t: 'sell', item }),
    onBuy: (kind, id) => send({ t: 'buy', kind, id }),
    onEquip: (kind, id) => send(kind === 'hull' ? { t: 'hull', id } : { t: 'weapon', id }),
    onRepair: () => send({ t: 'repair' }),
    onUndock: () => send({ t: 'dock', on: false }),
  });

  /** Всё, во что можно целиться: корабли и метеориты. Id у них из одного счётчика сервера. */
  const targets = () => [...remote.visible(), ...meteors.visible()];
  // Огонь без цели берёт ближайший корабль или камень: сначала в секторе, потом просто в дальности.
  // Никого — огонь не включается.
  fire.onPress = () => {
    if (docked) return false;
    const current = remote.get(targetId) ?? meteors.get(targetId);
    if (current && !current.dead) return true;
    const id = nearest(prediction.curr, targets(), weapons.get(weaponId));
    if (id !== null) {
      setTarget(id);
      return true;
    }
    if (isOnline()) feed.add('Нет цели в радиусе огня');
    return false;
  };
  fire.onChange = (on) => {
    if (isOnline()) connection!.send({ t: 'fire', on });
  };
  /**
   * Предыдущий / следующий объект по удалённости: Q/E, Shift+←/→, Tab на ПК, кнопки < > у кнопки огня на телефоне.
   * Перебираются корабли, добыча и станция вперемешку: id кораблей и добычи из одного счётчика сервера,
   * у станции свой, отрицательный. Прицел один: шаг на что-то одно снимает остальное.
   */
  const stepSelection = (step: -1 | 1) => {
    const candidates = [...targets(), ...loot.visible(), stationMark];
    const from = stationSelected ? STATION_ID : selectedLootId !== 0 ? selectedLootId : targetId;
    // Кольцо спирали — сектор: сначала обходим всё вокруг себя, потом уходим на виток дальше.
    const id = cycle(prediction.curr, candidates, from, step, sectorUnit);
    if (id === null) return;
    if (id === STATION_ID) setStation(true);
    else if (loot.get(id)) setLoot(id);
    else setTarget(id);
  };
  for (const [button, step] of [
    ['target-prev', -1],
    ['target-next', 1],
  ] as const) {
    el(button).addEventListener('pointerdown', (e) => {
      e.preventDefault(); // без фокуса: иначе Space «нажимал» бы кнопку
      stepSelection(step);
    });
  }
  // Пробел и кнопка огня — по тому, на чём прицел: груз берут, к станции стыкуются (в доке — вылет),
  // по противнику стреляют.
  const grabSelected = (): boolean => {
    if (docked) {
      send({ t: 'dock', on: false });
      return true;
    }
    if (stationSelected) {
      if (stationDistance() <= lootRules.stationRange) send({ t: 'dock', on: true });
      else feed.add('Подлетите ближе к станции');
      return true;
    }
    if (selectedLootId === 0) return false;
    if (isOnline()) connection!.send({ t: 'grab' });
    return true;
  };
  fire.onGrab = grabSelected;
  // Esc снимает сначала предмет, потом цель: отменяем самое недавнее и наименее важное.
  bindCombatKeys(fire, {
    step: stepSelection,
    clear: () => {
      if (selectedLootId !== 0) setLoot(0);
      else if (stationSelected) setStation(false);
      else setTarget(0);
    },
    grab: grabSelected,
  });
  // F — ближайший предмет: на ПК иначе до мелкого обломка не дотянуться мышью в бою.
  window.addEventListener('keydown', (e) => {
    if (e.code !== 'KeyF' || e.repeat || e.ctrlKey || e.metaKey || e.altKey) return;
    const id = nearestLoot(prediction.curr, loot.visible(), LOOT_KEY_RANGE);
    if (id !== null) setLoot(id);
  });
  // Тап мимо кораблей цель не сбрасывает: промах пальцем в бою не должен её терять.
  // Корабль за краем экрана выбирается тапом по его стрелке или подписи у края.
  // Двойной тап по цели — огонь по ней: только что выбранной — включить, уже выбранной — переключить.
  let tapChangedTarget = false;
  new TapSelect(app.canvas, (x, y, touch, double) => {
    const view = { x: camera.x, y: camera.y, zoom: camera.zoom, width: app.screen.width, height: app.screen.height };
    // Корабль выигрывает у предмета: промах пальцем в бою не должен вместо цели выбрать мусор.
    const shipId = pickAt(x, y, remote.visible(), view, touch) ?? pickAt(x, y, meteors.visible(), view, touch);
    if (shipId === null) {
      // Обломок мелкий: мышью по нему целятся с запасом, иначе подобрать на ПК мучительно.
      const lootId = pickAt(x, y, loot.visible(), view, touch, LOOT_MOUSE_RADIUS_PX);
      if (lootId !== null) {
        setLoot(lootId); // двойной тап по предмету ничего не добавляет: автопилота нет
        tapChangedTarget = false;
        return;
      }
      // Станция — последней: она большая и не должна перехватывать тап по тому, что рядом с ней.
      if (pickAt(x, y, [stationMark], view, touch) !== null) {
        setStation(true);
        tapChangedTarget = false;
        return;
      }
    }
    const id = shipId ?? pickArrow(x, y, overlay.edgeArrows(), touch);
    if (id === null) {
      tapChangedTarget = false;
      return;
    }
    if (double && id === targetId) {
      if (tapChangedTarget) fire.set(true);
      else fire.toggle();
      tapChangedTarget = false;
      return;
    }
    tapChangedTarget = id !== targetId;
    setTarget(id);
  });

  const dev = new DevOverlay(el('dev'), connection?.lag ?? null);
  const flight = new FlightHud(el('flight'), () => dev.toggle());

  // Бой: события из снапшотов, эффекты и статистика своих выстрелов для dev-панели.
  const combat = new CombatEvents();
  const fireStats = { shots: 0, hits: 0, chanceSum: 0 };
  let ownDto: ShipDto | null = null;
  let killedBy = '';
  let ownAnchor: FxAnchor = { x: SPAWN.x, y: SPAWN.y, size: hulls.get(prediction.hullId).size };
  const locate = (id: number): FxAnchor | null => {
    if (id === ownId()) return ownAnchor;
    const ship = remote.get(id);
    if (ship) return { x: ship.x, y: ship.y, size: ship.size };
    return meteors.lastSeen(id, performance.now());
  };
  // Метеоритов нет в ростере — их имя знает поле метеоритов, в том числе у только что разбитого.
  const nameOf = (id: number) => roster.get(id)?.name ?? meteors.nameOf(id) ?? '?';
  const isMeteor = (id: number) => id !== 0 && !roster.get(id) && meteors.nameOf(id) !== null;
  const play = (event: CombatEvent, now: number) => {
    if (event.kind === 'kill') {
      const at = locate(event.kill.id);
      if (at) fx.explosion(at.x, at.y, at.size, now);
      return;
    }
    if (event.kind === 'pick') {
      // Предмета в снапшоте уже нет — берём его последнюю позицию, как трассер берёт позицию корабля.
      const at = locate(event.pick.by);
      const from = loot.lastSeen(event.pick.id, now);
      if (!at || !from) return;
      const mine = event.pick.by === ownId();
      const label = mine ? `+${lootLabel(lootRules, event.pick.i, event.pick.n)}` : '';
      fx.tractor(event.pick.by, at, from.x, from.y, rarityColor(lootRules, event.pick.i), label, now);
      return;
    }
    const shot = event.shot;
    fx.shot(shot, now, locate);
    if (shot.from === ownId()) fire.reloadFrom(now, cooldownTicks(weapons.get(shot.w)) * DT * 1000);
  };

  // За кадр сверяемся только с самым свежим снапшотом; чужим кораблям нужен весь поток.
  let latestSnapshot: SnapshotMsg | null = null;
  if (connection) {
    connection.onWelcome = (message) => {
      hulls.set(message.hulls);
      weapons.set(message.weapons);
      zones.set(message.npcs, message.loot);
      sectorUnit = message.combat.sectorUnit || DEFAULT_SECTOR_UNIT;
      lootRules = message.loot ?? NO_LOOT;
      loot.setRules(message.loot);
      cargoHud.setRules(message.loot);
      dockScreen.setRules(message.loot, message.shop);
      loot.clear();
      meteors.setRules(message.meteors);
      meteors.clear();
      selectedLootId = 0; // предметы в космосе за это время сменились — выбор не переносим
      prediction.resetNet();
      remote.clear();
      combat.clear();
      ownDto = null;
      if (message.resumed) feed.add('Снова на связи — корабль ждал на месте');
      // Сервер после переподключения не помнит, во что мы целились и держим ли атаку.
      if (targetId !== 0) connection.send({ t: 'target', id: targetId });
      if (fire.active) connection.send({ t: 'fire', on: true });
    };
    connection.onConfig = (message) => {
      hulls.set(message.hulls);
      weapons.set(message.weapons);
      zones.set(message.npcs, message.loot);
      sectorUnit = message.combat.sectorUnit || DEFAULT_SECTOR_UNIT;
      lootRules = message.loot ?? NO_LOOT;
      loot.setRules(message.loot);
      cargoHud.setRules(message.loot);
      dockScreen.setRules(message.loot, message.shop);
      meteors.setRules(message.meteors);
    };
    connection.onCargo = (message) => {
      const cargo: CargoState = { used: message.used, max: message.max, items: message.items, credits: message.credits ?? 0 };
      cargoHud.setCargo(cargo);
      dockScreen.setCargo(cargo);
    };
    connection.onHangar = (message) => {
      const was = docked;
      docked = message.docked;
      prediction.hullId = message.hull;
      weaponId = message.weapon;
      dockScreen.setHangar(message);
      if (docked && !was) {
        // В доке не целятся и не стреляют; после вылета корабль не рванёт с места сам.
        setTarget(0);
        setLoot(0);
        setStation(false); // после вылета пробел снова стреляет, а не стыкует
        fire.release();
        stick.reset();
        controls.setThrottle(0);
      }
      // Вылет: сервер начал буфер входов заново — и мы нумеруем их с 1, первый снапшот принимаем как есть.
      if (!docked && was) prediction.resetNet();
    };
    connection.onAccount = (message) => {
      if (message.key && serverUrl) account.setKey(serverUrl, message.key);
      account.setName(message.name);
      pilotForm.hide();
    };
    connection.onDenied = (code) => {
      if (code === 'badKey' && serverUrl) account.forget(serverUrl);
      showPilotForm(describeDenied(code));
    };
    connection.onNotice = (message) => {
      const text = describeNotice(message.code);
      if (text) feed.add(text);
    };
    connection.onSnapshot = (message) => {
      const now = performance.now();
      const own = connection.playerId;
      latestSnapshot = message;
      remote.push(message, now);
      loot.push(message, now);
      meteors.push(message, now);
      for (const event of combat.push(message, own)) play(event, now);
      for (const shot of message.shots ?? []) {
        if (shot.from !== own) continue;
        fireStats.shots++;
        if (shot.hit) fireStats.hits++;
        fireStats.chanceSum += shot.ch;
      }
      for (const kill of message.kills ?? []) {
        // Камни бьются десятками в минуту: в ленту — только если участвуем сами, иначе они выбьют из неё всё.
        // by = 0 — камень разбился о корабль; об этом говорит сам таран.
        const withMeteor = isMeteor(kill.id) || isMeteor(kill.by) || kill.by === 0;
        const mine = kill.id === own || kill.by === own;
        if (!withMeteor || (mine && kill.by !== 0)) feed.add(describeKill(nameOf(kill.by), nameOf(kill.id)));
        if (kill.id === own) killedBy = nameOf(kill.by);
      }
    };
    connection.onRosterEvents = (events) => feed.push(events);
    const key = account.key(connection.url);
    if (key) connection.login({ key });
    else showPilotForm();
  } else {
    showPilotForm();
  }

  // Связи нет, а сервер задан: корабль тормозит, как его копия на сервере, — после возврата не будет рывка.
  const isBraking = () => connection !== null && !isOnline();
  const flightInput = (): MoveInput => {
    const input = controls.input();
    if (isBraking()) input.throttle = 0;
    return input;
  };

  const loop = new FixedLoop(() => {
    if (docked) return; // корабль в доке: ни шагов, ни входов
    keyboard.apply(prediction.curr, hulls.get(prediction.hullId));
    prediction.step(
      flightInput(),
      isOnline() ? (seq, input) => connection!.send({ t: 'input', seq, dx: input.dx, dy: input.dy, th: input.throttle }) : null,
    );
  });

  let lastFrame = performance.now();
  let wasOnline = false;
  let wasDead = false;
  app.ticker.add(() => {
    const now = performance.now();
    const frameSeconds = Math.min(0.1, (now - lastFrame) / 1000);
    lastFrame = now;

    const online = isOnline();
    if (wasOnline && !online) {
      prediction.resetNet(); // тормозим локально, при подключении примем состояние сервера
      remote.clear();
      meteors.clear();
      combat.clear();
      ownDto = null;
      docked = false; // с новым соединением сервер заново скажет, где корабль
      dockScreen.setHangar(null);
    }
    wasOnline = online;
    if (latestSnapshot && online) {
      const own = latestSnapshot.ships.find((ship) => ship.id === connection!.playerId);
      if (own) {
        prediction.reconcile(own);
        ownDto = own;
        weaponId = own.w;
      }
    }
    latestSnapshot = null;

    // Уничтожены: стик в центр и огонь отпущен — после респауна корабль не улетит и не начнёт палить сам.
    const dead = prediction.isDead;
    if (dead && !wasDead) {
      stick.reset();
      controls.setThrottle(0);
      fire.release();
    }
    if (!dead && wasDead) killedBy = '';
    wasDead = dead;

    const alpha = loop.advance(now);
    const state = prediction.render(alpha, frameSeconds);
    const hull = hulls.get(prediction.hullId);
    const input = flightInput();
    const desired = input.throttle > 0 && controls.source === 'stick' ? directionAngle(input.dx, input.dy) : null;
    ownShip.view.visible = !dead && !docked;
    ownShip.update(state.x, state.y, state.rot, hull, engineGlow(prediction.curr, input.throttle, hull), desired);
    ownAnchor = { x: state.x, y: state.y, size: hull.size };

    remote.update(now, online ? connection!.playerId : -1);
    loot.update(now, remote.renderTick);
    meteors.update(now);
    for (const event of combat.take(remote.renderTick)) play(event, now);

    // Предмет забрали или он протух — снимаем выбор.
    if (selectedLootId !== 0 && !loot.get(selectedLootId)) setLoot(0);
    const selectedLoot = selectedLootId !== 0 ? loot.get(selectedLootId) : undefined;

    // Цель ушла из системы (вышла, сервер перезапустился) или камень разбит, улетел — снимаем.
    const targetMeteor = targetId !== 0 ? meteors.get(targetId) : undefined;
    if (targetId !== 0 && !targetMeteor && remote.inLatest(targetId) === false) setTarget(0);
    const targetShip = targetId !== 0 && !targetMeteor ? remote.get(targetId) : undefined;
    const target = targetShip ?? targetMeteor;
    const weapon = weapons.get(weaponId);
    // Камень не уклоняется: шанс по нему — точность пушки минус штраф за дистанцию, как на сервере.
    const aim = targetShip
      ? assess(state, weapon, targetShip, evasion(hulls.get(targetShip.hull), Math.hypot(targetShip.vx, targetShip.vy)))
      : targetMeteor
        ? assess(state, weapon, targetMeteor, 0)
        : null;
    const tick = connection?.lastTick ?? 0;
    const protectedSeconds = ownDto?.pu ? Math.max(0, (ownDto.pu - tick) * DT) : 0;
    const me = ownId();
    let attackers = 0;
    for (const ship of remote.visible()) if (ship.kind === 'pirate' && ship.targetId === me) attackers++;

    camera.follow(state.x, state.y, zoom.value).apply(world, app.screen.width, app.screen.height);
    starfield.update(camera.x, camera.y, camera.zoom, app.screen.width, app.screen.height);
    nebula.update(now);
    weaponArc.update(state.x, state.y, state.rot, target && !dead && !docked ? weapon : null, aim?.state === 'ready');
    overlay.update({
      ships: remote.visible(),
      meteors: meteors.visible(),
      camera,
      width: app.screen.width,
      height: app.screen.height,
      target: target && aim ? { id: target.id, state: aim.state } : null,
      own: { x: state.x, y: state.y, size: hull.size, protected: !dead && protectedSeconds > 0 },
      ownId: me,
      showAi: dev.visible,
      loot: selectedLoot ? { x: selectedLoot.x, y: selectedLoot.y, size: selectedLoot.size } : null,
      station: stationSelected ? stationMark : null,
      sectorUnit,
    });
    fx.update(now, camera.zoom, locate);
    fire.setMode(stationSelected ? 'dock' : selectedLootId !== 0 ? 'grab' : 'fire');
    fire.render(now, !target ? 'none' : aim?.state === 'ready' ? 'ready' : 'blocked');

    combatHud.update(
      ownDto && online ? { hp: ownDto.hp, maxHp: hull.hp, sh: ownDto.sh, maxSh: hull.shield, protectedSeconds, attackers } : null,
      target && aim
        ? {
            name: target.name,
            // У камня вместо корпуса — просто «Камень»: строка класса под именем размера («Крупный метеорит»).
            hullName: targetShip ? hulls.get(targetShip.hull).name : 'Камень',
            hp: target.hp,
            maxHp: target.maxHp,
            sh: targetShip?.sh ?? 0,
            maxSh: targetShip?.maxSh ?? 0, // у камня щита нет
            aim,
            sectorUnit,
            fire: fire.active,
          }
        : null,
      dead && ownDto?.rt ? { by: killedBy, seconds: Math.max(0, (ownDto.rt - tick) * DT) } : null,
    );

    const toStation = Math.hypot(state.x - STATION.x, state.y - STATION.y);
    cargoHud.update(
      selectedLoot
        ? {
            kind: 'loot',
            item: selectedLoot.item,
            count: selectedLoot.count,
            distance: Math.hypot(selectedLoot.x - state.x, selectedLoot.y - state.y),
          }
        : stationSelected
          ? { kind: 'station', distance: toStation, inRange: toStation <= lootRules.stationRange }
          : null,
    );

    const speed = Math.hypot(prediction.curr.vx, prediction.curr.vy);
    const velocity = localVelocity(prediction.curr);
    status.update(connection, app.ticker.FPS, isBraking() && speed > STOPPED_SPEED);
    flight.update(hull.name, speed, input.throttle);
    dev.update({
      tick,
      tickRate: connection?.snapshotRate ?? 0,
      pingMs: connection?.rttMs ?? 0,
      speed,
      forward: velocity.forward,
      lateral: velocity.lateral,
      throttle: input.throttle,
      desiredDeg: toCompass(directionAngle(input.dx, input.dy)),
      headingDeg: toCompass(prediction.curr.rot),
      pending: prediction.pendingCount,
      correction: prediction.lastCorrection,
      peakCorrection: prediction.takePeakCorrection(),
      snaps: prediction.snaps,
      online: prediction.isSynced,
      interpMs: remote.clock.delayMs,
      jitterMs: remote.clock.jitterMs,
      extrapolations: remote.extrapolations,
      shots: fireStats.shots,
      hits: fireStats.hits,
      chanceSum: fireStats.chanceSum,
    });
  });
}

/** Угол в градусах по компасу экрана: 0 — вверх, 90 — вправо. */
function toCompass(angle: number): number {
  return ((angle * 180) / Math.PI + 360) % 360;
}

main();
