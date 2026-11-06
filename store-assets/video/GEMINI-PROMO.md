# "The handover" — a live-action promo made with Gemini (Veo)

A ~50 s film about one day and one night with a newborn, told through two parents who hardly see each other but
always know what happened, because it's in Mochi. Gemini makes the people shots (8 s each); the phone screens are
the real app, cut in by the Remotion project (`store-assets/video`), because Veo can't draw readable UI.

## Why it's built this way

- **Veo garbles screens.** Any readable app text it draws will be fake and wrong. So in the Gemini shots the phone
  screen is a soft glow, seen from the side or out of focus; the real screen follows as a full-frame insert.
- **Same people in every shot.** Paste the CHARACTERS and STYLE blocks into every prompt, word for word. If Gemini
  offers reference images ("ingredients"), give it the best frame of each parent from shot 1 and shot 3.
- **The baby is never the subject.** Swaddled, back of the head, a small hand: AI baby faces look wrong.
- **No music from Veo.** Ask for room sound only; the film gets the Mochi soundtrack in the edit.
- Generate 2–4 takes of each shot and keep the best. 16:9 for YouTube; the same prompts with "vertical 9:16" for a Short.

## Paste into every prompt

> **STYLE:** Cinematic, warm and natural, like a premium phone commercial. Handheld but steady, shallow depth of
> field, 35 mm look, soft practical light. Muted, warm palette with soft sage greens and cream; nothing neon. Real,
> lived-in apartment: linen, wood, plants. No text, no captions, no logos on screen. Room sound only (fabric, a
> kettle, a soft breath, the city far away), no music, no dialogue.
>
> **CHARACTERS:** Tom, early thirties, short dark-blond hair, a few days of stubble, grey t-shirt, tired but gentle.
> Anna, early thirties, shoulder-length brown hair loosely tied up, cream knit cardigan over a sage-green top. Their
> newborn daughter, always swaddled in a sage-green muslin wrap, face mostly hidden against a shoulder or turned away.
> The phone is a dark modern Android phone; its screen is only ever a soft, unreadable glow.

## The shots

**1 · Night, the nursery (8 s)** — Night. A dim nursery lit by one small warm lamp. Tom walks slowly across the room
with the baby on his shoulder, swaying, patting her back, eyes half closed. Slow push-in from the doorway. Blue
moonlight through the curtain edge. He yawns. Calm, quiet, tender.

**2 · One hand, in the dark (8 s)** — Night, same nursery. Tom sits down in an armchair with the baby asleep on his
chest, one arm around her. With his free hand he picks up the phone from the armrest and taps it twice with his thumb;
the screen lights his face softly with a dim, warm glow. Close-up on his hand and face from the side, the screen
angled away from camera. He smiles a little and puts the phone face down.

> INSERT (Remotion): the real night screen, `s7-night.png`: a tap on **Bottle**, then the entry slides into the
> Log. Caption: *Logged with one hand.*

**3 · Morning, the handover (8 s)** — Early morning, soft golden light in a bedroom. Anna wakes, reaches for the phone
on the nightstand and looks at it, still lying on her pillow. Her face relaxes into a small smile of relief. In the
background, through the open door, Tom is asleep in the armchair with the baby on his chest. Slow rack focus from
Anna to Tom and the baby.

> INSERT: `s1-today.png` — the Today screen: asleep for 33 min, the last feed, **Next side: right**. Caption:
> *The whole night, at a glance.*

**4 · She takes over (8 s)** — Morning, living room, sunlight. Anna gently lifts the swaddled baby from Tom's chest
without waking him and kisses his forehead. She settles on the sofa to breastfeed, discreetly, under a muslin cloth,
filmed from behind her shoulder. She taps the phone on the sofa arm once and leaves it.

> INSERT: `s8-timer.png` — the right side's timer running. Caption: *Right side. One tap.*

**5 · The day goes by (8 s)** — A montage-like single shot, time passing in a sunny apartment: a bottle being
warmed, a diaper changed on a changing table (hands only), the baby asleep in a bassinet by the window, Anna stretching
on a yoga mat, the light moving across the floor. Smooth, slow camera drift through the rooms.

> INSERT: `s2-log.png` — the day's band filling in: feeds, sleeps, diapers. Caption: *Every feed, sleep and diaper.*

**6 · Grandma checks in (8 s, optional)** — Afternoon. A grandmother in her sixties, silver bob, reading glasses on
her nose, in her own kitchen across town, looks at her phone and smiles warmly, then shows it to her husband
beside her, who chuckles. Cosy, warm interior.

> INSERT: the Log from a follower's phone. Caption: *Grandparents can follow along.*

**7 · Evening, together (8 s)** — Evening, warm lamp light. Tom and Anna on the sofa, the baby asleep in the bassinet
beside them. Anna holds the phone between them and they both look at it; Tom points at something and they share a
quiet laugh. Camera slowly pulls back. Feels like a small win.

> INSERT: the sleep-pattern animation (from the YouTube film) and **Next nap / Next feed**. Caption: *It learns
> her rhythm.*

**8 · End card** — The Mochi end card from the YouTube film: the bunny, the wordmark, *Baby Tracker & Feeding*,
*On Google Play*.

## Screens to have ready

For the edit, not for Gemini: `s7-night.png`, `s1-today.png`, `s8-timer.png`, `s2-log.png` and the sleep-pattern
scene, all in `store-assets/raw/` and `src/YouTube.tsx`.

If Gemini takes reference images, give it **`s7-night.png`** (night shots) and **`s1-today.png`** (the morning) only
as *colour reference* for the screen's glow: write "use this only for the colour of the phone screen's light, do not
show it readable". Never ask it to reproduce the screen.

## When the clips are back

Drop them in `store-assets/video/public/handover/` as `1.mp4` … `7.mp4`, and the Remotion project cuts them with the
inserts, captions and the music on the beat.
