import defaults from '../../../shared/hulls.json';
import type { HullConfig, HullParams } from './movement';

export const DEFAULT_HULL = 'light';

/**
 * Параметры корпусов. Встроенные значения из shared/hulls.json нужны только для полёта без сервера:
 * при подключении их заменяет то, что прислал сервер, иначе предсказание расходилось бы с ним.
 */
export class Hulls {
  private config: HullConfig = defaults as HullConfig;

  set(config: HullConfig): void {
    if (Object.keys(config).length > 0) this.config = config;
  }

  get(id: string): HullParams {
    return this.config[id] ?? this.config[DEFAULT_HULL] ?? Object.values(this.config)[0];
  }

  has(id: string): boolean {
    return id in this.config;
  }

  ids(): string[] {
    return Object.keys(this.config);
  }
}
