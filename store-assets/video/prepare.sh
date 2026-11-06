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
# The handover's Gemini soundtrack, decoded and two bars longer (extend-soundtrack.py).
if ls ../youtube/*-mochi-handover/soundtrack.mp3 >/dev/null 2>&1; then
  npx remotion ffmpeg -loglevel error -y -i ../youtube/*-mochi-handover/soundtrack.mp3 -ar 44100 -ac 2 public/soundtrack-handover-src.wav
  python3 -I extend-soundtrack.py public/soundtrack-handover-src.wav public/soundtrack-handover.wav
fi
