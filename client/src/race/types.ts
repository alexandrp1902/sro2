export interface Vec { x: number; y: number }
export interface Gate { a: Vec; b: Vec; normal: Vec }
export interface Wall { id: string; a: Vec; b: Vec; reason: string }
export interface Obstacle { id: string; center: Vec; radius: number; reason: string }
export interface Track {
  id: string; version: string; name: string; lapCount: number; spawn: Vec; spawnHeading: number;
  environment: string; isActive: boolean; centerline: Vec[]; walls: Wall[]; obstacles: Obstacle[];
  checkpoints: Gate[]; finishLine: Gate; length: number;
}
export interface Rules {
  id: string; physicsVersion: string; vehicleVersion: string;
  radius: number; hp: number; engineAccel: number; engineLimit: number; dragK: number; lateralK: number;
  brakeAccel: number; turnFast: number; turnSlow: number; angularAccel: number; headingGain: number;
  boostAccel: number; boostLimit: number; boostSeconds: number; cooldownSeconds: number;
  fatalSpeed: number; restitution: number; tangentLoss: number; eventEpsilon: number;
  contactSlop: number; maxContacts: number; countdownSeconds: number; inputTimeoutSeconds: number;
  abortTimeoutSeconds: number; loadTimeoutSeconds: number; timeLimitSeconds: number; overloadSeconds: number;
}
export interface Config { track: Track; rules: Rules; hash: string; rankingKey: string }
export interface Input {
  seq: number; steerMode: 'turn' | 'heading'; heading: number; turn: number; throttle: number;
  brake: boolean; boostPressId: number; controlActive: boolean;
}
export interface Snapshot {
  sessionId: string; tick: number; ackSeq: number; state: string; position: Vec; velocity: Vec;
  heading: number; omega: number; hp: number; boostState: string; boostRemainingMs: number;
  countdownMs: number; lap: number; nextCp: number; elapsedMs: number; lapElapsedMs: number; lapTimesMs: number[]; boostPressId: number;
}
export interface Result {
  sessionId: string; playerId: string | null; name: string; rankingKey: string; status: string;
  reason: string | null; elapsedMs: number; totalMs: number | null; lapTimesMs: number[]; finishedAt: string;
}
export interface Rank { rank: number; name: string; timeMs: number; achievedAt: string }
export interface Board { rankingKey: string; kind: string; rows: Rank[]; self: Rank | null }
export interface RaceEvent {
  t: 'raceEvent'; event: string; requestId?: string; sessionId?: string; config?: Config | null;
  snapshot?: Snapshot | null; result?: Result | null; board?: Board | null;
  validationStatus?: string; code?: string;
}
export type RaceCommand = { t: 'raceAct'; act: 'join' | 'ready' | 'input' | 'abort' | 'result' | 'leaderboard';
  requestId?: string; sessionId?: string; configHash?: string; input?: Input; kind?: 'total' | 'lap' };
