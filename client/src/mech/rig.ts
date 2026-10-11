import { Assets, Container, Graphics, Sprite, Texture } from 'pixi.js';
import meta from './rigMeta.json';
import catalogData from './artCatalog.json';
import auxiliaryData from './auxCatalog.json';
import { DIRECTION_NAMES, STEPS, type MechPart, type MechRules, type MechUnitDef } from './rules';

/**
 * Мех из слоёв (порт art/mechs/production/rig-renderer.js): шасси, корпус и две руки, у каждого слоя на каждое из 8
 * направлений свой кадр, сдвиг, масштаб и порядок. Числа — из medium-rig.json, нарезку делает tools/mechs.py.
 * P3 supplies step cycles, disabled chassis, destroyed sockets and wrecks.
 */

type Layer = 'chassis' | 'body' | 'left' | 'right' | 'backpack';
const LAYERS: Layer[] = ['chassis', 'body', 'left', 'right', 'backpack'];
type ArtComponent = {cellSize:number; pivot:number[]; frames:Record<string,{texture:string;x:number;y:number;scale:number;z:number}>};
const CATALOG = catalogData as Record<string,ArtComponent>;
type Transform = {x:number;y:number;scale:number;z:number};
type Auxiliary = {textures:string[];transforms:Transform[];group:string};
const AUXILIARY = auxiliaryData as Record<string,Auxiliary>;

/** Эффекты боя (пачка E): попадание, взрыв, блок щитом. Кадров у каждого — сколько нарезал tools/mechs.py. */
export type FxName = 'hit' | 'explosion' | 'block';
export const FX_FRAMES: Readonly<Record<FxName, number>> = (meta as { fx?: Record<FxName, number> }).fx ?? {
  hit: 0,
  explosion: 0,
  block: 0,
};

/** Кадры эффекта по порядку; пусто — эффект не загрузился, бой идёт и без него. */
export function fxTextures(name: FxName): Texture[] {
  const out: Texture[] = [];
  for (let i = 0; i < FX_FRAMES[name]; i++) {
    const texture = textures.get(`fx-${name}-${i}`);
    if (texture) out.push(texture);
  }
  return out;
}

/** Reviewed desert terrain exported by mech-catalog.py. */
export const TILE_NAMES = ['ground-1', 'ground-2', 'rough', 'crate', 'wall', 'building'] as const;
export type TileName = (typeof TILE_NAMES)[number];

const textures = new Map<string, Texture>();

export function mechUrl(name: string): string {
  return `mechs/${name}.webp`;
}

export function mechTexture(name: string): Texture {
  return textures.get(name) ?? Texture.EMPTY;
}

let loading: Promise<void> | null = null;

/** Все слои и тайлы — один раз, при первом бое: в космосе они не нужны. */
export function loadMechArt(): Promise<void> {
  loading ??= (async () => {
    const names = [
      ...LAYERS.flatMap((layer) => DIRECTION_NAMES.map((d) => `${layer}-${d}`)),
      ...Object.values(CATALOG).flatMap(entry=>Object.values(entry.frames).map(f=>f.texture)),
      ...Object.values(AUXILIARY).flatMap(entry=>entry.textures),
      ...TILE_NAMES.map((t) => `tile-${t}`),
      ...['ice','barren','jungle','station-interior'].flatMap(b=>TILE_NAMES.map(t=>`tile-${b}-${t}`)),
      ...(Object.keys(FX_FRAMES) as FxName[]).flatMap((fx) => Array.from({ length: FX_FRAMES[fx] }, (_, i) => `fx-${fx}-${i}`)),
    ];
    const available = [...new Set(names.filter(name=>!name.startsWith('backpack-')))];
    const loaded = await Assets.load(available.map((name) => ({ alias: `mech:${name}`, src: mechUrl(name), data: { autoGenerateMipmaps: true } })));
    for (const name of available) textures.set(name, loaded[`mech:${name}`] as Texture);
  })().catch((e) => {
    // Нет картинок — бой всё равно играется: мехи рисуются пустыми, а поле — цветом клеток.
    console.warn('mech art failed to load', e);
    loading = null;
  });
  return loading;
}

type Frame = { layers: Record<string, { x: number; y: number; scale: number; z: number }>; muzzle: number[] };
const FRAMES = meta.frames as Record<string, Frame>;
const CELL = meta.cellSize;
const [PIVOT_X, PIVOT_Y] = meta.groundPivot;

export class MechRig {
  readonly view = new Container();
  private readonly sprites = new Map<Layer, Sprite>();
  private dir = -1;
  private broken = new Set<MechPart>();
  private readonly schematic = new Graphics();
  private keys: Partial<Record<Layer,string>> | null = null;
  private definition: MechUnitDef | null = null;
  private objective = false;
  private objectiveKey = 'generator';
  private dead = false;
  private phase = -1;
  private readonly auxiliary = new Sprite(Texture.EMPTY);

  /**
   * @param size сторона квадрата рига на экране, px: в нём 512 единиц рига
   * @param tint оттенок врага — стенд-ин до своих спрайтов P2
   */
  constructor(size: number, tint = 0xffffff) {
    const k = size / CELL;
    this.view.scale.set(k);
    // Опорная точка рига — где мех стоит на земле; её и ставим на клетку.
    this.view.pivot.set(PIVOT_X, PIVOT_Y);
    this.view.sortableChildren = true;
    this.view.addChild(this.schematic);
    this.view.addChild(this.auxiliary);
    this.auxiliary.visible = false;
    for (const layer of LAYERS) {
      const sprite = new Sprite(Texture.EMPTY);
      sprite.tint = tint;
      this.sprites.set(layer, sprite);
      this.view.addChild(sprite);
    }
  }

  configure(def: MechUnitDef | null, rules: MechRules, objective=false, objectiveKey='generator'): void {
    const arm = (id: string | null | undefined,hand:string) => id ? `${rules.weapons?.[id]?.art ?? rules.shields?.[id]?.art}-${hand}` : undefined;
    const keys=def ? {body:rules.bodies?.[def.body]?.art,chassis:rules.chassis?.[def.chassis]?.art,
      left:arm(def.left,'left'),right:arm(def.right,'right'),backpack:def.backpack ? rules.backpacks?.[def.backpack]?.art : undefined} : null;
    if (JSON.stringify(keys)!==JSON.stringify(this.keys) || objective!==this.objective || objectiveKey!==this.objectiveKey) this.dir=-1;
    this.keys=keys;this.definition=def;this.objective=objective;
    this.objectiveKey=objectiveKey;
  }

  /** Направление 0..7, и какие руки разбиты. */
  set(dir: number, broken: ReadonlySet<MechPart>, dead=false, phase=-1): void {
    const same = broken.size === this.broken.size && [...broken].every((l) => this.broken.has(l));
    if (dir === this.dir && same && dead===this.dead && phase===this.phase) return;
    this.dir = dir;
    this.broken = new Set(broken);
    this.dead=dead;this.phase=phase;
    const name = DIRECTION_NAMES[dir];
    const frame = FRAMES[name];
    this.schematic.clear();
    this.auxiliary.visible=false;
    const special=this.objective ? `${this.objectiveKey}-${dead?'destroyed':'intact'}`
      : dead ? `wreck-${this.keys?.body?.replace('body-','') ?? 'medium'}` : null;
    if(special && AUXILIARY[special]) {
      for(const sprite of this.sprites.values())sprite.visible=false;
      this.drawAuxiliary(this.auxiliary,special,0);
      return;
    }
    for (const [layer, sprite] of this.sprites) {
      const entry=this.keys?.[layer] ? CATALOG[this.keys[layer]!] : null;
      const art=entry?.frames[name];
      const t = this.keys ? art : frame.layers[layer];
      const chassis=this.keys?.chassis?.replace('chassis-','') ?? 'medium-biped';
      const replacement=(layer==='left'||layer==='right') && broken.has(layer) ? `broken-socket-${layer}`
        : layer==='chassis' && broken.has('chassis') ? `disabled-${chassis}`
        : layer==='chassis' && phase>=0 ? `walk-${chassis}` : null;
      if(replacement && AUXILIARY[replacement]) {
        this.drawAuxiliary(sprite,replacement,replacement.startsWith('walk-')?(phase%4)*8+dir:dir);
        // Preserve anatomical near/far ordering of the destroyed socket.
        if(layer==='left'||layer==='right')sprite.zIndex=t?.z ?? 3;
        continue;
      }
      const hidden=(layer==='left'||layer==='right') && broken.has(layer);
      if (!t || hidden) {
        sprite.visible = false;
        if (!hidden && this.keys?.[layer]) this.drawMissing(layer,dir,broken);
        continue;
      }
      const edge = CELL * t.scale;
      sprite.visible = true;
      if (layer==='chassis') sprite.tint=broken.has('chassis') ? 0x754a48 : 0xffffff;
      sprite.texture = mechTexture(art?.texture ?? `${layer}-${name}`);
      sprite.width = edge;
      sprite.height = edge;
      sprite.position.set(entry ? PIVOT_X+(entry.cellSize/2-entry.pivot[0]+t.x)*CELL/entry.cellSize-edge/2 : CELL/2-edge/2+t.x,
        entry ? PIVOT_Y+(entry.cellSize/2-entry.pivot[1]+t.y)*CELL/entry.cellSize-edge/2 : CELL/2-edge/2+t.y);
      sprite.zIndex = t.z;
    }
  }

  private drawAuxiliary(sprite:Sprite,key:string,index:number):void {
    const entry=AUXILIARY[key],t=entry.transforms[index],edge=CELL*t.scale;
    sprite.texture=mechTexture(entry.textures[index]);sprite.tint=0xffffff;
    sprite.visible=true;sprite.width=edge;sprite.height=edge;
    sprite.position.set(CELL/2-edge/2+t.x,CELL/2-edge/2+t.y);sprite.zIndex=t.z;
  }

  /** Missing production files get a literal schematic, never another weapon's PNG. */
  private drawMissing(layer:Layer,dir:number,broken:ReadonlySet<MechPart>):void {
    const g=this.schematic,def=this.definition;const [dx,dy]=STEPS[dir];const color=0x97aabd;
    if(this.objective){if(layer==='body')g.roundRect(175,265,160,135,12).fill(0x425361).stroke({color:0xd9c08a,width:8});return;}
    if(layer==='body') {const w=def?.body.includes('heavy')?155:110;g.roundRect(256-w/2,250,w,100,15).fill(0x425361).stroke({color,width:5});}
    else if(layer==='chassis') {
      const chassisColor=broken.has('chassis')?0x754a48:color;
      g.roundRect(191,370,45,65,8).roundRect(276,370,45,65,8).fill(0x35414c).stroke({color:chassisColor,width:6});
    } else if(layer==='backpack')g.roundRect(225,275,62,45,8).stroke({color:0xd9c08a,width:6});
    else {
      const sign=layer==='left'?-1:1,x=256+sign*95,y=325;
      const id=layer==='left'?def?.left:def?.right;
      if(id==='sword')g.moveTo(x,y).lineTo(x+dx*95,y+dy*95-15).stroke({color:0xe3d4aa,width:12});
      else if(id==='light'||id==='heavy')g.poly([x-25,y-45,x+25,y-45,x+32,y+15,x,y+42,x-32,y+15]).fill(0x65788b).stroke({color,width:5});
      else {g.circle(x,y,18).fill(0x425361).stroke({color,width:5});g.moveTo(x,y).lineTo(x+dx*60,y+dy*60-15).stroke({color,width:id==='heavy-cannon'?18:10});}
    }
  }

  /** Дуло в координатах рига относительно опорной точки — туда рисуется трассер. */
  muzzle(dir: number): [number, number] {
    const [x, y] = FRAMES[DIRECTION_NAMES[dir]].muzzle;
    return [(x - PIVOT_X) * this.view.scale.x, (y - PIVOT_Y) * this.view.scale.y];
  }
}
