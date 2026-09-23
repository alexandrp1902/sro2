import { describe, expect, it } from 'vitest';
import { PRIORITY } from './cues';
import { AUDIBLE, SfxBudget, spatial, type Listener } from './mixer';

const here: Listener = { x: 0, y: 0, zoom: 1, halfWidth: 640 };

describe('панорама и дальность', () => {
  it('звук слева уходит влево, справа — вправо, в центре — посередине', () => {
    expect(spatial({ x: -400, y: 0 }, here).pan).toBeLessThan(0);
    expect(spatial({ x: 400, y: 0 }, here).pan).toBeGreaterThan(0);
    expect(spatial({ x: 0, y: 300 }, here).pan).toBe(0);
  });

  it('панорама не упирается в край: в наушниках это «выпадает» из головы', () => {
    expect(Math.abs(spatial({ x: 99999, y: 0 }, here).pan)).toBeLessThan(1);
  });

  it('чем дальше, тем тише, а за границей слышимости — тишина', () => {
    const near = spatial({ x: 200, y: 0 }, here).gain;
    const far = spatial({ x: 1800, y: 0 }, here).gain;
    expect(near).toBeGreaterThan(far);
    expect(far).toBeGreaterThan(0);
    expect(spatial({ x: AUDIBLE + 1, y: 0 }, here).gain).toBe(0);
  });

  it('на отдалении камеры мир и по панораме становится уже', () => {
    const close = Math.abs(spatial({ x: 600, y: 0 }, { ...here, zoom: 1 }).pan);
    const wide = Math.abs(spatial({ x: 600, y: 0 }, { ...here, zoom: 0.4 }).pan);
    expect(wide).toBeLessThan(close);
  });
});

describe('бюджет голосов', () => {
  it('залп из шести пушек одного корабля даёт не больше двух звуков', () => {
    const budget = new SfxBudget();
    let played = 0;
    for (let i = 0; i < 6; i++) if (budget.allow('shot-bolt', 7, PRIORITY.myShot, 170, 1000)) played++;
    expect(played).toBe(2);
  });

  it('второй выстрел залпа тише первого — иначе унисон просто громче вдвое', () => {
    const budget = new SfxBudget();
    expect(budget.allow('shot-bolt', 7, PRIORITY.myShot, 170, 1000)!.gain).toBe(1);
    expect(budget.allow('shot-bolt', 7, PRIORITY.myShot, 170, 1000)!.gain).toBeLessThan(1);
  });

  it('чужие выстрелы подряд глушатся антиспамом, свои — нет', () => {
    const alien = new SfxBudget();
    expect(alien.allow('shot-bolt', 3, PRIORITY.shot, 170, 1000)).not.toBeNull();
    expect(alien.allow('shot-bolt', 4, PRIORITY.shot, 170, 1010)).toBeNull();

    const mine = new SfxBudget();
    expect(mine.allow('shot-bolt', 3, PRIORITY.shot, 170, 1000)).not.toBeNull();
    expect(mine.allow('shot-bolt', 9, PRIORITY.myShot, 170, 1010)).not.toBeNull();
  });

  it('звук освобождает голос сам, когда доиграл', () => {
    const budget = new SfxBudget();
    budget.allow('explode', 1, PRIORITY.explosion, 900, 1000);
    expect(budget.playing).toBe(1);
    budget.allow('explode', 2, PRIORITY.explosion, 900, 2000);
    expect(budget.playing).toBe(1);
  });

  it('на переполнении тревога вытесняет самый неважный звук, а мелочь не проходит', () => {
    const budget = new SfxBudget();
    // Важность выше порога антиспама: проверяем именно потолок голосов, а не запрет на повтор.
    for (let i = 0; i < 16; i++) budget.allow('shot-bolt', i + 1, PRIORITY.myShot, 5000, 1000 + i);
    expect(budget.playing).toBe(16);

    expect(budget.allow('loot', 0, PRIORITY.minor, 400, 1100)).toBeNull();
    const alarm = budget.allow('alarm', 0, PRIORITY.alarm, 400, 1100);
    expect(alarm).not.toBeNull();
    expect(alarm!.stop).not.toBeNull();
    expect(budget.playing).toBe(16);
  });

  it('прыжок и обрыв связи обнуляют и голоса, и запрет на повтор', () => {
    const budget = new SfxBudget();
    budget.allow('shot-bolt', 3, PRIORITY.shot, 170, 1000);
    budget.clear();
    expect(budget.playing).toBe(0);
    expect(budget.allow('shot-bolt', 3, PRIORITY.shot, 170, 1001)).not.toBeNull();
  });
});
