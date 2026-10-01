# Spec 006 — Today and quick logging (offline)

Date: 2026-10-01

Status: **Draft — for review**

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
