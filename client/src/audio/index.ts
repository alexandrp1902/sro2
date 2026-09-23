import type { ShotDto } from '../net/protocol';
import type { WeaponParams } from '../sim/combat';
import { SfxBank } from './bank';
import { flightMs, impactVoice, killVoice, launchVoice, shotVoice, type SfxCue } from './cues';
import { AudioEngine } from './engine';
import { spatial, type Listener, type Place } from './mixer';
import { Music } from './music';
import { nextMood, type Mood } from './mood';
import { AudioSettings } from './settings';
import { Sfx } from './sfx';

/**
 * Звук игры одним лицом: главный модуль зовёт только его и ничего не знает ни про Web Audio, ни про слои.
 *
 * Если контекст создать не удалось (старый браузер, запрет) — молчим, но игра работает: звук нигде
 * не должен быть причиной падения.
 */

export interface AudioFrame {
  now: number;
  /** Слушатель — это камера: игрок слышит оттуда, откуда смотрит. */
  camera: Place & { zoom: number };
  /** Сколько враждебных кораблей держат меня целью. */
  threats: number;
  /** Сколько ракет летит в меня. */
  incoming: number;
  docked: boolean;
  dead: boolean;
}

/** Звуки «в рубке»: у них нет места в мире, они звучат ровно посередине. */
export type OwnCue = 'dock' | 'undock' | 'jump' | 'fire-on' | 'fire-off' | 'lock' | 'respawn';

/** Реже этого тревога о ракете не повторяется. */
const ALARM_EVERY_MS = 1400;

export class GameAudio {
  readonly settings = new AudioSettings();

  private readonly engine: AudioEngine | null;
  private readonly bank = new SfxBank();
  private readonly sfx: Sfx | null;
  private readonly music: Music | null;

  private listener: Listener = { x: 0, y: 0, zoom: 1, halfWidth: 640 };
  private mood: Mood = 'calm';
  private lastHostileShot = 0;
  private lastOwnShot = 0;
  private alarmAt = 0;
  private charge: number | null = null;
  private readonly pending = new Set<number>();

  constructor() {
    let engine: AudioEngine | null = null;
    try {
      engine = new AudioEngine(this.settings.prefs);
    } catch {
      engine = null;
    }
    this.engine = engine;
    this.sfx = engine ? new Sfx(engine, this.bank) : null;
    this.music = engine ? new Music(engine, this.settings.prefs.combat) : null;
    this.settings.onChange((prefs) => {
      engine?.applyPrefs(prefs);
      this.music?.setTheme(prefs.combat);
      this.ensureMusic(); // музыку прибавили после того, как выключили, — теперь её надо скачать
    });
  }

  /** Скачать банк, потом музыку: выстрел нужен раньше саундтрека. Жеста для этого не требуется. */
  prepare(): void {
    if (!this.engine) return;
    void this.bank.load(this.engine.ctx).then(() => this.ensureMusic());
  }

  /** Первый жест пользователя: вход в игру, тап по стику, нажатие клавиши. */
  unlock(): void {
    this.engine?.unlock();
    this.ensureMusic();
  }

  /** Раз в кадр: где слушатель, что вокруг, что играет. */
  frame(f: AudioFrame): void {
    if (!this.engine) return;
    this.listener = { x: f.camera.x, y: f.camera.y, zoom: f.camera.zoom, halfWidth: window.innerWidth / 2 };

    const mood = nextMood({
      now: f.now,
      lastHostileShot: this.lastHostileShot,
      lastOwnShot: this.lastOwnShot,
      threats: f.threats,
      incoming: f.incoming,
      docked: f.docked,
      dead: f.dead,
    });
    if (mood !== this.mood) {
      this.mood = mood;
      this.music?.setMood(mood);
    }

    if (f.incoming > 0 && f.now - this.alarmAt > ALARM_EVERY_MS) {
      this.alarmAt = f.now;
      this.cue('alarm', null, 0, f.now);
    }
  }

  /**
   * Выстрел из снапшота. Звук ствола звучит сразу, удар — когда снаряд долетел: на дальней дистанции
   * иначе слышно раньше, чем видно.
   *
   * @param vsShip стреляли по кораблю, а не по метеориту: только это считается боем
   */
  shot(
    shot: ShotDto,
    weapon: WeaponParams | null,
    from: Place | null,
    to: Place | null,
    ownId: number,
    now: number,
    vsShip: boolean,
  ): void {
    if (!this.sfx) return;
    const mine = shot.from === ownId;
    const atMe = shot.to === ownId;
    if (vsShip && mine) this.lastOwnShot = now;
    if (atMe) this.lastHostileShot = now;

    const voice = shotVoice(shot.w, weapon, mine);
    this.sfx.play(voice, this.at(from ?? to), shot.from, now);

    const impact = impactVoice(shot, atMe);
    if (!impact || !to) return;
    const delay = flightMs(shot.w, weapon);
    if (delay <= 0) {
      this.sfx.play(impact, this.at(to), shot.to, now);
      return;
    }
    // Точка удара берётся на момент выстрела: корабль за 150 мс далеко не улетит, а следить за ним
    // ради звука — лишняя работа в каждом кадре.
    const timer = window.setTimeout(() => {
      this.pending.delete(timer);
      this.sfx?.play(impact, this.at(to), shot.to, performance.now());
    }, delay);
    this.pending.add(timer);
  }

  /** Корабль уничтожен. */
  kill(at: Place | null, size: number, own: boolean, now: number): void {
    this.sfx?.play(killVoice(size, own), this.at(at), 0, now);
  }

  /** Пуск ракеты: новая ракета в снапшоте. */
  launch(weapon: WeaponParams | null, at: Place | null, source: number, mine: boolean, now: number): void {
    this.sfx?.play(launchVoice(weapon, mine), this.at(at), source, now);
  }

  /** Предмет ушёл в трюм. */
  pick(at: Place | null, now: number): void {
    this.cue('loot', at, 0, now);
  }

  /** Событие своего корабля без места в мире. */
  own(cue: OwnCue, now = performance.now()): void {
    this.cue(cue, null, 0, now);
  }

  /**
   * Накопление прыжка: один длинный звук, растянутый под время прыжка этой системы.
   * Второй вызов ничего не делает — прыжок уже гудит.
   */
  startJump(seconds: number): void {
    if (!this.sfx || this.charge !== null) return;
    this.charge = this.sfx.play(
      { cue: 'jump-charge', rate: Math.min(3, Math.max(0.4, 3 / Math.max(seconds, 0.5))), gain: 1, priority: 75 },
      null,
      0,
      performance.now(),
    );
  }

  /** Прыжок совершён или сорван: гул смолкает, и при успехе за ним идёт сам прыжок. */
  endJump(jumped: boolean): void {
    if (this.charge !== null) this.sfx?.stop(this.charge);
    this.charge = null;
    if (jumped) this.own('jump');
  }

  /** Обрыв связи, прыжок в другую систему, гибель: всё смолкает, счётчики боя обнуляются. */
  reset(): void {
    for (const timer of this.pending) window.clearTimeout(timer);
    this.pending.clear();
    this.charge = null;
    this.sfx?.stopAll();
    this.lastHostileShot = 0;
    this.lastOwnShot = 0;
  }

  /** Проверка громкости из окна настроек. */
  preview(cue: SfxCue = 'explode'): void {
    this.unlock();
    this.sfx?.preview(cue);
  }

  private cue(cue: SfxCue, at: Place | null, source: number, now: number): void {
    this.sfx?.play({ cue, rate: 1, gain: 1, priority: cue === 'alarm' ? 100 : 50 }, this.at(at), source, now);
  }

  private at(place: Place | null) {
    return place ? spatial(place, this.listener) : null;
  }

  /**
   * Скачать и запустить музыку. Выключенная музыка не качается вовсе: это два мегабайта, которые
   * человеку с ползунком на нуле не нужны ни разу.
   */
  private ensureMusic(): void {
    if (!this.engine || this.settings.prefs.music <= 0) return;
    // Круг запускается, не дожидаясь жеста: у спящего контекста часы стоят, и всё, что назначено
    // «через 120 мс», честно дождётся пробуждения. Проверять running здесь нельзя — resume асинхронный,
    // и сразу после жеста контекст ещё не running.
    void this.music?.load().then(() => this.music?.start());
  }
}
