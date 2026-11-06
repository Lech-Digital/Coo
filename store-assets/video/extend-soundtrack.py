#!/usr/bin/env python3
"""The handover's Gemini soundtrack, two bars longer: repeats its day groove (40.72-44.59 s, downbeat to downbeat at
~124 BPM) with a 20 ms crossfade, so the film has room for the yoga shot and the whisper lands after "On Google Play".
Run: python3 extend-soundtrack.py in.wav out.wav (prepare.sh decodes the mp3 first)."""
import array, sys, wave

A, B = 40.72, 44.592
w = wave.open(sys.argv[1])
sr, ch = w.getframerate(), w.getnchannels()
x = array.array('h', w.readframes(w.getnframes()))
a, b, xf = int(A * sr) * ch, int(B * sr) * ch, int(0.02 * sr) * ch
head, tail = x[:b], x[a:]
out = array.array('h', head[:-xf])
for i in range(xf):
    t = i / xf
    out.append(int(head[len(head) - xf + i] * (1 - t) + tail[i] * t))
out.extend(tail[xf:])
o = wave.open(sys.argv[2], 'wb')
o.setnchannels(ch)
o.setsampwidth(2)
o.setframerate(sr)
o.writeframes(out.tobytes())
o.close()
