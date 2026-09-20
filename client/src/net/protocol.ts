// Зеркало server/Sro.Server/Net/Protocol.cs. Поле t — тип сообщения.

import type { CombatRules, WeaponConfig } from '../sim/combat';
import type { ModuleConfig, ShipFit } from '../sim/fitting';
import type { LootRules } from '../sim/loot';
import type { MarketRules } from '../sim/market';
import type { MeteorRules } from '../sim/meteors';
import type { HullConfig } from '../sim/movement';
import type { NpcRules } from '../sim/npcs';
import type { ReputationRules } from '../sim/reputation';
import type { ShopRules } from '../sim/shop';

/** Версия протокола; зеркало Protocol.Version на сервере. Сервер другой версии (или старый, без поля) — не играем. */
export const PROTOCOL_VERSION = 19;

/** Состояние ИИ пирата: патруль, бой, возврат в логово (налётчик — полёт от врат к точке), уход из системы. */
export type AiState = 'patrol' | 'attack' | 'return' | 'leave';

export type ClientMessage =
  /**
   * Вход. С паролем или ключом устройства — пилот с аккаунтом: корабль, кредиты и трюм сервер берёт из аккаунта.
   * Без них — гость без сохранения (тесты, смоук-скрипты); hull, weapon и token нужны только гостю.
   */
  | {
      t: 'hello';
      name?: string;
      hull?: string;
      weapon?: string;
      token?: string;
      password?: string;
      key?: string;
    }
  | { t: 'ping'; c: number }
  /** Только управление (§49): направление на экране и тяга; координаты клиент не присылает. */
  | { t: 'input'; seq: number; dx: number; dy: number; th: number }
  | { t: 'hull'; id: string }
  | { t: 'weapon'; id: string }
  /** Поставить в слот пушку или модуль со склада; id = null — снять на склад. */
  | { t: 'fit'; slot: string; id: string | null }
  /** Продать со склада пушку или модуль — за долю цены. */
  | { t: 'sellItem'; id: string }
  | { t: 'name'; name: string }
  /** Выбранная цель (GDD §9); 0 — цели нет. */
  | { t: 'target'; id: number }
  /** Атака нажата или отпущена: пока нажата, пушка стреляет сама по готовности (GDD §47). */
  | { t: 'fire'; on: boolean }
  /** Выбранный предмет (боевой документ §45); 0 — нет. Его и забирает команда grab. */
  | { t: 'loot'; id: number }
  /** Взять выбранный предмет: подбор ручной, сам луч ничего не хватает. */
  | { t: 'grab' }
  /**
   * Продать груз в доке; item — что именно, без него — весь трюм (всё, чем здесь торгуют).
   * count — сколько штук; 0 или без него — вся стопка.
   */
  | { t: 'sell'; item?: string; count?: number }
  /**
   * Купить товар на рынке станции (M12). Отдельно от buy: тот берёт одну вещь в слот или на склад,
   * а здесь — N единиц в трюм. Сервер сам урежет count до склада, кредитов и места.
   */
  | { t: 'buyGoods'; item: string; count: number }
  /** Пристыковаться к станции или вылететь из дока. */
  | { t: 'dock'; on: boolean }
  /**
   * Купить в доке корпус (сразу ставится), пушку или модуль: со slot — сразу в этот слот (старое — на склад),
   * без него — в свободный подходящий слот или на склад.
   */
  | { t: 'buy'; kind: BuyKind; id: string; slot?: string }
  /** Починить корпус и зарядить щит в доке. */
  | { t: 'repair' }
  /** Начать гиперпрыжок через врата в систему to (GDD §5); null — отменить подготовку. */
  | { t: 'jump'; to: string | null }
  /** Заправить бак в доке до полного. */
  | { t: 'refuel' }
  /**
   * Задания (GDD §36, §54): accept — взять с доски (в доке), abandon — бросить своё, complete — сдать «собрать»
   * (в доке), skip — пропустить обучение.
   */
  | { t: 'mission'; action: MissionAction; id?: string }
  /** Группа (GDD §37): invite — позвать пилота id, accept/decline — ответить на приглашение пилота id, leave — выйти. */
  | { t: 'party'; action: PartyAction; id?: number }
  /** Переключатель PvP: выключен — пушки пилота не бьют игроков, торговцев и рейнджеров. */
  | { t: 'pvp'; on: boolean };

export type PartyAction = 'invite' | 'accept' | 'decline' | 'leave';

export type MissionAction = 'accept' | 'abandon' | 'complete' | 'skip';

/** Что покупают в доке. */
export type BuyKind = 'hull' | 'item';

export interface ShipDto {
  id: number;
  x: number;
  y: number;
  r: number;
  vx: number;
  vy: number;
  hull: string;
  th: number;
  /** Последний применённый seq владельца: состояние выше — ровно после этого входа. */
  ack: number;
  /** Корпус и щит, округлены вверх. */
  hp: number;
  sh: number;
  /** Первая стоящая пушка; "" — пушек нет. */
  w: string;
  /** Уничтожен и появится в этот тик; нет поля — цел. */
  rt?: number;
  /** Под защитой до этого тика; нет поля — без защиты. */
  pu?: number;
  /** Цель пирата в бою; нет поля — нет (и у игроков). */
  tg?: number;
  /** Состояние ИИ пирата; у игроков нет. */
  ai?: AiState | null;
  /** Готовится гиперпрыжок: корабль уйдёт из системы в этот тик; нет поля или 0 — нет. */
  j?: number;
  /** Замедлен ионкой (M11) до этого тика; нет поля или 0 — нет. */
  sl?: number;
}

export interface ShotDto {
  from: number;
  to: number;
  w: string;
  hit: boolean;
  /** Урон всего (0 при промахе) и из него — по щиту. */
  dmg: number;
  sh: number;
  /** Шанс попадания, %, по которому бросал сервер. */
  ch: number;
}

export interface KillDto {
  id: number;
  /** Кто нанёс смертельный удар. */
  by: number;
}

/** Предмет в космосе. Поля короткие: снапшот один на всех и уходит 20 раз в секунду. */
export interface LootDto {
  id: number;
  x: number;
  y: number;
  /** Идентификатор предмета из loot.json. */
  i: string;
  /** Количество в стопке. */
  n: number;
  /** Тик, когда предмет исчезнет: по нему считаем, когда мигать. */
  e: number;
  /** Предмет из контейнера, а не обломки: рисуем ящиком. Нет поля — обломки. */
  c?: boolean;
}

/** Подобранное в этом тике — видно всем: чужой луч объясняет, куда делся предмет. */
export interface PickDto {
  /** Чей тракторный луч забрал предмет. */
  by: number;
  id: number;
  i: string;
  n: number;
}

/**
 * Метеорит. Летит строго по прямой с постоянной скоростью: положение через Δt — x + vx·Δt, точно.
 * Радиус и максимум прочности — из meteors.json по размеру.
 */
export interface MeteorDto {
  id: number;
  x: number;
  y: number;
  vx: number;
  vy: number;
  /** Размер — ключ sizes в meteors.json. */
  s: string;
  /** Прочность, округлена вверх. */
  hp: number;
}

/** Ракета в полёте (боевой документ §37): скорость — из пушки w, курс — r; между кадрами клиент ведёт её сам. */
export interface MissileDto {
  id: number;
  x: number;
  y: number;
  /** Курс, как у корабля: 0 — нос вверх. */
  r: number;
  /** Кто запустил. */
  o: number;
  /** В кого летит. */
  t: number;
  /** Ракетница — ключ weapons.json. */
  w: string;
}

export interface SnapshotMsg {
  t: 'snapshot';
  tick: number;
  ships: ShipDto[];
  /** Выстрелы и уничтожения этого тика; нет — поля нет. */
  shots?: ShotDto[];
  kills?: KillDto[];
  /** Предметы, лежащие в космосе, и подобранное в этом тике. */
  loot?: LootDto[];
  picks?: PickDto[];
  /** Метеориты в системе; нет — поля нет. */
  meteors?: MeteorDto[];
  /** Ракеты в полёте; нет — поля нет. */
  missiles?: MissileDto[];
}

export interface WelcomeMsg {
  t: 'welcome';
  /** Id своего корабля в снапшотах. */
  id: number;
  tickRate: number;
  /** Версия протокола сервера; у серверов до M3 поля нет. */
  version?: number;
  hulls: HullConfig;
  weapons: WeaponConfig;
  combat: CombatRules;
  /** Вернулись к кораблю, который ждал нас после обрыва связи. */
  resumed: boolean;
  /** Пираты: логова и укрытие у станции — для карты. */
  npcs?: NpcRules;
  /** Лут: радиус захвата, вид и редкость предметов. */
  loot?: LootRules;
  /** Метеориты: размеры, прочность и пороги предупреждения о таране. */
  meteors?: MeteorRules;
  /** Магазин станции: цены корпусов, пушек и ремонта. */
  shop?: ShopRules;
  /** Система, где сейчас корабль. После гиперпрыжка приходит новый welcome с новой системой. */
  system?: SystemDto;
  /** Карта галактики. */
  galaxy?: GalaxyDto;
  /** Модули кораблей; нет — сервер без modules.json: щит, радар и бак даёт корпус. */
  modules?: ModuleConfig | null;
  /**
   * Правила рынка этой станции (M12): по ним считается цена пачки — той же формулой, что на сервере.
   * Живые цены приходят отдельным market; нет поля — рынка здесь нет.
   */
  market?: MarketRules | null;
  /** Правила репутации (M13); нет — сервер без reputation.json, всё как до M13. */
  reputation?: ReputationRules | null;
}

/** PvP в системе (GDD §34): off — нет; border — нет у станции; free — везде. */
export type PvpRule = 'off' | 'border' | 'free';

/** Врата: куда ведут, как называется та система и сколько топлива стоит прыжок. */
export interface GateDto {
  to: string;
  name: string;
  x: number;
  y: number;
  cost: number;
}

/** Круговая орбита вокруг звезды; положение — функция времени (sim/orbits.ts). */
export interface OrbitDto {
  /** Расстояние до звезды; 0 — в центре. */
  radius: number;
  /** Оборот за столько минут; отрицательное — в обратную сторону. */
  periodMinutes: number;
  /** Угол в градусах в момент 0 орбитального времени. */
  phase: number;
}

/** Звезда в центре системы: ближе burnRadius жжёт. */
export interface SunDto {
  /** Вид: yellow, orange, blue, red. */
  kind: string;
  radius: number;
  burnRadius: number;
  burnDps: number;
}

/** Планета на орбите: выбирается прицелом, сквозь неё можно пролететь. */
export interface PlanetDto {
  name: string;
  /** Вид: terran, desert, ice, gas. */
  kind: string;
  /** Радиус в мире. */
  size: number;
  orbit: OrbitDto;
}

/** Пиратская база: отсюда вылетают налётчики пиратской системы. */
export interface PirateBaseDto {
  name: string;
  x: number;
  y: number;
}

export interface SystemDto {
  id: string;
  name: string;
  /** Опасность 1–5 (GDD §33). */
  danger: number;
  pvp: PvpRule;
  /** В системе есть станция; иначе дока и укрытия нет. */
  station: boolean;
  /** Небо системы. */
  seed: number;
  /** Радиус укрытия у станции; 0 — укрытия нет. */
  core: number;
  /** Ближе этого к вратам можно начать прыжок. */
  gateRange: number;
  /** Подготовка прыжка, секунды. */
  jumpSeconds: number;
  gates: GateDto[];
  /** Звезда в центре; null — её нет (сервер без galaxy.json). */
  sun: SunDto | null;
  /** Орбита станции; радиус 0 — станция в центре. */
  stationOrbit: OrbitDto;
  planets: PlanetDto[];
  /** Орбитальное время в тик 0 системы, секунды: орбиты считаются от тика снапшота. */
  orbitEpoch: number;
  pirateBase?: PirateBaseDto | null;
  /** Картинка станции (M11); нет — по опасности системы. */
  stationSprite?: string | null;
  /** Свой набор фонов дока (M12): ranger…; нет — общие сцены станции. */
  dockScene?: string | null;
  /** Регион галактики (M11). */
  region?: string | null;
}

/** Система на карте галактики (GDD §55). */
export interface GalaxySystemDto {
  id: string;
  name: string;
  danger: number;
  pvp: PvpRule;
  station: boolean;
  x: number;
  y: number;
  /** Регион галактики (M11); нет — регионов нет. */
  region?: string | null;
}

/** Регион галактики (M11): Ядро, Пограничье, Дальний рубеж. */
export interface RegionDto {
  id: string;
  name: string;
  color: string;
}

/** Маршрут; cost — топлива на прыжок в любую сторону. */
export interface LinkDto {
  a: string;
  b: string;
  cost: number;
}

export interface GalaxyDto {
  systems: GalaxySystemDto[];
  links: LinkDto[];
  /** Регионы (M11); нет — сервер их не знает. */
  regions?: RegionDto[] | null;
}

export interface PlayerDto {
  id: number;
  name: string;
  /** false — связи нет, корабль висит в космосе и ждёт игрока. */
  online: boolean;
  /** Дрон или другой NPC: о нём не пишем в ленту и не считаем в «онлайн». */
  npc?: boolean;
  /** Своя прочность и щит NPC вместо корпусных; нет — как у корпуса. */
  maxHp?: number;
  maxSh?: number;
  /** Вид NPC — от него цвет; у игроков нет. */
  kind?: NpcKind;
}

export type NpcKind = 'drone' | 'pirate' | 'trader' | 'ranger';

/** Весь список кораблей с именами — игроки и NPC; приходит при любом изменении. */
export interface PlayersMsg {
  t: 'players';
  /** Корабли этой системы. */
  players: PlayerDto[];
  /** Пилотов на связи во всей галактике. */
  total?: number;
}

export interface ConfigMsg {
  t: 'config';
  hulls: HullConfig;
  weapons: WeaponConfig;
  combat: CombatRules;
  npcs?: NpcRules;
  loot?: LootRules;
  meteors?: MeteorRules;
  shop?: ShopRules;
  system?: SystemDto;
  galaxy?: GalaxyDto;
  modules?: ModuleConfig | null;
  market?: MarketRules | null;
  /** Правила репутации (M13); нет — сервер без reputation.json, всё как до M13. */
  reputation?: ReputationRules | null;
}

/** Вход принят; приходит раньше welcome. */
export interface AccountMsg {
  t: 'account';
  /** Ник аккаунта так, как он записан на сервере. */
  name: string;
  /** Новый ключ устройства — только после входа по паролю: храним его вместо пароля. */
  key?: string;
}

/** Причина отказа во входе; следом сервер закрывает соединение. */
export type DeniedCode = 'badName' | 'badPassword' | 'wrongPassword' | 'badKey';

export interface DeniedMsg {
  t: 'denied';
  code: DeniedCode;
}

/** Ангар пилота (GDD §51): что куплено, что стоит на корабле, что на складе, в доке ли он. */
export interface HangarMsg {
  t: 'hangar';
  hull: string;
  /** Что стоит на корабле: пушки по слотам и модули. */
  fit: ShipFit;
  /** Свои корпуса; у гостя — все. */
  hulls: string[];
  /** Склад: пушки и модули, которые куплены или сняты и сейчас не стоят, — id и сколько. */
  storage: Record<string, number>;
  /** Корабль в доке: в космосе его нет, экран станции открыт. */
  docked: boolean;
  /** Прочность корпуса — в доке снапшот о своём корабле молчит. */
  hp: number;
  maxHp: number;
  /** Топливо и бак активного корпуса (GDD §6): меняется прыжком и заправкой — тогда hangar приходит снова. */
  fuel?: number;
  maxFuel?: number;
  /** Система последней стыковки: здесь корабль появится после гибели и после входа. */
  home?: string;
  /** Сколько энергии забирает оснащение и сколько даёт генератор (GDD §18); powerMax 0 — энергию не считают. */
  power?: number;
  powerMax?: number;
  /** Гость: склада нет, ставить можно что угодно где угодно. */
  guest?: boolean;
}


/**
 * Трюм (GDD §21) — только своему соединению: снапшот один на всех, личному месту в нём нет.
 * Приходит по событию (подбор, вход, смена корпуса, правка баланса, сдача груза), а не каждый тик.
 */
export interface CargoMsg {
  t: 'cargo';
  /** Занято объёма и ёмкость трюма текущего корпуса. */
  used: number;
  max: number;
  /** Что лежит: идентификатор предмета — количество. */
  items: Record<string, number>;
  /** Кредиты пилота. */
  credits?: number;
  /** Из занятого — груз доставки: его не продать и не выбросить. */
  reserved?: number;
}

/** Строка рынка станции (M12): название, объём и цвет редкости товара клиент берёт из loot.json. */
export interface MarketItemDto {
  id: string;
  /** Сколько пилот платит за штуку прямо сейчас. */
  buy: number;
  /** Сколько пилот получает за штуку. */
  sell: number;
  /** Запас станции, штук: от него и пляшет цена. */
  stock: number;
  /** Равновесный запас — по нему видно, здесь «мало» или «много». */
  norm: number;
}

/** Слух торговца (M12): куда везти товар или где его дёшево взять. Текст собираем сами (sim/market.ts). */
export interface RumourDto {
  /** route — брать здесь и везти туда; glut — там этого навалом и дёшево. */
  kind: 'route' | 'glut';
  good: string;
  system: string;
  /** Название той системы: на экране дока взять его больше неоткуда. */
  name: string;
  /** Сколько туда прыжков. */
  hops: number;
  /** Цена штуки там. */
  price: number;
  /** Сколько выходит с штуки при route. */
  profit?: number;
  /** Там этого сейчас мало — отсюда разговоры про эпидемию и голод. */
  scarce?: boolean;
}

/**
 * Живые цены станции (M12) — только тому, кто в доке. Приходит по событию: стыковка, сделка,
 * поставка торговца, возврат запасов к норме, правка баланса.
 */
export interface MarketMsg {
  t: 'market';
  system: string;
  items: MarketItemDto[];
  /** О чём судачит здешний торговец; считается на стыковке и дальше не меняется. */
  rumours?: RumourDto[];
}

/** Отношение к пилоту здесь и сейчас (M13); нет — в этой системе станции нет. */
export interface RepHereDto {
  /** Ключ станции, например «st:vega». */
  place: string;
  /** Очки станции. */
  value: number;
  /** Действующая ступень магазина: лучшее из станции и среднего по региону. Считает сервер. */
  level: string;
  /** Очки системы. */
  system: number;
  /** Ступень системы: по ней закрывается док и звереют рейнджеры. */
  systemLevel: string;
  /** Среднее по региону. */
  region: number;
}

/** Одна строка журнала репутации; текст собираем сами (ui/feed.ts). */
export interface RepChangeDto {
  /** Повод: «traderKill», «missionDone» и прочие. */
  code: string;
  delta: number;
  /** Чьё отношение: «sys:vega» или «st:vega». */
  key: string;
  /** Сколько стало. */
  value: number;
}

/**
 * Репутация пилота (M13) — только своему соединению. Отдельным сообщением, а не полем ангара:
 * цвет систем на карте и предупреждение «Враг» нужны и в полёте.
 */
export interface RepMsg {
  t: 'rep';
  /** Очки по системам, по id системы; только ненулевые. */
  systems: Record<string, number>;
  /** Очки по станциям, по ключу «st:<система>»; только ненулевые. */
  places: Record<string, number>;
  here?: RepHereDto | null;
  /** Что только что изменилось; нет — полное состояние без повода. */
  change?: RepChangeDto | null;
}

/** Вид задания (GDD §36; M14 добавил четыре последних). */
export type MissionKind = 'kill' | 'collect' | 'deliver' | 'escort' | 'patrol' | 'courier' | 'hunt';

/** Задание на доске или взятое. Текст собираем сами (sim/missions.ts). */
export interface MissionOffer {
  id: string;
  kind: MissionKind;
  /**
   * kill и hunt — где бить; deliver и courier — куда везти; escort — за какие врата уходит конвой;
   * patrol — своя же система; collect — нет: сдать можно на любой станции.
   */
  system?: string | null;
  /** kill: тип пирата из npcs.json; patrol: тип звена рейнджеров; нет — любой. */
  npc?: string | null;
  /** collect: предмет из loot.json. */
  item?: string | null;
  /** kill — пиратов, collect и deliver — единиц, hunt — камней, patrol — точек, escort — засад, courier — 1. */
  count: number;
  reward: number;
  /** Где выдали. */
  from: string;
  /** Особый контракт доски: только друзьям станции и платит больше обычного (M13). */
  elite?: boolean;
  /** hunt: какой размер камня засчитывается; нет — любой (M14). */
  size?: string | null;
  /** courier: сколько секунд дали на доставку (M14). */
  seconds?: number;
  /** escort: в каком радиусе держаться у конвоя; patrol: как близко подойти к точке (M14). */
  radius?: number;
}

/** Куда смотреть по живому заданию (M14): ship — идти за этим кораблём, 0 — к точке (x, y). */
export interface MissionMarkDto {
  ship: number;
  x: number;
  y: number;
}

/** Шаг обучения (GDD §54). id — что его засчитывает. */
export interface TutorialDto {
  step: number;
  total: number;
  id: 'undock' | 'drone' | 'grab' | 'sell' | 'jump';
  title: string;
  hint: string;
}

/** Обучение и задания пилота: по событию — вход, прыжок, прогресс, правка баланса. */
export interface MissionsMsg {
  t: 'missions';
  tutorial: TutorialDto | null;
  /** Взятое задание; progress у kill — сколько уничтожено, у collect — сколько такого в трюме. */
  active: { offer: MissionOffer; progress: number; until?: number } | null;
  /** Доска станции этой системы; без станции пусто. */
  offers: MissionOffer[];
  /** Что сделано этим событием — строка в ленте. */
  done?: {
    kind: 'tutorial' | 'mission' | 'failed';
    reward: number;
    title?: string | null;
    mission?: MissionOffer | null;
    /** Это был последний шаг обучения. */
    last?: boolean;
    /** Почему провалено: trader, away, wing, dead, left, time (M14). */
    reason?: string | null;
  } | null;
  /** Куда смотреть по живому заданию (M14); нет — метки нет. */
  mark?: MissionMarkDto | null;
}

/** Короткое уведомление по коду; текст подставляем у себя (ui/feed.ts). */
export interface NoticeMsg {
  t: 'notice';
  code: string;
}

/**
 * SOS торговца всем пилотам системы: on — на него напали (и потом раз в секунду — где он), saved — отбился или
 * долетел, lost — погиб. reward — кредиты этому пилоту за помощь, только в saved.
 */
export interface SosMsg {
  t: 'sos';
  id: number;
  name: string;
  x: number;
  y: number;
  state: 'on' | 'saved' | 'lost';
  reward: number;
}

/** Пилот from зовёт в группу; ответить — party accept/decline в течение seconds. */
export interface PartyInviteMsg {
  t: 'partyInvite';
  from: number;
  name: string;
  seconds: number;
}

/** Участник группы: где он и цел ли. Расстояние считаем сами, если он в той же системе. */
export interface PartyMemberDto {
  id: number;
  name: string;
  system: string;
  systemName: string;
  x: number;
  y: number;
  hp: number;
  maxHp: number;
  sh: number;
  maxSh: number;
  online: boolean;
  dead: boolean;
  docked: boolean;
}

/** Своя группа: при изменении и раз в секунду. Пустой список — не в группе. */
export interface PartyStateMsg {
  t: 'partyState';
  leader: number;
  members: PartyMemberDto[];
}

export type PartyEventCode =
  | 'invited'
  | 'joined'
  | 'left'
  | 'declined'
  | 'expired'
  | 'full'
  | 'busy'
  | 'gone'
  | 'disbanded';

/** Событие группы для ленты; name — о ком (у joined без name — «вы в группе»). */
export interface PartyEventMsg {
  t: 'partyEvent';
  code: PartyEventCode;
  name?: string | null;
}

/** Награда за голову пирата: amount — своя доля, shared — на скольких поделили. */
export interface BountyMsg {
  t: 'bounty';
  amount: number;
  shared: number;
  name: string;
}

export interface InvasionScoreDto {
  name: string;
  damage: number;
  reward: number;
}

/**
 * «Вторжение пиратов» (GDD §38) — всем в галактике, раз в секунду: announce — скоро (secondsLeft до начала),
 * wave — идёт (secondsLeft до конца, nextIn — до следующей волны), won/lost — итог с результатами и своей долей.
 */
export interface InvasionMsg {
  t: 'invasion';
  state: 'announce' | 'wave' | 'won' | 'lost';
  system: string;
  systemName: string;
  secondsLeft: number;
  wave: number;
  waves: number;
  remaining: number;
  nextIn: number;
  /** Точка сбора пиратов; 0, 0 — ещё не известна. */
  x: number;
  y: number;
  results?: InvasionScoreDto[];
  reward: number;
  damage: number;
}

export type ServerMessage =
  | WelcomeMsg
  | { t: 'pong'; c: number; tick: number }
  | PlayersMsg
  | ConfigMsg
  | SnapshotMsg
  | CargoMsg
  | NoticeMsg
  | AccountMsg
  | DeniedMsg
  | HangarMsg
  | MissionsMsg
  | SosMsg
  | PartyInviteMsg
  | PartyStateMsg
  | PartyEventMsg
  | BountyMsg
  | InvasionMsg
  | MarketMsg
  | RepMsg;
