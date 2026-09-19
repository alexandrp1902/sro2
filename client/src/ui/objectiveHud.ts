import { keyHint, keymap } from '../input/keymap';

/**
 * Трекер цели (GDD §36, §54): шаг обучения или взятое задание — строка «что сделать» и подсказка «как».
 * Справа под миникартой. Тап открывает карту галактики: там отмечена система цели.
 */
export class ObjectiveHud {
  private key = '';
  private readonly title: HTMLElement;
  private readonly hint: HTMLElement;

  constructor(
    private readonly root: HTMLElement,
    onTap: () => void,
  ) {
    this.title = document.createElement('div');
    this.title.className = 'objective-title';
    this.hint = document.createElement('div');
    this.hint.className = 'objective-hint';
    root.append(this.title, this.hint);
    root.addEventListener('click', onTap);
  }

  /** null — прятать: заданий нет, в доке или нет связи. */
  update(lines: { title: string; hint: string } | null): void {
    // В подсказках обучения — клавиши из раскладки игрока, а не стандартные.
    if (lines) lines = { title: lines.title, hint: keyHint(lines.hint, keymap) };
    const key = lines ? `${lines.title}\n${lines.hint}` : '';
    if (key === this.key) return;
    this.key = key;
    this.root.hidden = !lines;
    if (!lines) return;
    this.title.textContent = lines.title;
    this.hint.textContent = lines.hint;
    this.hint.hidden = !lines.hint;
  }
}
