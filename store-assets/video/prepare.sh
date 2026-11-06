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
