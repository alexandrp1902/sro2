import { describe, expect, it } from 'vitest';
import { AUTO_TARGET_PAUSE_MS, AutoTarget } from './autoTarget';

describe('AutoTarget', () => {
  it('allows the first capture', () => {
    const aim = new AutoTarget();

    expect(aim.allows(0)).toBe(true);
    expect(aim.isAuto).toBe(false);
  });

  it('keeps quiet for five seconds after the player overrides it', () => {
    const aim = new AutoTarget();
    aim.changed(true, 1000); // по нам выстрелили — навелись сами

    aim.changed(false, 2000); // игрок снял или сменил цель

    expect(aim.allows(2000)).toBe(false);
    expect(aim.pausedFor(2000)).toBe(AUTO_TARGET_PAUSE_MS);
    expect(aim.allows(2000 + AUTO_TARGET_PAUSE_MS - 1)).toBe(false);
    expect(aim.allows(2000 + AUTO_TARGET_PAUSE_MS)).toBe(true);
  });

  it('does not sulk when the player picked the target himself all along', () => {
    // Игрок выбрал цель, потом сменил её на другую — мы в это не вмешивались, и молчать нам незачем.
    const aim = new AutoTarget();

    aim.changed(false, 1000);
    aim.changed(false, 2000);

    expect(aim.allows(2000)).toBe(true);
    expect(aim.pausedFor(2000)).toBe(0);
  });

  it('does not pause when one capture replaces another', () => {
    // Прежний стрелок погиб, стреляет новый: это по-прежнему наш выбор, а не спор с игроком.
    const aim = new AutoTarget();
    aim.changed(true, 1000);

    aim.changed(true, 1500);

    expect(aim.allows(1500)).toBe(true);
    expect(aim.isAuto).toBe(true);
  });

  it('remembers whose choice is standing', () => {
    const aim = new AutoTarget();
    expect(aim.isAuto).toBe(false);

    aim.changed(true, 0);
    expect(aim.isAuto).toBe(true);

    aim.changed(false, 100);
    expect(aim.isAuto).toBe(false);
  });

  it('starts the pause again if the player overrides a second capture', () => {
    const aim = new AutoTarget();
    aim.changed(true, 0);
    aim.changed(false, 100); // пауза до 5100

    aim.changed(true, 6000); // пауза прошла, навелись снова
    aim.changed(false, 6100); // и снова получили по рукам

    expect(aim.allows(6100)).toBe(false);
    expect(aim.allows(6100 + AUTO_TARGET_PAUSE_MS)).toBe(true);
  });
});
