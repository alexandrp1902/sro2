export interface TerritoryDebt { titanium: number; energy: number }
export interface TerritoryReward { credits: number; titanium: number; energy: number; rareMetal: number; part?: string | null; title?: string | null }
export interface TerritoryProfile { level: number; debts?: Record<string,TerritoryDebt>; emergencyAt?: string | null; titles?: string[]; evaStep: number; evaAccepted?: string | null; evaDone: boolean }
export interface TerritoryObject { id: string; control: number; owner: string; revision: number; active?: string | null; defenseAt?: string | null }
export interface TerritoryObjectDef { id: string; name: string; type: string; x: number; y: number; biome: string; links: string[]; startControl: number; fields: string[][] }
export interface TerritoryOperation { name: string; goal: string; winControl: number; loseControl: number; contribution: number; credits: number; objective: string }
export interface TerritoryConfig { seasonEpoch: string; seasonSeconds: number; daySeconds: number; activationSeconds: number; place: string; baseLinks: string[]; objects: TerritoryObjectDef[]; operations: Record<string,TerritoryOperation> }
export interface TerritoryClaim { id: string; account: string; season: number; kind: string; reward: TerritoryReward; claimed: boolean }
export interface TerritoryArchive { season: number; objects: TerritoryObject[]; contributions: Record<string,number>; ended: string }
export interface TerritoryWorld { season: number; seasonEnd: string; revision: number; objects: TerritoryObject[]; contributions: Record<string,number>; claims: TerritoryClaim[]; archives: TerritoryArchive[]; nextStrike: string }
export interface TerritoryReady { account: string; name: string; squad: string[]; ready: boolean }
export interface TerritoryPreparation { id: string; leader: string; object: string; operation: string; expires: string; players: TerritoryReady[] }
export interface TerritoryStateMsg { t: 'territoryState'; world: TerritoryWorld; config: TerritoryConfig; profile: TerritoryProfile; unlocked: boolean; docked: boolean; preparation?: TerritoryPreparation | null; refused?: string | null; requestId?: string | null; receipt?: string | null; waiting: boolean; account: string }
export interface TerritoryAct { t: 'territoryAct'; act: string; requestId?: string; revision?: number; season?: number; object?: string; operation?: string; squad?: string[]; preparation?: string; partner?: string; claim?: string; mech?: string }
export function serviceCost(lost: number, max: number, discount: boolean): TerritoryDebt {
  return max<=0 || lost<=0 ? {titanium:0,energy:0} : {titanium:Math.ceil(4*Math.min(lost,max)/max*(discount?.8:1)),energy:Math.ceil(2*Math.min(lost,max)/max*(discount?.8:1))};
}
export function resetControl(control: number, start: number): number { return Math.floor(start+(control-start)*2/3+.5); }
export function cargoVolume(hull: string, resource: string, volume: number): number { return hull==='dropship' && ['titanium','energy','rareMetal'].includes(resource) ? volume/1.2 : volume; }
export function distribute(pool: number, contributions: Record<string,number>): Record<string,number> {
  const entries=Object.entries(contributions).filter(([,n])=>n>0).sort(([a],[b])=>a<b?-1:a>b?1:0), sum=entries.reduce((n,[,v])=>n+BigInt(v),0n), total=BigInt(pool);
  if (!sum) return {};
  const result=Object.fromEntries(entries.map(([id,n])=>[id,Number(total*BigInt(n)/sum)]));
  const remainder=pool-Object.values(result).reduce((a,b)=>a+b,0);
  entries.sort(([a,x],[b,y])=>{const rx=total*BigInt(x)%sum,ry=total*BigInt(y)%sum;return rx>ry?-1:rx<ry?1:a<b?-1:a>b?1:0;}).slice(0,remainder).forEach(([id])=>result[id]++);
  return result;
}
export function accessible(config: TerritoryConfig, objects: TerritoryObject[], id: string): boolean {
  const owned=new Set(objects.filter(o=>o.owner==='resistance').map(o=>o.id));
  return owned.has(id)||config.baseLinks.includes(id)||config.objects.some(o=>owned.has(o.id)&&o.links.includes(id));
}
export function availableOperations(object: TerritoryObject): string[] {
  return object.owner==='resistance' ? [...(object.defenseAt?['defense']:[]),'escort','rescue'] : ['assault','sabotage','rescue'];
}
