# Additional equipment artwork

Generated with the built-in image_gen tool. Each PNG is a standalone source image with true alpha. WebP exports live in `client/public/sprites/`, trimmed with preserved proportions, at most 256 px along the longest side. These are inventory/dock icons in three-quarter view, not top-down weapon mounts or firing animations.

| File stem | Item | Intended use |
| --- | --- | --- |
| weapons-cannon | Ballistic cannon | Dedicated art for existing `cannon` |
| weapons-heavy-laser | Heavy laser | Dedicated art for existing `heavyLaser` |
| weapons-railgun | Railgun | Proposed new weapon |
| weapons-ion | Ion disruptor | Proposed new weapon |
| weapons-point-defense | Point-defense autocannon | Proposed new weapon |
| weapons-torpedoes | Torpedo launcher | Proposed new weapon |
| modules-reactor | Fusion reactor | Generator slot art |
| modules-fuel-tank | Fuel tank assembly | Tank slot art |
| modules-military-radar | Military phased-array radar | Radar slot variant |
| modules-afterburner | Twin-nozzle afterburner | Engine slot variant |
| modules-repair | Automated repair unit | Proposed new module |
| modules-cooling | Cooling system | Proposed new module |

Art only: no new gameplay rules, statistics or item mappings are applied. Register the selected images in sprite metadata and mappings before using them in game. New item types also require gameplay definitions. The existing sheet slicing script does not register these standalone exports.

See `prompts.md` for exact prompts and `manifest.json` for exported dimensions and paths. Original PNGs remain here for future higher-resolution exports.
