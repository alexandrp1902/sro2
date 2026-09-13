import { normalizeServerUrl } from '../net/serverUrl';

/**
 * Окно «Пилот» (тап по строке статуса): ник меняется на лету, без переподключения;
 * адрес игрового сервера нужен на GitHub Pages — туда сервер не встроен.
 */
export class PilotForm {
  private readonly nameInput: HTMLInputElement;
  private readonly serverInput: HTMLInputElement;
  private readonly error: HTMLElement;
  private currentUrl: string | null = null;

  constructor(
    private readonly root: HTMLElement,
    onRename: (name: string) => void,
  ) {
    this.nameInput = root.querySelector('input[name=name]')!;
    this.serverInput = root.querySelector('input[name=server]')!;
    this.error = root.querySelector('.connect-error')!;

    root.querySelector('form')!.addEventListener('submit', (e) => {
      e.preventDefault();
      const name = this.nameInput.value.trim();
      if (name) onRename(name);

      const server = this.serverInput.value.trim();
      let url: string | null = null;
      try {
        url = server ? normalizeServerUrl(server) : null;
      } catch {
        this.error.textContent = 'Неверный адрес сервера';
        return;
      }
      if (url === null || url === this.currentUrl) {
        this.hide();
        return;
      }
      const params = new URLSearchParams(location.search);
      params.set('server', url);
      location.search = params.toString(); // перезагрузка: адрес подхватит resolveServerUrl
    });
    root.querySelector('.connect-cancel')!.addEventListener('click', () => this.hide());
  }

  show(currentUrl: string | null, name: string): void {
    this.currentUrl = currentUrl;
    this.nameInput.value = name;
    this.serverInput.value = currentUrl ?? '';
    this.error.textContent = '';
    this.root.hidden = false;
  }

  hide(): void {
    this.root.hidden = true;
    // Фокус в поле ввода глушит клавиши управления (keyboard.ts).
    if (document.activeElement instanceof HTMLElement) document.activeElement.blur();
  }
}
