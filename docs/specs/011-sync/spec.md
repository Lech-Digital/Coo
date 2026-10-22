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
  | Household soft-locked or removed (§6.5) | "Sharing is paused. Your entries are safe on this phone." |
  | Stopped for good: the phone's entries are not in the account's family (`household-mismatch`) | "Sharing stopped: the entries on this phone aren't part of your family yet. Sign out and sign in again to choose what to do with them." |
  | Stopped for good: a pull that cannot move the cursor, or a push that cannot make progress (`no-progress`) | "Sharing stopped. Your changes are safe on this phone. Coo will try again when you reopen the app." |
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
> your baby's profile and your family settings on Coo's server in Germany. This is health information about your
> child, so we only store it with your permission.
>
> Everyone in your family can see it and add to it. You can withdraw your permission at any time by deleting your
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
`sync.role-forbidden`, `sync.change-malformed` (a change with an unknown entity type, a missing payload, an unparsable person or source, or an edit that changes the stored entry's kind).

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

## 13. Built and verified (2026-10-03 · Pixel 10 and an Android 16 emulator)

Trimmed Release build with the debug screen (0 IL warnings), signed with the debug key, on the Pixel 10 and on the emulator `coo-second` (Pixel 9 profile, Android 16, Google Play), both signed in as adzius.lech@gmail.com. Production at `0.11.0+4ca8b7f`; later commits change the phone only.

| # | Deliverable | Result |
|---|---|---|
| 1 | Consent | **PASS.** Pixel: Turn on sharing, Not now: "Sharing is off. Your log is only on this phone." and Turn on sharing. Again, Agree: "Up to date · 23:10" within seconds. Production: `care_entries` 20, `consents` 1, `baby_profiles` 1, `household_settings` 1. |
| 2 | Convergence | **PASS.** A Mixed diaper on the Pixel reached the emulator; a bottle on the emulator reached the Pixel. A timed round, Wet on the emulator to the Pixel, took 23 s. |
| 3 | Offline edits | **PASS.** Both phones offline: the emulator edited a bottle to 100 ml and deleted the Mixed diaper; the Pixel, later, edited the bottle to 150 ml. Online: 150 ml on both, the Mixed diaper gone on both. |
| 4 | Same event twice | **PASS.** Wet on the Pixel at 13:17 and on the emulator at 13:18: "Probably the same" on both. Keep both on the Pixel cleared the pair there and, after the next pull, on the emulator (7 to 6 entries). |
| 5 | Second phone's entries | **PASS.** The emulator signed in with no consent sheet (the account had consented); the bring-over sheet said "This phone has 2 entries for Maja. Your family's log has: Maja." Bring my entries over, Maja to Maja, Bring them over: the emulator showed the family's log, and its 2 diapers (23:07 the day before) reached the Pixel within about 30 s. This also closes Spec 010 deliverable 3. |
| 6 | Fresh phone | **FINDING, then PASS.** Fresh bootstrap opened the first-launch setup before the pull had returned (the baby was wiped; a duplicate baby was possible). Fixed in `a9f6469`: the wipe deletes the cursor row, and setup routing skips the account's household until its first full pull (test red, then green). Re-run: no setup, the log came back, the counts matched the Pixel, the deleted diaper stayed absent. |
| 7 | Honest status | **PASS.** Pixel offline, two diapers: "Waiting for a connection. 2 changes are safe on this phone." Online: "Up to date · 13:05" after 9 s, including the Wi-Fi reconnect. |
| 8 | Sessions under sync | **PASS.** After more than 20 minutes (the emulator signed in at 13:06, checked at 13:27) both phones were still signed in; `/api/me` answered as owner, with the devices Pixel 10 (13:27) and the emulator (13:20). |
| 9 | Deployed | **PASS.** `/health` shows `0.11.0+4ca8b7f`, database ok (the server is unchanged since that deploy); the privacy policy live check passes. |
| 10 | CI green | **PASS.** Pushed as `5850a7c`: GitHub Actions "Build, format and test" green (restore, format, build, test, API image build). |

Also: the Release build was installed on the owner's girlfriend's Samsung (SM-A256B, Android 16). It launches to setup without a crash. She uses her own account, so her phone is a separate family until Increment 11b brings invitations. The two-phone walk used only the Pixel and the emulator.

### 13.1 The plan's rulings on what this spec leaves open

1. **Deletes carry no payload** (ADR 0002 §3). A delete for a record the server has never stored is `applied` and stores nothing; the server keeps a stored record's columns and flips `is_deleted`.
2. **Per-change shape problems** (unknown entity type, upsert without its payload, a dismissal pairing an entry with itself, an unparsable person or source) are `rejected` with `sync.change-malformed`; only an unreadable request, or one naming no household or device, is `400 sync.malformed`.
3. **Role rule (ADR 0003 §6):** `BabyProfile` and `HouseholdSettings` changes (deletes too) need `CanManageBaby`; `CareEntry` and `DuplicateDismissal` need any active membership.
4. **`sync_conflicts`** stores both version stamps, the change ID and the record's identity, not payloads. It is purged with `applied_changes` after 30 days.
5. **The server issues no HLC stamps;** it only rejects stamps more than 24 h ahead.
6. **Stored details on the server** are the codec's encoding with the received document's unknown top-level properties merged back; the phone stores pulled details exactly as received.
7. **The active household:** `app_state.active_household` names the household the UI shows; `ListHouseholdsAsync` returns it first.
8. **Join-pending:** a cycle is the account household's pull only, never a push, so the bring-over sheet can name the family's babies.
9. **`created`** is handled by the cycle before its first push. (Refined by the controller's ruling in 13.2 under Task 11: an explicit marker instead of an inferred mismatch.) A sign-in answered `existing` whose household differs from this phone's is stored as join-pending.
10. **Consent states:** `Unknown` / `Given` / `Declined`, cached per account in `app_state`. "Not now" is remembered until the next sign-in; the sheet opens by itself once per app run while `Unknown`, and from Turn on sharing any time. `POST /api/consents` carries `appVersion` and `language`.
11. **The sync line:** "Sharing n changes…" only when n > 0; a failed cycle with nothing pending reads "Waiting for a connection."; before the first cycle with nothing pending the line is empty.
12. **Backoff:** Start, Resume, connectivity regained and Sync now reset it and run at once; the periodic tick, local writes and going to the background wait for it.
13. **`membership-revoked` (Q7):** the engine soft-locks, confirms through `/membership`, and deletes the household only when `SyncEngine.AcknowledgeRemovalAsync` is called. The removal sheet and hiding the household's UI ship with 11b.
14. **"Not accepted by the server"** lists rejected care entries (Edit, Delete), 50 at a time with Show more; rejected profile, settings or dismissal changes are kept and counted on the debug screen.
15. **Consent and bring-over are modal pages,** so they can open over whichever page is showing.
16. **A pull page that says `hasMore` without moving the cursor** fails the cycle.
17. **The daily purge is its own `SyncHistoryCleanupJob`,** beside the nonce job.
18. **`nextRefreshToken`** must be 43–128 characters and differ from the presented token, else `auth.refresh-invalid` with nothing changed. `Account.UpdateProfileAsync` does not move the generation.
19. **Schema versions:** `SchemaVersions.Current = Oldest = 1`; below `Oldest` is `426 client-too-old`; a change above `Current` is `rejected sync.schema-unsupported`. The N−1 seam is `SchemaUpgrade.ToCurrent`.
20. **The bootstrap skip needs an empty phone.** A phone that holds any entry, baby or dismissal of the household pulls from cursor 0 with `bootstrapHead=0`, which skips nothing; only an empty phone lets the server choose the head. No server change. ADR 0002 §6 is amended accordingly.

### 13.2 The controller's rulings

Each ruling gives its cost if wrong.

**Pre-flight**
- The version-guarded `ON CONFLICT DO UPDATE` makes a new entity's apply atomic (the incoming HLC must be greater than the stored one); a test with two different change IDs creating the same record proves it. Cost if wrong: none.
- A catch-all around the engine's `RefreshPendingAsync`, and around `BringOverPage.OnAppearing` and its back button. Cost if wrong: none.
- A synchronous wait in a test's event handler only if no async pattern fits. Cost if wrong: none.
- Intermediate commits (Tasks 12–14) where the host cannot resolve `SyncEngine` are acceptable; the host is first built and run in Task 19. Cost if wrong: a broken host build between those commits.

**Task 1.** The duplicate person/source parsing in `SyncText` is temporary; Task 5 replaced `ServerColumns`' copies. Cost if wrong: duplicate code.

**Task 3.** The server checks only the proposed refresh token's length (43–128) and that it differs from the presented one; the phone generates it with `RandomNumberGenerator`. Cost if wrong: a weak phone generator would weaken the session. A holder of the live token may propose the session's older previous token. Cost if wrong: none practical.

**Task 5.** `CareEntryStore.SaveAsync` refuses a lower or equal version instead of overwriting (last writer wins). Cost if wrong: an Increment 8 path expecting overwrite would see `Superseded`. A brief test bug (stamping `newer` before the write) was fixed with the assertions kept. Cost if wrong: none.

**Task 6.** A denied membership maps `MembershipRevoked` explicitly and throws on an unknown denial; the catch-all would have answered "revoked", which makes phones delete data. Cost if wrong: an unexpected denial becomes a 500. `TryReadJsonAsync` also catches `InvalidOperationException` (an unknown charset gives `400 sync.malformed`). Cost if wrong: none.

**Task 8.** The controller did Task 8 (the `DEPLOY.md` text and the deploy) itself under the owner's authorisation. Cost if wrong: an unreviewed docs paragraph.

**Task 10.** `LocalWrites.Notify` runs each handler inside a catch-all, so a failing trigger never fails a committed write. Cost if wrong: a swallowed handler bug.

**Task 11.** `IHouseholdMoves.MoveAllAsync` parameters are `source` and `target` (CA1716). Cost if wrong: none. The cycle moves only on an explicit persisted `created` marker written at sign-in; any other household mismatch stops the cycle with no move and no push. Cost if wrong: a real `created` phone without the marker (for example upgraded mid-flow) would show a sync failure; acceptable, no such installs exist. Pulled stamps are observed before the page is applied. Cost if wrong: none. The marker lives in the session (`StoredSession.PendingMoveFrom`, SessionStore v3) and is cleared by every sign-out and right after the move commits. Cost if wrong: none practical.

**Task 12.** `household-mismatch` and `no-progress` stop cycles with no backoff until Start, Resume, Sync now, `RequestSync` or an account change. Cost if wrong: a transient no-progress waits for the next wake. `WhenIdleAsync` is internal, visible to the test project only. Cost if wrong: none. Any account change mid-cycle discards that cycle's result. Cost if wrong: one status update lost.

**Task 13.** A pulled record that wins clears its `sync_rejections` row. Cost if wrong: a rejection disappears from the list when the server's version arrives. `ApplyPageAsync` skips unknown entity types but still advances the cursor. Cost if wrong: records of a future type are not re-pulled when the app learns it (needs a cursor reset in that release). A pulled record whose ID exists locally in another household is skipped. Cost if wrong: that record never appears on this phone (a UUIDv7 collision only).

**Task 14.** A household that ever synced is never moved: no bring-over offer, and a `created` move is refused (the marker is cleared, the account household used); the old household stays on the phone. Cost if wrong: a person signing into a second account on the same phone does not get the first account's log copied into it, which is the privacy-correct outcome.

**Task 15.** A pulled record with an empty device ID or missing details is dropped like a payload-less one; a local change that cannot be mapped to the wire fails the push as `SyncUnavailable`. Cost if wrong: a corrupt local change blocks pushes until fixed (never seen from app writes). No re-review of the polish round. Cost if wrong: a polish defect would surface at the final review.

**Task 16.** Soft-locked and removed households show "Sharing is paused. Your entries are safe on this phone." (the brief said "waiting for a connection"; the server answered, so "waiting" is false). Cost if wrong: a copy change. Owner decision: the consent text says "family", not "household", in all seven languages; the version ID stays `care-data-v1` (unreleased, test consents only).

**Task 17.** `BringEntriesOver.ExecuteAsync` returns `Done`, `NothingChanged` or `MovedButNotCompleted`; "nothing was changed" is claimed only for `NothingChanged`. Cost if wrong: one enum and its call sites.

**Task 18.** A rejection whose entry is quarantined or read-only is not listed in the rejected group (it shows in its own group, counted once). Cost if wrong: the server's reason is not shown for that rare entry. No separate re-review of the fix round. Cost if wrong: caught at the final review.

**Task 19.** `SyncPrompter` pushes no automatic sheet while the app is in the background; it re-checks on resume. Cost if wrong: a sheet appears on the next resume.

**Task 20.** Fresh bootstrap waits for the engine to be idle before wiping; a race with a cycle that starts in between is accepted for a debug action. Cost if wrong: one debug bootstrap needs repeating.

**Final review**
- A slow create landing after its delete is answered `applied` with nothing stored: accepted (see 13.4). Cost if wrong: a deleted entry reappears on other phones in that rare case.
- Server findings left standing: `logged_by` is not cross-checked, cross-record references are unchecked, no rate limits, a settings tombstone is allowed, transient rejections are stored 30 days, and `household-mismatch` is an existence oracle. ADR 0002 §9 excludes cross-record checks, the caller is an authenticated member of their own household, and UUIDv7s are unguessable. Cost if wrong: a hostile family member can mislabel or corrupt only their own family's log.
- Invariant: the active account household exists on the phone with a settings row, after every move or activation. Settings are re-keyed (bring-over into a settings-less family) or copied with an outbox row (refused move, activation); bring-over is offered only with consent given and after the family's first full pull; `ActiveHouseholdAsync` never persists a fallback. Cost if wrong: a new family starts with the old family's units, time zone and hidden kinds.
- A join-pending phone with no local entries simply joins, with no bring-over sheet, unless it has a baby the family lacks (the shortcut applies only when the phone has no baby or the family already has one). Cost if wrong: none.
- `HasSettingsAsync` counts a tombstoned settings row. Cost if wrong: the invariant breaks for a family whose settings were tombstoned (only an owner's sync can tombstone settings; no UI does).
- Phone: the pre-existing async-void paths in `StartupPage` (`EnsureProvisionalHousehold`, `PrewarmTabsAsync`) are out of this increment's scope and carry on.
- A write for a household that does not exist throws `HouseholdNotFoundException` (500). Unreachable, since membership implies the household. Cost if wrong: one 500 instead of applied-nothing-stored.

### 13.3 Findings and fixes

Per-task review findings that changed behaviour:

- **Task 3.** A client-chosen refresh proposal could hit `ix_device_sessions_refresh_hash` and give an unhandled 500 and a retry trap; the previous-token lookup had no `ORDER BY` and could pick another session's. Now a 23505 on rotate is a lost swap or refresh-invalid, and the lookup puts the current token first.
- **Task 5 (pre-flight F1).** The upsert for a new entity was not atomic and a lower version could overwrite a higher one; fixed with the version guard and a race test.
- **Task 9.** `TryCompleteJoinAsync` set state outside the gate (a sign-out race could leave `SignedIn` with an empty store), and `UpdateProfileAsync` could write account A's profile onto B's session. State now moves with the store under one lock, and the profile writes only to the same account.
- **Task 11.** No generation check before `MoveAllAsync`; `created` was inferred and could move entries across accounts; pulled stamps were observed after applying the page. Fixed with generation checks, the explicit marker and observing first; also a soft-lock check before join-pending, a push progress guard, and no soft-lock writes after the generation moved.
- **Task 12.** An in-flight cycle at sign-out leaked Removal and `LastSyncedAt` into the status. Any account change now discards the cycle's result.
- **Task 13.** A `(0, null)` cursor skipped the head-0 rule and could lose a tombstone. Fixed.
- **Task 14.** A synced household could be brought over or moved by `created`, and there was no generation check before bring-over. Fixed (see the Task 14 ruling).
- **Task 16.** Soft-locked and removed households read as "waiting for a connection". Now "Sharing is paused".
- **Task 17.** A "nothing was changed" toast followed a committed move. Fixed with the `BringOverResult` enum.
- **Task 18.** No catch-all on the Attention page's refresh paths; `DeleteRejected` untested. Fixed.
- **Task 19.** The periodic pull was lost after the activity was recreated (a static `SyncStarted` skipped Resumed). Fixed.

Final review, server: no Critical or Important. The fix wave (`f21a406`) took the seq lock before the row lock (including `CareEntryStore.SaveAsync`), a store guard so a care entry is written only into its own household, 401 tests for push and consents, and an edit that changes the stored kind now rejected as `sync.change-malformed`. Production was redeployed as `0.11.0+4ca8b7f`.

Final review, phone, fixed in `4ca8b7f` and `2c7cded`:

- **C1 (Critical).** Bringing entries over deleted the phone's only settings row when the family's settings were not on the phone. Bring-over now keeps the family's settings (re-key into a settings-less family) and is offered only with consent and after the family's first full pull.
- **I1 (Important).** A refused move activated a household without settings. A household made active always has settings now (copied with an outbox row).
- **I2 (Important).** `StartupPage` called `engine.Resumed()` and `Start()` without checking the app was in the foreground. Both are guarded now.
- Minors M1–M5, M7, M9 and a polish string were fixed. The re-review found all addressed and three new minors: two were fixed in `2c7cded` (the M7 shortcut, and setup routing that never replaces the page while Coo is off screen or a sheet is open); the third is listed in 13.4.

Device finding (deliverable 6): the Fresh bootstrap opened first-launch setup before the pull (see the table); fixed in `a9f6469`.

Two further fixes outside the reviews: a flaky `SyncEngineTests.FreshBootstrapAsync_OutboxNotEmpty` (the sign-in's own cycle pulled after `ClearReceivedCalls`) now waits for idle (`2cfe254`); Polish stopped-for-good lines were reworded so they differ from "paused" (carried from Task 16).

### 13.4 Known limits carried forward

- **Accepted server window:** a slow create can land after its delete. It needs a server apply slower than the phone's HTTP timeout. The server answers `applied` with nothing stored, and the deleting phone keeps its tombstone. If it happens, a deleted entry reappears on other phones.
- **Owner question, a shared phone:** when A signs back in on a phone B used, B's unsynced entries are offered into A's family ("this phone's entries", per §4). The owner may revisit this.
- **Carry to 11b:**
  - An HTTP-level test that a non-owner role is refused for `BabyProfile` and `HouseholdSettings` pushes (only use-case tests pin it until invitations exist).
  - Removing the only household leaves `ActiveHouseholdAsync` throwing until restart.
  - `NotAMember`, `Malformed` and undecided failed cycles still read "Waiting for a connection" (§6.5 table); permanent refusals need a true line.
  - Removed shows "paused"; the removal sheet should take over for Removed, and a soft-locked household should be hidden (ruling 13).
- **Carry to 11c or later:** the pre-existing `StartupPage` async-void paths.
- **Minor, deferred (server):** batch-limit check runs after the answerable scan, so an oversized batch with a bad change reads as malformed; `PushRequest.DeviceId` is validated but unused; `GetMe` consent null or older-wording untested; no HTTP `bootstrapHead` round-trip test; the cleanup job test polls on a real 20 ms delay; untested paths in the Task 1, 2 and 5 reviews (LWW counter and device tie-break, tombstone and subject-parse rejects, pull cursor above 0 without `bootstrapHead`, an all-skipped bootstrap page); `IX_sync_conflicts_household_id` naming; a stored-marker mismatch gives a permanent `household-mismatch` until sign-out; the progress guard compares the first change ID only; the `ConsentRequired` catch is not generation-guarded (benign).
- **Minor, deferred (phone):**
  - The account line keeps the old 12/24 h format until the next status change.
  - Phone `ObserveAsync` has no upper bound on pulled stamps (the server enforces 24 h).
  - An unparseable local offset falls back to UTC on push.
  - `SyncHttp` duplicates `AuthHttp`'s transport catch.
  - Two local babies may map onto one family baby.
  - A rejected entry that is edited keeps its old reason until the next push result.
  - `GetAttention` does one `FindAsync` per rejection; Show more may jump the scroll.
  - `Consent.Failed` says "check your connection" when the session is gone.
  - The settings page refreshes concurrently on the singleton view model.
  - `SyncStarted` is set before the try, so a partial start failure is not retried in process.
  - The debug screen's Sync now and Fresh bootstrap use `CancellationToken.None`, and its refusal text omits join-pending.
  - A stale pending count can land after a cycle's update (self-heals); a dropped cycle flickers `IsRunning`; `SyncTrigger.Background` is unused.
  - B sees A's log until B's first consented cycle (the `created` move runs only after consent); entries logged while join-pending (an A to B to A window) land in B's household; a removed household stays named in `app_state`.
  - A Dispose during an in-flight refresh throws `ObjectDisposedException` at shutdown only.
