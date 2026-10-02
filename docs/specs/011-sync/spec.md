# Spec 011 — Sync

Date: 2026-10-02

Status: Accepted (2026-10-02, under the owner's delegation: "spec and build 11, tell me once it's built" — Q1–Q8 as recommended; the owner may overturn any of them on review)

Depends on: ADR 0002 (the protocol), ADR 0003 §4 and §7 (sessions, the provisional household), ADR 0010 §1 and §3
(synced types; unknown kinds and properties are never lost), Spec 004 (the outbox), Spec 007 (Needs attention),
Spec 008 (`care_entries`, `seq`), Spec 010 (sign-in, `JoinPending`, the refreshing handler), the privacy policy at
`https://lechdigital.nl/projects/coo/privacy`.

Delivered by: **Increment 11** (`docs/ROADMAP.md`). The app is version **0.11.0** from this increment.

## 1. Purpose

Two phones signed in to one account converge: what is logged, edited or deleted on one appears on the other, offline
edits included, and nothing is lost (ADR 0002). Before anything is uploaded, the person gives the explicit consent the
privacy policy promises.

**Out of scope:**

| Not in this increment | Where |
|---|---|
| Invitations, the member list, roles in the UI, removing members | Increment 11b |
| Two households on one phone (switching between logs) | 11b or later (Q4) |
| The Washi restyle of the edit and Settings pages | Increment 11c |
| The SignalR doorbell, push notifications, WorkManager background sync | Increment 13 (Q3) |
| Showing conflicts in the UI | Not in v1 (ADR 0002 §3) |
| AI | Increment 15 |

## 2. What the person sees

- **Consent** (§3), once per account, before the first upload.
- **The sync line**, under the account's name in Settings → Account, the only sync indicator. It never says "up to
  date" while the outbox holds anything.

  | State | Line |
  |---|---|
  | Outbox empty, last cycle succeeded | "Up to date · 14:32" |
  | Outbox not empty, or a cycle running | "Sharing 3 changes…" |
  | Last cycle failed (offline, server down) | "Waiting for a connection. 3 changes are safe on this phone." |
  | Consent declined | "Sharing is off. Your log is only on this phone." and **Turn on sharing** |
  | Session refused (Spec 010 §3.3) | "Sign in again to keep sharing." (exists) |
  | `client-too-old` (ADR 0002 §11) | "Update Coo to keep sharing. Your changes are safe on this phone." |

- **Offline**, logging is unchanged; changes wait in the outbox.
- **Conflicts** resolve silently, per record, by last writer wins on the HLC stamp, identically on every phone
  (ADR 0002 §3–§4). The same event logged on two phones gives two entries: the `DuplicateDetector` runs after every
  pull (ADR 0002 §9) and the pair appears in Needs attention.
- **Deletions** reach every phone as tombstones. Undo writes a newer version, which wins.
- **Rejected changes** (ADR 0002 §5) fill a new Needs attention group, **Not accepted by the server**: the entry, the
  reason ("The end is before the start"), **Edit** and **Delete**. The entry stays on the timeline. It is a list,
  usable at hundreds of rows.

## 3. Consent before the first upload

The policy says care data is stored on the server "only with your explicit consent, which you give when you sign in to
share (Article 9(2)(a))", withdrawable "by deleting your account".

**When:** right after a successful sign-in, in place, and whenever a cycle would start for an account without consent.
Without consent the engine neither pushes nor pulls (Q8).

**The sheet** (wording version `care-data-v1`; seven languages):

> **Share your log with your family?**
>
> To share, Coo keeps your baby's care log (feeds, diapers, sleep and pumping, with their times, amounts and notes),
> your baby's profile and your household settings on Coo's server in Germany. This is health information about your
> child, so we only store it with your permission.
>
> Everyone in your household can see it and add to it. You can withdraw your permission at any time by deleting your
> account in Settings. The log on this phone always stays here.
>
> [Privacy policy]
>
> **Agree and share** · **Not now**

**Declining** (Not now, or Back): nothing is sent, the person stays signed in, and the sync line offers **Turn on
sharing**, which opens the same sheet.

**Stored per account, on the server** (Q1). `POST /api/consents { purpose: "care-data", wording: "care-data-v1" }`
records the user, wording, time, app version and language. Per account, because consent is a person's act: a household
flag would let one parent consent for another (11b); a device flag would ask the same person twice. On the server,
because GDPR Art. 7(1) requires proving it, and so the server can enforce it: sync without a current consent gets
`403 sync.consent-required`. A new wording version asks again. `GET /api/me` returns the consent, so the account's
second phone does not ask; the phone caches it in `app_state`.

**Withdrawal** is account deletion (Spec 010 §5; Q2). Signing out stops syncing and withdraws nothing.

## 4. The second phone's entries, and `created`

**`JoinPending`** (Spec 010 §3.2 and its Q1): after consent, the phone bootstraps the account's household (§6.2)
beside its provisional one, then asks:

> **This phone has 42 entries for Maja.** Your family's log has: Maja.
>
> **Bring my entries over** · **Keep them separate**

- **Bring my entries over** maps each local baby to a household baby (or **Add as a new baby**), then, in **one SQLite
  transaction**: moves every local care entry and dismissal to the household, rewriting `subject_baby_id` by the
  mapping; drops a mapped local baby profile, the provisional settings and their outbox rows (the household's win);
  moves an unmapped baby's profile; resolves `this-device` (§5). Outbox rows name only the record (Spec 004 §4.3), so
  the push sends the moved state. Versions are not bumped: these records were never on the server. The duplicate check
  then runs over the merged log.
- **Keep them separate** (Q4): the phone keeps its own log, local and unshared; Settings offers **Bring my entries
  over** later.

Until the person chooses, nothing of this phone's is uploaded.

**`created`** (the rare ID clash, Spec 010 §3.2): before the first push, the same transaction moves every local record
to the account's (empty) household, without a question.

## 5. What is synced

The four types of ADR 0010 §1: `CareEntry`, `BabyProfile`, `HouseholdSettings` (units, time zone, hidden kinds) and
`DuplicateDismissal` (Q6). Appearance, quiet hours and language stay per device (Spec 009 §3.5).

- **Nothing unknown is lost** (ADR 0010 §3): pulled `details` are stored exactly as received, through the read guard,
  never re-encoded; unknown kinds become read-only entries; edits merge unknown properties back (Spec 003 §7).
- **`this-device` becomes `user:<id>`** before a record is first pushed, in the transaction that prepares the push.
  The server rejects `this-device`.
- **Role rules** (ADR 0003 §6) are checked on push; only `Owner` exists until 11b.

## 6. The protocol (ADR 0002, unchanged unless stated)

### 6.1 Push

Oldest first, up to 200 changes for one household per batch. Several outbox rows for one record become one change: its
current state, read at push time, under the newest row's `changeId`. A final result deletes that record's rows up to
that row. `applied` and `superseded` as ADR 0002 §5; `rejected` keeps the record, writes a `sync_rejections` row
(entity, reason, time) and deletes its outbox rows. An HTTP failure retries the whole batch (`applied_changes` makes it
idempotent).

### 6.2 Pull

`GET /api/sync/pull?household&cursor&bootstrapHead` returns records of all four types with `seq > cursor`, in `seq`
order, up to 500, with the new cursor and `hasMore`; from cursor `0`, `bootstrapHead` as ADR 0002 §6. The cursor is
stored per household (`sync_cursors`) in the transaction that applies its page.

**Applying a pulled record** is a second write path beside `SaveAsync` (Spec 004 §5): no outbox row, and the local
record is kept when it has a pending change with a higher version. The phone's clock advances past every pulled stamp.

### 6.3 The cycle and its triggers

Push, then pull; one at a time; triggers coalesced; backoff with jitter up to 5 minutes (ADR 0002 §7). Triggers: start
and resume, a local write (debounced 1 s), connectivity regained, going to the background (one last cycle; Android
may cut it, which is safe), and **every 30 seconds in the foreground** (Q3). With an empty outbox a cycle is one pull,
an empty page at the head. No cycle runs signed out, without consent, in `JoinPending` or for a soft-locked household.

### 6.4 Validation and versions

Each change is checked against its `schemaVersion`'s rules (ADR 0002 §11): the codecs (a known `"v"` per kind), the
single-record domain rules, the role rule. `schemaVersion` is `1`; N−1 has a seam and no upgrader yet. Rejected too: a
stamp over 24 h ahead, and an ID that exists in another household (never moved).

### 6.5 `membership-revoked`

ADR 0002 §10 in full (Q7), though nobody can be removed until 11b: only the exact problem body for the same household
counts; soft-lock first; delete that household locally only after `GET /api/households/{id}/membership` answers
`200 { "status": "revoked" }`, after telling the person and offering unsent entries as text. Any other `403` is a
failed cycle.

## 7. The server

### 7.1 Endpoints

All need the access token; the bearer handler already refuses an inactive session per request (Spec 010). Every sync
request also checks an **active membership** of its household, per request.

| Endpoint | Purpose |
|---|---|
| `POST /api/sync/push` | `{ householdId, deviceId, schemaVersion, changes[≤200] }` → a result per change |
| `GET /api/sync/pull` | §6.2 |
| `GET /api/households/{id}/membership` | `{ status: "active" \| "revoked" }`; `404` if never a member |
| `POST /api/consents` | §3 |
| `GET /api/me` (exists) | adds `consent: { wording, givenAt } \| null` |

**Errors** (codes, never text; ADR 0006): `400 sync.malformed` and `sync.batch-too-large` (back off, log by type);
`403 sync.consent-required` (the sheet); `403` membership-revoked problem (§6.5); `403 sync.not-a-member` (back off,
change nothing); `426 client-too-old` (the update line; the outbox stays). **Rejected reasons:** the domain rule's
code, `sync.schema-unsupported`, `sync.stamp-in-future`, `sync.household-mismatch`, `sync.person-unresolved`,
`sync.role-forbidden`.

### 7.2 Data (one EF migration)

| Table | Notes |
|---|---|
| `baby_profiles`, `household_settings`, `duplicate_dismissals` | Record columns plus `household_id`, `seq`, `version_*`, `is_deleted`; unique `(household_id, seq)` |
| `applied_changes` | `change_id` PK, `result`, `reason`, `applied_at`; purged after 30 days by the daily job |
| `sync_conflicts` | Both versions of a losing write |
| `consents` | `user_id`, `purpose`, `wording`, `given_at`, `app_version`, `language` |
| `memberships` (exists) | adds `revoked_at` (set in 11b; read by §6.5) |

Each change applies in **one transaction**: the stored version (`FOR UPDATE`), the comparison with `HlcStamp` from
`Coo.Domain`, the next `seq`, the row, `applied_changes`, and `sync_conflicts` when something lost. Account deletion
cascades to the new tables.

## 8. Carried from Increment 10's final review

### 8.1 A refresh in flight during sign-out

A refresh started before sign-out can save its tokens afterwards (signing the phone back in) or set `SessionExpired`
after a deliberate sign-out. Sync makes requests constant, so it will happen.

**Fix: a session generation.** `Account` increments a generation on every sign-in and sign-out, under one lock.
`TokenRefresher` reads it before calling the server and writes back only through
`Account.TryReplaceTokensAsync(generation, …)` and `TryExpireAsync(generation)`, which do nothing once it has moved.
A sync cycle captures it at the start and stops at its next step once it has moved.

### 8.2 A lost refresh response reads as theft

The phone sends T1, the server rotates to T2, the response is lost, the phone retries T1 (now the previous token) and
the session is revoked. On mobile networks under sync, this will happen.

**Fix: the phone proposes the next token** (Q5). Before refreshing, the phone generates T2 (256 random bits), stores it
as pending beside T1, and sends `{ refreshToken: T1, nextRefreshToken: T2 }`; the server rotates to `hash(T2)`. A retry
sends the same pair: T1 matches the previous hash **and** `hash(T2)` the current one, so the server issues a fresh
access token and changes nothing. Anyone else presenting T1 cannot know T2, so it still revokes. No window to tune, no
new column, nothing secret in plain text. A 0.10.0 phone, sending no `nextRefreshToken`, is served as today.

### 8.3 "…when sharing arrives"

`Account.JoinPending` becomes "Signed in. The entries on this phone aren't in your family's log yet." with
**Choose what to do with them** (§4). `Account.SessionExpired` and `Account.SignInHint` are true from this increment.
The privacy policy's "coming soon" and "When sharing arrives" passages must be updated (owner, lech-digital) before
0.11.0 reaches anyone but the owner.

## 9. Where it lives

| Concern | Project |
|---|---|
| Change envelope, push/pull DTOs, result and error codes, consent DTO | `Coo.Contracts/Sync` |
| `SyncEngine`, apply-on-pull rule, `RecordConsent`, `BringEntriesOver`, the sync line's state; ports `ISyncApi`, `ISyncStore`, `IConsentApi`; the session generation | `Coo.Mobile.Application/Sync`, `/Auth` |
| `SyncClient` (via the authenticating handler), `TokenRefresher` (§8), `sync_cursors`, `sync_rejections`, the pulled-record path, the bring-over transaction | `Coo.Mobile.Infrastructure` |
| Connectivity and lifecycle triggers | `Coo.Mobile.Platform` |
| Consent and bring-over sheets, the sync line, the Needs attention group | `Coo.Mobile.Presentation`, `Coo.Mobile/Pages` |
| Push, pull, consent, membership use cases; the N−1 seam | `Coo.Api.Application/Sync` |
| New tables, the LWW apply, the pull across four tables | `Coo.Api.Infrastructure/Persistence` |
| Endpoints, the membership check, `nextRefreshToken` | `Coo.Api` |

## 10. Tests

| Where | What |
|---|---|
| Api.Application | `applied`, `superseded`, each `rejected` reason; a retried `changeId` returns its stored result; a tombstone beats a lower edit and loses to a higher one; `consent-required`; `not-a-member` vs `membership-revoked`; the T1+T2 retry is idempotent, T1 with another next token revokes. |
| Api.Infrastructure (Testcontainers) | The migration; the apply in one transaction; `seq` gapless across four tables; paging; a tombstone above `bootstrapHead` arrives, one below is skipped. |
| Api integration | **Two simulated devices** converge after interleaved offline edits, deletes and a revived edit; membership checked per request; `426`. |
| Mobile.Application | One cycle at a time, coalesced triggers, backoff; an outbox row goes only on a final result; `rejected` kept and listed; a pull never overwrites a newer pending edit; no cycle without consent or in `JoinPending`; a bare `403` deletes nothing; sign-out or sign-in during a delayed refresh (§8.1). |
| Mobile.Infrastructure (real SQLite) | Bring-over and `created` are one transaction (a failure changes nothing); unknown kinds and properties survive pull and edit; the cursor commits with its page; an app kill mid-push resends. |
| Presentation, Localization | The sync line's states, the sheets; every new key in seven languages. |

## 11. Increment 11 — deliverables

Without a second phone, the **Android emulator** (Google Play image, same account) is the second phone. The debug
screen gains the cursor, outbox and rejection counts, the last cycle's result, **Sync now** and **Fresh bootstrap**
(only with an empty outbox: clears the household locally and pulls from 0).

| # | Deliverable | How you check it |
|---|---|---|
| 1 | Consent | Sign in: the sheet; **Not now** sends nothing (debug: outbox unchanged); **Turn on sharing** → agree: entries reach the server |
| 2 | Convergence | Log on the Pixel; within 30 s it is on the emulator, and back |
| 3 | Offline edits | Both offline: edit one feed on each, delete a diaper on one; online: the later edit on both, the diaper gone on both |
| 4 | Same event twice | A diaper on each a minute apart: the pair in Needs attention on both; Keep both clears it on both |
| 5 | Second phone's entries | Emulator with its own entries signs in: **Bring my entries over**; they appear on the Pixel |
| 6 | Fresh phone | **Fresh bootstrap**: the log returns complete, without deleted entries |
| 7 | Honest status | Airplane mode, log twice: "Waiting for a connection. 2 changes are safe on this phone." |
| 8 | Sessions under sync | 20 minutes away with the emulator logging: still signed in on both |
| 9 | Deployed | `/health` shows 0.11.0; the privacy policy is updated |
| 10 | CI green | GitHub Actions |

## 12. Decisions for the owner

| # | Question | Recommendation |
|---|---|---|
| Q1 | Where is consent stored? | **Accepted as recommended (delegated).** **Per account, on the server, with the wording version:** provable, enforced, asked once per person. |
| Q2 | Withdrawal without deleting the account? | **Not in 11:** account deletion, as the policy says. "Stop sharing and delete the server copy" can follow with a policy change. |
| Q3 | SignalR doorbell now? | **No: pull every 30 s in the foreground.** Near real time in one room, no WebSocket, no token in a query string. The doorbell and background wake-up are designed together in 13. |
| Q4 | "Keep them separate" | **This phone's log stays local and unshared,** bring-over offered later. A second synced household needs household switching. |
| Q5 | The lost refresh response | **The phone proposes the next token:** idempotent retries, exact theft detection. A grace window misses retries after long offline gaps and blinds detection inside it. |
| Q6 | Sync `DuplicateDismissal`? | **Yes** (ADR 0010 §1); otherwise "Keep both" returns on the other phone. |
| Q7 | Build `membership-revoked` now? | **Yes,** engine, endpoint and tests; tried on a phone in 11b. |
| Q8 | Pull before consent? | **No:** no sync at all without consent. Simple to explain; for one account the server holds nothing yet. |
