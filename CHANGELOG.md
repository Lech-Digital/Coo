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
