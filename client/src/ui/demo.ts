/**
 * Витрина интерфейса без сервера: `?demo=<экран>` собирает настоящие построители HUD, дока и окон
 * на фиксированных данных. Нужна проходам по дизайну (скриншоты headless-браузером на ПК и телефоне)
 * и ничего не шлёт. Экраны: flight, dock-missions, dock-cargo, dock-hulls, dock-ships, dock-fitting,
 * galaxy, controls, confirm, menu, death, login.
 */
import type { Connection } from '../net/connection';
import type { GalaxyDto, HangarMsg, MarketMsg, MissionsMsg, RepMsg } from '../net/protocol';
import { Modules } from '../sim/fitting';
import { Hulls } from '../sim/hulls';
import type { LootRules } from '../sim/loot';
import type { MissionNames } from '../sim/missions';
import type { ReputationRules } from '../sim/reputation';
import type { ShopRules } from '../sim/shop';
import { Weapons } from '../sim/weapons';
import { CargoHud } from './cargoHud';
import { CombatHud } from './combatHud';
import { ConfirmCard, logoutLines } from './confirm';
import { ControlsWindow } from './controlsWindow';
import { DockScreen, type Tab } from './dockScreen';
import { Feed } from './feed';
import { FlightHud } from './flightHud';
import { GalaxyMap } from './galaxyMap';
import { InvasionHud } from './invasion';
import { BurgerMenu } from './menu';
import { Minimap } from './minimap';
import { ObjectiveHud } from './objectiveHud';
import { InviteCard, PartyPanel } from './party';
import { StatusHud } from './statusHud';

/* loot.json — JSONC с комментариями, сборщик его не ест: каталог груза для витрины задан здесь. */
const LOOT = {
  pickupRange: 130,
  lifetimeSeconds: 120,
  fadeSeconds: 10,
  stationUnload: true,
  stationRange: 200,
  items: {
    metal: { name: 'Металл', rarity: 'common', volume: 1, price: 10 },
    ore: { name: 'Руда', rarity: 'common', volume: 1, price: 8 },
    energy: { name: 'Энергоблок', rarity: 'uncommon', volume: 2, price: 45 },
    crystals: { name: 'Кристаллы', rarity: 'rare', volume: 2, price: 75 },
  },
} as unknown as LootRules;

const el = (id: string): HTMLElement => document.getElementById(id)!;
const noop = (): void => {};

/** Какой экран просят; null — обычная игра. */
export function demoScreen(search: string): string | null {
  return new URLSearchParams(search).get('demo');
}

const GALAXY: GalaxyDto = {
  systems: [
    { id: 'sol', name: 'Sol', danger: 1, pvp: 'off', station: true, x: 18, y: 55, region: 'core', places: [{ key: 'st:sol', name: 'Гавань Сол' }] },
    { id: 'vega', name: 'Vega', danger: 2, pvp: 'on', station: true, x: 45, y: 35, region: 'core', places: [{ key: 'st:vega', name: 'Вега-1' }] },
    { id: 'rigel', name: 'Rigel', danger: 4, pvp: 'on', station: false, x: 70, y: 60, region: 'rim', places: [] },
    { id: 'deneb', name: 'Deneb', danger: 3, pvp: 'on', station: true, x: 82, y: 25, region: 'rim', places: [{ key: 'st:deneb', name: 'Денеб' }] },
  ] as GalaxyDto['systems'],
  links: [
    { a: 'sol', b: 'vega' },
    { a: 'vega', b: 'rigel' },
    { a: 'vega', b: 'deneb' },
    { a: 'rigel', b: 'deneb' },
  ] as GalaxyDto['links'],
  regions: [
    { id: 'core', name: 'Ядро', color: '#3f7fbf' },
    { id: 'rim', name: 'Рубеж', color: '#b1495a' },
  ] as GalaxyDto['regions'],
};

const REP_RULES = {
  limit: 100,
  levels: [
    { id: 'enemy', name: 'Враг', from: -100, price: 1.1, color: '#c0392b' },
    { id: 'neutral', name: 'Чужак', from: -20, price: 1, color: '#8a93a6' },
    { id: 'friend', name: 'Друг', from: 40, price: 0.95, color: '#3f9f6a' },
  ],
  gate: null,
} as unknown as ReputationRules;

const NAMES: MissionNames = {
  system: (id) => GALAXY.systems.find((s) => s.id === id)?.name ?? id,
  npc: (type) => ({ raider: 'рейдер', scout: 'разведчик' })[type] ?? type,
  item: (id) => LOOT.items?.[id]?.name ?? id,
  place: (key) => ({ 'st:sol': 'Гавань Сол', 'st:vega': 'Вега-1', 'st:deneb': 'Денеб' })[key] ?? key,
};

export function runDemo(screen: string): void {
  const hulls = new Hulls();
  const weapons = new Weapons();
  const modules = new Modules();
  const loot = LOOT;
  const online = {
    state: 'online',
    serverVersion: null,
    replaced: false,
    hasCredentials: true,
    accountName: 'Рейнджер-7',
    totalOnline: 12,
    roster: { onlineCount: 12 },
    rttMs: 48,
  } as unknown as Connection;

  const feed = new Feed(el('feed'));
  const status = new StatusHud(el('status'), noop);
  const flight = new FlightHud(el('flight'), noop);
  const combatHud = new CombatHud(el('ship'), el('target'), el('death'), noop, noop);
  const cargoHud = new CargoHud(el('cargo'), el('loot'), noop, noop);
  const objectiveHud = new ObjectiveHud(el('objective'), noop);
  const invasionHud = new InvasionHud(el('event'), noop);
  const partyPanel = new PartyPanel(el('party'), noop);
  const inviteCard = new InviteCard(el('invite'), noop);
  const confirm = new ConfirmCard(el('confirm'));
  const menu = new BurgerMenu(el('menu'), noop);
  const galaxyMap = new GalaxyMap(el('galaxy'));
  const controlsWindow = new ControlsWindow(el('controls'));
  const minimap = new Minimap(el('minimap') as HTMLCanvasElement, noop);
  void inviteCard;

  const own = { hp: 120, maxHp: 150, sh: 40, maxSh: 60, protectedSeconds: 0, attackers: 2 };
  const target = {
    name: 'Пират «Коготь»',
    hullName: 'Рейдер',
    aim: { state: 'ready' as const, distance: 640, chance: 72 },
    sectorUnit: 500,
    fire: true,
    invite: false,
    member: false,
    hp: 80,
    maxHp: 120,
    sh: 10,
    maxSh: 40,
  };
  const cargo = { used: 12, max: 20, items: { metal: 5, crystals: 2 }, credits: 1240, reserved: 2 };

  const flightHud = (): void => {
    // Первый кадр может прийти раньше, чем истечёт порог перерисовки, — сбрасываем его напрямую.
    (status as unknown as { lastRender: number }).lastRender = -1e9;
    (flight as unknown as { lastRender: number }).lastRender = -1e9;
    status.update(online, 60, false, 'Sol · PvP нет');
    flight.update('Пчела', 240, 0.8);
    combatHud.update(own, target, null);
    cargoHud.setRules(loot);
    cargoHud.setCargo(cargo);
    cargoHud.update({ kind: 'station', distance: 340, inRange: false });
    objectiveHud.update({ title: 'Доставить Металл ×5 на Vega', hint: 'Откройте карту — M, прыгайте через врата' });
    invasionHud.update({ title: 'Вторжение пиратов', hint: 'волна 2 из 3 · 01:20', alert: true });
    partyPanel.update([
      { id: 1, name: 'Аякс', leader: true, self: true, hp: 120, maxHp: 150, sh: 40, maxSh: 60, where: '1.2с', status: '' },
      { id: 2, name: 'Борей', leader: false, self: false, hp: 60, maxHp: 150, sh: 0, maxSh: 60, where: 'Vega', status: 'в доке' },
    ]);
    feed.add('Рейнджер-123 входит в систему');
    feed.add('+5 Sol: пират уничтожен');
    feed.warn('Подлетите ближе к вратам');
    feed.add('SOS: торговец «Альба» под атакой у врат B', true);
    minimap.hidden = false;
    el('pvp').hidden = false;
    el('menu-open').hidden = false;
    minimap.update(
      {
        sun: true,
        station: { x: 900, y: -300 },
        planets: [{ x: -1500, y: 1200 }],
        pirateBase: null,
        gates: [],
        own: { x: 200, y: 400, rot: 0 },
        radar: 2000,
        ships: [
          { id: 2, x: 700, y: 600, kind: 'pirate', dead: false },
          { id: 3, x: -400, y: 900, kind: 'trader', dead: false },
        ],
        targetId: 2,
      },
      1e9,
    );
    // Телефон: стик и кнопки боя — как их показывает игра при первом касании.
    if (matchMedia('(pointer: coarse)').matches) {
      document.documentElement.style.setProperty('--stick-r', '62px');
      el('stick').style.setProperty('--stick-r', '62px');
      el('stick').hidden = false;
      el('combat-pad').hidden = false;
      el('fire').dataset.active = 'true';
      el('fire').dataset.aim = 'ready';
      el('fire').querySelector('.fire-state')!.textContent = 'вкл';
    }
  };

  const dock = (tab: Tab): void => {
    const shop: ShopRules = {
      startCredits: 1000,
      repairPrice: 0.25,
      hulls: Object.fromEntries(hulls.ids().slice(0, 4).map((id, i) => [id, i * 2500])),
      items: Object.fromEntries([...weapons.ids(), ...modules.ids()].map((id, i) => [id, 300 + i * 150])),
      sellShare: 0.5,
    };
    const screen = new DockScreen(el('dock'), hulls, weapons, modules, {
      onSell: noop, onBuyGoods: noop, onBuy: noop, onEquip: noop, onTransport: noop, onFit: noop, onSellItem: noop,
      onRepair: noop, onUndock: noop, onMenu: (anchor) => menu.toggleAt(anchor), onAccept: noop, onAbandon: noop,
      onComplete: noop, onSkipTutorial: noop,
    }, NAMES);
    screen.setRules(loot, shop, {}, REP_RULES);
    screen.setGalaxy(GALAXY);
    screen.setStation('Гавань Сол', 'sol');
    screen.setPlace({ key: 'st:sol', kind: 'st', name: 'Гавань Сол', shipyard: true });
    screen.setCargo(cargo);
    const fit = {
      weapons: [weapons.ids()[0] ?? null],
      engine: modules.ids().find((id) => modules.get(id)?.slot === 'engine') ?? null,
      radar: modules.ids().find((id) => modules.get(id)?.slot === 'radar') ?? null,
      generator: modules.ids().find((id) => modules.get(id)?.slot === 'generator') ?? null,
    };
    const hangar: HangarMsg = {
      t: 'hangar',
      hull: hulls.ids()[0],
      fit,
      hulls: hulls.ids().slice(0, 2),
      storage: { [weapons.ids()[1] ?? weapons.ids()[0]]: 1 },
      docked: true,
      hp: 120,
      maxHp: 150,
      power: 7,
      powerMax: 10,
      ships: { [hulls.ids()[1]]: 'st:vega' },
      place: { key: 'st:sol', kind: 'st', name: 'Гавань Сол', shipyard: true },
    };
    const missions: MissionsMsg = {
      t: 'missions',
      tutorial: { step: 2, total: 6, id: 'grab', title: 'Подберите груз у сбитого пирата', hint: 'Подлетите к контейнеру и нажмите G' },
      active: {
        offer: { id: 'm1', kind: 'deliver', system: 'vega', item: 'metal', count: 5, reward: 420, from: 'sol', place: 'st:vega' },
        progress: 2,
        until: Math.floor(Date.now() / 1000) + 300,
      },
      offers: [
        { id: 'o1', kind: 'kill', system: 'rigel', npc: 'raider', count: 3, reward: 600, from: 'sol' },
        { id: 'o2', kind: 'collect', item: 'ore', count: 6, reward: 180, from: 'sol' },
      ],
    };
    const market: MarketMsg = {
      t: 'market',
      system: 'sol',
      items: [
        { id: 'metal', buy: 14, sell: 11, stock: 40, norm: 50 },
        { id: 'ore', buy: 9, sell: 7, stock: 120, norm: 50 },
        { id: 'crystals', buy: 0, sell: 96, stock: 4, norm: 30 },
      ],
      rumours: [{ kind: 'route', good: 'crystals', system: 'vega', name: 'Vega', hops: 1, price: 120, profit: 45 }],
      station: { produces: ['metal', 'ore'], consumes: ['crystals'] } as MarketMsg['station'],
      demand: { case: 'uprising', title: 'Восстание на Терре', goods: ['energy'], mul: 1.8, left: 30, quota: 40 },
    };
    const rep: RepMsg = {
      t: 'rep',
      systems: { sol: 20 },
      places: { 'st:sol': 45 },
      here: { place: 'st:sol', value: 45, level: 'friend', system: 20, systemLevel: 'neutral', region: 10 },
    };
    screen.setMissions(missions);
    screen.setMarket(market);
    screen.setRep(rep);
    screen.setHangar(hangar);
    const index = (['missions', 'cargo', 'hulls', 'ships', 'fitting'] as Tab[]).indexOf(tab);
    document.querySelectorAll<HTMLButtonElement>('.dock-tab')[index]?.click();
  };

  switch (screen) {
    case 'flight':
      flightHud();
      break;
    case 'death':
      flightHud();
      combatHud.update(own, null, { by: 'Пират «Коготь»', seconds: 12 });
      break;
    case 'confirm':
      flightHud();
      confirm.ask(logoutLines(false), noop);
      break;
    case 'menu':
      flightHud();
      menu.showAt(el('menu-open').getBoundingClientRect());
      break;
    case 'galaxy':
      flightHud();
      galaxyMap.set({ galaxy: GALAXY, current: 'sol', home: 'sol', objective: 'vega', invasion: 'rigel', demand: 'deneb', loot, market: {} });
      galaxyMap.show();
      break;
    case 'controls':
      flightHud();
      controlsWindow.show();
      break;
    case 'login':
      el('connect').hidden = false;
      el('connect').querySelector<HTMLElement>('.connect-error')!.textContent = 'Не хватает пароля';
      break;
    default:
      if (screen.startsWith('dock-')) dock(screen.slice(5) as Tab);
      else flightHud();
  }
}
