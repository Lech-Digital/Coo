# 0012. Matcha identity: lighter brand green, soft-flat icon, Fredoka wordmark

Date: 2026-11-06

Status: Accepted

## Context

After the rename to Mochi (ADR 0011) the brand green was a deep moss, `#3F6B4A`. The owner found it too "mossy" and
asked for a lighter "matcha baby" green, a reworked icon and a new wordmark. Options were compared on a page of
renders and reviewed twice by a design reviewer. The constraint is contrast: white text is unreadable on a light matcha
(`#93B86A` on white is about 2.3:1), and the app's buttons and links use the accent.

## Decision

- **Two greens with separate jobs.** `Brand` (new token), light matcha `#93B86A` by day and `#A3C47F` at night, is the
  brand itself: the icon's glow and the wordmark's bunny. It is never text. `Accent` becomes a fresher leaf green,
  `#4A7036` (white text 5.5:1), by day; at night it is the light matcha `#A3C47F` with dark text. `AccentSoft` is a
  matcha tint (`#DFEBC8`), so pills, the selected tab and the avatar carry the matcha.
- **A soft-flat icon.** The bunny keeps its pose (one ear up, one flopped, happy closed eyes), now cream with a faint
  warm outline, one highlight and a crisp shadow, on a matcha glow that deepens to `#5E8A3E` at the rim so it holds on
  light wallpapers. No gradients on the bunny and no blur: it matches the flat app (the reviewer's main point; a 3D
  version was tried and dropped).
- **A Fredoka wordmark.** "mochi" in Fredoka SemiBold, the "i" in the accent with the logo bunny as its dot: flat with
  two sleepy eyes and pink inner ears in the app (so it is not read as the Android robot), with the logo's face at
  large sizes. It is drawn from outlines, so Fredoka is not bundled and never spreads to headings.
- **One source.** `eng/gen-brand.py` draws the launcher icon, splash, in-app Mochi, the wordmark geometry for the app
  and the film, and the store icon from the same shapes.

## Consequences

### Positive

- The day and night greens are one family, and the brand reads as softer and more distinctive among baby trackers.
- Buttons stay crisp and readable; every `PaletteTests` contrast rule still passes.
- The wordmark and the icon are the same character, and the dot sits exactly on the stem at any size.

### Negative

- The leaf-green buttons are close to the old moss, so the matcha has to show in the brand, pills and selected states
  for the change to be noticed.
- The second identity change in days (after Coo → Mochi): it ships once, in 1.12.0, with new store screenshots.
- `eng/gen-brand.py` needs fontTools to regenerate the wordmark.
