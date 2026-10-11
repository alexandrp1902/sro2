import config from '../../../shared/mech-territory.json';
import {TerritoryPanel} from './territory';
import {MechScreen} from './screen';
import {DEFAULT_RULES,type MechBattleView} from './rules';
import type {TerritoryConfig,TerritoryStateMsg} from './territoryRules';

export function territoryDemo(kind:string):void {
  const root=document.createElement('section');root.className='territory-demo sro-pane';root.style.cssText='position:fixed;inset:0;overflow:auto;padding:16px;z-index:110;background:#111d29';document.body.append(root);
  const data:TerritoryStateMsg={t:'territoryState',account:'demo-a',config:config as TerritoryConfig,unlocked:true,docked:true,waiting:kind==='territory-saving',
    profile:{level:2,evaStep:0,evaDone:false,debts:{'m1':{titanium:3,energy:2}},titles:[]},
    world:{season:1,seasonEnd:new Date(Date.now()+86400000*4).toISOString(),revision:1,nextStrike:new Date(Date.now()+21600000).toISOString(),
      objects:config.objects.map(o=>({id:o.id,owner:o.startControl===100?'resistance':'corporation',control:o.startControl,revision:0,active:kind==='territory-busy'&&o.id==='np-02'?'busy':null})),
      contributions:{'demo-a':48},claims:[{id:'demo-claim',account:'demo-a',season:1,kind:kind==='territory-season'?'final':'supply',claimed:false,reward:{credits:2500,titanium:12,energy:8,rareMetal:4,part:'backpack:armor'}}],archives:[]}};
  if(kind==='territory-invite')data.preparation={id:'prep',leader:'demo-a',object:'np-02',operation:'assault',expires:new Date(Date.now()+60000).toISOString(),players:[{account:'demo-a',name:'Аякс',squad:['m1','m2'],ready:true},{account:'demo-b',name:'Лина',squad:[],ready:false}]};
  if(kind==='territory-coop') {
    root.remove();const mission=DEFAULT_RULES.missions!.majorAssault;
    const unit=(id:string,owner:string,x:number,y:number)=>({id,side:'player' as const,unit:'prototype',name:owner==='demo-a'?'Мех Аякса':'Мех Лины',x,y,dir:0,hp:[2200,1100,600,1100],max:[2200,1100,600,1100],activated:false,accountOwner:owner,owner:id});
    const state:MechBattleView={mission:'majorAssault',map:mission.map,round:3,turn:'player',current:'p3',moved:false,steps:0,moveRange:4,units:[unit('p1','demo-a',2,10),unit('p2','demo-a',4,10),unit('p3','demo-b',2,8),unit('p4','demo-b',4,8)],goal:'Захватите зону. Сейчас управляет Лина.',progress:1,required:2};
    const screen=new MechScreen(document.getElementById('mech')!,{send:()=>{},confirm:(_lines,yes)=>yes()});screen.apply({t:'mechState',rules:DEFAULT_RULES,session:'demo-coop',revision:1,yourAccount:'demo-a',deadline:new Date(Date.now()+70000).toISOString(),battle:state});return;
  }
  const panel=new TerritoryPanel(()=>{});panel.set(data);
  panel.setWorkshop({t:'workshopState',state:{mechs:[{id:'m1',name:'Прототип',assembly:{},level:3,xp:150},{id:'m2',name:'Гвардеец',assembly:{},level:2,xp:80}],parts:[],resources:{titanium:42,energy:18,rareMetal:4},squad:['m1','m2'],grants:[],completed:['majorAssault'],results:[],revision:1},rules:DEFAULT_RULES,credits:7000,open:true,available:[]});panel.render(root);
}
