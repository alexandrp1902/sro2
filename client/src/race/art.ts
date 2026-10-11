export const liveries = ['copper', 'blue', 'yellow', 'white'] as const;
export type Livery = typeof liveries[number];
export const raceAssets = [
  ...liveries.map(l => `pod-${l}-v1`), 'pod-exhaust-v1', 'pod-energy-link-v1',
  'canyon-sand-tile-v1', 'canyon-road-tile-v1', 'canyon-rock-tile-v1',
  'canyon-bank-a-v1', 'canyon-bank-b-v1', 'canyon-spur-v1',
  'canyon-rock-small-v1', 'canyon-rock-large-v1', 'canyon-rock-cluster-v1',
  'canyon-column-v1', 'canyon-pipe-v1', 'canyon-scrap-v1', 'canyon-beacon-v1',
  'race-arrow-v1', 'race-start-stripe-v1', 'race-checkpoint-post-v1',
];
export const artUrl = (name: string) => `${import.meta.env.BASE_URL}race/${name}.webp`;
// Coordinates measured on the accepted 512px sprites, normalized to their full canvas.
export const podLayout = {
  copper: { seat: .195, nozzle: .60, engineY: .267, engineStart: .59 },
  blue: { seat: .174, nozzle: .52, engineY: .255, engineStart: .52 },
  yellow: { seat: .170, nozzle: .59, engineY: .247, engineStart: .59 },
  white: { seat: .172, nozzle: .51, engineY: .236, engineStart: .51 },
};
