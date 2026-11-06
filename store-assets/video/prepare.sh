#!/bin/zsh
# Copies the app's assets into public/ (ignored by git) and makes the soundtrack. Run once before `npm run render`.
set -e
cd "$(dirname "$0")"
R=../..
mkdir -p public
cp $R/store-assets/raw/*.png public/
cp $R/src/Mobile/Coo.Mobile/Resources/Fonts/Figtree-*.ttf public/
cp $R/src/Mobile/Coo.Mobile/Resources/Images/mochi_*.svg public/
python3 -I make-music.py
python3 -I make-music.py youtube
python3 -I make-music.py handover
mkdir -p public/handover && cp ../youtube/*-mochi-handover/clips/*.mp4 public/handover/ 2>/dev/null || true
# The handover's Gemini music and whisper, decoded; the whisper set to a peak of 0.6.
H=$(ls -d ../youtube/*-mochi-handover | tail -1)
if [ -f $H/soundtrack.mp3 ]; then
  npx remotion ffmpeg -loglevel error -y -i $H/soundtrack.mp3 -ar 44100 -ac 2 public/soundtrack-handover.wav
fi
if [ -f $H/whisper.mp3 ]; then
  npx remotion ffmpeg -loglevel error -y -i $H/whisper.mp3 -ar 44100 -ac 2 public/whisper-handover-src.wav
  python3 -I - public/whisper-handover-src.wav public/whisper-handover.wav <<'PY'
import array, sys, wave
w = wave.open(sys.argv[1]); x = array.array('h', w.readframes(w.getnframes()))
g = 0.6 * 32767 / max(1, max(abs(v) for v in x))
o = wave.open(sys.argv[2], 'wb'); o.setnchannels(w.getnchannels()); o.setsampwidth(2); o.setframerate(w.getframerate())
o.writeframes(array.array('h', (int(v * g) for v in x)).tobytes()); o.close()
PY
fi
