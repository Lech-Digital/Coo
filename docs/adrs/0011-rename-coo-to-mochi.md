# 11. Rename the product from Coo to Mochi

Date: 2026-11-05

Status: Accepted

## Context

Coo went live on Google Play on 2026-10-23 as "Coo: Baby Tracker & Sleep Log". Another baby tracker on Play,
`com.yildiz.mama_meow`, has existed since September 2025. It was first called "MamaMeow", then "Coo Care: AI Baby
Feed Log". It is now called "Coo Care: Baby Tracker & Sleep Log": our title word for word, with "Care" added. It has
the same core features (feeding timer, sleep log, diapers, pumping, PDF reports, an AI assistant). Every Play search
puts the two side by side, and "Coo", three letters and an everyday word, would be hard to defend. The owner believes
the other app was first and chose not to contest it.

Candidates were checked on Google Play, the App Store and TMview (EU, WIPO and national registers):

- Ruled out because a baby app already uses the name: Agu, CribNotes, Relay, Hatch, Nest, Bloom, Sprout, Pebble,
  Snuggle, Snug, Hush, Lumi (plus Pampers' 2019 "Lumi" baby-tracking system) and others.
- Too common or not baby-like enough for the owner: Cuddle, Cozy, Peep, Tally.
- Yuzu: free among baby apps, but sharper in sound and less known.

**Mochi** was chosen: soft and round in all seven languages ("mo-chee", "mo-shee", "mo-hee", "mo-kee"), and "mochi
cheeks" is how parents already describe chubby baby cheeks. About 30 Play apps carry the word (games, a parental-control
app, a US telehealth service), but none is a baby tracker. Mochi GmbH (Berlin) holds a German word mark "mochi" for
application software and nutrition information (2025); the owner accepted that risk.

## Decision

1. **Everything a person sees says Mochi.**
   - The app name (`ApplicationTitle`) and every resource string in seven languages, including "Tell Mochi" and
     "Mochi Premium".
   - The widgets, notifications, the invitation and delete-account pages, and the Advisor's and sleep plan's prompts.
   - The Play listing (`store-assets/PLAY-LISTING.md`). The new titles do not repeat the other app's "Sleep Log"
     wording. English is "Mochi: Baby Tracker & Feeding".
   - The site pages, which move to `lechdigital.nl/projects/mochi/`. The old `/projects/coo/` URLs forward there, so
     links in older app versions keep working.
2. **The look** (settled after three icon rounds the same day, after a look at about 40 baby-tracker icons on Play)
   - The icon is **Mochi, a bunny-shaped daifuku** (usagi mochi, a real Japanese sweet) with one ear flopped and a
     happy face, on matcha green `#3F6B4A` (`store-assets/icon-options/u6-usagi-one-ear-happy.svg`). Many baby
     trackers on Play use a moon and a sleeping baby on purple or navy, or a round baby face. Napper uses a dummy.
     Deep green and animal mascots are rare.
   - **Mochi tells the baby's state.** Ears up while the baby is awake, ears down and eyes closed while a sleep
     runs (`mochi_awake.svg`, `mochi_asleep.svg`). The badge appears beside "Awake for" or "Sleeping" on Today and in
     the home-screen widget, and nowhere else. The design system's "calm, not cute" principle becomes "calm, with one
     friend".
   - **The app's accent becomes matcha** (`#4F7A45` by day, `#9DBB7E` at night), with a slightly green-grey Washi
     ground. Pump moves from mint to sky (a grey slate at night), so no care colour can be mistaken for the
     brand or a button. Every contrast and distinctness rule in `PaletteTests` still passes.
   - The wordmark is "moch" in ink with the "i" in the accent.

3. **"Coo" stays as the code name.** None of these change:
   - The application ID `com.lechdigital.coo`, which Google Play does not allow to change.
   - Namespaces, project and solution names, log tags, the `Coo` connection string, database names, migrations, the
     server host `api.coo.lechdigital.nl` and the app-links verification.
   - Play product IDs (`premium_subscription`, `premium_lifetime`).

   Renaming these would touch phones' SQLite files, server configuration and DNS for no visible gain.

## Consequences

### Positive

- The product no longer shares a name and title with another baby tracker, and the icon looks like nothing else on the shelf.
- Installed phones update in place: same package and data, and only the strings and icon change.
- Old links (privacy policy, project page, invitations) keep working.

### Negative

- Code and documentation say "Coo" while the product says "Mochi". This ADR is the bridge.
- Invitation links still show `api.coo.lechdigital.nl`. Moving the host needs DNS, Caddy, App Links verification
  and a release, and is left for later.
- About 30 Play apps carry "Mochi" in their name, so the name alone ranks poorly in search. The title's keywords
  ("Baby Tracker & Feeding") carry the search.
