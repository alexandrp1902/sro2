// Пираты из shared/npcs.json — то, что сервер присылает клиенту вместе с балансом.
// Поведение пиратов целиком на сервере (server/Sro.Server/Game/PirateBrain.cs).

export interface NpcType {
  name: string;
  /** Корпус из hulls.json: по нему клиент узнаёт тип пирата — в снапшоте типа нет. */
  hull?: string;
}

export interface NpcSpawn {
  type: string;
  level: number;
  x: number;
  y: number;
  count?: number;
}

export interface NpcRules {
  /** Укрытие вокруг станции: пираты сюда не залетают. */
  stationSafeRadius: number;
  patrolRadius: number;
  types?: Record<string, NpcType>;
  spawns?: NpcSpawn[];
}
