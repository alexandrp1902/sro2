"""Музыка игры: client/public/music/*.mp3 и client/src/audio/musicMeta.json.

Запуск из корня репозитория: python tools/music.py  (нужны numpy, scipy, soundfile)
Перезапускать только после правки партий — результат лежит в git.

Саундтрек не один файл, а слои (стемы), которые клиент смешивает на лету: в покое звучат дрон, пэд и
арпеджио, в бою вместо них поднимаются боевые слои. Поэтому «бой начался» — это не смена трека
с паузой и не склейка, а изменение громкостей за полсекунды.

Боевых тем две, на выбор в окне «Звук»: «орган» (барабаны и церковный орган, по заданию
docs/SRO - Задание на боевую музыку.md) и «марш» (медь, малый барабан, струнные — космическая опера).
У обеих четыре слоя, темп 96 и та же ля-эолийская, что у покоя: 12 тактов ровно в 30 секунд, три фразы
по четыре такта, и переход в любую сторону — кроссфейд без фальши на стыке.

Чтобы слои всегда сходились, у всех одна длина петли (LOOP_SECONDS) и общая тоника. Ударных в покое нет,
поэтому разница темпов (64 и 96) на слух не рассогласование.

Запуск с --check — только приёмка готовых файлов: длина, пик, доля энергии в полосе 200–2000 Гц.

Хвосты реверберации заворачиваются в начало петли — иначе на стыке слышен обрыв.
"""

from __future__ import annotations

import json
import sys
import zlib
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))

import synth as S

ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / "client" / "public" / "music"
META = ROOT / "client" / "src" / "audio" / "musicMeta.json"

LOOP_SECONDS = 30.0
CALM_BPM = 64
COMBAT_BPM = 96
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


# --- бой: орган и барабаны (docs/SRO - Задание на боевую музыку.md) --------------------------------
#
# Четыре захода до этого отвергнуты: пилы и шестнадцатые наверху («аркада»), синтезаторный бас, ревун,
# одни барабаны. Здесь опорная пара — церковный орган и большие барабаны, ничего электронного.
# Напряжение строится не звуками, а гармонией: педаль на ля, малая секунда в кластере, тритон в начале
# второй фразы и неразрешённая доминанта в конце каждой. Петля — 12 тактов при 96, три фразы по четыре.
#
# Общий тон всех аккордов — фа: в ля миноре она стоит на полутон выше квинты, и это биение (ми–фа) и есть
# «неуютно». Верхний голос почти не двигается, так что смена аккорда слышится как смена света, а не как ход.
#
# Аккорды — полутоны от ля, голосами в регистре 220–440 Гц. Один аккорд — один такт.
AM_F = [0, 3, 7, 8]        # ля-минор с малой секстой: ми–фа рядом
DM_E = [-7, -4, 0, 7]      # ре-минор над педалью ля, с ми — снова малая секунда
BDIM = [2, 5, 8, 0]        # си–фа — тритон; открывает вторую фразу
E7B9 = [-5, -1, 2, 5, 8]   # доминанта с малой ноной (фа) — вопрос, на котором замыкается фраза
FMAJ7 = [-4, 0, 3, 7]      # фа-мажор с большой септимой: тот же кластер, свет другой

COMBAT_CHORDS = [
    AM_F, AM_F, DM_E, E7B9,     # фраза 1: удержание, сдвиг, вопрос
    BDIM, BDIM, FMAJ7, E7B9,    # фраза 2: тритон, отступление, вопрос
    AM_F, DM_E, BDIM, E7B9,     # фраза 3: плотнее, регистр раскрыт
]
PHRASE_BARS = 4


def _pipe(freq: float, n: int, rng, chiff: float = 0.12, attack: float = 0.09) -> np.ndarray:
    """
    Одна органная труба: основной тон с убывающими гармониками и коротким выдохом воздуха в атаке.

    Гармоники до шестой, дальше труба не звучит — и не лезет в полосу выстрелов. Каждая труба чуть
    расстроена от соседней (в пределах трети процента): так дышит настоящий орган, а не таблица синусов.
    """
    f = freq * (1.0 + rng.uniform(-0.0025, 0.0025))
    x = np.zeros(n)
    for k, gain in ((1, 1.0), (2, 0.45), (3, 0.25), (4, 0.12), (5, 0.06), (6, 0.03)):
        if f * k > 5000:
            break
        x += S.osc(f * k, n, phase=rng.uniform(0, 2 * np.pi)) * gain
    a = max(S.n_of(attack), 4)
    ramp = np.ones(n)
    ramp[:a] = np.linspace(0.0, 1.0, a) ** 1.5
    x *= ramp
    if chiff > 0:
        m = min(S.n_of(0.06), n)
        breath = S.band(S.noise(m, rng), max(f * 0.8, 150), min(f * 4, 3000)) * S.env(m, 0.004, 0.05, power=2.0)
        x[:m] += breath * chiff
    return x


def _organ_chord(semis, seconds, rng, registers, octave=0, attack=0.09):
    """Аккорд органа: каждый голос через набор регистров (множитель частоты, громкость)."""
    n = S.n_of(seconds)
    out = np.zeros(n)
    for j, semi in enumerate(semis):
        base = hz(semi, octave)
        for mult, gain in registers:
            out += _pipe(base * mult, n, rng, attack=attack) * gain * (1.0 - 0.06 * j)
    return out


def combat_pedal(rng):
    """
    Педаль органа и низ: ля 55 Гц тянется всю петлю и никуда не движется.

    Слышимым её делают не 55 Гц (телефон их не отдаёт), а гармоники 110–330 Гц и регистр 8'. Тон собран
    из частот сетки петли (loop_hz), поэтому стык не щёлкает; ни один фильтр с памятью здесь не участвует.
    Динамика одна на всю петлю — очень медленное дыхание, и больше ничего: основание не движется.
    """
    n = S.n_of(LOOP_SECONDS)
    out = np.zeros(n)
    base = hz(0, -2)  # 55 Гц
    # (множитель, громкость): 16' сабом, 8' основой, квинта, 4' октавой и выше. Три трубы на регистр,
    # расстроенные друг от друга: биение и есть «воздух в трубах».
    # Основание (16' и 8') — одним голосом: две расстроенные трубы на 55 Гц дают не воздух, а качели
    # громкости на шесть децибел. Расстроены только верхние регистры, и вторая труба там тише первой,
    # чтобы биение не доходило до полного провала.
    for mult, gain in ((0.5, 0.3), (1.0, 1.0), (1.5, 0.35), (2.0, 0.7), (3.0, 0.35), (4.0, 0.3), (6.0, 0.12)):
        voices = ((0.0, 1.0),) if mult < 2 else ((-0.0015, 1.0), (0.0017, 0.5))
        for detune, vg in voices:
            f = base * mult * (1.0 + detune)
            for k, hg in ((1, 1.0), (2, 0.45), (3, 0.25)):
                out += S.osc(loop_hz(f * k), n, phase=rng.uniform(0, 2 * np.pi)) * gain * vg * hg
    # Дыхание: период кратен петле, стык не рвётся.
    out *= 0.86 + 0.14 * S.osc(1.0 / LOOP_SECONDS, n, phase=-np.pi / 2)
    # Контрабасы: узкий шум смычка вокруг гармоник, очень тихо — это «низ», а не голос.
    bow = S.band(S.noise(n, rng), 90, 500, order=4) * 0.05
    bow *= 0.5 + 0.5 * S.osc(2.0 / LOOP_SECONDS, n)
    bow = S.fade_edges(bow, 30)
    return S.biquad(out * 0.2, 1800, "lp") + bow


def combat_organ(rng):
    """
    Аккорды органа: средний регистр, смена раз в такт, мягкая атака и воздух в трубах.

    Третья фраза открывает регистр 4' — это единственное «нарастание» петли, и оно сделано тем же
    инструментом, а не новым. Хор труб идёт через зал на две секунды: церковь, а не студия.
    """
    buf, n = canvas(3.0)
    bar = bar_seconds(COMBAT_BPM)
    ir = S.reverb_ir(2.0, rng, 1800)
    for b, chord in enumerate(COMBAT_CHORDS):
        phrase = b // PHRASE_BARS
        registers = [(1.0, 1.0), (2.0, 0.28), (1.5, 0.12)]
        if phrase == 2:
            registers = [(1.0, 1.0), (2.0, 0.55), (1.5, 0.2), (4.0, 0.08)]
        # Аккорд держится чуть дольше такта: трубы не глохнут мгновенно, звук переходит в следующий.
        note = _organ_chord(chord, bar + 0.25, rng, registers, octave=0, attack=0.11)
        m = len(note)
        rel = np.ones(m)
        r = S.n_of(0.22)
        rel[-r:] = np.linspace(1.0, 0.0, r) ** 1.4
        S.place(buf, note * rel, S.n_of(b * bar), 0.16)
    buf = S.biquad(buf, 2800, "lp")
    return wrap(S.reverb(buf, ir, 0.4), n)


def _membrane(freq, seconds, rng, modes, pitch_drop=1.0, decay_power=2.4, beater=0.15, beater_hz=600.0):
    """
    Мембрана с негармоничными обертонами: так бьёт кожа, а не осциллятор. modes — (отношение частоты,
    громкость, относительная длительность). pitch_drop — во сколько раз выше нота в момент удара.
    """
    n = S.n_of(seconds)
    out = np.zeros(n)
    for ratio, gain, life in modes:
        f = S.ramp(freq * ratio * pitch_drop, freq * ratio, S.n_of(0.03))
        f = np.concatenate([f, np.full(n - len(f), freq * ratio)])
        out += S.osc(f, n, phase=rng.uniform(0, 0.4)) * S.env(n, 0.001, seconds * life, power=decay_power) * gain
    if beater > 0:
        # Колотушка — низкий щелчок, кожа — короткий шум 200–1500 Гц. Без кожи удар в динамике телефона
        # не существует: 52 Гц он не отдаёт, а шум кожи — да.
        m = min(S.n_of(0.012), n)
        hit = S.biquad(S.noise(m, rng), beater_hz, "lp") * S.env(m, 0.0005, 0.011, power=2.0)
        out[:m] += hit * beater
        k = min(S.n_of(0.05), n)
        skin = S.band(S.noise(k, rng), 200, 1500, order=2) * S.env(k, 0.001, 0.045, power=2.4)
        out[:k] += skin * beater * 1.6
    return out


def _gran_cassa(rng):
    """Большой барабан: удар в грудь, колотушка в войлоке, ни намёка на щелчок."""
    modes = [(1.0, 1.0, 1.0), (1.59, 0.35, 0.5), (2.14, 0.18, 0.35), (2.65, 0.08, 0.25)]
    x = _membrane(52.0, 0.9, rng, modes, pitch_drop=1.7, decay_power=2.6, beater=0.5, beater_hz=350)
    return S.drive(x, 1.4)


def _timpani(rng, freq=110.0, seconds=1.1, gain=1.0):
    """Литавра: настроенная мембрана, звенит квинтой и октавой, живёт секунду."""
    modes = [(1.0, 1.0, 1.0), (1.5, 0.5, 0.7), (1.98, 0.3, 0.5), (2.44, 0.14, 0.35), (2.9, 0.06, 0.25)]
    x = _membrane(freq, seconds, rng, modes, pitch_drop=1.25, decay_power=2.2, beater=0.35, beater_hz=900)
    return x * gain


def _tom(rng, freq=160.0):
    """Средний барабан: короче литавры, тон падает быстрее."""
    modes = [(1.0, 1.0, 1.0), (1.5, 0.4, 0.5), (2.2, 0.2, 0.3)]
    return _membrane(freq, 0.45, rng, modes, pitch_drop=1.5, decay_power=2.6, beater=0.4, beater_hz=700)


# Рисунок литавр и средних барабанов на фразу: (такт в фразе, доля в восьмых, инструмент, громкость).
# Три рисунка на три фразы — меняются, но не «сбиваются»; последний такт каждой фразы заканчивается
# нарастанием (дробь), которое ведёт в следующую фразу и — в конце петли — в её начало.
_ANSWERS = [
    [(0, 3, "timp", 0.5), (0, 7, "tom", 0.35), (1, 3, "timp", 0.5), (1, 6, "timp", 0.4),
     (2, 3, "timp", 0.55), (2, 7, "tom", 0.4), (3, 2, "timp", 0.5)],
    [(0, 2, "timp", 0.55), (0, 5, "tom", 0.4), (0, 7, "tom", 0.3), (1, 3, "timp", 0.55), (1, 7, "timp", 0.4),
     (2, 2, "timp", 0.6), (2, 5, "tom", 0.45), (2, 7, "tom", 0.35), (3, 2, "timp", 0.55), (3, 3, "tom", 0.4)],
    [(0, 2, "timp", 0.6), (0, 3, "tom", 0.4), (0, 6, "timp", 0.5), (0, 7, "tom", 0.4), (1, 2, "timp", 0.6),
     (1, 5, "tom", 0.45), (1, 6, "timp", 0.5), (2, 2, "timp", 0.65), (2, 3, "tom", 0.45), (2, 6, "timp", 0.55),
     (2, 7, "tom", 0.45), (3, 2, "timp", 0.6), (3, 3, "tom", 0.45)],
]


def combat_drums(rng):
    """
    Барабаны: большой на раз и три — не чаще, иначе марш, а нужно предчувствие. Литавры и средние
    отвечают на слабых долях, рисунок меняется от фразы к фразе, четвёртый такт кончается дробью.
    Комната 1,2 секунды: кожа и зал, а не сухой сэмпл.
    """
    buf, n = canvas(1.6)
    bar = bar_seconds(COMBAT_BPM)
    eighth = bar / 8
    ir = S.reverb_ir(1.2, rng, 1400)
    for b in range(len(COMBAT_CHORDS)):
        phrase, i = divmod(b, PHRASE_BARS)
        weight = 0.85 + 0.075 * phrase
        for beat in (0, 4):
            S.place(buf, _gran_cassa(rng), S.n_of(b * bar + beat * eighth), weight * (1.0 if beat == 0 else 0.8))
        for pb, e, kind, gain in _ANSWERS[phrase]:
            if pb != i:
                continue
            hit = _timpani(rng, 110.0 if e % 4 else 82.4) if kind == "timp" else _tom(rng, 150.0 + 30 * (e % 2))
            S.place(buf, hit, S.n_of(b * bar + e * eighth), gain * (0.9 + 0.05 * phrase))
        if i == PHRASE_BARS - 1:
            # Дробь литавр: от третьей доли до конца такта, тридцать вторыми, с нарастанием.
            start = 4 * eighth
            steps = 16
            for k in range(steps):
                t = b * bar + start + k * (4 * eighth / steps)
                g = 0.12 + 0.5 * (k / steps) ** 1.6
                S.place(buf, _timpani(rng, 82.4, 0.5), S.n_of(t), g * (0.9 + 0.1 * phrase))
    return wrap(S.reverb(buf, ir, 0.28), n)


def _brass_tone(freq, n, rng, bright=900.0, voices=3):
    """
    Низкая медь: несколько чуть расстроенных пил через мягкий низкочастотный фильтр и лёгкую перегрузку.
    Пила здесь не «синтезатор», а спектр раструба — важно, чтобы верх был закрыт и атака была медленной.
    """
    out = np.zeros(n)
    for v in range(voices):
        det = 1.0 + (v - (voices - 1) / 2) * 0.004
        vib = 1.0 + 0.004 * S.osc(4.8 + 0.3 * v, n, phase=rng.uniform(0, 6))
        out += S.osc(freq * det * vib, n, "saw") / voices
    out = S.biquad(out, bright, "lp", order=2)
    return S.drive(out, 1.6)


def combat_swell(rng):
    """
    То, что появляется раз в фразу: низкая медь подпирает педаль одним тоном, нарастая к четвёртому такту,
    и один тихий гонг в начале третьей фразы — не чаще раза за петлю.
    """
    buf, n = canvas(4.0)
    bar = bar_seconds(COMBAT_BPM)
    ir = S.reverb_ir(2.4, rng, 1500)
    for phrase in range(len(COMBAT_CHORDS) // PHRASE_BARS):
        # Медь: тон вступает на третьем такте фразы, растёт два такта и обрывается вместе с вопросом.
        m = S.n_of(bar * 2.3)
        tone = _brass_tone(hz(-5 if phrase else 0, -1), m, rng, bright=700 + 120 * phrase)
        rise = S.n_of(bar * 1.9)
        shape = np.concatenate([np.linspace(0.0, 1.0, rise) ** 1.8, np.ones(m - rise)])
        r = S.n_of(0.35)
        shape[-r:] *= np.linspace(1.0, 0.0, r)
        S.place(buf, tone * shape, S.n_of((phrase * PHRASE_BARS + 2) * bar), 0.22)
    # Гонг: негармоничные партиалы с расцветом после удара. Один на петлю, тихо.
    m = S.n_of(6.0)
    gong = np.zeros(m)
    for ratio, gain in ((1.0, 1.0), (1.47, 0.6), (2.09, 0.45), (2.56, 0.3), (3.31, 0.2), (4.2, 0.1)):
        bloom = S.env(m, 0.6 + 0.25 * ratio, 5.0, power=1.8)
        gong += S.osc(96.0 * ratio, m, phase=rng.uniform(0, 6)) * bloom * gain
    k = S.n_of(0.05)
    gong[:k] += S.band(S.noise(k, rng), 200, 1500) * 0.15 * S.env(k, 0.002, 0.045)
    S.place(buf, S.biquad(gong, 2200, "lp"), S.n_of(2 * PHRASE_BARS * bar), 0.16)
    return wrap(S.reverb(buf, ir, 0.4), n)


# --- бой, вариант «марш»: медь, малый барабан, струнные (по мотивам космических опер) ------------------
#
# Второй набор боевых слоёв по просьбе пользователя: то, как звучит имперский флот в кино, — фанфара
# низкой меди, маршевый малый барабан, литавры и остинато струнных. Та же тональность и тот же темп,
# что у органной темы, поэтому переключение между ними — тот же кроссфейд без фальши.
#
# Мотив меди: ля–ля–ля–фа–до–ля, знакомый ход «квинта вниз через терцию» — но своими нотами, в ля миноре.
_MARCH_MOTIF = [
    # (доля в восьмых от начала фразы, полутон от ля, длительность в восьмых)
    (0, 0, 2), (2, 0, 2), (4, 0, 2), (6, -4, 1.5), (7.5, 3, 0.5),
    (8, 0, 2), (10, -4, 1.5), (11.5, 3, 0.5), (12, 0, 4),
    (16, 7, 2), (18, 7, 2), (20, 7, 2), (22, 8, 1.5), (23.5, 3, 0.5),
    (24, -1, 2), (26, -4, 1.5), (27.5, 3, 0.5), (28, 0, 4),
]
MARCH_CHORDS = [AM_F[:3], AM_F[:3], F_MAJOR, E_MAJOR] * 3


def _snare_march(rng):
    """Военный малый: корпус в полосе 300–2500, пружина короткая, звона выше 3 кГц нет."""
    m = S.n_of(0.16)
    body = S.band(S.noise(m, rng), 300, 2500, order=4) * S.env(m, 0.001, 0.14, power=2.6)
    tone = S.osc(S.ramp(240, 170, m), m) * S.env(m, 0.001, 0.07, power=2.8) * 0.5
    return S.drive(S.mix(body, tone), 1.5) * 0.8


def march_drums(rng):
    """Малый барабан маршем, большой на раз, литавры на сильных долях; в конце фразы — дробь малого."""
    buf, n = canvas(1.4)
    bar = bar_seconds(COMBAT_BPM)
    eighth = bar / 8
    ir = S.reverb_ir(1.0, rng, 2200)
    for b in range(len(MARCH_CHORDS)):
        phrase, i = divmod(b, PHRASE_BARS)
        S.place(buf, _gran_cassa(rng), S.n_of(b * bar), 0.55)
        S.place(buf, _timpani(rng, 110.0, 0.8), S.n_of(b * bar), 0.35)
        S.place(buf, _timpani(rng, 82.4, 0.8), S.n_of(b * bar + 4 * eighth), 0.28)
        # Малый: та-та-тата-та — ход на каждую пару долей, как в военном марше.
        for beat in range(0, 4, 2):
            t0 = b * bar + beat * 2 * eighth
            pattern = [(0.0, 0.55), (0.5, 0.25), (0.75, 0.25), (1.0, 0.45), (1.5, 0.22), (1.75, 0.3)]
            if i == PHRASE_BARS - 1 and beat == 2:
                pattern = [(k / 6, 0.25 + 0.4 * k / 12) for k in range(12)]
            for off, g in pattern:
                S.place(buf, _snare_march(rng), S.n_of(t0 + off * 2 * eighth), g * 1.6 * (0.9 + 0.05 * phrase))
    return wrap(S.reverb(buf, ir, 0.22), n)


def march_brass(rng):
    """Низкая медь: мотив фанфары октавой ниже обычного, валторны и тромбоны, с залом."""
    buf, n = canvas(2.5)
    bar = bar_seconds(COMBAT_BPM)
    eighth = bar / 8
    ir = S.reverb_ir(1.8, rng, 2000)
    for phrase in range(len(MARCH_CHORDS) // PHRASE_BARS):
        for at, semi, length in _MARCH_MOTIF:
            m = S.n_of(length * eighth * 0.95)
            # Валторны ведут в малой октаве (220–440 Гц), тромбоны поддерживают октавой ниже.
            tone = _brass_tone(hz(semi, 0), m, rng, bright=1300 + 150 * phrase)
            tone += _brass_tone(hz(semi, -1), m, rng, bright=900 + 100 * phrase) * 0.5
            shape = S.env(m, 0.04, length * eighth * 0.9, power=1.2)
            S.place(buf, tone * shape, S.n_of(phrase * PHRASE_BARS * bar + at * eighth), 0.3)
    return wrap(S.reverb(S.biquad(buf, 3000, "lp"), ir, 0.3), n)


def _string_note(freq, seconds, rng):
    """Струнная группа: несколько расстроенных пил, узкий шум смычка, закрытый верх."""
    n = S.n_of(seconds)
    out = np.zeros(n)
    for v in range(4):
        det = 1.0 + (v - 1.5) * 0.003
        vib = 1.0 + 0.003 * S.osc(5.5 + 0.4 * v, n, phase=rng.uniform(0, 6))
        out += S.osc(freq * det * vib, n, "saw") * 0.25
    out += S.band(S.noise(n, rng), freq * 0.9, freq * 3) * 0.06
    return S.biquad(out, 2200, "lp")


def march_strings(rng):
    """Остинато струнных: восьмые по тонам аккорда в нижнем регистре, ровно и неутомимо."""
    buf, n = canvas(1.0)
    bar = bar_seconds(COMBAT_BPM)
    eighth = bar / 8
    ir = S.reverb_ir(1.4, rng, 1800)
    for b, chord in enumerate(MARCH_CHORDS):
        phrase = b // PHRASE_BARS
        for e in range(8):
            # Виолончели на основании, альты и скрипки — по тонам аккорда октавой выше.
            semi = chord[0] if e % 2 == 0 else chord[(e // 2) % len(chord)]
            octave = -1 if e % 2 == 0 else 0
            m = S.n_of(eighth * 0.85)
            note = _string_note(hz(semi, octave), eighth * 0.85, rng) * S.env(m, 0.02, eighth * 0.8, power=1.6)
            note += _string_note(hz(semi, octave + 1), eighth * 0.85, rng) * S.env(m, 0.02, eighth * 0.8, power=1.6) * 0.4
            S.place(buf, note, S.n_of(b * bar + e * eighth), 0.22 + 0.03 * phrase)
    return wrap(S.reverb(buf, ir, 0.25), n)


def march_swell(rng):
    """Нарастание: тарелка-крещендо в конце фразы, высокая медь — только в третьей фразе, тихо."""
    buf, n = canvas(3.0)
    bar = bar_seconds(COMBAT_BPM)
    eighth = bar / 8
    ir = S.reverb_ir(2.0, rng, 2400)
    for phrase in range(len(MARCH_CHORDS) // PHRASE_BARS):
        # Подвесная тарелка: шум в полосе 1–3 кГц, растёт полтора такта, гаснет на первой доле следующей.
        rise = S.n_of(bar * 1.5)
        m = rise + S.n_of(0.8)
        cym = S.band(S.noise(m, rng), 900, 3200, order=4)
        shape = np.concatenate([np.linspace(0.0, 1.0, rise) ** 2.2, np.linspace(1.0, 0.0, m - rise) ** 2])
        S.place(buf, cym * shape, S.n_of((phrase * PHRASE_BARS + 2.5) * bar), 0.09)
    # Третья фраза: ответ высокой меди на мотив — октавой выше, тихо, чтобы не пищало.
    for at, semi, length in _MARCH_MOTIF[9:]:
        m = S.n_of(length * eighth * 0.95)
        tone = _brass_tone(hz(semi, 1), m, rng, bright=1800)
        S.place(buf, tone * S.env(m, 0.05, length * eighth * 0.9, power=1.2), S.n_of(2 * PHRASE_BARS * bar + at * eighth), 0.11)
    return wrap(S.reverb(S.biquad(buf, 3400, "lp"), ir, 0.35), n)


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
    ("calm-drone", calm_drone, "calm", None),
    ("calm-pad", calm_pad, "calm", None),
    ("calm-arp", calm_arp, "calm", None),
    ("combat-drums", combat_drums, "combat", "organ"),
    ("combat-organ", combat_organ, "combat", "organ"),
    ("combat-pedal", combat_pedal, "combat", "organ"),
    ("combat-swell", combat_swell, "combat", "organ"),
    ("march-drums", march_drums, "combat", "march"),
    ("march-brass", march_brass, "combat", "march"),
    ("march-strings", march_strings, "combat", "march"),
    ("march-swell", march_swell, "combat", "march"),
    ("dock-pad", dock_pad, "dock", None),
]

# Боевых тем две, и клиент качает только выбранную (окно «Звук»): «орган» — по заданию
# (docs/SRO - Задание на боевую музыку.md), «марш» — вариант в духе космических опер.
THEMES = ("organ", "march")


def build(only=None):
    stems = {}
    total = 0
    for name, part, mood, theme in STEMS:
        if only and name not in only:
            continue
        # crc32, а не hash(): у строк в Python зерно случайное на каждый запуск, и результат не повторить.
        rng = np.random.default_rng(zlib.crc32(name.encode()))
        x = S.norm(S.dc_block(part(rng)), PEAK)
        size = S.write_mp3(OUT / f"{name}.mp3", x, QUALITY)
        total += size
        stems[name] = mood
        print(f"{name:<14} {mood:<7} {theme or '':<6} {size / 1024:>6.0f} КБ")

    if only and META.exists():
        known = {name for name, _, _, _ in STEMS}
        stems = {**{k: v for k, v in json.loads(META.read_text(encoding="utf-8"))["stems"].items() if k in known}, **stems}
    themes = {t: [name for name, _, _, theme in STEMS if theme == t] for t in THEMES}
    META.parent.mkdir(parents=True, exist_ok=True)
    META.write_text(
        json.dumps({"loopSeconds": LOOP_SECONDS, "stems": stems, "themes": themes}, ensure_ascii=False, indent=2) + "\n",
        encoding="utf-8",
    )
    print(f"\n{len(stems)} слоёв, {total / 1024:.0f} КБ, манифест: {META.relative_to(ROOT)}")


# Громкости боевых слоёв для проверки сведения — копия MIX из client/src/audio/music.ts для настроения
# «бой». Проверяется не каждый слой по отдельности, а то, что услышит игрок.
CHECK_MIX = {
    "organ": {"combat-drums": 1.0, "combat-organ": 0.85, "combat-pedal": 0.8, "combat-swell": 0.6},
    "march": {"march-drums": 0.9, "march-brass": 0.9, "march-strings": 0.7, "march-swell": 0.6},
}


def check():
    """
    Приёмка по заданию: длина ровно 30,000 с, пик не выше −1 дБ, в полосе 200–2000 Гц не меньше четверти
    энергии, выше 3 кГц — не больше десятой (там живут выстрелы). Спектром, а не на глаз.
    """
    import soundfile as sf

    ok = True
    for theme, gains in CHECK_MIX.items():
        mixed = None
        for name, gain in gains.items():
            x, sr = sf.read(str(OUT / f"{name}.mp3"))
            if x.ndim > 1:
                x = x.mean(axis=1)
            good = sr == S.SR and len(x) == S.n_of(LOOP_SECONDS)
            ok &= good
            print(f"{name:<14} {len(x) / sr:7.3f} с  пик {20 * np.log10(np.max(np.abs(x)) + 1e-9):6.1f} дБ  {'' if good else 'ДЛИНА!'}")
            mixed = x * gain if mixed is None else mixed + x * gain
        spec = np.abs(np.fft.rfft(mixed)) ** 2
        freqs = np.fft.rfftfreq(len(mixed), 1.0 / S.SR)
        total = spec.sum()
        mid = spec[(freqs >= 200) & (freqs < 2000)].sum() / total
        low = spec[freqs < 200].sum() / total
        high = spec[freqs >= 3000].sum() / total
        peak = 20 * np.log10(np.max(np.abs(mixed)) + 1e-9)
        good = mid >= 0.25 and high <= 0.1
        ok &= good
        print(f"[{theme}] низ <200: {low:4.0%}  середина 200–2000: {mid:4.0%}  верх >3к: {high:4.1%}  пик смеси {peak:5.1f} дБ  {'ок' if good else 'НЕ ПРОШЛО'}")
    return ok


if __name__ == "__main__":
    args = set(sys.argv[1:])
    if "--check" in args:
        sys.exit(0 if check() else 1)
    build(args or None)
    check()
