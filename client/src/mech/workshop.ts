import type { ClientMessage, MechAssembly, MechPartInstance, OwnedMech, WorkshopAct, WorkshopStateMsg } from '../net/protocol';
import { armItem, assemblyWeight, skillCompatible, SKILL_DESCRIPTION, xpThreshold, type MechRules, type MechUnitDef } from './rules';
import { refusalText } from './text';
import { Application } from 'pixi.js';
import {loadMechArt, MechRig} from './rig';

const SLOTS = ['body', 'chassis', 'left', 'right', 'backpack'] as const;
const LABELS = ['Корпус', 'Шасси', 'Левая рука', 'Правая рука', 'Рюкзак'];

export function partName(rules: MechRules, part: Pick<MechPartInstance, 'category' | 'part'>): string {
  return (part.category === 'body' ? rules.bodies?.[part.part] : part.category === 'chassis' ? rules.chassis?.[part.part]
    : part.category === 'weapon' ? rules.weapons?.[part.part] : part.category === 'shield' ? rules.shields?.[part.part]
    : rules.backpacks?.[part.part])?.name ?? part.part;
}

export function resolveAssembly(state: WorkshopStateMsg, mech: OwnedMech, assembly = mech.assembly): MechUnitDef | null {
  const part = (id: string | null | undefined) => state.state.parts.find(p => p.id === id)?.part;
  const body = part(assembly.body), chassis = part(assembly.chassis);
  return body && chassis ? {name: mech.name, body, chassis, left: part(assembly.left), right: part(assembly.right), backpack: part(assembly.backpack)} : null;
}

/** Drafts are local; parts, credits and resources only change on an acknowledged server operation. */
export class WorkshopPanel {
  private message: WorkshopStateMsg | null = null;
  private selected: string | null = null;
  private draft: MechAssembly | null = null;
  private draftName: string | null = null;
  private mission: string | null = null;
  private squad: string[] = [];
  private recipe: string | null = null;
  private waiting = false;
  private connected = true;
  private host: HTMLElement | null = null;
  private readonly preview = new AssemblyPreview();
  constructor(private readonly send: (message: ClientMessage) => void,private readonly openTerritory?:()=>void) {}

  set(message: WorkshopStateMsg): void {
    const changed = this.message?.state.revision !== message.state.revision;
    this.message = message; this.waiting = false;
    if (!message.state.mechs.some(m => m.id === this.selected)) this.selected = message.state.mechs[0]?.id ?? null;
    if (changed) {this.draft = null; this.draftName = null;}
    if (!this.mission || !message.available.includes(this.mission))
      this.mission = message.available.find(m => !message.state.completed.includes(m)) ?? message.available[0] ?? null;
    this.squad = this.squad.filter(id => message.state.mechs.some(m => m.id === id));
    if (this.squad.length === 0) this.squad = [...message.state.squad];
    if (this.host?.isConnected) this.render(this.host);
  }
  online(connected: boolean): void { if (connected === this.connected) return; this.connected = connected; if (this.host?.isConnected) this.render(this.host); }
  private act(act: Omit<WorkshopAct, 't' | 'revision' | 'request'>): void {
    if (!this.message || this.waiting || !this.connected) return;
    this.waiting = true;
    this.send({t: 'workshopAct', ...act, revision: this.message.state.revision, request: crypto.randomUUID()});
    if (this.host) this.render(this.host);
  }
  render(host: HTMLElement): void {
    this.host = host; host.replaceChildren(); host.classList.add('workshop');
    const data = this.message;
    host.append(node('h2', 'Мастерская · Ева Морен'));
    if (!data) { host.append(node('p', 'Загрузка машин и деталей…')); return; }
    const {state, rules} = data;
    if (data.refused) host.append(node('p', refusalText(data.refused), 'sro-warn'));
    if (!this.connected) host.append(node('p', 'Связь потеряна. Восстанавливаем состояние сервера…', 'sro-warn'));
    if (!data.open) { host.append(node('p', 'Мастерская откроется после первой сюжетной победы.')); return; }
    host.append(node('p', `${data.credits.toLocaleString('ru-RU')} кр · машины ${state.mechs.length}/4 · детали ${state.parts.length}`));
    if(data.territory) {
      host.append(node('p',`Уровень мастерской ${data.territory.level} · ${data.territory.level===1?'улучшение: 5000 кр + 40 титана + 20 энергии':data.territory.level===2?'улучшение: 10000 кр + 80 титана + 40 энергии + 10 редкого металла':'все усиленные модули открыты'}`));
      for(const [id,debt] of Object.entries(data.territory.debts??{}))host.append(node('p',`${state.mechs.find(m=>m.id===id)?.name??'Машина'} · обслуживание: ${debt.titanium} титана + ${debt.energy} энергии`));
      if(this.openTerritory)host.append(button('Обслуживание, улучшение и снабжение',()=>this.openTerritory?.()));
    }
    const list = node('div', '', 'workshop-machines');
    for (const mech of state.mechs) {
      const b = button(`${mech.name} · ур. ${mech.level} · ${mech.xp}/${xpThreshold(mech.level)} XP`, () => {this.selected = mech.id; this.draft = null; this.draftName = null; this.render(host);});
      b.setAttribute('aria-pressed', String(this.selected === mech.id)); list.append(b);
    }
    host.append(list);
    const create = node('div', '', 'workshop-row');
    const newName = document.createElement('input'); newName.placeholder = 'Имя новой машины'; newName.maxLength = 40; newName.setAttribute('aria-label', 'Имя новой машины');
    create.append(newName, button('Создать меха', () => this.act({act: 'create', name: newName.value}), state.mechs.length >= 4 || this.busy)); host.append(create);
    const mech = state.mechs.find(m => m.id === this.selected);
    if (mech) {
      const form = node('section', '', 'workshop-assembly'); form.append(node('h3', 'Сборка · предпросмотр'));
      const name = document.createElement('input'); name.value = this.draftName ?? mech.name; name.oninput = () => {this.draftName = name.value;}; name.maxLength = 40; name.setAttribute('aria-label', 'Имя выбранной машины'); form.append(name);
      const draft = this.draft ??= {...mech.assembly};
      const used = new Set(state.mechs.filter(m => m.id !== mech.id).flatMap(m => Object.values(m.assembly).filter((id): id is string => !!id)));
      for (let i = 0; i < SLOTS.length; i++) {
        const slot = SLOTS[i]; const label = node('label', LABELS[i]); const select = document.createElement('select');
        select.append(new Option('Пусто', ''));
        for (const part of state.parts) if (!used.has(part.id) && (slot === 'left' || slot === 'right' ? ['weapon','shield'].includes(part.category) : part.category === slot))
          select.append(new Option(`${partName(rules, part)} · ${part.id.slice(0,6)}`, part.id));
        select.value = draft[slot] ?? ''; select.disabled = this.busy;
        select.onchange = () => {draft[slot] = select.value || null; this.render(host);}; label.append(select); form.append(label);
      }
      const def = resolveAssembly(data, mech, draft);
      let overload = 0;
      if (def) {
        const visual=node('div','','workshop-preview');visual.setAttribute('aria-label','Предпросмотр выбранной сборки');form.append(visual);this.preview.show(visual,def,rules);
        const mass = assemblyWeight(rules, def), load = rules.chassis?.[def.chassis]?.maxLoad ?? 0;
        overload = Math.max(0,mass-load);
        form.append(node('p', `Масса ${mass} / ${load}${overload ? ` · перегруз +${overload}` : ' · допустима'}`, overload ? 'sro-warn' : 'sro-ok'));
        const backpack = def.backpack ? rules.backpacks?.[def.backpack] : null;
        const armorBonus = backpack?.effect === 'armor' ? backpack.value : 0;
        const parts = [rules.bodies?.[def.body], armItem(rules,def.left),armItem(rules,def.right),rules.chassis?.[def.chassis]];
        form.append(node('p', parts.map((p,i) => `${LABELS[[0,2,3,1][i]]}: ${p?.hp ?? 0} HP / ${(p?.armor ?? 0)+armorBonus} брони`).join(' · ')));
        form.append(node('p', `Ход ${(rules.chassis?.[def.chassis]?.move ?? 0)+(backpack?.effect === 'mobility' ? backpack.value : 0)} клеток`));
        for (const [label,id] of [['Левая',def.left],['Правая',def.right]]) {
          const w = id ? rules.weapons?.[id] : null;
          if (w) form.append(node('p', `${label}: ${w.name} · урон ${Math.round(w.damage*(backpack?.effect === 'damage' ? backpack.value : 1))} · точность ${w.accuracy+(backpack?.effect === 'accuracy' ? backpack.value : 0)}% · дальность ${w.minRange}–${w.maxRange}`));
        }
      } else form.append(node('p','Нужны корпус и шасси для расчёта параметров. Недостроенную машину можно сохранить.'));
      form.append(node('p', 'Автоматические навыки'));
      for(const id of mech.skills ?? [])form.append(node('p', `${rules.skills?.[id]?.name ?? id}${!def || !skillCompatible(rules,def,id) ? ' · неактивен в этой сборке' : ''}: ${SKILL_DESCRIPTION[id] ?? ''}`));
      if(!mech.skills?.length)form.append(node('p','Ещё не открыты. Машина получает опыт в сюжетных и повторных боях.'));
      form.append(button('Сохранить сборку', () => this.act({act:'save',mech:mech.id,name:name.value,assembly:{...draft}}), this.busy || overload > 0));
      host.append(form);
    }
    const resources = node('section', '', 'workshop-resources'); resources.append(node('h3','Личный склад ресурсов'));
    for (const [id,label] of [['titanium','Титан'],['energy','Энергия'],['rareMetal','Редкий металл']]) {
      const row = node('div','', 'workshop-row'); const count = document.createElement('input'); count.type='number'; count.min='1'; count.max='1000000'; count.value='1'; count.setAttribute('aria-label',`Количество: ${label}`);
      row.append(node('span', `${label}: ${state.resources[id] ?? 0}`), count,
        button('Из трюма',()=>this.act({act:'transfer',resource:id,count:Number(count.value)}),this.busy),
        button('В трюм',()=>this.act({act:'transfer',resource:id,count:Number(count.value),toCargo:true}),this.busy)); resources.append(row);
    }
    const installed = new Set(state.mechs.flatMap(m => Object.values(m.assembly)));
    resources.append(node('p',`Свободные детали: ${state.parts.filter(p=>!installed.has(p.id)).map(p=>partName(rules,p)).join(', ') || 'нет'}`)); host.append(resources);
    const shop = node('section', '', 'workshop-catalog'); shop.append(node('h3','Каталог · выбор не расходует ресурсы'));
    const select = document.createElement('select'); select.setAttribute('aria-label','Рецепт детали');
    select.append(new Option('Выберите деталь',''));
    for (const [key,recipe] of Object.entries(rules.recipes ?? {})) {
      const option=new Option(`${partName(rules,recipe)}${(recipe.workshopLevel??1)>1?` · мастерская ур. ${recipe.workshopLevel}`:''}`,key);
      option.disabled=(recipe.workshopLevel??1)>(data.territory?.level??1);select.append(option);
    }
    select.value=this.recipe ?? ''; select.onchange=()=>{this.recipe=select.value||null;this.render(host);};shop.append(select);
    const recipe = this.recipe ? rules.recipes?.[this.recipe] : null;
    if (recipe) {
      const part = recipe.category === 'body' ? rules.bodies?.[recipe.part] : recipe.category === 'chassis' ? rules.chassis?.[recipe.part]
        : recipe.category === 'backpack' ? rules.backpacks?.[recipe.part] : armItem(rules,recipe.part);
      if (part) shop.append(node('p', `Масса ${part.weight ?? 0}${'hp' in part ? ` · ${part.hp} HP · броня ${part.armor}` : ''}${'maxLoad' in part ? ` · нагрузка до ${part.maxLoad} · ход ${part.move}` : ''}${'damage' in part ? ` · урон ${part.damage} · точность ${part.accuracy}% · дальность ${part.minRange}–${part.maxRange}` : ''}`));
      if(part&&'value' in part) {
        const baseKey=recipe.part.replace(/Mk[23]$/,'');const base=rules.backpacks?.[baseKey];
        shop.append(node('p',`Эффект: ${part.value}${base&&baseKey!==recipe.part?` · базовая версия ${base.value} · улучшение +${Math.round((part.value/base.value-1)*100)}%`:''}`));
      }
      const current=mech ? resolveAssembly(data,mech,this.draft ?? mech.assembly) : null;
      if(current)for(const slot of recipe.category==='weapon'||recipe.category==='shield' ? ['left','right'] as const : [recipe.category as 'body'|'chassis'|'backpack']) {
        const next={...current,[slot]:recipe.part};const mass=assemblyWeight(rules,next),load=rules.chassis?.[next.chassis]?.maxLoad ?? 0;
        const change=mass-assemblyWeight(rules,current);
        shop.append(node('p',`${LABELS[SLOTS.indexOf(slot)]}: после замены масса ${mass}/${load} (${change>=0?'+':''}${change}) · ${mass>load?`перегруз +${mass-load}`:'совместима по массе'}`,mass>load?'sro-warn':''));
      }
      shop.append(node('p', `${recipe.credits} кр · ${Object.entries(recipe.resources).map(([id,n])=>`${id}: ${state.resources[id] ?? 0}/${n}`).join(' · ')}`));
      shop.append(button('Купить выбранную деталь',()=>this.act({act:'buy',recipe:this.recipe!}),this.busy || data.credits<recipe.credits || Object.entries(recipe.resources).some(([id,n])=>(state.resources[id]??0)<n)));
    } host.append(shop);
    const sortie = node('section', '', 'workshop-sortie'); sortie.append(node('h3','Подготовка вылазки'));
    const missions = document.createElement('select'); missions.setAttribute('aria-label','Миссия');
    for (const id of data.available) missions.append(new Option(`${rules.missions?.[id]?.name ?? id}${state.completed.includes(id) ? ' · повтор' : ''}`,id));
    missions.value=this.mission??''; missions.onchange=()=>{this.mission=missions.value;this.render(host);};sortie.append(missions);
    const mission = this.mission ? rules.missions?.[this.mission] : null;
    const required = mission?.player?.length ?? 1;
    if (mission) {
      sortie.append(node('p',`${mission.goal} · собственных машин: ${required}`));
      sortie.append(node('p',state.completed.includes(this.mission!) ? 'Первая награда уже получена. Опыт повторного боя сохраняется.'
        : `Первая победа: ${mission.reward ?? 0} кр · ${(mission.rewardParts ?? []).map(key=>{const [category,part]=key.split(':');return partName(rules,{category,part});}).join(', ') || 'деталей нет'}`));
    }
    for (const m of state.mechs) {
      const label=node('label',m.name,'workshop-squad');const check=document.createElement('input');check.type='checkbox';check.checked=this.squad.includes(m.id);check.disabled=this.busy;
      check.onchange=()=>{this.squad=check.checked?[...this.squad,m.id]:this.squad.filter(id=>id!==m.id);this.render(host);};label.prepend(check);sortie.append(label);
    }
    sortie.append(button('Начать вылазку',()=>{this.send({t:'mechAct',act:'start',mission:this.mission!,squad:this.squad,request:crypto.randomUUID()});},this.busy || this.squad.length!==required || !this.mission));
    host.append(sortie);
    if (state.completed.includes('majorAssault')) host.append(node('p','Тихая война пройдена. Ева остаётся в мастерской.','sro-ok'));
  }
  private get busy(): boolean {return this.waiting || !this.connected;}
}
function node(tag:string,text='',className=''): HTMLElement {const n=document.createElement(tag);n.textContent=text;n.className=className;return n;}
function button(text:string,click:()=>void,disabled=false):HTMLButtonElement {const b=document.createElement('button');b.type='button';b.className='sro-btn sro-btn--sm';b.textContent=text;b.disabled=disabled;b.onclick=click;return b;}

class AssemblyPreview {
  private app:Application|null=null;
  private rig:MechRig|null=null;
  private loading:Promise<void>|null=null;
  private revision=0;
  async show(host:HTMLElement,def:MechUnitDef,rules:MechRules):Promise<void> {
    const revision=++this.revision;
    this.loading??=(async()=>{
      await loadMechArt();
      const app=new Application();await app.init({width:280,height:180,backgroundAlpha:0,resolution:Math.min(devicePixelRatio,2),autoDensity:true});
      app.stop();this.app=app;this.rig=new MechRig(220);this.rig.view.position.set(140,160);app.stage.addChild(this.rig.view);
    })().catch(e=>{console.warn('assembly preview unavailable',e);});
    await this.loading;
    if(revision!==this.revision || !this.app || !this.rig)return;
    this.rig.configure(def,rules);this.rig.set(2,new Set());this.app.render();host.append(this.app.canvas);
  }
}
