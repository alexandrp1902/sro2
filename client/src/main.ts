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
import type { GalaxyDto, MissionsMsg, RepMsg, ShipDto, SnapshotMsg, SystemDto } from './net/protocol';
import { RemoteShips } from './net/remoteShips';
import { Roster } from './net/roster';
import { resolveServerUrl } from './net/serverUrl';
import type { ReputationRules } from './sim/reputation';
import { Camera } from './render/camera';
import { CombatFx, isPseudoWeapon, type FxAnchor } from './render/combatFx';
import { JumpFx, type Jumper } from './render/jumpFx';
import { LootField } from './render/lootView';
import { MeteorField } from './render/meteorView';
import { MissileField } from './render/missiles';
import { Nebula } from './render/nebulaView';
import { PlayerOverlay } from './render/playerOverlay';
import { ShipView, engineGlow } from './render/ship';
import { loadSprites, moduleSprite, weaponSprite } from './render/sprites';
import { Starfield } from './render/starfield';
import { WeaponArc } from './render/weaponArc';
import { GATE_SIZE, SystemView, type PlanetInfo } from './render/world';
import { Landing } from './ui/landing';
import { DEFAULT_SECTOR_UNIT, assessBest, cooldownTicks, evasion, longestRange } from './sim/combat';
import { Modules, effectiveHull, fitWeapons, tierOf, type ShipFit } from './sim/fitting';
import { describeSystem, gateIndex, gateMarkId, pvpName } from './sim/galaxy';
import { DEFAULT_HULL, Hulls } from './sim/hulls';
import { NO_LOOT, lootLabel, rarityColor, type GearItem, type LootRules } from './sim/loot';
import type { MarketRules } from './sim/market';
import { DT, ION_SLOW, directionAngle, localVelocity, slowedHull, type MoveInput } from './sim/movement';
import { orbitSeconds } from './sim/orbits';
import { objective, objectiveSystem, trackerLines, doneLines, type MissionNames, type Objective } from './sim/missions';
import { AutoTarget } from './sim/autoTarget';
import type { NpcRules } from './sim/npcs';
import { DEFAULT_WEAPON, Weapons } from './sim/weapons';
import { CargoHud, type CargoState } from './ui/cargoHud';
import { CombatHud } from './ui/combatHud';
import { DevOverlay } from './ui/devOverlay';
import { DockScreen } from './ui/dockScreen';
import { Feed, describeBurn, describeKill, describeNotice, describeRepChange } from './ui/feed';
import { FlightHud } from './ui/flightHud';
import { GalaxyMap } from './ui/galaxyMap';
import { ControlsWindow } from './ui/controlsWindow';
import { keymap } from './input/keymap';
import { InvasionBoard, InvasionHud } from './ui/invasion';
import { Minimap } from './ui/minimap';
import { SosBoard } from './ui/sos';
import { ObjectiveHud } from './ui/objectiveHud';
import { InviteCard, PartyBoard, PartyPanel, describeBounty, describePartyEvent } from './ui/party';
import { PilotForm, describeDenied } from './ui/pilotForm';
import { StatusHud } from './ui/statusHud';
import { account } from './util/account';
import { storage } from './util/storage';

/** Id станции в прицеле: отрицательный, чтобы не совпасть с id кораблей и добычи от сервера. Врата — −2, −3… */
const STATION_ID = -1;
/** Планеты в прицеле: −100, −101… — дальше любых врат. */
const PLANET_ID = -100;
const planetMarkId = (index: number) => PLANET_ID - index;
const planetIndex = (markId: number) => PLANET_ID - markId;
/** Небо без сервера — как у стартовой системы. */
const SKY_SEED = 0;
/** Радар корпуса, если сервер его не прислал (старый баланс). */
const DEFAULT_RADAR = 2000;
/** Клавиша «взять ближайший предмет» ищет его в этом радиусе — примерно экран на среднем зуме. */
const LOOT_KEY_RANGE = 1200;
/** Размер ракеты как цели для эффектов: трассер зенитки летит в точку, а не в корабль. */
const MISSILE_TARGET_SIZE = 10;
/** Ниже этой скорости «корабль тормозит» в статусе не показываем. */
const STOPPED_SPEED = 1;
/** Размер точки маршрута патруля как цели: у неё нет тела, а стрелка должна во что-то упираться (M14). */
const OBJECTIVE_POINT_SIZE = 200;
/** Переключатель PvP на этом устройстве: '1' — включён. */
const PVP_KEY = 'sro.pvp';

async function main(): Promise<void> {
  preventBrowserGestures();

  const app = await createApp();
  document.getElementById('game')!.appendChild(app.canvas);
  // Картинки нужны видам с первого кадра: небо, станция и корабли строятся сразу ниже.
  await loadSprites();
  const el = (id: string) => document.getElementById(id)!;

  const hulls = new Hulls();
  const weapons = new Weapons();
  const modules = new Modules();
  /** Что стоит на своём корабле — из ангара; до него летим на стартовом. */
  let fit: ShipFit | null = null;
  /** Замедлен ионкой (M11) до этого тика сервера; 0 — нет. Предсказание летит на замедленном корпусе. */
  let slowUntilTick = 0;
  let serverTick = 0;
  /** Свой корабль летает и держит щит на корпусе с модулями — как его считает сервер. */
  const ownHulls = {
    get: (id: string) => {
      const hull = effectiveHull(hulls.get(id), fit, modules.catalog);
      return slowUntilTick > serverTick ? slowedHull(hull, ION_SLOW) : hull;
    },
  };
  /** Свои пушки по слотам; до ангара — стартовая. */
  const ownWeapons = () => (fit ? fitWeapons(fit, weapons) : [weapons.get(DEFAULT_WEAPON)]);
  /** Пушка первого слота: по её выстрелам крутится кольцо перезарядки на кнопке огня. */
  const mainWeaponId = () => fit?.weapons.find((id) => !!id) ?? DEFAULT_WEAPON;
  const controls = new Controls();
  const keyboard = new KeyboardControls(controls);
  bindKeyboard(keyboard);
  const stick = new Stick(el('stick'), controls);
  const zoom = new Zoom(app.canvas);

  // Корпус и оснащение сообщает сервер из аккаунта (hangar); до этого летим на стартовых.
  const prediction = new Prediction(ownHulls, DEFAULT_HULL, SPAWN);
  /** Каталог лута с сервера: названия, редкость и радиус захвата. */
  let lootRules: LootRules = NO_LOOT;
  /** Правила рынка (M12): по ним карта галактики показывает, что где производят и скупают. */
  let marketRules: MarketRules | null = null;
  let repRules: ReputationRules | null = null;
  let repState: RepMsg | null = null;
  /**
   * Каталог лута вместе со снаряжением (M11): выпавшие пушки и модули подписываются именами из каталогов,
   * а цвет им даёт тир — Mk2 синий, Mk3 фиолетовый.
   */
  const withGear = (rules: LootRules | null | undefined): LootRules => {
    const gear: Record<string, GearItem> = {};
    for (const id of weapons.ids()) gear[id] = { name: weapons.get(id).name, tier: tierOf(id), sprite: weaponSprite(id) };
    for (const id of modules.ids()) {
      const m = modules.get(id);
      if (m) gear[id] = { name: m.name, tier: tierOf(id), sprite: moduleSprite(m.slot, id) };
    }
    return { ...(rules ?? NO_LOOT), gear };
  };
  /** Единица дистанции для игрока: «цель в 1.4 сектора» вместо «в 980». */
  let sectorUnit = DEFAULT_SECTOR_UNIT;

  const serverUrl = resolveServerUrl();
  const connection = serverUrl ? new Connection(serverUrl) : null;
  const roster = connection?.roster ?? new Roster();

  // У каждой системы своё небо (сид из galaxy.json): после прыжка звёзды и туманность строятся заново.
  let skySeed = SKY_SEED;
  let starfield = new Starfield(skySeed);
  let nebula = new Nebula(skySeed);
  const world = new Container();
  const remote = new RemoteShips(hulls, roster);
  const ownShip = new ShipView('own');
  const weaponArc = new WeaponArc();
  const fx = new CombatFx(weapons);
  const overlay = new PlayerOverlay();
  const loot = new LootField();
  const meteors = new MeteorField();
  const missiles = new MissileField(weapons);
  const jumpFx = new JumpFx();
  let systemView = new SystemView(null);
  world.addChild(
    nebula.view,
    systemView.view,
    jumpFx.view,
    loot.view,
    meteors.view,
    missiles.view,
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
  /** Захват стрелка: когда можно навестись самим и когда игрок просил этого не делать. */
  const autoAim = new AutoTarget();
  const fire = new FireControl(el('fire'), el('combat-pad'));
  const setTarget = (id: number, auto = false) => {
    if (id === targetId) return;
    autoAim.changed(auto, Date.now());
    targetId = id;
    if (isOnline()) connection!.send({ t: 'target', id });
    if (id !== 0) {
      // прицел один: навёлся на корабль — отпустил груз, станцию и врата
      setLoot(0);
      setMark(0);
    }
    // Новая цель — огонь заново только новым нажатием: автоогонь не переносится на то, что выбрали для другого.
    // Захват стрелка — исключение: он ничего не выбирал, и гасить ему удержанный огонь не за что.
    // Так автозахват не меняет состояние огня ни в одну сторону: держал — стреляет в ответ, не держал — молчит.
    if (!auto) fire.release();
  };
  // Группа (GDD §37): свои — другим цветом, не цели для огня; позвать — с карточки цели.
  const party = new PartyBoard();
  remote.party = party.ids;
  const isAlly = (id: number) => party.isMember(id, ownId());
  /**
   * По нам выстрелили — наводимся на стрелка сами, но только если прицел свободен: свой выбор игрока
   * не трогаем никогда. Пять секунд после того, как он отменил наш выбор, тоже молчим.
   */
  const autoTarget = (shooter: number, now: number): void => {
    if (targetId !== 0 || docked || prediction.isDead || shooter <= 0 || shooter === ownId()) return;
    if (!autoAim.allows(now) || isAlly(shooter)) return;
    // Стрелок должен быть видимым кораблём: по метеориту и по тому, кого уже нет, целиться нечем.
    if (!remote.get(shooter)) return;
    setTarget(shooter, true);
  };
  const combatHud = new CombatHud(
    el('ship'),
    el('target'),
    el('death'),
    () => setTarget(0),
    () => {
      if (targetId > 0 && isOnline()) connection!.send({ t: 'party', action: 'invite', id: targetId });
    },
  );

  // Система (GDD §4): карта, станция или звезда, врата. Приходит в welcome, после прыжка — новая.
  let system: SystemDto | null = null;
  let galaxy: GalaxyDto | null = null;
  /** Топливо и бак (GDD §6) — из ангара: меняются только прыжком и заправкой. */
  let fuel = { fuel: 0, max: 0 };
  let home: string | null = null;
  /** Обучение и задания (GDD §36, §54) — от сервера, по событию. */
  let missions: MissionsMsg | null = null;
  let npcRules: NpcRules | null = null;
  /** Имена для текста заданий: сервер присылает id системы, типа пирата и предмета. */
  const names: MissionNames = {
    system: (id) => galaxy?.systems.find((s) => s.id === id)?.name ?? id,
    npc: (type) => npcRules?.types?.[type]?.name ?? type,
    item: (id) => lootRules.items?.[id]?.name ?? id,
    // Имена мест приходят вместе с картой галактики: адрес доставки бывает и в другой системе (M15).
    place: (key) => {
      for (const system of galaxy?.systems ?? []) {
        const found = system.places?.find((p) => p.key === key);
        if (found) return found.name;
      }
      return key.slice(key.indexOf(':') + 1);
    },
  };

  // Прицел один на всё: он на противнике, на грузе, на станции или на вратах. Наводка на предмет снимает цель и гасит огонь.
  // Тап по предмету только помечает его; подлетать игрок должен сам, автопилота в MVP нет (боевой документ §45).
  let selectedLootId = 0;
  const setLoot = (id: number) => {
    if (id === selectedLootId) return;
    selectedLootId = id;
    if (isOnline()) connection!.send({ t: 'loot', id });
    if (id !== 0) {
      // прицел один: навёлся на груз — отпустил противника, станцию и врата
      setTarget(0);
      setMark(0);
    }
  };

  // Станция и врата тоже выбираются прицелом (тап, клик, Q/E), но стрелять в них нельзя: прицел на станции —
  // чтобы пристыковаться, на вратах — чтобы прыгнуть. Выбор живёт только на клиенте: сервер про него не знает,
  // отрицательные id ни с чем не совпадут. 0 — ни станция, ни врата не выбраны.
  let markId = 0;
  /** Станция (если есть), планеты и врата системы — то, к чему летят, а не во что стреляют. Станция и планеты — на орбитах. */
  const marks = () => {
    const at = systemView.stationAt;
    const list = !system || system.station ? [{ id: STATION_ID, x: at.x, y: at.y, size: STATION.radius }] : [];
    systemView.planets.forEach((p, i) => list.push({ id: planetMarkId(i), x: p.x, y: p.y, size: p.size }));
    system?.gates.forEach((gate, i) => list.push({ id: gateMarkId(i), x: gate.x, y: gate.y, size: GATE_SIZE }));
    return list;
  };
  const selectedPlanet = () => (markId <= PLANET_ID ? (systemView.planets[planetIndex(markId)] ?? null) : null);
  const selectedMark = () => (markId === 0 ? null : (marks().find((m) => m.id === markId) ?? null));
  const selectedGate = () => (markId <= -2 && markId > PLANET_ID ? (system?.gates[gateIndex(markId)] ?? null) : null);
  function setMark(id: number): void {
    if (id === markId) return;
    markId = id;
    if (id !== 0) {
      setTarget(0);
      setLoot(0);
    }
  }
  const stationDistance = () => Math.hypot(prediction.curr.x - systemView.stationAt.x, prediction.curr.y - systemView.stationAt.y);
  const landingDistance = (planet: PlanetInfo) => Math.hypot(prediction.curr.x - planet.x, prediction.curr.y - planet.y);
  // Радиус посадки: как у станции, плюс радиус самой планеты — сервер считает ровно так же.
  const landingRange = (planet: PlanetInfo) => lootRules.stationRange + planet.size;
  // Жар звезды: предупреждаем один раз при входе в зону — щит тает раньше, чем это заметно по полоскам.
  let inHeat = false;
  const warnHeat = (x: number, y: number, away: boolean) => {
    const sun = system?.sun;
    const hot = !away && !!sun && Math.hypot(x, y) < sun.burnRadius;
    if (hot && !inHeat) feed.add('Жар звезды! Уходите — сгорите');
    inHeat = hot;
  };
  /** Свой корабль готовит гиперпрыжок. */
  const jumping = () => (ownDto?.j ?? 0) > 0;
  const landing = new Landing();
  /** Вид планеты по ключу её поселения: по нему выбирается кадр снижения. */
  const planetKindOf = (key: string): string | null =>
    system?.planets.find((p) => p.id && `pl:${p.id}` === key)?.kind ?? null;
  const cargoHud = new CargoHud(
    el('cargo'),
    el('loot'),
    () => {
      setLoot(0);
      setMark(0);
    },
    (item) => {
      if (isOnline()) connection!.send({ t: 'drop', item });
    },
  );

  // Док станции (GDD §26): корабль уходит из космоса, поверх мира — торговля, магазин и ангар.
  // Пока пристыкованы, полёт стоит: входы не шлём, при вылете их нумерация начинается заново.
  let docked = false;
  const send = (message: Parameters<Connection['send']>[0]) => {
    if (isOnline()) connection!.send(message);
  };
  const dockScreen = new DockScreen(el('dock'), hulls, weapons, modules, {
    onSell: (item, count) => send({ t: 'sell', item, count }),
    onBuyGoods: (item, count) => send({ t: 'buyGoods', item, count }),
    onBuy: (kind, id, slot) => send({ t: 'buy', kind, id, slot }),
    onEquip: (id) => send({ t: 'hull', id }),
    onFit: (slot, id) => send({ t: 'fit', slot, id }),
    onSellItem: (id) => send({ t: 'sellItem', id }),
    onRepair: () => send({ t: 'repair' }),
    onRefuel: () => send({ t: 'refuel' }),
    onUndock: () => send({ t: 'dock', on: false }),
    onControls: () => controlsWindow.toggle(),
    onAccept: (id) => send({ t: 'mission', action: 'accept', id }),
    onAbandon: () => send({ t: 'mission', action: 'abandon' }),
    onComplete: () => send({ t: 'mission', action: 'complete' }),
    onSkipTutorial: () => send({ t: 'mission', action: 'skip' }),
  }, names);

  // Карта галактики (GDD §55): M на ПК, тап по миникарте — везде.
  const galaxyMap = new GalaxyMap(el('galaxy'));
  // Окно «Управление» (M10.5): шестерёнка у миникарты и в доке, только на ПК — на телефоне кнопки на экране.
  const controlsWindow = new ControlsWindow(el('controls'));
  const controlsButton = el('controls-open') as HTMLButtonElement;
  controlsButton.addEventListener('click', () => {
    controlsButton.blur();
    controlsWindow.toggle();
  });
  const sos = new SosBoard();
  // Вторжение пиратов (GDD §38): табло справа, точка на миникарте, система на карте галактики.
  const invasion = new InvasionBoard();
  const minimap = new Minimap(el('minimap') as HTMLCanvasElement, () => galaxyMap.toggle());
  const refreshGalaxyMap = () =>
    galaxyMap.set(
      galaxy && system
        ? {
            galaxy,
            current: system.id,
            fuel: fuel.fuel,
            maxFuel: fuel.max,
            home,
            objective: objectiveSystem(missions, system.id, galaxy),
            invasion: invasion.system(performance.now()),
            market: marketRules,
            loot: lootRules,
            rep: repState?.systems ?? null,
            repRules,
          }
        : null,
    );
  // Трекер цели: тап — карта галактики, на ней звёздочкой отмечена система задания.
  const objectiveHud = new ObjectiveHud(el('objective'), () => galaxyMap.show());
  const invasionHud = new InvasionHud(el('event'), () => galaxyMap.show());
  const partyPanel = new PartyPanel(el('party'), () => send({ t: 'party', action: 'leave' }));
  const inviteCard = new InviteCard(el('invite'), (accept, from) =>
    send({ t: 'party', action: accept ? 'accept' : 'decline', id: from }),
  );
  window.addEventListener('keydown', (e) => {
    if (keymap.capturing || e.repeat || e.ctrlKey || e.metaKey || e.altKey) return;
    if (e.target instanceof HTMLInputElement) return;
    if (keymap.actionFor(e) === 'map') galaxyMap.toggle();
  });

  /** Всё, во что можно целиться: корабли и метеориты. Id у них из одного счётчика сервера. */
  const targets = () => [...remote.visible(), ...meteors.visible()];
  /** В системе без PvP (GDD §34) пилоты друг другу не цели: огонь их не выбирает. */
  const pvpOff = () => system?.pvp === 'off';
  /**
   * Свой переключатель PvP (рядом с миникартой): выключен — огонь не включается по игрокам, торговцам и рейнджерам,
   * и сервер их не повредит. Запоминается на устройстве; по умолчанию выключен.
   */
  let pvpOn = storage.get(PVP_KEY) === '1';
  const pvpButton = el('pvp') as HTMLButtonElement;
  const peaceful = (t: object) =>
    'kind' in t && (t.kind === 'player' || t.kind === 'trader' || t.kind === 'ranger' || t.kind === 'convoy' || t.kind === 'wing');
  const sendPvp = () => {
    if (isOnline()) connection!.send({ t: 'pvp', on: pvpOn });
  };
  const showPvp = () => {
    pvpButton.dataset.on = String(pvpOn);
    pvpButton.setAttribute('aria-pressed', String(pvpOn));
    pvpButton.querySelector('small')!.textContent = pvpOn ? 'вкл' : 'выкл';
  };
  showPvp();
  pvpButton.addEventListener('click', () => {
    pvpOn = !pvpOn;
    storage.set(PVP_KEY, pvpOn ? '1' : '0');
    showPvp();
    sendPvp();
    feed.add(pvpOn ? 'PvP включён: огонь бьёт игроков и торговцев' : 'PvP выключен: игроков и торговцев не атакуете');
    const current = remote.get(targetId);
    if (!pvpOn && current && peaceful(current)) fire.release();
  });
  // Огонь без цели берёт ближайший корабль или камень: сначала в секторе, потом просто в дальности.
  // Никого — огонь не включается.
  fire.onPress = () => {
    if (docked) return false;
    const current = remote.get(targetId) ?? meteors.get(targetId);
    if (current && !current.dead) {
      if (isAlly(current.id)) {
        feed.add(`${current.name} — в вашей группе`);
        return false;
      }
      if (!pvpOn && peaceful(current)) {
        feed.add(`${current.name}: PvP выключен — включите его у миникарты`);
        return false;
      }
      if (pvpOff() && 'kind' in current && current.kind === 'player') {
        feed.add(`В системе ${system!.name} PvP нет`);
        return false;
      }
      return true;
    }
    const foes = targets().filter((t) => !isAlly(t.id) && (pvpOn || !peaceful(t)));
    const candidates = pvpOff() ? foes.filter((t) => !('kind' in t) || t.kind !== 'player') : foes;
    const reach = longestRange(ownWeapons());
    const id = reach ? nearest(prediction.curr, candidates, reach) : null;
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
   * Перебираются корабли, добыча, станция и врата вперемешку: id кораблей и добычи из одного счётчика сервера,
   * у станции и врат свои, отрицательные. Прицел один: шаг на что-то одно снимает остальное.
   */
  const stepSelection = (step: -1 | 1) => {
    // Свои по группе в перебор не попадают: Q/E — для боя и дел, а союзника выбирают тапом.
    const candidates = [...targets().filter((t) => !isAlly(t.id)), ...loot.visible(), ...marks()];
    const from = markId !== 0 ? markId : selectedLootId !== 0 ? selectedLootId : targetId;
    // Кольцо спирали — сектор: сначала обходим всё вокруг себя, потом уходим на виток дальше.
    const id = cycle(prediction.curr, candidates, from, step, sectorUnit);
    if (id === null) return;
    if (id < 0) setMark(id);
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
  // у врат прыгают (во время подготовки — отменяют), по противнику стреляют.
  const grabSelected = (): boolean => {
    if (docked) {
      send({ t: 'dock', on: false });
      return true;
    }
    if (markId === STATION_ID) {
      if (stationDistance() <= lootRules.stationRange) send({ t: 'dock', on: true });
      else feed.add('Подлетите ближе к станции');
      return true;
    }
    const planet = selectedPlanet();
    if (planet) {
      // Садятся только туда, где есть поселение (M15): дикие планеты откроются вместе с мехами.
      if (!planet.place) feed.add(`${planet.name}: садиться некуда`);
      else if (landingDistance(planet) > landingRange(planet)) feed.add(`Подлетите ближе к поселению «${planet.placeName}»`);
      else send({ t: 'dock', on: true, place: planet.place });
      return true;
    }
    const gate = selectedGate();
    if (gate && system) {
      if (jumping()) send({ t: 'jump', to: null });
      else if (Math.hypot(prediction.curr.x - gate.x, prediction.curr.y - gate.y) > system.gateRange) {
        feed.add('Подлетите ближе к вратам');
      } else if (fuel.fuel < gate.cost) feed.add(`Не хватает топлива: ${fuel.fuel} из ${gate.cost}`);
      else send({ t: 'jump', to: gate.to });
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
      if (controlsWindow.open) controlsWindow.hide();
      else if (galaxyMap.open) galaxyMap.hide();
      else if (selectedLootId !== 0) setLoot(0);
      else if (markId !== 0) setMark(0);
      else setTarget(0);
    },
    grab: grabSelected,
  });
  // Esc закрывает окна, даже если «Снять цель» переназначена на другую клавишу.
  window.addEventListener('keydown', (e) => {
    if (e.code !== 'Escape' || keymap.capturing || keymap.actionFor(e) === 'targetClear') return;
    if (controlsWindow.open) controlsWindow.hide();
    else if (galaxyMap.open) galaxyMap.hide();
  });
  // F — ближайший предмет: на ПК иначе до мелкого обломка не дотянуться мышью в бою.
  window.addEventListener('keydown', (e) => {
    if (keymap.capturing || e.repeat || e.ctrlKey || e.metaKey || e.altKey) return;
    if (e.target instanceof HTMLInputElement || keymap.actionFor(e) !== 'nearestLoot') return;
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
      // Станция и врата — последними: они большие и не должны перехватывать тап по тому, что рядом с ними.
      const mark = pickAt(x, y, marks(), view, touch);
      if (mark !== null) {
        setMark(mark);
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
    // Зенитка (M11) стреляет по ракетам: цель выстрела — ракета, а не корабль.
    const missile = missiles.find(id);
    if (missile) return { x: missile.x, y: missile.y, size: MISSILE_TARGET_SIZE };
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
    // Осколки и таран идут под псевдо-пушкой (M15.5): полосу перезарядки они не крутят,
    // и спрашивать про них weapons.get нельзя — он молча вернёт импульсную.
    if (!isPseudoWeapon(shot.w) && shot.from === ownId() && shot.w === mainWeaponId() && !weapons.get(shot.w).missile) {
      fire.reloadFrom(now, cooldownTicks(weapons.get(shot.w)) * DT * 1000);
    }
  };

  /**
   * Система пришла или поменялась: карта, зоны, небо. После прыжка всё, что относилось к старой системе, —
   * цель, выбор, огонь, разгон — сбрасывается: корабль у врат новой системы стоит на месте.
   */
  const applySystem = (next: SystemDto | undefined, nextGalaxy: GalaxyDto | undefined, announce: boolean) => {
    const was = system;
    system = next ?? null;
    galaxy = nextGalaxy ?? null;
    if (JSON.stringify(was) !== JSON.stringify(system)) {
      world.removeChild(systemView.view);
      systemView.view.destroy({ children: true });
      systemView = new SystemView(system);
      world.addChildAt(systemView.view, 1);
    }
    const seed = system?.seed ?? SKY_SEED;
    if (seed !== skySeed) {
      skySeed = seed;
      const oldStars = starfield;
      starfield = new Starfield(seed);
      app.stage.addChildAt(starfield.view, 0);
      app.stage.removeChild(oldStars.view);
      oldStars.destroy();
      const oldNebula = nebula;
      nebula = new Nebula(seed);
      world.addChildAt(nebula.view, 0);
      world.removeChild(oldNebula.view);
      oldNebula.view.destroy({ children: true, texture: true, textureSource: true });
    }
    dockScreen.setStation(system?.station ? system.name : null, system?.id ?? null, system?.dockScene ?? null);
    if (was?.id !== system?.id) {
      sos.clear();
      setMark(0);
      setTarget(0);
      fire.release();
      if (was) {
        stick.reset();
        controls.setThrottle(0);
      }
      if (system && announce) feed.add(describeSystem(system));
    }
    refreshGalaxyMap();
  };

  /**
   * Где цель задания в этой системе: ближайший дрон, пират или груз, станция, врата. Кого нет на радаре —
   * на того и не указываем: трекер всё равно говорит, что делать.
   */
  const locateObjective = (goal: Objective | null, from: { x: number; y: number }) => {
    if (!goal) return null;
    const closest = <T extends { x: number; y: number }>(list: Iterable<T>): T | null => {
      let best: T | null = null;
      let bestDistance = Infinity;
      for (const item of list) {
        const d = Math.hypot(item.x - from.x, item.y - from.y);
        if (d < bestDistance) {
          best = item;
          bestDistance = d;
        }
      }
      return best;
    };
    switch (goal.kind) {
      case 'drone':
        return closest([...remote.visible()].filter((s) => s.kind === 'drone' && !s.dead));
      case 'pirate': {
        // Тип пирата в снапшоте не приходит — узнаём по корпусу типа из npcs.json.
        const hull = goal.npc ? npcRules?.types?.[goal.npc]?.hull : undefined;
        return closest([...remote.visible()].filter((s) => s.kind === 'pirate' && !s.dead && (!hull || s.hull === hull)));
      }
      case 'loot':
        return closest(loot.visible());
      case 'station':
        return system && !system.station ? null : { ...systemView.stationAt, size: STATION.radius };
      case 'gate': {
        const gates = system?.gates ?? [];
        const gate = goal.to ? gates.find((g) => g.to === goal.to) : [...gates].sort((a, b) => a.cost - b.cost)[0];
        return gate ? { x: gate.x, y: gate.y, size: GATE_SIZE } : null;
      }
      case 'meteor':
        return closest([...meteors.visible()].filter((m) => !goal.size || m.sizeId === goal.size));
      // Конвой и точка маршрута приходят от сервера: где конвой сейчас — знает снапшот, а не сообщение.
      case 'ship':
        return remote.get(goal.id) ?? null;
      case 'point':
        return { x: goal.x, y: goal.y, size: OBJECTIVE_POINT_SIZE };
    }
  };

  // За кадр сверяемся только с самым свежим снапшотом; чужим кораблям нужен весь поток.
  let latestSnapshot: SnapshotMsg | null = null;
  if (connection) {
    connection.onWelcome = (message, transfer) => {
      sendPvp(); // сервер ничего не помнит о переключателе: говорим после каждого входа и прыжка
      hulls.set(message.hulls);
      weapons.set(message.weapons);
      modules.set(message.modules);
      // Прыжок и возврат домой после гибели тоже приходят как resumed, но с другой системой — о ней и скажем.
      const sameSystem = (message.system?.id ?? null) === (system?.id ?? null);
      applySystem(message.system, message.galaxy, true);
      sectorUnit = message.combat.sectorUnit || DEFAULT_SECTOR_UNIT;
      lootRules = withGear(message.loot);
      npcRules = message.npcs ?? null;
      loot.setRules(lootRules);
      cargoHud.setRules(lootRules);
      marketRules = message.market ?? null;
      repRules = message.reputation ?? null;
      dockScreen.setRules(lootRules, message.shop, marketRules, message.reputation);
      loot.clear();
      meteors.setRules(message.meteors);
      meteors.clear();
      missiles.clear();
      selectedLootId = 0; // предметы в космосе за это время сменились — выбор не переносим
      if (transfer) prediction.resync();
      else prediction.resetNet();
      remote.clear();
      combat.clear();
      ownDto = null;
      slowUntilTick = 0;
      if (message.resumed && sameSystem) feed.add('Снова на связи — корабль ждал на месте');
      // Сервер после переподключения не помнит, во что мы целились и держим ли атаку.
      if (targetId !== 0) connection.send({ t: 'target', id: targetId });
      if (fire.active) connection.send({ t: 'fire', on: true });
    };
    connection.onConfig = (message) => {
      hulls.set(message.hulls);
      weapons.set(message.weapons);
      modules.set(message.modules);
      if (message.system) applySystem(message.system, message.galaxy, false);
      sectorUnit = message.combat.sectorUnit || DEFAULT_SECTOR_UNIT;
      lootRules = withGear(message.loot);
      npcRules = message.npcs ?? null;
      loot.setRules(lootRules);
      cargoHud.setRules(lootRules);
      marketRules = message.market ?? null;
      repRules = message.reputation ?? null;
      dockScreen.setRules(lootRules, message.shop, marketRules, message.reputation);
      meteors.setRules(message.meteors);
      dockScreen.refresh();
    };
    connection.onCargo = (message) => {
      const cargo: CargoState = {
        used: message.used,
        max: message.max,
        items: message.items,
        credits: message.credits ?? 0,
        reserved: message.reserved ?? 0,
      };
      cargoHud.setCargo(cargo);
      dockScreen.setCargo(cargo);
    };
    // Живые цены станции (M12): приходят, пока пилот в доке, и после каждой сделки.
    connection.onMarket = (message) => dockScreen.setMarket(message);
    connection.onRep = (message) => {
      repState = message;
      dockScreen.setRep(message);
      // Повод изменения — строкой в ленту: «−15 Vega: атака торговца».
      if (message.change) {
        const id = message.change.key.slice(message.change.key.indexOf(':') + 1);
        feed.add(describeRepChange(message.change, galaxy?.systems.find((s) => s.id === id)?.name));
      }
      refreshGalaxyMap(); // цвет колец на карте зависит от очков
    };
    connection.onHangar = (message) => {
      const was = docked;
      docked = message.docked;
      prediction.hullId = message.hull;
      fit = message.fit;
      fuel = { fuel: message.fuel ?? 0, max: message.maxFuel ?? 0 };
      home = message.home ?? null;
      refreshGalaxyMap();
      dockScreen.setPlace(message.place); // где именно стоим: от этого заголовок, фон и вкладки (M15)
      dockScreen.setHangar(message);
      cargoHud.setDocked(docked); // в доке груз продают, а не выбрасывают (M15.1)
      if (docked && !was) {
        // Посадка — это спуск, а не стыковка: показываем проход сквозь атмосферу поверх экрана поселения.
        if (message.place?.kind === 'pl') landing.show(planetKindOf(message.place.key));
        // В доке не целятся и не стреляют; после вылета корабль не рванёт с места сам.
        setTarget(0);
        setLoot(0);
        setMark(0); // после вылета пробел снова стреляет, а не стыкует
        fire.release();
        stick.reset();
        controls.setThrottle(0);
      }
      // Вылет: сервер начал буфер входов заново — и мы нумеруем их с 1, первый снапшот принимаем как есть.
      if (!docked && was) {
        prediction.resetNet();
        landing.stop(); // взлетели, не досмотрев спуск
        dockScreen.setMarket(null); // цены того места больше не наши: в следующем они свои
      }
    };
    connection.onMissions = (message) => {
      missions = message;
      dockScreen.setMissions(message);
      // Письмо места в трюме не занимает, поэтому в cargo его нет — показываем по взятому заданию (M14).
      cargoHud.setLetter(message.active?.offer.kind === 'courier');
      if (message.done) for (const line of doneLines(message.done)) feed.add(line);
      refreshGalaxyMap();
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
    connection.onSos = (message) => {
      const text = sos.apply(message, performance.now());
      if (text) feed.add(text, message.state === 'on');
    };
    connection.onParty = (message) => {
      if (message.t === 'partyInvite') inviteCard.show(message, performance.now());
      else if (message.t === 'partyState') party.apply(message);
      else feed.add(describePartyEvent(message));
    };
    connection.onBounty = (message) => feed.add(describeBounty(message));
    connection.onInvasion = (message) => {
      const line = invasion.apply(message, performance.now(), system?.id ?? '');
      if (line) feed.add(line.text, line.alert);
      refreshGalaxyMap();
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
      missiles.push(message);
      for (const event of combat.push(message, own)) play(event, now);
      for (const shot of message.shots ?? []) {
        if (shot.to === own) autoTarget(shot.from, now);
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
        // Звезда: убийцы нет, а погибший — корабль, не камень.
        const burned = kill.by === 0 && !isMeteor(kill.id);
        if (burned && (kill.id === own || roster.get(kill.id)?.npc === false)) feed.add(describeBurn(nameOf(kill.id)));
        else if (!withMeteor || (mine && kill.by !== 0)) feed.add(describeKill(nameOf(kill.by), nameOf(kill.id)));
        if (kill.id === own) killedBy = burned ? 'жар звезды' : nameOf(kill.by);
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
    keyboard.apply(prediction.curr, ownHulls.get(prediction.hullId));
    prediction.step(
      flightInput(),
      isOnline() ? (seq, input) => connection!.send({ t: 'input', seq, dx: input.dx, dy: input.dy, th: input.throttle }) : null,
    );
  });

  let lastFrame = performance.now();
  let wasOnline = false;
  let wasDead = false;
  let missileWarned = false;
  app.ticker.add(() => {
    const now = performance.now();
    const frameSeconds = Math.min(0.1, (now - lastFrame) / 1000);
    lastFrame = now;

    const online = isOnline();
    if (wasOnline && !online) {
      prediction.resetNet(); // тормозим локально, при подключении примем состояние сервера
      remote.clear();
      meteors.clear();
      missiles.clear();
      combat.clear();
      ownDto = null;
      docked = false; // с новым соединением сервер заново скажет, где корабль
      dockScreen.setHangar(null);
      missions = null;
      dockScreen.setMissions(null);
      cargoHud.setLetter(false);
      sos.clear();
      party.clear();
      inviteCard.hide();
      invasion.clear();
      refreshGalaxyMap();
    }
    wasOnline = online;
    if (latestSnapshot && online) {
      const own = latestSnapshot.ships.find((ship) => ship.id === connection!.playerId);
      serverTick = latestSnapshot.tick;
      if (own) {
        slowUntilTick = own.sl ?? 0;
        prediction.reconcile(own);
        ownDto = own;
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
    const hull = ownHulls.get(prediction.hullId);
    const input = flightInput();
    const desired = input.throttle > 0 && controls.source === 'stick' ? directionAngle(input.dx, input.dy) : null;
    ownShip.view.visible = !dead && !docked;
    ownShip.update(state.x, state.y, state.rot, prediction.hullId, hull, engineGlow(prediction.curr, input.throttle, hull), desired);
    ownAnchor = { x: state.x, y: state.y, size: hull.size };

    remote.update(now, online ? connection!.playerId : -1);
    // Станция и планеты — по орбитальному времени сервера: от тика, на котором сейчас рисуется мир.
    const worldTick = Number.isNaN(remote.renderTick) ? (connection?.lastTick ?? 0) : remote.renderTick;
    systemView.update(orbitSeconds(system, worldTick));
    warnHeat(state.x, state.y, dead || docked);
    const jumpers: Jumper[] = [];
    if (ownDto?.j && !dead) jumpers.push({ x: state.x, y: state.y, size: hull.size, jumpAt: ownDto.j });
    for (const ship of remote.visible()) if (ship.jumpAt > 0 && !ship.dead) jumpers.push(ship);
    jumpFx.update(jumpers, remote.renderTick, (system?.jumpSeconds ?? 3) / DT, now);
    loot.update(now, remote.renderTick);
    meteors.update(now);
    missiles.update(remote.renderTick, ownId());
    // Ракета в меня — одна строка в ленте, пока летит хоть одна: от неё уходят манёвром.
    const incoming = online && !dead && !docked ? missiles.incoming(ownId()) : 0;
    if (incoming > 0 && !missileWarned) feed.add('Ракета! Уходите манёвром');
    missileWarned = incoming > 0;
    for (const event of combat.take(remote.renderTick)) play(event, now);

    // Предмет забрали или он протух — снимаем выбор.
    if (selectedLootId !== 0 && !loot.get(selectedLootId)) setLoot(0);
    const selectedLoot = selectedLootId !== 0 ? loot.get(selectedLootId) : undefined;

    // Цель ушла из системы (вышла, сервер перезапустился) или камень разбит, улетел — снимаем.
    const targetMeteor = targetId !== 0 ? meteors.get(targetId) : undefined;
    if (targetId !== 0 && !targetMeteor && remote.inLatest(targetId) === false) setTarget(0);
    const targetShip = targetId !== 0 && !targetMeteor ? remote.get(targetId) : undefined;
    const target = targetShip ?? targetMeteor;
    // Камень не уклоняется: шанс по нему — точность пушки минус штраф за дистанцию, как на сервере.
    // Пушек несколько — карточка и сектор по той, что готова стрелять, иначе по самой дальнобойной.
    const best = targetShip
      ? assessBest(state, ownWeapons(), targetShip, evasion(hulls.get(targetShip.hull), Math.hypot(targetShip.vx, targetShip.vy)))
      : targetMeteor
        ? assessBest(state, ownWeapons(), targetMeteor, 0)
        : null;
    const aim = best?.aim ?? null;
    const weapon = best?.weapon ?? null;
    const tick = connection?.lastTick ?? 0;
    const protectedSeconds = ownDto?.pu ? Math.max(0, (ownDto.pu - tick) * DT) : 0;
    const me = ownId();
    let attackers = 0;
    for (const ship of remote.visible()) if (ship.kind !== 'player' && ship.kind !== 'drone' && ship.targetId === me) attackers++;

    // Цель задания или обучения: на неё указывает золотой маркер, на миникарте — кольцо.
    const goal = online ? locateObjective(objective(missions, system?.id ?? null, galaxy, docked || dead), state) : null;
    objectiveHud.update(online && !docked ? trackerLines(missions, system?.id ?? null, docked, names) : null);
    dockScreen.tick(Date.now()); // срок письма идёт и в доке (M14)
    invasionHud.update(online ? invasion.lines(now, roster.get(me)?.name ?? '') : null);
    partyPanel.update(online && party.size > 0 ? party.rows({ id: me, system: system?.id ?? '', x: state.x, y: state.y }, sectorUnit) : null);
    inviteCard.tick(now);

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
      station: selectedMark(),
      sectorUnit,
      objective: goal,
      party: party.ids,
    });
    fx.update(now, camera.zoom, locate);
    const gate = selectedGate();
    const landing = selectedPlanet();
    fire.setMode(
      markId === STATION_ID
        ? 'dock'
        : landing?.place
          ? 'land'
          : gate
            ? jumping()
              ? 'cancel'
              : 'jump'
            : selectedLootId !== 0
              ? 'grab'
              : 'fire',
    );
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
            invite: targetShip?.kind === 'player' && !isAlly(targetShip.id),
            member: !!targetShip && isAlly(targetShip.id),
          }
        : null,
      dead && ownDto?.rt ? { by: killedBy, seconds: Math.max(0, (ownDto.rt - tick) * DT) } : null,
    );

    const toStation = Math.hypot(state.x - systemView.stationAt.x, state.y - systemView.stationAt.y);
    const planet = selectedPlanet();
    cargoHud.update(
      selectedLoot
        ? {
            kind: 'loot',
            item: selectedLoot.item,
            count: selectedLoot.count,
            distance: Math.hypot(selectedLoot.x - state.x, selectedLoot.y - state.y),
          }
        : markId === STATION_ID
          ? { kind: 'station', distance: toStation, inRange: toStation <= lootRules.stationRange }
          : planet
            ? {
                kind: 'planet',
                name: planet.name,
                // От поверхности, а не от центра: у планеты радиус до 250, и «2400 м» до Терры сбивало бы с толку.
                distance: Math.max(0, Math.hypot(planet.x - state.x, planet.y - state.y) - planet.size),
                settlement: planet.placeName,
                inRange: !!planet.place && Math.hypot(planet.x - state.x, planet.y - state.y) <= landingRange(planet),
              }
          : gate && system
            ? {
                kind: 'gate',
                name: gate.name,
                distance: Math.hypot(gate.x - state.x, gate.y - state.y),
                inRange: Math.hypot(gate.x - state.x, gate.y - state.y) <= system.gateRange,
                cost: gate.cost,
                fuel: fuel.fuel,
                charging: jumping() ? Math.max(0, (ownDto!.j! - tick) * DT) : null,
              }
            : null,
    );

    minimap.hidden = !online || !system;
    pvpButton.hidden = minimap.hidden;
    controlsButton.hidden = minimap.hidden;
    minimap.update(
      {
        sun: Boolean(system?.sun),
        station: !system || system.station ? systemView.stationAt : null,
        planets: systemView.planets,
        pirateBase: system?.pirateBase ?? null,
        gates: system?.gates ?? [],
        own: dead || docked ? null : { x: state.x, y: state.y, rot: state.rot },
        radar: hull.radar ?? DEFAULT_RADAR,
        ships: [...remote.visible()].map((s) => ({ id: s.id, x: s.x, y: s.y, kind: isAlly(s.id) ? 'party' : s.kind, dead: s.dead })),
        targetId,
        objective: goal,
        missiles: missiles.visible(),
        sos: sos.active(now, (id) => {
          const ship = remote.get(id);
          return ship && !ship.dead ? ship : null;
        }),
        invasion: invasion.point(system?.id ?? ''),
      },
      now,
    );

    const speed = Math.hypot(prediction.curr.vx, prediction.curr.vy);
    const velocity = localVelocity(prediction.curr);
    status.update(connection, app.ticker.FPS, isBraking() && speed > STOPPED_SPEED, system ? `${system.name} · ${pvpName(system.pvp)}` : null);
    flight.update(hull.name, speed, input.throttle, online && fuel.max > 0 ? fuel : null);

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

/**
 * Холст игры. Chrome на части Android-телефонов отдаёт WebGL в проверке, но не создаёт контекст (GPU в чёрном
 * списке, контекст потерян) — тогда Pixi бросает исключение, и игра не запускалась вовсе. Пробуем по очереди:
 * WebGL со сглаживанием, без него, WebGPU, Canvas 2D.
 */
async function createApp(): Promise<Application> {
  const base = {
    resizeTo: window,
    background: '#05060a',
    resolution: Math.min(window.devicePixelRatio || 1, 2),
    autoDensity: true,
  };
  const attempts = [
    { preference: 'webgl', antialias: true },
    { preference: 'webgl', antialias: false },
    { preference: 'webgpu', antialias: false },
    { preference: 'canvas', antialias: false },
  ] as const;
  let error: unknown;
  for (const attempt of attempts) {
    const app = new Application();
    try {
      await app.init({ ...base, ...attempt, preference: [attempt.preference] });
      return app;
    } catch (e) {
      error = e;
      console.warn(`renderer ${attempt.preference} failed`, e);
    }
  }
  throw error;
}

/** Игра не запустилась: вместо мёртвой формы входа — понятная причина на экране. */
function showFatal(error: unknown): void {
  const box = document.createElement('div');
  box.style.cssText =
    'position:fixed;inset:0;z-index:1000;display:flex;align-items:center;justify-content:center;padding:24px;' +
    'background:#05060a;color:#e8eefc;font:16px system-ui,sans-serif;text-align:center;line-height:1.5';
  box.textContent =
    `Игра не запустилась в этом браузере: ${error instanceof Error ? error.message : String(error)}. ` +
    'Обновите Chrome или включите аппаратное ускорение (chrome://flags → WebGL).';
  document.body.appendChild(box);
}

main().catch((e: unknown) => {
  console.error(e);
  showFatal(e);
});
