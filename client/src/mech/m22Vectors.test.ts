import {expect, it} from 'vitest';
import vectors from '../../../shared/test-vectors/mech.json';
import {assemblyWeight, forecast, unitDef, xpThreshold, type MechBattleView, type MechPart, type MechRules} from './rules';

it('M22: прогноз обеих рук, всех оружий и рюкзаков совпадает с сервером', () => {
  for (const v of vectors.m22.forecast) {
    const rules = vectors.m22.rules as unknown as MechRules;
    const state = v.state as MechBattleView;
    const [me, target] = state.units;
    const f = forecast(rules, state, me, target, v.part as MechPart | null, v.hand);
    expect(f.ok ? f.chance : null, `${me.assembly?.right}/${me.assembly?.backpack}/${v.hand}`).toBe(v.chance);
    expect([f.block, f.min, f.max]).toEqual([v.block, v.min, v.max]);
    const def = unitDef(rules, me)!;
    expect(assemblyWeight(rules, def)).toBe(v.weight);
    expect(rules.chassis?.[def.chassis]?.maxLoad).toBe(v.load);
  }
});

it('порог следующего уровня в мастерской совпадает с сервером',()=>{
  for(const v of vectors.m22.development)expect(xpThreshold(v.level)).toBe(v.threshold);
});
