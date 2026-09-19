# Additional ship hull artwork

Six hulls generated with the built-in image_gen tool: scout, interceptor, freighter, frigate, cruiser, industrial mining/salvage vessel.

Original PNG files here preserve generator resolution and transparency. Runtime WebP exports are in `client/public/sprites/ships-{name}.webp`, trimmed to the hull and scaled proportionally to a maximum dimension of 512 px. They have true alpha, face upward, and contain no engine exhaust. Exact dimensions are in `manifest.json`; prompts are in `prompts.md`.

These are artwork assets, not new playable hull definitions. To make them selectable, the game implementation must add hull rules and sprite mappings in `shared/hulls.json`, `client/src/render/sprites.ts`, and sprite metadata/loading. Engine flames require separate assets/placement if needed. The existing `tools/sprites.py` processes the original sprite sheets and does not register these standalone exports.

Planet backgrounds are ready at `client/public/dock/planet-{office,trader,shipyard,hangar}.webp` (1600 × 1200), following the existing dock URL convention; their display depends on implementing planetary landing.
