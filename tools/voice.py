"""Радиоэфир (M17b): речь из shared/chatter.json → client/public/radio/*.mp3 и client/src/audio/radioMeta.json.

Запуск из корня репозитория: python tools/voice.py [категория ...] [--force]
Нужны numpy, scipy, soundfile и edge-tts (pip install edge-tts). Результат лежит в git; перезапускать
только после правки реплик — изменённые строки перерисуются, остальные возьмутся из кэша.

Речь синтезируют нейронные голоса Edge («Прочесть вслух»): Дмитрий и Светлана. Сырой результат кэшируется
в tools/.cache/voice/ (не в git) по хешу текста и голоса, так что повторный запуск не ходит в сеть. Если сети
нет, реплика читается локальным голосом Windows (Pavel/Irina через System.Speech) и помечается в манифесте
как «sapi» — чтобы потом перегенерировать нейронным.

Поверх речи — обработка рации: полоса 300–3000 Гц, мягкое искажение, компрессия, шорох эфира. Щелчки
включения и отбоя не запекаются: они отдельные звуки (squelch-open/close в tools/sfx.py), клиент ставит их сам.
"""

from __future__ import annotations

import asyncio
import hashlib
import json
import subprocess
import sys
import time
from pathlib import Path

import numpy as np
from scipy import signal

sys.path.insert(0, str(Path(__file__).resolve().parent))

import synth as S

ROOT = Path(__file__).resolve().parent.parent
SRC = ROOT / "shared" / "chatter.json"
OUT = ROOT / "client" / "public" / "radio"
META = ROOT / "client" / "src" / "audio" / "radioMeta.json"
CACHE = ROOT / "tools" / ".cache" / "voice"

# Голос в полосе 300–3000 Гц: 64 кбит/с хватает с запасом, а банк выходит вдвое легче.
QUALITY = 0.6
PEAK = 0.8

# Кто говорит → голос Edge, темп и высота. Пират ниже и медленнее, рейнджер быстрее и суше.
# Ключ voice в реплике («m»/«f») выбирает пол там, где у говорящего есть оба голоса.
PROFILES = {
    # Светлана — помедленнее и чуть ниже: так её разобрали на плейтесте. «Невнятность» первой версии была
    # не в голосе, а в обрезанных потоках Edge (см. tts_edge): реплика на 4 с приходила длиной 1,8 с.
    "trader": {"m": ("ru-RU-DmitryNeural", "-3%", "+5Hz"), "f": ("ru-RU-SvetlanaNeural", "-12%", "-10Hz")},
    # Понижение голоса держим малым: −25 Гц у Дмитрия превращало речь в невнятный рык.
    "pirate": {"m": ("ru-RU-DmitryNeural", "-5%", "-8Hz"), "f": ("ru-RU-SvetlanaNeural", "+0%", "-12Hz")},
    "ranger": {"m": ("ru-RU-DmitryNeural", "+4%", "-2Hz"), "f": ("ru-RU-SvetlanaNeural", "+4%", "+2Hz")},
    "convoy": {"m": ("ru-RU-DmitryNeural", "-7%", "+0Hz"), "f": ("ru-RU-SvetlanaNeural", "-6%", "+8Hz")},
}

# Обработка рации по говорящему: (нижняя частота, верхняя, перегруз, шум эфира).
# Первая версия (полоса до 3 кГц крутым срезом, перегруз до 2,4) была «как рация», но речь стала невнятной:
# согласные живут в 2–5 кГц, а перегруз размазывает их. Теперь полоса шире, срез пологий, перегруз едва слышен —
# рация читается по щелчкам и лёгкому шороху, а не по каше.
RADIO = {
    "trader": (250, 4200, 1.15, 0.004),
    "pirate": (250, 3800, 1.3, 0.007),
    "ranger": (300, 4000, 1.2, 0.005),
    "convoy": (280, 3800, 1.25, 0.009),
}

SAPI_VOICES = {"m": "Microsoft Pavel Desktop", "f": "Microsoft Irina Desktop"}


def cache_key(voice: str, rate: str, pitch: str, text: str) -> Path:
    h = hashlib.sha1(f"{voice}|{rate}|{pitch}|{text}".encode("utf-8")).hexdigest()[:20]
    return CACHE / f"{h}.mp3"


async def tts_edge(text: str, voice: str, rate: str, pitch: str, path: Path) -> None:
    """Edge иногда отдаёт пустой поток без ошибки — тогда файл нулевой длины; пробуем ещё, до трёх раз."""
    import edge_tts

    last: Exception | None = None
    for attempt in range(5):
        try:
            await edge_tts.Communicate(text, voice, rate=rate, pitch=pitch).save(str(path))
        except Exception as error:  # noqa: BLE001 — сеть, лимит запросов: подождать и повторить
            last = error
        if complete(path, text):
            return
        path.unlink(missing_ok=True)
        await asyncio.sleep(2.0 * (attempt + 1))
    raise RuntimeError(f"Edge не отдал звук для «{text[:40]}»: {last}")


# Меньше стольких миллисекунд на знак речь быть не может: поток пришёл обрезанным.
MIN_MS_PER_CHAR = 55


def complete(path: Path, text: str) -> bool:
    """
    Edge иногда обрывает поток посреди фразы и не считает это ошибкой: файл есть, а в нём половина
    реплики, и на слух это «читает какие-то буквы». Ловим по длительности: русская речь идёт примерно
    80–100 мс на знак, всё, что короче 55, — обрывок.
    """
    import soundfile as sf

    if not path.exists() or path.stat().st_size == 0:
        return False
    try:
        x = trim(load_audio(path))  # без тишины по краям: обрывок с длинной паузой в конце сошёл бы за целый
    except RuntimeError:
        return False
    return len(x) / S.SR * 1000 >= MIN_MS_PER_CHAR * len(text.strip())


def tts_sapi(text: str, sex: str, path: Path) -> None:
    """Запасной путь без сети: локальный голос Windows через PowerShell, wav в кэш."""
    wav = path.with_suffix(".wav")
    script = (
        "Add-Type -AssemblyName System.Speech; "
        "$s = New-Object System.Speech.Synthesis.SpeechSynthesizer; "
        f"try {{ $s.SelectVoice('{SAPI_VOICES[sex]}') }} catch {{}}; "
        f"$s.SetOutputToWaveFile('{wav}'); $s.Speak([Console]::In.ReadToEnd()); $s.SetOutputToNull(); $s.Dispose()"
    )
    subprocess.run(["powershell", "-NoProfile", "-Command", script], input=text.encode("utf-8"), check=True)
    # Файл отпускается не мгновенно: переименование иногда получает «отказано в доступе» — подождать и повторить.
    for attempt in range(5):
        try:
            wav.replace(path)
            return
        except PermissionError:
            time.sleep(0.5 * (attempt + 1))
    wav.replace(path)


def load_audio(path: Path) -> np.ndarray:
    import soundfile as sf

    x, sr = sf.read(str(path))
    if x.ndim > 1:
        x = x.mean(axis=1)
    if sr != S.SR:
        from math import gcd

        g = gcd(S.SR, sr)
        x = signal.resample_poly(x, S.SR // g, sr // g)
    return x.astype(np.float64)


def trim(x: np.ndarray, threshold: float = 0.01, pad: float = 0.06) -> np.ndarray:
    """Срезать тишину по краям: синтез отдаёт паузу до и после, а в эфире реплика идёт сразу за щелчком."""
    loud = np.flatnonzero(np.abs(x) > threshold)
    if len(loud) == 0:
        return x
    a = max(0, loud[0] - S.n_of(pad))
    b = min(len(x), loud[-1] + S.n_of(pad))
    return x[a:b]


def radio(x: np.ndarray, speaker: str, rng: np.random.Generator) -> np.ndarray:
    """Рация: узкая полоса, перегруз, компрессия, шорох эфира под голосом и после него."""
    low, high, drive, hiss = RADIO[speaker]
    x = trim(S.norm(x, 0.9))
    x = S.band(x, low, high, order=1)  # пологий срез: 6 дБ на октаву, согласные остаются
    x = S.compress(x, threshold=0.3, ratio=2.0)
    x = S.drive(S.norm(x, 0.7), drive)
    n = len(x) + S.n_of(0.12)
    x = S.fit(x, n)
    bed = S.band(S.noise(n, rng), low, high, order=2) * hiss
    # Шорох громче в паузах: простая инверсная огибающая, как у настоящего шумоподавителя.
    envelope = np.abs(signal.lfilter([0.004], [1.0, -0.996], np.abs(x)))
    bed *= 1.0 - np.clip(envelope * 6.0, 0.0, 0.7)
    out = S.dc_block(x + bed)
    return S.fade_edges(S.norm(out, PEAK), 6.0)


async def synth_all(jobs: list[dict], force: bool) -> str:
    """Синтезировать всё, чего нет в кэше. Возвращает движок: edge или sapi."""
    engine = "edge"
    todo = [j for j in jobs if force or not complete(j["cache"], j["text"])]
    if not todo:
        return engine
    CACHE.mkdir(parents=True, exist_ok=True)
    limit = asyncio.Semaphore(2)  # больше — Edge начинает отдавать пустые потоки

    async def one(job):
        async with limit:
            await tts_edge(job["text"], job["voice"], job["rate"], job["pitch"], job["cache"])
            print(f"  {job['key']:<16} {job['voice'][6:-6]:<9} «{job['text'][:48]}»")

    try:
        await asyncio.gather(*(one(j) for j in todo))
    except Exception as error:  # noqa: BLE001 — любой сбой сети: уходим на локальные голоса
        print(f"Edge недоступен ({type(error).__name__}: {error}); читаю локальными голосами Windows")
        engine = "sapi"
        for job in todo:
            if not complete(job["cache"], job["text"]):
                tts_sapi(job["text"], job["sex"], job["cache"])
                print(f"  {job['key']:<16} sapi      «{job['text'][:48]}»")
    return engine


def build(only: set[str] | None, force: bool) -> None:
    import soundfile as sf

    data = json.loads(SRC.read_text(encoding="utf-8"))
    jobs = []
    for category, block in data["categories"].items():
        if only and category not in only:
            continue
        speaker = block["speaker"]
        for line in block["lines"]:
            sex = line.get("voice", "m")
            voice, rate, pitch = PROFILES[speaker][sex]
            key = f"{category}-{line['id']}"
            jobs.append({
                "key": key, "category": category, "speaker": speaker, "sex": sex, "text": line["text"],
                "voice": voice, "rate": rate, "pitch": pitch, "cache": cache_key(voice, rate, pitch, line["text"]),
            })

    engine = asyncio.run(synth_all(jobs, force))

    lines = json.loads(META.read_text(encoding="utf-8"))["lines"] if META.exists() and only else {}
    if only:
        lines = {k: v for k, v in lines.items() if k.split("-")[0] in data["categories"]}
    total = 0
    OUT.mkdir(parents=True, exist_ok=True)
    for job in jobs:
        rng = np.random.default_rng(int(hashlib.sha1(job["key"].encode()).hexdigest()[:8], 16))
        x = radio(load_audio(job["cache"]), job["speaker"], rng)
        size = S.write_mp3(OUT / f"{job['key']}.mp3", x, QUALITY)
        total += size
        lines[job["key"]] = {"ms": int(round(len(x) / S.SR * 1000)), "category": job["category"]}
    # Файлы, которых больше нет в банке, — мусор.
    for path in OUT.glob("*.mp3"):
        if path.stem not in lines:
            path.unlink()
    META.parent.mkdir(parents=True, exist_ok=True)
    # Обновлённый банк получает новый URL: браузер не подставит прежнюю речь к новым субтитрам.
    digest = hashlib.sha256()
    for key in sorted(lines):
        digest.update(key.encode("utf-8"))
        digest.update((OUT / f"{key}.mp3").read_bytes())
    revision = digest.hexdigest()[:16]
    META.write_text(json.dumps({"engine": engine, "revision": revision, "lines": lines}, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    seconds = sum(v["ms"] for v in lines.values()) / 1000
    print(f"\n{len(lines)} реплик, {seconds:.0f} с речи, {total / 1024:.0f} КБ, движок {engine}, манифест: {META.relative_to(ROOT)}")


if __name__ == "__main__":
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    build(set(args) or None, "--force" in sys.argv)
