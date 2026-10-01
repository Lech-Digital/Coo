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
| 4 | **Local storage and the write path** | 004 | EF Core SQLite on the phone: the `care_entries` table with the G3 constraints, repositories through the codecs, the read guard, the local integrity scan, the outbox table (filled, not sent yet), and data-migration runner v1. **Test:** real-SQLite tests; a debug screen on the phone adds and lists entries that survive an app restart. | — |
| 5 | **Look and feel: styles** | 005 | The Washi UX tokens as MAUI resources, bundled Figtree, Auto/Day/Night switching, and the token-contrast test. **Test:** a style-gallery page on the phone, checked against `washi-ux.html`, which also closes the handover's open checks on tabular figures and Polish diacritics. | — |
| 6 | **Today and quick logging (offline)** | 006 | The Today screen and the log tiles for the four kinds: breast timer with side switching, bottle, diaper, sleep, pump. The timeline and the day ribbon. Localized in seven languages. **Test:** you use it for real, offline, for a day. | — |
| 7 | **Edit, delete, undo, Needs attention** | 007 | Editing forms, undo, the duplicate check with "Keep both", and the plausibility warnings in the UI. **Test:** the scenarios in the spec, by hand, on the phone. | — |
| 8 | **Backend and deployment** | 008 | `Coo.Api` with PostgreSQL (G3 constraints, `entry_kind_versions`), Docker, deployed to `coldstart-prod` behind Caddy, and the server integrity scan. **Test:** `https://api.coo.lechdigital.nl/health`. | — |
| 9 | **Identity and households** | 009 | Sign in with Google, the provisional household upgrade, invitations. **Test:** sign in on two phones, and invite one from the other. | — |
| 10 | **Sync** | 010 | Push and pull, HLC, tombstones, the SignalR doorbell, `membership-revoked`. **Test:** two phones, offline edits, both converge, and nothing is lost. | — |
| 11 | **Billing and entitlements** | 011 | Play Billing, the trial, RTDN, the reconciler, `FeatureGate`. **Test:** Play license testers. | — |
| 12 | **Reminders and notifications** | 012 | The local reminders from ADR 0007, and WorkManager sync. | — |
| 13 | **Sleep predictor** | 013 | ADR 0008, with the backtest harness gating it. | — |
| 14 | **AI logging** | 014 | Voice and text to proposals, the confirmation card, pending notes, evals. | — |
| 15 | **Reports (PDF)** | 015 | The pediatrician report, designed first. | — |
| 16 | **Wear OS companion** | 016 | ADR 0009. | — |
| 17 | **Release** | 017 | Signing, the Play listing in seven languages, the privacy policy, closed testing. | — |

Increments 1–7 need no server and no accounts. By the end of increment 7, the app is a usable
**single-phone baby log**. Everything after that adds the family, Premium and the smart features.

## Owner setup tasks that take calendar time

These are listed in `HANDOVER-2026-09-30.md`. They are needed from increment 8 onwards:

- the Play Console entry;
- the Google Cloud project and its OAuth clients;
- DNS;
- the Gemini key.
