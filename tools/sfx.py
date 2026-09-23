"""Синтез звуков игры: client/public/sfx/*.mp3 и client/src/audio/sfxMeta.json.

Запуск из корня репозитория: python tools/sfx.py  (нужны numpy, scipy, soundfile)
Перезапускать только после правки рецептов — результат лежит в git, как и спрайты.

Почему синтез, а не записи: записанных звуков взять негде, а рисовать их кодом игра уже умеет — тем же
способом сделаны звёзды, пламя и взрывы в картинке. Заодно тембр выстрела выводится прямо из shared/weapons.json:
kind задаёт вид звука, class S/M/L — высоту (крупный ствол ниже и длиннее), damageType — есть ли механический
слой (у кинетики лязгает затвор, у энергии нет).

Каждый звук пишется в нескольких вариантах с разными зёрнами шума: в бою одна и та же волна, повторённая
десять раз в секунду, слышна как дефект. Клиент дополнительно сдвигает высоту варианта на ±6%.

Файлы нормализованы по пику: так mp3 тратит биты на сам звук, а не на тишину. Относительная громкость
звуков живёт не в файле, а в поле gain манифеста — её правят, не перерисовывая звук.
"""

from __future__ import annotations

import json
import sys
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))

import synth as S

ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / "client" / "public" / "sfx"
META = ROOT / "client" / "src" / "audio" / "sfxMeta.json"

# Пик записи. Не 1.0: mp3 после декодирования местами выходит выше исходного, и на 1.0 это слышно как треск.
PEAK = 0.89
QUALITY = 0.4


# --- выстрелы ----------------------------------------------------------------------------------------

def shot_bolt(rng):
    """Импульсная и баллистическая: тугой «пыщ» с падающим тоном и лязгом затвора."""
    n = S.n_of(0.17)
    body = S.osc(S.ramp(430, 105, n), n, "saw") * S.env(n, 0.002, 0.16, power=3.0)
    body = S.band(body, 260, 2600)
    click = S.noise(S.n_of(0.01), rng) * S.env(S.n_of(0.01), 0.0005, 0.009, power=4.0)
    click = S.biquad(click, 2200, "hp") * 0.5
    return S.mix(body, click)


def shot_beam(rng):
    """
    Лазер: тяжёлый режущий луч, а не писк.

    Первая версия держала две пилы на 1400 и 2100 Гц — получался звук игрового автомата. Теперь
    основа лежит внизу (180 Гц и квинта), сверху идёт шипение плазмы, а «режет» луч низкая
    амплитудная модуляция: ухо слышит в ней работу, а не тон.
    """
    n = S.n_of(0.2)
    core = S.osc(180.0, n, "saw") + 0.7 * S.osc(270.6, n, "saw") + 0.4 * S.osc(90.0, n)
    grind = 0.55 + 0.45 * S.osc(72.0, n)  # рычание: модуляция громкости, а не высоты
    hiss = S.band(S.noise(n, rng), 300, 2000) * 0.5
    x = (core * grind + hiss) * S.env(n, 0.006, 0.09, hold=0.1)
    x = S.sweep(x, S.ramp(2400, 900, n), q=1.2)
    return S.biquad(S.drive(x, 2.6), 3400, "lp")


def shot_orb(rng):
    """Плазма: частотная модуляция и закрывающийся фильтр дают тяжёлый «вуф»."""
    n = S.n_of(0.3)
    freq = 180 + 200 * S.osc(60.0, n)
    x = S.osc(freq, n) * S.env(n, 0.006, 0.29, power=2.0)
    x = S.sweep(x, S.ramp(1900, 520, n), q=1.4)
    sub = S.osc(S.ramp(96, 58, n), n) * S.env(n, 0.004, 0.2) * 0.6
    return S.mix(x, sub)


def shot_rail(rng):
    """Рельса: свист разгона и низкий удар. Самый громкий одиночный выстрел — перезарядка у неё длинная."""
    n = S.n_of(0.26)
    whistle = S.sweep(S.noise(n, rng), S.ramp(3400, 620, n), q=3.5, mode="bp")
    whistle *= S.env(n, 0.002, 0.24, power=2.2)
    hit = S.osc(S.ramp(95, 42, n), n) * S.env(n, 0.001, 0.18, power=3.0)
    crack = S.biquad(S.noise(S.n_of(0.006), rng), 3000, "hp") * S.env(S.n_of(0.006), 0.0004, 0.005, power=4.0)
    return S.mix(whistle * 0.8, hit * 0.9, crack * 0.7)


def shot_ion(rng):
    """
    Ионка: тяжёлый разряд. Кольцевая модуляция осталась, но ушла вниз — раньше она разводила боковые
    тона под семь килогерц, и пушка звенела поверх всего боя. Теперь несущая на 110 Гц, модулятор
    медленный, а верх срезан: слышен гудящий удар тока, а не звон.
    """
    n = S.n_of(0.28)
    x = S.osc(110.0, n, "saw") * S.osc(23.0, n)
    x += S.osc(55.0, n) * 0.5
    crackle = S.band(S.noise(n, rng), 300, 1500, order=4) * (rng.random(n) > 0.8) * 0.5
    x = S.mix(x, crackle) * S.env(n, 0.005, 0.27, power=1.9)
    x = S.sweep(x, S.ramp(1900, 600, n), q=1.3)
    return S.biquad(S.drive(x, 3.0), 2600, "lp")


def _puffs(rng, count, gap, length, low, high, n):
    out = np.zeros(n)
    for i in range(count):
        m = S.n_of(length)
        puff = S.band(S.noise(m, rng), low, high) * S.env(m, 0.0008, length, power=3.0)
        S.place(out, puff, S.n_of(gap * i), 1.0 - 0.12 * i)
    return out


def shot_flak(rng):
    """Зенитка: короткая очередь пуфов, а не один выстрел — её слышно как скорострельную."""
    n = S.n_of(0.2)
    return _puffs(rng, 3, 0.042, 0.03, 1500, 5000, n)


def guard(rng):
    """Противоракетный комплекс: та же очередь, но ярче и суше — ухо отличает её от зенитки."""
    n = S.n_of(0.18)
    return _puffs(rng, 4, 0.03, 0.022, 2600, 7000, n)


def launch_missile(rng):
    """Пуск ракеты: раскрывающийся шум и низкий толчок стартового заряда."""
    n = S.n_of(0.5)
    body = S.sweep(S.noise(n, rng), S.ramp(300, 4200, n), q=0.9) * S.env(n, 0.02, 0.36, hold=0.1)
    kick = S.osc(S.ramp(70, 48, n), n) * S.env(n, 0.003, 0.12, power=3.0) * 0.8
    return S.mix(body, kick)


def launch_torpedo(rng):
    """Торпеда: тот же пуск, но тяжелее и дольше."""
    n = S.n_of(0.75)
    body = S.sweep(S.noise(n, rng), S.ramp(220, 2100, n), q=0.9) * S.env(n, 0.03, 0.55, hold=0.16)
    kick = S.osc(S.ramp(56, 38, n), n) * S.env(n, 0.004, 0.2, power=3.0)
    return S.mix(body, kick)


# --- попадания ---------------------------------------------------------------------------------------

def hit_shield(rng):
    """Щит принял удар: восходящий тон с негармоничным обертоном — «стеклянный динь»."""
    n = S.n_of(0.2)
    base = S.osc(S.ramp(900, 1500, S.n_of(0.08)), n)
    over = S.osc(S.ramp(2430, 4050, S.n_of(0.08)), n) * 0.32
    x = (base + over) * S.env(n, 0.002, 0.19, power=2.2)
    return S.band(x, 500, 6000)


def hit_hull(rng):
    """Попадание в корпус: глухой удар, низкий толчок и дребезг обшивки."""
    n = S.n_of(0.19)
    thud = S.biquad(S.noise(n, rng), 900, "lp") * S.env(n, 0.001, 0.13, power=3.0)
    low = S.osc(S.ramp(150, 68, n), n) * S.env(n, 0.001, 0.15, power=2.5)
    rattle = S.band(S.noise(S.n_of(0.06), rng), 2000, 3200, order=4) * S.env(S.n_of(0.06), 0.001, 0.058) * 0.45
    return S.mix(thud, low * 0.9, rattle)


def block(rng):
    """Защита отбила: как щит, но глуше и короче — броня, а не поле."""
    n = S.n_of(0.14)
    x = S.osc(S.ramp(520, 700, S.n_of(0.05)), n) * S.env(n, 0.002, 0.13, power=3.0)
    grain = S.band(S.noise(n, rng), 300, 1400) * S.env(n, 0.001, 0.09, power=3.0) * 0.5
    return S.mix(x, grain)


def whizz(rng):
    """Промах мимо меня: короткий свист. Играется только по своему кораблю — иначе в бою сплошное шипение."""
    n = S.n_of(0.13)
    x = S.band(S.noise(n, rng), 2200, 4200) * S.env(n, 0.03, 0.09, power=1.6)
    return x


# --- взрывы ------------------------------------------------------------------------------------------

def _boom(rng, seconds, sub_from, sub_to, debris, wet):
    n = S.n_of(seconds)
    sub = S.osc(S.ramp(sub_from, sub_to, n), n) * S.env(n, 0.003, seconds, power=2.0)
    body = S.sweep(S.noise(n, rng), S.ramp(3200, 180, n), q=0.7) * S.env(n, 0.004, seconds, power=1.7)
    out = S.mix(sub * 1.1, body)
    for _ in range(debris):
        m = S.n_of(rng.uniform(0.02, 0.06))
        low = rng.uniform(700, 2600)
        piece = S.band(S.noise(m, rng), low, low * 2.2) * S.env(m, 0.001, 0.05, power=3.0)
        S.place(out, piece, S.n_of(rng.uniform(0.03, seconds * 0.45)), rng.uniform(0.15, 0.4))
    out = S.drive(out, 1.5)
    return S.reverb(out, S.reverb_ir(0.6, rng, 1800), wet)


def explode(rng):
    """Корабль разорвало: суб-удар, раскрывающийся шум и разлетающиеся обломки."""
    return _boom(rng, 0.95, 120, 34, 6, 0.22)


def death(rng):
    """Свой корабль: тот же взрыв, но дольше и с большим залом — это событие, а не фон."""
    return _boom(rng, 1.5, 150, 28, 9, 0.38)


def splash(rng):
    """Осколки взрыва (псевдо-пушка splash): короткий сухой хлопок."""
    return _boom(rng, 0.42, 190, 60, 3, 0.12)


def ram(rng):
    """Таран метеорита: гравийный скрежет и низкий удар."""
    n = S.n_of(0.38)
    gravel = S.band(S.noise(n, rng), 380, 1300) * S.env(n, 0.002, 0.36, power=1.8)
    gravel *= 0.6 + 0.4 * S.osc(38.0, n)
    low = S.osc(S.ramp(92, 38, n), n) * S.env(n, 0.002, 0.3, power=2.4)
    return S.drive(S.mix(gravel, low), 1.8)


# --- мир и интерфейс ---------------------------------------------------------------------------------

def loot(rng):
    """Предмет в трюме: три ноты вверх и лёгкое всасывание тракторного луча."""
    n = S.n_of(0.42)
    out = np.zeros(n)
    for i, f in enumerate((660, 880, 1320)):
        m = S.n_of(0.14)
        note = S.osc(float(f), m) * S.env(m, 0.004, 0.13, power=2.0)
        note += S.osc(float(f) * 2, m) * S.env(m, 0.004, 0.09, power=3.0) * 0.25
        S.place(out, note, S.n_of(0.075 * i), 0.9 - 0.12 * i)
    pull = S.sweep(S.noise(n, rng), S.ramp(400, 2600, n), q=1.2) * S.env(n, 0.1, 0.3) * 0.12
    return S.mix(out, pull)


def dock(rng):
    """Стыковка: захваты щёлкнули, сервопривод дотянул."""
    n = S.n_of(0.5)
    out = np.zeros(n)
    for i, at in enumerate((0.0, 0.11)):
        m = S.n_of(0.03)
        clack = S.band(S.noise(m, rng), 900, 2600) * S.env(m, 0.001, 0.028, power=3.0)
        S.place(out, clack, S.n_of(at), 1.0 - 0.2 * i)
    servo = S.osc(S.ramp(74, 58, n), n, "saw") * S.env(n, 0.05, 0.3, hold=0.1) * 0.35
    return S.mix(out, S.biquad(servo, 700, "lp"))


def undock(rng):
    """Вылет: сервопривод отпустил, захваты разошлись."""
    n = S.n_of(0.5)
    servo = S.osc(S.ramp(58, 82, n), n, "saw") * S.env(n, 0.04, 0.34, hold=0.06) * 0.35
    out = S.biquad(servo, 800, "lp")
    m = S.n_of(0.035)
    clack = S.band(S.noise(m, rng), 800, 2400) * S.env(m, 0.001, 0.033, power=3.0)
    S.place(out, clack, S.n_of(0.24), 0.9)
    return out


def jump_charge(rng):
    """
    Накопление прыжка: нарастающие расстроенные пилы. Файл рассчитан на три секунды, клиент растягивает
    его скоростью воспроизведения под реальное время прыжка системы.
    """
    n = S.n_of(3.0)
    rise = S.ramp(0.12, 1.0, n, "lin") ** 1.7
    a = S.osc(S.ramp(60, 190, n), n, "saw")
    b = S.osc(S.ramp(60.7, 191.5, n), n, "saw")
    c = S.osc(S.ramp(90, 285, n), n, "saw") * 0.4
    x = (a + b + c) * rise
    x = S.sweep(x, S.ramp(220, 4200, n), q=1.6)
    shimmer = S.noise(n, rng) * rise * 0.08
    return S.mix(x * 0.5, S.band(shimmer, 2000, 7000))


def jump(rng):
    """Прыжок: провал вниз, эхо и резкая тишина."""
    n = S.n_of(0.9)
    whoosh = S.sweep(S.noise(n, rng), S.ramp(5200, 160, n), q=1.1) * S.env(n, 0.005, 0.5, power=1.5)
    tone = S.osc(S.ramp(420, 40, n), n, "saw") * S.env(n, 0.004, 0.4, power=2.0) * 0.4
    return S.delay(S.mix(whoosh, tone), 250, feedback=0.3, wet=0.35)


def alarm(rng):
    """Ракета идёт в меня: две ноты, которые повторяются, пока она летит."""
    n = S.n_of(0.42)
    out = np.zeros(n)
    for i, f in enumerate((1200, 900)):
        m = S.n_of(0.15)
        note = S.osc(float(f), m, "square") * S.env(m, 0.004, 0.14, power=1.5)
        S.place(out, S.biquad(note, 4000, "lp"), S.n_of(0.19 * i), 0.8)
    return out


def squelch_open(rng):
    """Щелчок рации перед репликой: без него голос начинается «из ниоткуда»."""
    n = S.n_of(0.09)
    click = S.band(S.noise(S.n_of(0.012), rng), 1400, 3400) * S.env(S.n_of(0.012), 0.0005, 0.011, power=3.0)
    hiss = S.band(S.noise(n, rng), 400, 3000) * S.env(n, 0.004, 0.08, power=2.0) * 0.14
    return S.mix(click, hiss)


def squelch_close(rng):
    """Отбой связи: щелчок короче и суше."""
    n = S.n_of(0.06)
    click = S.band(S.noise(S.n_of(0.009), rng), 1200, 3000) * S.env(S.n_of(0.009), 0.0005, 0.008, power=3.0)
    hiss = S.band(S.noise(n, rng), 500, 2600) * S.env(n, 0.002, 0.05, power=2.5) * 0.1
    return S.mix(click, hiss)


def fire_on(rng):
    """Автоогонь включён: механический клац спускового механизма."""
    n = S.n_of(0.08)
    clack = S.band(S.noise(n, rng), 1100, 2800) * S.env(n, 0.0008, 0.05, power=3.5)
    thunk = S.osc(S.ramp(180, 120, n), n) * S.env(n, 0.001, 0.05, power=3.0) * 0.4
    return S.mix(clack, thunk)


def fire_off(rng):
    """Огонь снят: тот же клац, но мягче и ниже."""
    n = S.n_of(0.07)
    clack = S.band(S.noise(n, rng), 700, 1900) * S.env(n, 0.001, 0.045, power=3.5) * 0.7
    return clack


def lock(rng):
    """Цель захвачена: два коротких писка. Автозахват в бою звучит так же — он и есть захват."""
    n = S.n_of(0.16)
    out = np.zeros(n)
    for i in range(2):
        m = S.n_of(0.04)
        beep = S.osc(1500.0 + 180 * i, m) * S.env(m, 0.002, 0.037, power=2.0)
        S.place(out, beep, S.n_of(0.06 * i), 0.8)
    return out


def respawn(rng):
    """Корабль снова в строю: восходящий аккорд запуска систем."""
    n = S.n_of(0.8)
    out = np.zeros(n)
    for i, f in enumerate((220, 330, 440, 660)):
        m = S.n_of(0.55)
        note = S.osc(float(f), m, "tri") * S.env(m, 0.02, 0.5, power=1.8)
        S.place(out, note, S.n_of(0.09 * i), 0.5 - 0.06 * i)
    return S.biquad(out, 5200, "lp")


# --- каталог -----------------------------------------------------------------------------------------

# (имя, рецепт, число вариантов, относительная громкость в игре).
# Громкости подобраны так: взрыв — самое громкое событие боя, выстрелы заметно тише (их много),
# служебные щелчки едва слышны. Правится здесь, без перерисовки звука.
CUES = [
    ("shot-bolt", shot_bolt, 3, 0.45),
    ("shot-beam", shot_beam, 3, 0.30),
    ("shot-orb", shot_orb, 3, 0.50),
    ("shot-rail", shot_rail, 3, 0.60),
    ("shot-ion", shot_ion, 3, 0.45),
    ("shot-flak", shot_flak, 3, 0.30),
    ("guard", guard, 2, 0.28),
    ("launch-missile", launch_missile, 2, 0.55),
    ("launch-torpedo", launch_torpedo, 2, 0.60),
    ("hit-shield", hit_shield, 3, 0.42),
    ("hit-hull", hit_hull, 3, 0.50),
    ("block", block, 2, 0.38),
    ("whizz", whizz, 2, 0.25),
    ("explode", explode, 3, 1.00),
    ("death", death, 1, 1.00),
    ("splash", splash, 2, 0.55),
    ("ram", ram, 2, 0.80),
    ("loot", loot, 2, 0.40),
    ("dock", dock, 1, 0.50),
    ("undock", undock, 1, 0.50),
    ("jump-charge", jump_charge, 1, 0.40),
    ("jump", jump, 1, 0.70),
    ("alarm", alarm, 1, 0.55),
    ("squelch-open", squelch_open, 2, 0.30),
    ("squelch-close", squelch_close, 2, 0.26),
    ("fire-on", fire_on, 1, 0.25),
    ("fire-off", fire_off, 1, 0.22),
    ("lock", lock, 1, 0.30),
    ("respawn", respawn, 1, 0.55),
]


def build(only=None):
    meta = {}
    total = 0
    for name, recipe, variants, gain in CUES:
        if only and name not in only:
            if META.exists():
                meta[name] = json.loads(META.read_text(encoding="utf-8"))["cues"].get(name)
            continue
        longest = 0
        for i in range(variants):
            rng = np.random.default_rng(abs(hash(name)) % (2**31) + i * 7919)
            x = recipe(rng)
            x = S.dc_block(x)
            x = S.fade_edges(S.norm(x, PEAK), 4.0)
            size = S.write_mp3(OUT / f"{name}-{i + 1}.mp3", x, QUALITY)
            total += size
            longest = max(longest, len(x))
        meta[name] = {"n": variants, "ms": int(round(longest / S.SR * 1000)), "gain": gain}
        print(f"{name:<15} x{variants}  {meta[name]['ms']:>5} мс")

    META.parent.mkdir(parents=True, exist_ok=True)
    META.write_text(json.dumps({"cues": meta}, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    files = sum(c["n"] for c in meta.values())
    print(f"\n{files} файлов, {total / 1024:.0f} КБ, манифест: {META.relative_to(ROOT)}")


if __name__ == "__main__":
    build(set(sys.argv[1:]) or None)
