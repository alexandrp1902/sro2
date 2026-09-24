import type { SpriteName } from './sprites';

type ShipSprite = Extract<SpriteName, `ships-${string}`>;
/** Центр среза сопла и его ширина в пикселях исходного спрайта, от левого верхнего угла. */
type Nozzle = readonly [x: number, y: number, width: number];

// Каждое сопло имеет собственную точку крепления: кормовые двигатели не всегда на одной линии.
export const SHIP_NOZZLES: Record<ShipSprite, readonly Nozzle[]> = {
  'ships-light': [[41, 217, 27], [137, 217, 27]],
  'ships-medium': [[61, 220, 27], [111, 220, 27]],
  'ships-heavy': [[67, 207, 25], [100, 211, 21], [134, 207, 25]],
  'ships-pirate': [[92, 216, 29], [160, 216, 29]],
  'ships-scout': [[25, 248, 16], [74, 248, 16]],
  'ships-interceptor': [[80, 240, 26], [164, 240, 26]],
  'ships-industrial': [[53, 250, 21], [110, 250, 21]],
  'ships-frigate': [[33, 248, 19], [93, 248, 19]],
  'ships-freighter': [[62, 243, 24], [103, 251, 28], [144, 243, 24]],
  'ships-cruiser': [[40, 246, 16], [62, 250, 16], [98, 250, 16], [120, 246, 16]],
  'ships-starterTrader': [[18, 217, 23], [80, 248, 19], [133, 248, 19], [195, 217, 23]],
  'ships-ranger': [[82, 247, 25], [155, 247, 25]],
  'ships-ranger-heavy': [[32, 249, 20], [95, 249, 20]],
  'ships-trader-hauler': [[30, 246, 24], [139, 246, 24]],
  'ships-trader-convoy': [[19, 248, 15], [41, 251, 16], [84, 251, 16], [106, 248, 15]],
  'ships-pirate-raider': [[85, 249, 20], [144, 249, 20]],
  'ships-pirate-brute': [[62, 247, 24], [128, 247, 24]],
  'ships-pirate-flagship': [[58, 238, 20], [95, 238, 18], [133, 238, 20]],
  'ships-drone': [[127, 248, 24]],
  // Флот пачки J (M19): точки сняты с нарезанных спрайтов по кормовым соплам.
  'ships-needle': [[39, 250, 30]],
  'ships-tug': [[78, 248, 30], [114, 248, 30]],
  'ships-surveyor': [[90, 230, 20], [157, 230, 20]],
  'ships-corsair': [[97, 198, 16], [120, 198, 16], [144, 198, 16]],
  'ships-clipper': [[92, 244, 14], [110, 244, 14], [127, 244, 14], [145, 244, 14]],
  'ships-runner': [[24, 242, 11], [41, 248, 13], [55, 252, 16], [69, 248, 13], [85, 242, 11]],
  'ships-lancer': [[121, 202, 22]],
  'ships-dropship': [[21, 240, 24], [128, 246, 34], [231, 240, 24]],
  'ships-galleon': [[15, 244, 14], [40, 250, 24], [81, 250, 24], [105, 244, 14]],
};

export function shipNozzles(sprite: SpriteName): readonly Nozzle[] {
  return SHIP_NOZZLES[sprite as ShipSprite] ?? [];
}
