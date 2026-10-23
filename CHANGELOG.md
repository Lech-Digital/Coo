# Changelog

Every build that leaves this machine (Play upload, a phone other than the owner's) gets a new version:

- **Version name** (`ApplicationDisplayVersion` in `src/Mobile/Coo.Mobile/Coo.Mobile.csproj`) follows SemVer:
  MAJOR for a change that needs the server to move with it or breaks sync with older phones, MINOR for features,
  PATCH for fixes.
- **Version code** (`ApplicationVersion`) goes up by one with every upload and is never reused. Play refuses a code it
  has seen. The server's `AppUpdates:Latest` (`src/Backend/Coo.Api/Hosting/AppUpdateEndpoints.cs`) follows it;
  `AppUpdates:Minimum` is raised only when old versions must stop (they get the update screen).
- Each release gets a section here, newest first, written for parents rather than developers where it ends up in
  Play's "What's new".

## 1.6.0 (code 23) — 2026-10-24

### New
- **Widgets that work, and two more**: the glance widget (it showed "Can't load widget"), a quick-log widget with
  one-tap Left, Right, Wet, Dirty, Sleep and Pump (Switch, Pause and Stop while feeding), and clocks since the last
  feed and awake or asleep.
- **Timers in the notification bar**: a running feed, sleep or pump shows its clock there, with Switch, Pause, Stop
  or Woke up, from the lock screen.
- **Invite a nanny, daycare or grandparents**: a caregiver logs feeds, sleep and diapers; family who follow along see
  the day and change nothing.
- **Import from another app** (Settings → Your data): a CSV export from another baby app, previewed before anything
  is saved; entries already in Coo are skipped.
- **Export your log** as a CSV file, which Coo can import again.

## 1.5.0 (code 22) — 2026-10-24

### New
- **Health from a photo**: take or choose a photo of a growth card, clinic leaflet or vaccination booklet (Health →
  From a photo). Coo reads the weights, lengths, head sizes and vaccinations with their dates or ages, and you check
  each one before it is saved.
- **The doctor's report in another language**: choose the PDF's language on Stats, whatever the app's language is.

### Changed
- **Tell Coo hears you better**: choose the language you speak to it ("Speak in"), even when the app is in another,
  and it listens for your baby's name and the care words (left, bottle, wet…).

## 1.4.0 (code 21) — 2026-10-24

### New
- **Personal nap times** (Premium): Coo learns your baby's own wake windows from the sleeps you log and shows when
  the next nap or bedtime is likely ("Nap likely 10:50–11:15"), with "Why this time?" one tap away. Until it has
  learned enough, it shows the typical wake window for your baby's age.
- **The nap reminder** follows that prediction, 5, 10, 15 or 20 minutes before the window opens.
- **Reminders on the minute**: allow "Alarms & reminders" for Coo (Settings → Reminders) and they arrive exactly on
  time, even when the phone is idle.
- **Sync in the background**: your partner's entries reach this phone every 15 minutes or so, with Coo closed, so
  reminders and the widget stay up to date.

### Changed
- **Tell Coo** listens better: hold the mic to talk and let go when done, or tap, talk, and tap again. It waits for
  you to start, keeps listening through pauses, and shows the words as you speak. It no longer gives up at once on
  phones without an offline speech pack for your language.
- Without Premium, Today shows the typical wake window for your baby's age instead of an estimated nap time.

## 1.3.0 (code 20) — 2026-10-23

### New
- **Health and growth**: log weight, length and head circumference, medicines and vitamins, temperatures,
  vaccinations and doctor visits from the new Health button on Today, in kg/cm/°C or lb/in/°F.
- **Growth chart**: weight, length and head against the WHO Child Growth Standards (3rd–97th percentile) for the first
  two years, with your baby's percentile at each measurement.
- **Report for the doctor**: a PDF of the last 7, 14 or 30 days (daily averages, each day, growth with percentiles,
  health) to share or print, from the Stats tab. Made on your phone.
- **This week, in words**: the Advisor writes a short summary of the week compared with the last, on Stats.
- **Tell Coo** understands health and growth too: "gave her vitamin D", "temperature 38.4", "she weighs 5.1 kg".
- The Advisor knows about recent medicines, fevers, vaccinations and growth percentiles.

### Changed
- The Tell Coo example uses your own baby's name.

## 1.2.0 (code 19) — 2026-10-23

### New
- **Yearly and lifetime Premium**: alongside monthly, a yearly plan (with how much it saves, and a 7-day free trial)
  and a one-time lifetime purchase. Whichever you choose covers your whole family.
- **Reminders** (Settings → Reminders): a nudge after 2–4 hours since the last feeding started (quiet at night unless
  you want it), when a feeding or pumping timer has been running for 45 minutes, and shortly before the nap window
  ends. Each parent chooses their own, and they work offline.
- **Home-screen widget**: the last feeding, sleep and diaper with their times, and a Tell Coo button.

## 1.1.0 (code 18) — 2026-10-23

### New
- **Tell Coo**: say or type what happened — "Maja started on the left", "120 ml formula at two, then a wet diaper",
  "she woke up" — and Coo shows what it understood before anything is saved. Starts and stops act on what is running,
  like the buttons; entries already logged by your partner are spotted and left unticked. Works in all seven languages.
  Long-press Coo's icon for a "Tell Coo" shortcut. Without a connection, your words are kept and offered for review later.
- Premium: up to 100 a day; 5 a day to try it without Premium.

## 1.0.0 (code 17) — 2026-10-22

The first public release (open testing).

### New
- **Stats tab**: daily averages per day, week or month as a bar chart, with each bar compared with the one before
  ("13 h a day, +1 h vs the week before"). Chips switch between sleep, longest sleep, feeds, breastfeed length, bottle,
  diapers and pumping. Days nobody logged and days not over yet do not drag the average down.
- **Baby Advisor** knows the last seven days of the log, answers in the app's language, and says plainly it is not
  medical advice. Before the first question it asks for consent to send the question and a log summary to
  Google's Gemini AI. Its own tab icon.
- **Coo Premium** page lists what it adds and links to Google Play to manage the subscription; a purchase now gives
  Premium to the whole family.
- In-app updates download in the background and ask for a restart; a review is asked for rarely, by day, after a week.

### Fixed
- Overnight sleep was counted on both days in the statistics, and Today and Log showed different sleep totals.
- "Next nap" could show a time already past.
- 12-hour times wrapped in the Log; long totals wrapped in the Log tiles; Advisor suggestions were cut off.
- "Resolve all" in Needs attention can be undone.
- The Advisor asked about the account's own family instead of the one on screen.

### Under the hood
- Purchases are verified on the server and bound to the family they were bought for; the Play notification webhook is
  authenticated; complimentary Premium for listed accounts (Play review).
- Google Play libraries moved off the deprecated Play Core.
- Account deletion page at https://api.coo.lechdigital.nl/delete-account; deleting an account also deletes its
  Advisor question counts.

## 0.13.0 (code 16) and earlier

Increments 1–11d and 15c: see `docs/ROADMAP.md`.
