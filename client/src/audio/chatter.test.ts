import { describe, expect, it } from 'vitest';
import bank from '../../../shared/chatter.json';
import { Chatter, PRIORITY, RULES, type ChatterFrame, type Speaker } from './chatter';

const me = { id: 1, x: 0, y: 0 };

function ship(over: Partial<Speaker> & { id: number; kind: string }): Speaker {
  return { name: `Борт-${over.id}`, x: 300, y: 0, dead: false, targetId: 0, ai: null, hp: 300, maxHp: 300, ...over };
}

function frame(over: Partial<ChatterFrame> & { now: number }): ChatterFrame {
  return { me, ships: [], combat: false, docked: false, dead: false, myHp: 1, ...over };
}

/** Детерминированный «случай»: всегда ниже шанса — наполнитель и последние слова выходят всегда. */
const always = () => 0;
const never = () => 0.999;

describe('радиоэфир: кто и когда говорит', () => {
  it('пират угрожает, когда впервые берёт меня целью, и только один раз за бой', () => {
    const chatter = new Chatter(RULES, never);
    const pirate = ship({ id: 7, kind: 'pirate', targetId: 1, ai: 'attack' });
    const first = chatter.tick(frame({ now: 1000, ships: [pirate], combat: true }));
    expect(first?.category).toBe('pirateThreat');
    expect(first?.shipId).toBe(7);
    expect(first?.text.length).toBeGreaterThan(0);
    for (let t = 2000; t < 60000; t += 500) expect(chatter.tick(frame({ now: t, ships: [pirate], combat: true }))?.category).not.toBe('pirateThreat');
  });

  it('пират, ушедший из боя и вернувшийся, угрожает снова', () => {
    const chatter = new Chatter(RULES, never);
    chatter.tick(frame({ now: 0, ships: [ship({ id: 7, kind: 'pirate', targetId: 1, ai: 'attack' })], combat: true }));
    chatter.tick(frame({ now: 10000, ships: [ship({ id: 7, kind: 'pirate', targetId: 0, ai: 'return' })], combat: false }));
    const again = chatter.tick(frame({ now: 100000, ships: [ship({ id: 7, kind: 'pirate', targetId: 1, ai: 'attack' })], combat: true }));
    expect(again?.category).toBe('pirateThreat');
  });

  it('мольба — когда корпус ниже порога и сервер перевёл пирата в бегство; раз за жизнь', () => {
    const chatter = new Chatter(RULES, never);
    const fighting = ship({ id: 7, kind: 'pirate', targetId: 1, ai: 'attack', hp: 60, maxHp: 300 });
    chatter.tick(frame({ now: 0, ships: [fighting], combat: true }));
    // Ещё бьётся — не просит.
    expect(chatter.tick(frame({ now: 500, ships: [fighting], combat: true }))).toBeNull();
    const fleeing = { ...fighting, ai: 'leave' };
    const beg = chatter.tick(frame({ now: 1000, ships: [fleeing], combat: true }));
    expect(beg?.category).toBe('beg');
    expect(chatter.tick(frame({ now: 20000, ships: [fleeing], combat: true }))).toBeNull();
  });

  it('мольба не ждёт общего кулдауна: она важнее только что прозвучавшей угрозы', () => {
    const chatter = new Chatter(RULES, never);
    const pirate = ship({ id: 7, kind: 'pirate', targetId: 1, ai: 'attack' });
    expect(chatter.tick(frame({ now: 0, ships: [pirate], combat: true }))?.category).toBe('pirateThreat');
    const beg = chatter.tick(frame({ now: 1500, ships: [{ ...pirate, ai: 'leave', hp: 20 }], combat: true }));
    expect(beg?.category).toBe('beg');
    expect(PRIORITY.beg).toBeGreaterThan(PRIORITY.pirateThreat);
  });

  it('насмешка — только когда бой идёт давно и у пирата дела лучше моих', () => {
    const chatter = new Chatter(RULES, never);
    const pirate = ship({ id: 7, kind: 'pirate', targetId: 1, ai: 'attack', hp: 280 });
    chatter.tick(frame({ now: 0, ships: [pirate], combat: true, myHp: 0.4 }));
    expect(chatter.tick(frame({ now: 8000, ships: [pirate], combat: true, myHp: 0.4 }))).toBeNull();
    expect(chatter.tick(frame({ now: 12000, ships: [pirate], combat: true, myHp: 0.4 }))?.category).toBe('pirateTaunt');
    // Мне лучше — не насмехается.
    const fresh = new Chatter(RULES, never);
    fresh.tick(frame({ now: 0, ships: [pirate], combat: true, myHp: 1 }));
    expect(fresh.tick(frame({ now: 12000, ships: [pirate], combat: true, myHp: 1 }))).toBeNull();
  });

  it('торговец здоровается при подходе и не повторяет приветствие три минуты', () => {
    const chatter = new Chatter(RULES, never);
    const trader = ship({ id: 3, kind: 'trader', x: 500 });
    expect(chatter.tick(frame({ now: 0, ships: [trader] }))?.category).toBe('traderHail');
    expect(chatter.tick(frame({ now: 60000, ships: [trader] }))).toBeNull();
    expect(chatter.tick(frame({ now: RULES.hailAgainMs + 1, ships: [trader] }))?.category).toBe('traderHail');
  });

  it('далёкий торговец не здоровается, в доке и после гибели эфир молчит', () => {
    const chatter = new Chatter(RULES, always);
    const beyond = ship({ id: 2, kind: 'trader', x: RULES.civilRange + 1 });
    expect(chatter.tick(frame({ now: 0, ships: [beyond] }))).toBeNull();
    const far = ship({ id: 3, kind: 'trader', x: RULES.hailRange + 1 });
    expect(chatter.tick(frame({ now: 0, ships: [far] }))?.category).not.toBe('traderHail');
    const near = ship({ id: 3, kind: 'trader', x: 100 });
    expect(chatter.tick(frame({ now: 0, ships: [near], docked: true }))).toBeNull();
    expect(chatter.tick(frame({ now: 0, ships: [near], dead: true }))).toBeNull();
  });

  it('рейнджер рядом с боем предупреждает, в покое — здоровается', () => {
    const chatter = new Chatter(RULES, never);
    const ranger = ship({ id: 5, kind: 'ranger', x: 800 });
    expect(chatter.tick(frame({ now: 0, ships: [ranger], combat: true }))?.category).toBe('rangerWarn');
    const calm = new Chatter(RULES, never);
    expect(calm.tick(frame({ now: 0, ships: [ranger] }))?.category).toBe('rangerHail');
  });

  it('наполнитель: раз в несколько секунд с шансом, и не чаще общего кулдауна', () => {
    const chatter = new Chatter(RULES, always);
    const trader = ship({ id: 3, kind: 'trader', x: 1000 }); // дальше hailRange, ближе civilRange
    expect(chatter.tick(frame({ now: 0, ships: [trader] }))?.category).toBe('traderIdle');
    expect(chatter.tick(frame({ now: 100, ships: [trader] }))).toBeNull();
    // Тот же торговец — кулдаун на корабль 45 с; второй торговец рядом — говорит он.
    const other = ship({ id: 4, kind: 'trader', x: 1100 });
    const next = chatter.tick(frame({ now: RULES.globalCooldownMs.calm + RULES.categoryCooldownMs + 1, ships: [trader, other] }));
    expect(next?.shipId).toBe(4);
  });

  it('в бою мирная болтовня не выходит в эфир', () => {
    const chatter = new Chatter(RULES, always);
    const trader = ship({ id: 3, kind: 'trader', x: 500 });
    expect(chatter.tick(frame({ now: 0, ships: [trader], combat: true }))).toBeNull();
  });

  it('реплики не повторяются, пока не прозвучали все из категории', () => {
    const chatter = new Chatter(RULES, () => 0.5);
    const total = bank.categories.traderHail.lines.length;
    const heard = new Set<string>();
    for (let i = 0; i < total; i++) {
      const trader = ship({ id: 100 + i, kind: 'trader', x: 200 });
      const line = chatter.tick(frame({ now: i * 100000, ships: [trader] }));
      expect(line?.category).toBe('traderHail');
      heard.add(line!.id);
    }
    expect(heard.size).toBe(total);
  });

  it('SOS зовёт на помощь, спасение с наградой благодарит, без награды — молчит', () => {
    const chatter = new Chatter(RULES, never);
    const trader = { id: 3, name: 'Торговец «Альба»', x: 900, y: 0 };
    expect(chatter.sos(trader, 'on', 0, 0)?.category).toBe('sos');
    expect(chatter.sos(trader, 'saved', 120, 5000)?.category).toBe('thanks');
    expect(chatter.sos(trader, 'saved', 0, 6000)).toBeNull();
    expect(chatter.sos(trader, 'lost', 0, 7000)).toBeNull();
  });

  it('последние слова — только у пирата, с которым шёл бой, и с шансом', () => {
    const pirate = ship({ id: 7, kind: 'pirate', targetId: 1, ai: 'attack' });
    expect(new Chatter(RULES, always).kill(pirate, 1, 0)?.category).toBe('lastWords');
    expect(new Chatter(RULES, never).kill(pirate, 1, 0)).toBeNull();
    expect(new Chatter(RULES, always).kill(ship({ id: 3, kind: 'trader' }), 1, 0)).toBeNull();
    expect(new Chatter(RULES, always).kill(ship({ id: 8, kind: 'pirate', targetId: 2, ai: 'attack' }), 1, 0)).toBeNull();
  });

  it('у каждой реплики банка есть текст без подстановок', () => {
    for (const block of Object.values(bank.categories)) {
      for (const line of block.lines) {
        expect(line.text).not.toMatch(/[{}]/);
        expect(line.text.length).toBeGreaterThan(5);
      }
    }
  });
});
