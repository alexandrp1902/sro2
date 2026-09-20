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
