import { storage } from '../util/storage';

const STORAGE_KEY = 'sro.server';

/**
 * Адрес игрового сервера: ?server=… → сохранённый ранее → тот же хост, что и страница
 * (игровой сервер по Wi-Fi, Vite-прокси, tunnel). На GitHub Pages без адреса возвращает null.
 */
export function resolveServerUrl(): string | null {
  const fromQuery = new URLSearchParams(location.search).get('server');
  if (fromQuery) {
    try {
      const url = normalizeServerUrl(fromQuery);
      storage.set(STORAGE_KEY, url);
      return url;
    } catch {
      return null;
    }
  }

  const saved = storage.get(STORAGE_KEY);
  if (saved) return saved;

  if (location.hostname.endsWith('github.io')) return null;
  return `${location.protocol === 'https:' ? 'wss' : 'ws'}://${location.host}/ws`;
}

/** «abc.trycloudflare.com», «https://abc.trycloudflare.com», «192.168.1.5:5000» → ws(s)://…/ws */
export function normalizeServerUrl(raw: string): string {
  let url = raw.trim();
  if (!/^[a-z]+:\/\//i.test(url)) url = (isLocalAddress(url) ? 'ws://' : 'wss://') + url;
  url = url.replace(/^http:/i, 'ws:').replace(/^https:/i, 'wss:');

  const parsed = new URL(url);
  if (parsed.pathname === '/') parsed.pathname = '/ws';
  return parsed.toString();
}

function isLocalAddress(hostAndPort: string): boolean {
  return /^(localhost|\d{1,3}(\.\d{1,3}){3})(:\d+)?(\/|$)/.test(hostAndPort);
}
