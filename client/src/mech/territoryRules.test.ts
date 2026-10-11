import {describe,it,expect} from 'vitest';
import vectors from '../../../shared/test-vectors/territory.json';
import configData from '../../../shared/mech-territory.json';
import {resetControl,serviceCost,distribute,cargoVolume,accessible,type TerritoryConfig} from './territoryRules';
describe('M23 shared vectors',()=>{
  it('matches seasonal rounding',()=>{for(const v of vectors.reset)expect(resetControl(v.control,v.start)).toBe(v.expected);});
  it('matches service rounding',()=>{for(const v of vectors.service)expect(serviceCost(v.lost,v.max,v.discount)).toEqual({titanium:v.titanium,energy:v.energy});});
  it('conserves integer resource shares',()=>{for(const v of vectors.distribution)expect(distribute(v.pool,v.contributions as Record<string,number>)).toEqual(v.expected);});
  it('matches dropship cargo volume',()=>{for(const v of vectors.cargo)expect(cargoVolume(v.hull,v.resource,v.volume)).toBe(v.expected);});
  it('keeps two base roads open after total defeat',()=>{
    const config=configData as TerritoryConfig, objects=config.objects.map(o=>({id:o.id,owner:'corporation',control:0,revision:0}));
    expect(objects.filter(o=>accessible(config,objects,o.id))).toHaveLength(2);
  });
});
