# Spec 009b — The Log day chart

Date: 2026-10-02

Status: Accepted (2026-10-02: Q1–Q3 answered in the session from mockups, band "B" and zoomed day "2"; Q4–Q6 as recommended)

Depends on: Spec 006 §7 (the Log screen, the day ribbon), Spec 007 (opening an entry to edit it), Spec 005 (palettes,
quiet hours), Spec 009 §3.2 and §3.4 (hidden kinds, quiet hours), the design system's "Day ribbon" entry.

Delivered by: **Increment 9b** (`docs/ROADMAP.md`), between Settings and Identity.

## 1. Purpose

The ribbon on Log should show **the day's rhythm** at a glance (owner, Q1): when the baby slept and for how long, and
how feeds, diapers and pumps fall around the sleeps. Today it does not. Every event is a 4 dp tick in one 44 dp bar,
so feeds 20 minutes apart merge into one block, a 7-minute feed looks like a 30-minute one, short naps are hairlines,
and there is no "now" and no detail to be had.

This spec replaces the ribbon with a **rhythm band** and adds a **zoomed day** that opens when the band is tapped.

**Out of scope:**

| Not in this increment | Where |
|---|---|
| The 24-hour clock view (the day as a ring) | A future Insights screen (owner, Q2) |
| Pinch-to-zoom | Later, if the fixed zoom is not enough |
| Several days at once, weekly patterns | Insights |
| Charts on Today | Not planned |

## 2. The band on Log

Replaces `RibbonView` in place, between the header and the totals.

- **One band, 24 hours, 00 to 24.** Sleep **fills** the band in `SleepMark`, so the gaps read as awake time.
- **Feeds** are pills **above** the band, in `FeedMark`, as long as the feed took. A feed without a length (a bottle)
  or a very short one gets a minimum width of 7 dp, so it stays visible.
- **Diapers** are dots **below** the band, in `DiaperMark`. **Pumps** are small rounded squares under the diapers, in
  `PumpMark`, at the pump's start.
- **The hour grid:** a hairline every 3 hours inside the band. Labels every 3 hours under it: 00, 06, 12, 18, 24 in
  `Meta`, and 03, 09, 15, 21 smaller and lighter.
- **Quiet hours** (Spec 009 §3.4) are a faint `SleepTint` wash behind the band. When the start and end are equal
  (no quiet hours), there is no wash.
- **Now:** on today, an `Accent` line with a dot on top. Running sessions (a sleep, a breast feed) draw up to now.
- **A sleep across midnight** is cut there, as before (Spec 006 §7).
- **Hidden kinds** (Spec 009 §3.2) leave the band, as they leave the legend and the totals. Their entries stay in the
  timeline.
- **The legend** moves directly under the band, above the totals.
- Height about 110 dp. Colours come from the live palette, in day and night.
- **Accessibility:** the band has a description for screen readers ("Slept 14 h 5 m in 8 sleeps; 7 feeds;
  7 diapers; 2 pumps. Double-tap for the day in detail."), and a tap target the size of the whole band.

## 3. The zoomed day

**Tapping the band** opens a sheet on the Log page (the existing `Sheet`, Spec 006 §2).

- **6 hours fill the screen width** (4× the band). The day scrolls sideways; it opens scrolled so the tapped time is
  in view, a third of the way in from the left, clamped to the day's start and end.
- **The title** names what is in view and follows the scroll: "Today, 12:30 – 18:30", "Wed 30 Sep, 03:00 – 09:00".
- **Every hour** has a hairline and a label (`13:00`, or `1 PM` with Android's 12-hour setting).
- **Labels on the items**, in the item's colour family:
  - a **sleep** shows its length inside the block ("1 h 30 m"), or nothing if the text does not fit;
  - an **awake stretch**, the gap between two sleeps, shows "awake 1 h 40 m" in `Ink2` when it fits;
  - a **feed** shows its time, side and length above its pill ("14:10 R 30 m"); a bottle shows its volume in the
    household unit ("17:20 120 ml");
  - a **diaper** shows wet, dirty or mixed under its dot;
  - a **pump** shows its yield when there is one ("21:00 90 ml").
- **A position bar** at the bottom shows the whole day and the part in view.
- **Tapping an item** opens its entry in the edit page (Spec 007), as a timeline row does. The hit area of each item
  is at least 24 dp wide; where items overlap, the nearest to the finger wins.
- **Back or a tap outside** closes the sheet. Unlike the "Earlier…" sheet, closing must not depend on the view model
  (the bug fixed on 2026-10-02): the page owns this sheet and closes it itself.
- A day with no entries shows the band empty; the zoomed day still opens and shows the hours.

## 4. Where it lives

| Concern | Project |
|---|---|
| `GetDay`: each `RibbonMark` also carries its entry's ID, and a session's real end (a feed or pump timer too, not only sleep) | `Coo.Mobile.Application/CareLog` |
| The chart items: kind, from, to, entry ID, the label for the zoomed day, the awake stretches, the screen-reader summary | `Coo.Mobile.Presentation/Log` (pure, MAUI-free, tested) |
| Quiet hours for the wash | `LogViewModel`, from the same source Auto uses (`ThemeController`) |
| Drawing the band and the zoomed day, the tap → time and tap → item hit tests, the sheet | `Coo.Mobile` (`RibbonView` reworked, a new `DayDetailView`) |
| Strings ("awake {0}", the screen-reader summary, the title's range) | `AppResources*.resx`, all seven languages |

No stored data changes, so there are no migrations and nothing to sync.

## 5. Tests

| Where | What |
|---|---|
| Application | `GetDay` marks carry their entry IDs; a breast feed's mark runs from its start to its end; a bottle's is a point; a running sleep ends at now on today and at midnight on a past day. |
| Presentation | The chart items: awake stretches between consecutive sleeps (none before the first or after the last sleep of the day); a sleep across midnight is cut on both days; labels for a breast feed (side and length), a bottle (ml and oz), each diaper content, a pump with and without yield; hidden kinds leave the items; the summary text. In all seven languages (the parity test, and English and Polish asserted). |
| Localization | Every new key in all seven languages, placeholders matching. |
| On the phone | The deliverables below, in day and night. |

## 6. Increment 9b — deliverables

| # | Deliverable | How you check it |
|---|---|---|
| 1 | The band | Log shows sleep filled in, feeds as pills of their real length above, diapers and pumps below, the 3-hour grid, quiet hours shaded, and "now" on today |
| 2 | Hidden kinds | Hide Pumping: the squares and the legend entry go |
| 3 | The zoomed day | Tap the band at about 14:00: the sheet opens with 14:00 in view; labels show lengths, awake stretches, sides, diaper contents |
| 4 | Scrolling | Swipe to the night: the title and the position bar follow |
| 5 | Into an entry | Tap a feed in the zoomed day: its edit page opens; Back returns to Log |
| 6 | Closing | Back closes the sheet; so does a tap outside; Coo never goes to the background with the sheet open |
| 7 | Night | The same in the night palette |
| 8 | CI green | GitHub Actions |

## 7. Decisions for the owner

| # | Question | Answer or recommendation |
|---|---|---|
| Q1 | What should the chart tell you first? | **Answered: the day's rhythm.** |
| Q2 | Layout | **Answered: B, one rhythm band**, and the 24-hour clock later, in Insights. |
| Q3 | What does a tap on the band open? | **Answered: the zoomed day (2)**, details and zoom in one. |
| Q4 | Zoom level | **6 hours per screen.** A 15-minute feed is then about 13 dp wide and its label fits; a whole night is one swipe. |
| Q5 | Pumps in the band | **A square at the start, no length.** A pump's length matters less than its yield, which the zoomed day shows. |
| Q6 | Awake stretches before the first sleep and after the last | **Not labelled.** The day before or after may hold the sleep that bounds them; labelling a part of a stretch would mislead. |
