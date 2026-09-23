"""Музыка игры: client/public/music/*.mp3 и client/src/audio/musicMeta.json.

Запуск из корня репозитория: python tools/music.py  (нужны numpy, scipy, soundfile)
Перезапускать только после правки партий — результат лежит в git.

Саундтрек не один файл, а слои (стемы), которые клиент смешивает на лету: в покое звучат дрон, пэд и
арпеджио, в бою к ним добавляются бас, ударные, лид и медь. Поэтому «бой начался» — это не смена трека
с паузой и не склейка, а изменение громкостей за полсекунды.

Чтобы слои всегда сходились, у всех одна длина петли (LOOP_SECONDS) и общая тоника: ля-эолийская.
Темп боя ровно вдвое быстрее покоя, так что доли совпадают, и переход не сбивает пульс.

Хвосты реверберации заворачиваются в начало петли — иначе на стыке слышен обрыв.
"""

from __future__ import annotations

import json
import sys
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))

import synth as S

ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / "client" / "public" / "music"
META = ROOT / "client" / "src" / "audio" / "musicMeta.json"

LOOP_SECONDS = 30.0
CALM_BPM = 64
COMBAT_BPM = 128
PEAK = 0.85
QUALITY = 0.22

# Полутона от ля. Аккорды записаны голосами так, как их берут, а не по учебнику: верхний голос
# почти не двигается, и смена аккорда слышится как смена света, а не как прыжок.
A_MINOR = [0, 3, 7]
F_MAJOR = [-4, 0, 3]
C_MAJOR = [-9, -5, 3]
G_MAJOR = [-2, 2, 5]
E_MAJOR = [-5, -1, 2]
D_MINOR = [-7, -4, 0]

CALM_CHORDS = [A_MINOR, F_MAJOR, C_MAJOR, G_MAJOR]  # по два такта
COMBAT_CHORDS = [A_MINOR, G_MAJOR, F_MAJOR, E_MAJOR]  # по такту
DOCK_CHORDS = [A_MINOR, D_MINOR]  # по четыре такта

# Ля первой октавы: от неё считаются все ноты.
A3 = 220.0


def hz(semitones: float, octave: int = 0) -> float:
    return A3 * (2.0 ** (semitones / 12.0 + octave))


def bar_seconds(bpm: int) -> float:
    return 4.0 * 60.0 / bpm


def loop_hz(freq: float) -> float:
    """
    Ближайшая частота, укладывающаяся в петлю целым числом периодов.

    Непрерывный тон (дрон) иначе приходит к концу петли в середине волны, и на стыке слышен щелчок.
    Сдвиг выходит меньше цента — на слух это та же нота.
    """
    cycles = max(1, round(freq * LOOP_SECONDS))
    return cycles / LOOP_SECONDS


def canvas(tail: float = 4.0) -> tuple[np.ndarray, int]:
    """Полотно петли плюс хвост: хвост потом заворачивается в начало."""
    n = S.n_of(LOOP_SECONDS)
    return np.zeros(n + S.n_of(tail)), n


def wrap(buf: np.ndarray, n: int) -> np.ndarray:
    """Завернуть хвост в начало — петля сходится без щелчка и без обрыва реверберации."""
    tail = buf[n:]
    out = buf[:n].copy()
    out[: len(tail)] += tail
    return out


# --- партии ------------------------------------------------------------------------------------------

def calm_drone(rng):
    """
    Дрон: три расстроенные пилы и суб. Он не смолкает никогда — у музыки всегда есть пол под ногами.

    Считается сразу на две петли, а отдаётся вторая: фильтр стартует с нулевого состояния, и первая
    секунда первой петли звучит иначе, чем то же место при повторе, — на стыке это слышно как толчок.
    Частоты вдобавок подогнаны под сетку петли (loop_hz), иначе тон приходит к стыку в середине волны.
    """
    n = S.n_of(LOOP_SECONDS)
    total = n * 2
    buf = np.zeros(total)
    breathe = 1.0 + 0.35 * S.osc(loop_hz(0.05), total)  # очень медленное дыхание фильтра
    voices = [(hz(0, -2), 1.0), (hz(0.08, -2), 0.9), (hz(7, -1), 0.55), (hz(-0.1, -1), 0.5)]
    for freq, gain in voices:
        buf += S.osc(loop_hz(freq), total, "saw") * gain
    buf = S.sweep(buf, 260 * breathe, q=1.1) * 0.25
    sub = S.osc(loop_hz(hz(0, -3)), total) * 0.35
    out = S.mix(buf, sub)
    # Тихое дыхание громкости: ровный дрон через минуту начинает давить.
    out *= 0.85 + 0.15 * S.osc(1.0 / LOOP_SECONDS, total)
    return out[n:]


def _pad_note(freq, seconds, rng, bright=1400.0):
    n = S.n_of(seconds)
    x = S.osc(freq, n, "tri") + 0.6 * S.osc(freq * 1.003, n, "saw") + 0.4 * S.osc(freq * 2, n)
    x *= S.env(n, 1.5, seconds - 1.5, power=1.4)
    return S.biquad(x, bright, "lp")


def calm_pad(rng):
    """Пэд: аккорд, который входит полторы секунды. Он и делает «космос»."""
    buf, n = canvas()
    bar = bar_seconds(CALM_BPM)
    ir = S.reverb_ir(2.6, rng, 2200)
    for i, chord in enumerate(CALM_CHORDS):
        at = S.n_of(i * 2 * bar)
        for j, semi in enumerate(chord):
            note = _pad_note(hz(semi, 0 if j else -1), 2 * bar + 1.5, rng)
            S.place(buf, note, at, 0.5 - 0.08 * j)
    return wrap(S.reverb(buf * 0.5, ir, 0.42), n)


def calm_arp(rng):
    """Арпеджио: редкий щипок по тонам аккорда с эхом. Через ноту — чтобы не суетилось."""
    buf, n = canvas()
    bar = bar_seconds(CALM_BPM)
    step = bar / 4  # четверти
    for i, chord in enumerate(CALM_CHORDS):
        for s in range(8):
            if s % 2 == 1:
                continue
            semi = chord[(s // 2) % len(chord)]
            octave = 1 if (s // 2) % 2 else 0
            m = S.n_of(0.5)
            note = S.osc(hz(semi, octave), m, "tri") * S.env(m, 0.005, 0.45, power=2.5)
            note += S.osc(hz(semi, octave + 1), m) * S.env(m, 0.004, 0.2, power=3.0) * 0.3
            S.place(buf, S.biquad(note, 4000, "lp"), S.n_of(i * 2 * bar + s * step), 0.32)
    return wrap(S.delay(buf, step * 1000 / 2, feedback=0.36, wet=0.4), n)


def combat_bass(rng):
    """Бас: основа боя. Восьмыми, с подтяжкой высоты в атаке — так он «толкает»."""
    buf, n = canvas(1.0)
    bar = bar_seconds(COMBAT_BPM)
    eighth = bar / 8
    pattern = [1, 0, 1, 1, 0, 1, 0, 1]
    for b in range(int(round(LOOP_SECONDS / bar))):
        chord = COMBAT_CHORDS[b % len(COMBAT_CHORDS)]
        root = hz(chord[0], -2)
        for s, on in enumerate(pattern):
            if not on:
                continue
            m = S.n_of(eighth * 0.9)
            freq = S.ramp(root * 1.6, root, S.n_of(0.02))
            note = S.osc(np.concatenate([freq, np.full(m - len(freq), root)]), m)
            # Перегруженная гармоника сверху: чистый синус на 55 Гц в телефоне просто не слышно.
            note += S.drive(S.osc(root * 2, m, "saw"), 2.5) * 0.3
            note *= S.env(m, 0.004, eighth * 0.8, power=2.2)
            S.place(buf, note, S.n_of(b * bar + s * eighth), 0.75)
    return wrap(S.biquad(S.drive(buf, 1.8), 2600, "lp"), n)


def _kick(rng):
    """Бочка: длиннее и ниже обычной, с перегрузом — в бою она должна бить в грудь, а не щёлкать."""
    m = S.n_of(0.24)
    x = S.osc(S.ramp(150, 38, m), m) * S.env(m, 0.001, 0.23, power=2.2)
    click = S.biquad(S.noise(S.n_of(0.005), rng), 1200, "hp") * 0.18
    return S.drive(S.mix(x, click), 2.0)


def _snare(rng):
    """Малый: корпуса больше, звона меньше — верхняя полоса срезана до 3 кГц."""
    m = S.n_of(0.22)
    body = S.band(S.noise(m, rng), 250, 3000) * S.env(m, 0.001, 0.2, power=2.2)
    tone = S.osc(S.ramp(200, 140, m), m) * S.env(m, 0.001, 0.12, power=2.6) * 0.6
    return S.drive(S.mix(body, tone), 1.6)


def _hat(rng, length=0.04):
    """Хэт держит темп, но не сверкает: полоса ниже и уже, иначе весь верх занимает он."""
    m = S.n_of(length)
    return S.band(S.noise(m, rng), 2600, 6500, order=4) * S.env(m, 0.0008, length, power=4.0)


def combat_drums(rng):
    """Ударные: бочка держит шаг, хэт гонит, малый отмечает вторую и четвёртую."""
    buf, n = canvas(0.6)
    bar = bar_seconds(COMBAT_BPM)
    sixteenth = bar / 16
    kick_on = {0, 6, 8, 14}
    snare_on = {4, 12}
    for b in range(int(round(LOOP_SECONDS / bar))):
        for s in range(16):
            at = S.n_of(b * bar + s * sixteenth)
            if s in kick_on:
                S.place(buf, _kick(rng), at, 0.9)
            if s in snare_on:
                S.place(buf, _snare(rng), at, 0.5)
            if s % 2 == 0:
                S.place(buf, _hat(rng), at, 0.07 if s % 4 else 0.12)
    return wrap(buf, n)


def combat_lead(rng):
    """
    Риф: рубленые восьмые внизу с перегрузом.

    Сначала здесь бежали шестнадцатые высоко наверху — получался игровой автомат, а не бой.
    Теперь партия лежит в басовом регистре, ноты короткие и заглушённые, две расстроенные пилы идут
    через мягкое искажение: слышно не мелодию, а работу. Эхо убрано — оно и размазывало ритм.
    """
    buf, n = canvas(1.0)
    bar = bar_seconds(COMBAT_BPM)
    eighth = bar / 8
    # (доля такта, полутон от основания аккорда). Рисунок не меняется от такта к такту — он опора.
    riff = [(0, 0), (2, 0), (3, 7), (4, 0), (6, 3), (7, 0)]
    for b in range(int(round(LOOP_SECONDS / bar))):
        chord = COMBAT_CHORDS[b % len(COMBAT_CHORDS)]
        root = chord[0]
        for step, interval in riff:
            m = S.n_of(eighth * 0.9)
            freq = hz(root + interval, -1)
            note = S.osc(freq, m, "saw") + 0.8 * S.osc(freq * 1.007, m, "saw") + 0.5 * S.osc(freq / 2, m)
            note *= S.env(m, 0.004, eighth * 0.55, power=2.2)
            note = S.sweep(note, S.ramp(3200, 1300, m), q=1.4)
            note = S.drive(note * 0.5, 3.2)
            # Серединный гриль: та же нота, перегруженная сильнее и оставленная в полосе 500-3000 Гц.
            # Без него риф уходит целиком под 200 Гц, и в динамике телефона от боя остаётся тишина —
            # так же, как у настоящей перегруженной гитары: основа внизу, узнаётся она по серединам.
            grit = S.band(S.drive(note, 5.0), 500, 3000) * 0.9
            S.place(buf, S.mix(note, grit), S.n_of(b * bar + step * eighth), 0.5)
    return wrap(S.biquad(buf, 5200, "lp"), n)


def combat_braam(rng):
    """Медь: расстроенный аккорд в начале каждой четырёхтактовой фразы. Именно он говорит «началось»."""
    buf, n = canvas(2.5)
    bar = bar_seconds(COMBAT_BPM)
    ir = S.reverb_ir(1.8, rng, 1600)
    for phrase in range(int(round(LOOP_SECONDS / (bar * 4)))):
        chord = COMBAT_CHORDS[(phrase * 4) % len(COMBAT_CHORDS)]
        m = S.n_of(bar * 2.0)
        stack = np.zeros(m)
        # Октавой ниже прежнего и с добавленным субом: медь должна давить, а не трубить.
        for j, semi in enumerate(chord):
            for detune in (-0.14, 0.0, 0.15):
                stack += S.osc(hz(semi + detune, -2 if j == 0 else -1), m, "saw") * (0.5 - 0.08 * j)
        stack += S.osc(hz(chord[0], -3), m) * 0.9
        stack *= S.env(m, 0.05, bar * 1.8, power=1.6)
        stack = S.sweep(stack, S.ramp(400, 1600, S.n_of(0.5)), q=1.2)
        S.place(buf, S.drive(stack * 0.14, 2.2), S.n_of(phrase * bar * 4), 1.0)
    return wrap(S.reverb(buf, ir, 0.3), n)


def dock_pad(rng):
    """Док: тот же пэд, но глуше и медленнее — под крышей станции музыка приглушена."""
    buf, n = canvas()
    bar = bar_seconds(CALM_BPM)
    ir = S.reverb_ir(2.2, rng, 1600)
    for i, chord in enumerate(DOCK_CHORDS):
        at = S.n_of(i * 4 * bar)
        for j, semi in enumerate(chord):
            note = _pad_note(hz(semi, -1 if j == 0 else 0), 4 * bar + 1.5, rng, bright=900)
            S.place(buf, note, at, 0.45 - 0.07 * j)
    return wrap(S.reverb(buf * 0.45, ir, 0.35), n)


# --- сборка ------------------------------------------------------------------------------------------

# (имя файла, партия, настроение-хозяин). Громкости слоёв по настроениям живут в client/src/audio/music.ts:
# это решение микшера, а не запись, и правится без перерисовки звука.
STEMS = [
    ("calm-drone", calm_drone, "calm"),
    ("calm-pad", calm_pad, "calm"),
    ("calm-arp", calm_arp, "calm"),
    ("combat-bass", combat_bass, "combat"),
    ("combat-drums", combat_drums, "combat"),
    ("combat-lead", combat_lead, "combat"),
    ("combat-braam", combat_braam, "combat"),
    ("dock-pad", dock_pad, "dock"),
]


def build(only=None):
    stems = {}
    total = 0
    for name, part, mood in STEMS:
        if only and name not in only:
            continue
        rng = np.random.default_rng(abs(hash(name)) % (2**31))
        x = S.norm(S.dc_block(part(rng)), PEAK)
        size = S.write_mp3(OUT / f"{name}.mp3", x, QUALITY)
        total += size
        stems[name] = mood
        print(f"{name:<14} {mood:<7} {size / 1024:>6.0f} КБ")

    if only and META.exists():
        stems = {**json.loads(META.read_text(encoding="utf-8"))["stems"], **stems}
    META.parent.mkdir(parents=True, exist_ok=True)
    META.write_text(
        json.dumps({"loopSeconds": LOOP_SECONDS, "stems": stems}, ensure_ascii=False, indent=2) + "\n",
        encoding="utf-8",
    )
    print(f"\n{len(stems)} слоёв, {total / 1024:.0f} КБ, манифест: {META.relative_to(ROOT)}")


if __name__ == "__main__":
    build(set(sys.argv[1:]) or None)
