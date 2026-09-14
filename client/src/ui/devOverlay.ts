import { LAG_PRESETS, type FakeLag } from '../net/fakeLag';
import type { Hulls } from '../sim/hulls';
import type { Weapons } from '../sim/weapons';
import { storage } from '../util/storage';

const STORAGE_KEY = 'sro.dev';
const RENDER_INTERVAL_MS = 250;

export interface DevInfo {
  tick: number;
  tickRate: number;
  pingMs: number;
  speed: number;
  forward: number;
  lateral: number;
  throttle: number;
  desiredDeg: number;
  headingDeg: number;
  pending: number;
  correction: number;
  peakCorrection: number;
  snaps: number;
  online: boolean;
  hullId: string;
  weaponId: string;
  /** Задержка интерполяции чужих кораблей и джиттер снапшотов, мс. */
  interpMs: number;
  jitterMs: number;
  extrapolations: number;
  /** Свои выстрелы, попадания и сумма шансов, по которым бросал сервер. */
  shots: number;
  hits: number;
  chanceSum: number;
}

/** Dev-панель плейтеста: `` ` `` (Ё) или тап по строке полёта. */
export class DevOverlay {
  private readonly stats: HTMLElement;
  private readonly hullRow: HTMLElement;
  private readonly weaponRow: HTMLElement;
  private readonly lagRow: HTMLElement;
  private lastRender = 0;

  constructor(
    private readonly root: HTMLElement,
    private readonly hulls: Hulls,
    private readonly onHull: (id: string) => void,
    private readonly lag: FakeLag | null,
    private readonly weapons: Weapons,
    private readonly onWeapon: (id: string) => void,
  ) {
    this.stats = document.createElement('pre');
    this.hullRow = document.createElement('div');
    this.weaponRow = document.createElement('div');
    this.lagRow = document.createElement('div');
    this.hullRow.className = this.weaponRow.className = this.lagRow.className = 'dev-row';
    root.append(this.stats, this.hullRow, this.weaponRow, this.lagRow);

    root.hidden = storage.get(STORAGE_KEY) !== '1';
    window.addEventListener('keydown', (e) => {
      if (e.code === 'Backquote' && !(e.target instanceof HTMLInputElement)) this.toggle();
    });
    this.renderButtons('', '');
  }

  toggle(): void {
    this.root.hidden = !this.root.hidden;
    storage.set(STORAGE_KEY, this.root.hidden ? '0' : '1');
  }

  update(info: DevInfo): void {
    const now = performance.now();
    if (this.root.hidden || now - this.lastRender < RENDER_INTERVAL_MS) return;
    this.lastRender = now;

    const net = info.online
      ? [
          `тик ${info.tick} · ${info.tickRate.toFixed(1)}/с · пинг ${Math.round(info.pingMs)} мс`,
          `чужие: интерп. ${Math.round(info.interpMs)} мс · джиттер ${Math.round(info.jitterMs)} мс · экстраполяций ${info.extrapolations}`,
        ]
      : ['нет сверки с сервером'];
    const fire =
      info.shots > 0
        ? `огонь: выстрелов ${info.shots} · попаданий ${info.hits} (${Math.round((100 * info.hits) / info.shots)}%) · ожидали ${Math.round(info.chanceSum / info.shots)}%`
        : 'огонь: выстрелов нет';
    this.stats.textContent = [
      ...net,
      `скорость ${Math.round(info.speed)} (вперёд ${Math.round(info.forward)}, бок ${Math.round(info.lateral)})`,
      `тяга ${Math.round(info.throttle * 100)}% · курс ${Math.round(info.headingDeg)}° → ${Math.round(info.desiredDeg)}°`,
      `входов в пути ${info.pending} · коррекция ${info.correction.toFixed(2)} (пик ${info.peakCorrection.toFixed(2)}) · щелчков ${info.snaps}`,
      fire,
    ].join('\n');
    this.renderButtons(info.hullId, info.weaponId);
  }

  private renderButtons(hullId: string, weaponId: string): void {
    const key = `${hullId}|${this.hulls.ids().join(',')}|${weaponId}|${this.weapons.ids().join(',')}|${this.lag?.rttMs ?? '-'}`;
    if (this.root.dataset.buttons === key) return;
    this.root.dataset.buttons = key;

    this.hullRow.replaceChildren(
      ...this.hulls.ids().map((id) => button(this.hulls.get(id).name, id === hullId, () => this.onHull(id))),
    );
    this.weaponRow.replaceChildren(
      ...this.weapons.ids().map((id) => button(this.weapons.get(id).name, id === weaponId, () => this.onWeapon(id))),
    );

    const lag = this.lag;
    if (!lag) {
      this.lagRow.replaceChildren();
      return;
    }
    this.lagRow.replaceChildren(
      label('лаг'),
      ...LAG_PRESETS.map((ms) =>
        button(ms === 0 ? 'нет' : `${ms} мс`, lag.rttMs === ms, () => {
          lag.rttMs = ms;
          this.root.dataset.buttons = ''; // перерисовать выбранную кнопку
        }),
      ),
    );
  }
}

function button(text: string, pressed: boolean, onClick: () => void): HTMLButtonElement {
  const b = document.createElement('button');
  b.type = 'button';
  b.textContent = text;
  b.setAttribute('aria-pressed', String(pressed));
  b.addEventListener('click', () => {
    b.blur(); // иначе Space (атака) «нажимал» бы кнопку в фокусе
    onClick();
  });
  return b;
}

function label(text: string): HTMLSpanElement {
  const span = document.createElement('span');
  span.textContent = text;
  return span;
}
