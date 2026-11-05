# Mochi — the other Play Console pages

Screenshots, graphics, store settings and the Data safety form. Names and descriptions are in `PLAY-LISTING.md`,
what's new per release in `releases/`.

## Screenshots and graphics

All rendered by `python3 store-assets/_src/compose.py` from the demo build's shots in `raw/` (made with
`-p:CooDemo=true` on the `coo-review` emulator, 2026-11-05: the made-up baby Lena, 7 weeks). The captions are in the
`SHOTS` list in `compose.py`, English only.

| Slot | File | Headline / subline | Screen |
|---|---|---|---|
| 1 | `phone/phone-1-log.png` | Every feed, nap and diaper *on one timeline.* / The whole day at a glance | Log, a full day |
| 2 | `phone/phone-2-today.png` | One tap to start. *Mochi keeps the time.* / Feeds, naps, diapers and pumping, live | Today, a sleep running |
| 3 | `phone/phone-3-night.png` | Night colours *for night feeds.* / A dark screen and big buttons for one hand | Today in night colours |
| 4 | `phone/phone-4-stats.png` | Your baby’s average day, *7, 14 or 30 days.* / Milk, feeds, sleep, diapers and pumping | Stats |
| 5 | `phone/phone-5-zoom.png` | Every nap and feed, *hour by hour.* / Zoom into any hours of the day or night | Log, the zoomed day (3–9 AM) |
| 6 | `phone/phone-6-advisor.png` | Questions about your baby? *Ask the Advisor.* / AI answers that know your baby’s log · not medical advice | Advisor |

| Asset | File | Size | Notes |
|---|---|---|---|
| App icon | `icon-512.png` | 512 × 512 | `icon-options/u6-usagi-one-ear-happy.svg`: the bunny mochi on matcha |
| Feature graphic | `feature-graphic.png` | 1024 × 500 | The icon, the "mochi" wordmark and "Baby tracker for feeds, sleep, diapers & pumping" |
| Phone screenshots | `phone/phone-{1..6}-*.png` | 1080 × 1920 | Captioned, on the Washi background |

## Store Settings & Metadata

- **Application category:** Parenting (Primary). Alternative: Health & Fitness.
- **Tags (up to 5):**
  1. Baby tracker
  2. Breastfeeding
  3. Baby sleep
  4. Parenting
  5. Newborn
- **Target audience:** 18+ (Parents, caregivers). Mochi is intended for parents to record their infant's care; it is not directed at children.
- **Content rating questionnaire:**
  - Violence: No
  - Sexuality / Profanity: No
  - Controlled substances / Tobacco / Alcohol: No
  - User interaction / Social sharing: No public feeds or chat; private family sharing between invited caregivers only.
  - Shares user physical location: No
  - In-app digital purchases: Yes (Premium: monthly or yearly subscription, or a one-time lifetime purchase, through
    Google Play).
  - Result: PEGI 3 / Everyone.
- **Data safety declaration** (Play Console → App content → Data safety; checked against the privacy policy of
  27 October 2026 and the 1.8.0 code):
  - **Does your app collect or share any of the required user data types?** Yes.
  - **Is all of the user data collected by your app encrypted in transit?** Yes (HTTPS only).
  - **Do you provide a way for users to request that their data is deleted?** Yes: in the app (Settings → Account →
    Delete account) and on the web at `https://api.coo.lechdigital.nl/delete-account`.
  - **Shared with third parties:** none. Google's Gemini API processes Advisor questions, Tell Mochi text, voice
    recordings and health-record photos on Mochi's behalf; under Play's definitions that is a service provider, not
    sharing. Nothing goes to advertisers, analytics or data brokers.
  - Nothing is collected while signed out: the log stays on the phone (Android backup is off). Every type below is
    therefore **optional** ("users can choose whether this data is collected") and only collected after sign-in.

  | Data type (Play's category) | Collected | Ephemeral | Purposes | What it is in Mochi |
  |---|---|---|---|---|
  | Personal info → Name | Yes | No | App functionality, Account management | Google display name, shown to the family |
  | Personal info → Email address | Yes | No | App functionality, Account management | Google account email |
  | Personal info → User IDs | Yes | No | Account management | Google account identifier |
  | Health and fitness → Health info | Yes | No | App functionality | The shared care log: feeds, sleep, diapers, pumping, growth, medicines, temperatures, vaccinations, doctor visits, baths, tummy time, solid food and milestones |
  | Financial info → Purchase history | Yes | No | App functionality | Premium purchase, verified with Google Play |
  | Photos and videos → Photos | Yes | Yes | App functionality | Health from a photo: sent to Gemini to be read, not kept |
  | Audio → Voice or sound recordings | Yes | Yes | App functionality | Tell Mochi and the Advisor by voice: sent to Gemini to be written down, not kept |
  | App activity → Other user-generated content | Yes | Yes | App functionality | Tell Mochi text and Advisor questions: sent to Gemini, not kept |
  | App activity → App interactions | Yes | No | App functionality | Daily counts of Advisor questions, Tell Mochi uses and voice messages, for the daily limits |
  | Device or other IDs | Yes | No | App functionality, Account management | A random device identifier and the device's name, sent at sign-in to list the family's phones |

  Not collected: location, contacts, messages, files and docs, calendar, web browsing, app info and performance
  (no crash reporting or analytics), and no advertising ID.
- **Government apps:** No.
- **Health apps declaration:** A baby care log (feeds, sleep, growth, medicines and vitamins with reminders,
  temperatures, vaccinations, solid food and milestones). Not a medical device; it does not diagnose or treat. The
  Advisor gives general information and says so, with "call your doctor; in an emergency, call 112".
- **Ads:** No (strictly ad-free).
- **Privacy policy URL:** `https://lechdigital.nl/projects/mochi/privacy/`
