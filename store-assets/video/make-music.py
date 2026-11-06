#!/usr/bin/env python3
"""The launch film's soundtrack, synthesised (no samples, no licence): 120 BPM, 32 s. A soft kick, a shaker, a
marimba-like pluck arpeggio over Fmaj7 - Am7 - Dm9 - G, a quiet pad, pops on the card beats and a riser into the end card.
Run: python3 make-music.py  ->  public/music.wav"""
import math, random, struct, wave, os

SR = 44100
BPM = 120
BEAT = 60 / BPM
LENGTH = 32.0
N = int(SR * LENGTH)
L = [0.0] * N
R = [0.0] * N
random.seed(7)


def add(t0, samples, gain=1.0, pan=0.0):
    i0 = int(t0 * SR)
    gl, gr = gain * (1 - max(0, pan)), gain * (1 + min(0, pan))
    for k, v in enumerate(samples):
        i = i0 + k
        if 0 <= i < N:
            L[i] += v * gl
            R[i] += v * gr


def kick(dur=0.32):
    out, phase = [], 0.0
    for k in range(int(dur * SR)):
        t = k / SR
        f = 48 + 90 * math.exp(-t * 28)
        phase += 2 * math.pi * f / SR
        out.append(math.sin(phase) * math.exp(-t * 9))
    return out


def shaker(dur=0.07):
    out, prev = [], 0.0
    for k in range(int(dur * SR)):
        t = k / SR
        n = random.uniform(-1, 1)
        hp = n - prev
        prev = n
        out.append(hp * math.exp(-t * 60) * 0.5)
    return out


def pluck(freq, dur=0.6):
    out = []
    for k in range(int(dur * SR)):
        t = k / SR
        env = math.exp(-t * 7) * min(1, t * 400)
        v = math.sin(2 * math.pi * freq * t) + 0.35 * math.sin(2 * math.pi * freq * 4 * t) * math.exp(-t * 25) + 0.15 * math.sin(2 * math.pi * freq * 2 * t)
        out.append(v * env)
    return out


def pad(freqs, dur):
    out = []
    for k in range(int(dur * SR)):
        t = k / SR
        env = min(1, t / 0.6) * min(1, (dur - t) / 0.6)
        v = sum(math.sin(2 * math.pi * f * t + i) * (1 + 0.003 * math.sin(2 * math.pi * 0.3 * t)) for i, f in enumerate(freqs))
        out.append(v / len(freqs) * env)
    return out


def pop(dur=0.12):
    out = []
    for k in range(int(dur * SR)):
        t = k / SR
        f = 700 + 900 * (1 - math.exp(-t * 40))
        out.append(math.sin(2 * math.pi * f * t) * math.exp(-t * 35))
    return out


def riser(dur):
    out, prev = [], 0.0
    for k in range(int(dur * SR)):
        t = k / SR
        n = random.uniform(-1, 1)
        prev = prev * 0.8 + n * 0.2
        out.append((n - prev) * (t / dur) ** 2 * 0.6)
    return out


def hz(midi):
    return 440 * 2 ** ((midi - 69) / 12)


# Fmaj7, Am7, Dm9, G6 - two bars each, the cycle twice.
chords = [[53, 57, 60, 64], [57, 60, 64, 67], [50, 57, 60, 64], [55, 59, 62, 64]]
arp = [0, 2, 1, 3, 2, 1, 3, 2]

bar = 4 * BEAT
for b in range(16):
    t_bar = b * bar
    chord = chords[(b // 2) % 4]
    # Pad under each two bars.
    if b % 2 == 0 and b < 15:
        add(t_bar, pad([hz(m) for m in chord], 2 * bar), 0.10)
    for beat in range(4):
        t = t_bar + beat * BEAT
        if b >= 1 and b < 15:
            add(t, kick(), 0.55 if b >= 2 else 0.35)
        if b >= 2 and b < 15:
            add(t + BEAT / 2, shaker(), 0.25, pan=0.3)
            add(t + BEAT * 0.75, shaker(), 0.12, pan=-0.3)
    for e in range(8):
        t = t_bar + e * BEAT / 2
        note = chord[arp[e] % 4] + 12
        if b < 15:
            add(t, pluck(hz(note)), 0.16 if b >= 1 else 0.11, pan=0.25 if e % 2 else -0.25)

# Pops on the hook words and the learning cards (seconds).
for t in [0, 0.5, 1.0, 1.5, 2.0] + [20.0, 21.0, 22.0, 23.0]:
    add(t, pop(), 0.22)
# Riser into the end card, then the last chord rings out.
add(28.0, riser(2.0), 0.5)
add(30.0, kick(0.6), 0.7)
add(30.0, pad([hz(m) for m in [53, 60, 64, 69, 72]], 2.0), 0.18)
for m in [65, 69, 72, 76]:
    add(30.0, pluck(hz(m), 1.8), 0.14)

peak = max(max(abs(x) for x in L), max(abs(x) for x in R))
fade = int(0.4 * SR)
out = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'public', 'music.wav')
with wave.open(out, 'wb') as w:
    w.setnchannels(2)
    w.setsampwidth(2)
    w.setframerate(SR)
    frames = bytearray()
    for i in range(N):
        g = 0.89 / peak * (min(1, (N - i) / fade))
        frames += struct.pack('<hh', int(L[i] * g * 32767), int(R[i] * g * 32767))
    w.writeframes(bytes(frames))
print('wrote', out)
