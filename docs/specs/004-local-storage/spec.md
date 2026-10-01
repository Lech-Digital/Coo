# Spec 004 — Local storage and the write path

Date: 2026-10-01

Status: **Draft — for review**

Depends on: ADR 0001 §4 (EF Core SQLite, migrations), ADR 0002 §2 (record plus outbox in one
transaction), ADR 0003 §1 (the provisional household), ADR 0010 §2, §3 and §7 (storage, forward
compatibility, the guards G3–G5); Spec 001 (the domain); Spec 003 (the codecs).

Delivered by: **Increment 4** (`docs/ROADMAP.md`).

## 1. Purpose

The phone gets a database that never loses an entry, and never holds a corrupted one:

- EF Core SQLite in `Coo.Mobile.Infrastructure`, with its schema managed by **migrations**;
- the tables, with the database-level guards (G3);
- the write path: record, outbox entry and version clock, through the codecs;
- reading back: live, read-only (newer than this app), or quarantined (unreadable) entries (G4);
- the integrity scan after every app update (G5);
- the data-migration runner (ADR 0010 §8);
- the device identity and the provisional household;
- a **debug screen** on the phone, the first thing to click through.

**Out of scope:** sending the outbox (sync), styles and real screens (Increments 5–6), editing UI,
duplicates UI (Increment 7), and baby onboarding (Increment 6).

## 2. Ports (`Coo.Mobile.Application`)

The Application layer owns the ports (CS-HEX-02). Infrastructure implements them, and the use
cases never see EF Core, SQL or JSON.

| Port | Purpose |
|---|---|
| `ICareLogStore` | Saves an entry with its outbox entry in one transaction. Loads entries for a time window. Writes tombstones, including for read-only entries. Lists quarantined and read-only entries. |
| `IHouseholdStore` | The provisional household, its settings, and its baby profiles |
| `IVersionClock` | Issues the next `HlcStamp` from the persisted hybrid logical clock |
| `IDeviceIdentity` | This install's `DeviceId`, created once |

Every method takes a `CancellationToken` (CS-DATA-03), and returns `Task<IReadOnlyList<T>>` rather
than `IQueryable` (CS-DATA-01).

**What a read returns.** A read never fails because one row is bad. It returns a `StoredEntry`:

| Variant | When | The UI can |
|---|---|---|
| `LiveEntry(CareEntry)` | The kind is known, and the details decode | Show it, edit it, delete it |
| `ReadOnlyEntry(core fields, reason)` | The kind is unknown, or the details are `Unrepresentable` (Spec 003 §5): written by a newer app | Show its time, kind code and who logged it, with "update the app to see it". Delete it. Never edit it. |
| `QuarantinedEntry(id, kind, start, reason)` | The details are `Malformed`, or the core fields cannot be rebuilt | List it in "Needs attention" (Increment 7). Delete it. |

`ReadOnlyEntry` and `QuarantinedEntry` are Application read models, not Domain types (Spec 001 §6.3).

## 3. Use cases (`Coo.Mobile.Application/CareLog`)

This increment needs only the generic ones. The debug screen calls them.

| Use case | Does |
|---|---|
| `LogEntry` | Takes a subject, details, an optional timing and a source. Takes a stamp from `IVersionClock`, generates a UUIDv7 from the injected `TimeProvider`, builds the entry through `CareEntry.Create`, and saves it. A Domain error returns to the caller unchanged: nothing is written. |
| `ReviseEntry` | Loads a `LiveEntry`, applies a details change or a new timing, and saves it |
| `DeleteEntry` | Writes a tombstone for any stored entry, read-only and quarantined ones included |
| `GetEntries` | Returns the stored entries whose start falls in a window, newest first |
| `EnsureProvisionalHousehold` | On first launch, creates the household and its settings: volume unit from the device region (US gives ounces), time zone from the device |

## 4. Tables (`Coo.Mobile.Infrastructure/Persistence`)

Domain objects are never mapped to EF Core directly. Each table has an internal row class, and the
repository maps between rows and the Domain (CS-DDD-03). The Domain stays free of EF attributes
and private constructors written for EF.

### 4.1 `care_entries`

| Column | Type | Notes |
|---|---|---|
| `id` | TEXT PK | UUID |
| `household_id` | TEXT | |
| `kind` | TEXT | The kind code |
| `subject_baby_id` | TEXT NULL | Set for baby subjects |
| `subject_person` | TEXT NULL | `user:<uuid>` or `this-device`, for caregiver subjects |
| `start_utc_ms`, `start_offset_min` | INTEGER, INTEGER | See §4.5 |
| `end_utc_ms`, `end_offset_min` | INTEGER NULL | Both null for a point, or for an open session |
| `is_session` | INTEGER | 0 for a point, 1 for a session |
| `logged_by` | TEXT | `user:<uuid>` or `this-device` |
| `source`, `source_prompt_version` | TEXT, TEXT NULL | |
| `note` | TEXT NULL | |
| `details` | TEXT | The codec's document, exactly as stored |
| `version_wall_ms`, `version_counter`, `version_device` | INTEGER, INTEGER, TEXT | The `HlcStamp` |
| `is_deleted` | INTEGER | |
| `integrity` | TEXT | `ok`, `read-only` or `quarantined` |
| `integrity_reason` | TEXT NULL | The decode reason, for diagnostics |

**The database refuses (G3)**, through `CHECK` constraints:

- `json_valid(details) AND json_type(details) = 'object' AND json_extract(details, '$.v') >= 1`;
- exactly one of `subject_baby_id` and `subject_person` is set;
- `end_utc_ms IS NULL OR end_utc_ms > start_utc_ms`;
- `is_session = 1 OR end_utc_ms IS NULL`;
- `is_deleted IN (0, 1)`, and `integrity IN ('ok', 'read-only', 'quarantined')`.

There is no foreign key on `kind`: the phone must store kinds it does not know (ADR 0010 §7, G3).

**Indexes:** `(household_id, start_utc_ms)` for the timeline, and
`(household_id, subject_baby_id, kind, start_utc_ms)` for "last feed" and the duplicate check.

### 4.2 `baby_profiles` and `household_settings`

One row per record, with the same version and `is_deleted` columns. Nicknames and hidden kinds
are JSON arrays of strings, with `CHECK (json_valid(...) AND json_type(...) = 'array')`. Unknown
kind codes in `hidden_kinds` are kept (Spec 001 §5.3).

`duplicate_dismissals` is added with the duplicates UI (Increment 7), in its own migration.

### 4.3 `outbox`

| Column | Type | Notes |
|---|---|---|
| `seq` | INTEGER PK AUTOINCREMENT | The order of local writes |
| `change_id` | TEXT UNIQUE | UUIDv7, the idempotency key (ADR 0002 §3) |
| `entity_type` | TEXT | `CareEntry`, `BabyProfile` or `HouseholdSettings` |
| `entity_id` | TEXT | |
| `operation` | TEXT | `upsert` or `delete` |
| `version_*` | | The stamp of that write |

The outbox stores **which record changed, and its version**, not a copy of the record. Sync is
state-based (ADR 0002 §3): the push sends the record's current state, read at push time. How
several pending changes to one record are coalesced belongs to the sync spec. This spec only
guarantees that every write leaves its outbox row in the same transaction.

### 4.4 `device_state`, `data_migrations`, `app_state`

- `device_state` is a single row: the `DeviceId` (created once, on first launch) and the hybrid
  logical clock's last stamp.
- `data_migrations` holds the ID and the completion time of each data migration that has run.
- `app_state` holds key–value pairs, starting with `last_integrity_scan_version`.

### 4.5 Why instants are integers

EF Core stores a `DateTimeOffset` in SQLite as text. Text sorts by its characters, so
`14:00+02:00` sorts after `13:30+00:00` even though it happened earlier. The timeline would be
wrong whenever offsets differ: across a time change, while travelling, or with a phone set to the
wrong zone. Instants are therefore stored as **UTC milliseconds, which sort correctly, plus the
offset in minutes**, which is part of the data (ADR 0006 §5). The repository maps them back to
exactly the original `DateTimeOffset`, and a test checks that.

## 5. The write path

`ICareLogStore.SaveAsync(CareEntry entry, CancellationToken)` performs, in **one SQLite
transaction**:

1. Read the stored `details` of this entry, if a row exists. It is the `previous` document, so the
   codec carries over unknown properties (Spec 003 §7).
2. Encode the details through the codec. That is the write gate: a `DetailsEncodingException`
   rolls the whole transaction back.
3. Upsert the row.
4. Append the outbox row.
5. Commit.

**A row is never written outside this path.** Repositories accept Domain objects, never JSON
(ADR 0010 §7, G2). An architecture test enforces that only `Coo.Mobile.Infrastructure/Persistence`
references EF Core.

Tombstones go through the same transaction: set `is_deleted` and the version, keep `details`
unchanged, and append a `delete` outbox row. Deleting a read-only or quarantined row never needs to
decode it.

`IVersionClock.NextAsync` advances and persists the clock in its own small transaction. If a write
then fails, a stamp is skipped. That is harmless: stamps must increase, not be contiguous.

## 6. Reading, and the read guard (G4)

For each row, the repository:

1. Looks up the kind in `EntryKinds`. An unknown kind gives a `ReadOnlyEntry`.
2. Decodes the details with the kind's codec:
   - `Decoded` → rebuilds the entry with `CareEntry.Rehydrate`, giving a `LiveEntry`;
   - `Unrepresentable` → a `ReadOnlyEntry`;
   - `Malformed` → a `QuarantinedEntry`.
3. Records the result in `integrity` if it changed, with the reason.

The read guard **never deletes and never rewrites `details`**. A quarantined row stays byte for
byte as it was, so a later fix (a codec bug fix, or a data migration) can recover it. A row that
fails to decode never throws out of a read: one bad row cannot blank the timeline.

## 7. Startup

`MauiProgram` runs one `StartupTasks.RunAsync(ct)` before the first page is shown:

1. `MigrateAsync()`: the EF schema migrations (never `EnsureCreated`, ADR 0001 §4).
2. **Data migrations**: run each `IDataMigration` not yet in `data_migrations`, in ID order.
   - Each one is idempotent, works in batches of 200 rows, and records itself only when done. A
     crash resumes safely.
   - There are none in v1. The runner is tested with a test migration.
3. **The integrity scan (G5)**, once per app version: decode every row, and update `integrity` in
   both directions. A `read-only` or `quarantined` row that this version can now read becomes
   `ok`. The scan then stores the app version in `app_state`.
4. Create the device identity, and the provisional household, if missing.

On a phone with a few thousand entries, this must finish in under one second. A test measures the
scan over 10,000 rows on the CI machine, and fails above 2 seconds.

## 8. The debug screen (Debug builds only)

The debug screen is a plain, unstyled page in `Coo.Mobile`, compiled only in Debug builds, and
deleted when the Today screen arrives (Increment 6). It has:

- **Log** buttons:
  - diaper wet, or dirty;
  - bottle of 120 ml formula;
  - start and end a sleep;
  - start a left breast timer, switch side, and complete it.
- **The last 20 entries:** time, kind, a one-line summary, and the integrity state.
- **Counters:** outbox rows, read-only entries and quarantined entries.
- **Delete** on each row.
- **Debug helpers:** a "corrupt one row" button that writes a malformed document with raw SQL, and
  a "foreign row" button that writes a row of an unknown kind with raw SQL. These show the read
  guard working on a real phone.
- **The data folder's path, and the app version.**

The first launch creates the provisional household and one test baby, "Test baby", so the buttons
work before onboarding exists.

## 9. Trimming

EF Core finds migrations by reflection, and the trimmed Release build can remove what reflection
needs. This increment therefore includes **one Release build on a real phone**:

- the host roots `Coo.Mobile.Infrastructure` (`TrimmerRootAssembly`). The sibling apps run EF Core in
  trimmed builds, but only with `EnsureCreated`, never with migrations, so this is unproven here;
- a clean install opens, migrates, and logs and lists entries;
- an over-the-top install of a later build keeps the data.

What was needed is recorded in `BUILD.md`. This closes the open question early, before there are
users whose data depends on it.

## 10. Tests

**`tests/Coo.Mobile.Infrastructure.Tests`** run against **real SQLite files** in a temp folder,
never an in-memory fake, because the guards are SQLite behaviour.

| Test | Proves |
|---|---|
| `Migrate_EmptyDatabase_CreatesSchema` | The migrations apply from nothing |
| `Insert_InvalidDetails_IsRefusedByDatabase` (one per `CHECK` in §4.1) | G3 holds even against raw SQL |
| `SaveThenLoad_EveryKind_ReturnsEqualEntry` | The mapping is lossless, for all four kinds |
| `SaveThenLoad_NonUtcOffsets_PreservesOffsets` | §4.5 |
| `GetEntries_MixedOffsets_OrdersByInstant` | §4.5: the reason the columns are integers |
| `Save_WritesOutboxRowInSameTransaction` | ADR 0002 §2 |
| `Save_WhenWriteGateThrows_WritesNeitherRowNorOutbox` | Rollback |
| `Save_StoredRowHasUnknownProperty_KeepsIt` | Spec 003 §7, end to end |
| `Read_UnknownKindRow_ReturnsReadOnlyAndKeepsBytes` | ADR 0010 §3.1 |
| `Read_MalformedRow_QuarantinesWithoutChangingDetails` | G4 |
| `Read_OneMalformedRowAmongMany_ReturnsTheRest` | One bad row never blanks the timeline |
| `Delete_QuarantinedRow_WritesTombstoneAndOutbox` | Nothing gets stuck |
| `IntegrityScan_SameAppVersion_RunsOnce` / `_NewAppVersion_ReevaluatesRows` | G5 |
| `IntegrityScan_10000Rows_FinishesUnderTwoSeconds` | §7 |
| `DataMigrationRunner_PendingMigration_RunsOnceAndRecordsIt`, `_CrashMidway_Resumes` | ADR 0010 §8 |
| `VersionClock_AcrossContexts_KeepsIncreasing` | The clock survives a restart |
| `DeviceIdentity_SecondLaunch_ReturnsSameId` | |

**`tests/Coo.Mobile.Application.Tests`** use NSubstitute for the ports only (never for Domain
types): `LogEntry` happy path, a Domain error writes nothing, and an unknown entry ID on revise.

**Architecture tests:** EF Core is referenced only from `Coo.Mobile.Infrastructure`, and inside it
only from `Persistence/`. `Coo.Mobile.Infrastructure` takes its first packages:
`Microsoft.EntityFrameworkCore.Sqlite` (and the design-time package for creating migrations).

## 11. Increment 4 — deliverables

| # | Deliverable | How you check it |
|---|---|---|
| 1 | Schema, guards and the write path | `dotnet test`: the §10 list, all green, on real SQLite |
| 2 | The debug screen on your phone | Log a few entries, kill the app, reopen it: the entries are still there |
| 3 | The read guard on a real phone | "Corrupt one row": it shows as quarantined, the other entries still show. "Foreign row": it shows as read-only. |
| 4 | The integrity scan | Install a newer build over the old one: it runs once, and the counters stay right |
| 5 | Trimmed Release build | A Release install on your phone opens, migrates and logs |
| 6 | CI green | GitHub Actions |

## 12. Decisions for the owner

| # | Question | Recommendation |
|---|---|---|
| Q1 | A Debug-only, unstyled debug screen now, deleted when the Today screen arrives | **Yes.** It makes this increment testable on the phone without designing screens early. |
| Q2 | The outbox stores which record changed and its version, not a copy, because sync sends current state | **Yes.** It keeps the outbox small and exact. Coalescing is decided in the sync spec. |
| Q3 | Instants stored as UTC milliseconds plus the offset, not as EF's text `DateTimeOffset` | **Yes.** Text ordering breaks across offsets (§4.5). |
| Q4 | One trimmed Release build on your phone as part of this increment | **Yes.** It removes the biggest unknown about EF on Android before anyone's data depends on it. It needs your phone once. |
