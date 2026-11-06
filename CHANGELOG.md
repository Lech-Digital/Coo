# Changelog

Every build that leaves this machine (Play upload, a phone other than the owner's) gets a new version:

- **Version name** (`ApplicationDisplayVersion` in `src/Mobile/Coo.Mobile/Coo.Mobile.csproj`) follows SemVer:
  MAJOR for a change that needs the server to move with it or breaks sync with older phones, MINOR for features,
  PATCH for fixes.
- **Version code** (`ApplicationVersion`) goes up by one with every upload and is never reused. Play refuses a code it
  has seen. The server's `AppUpdates:Latest` (`src/Backend/Coo.Api/Hosting/AppUpdateEndpoints.cs`) follows it, but
  only once the release is live on Google Play, then deploy: from 1.8.1 the app suggests updating to that version
  itself, and before then the store would have nothing new to show. `AppUpdates:Minimum` is raised only when old
  versions must stop (they get the update screen).
- Each release gets a section here, newest first, written for parents rather than developers where it ends up in
  Play's "What's new". That text, in all seven languages and ready to paste, goes in
  `store-assets/releases/<version>.md`.

## Unreleased

## Unreleased

### Changed
- **Night wakings are no longer treated like day wake windows.** At night a baby usually feeds and goes back to sleep
  within minutes, so Today no longer shows the age's day window (such as "60–90 min") at 3 AM. Instead, once a baby has
  three nights of wakings logged, the night screen says when she is likely back asleep ("Back to sleep · likely
  3:40–3:55 AM"), learned from her own wakings that were most like tonight's: after a similar stretch of sleep, at a
  similar hour, and counted from the end of the feed when there is one.
- **Bedtime learns her rhythm.** From about two months, the bedtime window leans towards the time this baby's night has
  usually started lately, more so as she grows and the more regular her bedtimes are.
- **Stats → Sleep pattern**: the last 7, 14 or 30 days side by side, each a column from noon to noon with her sleeps and
  feeds, the longest sleep of each day in full colour, and in words when it usually starts and how long it lasts.
- **The Advisor asks about her own week**: up to two questions from what changed in the log (shorter or longer
  nights, more feeds, fewer wet diapers) before the questions for her age, which now follow the corrected age of a baby
  born early.
- **Next feed**: once a baby has a few days of feeds logged, Today and the night screen say when the next feed is likely,
  learned from her own feeds at that time of day (night feeds are usually further apart), and say "feeds may come close
  together" in her usual evening cluster-feeding hours instead of a time.
- **This week** on Today: what changed against last week in a line or two ("Longest sleep 40 m longer than last week",
  "2 more feeds a day"), and nap transitions ("Moving from 4 naps to 3").
- **Sleep pattern**: tap a day to open it in the Log; diaper changes show as dots up to 14 days.
- **The Advisor in every language**: its age guide and the twelve questions per age are now in all seven languages, and
  its answers can use what Mochi has learned about the baby (her bedtime, night wakings, feeds, this week's changes).
- **Short naps, her own way.** After a few short naps, the next-nap estimate uses how much shorter this baby's own
  windows are after a short nap, instead of a fixed 15%; "Why this time?" says so.

## 1.11.0 (code 31) — 2026-11-05

Coo is now **Mochi** (ADR 0011): another baby tracker on Google Play took the name "Coo Care" with our title word for
word. Only the name and the look change; the package `com.lechdigital.coo`, the server, the data and every code name
stay. Deploy the server with the release (the invitation and delete-account pages and the Advisor's prompts say Mochi)
and push the site (`lechdigital.nl/projects/mochi/`, with redirects from `/projects/coo/`). In Play Console: the new
app name, descriptions, icon, feature graphic and screenshots from `store-assets/`.

### Changed
- **The name**: Coo is Mochi everywhere you see it, in all seven languages: the app name under the icon, "Tell Mochi",
  Mochi Premium, the widgets, notifications, the PDF report, the invitation page and the Advisor.
- **A new icon: Mochi**, a bunny-shaped mochi on matcha green. Mochi also sits beside "Awake for" on Today and in the
  home-screen widget: ears up while your baby is awake, ears down while she sleeps.
- **New colours**: the buttons and links are matcha green, and pumping is now sky blue, so it never looks like a
  button. The splash screen and the "mochi" wordmark at the top of every tab follow.
- **The privacy policy** moves to `lechdigital.nl/projects/mochi/privacy` (the old address forwards there).
- **A calmer, tidier layout on every tab**: a soft colour wash behind the pages (day and night) with glass cards over
  it, one top bar everywhere (the baby chip is gone; the day/night switch sits at the far right), big page titles,
  grouped lists in Settings and Stats, and the chosen tab on a soft green pill.
  - **Today**: the sleep timer and Mochi share one card; "Tell Mochi" and "Log something" are two equal buttons, and
    "Log something" opens one sheet for everything (now or earlier, care and health).
  - **Log**: each total says what it is made of and how it compares with a usual day ("Last 9:52 AM · usually 6").
  - **Stats**: the average day lists each kind with its icon; growth, the sleep plan and the milk stash sit under More.
  - **Premium**: the benefits with icons, and the plans side by side.
  - **Night**: the timer on a glass card with sleeping Mochi; no second "Woke up" button.
  - **Switching day and night** shows Mochi in the new colours for a moment instead of a half-repainted screen.
- **The milk stash** holds bottles and containers as well as bags.
- **Diapers** are wet or dirty; the colour and "mixed" are gone from the form.

### Fixed
- **Changing the text size in Android's settings** while the app was open closed it ("keeps stopping").
- **Large text and small phones** (checked as a OnePlus 8 Pro and a 360 dp phone at 1.3× text):
  - the four tiles on Today and the day's totals in the Log go two by two instead of cutting "Diaper" or breaking
    "pumped" in the middle
  - "Tell Mochi" and "Log something" are never cut short: with large text they go one under the other
  - the Log's times stay on one line
  - the night screen keeps "Last feed" on one line
  - the birth date shows as "17 Sep 2026" instead of a long date cut off at the end
- **The zoomed day:**
  - on 12-hour phones the hours read "3 AM" instead of "3:00 AM", so they no longer run together and the title fits on
    one line
  - pumps have their own row under the diapers, so a diaper and a pump at the same time no longer overlap
- **The top bar** is the same on every tab: the wordmark on the left and the day/night switch at the far right. The
  baby's name and picture are gone from it: they did nothing there. The baby is in Settings → Baby, and twins switch
  on Today.
- **One brand green**: buttons, links and the wordmark use the icon's matcha (`#3F6B4A`); the baby's initial circle is
  soft matcha instead of pink.

### New
- **Bottles and containers in the milk stash**, not only bags: choose what the milk is in when you add it or edit it,
  and the list says "Bottle #2 · 150 ml · Fridge". Bags stored before stay bags. The server must be deployed before
  this release: it checks each entry with the same rules.

## 1.10.0 (code 30) — 2026-11-04

The server is deployed already (370dd48: the `sleepplan` and `milkbag` entry kinds, migrations `AddSleepPlanUsage` and
`AddAdvisorBetterCount`, the Advisor's models and 17-second deadline). Publish the site's privacy update (sleep plans,
the milk stash) with the release. Live on Google Play 2026-11-04; `AppUpdates:Latest` 30 (server `.env` and the compose default), `Minimum` stays 29.

### New
- **Sleep plans** (Spec 019), free: a plan for your baby's day from her own log and her age (wake-up, each nap,
  bedtime), a bedtime routine and a gentle settling approach you choose (fading, chair, check and console, or no
  crying; under 4 months rhythm and routine only), with three safety questions first. A check-in on day 3 and then
  every week shows what changed in the log and suggests one small change. Today shows the plan's day and the next
  planned sleep, the nap reminder follows the plan, and past plans are kept. Parents start and change a plan;
  caregivers follow it.
- **With Premium**, the Advisor writes the plan and each check-in in words for your baby, from the plan's numbers
  (never her name); a reply that quotes a time the plan does not have is not shown.

- **How right the nap times have been**: "Why this time?" now says how many of the last two weeks' nap and bedtime
  windows were right for your baby ("12 of 15"), worked out on your phone from your own log.

- **Twins and siblings**: tap the baby's name at the top to choose which baby Coo shows; every tab follows. Today shows
  the others at a glance ("Mia · asleep 40 m · fed 1 h 20 m ago", a tap switches), "Log for both" logs a diaper,
  bottle, sleep or tummy time for each of them at once (one Undo takes them all back); a breastfeed becomes a tandem
  feed, one baby on each side. Stats puts their average days side by side.

- **Milk stash** (Stats → Milk stash): the bags of expressed milk in the fridge and freezer, the one to use first on
  top, with its use-by (4 days in the fridge, about 6 months frozen, 24 hours once thawed, as the CDC advises). Mark a
  bag used, freeze, thaw or throw it away; everyone in the family sees the same stash, and Today says when a bag
  should be used soon. Each bag gets a number to write on the container (#1, #2, #3; a used bag's number comes free
  again), and Edit changes the number, the note, or how much is left after part of it was used. After pumping, choose
  Fridge or Freezer with the amount and the bag goes straight into the stash, its number in the message.

- **The nanny's handover**: when a nanny or daycare logs for your baby, Today shows "Today with Anna" at pick-up: feeds
  and how much from bottles, naps and how long, diapers, medicines and her notes, from her first entry to her last.
  "Seen" puts it away until she logs more. A caregiver sees her own day and can share it as a message.

- **Last night**, every morning until noon on Today: how long your baby slept, how often she woke, the longest stretch
  and the night feeds; with a sleep plan, last night against the week before the plan.
- **While you were away**: open Coo after three hours or more and Today shows what the others logged meanwhile, per
  person (the night your partner did, the grandparents' afternoon), until "Got it".

### Changed
- **The Advisor waits for a busy Gemini**: up to a minute instead of 20 seconds, and says "unavailable, try again" rather
  than "couldn't reach the Advisor" when Google's AI is overloaded. Answers come from the fastest model first, and Tell
  Coo reads what you say faster too.
- **With Premium, better Advisor answers**: up to 60 a month from a stronger model (Gemini 3.6 Flash), then the standard
  one. Premium's Advisor allowance is 20 questions a day (was 100), so the AI costs of the heaviest month stay within
  what Premium brings in.
- **Bottle amounts in steps of 5 ml**: + and − on the bottle go 75, 80, 85 instead of 75, 85, 95 (ounces stay at ½ oz).
- **Personal nap times are free**: when the next nap or bedtime is likely, on Today, in the widget, and the nap
  reminder. They are worked out on your phone and cost nothing to run.

### Fixed
- **"Log a few more days for a personal suggestion" no longer stays for weeks.** Your baby's own nap window shows once
  there are three of her windows on three days; a baby whose windows vary a lot gets a wider window instead of none.

## 1.9.2 (code 29) — 2026-11-03

Everything in 1.9.1 (never uploaded), and:

### New
- **A loading page** while Coo signs you in and fetches your family's log, signs out, switches families, or checks
  Premium with Google Play: Coo's mark and a line on what is happening, instead of screens jumping one after another.

### Changed
- Store builds run R8 (`AndroidLinkTool=r8`, `Platforms/Android/proguard.cfg`), so unused Java code is removed from the
  app (Play's DEX optimisation check). .NET for Android keeps `-dontobfuscate` in its own rules, because C# finds Java
  methods by name; Play's "obfuscation" share stays at 0% for that reason.

## 1.9.1 (code 28) — 2026-11-03 (not uploaded; shipped in 1.9.2)

Works with the server of 1.9.0 (no server change).

### Fixed
- **Signing out takes your families' logs off this phone.** Before, they stayed: signing in with another Google account
  afterwards still showed the first account's family and its members. Coo sends anything not sent yet first, and says
  how many entries would be lost if some still have not reached the server. A log you kept on this phone before ever
  signing in stays. Sign in again and everything comes back.
- **"Show which family?"** reads clearly with several families, as a nanny or a grandparent has: each row shows the
  children, whose family it is and your role there ("Family of Andrzej · Caregiver"), and the shown family is
  highlighted. Bring its entries here and Remove from this phone moved behind "⋯".
- **Joining a family no longer leaves a copy of your baby behind.** Your own family goes by itself once you join: at
  once when nothing was logged in it, and after "Bring my entries over" once your entries are in the new family (it
  used to ask a second time, and saying nothing kept it). "Keep them separate" still keeps both. A family you share with
  someone else is never deleted for you.
- **A nanny joining a second family** keeps both: Coo no longer offers to copy the first family's log into the new one
  (and then leave it). Entries are only ever brought over from your own family. The invitation says which role it is
  for (parent, nanny or daycare, or family who follow along); it always said "parent".
- **Premium belongs to the family it was bought for.** One phone's Google Play purchase showed Premium in every family
  signed in on that phone. Signed out, there is no Premium.
- The empty family a phone makes at its first start is no longer listed after signing in from the welcome screen ("No
  baby yet · On this phone only").
- "Enter a code" no longer comes back filled in with a code that was already used.
- The bottle's one-tap amounts include the amount shown (a newborn's 15 ml is 15, 30, 45; it read 5, 20, 35, 50), and
  the last bottle comes back as it was logged, to the 5 ml.
- A long baby name no longer pushes into "coo Premium" and the night switch at the top.
- Today's headline no longer stays in the big type after switching to a family without sleeps ("No sleep logged yet"
  filled half the screen).

## 1.9.0 (code 27) — 2026-11-03

Works with the current server; deploy the server of the same commit to share a baby's girl/boy choice between phones
(migration `AddBabySex`) and to get the billing fixes below. The server now refuses to start without
`GOOGLE_PLAY_CREDENTIALS_JSON` (DEPLOY.md, "Billing").

### New
- **How Coo works**: a short tour of what Coo does, one tip per screen, shown the first time Coo opens (Skip at the
  top, Previous tip and Next tip at the bottom, or swipe) and again from Settings → About.
- **"On an average day" at the top of Stats**: milk from bottles a day and per bottle, feeds, sleep and diapers over
  the last 7, 14 or 30 days. The answer to the doctor's "how much does she drink a day?".
- **The growth chart in Stats**, with the latest measurement and its percentile.
- **Girl or boy, once, with the baby** (welcome screen and the baby page), shared with the family. The growth chart
  uses it instead of asking on every phone.
- **Restore purchases** on the Premium screen.

### Changed
- **Feed and Bottle sheets** are as tall as what they hold. Feed shows the last feed and marks the side to offer next;
  Bottle shows the last bottle, starts at that amount or at a typical one for the baby's age (15 ml for a newborn, not
  120 ml), and has one-tap amounts.
- **Diapers are Wet or Dirty.** "Both" is gone: a dirty diaper is usually wet too, and nobody can tell. Entries already
  saved as both keep it.
- "Fed just now" instead of "0 m since last feed".
- The Premium screen lists what Premium really adds (personal nap times and the nap reminder, Tell Coo's allowance),
  asks you to sign in before buying so the whole family gets it, and shows buying only to parents.

### Fixed
- **Premium**: a cancelled subscription keeps Premium for the whole family until the paid period ends (it stopped at
  once for everyone but the payer); a payment that was still pending, or a purchase interrupted by closing the app, is
  completed later instead of being refunded by Google after three days; closing Google Play's sheet no longer says the
  purchase failed; Premium is checked again when Coo comes back to the screen, not only at a cold start.
- Server: purchases are acknowledged by the server once recorded, re-read from Google every hour when about to renew,
  new or not checked for a day, and a Google outage answers "try again" instead of "invalid purchase".
- Stats: tapping a chip or a bar no longer throws the chip row back to the start or the page to the top.
- A family member invited as a viewer: the phone can store its family list (it was refused).

## 1.8.1 (code 26) — 2026-10-27

### Fixed
- **Signing in on the welcome screen gets your family's log.** Coo now syncs as soon as you sign in there, instead of
  waiting for a baby to be added, and shows "Getting your family's log…" in place of the baby form until Today opens.
  "Add a baby here instead" is there if the family has no baby yet.
- **Coo tells you about a new version itself**, once a day, when Google Play has not offered it in the app (it can
  lag a release by hours). Update opens Coo in Google Play.

## 1.8.0 (code 25) — 2026-10-24

Needs the server of the same day: it knows the new kinds of entry, and only then do they sync.

### New
- **Reminders for the diaper check, baths, and medicines and vitamins.** A diaper check a set time after the last
  change; a bath every day, every other day, every third day or weekly, at your bath time; and any medicine or
  vitamin at its own times of day, every day or for a course of days ("Vitamin D daily" is one tap). A dose you
  already logged is not reminded. Answer from the notification: Wet, Dirty or Both; Bath done; Given.
- **Baths**: log them from "Earlier…" or by telling Coo.
- **Tummy time**: a timer started from "Earlier…" and stopped on Today, with today's total; or log it afterwards.
- **Solid food**: what your baby ate, how much, and how it went (liked it, didn't, or a reaction). The Advisor sees
  every food tried and any reaction.
- **Milestones**: first smile, rolling over, first tooth, first steps and more, or your own, on the day they happened.
- Tell Coo understands baths, tummy time, solid food and milestones.
- **Already use Coo? Sign in** on the welcome screen: a new phone goes straight to your family's log.

### Fixed
- Joining a family from an invitation link: the code shows on the sign-in step and stays filled in under "Enter a
  code"; an account that is already in the family now gets its log at once, and the welcome screen gives way to
  Today without restarting the app.
- Icons sit in the middle of their circles (the back arrow had moved down and right), the milestone star draws as a
  star, and button labels such as "Switch & resume" fit instead of being cut off.
- "Earlier…" scrolls, so Solid food and Milestone at the bottom can be reached.

### Changed
- Coo's own dialogs everywhere instead of Android's grey ones, and a word from Coo before Android asks for the
  microphone, camera or notifications.
- Every icon sits in the middle of its circle (the mic was off-centre), and each health entry has its own icon.
- "Decide for me" on a sleep now picks night for a long sleep or one that starts in the evening; it always said nap.

## 1.7.0 (code 24) — 2026-10-24

### New
- **Speak to the Advisor**: a mic beside the question box; hold to talk, or tap and talk.
- **Tell Coo understands speech much better**: when you are signed in, what you say goes to Google's Gemini to be
  written down (the words show in the box before Coo reads them). Signed out or offline, your phone's own speech
  recognition is used as before. Voice messages have a daily allowance: 60 with Premium, 5 without.

### Changed
- **A better Advisor**: it sees every entry of the last two days, two weeks of daily totals, four weeks of trends and
  Coo's next-nap estimate, knows what time it is for you, answers the question first, and no longer starts every
  answer with "I am not a doctor" (the note stays under the conversation). It politely declines questions that are
  not about your baby and family.
- Tell Coo and the Advisor ask for your agreement again, because recordings are now sent.

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
- **Tell Coo**: say or type what happened — "Lena started on the left", "120 ml formula at two, then a wet diaper",
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
