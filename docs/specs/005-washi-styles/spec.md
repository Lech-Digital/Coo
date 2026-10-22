# Spec 005 — Washi UX styles in MAUI

Date: 2026-10-01

Status: Accepted (2026-10-01: Q1–Q5 as recommended; Q5 checked visually with `docs/design/contrast-proposal.html`)

Depends on: `docs/design/design-system.md` (locked), `docs/design/washi-ux.html` (the visual
reference), ADR 0007 §4 (quiet hours), ADR 0006 (seven languages, font scaling), ADR 0001 (layers).

Delivered by: **Increment 5** (`docs/ROADMAP.md`).

## 1. Purpose

This spec turns the locked design system into the app's resources, so that every screen from
Increment 6 onwards is built from the same tokens, type styles and components:

- the day and night colour tokens, with one source of truth and a contrast test;
- Figtree, bundled, with tabular figures for every number;
- the type scale, spacing, radii and touch targets as named resources;
- the appearance modes **Auto**, **Day** and **Night**, switched at runtime without a restart;
- the icon approach, and the first icons;
- the base components: buttons, pill, card, log tile, timeline row, totals cell and night timer;
- a **style gallery** on the phone, to check against `washi-ux.html`.

**Out of scope:** real screens and navigation (Increment 6), the semantic `Warning` and
`Critical` colours (with the first screen that needs them, as the design system says), and motion
beyond what the components need.

## 2. One source of truth for the tokens

The design system names the tokens. The code has to hold exactly those values, in both modes,
without two copies drifting apart.

- **The tokens are plain C# data** in `Coo.Mobile.Presentation/Theme/WashiPalette.cs`: one
  `Palette` record with a `string` hex value per token, and two instances, `Day` and `Night`. It
  has no MAUI dependency, so it is testable on the global SDK (ADR 0001 §3).
- **The host builds the colour dictionary from that record** at runtime
  (`Coo.Mobile/Resources/Styles/PaletteDictionary.cs`), with the token names as resource keys.
  There are no hand-written `Colors.Day.xaml` and `Colors.Night.xaml` files to drift from the
  record. This refines the design system's §2, "Appearance modes", which named those two files.
- **The record is checked against the design document.** A test parses the colour tables in
  `docs/design/design-system.md` and requires every token, in both modes, to have the same hex
  value in `WashiPalette`. A colour change is therefore always a change to the document *and* the
  code, in the same commit, or the build fails.
- Everything else is plain resources in XAML: `Styles.xaml` (type styles, controls),
  `Metrics.xaml` (spacing, radii, sizes). Styles refer to colours only through
  `DynamicResource`, so a palette swap restyles the whole app without a restart.

## 3. Colour rules, tested

`tests/Coo.Mobile.Presentation.Tests` asserts, for **both** palettes:

| Test | Rule (design system §2, §9) |
|---|---|
| `Tokens_MatchDesignSystemDocument` | §2 above |
| `Contrast_TextPairs_MeetWcagAA` | Each of these pairs is at least 4.5:1: `Ink` on `Ground` and on `Surface`; `Ink2` on `Ground` and on `Surface`; `OnAccent` on `Accent`; `AccentInk` on `AccentSoft`; each event's `Ink` on its `Tint`; `VoiceInk` and `VoiceHint` on `VoiceBg`; `CheekInk` on `Cheek`. |
| `Contrast_GraphicsAndLargeText_MeetThreeToOne` | WCAG 1.4.11, for graphics that carry meaning: `Accent` on `Ground` (links, icons); each event's `Mark` on `Ground` and on `Surface` (ribbon ticks, dots, sleep-block outlines) |
| `Palette_ContainsNoPureWhite` | No token is `#FFFFFF` |
| `Night_TextColours_AreNotBlueDominant` | For every night text token, blue is not the largest channel |
| `Events_AreDistinctFromEachOtherAndFromAccent` | Every event tint differs from the others and from `AccentSoft` by a minimum colour distance (ΔE 2000 ≥ 5: clearly noticeable at a glance; tints are never the only cue, since every event also has its icon and ink, and night tints are near-black by design), so pump never reads as matcha |

The contrast ratio uses the WCAG 2.x relative-luminance formula, in a small pure function with its
own tests against published examples (black on white is 21:1).

**If a pair fails**, the fix is a change to the design system first, then to the code, in one
commit. The test does not get an exemption list.

**Two pairs fail today** (computed while writing this spec). The design system's own claims all pass;
these are pairs it did not list:

| Pair | Mode | Now | Proposed (same hue, darker) | Result |
|---|---|---|---|---|
| `FeedMark` on `Ground` | Day | `#D9936C`, 2.16:1 | `#CC6F3B` | 3.05:1 (3.35 on `Surface`) |
| `DiaperMark` on `Ground` | Day | `#CFAE3E`, 1.84:1 | `#A08528` | 3.06:1 (3.36) |
| `SleepMark` on `Ground` | Day | `#9587C2`, 2.76:1 | `#8E7FBE` | 3.03:1 (3.34) |
| `PumpMark` on `Ground` | Day | `#6FA895`, 2.33:1 | `#5A9481` | 3.00:1 (3.30) |
| `VoiceHint` on `VoiceBg` | Night | `#5A4A33`, 3.66:1 | `#4A3D2A` | 4.52:1 |

A third came from the night rule: the night `SleepInk` `#B1A3C9` was blue-dominant (blue C9 above red B1).
It is warmed to `#C0A8C0`, a dusty mauve, which raises its contrast on `SleepTint` from 6.65 to 7.13:1.
Accepted by the owner on 2026-10-01, after the comparison in `docs/design/contrast-proposal.html`.

With the event-distinctness bar at ΔE 5, night `AccentSoft` (`#2E2820`, the pill and selected-state fill) was only
ΔE 4.2 from the night feed tint. It is lifted to `#37322B`: the same warm hue, slightly lighter and greyer. That puts it
at ΔE 6.5 or more from every event tint, with `AccentInk` on it at 8.06:1. `Cheek` keeps `#2E2820`.

The day marks matter: they are the ticks of the day ribbon, which caregivers read at a glance, and
the design system says no event may be conveyed by colour alone. They must at least be *visible*
against the page. All night marks already pass (4.96–7.55:1). See Q5.

## 4. Typography

### 4.1 Figtree, bundled

- **Static TTFs** for Light 300, Regular 400, Medium 500, SemiBold 600 and Bold 700, in
  `Coo.Mobile/Resources/Fonts/`, with the SIL Open Font License beside them (`OFL.txt`).
  MAUI on Android selects weights by font file, not through a variable font's axis, so static
  files are required. If the upstream release ships only the variable font, the static instances
  are generated from it with `fontTools.varLib.instancer`. The exact command, and the SHA-256 of
  each file, go in `Resources/Fonts/README.md`.
- Each file is registered with its own alias: `FigtreeLight`, `FigtreeRegular`, `FigtreeMedium`,
  `FigtreeSemiBold`, `FigtreeBold`.
- **The fonts are verified in the built APK.** `eng/verify-apk-fonts.sh` lists the APK and fails if
  any of the five is missing. This guards against the stale `ProcessMauiFonts` stamp that once
  shipped CoreChoice without fonts. `BUILD.md` runs it after every Android build.

### 4.2 Tabular figures

Every time, duration and amount uses tabular (fixed-width) digits, so a running timer does not
jitter. MAUI has no font-feature property, so there are two candidate ways, and the gallery
decides between them on the phone:

1. **Android font features.** A handler mapping sets `TextView.FontFeatureSettings = "tnum"` on
   labels with the `Tabular` style class. That needs nothing extra, if Figtree's `tnum` feature is
   honoured on Android.
2. **A frozen tabular font.** If the first does not render tabular digits, the five weights are
   rebuilt with `tnum` frozen in as the default (`pyftfeatfreeze`), as a family
   `FigtreeTabular…`. Number styles then use that family. It works on any platform with no API.

The gallery shows a live timer, and a column of `1111` over `8888` in each weight, with both
approaches side by side. The approach that keeps the columns aligned is chosen, recorded in the
design system §3, and the other is deleted. This closes the handover's open item 2.

### 4.3 The type scale

One style per row of the design system §3: `Timer`, `Hero`, `Window`, `Title`, `Section`, `Body`,
`BodyStrong`, `Meta`, `Label` and `Tile`, with size, family (weight) and character spacing as
written there.

- `Body` is the implicit `Label` style.
- `Timer` and `Hero` set `FontAutoScalingEnabled="False"`. Everything else scales with Android's
  font size, up to 200%, without clipping. The gallery has a 200% check.
- `Label` (the eyebrow) is upper-cased **by the style's converter, per culture**, never in the
  strings, so Turkish-style casing rules can't corrupt it later.
- **Polish diacritics.** The gallery renders "Zażółć gęślą jaźń" and "ZAŻÓŁĆ GĘŚLĄ JAŹŃ" in every
  weight. Any glyph that falls back to another font fails the check. This closes the handover's
  open item 3.

## 5. Space, shape and touch

`Metrics.xaml` holds the design system §4 values as named resources:

- spacing `Space4` … `Space48`, and `Gutter` (24);
- radii `RadiusCard` (22), `RadiusTile` (20), `RadiusTotal` (16), `RadiusBadge` (12),
  `RadiusSheet` (28) and `RadiusPill`;
- `TouchTarget` (48) and `NightTouchTarget` (64), as minimum sizes in the button styles.

No elevation, and no shadows on cards (design system §4, §9).

## 6. Appearance modes

### 6.1 The rule, in `Coo.Mobile.Presentation/Theme/AppearanceRule.cs`

A pure function: `Resolve(mode, localTime, quietHours, osIsDark) → Day | Night`.

| Mode | Result |
|---|---|
| `Auto` (default) | `Night` when the local time is inside quiet hours, **or** Android is in dark mode; `Day` otherwise |
| `Day` | Always `Day` |
| `Night` | Always `Night` |

- Quiet hours default to 22:00–07:00 (ADR 0007 §4), and may span midnight. The start is
  inclusive and the end is exclusive.
- `NextChange(mode, localTime, quietHours)` returns the next local time at which the result can
  change on its own, so the app schedules one timer rather than polling.

### 6.2 Switching (`ThemeController`, Presentation; applied by the host)

- The `ThemeController` re-evaluates the rule on start, on resume, when Android's dark mode
  changes, when the setting changes, and at `NextChange`.
- When the result changes, the host replaces the merged colour dictionary, and sets the Android
  status and navigation bar colours to `Ground`, with light or dark bar icons to match.
- The palette never changes mid-gesture: a swap that falls due while a sheet is open waits until
  the sheet closes.

### 6.3 Where the setting lives

The appearance mode and the quiet hours are **per device, not per household**. One parent may
want night mode at 21:00, and the other not. They are stored in the phone's `app_state` table
(Spec 004 §4.4) through a new port, `IDevicePreferences`, and they are never synced.

## 7. Icons

- **Icons are vector paths, not images.** Each icon is SVG path data on the 24-unit grid, kept in
  `Coo.Mobile.Presentation/Theme/IconGeometry.cs` (a string per icon, no MAUI dependency).
- They are rendered by one `Icon` control in the host: a MAUI `Path` with
  `StrokeThickness` 1.7, round caps and joins, no fill, and its stroke bound to a colour resource.
- This keeps icons sharp at every size and density, and tinted by the same `DynamicResource`
  colours as text, so a palette swap recolours them. It needs no extra package. A rasterised
  `MauiImage` with a tint behaviour would need the CommunityToolkit, and could blur at large sizes.
- **The first set:**
  - care events: feed (bottle), diaper (drop), sleep (moon), pump;
  - mic;
  - navigation: today, log, insights, family;
  - chevron;
  - left and right (for the breast-side buttons).

  The paths are drawn from `washi-ux.html`, which already defines most of them as inline SVG.
- A test checks that every path in `IconGeometry` parses, and stays inside the 24 × 24 box.

## 8. Base components

These are built as `ContentView`s or styles in `Coo.Mobile/Components/`, from the design system §6,
using only tokens and metrics:

| Component | In this increment |
|---|---|
| Primary and ghost buttons | Yes. Pill, 48 dp, one primary per screen. |
| Pill | Yes |
| Card | Yes (`Surface`, `RadiusCard`, a `Line` border, no shadow) |
| Log tile | Yes, for all four kinds: 96 dp, event `Tint`, icon, name, a "last at" line. The breast tile shows the side ("12:40 · L"). |
| Timeline row | Yes |
| Totals cell | Yes |
| Night timer | Yes: `Timer` digits ticking once a second, with `Stop` as the only accent |
| Baby chip, status hero, nap window card, voice bar, day ribbon, bottom navigation | No: with the screens that use them (Increment 6) |

Components take plain values and the event kind. They hold no logic and call no use cases. View
models come with the screens.

## 9. The style gallery

The style gallery is a page in `Coo.Mobile/DebugScreen/`, so it is in Debug builds and
`-p:CooDebugScreen=true` builds only (Spec 004 §8). A button on the debug screen opens it. It has:

1. **Appearance:** Auto, Day and Night buttons. A swap applies instantly, with no restart.
2. **Colour:** every token as a swatch with its name, hex value and, for each text pair, its contrast
   ratio.
3. **Type:** each style with sample text; the tabular-figures comparison (§4.2); the Polish
   pangram in every weight (§4.3); a 200% preview.
4. **Icons:** the whole set, in `Ink`, `Ink2` and each event `Ink`, at 24 and 48 dp.
5. **Components:** each one from §8, in both palettes. The four log tiles, a few timeline rows and
   a running night timer appear as they will on Today.

You compare the gallery with `docs/design/washi-ux.html` opened next to it.

## 10. Tests

| Project | Tests |
|---|---|
| `Coo.Mobile.Presentation.Tests` (new) | §3 colour rules; the contrast function; `AppearanceRule` (each mode; quiet hours across midnight; the exact boundaries 22:00 and 07:00; `NextChange`); icon paths parse and fit |
| `Coo.Mobile.Infrastructure.Tests` | `IDevicePreferences` round trip, with defaults when unset |
| `Coo.Architecture.Tests` | `Coo.Mobile.Presentation` does not reference MAUI (it stays `net10.0` and testable) |
| On the phone | The gallery (§9), and `eng/verify-apk-fonts.sh` on the APK |

## 11. Increment 5 — deliverables

| # | Deliverable | How you check it |
|---|---|---|
| 1 | Tokens, contrast and appearance rules | `dotnet test`, all green, including the document-parity test |
| 2 | Fonts in the APK | `eng/verify-apk-fonts.sh` passes |
| 3 | The gallery on your phone matches `washi-ux.html` | Side by side, in Day and in Night |
| 4 | Tabular figures decided | The gallery's column test; the choice recorded in the design system |
| 5 | Polish diacritics render in Figtree | The gallery's pangram, every weight |
| 6 | Auto mode follows dark mode and quiet hours | Switch Android to dark mode: the app turns night without a restart. Set quiet hours to start a minute from now, and wait. |
| 7 | CI green | GitHub Actions |

## 12. Decisions for the owner

| # | Question | Recommendation |
|---|---|---|
| Q1 | Tokens as C# data, with the colour dictionary built from them, and a test that keeps them identical to the design document, instead of two hand-written XAML colour files | **Yes.** One source in code, checked against the document, so they can never drift. It refines the design system's §2. |
| Q2 | Icons as vector paths drawn by one `Icon` control, instead of rasterised images with a tint behaviour | **Yes.** Sharp at every size, recoloured by the palette, and no extra package. |
| Q3 | Tabular figures: try Android's `tnum` font feature first, fall back to a frozen tabular font, and let the gallery decide | **Yes.** It is the cheapest way to settle the open item on the real phone. |
| Q4 | The appearance mode and quiet hours are per device, not synced | **Yes.** Night preferences differ between parents. |
| Q5 | Darken the four day `Mark` colours and the night `VoiceHint` to the values in §3, and update the locked design system | **Yes.** The day ribbon is barely visible otherwise (1.8–2.8:1). The change keeps each hue, and is subtle except for feed, which becomes a little more saturated. You can compare the old and new swatches in the gallery before release. |
