# Coo — Delivery roadmap

Work is incremental. Each increment has **one short spec**, agreed before its code, and a
**deliverable you can test yourself**. The next spec is written only once the previous increment
has been tested, so it can use what we learned.

This list is the intended order, not a contract. After each increment we revisit the next one or
two.

| # | Increment | Spec | Deliverable you can test | Status |
|---|---|---|---|---|
| 1 | **Solution skeleton** | 002 (accepted) | The solution builds with zero warnings. The architecture tests pass, and deliberate violations fail. `/health` answers. A placeholder app installs and opens on your phone. | Built; phone install pending |
| 2 | **Care-log domain and codecs** | 001 (accepted) | The whole domain from Spec 001, and `Coo.Codecs`: kinds, rules, duplicates, time resolver, HLC, codecs with the write gate and the upcaster chain, and the ADR 0010 completeness tests. **Test:** `dotnet test` runs the complete Spec 001 §14 list plus the round-trip property tests, all green. | — |
| 3 | **Local storage and the write path** | 004 | EF Core SQLite on the phone: the `care_entries` table with the G3 constraints, repositories through the codecs, the read guard, the local integrity scan, the outbox table (filled, not sent yet), and data-migration runner v1. **Test:** real-SQLite tests; a debug screen on the phone adds and lists entries that survive an app restart. | — |
| 4 | **Look and feel: styles** | 003 | The Washi UX tokens as MAUI resources, bundled Figtree, Auto/Day/Night switching, and the token-contrast test. **Test:** a style-gallery page on the phone, checked against `washi-ux.html`, which also closes the handover's open checks on tabular figures and Polish diacritics. | — |
| 5 | **Today and quick logging (offline)** | 005 | The Today screen and the log tiles for the four kinds: breast timer with side switching, bottle, diaper, sleep, pump. The timeline and the day ribbon. Localized in seven languages. **Test:** you use it for real, offline, for a day. | — |
| 6 | **Edit, delete, undo, Needs attention** | 006 | Editing forms, undo, the duplicate check with "Keep both", and the plausibility warnings in the UI. **Test:** the scenarios in the spec, by hand, on the phone. | — |
| 7 | **Backend and deployment** | 007 | `Coo.Api` with PostgreSQL (G3 constraints, `entry_kind_versions`), Docker, deployed to `coldstart-prod` behind Caddy, and the server integrity scan. **Test:** `https://api.coo.lechdigital.nl/health`. | — |
| 8 | **Identity and households** | 008 | Sign in with Google, the provisional household upgrade, invitations. **Test:** sign in on two phones, and invite one from the other. | — |
| 9 | **Sync** | 009 | Push and pull, HLC, tombstones, the SignalR doorbell, `membership-revoked`. **Test:** two phones, offline edits, both converge, and nothing is lost. | — |
| 10 | **Billing and entitlements** | 010 | Play Billing, the trial, RTDN, the reconciler, `FeatureGate`. **Test:** Play license testers. | — |
| 11 | **Reminders and notifications** | 011 | The local reminders from ADR 0007, and WorkManager sync. | — |
| 12 | **Sleep predictor** | 012 | ADR 0008, with the backtest harness gating it. | — |
| 13 | **AI logging** | 013 | Voice and text to proposals, the confirmation card, pending notes, evals. | — |
| 14 | **Reports (PDF)** | 014 | The pediatrician report, designed first. | — |
| 15 | **Wear OS companion** | 015 | ADR 0009. | — |
| 16 | **Release** | 016 | Signing, the Play listing in seven languages, the privacy policy, closed testing. | — |

Increments 1–6 need no server and no accounts. By the end of increment 6, the app is a usable
**single-phone baby log**. Everything after that adds the family, Premium and the smart features.

## Owner setup tasks that take calendar time

These are listed in `HANDOVER-2026-09-30.md`. They are needed from increment 7 onwards:

- the Play Console entry;
- the Google Cloud project and its OAuth clients;
- DNS;
- the Gemini key.
