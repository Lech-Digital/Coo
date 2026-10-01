# Spec 009 — Settings

Date: 2026-10-01

Status: Accepted (2026-10-01: Q1–Q5 answered in the session; Q6–Q8 as recommended; Q9 and §3.7 added during planning)

Depends on: Spec 001 (`BabyProfile`, `HouseholdSettings`, `HiddenKinds`), Spec 004 (storage, outbox, `app_state`),
Spec 005 (appearance modes, quiet hours, `ThemeController`), Spec 006 (Today, Log, the tab bar), ADR 0006 §1 and §5
(language selection, units, clock), ADR 0010 §6 (hidden kinds).

Delivered by: **Increment 9** (`docs/ROADMAP.md`). The app is version **0.9.0** from this increment.

## 1. Purpose

Today Coo decides everything that is not a care entry by itself: the units come from the region, the language from
Android, the appearance from Auto. This increment gives the parent one place to change all of it, offline, and
makes Today and Log follow each change.

**Out of scope:**

| Not in this increment | Where |
|---|---|
| Sign-in, Family, Premium | Increments 10–12; each adds its own section to this tab |
| Reminders and notifications (quiet hours are stored and used for Auto, but nothing is silenced yet) | Increment 13 |
| The household time zone (it stays the device's, as set at first launch) | When reports need it (Increment 16) |
| A second baby, twins | Later; the screen shows the household's one baby |
| Weight and length units | With growth charts |

## 2. Where it lives

- **A third tab, Settings,** after Today and Log (Q1). With Insights and Family later, the bar has five tabs, the
  Material maximum. The design system's bottom navigation entry is updated to list it.
- One scrolling page, Washi style: section headers in `Label` style, rows on `Surface` cards, chevrons on rows that
  open a page. Rows that toggle or pick in place do so without a page.

## 3. The sections, in order

### 3.1 Baby

- One row: the name, the age ("7 weeks", or "Due in 3 weeks"), opening the **baby page**.
- **The baby page** edits, in one form with one **Save**:
  - **Name** (required, at most 40 characters);
  - **Nicknames** (optional, up to 5): "Other names you call them". They are kept for voice logging (ADR 0005) and
    shown nowhere else yet;
  - **Birth date**, for a born baby; **Due date**, optional, for corrected age. An expected baby shows only the due
    date; "The baby has arrived" stays on Today, as in Spec 006 §3a.
- Errors are the Domain's codes shown inline: `baby.name-invalid`, `baby.nickname-duplicate`, `baby.birth-in-future`,
  `baby.due-date-out-of-range`.
- **Household-wide**: synced with the family later.

### 3.2 What you log

- A switch per kind, in the catalogue order: **Feeding, Diaper, Sleep, Pumping** (Q2).
- A hidden kind loses its **quick-log tile** on Today, its **totals** on Today and Log, and its "Earlier…" choice.
  **Its existing entries stay** in the timelines, so nothing logged disappears.
- **A running timer of a hidden kind keeps its card** until it is stopped, so a timer cannot be orphaned.
- **The last visible kind cannot be switched off**: its switch is disabled, and the use case refuses it with
  `settings.all-kinds-hidden`.
- **Unknown kind codes** in `HiddenKinds`, from a newer client, are kept on save (ADR 0010 §6).
- **Household-wide.**

### 3.3 Units

- **Volume:** a segmented control, **ml | oz**. Household-wide.
- Amounts are stored in ml, always (ADR 0006 §5). The unit only changes display and input, so switching back and
  forth never changes a stored value.
- Below it, a hint, no setting: "Time format follows your phone's settings." (Q6.)

### 3.4 Appearance

- **Theme:** a segmented control, **Auto | Day | Night**. Auto's line: "Night colours during quiet hours, or when
  your phone is in dark mode."
- **Quiet hours:** a row with the start and the end (time pickers), with the line "Night colours and quieter
  notifications during these hours." Equal start and end means none.
- **Per device**, never synced (Spec 005 §6.3). Applied at once through `ThemeController.SetAsync`.

### 3.5 Language

- A row showing the current choice, opening a list: **System (English)**, where the name in brackets is what System
  resolves to, then the seven languages, each named in its own language: English, Deutsch, Français, Español,
  Italiano, Polski, Nederlands.
- **Per device** (ADR 0006 §1). Stored in `app_state` as `language` = `system` or a tag (`pl`).
- **The change applies at once** (Q7): the culture is set and the pages are rebuilt (`App.ReloadForLanguage`), and
  the app returns to the Settings tab. Nothing is restarted, and no data or palette changes.
- **On a cold start** the stored language is applied before the first page is built. `Strings.Culture` follows the
  stored choice, so background code resolves it the same way (ADR 0006 §1).
- An unreadable value falls back to System.

### 3.6 About

- **Version:** "Coo 0.9.0 (e03dbdf)", from the assembly's `InformationalVersion`, the way the API reports it.
- **Open-source licences:** a page listing what the app ships, with each licence's full text, bundled as app assets:
  Figtree (SIL OFL 1.1), .NET MAUI, CommunityToolkit.Mvvm, Microsoft.Data.Sqlite (MIT), SQLitePCLRaw (Apache 2.0),
  SQLite (public domain). A test fails when a package reference is added to the mobile projects without an entry.
- **Send feedback:** opens the mail app to **andrzej@lechdigital.nl** (Q4), with the subject "Coo feedback" and the
  version and Android version in the body. Without a mail app, the address is copied and a toast says so.
- **Privacy policy:** opens **https://coo.lechdigital.nl/privacy** in the browser (Q3). The page is written in
  Increment 18; until then the link does not resolve, which is accepted for internal builds. The DNS record for
  `coo.lechdigital.nl` is already in the owner's setup list.

### 3.7 Night ↔ day from Today (Q9)

- **A sun or moon button on Today**, in both palettes: a moon in the day header, a sun next to the night title. It
  switches to the other palette at once.
- **In Auto, the switch holds until the next change:** the next quiet-hours start or end (day at 03:00 lasts until
  22:00; night at 15:00 lasts until 07:00), then Auto decides again. With no quiet hours, it holds for 12 hours. It is
  stored per device, so a restart at 3 AM keeps it.
- **With Day or Night fixed in Settings,** the button changes that setting to the other one.
- Choosing a theme in Settings, or changing the quiet hours, ends any temporary switch.

## 4. How changes reach Today and Log

- **Units, hidden kinds and the baby:** Today and Log already reload the household and the baby when they appear
  (Spec 006), so a tab switch shows the change. No event is added for these.
- **Theme, quiet hours and the Today switch:** live, through `ThemeController`, as today.
- **Language:** the rebuild in §3.5.

## 5. Storage

- **No migration.** `baby_profiles` already holds the name, nicknames and dates; `household_settings` the unit and
  hidden kinds; `app_state` the per-device values.
- Every household-wide change goes through the existing store: one save, with its outbox row, in one transaction,
  with a new HLC version (Spec 004).
- `app_state` gains the keys `language`, `appearance.override_palette` and `appearance.override_until`.

## 6. Where it lives

| Concern | Project |
|---|---|
| `UpdateBabyProfile`: name, nicknames, birth and due date, in one save | `Coo.Mobile.Application/Households` |
| `ChangeVolumeUnit`, `SetHiddenKinds` (with `settings.all-kinds-hidden`) | `Coo.Mobile.Application/Households` |
| `IDevicePreferences.GetLanguageAsync` / `SetLanguageAsync`; `LanguagePreference` (System or a tag) | `Coo.Mobile.Application/Preferences` |
| Their SQLite implementation in `app_state` | `Coo.Mobile.Infrastructure/Persistence` |
| `SettingsViewModel`, `BabyProfileViewModel`, `LicencesViewModel`; the language list; the version line | `Coo.Mobile.Presentation/Settings/` |
| `Strings.Culture` honours the stored language | `Coo.Mobile.Presentation/Localization` |
| Hidden kinds filtered from tiles, totals and "Earlier…" | `Coo.Mobile.Presentation/Today`, `/Log` |
| `IAppVersion` (version, commit; not MAUI's `IAppInfo`), `IExternalLauncher` (mail, browser, clipboard) ports | `Coo.Mobile.Presentation`, implemented in `Coo.Mobile.Platform` |
| The Settings, baby and licences pages; the third tab | `Coo.Mobile` |
| Every new string, in seven languages, release-ready | `eng/strings/strings.py` |

## 7. Tests

| Where | What |
|---|---|
| Application | `UpdateBabyProfile` saves all fields with one new version and one outbox row; a bad name or due date is refused and nothing is saved. `SetHiddenKinds` refuses hiding every kind; unknown codes are kept. `ChangeVolumeUnit` writes an outbox row; setting the same unit again writes nothing. |
| Infrastructure | The language round-trips; a missing or unreadable value reads as System. |
| Presentation | ml ↔ oz is display only: a 120 ml bottle shows 4 oz and is still 120 ml after switching back. A hidden kind's tile and totals are gone, its entries remain, and its running timer stays. The language list shows System with the resolved name. `Strings.Culture` follows the stored language. The version line. |
| Architecture | The new ports follow ADR 0001's rules (no MAUI in Application or Presentation). |
| Licences | Every `PackageReference` in the mobile projects has a licences entry. |
| Localization | Every new key exists in all seven languages, with matching placeholders (the parity test). |
| On the phone | The deliverables below |

## 8. Increment 9 — deliverables

| # | Deliverable | How you check it |
|---|---|---|
| 1 | The Settings tab | It is the third tab, in day and night |
| 2 | Edit the baby | Change the name and the due date; Today's header follows. A name of spaces is refused. |
| 3 | Hide a kind | Hide Pumping: its tile and totals go, old pump entries stay in the timeline. The last kind cannot be hidden. |
| 4 | Units | Switch to oz: the bottle sheet and the totals show oz; switch back, and 120 ml is still 120 ml |
| 5 | Theme and quiet hours | Night now, then Auto with quiet hours around the current time: the palette follows at once. During quiet hours, the sun on Today brings day colours back, and they stay after a forced stop. |
| 6 | Language | Pick Polski: the app is in Polish at once, and still after a forced stop. System brings back Android's language. |
| 7 | About | The version shows 0.9.0 and the commit; the licences open; Send feedback opens the mail app |
| 8 | CI green | GitHub Actions |

## 9. Decisions for the owner

| # | Question | Answer or recommendation |
|---|---|---|
| Q1 | Where does Settings live? | **Answered: a third tab.** Five tabs in the end, with Insights and Family. |
| Q2 | Hide kinds the household does not use? | **Answered: yes,** in "What you log". |
| Q3 | What goes in About? | **Answered:** version and commit, licences, Send feedback, and the privacy link now, before the page exists. |
| Q4 | The feedback address | **Answered: andrzej@lechdigital.nl.** |
| Q5 | The version | **Answered: 0.9.0 now, 1.0.0 at the public launch** (Increment 18). |
| Q6 | No 12/24-hour setting; a hint that it follows the phone | **Yes.** ADR 0006 §5 decided this; a second switch that can disagree with Android's is a source of confusion. |
| Q7 | Language applies at once, by rebuilding the pages, without a restart | **Yes.** `App.ReloadForLanguage` already does it; a restart prompt is friction for a setting that is changed once. |
| Q8 | A running timer of a hidden kind keeps its card until stopped | **Yes.** Hiding a kind must never strand a running timer or lose its entry. |
| Q9 | Night ↔ day from Today (owner, during planning) | **Answered: until the next change** (§3.7); with a fixed theme, it changes the setting. |
