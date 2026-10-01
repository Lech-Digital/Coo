# Spec 006 — Today and quick logging (offline)

Date: 2026-10-01

Status: Accepted (2026-10-01: Q1–Q8 as recommended; §3a added at the owner's request)

Depends on: Spec 001 (domain), Spec 004 (storage), Spec 005 (styles and components), ADR 0006
(seven languages), the design system and `docs/design/washi-ux.html` (Today, Log, and the 03:12 night
screen).

Delivered by: **Increment 6** (`docs/ROADMAP.md`).

## 1. Purpose

This is the first version of Coo that a parent can use for a real day, on one phone, offline:

- a short **first-launch setup** for the baby;
- the **Today** screen: what is running, how long since the last feed, diaper and sleep, four log
  tiles, and today's timeline;
- **quick logging** for all four kinds, with timers for breast feeds (left or right), sleep and
  pumping;
- the **night layout** of Today, from the 03:12 mockup;
- the **Log** screen: a day at a time, with the day ribbon, totals and the timeline;
- all text in the **seven languages**.

It replaces the debug screen as the first page.

**Out of scope, and where it goes:**

| Not in this increment | Where |
|---|---|
| Editing or deleting an entry, undo, logging a past event at a chosen time, duplicates, plausibility warnings in the UI | Increment 7 |
| The nap window card (the predictor; its age table also needs the pediatric review) | Increment 13 |
| Voice logging ("Tell Coo") | Increment 14 |
| Insights and Family tabs | With their features |
| A notification for a running timer | Increment 12 |

## 2. Navigation

- MAUI `Shell` with a bottom tab bar, styled as the design system's bottom navigation.
- **Two tabs now: Today and Log.** Insights and Family are added with the features that fill them.
  Empty tabs saying "coming soon" are noise, at 3 AM and at noon.
- **Sheets are in-page overlays, not modal pages** (design system §6, "bottom sheet"). A `Sheet`
  component slides up over the page, with a dimmed backdrop, dismissed by a tap outside, by Back, or
  by a downward swipe. Modal pages open in their own Android window (Spec 005 finding), which
  breaks the system-bar colours and UI automation.

## 3. First launch: the baby

When the household has no baby, the app opens a single setup page before Today:

- the baby's **name**, which is required;
- the **birth date**, which is required, with a date picker that cannot go into the future;
- "Born early?", which reveals the **due date**, for corrected age (ADR 0008 §2).

**Continue** creates the `BabyProfile` through the Domain rules (Spec 001 §5.1). Errors are shown as
localized text from their codes.

- Nicknames, a second baby, and units are changed in settings later.
- Units default from the region (Spec 004 §3).
- No account and no network are needed (ADR 0003 §1).

## 3a. Expecting a baby: room for pregnancy features later

The owner wants room for pregnancy features later: tips before the birth, information, exercises,
perhaps a contraction timer. **None of them is built now.** What is built is the room for them, so
that adding them later is a new feature, not a rework.

**The baby can be expected, not only born.**

- `BabyProfile` gets an optional `BirthDate`. A profile needs a **birth date or a due date**
  (`baby.birth-or-due-date-required`). While the birth date is missing, the baby is **expected**.
- An expected baby's due date must be at most 44 weeks ahead of today, and at most 6 weeks behind it,
  because babies come late (`baby.due-date-out-of-range`).
- `RecordBirth(birthDate, version, today)` turns an expected baby into a born one. The due date is
  kept, and is checked against the birth date as before, so corrected age works for a preterm birth.
- `BabyProfile.IsBorn`, and `BabyAge.On` is defined only for a born baby.
- Storage: `baby_profiles.birth_date` becomes nullable, through **migration 0002**, a table rebuild
  with `CHECK (birth_date IS NOT NULL OR due_date IS NOT NULL)`. Script 0001 is on the owner's phone
  already, so it is not edited: this is the first real use of "a fix is a new script" (Spec 004 §7.1).
  A test migrates a 0001 database with data to 0002 and checks that nothing is lost.

**Setup** asks "Has the baby arrived?":

- **Yes:** the name and the birth date, plus the optional due date, as in §3.
- **Not yet:** a name, which may be a placeholder ("Baby"), and the due date.

**Today is built from stage-aware sections.** An `IHomeSection` extension point (ADR 0010 §5) lets a
feature add a section to Today for a stage, `Expecting` or `Born`. Today itself only lays out the
sections for the current stage, in order:

- **Born:** the sections of §4. They are written as sections too, so Today has no special case.
- **Expecting:** one section now, **the due-date card**: "Due in 6 weeks · 14 November" (or
  "Due today", or "3 days past the due date"), with **The baby has arrived**, which records the
  birth and switches Today to the born stage. The log tiles are not shown while the baby is
  expected.

**How the later features would fit:**

| Later feature | Fits as |
|---|---|
| Tips and information before the birth | An `Expecting` home section, with localized content |
| Exercises | An `Expecting` home section, and perhaps its own page |
| Contraction timer | A new entry kind `contraction` (ADR 0010 §4): a session about the pregnant parent (subject `Caregiver`), with a Today section for the timer and a "time between contractions" readout. It syncs, exports and survives app kills like every other kind, with no migration. |

This adds one decision, Q8 below.

## 4. Today (day palette)

Top to bottom, following the mockup:

1. **Top bar.** The Coo wordmark, and the baby chip (initial on `Cheek`, the name, and the corrected
   age if it differs, for example "Zosia · 5 mo").
2. **Status hero.**
   - When a session is running: "Feeding · left", "Sleeping" or "Pumping", with the running time.
   - Otherwise: **"Awake for 1 h 20 m"**, from the end of the last sleep, with "since 12:25, after a
     1 h 15 m nap" underneath.
   - With no sleep logged yet: "No sleep logged yet".
3. **Running sessions.** One card per running timer, with its digits ticking each second and two
   actions:
   - breast: **Switch side** and **Stop**;
   - sleep: **Woke up**;
   - pumping: **Stop**.

   The time comes from the stored start, so a timer survives the app being killed. Nothing runs in
   the background.
4. **Log tiles.** Feed, Diaper, Sleep and Pump. Each shows its "last at" line:
   - feed: "12:40 · L" or "14:05 · 120 ml";
   - diaper: "13:05 · wet";
   - sleep: "woke 12:25", or "asleep 20:05" while sleeping;
   - pump: "09:15".

   A tile whose kind is running shows "running" and opens its controls.
5. **Today.** The day's entries, newest first, as timeline rows: time, badge, title, and who logged
   it ("You" on this phone, for now).

## 5. Quick logging

| Tile | Tap | Result |
|---|---|---|
| **Feed** | Opens the feed sheet: **Left**, **Right** (start a breast timer on that side), and **Bottle** | Breast: an open breast feed, `Source = Timer`. Bottle: the bottle panel (below). |
| **Diaper** | Opens the diaper sheet: **Wet**, **Dirty** and **Mixed** | One tap logs it now, and the sheet closes |
| **Sleep** | No sleep running: starts one. A sleep running: shows **Woke up**. | An open sleep, ended by **Woke up** |
| **Pump** | Starts a pumping timer, or opens its controls | **Stop** opens the yield panel: left and right amounts, or **Skip** (Spec 001 §7.5 allows no yield) |

**The bottle panel.**

- An amount with − and + buttons, in the household unit: steps of 10 ml or 0.5 oz, starting at the
  last bottle's amount, or 120 ml (4 oz).
- The milk: **Formula**, **Breast milk** (expressed) or **Both**. Both shows two amounts.
- **Log bottle** saves it now.

**Rules the UI follows:**

- One running session per kind per baby. Starting a second one is not offered while one runs; the
  tile opens the running one. Overlaps that come from sync are Increment 7's duplicate check.
- Every log goes through the existing use cases (`LogEntry`, `ReviseEntry`). A Domain error, which
  should not happen from these controls, shows its localized message and changes nothing.
- After a log, a quiet confirmation ("Logged · Diaper · wet") shows for 3 seconds. Undo arrives with
  editing in Increment 7.

## 5a. Logging while the baby is asleep (added 2026-10-01, at the owner's request)

When a sleep is running and the parent logs something that needs the baby awake (a **diaper**, a **bottle**, or
the start of a **breast feed**, in the day or the night layout), Coo does not log it straight away. A sheet asks
first:

- **"Zosia is asleep · since 20:05"**, then two buttons:
  - **Woke up** ends the sleep now, then logs the entry;
  - **Still asleep** logs the entry and leaves the sleep running.
- **The hint line:** "For a dream feed or a sleepy change, choose Still asleep."
- A tap outside, or Back, cancels: nothing is logged.

**Why ask, rather than end the sleep automatically:** dream feeds and sleepy night changes are real. A guessed end
time would corrupt the sleep totals and, later, the sleep predictor (ADR 0008). The parent decides with one tap, and
Coo never guesses.

**What does not ask:**

- **Pumping:** it is about the parent, not the baby, and runs alongside a sleep.
- **Starting a sleep while a feed runs:** falling asleep at the breast is normal.

The sleep's end time is the moment of the tap, the same moment the new entry gets.

## 6. Today at night

When the palette is Night (Spec 005 §6), Today switches to the **03:12 layout** of the mockup. It is
the same screen, simpler and with larger targets:

- the time ("03:12 · Night mode") and the baby chip;
- a running timer, if there is one, in `Timer` digits, with 64 dp actions (`Stop` is the only
  accent);
- three facts: **Last feed** ("23:50 · left · 18 min"), **Last diaper** ("23:55 · wet") and
  **Asleep since** or **Awake since**;
- the **night quick row**: 68 dp buttons on dark event tints for **Wet**, **Dirty**, **Mixed** and
  **Sleep** (or **Woke up**), plus **Feed L** and **Feed R**. Each logs in one tap, with no sheet;
- no timeline, and no decorative motion.

The quiet-hours line ("family alerts paused until 07:00") arrives with notifications (Increment 12).

## 7. Log

- One day at a time, with ‹ and › to move between days; today is the default, and › stops at today.
- **The day ribbon:** 24 hours. Sleeps are `SleepTint` blocks outlined in `SleepMark`. Feeds,
  diapers and pumps are 4 dp ticks in their `Mark`, on staggered lanes. It has hour labels (00, 06,
  12, 18, 24) and a legend.
- **Totals:** feeds, diapers, sleep time (the part of each sleep inside that day) and millilitres
  pumped.
- **The timeline:** that day's entries, newest first.

Days are local days in the **household time zone** (Spec 001 §5.3, ADR 0006 §5). A sleep across
midnight appears on both days, cut at midnight in the ribbon and in the totals.

## 8. Where it lives

| Concern | Project |
|---|---|
| `GetToday(baby, now)`: running sessions, the last entry of each kind, awake or asleep since, today's entries | `Coo.Mobile.Application/CareLog` |
| `GetDay(baby, date)`: that day's entries, totals, and ribbon segments | `Coo.Mobile.Application/CareLog` |
| `DayMath`: local-day windows in a time zone, cutting sessions at midnight, ribbon positions as fractions of the day | `Coo.Mobile.Application/CareLog` (pure, tested) |
| View models: `SetupViewModel`, `TodayViewModel`, `LogViewModel`, the sheets | `Coo.Mobile.Presentation` (CommunityToolkit.Mvvm) |
| Text formatting: durations ("1 h 20 m"), "last at" lines, 12- or 24-hour times, volumes in the household unit | `Coo.Mobile.Presentation/Formatting` (pure, tested in all seven languages) |
| Strings | `Coo.Mobile.Presentation/Resources/AppResources*.resx` (ADR 0006 §2) |
| Pages, `Sheet`, ribbon drawing, Shell | `Coo.Mobile` |
| `IClockFormat` (Android's 12- or 24-hour setting) | Port in Presentation; implemented in `Coo.Mobile.Platform` |

The view models know nothing about MAUI (Spec 005 §10's architecture test), so every screen's logic
is tested on the global SDK.

## 9. Languages

- `AppResources.resx` (English) and the six translations, with PurePrep's `LocalizationService`
  pattern: System or an explicit choice, stored per device (ADR 0006 §1). The language picker comes
  with settings; until then the app follows the system language.
- **I write the translations for all seven languages now.** They are marked for native-speaker
  review before release, which is the handover's open item 5. Care words ("wet", "dirty",
  "pumping") are the ones that most need it.
- **Plurals** use CLDR categories through `PluralRules` (ADR 0006 §4). Polish uses one, few and
  many: "1 karmienie · 2 karmienia · 5 karmień".
- Tests:
  - key parity across all seven files;
  - matching placeholders;
  - `PluralRules` against CLDR examples;
  - every Domain error code that this UI can show has a string.

## 10. Tests

| Where | What |
|---|---|
| `Coo.Mobile.Application.Tests` | `GetToday`: awake-since from the last sleep end; running sessions; the last of each kind across midnight. `GetDay`: a sleep across midnight split between days; totals; DST days (23 and 25 hours). `DayMath` ribbon fractions. |
| `Coo.Mobile.Presentation.Tests` | View models with the ports mocked: starting a breast timer on the right; switch side; woke up; bottle steps in ml and oz; one running session per kind; the night layout's facts. Formatting in all seven languages. Localization parity and plurals. |
| `Coo.Architecture.Tests` | Presentation still has no MAUI dependency; view models only use Application ports |
| On your phone | The deliverables below |

## 11. Increment 6 — deliverables

| # | Deliverable | How you check it |
|---|---|---|
| 1 | First launch asks for the baby, then shows Today | A clean install on your phone |
| 2 | Log all four kinds from Today, including a breast feed started on the right, with switch side | Tap through; the timeline and the "last at" lines update at once |
| 3 | Timers survive the app being killed | Start a sleep, swipe Coo away, reopen it: the timer still runs from the right time |
| 4 | Night layout | Switch to Night (or wait for 22:00): Today becomes the 03:12 layout, and the quick row logs in one tap |
| 5 | Log screen | Yesterday and today, with the ribbon and totals |
| 6 | Seven languages | Set the phone to Polish, then German: all text changes, plurals included |
| 7 | CI green | GitHub Actions |

## 12. Decisions for the owner

| # | Question | Recommendation |
|---|---|---|
| Q1 | Two tabs now (Today, Log), and Insights and Family added with their features | **Yes.** No "coming soon" tabs. |
| Q2 | Sheets as in-page overlays, not modal pages | **Yes.** It avoids the modal-window problems found in Increment 5. |
| Q3 | First-launch setup asks only the name, the birth date, and optionally the due date | **Yes.** Everything else has a sensible default and can be changed later. |
| Q4 | No nap window card until the predictor (Increment 13), not even the age-typical range | **Yes.** The age table still needs its pediatric review (handover item 4), and an unreviewed range is exactly what ADR 0008 warns against. |
| Q5 | The debug screen leaves the normal flow. In Debug builds and `-p:CooDebugScreen=true` builds, a long press on the Coo wordmark opens it. | **Yes.** The read-guard tools stay available for testing. |
| Q6 | I write all seven languages now, marked for native review before release | **Yes.** A parent can use it in their own language today, and the review stays on the release checklist. |
| Q7 | Today switches to the 03:12 night layout whenever the palette is Night | **Yes.** It is the design system's "night is a mode, not a theme". |
| Q8 | Room for pregnancy features: an expected baby (due date, no birth date yet), `RecordBirth`, migration 0002, and Today built from stage-aware `IHomeSection`s, with only the due-date card for now | **Yes** (asked for by the owner). The later features become additions, not changes. |

## 13. Built and verified (2026-10-01, Pixel 10)

All deliverables of §11 were checked on the owner's phone, on a clean install:

- **Setup:** both paths, "Yes" and "Not yet".
- **The expected stage:** the due-date card, then "The baby has arrived".
- **Quick logging:** breast on the right with switch side and stop; a bottle with the stepper; diaper; sleep;
  pumping with its amount.
- **A timer survives a forced stop.**
- **The night layout** and its quick row.
- **Log**, in the day and the night palettes.
- **Polish,** switched live, and back.

**Found on the phone, and fixed:**

| Finding | Fix |
|---|---|
| Today rebuilt every view each second, so taps on tiles were lost | Running items are stable objects with bound digits; lists change only when what they show changes |
| The app crashed after setup: the start page's `Window` is null once replaced | The window is kept before navigating |
| The Log page's ‹ › had no Figtree glyph | Chevron icons |
| The keyboard hid a sheet's second field and Save | The Today page pads for the keyboard (MAUI 10 `SafeAreaRegions.SoftInput`) |
| "Karmienie" broke mid-word on its tile | Long tile names step down to 12.5 sp; tiles have 10 dp side padding |
| A language change while running crashed MAUI's Shell teardown | `MainActivity` handles locale changes and rebuilds the page tree instead of recreating the activity |
| The setup hint promised "everything stays on this phone", which will not be true after sync | "No account needed to start. You can share with family later." |

**Asleep check (§5a), verified on the same phone:**

- Diaper while asleep asks the question, and **Woke up** ends the sleep, then logs.
- A night **Feed L** during a sleep asks too, and **Still asleep** keeps the sleep and starts the feed.

Two more findings, fixed:

| Finding | Fix |
|---|---|
| At night only one running timer showed, so a dream feed's Switch side and Stop were unreachable | The night layout shows every running timer: active care first in big digits, the sleep below |
| A cold start in dark mode showed the night layout in day colours: the window's Created event re-applied Day after the theme had chosen Night | Created applies the controller's current palette |

**Known gaps, carried forward:**

- The hero's units are not yet smaller than its digits (design system §3).
- The debug screen opens with three taps on the wordmark, because MAUI has no long-press gesture.
- A durations under a minute reads "0 m".
- Translations still await the native-speaker review (handover item 5).

