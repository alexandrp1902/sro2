"""Export existing P4 source tiles to runtime WebP; this does not certify visual acceptance.
Desert continues using the calibrated M22 assets. Run from any directory.
"""
from pathlib import Path
from PIL import Image

ROOT = Path(__file__).resolve().parents[1]
for biome in ['ice', 'barren', 'jungle', 'station-interior']:
    for tile in ['ground-1', 'ground-2', 'rough', 'crate', 'wall', 'building']:
        source = ROOT / 'art/mechs/tiles' / f'{biome}-{tile}.png'
        target = ROOT / 'client/public/mechs' / f'tile-{biome}-{tile}.webp'
        image = Image.open(source).convert('RGBA')
        side = 256 if tile == 'building' else 128
        image.resize((side, side), Image.Resampling.LANCZOS).save(target, 'WEBP', quality=88, method=6)
        print(target.name)
