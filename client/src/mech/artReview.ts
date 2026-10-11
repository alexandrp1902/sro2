import {Application, Text} from 'pixi.js';
import {loadMechArt, MechRig} from './rig';
import {DEFAULT_RULES, DIRECTION_NAMES, type MechPart, type MechUnitDef} from './rules';

/** Visual acceptance bench using the battle renderer and the actual exported textures. */
export async function showMechArtReview():Promise<void> {
  const host=document.createElement('section');
  host.style.cssText='position:fixed;inset:0;overflow:auto;background:#161d24;color:#eee;z-index:1000;padding:16px';
  document.body.append(host);
  const title=document.createElement('h2');title.textContent='M22 · проверка сборок';host.append(title);
  const controls=document.createElement('div');controls.style.cssText='display:flex;gap:16px;flex-wrap:wrap';host.append(controls);
  const select=(label:string,options:string[])=>{
    const field=document.createElement('label');field.textContent=label+' ';
    const input=document.createElement('select');
    for(const value of options){const option=document.createElement('option');option.value=value;option.textContent=value;input.append(option);}
    field.append(input);controls.append(field);return input;
  };
  const params=new URLSearchParams(location.search);
  const size=select('Размер',['64','96','128']);size.value=params.get('artSize') ?? '96';
  const state=select('Состояние',['целый','движение','левая рука','правая рука','шасси','обе руки','остов']);
  state.value=params.get('artState') ?? 'целый';
  const assemblies:MechUnitDef[]=[
    {name:'Лёгкий снайпер',body:'sniper',chassis:'lightBiped',left:'pistol-cannon',right:'sniper'},
    {name:'Тяжёлый мечник',body:'heavy',chassis:'heavyBiped',left:'heavy',right:'sword',backpack:'damage'},
    {name:'Две автопушки',body:'medium',chassis:'mediumBiped',left:'autocannon',right:'autocannon',backpack:'armor'},
    {name:'Гусеничный враг',body:'enemy-heavy',chassis:'tracked',left:'heavy',right:'energy',backpack:'repair'},
  ];
  const panel=document.createElement('div');panel.style.cssText='overflow:auto;margin-top:16px';host.append(panel);
  const app=new Application();await app.init({width:1080,height:640,background:0x161d24,antialias:true});panel.append(app.canvas);
  await loadMechArt();
  let rigs:Array<{rig:MechRig;dir:number}>=[];
  const draw=()=>{
    app.stage.removeChildren().forEach(c=>c.destroy({children:true}));rigs=[];
    const px=Number(size.value),spacing=px+20,left=155;
    app.renderer.resize(left+spacing*8,40+spacing*4);
    DIRECTION_NAMES.forEach((direction,col)=>{
      const text=new Text({text:direction.toUpperCase(),style:{fontSize:14,fill:0xcbd8e5}});
      text.position.set(left+col*spacing+px/2,5);app.stage.addChild(text);
    });
    assemblies.forEach((assembly,row)=>{
      const text=new Text({text:assembly.name,style:{fontSize:13,fill:0xcbd8e5,wordWrap:true,wordWrapWidth:145}});
      text.position.set(0,55+row*spacing);app.stage.addChild(text);
      for(let dir=0;dir<8;dir++){
        const rig=new MechRig(px);rig.configure(assembly,DEFAULT_RULES);
        rig.view.position.set(left+dir*spacing+px/2,35+row*spacing+px*.85);
        app.stage.addChild(rig.view);rigs.push({rig,dir});
      }
    });
  };
  size.addEventListener('change',draw);draw();
  app.ticker.add(()=>{
    const broken=new Set<MechPart>();
    if(state.value==='левая рука'||state.value==='обе руки')broken.add('left');
    if(state.value==='правая рука'||state.value==='обе руки')broken.add('right');
    if(state.value==='шасси')broken.add('chassis');
    const phase=state.value==='движение'?Math.floor(performance.now()/90)%4:-1;
    for(const {rig,dir} of rigs)rig.set(dir,broken,state.value==='остов',phase);
  });
  host.dataset.ready='true';
}
