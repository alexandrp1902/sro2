import type { RosterEvent } from '../net/roster';

const SHOW_MS = 4000;
const FADE_MS = 400;
const MAX_ITEMS = 4;

/** Настоящее время — без рода: «Рейнджер-123 входит в систему». */
const VERBS = {
  joined: 'входит в систему',
  lost: 'теряет связь',
  back: 'снова на связи',
  left: 'покидает систему',
} as const;

export function describe(event: RosterEvent): string {
  return event.kind === 'renamed' ? `${event.from} теперь ${event.name}` : `${event.name} ${VERBS[event.kind]}`;
}

export function describeKill(killer: string, victim: string): string {
  return `${killer} уничтожает ${victim}`;
}

/** Лента событий системы вверху экрана: сообщения живут несколько секунд. */
export class Feed {
  constructor(private readonly root: HTMLElement) {}

  push(events: RosterEvent[]): void {
    for (const event of events) this.add(describe(event));
  }

  add(text: string): void {
    const item = document.createElement('div');
    item.className = 'feed-item';
    item.textContent = text;
    this.root.append(item);
    while (this.root.children.length > MAX_ITEMS) this.root.firstElementChild!.remove();

    window.setTimeout(() => {
      item.classList.add('feed-out');
      window.setTimeout(() => item.remove(), FADE_MS);
    }, SHOW_MS);
  }
}
