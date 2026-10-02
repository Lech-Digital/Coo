# Coo — Delivery roadmap

Work is incremental. Each increment has **one short spec**, agreed before its code, and a
**deliverable you can test yourself**. The next spec is written only once the previous increment
has been tested, so it can use what we learned.

This list is the intended order, not a contract. After each increment we revisit the next one or
two.

| # | Increment | Spec | Deliverable you can test | Status |
|---|---|---|---|---|
| 1 | **Solution skeleton** | 002 (accepted) | The solution builds with zero warnings. The architecture tests pass, and deliberate violations fail. `/health` answers. A placeholder app installs and opens on your phone. | Done (phone install deferred until there is something to click through) |
| 2 | **Care-log domain** | 001 (accepted) | The whole domain from Spec 001: the core, the four kinds, rules, plausibility, duplicates, the time resolver, HLC and the catalogue tests. **Test:** `dotnet test` runs the Spec 001 §14 list, all green. | Done |
| 3 | **Entry-details codecs** | 003 | `Coo.Codecs`: a DTO and codec per kind, `"v"` versioning, the write gate (encode → decode → compare), merging back unknown properties, golden fixtures, round-trip property tests (FsCheck), malformed-input tests, and the first ADR 0010 completeness tests. **Test:** `dotnet test`, all green. | Done |
| 4 | **Local storage and the write path** | 004 | EF Core SQLite on the phone: the `care_entries` table with the G3 constraints, repositories through the codecs, the read guard, the local integrity scan, the outbox table (filled, not sent yet), and data-migration runner v1. **Test:** real-SQLite tests; a debug screen on the phone adds and lists entries that survive an app restart. | Done |
| 5 | **Look and feel: styles** | 005 | The Washi UX tokens as MAUI resources, bundled Figtree, Auto/Day/Night switching, and the token-contrast test. **Test:** a style-gallery page on the phone, checked against `washi-ux.html`, which also closes the handover's open checks on tabular figures and Polish diacritics. | Done |
| 6 | **Today and quick logging (offline)** | 006 | The Today screen and the log tiles for the four kinds: breast timer with side switching, bottle, diaper, sleep, pump. The timeline and the day ribbon. Localized in seven languages. **Test:** you use it for real, offline, for a day. | Done |
| 7 | **Edit, delete, undo, Needs attention** | 007 | Editing forms, undo, the duplicate check with "Keep both", and the plausibility warnings in the UI. **Test:** the scenarios in the spec, by hand, on the phone. | Done |
| 8 | **Backend and deployment** | 008 | `Coo.Api` with PostgreSQL (G3 constraints, `entry_kind_versions`), Docker, deployed to `coldstart-prod` behind Caddy, and the server integrity scan. **Test:** `https://api.coo.lechdigital.nl/health`. | Done |
| 9 | **Settings** | 009 (accepted) | A third tab with everything that works offline: the baby's profile, units, language, theme and quiet hours, about. Sign-in, Family and Premium add their sections to it later. **Test:** change each setting on the phone, and see Today and Log follow. | — |
| 9b | **The Log day chart** | 009b (accepted) | The ribbon becomes a rhythm band (sleep filled, feeds of their real length, now, quiet hours), and a tap opens the day zoomed to 6 hours, with labels and a way into each entry. **Test:** the deliverables in the spec, on the phone, day and night. | Done |
| 10a | **Spike: Sign in with Google** | 010a | Credential Manager returns a Google ID token with our nonce in a trimmed Release build, or the fallback is chosen. **Test:** the debug-screen probe on the phone. Throwaway. | — |
| 10 | **Sign-in and sessions** | 010 | Sign in with Google, sessions per device, the server takes over the phone's household, the same account on a second phone, sign-out, account deletion (in the app and on the web). **Test:** sign in on two phones with one account; the server shows one household and two device sessions. | — |
| 11 | **Sync** | 011 | Push and pull, HLC, tombstones, the SignalR doorbell, `membership-revoked`. **Test:** two phones, offline edits, both converge, and nothing is lost. | — |
| 11b | **Members and invitations** | 011b | Invite by link, join with your own Google account as a Parent, bring your entries over or keep them separate, remove a member (ADR 0003 §6–§7). **Test:** the owner and his girlfriend, each with their own account, log the same baby and see each other's entries. | — |
| 11c | **Washi forms** | 011c | The edit and Settings pages in the Washi style of Today and Log: Washi fields instead of Android's underlined boxes, the entry's kind colour and badge, grouped cards. Shown as mockups first. **Test:** side by side with Today and Log, day and night. | — |
| 12 | **Billing and entitlements** | 012 | Play Billing, the trial, RTDN, the reconciler, `FeatureGate`. **Test:** Play license testers. | — |
| 13 | **Reminders and notifications** | 013 | The local reminders from ADR 0007, and WorkManager sync. | — |
| 14 | **Sleep predictor** | 014 | ADR 0008, with the backtest harness gating it. | — |
| 15 | **AI logging** | 015 | Voice and text to proposals, the confirmation card, pending notes, evals. | — |
| 16 | **Reports (PDF)** | 016 | The pediatrician report, designed first. | — |
| 17 | **Wear OS companion** | 017 | ADR 0009. | — |
| 18 | **Release** | 018 | Signing, the Play listing in seven languages, the privacy policy, closed testing. | — |

Increments 1–7 and 9 need no server and no accounts. By the end of increment 7, the app is a usable
**single-phone baby log**. Everything after that adds the family, Premium and the smart features.

## Owner setup tasks that take calendar time

These are listed in `HANDOVER-2026-09-30.md`. They are needed from increment 8 onwards:

- the Play Console entry;
- the Google Cloud project and its OAuth clients;
- DNS;
- the Gemini key.
