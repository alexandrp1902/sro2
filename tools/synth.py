"""Маленький синтезатор на numpy: общие кирпичи для tools/sfx.py и tools/music.py.

Записанных звуков у игры нет и взяться им неоткуда, поэтому весь звук игра рисует сама — так же, как
tools/sprites.py режет спрайты из листов генератора. Здесь лежит то, из чего собираются и выстрелы, и музыка:
осцилляторы, огибающие, фильтры, реверберация и запись в mp3.

Частота 44100 и моно по умолчанию: звуки в игре панорамируются на лету (StereoPannerNode), поэтому стерео им
не нужно — только вдвое больше байтов. Стерео остаётся музыке.
"""

from __future__ import annotations

import numpy as np
from scipy import signal

SR = 44100


# --- время и шум -------------------------------------------------------------------------------------

def n_of(seconds: float) -> int:
    """Сколько отсчётов в такой длительности."""
    return int(round(seconds * SR))


def noise(n: int, rng: np.random.Generator) -> np.ndarray:
    """Белый шум. Все случайные звуки берут шум только отсюда — иначе варианты не повторить по зерну."""
    return rng.standard_normal(n)


# --- огибающие ---------------------------------------------------------------------------------------

def ramp(start: float, stop: float, n: int, curve: str = "exp") -> np.ndarray:
    """Плавный переход числа: exp — по логарифму (так слышит ухо), lin — по прямой."""
    if n <= 1:
        return np.full(max(n, 1), stop, dtype=np.float64)
    if curve == "exp":
        start = max(start, 1e-6)
        stop = max(stop, 1e-6)
        return np.exp(np.linspace(np.log(start), np.log(stop), n))
    return np.linspace(start, stop, n)


def env(n: int, attack: float, decay: float, hold: float = 0.0, power: float = 2.5) -> np.ndarray:
    """
    Огибающая «удар и затухание»: атака, полка, спад.

    Спад по степени, а не по прямой: прямая гаснет неестественно ровно, и короткий звук выходит
    синтетическим «пшиком». Атака никогда не нулевая — мгновенный старт даёт щелчок.
    """
    a = max(n_of(attack), 2)
    h = n_of(hold)
    d = max(n - a - h, 1)
    out = np.concatenate([
        np.linspace(0.0, 1.0, a),
        np.ones(h),
        np.linspace(1.0, 0.0, d) ** power,
    ])
    return fit(out, n)


def fade_edges(x: np.ndarray, ms: float = 4.0) -> np.ndarray:
    """
    Рампы по краям. Обязательны всем звукам: обрыв волны на ненулевом значении слышен как щелчок,
    а щелчки в бою складываются в треск.
    """
    k = min(n_of(ms / 1000), len(x) // 2)
    if k < 2:
        return x
    out = x.copy()
    out[:k] *= np.linspace(0.0, 1.0, k)
    out[-k:] *= np.linspace(1.0, 0.0, k)
    return out


def fit(x: np.ndarray, n: int) -> np.ndarray:
    """Подогнать массив под длину: обрезать или дополнить нулями."""
    if len(x) == n:
        return x
    if len(x) > n:
        return x[:n]
    return np.concatenate([x, np.zeros(n - len(x))])


# --- осцилляторы -------------------------------------------------------------------------------------

def osc(freq, n: int, kind: str = "sine", phase: float = 0.0) -> np.ndarray:
    """
    Волна заданной формы. freq — число или массив на каждый отсчёт (тогда это глиссандо).

    Фаза копится суммой, а не считается как 2*pi*f*t: при меняющейся частоте второй способ рвёт волну,
    и вместо съезжающего тона получается треск.
    """
    f = np.full(n, float(freq)) if np.isscalar(freq) else fit(np.asarray(freq, dtype=np.float64), n)
    ph = np.cumsum(2 * np.pi * f / SR) + phase
    if kind == "sine":
        return np.sin(ph)
    if kind == "saw":
        return 2.0 * ((ph / (2 * np.pi)) % 1.0) - 1.0
    if kind == "square":
        return np.sign(np.sin(ph))
    if kind == "tri":
        return 2.0 * np.abs(2.0 * ((ph / (2 * np.pi)) % 1.0) - 1.0) - 1.0
    raise ValueError("неизвестная форма волны: " + kind)


# --- фильтры -----------------------------------------------------------------------------------------

def _clip_hz(f):
    return np.clip(f, 20.0, SR * 0.45)


def biquad(x: np.ndarray, cutoff: float, mode: str = "lp", order: int = 2) -> np.ndarray:
    """Обычный фильтр с постоянным срезом — через scipy, быстро."""
    btype = {"lp": "low", "hp": "high"}[mode]
    sos = signal.butter(order, _clip_hz(cutoff) / (SR / 2), btype=btype, output="sos")
    return signal.sosfilt(sos, x)


def band(x: np.ndarray, low: float, high: float, order: int = 2) -> np.ndarray:
    """Полоса пропускания. Ей же делается «рация»: 300-3000 Гц."""
    lo = _clip_hz(low) / (SR / 2)
    hi = _clip_hz(high) / (SR / 2)
    sos = signal.butter(order, [min(lo, hi * 0.99), hi], btype="band", output="sos")
    return signal.sosfilt(sos, x)


def sweep(x: np.ndarray, cutoff, q: float = 0.8, mode: str = "lp") -> np.ndarray:
    """
    Фильтр с ползущим срезом (цифровая state-variable схема). Именно он делает «вуф» плазмы и раскрытие
    взрыва: постоянный срез такого не даёт, а scipy фильтрует только неизменным коэффициентом.

    Схема устойчива до самой Найквисты — в отличие от классической схемы Чемберлина, которая на высоком
    срезе идёт вразнос.
    """
    n = len(x)
    fc = _clip_hz(np.full(n, float(cutoff)) if np.isscalar(cutoff) else fit(np.asarray(cutoff, dtype=np.float64), n))
    g = np.tan(np.pi * fc / SR)
    k = 1.0 / max(q, 0.05)
    a1 = 1.0 / (1.0 + g * (g + k))
    a2 = g * a1
    a3 = g * a2
    out = np.empty(n)
    ic1 = ic2 = 0.0
    for i in range(n):
        v3 = x[i] - ic2
        v1 = a1[i] * ic1 + a2[i] * v3
        v2 = ic2 + a2[i] * ic1 + a3[i] * v3
        ic1 = 2.0 * v1 - ic1
        ic2 = 2.0 * v2 - ic2
        if mode == "lp":
            out[i] = v2
        elif mode == "hp":
            out[i] = x[i] - k * v1 - v2
        else:
            out[i] = v1
    return out


# --- обработка ---------------------------------------------------------------------------------------

def drive(x: np.ndarray, amount: float = 2.0) -> np.ndarray:
    """Мягкое искажение: тангенс гиперболический вместо обрезания — громкое не превращается в хрип."""
    return np.tanh(x * amount) / np.tanh(amount)


def reverb_ir(seconds: float, rng: np.random.Generator, bright: float = 2500.0) -> np.ndarray:
    """
    Импульсная характеристика зала: затухающий шум с приглушённым верхом. Файла-импульса в проекте нет
    и не будет — зал тоже рисуем сами.
    """
    n = n_of(seconds)
    tail = noise(n, rng) * np.exp(-np.linspace(0.0, 6.0, n))
    tail = biquad(tail, bright, "lp")
    tail[: n_of(0.005)] = 0.0  # прямой звук добавит вызывающий, здесь только отражения
    return tail / (np.max(np.abs(tail)) + 1e-9)


def reverb(x: np.ndarray, ir: np.ndarray, wet: float = 0.3) -> np.ndarray:
    peak = np.max(np.abs(x))
    wet_sig = signal.fftconvolve(x, ir)[: len(x)]
    wet_sig /= np.max(np.abs(wet_sig)) + 1e-9
    return (1.0 - wet) * x + wet * wet_sig * peak


def delay(x: np.ndarray, ms: float, feedback: float = 0.35, wet: float = 0.3, taps: int = 6) -> np.ndarray:
    step = n_of(ms / 1000)
    out = x.copy()
    gain = feedback
    for i in range(1, taps + 1):
        shift = step * i
        if shift >= len(x):
            break
        out[shift:] += x[: len(x) - shift] * gain * wet
        gain *= feedback
    return out


def compress(x: np.ndarray, threshold: float = 0.35, ratio: float = 4.0) -> np.ndarray:
    """Компрессия по огибающей: держит разницу между тихим и громким в разумных пределах."""
    envelope = np.abs(signal.lfilter([0.002], [1.0, -0.998], np.abs(x)))
    over = np.maximum(envelope - threshold, 0.0)
    return x / (1.0 + over * (ratio - 1.0))


def dc_block(x: np.ndarray) -> np.ndarray:
    return signal.lfilter([1.0, -1.0], [1.0, -0.995], x)


def norm(x: np.ndarray, peak: float = 1.0) -> np.ndarray:
    m = np.max(np.abs(x))
    return x * (peak / m) if m > 1e-9 else x


def mix(*parts: np.ndarray) -> np.ndarray:
    """Сложить куски разной длины — по самому длинному."""
    n = max(len(p) for p in parts)
    out = np.zeros(n)
    for p in parts:
        out[: len(p)] += p
    return out


def place(target: np.ndarray, part: np.ndarray, at: int, gain: float = 1.0) -> None:
    """Вписать кусок в готовый буфер начиная с отсчёта at (для нот и очередей)."""
    at = max(at, 0)
    end = min(len(target), at + len(part))
    if end > at:
        target[at:end] += part[: end - at] * gain


# --- запись ------------------------------------------------------------------------------------------

def write_mp3(path, x: np.ndarray, quality: float = 0.35) -> int:
    """
    Записать mp3. quality — уровень сжатия libsndfile (0 — лучше и больше, 1 — хуже и меньше).

    mp3, а не ogg: игру смотрят с iPhone, а Safari ogg vorbis не обещает. mp3 играет везде.
    """
    import soundfile as sf

    path.parent.mkdir(parents=True, exist_ok=True)
    data = np.clip(x, -1.0, 1.0).astype(np.float32)
    sf.write(str(path), data.T if data.ndim > 1 else data, SR, format="MP3", compression_level=quality)
    return path.stat().st_size
