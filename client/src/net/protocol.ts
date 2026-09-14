// Зеркало server/Sro.Server/Net/Protocol.cs. Поле t — тип сообщения.

import type { CombatRules, WeaponConfig } from '../sim/combat';
import type { HullConfig } from '../sim/movement';

/** Версия протокола; зеркало Protocol.Version на сервере. Сервер другой версии (или старый, без поля) — не играем. */
export const PROTOCOL_VERSION = 3;

export type ClientMessage =
  /** token — сессия вкладки: с ней после обрыва связи игрок возвращается к своему кораблю. */
  | { t: 'hello'; name: string; hull: string; weapon: string; token: string }
  | { t: 'ping'; c: number }
  /** Только управление (§49): направление на экране и тяга; координаты клиент не присылает. */
  | { t: 'input'; seq: number; dx: number; dy: number; th: number }
  | { t: 'hull'; id: string }
  | { t: 'weapon'; id: string }
  | { t: 'name'; name: string }
  /** Выбранная цель (GDD §9); 0 — цели нет. */
  | { t: 'target'; id: number }
  /** Атака нажата или отпущена: пока нажата, пушка стреляет сама по готовности (GDD §47). */
  | { t: 'fire'; on: boolean };

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

export interface SnapshotMsg {
  t: 'snapshot';
  tick: number;
  ships: ShipDto[];
  /** Выстрелы и уничтожения этого тика; нет — поля нет. */
  shots?: ShotDto[];
  kills?: KillDto[];
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
}

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
}

export type ServerMessage = WelcomeMsg | { t: 'pong'; c: number; tick: number } | PlayersMsg | ConfigMsg | SnapshotMsg;
