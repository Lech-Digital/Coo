# Mochi design system (code name Coo) — "Washi UX"

Status: Locked (2026-09-29; day `Mark`s and night `VoiceHint` darkened for contrast, and night `SleepInk` warmed
from `#B1A3C9` so night text is never blue-dominant, and night `AccentSoft` lifted from `#2E2820` so a selected
state never looks like a feed tile, on 2026-10-01, Spec 005). Visual reference: `docs/design/washi-ux.html`.
Superseded explorations: `docs/design/style-directions.html`.

Washi UX combines three things:

- the **Washi & Matcha** base: rice-paper ground, dark ink text, a matcha accent;
- its **candle-lit amber night mode**;
- **soft pastel colour coding per care event**, taken from the Mochi exploration.

**Figtree** is the only typeface. The product promise it serves is "reducing friction at 3 AM":
calm by day, dim and warm by night, and readable at a glance with bleary eyes.

The token names below are the resource keys the MAUI app uses (`Coo.Mobile/Resources/Styles`).
They are the contract between this document and the XAML.

## 1. Principles

1. **Glanceable first.** Every care event has a colour **and** an icon, always together. Colour
   gives the immediate cue, and the icon makes it unambiguous for colour-blind caregivers.
2. **Night is a mode, not a theme.** After dark the app gets dimmer, warmer and simpler:
   - it contains no white and no blue-dominant text;
   - its touch targets are larger;
   - it shows only what a half-awake parent needs.
3. **Calm, with one friend.** Japandi restraint: lots of space, hairline borders and one accent.
   The one exception is Mochi, the bunny-shaped mochi of the icon, shown small where it tells the
   baby's state: awake (ears up) or asleep (ears down). Nowhere else, never decoration.
4. **Numbers are the heroes.** Times and durations are the largest things on screen. They use
   Figtree Light with tabular figures.

## 2. Colour

### Base

| Token | Day | Night | Use |
|---|---|---|---|
| `Ground` | `#EEEEE6` | `#121511` | Page background |
| `Surface` | `#FAFAF5` | `#1B1F19` | Cards, bottom navigation, chips |
| `Line` | `#D8DACB` | `#2C3228` | Hairline borders and separators |
| `Ink` | `#262A22` | `#D9E3C8` | Primary text and icons |
| `Ink2` | `#62685A` | `#97A486` | Secondary text, metadata |
| `Accent` | `#3F6B4A` (matcha) | `#9DBB7E` (light matcha) | Primary actions, links |
| `AccentSoft` | `#E3EBD8` | `#283022` | Pills, selected states |
| `AccentInk` | `#355229` | `#CFE0B6` | Text on `AccentSoft`; link text |
| `OnAccent` | `#FAFAF5` | `#121511` | Text on `Accent` |
| `Cheek` | `#DCE6D2` | `#2A2A22` | Baby avatar fill |
| `CheekInk` | `#355229` | `#D9E3C8` | Baby avatar initial |
| `VoiceBg` / `VoiceInk` / `VoiceHint` | `#262A22` / `#EEEEE6` / `#B4B8AA` | `#9DBB7E` / `#121511` / `#36412C` | The "Tell Mochi" bar |

### Care events

Each event has three tokens:

- `Tint`: the fill of tiles, badges and totals;
- `Mark`: dots, ribbon ticks and bars;
- `Ink`: text and icons on the tint.

| Event | Day tint / mark / ink | Night tint / mark / ink |
|---|---|---|
| **Feed** (peach) | `#F5DCCB` / `#CC6F3B` / `#7A3F20` | `#2E2219` / `#C98A63` / `#D9A07C` |
| **Diaper** (yuzu) | `#F3E6B3` / `#A08528` / `#6A5410` | `#2C2716` / `#BFA34E` / `#CDB266` |
| **Sleep** (lavender) | `#E2DCEF` / `#8E7FBE` / `#473A78` | `#25222E` / `#9A8AB5` / `#C0A8C0` |
| **Pump** (sky) | `#DCE6EE` / `#5B84A0` / `#2B4A5F` | `#1D2326` / `#7C939B` / `#A3B5B5` |

Keys follow the pattern `FeedTint`, `FeedMark`, `FeedInk`, and so on.

Colour rules:

- **Pump is sky, never matcha.** Matcha is the brand and the action colour (the Mochi icon is matcha,
  ADR 0011), so an event colour is never mistaken for a button. Pump was mint until 1.11; at night it
  is a grey slate, so no text turns blue.
- **Event colours are for events only.** They are never used for status, errors or branding.
- **Semantic states are separate from events and from the accent.** They will be defined with the
  first screen that needs them: `Warning` (implausible AI values, "reminders may be late") and
  `Critical` (sync rejected). They must remain distinguishable in lightness, not only in hue.
- **Contrast.** Text and background pairs meet WCAG AA: 4.5:1 for body text, and 3:1 for text of
  24 px or more and for icons. The event `Ink` on its `Tint`, `Ink`/`Ink2` on `Ground` and
  `Surface`, and `OnAccent` on `Accent` were chosen for this in both modes. Meaningful graphics (the
  event `Mark`s on `Ground` and `Surface`: ribbon ticks, dots, outlines) meet 3:1 (WCAG 1.4.11). A contrast test in
  `Coo.Mobile.Presentation.Tests` asserts these pairs from the token file, so a colour tweak cannot
  quietly break them.

### Appearance modes

| Setting | Behaviour |
|---|---|
| **Auto** (default) | Night palette during the device's quiet hours (ADR 0007 §4, default 22:00–07:00) **or** whenever Android is in dark mode; day palette otherwise |
| **Day** | Always the day palette |
| **Night** | Always the night palette |

The palette is swapped by replacing the merged colour dictionary: `Colors.Day.xaml` or
`Colors.Night.xaml`. `AppThemeBinding` is not used, because the switch depends on quiet hours as
well as the OS theme. All styles reference colours with `DynamicResource`, so a swap needs no
restart. The Android status and navigation bars follow the palette.

## 3. Typography

**Figtree** only, bundled as static TTF files: Light 300, Regular 400, Medium 500, SemiBold 600 and
Bold 700. It is licensed under the SIL Open Font License. After every build, verify that the fonts
are in the APK (CoreChoice's `BUILD.md` records a stale `ProcessMauiFonts` stamp that shipped an
app with no fonts).

| Style key | Size (sp) | Weight | Tracking | Use |
|---|---|---|---|---|
| `Timer` | 88 | Light | −3 | Running feed or sleep timer (night) |
| `Hero` | 64 | Light | −2.5 | "Awake for 1 h 20 m" |
| `Window` | 32 | Medium | −0.8 | Nap window "14:10 – 14:40" |
| `Title` | 28 | SemiBold | −0.6 | Screen titles ("Tuesday") |
| `Section` | 20 | SemiBold | −0.3 | Section headings ("Today") |
| `Body` | 15 | Regular | 0 | Default text |
| `BodyStrong` | 15 | Medium | 0 | Timeline entry titles |
| `Meta` | 13 | Regular | 0 | Who logged it, secondary facts |
| `Label` | 12 | SemiBold | +1.2, caps | Eyebrows ("NEXT NAP WINDOW") |
| `Tile` | 14 | SemiBold | 0 | Log tile captions |

Type rules:

- **Tabular figures** for every time, duration and amount, so timers do not jitter. Confirmed on a
  Pixel 10 on 2026-10-01: Figtree's `tnum` feature, applied with Android's `fontFeatureSettings`, aligns
  every weight. Plain Figtree digits are proportional and do jitter (Spec 005 §4.2).
- **Tracking** values are px at each style's size. In MAUI they are converted to `CharacterSpacing`
  units (px / size / 0.0624), because Android applies `CharacterSpacing` in sixteenths of an em.
- **Units in the hero** are smaller and lighter than the digits: 1<small>h</small> 20<small>m</small>.
- **Font scaling.** The app honours Android font scaling, and layouts wrap rather than clip up to
  200%. `Timer` and `Hero` are exempt from scaling (`FontAutoScalingEnabled="False"`): they are
  already large, and scaling them would overflow the screen.
- **Seven languages** (ADR 0006). German and Dutch strings run about 30–40% longer than English.
  Tiles, pills and buttons must allow a second line or shrink labels; they must never truncate. The
  pseudo-locale (ADR 0006 §7) is the check.

## 4. Space, shape, elevation

- **Spacing scale:** 4, 8, 12, 16, 20, 24, 32, 48. The screen gutter is 24.
- **Radii:**

  | Token | Value | Use |
  |---|---|---|
  | `RadiusCard` | 22 | Cards |
  | `RadiusTile` | 20 | Log tiles |
  | `RadiusTotal` | 16 | Totals |
  | `RadiusBadge` | 12 | Badges |
  | `RadiusSheet` | 28 | Bottom sheets |
  | `RadiusPill` | full | Pills, chips, voice bar, buttons |

- **Elevation:** none. Depth comes from `Surface` on `Ground`, plus a 1 px `Line` border where two
  surfaces meet. Shadows only on overlays: bottom sheets and dialogs.
- **Touch targets:** at least 48 dp everywhere (the Android guideline), and at least 64 dp for the
  primary actions of night mode.

## 5. Iconography

- A custom set of line icons on a 24 grid: 1.7 stroke, rounded caps and joins, no fills. The
  first icons, from the mockup:
  - care events: feed (bottle), diaper (drop), sleep (moon), pump;
  - voice (mic);
  - navigation: today, log, insights, family;
  - chevrons.
- Icons are tinted with the colour of their context: the event `Ink` on tiles and badges, `Ink2` in
  inactive navigation. They are never multicolour.
- Icons come from SVG source files in `Coo.Mobile/Resources/Images`. How they are rendered and
  tinted (`MauiImage` with a tint behaviour, or an icon font) is decided in the XAML spec.

## 6. Components

These are the components in the reference mockup. Each gets a `ContentView` or style in the XAML
spec.

| Component | Anatomy and rules |
|---|---|
| **Baby chip** | Avatar with the initial on `Cheek`, the name and age. It opens the baby switcher (twins, several babies). |
| **Status hero** | `Label` eyebrow, `Hero` number, and a `Body`/`Ink2` sentence of context. |
| **Nap window card** | `Surface` card. A `Sleep`-inked eyebrow, a confidence pill, `Window` digits with a `SleepMark` dot, and a one-line explanation (ADR 0008). Low confidence shows the age-typical range instead of times. |
| **Log tile** | A 96 dp square in the event `Tint`. It holds the icon, the name, and a "last at" line (for example "12:40 · L"). A tap logs or opens the quick form; a long press opens details. |
| **Voice bar** | A full-width pill in `VoiceBg`: mic icon, "Tell Mochi", and a rotating example hint. |
| **Timeline row** | Time (tabular), event badge (34 dp, `Tint` with an `Ink` icon), title in `BodyStrong`, and who logged it in `Meta`. Hairline separators. |
| **Day ribbon** | A 24-hour rhythm band (Spec 009b). Sleep fills the band in `SleepMark`; feeds are `FeedMark` pills of their real length above it; diapers are `DiaperMark` dots and pumps `PumpMark` squares below it. Quiet hours are a `SleepTint` wash, today has an `Accent` "now" line, and labels mark every 3 hours. A tap opens the zoomed day: 6 hours per screen around the tap, swiped, with every item labelled. |
| **Totals** | Four `Tint` cells, each with a number and a unit, in the event `Ink`. |
| **Night timer** | `Label` eyebrow with an event dot, `Timer` digits, and the start time. Below it, two 64 dp actions: `Stop` (the only accent) and a secondary action. |
| **Night quick row** | 68 dp buttons on dark event tints, with no borders. |
| **Buttons** | Primary: `Accent` fill, `OnAccent` text, pill, 48 dp. Ghost: transparent with a `Line` border, `Ink` text. There is one primary action per screen. |
| **Pill** | `AccentSoft` fill, `AccentInk` text, `Label`-sized. For status, never for actions. |
| **Bottom navigation** | Four tabs: Today, Log, Insights, Family. `Surface` background with a `Line` border on top. The active tab uses `Ink` and Bold; inactive tabs use `Ink2`. |

Components still to be designed with their screens:

- the AI confirmation card (ADR 0005), including its duplicate and implausible-value states;
- the "Needs attention" list (ADR 0002 §11);
- the Premium screen (ADR 0004);
- invitations and onboarding;
- settings;
- the watch tile (ADR 0009), which uses the night palette on the always-dark watch face.

## 7. Motion

Motion is minimal and purposeful:

- a 150–200 ms fade or slide for sheets;
- a gentle confirmation when an entry is saved;
- a 1-second tick for timers.

Night mode has no decorative motion. The app honours Android's "remove animations" setting,
following CoreChoice's `IReducedMotionPreference` pattern.

## 8. Voice and copy

- Write plainly and calmly, with no exclamation marks. This reads well at 3 AM and at noon.
- For predictions, use "suggested" or "likely", never "should" (ADR 0008 §8).
- Name things the way parents do: "Feed", "Nap", "Wet". Never "event" or "entry type".
- Times follow the device's 12- or 24-hour setting. Units follow the household setting
  (ADR 0006 §5).
- Every string is translated in all seven languages before release (ADR 0006).

## 9. Don'ts

- No serif or display fonts, no second typeface.
- No pure white (`#FFFFFF`) anywhere, and no white or blue-dominant text at night.
- No event colour used for anything other than its event. No event conveyed by colour alone.
- No shadows on cards, and no gradients.
- No emoji in the UI.
- No mascots or baby illustrations in core screens.
