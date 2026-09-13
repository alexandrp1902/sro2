import { normalizeServerUrl } from '../net/serverUrl';

/** Ввод адреса игрового сервера. Нужен на GitHub Pages: туда сервер не встроен. */
export class ConnectForm {
  private readonly input: HTMLInputElement;
  private readonly error: HTMLElement;

  constructor(private readonly root: HTMLElement) {
    this.input = root.querySelector('input')!;
    this.error = root.querySelector('.connect-error')!;

    root.querySelector('form')!.addEventListener('submit', (e) => {
      e.preventDefault();
      try {
        const params = new URLSearchParams(location.search);
        params.set('server', normalizeServerUrl(this.input.value));
        location.search = params.toString(); // перезагрузка: адрес подхватит resolveServerUrl
      } catch {
        this.error.textContent = 'Неверный адрес';
      }
    });
    root.querySelector('.connect-cancel')!.addEventListener('click', () => this.hide());
  }

  show(currentUrl: string | null): void {
    this.input.value = currentUrl ?? '';
    this.error.textContent = '';
    this.root.hidden = false;
  }

  hide(): void {
    this.root.hidden = true;
  }
}
