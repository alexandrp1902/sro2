import defaults from '../../../shared/weapons.json';
import type { WeaponConfig, WeaponParams } from './combat';

/** Пушка нового игрока. Зеркало SimConfig.DefaultWeapon на сервере. */
export const DEFAULT_WEAPON = 'pulse';

/** Параметры пушек. Встроенные значения из shared/weapons.json заменяются присланными сервером. */
export class Weapons {
  /** Весь каталог — для расчёта энергии и проверки слотов (sim/fitting.ts). */
  config: WeaponConfig = defaults as WeaponConfig;

  set(config: WeaponConfig): void {
    if (Object.keys(config).length > 0) this.config = config;
  }

  get(id: string): WeaponParams {
    return this.config[id] ?? this.config[DEFAULT_WEAPON] ?? Object.values(this.config)[0];
  }

  has(id: string): boolean {
    return id in this.config;
  }

  ids(): string[] {
    return Object.keys(this.config);
  }
}
