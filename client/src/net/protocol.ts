// Зеркало server/Sro.Server/Net/Protocol.cs. Поле t — тип сообщения.

import type { CombatRules, WeaponConfig } from '../sim/combat';
import type { ModuleConfig, ShipFit } from '../sim/fitting';
import type { LootRules } from '../sim/loot';
import type { MeteorRules } from '../sim/meteors';
import type { HullConfig } from '../sim/movement';
import type { NpcRules } from '../sim/npcs';
import type { ShopRules } from '../sim/shop';

/** Версия протокола; зеркало Protocol.Version на сервере. Сервер другой версии (или старый, без поля) — не играем. */
export const PROTOCOL_VERSION = 12;

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
  /** Продать груз в доке; item — что именно, без него — весь трюм. */
  | { t: 'sell'; item?: string }
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
  | { t: 'mission'; action: MissionAction; id?: string };

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

/** Вид задания (GDD §36). */
export type MissionKind = 'kill' | 'collect' | 'deliver';

/** Задание на доске или взятое. Текст собираем сами (sim/missions.ts). */
export interface MissionOffer {
  id: string;
  kind: MissionKind;
  /** kill — где бить; deliver — куда везти; collect — нет: сдать можно на любой станции. */
  system?: string | null;
  /** kill: тип пирата из npcs.json; нет — любой. */
  npc?: string | null;
  /** collect: предмет из loot.json. */
  item?: string | null;
  count: number;
  reward: number;
  /** Где выдали. */
  from: string;
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
  active: { offer: MissionOffer; progress: number } | null;
  /** Доска станции этой системы; без станции пусто. */
  offers: MissionOffer[];
  /** Что сделано этим событием — строка в ленте. */
  done?: {
    kind: 'tutorial' | 'mission';
    reward: number;
    title?: string | null;
    mission?: MissionOffer | null;
    /** Это был последний шаг обучения. */
    last?: boolean;
  } | null;
}

/** Короткое уведомление по коду; текст подставляем у себя (ui/feed.ts). */
export interface NoticeMsg {
  t: 'notice';
  code: string;
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
  | MissionsMsg;
