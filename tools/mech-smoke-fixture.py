"""Isolated M22 smoke catalog: normal mech rules, ranger starts docked at Nova Prime.

Run with SRO_STORY_SKIP=quietWar to test ground operations independently of space travel.
Never rewrites the game's shared data or the user's account directory.
"""
import json
import pathlib
import re
import shutil

ROOT=pathlib.Path(__file__).resolve().parents[1]
OUT=ROOT/'.artifacts'/'m22-shared'
OUT.mkdir(parents=True,exist_ok=True)
for source in (ROOT/'shared').iterdir():
    if source.is_file():shutil.copyfile(source,OUT/source.name)
path=OUT/'careers.json'
data=json.loads(re.sub(r'//[^\n]*','',path.read_text(encoding='utf-8')))
data['careers']['ranger'].update(system='nova',place='pl:novaPrime')
path.write_text(json.dumps(data,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
print(OUT)
