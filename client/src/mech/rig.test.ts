import {beforeAll, describe, expect, it, vi} from 'vitest';
import {DEFAULT_RULES, type MechPart} from './rules';

vi.mock('pixi.js',()=>{
  class Point {x=1;y=1;set(x:number,y=x){this.x=x;this.y=y;}}
  class Container {
    children:unknown[]=[];scale=new Point();pivot=new Point();position=new Point();sortableChildren=false;
    addChild(...items:unknown[]){this.children.push(...items);}
  }
  class Sprite {
    texture:unknown;visible=true;tint=0xffffff;width=0;height=0;zIndex=0;position=new Point();
    constructor(texture:unknown){this.texture=texture;}
  }
  class Graphics {
    clear(){return this;}roundRect(){return this;}fill(){return this;}stroke(){return this;}
    circle(){return this;}poly(){return this;}moveTo(){return this;}lineTo(){return this;}
  }
  return {Container,Sprite,Graphics,Texture:{EMPTY:'empty'},Assets:{load:async(items:Array<{alias:string;src:string}>)=>
    Object.fromEntries(items.map(item=>[item.alias,item.src]))}};
});
import {loadMechArt,MechRig} from './rig';

type Drawn={texture:string;visible:boolean};
const drawn=(rig:MechRig)=>rig.view.children.filter(c=>
  'texture' in c && (c as unknown as Drawn).visible).map(c=>(c as unknown as Drawn).texture);
const setup=()=>{
  const rig=new MechRig(96);
  rig.configure({name:'Снайпер',body:'sniper',chassis:'lightBiped',left:'pistol-cannon',right:'sniper'},DEFAULT_RULES);
  return rig;
};
beforeAll(()=>loadMechArt());
describe('production art selection',()=>{
  it('uses distinct anatomical weapons in all eight directions',()=>{
    const rig=setup();
    for(const [dir,name]of ['n','ne','e','se','s','sw','w','nw'].entries()){
      rig.set(dir,new Set());
      expect(drawn(rig)).toContain(`mechs/part-arm-pistol-cannon-left-${name}.webp`);
      expect(drawn(rig)).toContain(`mechs/part-arm-sniper-right-${name}.webp`);
    }
  });
  it('replaces each destroyed arm with its own socket, retaining the other weapon',()=>{
    const rig=setup();
    for(const hand of ['left','right'] as MechPart[]){
      rig.set(2,new Set([hand]));
      expect(drawn(rig)).toContain(`mechs/aux-broken-socket-${hand}-2.webp`);
      expect(drawn(rig)).toContain(`mechs/part-arm-${hand==='left'?'sniper-right':'pistol-cannon-left'}-e.webp`);
    }
  });
  it('shows a disabled chassis and returns to the static chassis after walking',()=>{
    const rig=setup();rig.set(4,new Set(['chassis']));
    expect(drawn(rig)).toContain('mechs/aux-disabled-light-biped-4.webp');
    for(let phase=0;phase<4;phase++){
      rig.set(4,new Set(),false,phase);
      expect(drawn(rig)).toContain(`mechs/aux-walk-light-biped-${phase*8+4}.webp`);
    }
    rig.set(4,new Set());expect(drawn(rig)).toContain('mechs/part-chassis-light-biped-s.webp');
  });
  it('shows only the wreck on death and restores the full rig after repair',()=>{
    const rig=setup();rig.set(0,new Set(),true);
    expect(drawn(rig)).toEqual(['mechs/aux-wreck-sniper-0.webp']);
    rig.set(0,new Set());expect(drawn(rig)).toHaveLength(4);
  });
  it('uses generator and tractor states instead of assembling an objective as a mech',()=>{
    const rig=setup();
    for(const type of ['generator','tractor']){
      rig.configure(DEFAULT_RULES.units!.mine,DEFAULT_RULES,true,type);
      rig.set(0,new Set());expect(drawn(rig)).toEqual([`mechs/aux-${type}-intact-0.webp`]);
      rig.set(0,new Set(),true);expect(drawn(rig)).toEqual([`mechs/aux-${type}-destroyed-0.webp`]);
    }
  });
});
