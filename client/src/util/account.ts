import { storage } from './storage';

const KEY_PREFIX = 'sro.key:';
const NAME_KEY = 'sro.name';

/**
 * Вход на этом устройстве (GDD §61). Пароль не хранится: после входа сервер выдаёт ключ устройства,
 * и переподключения — iOS усыпил вкладку, перезагрузка страницы — идут по нему.
 * Ключ свой у каждого сервера: аккаунты живут на сервере, и у другого сервера их просто нет.
 */
export const account = {
  key(serverUrl: string): string | null {
    return storage.get(KEY_PREFIX + serverUrl);
  },
  setKey(serverUrl: string, key: string): void {
    storage.set(KEY_PREFIX + serverUrl, key);
  },
  /** Выход: следующий вход — снова по паролю. */
  forget(serverUrl: string): void {
    storage.remove(KEY_PREFIX + serverUrl);
  },
  /** Последний ник — подставляется в форму входа. */
  name(): string {
    return storage.get(NAME_KEY) ?? '';
  },
  setName(name: string): void {
    storage.set(NAME_KEY, name);
  },
};
