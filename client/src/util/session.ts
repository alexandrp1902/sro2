const STORAGE_KEY = 'sro.session';
const TOKEN_BYTES = 16;

let current: string | null = null;

/**
 * Сессия вкладки: с ней после обрыва связи или перезагрузки игрок возвращается к своему кораблю.
 * sessionStorage, а не localStorage: две вкладки — два разных пилота. Если хранилище недоступно
 * (приватный режим), сессия живёт до перезагрузки.
 */
export function sessionToken(): string {
  current ??= read() ?? renewSession();
  return current;
}

/** Новый пилот в этой вкладке: нужен, когда вкладку продублировали вместе с sessionStorage. */
export function renewSession(): string {
  current = newToken();
  try {
    sessionStorage.setItem(STORAGE_KEY, current);
  } catch {
    // сессия просто не переживёт перезагрузку
  }
  return current;
}

/** crypto.randomUUID есть только в secure context, а iPhone по Wi-Fi открывает игру по http://<IP-ПК>. */
export function newToken(): string {
  const bytes = crypto.getRandomValues(new Uint8Array(TOKEN_BYTES));
  return Array.from(bytes, (b) => b.toString(16).padStart(2, '0')).join('');
}

function read(): string | null {
  try {
    return sessionStorage.getItem(STORAGE_KEY);
  } catch {
    return null;
  }
}
