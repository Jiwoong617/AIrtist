"""Original AIrtist score and synthesized instruments. Dev-only dependency: numpy.

No downloaded recordings, samples, soundfonts or music models. Regenerates the
shipping mono PCM bank and keeps 44.1 kHz stereo masters/previews outside Git.
"""
import argparse
import hashlib
import json
import math
import wave
from pathlib import Path

import numpy as np

RATE = 44100
SHIP_RATE = 22050
RNG = np.random.default_rng(20261018)


def tone(midi, seconds, kind="piano", velocity=1.0):
    t = np.arange(round(seconds * RATE), dtype=np.float64) / RATE
    f = 440 * 2 ** ((midi - 69) / 12)
    if kind == "marimba":
        partials = [(1, 1, .32), (4.01, .13, .055), (9.02, .018, .025)]
    elif kind == "pluck":
        partials = [(1, 1, .45), (2, .25, .25), (3, .10, .15), (4, .035, .1)]
    else:
        partials = [(1, 1, 1.05), (2, .27, .62), (3, .11, .4), (4, .035, .25), (5, .012, .16)]
    y = sum(a * np.sin(2 * np.pi * f * harmonic * t) * np.exp(-t / decay)
            for harmonic, a, decay in partials)
    y *= (1 - np.exp(-t / .006)) * velocity
    fade = min(len(y), round(.035 * RATE))
    y[-fade:] *= np.linspace(1, 0, fade) ** 2
    return y


def paper(seconds, strength=1.0):
    size = round(seconds * RATE)
    noise = RNG.standard_normal(size)
    # Soft, band-limited friction: no sharp white-noise edge.
    smooth = np.convolve(noise, np.ones(11) / 11, "same")
    low = np.convolve(noise, np.ones(121) / 121, "same")
    envelope = np.sin(np.linspace(0, np.pi, size)) ** 2
    return (smooth - low) * envelope * strength


def wood(seconds, pitch=660):
    t = np.arange(round(seconds * RATE)) / RATE
    y = (np.sin(2 * np.pi * pitch * t) + .22 * np.sin(2 * np.pi * pitch * 1.73 * t))
    y *= np.exp(-t / .014) * (1 - np.exp(-t / .0015))
    y[-min(150, len(y)):] *= np.linspace(1, 0, min(150, len(y)))
    return y


def place(track, sound, seconds, gain=1.0, pan=0.0, loop=False):
    sound = sound * gain
    stereo = sound[:, None] * np.array([math.sqrt((1 - pan) / 2), math.sqrt((1 + pan) / 2)])
    start = round(seconds * RATE)
    if loop:
        start %= len(track)
    count = min(len(stereo), len(track) - start)
    if count > 0:
        track[start:start + count] += stereo[:count]
    if loop and count < len(stereo):
        track[:len(stereo) - count] += stereo[count:]


def room(track, loop):
    dry = track.copy()
    for milliseconds, gain in [(47, .09), (83, .06), (137, .04), (211, .025)]:
        shift = round(milliseconds * RATE / 1000)
        if loop:
            track += np.roll(dry[:, ::-1], shift, axis=0) * gain
        elif shift < len(track):
            track[shift:] += dry[:-shift, ::-1] * gain
    return track


def finish(track, rms, peak, loop=False):
    track = room(track, loop)
    track -= np.mean(track, axis=0)
    track *= min(rms / max(1e-8, np.sqrt(np.mean(track ** 2))), peak / max(1e-8, np.max(np.abs(track))))
    if not loop:
        n = min(300, len(track) // 4)
        track[:n] *= np.linspace(0, 1, n)[:, None]
        track[-n:] *= np.linspace(1, 0, n)[:, None]
    return track


CHORDS = [
    (53, [57, 60, 64, 67]), (50, [57, 60, 64, 65]),
    (46, [53, 57, 60, 62]), (48, [55, 60, 62, 64]),
    (45, [52, 55, 60, 64]), (50, [53, 57, 60, 64]),
    (55, [58, 62, 65, 69]), (48, [55, 57, 60, 62]),
]
# A short F-A-C/G motif, answered with chord tones; both tracks share its rhythm.
MELODY = [
    [65, 69, 72, 67], [69, 65, 64, 60], [65, 69, 70, 65], [67, 64, 62, 60],
    [64, 67, 69, 72], [69, 65, 64, 62], [67, 70, 69, 65], [67, 64, 62, 60],
]


def music(game=False):
    bpm, bars = (96, 48) if game else (88, 32)
    beat = 60 / bpm
    track = np.zeros((round(bars * 4 * beat * RATE), 2))
    for bar in range(bars):
        root, chord = CHORDS[(bar // 2) % 8]
        start = bar * 4 * beat
        section = bar // 16
        place(track, tone(root + 12, 2.0), start, .075, -.12, True)
        for j, pitch in enumerate(chord[:3]):
            place(track, tone(pitch, 2.3), start + j * .024, .052, (j - 1) * .2, True)
        offsets = [0.5, 1.5, 2.5, 3.5] if game else [1.0, 2.5, 3.5]
        for j, off in enumerate(offsets):
            pitch = chord[(j + bar) % 4] + (12 if j == 1 else 0)
            place(track, tone(pitch, 1.1, "pluck"), start + off * beat, .035 if game else .025, .25, True)
        # Leave alternate bars open, and thin the middle section of the game loop.
        if bar % 2 == 0 and (not game or (bar % 4 == 0 and section != 1)):
            melody = MELODY[(bar // 2) % 8]
            for j, pitch in enumerate(melody):
                if section % 2 and j == 3:
                    continue
                place(track, tone(pitch, 1.6, "piano"), start + [0, .75, 2, 3][j] * beat,
                      (.065 if game else .09) * [1, .8, .9, .68][j], -.18, True)
        if bar % 2:
            place(track, tone(chord[1] + 12, .9, "marimba"), start + 2.5 * beat, .028, .18, True)
        for off in ([1, 3] if game else [3]):
            place(track, wood(.08, 430), start + off * beat, .012, 0, True)
        if game or bar % 2 == 0:
            place(track, paper(.13), start + 1.5 * beat, .012, -.25, True)
    return finish(track, .040 if game else .045, .22, True), bpm, bars


def effects():
    definitions = {"ui_click": .08, "round_start": .8, "generation_complete": .7,
                   "gallery_open": .7, "deadline_tick": .1, "round_result": 1.5, "error_notice": .35}
    result = {}
    for name, duration in definitions.items():
        track = np.zeros((round(duration * RATE), 2))
        if name in ("ui_click", "deadline_tick"):
            place(track, wood(duration, 620 if name == "ui_click" else 480), 0, .7)
        elif name == "round_start":
            place(track, paper(.24), 0, .12)
            for pos, note in [(.1, 65), (.27, 69)]:
                place(track, tone(note, duration - pos, "marimba"), pos, .3)
        elif name == "generation_complete":
            place(track, paper(.19), 0, .10)
            for pos, note in [(.13, 69), (.3, 72)]:
                place(track, tone(note, duration - pos, "marimba"), pos, .3)
        elif name == "gallery_open":
            for pos in [0, .08, .16]:
                place(track, paper(.16), pos, .11)
            for pos, note in [(.24, 72), (.30, 77)]:
                place(track, tone(note, duration - pos, "pluck"), pos, .21)
        elif name == "round_result":
            for pos, note in [(0, 65), (.24, 69), (.5, 72)]:
                place(track, tone(note, duration - pos, "piano"), pos, .3)
        else:
            for pos, note in [(0, 64), (.13, 60)]:
                place(track, tone(note, duration - pos, "marimba"), pos, .25)
        result[name] = finish(track, .075 if name != "deadline_tick" else .045, .30)
    return result


def wav(path, signal, rate):
    path.parent.mkdir(parents=True, exist_ok=True)
    pcm = np.round(np.clip(signal, -1, 1) * 32767).astype("<i2")
    with wave.open(str(path), "wb") as out:
        out.setnchannels(1 if signal.ndim == 1 else signal.shape[1])
        out.setsampwidth(2)
        out.setframerate(rate)
        out.writeframes(pcm.tobytes())


def downsample(track, loop):
    mono = np.mean(track, axis=1)
    taps = np.arange(-40, 41)
    kernel = .44 * np.sinc(.44 * taps) * np.hamming(len(taps))
    kernel /= kernel.sum()
    padded = np.pad(mono, (40, 40), mode="wrap" if loop else "constant")
    return np.convolve(padded, kernel, "valid")[::2]


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path, default=Path("assets/audio"))
    parser.add_argument("--masters", type=Path, default=Path("outputs/audio-production-20261018"))
    args = parser.parse_args()
    entries = []
    for name, game in [("bgm_menu", False), ("bgm_game", True)]:
        track, bpm, bars = music(game)
        wav(args.masters / "masters" / f"{name}.wav", track, RATE)
        shipping = downsample(track, True)
        wav(args.output / f"{name}.wav", shipping, SHIP_RATE)
        if not game:
            preview = track[:20 * RATE].copy()
            preview[-RATE:] *= np.linspace(1, 0, RATE)[:, None]
            wav(args.masters / "preview-main-20s.wav", preview, RATE)
        entries.append(describe(args.output, name, shipping, bpm=bpm, bars=bars, loop=True))
    for name, track in effects().items():
        wav(args.masters / "masters" / f"{name}.wav", track, RATE)
        shipping = downsample(track, False)
        wav(args.output / f"{name}.wav", shipping, SHIP_RATE)
        entries.append(describe(args.output, name, shipping, loop=False))
    total = sum(e["bytes"] for e in entries)
    assert total < 10_000_000, total
    manifest = dict(version=1, sampleRate=SHIP_RATE, channels=1, bitsPerSample=16,
                    provenance="Original deterministic composition and synthesized sounds; no third-party samples.",
                    generator="scripts/compose-audio.py", totalAudioBytes=total, files=entries)
    (args.output / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"status": "passed", "audioFiles": len(entries), "bytes": total,
                      "tracks": [{k: e[k] for k in ("id", "seconds", "peak", "rms")} for e in entries]}, indent=2))


def describe(root, name, audio, **metadata):
    path = root / f"{name}.wav"
    data = path.read_bytes()
    peak = float(np.max(np.abs(audio)))
    edge = float(abs(audio[-1] - audio[0]))
    if metadata["loop"]:
        assert edge < .015, (name, edge)
        # Three cycles have exactly the same boundary; no inserted silence.
        cyclic = np.tile(audio, 3)
        assert np.max(np.abs(np.diff(cyclic))) < .20
    assert peak < .31
    return dict(id=name, path=path.name, bytes=len(data), sha256=hashlib.sha256(data).hexdigest(),
                frames=len(audio), seconds=len(audio) / SHIP_RATE, loopStart=0,
                loopEnd=len(audio), peak=peak, rms=float(np.sqrt(np.mean(audio ** 2))),
                seamDelta=edge, **metadata)


if __name__ == "__main__":
    main()
