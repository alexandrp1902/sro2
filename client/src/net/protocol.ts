// Зеркало server/Sro.Server/Net/Protocol.cs. Поле t — тип сообщения.

export type ClientMessage =
  | { t: 'hello'; name: string }
  | { t: 'ping'; c: number };

export type ServerMessage =
  | { t: 'welcome'; id: number; tickRate: number }
  | { t: 'pong'; c: number; tick: number }
  | { t: 'online'; count: number };
