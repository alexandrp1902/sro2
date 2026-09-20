// Сообщения сервера для смоук-скриптов: JSON-текст или бинарный дельта-снапшот (M7). Снапшот собирает тот же
// декодер, что и в игре (client/src/net/snapshotCodec.ts), — Node 24 читает TypeScript сам.
import { SnapshotDecoder } from '../client/src/net/snapshotCodec.ts';

// Версия протокола берётся из клиента, а не вписывается в смоук руками: иначе она устаревает
// каждый этап, и падение смоука перестаёт что-либо значить.
export { PROTOCOL_VERSION } from '../client/src/net/protocol.ts';

/**
 * Вылет сразу после входа: с M15.6 вход всегда в доке (Room.Enter), а смоукам нужен корабль в космосе.
 * Клиент у каждого скрипта свой, поэтому здесь используются только send/until/hangar — они есть у всех.
 */
export async function undock(client, name = 'ship') {
  client.send({ t: 'dock', on: false });
  // Ждать подтверждения можно только там, где скрипт слушает hangar: у части смоуков его нет вовсе,
  // и им довольно того, что дальше они сами ждут свой корабль в снапшоте.
  if ('hangar' in client) await client.until(() => client.hangar && !client.hangar.docked, 3000, `${name} out of the dock`);
}

/** WebSocket, который отдаёт бинарные кадры как ArrayBuffer, и функция чтения его сообщений. */
export function openSocket(url) {
  const ws = new WebSocket(url);
  ws.binaryType = 'arraybuffer';
  const decoder = new SnapshotDecoder();
  const read = (data) => (typeof data === 'string' ? JSON.parse(data) : decoder.decode(new Uint8Array(data)));
  return { ws, read };
}

/**
 * Где станция системы в тик tick: она ходит по орбите вокруг звезды. Та же формула, что в client/src/sim/orbits.ts
 * и Sro.Sim/Orbits.cs (сюда её не импортировать: TS-модуль клиента тянет другие без расширений).
 */
export function stationAt(system, tick) {
  return orbitAt(system, system?.stationOrbit, tick);
}

/** Где тело на этой орбите в тик tick: станция, планета — формула одна (M15). */
export function orbitAt(system, orbit, tick) {
  if (!orbit || orbit.radius <= 0) return { x: 0, y: 0 };
  const seconds = (system?.orbitEpoch ?? 0) + tick / 20;
  const turns = seconds / (orbit.periodMinutes * 60);
  const angle = (orbit.phase * Math.PI) / 180 + (turns - Math.floor(turns)) * 2 * Math.PI;
  return { x: orbit.radius * Math.cos(angle), y: orbit.radius * Math.sin(angle) };
}

/** Планета системы с поселением по ключу места («pl:terra»); undefined — такой тут нет. */
export function settlement(system, key) {
  return (system?.planets ?? []).find((p) => p.id && `pl:${p.id}` === key);
}

/**
 * Куда лететь из me к target, не заходя в жар звезды: если прямая проходит ближе burnRadius + запас от центра,
 * сначала — точка сбоку от звезды, с той стороны, где корабль.
 */
export function aroundSun(system, me, target, margin = 350) {
  const sun = system?.sun;
  if (!sun) return target;
  const safe = sun.burnRadius + margin;
  const dx = target.x - me.x;
  const dy = target.y - me.y;
  const length = Math.hypot(dx, dy);
  if (length < 1) return target;
  // Ближайшая к центру точка отрезка me → target.
  const t = Math.max(0, Math.min(1, -(me.x * dx + me.y * dy) / (length * length)));
  const cx = me.x + dx * t;
  const cy = me.y + dy * t;
  if (Math.hypot(cx, cy) >= safe) return target;
  // Нормаль к пути, в сторону корабля от центра (или любую, если путь идёт ровно через центр).
  let nx = -dy / length;
  let ny = dx / length;
  if (nx * me.x + ny * me.y < 0) {
    nx = -nx;
    ny = -ny;
  }
  return { x: nx * safe * 1.2, y: ny * safe * 1.2 };
}
