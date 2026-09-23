# Заставка стартового экрана: art/splash/*.png -> client/public/splash/*.webp.
# Отдельно от dock.py: там сцены дока 4:3 и приведение растяжением, а здесь кадр 16:9 во весь
# экран — генератор отдаёт почти квадрат, и его надо обрезать по центру, а не растягивать.
# Перезапускать только после новых картинок — результат лежит в git.
#   python tools/splash.py

from pathlib import Path

from PIL import Image, ImageOps

ROOT = Path(__file__).resolve().parent.parent
SRC = ROOT / "art" / "splash"
OUT = ROOT / "client" / "public" / "splash"

SIZE = (1920, 1080)
# Ниже, чем у сцен дока: этот кадр качается первым, до всего остального.
QUALITY = 82

# (файл под art/splash, имя в игре). Имя — это то, что ищет START_ART в ui/pilotForm.ts.
SPLASHES = [
    ("splash-cockpit", "cockpit"),
]


def main() -> None:
    OUT.mkdir(parents=True, exist_ok=True)
    for source, name in SPLASHES:
        path = SRC / f"{source}.png"
        if not path.exists():
            print(f"{name:12} пропущен: нет {path.relative_to(ROOT)}")
            continue
        image = ImageOps.fit(Image.open(path).convert("RGB"), SIZE, Image.LANCZOS, centering=(0.5, 0.5))
        target = OUT / f"{name}.webp"
        image.save(target, "WEBP", quality=QUALITY, method=6)
        print(f"{name:12} {SIZE[0]}x{SIZE[1]}  {target.stat().st_size // 1024} KB")


if __name__ == "__main__":
    main()
