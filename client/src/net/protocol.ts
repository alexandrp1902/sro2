// Зеркало server/Sro.Server/Net/Protocol.cs. Поле t — тип сообщения.

import type { HullConfig } from '../sim/movement';

export type ClientMessage =
  /** token — сессия вкладки: с ней после обрыва связи игрок возвращается к своему кораблю. */
  | { t: 'hello'; name: string; hull: string; token: string }
  | { t: 'ping'; c: number }
  /** Только управление (§49): направление на экране и тяга; координаты клиент не присылает. */
  | { t: 'input'; seq: number; dx: number; dy: number; th: number }
  | { t: 'hull'; id: string }
  | { t: 'name'; name: string };

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
}

export interface SnapshotMsg {
  t: 'snapshot';
  tick: number;
  ships: ShipDto[];
}

export interface WelcomeMsg {
  t: 'welcome';
  /** Id своего корабля в снапшотах. */
  id: number;
  tickRate: number;
  hulls: HullConfig;
  /** Вернулись к кораблю, который ждал нас после обрыва связи. */
  resumed: boolean;
}

export interface PlayerDto {
  id: number;
  name: string;
  /** false — связи нет, корабль висит в космосе и ждёт игрока. */
  online: boolean;
}

/** Весь список игроков системы; приходит при любом изменении. */
export interface PlayersMsg {
  t: 'players';
  players: PlayerDto[];
}

export type ServerMessage =
  | WelcomeMsg
  | { t: 'pong'; c: number; tick: number }
  | PlayersMsg
  | { t: 'config'; hulls: HullConfig }
  | SnapshotMsg;
