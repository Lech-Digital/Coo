# Mochi launch film — brief

The prompt for the launch video, whoever makes it (this repo's Remotion build in `store-assets/video/`, or Higgsfield
Katana later). Copy follows `store-assets/PLAY-LISTING.md`: plain, feature-first, the words parents search for (baby
tracker, breastfeeding timer, bottle, sleep, diapers, pumping, growth). No "calm", no "at 3 a.m.", no "free", no founder
story.

## One line

A 36-second, beat-cut vertical film that shows Mochi doing the work: one tap logs a feed, the day draws itself, and the
app learns this baby's rhythm, then names it.

## The prompt

> Product launch film for **Mochi**, an Android baby tracker. Vertical 9:16, 1080×1920, 30 fps, 36 seconds, 120 BPM,
> every cut on the beat. Style: Apple-keynote clean meets playful kinetic type. A soft matcha / lavender / peach colour
> wash moves slowly behind everything; frosted-glass cards; the Figtree typeface, big and tight (semibold, -2% tracking).
> One mascot: Mochi, a round white bunny-shaped mochi on a matcha disc, ears up when the baby is awake, ears down when
> asleep. Real app screens in a slim phone frame, never fake UI. Motion: springy (overshoot, then settle), short and
> decisive; things arrive from the direction they will leave to; nothing fades in slowly. Colour carries meaning: feed
> is peach, diaper is mustard, sleep is lavender, pump is sky, the brand is matcha green.
>
> 1. **Words (0–4 s)**: five words slam in on the beat, one per kind, each in its colour with its icon: *Feeds. Sleep.
>    Diapers. Pumping. Growth.* They stack, then collapse into Mochi on the matcha icon.
> 2. **Question (4–7 s)**: the Today screen rises. Big type, on the beat: *Left or right?*, then *Mochi remembers.* in
>    the accent, and a ring lands on Next up's "Start right".
> 3. **Breastfeeding timer (7–11 s)**: a tap on "Start right"; the timer on the right side starts counting. Caption:
>    *One tap. Left or right.*
> 4. **Baby log (11–15 s)**: a full day in the Log. Caption: *Feeds, sleep, diapers, pumping. One timeline.*
> 5. **Sleep pattern (15–20 s)**: the hero. Columns of days grow side by side, noon to noon, until the nights line up;
>    a dashed line snaps to when the longest sleep usually starts. Caption: *Her sleep pattern, from your own log.*
> 6. **Next up (20–24 s)**: three cards pop in on the beat: *Next side: right · Next feed likely 12:37–1:07 PM · Next
>    nap likely 1:10–1:35 PM* (marked Premium). Caption: *Worked out on your phone.*
> 7. **Night mode (24–28 s)**: a wipe to the dark night screen, big buttons, the next side ringed. Caption: *Night
>    colours. Big buttons.*
> 8. **Together (28–32 s)**: two phones, two parents' names, the same log. Caption: *One log for the whole family.*
> 9. **End card (32–36 s)**: the icon pops, the wordmark's letters arrive and the bunny drops onto the "i"; *Baby
>    Tracker & Feeding*, the five kinds, *On Google Play* (Google's own badge artwork if it is ever added, never a
>    home-made one). Hold.
>
> Sound: a warm, light electronic loop at 120 BPM (soft kick, shaker, marimba-like plucks on the beat changes), a
> small "pop" on the hook's lines and each Next up card, a riser into the end card and a pop as the bunny lands. No
> voice-over; every line is on screen.

## Do and don't

- Do: real screens from the demo build (`store-assets/raw/`), real numbers from the demo baby, the app's own colours.
- Don't: stock babies, sleeping-baby clichés, the moon-and-stars look of other trackers, AI-generated faces, lens flares.
- Don't claim what the app does not do: no "AI" in the hook (the learning is plain statistics on the phone), no
  medical promises.

## History

- 2026-11-06: re-cut after a marketing review: the hook is a parent's question answered by a real screen inside
  2 s (the old one listed five nouns and showed no app until 8 s); the name moved to a 4 s end card; "Smart insights"
  became the app's own "Next up" (no "AI" wording for the on-phone statistics); only what the demo baby's screens show.
  Alternative hooks to test: "When's the next feed?" and "Log a feed in one tap." The owner kept the opening's
  popping words, so they come first and the film runs 36 s.

## Cuts

- `mochi-launch-9x16.mp4`: 1080×1920, the film above (Shorts, Reels, TikTok, the Play promo link).
- `mochi-youtube-16x9.mp4` (`npm run render:youtube`, `src/YouTube.tsx`): 1920×1080, 68 s, for YouTube and the Play
  listing's promo video. The same beats with the words on the left and the screens on the right, and room for more:
  the real running timer (`s8-timer.png`), quick log, the Log zoomed, Stats & trends (`s9-trends.png`), the family,
  a wall of the health / growth / firsts features, "Works offline. No ads. No trackers." with the seven languages,
  and a 6 s end card with lechdigital.nl/mochi. The drums rest under the night scene. Its music is
  `make-music.py youtube`. Thumbnail: `npx remotion still src/index.ts YouTube out/mochi-youtube-thumb.png
  --frame=200 --scale=0.6667`.

Uploaded videos, with their titles, descriptions and thumbnails: `store-assets/youtube/` (one dated folder each).
