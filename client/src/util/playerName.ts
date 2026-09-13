import { storage } from './storage';

const STORAGE_KEY = 'sro.name';

/** Временное имя до появления аккаунтов (M6): генерируется один раз на устройство. */
export function playerName(): string {
  let name = storage.get(STORAGE_KEY);
  if (!name) {
    name = `Рейнджер-${Math.floor(100 + Math.random() * 900)}`;
    storage.set(STORAGE_KEY, name);
  }
  return name;
}
