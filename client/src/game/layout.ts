// Раскладка тестовой системы M1 (только визуал, без столкновений). Точка спауна совпадает с сервером.

export const SPAWN = { x: 0, y: 420 };
export const STATION = { x: 0, y: 0, radius: 90 };
/** Круг для проверки точной остановки. */
export const PARKING = { x: 0, y: 200, radius: 40 };
/** Слалом из буёв справа от станции. */
export const BUOYS = Array.from({ length: 7 }, (_, i) => ({ x: 900 + (i % 2 === 0 ? -170 : 170), y: 800 - i * 330 }));
