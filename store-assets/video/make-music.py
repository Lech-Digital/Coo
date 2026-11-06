#!/usr/bin/env python3
"""The films' soundtrack, synthesised (no samples, no licence): 120 BPM. A soft kick, a shaker, a marimba-like pluck
arpeggio over Fmaj7 - Am7 - Dm9 - G, a quiet pad, pops on the card beats and a riser into the end card.
Run: python3 make-music.py            ->  public/music.wav     (the 36 s vertical launch film)
     python3 make-music.py youtube    ->  public/music-yt.wav  (the 68 s 16:9 YouTube film)
     python3 make-music.py handover   ->  public/music-handover.wav  (the 62 s live-action film)"""
import math, random, struct, sys, wave, os

CUT = sys.argv[1] if len(sys.argv) > 1 else 'launch'
SR = 44100
BPM = 120
BEAT = 60 / BPM
# Per cut: length, the bar the end card starts on, the bars without drums (the night's breakdown), the pops (seconds).
CUTS = {
    'launch': dict(length=36.0, end_bar=16, quiet=[], out='music.wav',
                   # The five words; the question, its answer and the ring; the Next up cards.
                   pops=[0, 0.5, 1.0, 1.5, 2.0, 4.5, 5.0, 5.5, 21.0, 22.0, 23.0]),
    'youtube': dict(length=68.0, end_bar=31, quiet=[20, 21, 22], out='music-yt.wav',
                    # The words; the question; the four quick-log tiles; the Next up cards; "offline, no ads, no
                    # trackers".
                    pops=[0, 0.5, 1.0, 1.5, 2.0, 4.5, 5.0, 5.5, 14.5, 15.0, 15.5, 16.0, 31.0, 32.0, 33.0, 58.5, 59.0, 59.5]),
    'handover': dict(length=62.0, end_bar=28, quiet=[0, 1, 2, 3, 4, 5, 6, 7], out='music-handover.wav',
                     # Each Mochi card after a Gemini shot (every 8 s from 5 s); the night ones are drum-less.
                     pops=[5.0, 13.0, 21.0, 29.0, 37.0, 45.0, 53.0]),
}[CUT]
LENGTH = CUTS['length']
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
END = CUTS['end_bar']
for b in range(END + 2):
    t_bar = b * bar
    chord = chords[(b // 2) % 4]
    quiet = b in CUTS['quiet']
    # Pad under each two bars, louder while the drums rest.
    if b % 2 == 0 and b < END:
        add(t_bar, pad([hz(m) for m in chord], 2 * bar), 0.16 if quiet else 0.10)
    for beat in range(4):
        t = t_bar + beat * BEAT
        if 1 <= b < END and not quiet:
            add(t, kick(), 0.55 if b >= 2 else 0.35)
        if 2 <= b < END and not quiet:
            add(t + BEAT / 2, shaker(), 0.25, pan=0.3)
            add(t + BEAT * 0.75, shaker(), 0.12, pan=-0.3)
    for e in range(8):
        t = t_bar + e * BEAT / 2
        note = chord[arp[e] % 4] + 12
        if b < END and not (quiet and e % 2):
            add(t, pluck(hz(note)), 0.11 if b < 1 or quiet else 0.16, pan=0.25 if e % 2 else -0.25)

for t in CUTS['pops']:
    add(t, pop(), 0.22)
# Riser into the end card, then the last chord rings out under it.
t_end = END * bar
add(t_end - 2.0, riser(2.0), 0.5)
add(t_end, kick(0.6), 0.7)
add(t_end, pad([hz(m) for m in [53, 60, 64, 69, 72]], LENGTH - t_end), 0.18)
for m in [65, 69, 72, 76]:
    add(t_end, pluck(hz(m), LENGTH - t_end - 1), 0.14)
add(t_end + 0.8, pop(), 0.18)  # the bunny lands on the "i"

peak = max(max(abs(x) for x in L), max(abs(x) for x in R))
fade = int(0.4 * SR)
out = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'public', CUTS['out'])
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
