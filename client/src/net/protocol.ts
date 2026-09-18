// Зеркало server/Sro.Server/Net/Protocol.cs. Поле t — тип сообщения.

import type { CombatRules, WeaponConfig } from '../sim/combat';
import type { LootRules } from '../sim/loot';
import type { MeteorRules } from '../sim/meteors';
import type { HullConfig } from '../sim/movement';
import type { NpcRules } from '../sim/npcs';
import type { ShopRules } from '../sim/shop';

/** Версия протокола; зеркало Protocol.Version на сервере. Сервер другой версии (или старый, без поля) — не играем. */
export const PROTOCOL_VERSION = 8;

/** Состояние ИИ пирата: патруль, бой, возврат в логово. */
export type AiState = 'patrol' | 'attack' | 'return';

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
  /** Купить в доке корпус или пушку; купленное сразу ставится на корабль. */
  | { t: 'buy'; kind: BuyKind; id: string }
  /** Починить корпус и зарядить щит в доке. */
  | { t: 'repair' };

/** Что покупают в доке. */
export type BuyKind = 'hull' | 'weapon';

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
  /** Пушка. */
  w: string;
  /** Уничтожен и появится в этот тик; нет поля — цел. */
  rt?: number;
  /** Под защитой до этого тика; нет поля — без защиты. */
  pu?: number;
  /** Цель пирата в бою; нет поля — нет (и у игроков). */
  tg?: number;
  /** Состояние ИИ пирата; у игроков нет. */
  ai?: AiState;
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

export type NpcKind = 'drone' | 'pirate';

/** Весь список кораблей с именами — игроки и NPC; приходит при любом изменении. */
export interface PlayersMsg {
  t: 'players';
  players: PlayerDto[];
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

/** Ангар пилота (GDD §51): что куплено, что стоит на корабле, в доке ли он. */
export interface HangarMsg {
  t: 'hangar';
  hull: string;
  weapon: string;
  /** Свои корпуса и пушки; у гостя — все. */
  hulls: string[];
  weapons: string[];
  /** Корабль в доке: в космосе его нет, экран станции открыт. */
  docked: boolean;
  /** Прочность корпуса — в доке снапшот о своём корабле молчит. */
  hp: number;
  maxHp: number;
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
  | HangarMsg;
