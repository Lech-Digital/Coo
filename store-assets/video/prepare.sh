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
cp ../youtube/*-mochi-handover/soundtrack.mp3 public/music-handover.mp3 2>/dev/null || true
