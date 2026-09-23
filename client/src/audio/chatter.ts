import bank from '../../../shared/chatter.json';

/**
 * Радиоэфир (M17b): кто, когда и что говорит. Чистый модуль без браузера — правила проверяются тестами.
 *
 * Главная забота — чтобы эфир не тараторил. Кулдауны: общий (в бою короче), на корабль, на категорию;
 * торговец здоровается с тем же пилотом не чаще раза в три минуты; пират угрожает один раз за бой и
 * просит пощады один раз за жизнь. Говорит всегда один: реплика с большим приоритетом обрывает текущую,
 * с меньшим — не выходит в эфир вовсе.
 *
 * Реплики берутся «мешком»: пока не прозвучали все из категории, повтора не будет. Тексты и правила —
 * shared/chatter.json, тот же файл, из которого tools/voice.py рендерит речь.
 */

export type Category = keyof typeof bank.categories;

export interface ChatterRules {
  globalCooldownMs: { calm: number; combat: number };
  shipCooldownMs: number;
  categoryCooldownMs: number;
  hailAgainMs: number;
  idleEveryMs: number;
  idleChance: number;
  hailRange: number;
  civilRange: number;
  hostileRange: number;
  tauntAfterMs: number;
  begHp: number;
  lastWordsChance: number;
}

/** Чужой корабль так, как его видит эфир: подмножество RemoteShipInfo. */
export interface Speaker {
  id: number;
  kind: string;
  name: string;
  x: number;
  y: number;
  dead: boolean;
  /** Цель пирата; 0 — нет. */
  targetId: number;
  ai: string | null;
  hp: number;
  maxHp: number;
}

export interface ChatterFrame {
  now: number;
  me: { id: number; x: number; y: number };
  ships: readonly Speaker[];
  /** Идёт бой (настроение музыки combat). */
  combat: boolean;
  docked: boolean;
  dead: boolean;
  /** Мой корпус, доля от полного. */
  myHp: number;
}

export interface Line {
  category: Category;
  /** Ключ файла и манифеста: «pirateThreat-3». */
  id: string;
  text: string;
  shipId: number;
  name: string;
  x: number;
  y: number;
  priority: number;
}

export const RULES: ChatterRules = bank.rules;

/** Больше — важнее: такая реплика обрывает ту, что звучит, и не ждёт общего кулдауна. */
export const PRIORITY: Record<Category, number> = {
  beg: 90,
  lastWords: 85,
  pirateThreat: 80,
  sos: 70,
  thanks: 65,
  rangerWarn: 60,
  pirateTaunt: 50,
  traderHail: 40,
  rangerHail: 40,
  traderIdle: 20,
  convoyIdle: 20,
};

/** С этого приоритета реплика не ждёт общего кулдауна: мольба о пощаде не может «не успеть». */
const URGENT = 70;
/**
 * Кулдаун на корабль держит только мирную болтовню: угроза, насмешка и мольба идут от одного и того же
 * пирата с интервалом в секунды, и это правильно — его сдерживают общий кулдаун и кулдаун категории.
 */
const NO_SHIP_COOLDOWN = new Set<Category>(['beg', 'lastWords', 'sos', 'thanks', 'pirateThreat', 'pirateTaunt', 'rangerWarn']);
const NO_CATEGORY_COOLDOWN = new Set<Category>(['beg', 'lastWords', 'thanks']);

const HOSTILE = new Set(['pirate']);
const CIVIL = new Set(['trader', 'convoy']);
const PATROL = new Set(['ranger', 'wing']);

export class Chatter {
  private lastSpokeAt = -Infinity;
  private lastPriority = 0;
  private readonly spokeShip = new Map<number, number>();
  private readonly spokeCategory = new Map<Category, number>();
  private readonly hailed = new Map<number, number>();
  private readonly threatened = new Set<number>();
  private readonly begged = new Set<number>();
  private fightStart: number | null = null;
  private lastIdlePoll = -Infinity;
  private readonly bags = new Map<Category, string[]>();
  private readonly lastLine = new Map<Category, string>();

  constructor(
    private readonly rules: ChatterRules = RULES,
    private readonly random: () => number = Math.random,
  ) {}

  /** Раз в кадр. @returns реплика, которую пора выдать в эфир, или null. */
  tick(f: ChatterFrame): Line | null {
    if (f.docked || f.dead) return null;
    if (f.combat && this.fightStart === null) this.fightStart = f.now;
    if (!f.combat) this.fightStart = null;
    this.forget(f.ships);

    const near = (ship: Speaker, range: number) => Math.hypot(ship.x - f.me.x, ship.y - f.me.y) <= range;
    const alive = f.ships.filter((s) => !s.dead);

    // Мольба: пират сломался — корпус ниже порога, сервер перевёл его в бегство. Раз за жизнь.
    for (const ship of this.nearest(alive, f.me, (s) => HOSTILE.has(s.kind) && this.wasMyFoe(s, f.me.id) && s.ai === 'leave' && s.maxHp > 0 && s.hp / s.maxHp < this.rules.begHp && !this.begged.has(s.id))) {
      if (near(ship, this.rules.hostileRange) && this.allowed('beg', ship.id, f.now, f.combat)) {
        this.begged.add(ship.id);
        return this.say('beg', ship, f.now);
      }
    }

    // Угроза: пират впервые взял меня целью. Раз за бой с этим пиратом.
    for (const ship of this.nearest(alive, f.me, (s) => HOSTILE.has(s.kind) && s.targetId === f.me.id && s.ai === 'attack' && !this.threatened.has(s.id))) {
      if (!near(ship, this.rules.hostileRange)) continue;
      this.threatened.add(ship.id);
      if (this.allowed('pirateThreat', ship.id, f.now, f.combat)) return this.say('pirateThreat', ship, f.now);
    }

    if (f.combat) {
      // Рейнджер рядом с боем предупреждает.
      for (const ship of this.nearest(alive, f.me, (s) => PATROL.has(s.kind) && near(s, this.rules.hostileRange))) {
        if (this.allowed('rangerWarn', ship.id, f.now, true)) return this.say('rangerWarn', ship, f.now);
      }
      // Насмешка: бой идёт давно, и у пирата дела лучше моих.
      if (this.fightStart !== null && f.now - this.fightStart >= this.rules.tauntAfterMs) {
        for (const ship of this.nearest(alive, f.me, (s) => HOSTILE.has(s.kind) && s.targetId === f.me.id && s.maxHp > 0 && s.hp / s.maxHp > f.myHp + 0.1 && near(s, this.rules.hostileRange))) {
          if (this.allowed('pirateTaunt', ship.id, f.now, true)) return this.say('pirateTaunt', ship, f.now);
        }
      }
      return null;
    }

    // Покой: приветствия тех, кто подошёл впервые за долгое время.
    for (const ship of this.nearest(alive, f.me, (s) => (s.kind === 'trader' || PATROL.has(s.kind)) && near(s, this.rules.hailRange) && f.now - (this.hailed.get(s.id) ?? -Infinity) >= this.rules.hailAgainMs)) {
      const category: Category = ship.kind === 'trader' ? 'traderHail' : 'rangerHail';
      this.hailed.set(ship.id, f.now);
      if (this.allowed(category, ship.id, f.now, false)) return this.say(category, ship, f.now);
    }

    // Наполнитель: раз в несколько секунд с небольшим шансом кто-то из мирных рядом что-то бурчит.
    if (f.now - this.lastIdlePoll < this.rules.idleEveryMs) return null;
    this.lastIdlePoll = f.now;
    if (this.random() >= this.rules.idleChance) return null;
    for (const ship of this.nearest(alive, f.me, (s) => (CIVIL.has(s.kind) || s.kind === 'wing') && near(s, this.rules.civilRange))) {
      const category: Category = ship.kind === 'trader' ? 'traderIdle' : 'convoyIdle';
      if (this.allowed(category, ship.id, f.now, false)) return this.say(category, ship, f.now);
    }
    return null;
  }

  /** SOS торговца: зовёт на помощь (on) или благодарит (saved с наградой). */
  sos(ship: { id: number; name: string; x: number; y: number }, state: 'on' | 'saved' | 'lost', reward: number, now: number): Line | null {
    if (state === 'on') return this.allowed('sos', ship.id, now, true) ? this.say('sos', ship, now) : null;
    if (state === 'saved' && reward > 0) return this.say('thanks', ship, now);
    return null;
  }

  /** Корабль уничтожен: пират, с которым шёл бой, иногда успевает сказать последнее. */
  kill(ship: Speaker, myId: number, now: number): Line | null {
    this.threatened.delete(ship.id);
    this.begged.delete(ship.id);
    if (!HOSTILE.has(ship.kind) || !this.wasMyFoe(ship, myId)) return null;
    if (this.random() >= this.rules.lastWordsChance) return null;
    return this.say('lastWords', ship, now);
  }

  /** Прыжок, обрыв связи: чужие корабли — уже другие, память о них лишняя. Кулдауны остаются. */
  reset(): void {
    this.threatened.clear();
    this.begged.clear();
    this.hailed.clear();
    this.spokeShip.clear();
    this.fightStart = null;
  }

  /** Что сейчас в эфире важнее: чтобы реплика с меньшим приоритетом не перебила ту, что звучит. */
  get speaking(): number {
    return this.lastPriority;
  }

  private wasMyFoe(ship: Speaker, myId: number): boolean {
    return this.threatened.has(ship.id) || ship.targetId === myId;
  }

  /** Живые корабли по условию, ближние первыми. */
  private nearest(ships: Speaker[], me: { x: number; y: number }, ok: (s: Speaker) => boolean): Speaker[] {
    return ships.filter(ok).sort((a, b) => Math.hypot(a.x - me.x, a.y - me.y) - Math.hypot(b.x - me.x, b.y - me.y));
  }

  private allowed(category: Category, shipId: number, now: number, combat: boolean): boolean {
    const priority = PRIORITY[category];
    const global = combat ? this.rules.globalCooldownMs.combat : this.rules.globalCooldownMs.calm;
    if (priority < URGENT && now - this.lastSpokeAt < global) return false;
    if (!NO_SHIP_COOLDOWN.has(category) && now - (this.spokeShip.get(shipId) ?? -Infinity) < this.rules.shipCooldownMs) return false;
    if (!NO_CATEGORY_COOLDOWN.has(category) && now - (this.spokeCategory.get(category) ?? -Infinity) < this.rules.categoryCooldownMs) return false;
    return true;
  }

  private say(category: Category, ship: { id: number; name: string; x: number; y: number }, now: number): Line {
    const id = this.pick(category);
    const text = bank.categories[category].lines.find((l) => `${category}-${l.id}` === id)?.text ?? '';
    this.lastSpokeAt = now;
    this.lastPriority = PRIORITY[category];
    this.spokeShip.set(ship.id, now);
    this.spokeCategory.set(category, now);
    return { category, id, text, shipId: ship.id, name: ship.name, x: ship.x, y: ship.y, priority: PRIORITY[category] };
  }

  /** Мешок: перетасованные реплики категории; кончились — новый мешок, но не с той же реплики, что была последней. */
  private pick(category: Category): string {
    let bag = this.bags.get(category);
    if (!bag || bag.length === 0) {
      bag = bank.categories[category].lines.map((l) => `${category}-${l.id}`);
      for (let i = bag.length - 1; i > 0; i--) {
        const j = Math.floor(this.random() * (i + 1));
        [bag[i], bag[j]] = [bag[j], bag[i]];
      }
      if (bag.length > 1 && bag[bag.length - 1] === this.lastLine.get(category)) [bag[0], bag[bag.length - 1]] = [bag[bag.length - 1], bag[0]];
      this.bags.set(category, bag);
    }
    const id = bag.pop()!;
    this.lastLine.set(category, id);
    return id;
  }

  /** Корабли, которых больше нет или которые вышли из боя, отпускают свои «раз за бой» и «раз за жизнь». */
  private forget(ships: readonly Speaker[]): void {
    const byId = new Map(ships.map((s) => [s.id, s]));
    for (const id of this.threatened) {
      const ship = byId.get(id);
      if (!ship || ship.dead || ship.ai !== 'attack') this.threatened.delete(id);
    }
    for (const id of this.begged) {
      const ship = byId.get(id);
      if (!ship || ship.dead) this.begged.delete(id);
    }
  }
}
