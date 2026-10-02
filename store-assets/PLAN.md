# Play Store assets — the plan (agreed 2026-10-02, to do near the release)

The owner asked for every Play Store asset, PurePrep-style (`../PurePrep/store-assets/`). Screenshots wait until the
features are built (sign-in, sync, sharing, Premium), so they show the finished app. The icon is done: D2, see
`icon-options/`.

## 1. A demo build, beside the real app

- A build switch (e.g. `-p:CooDemo=true`) makes a separate app: application ID `com.lechdigital.coo.demo`, label
  "Coo Demo". It never touches the owner's real log, and it never exists in a store build.
- On first launch it fills itself with a realistic, made-up day for a baby called **Lena** (about seven weeks old):
  breast feeds every 2–3 hours and a few bottles, wet and dirty diapers, naps and last night's long sleep, a couple of
  pumps, and yesterday too, so the Log has history. **Nothing personal** (owner's choice: demo data, not his own).
- Uninstalled from the phone afterwards.

## 2. Screenshots from the phone (adb), about 6–8

Today mid-afternoon; a feed timer running; the Log with its rhythm band; the zoomed day; the night layout at 3 a.m.;
logging something earlier; sharing with family (once built); Settings or the seven languages.

## 3. Store images: HTML templates rendered with headless Chrome

- Captioned phone screenshots, 1080 × 1920, on the Washi background, short headlines ("One tap at 3 a.m.").
- Feature graphic, 1024 × 500.
- The 512 px icon, from `icon-options/d2-saying-coo.svg`.
- Optional tablet shots, as PurePrep has.

## 4. `store-assets/PLAY-LISTING.md`

- App name ≤ 30: e.g. **"Coo: Baby Tracker & Sleep Log"** (29), with alternates. No "free" in the title (Play policy);
  it goes in the descriptions.
- Short description ≤ 80, full description ≤ 4,000: ASO-friendly, in the owner's voice — the paper log, the 3 a.m.
  briefings, no app had it all in one place for free; free, with Premium extras (sleep prediction, AI logging, night
  and weekly summaries), a 7-day trial, then a month, a year, lifetime, or free forever.
- A script that counts characters as Play does (code points), like PurePrep's `check-listing.mjs`.
- English first, then de, fr, es, it, pl, nl — the app's seven languages.
