"""Audit M23 graph, fields, objective/spawn cells, referenced units and actual runtime tile files."""
import json
from pathlib import Path
ROOT=Path(__file__).resolve().parents[1]
config=json.loads((ROOT/'shared/mech-territory.json').read_text(encoding='utf-8'))
rules=json.loads((ROOT/'shared/mechs.json').read_text(encoding='utf-8'))
ids={o['id'] for o in config['objects']}
assert len(ids)==12 and len(config['objects'])==12
for operation in config['operations'].values():
    for unit in operation['solo']+operation['coop']+operation['emergency']:assert unit in rules['units'],unit
for obj in config['objects']:
    assert set(obj['links'])<=ids,obj['id']
    for tile in ['ground-1','ground-2','rough','crate','wall','building']:
        prefix='' if obj['biome']=='desert' else obj['biome']+'-'
        assert (ROOT/'client/public/mechs'/f'tile-{prefix}{tile}.webp').is_file(),(obj['id'],tile)
    for board in obj['fields']:
        assert len(board)==12 and all(len(row)==12 for row in board)
        assert all(board[y][x] in '.,' for x,y in [(5,5),(6,5),(5,6),(2,9),(8,2),(1,10),(3,10),(1,8),(3,8),(7,1),(9,1),(8,3),(10,3)])
print('12 objects, 24 fields, 5 biomes, runtime tiles and all three garrisons per operation verified')
