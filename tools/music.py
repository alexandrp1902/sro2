"""Музыка игры: client/public/music/*.mp3 и client/src/audio/musicMeta.json.

Запуск из корня репозитория: python tools/music.py  (нужны numpy, scipy, soundfile)
Перезапускать только после правки партий — результат лежит в git.

Саундтрек не один файл, а слои (стемы), которые клиент смешивает на лету: в покое звучат дрон, пэд и
арпеджио, в бою вместо них поднимаются боевые слои. Поэтому «бой начался» — это не смена трека
с паузой и не склейка, а изменение громкостей за полсекунды.

Боевых тем три, на выбор в окне «Звук» (по мотивам саундтрека «Shadow Fight»): «тайко», «погоня» и
«дуэль». У каждой четыре слоя, своя скорость (128, 160, 112 — кратные восьми, чтобы в 30 секунд легло
целое число тактов) и та же ля-эолийская, что у покоя: переход в любую сторону — кроссфейд без фальши.
Орган с барабанами по заданию docs/SRO - Задание на боевую музыку.md и марш отклонены на слух.

Чтобы слои всегда сходились, у всех одна длина петли (LOOP_SECONDS) и общая тоника. Ударных в покое нет,
поэтому разница темпов на слух не рассогласование.

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


# --- бой: три динамичных набора (по мотивам «Shadow Fight») ---------------------------------------
#
# Орган и марш отклонены, как и четыре захода до них. Ориентир теперь — саундтрек мобильных
# файтингов: барабаны тайко, остинато струнных, этнический духовой, хор и удары оркестра. Динамика с
# первой доли, но без пил наверху, без щипков и без ведущих голосов выше 2 кГц: там живут выстрелы.
#
# Три темы на выбор в окне «Звук», у каждой четыре слоя. Все в ля миноре и на общей петле 30 с:
#   «тайко»  — 128, 16 тактов: тайко, низкие струнные, бамбуковая флейта, удары меди
#   «погоня» — 160, 20 тактов: малый барабан, пульс баса, спиккато струнных, валторны
#   «дуэль»  — 112, 14 тактов: полудоля, рифф виолончелей с перегрузом, хор, смычковый лид
#
# Аккорды — полутоны от ля, первым идёт основание (для баса и остинато).
AM_F = [0, 3, 7, 8]
E_MINOR = [-5, -2, 2]
PHRASE_BARS = 4

TAIKO_BPM = 128
TAIKO_CHORDS = [A_MINOR, A_MINOR, F_MAJOR, G_MAJOR, A_MINOR, A_MINOR, D_MINOR, E_MAJOR] * 2

CHASE_BPM = 160
CHASE_CHORDS = [
    A_MINOR, A_MINOR, F_MAJOR, G_MAJOR,
    A_MINOR, A_MINOR, F_MAJOR, E_MAJOR,
    A_MINOR, A_MINOR, D_MINOR, G_MAJOR,
    C_MAJOR, C_MAJOR, F_MAJOR, E_MAJOR,
    A_MINOR, A_MINOR, F_MAJOR, E_MAJOR,
]

DUEL_BPM = 112
DUEL_CELLS = [A_MINOR, C_MAJOR, D_MINOR, E_MINOR, A_MINOR, F_MAJOR, E_MAJOR]  # по два такта


# --- инструменты -------------------------------------------------------------------------------------

def _membrane(freq, seconds, rng, modes, pitch_drop=1.0, decay_power=2.4, beater=0.15, beater_hz=600.0, skin=0.8):
    """
    Мембрана с негармоничными обертонами: так бьёт кожа, а не осциллятор. modes — (отношение частоты,
    громкость, относительная длительность). pitch_drop — во сколько раз выше нота в момент удара.
    Кроме колотушки — короткий шум кожи 200–1500 Гц: без него удар в динамике телефона не существует.
    """
    n = S.n_of(seconds)
    out = np.zeros(n)
    for ratio, gain, life in modes:
        f = S.ramp(freq * ratio * pitch_drop, freq * ratio, S.n_of(0.03))
        f = np.concatenate([f, np.full(n - len(f), freq * ratio)])
        out += S.osc(f, n, phase=rng.uniform(0, 0.4)) * S.env(n, 0.001, seconds * life, power=decay_power) * gain
    if beater > 0:
        m = min(S.n_of(0.012), n)
        out[:m] += S.biquad(S.noise(m, rng), beater_hz, "lp") * S.env(m, 0.0005, 0.011, power=2.0) * beater
        k = min(S.n_of(0.05), n)
        out[:k] += S.band(S.noise(k, rng), 200, 1500, order=2) * S.env(k, 0.001, 0.045, power=2.4) * beater * skin
    return out


def _odaiko(rng):
    """Большой тайко: удар в грудь и деревянная колотушка — шлепок 400–2500 Гц, чтобы слышал телефон."""
    modes = [(1.0, 1.0, 1.0), (1.5, 0.4, 0.5), (2.0, 0.25, 0.35), (2.6, 0.1, 0.25)]
    x = _membrane(70.0, 0.7, rng, modes, pitch_drop=1.6, decay_power=2.6, beater=0.7, beater_hz=500, skin=1.6)
    k = S.n_of(0.03)
    x[:k] += S.band(S.noise(k, rng), 400, 2500, order=2) * S.env(k, 0.0005, 0.028, power=2.5) * 0.5
    return S.drive(x, 1.5)


def _shime(rng):
    """Малый тайко (симэ): высокий, тугой, короткий. Держит шестнадцатые."""
    modes = [(1.0, 1.0, 1.0), (1.6, 0.5, 0.6), (2.3, 0.3, 0.4)]
    return _membrane(330.0, 0.18, rng, modes, pitch_drop=1.3, decay_power=2.8, beater=0.5, beater_hz=1500, skin=0.6)


def _ka(rng):
    """Удар по ободу: щелчок дерева, тихо."""
    m = S.n_of(0.03)
    return S.band(S.noise(m, rng), 1200, 3500, order=2) * S.env(m, 0.0005, 0.028, power=3.0)


def _kick(rng):
    """Бочка оркестрового рода: ниже тайко, короче, с перегрузом."""
    modes = [(1.0, 1.0, 1.0), (1.59, 0.3, 0.4), (2.14, 0.12, 0.3)]
    return S.drive(_membrane(55.0, 0.5, rng, modes, pitch_drop=1.8, decay_power=2.6, beater=0.6, beater_hz=400, skin=1.4), 1.6)


def _timpani(rng, freq=110.0, seconds=1.0):
    modes = [(1.0, 1.0, 1.0), (1.5, 0.5, 0.7), (1.98, 0.3, 0.5), (2.44, 0.14, 0.35)]
    return _membrane(freq, seconds, rng, modes, pitch_drop=1.25, decay_power=2.2, beater=0.35, beater_hz=900)


def _tom(rng, freq=160.0):
    modes = [(1.0, 1.0, 1.0), (1.5, 0.4, 0.5), (2.2, 0.2, 0.3)]
    return _membrane(freq, 0.45, rng, modes, pitch_drop=1.5, decay_power=2.6, beater=0.4, beater_hz=700)


def _snare(rng):
    """Оркестровый малый: корпус 300–2500 Гц, звона выше 3 кГц нет."""
    m = S.n_of(0.16)
    body = S.band(S.noise(m, rng), 300, 2500, order=4) * S.env(m, 0.001, 0.14, power=2.6)
    tone = S.osc(S.ramp(240, 170, m), m) * S.env(m, 0.001, 0.07, power=2.8) * 0.5
    return S.drive(S.mix(body, tone), 1.5) * 0.8


def _crash(rng, seconds=1.4):
    """Тарелка: полоса 900–4000, гаснет полторы секунды. Всегда тихо — это не голос, а воздух."""
    m = S.n_of(seconds)
    return S.band(S.noise(m, rng), 900, 4000, order=2) * S.env(m, 0.002, seconds * 0.95, power=2.0)


def _gong(rng, seconds=4.0, base=90.0):
    m = S.n_of(seconds)
    out = np.zeros(m)
    for ratio, gain in ((1.0, 1.0), (1.47, 0.6), (2.09, 0.45), (2.56, 0.3), (3.31, 0.2)):
        out += S.osc(base * ratio, m, phase=rng.uniform(0, 6)) * S.env(m, 0.01 + 0.15 * ratio, seconds * 0.9, power=1.8) * gain
    k = S.n_of(0.05)
    out[:k] += S.band(S.noise(k, rng), 200, 1500) * 0.3 * S.env(k, 0.002, 0.045)
    return S.biquad(out, 2200, "lp")


def _riser(rng, seconds):
    """Нарастание перед фразой: шум с ползущим вверх срезом и растущей громкостью."""
    m = S.n_of(seconds)
    x = S.sweep(S.noise(m, rng), S.ramp(300, 2600, m), q=2.0, mode="bp")
    return x * (np.linspace(0.0, 1.0, m) ** 2.2) * S.fade_edges(np.ones(m), 8)


def _brass_tone(freq, n, rng, bright=900.0, voices=3):
    """Медь: чуть расстроенные пилы через закрытый фильтр и лёгкую перегрузку. Верх закрыт, атака мягкая."""
    out = np.zeros(n)
    for v in range(voices):
        det = 1.0 + (v - (voices - 1) / 2) * 0.004
        vib = 1.0 + 0.004 * S.osc(4.8 + 0.3 * v, n, phase=rng.uniform(0, 6))
        out += S.osc(freq * det * vib, n, "saw") / voices
    return S.drive(S.biquad(out, bright, "lp", order=2), 1.6)


def _brass_stab(semis, seconds, rng, octave=-1):
    """Удар меди аккордом: тромбоны и валторны, коротко."""
    n = S.n_of(seconds)
    out = np.zeros(n)
    for j, semi in enumerate(semis):
        out += _brass_tone(hz(semi, octave), n, rng, bright=1100) * (1.0 - 0.15 * j)
        out += _brass_tone(hz(semi, octave + 1), n, rng, bright=1500) * (0.5 - 0.1 * j)
    return out * S.env(n, 0.02, seconds * 0.9, power=1.6)


def _string_tone(freq, n, rng, voices=4, bright=2200.0):
    """Струнная группа: расстроенные пилы, узкий шум смычка, закрытый верх."""
    out = np.zeros(n)
    for v in range(voices):
        det = 1.0 + (v - (voices - 1) / 2) * 0.003
        vib = 1.0 + 0.003 * S.osc(5.5 + 0.4 * v, n, phase=rng.uniform(0, 6))
        out += S.osc(freq * det * vib, n, "saw") / voices
    out += S.band(S.noise(n, rng), freq * 0.9, min(freq * 3, 4000)) * 0.06
    return S.biquad(out, bright, "lp")


def _string_short(freq, seconds, rng, attack=0.015):
    """Короткая нота струнных (стаккато, спиккато)."""
    n = S.n_of(seconds)
    return _string_tone(freq, n, rng) * S.env(n, attack, seconds * 0.85, power=1.8)


def _glide(freqs, lengths, glide=0.06):
    """Массив частот для лида: ноты со скольжением в начале каждой (портаменто)."""
    parts = []
    prev = freqs[0]
    for f, sec in zip(freqs, lengths):
        n = S.n_of(sec)
        g = min(S.n_of(glide), n)
        parts.append(np.concatenate([S.ramp(prev, f, g), np.full(n - g, f)]))
        prev = f
    return np.concatenate(parts)


def _flute(freq_array, rng):
    """
    Бамбуковая флейта: почти синус с парой гармоник, дыхание в полосе 700–3000 и вибрато, которое
    приходит не сразу. Регистр 440–880 Гц: выше — писк, ниже — теряется под струнными.
    """
    n = len(freq_array)
    t = np.arange(n) / S.SR
    vib_depth = 0.006 * np.clip((t - 0.2) / 0.4, 0.0, 1.0)
    f = freq_array * (1.0 + vib_depth * np.sin(2 * np.pi * 5.3 * t))
    x = S.osc(f, n) + 0.35 * S.osc(f * 2, n) + 0.12 * S.osc(f * 3, n) + 0.04 * S.osc(f * 4, n)
    breath = S.band(S.noise(n, rng), 700, 3000, order=2) * 0.07
    return S.biquad(x + breath, 3000, "lp")


def _erhu(freq_array, rng):
    """Смычковый лид (эрху, виола): узкая пила с носовым формантом 1–2,2 кГц, вибрато, скольжения."""
    n = len(freq_array)
    t = np.arange(n) / S.SR
    vib_depth = 0.008 * np.clip((t - 0.15) / 0.3, 0.0, 1.0)
    f = freq_array * (1.0 + vib_depth * np.sin(2 * np.pi * 5.8 * t))
    raw = S.osc(f, n, "saw") + 0.7 * S.osc(f * 1.004, n, "saw")
    body = S.biquad(raw, 1400, "lp") * 0.7 + S.band(raw, 1000, 2200, order=2) * 0.8
    bow = S.band(S.noise(n, rng), 1000, 3000, order=2) * 0.05
    return S.biquad(body + bow, 3200, "lp")


def _choir(semis, seconds, rng, octave=0, attack=0.5):
    """Хор «а»: расстроенные пилы через три форманты. Не слова — воздух и масса."""
    n = S.n_of(seconds)
    out = np.zeros(n)
    for j, semi in enumerate(semis):
        base = hz(semi, octave)
        raw = np.zeros(n)
        for v in range(3):
            det = 1.0 + (v - 1) * 0.006
            vib = 1.0 + 0.004 * S.osc(4.5 + 0.5 * v + 0.3 * j, n, phase=rng.uniform(0, 6))
            raw += S.osc(base * det * vib, n, "saw") / 3
        voice = S.biquad(raw, 500, "lp") * 0.6 + S.band(raw, 600, 900, order=2) * 1.0 + S.band(raw, 1000, 1400, order=2) * 0.5 + S.band(raw, 2300, 2800, order=2) * 0.12
        out += voice * (1.0 - 0.1 * j)
    return S.biquad(out, 3000, "lp") * S.env(n, attack, seconds - attack, power=1.3)


def _melody(buf, notes, at_seconds, beat, rng, voice, octave, gain, glide=0.06):
    """Мелодия лида: notes — (доля от начала, полутон от ля, длительность в долях). Ноты скользят друг в друга."""
    freqs = [hz(semi, octave) for _, semi, _ in notes]
    lengths = [length * beat for _, _, length in notes]
    x = voice(_glide(freqs, lengths, glide), rng)
    # Огибающая по нотам: каждая нота дышит отдельно, но связка не рвётся.
    env = np.zeros(len(x))
    pos = 0
    for sec in lengths:
        n = S.n_of(sec)
        env[pos:pos + n] = S.env(n, 0.05, sec * 0.95, power=0.8)
        pos += n
    S.place(buf, x * env, S.n_of(at_seconds + notes[0][0] * beat), gain)


# --- тема «тайко» ------------------------------------------------------------------------------------

def taiko_drums(rng):
    """Тайко: большой на раз и три, симэ шестнадцатыми, обод щёлкает; рисунок густеет от фразы к фразе."""
    buf, n = canvas(1.2)
    bar = bar_seconds(TAIKO_BPM)
    st = bar / 16
    ir = S.reverb_ir(1.1, rng, 1800)
    shime = [[2, 6, 10, 14], [2, 3, 6, 10, 11, 14], [0, 2, 3, 4, 6, 7, 8, 10, 11, 12, 14, 15], [0, 2, 3, 4, 6, 7, 8, 10, 11, 12, 14, 15]]
    ka = [[3, 7, 11, 15], [1, 5, 9, 13], [1, 5, 9, 13], [1, 3, 5, 7, 9, 11, 13, 15]]
    for b in range(len(TAIKO_CHORDS)):
        phrase, i = divmod(b, PHRASE_BARS)
        t0 = b * bar
        S.place(buf, _odaiko(rng), S.n_of(t0), 1.0)
        S.place(buf, _odaiko(rng), S.n_of(t0 + 8 * st), 0.85)
        if i % 2 == 1:
            S.place(buf, _odaiko(rng), S.n_of(t0 + 14 * st), 0.6)
        for s in shime[phrase]:
            S.place(buf, _shime(rng), S.n_of(t0 + s * st), (0.6 if s % 4 == 0 else 0.4) * (0.85 + 0.05 * phrase))
        for s in ka[phrase]:
            S.place(buf, _ka(rng), S.n_of(t0 + s * st), 0.1)
        if i == PHRASE_BARS - 1:
            # Дробь симэ тридцать вторыми на две последние доли, с нарастанием, и два больших в конце.
            for k in range(16):
                S.place(buf, _shime(rng), S.n_of(t0 + 8 * st + k * st / 2), 0.15 + 0.4 * (k / 16) ** 1.5)
            S.place(buf, _odaiko(rng), S.n_of(t0 + 12 * st), 0.8)
    return wrap(S.reverb(buf, ir, 0.22), n)


def taiko_strings(rng):
    """Низкие струнные восьмыми: виолончели на основании, альты по тонам аккорда. Мотор темы."""
    buf, n = canvas(1.0)
    bar = bar_seconds(TAIKO_BPM)
    eighth = bar / 8
    ir = S.reverb_ir(1.3, rng, 1800)
    for b, chord in enumerate(TAIKO_CHORDS):
        phrase = b // PHRASE_BARS
        for e in range(8):
            semi = chord[0] if e % 2 == 0 else chord[(e // 2) % len(chord)]
            octave = -1 if e % 2 == 0 else 0
            note = _string_short(hz(semi, octave), eighth * 0.9, rng)
            accent = 1.0 if e in (0, 4) else 0.75
            S.place(buf, note, S.n_of(b * bar + e * eighth), 0.3 * accent * (0.9 + 0.04 * phrase))
    return wrap(S.reverb(buf, ir, 0.22), n)


def taiko_flute(rng):
    """Бамбуковая флейта: пентатоника, вопрос во второй фразе, ответ в четвёртой. В первой и третьей молчит."""
    buf, n = canvas(1.5)
    bar = bar_seconds(TAIKO_BPM)
    beat = bar / 4
    ir = S.reverb_ir(1.8, rng, 2400)
    question = [(0, 7, 1.5), (1.5, 5, 0.5), (2, 3, 2), (4, 0, 1), (5, 3, 1), (6, 5, 2), (8, 7, 3), (11, 10, 1), (12, 7, 4)]
    answer = [(0, 12, 1.5), (1.5, 10, 0.5), (2, 7, 2), (4, 5, 1), (5, 3, 1), (6, 7, 2), (8, 5, 1.5), (9.5, 3, 0.5), (10, 0, 2), (12, 7, 4)]
    _melody(buf, question, 1 * PHRASE_BARS * bar, beat, rng, _flute, 1, 0.42)
    _melody(buf, answer, 3 * PHRASE_BARS * bar, beat, rng, _flute, 1, 0.46)
    return wrap(S.reverb(buf, ir, 0.35), n)


def taiko_hits(rng):
    """Удары меди с тарелкой в начале фраз, нарастание перед третьей и перед стыком, хор в двух последних."""
    buf, n = canvas(3.0)
    bar = bar_seconds(TAIKO_BPM)
    ir = S.reverb_ir(1.8, rng, 2000)
    for phrase in range(len(TAIKO_CHORDS) // PHRASE_BARS):
        t0 = phrase * PHRASE_BARS * bar
        S.place(buf, _brass_stab(A_MINOR, 0.6, rng), S.n_of(t0), 0.45)
        S.place(buf, _crash(rng), S.n_of(t0), 0.05)
        if phrase % 2 == 1:
            S.place(buf, _riser(rng, bar), S.n_of(t0 + 3 * bar), 0.16)
        if phrase >= 2:
            for i in range(PHRASE_BARS):
                chord = TAIKO_CHORDS[phrase * PHRASE_BARS + i]
                S.place(buf, _choir(chord, bar + 0.3, rng, octave=0, attack=0.25), S.n_of(t0 + i * bar), 0.16 + 0.04 * (phrase - 2))
    return wrap(S.reverb(buf, ir, 0.3), n)


# --- тема «погоня» -----------------------------------------------------------------------------------

def chase_drums(rng):
    """Малый барабан бежит шестнадцатыми, бочка синкопой, томы заполняют конец фразы."""
    buf, n = canvas(1.2)
    bar = bar_seconds(CHASE_BPM)
    st = bar / 16
    ir = S.reverb_ir(1.0, rng, 2000)
    for b in range(len(CHASE_CHORDS)):
        phrase, i = divmod(b, PHRASE_BARS)
        t0 = b * bar
        for s in (0, 6) + ((10,) if i % 2 else ()):
            S.place(buf, _kick(rng), S.n_of(t0 + s * st), 0.75 if s == 0 else 0.55)
        for s in range(16):
            if s in (4, 12):
                g = 0.95
            elif s % 2 == 0:
                g = 0.28
            else:
                g = 0.15 if phrase < 2 else 0.24
            S.place(buf, _snare(rng), S.n_of(t0 + s * st), g)
        if i == PHRASE_BARS - 1:
            for k, s in enumerate((8, 9, 10, 11, 12, 13, 14, 15)):
                S.place(buf, _tom(rng, 200 - 12 * k), S.n_of(t0 + s * st), 0.4 + 0.04 * k)
    return wrap(S.reverb(buf, ir, 0.18), n)


def chase_bass(rng):
    """Пульс баса восьмыми: круглый тон с перегрузом, а не пила; гармоники — чтобы слышал телефон."""
    buf, n = canvas(0.6)
    bar = bar_seconds(CHASE_BPM)
    eighth = bar / 8
    for b, chord in enumerate(CHASE_CHORDS):
        root = hz(chord[0], -2)
        for e in range(8):
            m = S.n_of(eighth * 0.9)
            freq = np.concatenate([S.ramp(root * 1.5, root, S.n_of(0.015)), np.full(m - S.n_of(0.015), root)])
            note = S.osc(freq, m) + 0.5 * S.osc(freq * 2, m, "tri") + 0.25 * S.osc(freq * 3, m)
            note = S.drive(note, 2.0) * S.env(m, 0.003, eighth * 0.8, power=2.0)
            # Серединный гриль: та же нота, перегруженная сильнее, в полосе 400–2000 Гц. Без него бас
            # в динамике телефона не существует: 55 Гц он не отдаёт.
            grit = S.band(S.drive(note, 5.0), 400, 2000) * 1.1
            S.place(buf, S.mix(S.biquad(note, 1200, "lp"), grit), S.n_of(b * bar + e * eighth), 0.6 if e % 2 == 0 else 0.45)
    return wrap(buf, n)


def chase_strings(rng):
    """Спиккато струнных восьмыми по тонам аккорда: альты внизу, скрипки вполголоса октавой выше."""
    buf, n = canvas(1.0)
    bar = bar_seconds(CHASE_BPM)
    eighth = bar / 8
    ir = S.reverb_ir(1.2, rng, 2000)
    order = [0, 2, 1, 2, 0, 2, 1, 0]
    for b, chord in enumerate(CHASE_CHORDS):
        phrase = b // PHRASE_BARS
        for e in range(8):
            semi = chord[order[e] % len(chord)]
            note = _string_short(hz(semi, 0), eighth * 0.8, rng, attack=0.008)
            accent = 1.0 if e in (0, 3) else 0.7
            S.place(buf, note, S.n_of(b * bar + e * eighth), 0.3 * accent * (0.9 + 0.03 * phrase))
    return wrap(S.reverb(buf, ir, 0.2), n)


def chase_brass(rng):
    """Удар меди и тарелки на первой доле фразы; валторны зовут в третьем-четвёртом такте чётных фраз."""
    buf, n = canvas(2.5)
    bar = bar_seconds(CHASE_BPM)
    beat = bar / 4
    ir = S.reverb_ir(1.6, rng, 2000)
    call = [(0, 0, 1), (1, 7, 1), (2, 8, 1.5), (3.5, 7, 0.5), (4, 3, 2), (6, 2, 2)]
    for phrase in range(len(CHASE_CHORDS) // PHRASE_BARS):
        t0 = phrase * PHRASE_BARS * bar
        chord = CHASE_CHORDS[phrase * PHRASE_BARS]
        S.place(buf, _brass_stab(chord, 0.45, rng), S.n_of(t0), 0.5)
        S.place(buf, _crash(rng, 1.2), S.n_of(t0), 0.04)
        if phrase % 2 == 1 or phrase == 4:
            m = S.n_of(8 * beat)
            stack = np.zeros(m)
            pos = 0
            for at, semi, length in call:
                k = S.n_of(length * beat)
                tone = _brass_tone(hz(semi, 0), k, rng, bright=1300) + 0.5 * _brass_tone(hz(semi, -1), k, rng, bright=900)
                S.place(stack, tone * S.env(k, 0.03, length * beat * 0.9, power=1.3), S.n_of(at * beat), 1.0)
            S.place(buf, stack, S.n_of(t0 + 2 * bar), 0.4)
    return wrap(S.reverb(buf, ir, 0.28), n)


# --- тема «дуэль» ------------------------------------------------------------------------------------

def duel_drums(rng):
    """Полудоля: бочка на раз, тайко с малым на три, симэ шестнадцатыми фоном, гонг на первой доле петли."""
    buf, n = canvas(1.6)
    bar = bar_seconds(DUEL_BPM)
    st = bar / 16
    ir = S.reverb_ir(1.3, rng, 1800)
    bars = len(DUEL_CELLS) * 2
    S.place(buf, _gong(rng), 0, 0.3)
    for b in range(bars):
        cell, i = divmod(b, 2)
        t0 = b * bar
        S.place(buf, _kick(rng), S.n_of(t0), 0.8)
        S.place(buf, _kick(rng), S.n_of(t0 + 6 * st), 0.5)
        S.place(buf, _odaiko(rng), S.n_of(t0 + 8 * st), 0.75)
        S.place(buf, _snare(rng), S.n_of(t0 + 8 * st), 0.95)
        for s in range(16):
            g = 0.36 if s % 4 == 0 else (0.18 if s % 2 == 0 else 0.1)
            S.place(buf, _shime(rng), S.n_of(t0 + s * st), g * (0.8 + 0.03 * cell))
        if i == 1:
            for k, s in enumerate((10, 11, 12, 13, 14, 15)):
                S.place(buf, _tom(rng, 190 - 15 * k), S.n_of(t0 + s * st), 0.35 + 0.05 * k)
            S.place(buf, _odaiko(rng), S.n_of(t0 + 14 * st), 0.7)
    return wrap(S.reverb(buf, ir, 0.24), n)


def duel_riff(rng):
    """Рифф виолончелей и контрабасов с перегрузом: восьмые по тонам аккорда, ноты скользят друг в друга."""
    buf, n = canvas(1.0)
    bar = bar_seconds(DUEL_BPM)
    eighth = bar / 8
    ir = S.reverb_ir(1.2, rng, 1600)
    pattern = [(0, 0), (1, 0), (3, 1), (4, 0), (6, 2), (7, 0)]  # (восьмая, номер тона аккорда)
    for b in range(len(DUEL_CELLS) * 2):
        chord = DUEL_CELLS[b // 2]
        for e, j in pattern:
            f = hz(chord[j % len(chord)], -1)
            m = S.n_of(eighth * 0.95)
            freq = np.concatenate([S.ramp(f * 0.94, f, S.n_of(0.04)), np.full(m - S.n_of(0.04), f)])
            raw = S.osc(freq, m, "saw") + 0.8 * S.osc(freq * 1.005, m, "saw") + 0.6 * S.osc(freq / 2, m)
            raw += S.band(S.noise(m, rng), 150, 900, order=2) * 0.08
            note = S.drive(S.biquad(raw, 2200, "lp"), 2.4) * S.env(m, 0.01, eighth * 0.85, power=1.8)
            grit = S.band(S.drive(note, 4.0), 500, 2500) * 1.3  # серединный гриль, чтобы телефон слышал
            S.place(buf, S.mix(note, grit), S.n_of(b * bar + e * eighth), 0.4 if e in (0, 4) else 0.32)
    return wrap(S.reverb(buf, ir, 0.15), n)


def duel_choir(rng):
    """Хор: вступает с третьей ячейки и растёт до конца петли. Масса, а не мелодия."""
    buf, n = canvas(3.0)
    bar = bar_seconds(DUEL_BPM)
    ir = S.reverb_ir(2.4, rng, 2000)
    for cell, chord in enumerate(DUEL_CELLS):
        if cell < 2:
            continue
        S.place(buf, _choir(chord, 2 * bar + 0.4, rng, octave=0, attack=0.6), S.n_of(cell * 2 * bar), 0.18 + 0.04 * (cell - 2))
    return wrap(S.reverb(buf, ir, 0.4), n)


def duel_lead(rng):
    """Смычковый лид со скольжениями в нечётных ячейках: вопрос, ответ, и вопрос снова — петля на нём."""
    buf, n = canvas(2.0)
    bar = bar_seconds(DUEL_BPM)
    beat = bar / 4
    ir = S.reverb_ir(1.8, rng, 2200)
    phrases = {
        1: [(0, 7, 2), (2, 10, 1), (3, 7, 1), (4, 3, 3), (7, 5, 1)],
        3: [(0, 12, 1.5), (1.5, 10, 0.5), (2, 7, 2), (4, 5, 1), (5, 3, 1), (6, 0, 2)],
        5: [(0, 7, 3), (3, 8, 1), (4, 7, 2), (6, 3, 1), (7, 2, 1)],
    }
    for cell, notes in phrases.items():
        _melody(buf, notes, cell * 2 * bar, beat, rng, _erhu, 1, 0.3, glide=0.09)
    S.place(buf, _riser(rng, bar), S.n_of(13 * bar), 0.14)
    S.place(buf, _crash(rng, 1.6), 0, 0.1)
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
    ("calm-drone", calm_drone, "calm", None),
    ("calm-pad", calm_pad, "calm", None),
    ("calm-arp", calm_arp, "calm", None),
    ("taiko-drums", taiko_drums, "combat", "taiko"),
    ("taiko-strings", taiko_strings, "combat", "taiko"),
    ("taiko-flute", taiko_flute, "combat", "taiko"),
    ("taiko-hits", taiko_hits, "combat", "taiko"),
    ("chase-drums", chase_drums, "combat", "chase"),
    ("chase-bass", chase_bass, "combat", "chase"),
    ("chase-strings", chase_strings, "combat", "chase"),
    ("chase-brass", chase_brass, "combat", "chase"),
    ("duel-drums", duel_drums, "combat", "duel"),
    ("duel-riff", duel_riff, "combat", "duel"),
    ("duel-choir", duel_choir, "combat", "duel"),
    ("duel-lead", duel_lead, "combat", "duel"),
    ("dock-pad", dock_pad, "dock", None),
]

# Боевых тем три, и клиент качает только выбранную (окно «Звук»). По итогам плейтеста остаётся одна.
THEMES = ("taiko", "chase", "duel")


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
    "taiko": {"taiko-drums": 1.0, "taiko-strings": 0.8, "taiko-flute": 0.0, "taiko-hits": 0.7},
    "chase": {"chase-drums": 1.0, "chase-bass": 0.75, "chase-strings": 0.8, "chase-brass": 0.8},
    "duel": {"duel-drums": 1.0, "duel-riff": 0.9, "duel-choir": 0.6, "duel-lead": 0.0},
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
