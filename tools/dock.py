# Фоны экрана дока: art/dock/**/*.png -> client/public/dock/*.webp.
# Исходники генератор отдаёт в 4:3, но не ровно 1600x1200 — приводим к одному размеру,
# иначе сцены заметно «дышат» при переключении вкладок.
# Перезапускать только после новых картинок — результат лежит в git.
#   python tools/dock.py

from pathlib import Path

from PIL import Image

ROOT = Path(__file__).resolve().parent.parent
SRC = ROOT / "art" / "dock"
OUT = ROOT / "client" / "public" / "dock"

SIZE = (1600, 1200)
QUALITY = 88

# (файл под art/dock, имя в игре). Имя — это то, что ищет sceneUrl в ui/dockScreen.ts.
SCENES = [
    # Свой офис у станций рейнджеров (пачка C): «Пост рейнджеров Барнарда».
    ("ranger/station-ranger-office", "ranger-office"),

    # Поселения на планетах (пачка D, M15): пять биомов и орбитальная платформа газового гиганта.
    # Земного набора здесь нет: земное поселение рисуют общие сцены planet-*, нарезанные раньше.
    ("planet-settlements/desert-office", "desert-office"),
    ("planet-settlements/desert-trader", "desert-trader"),
    ("planet-settlements/desert-shipyard", "desert-shipyard"),
    ("planet-settlements/desert-hangar", "desert-hangar"),
    ("planet-settlements/ice-office", "ice-office"),
    ("planet-settlements/ice-trader", "ice-trader"),
    ("planet-settlements/ice-shipyard", "ice-shipyard"),
    ("planet-settlements/ice-hangar", "ice-hangar"),
    ("planet-settlements/jungle-office", "jungle-office"),
    ("planet-settlements/jungle-trader", "jungle-trader"),
    ("planet-settlements/jungle-shipyard", "jungle-shipyard"),
    ("planet-settlements/jungle-hangar", "jungle-hangar"),
    ("planet-settlements/lava-office", "lava-office"),
    ("planet-settlements/lava-trader", "lava-trader"),
    ("planet-settlements/lava-shipyard", "lava-shipyard"),
    ("planet-settlements/lava-hangar", "lava-hangar"),
    ("planet-settlements/barren-office", "barren-office"),
    ("planet-settlements/barren-trader", "barren-trader"),
    ("planet-settlements/barren-shipyard", "barren-shipyard"),
    ("planet-settlements/barren-hangar", "barren-hangar"),
    ("planet-settlements/orbital-platform-office", "orbital-platform-office"),
    ("planet-settlements/orbital-platform-trader", "orbital-platform-trader"),
    ("planet-settlements/orbital-platform-shipyard", "orbital-platform-shipyard"),
    ("planet-settlements/orbital-platform-hangar", "orbital-platform-hangar"),
]


def main() -> None:
    OUT.mkdir(parents=True, exist_ok=True)
    for source, name in SCENES:
        path = SRC / f"{source}.png"
        if not path.exists():
            print(f"{name:24} пропущен: нет {path.relative_to(ROOT)}")
            continue
        image = Image.open(path).convert("RGB").resize(SIZE, Image.LANCZOS)
        target = OUT / f"{name}.webp"
        image.save(target, "WEBP", quality=QUALITY, method=6)
        print(f"{name:24} {SIZE[0]}x{SIZE[1]}  {target.stat().st_size // 1024} KB")


if __name__ == "__main__":
    main()
