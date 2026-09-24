/**
 * Витрина интерфейса без сервера: `?demo=<экран>` собирает настоящие построители HUD, дока и окон
 * на фиксированных данных. Нужна проходам по дизайну (скриншоты headless-браузером на ПК и телефоне)
 * и ничего не шлёт. Экраны: flight, dock-missions, dock-cargo, dock-hulls, dock-ships, dock-fitting,
 * galaxy, controls, audio, radio, confirm, menu, password, death, login, login-new, login-over, party10, trade.
 */
import type { Connection } from '../net/connection';
import type { CareerDto, GalaxyDto, HangarMsg, MarketMsg, MissionsMsg, RepMsg } from '../net/protocol';
import { Modules } from '../sim/fitting';
import { Hulls } from '../sim/hulls';
import type { LootRules } from '../sim/loot';
import type { MissionNames } from '../sim/missions';
import type { ReputationRules } from '../sim/reputation';
import type { ShopRules } from '../sim/shop';
import { Weapons } from '../sim/weapons';
import { Controls } from '../input/controls';
import { KeyboardControls, bindKeyboard } from '../input/keyboard';
import { CargoHud } from './cargoHud';
import { CombatHud } from './combatHud';
import { ConfirmCard, logoutLines } from './confirm';
import { DialogCard } from './dialog';
import { AudioWindow } from './audioWindow';
import { AudioSettings } from '../audio/settings';
import { ControlsWindow } from './controlsWindow';
import { DockScreen, type Tab } from './dockScreen';
import { Feed } from './feed';
import { FlightHud } from './flightHud';
import { GalaxyMap, type GalaxyMapState } from './galaxyMap';
import { InvasionHud } from './invasion';
import { BurgerMenu, coarsePointer } from './menu';
import { PasswordForm } from './passwordForm';
import { PilotForm } from './pilotForm';
import { Minimap, type MinimapFrame } from './minimap';
import { ObjectiveHud } from './objectiveHud';
import { InviteCard, PartyPanel, type PartyMark, type PartyRow } from './party';
import { TradeWindow } from './trade';
import { StatusHud } from './statusHud';
import { TipsCard } from './tips';

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

/** Пути пилота для витрины: сервер присылает их в nameFree, здесь они фиксированные. */
const CAREERS: CareerDto[] = [
  { id: 'ranger', name: 'Рейнджер', hint: 'Боевой корабль', enabled: true },
  { id: 'trader', name: 'Торговец', hint: 'Грузовой трюм', enabled: true },
  { id: 'pirate', name: 'Пират', hint: 'Скоро', enabled: false },
];

/** Кадр миникарты для витрины: система со звездой, станцией, планетой и парой чужих кораблей. */
const DEMO_MINIMAP: MinimapFrame = {
  name: 'Сол',
  danger: 1,
  sun: true,
  burnRadius: 600,
  // Орбиты станции и планет; обитаемая планета — кольцо, необитаемая — точка.
  orbits: [950, 1920, 3300],
  station: { x: 900, y: -300 },
  planets: [
    { x: -1500, y: 1200, settled: true },
    { x: 2900, y: -1600, settled: false },
  ],
  pirateBase: null,
  // Врата подписаны буквой системы за ними (M16c); вторые — по проложенному курсу.
  gates: [
    { to: 'vega', name: 'Вега', x: -1900, y: -2300 },
    { to: 'tau', name: 'Тау', x: 2600, y: 1500 },
    { to: 'castor', name: 'Кастор', x: 2300, y: -2400 },
  ],
  routeGate: 2,
  own: { x: 200, y: 400, rot: 0 },
  radar: 2000,
  ships: [
    { id: 2, x: 700, y: 600, kind: 'pirate', dead: false },
    { id: 3, x: -400, y: 900, kind: 'trader', dead: false },
  ],
  targetId: 2,
};

/** Полная группа (M16b): десять строк — я, лидер, раненые, в доке, сбитый и без связи. */
const BIG_PARTY: PartyRow[] = [
  { id: 1, n: 3, name: 'Аякс', leader: false, self: true, hp: 120, maxHp: 150, sh: 40, maxSh: 60, where: '', status: '' },
  { id: 2, n: 1, name: 'Борей', leader: true, self: false, hp: 150, maxHp: 150, sh: 60, maxSh: 60, where: '0.8с', status: '' },
  { id: 3, n: 2, name: 'Вега', leader: false, self: false, hp: 40, maxHp: 150, sh: 0, maxSh: 60, where: '1.4с', status: '' },
  { id: 4, n: 4, name: 'Гелиос', leader: false, self: false, hp: 150, maxHp: 400, sh: 90, maxSh: 200, where: '3.1с', status: '' },
  { id: 5, n: 5, name: 'Дедал', leader: false, self: false, hp: 400, maxHp: 400, sh: 200, maxSh: 200, where: 'Вега', status: 'в доке' },
  { id: 6, n: 6, name: 'Елена', leader: false, self: false, hp: 0, maxHp: 150, sh: 0, maxSh: 60, where: '2.2с', status: 'сбит' },
  { id: 7, n: 7, name: 'Зевс', leader: false, self: false, hp: 260, maxHp: 400, sh: 120, maxSh: 200, where: 'Тау', status: 'нет связи' },
  { id: 8, n: 8, name: 'Икар', leader: false, self: false, hp: 90, maxHp: 150, sh: 25, maxSh: 60, where: '0.4с', status: '' },
  { id: 9, n: 9, name: 'Кассиопея', leader: false, self: false, hp: 330, maxHp: 400, sh: 180, maxSh: 200, where: '5.0с', status: '' },
  { id: 10, n: 10, name: 'Лира', leader: false, self: false, hp: 140, maxHp: 150, sh: 55, maxSh: 60, where: 'Нова', status: '' },
];

/** Метки тех из группы, кто в этой же системе: номера совпадают с панелью. */
const DEMO_PARTY_MARKS: PartyMark[] = [
  { id: 2, n: 1, x: 900, y: 200 },
  { id: 3, n: 2, x: -800, y: -600 },
  { id: 4, n: 4, x: 2100, y: 2700 },
  { id: 8, n: 8, x: 300, y: -1400 },
  { id: 9, n: 9, x: -2400, y: 2400 },
];

const el = (id: string): HTMLElement => document.getElementById(id)!;
const noop = (): void => {};

/** Какой экран просят; null — обычная игра. */
export function demoScreen(search: string): string | null {
  return new URLSearchParams(search).get('demo');
}

/** Восемь систем в трёх регионах — плотность как у настоящей galaxy.json; порядок gates согласован со связями. */
const GALAXY: GalaxyDto = {
  systems: [
    { id: 'sol', name: 'Сол', danger: 1, pvp: 'off', station: true, x: 18, y: 55, region: 'core', gates: ['vega', 'tau'], places: [{ key: 'st:sol', name: 'Гавань Сол' }] },
    { id: 'vega', name: 'Вега', danger: 2, pvp: 'on', station: true, x: 45, y: 35, region: 'core', gates: ['sol', 'rigel', 'deneb', 'nova'], places: [{ key: 'st:vega', name: 'Вега-1' }] },
    { id: 'tau', name: 'Тау', danger: 3, pvp: 'on', station: false, x: 40, y: 74, region: 'frontier', gates: ['sol', 'nova'], places: [] },
    { id: 'nova', name: 'Нова', danger: 4, pvp: 'on', station: true, x: 62, y: 46, region: 'frontier', gates: ['tau', 'vega', 'castor'], places: [{ key: 'st:nova', name: 'Форт Нова' }] },
    { id: 'castor', name: 'Кастор', danger: 3, pvp: 'on', station: true, x: 56, y: 14, region: 'frontier', gates: ['nova', 'deneb'], places: [{ key: 'st:castor', name: 'Рудник' }] },
    { id: 'rigel', name: 'Ригель', danger: 4, pvp: 'on', station: false, x: 72, y: 66, region: 'rim', gates: ['vega', 'deneb', 'sigma'], places: [] },
    { id: 'deneb', name: 'Денеб', danger: 5, pvp: 'on', station: true, x: 84, y: 28, region: 'rim', gates: ['vega', 'rigel', 'castor'], places: [{ key: 'st:deneb', name: 'Денеб' }] },
    { id: 'sigma', name: 'Сигма', danger: 6, pvp: 'on', station: false, x: 92, y: 82, region: 'rim', gates: ['rigel'], places: [] },
  ] as GalaxyDto['systems'],
  links: [
    { a: 'sol', b: 'vega' },
    { a: 'sol', b: 'tau' },
    { a: 'vega', b: 'rigel' },
    { a: 'vega', b: 'deneb' },
    { a: 'vega', b: 'nova' },
    { a: 'tau', b: 'nova' },
    { a: 'nova', b: 'castor' },
    { a: 'castor', b: 'deneb' },
    { a: 'rigel', b: 'deneb' },
    { a: 'rigel', b: 'sigma' },
  ] as GalaxyDto['links'],
  regions: [
    { id: 'core', name: 'Ядро', color: '#3f7fbf' },
    { id: 'frontier', name: 'Пограничье', color: '#c99a3a' },
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
  // Те же обработчики ввода, что в игре: колесо тяги и блокировка жестов должны не мешать прокрутке списков.
  bindKeyboard(new KeyboardControls(new Controls()));
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
  const tradeWindow = new TradeWindow(el('trade'), { onOffer: noop, onReady: noop, onCancel: noop });
  const inviteCard = new InviteCard(el('invite'), noop);
  const confirm = new ConfirmCard(el('confirm'));
  const menu = new BurgerMenu(el('menu'), noop);
  menu.account = true; // в витрине пилот всегда с аккаунтом: иначе «Сменить пароль» в меню не увидеть
  const passwordForm = new PasswordForm(el('password'), noop);
  const pilotForm = new PilotForm(el('connect'), { onLogin: noop, onLogout: noop, onCheckName: noop });
  const galaxyMap = new GalaxyMap(el('galaxy'));
  const controlsWindow = new ControlsWindow(el('controls'));
  // Окно «Звук» (M17) живёт на порте настроек, поэтому в витрине обходится без AudioContext.
  // Ключ свой: витрина не должна переписывать громкости, с которыми человек играет.
  const audioWindow = new AudioWindow(el('audio'), new AudioSettings('sro.audio.demo'));
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
    trade: false,
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
    status.update(online, 60, false, 'Сол · PvP нет');
    flight.update('Пчела', 240, 0.8);
    combatHud.update(own, target, null);
    cargoHud.setRules(loot);
    cargoHud.setCargo(cargo);
    cargoHud.update({ kind: 'station', distance: 340, inRange: false });
    objectiveHud.update({ title: 'Доставить Металл ×5 на Вегу', hint: 'Откройте карту — M, прыгайте через врата' });
    invasionHud.update({ title: 'Вторжение пиратов', hint: 'волна 2 из 3 · 01:20', alert: true });
    partyPanel.update(
      [
        { id: 1, n: 1, name: 'Аякс', leader: true, self: true, hp: 120, maxHp: 150, sh: 40, maxSh: 60, where: '1.2с', status: '' },
        { id: 2, n: 2, name: 'Борей', leader: false, self: false, hp: 60, maxHp: 150, sh: 0, maxSh: 60, where: 'Вега', status: 'в доке' },
      ],
      10,
    );
    feed.add('Рейнджер-123 входит в систему');
    feed.add('+5 Сол: пират уничтожен');
    feed.warn('Подлетите ближе к вратам');
    feed.add('SOS: торговец «Альба» под атакой у врат B', true);
    minimap.hidden = false;
    el('pvp').hidden = false;
    el('menu-open').hidden = false;
    minimap.update(DEMO_MINIMAP, 1e9);
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
      onSell: noop, onBuyGoods: noop, onBuy: noop, onEquip: noop, onTransport: noop, onFit: noop, onSellItem: noop, onSellGear: noop,
      onRepair: noop, onUndock: noop, onMenu: (anchor) => menu.toggleAt(anchor), onAccept: noop, onAbandon: noop,
      onComplete: noop, onSkipTutorial: noop,
    }, NAMES);
    screen.setRules(loot, shop, {}, REP_RULES);
    screen.setGalaxy(GALAXY);
    screen.setStation('Гавань Сол', 'sol');
    // «Гавань Сол» кольцевая, и набор сцен у неё свой (пачка G): в игре scene приезжает в месте с сервера.
    screen.setPlace({ key: 'st:sol', kind: 'st', name: 'Гавань Сол', scene: 'ring', shipyard: true });
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
      // Два разных модуля на складе: на рынке по ним считается кнопка «Продать модули» (M16a).
      storage: { [weapons.ids()[1] ?? weapons.ids()[0]]: 1, [modules.ids()[0]]: 2 },
      docked: true,
      hp: 120,
      maxHp: 150,
      power: 7,
      powerMax: 10,
      ships: { [hulls.ids()[1]]: 'st:vega' },
      place: { key: 'st:sol', kind: 'st', name: 'Гавань Сол', scene: 'ring', shipyard: true },
    };
    const missions: MissionsMsg = {
      t: 'missions',
      tutorial: { step: 2, total: 6, id: 'grab', kind: 'grab', title: 'Подберите груз у сбитого пирата', hint: 'Подлетите к контейнеру и нажмите G' },
      active: {
        offer: { id: 'm1', kind: 'deliver', system: 'vega', item: 'metal', count: 5, reward: 420, from: 'sol', place: 'st:vega' },
        progress: 2,
        until: Math.floor(Date.now() / 1000) + 300,
      },
      offers: [
        { id: 'o1', kind: 'kill', system: 'rigel', npc: 'raider', count: 3, reward: 600, from: 'sol' },
        { id: 'o2', kind: 'collect', item: 'ore', count: 6, reward: 180, from: 'sol' },
      ],
      // Кампания (M20a): на доске у неё свой раздел над работой станции.
      story: {
        campaign: 'quietWar',
        name: 'Тихая война',
        done: 2,
        total: 14,
        lines: ['Дошли. Хорошо.'],
        offer: {
          id: 'story:quietWar:medic',
          kind: 'deliver',
          system: 'sol',
          count: 20,
          reward: 900,
          from: 'st:sol',
          place: 'st:vega',
          story: {
            campaign: 'quietWar',
            mission: 'medic',
            name: 'Тихая война',
            title: 'Инженер Морен',
            brief: 'На Руднике Прайм кончились лекарства. Отвезите — и заодно посмотрите, чем там дышат.',
            objective: 'Отвезите медикаменты на Рудник Прайм',
            hint: 'Груз занимает трюм; сдать — в доке Рудника Прайм',
            giver: 'Капитан Холт',
            role: 'начальник охраны станции',
            number: 3,
            total: 14,
          },
        },
      },
    };
    const market: MarketMsg = {
      t: 'market',
      system: 'sol',
      items: [
        { id: 'metal', buy: 14, sell: 11, stock: 40, norm: 50, sells: true },
        // Станция его не делает, но он есть на складе — значит, продаётся (M16a).
        { id: 'ore', buy: 9, sell: 7, stock: 120, norm: 50, sells: true },
        // А это — груз здешнего задания «собрать»: запас виден, купить нельзя.
        { id: 'crystals', buy: 104, sell: 96, stock: 4, norm: 30, sells: false },
      ],
      rumours: [{ kind: 'route', good: 'crystals', system: 'vega', name: 'Вега', hops: 1, price: 120, profit: 45 }],
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
    case 'password':
      flightHud();
      passwordForm.show();
      break;
    case 'buoy': {
      // Шаг «остановиться» (M18): подсказка по источнику ввода и зона буя на миникарте.
      flightHud();
      const touch = coarsePointer();
      objectiveHud.update({
        title: 'Обучение 2/8: Долетите до буя и остановитесь',
        hint: touch ? 'Стоп — двойной тап по стику' : 'Тормоз — полный назад: удерживайте {brake}, пока скорость не упадёт до нуля',
      });
      const own = DEMO_MINIMAP.own!;
      const buoy = { x: own.x + 900, y: own.y - 700 };
      minimap.update({ ...DEMO_MINIMAP, buoy, objective: buoy }, 1e9 + 1000);
      break;
    }
    case 'dialog':
      // Сюжетный диалог (M20a) с выбором: самая сложная его форма — вопрос и две кнопки.
      flightHud();
      new DialogCard(el('dialog')).show(
        {
          who: 'Звено «Клык»',
          role: 'патруль рейнджеров',
          lines: ['Борт, у вас на борту записи с места крушения. Приказ — изъять. Что нам передать?'],
          options: [
            { label: 'Отдать записи', flag: 'logGiven' },
            { label: 'Оставить себе', flag: 'logKept' },
          ],
        },
        'Тихая война',
      );
      break;
    case 'tips':
      // «Что дальше» (M18): карточка после последнего шага обучения.
      flightHud();
      new TipsCard(el('tips')).show(coarsePointer());
      break;
    case 'trade':
      // Стол обмена (M16b): своя половина со степперами, чужая только для чтения, и он уже готов.
      flightHud();
      tradeWindow.setRules(loot);
      tradeWindow.setCargo(cargo);
      tradeWindow.set({
        t: 'tradeState',
        active: true,
        own: { id: 1, name: 'Аякс', credits: 300, items: { metal: 3 }, ready: false },
        their: { id: 2, name: 'Борей', credits: 0, items: { crystals: 1, energy: 2 }, ready: true },
        rev: 4,
      });
      break;
    case 'party10':
      // Полная группа (M16b): компактные строки, номера, прокрутка — и метки на миникарте.
      flightHud();
      partyPanel.update(BIG_PARTY, 10);
      minimap.update({ ...DEMO_MINIMAP, party: DEMO_PARTY_MARKS }, 1e9 + 1000); // позже порога перерисовки
      break;
    case 'galaxy':
      flightHud();
      galaxyMap.set({
        galaxy: GALAXY,
        current: 'sol',
        home: 'sol',
        objective: 'vega',
        invasion: 'rigel',
        demand: 'deneb',
        loot,
        market: { places: { 'st:sol': { produces: ['metal', 'ore'], consumes: ['crystals'] } } } as GalaxyMapState['market'],
        // Отношение (M13): в Vega ценят, в Deneb док закрыт — кольца у узлов и строка в карточке.
        rep: { vega: 60, deneb: -50 },
        repRules: REP_RULES,
        // Проложенный курс (M16b): Sol → Vega → Rigel, и в карточке — сколько прыжков и какие врата.
        course: 'rigel',
        gates: ['vega', 'tau'],
      });
      galaxyMap.show();
      break;
    case 'controls':
      flightHud();
      controlsWindow.show();
      break;
    case 'audio':
      flightHud();
      audioWindow.show();
      break;
    case 'radio':
      // Субтитры эфира (M17b): три реплики в ленте — торговец, рейнджер, пират.
      flightHud();
      feed.radio('Торговец «Альба»', 'Борт, это торговец. Идём с грузом, не стреляйте.');
      feed.radio('Рейнджер-7', 'Патруль: в секторе бой. Мирным отойти.');
      feed.radio('Пират «Коготь»', 'Ну здравствуй, добыча. Сдавай груз — и, может, полетишь дальше.');
      break;
    case 'login':
      // Стартовый экран, вкладка «Вход»: ник помним с прошлого раза, за окном заставка.
      pilotForm.show({ url: 'sro.example.com', name: 'Новичок', loggedIn: false }, 'Не хватает пароля');
      break;
    case 'login-new':
      // Первый заход: ника не помним, и окно открывается на «Регистрации» само (M15.8).
      pilotForm.show({ url: 'sro.example.com', name: '', loggedIn: false });
      pilotForm.setNameFree({ t: 'nameFree', name: '', free: true, career: 'ranger', careers: CAREERS });
      break;
    case 'login-over':
      // То же окно поверх идущей игры: заставки нет, мир виден сквозь затемнение.
      flightHud();
      pilotForm.show({ url: 'sro.example.com', name: 'Новичок', loggedIn: true });
      break;
    default:
      if (screen.startsWith('dock-')) dock(screen.slice(5) as Tab);
      else flightHud();
  }
}
