// Зеркало server/Sro.Server/Net/Protocol.cs. Поле t — тип сообщения.

import type { HullConfig } from '../sim/movement';

export type ClientMessage =
  | { t: 'hello'; name: string; hull: string }
  | { t: 'ping'; c: number }
  /** Только управление (§49): направление на экране и тяга; координаты клиент не присылает. */
  | { t: 'input'; seq: number; dx: number; dy: number; th: number }
  | { t: 'hull'; id: string };

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
  id: number;
  tickRate: number;
  hulls: HullConfig;
}

export type ServerMessage =
  | WelcomeMsg
  | { t: 'pong'; c: number; tick: number }
  | { t: 'online'; count: number }
  | { t: 'config'; hulls: HullConfig }
  | SnapshotMsg;
