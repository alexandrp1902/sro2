import type { PlayerDto } from './protocol';

export type RosterEvent =
  | { kind: 'joined' | 'lost' | 'back' | 'left'; name: string }
  | { kind: 'renamed'; name: string; from: string };

/** Игроки системы. Из разницы двух списков получаются события для ленты: вошёл, потерял связь, вернулся, вышел. */
export class Roster {
  private players = new Map<number, PlayerDto>();
  private fresh = true;

  /** Новая сессия: первый список после welcome — это «кто уже здесь», а не события. */
  reset(): void {
    this.players.clear();
    this.fresh = true;
  }

  get(id: number): PlayerDto | undefined {
    return this.players.get(id);
  }

  get onlineCount(): number {
    let count = 0;
    for (const player of this.players.values()) if (player.online) count++;
    return count;
  }

  /** @param ownId о себе события не нужны */
  update(players: PlayerDto[], ownId: number): RosterEvent[] {
    const next = new Map(players.map((player) => [player.id, player]));
    const events: RosterEvent[] = [];
    if (!this.fresh) {
      for (const player of players) {
        if (player.id === ownId) continue;
        const was = this.players.get(player.id);
        if (!was) events.push({ kind: 'joined', name: player.name });
        else {
          if (was.name !== player.name) events.push({ kind: 'renamed', name: player.name, from: was.name });
          if (was.online && !player.online) events.push({ kind: 'lost', name: player.name });
          else if (!was.online && player.online) events.push({ kind: 'back', name: player.name });
        }
      }
      for (const [id, was] of this.players) {
        if (id !== ownId && !next.has(id)) events.push({ kind: 'left', name: was.name });
      }
    }
    this.players = next;
    this.fresh = false;
    return events;
  }
}
