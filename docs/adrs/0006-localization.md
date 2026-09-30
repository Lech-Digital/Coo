# 6. Localization from day one

Date: 2026-09-29

Status: Accepted

## Context

Coo launches in the same seven languages as PurePrep: **English, German, French, Spanish, Italian,
Polish and Dutch**. PurePrep shows what works on this toolchain:

- `.resx` resources (`AppResources.resx` plus one file per language);
- a `TranslateExtension` for XAML;
- a `LocalizationService` offering "System" or an explicit language, stored in `Preferences`, and
  falling back to English when the OS language is not supported;
- a `ResolveCulture` helper for code that runs before the app has applied its language. PurePrep
  needs it for a timer alarm firing in a cold process.
- a Play listing localized into all seven languages.

Adding languages late is expensive: every hard-coded string, every server message, and every
concatenated sentence has to be found again. Coo also has localization needs that PurePrep does not:

- Counts everywhere ("3 feedings today"). Polish has three plural forms, and `.resx` has no plural
  support.
- Notifications composed on the device while the app is not running (ADR 0007).
- Dictation and AI parsing in any of the seven languages (ADR 0005).
- Server-rendered content: PDF reports, invitation landing pages, the account-deletion web page.
- Times that mean something to a pediatrician, and units (`ml`/`oz`) that differ by region, not by
  language.

## Decision

### 1. Languages and selection

- The supported languages are `en`, `de`, `fr`, `es`, `it`, `pl` and `nl`. English is the source
  language and the fallback.
- The language setting is PurePrep's: **System**, meaning the OS language if supported and English
  otherwise, or an explicit choice. It is stored on the device, per device. It is not synced: two
  caregivers in one household can use different languages.
- PurePrep's `LocalizationService`, `TranslateExtension` and `ResolveCulture` are carried over,
  including the rule that background code resolves the culture itself rather than assuming the
  app has set it.
- Right-to-left languages are out of scope. Layouts still use start/end rather than left/right,
  so adding one later is not a rewrite.

### 2. Where text lives

- **The mobile UI's strings** live in `Coo.Mobile.Presentation/Resources/AppResources*.resx`. That
  is a `net10.0` project, so ViewModels can produce localized text and their tests can assert it
  without the MAUI workload. The XAML markup extension lives in the MAUI host.
- **The Domain, Contracts and API never return human-readable text to the app.** They return
  codes:
  - problem-detail `type` URIs (ADR 0002 §10);
  - sync rejection reasons (ADR 0002 §5);
  - billing refusal reasons (ADR 0004 §5);
  - AI plausibility warnings (ADR 0005 §5);
  - inbox message kinds (ADR 0007).

  The app maps each code to a string. A new language therefore never needs a server change.
- **Server-rendered content** (PDF reports, invitation and deletion pages) uses `Coo.Api`
  resources in the same seven languages. The server picks the language from an explicit `lang`
  parameter sent by the app, and falls back to `Accept-Language` and then English.
- **Wear OS** uses Android string resources (`values-de/strings.xml` and so on) in the same seven
  languages (ADR 0009).
- **Nothing a person typed is ever translated**: notes, nicknames, the AI's unparsed fragments, or
  baby names.

### 3. Completeness is a test, not a process

- **Key parity.** A test in `Coo.Mobile.Presentation.Tests` fails if any key in `AppResources.resx`
  is missing, or empty, in any of the six other languages. The same test covers `Coo.Api`'s
  resources, and a script covers the Wear OS `strings.xml` files. The runtime fallback to English
  is a safety net, not a way to ship missing translations.
- **Every code is mapped.** A test enumerates every rejection reason, refusal reason, warning code
  and inbox kind in `Coo.Contracts`, and fails if any lacks a resource key.
- **Placeholders match.** The test checks that each translation has the same format placeholders
  (`{0}`, `{1}`) as the English string.

### 4. Plurals

`.resx` cannot express plural rules, and Polish needs three plural forms. Plural strings therefore
use one key per CLDR plural category, for example `FeedingsCount.one`, `.few`, `.many` and
`.other`. A pure `PluralRules.Select(language, n)` function picks the category:

| Language | Categories used | Note |
|---|---|---|
| `pl` | one, few, many | 1 karmienie · 2–4 karmienia (except 12–14) · 5+ karmień |
| `fr` | one, other | 0 and 1 are `one` |
| `en`, `de`, `es`, `it`, `nl` | one, other | |

`PluralRules` is unit-tested against CLDR's published examples for each language. The key-parity
test (section 3) knows which categories each language requires. String concatenation for
sentences is banned. A sentence is always one resource with placeholders, because word order
differs between languages.

### 5. Times, dates and units

- **Storage.** A care entry stores when it happened as a `DateTimeOffset`: the UTC instant plus the
  offset at the place where it happened. It is displayed in the viewer's current time zone.
  Reports use the household's time zone, a household setting that defaults to the Owner's device
  time zone.
- **Formatting** follows the culture for dates and number separators. **The 12-hour or 24-hour
  clock follows the Android system setting** (`DateFormat.is24HourFormat`), not the language. An
  English-speaking parent in Amsterdam expects 14:00.
- **Units are a household setting, independent of language.** Volumes are `ml` or `oz`; later,
  for growth charts, weight and length are metric or imperial. The domain stores volumes in `ml`
  and weights in grams, and converts only for display and input. The default comes from the device
  region: the US defaults to `oz`, everywhere else to `ml`.

### 6. Voice and AI

- **Dictation language.** Dictation (ADR 0005 §2) uses the app language by default. A separate
  "dictation language" setting serves bilingual families, for example a Polish parent using the app
  in English.
- **Language hint.** The parse request carries the dictation language as a hint. The model's
  output is language-independent (entry types, numbers, structured time expressions), so no
  translation step exists.
- **Evals.** The AI eval corpus (ADR 0005 §6) has native cases for every supported language, not
  translations of the English cases.

### 7. Catching hard-coded and overflowing strings

Debug builds offer a **pseudo-locale**. It wraps every resource in markers and lengthens it by
about 40%, for example `[Ƒéééđîñĝ ~~~~]`. Any unmarked text on screen is hard-coded, and any
truncated text shows where a German compound word will break the layout. The pseudo-locale is
not compiled into Release builds.

### 8. Store presence

The Play listing ("Coo — Baby Log & Sleep"), screenshots and release notes are produced in all
seven languages, following PurePrep's `store-assets/PLAY-LISTING.md` process: screenshots captured
from each language's UI.

## Consequences

### Positive

- Every string is localizable from the first commit. There is no retrofit project later.
- The server is language-agnostic for everything the app shows. Adding an eighth language touches
  resources only, plus server resources for reports.
- Missing translations and unmapped codes fail the build, not a user.
- Correct plurals in Polish, the language most likely to expose a naive implementation.
- Units and the clock format follow what parents expect in their region, not what their language
  implies.
- The pieces PurePrep has proven are reused as they are.

### Negative

- Every user-visible string costs seven translations before merge, which slows small UI changes.
- Plural keys and the `PluralRules` function are custom code, where a library with ICU message
  format might have done. No such library is proven on this toolchain, and the function is small,
  but it is ours to maintain.
- Sending codes rather than messages means the app must be updated before a new server-side reason
  can be shown with a proper message. Until then, older apps show a generic "needs attention" text
  for codes they do not know.
- Translation quality for medical-adjacent wording ("wet", "dirty", "pumping", "latch") needs a
  native-speaker review per language. Machine translation alone is not enough for text a
  pediatrician may read in a report.
- A seven-language store listing with per-language screenshots is a real cost at every release
  that changes the UI.
