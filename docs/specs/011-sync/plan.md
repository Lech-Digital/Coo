# Sync (Increment 11) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or
> superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Two phones signed in to one account converge (log, edit, delete, offline included, nothing lost) after the
person has given explicit, server-recorded consent; a second phone can bring its own entries over; refreshes survive
sign-out races and lost responses; the API is deployed as 0.11.0 and the app is 0.11.0.

**Architecture:** `Coo.Contracts/Sync` holds the wire DTOs. On the server, `Coo.Api.Application/Sync` validates each
change against the Domain (`ChangeValidator`), decides last-writer-wins with `HlcStamp` (`LastWriterWinsRule`), and runs
the push, pull, consent and membership use cases behind the ports `ISyncStore`, `IMembershipStore`, `IConsentStore`;
`Coo.Api.Infrastructure/Persistence` implements them (one EF migration, raw Npgsql for the one-transaction apply and the
four-table pull); `Coo.Api` maps the endpoints, checks membership and consent per request, and purges sync history
daily. On the phone, `Coo.Mobile.Application/Sync` holds the cycle (`SyncCycle`), the trigger scheduler
(`SyncScheduler`), the facade (`SyncEngine`), the consent gate and the bring-over use case behind `ISyncApi`,
`IConsentApi`, `ISyncStore`, `IHouseholdMoves`, `ISyncState`; `Coo.Mobile.Infrastructure` implements them over SQLite
(migration 0004) and HTTP (through the existing refreshing handler); `Coo.Mobile.Platform` watches connectivity; the
host shows the consent and bring-over pages, forwards lifecycle events, and adds the sync line and the "Not accepted by
the server" group. `Account` gains a session generation and `TokenRefresher` proposes the next refresh token.

**Tech Stack:** .NET 10, ASP.NET Core 10 minimal APIs, EF Core 10 + Npgsql (migrations; raw `NpgsqlCommand` for the
sync store), MAUI 10.0.0 (Android), Microsoft.Data.Sqlite, CommunityToolkit.Mvvm, System.Text.Json source generation,
xUnit 2.9 + NSubstitute + FluentAssertions 7, `Microsoft.Extensions.TimeProvider.Testing` (`FakeTimeProvider`),
Testcontainers PostgreSQL 17, `WebApplicationFactory`.

**Spec:** `docs/specs/011-sync/spec.md` (accepted 2026-10-02, Q1–Q8 as recommended). Also read ADR 0002 (the protocol;
§3–§7 and §10 are implemented here), ADR 0010 §1 and §3 (four synced types; nothing unknown is lost), ADR 0003 §6–§7
(roles; bringing entries over), and Spec 010's plan for the code this builds on.

## Global Constraints

- Work on `main` (owner's rule: only developer, unreleased). One commit per task; push at the end (Task 23); CI runs on
  push to `main`.
- Commits authored as `adziusmaster <adzius.lech@gmail.com>` (repo-local config, already set). **No `Co-Authored-By`
  trailer** and no "Generated with" line. The website repository (Task 22) commits as the same author.
- Zero warnings (`TreatWarningsAsErrors`); `dotnet format Coo.Tests.slnf --verify-no-changes` passes; the architecture
  tests stay green (the project graph does not change; no new project).
- No `TimeProvider.System`, `DateTime.Now/UtcNow`, `DateTimeOffset.Now/UtcNow`, `Guid.NewGuid()`, parameterless
  `Guid.CreateVersion7()` or `System.Random` outside the two hosts (`BannedSymbols.txt`). Random values (the proposed
  refresh token, backoff jitter) use `RandomNumberGenerator`. Times come from the injected `TimeProvider`.
- No MAUI types in `Coo.Mobile.Application`, `Coo.Mobile.Presentation` or `Coo.Mobile.Platform`. **No
  `System.Text.Json` in `Coo.Mobile.Application`, `Coo.Api.Application` or `Coo.Mobile.Presentation`:** details travel
  through those layers as a JSON *string* (`DetailsJson`); `JsonElement` exists only in `Coo.Contracts` DTOs and is
  converted at the HTTP edge (`Coo.Api/Hosting`, `Coo.Mobile.Infrastructure/Sync`).
- SQLite only in `Coo.Mobile.Infrastructure.Persistence`; EF Core and Npgsql only in `Coo.Api.Infrastructure.Persistence`
  (and `ServiceCollectionExtensions`). Infrastructure types are `internal` except `ServiceCollectionExtensions`.
- Every user-facing string goes in `eng/strings/strings.py` in all seven languages (en, de, fr, es, it, pl, nl), then
  `python3 eng/strings/generate.py`. English copy is the spec's, verbatim.
- Tests: `MethodName_StateUnderTest_ExpectedBehavior`, strict `// Arrange` / `// Act` / `// Assert`, mock only outbound
  ports (NSubstitute or hand-written in-memory fakes), build Domain objects for real. Tests that set a language join
  `[Collection(nameof(StringsState))]` where the existing tests do, and reset with `Strings.Use(LanguagePreference.System,
  null)` in `Dispose`. **No sleeps as synchronisation:** time moves with `FakeTimeProvider.Advance`; concurrency is held
  with `TaskCompletionSource`; background work is awaited through `SyncScheduler.WhenIdleAsync()`.
- **TDD with a captured RED:** every task's "verify it fails" step is run, and the failing output (the first error
  line per test) is pasted into the task's review notes before the implementation is written.
- `CancellationToken` on every IO-bound method, passed down to IO.
- **Fire-and-forget code catches everything** (Increment 10 lesson): MAUI `Command`/`Clicked` handlers,
  `Dispatcher.Dispatch(async …)`, timer callbacks, `_ = SomethingAsync()` and the scheduler loop each end in a catch-all
  (`#pragma warning disable CA1031` with a one-line reason where the analyzer asks). An escaped exception crashes the app.
- **Java callbacks never let a managed exception escape** (the connectivity callback, Task 19).
- **Phone HTTP:** `JsonException`, `NotSupportedException`, `InvalidOperationException`, `HttpRequestException` and
  `IOException` while reading a body mean "unavailable" (a failed cycle), never a crash; only the server's explicit codes
  mean "refused". **Only `auth.refresh-invalid` signs out** (already in `TokenRefresher`; kept).
- **BackgroundService loops** filter their inner catch with `when (!stoppingToken.IsCancellationRequested)`, so a stray
  `OperationCanceledException` (a command timeout) never stops the host.
- **Single-statement atomic SQL** for every compare-and-swap or consume (the refresh rotation, the `applied_changes`
  insert, the consent insert); **a unique index or primary key backs every `ON CONFLICT`**.
- Error responses are codes, never server text (ADR 0006): `{ "code": "…" }`, except the membership-revoked problem body
  (`application/problem+json` with `type` and `householdId`, ADR 0002 §10).
- Protocol numbers (spec §6, ADR 0002): push ≤ **200** changes per request; pull ≤ **500** records per page; foreground
  pull every **30 s**; local writes debounced **1 s**; backoff from **2 s** doubling to **5 min**, jittered ×[0.5, 1];
  stamps more than **24 h** ahead are rejected; `applied_changes` and `sync_conflicts` kept **30 days**;
  `schemaVersion` **1** (current and oldest accepted).
- Consent: purpose `care-data`, wording **`care-data-v1`**.
- NuGet: no new package versions. `Microsoft.Extensions.TimeProvider.Testing` (already central) is added as a reference
  to `tests/Coo.Mobile.Application.Tests` only. The Credential Manager bindings stay pinned (1.5.0 / 1.5.0 / 1.1.0.8).
  If a package ever has to be added, confirm the version with `dotnet package search <id> --exact-match` first.
- App version **0.11.0**, versionCode **14** (0.10.0 was 13 in `Coo.Mobile.csproj`); API version **0.11.0**.
- Run tests with `dotnet test Coo.Tests.slnf` on the global SDK (**Docker Desktop running** for PostgreSQL). Before
  pushing, also `TZ=UTC dotnet test Coo.Tests.slnf`. Build the app with the MAUI SDK (`BUILD.md`); a Debug APK
  installed with `adb install` needs `-p:EmbedAssembliesIntoApk=true`.
- **Production:** the owner allows the agent to deploy (`./deploy/deploy-prod.sh --confirm`; its interlocks need a clean,
  committed backend and green tests). Read-only `ssh coldstart-prod` queries are fine; no other SSH writes.
- **Phones:** the agent drives the owner's Pixel 10 with `adb` (serial `5B130DLCR00011`) and the emulator (Task 21).
  In Google's account sheet always pick **adzius.lech@gmail.com**. Phone data is test data.
- Copy must be true for the real release (owner's rule): no string, page or policy line may promise something 0.11.0
  does not do.

## Review Focus

1. **Upgrading from 0.10.0 while already signed in** (the owner's Pixel): there is no sign-in event, the stored session
   is format v1 (no pending refresh token), and nothing may be uploaded before consent. Pinned in Task 9
   (`Parse_VersionOneSession_ReadsWithoutAPendingToken`) and Task 11
   (`RunAsync_SignedInWithoutConsent_NeitherPushesNorPulls`).
2. **Offline in the foreground:** the 30-second tick must not hammer the network during backoff, and connectivity coming
   back must sync at once. Pinned in Task 12 (`Request_PeriodicDuringBackoff_WaitsForTheBackoff`,
   `Request_ConnectivityRegained_ResetsTheBackoffAndRuns`).
3. **A captive portal's `200` HTML page or a proxy's bare `403` on a sync call:** a failed cycle; nothing deleted,
   soft-locked or crashed. Pinned in Task 15 (`PullAsync_HtmlBody_IsUnavailable`, `PushAsync_Bare403OrAnOutage_IsUnavailable`,
   `PushAsync_RevokedProblemForAnotherHousehold_IsUnavailable`).
4. **An entry created and deleted while offline, never on the server:** its delete is `applied` with nothing stored, the
   outbox drains, and no other phone ever sees it. Pinned in Task 5 (`ApplyAsync_DeleteOfAnUnknownRecord_IsAppliedAndStoresNothing`)
   and Task 7 (the convergence scenario's entry `Z`).
5. **Signing out (or in again) while a cycle is between push batches:** no further batch, no pull into the old account's
   household. Pinned in Task 11 (`RunAsync_SignedOutBetweenBatches_StopsBeforeTheNextBatch`).

## Rulings on what the spec leaves open

Recorded so the reviewer can check them against the spec; Task 23 copies them into the spec's "Built and verified".

1. **Deletes carry no payload** (ADR 0002 §3). A delete for a record the server has never stored is `applied` and stores
   nothing: only the creating phone ever had it. The server keeps a stored record's columns and flips `is_deleted`.
2. **Per-change shape problems** (unknown entity type, upsert without its payload, a dismissal pairing an entry with
   itself, an unparsable person or source) are `rejected` with the new reason **`sync.change-malformed`**; only a request
   that cannot be read at all, or names no household or device, is `400 sync.malformed`.
3. **Role rule (ADR 0003 §6):** `BabyProfile` and `HouseholdSettings` changes (deletes too) need `CanManageBaby`;
   `CareEntry` and `DuplicateDismissal` need any active membership.
4. **`sync_conflicts`** stores both version stamps, the change ID and the record's identity, not payloads (the winner is
   the row; data minimisation). It is purged with `applied_changes` after 30 days.
5. **The server issues no HLC stamps**, so there is no server clock to advance; it only rejects stamps over 24 h ahead.
6. **Stored details on the server** are the codec's encoding of the decoded details with the *received* document's
   unknown top-level properties merged back (`Encode(details, previous: received)`), so a newer field a phone carried is
   kept. The phone stores pulled details exactly as received.
7. **The active household:** `app_state.active_household` names the household the UI shows; `IHouseholdStore.
   ListHouseholdsAsync` returns it first, so every existing `First(h => !h.IsDeleted)` keeps working when a second
   household (the account's, pulled while join-pending) is on the phone.
8. **Join-pending (spec §4 vs §6.3):** a cycle is the account household's **pull only**, never a push, so the
   bring-over sheet can name the family's babies; nothing of this phone's is uploaded until the person chooses.
9. **`created`** is detected as "signed in, not join-pending, active household ≠ account household" and handled by the
   cycle before its first push. A sign-in answered `existing` whose household differs from this phone's is stored as
   join-pending (a phone that signed out while join-pending must still be asked).
10. **Consent states:** `Unknown` / `Given` / `Declined`, cached per account in `app_state`. "Not now" is remembered until
    the next sign-in; the sheet opens by itself once per app run while consent is `Unknown`, and from **Turn on sharing**
    any time. Consent is checked against the server (`/api/me`) when the cache says `Unknown`. `POST /api/consents`
    carries `appVersion` and `language` in its body (additive fields), as the spec requires them recorded.
11. **The sync line:** "Sharing n changes…" only when n > 0 (a pull-only cycle every 30 s must not flicker the line); a
    failed cycle with nothing pending reads "Waiting for a connection."; before the first cycle with nothing pending the
    line is empty.
12. **Backoff:** Start, Resume, connectivity regained and **Sync now** reset it and run at once; the periodic tick, local
    writes and going to the background wait for it.
13. **`membership-revoked` (Q7):** the engine soft-locks, confirms through `/membership`, and deletes the household only
    when `SyncEngine.AcknowledgeRemovalAsync` is called. The removal sheet and hiding the household's UI ship with 11b,
    where removal exists; no phone can reach that state before then.
14. **"Not accepted by the server"** lists rejected **care entries** (Edit, Delete), 50 at a time with **Show more**;
    rejected profile, settings or dismissal changes are kept and counted on the debug screen (no shipped build can
    produce them: the phone's rules are the server's).
15. **Consent and bring-over are modal pages**, not in-page sheets, so they can open over whichever page is showing.
16. **A pull page that says `hasMore` without moving the cursor** fails the cycle (no endless loop).
17. **The daily purge is its own `SyncHistoryCleanupJob`**, beside the nonce job, rather than a renamed one.
18. **`nextRefreshToken`** must be 43–128 characters and differ from the presented token, else `auth.refresh-invalid`
    with nothing changed. `Account.UpdateProfileAsync` (Settings' name refresh) does not move the generation.
19. **Schema versions:** `SchemaVersions.Current = Oldest = 1`; a request below `Oldest` is `426 client-too-old`; a change
    above `Current` is `rejected sync.schema-unsupported`. The N−1 seam is `SchemaUpgrade.ToCurrent` (identity today).
20. **The bootstrap skip needs an empty phone.** ADR 0002 §6 skips tombstones at or below the head because "a device
    pulling from cursor 0 has nothing to delete". A phone that pushed before its first pull succeeded (the first cycle
    is push, then pull) is not empty: another phone may have deleted one of its entries meanwhile. So a phone that holds
    any entry, baby or dismissal of the household pulls from cursor 0 with **`bootstrapHead=0`**, which skips nothing;
    only an empty phone (a join-pending bootstrap, a Fresh bootstrap) lets the server choose the head. No server change:
    the server already honours a head it is given. Pinned in Task 5, Task 7 and Task 13.

---

## File Structure

| File | Responsibility |
|---|---|
| `src/Shared/Coo.Contracts/Sync/SyncContracts.cs` (create) | Wire DTOs, entity types, results, error and rejection codes, limits, consent wording |
| `src/Shared/Coo.Contracts/Auth/AuthContracts.cs` (modify) | `RefreshRequest.NextRefreshToken`, `MeResponse.Consent` |
| `src/Backend/Coo.Api.Application/Sync/Model.cs` (create) | Incoming changes, payloads, validated writes, results, pull page |
| `src/Backend/Coo.Api.Application/Sync/SyncText.cs` (create) | `this-device` / `user:<id>` and source text (shared with `ServerColumns`) |
| `src/Backend/Coo.Api.Application/Sync/ChangeValidator.cs` (create) | A change against the Domain, the role rule, the stamp bound |
| `src/Backend/Coo.Api.Application/Sync/LastWriterWinsRule.cs` (create) | The LWW decision used inside the store's transaction |
| `src/Backend/Coo.Api.Application/Sync/Ports.cs` (create) | `ISyncStore`, `IMembershipStore`, `IConsentStore` |
| `src/Backend/Coo.Api.Application/Sync/UseCases.cs` (create) | `SyncAccess`, `PushChanges`, `PullChanges`, `RecordConsent`, `GetMembershipStatus`, `PurgeSyncHistory`, `SchemaUpgrade` |
| `src/Backend/Coo.Api.Application/Auth/Sessions.cs`, `Ports.cs`, `AccountUseCases.cs` (modify) | Proposed next refresh token; `GetMe` consent |
| `src/Backend/Coo.Api.Infrastructure/Persistence/Records.cs`, `CooDbContext.cs` (modify), `Migrations/*_Sync.cs` (generated) | New tables, `memberships.revoked_at` |
| `src/Backend/Coo.Api.Infrastructure/Persistence/CareEntryWriter.cs` (create), `CareEntryStore.cs`, `ServerColumns.cs`, `DeviceSessionStore.cs` (modify) | The shared upsert; text forms delegate to `SyncText`; current hash on lookup |
| `src/Backend/Coo.Api.Infrastructure/Persistence/SyncStore.cs`, `MembershipStore.cs`, `ConsentStore.cs` (create) | The stores |
| `src/Backend/Coo.Api/Hosting/SyncEndpoints.cs`, `SyncMapping.cs`, `SyncHistoryCleanupJob.cs` (create); `AuthEndpoints.cs`, `Program.cs`, `Coo.Api.csproj` (modify) | Endpoints, mapping, purge, wiring, 0.11.0 |
| `tests/Coo.Api.Application.Tests/Sync*.cs`, `tests/Coo.Api.Infrastructure.Tests/Sync*.cs`, `tests/Coo.Api.IntegrationTests/Sync*.cs`, `ConvergenceTests.cs`, `SimulatedDevice.cs` (create) | Server tests |
| `src/Mobile/Coo.Mobile.Application/Auth/Account.cs`, `Ports.cs`, `UseCases.cs` (modify) | Generation, pending refresh token, consent on `/api/me` |
| `src/Mobile/Coo.Mobile.Application/Sync/*.cs` (create) | Model, ports, `PullRule`, `LocalWrites`, `ConsentGate`, `SyncCycle`, `SyncScheduler`, `SyncEngine`, use cases |
| `src/Mobile/Coo.Mobile.Application/CareLog/Attention.cs`, `Ports.cs` (modify) | Rejected entries in Needs attention; `IVersionClock.ObserveAsync` |
| `src/Mobile/Coo.Mobile.Infrastructure/Persistence/Migrations/0004-sync.sql` (create) | `sync_cursors`, `sync_rejections` |
| `src/Mobile/Coo.Mobile.Infrastructure/Persistence/SyncStore.cs`, `HouseholdMoves.cs`, `SyncState.cs` (create); `HouseholdStore.cs`, `CareLogStore.cs`, `DuplicateDismissalStore.cs`, `DeviceState.cs`, `Outbox.cs` (modify) | SQLite sync |
| `src/Mobile/Coo.Mobile.Infrastructure/Sync/SyncClient.cs`, `SyncHttp.cs`, `SyncJsonContext.cs` (create); `Auth/*.cs` (modify) | HTTP; the refresher |
| `src/Mobile/Coo.Mobile.Presentation/Settings/SyncLine.cs`, `ConsentViewModel.cs`, `BringOverViewModel.cs` (create); `AccountViewModel.cs`, `Attention/AttentionViewModel.cs`, `Today/Undo.cs`, `Localization/Strings.cs` (modify) | View models |
| `src/Mobile/Coo.Mobile.Platform/Sync/AndroidConnectivity.cs` (create) | Connectivity regained |
| `src/Mobile/Coo.Mobile/Sync/SyncPrompter.cs`, `Pages/ConsentPage.cs`, `Pages/BringOverPage.cs` (create); `App.xaml.cs`, `StartupPage.cs`, `MauiProgram.cs`, `Pages/SettingsPage.cs`, `Pages/AttentionPage.cs`, `DebugScreen/DebugPage.cs`, `Coo.Mobile.csproj` (modify) | The host |
| `eng/strings/strings.py` (modify) + generated `.resx` | Consent, bring-over, sync line, rejected group, reasons |
| `deploy/**`, `DEPLOY.md`, `BUILD.md`, `docs/**` (modify) | Deploy notes, the emulator, the record |
| `/Users/andrzej.lech/Code/private/lech-digital/projects/coo/privacy/index.html`, `delete-account/index.html`, `sitemap.xml` (modify, other repo) | The policy in the present tense |

---
### Task 1: The sync contracts, and a change checked against the Domain (`Coo.Api.Application/Sync`)

**Files:**
- Create: `src/Shared/Coo.Contracts/Sync/SyncContracts.cs`
- Modify: `src/Shared/Coo.Contracts/Auth/AuthContracts.cs`
- Create: `src/Backend/Coo.Api.Application/Sync/Model.cs`, `SyncText.cs`, `ChangeValidator.cs`, `LastWriterWinsRule.cs`
- Test: `tests/Coo.Api.Application.Tests/SyncValidationTests.cs`

**Interfaces:**
- Consumes: Domain `CareEntry.Create`, `BabyProfile.Create`, `HouseholdSettings.Create`, `DuplicateDismissal.Create`,
  `EntryPair`, `HlcStamp.IsTooFarAhead`, `Membership.CanManageBaby`, `Note.From`, `TimeRange.Create`; Codecs
  `EntryDetailsCodecs.For(EntryKindCode)` and `Decode`.
- Produces (later tasks rely on these exact names):
  - Contracts: everything in `SyncContracts.cs` below; `RefreshRequest(string RefreshToken, string? NextRefreshToken = null)`;
    `MeResponse(UserDto User, MembershipDto? Household, IReadOnlyList<DeviceSessionDto> Devices, ConsentDto? Consent = null)`.
  - `IncomingChange(Guid ChangeId, string EntityType, Guid EntityId, bool IsDelete, HlcStamp Version, ChangePayload? Payload)`
  - `ChangePayload` subtypes `CareEntryPayload`, `BabyProfilePayload`, `HouseholdSettingsPayload`, `DismissalPayload`
  - `SyncWrite` subtypes `CareEntryWrite(CareEntry Entry, string ReceivedDetails)`, `BabyProfileWrite(BabyProfile Baby)`,
    `HouseholdSettingsWrite(HouseholdSettings Settings)`, `DismissalWrite(DuplicateDismissal Dismissal)`, `TombstoneWrite`
  - `ChangeOutcome { Applied, Superseded, Rejected }`, `ChangeResult(ChangeOutcome Outcome, string? Reason = null)`,
    `ChangeVerdict(Guid ChangeId, ChangeResult Result)`
  - `StoredVersion(HouseholdId Household, HlcStamp Version)`, `WriteDecision { Apply, Superseded, HouseholdMismatch }`,
    `LastWriterWinsRule.Decide(HouseholdId household, StoredVersion? stored, HlcStamp incoming)`
  - `PulledRecord(string EntityType, Guid EntityId, HlcStamp Version, bool IsDeleted, long Seq, ChangePayload Payload)`,
    `PullPage(IReadOnlyList<PulledRecord> Records, long Cursor, bool HasMore, long? BootstrapHead)`
  - `ChangeValidator.Validate(IncomingChange change, HouseholdId household, Membership member, int schemaVersion, DateTimeOffset now)`
    → `Result<SyncWrite>` whose error code is the rejection reason
  - `SyncText.ThisDevice`, `SyncText.Person(PersonRef)`, `SyncText.ParsePerson(string?)`, `SyncText.Source(EntrySource)`,
    `SyncText.ParseSource(string?, string?)`

- [ ] **Step 1: The contracts** (no test of their own; every later test exercises them)

`src/Shared/Coo.Contracts/Sync/SyncContracts.cs`:

```csharp
using System.Text.Json;

namespace Coo.Contracts.Sync;

// Wire DTOs for sync (Spec 011 §6–§7, ADR 0002 §3, §5, §6, §10, §11). Versioned by adding, never by renaming.
// JsonElement keeps a care entry's details exactly as written; the Application layers see them as a string.

public sealed record HlcStampDto(long WallClockMs, int Counter, Guid DeviceId);

public sealed record CareEntryDto(
    string Kind, Guid? SubjectBabyId, string? SubjectPerson, bool IsSession, DateTimeOffset StartAt, DateTimeOffset? EndAt,
    string LoggedBy, string Source, string? SourcePromptVersion, string? Note, JsonElement Details);

public sealed record BabyProfileDto(string Name, IReadOnlyList<string> Nicknames, DateOnly? BirthDate, DateOnly? DueDate);

/// <summary><see cref="VolumeUnit"/> is <c>ml</c> or <c>oz</c>; <see cref="HiddenKinds"/> are kind codes.</summary>
public sealed record HouseholdSettingsDto(string VolumeUnit, string TimeZoneId, IReadOnlyList<string> HiddenKinds);

public sealed record DuplicateDismissalDto(Guid EntryA, Guid EntryB);

/// <summary>One change (ADR 0002 §3). <see cref="Operation"/> is one of <see cref="SyncOperations"/>; an upsert carries the
/// payload of its <see cref="EntityType"/>, a delete carries none.</summary>
public sealed record SyncChangeDto(
    Guid ChangeId, string EntityType, Guid EntityId, string Operation, HlcStampDto Version,
    CareEntryDto? CareEntry = null, BabyProfileDto? BabyProfile = null, HouseholdSettingsDto? HouseholdSettings = null,
    DuplicateDismissalDto? DuplicateDismissal = null);

public sealed record PushRequest(Guid HouseholdId, Guid DeviceId, int SchemaVersion, IReadOnlyList<SyncChangeDto> Changes);

/// <summary><see cref="Result"/> is one of <see cref="ChangeResults"/>; <see cref="Reason"/> is set for <c>rejected</c>.</summary>
public sealed record ChangeResultDto(Guid ChangeId, string Result, string? Reason);

public sealed record PushResponse(IReadOnlyList<ChangeResultDto> Results);

/// <summary>A record's current state, tombstones included, with its full payload.</summary>
public sealed record SyncRecordDto(
    string EntityType, Guid EntityId, HlcStampDto Version, bool IsDeleted, long Seq,
    CareEntryDto? CareEntry = null, BabyProfileDto? BabyProfile = null, HouseholdSettingsDto? HouseholdSettings = null,
    DuplicateDismissalDto? DuplicateDismissal = null);

public sealed record PullResponse(IReadOnlyList<SyncRecordDto> Records, long Cursor, bool HasMore, long? BootstrapHead);

/// <summary><see cref="Status"/> is one of <see cref="MembershipStatuses"/>.</summary>
public sealed record MembershipStatusResponse(string Status);

public sealed record ConsentRequest(string Purpose, string Wording, string? AppVersion = null, string? Language = null);

public sealed record ConsentDto(string Wording, DateTimeOffset GivenAt);

/// <summary>The fields of a problem body the phone reads (ADR 0002 §10): only this exact type, for this household, counts.</summary>
public sealed record ProblemResponse(string? Type, int? Status, Guid? HouseholdId);

public static class SyncEntityTypes
{
    public const string CareEntry = "CareEntry";
    public const string BabyProfile = "BabyProfile";
    public const string HouseholdSettings = "HouseholdSettings";
    public const string DuplicateDismissal = "DuplicateDismissal";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        CareEntry, BabyProfile, HouseholdSettings, DuplicateDismissal,
    };
}

public static class SyncOperations
{
    public const string Upsert = "upsert";
    public const string Delete = "delete";
}

public static class ChangeResults
{
    public const string Applied = "applied";
    public const string Superseded = "superseded";
    public const string Rejected = "rejected";
}

/// <summary>Request-level refusals (Spec 011 §7.1). Once released, a code is never renamed.</summary>
public static class SyncErrors
{
    public const string Malformed = "sync.malformed";
    public const string BatchTooLarge = "sync.batch-too-large";
    public const string ConsentRequired = "sync.consent-required";
    public const string NotAMember = "sync.not-a-member";
    public const string ClientTooOld = "client-too-old";
    public const string ConsentUnknownWording = "consent.unknown-wording";
    public const string MembershipRevokedType = "https://coo.lechdigital.nl/problems/membership-revoked";
}

/// <summary>Per-change <c>rejected</c> reasons beside the Domain's own codes (Spec 011 §7.1; ruling 2).</summary>
public static class RejectionReasons
{
    public const string SchemaUnsupported = "sync.schema-unsupported";
    public const string StampInFuture = "sync.stamp-in-future";
    public const string HouseholdMismatch = "sync.household-mismatch";
    public const string PersonUnresolved = "sync.person-unresolved";
    public const string RoleForbidden = "sync.role-forbidden";
    public const string ChangeMalformed = "sync.change-malformed";
}

public static class SchemaVersions
{
    public const int Current = 1;

    /// <summary>N−1 (ADR 0002 §11). There is no version before 1, so today the oldest accepted is the current one.</summary>
    public const int Oldest = 1;
}

public static class SyncLimits
{
    public const int MaxPushChanges = 200;
    public const int MaxPullRecords = 500;
}

/// <summary>The consent of Spec 011 §3. A new wording asks again.</summary>
public static class CareDataConsent
{
    public const string Purpose = "care-data";
    public const string CurrentWording = "care-data-v1";
}

public static class MembershipStatuses
{
    public const string Active = "active";
    public const string Revoked = "revoked";
}
```

In `src/Shared/Coo.Contracts/Auth/AuthContracts.cs`, add `using Coo.Contracts.Sync;` and change two records (additive,
so 0.10.0 phones and servers keep working):

```csharp
/// <summary><see cref="NextRefreshToken"/> is the token the phone proposes as the next one (Spec 011 §8.2); 0.10.0 phones omit it.</summary>
public sealed record RefreshRequest(string RefreshToken, string? NextRefreshToken = null);

public sealed record MeResponse(UserDto User, MembershipDto? Household, IReadOnlyList<DeviceSessionDto> Devices, ConsentDto? Consent = null);
```

Run: `dotnet build Coo.Tests.slnf`
Expected: builds (Contracts references nothing; `System.Text.Json` is in the shared framework, so the
`PackageReferences_SharedCoreProject_ReferencesNoPackage` test stays green).

- [ ] **Step 2: Write the failing tests**

`tests/Coo.Api.Application.Tests/SyncValidationTests.cs`:

```csharp
using Coo.Api.Application.Sync;
using Coo.Contracts.Sync;
using Coo.Domain.CareLog;
using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;
using Coo.Domain.Households;

namespace Coo.Api.Application.Tests;

/// <summary>Spec 011 §6.4: each change against its schema version's rules, the role rule, the stamp bound.</summary>
public sealed class SyncValidationTests
{
    internal static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
    internal static readonly HouseholdId Household = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a1"));
    internal static readonly UserId Parent = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000c1"));
    internal static readonly DeviceId Phone = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000d1"));
    internal static readonly Guid Baby = Guid.Parse("7a1c0000-0000-7000-8000-0000000000b1");
    private static readonly Membership Owner = Membership.Create(Household, Parent, Role.Owner, Now);
    private static readonly Membership Nanny = Membership.Create(Household, Parent, Role.Caregiver, Now);

    internal static HlcStamp Stamp(long offsetMs = 0) => new(Now.ToUnixTimeMilliseconds() + offsetMs, 0, Phone);

    internal static CareEntryPayload Diaper(DateTimeOffset at, string loggedBy = "user:7a1c0000-0000-7000-8000-0000000000c1") =>
        new("diaper", Baby, null, false, at, null, loggedBy, "manual", null, null, """{"v":1,"contents":"wet"}""");

    internal static IncomingChange Upsert(Guid id, ChangePayload payload, string type = SyncEntityTypes.CareEntry, long offsetMs = 0) =>
        new(Guid.CreateVersion7(Now.AddMilliseconds(offsetMs)), type, id, IsDelete: false, Stamp(offsetMs), payload);

    internal static Guid Id(int n) => Guid.Parse("7a1c0000-0000-7000-8000-" + n.ToString("D12", System.Globalization.CultureInfo.InvariantCulture));

    [Fact]
    public void Validate_ValidDiaper_IsACareEntryWriteKeepingTheReceivedDetails()
    {
        // Arrange
        var change = Upsert(Id(1), Diaper(Now.AddMinutes(-5)));

        // Act
        var result = ChangeValidator.Validate(change, Household, Owner, SchemaVersions.Current, Now);

        // Assert
        result.IsSuccess.Should().BeTrue();
        var write = result.Value.Should().BeOfType<CareEntryWrite>().Subject;
        write.Entry.Id.Value.Should().Be(Id(1));
        write.Entry.HouseholdId.Should().Be(Household);
        write.ReceivedDetails.Should().Be("""{"v":1,"contents":"wet"}""");
    }

    [Fact]
    public void Validate_SchemaNewerThanTheServer_IsRejectedAsUnsupported()
    {
        // Arrange
        var change = Upsert(Id(1), Diaper(Now));

        // Act
        var result = ChangeValidator.Validate(change, Household, Owner, SchemaVersions.Current + 1, Now);

        // Assert
        result.Error!.Code.Should().Be(RejectionReasons.SchemaUnsupported);
    }

    [Fact]
    public void Validate_StampMoreThanADayAhead_IsRejected()
    {
        // Arrange
        var change = Upsert(Id(1), Diaper(Now), offsetMs: (long)TimeSpan.FromHours(25).TotalMilliseconds);

        // Act
        var result = ChangeValidator.Validate(change, Household, Owner, SchemaVersions.Current, Now);

        // Assert
        result.Error!.Code.Should().Be(RejectionReasons.StampInFuture);
    }

    [Theory]
    [InlineData("this-device", null)]
    [InlineData("user:7a1c0000-0000-7000-8000-0000000000c1", "this-device")]
    public void Validate_ThisDevicePerson_IsRejectedAsUnresolved(string loggedBy, string? subjectPerson)
    {
        // Arrange: pumping is about a caregiver, so its subject is a person too.
        ChangePayload payload = subjectPerson is null
            ? Diaper(Now, loggedBy)
            : new CareEntryPayload("pump", null, subjectPerson, true, Now.AddMinutes(-20), Now.AddMinutes(-5), loggedBy, "manual", null, null, """{"v":1}""");
        var change = Upsert(Id(1), payload);

        // Act
        var result = ChangeValidator.Validate(change, Household, Owner, SchemaVersions.Current, Now);

        // Assert
        result.Error!.Code.Should().Be(RejectionReasons.PersonUnresolved);
    }

    [Fact]
    public void Validate_SessionEndingBeforeItStarts_IsRejectedWithTheDomainCode()
    {
        // Arrange
        var payload = new CareEntryPayload("sleep", Baby, null, true, Now.AddMinutes(-10), Now.AddMinutes(-30),
            "user:7a1c0000-0000-7000-8000-0000000000c1", "manual", null, null, """{"v":1}""");

        // Act
        var result = ChangeValidator.Validate(Upsert(Id(1), payload), Household, Owner, SchemaVersions.Current, Now);

        // Assert
        result.Error!.Code.Should().Be(TimeRange.EndNotAfterStart);
    }

    [Fact]
    public void Validate_DetailsThatDoNotDecode_IsRejectedWithTheCodecReason()
    {
        // Arrange: a breast feed with no segments (the debug screen's "corrupt" row).
        var payload = new CareEntryPayload("feeding", Baby, null, true, Now.AddMinutes(-10), null,
            "user:7a1c0000-0000-7000-8000-0000000000c1", "timer", null, null, """{"v":1,"method":"breast","completed":true,"segments":[]}""");

        // Act
        var result = ChangeValidator.Validate(Upsert(Id(1), payload), Household, Owner, SchemaVersions.Current, Now);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().NotBe(RejectionReasons.ChangeMalformed);
    }

    [Fact]
    public void Validate_UnknownKind_IsRejectedAsKindUnknown()
    {
        // Arrange
        var payload = Diaper(Now) with { Kind = "medication" };

        // Act
        var result = ChangeValidator.Validate(Upsert(Id(1), payload), Household, Owner, SchemaVersions.Current, Now);

        // Assert
        result.Error!.Code.Should().Be(EntryErrors.KindUnknown);
    }

    [Fact]
    public void Validate_CaregiverChangingTheBaby_IsRejectedByTheRoleRule()
    {
        // Arrange
        var payload = new BabyProfilePayload("Maja", [], new DateOnly(2026, 8, 20), null);

        // Act
        var result = ChangeValidator.Validate(Upsert(Id(2), payload, SyncEntityTypes.BabyProfile), Household, Nanny, SchemaVersions.Current, Now);

        // Assert
        result.Error!.Code.Should().Be(RejectionReasons.RoleForbidden);
    }

    [Fact]
    public void Validate_SettingsForAnotherHousehold_IsAHouseholdMismatch()
    {
        // Arrange
        var payload = new HouseholdSettingsPayload("ml", "Europe/Amsterdam", []);

        // Act
        var result = ChangeValidator.Validate(Upsert(Id(9), payload, SyncEntityTypes.HouseholdSettings), Household, Owner, SchemaVersions.Current, Now);

        // Assert
        result.Error!.Code.Should().Be(RejectionReasons.HouseholdMismatch);
    }

    [Theory]
    [InlineData("Medication")]
    [InlineData("")]
    public void Validate_UnknownEntityType_IsMalformed(string type)
    {
        // Act
        var result = ChangeValidator.Validate(Upsert(Id(1), Diaper(Now), type), Household, Owner, SchemaVersions.Current, Now);

        // Assert
        result.Error!.Code.Should().Be(RejectionReasons.ChangeMalformed);
    }

    [Fact]
    public void Validate_UpsertWithTheWrongPayload_IsMalformed()
    {
        // Act
        var result = ChangeValidator.Validate(
            Upsert(Id(1), new DismissalPayload(Id(3), Id(4))), Household, Owner, SchemaVersions.Current, Now);

        // Assert
        result.Error!.Code.Should().Be(RejectionReasons.ChangeMalformed);
    }

    [Fact]
    public void Validate_DismissalPairingAnEntryWithItself_IsMalformed()
    {
        // Act
        var result = ChangeValidator.Validate(
            Upsert(Id(5), new DismissalPayload(Id(3), Id(3)), SyncEntityTypes.DuplicateDismissal), Household, Owner, SchemaVersions.Current, Now);

        // Assert
        result.Error!.Code.Should().Be(RejectionReasons.ChangeMalformed);
    }

    [Fact]
    public void Validate_Delete_IsATombstoneWithoutAPayload()
    {
        // Arrange
        var change = new IncomingChange(Guid.CreateVersion7(Now), SyncEntityTypes.CareEntry, Id(1), IsDelete: true, Stamp(), null);

        // Act
        var result = ChangeValidator.Validate(change, Household, Owner, SchemaVersions.Current, Now);

        // Assert
        result.Value.Should().BeOfType<TombstoneWrite>().Which.EntityId.Should().Be(Id(1));
    }

    [Fact]
    public void Validate_SettingsWithAnUnknownHiddenKind_KeepsIt()
    {
        // Arrange: a newer phone may hide a kind this server build does not know (ADR 0010 §6).
        var payload = new HouseholdSettingsPayload("oz", "Europe/Warsaw", ["solids"]);

        // Act
        var result = ChangeValidator.Validate(
            Upsert(Household.Value, payload, SyncEntityTypes.HouseholdSettings), Household, Owner, SchemaVersions.Current, Now);

        // Assert
        var settings = result.Value.Should().BeOfType<HouseholdSettingsWrite>().Subject.Settings;
        settings.VolumeUnit.Should().Be(VolumeUnit.Ounces);
        settings.HiddenKinds.Select(k => k.Value).Should().Equal("solids");
    }

    [Theory]
    [InlineData(false, 0, WriteDecision.Apply)]
    [InlineData(true, -1, WriteDecision.Apply)]
    [InlineData(true, 0, WriteDecision.Superseded)]
    [InlineData(true, 1, WriteDecision.Superseded)]
    public void Decide_StoredVersion_FollowsLastWriterWins(bool exists, long storedOffsetMs, WriteDecision expected)
    {
        // Arrange
        var stored = exists ? new StoredVersion(Household, Stamp(storedOffsetMs)) : null;

        // Act
        var decision = LastWriterWinsRule.Decide(Household, stored, Stamp());

        // Assert
        decision.Should().Be(expected);
    }

    [Fact]
    public void Decide_RecordInAnotherHousehold_IsAMismatchWhateverTheVersion()
    {
        // Arrange
        var stored = new StoredVersion(new HouseholdId(Id(99)), Stamp(-1000));

        // Act
        var decision = LastWriterWinsRule.Decide(Household, stored, Stamp());

        // Assert
        decision.Should().Be(WriteDecision.HouseholdMismatch);
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test tests/Coo.Api.Application.Tests --filter FullyQualifiedName~SyncValidationTests`
Expected: FAIL to compile: `The type or namespace name 'Sync' does not exist in the namespace 'Coo.Api.Application'`.
Paste the first error into the review notes.

- [ ] **Step 4: The model, the text forms and the rule**

`src/Backend/Coo.Api.Application/Sync/Model.cs`:

```csharp
using Coo.Contracts.Sync;
using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Duplicates;
using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;
using Coo.Domain.Households;

namespace Coo.Api.Application.Sync;

// What a push carries once the endpoint has read the JSON: details stay a string here (no System.Text.Json in this layer).

public sealed record IncomingChange(Guid ChangeId, string EntityType, Guid EntityId, bool IsDelete, HlcStamp Version, ChangePayload? Payload);

public abstract record ChangePayload
{
    private protected ChangePayload()
    {
    }
}

public sealed record CareEntryPayload(
    string Kind, Guid? SubjectBabyId, string? SubjectPerson, bool IsSession, DateTimeOffset StartAt, DateTimeOffset? EndAt,
    string LoggedBy, string Source, string? SourcePromptVersion, string? Note, string DetailsJson) : ChangePayload;

public sealed record BabyProfilePayload(string Name, IReadOnlyList<string> Nicknames, DateOnly? BirthDate, DateOnly? DueDate) : ChangePayload;

public sealed record HouseholdSettingsPayload(string VolumeUnit, string TimeZoneId, IReadOnlyList<string> HiddenKinds) : ChangePayload;

public sealed record DismissalPayload(Guid EntryA, Guid EntryB) : ChangePayload;

/// <summary>A change that passed validation: the Domain object to store, or a tombstone.</summary>
public abstract record SyncWrite(string EntityType, Guid EntityId, HlcStamp Version);

/// <summary><see cref="ReceivedDetails"/> is the document the phone sent; its unknown properties are kept (ruling 6).</summary>
public sealed record CareEntryWrite(CareEntry Entry, string ReceivedDetails) : SyncWrite(SyncEntityTypes.CareEntry, Entry.Id.Value, Entry.Version);

public sealed record BabyProfileWrite(BabyProfile Baby) : SyncWrite(SyncEntityTypes.BabyProfile, Baby.Id.Value, Baby.Version);

public sealed record HouseholdSettingsWrite(HouseholdSettings Settings) : SyncWrite(SyncEntityTypes.HouseholdSettings, Settings.Id.Value, Settings.Version);

public sealed record DismissalWrite(DuplicateDismissal Dismissal) : SyncWrite(SyncEntityTypes.DuplicateDismissal, Dismissal.Id.Value, Dismissal.Version);

/// <summary>A delete: only the version travels (ruling 1).</summary>
public sealed record TombstoneWrite : SyncWrite
{
    public TombstoneWrite(string entityType, Guid entityId, HlcStamp version)
        : base(entityType, entityId, version)
    {
    }
}

public enum ChangeOutcome
{
    Applied,
    Superseded,
    Rejected,
}

public sealed record ChangeResult(ChangeOutcome Outcome, string? Reason = null)
{
    public static ChangeResult Applied { get; } = new(ChangeOutcome.Applied);

    public static ChangeResult Superseded { get; } = new(ChangeOutcome.Superseded);

    public static ChangeResult Rejected(string reason) => new(ChangeOutcome.Rejected, reason);
}

public sealed record ChangeVerdict(Guid ChangeId, ChangeResult Result);

/// <summary>What the store holds for a record, read under its row lock.</summary>
public sealed record StoredVersion(HouseholdId Household, HlcStamp Version);

public enum WriteDecision
{
    Apply,
    Superseded,
    HouseholdMismatch,
}

public sealed record PulledRecord(string EntityType, Guid EntityId, HlcStamp Version, bool IsDeleted, long Seq, ChangePayload Payload);

public sealed record PullPage(IReadOnlyList<PulledRecord> Records, long Cursor, bool HasMore, long? BootstrapHead);
```

`src/Backend/Coo.Api.Application/Sync/LastWriterWinsRule.cs`:

```csharp
using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;

namespace Coo.Api.Application.Sync;

/// <summary>
/// The server's half of ADR 0002 §3: the higher stamp wins, compared with the Domain's <see cref="HlcStamp"/>, the same
/// compiled rule every phone uses. An equal stamp is the same write again (a retry after <c>applied_changes</c> was
/// purged), so it is superseded. An ID in another household is never moved.
/// </summary>
public static class LastWriterWinsRule
{
    public static WriteDecision Decide(HouseholdId household, StoredVersion? stored, HlcStamp incoming) =>
        stored is null ? WriteDecision.Apply
        : stored.Household != household ? WriteDecision.HouseholdMismatch
        : stored.Version >= incoming ? WriteDecision.Superseded
        : WriteDecision.Apply;
}
```

`src/Backend/Coo.Api.Application/Sync/SyncText.cs`:

```csharp
using System.Globalization;
using Coo.Domain.CareLog;
using Coo.Domain.Common;

namespace Coo.Api.Application.Sync;

/// <summary>
/// The text forms of a person and a source, as the phone writes them and the server stores them (Spec 004 §4.5). Each
/// parse is total: null instead of throwing. <c>ServerColumns</c> delegates here, so there is one definition.
/// </summary>
public static class SyncText
{
    public const string ThisDevice = "this-device";
    private const string UserPrefix = "user:";

    public static string Person(PersonRef person) => person switch
    {
        UserPerson user => UserPrefix + user.UserId.Value.ToString("D", CultureInfo.InvariantCulture),
        ThisDevicesPersonRef => ThisDevice,
        _ => throw new ArgumentOutOfRangeException(nameof(person), person?.GetType().Name, "Unknown person reference."),
    };

    public static PersonRef? ParsePerson(string? value)
    {
        if (value == ThisDevice)
            return PersonRef.ThisDevicesPerson;
        if (value is not null && value.StartsWith(UserPrefix, StringComparison.Ordinal)
            && Guid.TryParseExact(value[UserPrefix.Length..], "D", out var id) && id != Guid.Empty)
            return PersonRef.User(new UserId(id));
        return null;
    }

    public static string Source(EntrySource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source.Kind switch
        {
            EntrySourceKind.Manual => "manual",
            EntrySourceKind.Timer => "timer",
            EntrySourceKind.Watch => "watch",
            EntrySourceKind.AiParsed => "ai-parsed",
            _ => throw new ArgumentOutOfRangeException(nameof(source), source.Kind, "Unknown entry source."),
        };
    }

    public static EntrySource? ParseSource(string? value, string? promptVersion) => value switch
    {
        "manual" => EntrySource.Manual,
        "timer" => EntrySource.Timer,
        "watch" => EntrySource.Watch,
        "ai-parsed" when !string.IsNullOrWhiteSpace(promptVersion) => EntrySource.AiParsed(promptVersion),
        _ => null,
    };
}
```

- [ ] **Step 5: The validator**

`src/Backend/Coo.Api.Application/Sync/ChangeValidator.cs`:

```csharp
using Coo.Codecs;
using Coo.Contracts.Sync;
using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Duplicates;
using Coo.Domain.Common;
using Coo.Domain.Households;

namespace Coo.Api.Application.Sync;

/// <summary>
/// One change against the rules of its schema version (Spec 011 §6.4, ADR 0002 §11): the codecs, the single-record
/// Domain rules, the role rule, the stamp bound. A failure's code is the <c>rejected</c> reason. Rules that span records
/// (overlapping sleeps, a baby that is not in the household) are never checked here (ADR 0002 §9).
/// </summary>
public static class ChangeValidator
{
    public static Result<SyncWrite> Validate(IncomingChange change, HouseholdId household, Membership member, int schemaVersion, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(member);

        if (schemaVersion > SchemaVersions.Current)
            return Reject(RejectionReasons.SchemaUnsupported);
        if (change.Version.IsTooFarAhead(now.ToUnixTimeMilliseconds()))
            return Reject(RejectionReasons.StampInFuture);
        if (change.EntityType is null || !SyncEntityTypes.All.Contains(change.EntityType) || change.EntityId == Guid.Empty)
            return Reject(RejectionReasons.ChangeMalformed);
        if ((change.EntityType is SyncEntityTypes.BabyProfile or SyncEntityTypes.HouseholdSettings) && !member.CanManageBaby)
            return Reject(RejectionReasons.RoleForbidden);
        if (change.EntityType == SyncEntityTypes.HouseholdSettings && change.EntityId != household.Value)
            return Reject(RejectionReasons.HouseholdMismatch);
        if (change.IsDelete)
            return new TombstoneWrite(change.EntityType, change.EntityId, change.Version);

        return change.Payload switch
        {
            CareEntryPayload p when change.EntityType == SyncEntityTypes.CareEntry => ForCareEntry(change, household, p, now),
            BabyProfilePayload p when change.EntityType == SyncEntityTypes.BabyProfile => ForBaby(change, household, p, now),
            HouseholdSettingsPayload p when change.EntityType == SyncEntityTypes.HouseholdSettings => ForSettings(change, household, p),
            DismissalPayload p when change.EntityType == SyncEntityTypes.DuplicateDismissal => ForDismissal(change, household, p),
            _ => Reject(RejectionReasons.ChangeMalformed),
        };
    }

    private static Result<SyncWrite> ForCareEntry(IncomingChange change, HouseholdId household, CareEntryPayload p, DateTimeOffset now)
    {
        if (EntryKindCode.From(p.Kind) is not { IsSuccess: true } code || EntryKinds.Find(code.Value) is not { } kind
            || EntryDetailsCodecs.For(code.Value) is not { } codec)
            return Reject(EntryErrors.KindUnknown);

        EntryDetails details;
        switch (codec.Decode(p.DetailsJson ?? ""))
        {
            case Decoded decoded:
                details = decoded.Details;
                break;
            case Unrepresentable:
                // Newer than this server: the server is meant to be the newest, so the phone is ahead of it.
                return Reject(RejectionReasons.SchemaUnsupported);
            case Malformed malformed:
                return Reject(malformed.Reason);
            default:
                return Reject(RejectionReasons.ChangeMalformed);
        }

        if (p.LoggedBy == SyncText.ThisDevice || p.SubjectPerson == SyncText.ThisDevice)
            return Reject(RejectionReasons.PersonUnresolved);

        var loggedBy = SyncText.ParsePerson(p.LoggedBy);
        var source = SyncText.ParseSource(p.Source, p.SourcePromptVersion);
        EntrySubject? subject = (p.SubjectBabyId, p.SubjectPerson) switch
        {
            ({ } baby, null) when baby != Guid.Empty => EntrySubject.Baby(new BabyId(baby)),
            (null, { } person) when SyncText.ParsePerson(person) is { } who => EntrySubject.Caregiver(who),
            _ => null,
        };
        if (loggedBy is null || source is null || subject is null)
            return Reject(RejectionReasons.ChangeMalformed);

        var note = Note.From(p.Note);
        if (note.IsFailure)
            return Reject(note.Error!.Code);

        EntryTiming timing;
        if (p.IsSession)
        {
            var range = TimeRange.Create(p.StartAt, p.EndAt);
            if (range.IsFailure)
                return Reject(range.Error!.Code);
            timing = EntryTiming.Session(range.Value);
        }
        else if (p.EndAt is null)
        {
            timing = EntryTiming.Point(p.StartAt);
        }
        else
        {
            return Reject(EntryErrors.TimingShapeNotAllowed);
        }

        var derived = kind.TimingFor(details) == TimingRule.DerivedFromDetails;
        var created = CareEntry.Create(
            new EntryId(change.EntityId), household, subject, details, derived ? null : timing, loggedBy, source, change.Version, now, note.Value);
        if (created.IsFailure)
            return Reject(created.Error!.Code);

        // A kind that derives its timing (a breast feed) must carry the timing its details give.
        if (derived && created.Value.Timing != timing)
            return Reject(EntryErrors.TimingShapeNotAllowed);

        return new CareEntryWrite(created.Value, p.DetailsJson!);
    }

    private static Result<SyncWrite> ForBaby(IncomingChange change, HouseholdId household, BabyProfilePayload p, DateTimeOffset now)
    {
        var created = BabyProfile.Create(
            new BabyId(change.EntityId), household, p.Name, p.Nicknames ?? [], p.BirthDate, p.DueDate, change.Version,
            DateOnly.FromDateTime(now.UtcDateTime));
        return created.IsSuccess ? new BabyProfileWrite(created.Value) : Reject(created.Error!.Code);
    }

    private static Result<SyncWrite> ForSettings(IncomingChange change, HouseholdId household, HouseholdSettingsPayload p)
    {
        VolumeUnit? unit = p.VolumeUnit switch
        {
            "ml" => VolumeUnit.Millilitres,
            "oz" => VolumeUnit.Ounces,
            _ => null,
        };
        var hidden = (p.HiddenKinds ?? []).Select(EntryKindCode.From).ToList();
        if (unit is null || hidden.Any(h => h.IsFailure))
            return Reject(RejectionReasons.ChangeMalformed);

        var created = HouseholdSettings.Create(household, unit.Value, p.TimeZoneId ?? "", change.Version, hidden.Select(h => h.Value));
        return created.IsSuccess ? new HouseholdSettingsWrite(created.Value) : Reject(created.Error!.Code);
    }

    private static Result<SyncWrite> ForDismissal(IncomingChange change, HouseholdId household, DismissalPayload p)
    {
        if (p.EntryA == Guid.Empty || p.EntryB == Guid.Empty || p.EntryA == p.EntryB)
            return Reject(RejectionReasons.ChangeMalformed);

        var pair = new EntryPair(new EntryId(p.EntryA), new EntryId(p.EntryB));
        return new DismissalWrite(DuplicateDismissal.Create(new DismissalId(change.EntityId), household, pair, change.Version));
    }

    private static Result<SyncWrite> Reject(string reason) => Result<SyncWrite>.Failure(reason);
}
```

(If the compiler refuses `return new TombstoneWrite(...)` as `Result<SyncWrite>`, write
`return Result<SyncWrite>.Ok(new TombstoneWrite(...))`; the same for the other writes.)

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test tests/Coo.Api.Application.Tests --filter FullyQualifiedName~SyncValidationTests`
Expected: PASS. If `Validate_DetailsThatDoNotDecode_IsRejectedWithTheCodecReason` fails because the codec decodes the
document, read `FeedingCodec` and pick the document it reports `Malformed` for (the debug screen's "Corrupt one row"
uses this one; it may come back as a Domain code from `CareEntry.Create` instead, which also satisfies the test).

- [ ] **Step 7: Commit**

```bash
dotnet format Coo.Tests.slnf
git add src/Shared/Coo.Contracts src/Backend/Coo.Api.Application/Sync tests/Coo.Api.Application.Tests/SyncValidationTests.cs
git commit -m "Sync contracts, and a pushed change checked against the Domain, the role rule and the stamp bound (Spec 011 §6.4, §7.1)"
```

---

### Task 2: The server's sync use cases: access, push, pull, consent, membership, purge (`Coo.Api.Application/Sync`)

**Files:**
- Create: `src/Backend/Coo.Api.Application/Sync/Ports.cs`, `src/Backend/Coo.Api.Application/Sync/UseCases.cs`
- Test: `tests/Coo.Api.Application.Tests/SyncFakes.cs`, `tests/Coo.Api.Application.Tests/SyncUseCaseTests.cs`

**Interfaces:**
- Consumes: Task 1's model, `ChangeValidator`, `LastWriterWinsRule`.
- Produces:
  - `ISyncStore`: `Task<ChangeResult?> FindResultAsync(Guid changeId, CancellationToken)`;
    `Task<ChangeResult> ApplyAsync(HouseholdId household, Guid changeId, SyncWrite write, DateTimeOffset now, CancellationToken)`;
    `Task<ChangeResult> RecordRejectionAsync(Guid changeId, string reason, DateTimeOffset now, CancellationToken)`;
    `Task<PullPage> PullAsync(HouseholdId household, long cursor, long? bootstrapHead, int limit, CancellationToken)`;
    `Task<int> PurgeAsync(DateTimeOffset before, CancellationToken)`.
  - `MembershipLookup(Membership? Membership, bool IsRevoked)` with `static None`; `IMembershipStore.FindAsync(HouseholdId, UserId, CancellationToken)`.
  - `ConsentRecord(UserId User, string Purpose, string Wording, DateTimeOffset GivenAt, string? AppVersion, string? Language)`;
    `IConsentStore.RecordAsync(ConsentRecord, CancellationToken)`, `IConsentStore.FindAsync(UserId, string purpose, string wording, CancellationToken)`.
  - `AccessDenial { None, NotAMember, MembershipRevoked, ConsentRequired }`, `SyncAccessResult(Membership? Membership, AccessDenial Denial)`,
    `SyncAccess.CheckAsync(UserId user, HouseholdId household, CancellationToken)`.
  - `PushChanges.ExecuteAsync(HouseholdId, Membership, int schemaVersion, IReadOnlyList<IncomingChange>, CancellationToken)` → `IReadOnlyList<ChangeVerdict>`.
  - `PullChanges.ExecuteAsync(HouseholdId, long cursor, long? bootstrapHead, CancellationToken)` → `PullPage`.
  - `RecordConsent.ExecuteAsync(UserId, string purpose, string wording, string? appVersion, string? language, CancellationToken)` → `Result`.
  - `GetMembershipStatus.ExecuteAsync(UserId, HouseholdId, CancellationToken)` → `MembershipLookup`.
  - `PurgeSyncHistory.ExecuteAsync(CancellationToken)` → `int`, `PurgeSyncHistory.KeepFor` = 30 days.
  - `SchemaUpgrade.ToCurrent(int schemaVersion, IncomingChange change)` → `IncomingChange`.
  - (`GetMe` learns the consent in Task 6, together with the store that backs it, so the API never runs with an
    unregistered port in between.)

- [ ] **Step 1: Write the fakes and the failing tests**

`tests/Coo.Api.Application.Tests/SyncFakes.cs` (the store fake decides with the same `LastWriterWinsRule` the real
store uses inside its transaction):

```csharp
using Coo.Api.Application.Sync;
using Coo.Contracts.Sync;
using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;

namespace Coo.Api.Application.Tests;

internal sealed class FakeSyncStore : ISyncStore
{
    private long _seq;

    public Dictionary<Guid, ChangeResult> Results { get; } = [];

    public Dictionary<(string Type, Guid Id), (HouseholdId Household, HlcStamp Version, bool Deleted, long Seq, SyncWrite Write)> Rows { get; } = [];

    public List<(Guid ChangeId, HlcStamp Winner, HlcStamp Loser)> Conflicts { get; } = [];

    public int ApplyCalls { get; private set; }

    public Task<ChangeResult?> FindResultAsync(Guid changeId, CancellationToken cancellationToken) =>
        Task.FromResult(Results.GetValueOrDefault(changeId));

    public Task<ChangeResult> ApplyAsync(HouseholdId household, Guid changeId, SyncWrite write, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ApplyCalls++;
        var key = (write.EntityType, write.EntityId);
        StoredVersion? stored = Rows.TryGetValue(key, out var row) ? new StoredVersion(row.Household, row.Version) : null;
        ChangeResult result;
        switch (LastWriterWinsRule.Decide(household, stored, write.Version))
        {
            case WriteDecision.HouseholdMismatch:
                result = ChangeResult.Rejected(RejectionReasons.HouseholdMismatch);
                break;
            case WriteDecision.Superseded:
                Conflicts.Add((changeId, stored!.Version, write.Version));
                result = ChangeResult.Superseded;
                break;
            default:
                if (write is not TombstoneWrite || stored is not null)
                    Rows[key] = (household, write.Version, write is TombstoneWrite, ++_seq, write);
                result = ChangeResult.Applied;
                break;
        }

        Results.TryAdd(changeId, result);
        return Task.FromResult(Results[changeId]);
    }

    public Task<ChangeResult> RecordRejectionAsync(Guid changeId, string reason, DateTimeOffset now, CancellationToken cancellationToken)
    {
        Results.TryAdd(changeId, ChangeResult.Rejected(reason));
        return Task.FromResult(Results[changeId]);
    }

    public Task<PullPage> PullAsync(HouseholdId household, long cursor, long? bootstrapHead, int limit, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Pull paging is the real store's (SyncStoreTests).");

    public Task<int> PurgeAsync(DateTimeOffset before, CancellationToken cancellationToken) => Task.FromResult(0);
}

internal sealed class FakeMemberships : IMembershipStore
{
    public Dictionary<(HouseholdId, UserId), MembershipLookup> Rows { get; } = [];

    public Task<MembershipLookup> FindAsync(HouseholdId household, UserId user, CancellationToken cancellationToken) =>
        Task.FromResult(Rows.GetValueOrDefault((household, user), MembershipLookup.None));
}

internal sealed class FakeConsents : IConsentStore
{
    public List<ConsentRecord> Rows { get; } = [];

    public Task RecordAsync(ConsentRecord record, CancellationToken cancellationToken)
    {
        if (!Rows.Any(r => r.User == record.User && r.Purpose == record.Purpose && r.Wording == record.Wording))
            Rows.Add(record);
        return Task.CompletedTask;
    }

    public Task<ConsentRecord?> FindAsync(UserId user, string purpose, string wording, CancellationToken cancellationToken) =>
        Task.FromResult(Rows.FirstOrDefault(r => r.User == user && r.Purpose == purpose && r.Wording == wording));
}
```

`tests/Coo.Api.Application.Tests/SyncUseCaseTests.cs`:

```csharp
using Coo.Api.Application.Sync;
using Coo.Contracts.Sync;
using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;
using Coo.Domain.Households;
using Microsoft.Extensions.Time.Testing;
using static Coo.Api.Application.Tests.SyncValidationTests;

namespace Coo.Api.Application.Tests;

/// <summary>Spec 011 §6.1, §6.5, §7.1, §10 (Api.Application row).</summary>
public sealed class SyncUseCaseTests
{
    private static readonly Membership Owner = Membership.Create(Household, Parent, Role.Owner, Now);

    private readonly FakeTimeProvider _time = new(Now);
    private readonly FakeSyncStore _store = new();
    private readonly FakeMemberships _memberships = new();
    private readonly FakeConsents _consents = new();

    private PushChanges Push() => new(_store, _time);

    private Task<IReadOnlyList<ChangeVerdict>> PushAsync(params IncomingChange[] changes) =>
        Push().ExecuteAsync(Household, Owner, SchemaVersions.Current, changes, CancellationToken.None);

    private static IncomingChange Delete(Guid id, long offsetMs) =>
        new(Guid.CreateVersion7(Now.AddMilliseconds(offsetMs)), SyncEntityTypes.CareEntry, id, IsDelete: true, Stamp(offsetMs), null);

    [Fact]
    public async Task ExecuteAsync_NewEntry_IsApplied()
    {
        // Act
        var verdicts = await PushAsync(Upsert(Id(1), Diaper(Now.AddMinutes(-5))));

        // Assert
        verdicts.Single().Result.Should().Be(ChangeResult.Applied);
        _store.Rows.Should().ContainKey((SyncEntityTypes.CareEntry, Id(1)));
    }

    [Fact]
    public async Task ExecuteAsync_OlderEditOfAStoredEntry_IsSupersededAndRecordedAsAConflict()
    {
        // Arrange
        await PushAsync(Upsert(Id(1), Diaper(Now.AddMinutes(-5)), offsetMs: 10));

        // Act
        var verdicts = await PushAsync(Upsert(Id(1), Diaper(Now.AddMinutes(-6)), offsetMs: 5));

        // Assert
        verdicts.Single().Result.Should().Be(ChangeResult.Superseded);
        _store.Conflicts.Should().ContainSingle();
    }

    [Fact]
    public async Task ExecuteAsync_RetriedChangeId_ReturnsTheStoredResultWithoutApplyingAgain()
    {
        // Arrange: the response to the first push was lost; the phone sends the same batch.
        var change = Upsert(Id(1), Diaper(Now.AddMinutes(-5)));
        await PushAsync(change);

        // Act
        var verdicts = await PushAsync(change);

        // Assert
        verdicts.Single().Result.Should().Be(ChangeResult.Applied);
        _store.ApplyCalls.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_RejectedChangeRetried_ReturnsTheSameRejection()
    {
        // Arrange
        var change = Upsert(Id(1), Diaper(Now, loggedBy: "this-device"));
        await PushAsync(change);

        // Act
        var verdicts = await PushAsync(change);

        // Assert
        verdicts.Single().Result.Should().Be(ChangeResult.Rejected(RejectionReasons.PersonUnresolved));
        _store.ApplyCalls.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_TombstoneAgainstALowerEdit_Wins()
    {
        // Arrange
        await PushAsync(Upsert(Id(1), Diaper(Now.AddMinutes(-5)), offsetMs: 10));

        // Act
        var verdicts = await PushAsync(Delete(Id(1), offsetMs: 20));

        // Assert
        verdicts.Single().Result.Should().Be(ChangeResult.Applied);
        _store.Rows[(SyncEntityTypes.CareEntry, Id(1))].Deleted.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_TombstoneAgainstAHigherEdit_LosesAndTheEditStays()
    {
        // Arrange: a correction made after a stale delete survives it (ADR 0002 §3).
        await PushAsync(Upsert(Id(1), Diaper(Now.AddMinutes(-5)), offsetMs: 30));

        // Act
        var verdicts = await PushAsync(Delete(Id(1), offsetMs: 20));

        // Assert
        verdicts.Single().Result.Should().Be(ChangeResult.Superseded);
        _store.Rows[(SyncEntityTypes.CareEntry, Id(1))].Deleted.Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_HigherEditOfATombstone_RevivesTheEntry()
    {
        // Arrange
        await PushAsync(Upsert(Id(1), Diaper(Now.AddMinutes(-5)), offsetMs: 10));
        await PushAsync(Delete(Id(1), offsetMs: 20));

        // Act
        var verdicts = await PushAsync(Upsert(Id(1), Diaper(Now.AddMinutes(-4)), offsetMs: 30));

        // Assert
        verdicts.Single().Result.Should().Be(ChangeResult.Applied);
        _store.Rows[(SyncEntityTypes.CareEntry, Id(1))].Deleted.Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_EntryOfAnotherHousehold_IsRejectedAndNeverMoved()
    {
        // Arrange
        var other = new HouseholdId(Id(77));
        await new PushChanges(_store, _time).ExecuteAsync(
            other, Membership.Create(other, Parent, Role.Owner, Now), SchemaVersions.Current, [Upsert(Id(1), Diaper(Now))], CancellationToken.None);

        // Act
        var verdicts = await PushAsync(Upsert(Id(1), Diaper(Now), offsetMs: 50));

        // Assert
        verdicts.Single().Result.Should().Be(ChangeResult.Rejected(RejectionReasons.HouseholdMismatch));
        _store.Rows[(SyncEntityTypes.CareEntry, Id(1))].Household.Should().Be(other);
    }

    [Theory]
    [InlineData(RejectionReasons.SchemaUnsupported)]
    [InlineData(RejectionReasons.StampInFuture)]
    [InlineData(RejectionReasons.PersonUnresolved)]
    [InlineData(RejectionReasons.RoleForbidden)]
    [InlineData(RejectionReasons.ChangeMalformed)]
    [InlineData("time.end-not-after-start")]
    public async Task ExecuteAsync_EachRejectedReason_IsReportedPerChangeAndStored(string reason)
    {
        // Arrange
        var (change, member, schema) = reason switch
        {
            RejectionReasons.SchemaUnsupported => (Upsert(Id(1), Diaper(Now)), Owner, SchemaVersions.Current + 1),
            RejectionReasons.StampInFuture => (Upsert(Id(1), Diaper(Now), offsetMs: (long)TimeSpan.FromDays(2).TotalMilliseconds), Owner, SchemaVersions.Current),
            RejectionReasons.PersonUnresolved => (Upsert(Id(1), Diaper(Now, "this-device")), Owner, SchemaVersions.Current),
            RejectionReasons.RoleForbidden => (Upsert(Id(2), new BabyProfilePayload("Maja", [], new DateOnly(2026, 8, 20), null), SyncEntityTypes.BabyProfile),
                Membership.Create(Household, Parent, Role.Caregiver, Now), SchemaVersions.Current),
            RejectionReasons.ChangeMalformed => (Upsert(Id(1), Diaper(Now), "Medication"), Owner, SchemaVersions.Current),
            _ => (Upsert(Id(1), new CareEntryPayload("sleep", Baby, null, true, Now, Now.AddMinutes(-5), "user:7a1c0000-0000-7000-8000-0000000000c1",
                "manual", null, null, """{"v":1}""")), Owner, SchemaVersions.Current),
        };

        // Act
        var verdicts = await Push().ExecuteAsync(Household, member, schema, [change], CancellationToken.None);

        // Assert
        verdicts.Single().Result.Should().Be(ChangeResult.Rejected(reason));
        _store.Results[change.ChangeId].Should().Be(ChangeResult.Rejected(reason));
    }

    [Fact]
    public async Task ExecuteAsync_OneBadChangeInABatch_DoesNotStopTheOthers()
    {
        // Act
        var verdicts = await PushAsync(Upsert(Id(1), Diaper(Now, "this-device")), Upsert(Id(2), Diaper(Now.AddMinutes(-1))));

        // Assert
        verdicts.Select(v => v.Result.Outcome).Should().Equal(ChangeOutcome.Rejected, ChangeOutcome.Applied);
    }

    [Fact]
    public async Task CheckAsync_NeverAMember_IsNotAMember()
    {
        // Act
        var access = await new SyncAccess(_memberships, _consents).CheckAsync(Parent, Household, CancellationToken.None);

        // Assert
        access.Denial.Should().Be(AccessDenial.NotAMember);
    }

    [Fact]
    public async Task CheckAsync_RevokedMember_IsMembershipRevokedNotNotAMember()
    {
        // Arrange
        _memberships.Rows[(Household, Parent)] = new MembershipLookup(Owner, IsRevoked: true);

        // Act
        var access = await new SyncAccess(_memberships, _consents).CheckAsync(Parent, Household, CancellationToken.None);

        // Assert
        access.Denial.Should().Be(AccessDenial.MembershipRevoked);
    }

    [Fact]
    public async Task CheckAsync_MemberWithoutConsent_IsConsentRequired()
    {
        // Arrange
        _memberships.Rows[(Household, Parent)] = new MembershipLookup(Owner, IsRevoked: false);

        // Act
        var access = await new SyncAccess(_memberships, _consents).CheckAsync(Parent, Household, CancellationToken.None);

        // Assert
        access.Denial.Should().Be(AccessDenial.ConsentRequired);
    }

    [Fact]
    public async Task CheckAsync_ConsentToAnOlderWording_IsConsentRequired()
    {
        // Arrange: a new wording asks again (Spec 011 §3).
        _memberships.Rows[(Household, Parent)] = new MembershipLookup(Owner, IsRevoked: false);
        await _consents.RecordAsync(new ConsentRecord(Parent, CareDataConsent.Purpose, "care-data-v0", Now, "0.11.0", "en"), CancellationToken.None);

        // Act
        var access = await new SyncAccess(_memberships, _consents).CheckAsync(Parent, Household, CancellationToken.None);

        // Assert
        access.Denial.Should().Be(AccessDenial.ConsentRequired);
    }

    [Fact]
    public async Task CheckAsync_MemberWithCurrentConsent_IsAllowedWithTheMembership()
    {
        // Arrange
        _memberships.Rows[(Household, Parent)] = new MembershipLookup(Owner, IsRevoked: false);
        await new RecordConsent(_consents, _time).ExecuteAsync(Parent, CareDataConsent.Purpose, CareDataConsent.CurrentWording, "0.11.0", "pl", CancellationToken.None);

        // Act
        var access = await new SyncAccess(_memberships, _consents).CheckAsync(Parent, Household, CancellationToken.None);

        // Assert
        access.Denial.Should().Be(AccessDenial.None);
        access.Membership.Should().Be(Owner);
    }

    [Fact]
    public async Task ExecuteAsync_ConsentWithAnUnknownWording_IsRefusedAndNotStored()
    {
        // Act
        var result = await new RecordConsent(_consents, _time).ExecuteAsync(Parent, CareDataConsent.Purpose, "care-data-v9", null, null, CancellationToken.None);

        // Assert
        result.Error!.Code.Should().Be(SyncErrors.ConsentUnknownWording);
        _consents.Rows.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_ConsentGivenTwice_KeepsTheFirstTime()
    {
        // Arrange
        var record = new RecordConsent(_consents, _time);
        await record.ExecuteAsync(Parent, CareDataConsent.Purpose, CareDataConsent.CurrentWording, "0.11.0", "en", CancellationToken.None);
        _time.Advance(TimeSpan.FromDays(1));

        // Act
        await record.ExecuteAsync(Parent, CareDataConsent.Purpose, CareDataConsent.CurrentWording, "0.11.0", "en", CancellationToken.None);

        // Assert
        _consents.Rows.Should().ContainSingle().Which.GivenAt.Should().Be(Now);
    }

    [Fact]
    public async Task ExecuteAsync_LongAppVersionAndLanguage_AreCutToTheColumnSizes()
    {
        // Act
        await new RecordConsent(_consents, _time).ExecuteAsync(
            Parent, CareDataConsent.Purpose, CareDataConsent.CurrentWording, new string('9', 100), new string('x', 50), CancellationToken.None);

        // Assert
        _consents.Rows.Single().AppVersion.Should().HaveLength(RecordConsent.AppVersionMaxLength);
        _consents.Rows.Single().Language.Should().HaveLength(RecordConsent.LanguageMaxLength);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Coo.Api.Application.Tests --filter FullyQualifiedName~SyncUseCaseTests`
Expected: FAIL to compile (`ISyncStore`, `PushChanges`, `SyncAccess`, … not found). Paste the first error.

- [ ] **Step 3: The ports**

`src/Backend/Coo.Api.Application/Sync/Ports.cs`:

```csharp
using Coo.Domain.Common;
using Coo.Domain.Households;

namespace Coo.Api.Application.Sync;

/// <summary>
/// The four synced tables, <c>applied_changes</c> and <c>sync_conflicts</c> (Spec 011 §7.2). Each apply is one
/// transaction: the stored version under its row lock, <see cref="LastWriterWinsRule"/>, the household's next
/// <c>seq</c>, the row, the result, and a conflict row when something lost.
/// </summary>
public interface ISyncStore
{
    /// <summary>The result stored for a change ID, if it was decided before (a retried batch).</summary>
    Task<ChangeResult?> FindResultAsync(Guid changeId, CancellationToken cancellationToken);

    /// <summary>Applies a validated write. A concurrent decision for the same change ID wins; its result is returned.</summary>
    Task<ChangeResult> ApplyAsync(HouseholdId household, Guid changeId, SyncWrite write, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Stores a rejection for a change ID, unless a result is already stored; returns the stored result.</summary>
    Task<ChangeResult> RecordRejectionAsync(Guid changeId, string reason, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// Records of all four types with <c>seq &gt; cursor</c>, in <c>seq</c> order, at most <paramref name="limit"/>
    /// (ADR 0002 §6). From cursor 0 without a head, the head is read first; tombstones at or below the head are skipped,
    /// and the cursor still moves past them.
    /// </summary>
    Task<PullPage> PullAsync(HouseholdId household, long cursor, long? bootstrapHead, int limit, CancellationToken cancellationToken);

    /// <summary>Deletes <c>applied_changes</c> and <c>sync_conflicts</c> rows older than <paramref name="before"/>.</summary>
    Task<int> PurgeAsync(DateTimeOffset before, CancellationToken cancellationToken);
}

/// <summary>A user's membership of a household: active, revoked (ADR 0002 §10), or never (<see cref="None"/>).</summary>
public sealed record MembershipLookup(Membership? Membership, bool IsRevoked)
{
    public static MembershipLookup None { get; } = new(null, false);
}

public interface IMembershipStore
{
    Task<MembershipLookup> FindAsync(HouseholdId household, UserId user, CancellationToken cancellationToken);
}

/// <summary>A consent as GDPR Art. 7(1) needs it proven: who, to which wording, when, from which app, in which language.</summary>
public sealed record ConsentRecord(UserId User, string Purpose, string Wording, DateTimeOffset GivenAt, string? AppVersion, string? Language);

/// <summary><c>consents</c>. Recording is idempotent: the first consent to a wording is kept.</summary>
public interface IConsentStore
{
    Task RecordAsync(ConsentRecord record, CancellationToken cancellationToken);

    Task<ConsentRecord?> FindAsync(UserId user, string purpose, string wording, CancellationToken cancellationToken);
}
```

- [ ] **Step 4: The use cases**

`src/Backend/Coo.Api.Application/Sync/UseCases.cs`:

```csharp
using Coo.Contracts.Sync;
using Coo.Domain.Common;
using Coo.Domain.Households;

namespace Coo.Api.Application.Sync;

public enum AccessDenial
{
    None,
    NotAMember,
    MembershipRevoked,
    ConsentRequired,
}

public sealed record SyncAccessResult(Membership? Membership, AccessDenial Denial);

/// <summary>
/// The per-request check of every sync call (Spec 011 §7.1): an active membership of the household, then a consent to
/// the current wording (Q1, Q8). Revoked and never-a-member differ: only the first is ADR 0002 §10's signal.
/// </summary>
public sealed class SyncAccess(IMembershipStore memberships, IConsentStore consents)
{
    public async Task<SyncAccessResult> CheckAsync(UserId user, HouseholdId household, CancellationToken cancellationToken)
    {
        var found = await memberships.FindAsync(household, user, cancellationToken);
        if (found.Membership is null)
            return new SyncAccessResult(null, AccessDenial.NotAMember);
        if (found.IsRevoked)
            return new SyncAccessResult(null, AccessDenial.MembershipRevoked);
        if (await consents.FindAsync(user, CareDataConsent.Purpose, CareDataConsent.CurrentWording, cancellationToken) is null)
            return new SyncAccessResult(found.Membership, AccessDenial.ConsentRequired);
        return new SyncAccessResult(found.Membership, AccessDenial.None);
    }
}

/// <summary>The N−1 seam of ADR 0002 §11. There is no older version yet, so a change passes unchanged.</summary>
public static class SchemaUpgrade
{
    /// <remarks>A version above the current one passes too: the validator rejects it as <c>sync.schema-unsupported</c>.
    /// Below <see cref="SchemaVersions.Oldest"/> the endpoint has already answered 426.</remarks>
    public static IncomingChange ToCurrent(int schemaVersion, IncomingChange change) =>
        schemaVersion >= SchemaVersions.Current
            ? change
            : throw new NotSupportedException($"No upgrader from schema version {schemaVersion}.");
}

/// <summary>
/// <c>POST /api/sync/push</c> (Spec 011 §6.1). Each change gets its own result; a change ID decided before returns its
/// stored result and is not applied twice (ADR 0002 §5). One bad change never stops the others.
/// </summary>
public sealed class PushChanges(ISyncStore store, TimeProvider time)
{
    public async Task<IReadOnlyList<ChangeVerdict>> ExecuteAsync(
        HouseholdId household, Membership member, int schemaVersion, IReadOnlyList<IncomingChange> changes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(member);
        ArgumentNullException.ThrowIfNull(changes);
        var verdicts = new List<ChangeVerdict>(changes.Count);
        foreach (var change in changes)
        {
            if (await store.FindResultAsync(change.ChangeId, cancellationToken) is { } stored)
            {
                verdicts.Add(new ChangeVerdict(change.ChangeId, stored));
                continue;
            }

            var now = time.GetUtcNow();
            var current = SchemaUpgrade.ToCurrent(schemaVersion, change);
            var validated = ChangeValidator.Validate(current, household, member, schemaVersion, now);
            var result = validated.IsSuccess
                ? await store.ApplyAsync(household, change.ChangeId, validated.Value, now, cancellationToken)
                : await store.RecordRejectionAsync(change.ChangeId, validated.Error!.Code, now, cancellationToken);
            verdicts.Add(new ChangeVerdict(change.ChangeId, result));
        }

        return verdicts;
    }
}

/// <summary><c>GET /api/sync/pull</c> (Spec 011 §6.2): one page of at most 500 records.</summary>
public sealed class PullChanges(ISyncStore store)
{
    public Task<PullPage> ExecuteAsync(HouseholdId household, long cursor, long? bootstrapHead, CancellationToken cancellationToken) =>
        store.PullAsync(household, cursor, bootstrapHead, SyncLimits.MaxPullRecords, cancellationToken);
}

/// <summary><c>POST /api/consents</c> (Spec 011 §3): only the current wording of the care-data purpose.</summary>
public sealed class RecordConsent(IConsentStore consents, TimeProvider time)
{
    public const int AppVersionMaxLength = 40;
    public const int LanguageMaxLength = 16;

    public async Task<Result> ExecuteAsync(
        UserId user, string? purpose, string? wording, string? appVersion, string? language, CancellationToken cancellationToken)
    {
        if (purpose != CareDataConsent.Purpose || wording != CareDataConsent.CurrentWording)
            return Result.Failure(SyncErrors.ConsentUnknownWording);

        await consents.RecordAsync(
            new ConsentRecord(user, purpose, wording, time.GetUtcNow(), Cut(appVersion, AppVersionMaxLength), Cut(language, LanguageMaxLength)),
            cancellationToken);
        return Result.Success;
    }

    private static string? Cut(string? value, int max)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed.Length <= max ? trimmed : trimmed[..max];
    }
}

/// <summary><c>GET /api/households/{id}/membership</c> (ADR 0002 §10, step 3).</summary>
public sealed class GetMembershipStatus(IMembershipStore memberships)
{
    public Task<MembershipLookup> ExecuteAsync(UserId user, HouseholdId household, CancellationToken cancellationToken) =>
        memberships.FindAsync(household, user, cancellationToken);
}

/// <summary>The daily clean-up (Spec 011 §7.2): change results and conflict records older than 30 days go.</summary>
public sealed class PurgeSyncHistory(ISyncStore store, TimeProvider time)
{
    public static readonly TimeSpan KeepFor = TimeSpan.FromDays(30);

    public Task<int> ExecuteAsync(CancellationToken cancellationToken) => store.PurgeAsync(time.GetUtcNow() - KeepFor, cancellationToken);
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/Coo.Api.Application.Tests`
Expected: PASS (all, including the Increment 10 tests).

- [ ] **Step 6: Commit**

```bash
dotnet format Coo.Tests.slnf
git add src/Backend/Coo.Api.Application tests/Coo.Api.Application.Tests
git commit -m "Server sync use cases: per-request access, idempotent push with per-change results, pull, consent, membership, purge (Spec 011 §3, §6, §7)"
```

---
### Task 3: A lost refresh response is not theft: the phone's proposed next token, on the server (Spec 011 §8.2)

**Files:**
- Modify: `src/Backend/Coo.Api.Application/Auth/Ports.cs` (`RefreshLookup` carries the current hash)
- Modify: `src/Backend/Coo.Api.Application/Auth/Sessions.cs` (`RefreshSession`)
- Modify: `src/Backend/Coo.Api.Infrastructure/Persistence/DeviceSessionStore.cs` (the lookup returns the current hash)
- Modify: `src/Backend/Coo.Api/Hosting/AuthEndpoints.cs` (`/refresh` passes `NextRefreshToken`)
- Test: `tests/Coo.Api.Application.Tests/SessionTests.cs`, `tests/Coo.Api.Application.Tests/Fakes.cs`,
  `tests/Coo.Api.Infrastructure.Tests/AuthStoreTests.cs`, `tests/Coo.Api.IntegrationTests/AuthApiTests.cs`

**Interfaces:**
- Consumes: `RefreshRequest.NextRefreshToken` (Task 1).
- Produces:
  - `RefreshLookup(DeviceSession Session, RefreshMatch Match, string CurrentHash)`.
  - `RefreshSession.ExecuteAsync(string refreshToken, string? nextRefreshToken, CancellationToken)`; the two-argument
    overload stays and means "no proposal" (a 0.10.0 phone). `RefreshSession.NextTokenMinLength = 43`,
    `NextTokenMaxLength = 128`.
  - With a proposal T2 for the presented T1: T1 current → rotate to `hash(T2)` and return T2 as the refresh token. T1
    previous **and** `hash(T2)` current → a fresh access token, nothing changed (the retry). T1 previous with any other
    T2, or none → revoke (theft, as today).

- [ ] **Step 1: Write the failing tests**

Add to `tests/Coo.Api.Application.Tests/SessionTests.cs`:

```csharp
    private static readonly string NextToken = new('n', 43);
    private static readonly string OtherNextToken = new('m', 43);

    [Fact]
    public async Task ExecuteAsync_CurrentTokenWithAProposal_RotatesToTheProposedToken()
    {
        // Arrange
        var signedIn = await _kit.SignInAsync("google-1", Phone, Household);

        // Act
        var result = await _kit.Refresh().ExecuteAsync(signedIn.Session.RefreshToken, NextToken, CancellationToken.None);

        // Assert
        result.Value.RefreshToken.Should().Be(NextToken);
        _kit.Sessions.Of(signedIn.Session.Id).RefreshHash.Should().Be(AuthSecrets.Hash(NextToken));
    }

    [Fact]
    public async Task ExecuteAsync_SamePairRetriedAfterALostResponse_IssuesAccessAndChangesNothing()
    {
        // Arrange: the server rotated T1 to T2, the response was lost, the phone sends T1 + T2 again.
        var signedIn = await _kit.SignInAsync("google-1", Phone, Household);
        await _kit.Refresh().ExecuteAsync(signedIn.Session.RefreshToken, NextToken, CancellationToken.None);
        var before = _kit.Sessions.Of(signedIn.Session.Id);
        _kit.Time.Advance(TimeSpan.FromSeconds(30));

        // Act
        var retried = await _kit.Refresh().ExecuteAsync(signedIn.Session.RefreshToken, NextToken, CancellationToken.None);

        // Assert
        retried.IsSuccess.Should().BeTrue();
        retried.Value.RefreshToken.Should().Be(NextToken);
        retried.Value.AccessToken.ExpiresAt.Should().Be(_kit.Time.GetUtcNow() + TimeSpan.FromMinutes(15));
        _kit.Sessions.Of(signedIn.Session.Id).Should().Be(before, "the retry changes nothing");
    }

    [Fact]
    public async Task ExecuteAsync_PreviousTokenWithAnotherProposal_RevokesTheSession()
    {
        // Arrange: someone holding T1 cannot know T2.
        var signedIn = await _kit.SignInAsync("google-1", Phone, Household);
        await _kit.Refresh().ExecuteAsync(signedIn.Session.RefreshToken, NextToken, CancellationToken.None);

        // Act
        var stolen = await _kit.Refresh().ExecuteAsync(signedIn.Session.RefreshToken, OtherNextToken, CancellationToken.None);

        // Assert
        stolen.Error!.Code.Should().Be(AuthErrors.RefreshInvalid);
        _kit.Sessions.Of(signedIn.Session.Id).Session.IsRevoked.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_PreviousTokenWithoutAProposal_StillRevokes()
    {
        // Arrange: a 0.10.0 phone, or a thief that sends no proposal.
        var signedIn = await _kit.SignInAsync("google-1", Phone, Household);
        await _kit.Refresh().ExecuteAsync(signedIn.Session.RefreshToken, NextToken, CancellationToken.None);

        // Act
        var reused = await _kit.Refresh().ExecuteAsync(signedIn.Session.RefreshToken, CancellationToken.None);

        // Assert
        reused.Error!.Code.Should().Be(AuthErrors.RefreshInvalid);
        _kit.Sessions.Of(signedIn.Session.Id).Session.IsRevoked.Should().BeTrue();
    }

    [Theory]
    [InlineData("short")]
    [InlineData("")]
    public async Task ExecuteAsync_ProposalTooShort_IsRefusedAndChangesNothing(string proposal)
    {
        // Arrange
        var signedIn = await _kit.SignInAsync("google-1", Phone, Household);
        var before = _kit.Sessions.Of(signedIn.Session.Id);

        // Act
        var result = await _kit.Refresh().ExecuteAsync(signedIn.Session.RefreshToken, proposal, CancellationToken.None);

        // Assert
        result.Error!.Code.Should().Be(AuthErrors.RefreshInvalid);
        _kit.Sessions.Of(signedIn.Session.Id).Should().Be(before);
    }

    [Fact]
    public async Task ExecuteAsync_ProposalEqualToThePresentedToken_IsRefused()
    {
        // Arrange
        var signedIn = await _kit.SignInAsync("google-1", Phone, Household);

        // Act
        var result = await _kit.Refresh().ExecuteAsync(signedIn.Session.RefreshToken, signedIn.Session.RefreshToken, CancellationToken.None);

        // Assert
        result.Error!.Code.Should().Be(AuthErrors.RefreshInvalid);
        _kit.Sessions.Of(signedIn.Session.Id).Session.IsRevoked.Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_TwoIdenticalRequestsRacing_BothSucceed()
    {
        // Arrange: the phone's retry arrives while the first request is still rotating. The fake's rotation is
        // the store's compare-and-swap; the second request finds T1 current, loses the swap, and must see the retry.
        var signedIn = await _kit.SignInAsync("google-1", Phone, Household);
        _kit.Sessions.BeforeRotate = () => _kit.Sessions.RotateDirectly(signedIn.Session.Id, AuthSecrets.Hash(signedIn.Session.RefreshToken), AuthSecrets.Hash(NextToken));

        // Act
        var second = await _kit.Refresh().ExecuteAsync(signedIn.Session.RefreshToken, NextToken, CancellationToken.None);

        // Assert
        second.IsSuccess.Should().BeTrue();
        second.Value.RefreshToken.Should().Be(NextToken);
        _kit.Sessions.Of(signedIn.Session.Id).Session.IsRevoked.Should().BeFalse();
    }
```

In `tests/Coo.Api.Application.Tests/Fakes.cs`, `FakeSessions`: return the current hash from the lookup, and add the
race hook used above:

```csharp
    /// <summary>Runs once inside the next <see cref="RotateAsync"/>, before its compare-and-swap: a concurrent request winning.</summary>
    public Action? BeforeRotate { get; set; }

    public Task<RefreshLookup?> FindByRefreshHashAsync(string refreshHash, CancellationToken cancellationToken)
    {
        var current = Rows.FirstOrDefault(r => r.RefreshHash == refreshHash);
        if (current is not null)
            return Task.FromResult<RefreshLookup?>(new RefreshLookup(current.Session, RefreshMatch.Current, current.RefreshHash));
        var previous = Rows.FirstOrDefault(r => r.PreviousRefreshHash == refreshHash);
        return Task.FromResult(previous is null ? null : new RefreshLookup(previous.Session, RefreshMatch.Previous, previous.RefreshHash));
    }

    public Task<bool> RotateAsync(DeviceSessionId id, string expectedHash, string newHash, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var hook = BeforeRotate;
        BeforeRotate = null;
        hook?.Invoke();
        var index = Rows.FindIndex(r => r.Session.Id == id && r.RefreshHash == expectedHash && !r.Session.IsRevoked);
        if (index < 0)
            return Task.FromResult(false);
        var row = Rows[index];
        Rows[index] = new Row(row.Session with { LastUsedAt = now }, newHash, expectedHash);
        return Task.FromResult(true);
    }

    public void RotateDirectly(DeviceSessionId id, string expectedHash, string newHash)
    {
        var index = Rows.FindIndex(r => r.Session.Id == id && r.RefreshHash == expectedHash);
        Rows[index] = new Row(Rows[index].Session, newHash, expectedHash);
    }
```

Add to `tests/Coo.Api.Infrastructure.Tests/AuthStoreTests.cs`, next to
`FindByRefreshHashAsync_AfterRotation_FindsTheOldHashAsPrevious` (reuse that test's arrangement: a session created with
`h1`, rotated to `h2`):

```csharp
    [Fact]
    public async Task FindByRefreshHashAsync_PreviousHash_ReportsTheCurrentHash()
    {
        // Arrange
        await using var db = await ServerDatabase.CreateAsync(server);
        await CreateAccountAsync(db, User, "sub-1", ServerDatabase.Household);
        var session = Session(User, Phone);
        await db.Sessions.CreateAsync(session, "h1", CancellationToken.None);
        await db.Sessions.RotateAsync(session.Id, "h1", "h2", T0.AddMinutes(20), CancellationToken.None);

        // Act
        var previous = await db.Sessions.FindByRefreshHashAsync("h1", CancellationToken.None);

        // Assert
        previous!.Match.Should().Be(RefreshMatch.Previous);
        previous.CurrentHash.Should().Be("h2");
    }
```

Add to `tests/Coo.Api.IntegrationTests/AuthApiTests.cs`:

```csharp
    [Fact]
    public async Task Refresh_SamePairSentTwice_SucceedsBothTimesAndKeepsTheSession()
    {
        // Arrange
        using var client = api.Factory.CreateClient();
        var session = await Calls.SignInAsync(client, "retry-1", Calls.Id(9101), Calls.Id(9102));
        var next = new string('t', 43);
        var first = await client.PostAsJsonAsync(Calls.Refresh, new RefreshRequest(session.RefreshToken, next));

        // Act
        var retried = await client.PostAsJsonAsync(Calls.Refresh, new RefreshRequest(session.RefreshToken, next));

        // Assert
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        retried.StatusCode.Should().Be(HttpStatusCode.OK);
        var tokens = await retried.Content.ReadFromJsonAsync<TokensResponse>();
        tokens!.RefreshToken.Should().Be(next);
        (await Calls.SendAsync(client, HttpMethod.Get, Calls.Me, tokens.AccessToken)).StatusCode.Should().Be(HttpStatusCode.OK);
    }
```

(Use the field name the class already uses for its `AuthApi` fixture in place of `api`.)

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Coo.Api.Application.Tests --filter FullyQualifiedName~SessionTests`
Expected: FAIL to compile (`RefreshLookup` has no `CurrentHash`; no three-argument `ExecuteAsync`). Paste the first error.

- [ ] **Step 3: Implement**

`src/Backend/Coo.Api.Application/Auth/Ports.cs`:

```csharp
/// <summary>The session a refresh token belongs to, whether it is its current or previous one, and its current hash.</summary>
public sealed record RefreshLookup(DeviceSession Session, RefreshMatch Match, string CurrentHash);
```

`src/Backend/Coo.Api.Application/Auth/Sessions.cs`, `RefreshSession`:

```csharp
/// <summary>
/// <c>POST /api/auth/refresh</c> (ADR 0003 §4, Spec 011 §8.2). Rotates on every use; the previous token again means theft,
/// unless it comes with the next token the phone proposed and that token is now the current one: then it is the phone
/// retrying after a lost response, and it gets a fresh access token while nothing changes.
/// </summary>
public sealed class RefreshSession(IDeviceSessionStore sessions, ITokenIssuer tokens, TimeProvider time)
{
    public static readonly TimeSpan IdleLimit = TimeSpan.FromDays(180);

    /// <summary>A proposal is 256 random bits in base64url (43 characters); anything shorter is not one.</summary>
    public const int NextTokenMinLength = 43;
    public const int NextTokenMaxLength = 128;

    /// <summary>A 0.10.0 phone: no proposal, the server picks the next token.</summary>
    public Task<Result<RefreshResult>> ExecuteAsync(string refreshToken, CancellationToken cancellationToken) =>
        ExecuteAsync(refreshToken, null, cancellationToken);

    public async Task<Result<RefreshResult>> ExecuteAsync(string refreshToken, string? nextRefreshToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
            return Result<RefreshResult>.Failure(AuthErrors.RefreshInvalid);
        if (nextRefreshToken is not null
            && (nextRefreshToken.Length is < NextTokenMinLength or > NextTokenMaxLength || nextRefreshToken == refreshToken))
            return Result<RefreshResult>.Failure(AuthErrors.RefreshInvalid);

        var hash = AuthSecrets.Hash(refreshToken);
        var nextHash = nextRefreshToken is null ? null : AuthSecrets.Hash(nextRefreshToken);
        var found = await sessions.FindByRefreshHashAsync(hash, cancellationToken);
        if (found is null || found.Session.IsRevoked)
            return Result<RefreshResult>.Failure(AuthErrors.RefreshInvalid);

        var now = time.GetUtcNow();
        if (now - found.Session.LastUsedAt > IdleLimit)
            return await RevokeAsync(found.Session, now, cancellationToken);

        if (found.Match == RefreshMatch.Previous)
        {
            return IsRetry(found, nextHash)
                ? new RefreshResult(tokens.Issue(found.Session.UserId, found.Session.Id, now), nextRefreshToken!)
                : await RevokeAsync(found.Session, now, cancellationToken);
        }

        var next = nextRefreshToken ?? AuthSecrets.NewToken();
        if (await sessions.RotateAsync(found.Session.Id, hash, AuthSecrets.Hash(next), now, cancellationToken))
            return new RefreshResult(tokens.Issue(found.Session.UserId, found.Session.Id, now), next);

        // Lost the compare-and-swap: a concurrent refresh with the same token won. With the same proposal that was this
        // phone's own retry; anything else gets what a concurrent loser always got.
        var again = await sessions.FindByRefreshHashAsync(hash, cancellationToken);
        return again is { Match: RefreshMatch.Previous } && !again.Session.IsRevoked && IsRetry(again, nextHash)
            ? new RefreshResult(tokens.Issue(again.Session.UserId, again.Session.Id, now), nextRefreshToken!)
            : Result<RefreshResult>.Failure(AuthErrors.RefreshInvalid);
    }

    private static bool IsRetry(RefreshLookup found, string? nextHash) => nextHash is not null && found.CurrentHash == nextHash;

    private async Task<Result<RefreshResult>> RevokeAsync(DeviceSession session, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await sessions.RevokeAsync(session.Id, now, cancellationToken);
        return Result<RefreshResult>.Failure(AuthErrors.RefreshInvalid);
    }
}
```

`src/Backend/Coo.Api.Infrastructure/Persistence/DeviceSessionStore.cs`: select the current hash as the ninth column.

```csharp
    private const string FindSql = SelectColumns + ", refresh_hash = @hash, refresh_hash FROM device_sessions "
        + "WHERE refresh_hash = @hash OR previous_refresh_hash = @hash LIMIT 1;";

    public async Task<RefreshLookup?> FindByRefreshHashAsync(string refreshHash, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(FindSql);
        command.Parameters.AddWithValue("hash", refreshHash);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;
        return new RefreshLookup(Read(reader), reader.GetBoolean(7) ? RefreshMatch.Current : RefreshMatch.Previous, reader.GetString(8));
    }
```

`src/Backend/Coo.Api/Hosting/AuthEndpoints.cs`, the `/refresh` handler:

```csharp
            var result = await refresh.ExecuteAsync(request.RefreshToken ?? "", request.NextRefreshToken, cancellationToken);
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Coo.Api.Application.Tests --filter FullyQualifiedName~SessionTests && dotnet test tests/Coo.Api.Infrastructure.Tests --filter FullyQualifiedName~AuthStoreTests && dotnet test tests/Coo.Api.IntegrationTests --filter FullyQualifiedName~AuthApiTests`
Expected: PASS, the Increment 10 refresh tests included (they call the two-argument overload).

- [ ] **Step 5: Commit**

```bash
dotnet format Coo.Tests.slnf
git add src/Backend tests/Coo.Api.Application.Tests tests/Coo.Api.Infrastructure.Tests tests/Coo.Api.IntegrationTests
git commit -m "Refresh: the phone may propose the next token; the same pair again is a retry, not theft (Spec 011 §8.2)"
```

---

### Task 4: The schema: three synced tables, `applied_changes`, `sync_conflicts`, `consents`, `memberships.revoked_at`

**Files:**
- Modify: `src/Backend/Coo.Api.Infrastructure/Persistence/Records.cs`, `CooDbContext.cs`
- Create (generated): `src/Backend/Coo.Api.Infrastructure/Persistence/Migrations/<timestamp>_Sync.cs`, its `.Designer.cs`;
  modify `CooDbContextModelSnapshot.cs`
- Test: `tests/Coo.Api.Infrastructure.Tests/SyncSchemaTests.cs`; modify `SchemaGuardTests.cs` (the expected table set)

**Interfaces:**
- Consumes: nothing new.
- Produces (column names the stores in Task 5 use):
  - `baby_profiles (id uuid PK, household_id uuid FK→households ON DELETE CASCADE, name varchar(40), nicknames text[],
    birth_date date NULL, due_date date NULL, version_wall_ms bigint, version_counter int, version_device uuid,
    is_deleted bool, seq bigint)`, unique `ix_baby_profiles_household_seq (household_id, seq)`.
  - `household_settings (household_id uuid PK FK→households CASCADE, volume_unit text CHECK IN ('ml','oz'),
    time_zone_id text, hidden_kinds text[], version_*, is_deleted, seq)`, unique `(household_id, seq)`.
  - `duplicate_dismissals (id uuid PK, household_id FK CASCADE, entry_a uuid, entry_b uuid CHECK entry_a <> entry_b,
    version_*, is_deleted, seq)`, unique `(household_id, seq)`.
  - `applied_changes (change_id uuid PK, result text CHECK IN ('applied','superseded','rejected'), reason text NULL,
    applied_at timestamptz)`, index on `applied_at`.
  - `sync_conflicts (id bigint identity PK, household_id FK CASCADE, change_id uuid, entity_type text, entity_id uuid,
    winner_wall_ms, winner_counter, winner_device, loser_wall_ms, loser_counter, loser_device, recorded_at timestamptz)`,
    index on `recorded_at`.
  - `consents (user_id uuid FK→AspNetUsers CASCADE, purpose text, wording text, given_at timestamptz,
    app_version varchar(40) NULL, language varchar(16) NULL)`, PK `(user_id, purpose, wording)`.
  - `memberships.revoked_at timestamptz NULL`.

- [ ] **Step 1: Write the failing tests**

`tests/Coo.Api.Infrastructure.Tests/SyncSchemaTests.cs`:

```csharp
using Npgsql;

namespace Coo.Api.Infrastructure.Tests;

/// <summary>Spec 011 §7.2: the new tables, their guards, and an upgrade of a database from Increment 10.</summary>
[Collection(PostgresTests.Name)]
public sealed class SyncSchemaTests(PostgresServer server)
{
    [Fact]
    public async Task Migrate_EmptyDatabase_CreatesTheSyncColumns()
    {
        // Arrange
        await using var db = await ServerDatabase.CreateAsync(server);

        // Act
        var columns = (string)(await db.ScalarAsync(
            """
            SELECT string_agg(table_name || '.' || column_name, ',')
            FROM information_schema.columns
            WHERE table_schema = 'public'
              AND (table_name IN ('baby_profiles', 'household_settings', 'duplicate_dismissals', 'applied_changes', 'sync_conflicts', 'consents')
                   OR (table_name = 'memberships' AND column_name = 'revoked_at'));
            """))!;

        // Assert
        columns.Split(',').Should().BeEquivalentTo(
            "baby_profiles.id", "baby_profiles.household_id", "baby_profiles.name", "baby_profiles.nicknames", "baby_profiles.birth_date",
            "baby_profiles.due_date", "baby_profiles.version_wall_ms", "baby_profiles.version_counter", "baby_profiles.version_device",
            "baby_profiles.is_deleted", "baby_profiles.seq",
            "household_settings.household_id", "household_settings.volume_unit", "household_settings.time_zone_id",
            "household_settings.hidden_kinds", "household_settings.version_wall_ms", "household_settings.version_counter",
            "household_settings.version_device", "household_settings.is_deleted", "household_settings.seq",
            "duplicate_dismissals.id", "duplicate_dismissals.household_id", "duplicate_dismissals.entry_a", "duplicate_dismissals.entry_b",
            "duplicate_dismissals.version_wall_ms", "duplicate_dismissals.version_counter", "duplicate_dismissals.version_device",
            "duplicate_dismissals.is_deleted", "duplicate_dismissals.seq",
            "applied_changes.change_id", "applied_changes.result", "applied_changes.reason", "applied_changes.applied_at",
            "sync_conflicts.id", "sync_conflicts.household_id", "sync_conflicts.change_id", "sync_conflicts.entity_type",
            "sync_conflicts.entity_id", "sync_conflicts.winner_wall_ms", "sync_conflicts.winner_counter", "sync_conflicts.winner_device",
            "sync_conflicts.loser_wall_ms", "sync_conflicts.loser_counter", "sync_conflicts.loser_device", "sync_conflicts.recorded_at",
            "consents.user_id", "consents.purpose", "consents.wording", "consents.given_at", "consents.app_version", "consents.language",
            "memberships.revoked_at");
    }

    [Fact]
    public async Task Migrate_FromSignInAndSessionsWithData_KeepsEveryRow()
    {
        // Arrange: production as Increment 10 left it, with a care entry.
        await using var db = await ServerDatabase.CreateAsync(server, migrate: false);
        await db.MigrateToAsync("20261002143234_SignInAndSessions");
        await db.Households.EnsureAsync(ServerDatabase.Household, CancellationToken.None);
        await db.ExecuteAsync("INSERT INTO entry_kind_versions (kind, version) VALUES ('diaper', 1) ON CONFLICT DO NOTHING;");
        await db.ExecuteAsync($"""
            INSERT INTO care_entries (id, household_id, kind, subject_baby_id, is_session, start_at, start_offset_min, logged_by, source,
                details, version_wall_ms, version_counter, version_device, is_deleted, seq)
            VALUES ('{db.NewId()}', '{ServerDatabase.Household.Value}', 'diaper', '{ServerDatabase.Baby.Value}', false, now(), 120,
                'user:7a1c0000-0000-7000-8000-0000000000c1', 'manual', '{"{"}"v":1,"contents":"wet"{"}"}', 1, 0, '{ServerDatabase.Device.Value}', false, 1);
            """);

        // Act
        await db.MigrateAsync();

        // Assert
        (await db.ScalarAsync("SELECT count(*) FROM care_entries;")).Should().Be(1L);
    }

    [Theory]
    [InlineData("INSERT INTO household_settings (household_id, volume_unit, time_zone_id, hidden_kinds, version_wall_ms, version_counter, version_device, is_deleted, seq) VALUES ('{H}', 'litres', 'Europe/Amsterdam', '{}', 1, 0, '{D}', false, 1);")]
    [InlineData("INSERT INTO duplicate_dismissals (id, household_id, entry_a, entry_b, version_wall_ms, version_counter, version_device, is_deleted, seq) VALUES ('{N}', '{H}', '{D}', '{D}', 1, 0, '{D}', false, 1);")]
    [InlineData("INSERT INTO applied_changes (change_id, result, reason, applied_at) VALUES ('{N}', 'maybe', NULL, now());")]
    public async Task RawInsert_AgainstAGuard_IsRefusedByTheDatabase(string sql)
    {
        // Arrange
        await using var db = await ServerDatabase.CreateAsync(server);
        await db.Households.EnsureAsync(ServerDatabase.Household, CancellationToken.None);
        var statement = sql.Replace("{H}", ServerDatabase.Household.Value.ToString(), StringComparison.Ordinal)
            .Replace("{D}", ServerDatabase.Device.Value.ToString(), StringComparison.Ordinal)
            .Replace("{N}", db.NewId().ToString(), StringComparison.Ordinal);

        // Act
        var act = async () => await db.ExecuteAsync(statement);

        // Assert
        await act.Should().ThrowAsync<PostgresException>();
    }

    [Fact]
    public async Task RawInsert_SecondRowWithTheSameSeqAcrossOneTable_IsRefused()
    {
        // Arrange
        await using var db = await ServerDatabase.CreateAsync(server);
        await db.Households.EnsureAsync(ServerDatabase.Household, CancellationToken.None);
        string Insert() => $"""
            INSERT INTO baby_profiles (id, household_id, name, nicknames, birth_date, version_wall_ms, version_counter, version_device, is_deleted, seq)
            VALUES ('{db.NewId()}', '{ServerDatabase.Household.Value}', 'Maja', '{"{}"}', '2026-08-20', 1, 0, '{ServerDatabase.Device.Value}', false, 7);
            """;
        await db.ExecuteAsync(Insert());

        // Act
        var act = async () => await db.ExecuteAsync(Insert());

        // Assert
        (await act.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
    }

    [Fact]
    public async Task DeleteHousehold_WithSyncedRows_CascadesToTheNewTables()
    {
        // Arrange: account deletion removes a household; its profiles, settings, dismissals and conflicts go with it.
        await using var db = await ServerDatabase.CreateAsync(server);
        await db.Households.EnsureAsync(ServerDatabase.Household, CancellationToken.None);
        await db.ExecuteAsync($"""
            INSERT INTO household_settings (household_id, volume_unit, time_zone_id, hidden_kinds, version_wall_ms, version_counter, version_device, is_deleted, seq)
            VALUES ('{ServerDatabase.Household.Value}', 'ml', 'Europe/Amsterdam', '{"{}"}', 1, 0, '{ServerDatabase.Device.Value}', false, 1);
            INSERT INTO sync_conflicts (household_id, change_id, entity_type, entity_id, winner_wall_ms, winner_counter, winner_device,
                loser_wall_ms, loser_counter, loser_device, recorded_at)
            VALUES ('{ServerDatabase.Household.Value}', '{db.NewId()}', 'CareEntry', '{db.NewId()}', 2, 0, '{ServerDatabase.Device.Value}',
                1, 0, '{ServerDatabase.Device.Value}', now());
            """);

        // Act
        await db.ExecuteAsync($"DELETE FROM households WHERE id = '{ServerDatabase.Household.Value}';");

        // Assert
        (await db.ScalarAsync("SELECT (SELECT count(*) FROM household_settings) + (SELECT count(*) FROM sync_conflicts);")).Should().Be(0L);
    }
}
```

In `tests/Coo.Api.Infrastructure.Tests/SchemaGuardTests.cs`, the expected table set of
`Migrate_EmptyDatabase_CreatesTablesAndRegistersEveryKindVersion` gains `"applied_changes"`, `"baby_profiles"`,
`"consents"`, `"duplicate_dismissals"`, `"household_settings"`, `"sync_conflicts"`.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Coo.Api.Infrastructure.Tests --filter "FullyQualifiedName~SyncSchemaTests|FullyQualifiedName~SchemaGuardTests"`
Expected: FAIL (the columns query returns only `memberships.*` none of the new names; the expected tables are missing).
Paste the first assertion message.

- [ ] **Step 3: The records**

Append to `src/Backend/Coo.Api.Infrastructure/Persistence/Records.cs`:

```csharp
/// <summary><c>baby_profiles</c> (Spec 011 §7.2): a synced record; <c>seq</c> from the household's counter.</summary>
internal sealed class BabyProfileRecord
{
    public required Guid Id { get; init; }

    public required Guid HouseholdId { get; init; }

    public required string Name { get; init; }

    public required List<string> Nicknames { get; init; }

    public DateOnly? BirthDate { get; init; }

    public DateOnly? DueDate { get; init; }

    public required long VersionWallMs { get; init; }

    public required int VersionCounter { get; init; }

    public required Guid VersionDevice { get; init; }

    public required bool IsDeleted { get; init; }

    public required long Seq { get; init; }
}

/// <summary><c>household_settings</c>: one per household, keyed by it (Spec 001 §5.3).</summary>
internal sealed class HouseholdSettingsRecord
{
    public required Guid HouseholdId { get; init; }

    public required string VolumeUnit { get; init; }

    public required string TimeZoneId { get; init; }

    public required List<string> HiddenKinds { get; init; }

    public required long VersionWallMs { get; init; }

    public required int VersionCounter { get; init; }

    public required Guid VersionDevice { get; init; }

    public required bool IsDeleted { get; init; }

    public required long Seq { get; init; }
}

/// <summary><c>duplicate_dismissals</c>: "keep both" decisions (Q6), the pair in canonical order.</summary>
internal sealed class DuplicateDismissalRecord
{
    public required Guid Id { get; init; }

    public required Guid HouseholdId { get; init; }

    public required Guid EntryA { get; init; }

    public required Guid EntryB { get; init; }

    public required long VersionWallMs { get; init; }

    public required int VersionCounter { get; init; }

    public required Guid VersionDevice { get; init; }

    public required bool IsDeleted { get; init; }

    public required long Seq { get; init; }
}

/// <summary><c>applied_changes</c>: the result of every decided change ID, for 30 days (ADR 0002 §5).</summary>
internal sealed class AppliedChangeRecord
{
    public required Guid ChangeId { get; init; }

    public required string Result { get; init; }

    public string? Reason { get; init; }

    public required DateTime AppliedAt { get; init; }
}

/// <summary><c>sync_conflicts</c>: both version stamps of a losing write (ruling 4), for 30 days.</summary>
internal sealed class SyncConflictRecord
{
    public long Id { get; init; }

    public required Guid HouseholdId { get; init; }

    public required Guid ChangeId { get; init; }

    public required string EntityType { get; init; }

    public required Guid EntityId { get; init; }

    public required long WinnerWallMs { get; init; }

    public required int WinnerCounter { get; init; }

    public required Guid WinnerDevice { get; init; }

    public required long LoserWallMs { get; init; }

    public required int LoserCounter { get; init; }

    public required Guid LoserDevice { get; init; }

    public required DateTime RecordedAt { get; init; }
}

/// <summary><c>consents</c> (Spec 011 §3, Q1): per account, with the wording version, provable (GDPR Art. 7(1)).</summary>
internal sealed class UserConsentRecord
{
    public required Guid UserId { get; init; }

    public required string Purpose { get; init; }

    public required string Wording { get; init; }

    public required DateTime GivenAt { get; init; }

    public string? AppVersion { get; init; }

    public string? Language { get; init; }
}
```

and add to `MembershipRecord`:

```csharp
    /// <summary>Set when the member is removed (Increment 11b); read by the membership check (Spec 011 §6.5).</summary>
    public DateTime? RevokedAt { get; init; }
```

- [ ] **Step 4: The model**

In `CooDbContext.cs`, add the `DbSet`s (`BabyProfiles`, `HouseholdSettings`, `DuplicateDismissals`, `AppliedChanges`,
`SyncConflicts`, `Consents`) and, at the end of `OnModelCreating`:

```csharp
        modelBuilder.Entity<MembershipRecord>(e => e.Property(m => m.RevokedAt).HasColumnName("revoked_at").HasColumnType("timestamptz"));

        modelBuilder.Entity<BabyProfileRecord>(e =>
        {
            e.ToTable("baby_profiles", t =>
            {
                t.HasCheckConstraint("ck_baby_profiles_date", "birth_date IS NOT NULL OR due_date IS NOT NULL");
                t.HasCheckConstraint("ck_baby_profiles_seq", "seq >= 1");
            });
            e.HasKey(b => b.Id);
            e.Property(b => b.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(b => b.HouseholdId).HasColumnName("household_id");
            e.Property(b => b.Name).HasColumnName("name").HasMaxLength(40);
            e.Property(b => b.Nicknames).HasColumnName("nicknames");
            e.Property(b => b.BirthDate).HasColumnName("birth_date");
            e.Property(b => b.DueDate).HasColumnName("due_date");
            SyncColumns(e);
            e.HasOne<HouseholdRecord>().WithMany().HasForeignKey(b => b.HouseholdId)
                .HasConstraintName("fk_baby_profiles_households").OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(b => new { b.HouseholdId, b.Seq }).IsUnique().HasDatabaseName("ix_baby_profiles_household_seq");
        });

        modelBuilder.Entity<HouseholdSettingsRecord>(e =>
        {
            e.ToTable("household_settings", t =>
            {
                t.HasCheckConstraint("ck_household_settings_unit", "volume_unit IN ('ml', 'oz')");
                t.HasCheckConstraint("ck_household_settings_seq", "seq >= 1");
            });
            e.HasKey(s => s.HouseholdId);
            e.Property(s => s.HouseholdId).HasColumnName("household_id").ValueGeneratedNever();
            e.Property(s => s.VolumeUnit).HasColumnName("volume_unit");
            e.Property(s => s.TimeZoneId).HasColumnName("time_zone_id");
            e.Property(s => s.HiddenKinds).HasColumnName("hidden_kinds");
            SyncColumns(e);
            e.HasOne<HouseholdRecord>().WithMany().HasForeignKey(s => s.HouseholdId)
                .HasConstraintName("fk_household_settings_households").OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(s => new { s.HouseholdId, s.Seq }).IsUnique().HasDatabaseName("ix_household_settings_household_seq");
        });

        modelBuilder.Entity<DuplicateDismissalRecord>(e =>
        {
            e.ToTable("duplicate_dismissals", t =>
            {
                t.HasCheckConstraint("ck_duplicate_dismissals_pair", "entry_a <> entry_b");
                t.HasCheckConstraint("ck_duplicate_dismissals_seq", "seq >= 1");
            });
            e.HasKey(d => d.Id);
            e.Property(d => d.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(d => d.HouseholdId).HasColumnName("household_id");
            e.Property(d => d.EntryA).HasColumnName("entry_a");
            e.Property(d => d.EntryB).HasColumnName("entry_b");
            SyncColumns(e);
            e.HasOne<HouseholdRecord>().WithMany().HasForeignKey(d => d.HouseholdId)
                .HasConstraintName("fk_duplicate_dismissals_households").OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(d => new { d.HouseholdId, d.Seq }).IsUnique().HasDatabaseName("ix_duplicate_dismissals_household_seq");
        });

        modelBuilder.Entity<AppliedChangeRecord>(e =>
        {
            e.ToTable("applied_changes", t => t.HasCheckConstraint("ck_applied_changes_result", "result IN ('applied', 'superseded', 'rejected')"));
            e.HasKey(a => a.ChangeId);
            e.Property(a => a.ChangeId).HasColumnName("change_id").ValueGeneratedNever();
            e.Property(a => a.Result).HasColumnName("result");
            e.Property(a => a.Reason).HasColumnName("reason");
            e.Property(a => a.AppliedAt).HasColumnName("applied_at").HasColumnType("timestamptz");
            e.HasIndex(a => a.AppliedAt).HasDatabaseName("ix_applied_changes_applied_at");
        });

        modelBuilder.Entity<SyncConflictRecord>(e =>
        {
            e.ToTable("sync_conflicts");
            e.HasKey(c => c.Id);
            e.Property(c => c.Id).HasColumnName("id").UseIdentityByDefaultColumn();
            e.Property(c => c.HouseholdId).HasColumnName("household_id");
            e.Property(c => c.ChangeId).HasColumnName("change_id");
            e.Property(c => c.EntityType).HasColumnName("entity_type");
            e.Property(c => c.EntityId).HasColumnName("entity_id");
            e.Property(c => c.WinnerWallMs).HasColumnName("winner_wall_ms");
            e.Property(c => c.WinnerCounter).HasColumnName("winner_counter");
            e.Property(c => c.WinnerDevice).HasColumnName("winner_device");
            e.Property(c => c.LoserWallMs).HasColumnName("loser_wall_ms");
            e.Property(c => c.LoserCounter).HasColumnName("loser_counter");
            e.Property(c => c.LoserDevice).HasColumnName("loser_device");
            e.Property(c => c.RecordedAt).HasColumnName("recorded_at").HasColumnType("timestamptz");
            e.HasOne<HouseholdRecord>().WithMany().HasForeignKey(c => c.HouseholdId)
                .HasConstraintName("fk_sync_conflicts_households").OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(c => c.RecordedAt).HasDatabaseName("ix_sync_conflicts_recorded_at");
        });

        modelBuilder.Entity<UserConsentRecord>(e =>
        {
            e.ToTable("consents");
            e.HasKey(c => new { c.UserId, c.Purpose, c.Wording });
            e.Property(c => c.UserId).HasColumnName("user_id");
            e.Property(c => c.Purpose).HasColumnName("purpose");
            e.Property(c => c.Wording).HasColumnName("wording");
            e.Property(c => c.GivenAt).HasColumnName("given_at").HasColumnType("timestamptz");
            e.Property(c => c.AppVersion).HasColumnName("app_version").HasMaxLength(40);
            e.Property(c => c.Language).HasColumnName("language").HasMaxLength(16);
            e.HasOne<CooUser>().WithMany().HasForeignKey(c => c.UserId)
                .HasConstraintName("fk_consents_users").OnDelete(DeleteBehavior.Cascade);
        });
```

with the shared columns as a private generic helper in the same class:

```csharp
    /// <summary>The ADR 0002 sync columns every synced table carries.</summary>
    private static void SyncColumns<T>(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<T> e)
        where T : class
    {
        e.Property<long>("VersionWallMs").HasColumnName("version_wall_ms");
        e.Property<int>("VersionCounter").HasColumnName("version_counter");
        e.Property<Guid>("VersionDevice").HasColumnName("version_device");
        e.Property<bool>("IsDeleted").HasColumnName("is_deleted");
        e.Property<long>("Seq").HasColumnName("seq");
    }
```

(`Property<T>(string)` maps the CLR property of that name; if EF reports a shadow-property clash, spell the five
lines out in each entity instead.)

- [ ] **Step 5: Generate the migration**

```sh
dotnet tool restore
dotnet tool run dotnet-ef migrations add Sync --project src/Backend/Coo.Api.Infrastructure \
  --startup-project src/Backend/Coo.Api.Infrastructure --output-dir Persistence/Migrations \
  --namespace Coo.Api.Infrastructure.Persistence.Migrations
```

Then, as `BUILD.md` says: make the two generated migration classes `internal`; if EF wrote the snapshot elsewhere,
move it over `Persistence/Migrations/CooDbContextModelSnapshot.cs`. Read the generated `Up`: it must create exactly
the six tables above, add the nullable `memberships.revoked_at`, the checks, FKs and indexes; it must not alter
`care_entries`, `households` or Identity's tables. `nicknames` and `hidden_kinds` must be `text[]`.

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test tests/Coo.Api.Infrastructure.Tests`
Expected: PASS, the Increment 8 and 10 store and schema tests included.

- [ ] **Step 7: Commit**

```bash
dotnet format Coo.Tests.slnf
git add src/Backend/Coo.Api.Infrastructure tests/Coo.Api.Infrastructure.Tests
git commit -m "Server schema: baby_profiles, household_settings, duplicate_dismissals, applied_changes, sync_conflicts, consents, memberships.revoked_at (Spec 011 §7.2)"
```

---
### Task 5: The server's stores: last-writer-wins in one transaction, `seq` across four tables, the pull, membership, consent

**Files:**
- Create: `src/Backend/Coo.Api.Infrastructure/Persistence/CareEntryWriter.cs` (the upsert, moved out of `CareEntryStore`)
- Modify: `src/Backend/Coo.Api.Infrastructure/Persistence/CareEntryStore.cs` (uses `CareEntryWriter`),
  `ServerColumns.cs` (person and source delegate to `SyncText`)
- Create: `src/Backend/Coo.Api.Infrastructure/Persistence/SyncStore.cs`, `MembershipStore.cs`, `ConsentStore.cs`
- Modify: `src/Backend/Coo.Api.Infrastructure/ServiceCollectionExtensions.cs` (registers the three stores)
- Test: `tests/Coo.Api.Infrastructure.Tests/SyncStoreTests.cs`; modify `PostgresServer.cs` (`ServerDatabase` exposes them)

**Interfaces:**
- Consumes: Task 2's ports, Task 1's model and `LastWriterWinsRule`, Task 4's tables.
- Produces: `internal sealed class SyncStore(NpgsqlDataSource) : ISyncStore`, `MembershipStore(NpgsqlDataSource) :
  IMembershipStore`, `ConsentStore(NpgsqlDataSource) : IConsentStore`, all singletons;
  `CareEntryWriter.UpsertSql`, `CareEntryWriter.AddParameters(NpgsqlCommand, CareEntry, string details, long seq)`.

- [ ] **Step 1: Write the failing tests**

In `tests/Coo.Api.Infrastructure.Tests/PostgresServer.cs`, `ServerDatabase` gains:

```csharp
    public ISyncStore Sync => _services.GetRequiredService<ISyncStore>();

    public IMembershipStore Memberships => _services.GetRequiredService<IMembershipStore>();

    public IConsentStore Consents => _services.GetRequiredService<IConsentStore>();
```

(with `using Coo.Api.Application.Sync;`).

`tests/Coo.Api.Infrastructure.Tests/SyncStoreTests.cs`:

```csharp
using Coo.Api.Application.Auth;
using Coo.Api.Application.Sync;
using Coo.Contracts.Sync;
using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Duplicates;
using Coo.Domain.CareLog.Kinds.Diaper;
using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;
using Coo.Domain.Households;
using Microsoft.Extensions.DependencyInjection;

namespace Coo.Api.Infrastructure.Tests;

/// <summary>Spec 011 §7.2 and §10 (Api.Infrastructure row), against a real PostgreSQL 17.</summary>
[Collection(PostgresTests.Name)]
public sealed class SyncStoreTests(PostgresServer server)
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 9, 0, 0, TimeSpan.FromHours(2));
    private static readonly HouseholdId Household = ServerDatabase.Household;

    private static async Task<ServerDatabase> DatabaseAsync(PostgresServer server)
    {
        var db = await ServerDatabase.CreateAsync(server);
        await db.Households.EnsureAsync(Household, CancellationToken.None);
        return db;
    }

    private static CareEntryWrite Diaper(
        ServerDatabase db, Guid id, HlcStamp version, string details = """{"v":1,"contents":"wet"}""", HouseholdId? household = null) =>
        new(CareEntry.Create(new EntryId(id), household ?? Household, EntrySubject.Baby(ServerDatabase.Baby), new DiaperDetails(DiaperContents.Wet),
            EntryTiming.Point(T0), PersonRef.User(new UserId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000c1"))), EntrySource.Manual,
            version, T0).Value, details);

    private static Task<ChangeResult> ApplyAsync(ServerDatabase db, SyncWrite write, Guid? changeId = null) =>
        db.Sync.ApplyAsync(Household, changeId ?? db.NewId(), write, T0, CancellationToken.None);

    [Fact]
    public async Task ApplyAsync_NewEntry_StoresItWithTheHouseholdsNextSeqAndRecordsTheResult()
    {
        // Arrange
        await using var db = await DatabaseAsync(server);
        var id = db.NewId();
        var change = db.NewId();

        // Act
        var result = await ApplyAsync(db, Diaper(db, id, db.NextStamp()), change);

        // Assert
        result.Should().Be(ChangeResult.Applied);
        (await db.ScalarAsync($"SELECT seq FROM care_entries WHERE id = '{id}';")).Should().Be(1L);
        (await db.Sync.FindResultAsync(change, CancellationToken.None)).Should().Be(ChangeResult.Applied);
    }

    [Fact]
    public async Task ApplyAsync_OneOfEachType_TakesConsecutiveSeqsFromOneCounter()
    {
        // Arrange: one counter per household serves all four tables (ADR 0002 §6).
        await using var db = await DatabaseAsync(server);
        var a = db.NewId();
        var b = db.NewId();
        var baby = BabyProfile.Create(new BabyId(db.NewId()), Household, "Maja", ["Majka"], new DateOnly(2026, 8, 20), null, db.NextStamp(), new DateOnly(2026, 10, 1)).Value;
        var settings = HouseholdSettings.Create(Household, VolumeUnit.Ounces, "Europe/Amsterdam", db.NextStamp(), [EntryKindCode.From("pump").Value]).Value;

        // Act
        await ApplyAsync(db, Diaper(db, a, db.NextStamp()));
        await ApplyAsync(db, new BabyProfileWrite(baby));
        await ApplyAsync(db, new HouseholdSettingsWrite(settings));
        await ApplyAsync(db, new DismissalWrite(DuplicateDismissal.Create(new DismissalId(db.NewId()), Household,
            new EntryPair(new EntryId(a), new EntryId(b)), db.NextStamp())));

        // Assert
        (await db.ScalarAsync(
            """
            SELECT string_agg(seq::text, ',' ORDER BY seq) FROM (
                SELECT seq FROM care_entries UNION ALL SELECT seq FROM baby_profiles
                UNION ALL SELECT seq FROM household_settings UNION ALL SELECT seq FROM duplicate_dismissals) s;
            """)).Should().Be("1,2,3,4");
        (await db.ScalarAsync($"SELECT next_seq FROM households WHERE id = '{Household.Value}';")).Should().Be(5L);
    }

    [Fact]
    public async Task ApplyAsync_SupersededWrite_TakesNoSeqAndRecordsBothVersions()
    {
        // Arrange
        await using var db = await DatabaseAsync(server);
        var id = db.NewId();
        var older = db.NextStamp();
        var newer = db.NextStamp();
        await ApplyAsync(db, Diaper(db, id, newer));

        // Act
        var result = await ApplyAsync(db, Diaper(db, id, older));

        // Assert
        result.Should().Be(ChangeResult.Superseded);
        (await db.ScalarAsync($"SELECT next_seq FROM households WHERE id = '{Household.Value}';")).Should().Be(2L);
        (await db.ScalarAsync("SELECT winner_wall_ms::text || '>' || loser_wall_ms::text FROM sync_conflicts;"))
            .Should().Be($"{newer.WallClockMs}>{older.WallClockMs}");
    }

    [Fact]
    public async Task ApplyAsync_DeleteOfAnUnknownRecord_IsAppliedAndStoresNothing()
    {
        // Arrange: created and deleted while offline, never pushed before (Review Focus 4).
        await using var db = await DatabaseAsync(server);

        // Act
        var result = await ApplyAsync(db, new TombstoneWrite(SyncEntityTypes.CareEntry, db.NewId(), db.NextStamp()));

        // Assert
        result.Should().Be(ChangeResult.Applied);
        (await db.ScalarAsync("SELECT count(*) FROM care_entries;")).Should().Be(0L);
        (await db.ScalarAsync($"SELECT next_seq FROM households WHERE id = '{Household.Value}';")).Should().Be(1L);
    }

    [Fact]
    public async Task ApplyAsync_TombstoneThenHigherEdit_RevivesTheRowWithANewSeq()
    {
        // Arrange
        await using var db = await DatabaseAsync(server);
        var id = db.NewId();
        await ApplyAsync(db, Diaper(db, id, db.NextStamp()));
        await ApplyAsync(db, new TombstoneWrite(SyncEntityTypes.CareEntry, id, db.NextStamp()));

        // Act
        var result = await ApplyAsync(db, Diaper(db, id, db.NextStamp()));

        // Assert
        result.Should().Be(ChangeResult.Applied);
        (await db.ScalarAsync($"SELECT is_deleted::text || ':' || seq::text FROM care_entries WHERE id = '{id}';")).Should().Be("false:3");
    }

    [Fact]
    public async Task ApplyAsync_IdOfAnotherHousehold_IsRejectedAndTheRowStays()
    {
        // Arrange
        await using var db = await DatabaseAsync(server);
        var other = new HouseholdId(db.NewId());
        await db.Households.EnsureAsync(other, CancellationToken.None);
        var id = db.NewId();
        await db.Sync.ApplyAsync(other, db.NewId(), Diaper(db, id, db.NextStamp(), household: other), T0, CancellationToken.None);

        // Act
        var result = await ApplyAsync(db, Diaper(db, id, db.NextStamp()));

        // Assert
        result.Should().Be(ChangeResult.Rejected(RejectionReasons.HouseholdMismatch));
        (await db.ScalarAsync($"SELECT household_id FROM care_entries WHERE id = '{id}';")).Should().Be(other.Value);
    }

    [Fact]
    public async Task ApplyAsync_HouseholdUnknownToTheServer_FailsWholeAndRecordsNothing()
    {
        // Arrange: the counter row is missing, so the transaction fails after the row lock and before the result.
        await using var db = await ServerDatabase.CreateAsync(server);
        var change = db.NewId();

        // Act
        var act = async () => await ApplyAsync(db, Diaper(db, db.NewId(), db.NextStamp()), change);

        // Assert
        await act.Should().ThrowAsync<Exception>();
        (await db.Sync.FindResultAsync(change, CancellationToken.None)).Should().BeNull();
        (await db.ScalarAsync("SELECT count(*) FROM care_entries;")).Should().Be(0L);
    }

    [Fact]
    public async Task ApplyAsync_SameChangeTwiceAtOnce_StoresOneResultAndBothSeeIt()
    {
        // Arrange: the phone's retry races its own first attempt.
        await using var db = await DatabaseAsync(server);
        var change = db.NewId();
        var write = Diaper(db, db.NewId(), db.NextStamp());

        // Act
        var results = await Task.WhenAll(ApplyAsync(db, write, change), ApplyAsync(db, write, change));

        // Assert
        results.Should().OnlyContain(r => r == ChangeResult.Applied);
        (await db.ScalarAsync($"SELECT count(*) FROM applied_changes WHERE change_id = '{change}';")).Should().Be(1L);
        (await db.ScalarAsync("SELECT count(*) FROM care_entries;")).Should().Be(1L);
    }

    [Fact]
    public async Task ApplyAsync_DetailsWithAPropertyThisBuildDoesNotKnow_KeepsIt()
    {
        // Arrange: a newer phone added "stoolColour"; the server's codec does not know it yet (ruling 6).
        await using var db = await DatabaseAsync(server);
        var id = db.NewId();

        // Act
        await ApplyAsync(db, Diaper(db, id, db.NextStamp(), """{"v":1,"contents":"wet","stoolColour":"green"}"""));

        // Assert
        (await db.ScalarAsync($"SELECT details->>'stoolColour' FROM care_entries WHERE id = '{id}';")).Should().Be("green");
    }

    [Fact]
    public async Task RecordRejectionAsync_ThenAgain_KeepsTheFirstReason()
    {
        // Arrange
        await using var db = await DatabaseAsync(server);
        var change = db.NewId();
        await db.Sync.RecordRejectionAsync(change, "sync.stamp-in-future", T0, CancellationToken.None);

        // Act
        var second = await db.Sync.RecordRejectionAsync(change, "something-else", T0, CancellationToken.None);

        // Assert
        second.Should().Be(ChangeResult.Rejected("sync.stamp-in-future"));
    }

    [Fact]
    public async Task PullAsync_SevenRecordsInPagesOfThree_ReturnsThemInSeqOrderAcrossTables()
    {
        // Arrange
        await using var db = await DatabaseAsync(server);
        for (var i = 0; i < 5; i++)
            await ApplyAsync(db, Diaper(db, db.NewId(), db.NextStamp()));
        await ApplyAsync(db, new BabyProfileWrite(BabyProfile.Create(new BabyId(db.NewId()), Household, "Maja", [], new DateOnly(2026, 8, 20), null, db.NextStamp(), new DateOnly(2026, 10, 1)).Value));
        await ApplyAsync(db, new HouseholdSettingsWrite(HouseholdSettings.Create(Household, VolumeUnit.Millilitres, "Europe/Amsterdam", db.NextStamp()).Value));

        // Act
        var first = await db.Sync.PullAsync(Household, 0, null, 3, CancellationToken.None);
        var second = await db.Sync.PullAsync(Household, first.Cursor, first.BootstrapHead, 3, CancellationToken.None);
        var third = await db.Sync.PullAsync(Household, second.Cursor, second.BootstrapHead, 3, CancellationToken.None);

        // Assert
        first.Records.Select(r => r.Seq).Should().Equal(1, 2, 3);
        first.HasMore.Should().BeTrue();
        first.BootstrapHead.Should().Be(7);
        second.Records.Select(r => r.Seq).Should().Equal(4, 5, 6);
        second.Records[^1].EntityType.Should().Be(SyncEntityTypes.BabyProfile);
        third.Records.Single().Payload.Should().BeOfType<HouseholdSettingsPayload>();
        third.HasMore.Should().BeFalse();
        third.Cursor.Should().Be(7);
    }

    [Fact]
    public async Task PullAsync_Bootstrap_SkipsTombstonesAtOrBelowTheHeadAndSendsOnesAbove()
    {
        // Arrange: A, B, C written; A deleted (seq 4, at the head) before the bootstrap starts.
        await using var db = await DatabaseAsync(server);
        var (a, b, c) = (db.NewId(), db.NewId(), db.NewId());
        await ApplyAsync(db, Diaper(db, a, db.NextStamp()));
        await ApplyAsync(db, Diaper(db, b, db.NextStamp()));
        await ApplyAsync(db, Diaper(db, c, db.NextStamp()));
        await ApplyAsync(db, new TombstoneWrite(SyncEntityTypes.CareEntry, a, db.NextStamp()));
        var first = await db.Sync.PullAsync(Household, 0, null, 2, CancellationToken.None);

        // Act: B is deleted while the bootstrap is half-way (its tombstone gets seq 5, above the head).
        await ApplyAsync(db, new TombstoneWrite(SyncEntityTypes.CareEntry, b, db.NextStamp()));
        var second = await db.Sync.PullAsync(Household, first.Cursor, first.BootstrapHead, 2, CancellationToken.None);

        // Assert
        first.BootstrapHead.Should().Be(4);
        first.Records.Select(r => r.EntityId).Should().Equal(b, c);
        first.HasMore.Should().BeTrue("A's tombstone, at seq 4, is still above the cursor");
        second.Records.Should().ContainSingle().Which.Should().Match<PulledRecord>(r => r.EntityId == b && r.IsDeleted);
        second.Cursor.Should().Be(5, "the cursor moves past the skipped tombstone of A");
        second.HasMore.Should().BeFalse();
    }

    [Fact]
    public async Task PullAsync_FromZeroWithHeadZero_SendsEveryTombstone()
    {
        // Arrange: a phone that already holds entries asks for every tombstone (ruling 20).
        await using var db = await DatabaseAsync(server);
        var a = db.NewId();
        await ApplyAsync(db, Diaper(db, a, db.NextStamp()));
        await ApplyAsync(db, new TombstoneWrite(SyncEntityTypes.CareEntry, a, db.NextStamp()));

        // Act
        var page = await db.Sync.PullAsync(Household, 0, 0, 10, CancellationToken.None);

        // Assert
        page.Records.Should().ContainSingle().Which.IsDeleted.Should().BeTrue();
        page.BootstrapHead.Should().Be(0);
    }

    [Fact]
    public async Task PullAsync_CareEntry_ReturnsTheStoredDetailsAndTheOffsetPeopleSaw()
    {
        // Arrange
        await using var db = await DatabaseAsync(server);
        var id = db.NewId();
        await ApplyAsync(db, Diaper(db, id, db.NextStamp()));

        // Act
        var page = await db.Sync.PullAsync(Household, 0, null, 10, CancellationToken.None);

        // Assert
        var payload = page.Records.Single().Payload.Should().BeOfType<CareEntryPayload>().Subject;
        payload.StartAt.Should().Be(T0);
        payload.StartAt.Offset.Should().Be(TimeSpan.FromHours(2));
        payload.DetailsJson.Should().Contain("\"contents\"");
        payload.LoggedBy.Should().Be("user:7a1c0000-0000-7000-8000-0000000000c1");
    }

    [Fact]
    public async Task PurgeAsync_ResultsAndConflictsOlderThanTheCutoff_Go()
    {
        // Arrange
        await using var db = await DatabaseAsync(server);
        var id = db.NewId();
        var newer = db.NextStamp();
        await ApplyAsync(db, Diaper(db, id, db.NextStamp()));
        await db.Sync.ApplyAsync(Household, db.NewId(), Diaper(db, id, newer), T0.AddDays(40), CancellationToken.None);
        await db.Sync.ApplyAsync(Household, db.NewId(), Diaper(db, id, newer), T0.AddDays(40), CancellationToken.None);   // superseded: a conflict at day 40

        // Act
        var deleted = await db.Sync.PurgeAsync(T0.AddDays(10), CancellationToken.None);

        // Assert
        deleted.Should().Be(1);
        (await db.ScalarAsync("SELECT count(*) FROM applied_changes;")).Should().Be(2L);
        (await db.ScalarAsync("SELECT count(*) FROM sync_conflicts;")).Should().Be(1L);
    }

    [Fact]
    public async Task FindAsync_Memberships_TellActiveRevokedAndNever()
    {
        // Arrange
        await using var db = await ServerDatabase.CreateAsync(server);
        var (active, revoked, never) = (new UserId(db.NewId()), new UserId(db.NewId()), new UserId(db.NewId()));
        await CreateAccountAsync(db, active, "sub-a", Household);
        await CreateAccountAsync(db, revoked, "sub-r", new HouseholdId(db.NewId()));
        await db.ExecuteAsync($"INSERT INTO memberships (household_id, user_id, role, joined_at, revoked_at) VALUES ('{Household.Value}', '{revoked.Value}', 'parent', now(), now());");

        // Act
        var a = await db.Memberships.FindAsync(Household, active, CancellationToken.None);
        var r = await db.Memberships.FindAsync(Household, revoked, CancellationToken.None);
        var n = await db.Memberships.FindAsync(Household, never, CancellationToken.None);

        // Assert
        a.Membership!.Role.Should().Be(Role.Owner);
        a.IsRevoked.Should().BeFalse();
        r.IsRevoked.Should().BeTrue();
        n.Should().Be(MembershipLookup.None);
    }

    [Fact]
    public async Task RecordAsync_SameConsentTwice_KeepsTheFirst()
    {
        // Arrange
        await using var db = await ServerDatabase.CreateAsync(server);
        var user = new UserId(db.NewId());
        await CreateAccountAsync(db, user, "sub-c", Household);
        await db.Consents.RecordAsync(new ConsentRecord(user, CareDataConsent.Purpose, CareDataConsent.CurrentWording, T0, "0.11.0", "pl"), CancellationToken.None);

        // Act
        await db.Consents.RecordAsync(new ConsentRecord(user, CareDataConsent.Purpose, CareDataConsent.CurrentWording, T0.AddDays(1), "0.11.1", "en"), CancellationToken.None);
        var found = await db.Consents.FindAsync(user, CareDataConsent.Purpose, CareDataConsent.CurrentWording, CancellationToken.None);

        // Assert
        found!.GivenAt.Should().Be(T0);
        found.Language.Should().Be("pl");
    }

    [Fact]
    public async Task DeleteAccountAsync_LastParent_RemovesTheSyncedRowsAndTheConsent()
    {
        // Arrange
        await using var db = await ServerDatabase.CreateAsync(server);
        var user = new UserId(db.NewId());
        await CreateAccountAsync(db, user, "sub-d", Household);
        await db.Consents.RecordAsync(new ConsentRecord(user, CareDataConsent.Purpose, CareDataConsent.CurrentWording, T0, null, null), CancellationToken.None);
        await ApplyAsync(db, Diaper(db, db.NewId(), db.NextStamp()));
        await ApplyAsync(db, new HouseholdSettingsWrite(HouseholdSettings.Create(Household, VolumeUnit.Millilitres, "Europe/Amsterdam", db.NextStamp()).Value));

        // Act
        await using (var scope = db.NewScope())
            await scope.ServiceProvider.GetRequiredService<IAccountStore>().DeleteAccountAsync(
                user, new AccountDeletionPlan([Household], []), CancellationToken.None);

        // Assert
        (await db.ScalarAsync(
            "SELECT (SELECT count(*) FROM care_entries) + (SELECT count(*) FROM household_settings) + (SELECT count(*) FROM consents);"))
            .Should().Be(0L);
    }

    private static async Task CreateAccountAsync(ServerDatabase db, UserId user, string subject, HouseholdId household)
    {
        await using var scope = db.NewScope();
        await scope.ServiceProvider.GetRequiredService<IAccountStore>().CreateAccountAsync(
            new NewAccount(user, subject, subject + "@example.com", "Parent " + subject, household, T0), CancellationToken.None);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Coo.Api.Infrastructure.Tests --filter FullyQualifiedName~SyncStoreTests`
Expected: FAIL: `No service for type 'Coo.Api.Application.Sync.ISyncStore' has been registered.` Paste it.

- [ ] **Step 3: Share the care-entry upsert and the text forms**

Create `src/Backend/Coo.Api.Infrastructure/Persistence/CareEntryWriter.cs` and **move** `UpsertSql` and
`AddParameters` out of `CareEntryStore` into it, unchanged except that they become `internal`:

```csharp
using Coo.Domain.CareLog;
using Npgsql;
using NpgsqlTypes;

namespace Coo.Api.Infrastructure.Persistence;

/// <summary>
/// The one upsert of a care entry (Spec 008 §4), shared by the store and sync. An existing ID in another household is
/// refused, never moved (the WHERE). Immutable columns (kind, subject, who logged it, source) are never updated.
/// </summary>
internal static class CareEntryWriter
{
    public const string UpsertSql = /* the UpsertSql text from CareEntryStore, unchanged */ "";

    public static void AddParameters(NpgsqlCommand command, CareEntry entry, string details, long seq)
    {
        // the AddParameters body from CareEntryStore, unchanged
    }
}
```

In `CareEntryStore`, replace the uses with `CareEntryWriter.UpsertSql` and `CareEntryWriter.AddParameters(…)` and delete
the moved members. In `ServerColumns`, replace the bodies of `Person`, `ParsePerson`, `Source` and `ParseSource` with
calls to `SyncText` (keep the method names, so `ServerReadGuard` is untouched), and `ThisDevice => SyncText.ThisDevice`.

Run: `dotnet test tests/Coo.Api.Infrastructure.Tests --filter FullyQualifiedName~CareEntryStoreTests`
Expected: PASS (a pure move).

- [ ] **Step 4: The sync store**

`src/Backend/Coo.Api.Infrastructure/Persistence/SyncStore.cs`:

```csharp
using System.Data;
using Coo.Api.Application.Sync;
using Coo.Codecs;
using Coo.Contracts.Sync;
using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;
using Npgsql;
using NpgsqlTypes;

namespace Coo.Api.Infrastructure.Persistence;

/// <summary>
/// Sync in PostgreSQL (Spec 011 §7.2, ADR 0002 §5–§6). An apply is one transaction: the stored version
/// (<c>FOR UPDATE</c>), <see cref="LastWriterWinsRule"/>, the household's next <c>seq</c>, the row, the result in
/// <c>applied_changes</c>, and a <c>sync_conflicts</c> row when the write lost. A pull reads the four tables in one
/// snapshot. Table and key names come from a closed map, never from input.
/// </summary>
internal sealed class SyncStore(NpgsqlDataSource dataSource) : ISyncStore
{
    private sealed record Table(string Name, string Key);

    private static readonly Dictionary<string, Table> Tables = new(StringComparer.Ordinal)
    {
        [SyncEntityTypes.CareEntry] = new("care_entries", "id"),
        [SyncEntityTypes.BabyProfile] = new("baby_profiles", "id"),
        [SyncEntityTypes.HouseholdSettings] = new("household_settings", "household_id"),
        [SyncEntityTypes.DuplicateDismissal] = new("duplicate_dismissals", "id"),
    };

    public async Task<ChangeResult?> FindResultAsync(Guid changeId, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("SELECT result, reason FROM applied_changes WHERE change_id = @id;");
        command.Parameters.AddWithValue("id", changeId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ToResult(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1)) : null;
    }

    public async Task<ChangeResult> ApplyAsync(HouseholdId household, Guid changeId, SyncWrite write, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(write);
        var table = Tables[write.EntityType];
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // 1. The stored version, locked until this transaction ends.
        var stored = await ReadStoredAsync(connection, transaction, table, write.EntityId, cancellationToken);

        // 2. The rule, with Coo.Domain's HlcStamp.
        ChangeResult result;
        switch (LastWriterWinsRule.Decide(household, stored, write.Version))
        {
            case WriteDecision.HouseholdMismatch:
                result = ChangeResult.Rejected(RejectionReasons.HouseholdMismatch);
                break;
            case WriteDecision.Superseded:
                await RecordConflictAsync(connection, transaction, household, changeId, write, stored!.Version, now, cancellationToken);
                result = ChangeResult.Superseded;
                break;
            default:
                // A delete of a record the server never stored needs nothing: only its own phone ever had it (ruling 1).
                if (write is not TombstoneWrite || stored is not null)
                {
                    // 3. The household's next seq. Updating its counter row serialises writes within one household.
                    var seq = await NextSeqAsync(connection, transaction, household, cancellationToken);

                    // 4. The row.
                    await WriteAsync(connection, transaction, household, write, table, seq, cancellationToken);
                }

                result = ChangeResult.Applied;
                break;
        }

        // 5. The result, once per change ID. If a concurrent attempt at the same change committed first, this one rolls
        // back whole (its seq included) and reports that attempt's result.
        if (!await TryRecordAsync(connection, transaction, changeId, result, now, cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return await FindResultAsync(changeId, cancellationToken)
                ?? throw new InvalidOperationException($"Change {changeId} lost the race but has no stored result.");
        }

        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<ChangeResult> RecordRejectionAsync(Guid changeId, string reason, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var rejected = ChangeResult.Rejected(reason);
        return await TryRecordAsync(connection, null, changeId, rejected, now, cancellationToken)
            ? rejected
            : await FindResultAsync(changeId, cancellationToken) ?? rejected;
    }

    public async Task<PullPage> PullAsync(HouseholdId household, long cursor, long? bootstrapHead, int limit, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        // One snapshot for the head and the four tables. Commit order is seq order (the counter row lock), so a reader
        // never sees seq n+1 without seq n.
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        var head = bootstrapHead;
        if (cursor == 0 && head is null)
        {
            await using var read = new NpgsqlCommand("SELECT next_seq - 1 FROM households WHERE id = @h;", connection, transaction);
            read.Parameters.AddWithValue("h", household.Value);
            head = await read.ExecuteScalarAsync(cancellationToken) as long? ?? 0;
        }

        var rows = new List<PulledRecord>();
        rows.AddRange(await ReadCareEntriesAsync(connection, transaction, household, cursor, limit + 1, cancellationToken));
        rows.AddRange(await ReadBabiesAsync(connection, transaction, household, cursor, limit + 1, cancellationToken));
        rows.AddRange(await ReadSettingsAsync(connection, transaction, household, cursor, limit + 1, cancellationToken));
        rows.AddRange(await ReadDismissalsAsync(connection, transaction, household, cursor, limit + 1, cancellationToken));
        await transaction.CommitAsync(cancellationToken);

        // Each table gave at most limit + 1 rows above the cursor, so the first limit + 1 of the union are exact.
        var ordered = rows.OrderBy(r => r.Seq).ToList();
        var page = ordered.Take(limit).ToList();
        var next = page.Count == 0 ? cursor : page[^1].Seq;
        var visible = page.Where(r => !(r.IsDeleted && head is { } h && r.Seq <= h)).ToList();
        return new PullPage(visible, next, ordered.Count > limit, head);
    }

    public async Task<int> PurgeAsync(DateTimeOffset before, CancellationToken cancellationToken)
    {
        var deleted = 0;
        foreach (var sql in new[]
        {
            "DELETE FROM applied_changes WHERE applied_at < @before;",
            "DELETE FROM sync_conflicts WHERE recorded_at < @before;",
        })
        {
            await using var command = dataSource.CreateCommand(sql);
            command.Parameters.AddWithValue("before", before.UtcDateTime);
            deleted += await command.ExecuteNonQueryAsync(cancellationToken);
        }

        return deleted;
    }

    private static async Task<StoredVersion?> ReadStoredAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Table table, Guid id, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"SELECT household_id, version_wall_ms, version_counter, version_device FROM {table.Name} WHERE {table.Key} = @id FOR UPDATE;",
            connection, transaction);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;
        return new StoredVersion(new HouseholdId(reader.GetGuid(0)), new HlcStamp(reader.GetInt64(1), reader.GetInt32(2), new DeviceId(reader.GetGuid(3))));
    }

    private static async Task<long> NextSeqAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, HouseholdId household, CancellationToken cancellationToken)
    {
        await using var next = new NpgsqlCommand(
            "UPDATE households SET next_seq = next_seq + 1 WHERE id = @household RETURNING next_seq - 1;", connection, transaction);
        next.Parameters.AddWithValue("household", household.Value);
        return await next.ExecuteScalarAsync(cancellationToken) is long value
            ? value
            : throw new Coo.Api.Application.CareLog.HouseholdNotFoundException($"Household {household.Value} does not exist on the server.");
    }

    private static async Task WriteAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, HouseholdId household, SyncWrite write, Table table, long seq,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand { Connection = connection, Transaction = transaction };
        switch (write)
        {
            case CareEntryWrite care:
                command.CommandText = CareEntryWriter.UpsertSql;
                CareEntryWriter.AddParameters(command, care.Entry, EntryDetailsCodecs.For(care.Entry.Details).Encode(care.Entry.Details, care.ReceivedDetails), seq);
                break;
            case BabyProfileWrite baby:
                command.CommandText =
                    """
                    INSERT INTO baby_profiles (id, household_id, name, nicknames, birth_date, due_date, version_wall_ms, version_counter, version_device, is_deleted, seq)
                    VALUES (@id, @household, @name, @nicknames, @birth, @due, @wall, @counter, @device, false, @seq)
                    ON CONFLICT (id) DO UPDATE SET name = excluded.name, nicknames = excluded.nicknames, birth_date = excluded.birth_date,
                        due_date = excluded.due_date, version_wall_ms = excluded.version_wall_ms, version_counter = excluded.version_counter,
                        version_device = excluded.version_device, is_deleted = false, seq = excluded.seq
                    WHERE baby_profiles.household_id = excluded.household_id;
                    """;
                command.Parameters.AddWithValue("id", baby.Baby.Id.Value);
                command.Parameters.AddWithValue("name", baby.Baby.Name);
                command.Parameters.AddWithValue("nicknames", baby.Baby.Nicknames.ToArray());
                command.Parameters.AddWithValue("birth", NpgsqlDbType.Date, (object?)baby.Baby.BirthDate ?? DBNull.Value);
                command.Parameters.AddWithValue("due", NpgsqlDbType.Date, (object?)baby.Baby.DueDate ?? DBNull.Value);
                break;
            case HouseholdSettingsWrite settings:
                command.CommandText =
                    """
                    INSERT INTO household_settings (household_id, volume_unit, time_zone_id, hidden_kinds, version_wall_ms, version_counter, version_device, is_deleted, seq)
                    VALUES (@household, @unit, @zone, @hidden, @wall, @counter, @device, false, @seq)
                    ON CONFLICT (household_id) DO UPDATE SET volume_unit = excluded.volume_unit, time_zone_id = excluded.time_zone_id,
                        hidden_kinds = excluded.hidden_kinds, version_wall_ms = excluded.version_wall_ms, version_counter = excluded.version_counter,
                        version_device = excluded.version_device, is_deleted = false, seq = excluded.seq;
                    """;
                command.Parameters.AddWithValue("unit", settings.Settings.VolumeUnit == Coo.Domain.Households.VolumeUnit.Ounces ? "oz" : "ml");
                command.Parameters.AddWithValue("zone", settings.Settings.TimeZoneId);
                command.Parameters.AddWithValue("hidden", settings.Settings.HiddenKinds.Select(k => k.Value).Order(StringComparer.Ordinal).ToArray());
                break;
            case DismissalWrite dismissal:
                command.CommandText =
                    """
                    INSERT INTO duplicate_dismissals (id, household_id, entry_a, entry_b, version_wall_ms, version_counter, version_device, is_deleted, seq)
                    VALUES (@id, @household, @a, @b, @wall, @counter, @device, false, @seq)
                    ON CONFLICT (id) DO UPDATE SET version_wall_ms = excluded.version_wall_ms, version_counter = excluded.version_counter,
                        version_device = excluded.version_device, is_deleted = false, seq = excluded.seq
                    WHERE duplicate_dismissals.household_id = excluded.household_id;
                    """;
                command.Parameters.AddWithValue("id", dismissal.Dismissal.Id.Value);
                command.Parameters.AddWithValue("a", dismissal.Dismissal.Pair.First.Value);
                command.Parameters.AddWithValue("b", dismissal.Dismissal.Pair.Second.Value);
                break;
            case TombstoneWrite:
                command.CommandText =
                    $"UPDATE {table.Name} SET is_deleted = true, version_wall_ms = @wall, version_counter = @counter, version_device = @device, seq = @seq "
                    + $"WHERE {table.Key} = @id AND household_id = @household;";
                command.Parameters.AddWithValue("id", write.EntityId);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(write), write.GetType().Name, "Unknown write.");
        }

        if (write is not CareEntryWrite)
        {
            command.Parameters.AddWithValue("household", household.Value);
            command.Parameters.AddWithValue("wall", write.Version.WallClockMs);
            command.Parameters.AddWithValue("counter", write.Version.Counter);
            command.Parameters.AddWithValue("device", write.Version.DeviceId.Value);
            command.Parameters.AddWithValue("seq", seq);
        }

        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException($"{write.EntityType} {write.EntityId} was not written (another household's ID?).");
    }

    private static async Task RecordConflictAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, HouseholdId household, Guid changeId, SyncWrite loser, HlcStamp winner,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO sync_conflicts (household_id, change_id, entity_type, entity_id, winner_wall_ms, winner_counter, winner_device,
                loser_wall_ms, loser_counter, loser_device, recorded_at)
            VALUES (@household, @change, @type, @entity, @ww, @wc, @wd, @lw, @lc, @ld, @at);
            """, connection, transaction);
        var p = command.Parameters;
        p.AddWithValue("household", household.Value);
        p.AddWithValue("change", changeId);
        p.AddWithValue("type", loser.EntityType);
        p.AddWithValue("entity", loser.EntityId);
        p.AddWithValue("ww", winner.WallClockMs);
        p.AddWithValue("wc", winner.Counter);
        p.AddWithValue("wd", winner.DeviceId.Value);
        p.AddWithValue("lw", loser.Version.WallClockMs);
        p.AddWithValue("lc", loser.Version.Counter);
        p.AddWithValue("ld", loser.Version.DeviceId.Value);
        p.AddWithValue("at", now.UtcDateTime);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>One statement: the primary key decides which attempt's result is the change's.</summary>
    private static async Task<bool> TryRecordAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid changeId, ChangeResult result, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "INSERT INTO applied_changes (change_id, result, reason, applied_at) VALUES (@id, @result, @reason, @at) ON CONFLICT (change_id) DO NOTHING;",
            connection, transaction);
        command.Parameters.AddWithValue("id", changeId);
        command.Parameters.AddWithValue("result", ResultText(result.Outcome));
        command.Parameters.AddWithValue("reason", NpgsqlDbType.Text, (object?)result.Reason ?? DBNull.Value);
        command.Parameters.AddWithValue("at", now.UtcDateTime);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private static string ResultText(ChangeOutcome outcome) => outcome switch
    {
        ChangeOutcome.Applied => ChangeResults.Applied,
        ChangeOutcome.Superseded => ChangeResults.Superseded,
        _ => ChangeResults.Rejected,
    };

    private static ChangeResult ToResult(string result, string? reason) => result switch
    {
        ChangeResults.Applied => ChangeResult.Applied,
        ChangeResults.Superseded => ChangeResult.Superseded,
        _ => ChangeResult.Rejected(reason ?? RejectionReasons.ChangeMalformed),
    };

    private static HlcStamp Stamp(NpgsqlDataReader r, int at) => new(r.GetInt64(at), r.GetInt32(at + 1), new DeviceId(r.GetGuid(at + 2)));

    private static async Task<List<PulledRecord>> ReadCareEntriesAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, HouseholdId household, long cursor, int limit, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT id, kind, subject_baby_id, subject_person, is_session, start_at, start_offset_min, end_at, end_offset_min, logged_by,
                source, source_prompt_version, note, details::text, version_wall_ms, version_counter, version_device, is_deleted, seq
            FROM care_entries WHERE household_id = @h AND seq > @cursor ORDER BY seq LIMIT @limit;
            """, connection, transaction);
        AddPage(command, household, cursor, limit);
        var list = new List<PulledRecord>();
        await using var r = await command.ExecuteReaderAsync(cancellationToken);
        while (await r.ReadAsync(cancellationToken))
        {
            var start = ServerColumns.ParseInstant(r.GetDateTime(5), r.GetInt16(6)) ?? ServerColumns.Utc(r.GetDateTime(5));
            DateTimeOffset? end = r.IsDBNull(7) ? null : ServerColumns.ParseInstant(r.GetDateTime(7), r.GetInt16(8)) ?? ServerColumns.Utc(r.GetDateTime(7));
            var payload = new CareEntryPayload(
                r.GetString(1), r.IsDBNull(2) ? null : r.GetGuid(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetBoolean(4), start, end,
                r.GetString(9), r.GetString(10), r.IsDBNull(11) ? null : r.GetString(11), r.IsDBNull(12) ? null : r.GetString(12), r.GetString(13));
            list.Add(new PulledRecord(SyncEntityTypes.CareEntry, r.GetGuid(0), Stamp(r, 14), r.GetBoolean(17), r.GetInt64(18), payload));
        }

        return list;
    }

    private static async Task<List<PulledRecord>> ReadBabiesAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, HouseholdId household, long cursor, int limit, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT id, name, nicknames, birth_date, due_date, version_wall_ms, version_counter, version_device, is_deleted, seq
            FROM baby_profiles WHERE household_id = @h AND seq > @cursor ORDER BY seq LIMIT @limit;
            """, connection, transaction);
        AddPage(command, household, cursor, limit);
        var list = new List<PulledRecord>();
        await using var r = await command.ExecuteReaderAsync(cancellationToken);
        while (await r.ReadAsync(cancellationToken))
        {
            var payload = new BabyProfilePayload(
                r.GetString(1), r.GetFieldValue<string[]>(2), r.IsDBNull(3) ? null : r.GetFieldValue<DateOnly>(3), r.IsDBNull(4) ? null : r.GetFieldValue<DateOnly>(4));
            list.Add(new PulledRecord(SyncEntityTypes.BabyProfile, r.GetGuid(0), Stamp(r, 5), r.GetBoolean(8), r.GetInt64(9), payload));
        }

        return list;
    }

    private static async Task<List<PulledRecord>> ReadSettingsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, HouseholdId household, long cursor, int limit, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT household_id, volume_unit, time_zone_id, hidden_kinds, version_wall_ms, version_counter, version_device, is_deleted, seq
            FROM household_settings WHERE household_id = @h AND seq > @cursor ORDER BY seq LIMIT @limit;
            """, connection, transaction);
        AddPage(command, household, cursor, limit);
        var list = new List<PulledRecord>();
        await using var r = await command.ExecuteReaderAsync(cancellationToken);
        while (await r.ReadAsync(cancellationToken))
        {
            var payload = new HouseholdSettingsPayload(r.GetString(1), r.GetString(2), r.GetFieldValue<string[]>(3));
            list.Add(new PulledRecord(SyncEntityTypes.HouseholdSettings, r.GetGuid(0), Stamp(r, 4), r.GetBoolean(7), r.GetInt64(8), payload));
        }

        return list;
    }

    private static async Task<List<PulledRecord>> ReadDismissalsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, HouseholdId household, long cursor, int limit, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT id, entry_a, entry_b, version_wall_ms, version_counter, version_device, is_deleted, seq
            FROM duplicate_dismissals WHERE household_id = @h AND seq > @cursor ORDER BY seq LIMIT @limit;
            """, connection, transaction);
        AddPage(command, household, cursor, limit);
        var list = new List<PulledRecord>();
        await using var r = await command.ExecuteReaderAsync(cancellationToken);
        while (await r.ReadAsync(cancellationToken))
            list.Add(new PulledRecord(SyncEntityTypes.DuplicateDismissal, r.GetGuid(0), Stamp(r, 3), r.GetBoolean(6), r.GetInt64(7), new DismissalPayload(r.GetGuid(1), r.GetGuid(2))));

        return list;
    }

    private static void AddPage(NpgsqlCommand command, HouseholdId household, long cursor, int limit)
    {
        command.Parameters.AddWithValue("h", household.Value);
        command.Parameters.AddWithValue("cursor", cursor);
        command.Parameters.AddWithValue("limit", limit);
    }
}
```

(The `db` parameter of the test's `Diaper` helper is unused by design: it keeps every helper call shaped alike. If the
analyzer flags it, drop it.)

- [ ] **Step 5: Membership and consent stores, and the registrations**

`src/Backend/Coo.Api.Infrastructure/Persistence/MembershipStore.cs`:

```csharp
using Coo.Api.Application.Sync;
using Coo.Domain.Common;
using Coo.Domain.Households;
using Npgsql;

namespace Coo.Api.Infrastructure.Persistence;

/// <summary>The per-request membership check (Spec 011 §7.1): one indexed lookup by the primary key.</summary>
internal sealed class MembershipStore(NpgsqlDataSource dataSource) : IMembershipStore
{
    public async Task<MembershipLookup> FindAsync(HouseholdId household, UserId user, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("SELECT role, joined_at, revoked_at FROM memberships WHERE household_id = @h AND user_id = @u;");
        command.Parameters.AddWithValue("h", household.Value);
        command.Parameters.AddWithValue("u", user.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return MembershipLookup.None;
        var membership = Membership.Create(household, user, RoleText.Parse(reader.GetString(0)), ServerColumns.Utc(reader.GetDateTime(1)));
        return new MembershipLookup(membership, IsRevoked: !reader.IsDBNull(2));
    }
}
```

`src/Backend/Coo.Api.Infrastructure/Persistence/ConsentStore.cs`:

```csharp
using Coo.Api.Application.Sync;
using Coo.Domain.Common;
using Npgsql;
using NpgsqlTypes;

namespace Coo.Api.Infrastructure.Persistence;

/// <summary><c>consents</c> (Spec 011 §3). One statement; the primary key keeps the first consent to a wording.</summary>
internal sealed class ConsentStore(NpgsqlDataSource dataSource) : IConsentStore
{
    public async Task RecordAsync(ConsentRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        await using var command = dataSource.CreateCommand(
            "INSERT INTO consents (user_id, purpose, wording, given_at, app_version, language) VALUES (@u, @p, @w, @at, @v, @l) "
            + "ON CONFLICT (user_id, purpose, wording) DO NOTHING;");
        command.Parameters.AddWithValue("u", record.User.Value);
        command.Parameters.AddWithValue("p", record.Purpose);
        command.Parameters.AddWithValue("w", record.Wording);
        command.Parameters.AddWithValue("at", record.GivenAt.UtcDateTime);
        command.Parameters.AddWithValue("v", NpgsqlDbType.Varchar, (object?)record.AppVersion ?? DBNull.Value);
        command.Parameters.AddWithValue("l", NpgsqlDbType.Varchar, (object?)record.Language ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<ConsentRecord?> FindAsync(UserId user, string purpose, string wording, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT given_at, app_version, language FROM consents WHERE user_id = @u AND purpose = @p AND wording = @w;");
        command.Parameters.AddWithValue("u", user.Value);
        command.Parameters.AddWithValue("p", purpose);
        command.Parameters.AddWithValue("w", wording);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;
        return new ConsentRecord(user, purpose, wording, ServerColumns.Utc(reader.GetDateTime(0)),
            reader.IsDBNull(1) ? null : reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2));
    }
}
```

In `ServiceCollectionExtensions.AddApiInfrastructure`, after `IDeviceSessionStore`:

```csharp
        // Sync (Spec 011 §7): singletons over the data source, like the other raw-SQL stores.
        services.AddSingleton<ISyncStore, SyncStore>();
        services.AddSingleton<IMembershipStore, MembershipStore>();
        services.AddSingleton<IConsentStore, ConsentStore>();
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test tests/Coo.Api.Infrastructure.Tests`
Expected: PASS. If `ApplyAsync_SameChangeTwiceAtOnce_StoresOneResultAndBothSeeIt` ever reports a unique violation on
`ix_care_entries_household_seq`, the second attempt took a seq before the first committed: check that `NextSeqAsync`
runs inside the same transaction as the row lock (it must; the counter row lock then orders them).

- [ ] **Step 7: Commit**

```bash
dotnet format Coo.Tests.slnf
git add src/Backend/Coo.Api.Infrastructure tests/Coo.Api.Infrastructure.Tests
git commit -m "Server sync store: last-writer-wins in one transaction, seq across four tables, the bootstrap pull, membership and consent stores (Spec 011 §7.2)"
```

---
### Task 6: The endpoints, the per-request checks, `/api/me` consent, the daily purge, and API 0.11.0

**Files:**
- Create: `src/Backend/Coo.Api/Hosting/SyncMapping.cs`, `src/Backend/Coo.Api/Hosting/SyncEndpoints.cs`,
  `src/Backend/Coo.Api/Hosting/SyncHistoryCleanupJob.cs`
- Modify: `src/Backend/Coo.Api/Hosting/AuthEndpoints.cs` (`UserOf`, `TryReadJsonAsync` and `Refused` become `internal`;
  `/api/me` returns the consent), `src/Backend/Coo.Api/Program.cs`, `src/Backend/Coo.Api/Coo.Api.csproj` (0.11.0)
- Modify: `src/Backend/Coo.Api.Application/Auth/AccountUseCases.cs` (`GetMe` reports the consent)
- Test: `tests/Coo.Api.IntegrationTests/SyncApiTests.cs`, `tests/Coo.Api.IntegrationTests/SyncHistoryCleanupJobTests.cs`;
  modify `tests/Coo.Api.Application.Tests/Fakes.cs` and `AccountTests.cs`

**Interfaces:**
- Consumes: Tasks 1–5.
- Produces (the wire the phone uses in Task 15):
  - `POST /api/sync/push` body `PushRequest` → `200 PushResponse`; `400 {code: sync.malformed | sync.batch-too-large}`;
    `426 {code: client-too-old}`; `403 {code: sync.not-a-member | sync.consent-required}`; `403 application/problem+json
    {type: SyncErrors.MembershipRevokedType, status: 403, householdId}`.
  - `GET /api/sync/pull?household=<guid>&cursor=<long>&bootstrapHead=<long, optional>&schemaVersion=<int>` → `200 PullResponse`
    (same refusals).
  - `GET /api/households/{id}/membership` → `200 {status: active|revoked}` or `404`.
  - `POST /api/consents` body `ConsentRequest` → `204`, or `400 {code: consent.unknown-wording}`.
  - `GET /api/me` → `MeResponse` with `consent: {wording, givenAt} | null`.
  - `AccountOverview(UserAccount User, Membership? Membership, IReadOnlyList<DeviceSession> Devices, ConsentRecord? Consent = null)`;
    `GetMe(IAccountStore accounts, IDeviceSessionStore sessions, IConsentStore consents)`.

- [ ] **Step 1: Write the failing tests**

`tests/Coo.Api.IntegrationTests/SyncApiTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Coo.Contracts.Auth;
using Coo.Contracts.Sync;
using Npgsql;

namespace Coo.Api.IntegrationTests;

internal static class SyncCalls
{
    public static readonly Uri Push = new("/api/sync/push", UriKind.Relative);
    public static readonly Uri Consents = new("/api/consents", UriKind.Relative);

    /// <summary>A wall clock safely in the past of the API's real clock: stamps are never "in the future".</summary>
    public const long Wall = 1_790_000_000_000;

    public static readonly DateTimeOffset At = new(2026, 9, 20, 9, 0, 0, TimeSpan.FromHours(2));

    public static Uri Pull(Guid household, long cursor, long? head = null, int schema = SchemaVersions.Current) => new(
        FormattableString.Invariant($"/api/sync/pull?household={household}&cursor={cursor}{(head is { } h ? $"&bootstrapHead={h}" : "")}&schemaVersion={schema}"),
        UriKind.Relative);

    public static Uri Membership(Guid household) => new($"/api/households/{household}/membership", UriKind.Relative);

    public static async Task<SessionResponse> SignInWithConsentAsync(HttpClient client, string subject, Guid device, Guid household)
    {
        var session = await Calls.SignInAsync(client, subject, device, household);
        var consent = await Calls.SendAsync(client, HttpMethod.Post, Consents, session.AccessToken,
            new ConsentRequest(CareDataConsent.Purpose, CareDataConsent.CurrentWording, "0.11.0", "en"));
        consent.StatusCode.Should().Be(HttpStatusCode.NoContent);
        return session;
    }

    public static SyncChangeDto Diaper(Guid change, Guid entry, long wall, Guid device, string? note = null) => new(
        change, SyncEntityTypes.CareEntry, entry, SyncOperations.Upsert, new HlcStampDto(wall, 0, device),
        CareEntry: new CareEntryDto("diaper", Calls.Id(5000), null, false, At, null, "user:7a1c0000-0000-7000-8000-000000000001",
            "manual", null, note, JsonDocument.Parse("""{"v":1,"contents":"wet"}""").RootElement.Clone()));

    public static Task<HttpResponseMessage> PushAsync(HttpClient client, string token, Guid household, Guid device, params SyncChangeDto[] changes) =>
        Calls.SendAsync(client, HttpMethod.Post, Push, token, new PushRequest(household, device, SchemaVersions.Current, changes));
}

/// <summary>Spec 011 §7.1 and §10 (Api integration row), end to end with a fake Google.</summary>
public sealed class SyncApiTests(AuthApi api) : IClassFixture<AuthApi>
{
    [Fact]
    public async Task Push_ThenPull_RoundTripsTheEntryWithItsDetails()
    {
        // Arrange
        using var client = api.Factory.CreateClient();
        var (device, household) = (Calls.Id(6001), Calls.Id(6002));
        var session = await SyncCalls.SignInWithConsentAsync(client, "sync-roundtrip", device, household);

        // Act
        var pushed = await SyncCalls.PushAsync(client, session.AccessToken, household, device,
            SyncCalls.Diaper(Calls.Id(6003), Calls.Id(6004), SyncCalls.Wall, device, "first"));
        var pulled = await (await Calls.SendAsync(client, HttpMethod.Get, SyncCalls.Pull(household, 0), session.AccessToken))
            .Content.ReadFromJsonAsync<PullResponse>();

        // Assert
        (await pushed.Content.ReadFromJsonAsync<PushResponse>())!.Results.Single().Result.Should().Be(ChangeResults.Applied);
        var record = pulled!.Records.Single();
        record.EntityId.Should().Be(Calls.Id(6004));
        record.CareEntry!.Note.Should().Be("first");
        record.CareEntry.Details.GetProperty("contents").GetString().Should().Be("wet");
        pulled.Cursor.Should().Be(1);
        pulled.HasMore.Should().BeFalse();
    }

    [Fact]
    public async Task Push_SameBatchTwice_ReturnsTheStoredResults()
    {
        // Arrange
        using var client = api.Factory.CreateClient();
        var (device, household) = (Calls.Id(6011), Calls.Id(6012));
        var session = await SyncCalls.SignInWithConsentAsync(client, "sync-retry", device, household);
        var change = SyncCalls.Diaper(Calls.Id(6013), Calls.Id(6014), SyncCalls.Wall, device);
        await SyncCalls.PushAsync(client, session.AccessToken, household, device, change);

        // Act
        var again = await SyncCalls.PushAsync(client, session.AccessToken, household, device, change);

        // Assert
        (await again.Content.ReadFromJsonAsync<PushResponse>())!.Results.Single().Result.Should().Be(ChangeResults.Applied);
        var page = await (await Calls.SendAsync(client, HttpMethod.Get, SyncCalls.Pull(household, 0), session.AccessToken)).Content.ReadFromJsonAsync<PullResponse>();
        page!.Cursor.Should().Be(1, "the retry was not applied a second time");
    }

    [Fact]
    public async Task Push_WithoutConsent_IsRefusedAsConsentRequired()
    {
        // Arrange
        using var client = api.Factory.CreateClient();
        var (device, household) = (Calls.Id(6021), Calls.Id(6022));
        var session = await Calls.SignInAsync(client, "sync-no-consent", device, household);

        // Act
        var response = await SyncCalls.PushAsync(client, session.AccessToken, household, device);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Calls.CodeAsync(response)).Should().Be(SyncErrors.ConsentRequired);
    }

    [Fact]
    public async Task Push_ToAnotherAccountsHousehold_IsRefusedAsNotAMember()
    {
        // Arrange
        using var client = api.Factory.CreateClient();
        await SyncCalls.SignInWithConsentAsync(client, "sync-owner", Calls.Id(6031), Calls.Id(6032));
        var stranger = await SyncCalls.SignInWithConsentAsync(client, "sync-stranger", Calls.Id(6033), Calls.Id(6034));

        // Act
        var response = await SyncCalls.PushAsync(client, stranger.AccessToken, Calls.Id(6032), Calls.Id(6033));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Calls.CodeAsync(response)).Should().Be(SyncErrors.NotAMember);
    }

    [Fact]
    public async Task Pull_AfterTheMembershipWasRevoked_IsTheProblemForThatHousehold()
    {
        // Arrange: the access token stays valid; the membership is checked on every request (Spec 011 §7.1).
        using var client = api.Factory.CreateClient();
        var (device, household) = (Calls.Id(6041), Calls.Id(6042));
        var session = await SyncCalls.SignInWithConsentAsync(client, "sync-revoked", device, household);
        (await Calls.SendAsync(client, HttpMethod.Get, SyncCalls.Pull(household, 0), session.AccessToken)).StatusCode.Should().Be(HttpStatusCode.OK);
        await RevokeAsync(household);

        // Act
        var response = await Calls.SendAsync(client, HttpMethod.Get, SyncCalls.Pull(household, 0), session.AccessToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        var problem = await response.Content.ReadFromJsonAsync<ProblemResponse>();
        problem!.Type.Should().Be(SyncErrors.MembershipRevokedType);
        problem.HouseholdId.Should().Be(household);
    }

    [Fact]
    public async Task Membership_ActiveRevokedNever_AnswersEachAsTheAdrSays()
    {
        // Arrange
        using var client = api.Factory.CreateClient();
        var (device, household) = (Calls.Id(6051), Calls.Id(6052));
        var session = await Calls.SignInAsync(client, "sync-membership", device, household);

        // Act
        var active = await (await Calls.SendAsync(client, HttpMethod.Get, SyncCalls.Membership(household), session.AccessToken))
            .Content.ReadFromJsonAsync<MembershipStatusResponse>();
        var never = await Calls.SendAsync(client, HttpMethod.Get, SyncCalls.Membership(Calls.Id(6059)), session.AccessToken);
        await RevokeAsync(household);
        var revoked = await (await Calls.SendAsync(client, HttpMethod.Get, SyncCalls.Membership(household), session.AccessToken))
            .Content.ReadFromJsonAsync<MembershipStatusResponse>();

        // Assert
        active!.Status.Should().Be(MembershipStatuses.Active);
        never.StatusCode.Should().Be(HttpStatusCode.NotFound);
        revoked!.Status.Should().Be(MembershipStatuses.Revoked);
    }

    [Fact]
    public async Task Push_SchemaOlderThanSupported_Is426ClientTooOld()
    {
        // Arrange
        using var client = api.Factory.CreateClient();
        var (device, household) = (Calls.Id(6061), Calls.Id(6062));
        var session = await SyncCalls.SignInWithConsentAsync(client, "sync-old", device, household);

        // Act
        var response = await Calls.SendAsync(client, HttpMethod.Post, SyncCalls.Push, session.AccessToken,
            new PushRequest(household, device, SchemaVersions.Oldest - 1, []));

        // Assert
        response.StatusCode.Should().Be((HttpStatusCode)426);
        (await Calls.CodeAsync(response)).Should().Be(SyncErrors.ClientTooOld);
    }

    [Fact]
    public async Task Push_201Changes_IsBatchTooLarge()
    {
        // Arrange
        using var client = api.Factory.CreateClient();
        var (device, household) = (Calls.Id(6071), Calls.Id(6072));
        var session = await SyncCalls.SignInWithConsentAsync(client, "sync-large", device, household);
        var changes = Enumerable.Range(0, SyncLimits.MaxPushChanges + 1)
            .Select(i => SyncCalls.Diaper(Calls.Id(100_000 + i), Calls.Id(200_000 + i), SyncCalls.Wall + i, device)).ToArray();

        // Act
        var response = await SyncCalls.PushAsync(client, session.AccessToken, household, device, changes);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Calls.CodeAsync(response)).Should().Be(SyncErrors.BatchTooLarge);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"householdId":"00000000-0000-0000-0000-000000000000","deviceId":"7a1c0000-0000-7000-8000-000000000001","schemaVersion":1,"changes":[]}""")]
    public async Task Push_UnreadableRequest_IsMalformed(string body)
    {
        // Arrange
        using var client = api.Factory.CreateClient();
        var session = await SyncCalls.SignInWithConsentAsync(client, "sync-malformed-" + body.Length, Calls.Id(6081 + body.Length), Calls.Id(7081 + body.Length));
        using var request = new HttpRequestMessage(HttpMethod.Post, SyncCalls.Push) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new("Bearer", session.AccessToken);

        // Act
        var response = await client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Calls.CodeAsync(response)).Should().Be(SyncErrors.Malformed);
    }

    [Fact]
    public async Task Pull_WithoutAToken_Is401()
    {
        // Arrange
        using var client = api.Factory.CreateClient();

        // Act
        var response = await client.GetAsync(SyncCalls.Pull(Calls.Id(6091), 0));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Me_AfterConsent_ReportsTheCurrentWording()
    {
        // Arrange
        using var client = api.Factory.CreateClient();
        var session = await SyncCalls.SignInWithConsentAsync(client, "sync-me", Calls.Id(6101), Calls.Id(6102));

        // Act
        var me = await (await Calls.SendAsync(client, HttpMethod.Get, Calls.Me, session.AccessToken)).Content.ReadFromJsonAsync<MeResponse>();

        // Assert
        me!.Consent!.Wording.Should().Be(CareDataConsent.CurrentWording);
    }

    [Fact]
    public async Task Consents_UnknownWording_IsRefused()
    {
        // Arrange
        using var client = api.Factory.CreateClient();
        var session = await Calls.SignInAsync(client, "sync-wording", Calls.Id(6111), Calls.Id(6112));

        // Act
        var response = await Calls.SendAsync(client, HttpMethod.Post, SyncCalls.Consents, session.AccessToken,
            new ConsentRequest(CareDataConsent.Purpose, "care-data-v0"));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Calls.CodeAsync(response)).Should().Be(SyncErrors.ConsentUnknownWording);
    }

    private async Task RevokeAsync(Guid household)
    {
        await using var connection = new NpgsqlConnection(api.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("UPDATE memberships SET revoked_at = now() WHERE household_id = @h;", connection);
        command.Parameters.AddWithValue("h", household);
        await command.ExecuteNonQueryAsync();
    }
}
```

`tests/Coo.Api.IntegrationTests/SyncHistoryCleanupJobTests.cs`: copy `AuthNonceCleanupJobTests` (all three tests and its
`AdvanceUntilAsync` helper) with `ISyncStore _store = Substitute.For<ISyncStore>()`, `_store.PurgeAsync(…)` in place of
`DeleteIssuedBeforeAsync`, `new SyncHistoryCleanupJob(new PurgeSyncHistory(_store, _time), _time, NullLogger<SyncHistoryCleanupJob>.Instance)`,
a first advance step of `TimeSpan.FromMinutes(20)`, and test names `ExecuteAsync_PurgeCancelledWhileTheApiRuns_KeepsRunningAndPurgesTheNextDay`,
`ExecuteAsync_PurgeThrows_KeepsRunningAndPurgesTheNextDay`, `StopAsync_WhileWaiting_EndsWithoutAFault`. Add a fourth:

```csharp
    [Fact]
    public async Task ExecuteAsync_FirstRun_PurgesWhatIsOlderThanThirtyDays()
    {
        // Arrange
        var purged = new TaskCompletionSource<DateTimeOffset>(TaskCreationOptions.RunContinuationsAsynchronously);
        _store.PurgeAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            purged.TrySetResult(call.Arg<DateTimeOffset>());
            return Task.FromResult(0);
        });
        using var job = new SyncHistoryCleanupJob(new PurgeSyncHistory(_store, _time), _time, NullLogger<SyncHistoryCleanupJob>.Instance);
        await job.StartAsync(CancellationToken.None);

        // Act
        await AdvanceUntilAsync(job, purged.Task, TimeSpan.FromMinutes(20));

        // Assert
        (await purged.Task).Should().Be(_time.GetUtcNow() - TimeSpan.FromDays(30));
        await job.StopAsync(CancellationToken.None);
    }
```

In `tests/Coo.Api.Application.Tests/Fakes.cs`, `AuthKit` gains `public FakeConsents Consents { get; } = new();` and
`public GetMe GetMe() => new(Accounts, Sessions, Consents);`. Add to `AccountTests.cs` (with
`using Coo.Api.Application.Sync; using Coo.Contracts.Sync;`):

```csharp
    [Fact]
    public async Task ExecuteAsync_UserWithCurrentConsent_ReportsIt()
    {
        // Arrange
        var kit = new AuthKit();
        var signedIn = await kit.SignInAsync("sub-1", new DeviceId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000d1")),
            new HouseholdId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a1")));
        await kit.Consents.RecordAsync(new ConsentRecord(signedIn.User.Id, CareDataConsent.Purpose, CareDataConsent.CurrentWording,
            AuthKit.Start, "0.11.0", "en"), CancellationToken.None);

        // Act
        var me = await kit.GetMe().ExecuteAsync(signedIn.User.Id, CancellationToken.None);

        // Assert
        me!.Consent!.Wording.Should().Be(CareDataConsent.CurrentWording);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Coo.Api.IntegrationTests --filter "FullyQualifiedName~SyncApiTests|FullyQualifiedName~SyncHistoryCleanupJobTests"`
Expected: FAIL to compile (`SyncHistoryCleanupJob` not found), then, once it exists, `404` from `/api/sync/push`.
Paste the first error.

- [ ] **Step 3: `GetMe` and `/api/me`**

`src/Backend/Coo.Api.Application/Auth/AccountUseCases.cs`:

```csharp
using Coo.Api.Application.Sync;
using Coo.Contracts.Sync;
// …
public sealed record AccountOverview(UserAccount User, Membership? Membership, IReadOnlyList<DeviceSession> Devices, ConsentRecord? Consent = null);

/// <summary>
/// <c>GET /api/me</c>: the user, their household and role, their active device sessions, and their consent to the
/// current wording (Spec 011 §3), so the account's second phone does not ask again.
/// </summary>
public sealed class GetMe(IAccountStore accounts, IDeviceSessionStore sessions, IConsentStore consents)
{
    public async Task<AccountOverview?> ExecuteAsync(UserId user, CancellationToken cancellationToken)
    {
        var account = await accounts.FindAsync(user, cancellationToken);
        if (account is null)
            return null;
        var membership = await accounts.FindEarliestMembershipAsync(user, cancellationToken);
        var devices = (await sessions.ListForUserAsync(user, cancellationToken))
            .Where(s => !s.IsRevoked)
            .OrderByDescending(s => s.LastUsedAt)
            .ToList();
        var consent = await consents.FindAsync(user, CareDataConsent.Purpose, CareDataConsent.CurrentWording, cancellationToken);
        return new AccountOverview(account, membership, devices, consent);
    }
}
```

In `AuthEndpoints.cs`: make `Refused`, `TryReadJsonAsync` and `UserOf` `internal static`, and return the consent from
`/api/me`:

```csharp
            return Results.Ok(new MeResponse(
                User(me.User),
                me.Membership is null ? null : new MembershipDto(me.Membership.HouseholdId.Value, RoleName(me.Membership.Role)),
                [.. me.Devices.Select(d => new DeviceSessionDto(d.Id.Value, d.DeviceName, d.LastUsedAt, d.Id == current))],
                me.Consent is null ? null : new ConsentDto(me.Consent.Wording, me.Consent.GivenAt)));
```

- [ ] **Step 4: The mapping**

`src/Backend/Coo.Api/Hosting/SyncMapping.cs`:

```csharp
using System.Text.Json;
using Coo.Api.Application.Sync;
using Coo.Contracts.Sync;
using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;

namespace Coo.Api.Hosting;

/// <summary>
/// Contracts to the use cases and back (the anti-corruption layer's edge, ADR 0002 §11). Details become a string here and
/// a <see cref="JsonElement"/> again on the way out; the Application layer never sees JSON types.
/// </summary>
internal static class SyncMapping
{
    /// <summary>
    /// A change the server can answer about: it has an ID and a version from a real device. Anything else makes the whole
    /// request <c>sync.malformed</c>, because there would be no change ID to report a result under.
    /// </summary>
    public static bool IsAnswerable(SyncChangeDto? change) =>
        change is { ChangeId: var id, Version: { DeviceId: var device } } && id != Guid.Empty && device != Guid.Empty;

    public static IncomingChange ToChange(SyncChangeDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);
        var isDelete = dto.Operation == SyncOperations.Delete;
        var payload = dto.Operation == SyncOperations.Upsert ? Payload(dto) : null;
        return new IncomingChange(
            dto.ChangeId, dto.EntityType ?? "", dto.EntityId, isDelete,
            new HlcStamp(dto.Version.WallClockMs, dto.Version.Counter, new DeviceId(dto.Version.DeviceId)), payload);
    }

    public static ChangeResultDto ToDto(ChangeVerdict verdict)
    {
        ArgumentNullException.ThrowIfNull(verdict);
        var result = verdict.Result.Outcome switch
        {
            ChangeOutcome.Applied => ChangeResults.Applied,
            ChangeOutcome.Superseded => ChangeResults.Superseded,
            _ => ChangeResults.Rejected,
        };
        return new ChangeResultDto(verdict.ChangeId, result, verdict.Result.Reason);
    }

    public static PullResponse ToDto(PullPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        return new PullResponse([.. page.Records.Select(ToDto)], page.Cursor, page.HasMore, page.BootstrapHead);
    }

    private static SyncRecordDto ToDto(PulledRecord record)
    {
        var version = new HlcStampDto(record.Version.WallClockMs, record.Version.Counter, record.Version.DeviceId.Value);
        var dto = new SyncRecordDto(record.EntityType, record.EntityId, version, record.IsDeleted, record.Seq);
        return record.Payload switch
        {
            CareEntryPayload c => dto with
            {
                CareEntry = new CareEntryDto(c.Kind, c.SubjectBabyId, c.SubjectPerson, c.IsSession, c.StartAt, c.EndAt, c.LoggedBy, c.Source,
                    c.SourcePromptVersion, c.Note, Element(c.DetailsJson)),
            },
            BabyProfilePayload b => dto with { BabyProfile = new BabyProfileDto(b.Name, b.Nicknames, b.BirthDate, b.DueDate) },
            HouseholdSettingsPayload s => dto with { HouseholdSettings = new HouseholdSettingsDto(s.VolumeUnit, s.TimeZoneId, s.HiddenKinds) },
            DismissalPayload d => dto with { DuplicateDismissal = new DuplicateDismissalDto(d.EntryA, d.EntryB) },
            _ => throw new ArgumentOutOfRangeException(nameof(record), record.Payload.GetType().Name, "Unknown payload."),
        };
    }

    private static ChangePayload? Payload(SyncChangeDto dto) => dto.EntityType switch
    {
        SyncEntityTypes.CareEntry when dto.CareEntry is { } c => new CareEntryPayload(
            c.Kind ?? "", c.SubjectBabyId, c.SubjectPerson, c.IsSession, c.StartAt, c.EndAt, c.LoggedBy ?? "", c.Source ?? "",
            c.SourcePromptVersion, c.Note, c.Details.ValueKind == JsonValueKind.Undefined ? "" : c.Details.GetRawText()),
        SyncEntityTypes.BabyProfile when dto.BabyProfile is { } b => new BabyProfilePayload(b.Name ?? "", b.Nicknames ?? [], b.BirthDate, b.DueDate),
        SyncEntityTypes.HouseholdSettings when dto.HouseholdSettings is { } s => new HouseholdSettingsPayload(s.VolumeUnit ?? "", s.TimeZoneId ?? "", s.HiddenKinds ?? []),
        SyncEntityTypes.DuplicateDismissal when dto.DuplicateDismissal is { } d => new DismissalPayload(d.EntryA, d.EntryB),
        _ => null,
    };

    private static JsonElement Element(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
```

- [ ] **Step 5: The endpoints**

`src/Backend/Coo.Api/Hosting/SyncEndpoints.cs`:

```csharp
using System.Globalization;
using System.Security.Claims;
using Coo.Api.Application.Sync;
using Coo.Contracts.Sync;
using Coo.Domain.Common;

namespace Coo.Api.Hosting;

/// <summary>
/// Sync (Spec 011 §7.1). Every call needs the access token (the bearer handler refuses an inactive session) and, per
/// request, an active membership of its household and a consent to the current wording. Refusals are codes; only the
/// membership-revoked answer is a problem body, ADR 0002 §10's exact signal.
/// </summary>
internal static class SyncEndpoints
{
    public static void MapSyncEndpoints(this IEndpointRouteBuilder app)
    {
        var sync = app.MapGroup("/api/sync").RequireAuthorization();

        sync.MapPost("/push", async Task<IResult> (
            HttpRequest http, ClaimsPrincipal principal, SyncAccess access, PushChanges push, CancellationToken cancellationToken) =>
        {
            var request = await AuthEndpoints.TryReadJsonAsync<PushRequest>(http, cancellationToken);
            if (request is null || request.HouseholdId == Guid.Empty || request.DeviceId == Guid.Empty || request.Changes is null
                || !request.Changes.All(SyncMapping.IsAnswerable))
                return AuthEndpoints.Refused(SyncErrors.Malformed, StatusCodes.Status400BadRequest);
            if (request.Changes.Count > SyncLimits.MaxPushChanges)
                return AuthEndpoints.Refused(SyncErrors.BatchTooLarge, StatusCodes.Status400BadRequest);
            if (request.SchemaVersion < SchemaVersions.Oldest)
                return AuthEndpoints.Refused(SyncErrors.ClientTooOld, StatusCodes.Status426UpgradeRequired);

            var household = new HouseholdId(request.HouseholdId);
            var allowed = await access.CheckAsync(AuthEndpoints.UserOf(principal), household, cancellationToken);
            if (Denied(allowed, household) is { } denied)
                return denied;

            var verdicts = await push.ExecuteAsync(
                household, allowed.Membership!, request.SchemaVersion, [.. request.Changes.Select(SyncMapping.ToChange)], cancellationToken);
            return Results.Ok(new PushResponse([.. verdicts.Select(SyncMapping.ToDto)]));
        });

        sync.MapGet("/pull", async Task<IResult> (
            HttpRequest http, ClaimsPrincipal principal, SyncAccess access, PullChanges pull, CancellationToken cancellationToken) =>
        {
            var query = http.Query;
            long? head = null;
            if (!Guid.TryParse(query["household"], out var id) || id == Guid.Empty
                || !long.TryParse(query["cursor"], NumberStyles.None, CultureInfo.InvariantCulture, out var cursor)
                || !int.TryParse(query["schemaVersion"], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var schema))
                return AuthEndpoints.Refused(SyncErrors.Malformed, StatusCodes.Status400BadRequest);
            if (!string.IsNullOrEmpty(query["bootstrapHead"]))
            {
                if (!long.TryParse(query["bootstrapHead"], NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
                    return AuthEndpoints.Refused(SyncErrors.Malformed, StatusCodes.Status400BadRequest);
                head = parsed;
            }

            if (schema < SchemaVersions.Oldest)
                return AuthEndpoints.Refused(SyncErrors.ClientTooOld, StatusCodes.Status426UpgradeRequired);

            var household = new HouseholdId(id);
            var allowed = await access.CheckAsync(AuthEndpoints.UserOf(principal), household, cancellationToken);
            if (Denied(allowed, household) is { } denied)
                return denied;

            return Results.Ok(SyncMapping.ToDto(await pull.ExecuteAsync(household, cursor, head, cancellationToken)));
        });

        app.MapGet("/api/households/{id:guid}/membership", async Task<IResult> (
            Guid id, ClaimsPrincipal principal, GetMembershipStatus status, CancellationToken cancellationToken) =>
        {
            if (id == Guid.Empty)
                return Results.NotFound();
            var found = await status.ExecuteAsync(AuthEndpoints.UserOf(principal), new HouseholdId(id), cancellationToken);
            return found.Membership is null
                ? Results.NotFound()
                : Results.Ok(new MembershipStatusResponse(found.IsRevoked ? MembershipStatuses.Revoked : MembershipStatuses.Active));
        }).RequireAuthorization();

        app.MapPost("/api/consents", async Task<IResult> (
            HttpRequest http, ClaimsPrincipal principal, RecordConsent record, CancellationToken cancellationToken) =>
        {
            var request = await AuthEndpoints.TryReadJsonAsync<ConsentRequest>(http, cancellationToken);
            var result = await record.ExecuteAsync(
                AuthEndpoints.UserOf(principal), request?.Purpose, request?.Wording, request?.AppVersion, request?.Language, cancellationToken);
            return result.IsFailure ? AuthEndpoints.Refused(result.Error!.Code, StatusCodes.Status400BadRequest) : Results.NoContent();
        }).RequireAuthorization();
    }

    private static IResult? Denied(SyncAccessResult access, HouseholdId household) => access.Denial switch
    {
        AccessDenial.None => null,
        AccessDenial.NotAMember => AuthEndpoints.Refused(SyncErrors.NotAMember, StatusCodes.Status403Forbidden),
        AccessDenial.ConsentRequired => AuthEndpoints.Refused(SyncErrors.ConsentRequired, StatusCodes.Status403Forbidden),
        _ => Results.Problem(
            statusCode: StatusCodes.Status403Forbidden,
            type: SyncErrors.MembershipRevokedType,
            title: "membership-revoked",
            extensions: new Dictionary<string, object?> { ["householdId"] = household.Value }),
    };
}
```

- [ ] **Step 6: The daily purge**

`src/Backend/Coo.Api/Hosting/SyncHistoryCleanupJob.cs`:

```csharp
using Coo.Api.Application.Sync;

namespace Coo.Api.Hosting;

/// <summary>
/// The daily purge of change results and conflict records older than 30 days (Spec 011 §7.2). The first run is 20
/// minutes after start-up, apart from the nonce clean-up's 15.
/// </summary>
internal sealed partial class SyncHistoryCleanupJob(PurgeSyncHistory purge, TimeProvider time, ILogger<SyncHistoryCleanupJob> logger) : BackgroundService
{
    private static readonly TimeSpan FirstRun = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(FirstRun, time, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    LogPurged(logger, await purge.ExecuteAsync(stoppingToken));
                }
                catch (Exception e) when (!stoppingToken.IsCancellationRequested)
                {
                    // One failed purge must not stop the next one, or the API (a command timeout included). Type only.
                    LogFailed(logger, e.GetType().Name);
                }

                await Task.Delay(Interval, time, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Sync history: {Count} old rows deleted")]
    private static partial void LogPurged(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Sync history clean-up failed: {ExceptionType}")]
    private static partial void LogFailed(ILogger logger, string exceptionType);
}
```

- [ ] **Step 7: Wiring and the version**

`src/Backend/Coo.Api/Program.cs`, after the auth registrations:

```csharp
// Sync (Spec 011 §7). The stores are singletons over the data source, so the use cases are too.
builder.Services.AddSingleton<SyncAccess>();
builder.Services.AddSingleton<PushChanges>();
builder.Services.AddSingleton<PullChanges>();
builder.Services.AddSingleton<RecordConsent>();
builder.Services.AddSingleton<GetMembershipStatus>();
builder.Services.AddSingleton<PurgeSyncHistory>();
```

inside `if (!verifyOnly)`: `builder.Services.AddHostedService<SyncHistoryCleanupJob>();`; after `app.MapAuthEndpoints();`:
`app.MapSyncEndpoints();`; add `using Coo.Api.Application.Sync;`; update the version comment to `"0.11.0+<commit>"`.
In `Coo.Api.csproj`: `<Version>0.11.0</Version>`.

- [ ] **Step 8: Run the tests to verify they pass**

Run: `dotnet test Coo.Tests.slnf`
Expected: PASS, everything (the architecture tests too: `Coo.Api` references no EF or Npgsql type).

- [ ] **Step 9: Commit**

```bash
dotnet format Coo.Tests.slnf
git add src/Backend tests
git commit -m "Sync endpoints with per-request membership and consent checks, /api/me consent, the daily purge; API 0.11.0 (Spec 011 §7)"
```

---

### Task 7: Two simulated devices converge (the protocol, end to end)

**Files:**
- Create: `tests/Coo.Api.IntegrationTests/SimulatedDevice.cs`, `tests/Coo.Api.IntegrationTests/ConvergenceTests.cs`

**Interfaces:**
- Consumes: Task 6's endpoints; Domain `HybridLogicalClock`, `HlcStamp`.
- Produces: nothing for production code. The test device follows the phone's rules (ADR 0002 §2–§6): an outbox per
  record, one change per record under its newest change ID, rows removed only on a final result, a pulled record kept
  out only when a pending local change is newer, the clock advanced past every pulled stamp.

- [ ] **Step 1: The simulated device**

`tests/Coo.Api.IntegrationTests/SimulatedDevice.cs`:

```csharp
using System.Net.Http.Json;
using System.Text.Json;
using Coo.Contracts.Sync;
using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;

namespace Coo.Api.IntegrationTests;

/// <summary>
/// A phone reduced to the protocol (ADR 0002): diapers with a note, an outbox, push, pull. Its wall clock is moved by
/// the test, so "later" is exact. It shares no code with the app: it checks the server against the ADR, not against the
/// phone's own implementation.
/// </summary>
internal sealed class SimulatedDevice(HttpClient client, string accessToken, Guid household, Guid device, int number, long startWallMs)
{
    private readonly HybridLogicalClock _clock = new(new DeviceId(device));
    private readonly List<(Guid ChangeId, Guid EntityId)> _outbox = [];
    private long _wall = startWallMs;
    private long _cursor;
    private long? _head;
    private int _changes;

    public sealed record Diaper(Guid Id, HlcStamp Version, bool IsDeleted, string? Note);

    public Dictionary<Guid, Diaper> Records { get; } = [];

    public int Pending => _outbox.Count;

    public void Wait(TimeSpan time) => _wall += (long)time.TotalMilliseconds;

    public void Log(Guid id, string note) => Write(new Diaper(id, Tick(), false, note));

    public void Edit(Guid id, string note) => Write(Records[id] with { Version = Tick(), Note = note });

    public void Delete(Guid id) => Write(Records[id] with { Version = Tick(), IsDeleted = true });

    /// <summary>The live entries, by ID, with their notes: what the timeline shows.</summary>
    public IReadOnlyDictionary<Guid, string?> Timeline() =>
        Records.Values.Where(r => !r.IsDeleted).ToDictionary(r => r.Id, r => r.Note);

    public async Task PushAsync()
    {
        // One change per record: its current state under its newest change ID (Spec 011 §6.1).
        var batch = _outbox.GroupBy(o => o.EntityId).Select(g => (Change: g.Last().ChangeId, Record: Records[g.Key])).ToList();
        if (batch.Count == 0)
            return;
        var changes = batch.Select(b => b.Record.IsDeleted
            ? new SyncChangeDto(b.Change, SyncEntityTypes.CareEntry, b.Record.Id, SyncOperations.Delete, Dto(b.Record.Version))
            : new SyncChangeDto(b.Change, SyncEntityTypes.CareEntry, b.Record.Id, SyncOperations.Upsert, Dto(b.Record.Version),
                CareEntry: new CareEntryDto("diaper", Calls.Id(5000), null, false, SyncCalls.At, null,
                    "user:7a1c0000-0000-7000-8000-000000000001", "manual", null, b.Record.Note,
                    JsonDocument.Parse("""{"v":1,"contents":"wet"}""").RootElement.Clone()))).ToArray();
        var response = await SyncCalls.PushAsync(client, accessToken, household, device, changes);
        response.EnsureSuccessStatusCode();

        // A final result removes that record's rows up to the change it answered (ADR 0002 §2).
        var answered = (await response.Content.ReadFromJsonAsync<PushResponse>())!.Results.Select(r => r.ChangeId).ToHashSet();
        var cut = batch.Where(b => answered.Contains(b.Change))
            .ToDictionary(b => b.Record.Id, b => _outbox.FindLastIndex(o => o.ChangeId == b.Change));
        var kept = _outbox.Where((o, i) => !cut.TryGetValue(o.EntityId, out var last) || i > last).ToList();
        _outbox.Clear();
        _outbox.AddRange(kept);
    }

    public async Task PullAsync()
    {
        // A device that already holds records must see every tombstone, even on its first pull (ruling 20).
        if (_cursor == 0 && _head is null && Records.Count > 0)
            _head = 0;

        while (true)
        {
            var response = await Calls.SendAsync(client, HttpMethod.Get, SyncCalls.Pull(household, _cursor, _head), accessToken);
            response.EnsureSuccessStatusCode();
            var page = (await response.Content.ReadFromJsonAsync<PullResponse>())!;
            foreach (var record in page.Records)
            {
                var incoming = new HlcStamp(record.Version.WallClockMs, record.Version.Counter, new DeviceId(record.Version.DeviceId));
                _clock.Observe(incoming, _wall);
                var pending = _outbox.Any(o => o.EntityId == record.EntityId);
                if (pending && Records[record.EntityId].Version > incoming)
                    continue;
                Records[record.EntityId] = new Diaper(record.EntityId, incoming, record.IsDeleted, record.CareEntry?.Note);
            }

            (_cursor, _head) = (page.Cursor, page.BootstrapHead ?? _head);
            if (!page.HasMore)
                return;
        }
    }

    private void Write(Diaper diaper)
    {
        Records[diaper.Id] = diaper;
        _outbox.Add((Calls.Id(900_000 + number * 10_000 + ++_changes), diaper.Id));
    }

    private HlcStamp Tick() => _clock.Tick(_wall);

    private static HlcStampDto Dto(HlcStamp stamp) => new(stamp.WallClockMs, stamp.Counter, stamp.DeviceId.Value);
}
```

(`Calls.Id(n)` turns a number into a fixed GUID; `number` keeps the two devices' change IDs apart.)

- [ ] **Step 2: Write the test**

`tests/Coo.Api.IntegrationTests/ConvergenceTests.cs`:

```csharp
namespace Coo.Api.IntegrationTests;

/// <summary>Spec 011 §10 (Api integration): two devices converge after interleaved offline edits, deletes and a revived edit.</summary>
public sealed class ConvergenceTests(AuthApi api) : IClassFixture<AuthApi>
{
    [Fact]
    public async Task PushAndPull_InterleavedOfflineWork_BothDevicesShowTheSameLog()
    {
        // Arrange: one account on two phones; the second joins the first's household.
        using var client = api.Factory.CreateClient();
        var household = Calls.Id(8001);
        var first = await SyncCalls.SignInWithConsentAsync(client, "converge", Calls.Id(8002), household);
        var second = await Calls.SignInAsync(client, "converge", Calls.Id(8003), Calls.Id(8004));
        var a = new SimulatedDevice(client, first.AccessToken, household, Calls.Id(8002), 1, SyncCalls.Wall);
        var b = new SimulatedDevice(client, second.AccessToken, second.HouseholdId, Calls.Id(8003), 2, SyncCalls.Wall);
        var (x, y, w, z) = (Calls.Id(8101), Calls.Id(8102), Calls.Id(8103), Calls.Id(8104));

        a.Log(x, "x");
        a.Log(y, "y");
        a.Log(w, "w");
        await a.PushAsync();
        await b.PullAsync();

        // Both offline.
        a.Wait(TimeSpan.FromSeconds(1));
        a.Edit(x, "x by A");                      // earlier than B's edit: loses
        b.Wait(TimeSpan.FromSeconds(5));
        b.Edit(x, "x by B");                      // the later correction: wins
        b.Delete(y);                              // later than A's edit of y: the delete wins
        a.Wait(TimeSpan.FromSeconds(1));
        a.Edit(y, "y by A");
        b.Wait(TimeSpan.FromSeconds(1));
        b.Delete(w);                              // a stale delete …
        a.Wait(TimeSpan.FromSeconds(10));
        a.Edit(w, "w revived by A");              // … loses to a later edit: w comes back
        a.Log(z, "z");
        a.Delete(z);                              // never on the server (Review Focus 4)

        // Act: back online, in an interleaved order. A has pushed but never pulled: its first pull starts at cursor 0
        // while it holds y, which B deleted (ruling 20).
        await b.PushAsync();
        await a.PushAsync();
        await b.PullAsync();
        await a.PullAsync();
        await b.PullAsync();

        // Assert
        var expected = new Dictionary<Guid, string?> { [x] = "x by B", [w] = "w revived by A" };
        a.Timeline().Should().BeEquivalentTo(expected);
        b.Timeline().Should().BeEquivalentTo(expected);
        a.Pending.Should().Be(0, "every change got a final result");
        b.Pending.Should().Be(0);
        b.Records.Should().NotContainKey(z, "an entry created and deleted offline never reaches another phone");
    }
}
```

- [ ] **Step 3: Run it**

Run: `dotnet test tests/Coo.Api.IntegrationTests --filter FullyQualifiedName~ConvergenceTests`
Expected: PASS (it exercises Tasks 1–6; if it fails, the failure is in those tasks' code, not in the simulator: compare
against ADR 0002 §3–§6 before touching `SimulatedDevice`). To capture a RED, temporarily change `LastWriterWinsRule` to
`stored.Version > incoming ? Superseded : Apply` with the operands swapped, run, paste the failure, revert.

- [ ] **Step 4: Commit**

```bash
dotnet format Coo.Tests.slnf
git add tests/Coo.Api.IntegrationTests
git commit -m "Sync: two simulated devices converge after offline edits, deletes and a revived edit (Spec 011 §10)"
```

---

### Task 8: Deploy the server (0.11.0)

**Files:**
- Modify: `DEPLOY.md` (a "Sync (Spec 011)" section)

**Interfaces:**
- Consumes: Tasks 1–7, committed.
- Produces: production at `0.11.0+<commit>` with the `Sync` migration applied; the phone tasks build against it.

- [ ] **Step 1: `DEPLOY.md`**

Add after "Sign-in settings":

```markdown
## Sync (Spec 011)

No new settings. The `Sync` migration runs at start-up like the others: it adds `baby_profiles`, `household_settings`,
`duplicate_dismissals`, `applied_changes`, `sync_conflicts`, `consents` and `memberships.revoked_at`.

- Every `/api/sync/*` request checks the caller's membership of the household and their consent to the current wording
  (`care-data-v1`). A `403` with `sync.consent-required` means the person has not agreed yet; it is not an outage.
- `SyncHistoryCleanupJob` deletes `applied_changes` and `sync_conflicts` rows older than 30 days, daily, 20 minutes after
  start-up. It logs `Sync history: N old rows deleted`, or `Sync history clean-up failed: <type>`.
- Consents are evidence (GDPR Art. 7(1)): never delete `consents` rows by hand; account deletion removes them.

Check after a deploy (read-only):

    ssh coldstart-prod "docker exec coo-postgres psql -U coo -d coo -tAc \"SELECT count(*) FROM information_schema.tables WHERE table_name IN ('applied_changes','consents','baby_profiles')\""

Expected: `3`.
```

Commit it: `git add DEPLOY.md && git commit -m "DEPLOY.md: sync, its checks and its daily purge (Spec 011)"`.

- [ ] **Step 2: The full check, then deploy** (Docker Desktop running; the owner allows the agent to run this)

```sh
git status --porcelain          # must print nothing
dotnet test Coo.Tests.slnf
./deploy/deploy-prod.sh --confirm
```

Expected: the script's own test run is green and it ends with `Healthy: {"status":"ok","version":"0.11.0+<commit>","database":"ok"}`.

- [ ] **Step 3: Smoke-test production** (read-only)

```sh
curl -s https://api.coo.lechdigital.nl/health
curl -s -o /dev/null -w "%{http_code}\n" -X POST https://api.coo.lechdigital.nl/api/sync/push
curl -s -o /dev/null -w "%{http_code}\n" "https://api.coo.lechdigital.nl/api/sync/pull?household=7a1c0000-0000-7000-8000-000000000001&cursor=0&schemaVersion=1"
curl -s -o /dev/null -w "%{http_code}\n" -X POST https://api.coo.lechdigital.nl/api/consents
ssh coldstart-prod "docker exec coo-postgres psql -U coo -d coo -tAc \"SELECT count(*) FROM information_schema.tables WHERE table_name IN ('applied_changes','consents','baby_profiles')\""
```

Expected: `"version":"0.11.0+…"`; `401`; `401`; `401`; `3`. A 0.10.0 phone keeps refreshing (it sends no
`nextRefreshToken`, served as before).

---
### Task 9: The phone's session generation and proposed next refresh token (Spec 011 §8.1, §8.2)

**Files:**
- Modify: `src/Mobile/Coo.Mobile.Application/Auth/Account.cs`, `Ports.cs`, `UseCases.cs`
- Modify: `src/Mobile/Coo.Mobile.Infrastructure/Auth/SessionStore.cs`, `TokenRefresher.cs`, `AuthClient.cs`
- Test: `tests/Coo.Mobile.Application.Tests/AccountGenerationTests.cs` (create), `AuthUseCaseTests.cs`;
  `tests/Coo.Mobile.Infrastructure.Tests/SessionStoreTests.cs`, `AuthenticatingHandlerTests.cs`, `AuthClientTests.cs`

**Interfaces:**
- Consumes: `RefreshRequest(string RefreshToken, string? NextRefreshToken)` (Task 1); the server of Task 3, deployed in Task 8.
- Produces:
  - `StoredSession(… , bool JoinPending, string? PendingRefreshToken = null)`.
  - `Account.Generation` (`long`); `Account.TrySetPendingRefreshTokenAsync(long generation, string next, CancellationToken)`,
    `TryReplaceTokensAsync(long generation, SessionTokens tokens, CancellationToken)` (clears the pending token),
    `TryExpireAsync(long generation, CancellationToken)`, `TryCompleteJoinAsync(long generation, CancellationToken)`,
    `UpdateProfileAsync(AccountProfile profile, CancellationToken)` — all `Task<bool>` except the last (`Task`).
    `SignedInAsync` and `SignedOutAsync` move the generation; nothing else does. `Account : IDisposable`.
  - `IAuthApi.RefreshAsync(string refreshToken, string nextRefreshToken, CancellationToken)`.
  - `AccountOverview(… , IReadOnlyList<DeviceSessionInfo> Devices, ConsentInfo? Consent = null)`,
    `ConsentInfo(string Wording, DateTimeOffset GivenAt)` (filled by `AccountClient` in Task 15).
  - `SignIn`: an `existing` outcome whose household is not this phone's active one is stored as join-pending (ruling 9).

- [ ] **Step 1: Write the failing tests**

`tests/Coo.Mobile.Application.Tests/AccountGenerationTests.cs`:

```csharp
using Coo.Domain.Common;
using Coo.Mobile.Application.Auth;

namespace Coo.Mobile.Application.Tests;

/// <summary>Spec 011 §8.1: a refresh started before a sign-out or sign-in may not write afterwards.</summary>
public sealed class AccountGenerationTests : IDisposable
{
    private static readonly AccountProfile Profile = new(new UserId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000c1")), "Andrzej", "andrzej@example.com");
    private static readonly HouseholdId Household = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a1"));
    private static readonly DateTimeOffset Expires = new(2026, 10, 2, 12, 15, 0, TimeSpan.Zero);
    private static readonly StoredSession Session = new("access-1", Expires, "refresh-1", Profile, Household, JoinPending: false);
    private static readonly SessionTokens Fresh = new("access-2", Expires.AddMinutes(15), "refresh-2");

    private readonly MemorySessions _sessions = new();
    private readonly Account _account;

    public AccountGenerationTests() => _account = new Account(_sessions);

    public void Dispose() => _account.Dispose();

    [Fact]
    public async Task SignedInAsync_ThenSignedOutAsync_MovesTheGenerationEachTime()
    {
        // Arrange
        var start = _account.Generation;

        // Act
        await _account.SignedInAsync(Session, CancellationToken.None);
        var afterSignIn = _account.Generation;
        await _account.SignedOutAsync(sessionExpired: false, CancellationToken.None);

        // Assert
        afterSignIn.Should().BeGreaterThan(start);
        _account.Generation.Should().BeGreaterThan(afterSignIn);
    }

    [Fact]
    public async Task TryReplaceTokensAsync_AfterASignOut_DoesNotSignBackIn()
    {
        // Arrange
        await _account.SignedInAsync(Session, CancellationToken.None);
        var generation = _account.Generation;
        await _account.SignedOutAsync(sessionExpired: false, CancellationToken.None);

        // Act
        var replaced = await _account.TryReplaceTokensAsync(generation, Fresh, CancellationToken.None);

        // Assert
        replaced.Should().BeFalse();
        _sessions.Stored.Should().BeNull();
        _account.State.Should().Be(new SignedOut(SessionExpired: false));
    }

    [Fact]
    public async Task TryReplaceTokensAsync_AfterSigningInAgain_KeepsTheNewSession()
    {
        // Arrange
        await _account.SignedInAsync(Session, CancellationToken.None);
        var generation = _account.Generation;
        var again = Session with { AccessToken = "access-9", RefreshToken = "refresh-9" };
        await _account.SignedInAsync(again, CancellationToken.None);

        // Act
        var replaced = await _account.TryReplaceTokensAsync(generation, Fresh, CancellationToken.None);

        // Assert
        replaced.Should().BeFalse();
        _sessions.Stored.Should().Be(again);
    }

    [Fact]
    public async Task TryReplaceTokensAsync_SameGeneration_SavesTheTokensAndClearsThePendingOne()
    {
        // Arrange
        await _account.SignedInAsync(Session, CancellationToken.None);
        var generation = _account.Generation;
        await _account.TrySetPendingRefreshTokenAsync(generation, "pending", CancellationToken.None);

        // Act
        var replaced = await _account.TryReplaceTokensAsync(generation, Fresh, CancellationToken.None);

        // Assert
        replaced.Should().BeTrue();
        _sessions.Stored.Should().Be(Session with { AccessToken = "access-2", AccessTokenExpiresAt = Fresh.AccessTokenExpiresAt, RefreshToken = "refresh-2" });
        _account.Generation.Should().Be(generation, "replacing tokens is not a sign-in");
    }

    [Fact]
    public async Task TryExpireAsync_AfterADeliberateSignOut_DoesNotSayTheSessionExpired()
    {
        // Arrange
        await _account.SignedInAsync(Session, CancellationToken.None);
        var generation = _account.Generation;
        await _account.SignedOutAsync(sessionExpired: false, CancellationToken.None);

        // Act
        var expired = await _account.TryExpireAsync(generation, CancellationToken.None);

        // Assert
        expired.Should().BeFalse();
        _account.State.Should().Be(new SignedOut(SessionExpired: false));
    }

    [Fact]
    public async Task TryExpireAsync_SameGeneration_SignsOutAsExpired()
    {
        // Arrange
        await _account.SignedInAsync(Session, CancellationToken.None);

        // Act
        var expired = await _account.TryExpireAsync(_account.Generation, CancellationToken.None);

        // Assert
        expired.Should().BeTrue();
        _account.State.Should().Be(new SignedOut(SessionExpired: true));
        _sessions.Stored.Should().BeNull();
    }

    [Fact]
    public async Task UpdateProfileAsync_NewName_KeepsTheGeneration()
    {
        // Arrange
        await _account.SignedInAsync(Session, CancellationToken.None);
        var generation = _account.Generation;

        // Act
        await _account.UpdateProfileAsync(Profile with { Name = "Andrzej L." }, CancellationToken.None);

        // Assert
        _account.Generation.Should().Be(generation);
        _account.State.Should().Be(new SignedIn(Profile with { Name = "Andrzej L." }));
    }

    [Fact]
    public async Task TryCompleteJoinAsync_SameGeneration_SignsInFully()
    {
        // Arrange
        await _account.SignedInAsync(Session with { JoinPending = true }, CancellationToken.None);

        // Act
        var completed = await _account.TryCompleteJoinAsync(_account.Generation, CancellationToken.None);

        // Assert
        completed.Should().BeTrue();
        _account.State.Should().Be(new SignedIn(Profile));
        _sessions.Stored!.JoinPending.Should().BeFalse();
    }
}
```

Add to `tests/Coo.Mobile.Application.Tests/AuthUseCaseTests.cs` (it already has `Grants(outcome, household)`):

```csharp
    [Fact]
    public async Task ExecuteAsync_ExistingWithAnotherHousehold_IsJoinPending()
    {
        // Arrange: this phone signed out while join-pending and signs in again; it must still be asked (ruling 9).
        Grants(SignInOutcome.Existing, AccountsHousehold);

        // Act
        await SignIn().ExecuteAsync(CancellationToken.None);

        // Assert
        _account.State.Should().Be(new JoinPending(Profile, AccountsHousehold));
    }
```

and change the `_api.RefreshAsync(…)` arrangements nowhere in this file (it has none). In `RefreshAccount` tests, the
existing assertion that the profile is replaced still holds (`UpdateProfileAsync`).

Add to `tests/Coo.Mobile.Infrastructure.Tests/SessionStoreTests.cs`:

```csharp
    [Fact]
    public void Parse_VersionOneSession_ReadsWithoutAPendingToken()
    {
        // Arrange: what 0.10.0 stored (Review Focus 1).
        var text = "1\u001faccess-1\u001f1790000000000\u001frefresh-1\u001f7a1c0000-0000-7000-8000-0000000000c1\u001fAndrzej"
            + "\u001fandrzej@example.com\u001f7a1c0000-0000-7000-8000-0000000000a1\u001f0";

        // Act
        var session = SessionStore.Parse(text);

        // Assert
        session!.RefreshToken.Should().Be("refresh-1");
        session.PendingRefreshToken.Should().BeNull();
    }

    [Fact]
    public async Task SaveAsyncThenGetAsync_PendingToken_RoundTrips()
    {
        // Arrange
        var store = new SessionStore(_secrets);
        var pending = Session with { PendingRefreshToken = new string('p', 43) };

        // Act
        await store.SaveAsync(pending, CancellationToken.None);

        // Assert
        (await store.GetAsync(CancellationToken.None)).Should().Be(pending);
    }
```

In `tests/Coo.Mobile.Infrastructure.Tests/AuthenticatingHandlerTests.cs`, every `_api.RefreshAsync("refresh-1",
Arg.Any<CancellationToken>())` becomes `_api.RefreshAsync("refresh-1", Arg.Any<string>(), Arg.Any<CancellationToken>())`,
and `DidNotReceiveWithAnyArgs().RefreshAsync(default!, default)` becomes `…RefreshAsync(default!, default!, default)`.
Add:

```csharp
    [Fact]
    public async Task RefreshAsync_FirstAttemptUnavailable_SendsTheSameProposalAgain()
    {
        // Arrange: the response to the first refresh is lost; the retry must carry the same pair (Spec 011 §8.2).
        await _account.SignedInAsync(Session, CancellationToken.None);
        var proposals = new List<string>();
        _api.RefreshAsync("refresh-1", Arg.Do<string>(proposals.Add), Arg.Any<CancellationToken>())
            .Returns(_ => throw new AuthUnavailableException("lost"), _ => Task.FromResult(new SessionTokens("new", Now.AddMinutes(15), proposals[^1])));

        // Act
        var first = await Record(() => _refresher.RefreshAsync("old", CancellationToken.None));
        var token = await _refresher.RefreshAsync("old", CancellationToken.None);

        // Assert
        first.Should().BeOfType<AuthUnavailableException>();
        token.Should().Be("new");
        proposals.Should().HaveCount(2);
        proposals[1].Should().Be(proposals[0]);
        proposals[0].Should().HaveLength(43);
        (await _sessions.GetAsync(CancellationToken.None))!.Should().Match<StoredSession>(s => s.RefreshToken == proposals[0] && s.PendingRefreshToken == null);
    }

    [Fact]
    public async Task RefreshAsync_SignedOutWhileTheRefreshIsInFlight_StaysSignedOut()
    {
        // Arrange
        await _account.SignedInAsync(Session, CancellationToken.None);
        var answer = new TaskCompletionSource<SessionTokens>(TaskCreationOptions.RunContinuationsAsynchronously);
        _api.RefreshAsync("refresh-1", Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(answer.Task);
        var refresh = _refresher.RefreshAsync("old", CancellationToken.None);
        await _account.SignedOutAsync(sessionExpired: false, CancellationToken.None);

        // Act
        answer.SetResult(new SessionTokens("new", Now.AddMinutes(15), "refresh-2"));
        var token = await refresh;

        // Assert
        token.Should().BeNull();
        (await _sessions.GetAsync(CancellationToken.None)).Should().BeNull();
        _account.State.Should().Be(new SignedOut(SessionExpired: false));
    }

    [Fact]
    public async Task RefreshAsync_RefusedAfterADeliberateSignOut_DoesNotSayExpired()
    {
        // Arrange
        await _account.SignedInAsync(Session, CancellationToken.None);
        var answer = new TaskCompletionSource<SessionTokens>(TaskCreationOptions.RunContinuationsAsynchronously);
        _api.RefreshAsync("refresh-1", Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(answer.Task);
        var refresh = _refresher.RefreshAsync("old", CancellationToken.None);
        await _account.SignedOutAsync(sessionExpired: false, CancellationToken.None);

        // Act
        answer.SetException(new AuthRejectedException("auth.refresh-invalid"));
        var token = await refresh;

        // Assert
        token.Should().BeNull();
        _account.State.Should().Be(new SignedOut(SessionExpired: false));
    }

    [Fact]
    public async Task RefreshAsync_SignedInAgainWhileInFlight_KeepsTheNewSession()
    {
        // Arrange
        await _account.SignedInAsync(Session, CancellationToken.None);
        var answer = new TaskCompletionSource<SessionTokens>(TaskCreationOptions.RunContinuationsAsynchronously);
        _api.RefreshAsync("refresh-1", Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(answer.Task);
        var refresh = _refresher.RefreshAsync("old", CancellationToken.None);
        var again = Session with { AccessToken = "access-9", RefreshToken = "refresh-9" };
        await _account.SignedInAsync(again, CancellationToken.None);

        // Act
        answer.SetResult(new SessionTokens("new", Now.AddMinutes(15), "refresh-2"));
        await refresh;

        // Assert
        (await _sessions.GetAsync(CancellationToken.None)).Should().Be(again);
    }
```

In `tests/Coo.Mobile.Infrastructure.Tests/AuthClientTests.cs`, every `client.RefreshAsync("refresh-1", CancellationToken.None)`
becomes `client.RefreshAsync("refresh-1", new string('n', 43), CancellationToken.None)`, and add:

```csharp
    [Fact]
    public async Task RefreshAsync_Proposal_TravelsInTheBody()
    {
        // Arrange
        string? body = null;
        var client = Client(async r =>
        {
            body = await r.Content!.ReadAsStringAsync();
            return await Json(HttpStatusCode.OK, """{"accessToken":"a","accessTokenExpiresAt":"2026-10-02T12:15:00Z","refreshToken":"nnn"}""");
        });

        // Act
        await client.RefreshAsync("refresh-1", "nnn", CancellationToken.None);

        // Assert
        body.Should().Contain("\"refreshToken\":\"refresh-1\"").And.Contain("\"nextRefreshToken\":\"nnn\"");
    }
```

(Adapt `Client`/`Json` to the helpers' exact shapes in that file.)

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Coo.Mobile.Application.Tests && dotnet test tests/Coo.Mobile.Infrastructure.Tests`
Expected: FAIL to compile (`Account.Generation`, `PendingRefreshToken`, the three-argument `RefreshAsync` not found).
Paste the first error.

- [ ] **Step 3: The account**

`src/Mobile/Coo.Mobile.Application/Auth/Ports.cs`:

```csharp
/// <summary>
/// What the phone keeps after signing in (Spec 010 §3.3): both tokens, who is signed in, the account's household, whether
/// this phone's entries are still to join it, and the refresh token this phone proposed and has not seen accepted yet
/// (Spec 011 §8.2): a retry sends the same one.
/// </summary>
public sealed record StoredSession(
    string AccessToken, DateTimeOffset AccessTokenExpiresAt, string RefreshToken, AccountProfile Profile, HouseholdId AccountHousehold,
    bool JoinPending, string? PendingRefreshToken = null);

// IAuthApi:
    /// <summary>Refreshes with <paramref name="refreshToken"/>, proposing <paramref name="nextRefreshToken"/> as the next one.</summary>
    Task<SessionTokens> RefreshAsync(string refreshToken, string nextRefreshToken, CancellationToken cancellationToken);

/// <summary>The account's consent to a wording (Spec 011 §3), as <c>/api/me</c> reports it.</summary>
public sealed record ConsentInfo(string Wording, DateTimeOffset GivenAt);

public sealed record AccountOverview(
    AccountProfile Profile, HouseholdId? Household, string? Role, IReadOnlyList<DeviceSessionInfo> Devices, ConsentInfo? Consent = null);
```

`src/Mobile/Coo.Mobile.Application/Auth/Account.cs`, the class:

```csharp
/// <summary>
/// Who is signed in on this phone, in step with the session store. One per app. Being signed out never blocks logging
/// (ADR 0003 §4), and nothing here touches local care data. Every sign-in and sign-out moves <see cref="Generation"/>
/// under one lock (Spec 011 §8.1); work started before a move (a refresh, a sync cycle) writes back only through the
/// <c>Try…</c> methods, which do nothing once it has moved.
/// </summary>
public sealed class Account(ISessionStore sessions) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _generation;

    public AccountState State { get; private set; } = new SignedOut(SessionExpired: false);

    public long Generation => Interlocked.Read(ref _generation);

    public event EventHandler? Changed;

    public async Task<AccountState> LoadAsync(CancellationToken cancellationToken)
    {
        Set(From(await sessions.GetAsync(cancellationToken)));
        return State;
    }

    public async Task SignedInAsync(StoredSession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Interlocked.Increment(ref _generation);
            await sessions.SaveAsync(session, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }

        Set(From(session));
    }

    /// <summary>The tokens go; every local record and the outbox stay (ADR 0003 §4).</summary>
    public async Task SignedOutAsync(bool sessionExpired, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Interlocked.Increment(ref _generation);
            await sessions.ClearAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }

        Set(new SignedOut(sessionExpired));
    }

    public Task<bool> TrySetPendingRefreshTokenAsync(long generation, string next, CancellationToken cancellationToken) =>
        TryChangeAsync(generation, s => s with { PendingRefreshToken = next }, cancellationToken);

    public Task<bool> TryReplaceTokensAsync(long generation, SessionTokens tokens, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        return TryChangeAsync(
            generation,
            s => s with
            {
                AccessToken = tokens.AccessToken, AccessTokenExpiresAt = tokens.AccessTokenExpiresAt, RefreshToken = tokens.RefreshToken,
                PendingRefreshToken = null,
            },
            cancellationToken);
    }

    /// <summary>The bring-over committed (Spec 011 §4): this phone now shows the account's household.</summary>
    public async Task<bool> TryCompleteJoinAsync(long generation, CancellationToken cancellationToken)
    {
        if (!await TryChangeAsync(generation, s => s with { JoinPending = false }, cancellationToken))
            return false;
        Set(From(await sessions.GetAsync(cancellationToken)));
        return true;
    }

    /// <summary>The server ended the session (only <c>auth.refresh-invalid</c>); after a deliberate sign-out it does nothing.</summary>
    public async Task<bool> TryExpireAsync(long generation, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (generation != _generation)
                return false;
            Interlocked.Increment(ref _generation);
            await sessions.ClearAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }

        Set(new SignedOut(SessionExpired: true));
        return true;
    }

    /// <summary>Settings' refresh of the name and email. Not a sign-in: the generation stays.</summary>
    public async Task UpdateProfileAsync(AccountProfile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        StoredSession? updated = null;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (await sessions.GetAsync(cancellationToken) is { } session)
            {
                updated = session with { Profile = profile };
                await sessions.SaveAsync(updated, cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }

        if (updated is not null)
            Set(From(updated));
    }

    public void Dispose() => _gate.Dispose();

    public static AccountState From(StoredSession? session) => session switch
    {
        null => new SignedOut(SessionExpired: false),
        { JoinPending: true } => new JoinPending(session.Profile, session.AccountHousehold),
        _ => new SignedIn(session.Profile),
    };

    private async Task<bool> TryChangeAsync(long generation, Func<StoredSession, StoredSession> change, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (generation != _generation || await sessions.GetAsync(cancellationToken) is not { } session)
                return false;
            await sessions.SaveAsync(change(session), cancellationToken);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    // Raised outside the lock: Changed's handlers are not this class's to hold a lock over.
    private void Set(AccountState state)
    {
        if (state == State)
            return;
        State = state;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
```

`src/Mobile/Coo.Mobile.Application/Auth/UseCases.cs`:

```csharp
// SignIn, where the session is stored:
            // `existing` with another household: this phone left while join-pending and must still be asked (ruling 9).
            var joinPending = grant.Outcome == SignInOutcome.Joined
                || (grant.Outcome == SignInOutcome.Existing && grant.Household != household);
            await account.SignedInAsync(
                new StoredSession(grant.Tokens.AccessToken, grant.Tokens.AccessTokenExpiresAt, grant.Tokens.RefreshToken, grant.Profile,
                    grant.Household, JoinPending: joinPending),
                cancellationToken);

// RefreshAccount, the end:
        var session = await sessions.GetAsync(cancellationToken);
        if (session is not null && session.Profile != me.Profile)
            await account.UpdateProfileAsync(me.Profile, cancellationToken);
```

(Keep the comment above `SignIn`'s stored session in step: "`created` and `joined` are resolved by sync (Spec 011 §4)".)

- [ ] **Step 4: The session codec, the client and the refresher**

`SessionStore`: write version `"2"` with a tenth field, the pending token (empty for none); read both:

```csharp
    private const string Version = "2";

    internal static string Format(StoredSession session) => string.Join(
        Separator,
        Version,
        session.AccessToken,
        session.AccessTokenExpiresAt.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
        session.RefreshToken,
        session.Profile.UserId.Value.ToString("D", CultureInfo.InvariantCulture),
        Clean(session.Profile.Name),
        Clean(session.Profile.Email),
        session.AccountHousehold.Value.ToString("D", CultureInfo.InvariantCulture),
        session.JoinPending ? "1" : "0",
        session.PendingRefreshToken ?? "");

    /// <summary>Version 1 (0.10.0) has nine fields and no pending token; version 2 has ten.</summary>
    internal static StoredSession? Parse(string text)
    {
        var f = text.Split(Separator);
        var known = (f[0] == "1" && f.Length == 9) || (f[0] == "2" && f.Length == 10);
        if (!known || f[1].Length == 0 || f[3].Length == 0 || f[8] is not ("0" or "1")
            || !long.TryParse(f[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var expires) || expires is < 0 or > LatestUnixMs
            || !Guid.TryParseExact(f[4], "D", out var user) || user == Guid.Empty
            || !Guid.TryParseExact(f[7], "D", out var household) || household == Guid.Empty)
            return null;
        var pending = f.Length == 10 && f[9].Length > 0 ? f[9] : null;
        return new StoredSession(
            f[1], DateTimeOffset.FromUnixTimeMilliseconds(expires), f[3], new AccountProfile(new UserId(user), f[5], f[6]),
            new HouseholdId(household), f[8] == "1", pending);
    }
```

`AuthClient.RefreshAsync(string refreshToken, string nextRefreshToken, CancellationToken cancellationToken)` sends
`new RefreshRequest(refreshToken, nextRefreshToken)`; nothing else changes.

`TokenRefresher.RefreshCoreAsync` (the class summary gains: "A refresh started before a sign-out or sign-in writes back
only if the account's generation has not moved (Spec 011 §8.1); it proposes the next refresh token and keeps proposing
the same one until an answer arrives (§8.2)."):

```csharp
    private async Task<string?> RefreshCoreAsync(string staleAccessToken, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var generation = account.Generation;
            var session = await sessions.GetAsync(cancellationToken);
            if (session is null)
                return null;
            if (session.AccessToken != staleAccessToken)
                return session.AccessToken;

            // The proposal is stored before it is sent, so a lost response is retried with the same pair.
            var next = session.PendingRefreshToken;
            if (next is null)
            {
                next = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
                if (!await account.TrySetPendingRefreshTokenAsync(generation, next, cancellationToken))
                    return null;
            }

            SessionTokens tokens;
            try
            {
                tokens = await api.RefreshAsync(session.RefreshToken, next, cancellationToken);
            }
            catch (AuthRejectedException e) when (e.Code == RefreshInvalid)
            {
                await account.TryExpireAsync(generation, cancellationToken);
                return null;
            }
            catch (AuthRejectedException e)
            {
                // A captive portal's 403, a proxy's 404 during a deploy: not the server refusing this session.
                throw new AuthUnavailableException("The refresh was refused with " + e.Code + ".", e);
            }

            return await account.TryReplaceTokensAsync(generation, tokens, cancellationToken) ? tokens.AccessToken : null;
        }
        finally
        {
            _gate.Release();
        }
    }
```

with `using System.Buffers.Text; using System.Security.Cryptography;`.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/Coo.Mobile.Application.Tests && dotnet test tests/Coo.Mobile.Infrastructure.Tests && dotnet test tests/Coo.Mobile.Presentation.Tests`
Expected: PASS. If a test somewhere asserts the 0.10.0 `RefreshAccount` behaviour through `SignedInAsync`, it still
passes: the state changes the same way.

- [ ] **Step 6: Commit**

```bash
dotnet format Coo.Tests.slnf
git add src/Mobile tests/Coo.Mobile.Application.Tests tests/Coo.Mobile.Infrastructure.Tests
git commit -m "Sessions: a generation stops a refresh that outlived its sign-in; the phone proposes the next refresh token (Spec 011 §8.1, §8.2)"
```

---
### Task 10: The phone's sync tables, the active household, the local-write signal, and observing pulled stamps

**Files:**
- Create: `src/Mobile/Coo.Mobile.Infrastructure/Persistence/Migrations/0004-sync.sql`
- Create: `src/Mobile/Coo.Mobile.Application/Sync/LocalWrites.cs`
- Modify: `src/Mobile/Coo.Mobile.Application/CareLog/Ports.cs` (`IVersionClock.ObserveAsync`),
  `src/Mobile/Coo.Mobile.Application/Households/Households.cs` (`ListHouseholdsAsync` documents the active-first order),
  `src/Mobile/Coo.Mobile.Application/ServiceCollectionExtensions.cs` (registers `LocalWrites`)
- Modify: `src/Mobile/Coo.Mobile.Infrastructure/Persistence/DeviceState.cs`, `HouseholdStore.cs`, `CareLogStore.cs`,
  `DuplicateDismissalStore.cs`, `Outbox.cs` (entity types from `Coo.Contracts.Sync`)
- Test: `tests/Coo.Mobile.Infrastructure.Tests/SyncSchemaAndSignalTests.cs` (create); modify `TestDatabase.cs`,
  `DeviceAndMigrationTests.cs` (the dismissal store's constructor), `migrations-manifest.txt`

**Interfaces:**
- Consumes: nothing from earlier phone tasks.
- Produces:
  - Tables `sync_cursors (household_id TEXT PK, last_seq INTEGER ≥ 0, bootstrap_head INTEGER NULL)` and
    `sync_rejections (entity_type, entity_id, household_id, reason, rejected_utc_ms; PK (entity_type, entity_id))`;
    index `ix_outbox_entity (entity_type, entity_id)`.
  - `app_state` keys used from here on: `active_household`, `consent:<userId>`, `softlock:<householdId>`.
  - `public sealed class LocalWrites { event EventHandler? Written; void Notify(); }` — raised after every committed
    local write that appended an outbox row.
  - `IVersionClock.ObserveAsync(HlcStamp remote, CancellationToken)`.
  - `CareLogStore(LocalDatabase, TimeProvider, LocalWrites)`, `HouseholdStore(LocalDatabase, TimeProvider, LocalWrites)`,
    `DuplicateDismissalStore(LocalDatabase, TimeProvider, LocalWrites)`.
  - `IHouseholdStore.ListHouseholdsAsync` returns the household named by `app_state.active_household` first (ruling 7).

- [ ] **Step 1: Write the failing tests**

In `tests/Coo.Mobile.Infrastructure.Tests/TestDatabase.cs`: add `public LocalWrites Writes { get; } = new();` and
construct `Store = new CareLogStore(Database, Time, Writes);` and `Households = new HouseholdStore(Database, Time, Writes);`.
In `DeviceAndMigrationTests.cs`, `new DuplicateDismissalStore(db.Database, db.Time)` becomes
`new DuplicateDismissalStore(db.Database, db.Time, db.Writes)`.

`tests/Coo.Mobile.Infrastructure.Tests/SyncSchemaAndSignalTests.cs`:

```csharp
using Coo.Domain.CareLog.Duplicates;
using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;
using Coo.Domain.Households;
using Coo.Mobile.Infrastructure.Persistence;

namespace Coo.Mobile.Infrastructure.Tests;

/// <summary>Spec 011: migration 0004, the active household, the local-write signal, and the clock seeing pulled stamps.</summary>
public sealed class SyncSchemaAndSignalTests
{
    [Fact]
    public async Task Migrate_EmptyDatabase_CreatesTheSyncTablesAtVersionFour()
    {
        // Arrange
        await using var db = await TestDatabase.CreateAsync();

        // Act
        var tables = await db.ScalarAsync("SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name IN ('sync_cursors', 'sync_rejections');");

        // Assert
        tables.Should().Be(2);
        (await db.ScalarAsync("PRAGMA user_version;")).Should().Be(4);
    }

    [Fact]
    public async Task SaveAsync_ACareEntry_SignalsOneLocalWriteAfterItIsStored()
    {
        // Arrange
        await using var db = await TestDatabase.CreateAsync();
        long outboxWhenSignalled = -1;
        var signals = 0;
        db.Writes.Written += (_, _) =>
        {
            signals++;
            outboxWhenSignalled = db.ScalarAsync("SELECT count(*) FROM outbox;").GetAwaiter().GetResult();
        };

        // Act
        await db.DiaperAsync(db.Time.GetLocalNow());

        // Assert
        signals.Should().Be(1);
        outboxWhenSignalled.Should().Be(1, "the signal comes after the commit");
    }

    [Fact]
    public async Task DeleteAsync_AnEntry_SignalsALocalWrite()
    {
        // Arrange
        await using var db = await TestDatabase.CreateAsync();
        var entry = await db.DiaperAsync(db.Time.GetLocalNow());
        var signals = 0;
        db.Writes.Written += (_, _) => signals++;

        // Act
        await db.Store.DeleteAsync(entry.Id, await db.Device.NextAsync(CancellationToken.None), CancellationToken.None);

        // Assert
        signals.Should().Be(1);
    }

    [Fact]
    public async Task SaveSettingsSaveBabyAndSaveDismissal_EachSignalALocalWrite()
    {
        // Arrange
        await using var db = await TestDatabase.CreateAsync();
        var signals = 0;
        db.Writes.Written += (_, _) => signals++;
        var settings = HouseholdSettings.Create(TestDatabase.Household, VolumeUnit.Millilitres, "Europe/Amsterdam", await db.Device.NextAsync(CancellationToken.None)).Value;
        var baby = BabyProfile.Create(TestDatabase.Baby, TestDatabase.Household, "Maja", [], new DateOnly(2026, 8, 20), null,
            await db.Device.NextAsync(CancellationToken.None), new DateOnly(2026, 10, 1)).Value;
        var dismissal = DuplicateDismissal.Create(new DismissalId(Guid.CreateVersion7(db.Time.GetUtcNow())), TestDatabase.Household,
            new EntryPair(await db.NewIdAsync(), await db.NewIdAsync()), await db.Device.NextAsync(CancellationToken.None));

        // Act
        await db.Households.SaveSettingsAsync(settings, CancellationToken.None);
        await db.Households.SaveBabyAsync(baby, CancellationToken.None);
        await new DuplicateDismissalStore(db.Database, db.Time, db.Writes).SaveAsync(dismissal, CancellationToken.None);

        // Assert
        signals.Should().Be(3);
    }

    [Fact]
    public async Task ListHouseholdsAsync_ActiveHouseholdNamed_ComesFirstWhateverItsId()
    {
        // Arrange: the account's household (pulled while join-pending) sorts before this phone's by ID.
        await using var db = await TestDatabase.CreateAsync();
        var mine = new HouseholdId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000f1"));
        var accounts = new HouseholdId(Guid.Parse("7a1c0000-0000-7000-8000-000000000001"));
        foreach (var id in new[] { mine, accounts })
            await db.Households.SaveSettingsAsync(
                HouseholdSettings.Create(id, VolumeUnit.Millilitres, "Europe/Amsterdam", await db.Device.NextAsync(CancellationToken.None)).Value, CancellationToken.None);
        await using (var connection = await db.OpenAsync())
            await connection.ExecuteAsync("INSERT INTO app_state (key, value) VALUES ('active_household', $v);", CancellationToken.None, null, ("$v", Columns.Id(mine.Value)));

        // Act
        var listed = await db.Households.ListHouseholdsAsync(CancellationToken.None);

        // Assert
        listed.Select(h => h.Id).Should().Equal(mine, accounts);
    }

    [Fact]
    public async Task ObserveAsync_StampFromTheFuture_TheNextLocalStampIsHigherAfterARestart()
    {
        // Arrange: another phone's clock is ten minutes ahead (ADR 0002 §4).
        await using var db = await TestDatabase.CreateAsync();
        var remote = new HlcStamp(db.Time.GetUtcNow().AddMinutes(10).ToUnixTimeMilliseconds(), 3, new DeviceId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000d9")));

        // Act
        await db.Device.ObserveAsync(remote, CancellationToken.None);
        using var restarted = new DeviceState(db.Database, db.Time);
        var next = await restarted.NextAsync(CancellationToken.None);

        // Assert
        next.Should().BeGreaterThan(remote);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Coo.Mobile.Infrastructure.Tests`
Expected: FAIL to compile (`LocalWrites`, `ObserveAsync`, the three-argument store constructors not found). Paste the
first error.

- [ ] **Step 3: The migration**

`src/Mobile/Coo.Mobile.Infrastructure/Persistence/Migrations/0004-sync.sql`:

```sql
-- 0004 (Spec 011 §6.1, §6.2): the pull cursor per household, committed with the page it applied; the changes the server
-- did not accept, kept until the record is fixed (ADR 0002 §5); and an index for the per-record outbox lookups.

CREATE TABLE sync_cursors (
    household_id   TEXT    NOT NULL PRIMARY KEY,
    last_seq       INTEGER NOT NULL CHECK (last_seq >= 0),
    bootstrap_head INTEGER NULL CHECK (bootstrap_head IS NULL OR bootstrap_head >= 0)
) STRICT;

CREATE TABLE sync_rejections (
    entity_type     TEXT    NOT NULL,
    entity_id       TEXT    NOT NULL,
    household_id    TEXT    NOT NULL,
    reason          TEXT    NOT NULL CHECK (length(reason) > 0),
    rejected_utc_ms INTEGER NOT NULL,
    PRIMARY KEY (entity_type, entity_id)
) STRICT;

CREATE INDEX ix_sync_rejections_household ON sync_rejections (household_id);
CREATE INDEX ix_outbox_entity ON outbox (entity_type, entity_id);
```

Append its hash to `tests/Coo.Mobile.Infrastructure.Tests/migrations-manifest.txt` (it ships in 0.11.0):

```sh
echo "$(shasum -a 256 src/Mobile/Coo.Mobile.Infrastructure/Persistence/Migrations/0004-sync.sql | cut -d' ' -f1) 0004-sync.sql" \
  >> tests/Coo.Mobile.Infrastructure.Tests/migrations-manifest.txt
```

- [ ] **Step 4: The signal, the clock and the order**

`src/Mobile/Coo.Mobile.Application/Sync/LocalWrites.cs`:

```csharp
namespace Coo.Mobile.Application.Sync;

/// <summary>
/// Raised after a local write committed with its outbox row (Spec 011 §6.3: a local write triggers a debounced cycle).
/// The stores raise it; pulled records never do, because they leave no outbox row. Handlers must not throw.
/// </summary>
public sealed class LocalWrites
{
    public event EventHandler? Written;

    public void Notify() => Written?.Invoke(this, EventArgs.Empty);
}
```

Register it in `AddMobileApplication`: `services.AddSingleton<LocalWrites>();` (under a `// Sync (Spec 011)` comment).

In `Coo.Mobile.Application/CareLog/Ports.cs`, `IVersionClock` gains:

```csharp
    /// <summary>Advances past a stamp pulled from the server, so the next local stamp is higher (ADR 0002 §4).</summary>
    Task ObserveAsync(HlcStamp remote, CancellationToken cancellationToken);
```

`DeviceState`:

```csharp
    public async Task ObserveAsync(HlcStamp remote, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var clock = await LoadAsync(cancellationToken);
            var stamp = clock.Observe(remote, time.GetUtcNow().ToUnixTimeMilliseconds());
            await using var connection = await database.OpenAsync(cancellationToken);
            await connection.ExecuteAsync(
                "UPDATE device_state SET hlc_wall_ms = $wall, hlc_counter = $counter WHERE id = 1;",
                cancellationToken, null, ("$wall", stamp.WallClockMs), ("$counter", stamp.Counter));
        }
        finally
        {
            _gate.Release();
        }
    }
```

`CareLogStore`, `HouseholdStore`, `DuplicateDismissalStore`: add `LocalWrites writes` as the third primary-constructor
parameter, and call `writes.Notify();` right after each `await transaction.CommitAsync(cancellationToken);` (in
`CareLogStore.SaveAsync`, `CareLogStore.DeleteAsync`, `HouseholdStore.SaveSettingsAsync`, `HouseholdStore.SaveBabyAsync`,
`DuplicateDismissalStore.SaveAsync`).

`HouseholdStore.ListHouseholdsAsync` orders the active household first:

```sql
SELECT household_id, volume_unit, time_zone_id, hidden_kinds, version_wall_ms, version_counter, version_device, is_deleted
FROM household_settings
ORDER BY coalesce(household_id = (SELECT value FROM app_state WHERE key = 'active_household'), 0) DESC, household_id;
```

and `IHouseholdStore.ListHouseholdsAsync`'s summary says: "The active household (`app_state.active_household`, set by
sync) first, then by ID: callers that show one household take the first live one (Spec 011 ruling 7)."

In `Outbox.cs`, define `SyncedEntityType`'s four constants from `Coo.Contracts.Sync.SyncEntityTypes` (same strings), so
there is one list.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/Coo.Mobile.Infrastructure.Tests && dotnet test tests/Coo.Mobile.Application.Tests`
Expected: PASS (substitutes of `IVersionClock` in the Application tests need no change; if a hand-written fake
implements it, add a no-op `ObserveAsync`).

- [ ] **Step 6: Commit**

```bash
dotnet format Coo.Tests.slnf
git add src/Mobile tests/Coo.Mobile.Infrastructure.Tests tests/Coo.Mobile.Application.Tests
git commit -m "Phone: sync_cursors and sync_rejections (migration 0004), the active household first, a local-write signal, pulled stamps observed (Spec 011 §6)"
```

---
### Task 11: The phone's sync cycle, the consent gate and the pull rule (`Coo.Mobile.Application/Sync`)

**Files:**
- Create: `src/Mobile/Coo.Mobile.Application/Sync/Model.cs`, `Ports.cs`, `PullRule.cs`, `ConsentGate.cs`, `SyncCycle.cs`,
  `ConsentUseCases.cs`
- Test: `tests/Coo.Mobile.Application.Tests/SyncCycleTests.cs`, `ConsentGateTests.cs`, `PullRuleTests.cs` (create)

**Interfaces:**
- Consumes: `Account` and `ISessionStore` (Task 9), `IVersionClock.ObserveAsync` and `LocalWrites` (Task 10),
  `IAccountApi.GetMeAsync` → `AccountOverview.Consent` (Task 9), `IDeviceIdentity`, `CareDataConsent` and
  `SyncEntityTypes` (Task 1).
- Produces (Tasks 12–19 use these names):
  - Payloads: `RecordPayload`; `CareEntryPayload(string Kind, Guid? SubjectBabyId, string? SubjectPerson, bool IsSession,
    DateTimeOffset StartAt, DateTimeOffset? EndAt, string LoggedBy, string Source, string? SourcePromptVersion, string? Note,
    string DetailsJson)`; `BabyProfilePayload(string Name, IReadOnlyList<string> Nicknames, DateOnly? BirthDate, DateOnly? DueDate)`;
    `HouseholdSettingsPayload(string VolumeUnit, string TimeZoneId, IReadOnlyList<string> HiddenKinds)`;
    `DismissalPayload(Guid EntryA, Guid EntryB)`.
  - `OutgoingChange(Guid ChangeId, string EntityType, Guid EntityId, bool IsDelete, HlcStamp Version, long LastOutboxSeq, RecordPayload? Payload)`;
    `PushOutcome { Applied, Superseded, Rejected }`; `PushResult(Guid ChangeId, PushOutcome Outcome, string? Reason)`;
    `PulledRecord(string EntityType, Guid EntityId, HlcStamp Version, bool IsDeleted, RecordPayload Payload)`;
    `PulledPage(IReadOnlyList<PulledRecord> Records, long Cursor, bool HasMore, long? BootstrapHead)`;
    `SyncCursor(long Cursor, long? BootstrapHead)`; `SyncCounts(long Cursor, int Outbox, int Rejections)`;
    `SyncRejection(string EntityType, Guid EntityId, string Reason, DateTimeOffset RejectedAt)`;
    `MembershipStatus { Active, Revoked, Unknown }`; `ConsentState { Unknown, Given, Declined }`;
    `LocalBaby(BabyId Id, string Name)`; `LocalSummary(int EntryCount, IReadOnlyList<LocalBaby> Babies)`;
    `BringOverPlan(HouseholdId From, HouseholdId To, IReadOnlyDictionary<BabyId, BabyId?> Babies)` (null = add as new).
  - `SyncRefusal { ConsentRequired, NotAMember, MembershipRevoked, ClientTooOld, Malformed }`,
    `SyncRefusedException(SyncRefusal refusal, HouseholdId? household)`, `SyncUnavailableException`.
  - Ports `ISyncApi`, `IConsentApi`, `ISyncStore`, `IHouseholdMoves`, `ISyncState` (members below).
  - `PullRule.KeepsLocal(HlcStamp? pendingLocalVersion, HlcStamp incoming)`.
  - `ConsentGate(ISyncState state, IAccountApi accounts, IConsentApi api)`: `CheckAsync(UserId)`, `AgreeAsync(UserId, string appVersion, string language)`,
    `DeclineAsync(UserId)`, `ForgetAsync(UserId)`, `ResetDeclinedAsync(UserId)`.
  - `CycleOutcome { Synced, Failed, NotSignedIn, ConsentNeeded, ConsentDeclined, JoinPending, SoftLocked, Removed, ClientTooOld, Interrupted }`,
    `CycleReport(CycleOutcome Outcome, bool DataChanged = false, string? Error = null, HouseholdId? Household = null)`,
    `SyncCycle.RunAsync(CancellationToken)` → `CycleReport`, `SyncCycle.PushBatchSize = 200`.
  - `RecordConsent(ISessionStore, ConsentGate, ISyncControl).ExecuteAsync(string appVersion, string language, CancellationToken)` → `bool`;
    `DeclineConsent(ISessionStore, ConsentGate, ISyncControl).ExecuteAsync(CancellationToken)`;
    `ISyncControl { void RequestSync(); Task SyncNowAsync(CancellationToken); }` (implemented by `SyncEngine`, Task 12).

- [ ] **Step 1: Write the failing tests**

`tests/Coo.Mobile.Application.Tests/PullRuleTests.cs`:

```csharp
using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;
using Coo.Mobile.Application.Sync;

namespace Coo.Mobile.Application.Tests;

/// <summary>ADR 0002 §6: a pulled record replaces the local one unless a pending local change is newer.</summary>
public sealed class PullRuleTests
{
    private static readonly DeviceId Phone = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000d1"));

    [Theory]
    [InlineData(null, 5, false)]
    [InlineData(4L, 5, false)]
    [InlineData(5L, 5, false)]
    [InlineData(6L, 5, true)]
    public void KeepsLocal_PendingVersionAgainstIncoming_KeepsOnlyANewerPendingEdit(long? pending, long incoming, bool keeps)
    {
        // Act
        var result = PullRule.KeepsLocal(pending is { } p ? new HlcStamp(p, 0, Phone) : null, new HlcStamp(incoming, 0, Phone));

        // Assert
        result.Should().Be(keeps);
    }
}
```

`tests/Coo.Mobile.Application.Tests/ConsentGateTests.cs`:

```csharp
using Coo.Contracts.Sync;
using Coo.Domain.Common;
using Coo.Mobile.Application.Auth;
using Coo.Mobile.Application.Sync;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Coo.Mobile.Application.Tests;

/// <summary>Spec 011 §3: consent per account, cached on the phone, read from the server so a second phone does not ask.</summary>
public sealed class ConsentGateTests
{
    private static readonly UserId User = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000c1"));
    private static readonly AccountProfile Profile = new(User, "Andrzej", "andrzej@example.com");

    private readonly ISyncState _state = Substitute.For<ISyncState>();
    private readonly IAccountApi _accounts = Substitute.For<IAccountApi>();
    private readonly IConsentApi _api = Substitute.For<IConsentApi>();

    private ConsentGate Gate() => new(_state, _accounts, _api);

    [Fact]
    public async Task CheckAsync_CachedAsGiven_DoesNotAskTheServer()
    {
        // Arrange
        _state.GetConsentAsync(User, Arg.Any<CancellationToken>()).Returns(ConsentState.Given);

        // Act
        var state = await Gate().CheckAsync(User, CancellationToken.None);

        // Assert
        state.Should().Be(ConsentState.Given);
        await _accounts.DidNotReceiveWithAnyArgs().GetMeAsync(default);
    }

    [Fact]
    public async Task CheckAsync_UnknownHereButGivenOnTheServer_CachesItAsGiven()
    {
        // Arrange: the account's second phone (Spec 011 §3).
        _state.GetConsentAsync(User, Arg.Any<CancellationToken>()).Returns(ConsentState.Unknown);
        _accounts.GetMeAsync(Arg.Any<CancellationToken>()).Returns(
            new AccountOverview(Profile, null, null, [], new ConsentInfo(CareDataConsent.CurrentWording, DateTimeOffset.UnixEpoch)));

        // Act
        var state = await Gate().CheckAsync(User, CancellationToken.None);

        // Assert
        state.Should().Be(ConsentState.Given);
        await _state.Received(1).SetConsentAsync(User, ConsentState.Given, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckAsync_ServerUnreachable_StaysUnknown()
    {
        // Arrange
        _state.GetConsentAsync(User, Arg.Any<CancellationToken>()).Returns(ConsentState.Unknown);
        _accounts.GetMeAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new AuthUnavailableException("offline"));

        // Act
        var state = await Gate().CheckAsync(User, CancellationToken.None);

        // Assert
        state.Should().Be(ConsentState.Unknown);
    }

    [Fact]
    public async Task AgreeAsync_ServerRecordsIt_CachesGiven()
    {
        // Act
        var agreed = await Gate().AgreeAsync(User, "0.11.0", "pl", CancellationToken.None);

        // Assert
        agreed.Should().BeTrue();
        await _api.Received(1).RecordAsync("0.11.0", "pl", Arg.Any<CancellationToken>());
        await _state.Received(1).SetConsentAsync(User, ConsentState.Given, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AgreeAsync_Offline_ReturnsFalseAndCachesNothing()
    {
        // Arrange
        _api.RecordAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).ThrowsAsync(new SyncUnavailableException("offline"));

        // Act
        var agreed = await Gate().AgreeAsync(User, "0.11.0", "pl", CancellationToken.None);

        // Assert
        agreed.Should().BeFalse();
        await _state.DidNotReceiveWithAnyArgs().SetConsentAsync(default, default, default);
    }

    [Fact]
    public async Task ResetDeclinedAsync_Declined_BecomesUnknownSoTheSheetAsksAgain()
    {
        // Arrange
        _state.GetConsentAsync(User, Arg.Any<CancellationToken>()).Returns(ConsentState.Declined);

        // Act
        await Gate().ResetDeclinedAsync(User, CancellationToken.None);

        // Assert
        await _state.Received(1).SetConsentAsync(User, ConsentState.Unknown, Arg.Any<CancellationToken>());
    }
}
```

`tests/Coo.Mobile.Application.Tests/SyncCycleTests.cs`:

```csharp
using Coo.Contracts.Sync;
using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;
using Coo.Mobile.Application.Auth;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Application.Sync;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Coo.Mobile.Application.Tests;

/// <summary>Spec 011 §4–§6 and §10 (Mobile.Application row). Only the outbound ports are substitutes.</summary>
public sealed class SyncCycleTests : IDisposable
{
    internal static readonly UserId User = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000c1"));
    internal static readonly AccountProfile Profile = new(User, "Andrzej", "andrzej@example.com");
    internal static readonly HouseholdId Household = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a1"));
    internal static readonly HouseholdId Provisional = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a2"));
    internal static readonly DeviceId Phone = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000d1"));
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    private readonly MemorySessions _sessions = new();
    private readonly Account _account;
    private readonly ISyncState _state = Substitute.For<ISyncState>();
    private readonly IAccountApi _accounts = Substitute.For<IAccountApi>();
    private readonly IConsentApi _consentApi = Substitute.For<IConsentApi>();
    private readonly ISyncStore _store = Substitute.For<ISyncStore>();
    private readonly IHouseholdMoves _moves = Substitute.For<IHouseholdMoves>();
    private readonly ISyncApi _api = Substitute.For<ISyncApi>();
    private readonly IVersionClock _clock = Substitute.For<IVersionClock>();
    private readonly IDeviceIdentity _device = Substitute.For<IDeviceIdentity>();
    private readonly FakeTimeProvider _time = new(Now);

    public SyncCycleTests()
    {
        _account = new Account(_sessions);
        _state.GetConsentAsync(User, Arg.Any<CancellationToken>()).Returns(ConsentState.Given);
        _moves.ActiveHouseholdAsync(Arg.Any<CancellationToken>()).Returns(Household);
        _device.GetAsync(Arg.Any<CancellationToken>()).Returns(Phone);
        _store.NextBatchAsync(Household, Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);
        _store.GetCursorAsync(Household, Arg.Any<CancellationToken>()).Returns(new SyncCursor(0, null));
        _api.PullAsync(Household, Arg.Any<long>(), Arg.Any<long?>(), Arg.Any<CancellationToken>()).Returns(new PulledPage([], 0, false, 0));
    }

    public void Dispose() => _account.Dispose();

    private SyncCycle Cycle() => new(
        _account, _sessions, new ConsentGate(_state, _accounts, _consentApi), _store, _moves, _state, _api, _clock, _device, _time);

    private Task SignInAsync(bool joinPending = false) => _account.SignedInAsync(
        new StoredSession("access", Now.AddMinutes(15), "refresh", Profile, Household, joinPending), CancellationToken.None);

    internal static OutgoingChange Change(int n) => new(
        Guid.Parse("7a1c0000-0000-7000-8000-" + n.ToString("D12", System.Globalization.CultureInfo.InvariantCulture)), SyncEntityTypes.CareEntry,
        Guid.Parse("7a1c0000-0000-7000-9000-" + n.ToString("D12", System.Globalization.CultureInfo.InvariantCulture)), false,
        new HlcStamp(1_790_000_000_000 + n, 0, Phone), n, null);

    [Fact]
    public async Task RunAsync_SignedOut_TouchesNothing()
    {
        // Act
        var report = await Cycle().RunAsync(CancellationToken.None);

        // Assert
        report.Outcome.Should().Be(CycleOutcome.NotSignedIn);
        await _api.DidNotReceiveWithAnyArgs().PushAsync(default, default, default!, default);
        await _api.DidNotReceiveWithAnyArgs().PullAsync(default, default, default, default);
    }

    [Fact]
    public async Task RunAsync_SignedInWithoutConsent_NeitherPushesNorPulls()
    {
        // Arrange: upgraded from 0.10.0 while signed in (Review Focus 1), and the server has no consent either.
        await SignInAsync();
        _state.GetConsentAsync(User, Arg.Any<CancellationToken>()).Returns(ConsentState.Unknown);
        _accounts.GetMeAsync(Arg.Any<CancellationToken>()).Returns(new AccountOverview(Profile, Household, "owner", []));

        // Act
        var report = await Cycle().RunAsync(CancellationToken.None);

        // Assert
        report.Outcome.Should().Be(CycleOutcome.ConsentNeeded);
        await _store.DidNotReceiveWithAnyArgs().PrepareForPushAsync(default, default, default);
        await _api.DidNotReceiveWithAnyArgs().PushAsync(default, default, default!, default);
        await _api.DidNotReceiveWithAnyArgs().PullAsync(default, default, default, default);
    }

    [Fact]
    public async Task RunAsync_ConsentDeclined_NeitherPushesNorPulls()
    {
        // Arrange
        await SignInAsync();
        _state.GetConsentAsync(User, Arg.Any<CancellationToken>()).Returns(ConsentState.Declined);

        // Act
        var report = await Cycle().RunAsync(CancellationToken.None);

        // Assert
        report.Outcome.Should().Be(CycleOutcome.ConsentDeclined);
        await _api.DidNotReceiveWithAnyArgs().PushAsync(default, default, default!, default);
        await _api.DidNotReceiveWithAnyArgs().PullAsync(default, default, default, default);
    }

    [Fact]
    public async Task RunAsync_JoinPending_PullsTheAccountsHouseholdAndPushesNothing()
    {
        // Arrange: this phone shows its own household; the account's is pulled beside it (ruling 8).
        await SignInAsync(joinPending: true);
        _moves.ActiveHouseholdAsync(Arg.Any<CancellationToken>()).Returns(Provisional);

        // Act
        var report = await Cycle().RunAsync(CancellationToken.None);

        // Assert
        report.Outcome.Should().Be(CycleOutcome.JoinPending);
        await _api.Received(1).PullAsync(Household, 0, null, Arg.Any<CancellationToken>());
        await _store.DidNotReceiveWithAnyArgs().PrepareForPushAsync(default, default, default);
        await _api.DidNotReceiveWithAnyArgs().PushAsync(default, default, default!, default);
    }

    [Fact]
    public async Task RunAsync_Created_MovesEverythingBeforeTheFirstPush()
    {
        // Arrange: the account's household is new and empty; this phone still shows its provisional one (ruling 9).
        await SignInAsync();
        _moves.ActiveHouseholdAsync(Arg.Any<CancellationToken>()).Returns(Provisional);

        // Act
        var report = await Cycle().RunAsync(CancellationToken.None);

        // Assert
        report.Outcome.Should().Be(CycleOutcome.Synced);
        report.DataChanged.Should().BeTrue();
        Received.InOrder(() =>
        {
            _moves.MoveAllAsync(Provisional, Household, User, Arg.Any<CancellationToken>());
            _store.PrepareForPushAsync(Household, User, Arg.Any<CancellationToken>());
            _store.NextBatchAsync(Household, SyncCycle.PushBatchSize, Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task RunAsync_Changes_PushesThenAppliesTheResultsThenPulls()
    {
        // Arrange
        await SignInAsync();
        var batch = new[] { Change(1), Change(2) };
        var results = new[] { new PushResult(batch[0].ChangeId, PushOutcome.Applied, null), new PushResult(batch[1].ChangeId, PushOutcome.Rejected, "time.end-not-after-start") };
        _store.NextBatchAsync(Household, SyncCycle.PushBatchSize, Arg.Any<CancellationToken>()).Returns(batch, []);
        _api.PushAsync(Household, Phone, batch, Arg.Any<CancellationToken>()).Returns(results);

        // Act
        var report = await Cycle().RunAsync(CancellationToken.None);

        // Assert
        report.Outcome.Should().Be(CycleOutcome.Synced);
        Received.InOrder(() =>
        {
            _store.PrepareForPushAsync(Household, User, Arg.Any<CancellationToken>());
            _api.PushAsync(Household, Phone, batch, Arg.Any<CancellationToken>());
            _store.ApplyResultsAsync(Household, batch, results, Now, Arg.Any<CancellationToken>());
            _api.PullAsync(Household, 0, null, Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task RunAsync_PushFailsOnTheWire_KeepsTheOutboxAndFails()
    {
        // Arrange: "nothing was decided" (ADR 0002 §5): no result is applied, so no outbox row goes.
        await SignInAsync();
        _store.NextBatchAsync(Household, SyncCycle.PushBatchSize, Arg.Any<CancellationToken>()).Returns([Change(1)]);
        _api.PushAsync(default, default, default!, default).ReturnsForAnyArgs(Task.FromException<IReadOnlyList<PushResult>>(new SyncUnavailableException("offline")));

        // Act
        var report = await Cycle().RunAsync(CancellationToken.None);

        // Assert
        report.Outcome.Should().Be(CycleOutcome.Failed);
        await _store.DidNotReceiveWithAnyArgs().ApplyResultsAsync(default, default!, default!, default, default);
        await _api.DidNotReceiveWithAnyArgs().PullAsync(default, default, default, default);
    }

    [Fact]
    public async Task RunAsync_ServerAnsweredOnlySomeChanges_AppliesThoseAndFails()
    {
        // Arrange
        await SignInAsync();
        var batch = new[] { Change(1), Change(2) };
        var partial = new[] { new PushResult(batch[0].ChangeId, PushOutcome.Applied, null) };
        _store.NextBatchAsync(Household, SyncCycle.PushBatchSize, Arg.Any<CancellationToken>()).Returns(batch);
        _api.PushAsync(Household, Phone, batch, Arg.Any<CancellationToken>()).Returns(partial);

        // Act
        var report = await Cycle().RunAsync(CancellationToken.None);

        // Assert
        report.Outcome.Should().Be(CycleOutcome.Failed);
        await _store.Received(1).ApplyResultsAsync(Household, batch, partial, Now, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_SignedOutBetweenBatches_StopsBeforeTheNextBatch()
    {
        // Arrange: a full batch, then the person signs out while it is in flight (Review Focus 5).
        await SignInAsync();
        var batch = Enumerable.Range(1, SyncCycle.PushBatchSize).Select(Change).ToArray();
        _store.NextBatchAsync(Household, SyncCycle.PushBatchSize, Arg.Any<CancellationToken>()).Returns(batch);
        _api.PushAsync(Household, Phone, batch, Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            await _account.SignedOutAsync(sessionExpired: false, CancellationToken.None);
            return (IReadOnlyList<PushResult>)[.. batch.Select(c => new PushResult(c.ChangeId, PushOutcome.Applied, null))];
        });

        // Act
        var report = await Cycle().RunAsync(CancellationToken.None);

        // Assert
        report.Outcome.Should().Be(CycleOutcome.Interrupted);
        await _store.Received(1).NextBatchAsync(Household, SyncCycle.PushBatchSize, Arg.Any<CancellationToken>());
        await _api.DidNotReceiveWithAnyArgs().PullAsync(default, default, default, default);
    }

    [Fact]
    public async Task RunAsync_ABare403_FailsAndDeletesOrLocksNothing()
    {
        // Arrange: a proxy's 403 reaches the cycle as "unavailable" (ADR 0002 §10 step 1).
        await SignInAsync();
        _api.PullAsync(Household, Arg.Any<long>(), Arg.Any<long?>(), Arg.Any<CancellationToken>()).ThrowsAsync(new SyncUnavailableException("403"));

        // Act
        var report = await Cycle().RunAsync(CancellationToken.None);

        // Assert
        report.Outcome.Should().Be(CycleOutcome.Failed);
        await _state.DidNotReceiveWithAnyArgs().SetSoftLockAsync(default, default, default);
        await _moves.DidNotReceiveWithAnyArgs().DeleteHouseholdAsync(default, default);
    }

    [Fact]
    public async Task RunAsync_MembershipRevokedForThisHousehold_SoftLocksAndDeletesNothing()
    {
        // Arrange
        await SignInAsync();
        _api.PullAsync(Household, Arg.Any<long>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new SyncRefusedException(SyncRefusal.MembershipRevoked, Household));

        // Act
        var report = await Cycle().RunAsync(CancellationToken.None);

        // Assert
        report.Outcome.Should().Be(CycleOutcome.SoftLocked);
        await _state.Received(1).SetSoftLockAsync(Household, true, Arg.Any<CancellationToken>());
        await _moves.DidNotReceiveWithAnyArgs().DeleteHouseholdAsync(default, default);
    }

    [Fact]
    public async Task RunAsync_MembershipRevokedNamingAnotherHousehold_IsAFailedCycle()
    {
        // Arrange
        await SignInAsync();
        _api.PullAsync(Household, Arg.Any<long>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new SyncRefusedException(SyncRefusal.MembershipRevoked, Provisional));

        // Act
        var report = await Cycle().RunAsync(CancellationToken.None);

        // Assert
        report.Outcome.Should().Be(CycleOutcome.Failed);
        await _state.DidNotReceiveWithAnyArgs().SetSoftLockAsync(default, default, default);
    }

    [Theory]
    [InlineData(MembershipStatus.Revoked, CycleOutcome.Removed)]
    [InlineData(MembershipStatus.Unknown, CycleOutcome.SoftLocked)]
    public async Task RunAsync_SoftLocked_ConfirmsButNeverDeletes(MembershipStatus status, CycleOutcome expected)
    {
        // Arrange: deletion waits for the person (ruling 13).
        await SignInAsync();
        _state.IsSoftLockedAsync(Household, Arg.Any<CancellationToken>()).Returns(true);
        _api.GetMembershipAsync(Household, Arg.Any<CancellationToken>()).Returns(status);

        // Act
        var report = await Cycle().RunAsync(CancellationToken.None);

        // Assert
        report.Outcome.Should().Be(expected);
        await _moves.DidNotReceiveWithAnyArgs().DeleteHouseholdAsync(default, default);
        await _api.DidNotReceiveWithAnyArgs().PullAsync(default, default, default, default);
    }

    [Fact]
    public async Task RunAsync_SoftLockedButActiveAgain_LiftsTheLock()
    {
        // Arrange
        await SignInAsync();
        _state.IsSoftLockedAsync(Household, Arg.Any<CancellationToken>()).Returns(true);
        _api.GetMembershipAsync(Household, Arg.Any<CancellationToken>()).Returns(MembershipStatus.Active);

        // Act
        await Cycle().RunAsync(CancellationToken.None);

        // Assert
        await _state.Received(1).SetSoftLockAsync(Household, false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_ServerSaysConsentRequired_ForgetsTheCachedConsent()
    {
        // Arrange: a new wording on the server (Spec 011 §3).
        await SignInAsync();
        _api.PullAsync(Household, Arg.Any<long>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new SyncRefusedException(SyncRefusal.ConsentRequired, null));

        // Act
        var report = await Cycle().RunAsync(CancellationToken.None);

        // Assert
        report.Outcome.Should().Be(CycleOutcome.ConsentNeeded);
        await _state.Received(1).SetConsentAsync(User, ConsentState.Unknown, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_ClientTooOld_ReportsItAndKeepsTheOutbox()
    {
        // Arrange
        await SignInAsync();
        _store.NextBatchAsync(Household, SyncCycle.PushBatchSize, Arg.Any<CancellationToken>()).Returns([Change(1)]);
        _api.PushAsync(default, default, default!, default).ReturnsForAnyArgs(
            Task.FromException<IReadOnlyList<PushResult>>(new SyncRefusedException(SyncRefusal.ClientTooOld, null)));

        // Act
        var report = await Cycle().RunAsync(CancellationToken.None);

        // Assert
        report.Outcome.Should().Be(CycleOutcome.ClientTooOld);
        await _store.DidNotReceiveWithAnyArgs().ApplyResultsAsync(default, default!, default!, default, default);
    }

    [Fact]
    public async Task RunAsync_PulledPages_AppliesEachAndObservesTheNewestStamp()
    {
        // Arrange
        await SignInAsync();
        var older = new HlcStamp(10, 0, Phone);
        var newest = new HlcStamp(20, 0, Phone);
        PulledRecord Record(HlcStamp v) => new(SyncEntityTypes.DuplicateDismissal, Guid.Parse("7a1c0000-0000-7000-9000-000000000001"), v, false,
            new DismissalPayload(Guid.Parse("7a1c0000-0000-7000-9000-000000000002"), Guid.Parse("7a1c0000-0000-7000-9000-000000000003")));
        var first = new PulledPage([Record(older)], 1, true, 2);
        var second = new PulledPage([Record(newest)], 2, false, 2);
        _store.GetCursorAsync(Household, Arg.Any<CancellationToken>()).Returns(new SyncCursor(0, null), new SyncCursor(1, 2));
        _api.PullAsync(Household, 0, null, Arg.Any<CancellationToken>()).Returns(first);
        _api.PullAsync(Household, 1, 2, Arg.Any<CancellationToken>()).Returns(second);

        // Act
        var report = await Cycle().RunAsync(CancellationToken.None);

        // Assert
        report.DataChanged.Should().BeTrue();
        await _store.Received(1).ApplyPageAsync(Household, first, Arg.Any<CancellationToken>());
        await _store.Received(1).ApplyPageAsync(Household, second, Arg.Any<CancellationToken>());
        await _clock.Received(1).ObserveAsync(newest, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_PageSaysMoreWithoutMovingTheCursor_Fails()
    {
        // Arrange: a server bug must not spin the phone forever (ruling 16).
        await SignInAsync();
        _store.GetCursorAsync(Household, Arg.Any<CancellationToken>()).Returns(new SyncCursor(5, null));
        _api.PullAsync(Household, 5, null, Arg.Any<CancellationToken>()).Returns(new PulledPage([], 5, true, null));

        // Act
        var report = await Cycle().RunAsync(CancellationToken.None);

        // Assert
        report.Outcome.Should().Be(CycleOutcome.Failed);
        await _store.DidNotReceiveWithAnyArgs().ApplyPageAsync(default, default!, default);
    }

    [Fact]
    public async Task RunAsync_JoinPendingButTheBringOverAlreadyCommitted_CompletesTheJoin()
    {
        // Arrange: the app stopped between the bring-over's transaction and the session update.
        await SignInAsync(joinPending: true);
        _moves.ActiveHouseholdAsync(Arg.Any<CancellationToken>()).Returns(Household);

        // Act
        await Cycle().RunAsync(CancellationToken.None);

        // Assert
        _account.State.Should().Be(new SignedIn(Profile));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Coo.Mobile.Application.Tests --filter "FullyQualifiedName~SyncCycleTests|FullyQualifiedName~ConsentGateTests|FullyQualifiedName~PullRuleTests"`
Expected: FAIL to compile (namespace `Coo.Mobile.Application.Sync` has no `SyncCycle`, …). Paste the first error.
(Add `<PackageReference Include="Microsoft.Extensions.TimeProvider.Testing" />` to
`tests/Coo.Mobile.Application.Tests/Coo.Mobile.Application.Tests.csproj` if the compiler cannot find `FakeTimeProvider`.)

- [ ] **Step 3: The model and the ports**

`src/Mobile/Coo.Mobile.Application/Sync/Model.cs`:

```csharp
using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;

namespace Coo.Mobile.Application.Sync;

// A record's state as it travels (ADR 0002 §3). Rows, not Domain objects: a pulled entry of a kind this build does not know
// must be stored as received (ADR 0010 §3), so nothing here is validated. Details stay a JSON string.

public abstract record RecordPayload
{
    private protected RecordPayload()
    {
    }
}

public sealed record CareEntryPayload(
    string Kind, Guid? SubjectBabyId, string? SubjectPerson, bool IsSession, DateTimeOffset StartAt, DateTimeOffset? EndAt,
    string LoggedBy, string Source, string? SourcePromptVersion, string? Note, string DetailsJson) : RecordPayload;

public sealed record BabyProfilePayload(string Name, IReadOnlyList<string> Nicknames, DateOnly? BirthDate, DateOnly? DueDate) : RecordPayload;

public sealed record HouseholdSettingsPayload(string VolumeUnit, string TimeZoneId, IReadOnlyList<string> HiddenKinds) : RecordPayload;

public sealed record DismissalPayload(Guid EntryA, Guid EntryB) : RecordPayload;

/// <summary>One record's change: its current state under its newest outbox row's change ID (Spec 011 §6.1).</summary>
/// <param name="LastOutboxSeq">The newest outbox row this change answers for; a final result deletes the record's rows up to it.</param>
public sealed record OutgoingChange(
    Guid ChangeId, string EntityType, Guid EntityId, bool IsDelete, HlcStamp Version, long LastOutboxSeq, RecordPayload? Payload);

public enum PushOutcome
{
    Applied,
    Superseded,
    Rejected,
}

public sealed record PushResult(Guid ChangeId, PushOutcome Outcome, string? Reason);

public sealed record PulledRecord(string EntityType, Guid EntityId, HlcStamp Version, bool IsDeleted, RecordPayload Payload);

public sealed record PulledPage(IReadOnlyList<PulledRecord> Records, long Cursor, bool HasMore, long? BootstrapHead);

/// <summary>Where a household's pull stands; <see cref="BootstrapHead"/> is sent with every page of a bootstrap.</summary>
public sealed record SyncCursor(long Cursor, long? BootstrapHead);

/// <summary>For the debug screen (Spec 011 §11).</summary>
public sealed record SyncCounts(long Cursor, int Outbox, int Rejections);

public sealed record SyncRejection(string EntityType, Guid EntityId, string Reason, DateTimeOffset RejectedAt);

public enum MembershipStatus
{
    Active,
    Revoked,

    /// <summary>Anything that is not a clear answer: keep the soft-lock and ask again later (ADR 0002 §10).</summary>
    Unknown,
}

public enum ConsentState
{
    Unknown,
    Given,
    Declined,
}

public sealed record LocalBaby(BabyId Id, string Name);

public sealed record LocalSummary(int EntryCount, IReadOnlyList<LocalBaby> Babies);

/// <summary>Bring my entries over (Spec 011 §4): each local baby to a household baby, or null for "Add as a new baby".</summary>
public sealed record BringOverPlan(HouseholdId From, HouseholdId To, IReadOnlyDictionary<BabyId, BabyId?> Babies);

public enum CycleOutcome
{
    Synced,
    Failed,
    NotSignedIn,
    ConsentNeeded,
    ConsentDeclined,
    JoinPending,
    SoftLocked,
    Removed,
    ClientTooOld,
    Interrupted,
}

/// <param name="DataChanged">Local data changed (pulled records, a move): pages refresh and the duplicate check reruns.</param>
/// <param name="Error">The failure's type or refusal, for the debug screen; never shown to the person.</param>
/// <param name="Household">For <see cref="CycleOutcome.Removed"/>: the household the person was removed from.</param>
public sealed record CycleReport(CycleOutcome Outcome, bool DataChanged = false, string? Error = null, HouseholdId? Household = null);
```

`src/Mobile/Coo.Mobile.Application/Sync/Ports.cs`:

```csharp
using Coo.Domain.Common;

namespace Coo.Mobile.Application.Sync;

/// <summary><c>/api/sync</c> and <c>/api/households/{id}/membership</c>, through the refreshing handler (Spec 011 §7.1).</summary>
public interface ISyncApi
{
    /// <summary>Throws <see cref="SyncUnavailableException"/> when nothing was decided, <see cref="SyncRefusedException"/> for a refusal.</summary>
    Task<IReadOnlyList<PushResult>> PushAsync(HouseholdId household, DeviceId device, IReadOnlyList<OutgoingChange> changes, CancellationToken cancellationToken);

    Task<PulledPage> PullAsync(HouseholdId household, long cursor, long? bootstrapHead, CancellationToken cancellationToken);

    /// <summary>Never throws: anything but a clear answer is <see cref="MembershipStatus.Unknown"/>.</summary>
    Task<MembershipStatus> GetMembershipAsync(HouseholdId household, CancellationToken cancellationToken);
}

/// <summary><c>POST /api/consents</c> for the current wording (Spec 011 §3).</summary>
public interface IConsentApi
{
    Task RecordAsync(string appVersion, string language, CancellationToken cancellationToken);
}

/// <summary>The outbox, the pulled records and the cursors on this phone (Spec 011 §6).</summary>
public interface ISyncStore
{
    /// <summary>In one transaction, <c>this-device</c> becomes <c>user:&lt;id&gt;</c> in the household's entries (Spec 011 §5).</summary>
    Task PrepareForPushAsync(HouseholdId household, UserId user, CancellationToken cancellationToken);

    /// <summary>Oldest first, at most <paramref name="max"/> records of this household, one change each.</summary>
    Task<IReadOnlyList<OutgoingChange>> NextBatchAsync(HouseholdId household, int max, CancellationToken cancellationToken);

    /// <summary>
    /// In one transaction: each final result deletes its record's outbox rows up to the change; <c>rejected</c> keeps the
    /// record and lists it; <c>applied</c> and <c>superseded</c> clear an earlier rejection. Changes without a result stay.
    /// </summary>
    Task ApplyResultsAsync(
        HouseholdId household, IReadOnlyList<OutgoingChange> changes, IReadOnlyList<PushResult> results, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>The household's cursor. With none yet, a household this phone already holds records of starts with head 0 (ruling 20).</summary>
    Task<SyncCursor> GetCursorAsync(HouseholdId household, CancellationToken cancellationToken);

    /// <summary>In one transaction: each record (unless a newer local change is pending, <see cref="PullRule"/>), and the new cursor.</summary>
    Task ApplyPageAsync(HouseholdId household, PulledPage page, CancellationToken cancellationToken);

    Task<SyncCounts> CountsAsync(HouseholdId household, CancellationToken cancellationToken);

    Task<IReadOnlyList<SyncRejection>> ListRejectionsAsync(HouseholdId household, CancellationToken cancellationToken);
}

/// <summary>Moving records between households on this phone (Spec 011 §4), each in one SQLite transaction.</summary>
public interface IHouseholdMoves
{
    /// <summary>The household the UI shows; names the first live one as active when none is named yet.</summary>
    Task<HouseholdId> ActiveHouseholdAsync(CancellationToken cancellationToken);

    /// <summary><c>created</c>: every record moves to the account's empty household, which becomes active.</summary>
    Task MoveAllAsync(HouseholdId from, HouseholdId to, UserId user, CancellationToken cancellationToken);

    Task<LocalSummary> SummaryAsync(HouseholdId household, CancellationToken cancellationToken);

    /// <summary>Bring my entries over. The account's household becomes active.</summary>
    Task BringOverAsync(BringOverPlan plan, UserId user, CancellationToken cancellationToken);

    /// <summary>Only with an empty outbox: clears the household's records (not its settings) and its cursor. False otherwise.</summary>
    Task<bool> FreshBootstrapAsync(HouseholdId household, CancellationToken cancellationToken);

    /// <summary>After a confirmed removal (ADR 0002 §10): every record, outbox row, rejection and cursor of the household.</summary>
    Task DeleteHouseholdAsync(HouseholdId household, CancellationToken cancellationToken);
}

/// <summary>Small sync facts in <c>app_state</c>.</summary>
public interface ISyncState
{
    Task<ConsentState> GetConsentAsync(UserId user, CancellationToken cancellationToken);

    Task SetConsentAsync(UserId user, ConsentState state, CancellationToken cancellationToken);

    Task<bool> IsSoftLockedAsync(HouseholdId household, CancellationToken cancellationToken);

    Task SetSoftLockAsync(HouseholdId household, bool locked, CancellationToken cancellationToken);
}

/// <summary>What the UI may ask of sync: a cycle soon, or one now that it can wait for.</summary>
public interface ISyncControl
{
    void RequestSync();

    Task SyncNowAsync(CancellationToken cancellationToken);
}

public enum SyncRefusal
{
    ConsentRequired,
    NotAMember,
    MembershipRevoked,
    ClientTooOld,
    Malformed,
}

/// <summary>The server answered and refused the request with one of its codes (Spec 011 §7.1).</summary>
public sealed class SyncRefusedException : Exception
{
    public SyncRefusedException()
    {
    }

    public SyncRefusedException(string message)
        : base(message)
    {
    }

    public SyncRefusedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public SyncRefusedException(SyncRefusal refusal, HouseholdId? household)
        : base(refusal.ToString())
    {
        Refusal = refusal;
        Household = household;
    }

    public SyncRefusal Refusal { get; }

    /// <summary>For <see cref="SyncRefusal.MembershipRevoked"/>: the household the problem body named.</summary>
    public HouseholdId? Household { get; }
}

/// <summary>Nothing was decided: no network, a timeout, 408, 429, 5xx, an unreadable body, any other refusal.</summary>
public sealed class SyncUnavailableException : Exception
{
    public SyncUnavailableException()
    {
    }

    public SyncUnavailableException(string message)
        : base(message)
    {
    }

    public SyncUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
```

`src/Mobile/Coo.Mobile.Application/Sync/PullRule.cs`:

```csharp
using Coo.Domain.Common.Versioning;

namespace Coo.Mobile.Application.Sync;

/// <summary>
/// ADR 0002 §6 on the phone: a pulled record replaces the local one unless the local one has a pending change with a
/// higher version; the next push lets the server settle it with the same rule.
/// </summary>
public static class PullRule
{
    /// <param name="pendingLocalVersion">The local record's version when it has an outbox row; null when nothing is pending.</param>
    public static bool KeepsLocal(HlcStamp? pendingLocalVersion, HlcStamp incoming) =>
        pendingLocalVersion is { } local && local > incoming;
}
```

- [ ] **Step 4: The consent gate and its use cases**

`src/Mobile/Coo.Mobile.Application/Sync/ConsentGate.cs`:

```csharp
using Coo.Contracts.Sync;
using Coo.Domain.Common;
using Coo.Mobile.Application.Auth;

namespace Coo.Mobile.Application.Sync;

/// <summary>
/// The consent of Spec 011 §3, per account: cached on the phone, recorded on the server, read back from <c>/api/me</c> so
/// the account's second phone does not ask again. Without it the engine neither pushes nor pulls (Q8).
/// </summary>
public sealed class ConsentGate(ISyncState state, IAccountApi accounts, IConsentApi api)
{
    public async Task<ConsentState> CheckAsync(UserId user, CancellationToken cancellationToken)
    {
        var cached = await state.GetConsentAsync(user, cancellationToken);
        if (cached != ConsentState.Unknown)
            return cached;

        try
        {
            var me = await accounts.GetMeAsync(cancellationToken);
            if (me.Consent?.Wording != CareDataConsent.CurrentWording)
                return ConsentState.Unknown;
        }
        catch (Exception e) when (e is AuthUnavailableException or AuthRejectedException)
        {
            return ConsentState.Unknown;
        }

        await state.SetConsentAsync(user, ConsentState.Given, cancellationToken);
        return ConsentState.Given;
    }

    /// <summary>Agree and share. False when the server could not record it (offline): nothing is cached then.</summary>
    public async Task<bool> AgreeAsync(UserId user, string appVersion, string language, CancellationToken cancellationToken)
    {
        try
        {
            await api.RecordAsync(appVersion, language, cancellationToken);
        }
        catch (Exception e) when (e is SyncUnavailableException or SyncRefusedException or AuthUnavailableException or AuthRejectedException)
        {
            return false;
        }

        await state.SetConsentAsync(user, ConsentState.Given, cancellationToken);
        return true;
    }

    /// <summary>Not now: remembered until the next sign-in (ruling 10).</summary>
    public Task DeclineAsync(UserId user, CancellationToken cancellationToken) => state.SetConsentAsync(user, ConsentState.Declined, cancellationToken);

    /// <summary>The server asked again (a new wording): the cached consent no longer counts.</summary>
    public Task ForgetAsync(UserId user, CancellationToken cancellationToken) => state.SetConsentAsync(user, ConsentState.Unknown, cancellationToken);

    /// <summary>A new sign-in asks again after a "Not now"; a given consent stays.</summary>
    public async Task ResetDeclinedAsync(UserId user, CancellationToken cancellationToken)
    {
        if (await state.GetConsentAsync(user, cancellationToken) == ConsentState.Declined)
            await state.SetConsentAsync(user, ConsentState.Unknown, cancellationToken);
    }
}
```

`src/Mobile/Coo.Mobile.Application/Sync/ConsentUseCases.cs`:

```csharp
using Coo.Mobile.Application.Auth;

namespace Coo.Mobile.Application.Sync;

/// <summary>Agree and share (Spec 011 §3): recorded on the server, then a cycle. False when it could not be recorded.</summary>
public sealed class RecordConsent(ISessionStore sessions, ConsentGate consent, ISyncControl sync)
{
    public async Task<bool> ExecuteAsync(string appVersion, string language, CancellationToken cancellationToken)
    {
        if (await sessions.GetAsync(cancellationToken) is not { } session)
            return false;
        if (!await consent.AgreeAsync(session.Profile.UserId, appVersion, language, cancellationToken))
            return false;
        sync.RequestSync();
        return true;
    }
}

/// <summary>Not now, or Back: nothing is sent, the person stays signed in, the sync line offers Turn on sharing.</summary>
public sealed class DeclineConsent(ISessionStore sessions, ConsentGate consent, ISyncControl sync)
{
    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        if (await sessions.GetAsync(cancellationToken) is not { } session)
            return;
        await consent.DeclineAsync(session.Profile.UserId, cancellationToken);
        sync.RequestSync();
    }
}
```

- [ ] **Step 5: The cycle**

`src/Mobile/Coo.Mobile.Application/Sync/SyncCycle.cs`:

```csharp
using Coo.Domain.Common;
using Coo.Mobile.Application.Auth;
using Coo.Mobile.Application.CareLog;

namespace Coo.Mobile.Application.Sync;

/// <summary>
/// One sync cycle (Spec 011 §6.3, ADR 0002 §7): push, then pull. It captures the account's generation at the start and
/// stops at its next step once it has moved (§8.1). It never runs signed out or without consent; join-pending, it only
/// pulls the account's household (ruling 8); for a soft-locked household it only asks about the membership (§6.5).
/// It throws only for programming errors; network and server answers become a <see cref="CycleReport"/>.
/// </summary>
public sealed class SyncCycle(
    Account account, ISessionStore sessions, ConsentGate consent, ISyncStore store, IHouseholdMoves moves, ISyncState state,
    ISyncApi api, IVersionClock clock, IDeviceIdentity device, TimeProvider time)
{
    public const int PushBatchSize = 200;

    public async Task<CycleReport> RunAsync(CancellationToken cancellationToken)
    {
        var generation = account.Generation;
        var session = await sessions.GetAsync(cancellationToken);
        if (session is null || account.State is SignedOut)
            return new CycleReport(CycleOutcome.NotSignedIn);

        var user = session.Profile.UserId;
        switch (await consent.CheckAsync(user, cancellationToken))
        {
            case ConsentState.Declined:
                return new CycleReport(CycleOutcome.ConsentDeclined);
            case ConsentState.Unknown:
                return new CycleReport(CycleOutcome.ConsentNeeded);
        }

        var household = session.AccountHousehold;
        var local = await moves.ActiveHouseholdAsync(cancellationToken);
        var changed = false;
        try
        {
            if (session.JoinPending)
            {
                if (local == household)
                {
                    // The bring-over committed, but the app stopped before the session said so.
                    await account.TryCompleteJoinAsync(generation, cancellationToken);
                    return new CycleReport(CycleOutcome.Interrupted);
                }

                var pulled = await PullAllAsync(household, generation, cancellationToken);
                return new CycleReport(pulled.Interrupted ? CycleOutcome.Interrupted : CycleOutcome.JoinPending, pulled.Changed);
            }

            if (await state.IsSoftLockedAsync(household, cancellationToken))
                return await CheckMembershipAsync(household, cancellationToken);

            if (local != household)
            {
                // `created` (Spec 011 §4): the account's household is new and empty; everything here moves into it.
                await moves.MoveAllAsync(local, household, user, cancellationToken);
                changed = true;
            }

            if (Moved(generation))
                return new CycleReport(CycleOutcome.Interrupted, changed);

            await store.PrepareForPushAsync(household, user, cancellationToken);
            var deviceId = await device.GetAsync(cancellationToken);
            while (true)
            {
                if (Moved(generation))
                    return new CycleReport(CycleOutcome.Interrupted, changed);

                var batch = await store.NextBatchAsync(household, PushBatchSize, cancellationToken);
                if (batch.Count == 0)
                    break;

                var results = await api.PushAsync(household, deviceId, batch, cancellationToken);
                await store.ApplyResultsAsync(household, batch, results, time.GetUtcNow(), cancellationToken);

                // A change the server did not answer stays in the outbox; pushing again now would send it again at once.
                var answered = results.Select(r => r.ChangeId).ToHashSet();
                if (batch.Any(c => !answered.Contains(c.ChangeId)))
                    return new CycleReport(CycleOutcome.Failed, changed, "undecided");
                if (batch.Count < PushBatchSize)
                    break;
            }

            var pull = await PullAllAsync(household, generation, cancellationToken);
            return new CycleReport(pull.Interrupted ? CycleOutcome.Interrupted : CycleOutcome.Synced, changed || pull.Changed);
        }
        catch (SyncRefusedException e) when (e.Refusal == SyncRefusal.MembershipRevoked && e.Household == household)
        {
            // ADR 0002 §10 step 2: soft-lock first; delete nothing.
            await state.SetSoftLockAsync(household, true, cancellationToken);
            return new CycleReport(CycleOutcome.SoftLocked, changed);
        }
        catch (SyncRefusedException e) when (e.Refusal == SyncRefusal.ConsentRequired)
        {
            await consent.ForgetAsync(user, cancellationToken);
            return new CycleReport(CycleOutcome.ConsentNeeded, changed);
        }
        catch (SyncRefusedException e) when (e.Refusal == SyncRefusal.ClientTooOld)
        {
            return new CycleReport(CycleOutcome.ClientTooOld, changed, e.Refusal.ToString());
        }
        catch (SyncRefusedException e)
        {
            return new CycleReport(CycleOutcome.Failed, changed, e.Refusal.ToString());
        }
        catch (SyncUnavailableException)
        {
            return new CycleReport(CycleOutcome.Failed, changed, nameof(SyncUnavailableException));
        }
    }

    private bool Moved(long generation) => account.Generation != generation;

    private async Task<CycleReport> CheckMembershipAsync(HouseholdId household, CancellationToken cancellationToken)
    {
        switch (await api.GetMembershipAsync(household, cancellationToken))
        {
            case MembershipStatus.Active:
                await state.SetSoftLockAsync(household, false, cancellationToken);
                return new CycleReport(CycleOutcome.Interrupted);
            case MembershipStatus.Revoked:
                // Confirmed. Deleting waits for the person (ADR 0002 §10; ruling 13).
                return new CycleReport(CycleOutcome.Removed, Household: household);
            default:
                return new CycleReport(CycleOutcome.SoftLocked);
        }
    }

    private async Task<(bool Changed, bool Interrupted)> PullAllAsync(HouseholdId household, long generation, CancellationToken cancellationToken)
    {
        var changed = false;
        while (true)
        {
            if (Moved(generation))
                return (changed, true);

            var cursor = await store.GetCursorAsync(household, cancellationToken);
            var page = await api.PullAsync(household, cursor.Cursor, cursor.BootstrapHead, cancellationToken);
            if (page.HasMore && page.Cursor <= cursor.Cursor)
                throw new SyncUnavailableException("The server reported more records without moving the cursor.");

            // A page for an account this phone no longer holds is not applied.
            if (Moved(generation))
                return (changed, true);

            await store.ApplyPageAsync(household, page, cancellationToken);
            if (page.Records.Count > 0)
            {
                changed = true;
                await clock.ObserveAsync(page.Records.Max(r => r.Version), cancellationToken);
            }

            if (!page.HasMore)
                return (changed, false);
        }
    }
}
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test tests/Coo.Mobile.Application.Tests`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
dotnet format Coo.Tests.slnf
git add src/Mobile/Coo.Mobile.Application tests/Coo.Mobile.Application.Tests
git commit -m "Phone sync cycle: consent first, push then pull, the generation stops it, soft-lock without deletion, the pull rule (Spec 011 §3–§6)"
```

---
### Task 12: Triggers, coalescing and backoff (`SyncScheduler`), and the engine the app talks to (`SyncEngine`)

**Files:**
- Create: `src/Mobile/Coo.Mobile.Application/Sync/SyncScheduler.cs`, `src/Mobile/Coo.Mobile.Application/Sync/SyncEngine.cs`
- Modify: `src/Mobile/Coo.Mobile.Application/ServiceCollectionExtensions.cs`
- Modify: `tests/Coo.Mobile.Application.Tests/Coo.Mobile.Application.Tests.csproj` (`Microsoft.Extensions.TimeProvider.Testing`)
- Test: `tests/Coo.Mobile.Application.Tests/SyncSchedulerTests.cs`, `SyncEngineTests.cs` (create)

**Interfaces:**
- Consumes: Task 11's `SyncCycle`, `ConsentGate`, ports; `LocalWrites` (Task 10); `Account` (Task 9).
- Produces:
  - `SyncTrigger { Start, Resume, LocalWrite, ConnectivityRegained, Background, Periodic, Manual }`.
  - `SyncScheduler(Func<CancellationToken, Task<bool>> cycle, TimeProvider time, Func<double>? jitter = null)` with
    `Request(SyncTrigger)`, `SetForeground(bool)`, `RunNowAsync(CancellationToken)`, `WhenIdleAsync()`, `Failures`,
    constants `Debounce` (1 s), `ForegroundInterval` (30 s), `FirstBackoff` (2 s), `MaxBackoff` (5 min). The cycle
    delegate returns false for "failed, back off".
  - `SyncStatus(CycleOutcome? Last, DateTimeOffset? LastSyncedAt, int Pending, bool IsRunning, string? LastError, HouseholdId? Removal)`,
    `SyncStatus.Initial`; `ISyncStatus { SyncStatus Status; event EventHandler? StatusChanged; }`.
  - `SyncOptions(Func<double> Jitter)`.
  - `SyncEngine(SyncCycle cycle, ISyncStore store, IHouseholdMoves moves, ISessionStore sessions, Account account,
    ConsentGate consent, LocalWrites writes, TimeProvider time, SyncOptions? options = null)` : `ISyncStatus`,
    `ISyncControl`, `IDisposable`, with `Start()`, `Resumed()`, `Backgrounded()`, `ConnectivityRegained()`,
    `RequestSync()`, `SyncNowAsync(CancellationToken)`, `FreshBootstrapAsync(CancellationToken)` → `bool`,
    `CountsAsync(CancellationToken)` → `SyncCounts?`, `AcknowledgeRemovalAsync(CancellationToken)`, and
    `event EventHandler? DataChanged`.

- [ ] **Step 1: Write the failing tests**

Add `<PackageReference Include="Microsoft.Extensions.TimeProvider.Testing" />` to the test project.

`tests/Coo.Mobile.Application.Tests/SyncSchedulerTests.cs`:

```csharp
using Coo.Mobile.Application.Sync;
using Microsoft.Extensions.Time.Testing;

namespace Coo.Mobile.Application.Tests;

/// <summary>Spec 011 §6.3, ADR 0002 §7: one cycle at a time, triggers coalesced, backoff with jitter up to 5 minutes.</summary>
public sealed class SyncSchedulerTests : IDisposable
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero));
    private readonly SyncScheduler _scheduler;
    private TaskCompletionSource _gate = Released();
    private int _runs;
    private int _running;
    private int _maxRunning;
    private Func<bool> _outcome = () => true;

    public SyncSchedulerTests() => _scheduler = new SyncScheduler(CycleAsync, _time, jitter: () => 1.0);

    public void Dispose() => _scheduler.Dispose();

    private static TaskCompletionSource Released()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gate.SetResult();
        return gate;
    }

    private async Task<bool> CycleAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _runs);
        var now = Interlocked.Increment(ref _running);
        InterlockedMax(ref _maxRunning, now);
        try
        {
            await _gate.Task;
            return _outcome();
        }
        finally
        {
            Interlocked.Decrement(ref _running);
        }
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while ((seen = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, seen) != seen)
        {
        }
    }

    [Fact]
    public async Task Request_ManyTriggersDuringACycle_RunOneMoreCycleNotOneEach()
    {
        // Arrange
        _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _scheduler.Request(SyncTrigger.Start);

        // Act
        _scheduler.Request(SyncTrigger.Periodic);
        _scheduler.Request(SyncTrigger.Resume);
        _scheduler.Request(SyncTrigger.Manual);
        var idle = _scheduler.WhenIdleAsync();
        _gate.SetResult();
        await idle;

        // Assert
        _runs.Should().Be(2);
        _maxRunning.Should().Be(1);
    }

    [Fact]
    public async Task Request_LocalWritesInABurst_AreDebouncedIntoOneCycle()
    {
        // Act
        for (var i = 0; i < 5; i++)
        {
            _scheduler.Request(SyncTrigger.LocalWrite);
            _time.Advance(TimeSpan.FromMilliseconds(300));
        }

        var before = _runs;
        _time.Advance(SyncScheduler.Debounce);
        await _scheduler.WhenIdleAsync();

        // Assert
        before.Should().Be(0);
        _runs.Should().Be(1);
    }

    [Fact]
    public async Task Request_PeriodicDuringBackoff_WaitsForTheBackoff()
    {
        // Arrange: offline (Review Focus 2).
        _outcome = () => false;
        _scheduler.Request(SyncTrigger.Start);
        await _scheduler.WhenIdleAsync();

        // Act
        _scheduler.Request(SyncTrigger.Periodic);
        _scheduler.Request(SyncTrigger.LocalWrite);
        _time.Advance(SyncScheduler.Debounce);
        await _scheduler.WhenIdleAsync();
        var duringBackoff = _runs;
        _time.Advance(SyncScheduler.FirstBackoff);
        await _scheduler.WhenIdleAsync();

        // Assert
        duringBackoff.Should().Be(1);
        _runs.Should().Be(2);
    }

    [Fact]
    public async Task Request_RepeatedFailures_DoubleTheBackoffUpToFiveMinutes()
    {
        // Arrange
        _outcome = () => false;
        _scheduler.Request(SyncTrigger.Start);
        await _scheduler.WhenIdleAsync();
        var delays = new List<TimeSpan>();

        // Act: measure each wait by advancing a second at a time until the next cycle starts.
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var runs = _runs;
            var waited = TimeSpan.Zero;
            while (_runs == runs)
            {
                _time.Advance(TimeSpan.FromSeconds(1));
                waited += TimeSpan.FromSeconds(1);
                await _scheduler.WhenIdleAsync();
            }

            delays.Add(waited);
        }

        // Assert (jitter fixed at 1.0)
        delays.Take(5).Should().Equal(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(16), TimeSpan.FromSeconds(32));
        delays.Max().Should().Be(SyncScheduler.MaxBackoff);
    }

    [Fact]
    public async Task Request_ConnectivityRegained_ResetsTheBackoffAndRuns()
    {
        // Arrange
        _outcome = () => false;
        _scheduler.Request(SyncTrigger.Start);
        await _scheduler.WhenIdleAsync();
        _outcome = () => true;

        // Act
        _scheduler.Request(SyncTrigger.ConnectivityRegained);
        await _scheduler.WhenIdleAsync();

        // Assert
        _runs.Should().Be(2);
        _scheduler.Failures.Should().Be(0);
    }

    [Fact]
    public async Task SetForeground_ThirtySecondsPass_RunsACycleEachTime()
    {
        // Arrange
        _scheduler.SetForeground(true);

        // Act
        _time.Advance(SyncScheduler.ForegroundInterval);
        await _scheduler.WhenIdleAsync();
        _time.Advance(SyncScheduler.ForegroundInterval);
        await _scheduler.WhenIdleAsync();

        // Assert
        _runs.Should().Be(2);
    }

    [Fact]
    public async Task SetForeground_GoingToTheBackground_RunsOneLastCycleAndStopsTheTimer()
    {
        // Arrange
        _scheduler.SetForeground(true);

        // Act
        _scheduler.SetForeground(false);
        await _scheduler.WhenIdleAsync();
        _time.Advance(SyncScheduler.ForegroundInterval * 3);
        await _scheduler.WhenIdleAsync();

        // Assert
        _runs.Should().Be(1);
    }

    [Fact]
    public async Task Request_CycleThrows_CountsAsAFailureAndTheSchedulerKeepsWorking()
    {
        // Arrange: nothing may escape the fire-and-forget loop.
        _outcome = () => throw new InvalidOperationException("boom");
        _scheduler.Request(SyncTrigger.Start);
        await _scheduler.WhenIdleAsync();
        _outcome = () => true;

        // Act
        _scheduler.Request(SyncTrigger.Manual);
        await _scheduler.WhenIdleAsync();

        // Assert
        _runs.Should().Be(2);
        _scheduler.Failures.Should().Be(0);
    }

    [Fact]
    public async Task RunNowAsync_WhileACycleRuns_CompletesAfterTheNextOne()
    {
        // Arrange
        _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _scheduler.Request(SyncTrigger.Start);

        // Act
        var now = _scheduler.RunNowAsync(CancellationToken.None);
        var completedEarly = now.IsCompleted;
        _gate.SetResult();
        await now;

        // Assert
        completedEarly.Should().BeFalse();
        _runs.Should().Be(2);
    }
}
```

`tests/Coo.Mobile.Application.Tests/SyncEngineTests.cs`:

```csharp
using Coo.Domain.Common;
using Coo.Mobile.Application.Auth;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Application.Sync;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using static Coo.Mobile.Application.Tests.SyncCycleTests;

namespace Coo.Mobile.Application.Tests;

/// <summary>Spec 011 §2, §6.3, §11: the status the sync line reads, the triggers it wires, removal only on acknowledgement.</summary>
public sealed class SyncEngineTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    private readonly MemorySessions _sessions = new();
    private readonly Account _account;
    private readonly ISyncState _state = Substitute.For<ISyncState>();
    private readonly IAccountApi _accounts = Substitute.For<IAccountApi>();
    private readonly ISyncStore _store = Substitute.For<ISyncStore>();
    private readonly IHouseholdMoves _moves = Substitute.For<IHouseholdMoves>();
    private readonly ISyncApi _api = Substitute.For<ISyncApi>();
    private readonly LocalWrites _writes = new();
    private readonly FakeTimeProvider _time = new(Now);
    private readonly SyncEngine _engine;

    public SyncEngineTests()
    {
        _account = new Account(_sessions);
        _state.GetConsentAsync(User, Arg.Any<CancellationToken>()).Returns(ConsentState.Given);
        _moves.ActiveHouseholdAsync(Arg.Any<CancellationToken>()).Returns(Household);
        _store.NextBatchAsync(Household, Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);
        _store.GetCursorAsync(Household, Arg.Any<CancellationToken>()).Returns(new SyncCursor(0, null));
        _store.CountsAsync(Household, Arg.Any<CancellationToken>()).Returns(new SyncCounts(0, 0, 0));
        _api.PullAsync(Household, Arg.Any<long>(), Arg.Any<long?>(), Arg.Any<CancellationToken>()).Returns(new PulledPage([], 0, false, 0));
        var device = Substitute.For<IDeviceIdentity>();
        device.GetAsync(Arg.Any<CancellationToken>()).Returns(Phone);
        var consent = new ConsentGate(_state, _accounts, Substitute.For<IConsentApi>());
        var cycle = new SyncCycle(_account, _sessions, consent, _store, _moves, _state, _api, Substitute.For<IVersionClock>(), device, _time);
        _engine = new SyncEngine(cycle, _store, _moves, _sessions, _account, consent, _writes, _time, new SyncOptions(() => 1.0));
    }

    public void Dispose()
    {
        _engine.Dispose();
        _account.Dispose();
    }

    private Task SignInAsync() => _account.SignedInAsync(
        new StoredSession("access", Now.AddMinutes(15), "refresh", Profile, Household, JoinPending: false), CancellationToken.None);

    [Fact]
    public async Task SyncNowAsync_Succeeds_StatusSaysSyncedWithTheTimeAndNothingPending()
    {
        // Arrange
        await SignInAsync();

        // Act
        await _engine.SyncNowAsync(CancellationToken.None);

        // Assert
        _engine.Status.Should().Be(SyncStatus.Initial with { Last = CycleOutcome.Synced, LastSyncedAt = _time.GetLocalNow() });
    }

    [Fact]
    public async Task SyncNowAsync_Offline_StatusSaysFailedWithThePendingCount()
    {
        // Arrange
        await SignInAsync();
        _store.CountsAsync(Household, Arg.Any<CancellationToken>()).Returns(new SyncCounts(0, 2, 0));
        _api.PullAsync(Household, Arg.Any<long>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<PulledPage>(new SyncUnavailableException("offline")));

        // Act
        await _engine.SyncNowAsync(CancellationToken.None);

        // Assert
        _engine.Status.Last.Should().Be(CycleOutcome.Failed);
        _engine.Status.Pending.Should().Be(2);
    }

    [Fact]
    public async Task LocalWrite_AfterTheDebounce_RunsACycle()
    {
        // Arrange
        await SignInAsync();
        await _engine.SyncNowAsync(CancellationToken.None);
        var pulled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _api.PullAsync(Household, Arg.Any<long>(), Arg.Any<long?>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            pulled.TrySetResult();
            return Task.FromResult(new PulledPage([], 0, false, 0));
        });

        // Act
        _writes.Notify();
        var beforeTheDebounce = pulled.Task.IsCompleted;
        _time.Advance(SyncScheduler.Debounce);

        // Assert (the timeout only bounds a broken build; the cycle starts inside Advance)
        await pulled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        beforeTheDebounce.Should().BeFalse();
    }

    [Fact]
    public async Task SignedIn_AfterASignOutWithAnOldDecline_AsksAgain()
    {
        // Arrange: "Not now" lasts until the next sign-in (ruling 10).
        _state.GetConsentAsync(User, Arg.Any<CancellationToken>()).Returns(ConsentState.Declined);

        // Act
        await SignInAsync();
        await _engine.SyncNowAsync(CancellationToken.None);

        // Assert
        await _state.Received().SetConsentAsync(User, ConsentState.Unknown, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AcknowledgeRemovalAsync_AfterAConfirmedRemoval_DeletesTheHouseholdThenAndOnlyThen()
    {
        // Arrange
        await SignInAsync();
        _state.IsSoftLockedAsync(Household, Arg.Any<CancellationToken>()).Returns(true);
        _api.GetMembershipAsync(Household, Arg.Any<CancellationToken>()).Returns(MembershipStatus.Revoked);
        await _engine.SyncNowAsync(CancellationToken.None);
        var removalBefore = _engine.Status.Removal;
        await _moves.DidNotReceiveWithAnyArgs().DeleteHouseholdAsync(default, default);

        // Act
        await _engine.AcknowledgeRemovalAsync(CancellationToken.None);

        // Assert
        removalBefore.Should().Be(Household);
        await _moves.Received(1).DeleteHouseholdAsync(Household, Arg.Any<CancellationToken>());
        _engine.Status.Removal.Should().BeNull();
    }

    [Fact]
    public async Task FreshBootstrapAsync_OutboxNotEmpty_RefusesAndPullsNothing()
    {
        // Arrange
        await SignInAsync();
        _moves.FreshBootstrapAsync(Household, Arg.Any<CancellationToken>()).Returns(false);
        _api.ClearReceivedCalls();

        // Act
        var done = await _engine.FreshBootstrapAsync(CancellationToken.None);

        // Assert
        done.Should().BeFalse();
        await _api.DidNotReceiveWithAnyArgs().PullAsync(default, default, default, default);
    }

    [Fact]
    public async Task JoinCompleted_BringOverDone_TellsThePagesToRefresh()
    {
        // Arrange (subscribed first: the engine's own first cycle may complete the join at once)
        var raised = 0;
        _engine.DataChanged += (_, _) => Interlocked.Increment(ref raised);
        await _account.SignedInAsync(
            new StoredSession("access", Now.AddMinutes(15), "refresh", Profile, Household, JoinPending: true), CancellationToken.None);

        // Act
        await _account.TryCompleteJoinAsync(_account.Generation, CancellationToken.None);
        await _engine.SyncNowAsync(CancellationToken.None);

        // Assert
        raised.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task SignedOut_StatusSaysNotSignedIn()
    {
        // Arrange
        await SignInAsync();
        await _engine.SyncNowAsync(CancellationToken.None);

        // Act
        await _account.SignedOutAsync(sessionExpired: false, CancellationToken.None);

        // Assert
        _engine.Status.Last.Should().Be(CycleOutcome.NotSignedIn);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Coo.Mobile.Application.Tests --filter "FullyQualifiedName~SyncSchedulerTests|FullyQualifiedName~SyncEngineTests"`
Expected: FAIL to compile (`SyncScheduler`, `SyncEngine` not found). Paste the first error.

- [ ] **Step 3: The scheduler**

`src/Mobile/Coo.Mobile.Application/Sync/SyncScheduler.cs`:

```csharp
using System.Security.Cryptography;

namespace Coo.Mobile.Application.Sync;

public enum SyncTrigger
{
    Start,
    Resume,
    LocalWrite,
    ConnectivityRegained,
    Background,
    Periodic,
    Manual,
}

/// <summary>
/// When cycles run (Spec 011 §6.3, ADR 0002 §7): one at a time; a trigger during a cycle marks it dirty and one more runs
/// after it (coalesced, never queued); local writes are debounced by a second; every 30 seconds in the foreground; one
/// last cycle on the way to the background. A failed cycle backs off from 2 s, doubling to 5 minutes, jittered ×[0.5, 1];
/// during a backoff only Start, Resume, connectivity regained and Manual run at once (they reset it, ruling 12).
/// Nothing escapes: a cycle that throws is a failure.
/// </summary>
public sealed class SyncScheduler : IDisposable
{
    public static readonly TimeSpan Debounce = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan ForegroundInterval = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan FirstBackoff = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);

    private readonly Func<CancellationToken, Task<bool>> _cycle;
    private readonly TimeProvider _time;
    private readonly Func<double> _jitter;
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private TaskCompletionSource _idle = CompletedIdle();
    private ITimer? _debounce;
    private ITimer? _backoff;
    private ITimer? _periodic;
    private bool _running;
    private bool _again;
    private bool _waitingForBackoff;
    private bool _foreground;
    private bool _disposed;
    private int _failures;

    /// <param name="cycle">Runs one cycle; false means "failed, back off".</param>
    /// <param name="jitter">A factor in [0.5, 1]; the default draws it from <see cref="RandomNumberGenerator"/>.</param>
    public SyncScheduler(Func<CancellationToken, Task<bool>> cycle, TimeProvider time, Func<double>? jitter = null)
    {
        _cycle = cycle ?? throw new ArgumentNullException(nameof(cycle));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _jitter = jitter ?? (() => 0.5 + (RandomNumberGenerator.GetInt32(0, 501) / 1000.0));
    }

    public int Failures
    {
        get
        {
            lock (_gate)
                return _failures;
        }
    }

    public void Request(SyncTrigger trigger)
    {
        bool start;
        lock (_gate)
        {
            if (_disposed)
                return;
            if (trigger == SyncTrigger.LocalWrite)
            {
                _debounce?.Dispose();
                _debounce = _time.CreateTimer(_ => Fire(resetBackoff: false), null, Debounce, Timeout.InfiniteTimeSpan);
                return;
            }

            start = Arm(resetBackoff: trigger is SyncTrigger.Start or SyncTrigger.Resume or SyncTrigger.ConnectivityRegained or SyncTrigger.Manual);
        }

        if (start)
            Launch();
    }

    public void SetForeground(bool foreground)
    {
        var start = false;
        lock (_gate)
        {
            if (_disposed || _foreground == foreground)
                return;
            _foreground = foreground;
            _periodic?.Dispose();
            _periodic = foreground ? _time.CreateTimer(_ => Fire(resetBackoff: false), null, ForegroundInterval, ForegroundInterval) : null;
            if (!foreground)
                start = Arm(resetBackoff: false);
        }

        if (start)
            Launch();
    }

    /// <summary>A cycle now (Sync now, after consent or a bring-over); completes when the cycles it caused have ended.</summary>
    public async Task RunNowAsync(CancellationToken cancellationToken)
    {
        Request(SyncTrigger.Manual);
        await WhenIdleAsync().WaitAsync(cancellationToken);
    }

    /// <summary>Completes when no cycle is running or about to run. For tests and <see cref="RunNowAsync"/>.</summary>
    public Task WhenIdleAsync()
    {
        lock (_gate)
            return _idle.Task;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _debounce?.Dispose();
            _backoff?.Dispose();
            _periodic?.Dispose();
            _idle.TrySetResult();
        }

        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    private static TaskCompletionSource CompletedIdle()
    {
        var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        idle.SetResult();
        return idle;
    }

    private void Fire(bool resetBackoff)
    {
        bool start;
        lock (_gate)
        {
            if (_disposed)
                return;
            start = Arm(resetBackoff);
        }

        if (start)
            Launch();
    }

    /// <summary>Under the lock. True when the caller must start the loop.</summary>
    private bool Arm(bool resetBackoff)
    {
        if (resetBackoff)
        {
            _failures = 0;
            _waitingForBackoff = false;
            _backoff?.Dispose();
            _backoff = null;
        }

        if (_waitingForBackoff)
            return false;
        if (_running)
        {
            _again = true;
            return false;
        }

        _running = true;
        if (_idle.Task.IsCompleted)
            _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return true;
    }

    // Started outside the lock and off the caller's thread: a cycle never runs under the lock or inside a timer callback.
    private void Launch() => _ = Task.Run(LoopAsync);

    private async Task LoopAsync()
    {
        while (true)
        {
            bool succeeded;
            try
            {
                succeeded = await _cycle(_lifetime.Token);
            }
#pragma warning disable CA1031 // Fire-and-forget: anything a cycle throws is a failed cycle, never a crash.
            catch (Exception)
#pragma warning restore CA1031
            {
                succeeded = false;
            }

            lock (_gate)
            {
                if (succeeded)
                {
                    _failures = 0;
                }
                else
                {
                    _failures++;
                    _again = false;
                    ArmBackoff();
                }

                if (succeeded && _again && !_disposed)
                {
                    _again = false;
                    continue;
                }

                _running = false;
                _idle.TrySetResult();
                return;
            }
        }
    }

    /// <summary>Under the lock.</summary>
    private void ArmBackoff()
    {
        if (_disposed)
            return;
        var doubled = FirstBackoff.Ticks << Math.Min(_failures - 1, 20);
        var delay = TimeSpan.FromTicks((long)(Math.Min(MaxBackoff.Ticks, doubled) * Math.Clamp(_jitter(), 0.5, 1.0)));
        _waitingForBackoff = true;
        _backoff?.Dispose();
        _backoff = _time.CreateTimer(
            _ =>
            {
                lock (_gate)
                    _waitingForBackoff = false;
                Fire(resetBackoff: false);
            },
            null, delay, Timeout.InfiniteTimeSpan);
    }
}
```

- [ ] **Step 4: The engine**

`src/Mobile/Coo.Mobile.Application/Sync/SyncEngine.cs`:

```csharp
using Coo.Domain.Common;
using Coo.Mobile.Application.Auth;

namespace Coo.Mobile.Application.Sync;

/// <summary>What the sync line and the debug screen read (Spec 011 §2, §11).</summary>
/// <param name="LastSyncedAt">The phone's local time of the last successful cycle.</param>
/// <param name="Pending">Outbox rows of the account's household.</param>
/// <param name="Removal">A household the person was removed from, waiting for <see cref="SyncEngine.AcknowledgeRemovalAsync"/>.</param>
public sealed record SyncStatus(CycleOutcome? Last, DateTimeOffset? LastSyncedAt, int Pending, bool IsRunning, string? LastError, HouseholdId? Removal)
{
    public static SyncStatus Initial { get; } = new(null, null, 0, false, null, null);
}

public interface ISyncStatus
{
    SyncStatus Status { get; }

    event EventHandler? StatusChanged;
}

/// <summary>For tests: a fixed jitter makes backoff exact.</summary>
public sealed record SyncOptions(Func<double> Jitter);

/// <summary>
/// The phone's sync, as the app sees it (Spec 011 §6.3): it wires the triggers (start, resume, local writes, connectivity,
/// background, every 30 s in the foreground) to the scheduler, runs <see cref="SyncCycle"/>, and keeps the status the
/// sync line reads. Events are raised from background threads; handlers dispatch to the UI themselves.
/// </summary>
public sealed class SyncEngine : ISyncStatus, ISyncControl, IDisposable
{
    private readonly SyncCycle _cycle;
    private readonly ISyncStore _store;
    private readonly IHouseholdMoves _moves;
    private readonly ISessionStore _sessions;
    private readonly Account _account;
    private readonly ConsentGate _consent;
    private readonly LocalWrites _writes;
    private readonly TimeProvider _time;
    private readonly SyncScheduler _scheduler;
    private readonly Lock _statusLock = new();
    private SyncStatus _status = SyncStatus.Initial;
    private AccountState _lastAccount;

    public SyncEngine(
        SyncCycle cycle, ISyncStore store, IHouseholdMoves moves, ISessionStore sessions, Account account, ConsentGate consent,
        LocalWrites writes, TimeProvider time, SyncOptions? options = null)
    {
        _cycle = cycle;
        _store = store;
        _moves = moves;
        _sessions = sessions;
        _account = account;
        _consent = consent;
        _writes = writes;
        _time = time;
        _scheduler = new SyncScheduler(RunCycleAsync, time, options?.Jitter);
        _lastAccount = account.State;
        account.Changed += OnAccountChanged;
        writes.Written += OnLocalWrite;
    }

    public event EventHandler? StatusChanged;

    /// <summary>Local data changed under the UI (pulled records, a move, a removal): pages refresh.</summary>
    public event EventHandler? DataChanged;

    public SyncStatus Status
    {
        get
        {
            lock (_statusLock)
                return _status;
        }
    }

    /// <summary>The app's first page is up: foreground, and a first cycle.</summary>
    public void Start()
    {
        _scheduler.SetForeground(true);
        _scheduler.Request(SyncTrigger.Start);
        _ = RefreshPendingAsync();
    }

    public void Resumed()
    {
        _scheduler.SetForeground(true);
        _scheduler.Request(SyncTrigger.Resume);
    }

    public void Backgrounded() => _scheduler.SetForeground(false);

    public void ConnectivityRegained() => _scheduler.Request(SyncTrigger.ConnectivityRegained);

    public void RequestSync() => _scheduler.Request(SyncTrigger.Manual);

    public Task SyncNowAsync(CancellationToken cancellationToken) => _scheduler.RunNowAsync(cancellationToken);

    /// <summary>The debug screen's Fresh bootstrap (Spec 011 §11): only with an empty outbox; the log comes back from cursor 0.</summary>
    public async Task<bool> FreshBootstrapAsync(CancellationToken cancellationToken)
    {
        if (await _sessions.GetAsync(cancellationToken) is not { JoinPending: false } session)
            return false;
        if (!await _moves.FreshBootstrapAsync(session.AccountHousehold, cancellationToken))
            return false;
        DataChanged?.Invoke(this, EventArgs.Empty);
        await SyncNowAsync(cancellationToken);
        return true;
    }

    public async Task<SyncCounts?> CountsAsync(CancellationToken cancellationToken) =>
        await _sessions.GetAsync(cancellationToken) is { } session ? await _store.CountsAsync(session.AccountHousehold, cancellationToken) : null;

    /// <summary>The person was told they were removed (11b's sheet): the household goes from this phone (ADR 0002 §10).</summary>
    public async Task AcknowledgeRemovalAsync(CancellationToken cancellationToken)
    {
        if (Status.Removal is not { } household)
            return;
        await _moves.DeleteHouseholdAsync(household, cancellationToken);
        Update(s => s with { Removal = null, Last = null });
        DataChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        _account.Changed -= OnAccountChanged;
        _writes.Written -= OnLocalWrite;
        _scheduler.Dispose();
    }

    private async Task<bool> RunCycleAsync(CancellationToken cancellationToken)
    {
        Update(s => s with { IsRunning = true });
        CycleReport report;
        try
        {
            report = await _cycle.RunAsync(cancellationToken);
        }
#pragma warning disable CA1031 // A store failure (SQLite) is a failed cycle; the scheduler backs off.
        catch (Exception e)
#pragma warning restore CA1031
        {
            report = new CycleReport(CycleOutcome.Failed, Error: e.GetType().Name);
        }

        var pending = await PendingAsync(cancellationToken);
        var now = _time.GetLocalNow();
        Update(s => s with
        {
            Last = report.Outcome,
            LastSyncedAt = report.Outcome == CycleOutcome.Synced ? now : s.LastSyncedAt,
            Pending = pending ?? s.Pending,
            IsRunning = false,
            LastError = report.Error,
            Removal = report.Outcome == CycleOutcome.Removed ? report.Household : s.Removal,
        });
        if (report.DataChanged)
            DataChanged?.Invoke(this, EventArgs.Empty);

        return report.Outcome is not (CycleOutcome.Failed or CycleOutcome.ClientTooOld or CycleOutcome.SoftLocked or CycleOutcome.Removed);
    }

    private async Task<int?> PendingAsync(CancellationToken cancellationToken)
    {
        try
        {
            return (await CountsAsync(cancellationToken))?.Outbox;
        }
#pragma warning disable CA1031 // The count is for the sync line only; the last known one stays.
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
    }

    private async Task RefreshPendingAsync()
    {
        if (await PendingAsync(CancellationToken.None) is { } pending)
            Update(s => s with { Pending = pending });
    }

    private void OnLocalWrite(object? sender, EventArgs e)
    {
        _scheduler.Request(SyncTrigger.LocalWrite);
        _ = RefreshPendingAsync();
    }

    private void OnAccountChanged(object? sender, EventArgs e)
    {
        var now = _account.State;
        var before = _lastAccount;
        _lastAccount = now;
        if (now is SignedOut)
        {
            Update(_ => SyncStatus.Initial with { Last = CycleOutcome.NotSignedIn });
            return;
        }

        if (before is SignedOut)
        {
            _ = ResetDeclineThenStartAsync();
            return;
        }

        // Join-pending to signed in: the bring-over moved this phone into the family's log; the pages show it now.
        if (before is JoinPending && now is SignedIn)
            DataChanged?.Invoke(this, EventArgs.Empty);
        _scheduler.Request(SyncTrigger.Manual);
    }

    private async Task ResetDeclineThenStartAsync()
    {
        try
        {
            if (await _sessions.GetAsync(CancellationToken.None) is { } session)
                await _consent.ResetDeclinedAsync(session.Profile.UserId, CancellationToken.None);
        }
#pragma warning disable CA1031 // Fire-and-forget: the next cycle reads the consent again anyway.
        catch (Exception)
#pragma warning restore CA1031
        {
        }

        _scheduler.Request(SyncTrigger.Start);
    }

    private void Update(Func<SyncStatus, SyncStatus> change)
    {
        lock (_statusLock)
            _status = change(_status);
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }
}
```

(Check `RefreshPendingAsync`: it is fire-and-forget, and `PendingAsync` already catches everything; `Update` invokes
handlers that must not throw — the view models only set properties and dispatch.)

- [ ] **Step 5: Registration**

In `AddMobileApplication`, under the `// Sync (Spec 011)` comment:

```csharp
        services.AddTransient<ConsentGate>();
        services.AddTransient<SyncCycle>();
        services.AddSingleton<SyncEngine>();
        services.AddSingleton<ISyncStatus>(sp => sp.GetRequiredService<SyncEngine>());
        services.AddSingleton<ISyncControl>(sp => sp.GetRequiredService<SyncEngine>());
        services.AddTransient<RecordConsent>();
        services.AddTransient<DeclineConsent>();
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test tests/Coo.Mobile.Application.Tests` (twice; then `TZ=UTC dotnet test tests/Coo.Mobile.Application.Tests`)
Expected: PASS every time. A flaky result here is a bug in the scheduler's locking, not in the test: every wait is on
`WhenIdleAsync`, and every timer fires inside `FakeTimeProvider.Advance`.

- [ ] **Step 7: Commit**

```bash
dotnet format Coo.Tests.slnf
git add src/Mobile/Coo.Mobile.Application tests/Coo.Mobile.Application.Tests
git commit -m "Phone sync engine: triggers, one cycle at a time, coalescing, debounce, backoff with jitter, the status for the sync line (Spec 011 §6.3)"
```

---
### Task 13: The phone's sync store over SQLite: the outbox batch, results, the pulled-record path, cursors

**Files:**
- Create: `src/Mobile/Coo.Mobile.Infrastructure/Persistence/SyncStore.cs`
- Test: `tests/Coo.Mobile.Infrastructure.Tests/SyncStoreTests.cs` (create)

**Interfaces:**
- Consumes: Task 10's tables and `LocalWrites`; Task 11's `ISyncStore`, payloads and `PullRule`; `Columns`,
  `CareEntryRow`, `CareEntryClassifier`, `StringListJson`, `SyncedEntityType` (existing).
- Produces: `internal sealed class SyncStore(LocalDatabase database) : ISyncStore`.

- [ ] **Step 1: Write the failing tests**

`tests/Coo.Mobile.Infrastructure.Tests/SyncStoreTests.cs`:

```csharp
using Coo.Contracts.Sync;
using Coo.Domain.CareLog;
using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;
using Coo.Domain.Households;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Application.Sync;
using Coo.Mobile.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace Coo.Mobile.Infrastructure.Tests;

/// <summary>Spec 011 §6 and §10 (Mobile.Infrastructure row), on a real SQLite file.</summary>
public sealed class SyncStoreTests
{
    private static readonly UserId User = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000c1"));
    private static readonly HouseholdId Other = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a9"));
    private static readonly DeviceId Remote = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000d9"));
    private const string WetDetails = """{"v":1,"contents":"wet"}""";

    private static SyncStore Store(TestDatabase db) => new(db.Database);

    private static PulledRecord PulledDiaper(Guid id, long wall, string details = WetDetails, bool deleted = false, string kind = "diaper") => new(
        SyncEntityTypes.CareEntry, id, new HlcStamp(wall, 0, Remote), deleted,
        new CareEntryPayload(kind, TestDatabase.Baby.Value, null, false, new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.FromHours(2)), null,
            "user:7a1c0000-0000-7000-8000-0000000000c1", "manual", null, null, details));

    private static async Task<long> CountAsync(TestDatabase db, string sql) => await db.ScalarAsync(sql);

    [Fact]
    public async Task NextBatchAsync_ThreeWritesOfOneEntry_IsOneChangeWithItsCurrentStateUnderTheNewestChangeId()
    {
        // Arrange
        await using var db = await TestDatabase.CreateAsync();
        var entry = await db.DiaperAsync(db.Time.GetLocalNow());
        entry.SetNote(Note.From("one").Value, await db.Device.NextAsync(CancellationToken.None)).IsSuccess.Should().BeTrue();
        await db.Store.SaveAsync(entry, CancellationToken.None);
        entry.SetNote(Note.From("two").Value, await db.Device.NextAsync(CancellationToken.None));
        await db.Store.SaveAsync(entry, CancellationToken.None);
        var newestChange = (string)(await (await db.OpenAsync()).ScalarAsync("SELECT change_id FROM outbox ORDER BY seq DESC LIMIT 1;", CancellationToken.None))!;

        // Act
        var batch = await Store(db).NextBatchAsync(TestDatabase.Household, 200, CancellationToken.None);

        // Assert
        var change = batch.Should().ContainSingle().Subject;
        change.ChangeId.Should().Be(Guid.Parse(newestChange));
        change.Version.Should().Be(entry.Version);
        change.Payload.Should().BeOfType<CareEntryPayload>().Which.Note.Should().Be("two");
        change.LastOutboxSeq.Should().Be(3);
    }

    [Fact]
    public async Task NextBatchAsync_AnotherHouseholdsRecords_AreNotSent()
    {
        // Arrange: a phone that kept its own log separate (Q4) has two households' outbox rows.
        await using var db = await TestDatabase.CreateAsync();
        await db.DiaperAsync(db.Time.GetLocalNow());
        await db.Households.SaveSettingsAsync(
            HouseholdSettings.Create(Other, VolumeUnit.Millilitres, "Europe/Amsterdam", await db.Device.NextAsync(CancellationToken.None)).Value, CancellationToken.None);

        // Act
        var batch = await Store(db).NextBatchAsync(TestDatabase.Household, 200, CancellationToken.None);

        // Assert
        batch.Should().ContainSingle().Which.EntityType.Should().Be(SyncEntityTypes.CareEntry);
    }

    [Fact]
    public async Task NextBatchAsync_DeletedEntryOfANewerKind_IsADeleteWithoutAPayload()
    {
        // Arrange: a read-only entry (ADR 0010 §3) can be deleted; its details are never decoded.
        await using var db = await TestDatabase.CreateAsync();
        var id = await db.NewIdAsync();
        await Store(db).ApplyPageAsync(TestDatabase.Household, new PulledPage([PulledDiaper(id.Value, 10, """{"v":1,"drug":"vitamin D"}""", kind: "medication")], 1, false, null), CancellationToken.None);
        await db.Store.DeleteAsync(id, await db.Device.NextAsync(CancellationToken.None), CancellationToken.None);

        // Act
        var batch = await Store(db).NextBatchAsync(TestDatabase.Household, 200, CancellationToken.None);

        // Assert
        var change = batch.Should().ContainSingle().Subject;
        change.IsDelete.Should().BeTrue();
        change.Payload.Should().BeNull();
    }

    [Fact]
    public async Task ApplyResultsAsync_Applied_RemovesTheRowsUpToTheChangeAndKeepsALaterEdit()
    {
        // Arrange: an edit lands while the batch is in flight.
        await using var db = await TestDatabase.CreateAsync();
        var entry = await db.DiaperAsync(db.Time.GetLocalNow());
        var store = Store(db);
        var batch = await store.NextBatchAsync(TestDatabase.Household, 200, CancellationToken.None);
        entry.SetNote(Note.From("later").Value, await db.Device.NextAsync(CancellationToken.None));
        await db.Store.SaveAsync(entry, CancellationToken.None);

        // Act
        await store.ApplyResultsAsync(TestDatabase.Household, batch, [new PushResult(batch[0].ChangeId, PushOutcome.Applied, null)], db.Time.GetUtcNow(), CancellationToken.None);

        // Assert
        (await CountAsync(db, "SELECT count(*) FROM outbox;")).Should().Be(1);
        (await store.NextBatchAsync(TestDatabase.Household, 200, CancellationToken.None)).Single().Payload
            .Should().BeOfType<CareEntryPayload>().Which.Note.Should().Be("later");
    }

    [Fact]
    public async Task ApplyResultsAsync_Rejected_KeepsTheEntryListsTheReasonAndALaterApplyClearsIt()
    {
        // Arrange
        await using var db = await TestDatabase.CreateAsync();
        var entry = await db.DiaperAsync(db.Time.GetLocalNow());
        var store = Store(db);
        var batch = await store.NextBatchAsync(TestDatabase.Household, 200, CancellationToken.None);

        // Act
        await store.ApplyResultsAsync(TestDatabase.Household, batch,
            [new PushResult(batch[0].ChangeId, PushOutcome.Rejected, "time.end-not-after-start")], db.Time.GetUtcNow(), CancellationToken.None);
        var listed = await store.ListRejectionsAsync(TestDatabase.Household, CancellationToken.None);
        entry.SetNote(Note.From("fixed").Value, await db.Device.NextAsync(CancellationToken.None));
        await db.Store.SaveAsync(entry, CancellationToken.None);
        var fix = await store.NextBatchAsync(TestDatabase.Household, 200, CancellationToken.None);
        await store.ApplyResultsAsync(TestDatabase.Household, fix, [new PushResult(fix[0].ChangeId, PushOutcome.Applied, null)], db.Time.GetUtcNow(), CancellationToken.None);

        // Assert
        listed.Should().ContainSingle().Which.Should().Match<SyncRejection>(r => r.EntityId == entry.Id.Value && r.Reason == "time.end-not-after-start");
        (await db.Store.FindAsync(entry.Id, CancellationToken.None)).Should().BeOfType<LiveEntry>("a rejected entry is never deleted");
        (await store.ListRejectionsAsync(TestDatabase.Household, CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task NextBatchAsync_AfterAnAppKillMidPush_SendsTheSameChangeAgain()
    {
        // Arrange: the batch went out; the app died before any result was applied.
        await using var db = await TestDatabase.CreateAsync();
        await db.DiaperAsync(db.Time.GetLocalNow());
        var first = await Store(db).NextBatchAsync(TestDatabase.Household, 200, CancellationToken.None);
        SqliteConnection.ClearAllPools();

        // Act
        var afterRestart = await Store(db).NextBatchAsync(TestDatabase.Household, 200, CancellationToken.None);

        // Assert
        afterRestart.Single().ChangeId.Should().Be(first.Single().ChangeId, "the server's applied_changes makes the resend idempotent");
    }

    [Fact]
    public async Task PrepareForPushAsync_ThisDevicePeople_BecomeTheUser()
    {
        // Arrange: logged before sign-in, and a pumping session about the caregiver holding the phone.
        await using var db = await TestDatabase.CreateAsync();
        await using (var connection = await db.OpenAsync())
            await connection.ExecuteAsync(
                $"""
                INSERT INTO care_entries (id, household_id, kind, subject_person, is_session, start_utc_ms, start_offset_min, end_utc_ms, end_offset_min,
                    logged_by, source, details, version_wall_ms, version_counter, version_device)
                VALUES ('{(await db.NewIdAsync()).Value}', '{TestDatabase.Household.Value}', 'pump', 'this-device', 1, 1000, 0, 2000, 0,
                    'this-device', 'manual', '{"{"}"v":1{"}"}', 1, 0, '{Remote.Value}');
                """, CancellationToken.None);

        // Act
        await Store(db).PrepareForPushAsync(TestDatabase.Household, User, CancellationToken.None);

        // Assert
        (await CountAsync(db, "SELECT count(*) FROM care_entries WHERE logged_by = 'this-device' OR subject_person = 'this-device';")).Should().Be(0);
        (await CountAsync(db, "SELECT count(*) FROM care_entries WHERE logged_by = 'user:7a1c0000-0000-7000-8000-0000000000c1' AND subject_person = logged_by;")).Should().Be(1);
    }

    [Fact]
    public async Task ApplyPageAsync_EntryOfAKindThisBuildDoesNotKnow_IsStoredAsReceivedAndReadOnly()
    {
        // Arrange
        await using var db = await TestDatabase.CreateAsync();
        var id = await db.NewIdAsync();
        const string Foreign = """{"v":1,"drug":"vitamin D","doseDrops":1}""";

        // Act
        await Store(db).ApplyPageAsync(TestDatabase.Household, new PulledPage([PulledDiaper(id.Value, 10, Foreign, kind: "medication")], 1, false, null), CancellationToken.None);

        // Assert
        (await (await db.OpenAsync()).ScalarAsync($"SELECT details FROM care_entries WHERE id = '{id.Value}';", CancellationToken.None)).Should().Be(Foreign);
        (await db.Store.FindAsync(id, CancellationToken.None)).Should().BeOfType<ReadOnlyEntry>();
        (await CountAsync(db, "SELECT count(*) FROM outbox;")).Should().Be(0, "a pulled record leaves no outbox row");
    }

    [Fact]
    public async Task ApplyPageAsync_PropertyThisBuildDoesNotKnow_SurvivesALocalEdit()
    {
        // Arrange: a newer phone added "stoolColour" (ADR 0010 §3.2).
        await using var db = await TestDatabase.CreateAsync();
        var id = await db.NewIdAsync();
        await Store(db).ApplyPageAsync(TestDatabase.Household,
            new PulledPage([PulledDiaper(id.Value, 10, """{"v":1,"contents":"wet","stoolColour":"green"}""")], 1, false, null), CancellationToken.None);
        var entry = ((LiveEntry)(await db.Store.FindAsync(id, CancellationToken.None))!).Entry;

        // Act
        entry.SetNote(Note.From("edited here").Value, await db.Device.NextAsync(CancellationToken.None));
        await db.Store.SaveAsync(entry, CancellationToken.None);

        // Assert
        var batch = await Store(db).NextBatchAsync(TestDatabase.Household, 200, CancellationToken.None);
        ((CareEntryPayload)batch.Single().Payload!).DetailsJson.Should().Contain("\"stoolColour\":\"green\"");
    }

    [Theory]
    [InlineData(20, 10, "mine")]
    [InlineData(5, 10, "theirs")]
    public async Task ApplyPageAsync_PendingLocalEdit_IsKeptOnlyWhenNewer(long localWall, long remoteWall, string expected)
    {
        // Arrange
        await using var db = await TestDatabase.CreateAsync();
        var id = await db.NewIdAsync();
        await using (var connection = await db.OpenAsync())
        {
            await connection.ExecuteAsync(
                $"""
                INSERT INTO care_entries (id, household_id, kind, subject_baby_id, is_session, start_utc_ms, start_offset_min, logged_by, source,
                    note, details, version_wall_ms, version_counter, version_device)
                VALUES ('{id.Value}', '{TestDatabase.Household.Value}', 'diaper', '{TestDatabase.Baby.Value}', 0, 1000, 0, 'this-device', 'manual',
                    'mine', '{WetDetails}', {localWall}, 0, '{Remote.Value}');
                INSERT INTO outbox (change_id, entity_type, entity_id, operation, version_wall_ms, version_counter, version_device)
                VALUES ('{(await db.NewIdAsync()).Value}', 'CareEntry', '{id.Value}', 'upsert', {localWall}, 0, '{Remote.Value}');
                """, CancellationToken.None);
        }

        var theirs = PulledDiaper(id.Value, remoteWall) with { };
        var page = new PulledPage([theirs with { Payload = ((CareEntryPayload)theirs.Payload) with { Note = "theirs" } }], 1, false, null);

        // Act
        await Store(db).ApplyPageAsync(TestDatabase.Household, page, CancellationToken.None);

        // Assert
        (await (await db.OpenAsync()).ScalarAsync($"SELECT note FROM care_entries WHERE id = '{id.Value}';", CancellationToken.None)).Should().Be(expected);
    }

    [Fact]
    public async Task ApplyPageAsync_ARecordTheDatabaseRefuses_ChangesNothingAndKeepsTheCursor()
    {
        // Arrange: the cursor commits with its page (Spec 011 §6.2); a refused row undoes the whole page.
        await using var db = await TestDatabase.CreateAsync();
        var good = PulledDiaper((await db.NewIdAsync()).Value, 10);
        var bad = new PulledRecord(SyncEntityTypes.BabyProfile, (await db.NewIdAsync()).Value, new HlcStamp(11, 0, Remote), false,
            new BabyProfilePayload(new string('M', 41), [], new DateOnly(2026, 8, 20), null));

        // Act
        var act = async () => await Store(db).ApplyPageAsync(TestDatabase.Household, new PulledPage([good, bad], 2, false, null), CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<SqliteException>();
        (await CountAsync(db, "SELECT count(*) FROM care_entries;")).Should().Be(0);
        (await Store(db).GetCursorAsync(TestDatabase.Household, CancellationToken.None)).Cursor.Should().Be(0);
    }

    [Fact]
    public async Task ApplyPageAsync_TombstoneOfAnUnknownEntry_IsStoredDeleted()
    {
        // Arrange
        await using var db = await TestDatabase.CreateAsync();
        var id = await db.NewIdAsync();

        // Act
        await Store(db).ApplyPageAsync(TestDatabase.Household, new PulledPage([PulledDiaper(id.Value, 10, deleted: true)], 3, false, null), CancellationToken.None);

        // Assert
        (await CountAsync(db, $"SELECT is_deleted FROM care_entries WHERE id = '{id.Value}';")).Should().Be(1);
        (await Store(db).GetCursorAsync(TestDatabase.Household, CancellationToken.None)).Should().Be(new SyncCursor(3, null));
    }

    [Fact]
    public async Task ApplyPageAsync_MiddleOfABootstrap_KeepsTheHeadForTheNextPage()
    {
        // Arrange
        await using var db = await TestDatabase.CreateAsync();

        // Act
        await Store(db).ApplyPageAsync(Other, new PulledPage([], 500, true, 900), CancellationToken.None);

        // Assert
        (await Store(db).GetCursorAsync(Other, CancellationToken.None)).Should().Be(new SyncCursor(500, 900));
    }

    [Fact]
    public async Task GetCursorAsync_NoCursorButLocalEntries_AsksForEveryTombstone()
    {
        // Arrange: pushed before its first pull (ruling 20).
        await using var db = await TestDatabase.CreateAsync();
        await db.DiaperAsync(db.Time.GetLocalNow());

        // Act
        var cursor = await Store(db).GetCursorAsync(TestDatabase.Household, CancellationToken.None);

        // Assert
        cursor.Should().Be(new SyncCursor(0, 0));
    }

    [Fact]
    public async Task GetCursorAsync_EmptyHousehold_LetsTheServerChooseTheHead()
    {
        // Arrange
        await using var db = await TestDatabase.CreateAsync();

        // Act
        var cursor = await Store(db).GetCursorAsync(Other, CancellationToken.None);

        // Assert
        cursor.Should().Be(new SyncCursor(0, null));
    }

    [Fact]
    public async Task CountsAsync_EntriesEditedAndRejected_CountsRecordsNotRows()
    {
        // Arrange
        await using var db = await TestDatabase.CreateAsync();
        var entry = await db.DiaperAsync(db.Time.GetLocalNow());
        entry.SetNote(Note.From("again").Value, await db.Device.NextAsync(CancellationToken.None));
        await db.Store.SaveAsync(entry, CancellationToken.None);
        await db.DiaperAsync(db.Time.GetLocalNow());

        // Act
        var counts = await Store(db).CountsAsync(TestDatabase.Household, CancellationToken.None);

        // Assert
        counts.Should().Be(new SyncCounts(0, 2, 0));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Coo.Mobile.Infrastructure.Tests --filter FullyQualifiedName~SyncStoreTests`
Expected: FAIL to compile (`SyncStore` not found). Paste the first error.

- [ ] **Step 3: Implement**

`src/Mobile/Coo.Mobile.Infrastructure/Persistence/SyncStore.cs`:

```csharp
using System.Globalization;
using System.Text.Json;
using Coo.Contracts.Sync;
using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;
using Coo.Mobile.Application.Sync;
using Microsoft.Data.Sqlite;

namespace Coo.Mobile.Infrastructure.Persistence;

/// <summary>
/// Sync on the phone (Spec 011 §6). The outbox names records; a change is the record's current state, read now (Spec 004
/// §4.3). Results and pulled pages are each one transaction; a page commits together with its cursor. Pulled details
/// are stored exactly as received and classified by the read guard (ADR 0010 §3); a pulled record leaves no outbox row.
/// </summary>
internal sealed class SyncStore(LocalDatabase database) : ISyncStore
{
    /// <summary>The outbox rows whose record belongs to <c>$h</c>; settings are keyed by the household itself.</summary>
    private const string InHousehold =
        """
        ((o.entity_type = 'CareEntry' AND EXISTS (SELECT 1 FROM care_entries c WHERE c.id = o.entity_id AND c.household_id = $h))
         OR (o.entity_type = 'BabyProfile' AND EXISTS (SELECT 1 FROM baby_profiles b WHERE b.id = o.entity_id AND b.household_id = $h))
         OR (o.entity_type = 'HouseholdSettings' AND o.entity_id = $h AND EXISTS (SELECT 1 FROM household_settings s WHERE s.household_id = $h))
         OR (o.entity_type = 'DuplicateDismissal' AND EXISTS (SELECT 1 FROM duplicate_dismissals d WHERE d.id = o.entity_id AND d.household_id = $h)))
        """;

    public async Task PrepareForPushAsync(HouseholdId household, UserId user, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await RewriteThisDeviceAsync(connection, transaction, household, user, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>The server rejects <c>this-device</c> (Spec 011 §5); these records were never on the server, so no version moves.</summary>
    internal static async Task RewriteThisDeviceAsync(
        SqliteConnection connection, SqliteTransaction transaction, HouseholdId household, UserId user, CancellationToken cancellationToken)
    {
        var person = Columns.Person(Coo.Domain.CareLog.PersonRef.User(user));
        var h = Columns.Id(household.Value);
        await connection.ExecuteAsync(
            "UPDATE care_entries SET logged_by = $person WHERE household_id = $h AND logged_by = 'this-device';",
            cancellationToken, transaction, ("$person", person), ("$h", h));
        await connection.ExecuteAsync(
            "UPDATE care_entries SET subject_person = $person WHERE household_id = $h AND subject_person = 'this-device';",
            cancellationToken, transaction, ("$person", person), ("$h", h));
    }

    public async Task<IReadOnlyList<OutgoingChange>> NextBatchAsync(HouseholdId household, int max, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        var groups = new List<(string Type, string Id, long LastSeq)>();
        await using (var command = connection.Command(
            $"""
            SELECT o.entity_type, o.entity_id, max(o.seq) FROM outbox o WHERE {InHousehold}
            GROUP BY o.entity_type, o.entity_id ORDER BY min(o.seq) LIMIT $max;
            """,
            null, ("$h", Columns.Id(household.Value)), ("$max", max)))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                groups.Add((reader.GetString(0), reader.GetString(1), reader.GetInt64(2)));
        }

        var changes = new List<OutgoingChange>(groups.Count);
        foreach (var (type, id, lastSeq) in groups)
        {
            var changeId = (string)(await connection.ScalarAsync("SELECT change_id FROM outbox WHERE seq = $seq;", cancellationToken, null, ("$seq", lastSeq)))!;
            if (await ReadStateAsync(connection, type, id, cancellationToken) is not { } state)
                continue;
            changes.Add(new OutgoingChange(
                Guid.Parse(changeId), type, Guid.Parse(id), state.IsDeleted, state.Version, lastSeq, state.IsDeleted ? null : state.Payload));
        }

        return changes;
    }

    public async Task ApplyResultsAsync(
        HouseholdId household, IReadOnlyList<OutgoingChange> changes, IReadOnlyList<PushResult> results, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(results);
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        foreach (var result in results)
        {
            if (changes.FirstOrDefault(c => c.ChangeId == result.ChangeId) is not { } change)
                continue;
            var key = new[] { ("$type", (object?)change.EntityType), ("$id", Columns.Id(change.EntityId)) };

            // A final result: this record's rows up to the change go; a later edit's row stays (ADR 0002 §2).
            await connection.ExecuteAsync(
                "DELETE FROM outbox WHERE entity_type = $type AND entity_id = $id AND seq <= $last;",
                cancellationToken, transaction, [.. key, ("$last", change.LastOutboxSeq)]);

            if (result.Outcome == PushOutcome.Rejected)
            {
                await connection.ExecuteAsync(
                    """
                    INSERT INTO sync_rejections (entity_type, entity_id, household_id, reason, rejected_utc_ms) VALUES ($type, $id, $h, $reason, $at)
                    ON CONFLICT (entity_type, entity_id) DO UPDATE SET household_id = excluded.household_id, reason = excluded.reason,
                        rejected_utc_ms = excluded.rejected_utc_ms;
                    """,
                    cancellationToken, transaction,
                    [.. key, ("$h", Columns.Id(household.Value)), ("$reason", string.IsNullOrEmpty(result.Reason) ? RejectionReasons.ChangeMalformed : result.Reason),
                        ("$at", now.ToUnixTimeMilliseconds())]);
            }
            else
            {
                await connection.ExecuteAsync("DELETE FROM sync_rejections WHERE entity_type = $type AND entity_id = $id;", cancellationToken, transaction, key);
            }
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<SyncCursor> GetCursorAsync(HouseholdId household, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        var h = ("$h", (object?)Columns.Id(household.Value));
        await using (var command = connection.Command("SELECT last_seq, bootstrap_head FROM sync_cursors WHERE household_id = $h;", null, h))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
                return new SyncCursor(reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetInt64(1));
        }

        // Ruling 20: a phone that holds records of this household must see every tombstone on its first pull.
        var holds = await connection.ScalarAsync(
            """
            SELECT EXISTS (SELECT 1 FROM care_entries WHERE household_id = $h) OR EXISTS (SELECT 1 FROM baby_profiles WHERE household_id = $h)
                OR EXISTS (SELECT 1 FROM duplicate_dismissals WHERE household_id = $h);
            """,
            cancellationToken, null, h);
        return new SyncCursor(0, Convert.ToInt64(holds, CultureInfo.InvariantCulture) == 1 ? 0 : null);
    }

    public async Task ApplyPageAsync(HouseholdId household, PulledPage page, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        foreach (var record in page.Records)
        {
            if (PullRule.KeepsLocal(await PendingVersionAsync(connection, transaction, record.EntityType, record.EntityId, cancellationToken), record.Version))
                continue;
            await UpsertAsync(connection, transaction, household, record, cancellationToken);
        }

        await connection.ExecuteAsync(
            """
            INSERT INTO sync_cursors (household_id, last_seq, bootstrap_head) VALUES ($h, $seq, $head)
            ON CONFLICT (household_id) DO UPDATE SET last_seq = excluded.last_seq, bootstrap_head = excluded.bootstrap_head;
            """,
            cancellationToken, transaction,
            ("$h", Columns.Id(household.Value)), ("$seq", page.Cursor), ("$head", page.HasMore ? page.BootstrapHead : null));
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<SyncCounts> CountsAsync(HouseholdId household, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        var h = ("$h", (object?)Columns.Id(household.Value));
        var cursor = await connection.ScalarAsync("SELECT last_seq FROM sync_cursors WHERE household_id = $h;", cancellationToken, null, h);
        var outbox = await connection.ScalarAsync(
            $"SELECT count(*) FROM (SELECT DISTINCT o.entity_type, o.entity_id FROM outbox o WHERE {InHousehold});", cancellationToken, null, h);
        var rejections = await connection.ScalarAsync("SELECT count(*) FROM sync_rejections WHERE household_id = $h;", cancellationToken, null, h);
        return new SyncCounts(
            cursor is null ? 0 : Convert.ToInt64(cursor, CultureInfo.InvariantCulture),
            Convert.ToInt32(outbox, CultureInfo.InvariantCulture),
            Convert.ToInt32(rejections, CultureInfo.InvariantCulture));
    }

    public async Task<IReadOnlyList<SyncRejection>> ListRejectionsAsync(HouseholdId household, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.Command(
            "SELECT entity_type, entity_id, reason, rejected_utc_ms FROM sync_rejections WHERE household_id = $h ORDER BY rejected_utc_ms DESC, entity_id;",
            null, ("$h", Columns.Id(household.Value)));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var list = new List<SyncRejection>();
        while (await reader.ReadAsync(cancellationToken))
        {
            if (Columns.ParseGuid(reader.GetString(1)) is { } id)
                list.Add(new SyncRejection(reader.GetString(0), id, reader.GetString(2), DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3))));
        }

        return list;
    }

    private sealed record State(HlcStamp Version, bool IsDeleted, RecordPayload Payload);

    private static async Task<State?> ReadStateAsync(SqliteConnection connection, string type, string id, CancellationToken cancellationToken)
    {
        switch (type)
        {
            case SyncEntityTypes.CareEntry:
            {
                var rows = await CareLogStore.ReadRowsAsync(connection, $"SELECT {CareEntryRow.SelectColumns} FROM care_entries WHERE id = $id;", cancellationToken, ("$id", id));
                if (rows.Count == 0 || Columns.ParseStamp(rows[0].VersionWallMs, rows[0].VersionCounter, rows[0].VersionDevice) is not { } version)
                    return null;
                var r = rows[0];
                var start = Columns.ParseInstant(r.StartUtcMs, r.StartOffsetMin) ?? DateTimeOffset.FromUnixTimeMilliseconds(r.StartUtcMs);
                var end = r.EndUtcMs is { } endMs ? Columns.ParseInstant(endMs, r.EndOffsetMin ?? 0) ?? DateTimeOffset.FromUnixTimeMilliseconds(endMs) : (DateTimeOffset?)null;
                return new State(version, r.IsDeleted, new CareEntryPayload(
                    r.Kind, Columns.ParseGuid(r.SubjectBabyId), r.SubjectPerson, r.IsSession, start, end, r.LoggedBy, r.Source, r.SourcePromptVersion,
                    r.Note, r.Details));
            }

            case SyncEntityTypes.BabyProfile:
            {
                await using var command = connection.Command(
                    "SELECT name, nicknames, birth_date, due_date, version_wall_ms, version_counter, version_device, is_deleted FROM baby_profiles WHERE id = $id;",
                    null, ("$id", id));
                await using var r = await command.ExecuteReaderAsync(cancellationToken);
                if (!await r.ReadAsync(cancellationToken) || Columns.ParseStamp(r.GetInt64(4), r.GetInt64(5), r.GetString(6)) is not { } version)
                    return null;
                return new State(version, r.GetInt64(7) == 1, new BabyProfilePayload(
                    r.GetString(0), JsonSerializer.Deserialize(r.GetString(1), StringListJson.Default.ListString) ?? [],
                    r.IsDBNull(2) ? null : DateOnly.ParseExact(r.GetString(2), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                    r.IsDBNull(3) ? null : DateOnly.ParseExact(r.GetString(3), "yyyy-MM-dd", CultureInfo.InvariantCulture)));
            }

            case SyncEntityTypes.HouseholdSettings:
            {
                await using var command = connection.Command(
                    "SELECT volume_unit, time_zone_id, hidden_kinds, version_wall_ms, version_counter, version_device, is_deleted FROM household_settings WHERE household_id = $id;",
                    null, ("$id", id));
                await using var r = await command.ExecuteReaderAsync(cancellationToken);
                if (!await r.ReadAsync(cancellationToken) || Columns.ParseStamp(r.GetInt64(3), r.GetInt64(4), r.GetString(5)) is not { } version)
                    return null;
                return new State(version, r.GetInt64(6) == 1, new HouseholdSettingsPayload(
                    r.GetString(0), r.GetString(1), JsonSerializer.Deserialize(r.GetString(2), StringListJson.Default.ListString) ?? []));
            }

            case SyncEntityTypes.DuplicateDismissal:
            {
                await using var command = connection.Command(
                    "SELECT entry_a, entry_b, version_wall_ms, version_counter, version_device, is_deleted FROM duplicate_dismissals WHERE id = $id;",
                    null, ("$id", id));
                await using var r = await command.ExecuteReaderAsync(cancellationToken);
                if (!await r.ReadAsync(cancellationToken) || Columns.ParseStamp(r.GetInt64(2), r.GetInt64(3), r.GetString(4)) is not { } version)
                    return null;
                return new State(version, r.GetInt64(5) == 1, new DismissalPayload(Guid.Parse(r.GetString(0)), Guid.Parse(r.GetString(1))));
            }

            default:
                return null;
        }
    }

    /// <summary>The local version when the record has an outbox row: that is a pending change (ADR 0002 §6).</summary>
    private static async Task<HlcStamp?> PendingVersionAsync(
        SqliteConnection connection, SqliteTransaction transaction, string type, Guid id, CancellationToken cancellationToken)
    {
        var key = Columns.Id(id);
        var pending = await connection.ScalarAsync(
            "SELECT EXISTS (SELECT 1 FROM outbox WHERE entity_type = $type AND entity_id = $id);", cancellationToken, transaction, ("$type", type), ("$id", key));
        if (Convert.ToInt64(pending, CultureInfo.InvariantCulture) == 0)
            return null;

        var (table, column) = type switch
        {
            SyncEntityTypes.CareEntry => ("care_entries", "id"),
            SyncEntityTypes.BabyProfile => ("baby_profiles", "id"),
            SyncEntityTypes.HouseholdSettings => ("household_settings", "household_id"),
            _ => ("duplicate_dismissals", "id"),
        };
        await using var command = connection.Command(
            $"SELECT version_wall_ms, version_counter, version_device FROM {table} WHERE {column} = $id;", transaction, ("$id", key));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Columns.ParseStamp(reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2)) : null;
    }

    private static Task UpsertAsync(SqliteConnection connection, SqliteTransaction transaction, HouseholdId household, PulledRecord record, CancellationToken cancellationToken)
    {
        var h = Columns.Id(household.Value);
        var id = Columns.Id(record.EntityId);
        var deleted = record.IsDeleted ? 1 : 0;
        switch (record.Payload)
        {
            case CareEntryPayload c:
            {
                var (startMs, startOffset) = Columns.Instant(c.StartAt);
                var endParts = c.EndAt is { } e ? Columns.Instant(e) : ((long, int)?)null;
                var row = new CareEntryRow(
                    id, h, c.Kind, c.SubjectBabyId is { } b ? Columns.Id(b) : null, c.SubjectPerson, c.IsSession, startMs, startOffset,
                    endParts?.Item1, endParts?.Item2, c.LoggedBy, c.Source, c.SourcePromptVersion, c.Note, c.DetailsJson,
                    record.Version.WallClockMs, record.Version.Counter, Columns.Id(record.Version.DeviceId.Value), record.IsDeleted, CareEntryClassifier.Ok);
                var classified = CareEntryClassifier.Classify(row);
                return connection.ExecuteAsync(
                    """
                    INSERT INTO care_entries (id, household_id, kind, subject_baby_id, subject_person, is_session, start_utc_ms, start_offset_min,
                        end_utc_ms, end_offset_min, logged_by, source, source_prompt_version, note, details, version_wall_ms, version_counter,
                        version_device, is_deleted, integrity, integrity_reason)
                    VALUES ($id, $h, $kind, $baby, $person, $session, $start, $start_offset, $end, $end_offset, $logged_by, $source, $prompt, $note,
                        $details, $version_wall_ms, $version_counter, $version_device, $deleted, $integrity, $reason)
                    ON CONFLICT (id) DO UPDATE SET household_id = excluded.household_id, kind = excluded.kind, subject_baby_id = excluded.subject_baby_id,
                        subject_person = excluded.subject_person, is_session = excluded.is_session, start_utc_ms = excluded.start_utc_ms,
                        start_offset_min = excluded.start_offset_min, end_utc_ms = excluded.end_utc_ms, end_offset_min = excluded.end_offset_min,
                        logged_by = excluded.logged_by, source = excluded.source, source_prompt_version = excluded.source_prompt_version,
                        note = excluded.note, details = excluded.details, version_wall_ms = excluded.version_wall_ms,
                        version_counter = excluded.version_counter, version_device = excluded.version_device, is_deleted = excluded.is_deleted,
                        integrity = excluded.integrity, integrity_reason = excluded.integrity_reason;
                    """,
                    cancellationToken, transaction,
                    [
                        ("$id", id), ("$h", h), ("$kind", c.Kind), ("$baby", row.SubjectBabyId), ("$person", c.SubjectPerson),
                        ("$session", c.IsSession ? 1 : 0), ("$start", startMs), ("$start_offset", startOffset), ("$end", endParts?.Item1),
                        ("$end_offset", endParts?.Item2), ("$logged_by", c.LoggedBy), ("$source", c.Source), ("$prompt", c.SourcePromptVersion),
                        ("$note", c.Note), ("$details", c.DetailsJson), .. Columns.Stamp(record.Version), ("$deleted", deleted),
                        ("$integrity", classified.Integrity), ("$reason", classified.Reason),
                    ]);
            }

            case BabyProfilePayload b:
                return connection.ExecuteAsync(
                    """
                    INSERT INTO baby_profiles (id, household_id, name, nicknames, birth_date, due_date, version_wall_ms, version_counter, version_device, is_deleted)
                    VALUES ($id, $h, $name, $nicknames, $birth, $due, $version_wall_ms, $version_counter, $version_device, $deleted)
                    ON CONFLICT (id) DO UPDATE SET household_id = excluded.household_id, name = excluded.name, nicknames = excluded.nicknames,
                        birth_date = excluded.birth_date, due_date = excluded.due_date, version_wall_ms = excluded.version_wall_ms,
                        version_counter = excluded.version_counter, version_device = excluded.version_device, is_deleted = excluded.is_deleted;
                    """,
                    cancellationToken, transaction,
                    [
                        ("$id", id), ("$h", h), ("$name", b.Name), ("$nicknames", JsonSerializer.Serialize(b.Nicknames.ToList(), StringListJson.Default.ListString)),
                        ("$birth", b.BirthDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                        ("$due", b.DueDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), .. Columns.Stamp(record.Version), ("$deleted", deleted),
                    ]);

            case HouseholdSettingsPayload s:
                return connection.ExecuteAsync(
                    """
                    INSERT INTO household_settings (household_id, volume_unit, time_zone_id, hidden_kinds, version_wall_ms, version_counter, version_device, is_deleted)
                    VALUES ($h, $unit, $zone, $hidden, $version_wall_ms, $version_counter, $version_device, $deleted)
                    ON CONFLICT (household_id) DO UPDATE SET volume_unit = excluded.volume_unit, time_zone_id = excluded.time_zone_id,
                        hidden_kinds = excluded.hidden_kinds, version_wall_ms = excluded.version_wall_ms, version_counter = excluded.version_counter,
                        version_device = excluded.version_device, is_deleted = excluded.is_deleted;
                    """,
                    cancellationToken, transaction,
                    [
                        ("$h", h), ("$unit", s.VolumeUnit), ("$zone", s.TimeZoneId),
                        ("$hidden", JsonSerializer.Serialize(s.HiddenKinds.Order(StringComparer.Ordinal).ToList(), StringListJson.Default.ListString)),
                        .. Columns.Stamp(record.Version), ("$deleted", deleted),
                    ]);

            case DismissalPayload d:
            {
                var (a, b) = (Columns.Id(d.EntryA), Columns.Id(d.EntryB));
                if (string.CompareOrdinal(a, b) > 0)
                    (a, b) = (b, a);
                return connection.ExecuteAsync(
                    """
                    INSERT INTO duplicate_dismissals (id, household_id, entry_a, entry_b, version_wall_ms, version_counter, version_device, is_deleted)
                    VALUES ($id, $h, $a, $b, $version_wall_ms, $version_counter, $version_device, $deleted)
                    ON CONFLICT (id) DO UPDATE SET household_id = excluded.household_id, version_wall_ms = excluded.version_wall_ms,
                        version_counter = excluded.version_counter, version_device = excluded.version_device, is_deleted = excluded.is_deleted;
                    """,
                    cancellationToken, transaction,
                    [("$id", id), ("$h", h), ("$a", a), ("$b", b), .. Columns.Stamp(record.Version), ("$deleted", deleted)]);
            }

            default:
                return Task.CompletedTask;
        }
    }
}
```

(`SqliteExtensions.ExecuteAsync` takes `params (string, object?)[]`; the collection expressions above build that
array. If the compiler wants explicit `(string Name, object? Value)` element types, cast the first tuple.)

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Coo.Mobile.Infrastructure.Tests`
Expected: PASS. The architecture test `MobileInfrastructure_OutsidePersistence_DoesNotDependOnSqlite` stays green
(the store is in `Persistence`).

- [ ] **Step 5: Commit**

```bash
dotnet format Coo.Tests.slnf
git add src/Mobile/Coo.Mobile.Infrastructure tests/Coo.Mobile.Infrastructure.Tests
git commit -m "Phone sync store: one change per record, results in one transaction, rejections kept, pulled records as received, the cursor with its page (Spec 011 §6)"
```

---
### Task 14: Bringing entries over, `created`, Fresh bootstrap and removal, each in one SQLite transaction

**Files:**
- Create: `src/Mobile/Coo.Mobile.Infrastructure/Persistence/HouseholdMoves.cs`, `SyncState.cs`
- Modify: `src/Mobile/Coo.Mobile.Infrastructure/Persistence/SyncStore.cs` (`InHousehold` becomes `internal`)
- Modify: `src/Mobile/Coo.Mobile.Infrastructure/ServiceCollectionExtensions.cs` (registers `ISyncStore`, `IHouseholdMoves`, `ISyncState`)
- Create: `src/Mobile/Coo.Mobile.Application/Sync/BringEntriesOver.cs`; modify `ServiceCollectionExtensions.cs`
- Test: `tests/Coo.Mobile.Infrastructure.Tests/HouseholdMovesTests.cs`, `tests/Coo.Mobile.Application.Tests/BringEntriesOverTests.cs` (create)

**Interfaces:**
- Consumes: Task 11's `IHouseholdMoves`, `ISyncState`, `BringOverPlan`, `LocalSummary`; Task 13's
  `SyncStore.RewriteThisDeviceAsync` and `SyncStore.InHousehold`; `Account.TryCompleteJoinAsync` (Task 9).
- Produces:
  - `internal sealed class HouseholdMoves(LocalDatabase database) : IHouseholdMoves`,
    `internal sealed class SyncState(LocalDatabase database) : ISyncState`.
  - `BringOverChoice(LocalSummary Local, IReadOnlyList<LocalBaby> Family)`;
    `BringEntriesOver(ISessionStore, IHouseholdMoves, Account, ISyncControl)` with
    `PrepareAsync(CancellationToken)` → `BringOverChoice?` and
    `ExecuteAsync(IReadOnlyDictionary<BabyId, BabyId?> mapping, CancellationToken)` → `bool`.

- [ ] **Step 1: Write the failing tests**

`tests/Coo.Mobile.Infrastructure.Tests/HouseholdMovesTests.cs`:

```csharp
using System.Text;
using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Duplicates;
using Coo.Domain.Common;
using Coo.Domain.Households;
using Coo.Mobile.Application.Sync;
using Coo.Mobile.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace Coo.Mobile.Infrastructure.Tests;

/// <summary>Spec 011 §4 and §11 on a real SQLite file: each move is one transaction, and a failure changes nothing.</summary>
public sealed class HouseholdMovesTests
{
    private static readonly HouseholdId Account = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a9"));
    private static readonly BabyId FamilyBaby = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000b9"));
    private static readonly UserId User = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000c1"));

    /// <summary>This phone's provisional household with a baby, its settings, an entry logged before sign-in, and a dismissal.</summary>
    private static async Task<(TestDatabase Db, CareEntry Entry)> PhoneAsync()
    {
        var db = await TestDatabase.CreateAsync();
        await db.Households.SaveSettingsAsync(HouseholdSettings.Create(TestDatabase.Household, VolumeUnit.Millilitres, "Europe/Amsterdam",
            await db.Device.NextAsync(CancellationToken.None)).Value, CancellationToken.None);
        await db.Households.SaveBabyAsync(BabyProfile.Create(TestDatabase.Baby, TestDatabase.Household, "Maja", [], new DateOnly(2026, 8, 20), null,
            await db.Device.NextAsync(CancellationToken.None), new DateOnly(2026, 10, 1)).Value, CancellationToken.None);
        var entry = CareEntry.Create(await db.NewIdAsync(), TestDatabase.Household, EntrySubject.Baby(TestDatabase.Baby),
            new Coo.Domain.CareLog.Kinds.Diaper.DiaperDetails(Coo.Domain.CareLog.Kinds.Diaper.DiaperContents.Wet), EntryTiming.Point(db.Time.GetLocalNow()),
            PersonRef.ThisDevicesPerson, EntrySource.Manual, await db.Device.NextAsync(CancellationToken.None), db.Time.GetUtcNow()).Value;
        await db.Store.SaveAsync(entry, CancellationToken.None);
        await new DuplicateDismissalStore(db.Database, db.Time, db.Writes).SaveAsync(DuplicateDismissal.Create(
            new DismissalId(Guid.CreateVersion7(db.Time.GetUtcNow())), TestDatabase.Household, new EntryPair(entry.Id, await db.NewIdAsync()),
            await db.Device.NextAsync(CancellationToken.None)), CancellationToken.None);
        return (db, entry);
    }

    /// <summary>The family's household as the join-pending bootstrap pulled it: settings and a baby, no outbox rows.</summary>
    private static async Task PullFamilyAsync(TestDatabase db)
    {
        await using var connection = await db.OpenAsync();
        await connection.ExecuteAsync(
            $"""
            INSERT INTO household_settings (household_id, volume_unit, time_zone_id, hidden_kinds, version_wall_ms, version_counter, version_device)
            VALUES ('{Account.Value}', 'oz', 'Europe/Warsaw', '[]', 1, 0, '{TestDatabase.Baby.Value}');
            INSERT INTO baby_profiles (id, household_id, name, nicknames, birth_date, version_wall_ms, version_counter, version_device)
            VALUES ('{FamilyBaby.Value}', '{Account.Value}', 'Maja', '[]', '2026-08-20', 1, 0, '{TestDatabase.Baby.Value}');
            """, CancellationToken.None);
    }

    private static async Task<string> SnapshotAsync(TestDatabase db)
    {
        var text = new StringBuilder();
        await using var connection = await db.OpenAsync();
        foreach (var table in new[] { "care_entries", "baby_profiles", "household_settings", "duplicate_dismissals", "outbox", "app_state", "sync_cursors" })
        {
            await using var command = connection.Command($"SELECT * FROM {table} ORDER BY rowid;");
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                for (var i = 0; i < reader.FieldCount; i++)
                    text.Append(reader.IsDBNull(i) ? "∅" : reader.GetValue(i)).Append('|');
                text.AppendLine();
            }
        }

        return text.ToString();
    }

    private static async Task BreakAsync(TestDatabase db, string trigger)
    {
        await using var connection = await db.OpenAsync();
        await connection.ExecuteAsync(trigger, CancellationToken.None);
    }

    [Fact]
    public async Task ActiveHouseholdAsync_NoneNamed_NamesTheFirstLiveOneAndKeepsIt()
    {
        // Arrange
        var (db, _) = await PhoneAsync();
        await using var cleanup = db;

        // Act
        var active = await new HouseholdMoves(db.Database).ActiveHouseholdAsync(CancellationToken.None);
        await PullFamilyAsync(db);
        var again = await new HouseholdMoves(db.Database).ActiveHouseholdAsync(CancellationToken.None);

        // Assert
        active.Should().Be(TestDatabase.Household);
        again.Should().Be(TestDatabase.Household, "a pulled household never takes over the screen by itself");
    }

    [Fact]
    public async Task MoveAllAsync_Created_EveryRecordMovesTheSettingsKeepTheirOutboxRowAndItBecomesActive()
    {
        // Arrange
        var (db, entry) = await PhoneAsync();
        await using var cleanup = db;
        var outboxBefore = await db.ScalarAsync("SELECT count(*) FROM outbox;");

        // Act
        await new HouseholdMoves(db.Database).MoveAllAsync(TestDatabase.Household, Account, User, CancellationToken.None);

        // Assert
        (await db.ScalarAsync($"SELECT count(*) FROM care_entries WHERE household_id = '{Account.Value}' AND logged_by = 'user:{User.Value}';")).Should().Be(1);
        (await db.ScalarAsync($"SELECT count(*) FROM baby_profiles WHERE household_id = '{Account.Value}';")).Should().Be(1);
        (await db.ScalarAsync($"SELECT count(*) FROM duplicate_dismissals WHERE household_id = '{Account.Value}';")).Should().Be(1);
        (await db.ScalarAsync($"SELECT count(*) FROM household_settings WHERE household_id = '{Account.Value}';")).Should().Be(1);
        (await db.ScalarAsync($"SELECT count(*) FROM outbox WHERE entity_type = 'HouseholdSettings' AND entity_id = '{Account.Value}';")).Should().Be(1);
        (await db.ScalarAsync("SELECT count(*) FROM outbox;")).Should().Be(outboxBefore);
        (await new HouseholdMoves(db.Database).ActiveHouseholdAsync(CancellationToken.None)).Should().Be(Account);
        (await db.Store.FindAsync(entry.Id, CancellationToken.None)).Should().BeOfType<Coo.Mobile.Application.CareLog.LiveEntry>()
            .Which.Entry.Version.Should().Be(entry.Version, "these records were never on the server: no version moves");
    }

    [Fact]
    public async Task MoveAllAsync_AFailureHalfWay_ChangesNothing()
    {
        // Arrange
        var (db, _) = await PhoneAsync();
        await using var cleanup = db;
        await BreakAsync(db, "CREATE TRIGGER t_break BEFORE UPDATE ON household_settings BEGIN SELECT RAISE(ABORT, 'test'); END;");
        var before = await SnapshotAsync(db);

        // Act
        var act = async () => await new HouseholdMoves(db.Database).MoveAllAsync(TestDatabase.Household, Account, User, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<SqliteException>();
        (await SnapshotAsync(db)).Should().Be(before);
    }

    [Fact]
    public async Task BringOverAsync_LocalBabyMappedToTheFamilys_EntriesFollowAndTheLocalProfileAndSettingsGo()
    {
        // Arrange
        var (db, entry) = await PhoneAsync();
        await using var cleanup = db;
        await PullFamilyAsync(db);
        var plan = new BringOverPlan(TestDatabase.Household, Account, new Dictionary<BabyId, BabyId?> { [TestDatabase.Baby] = FamilyBaby });

        // Act
        await new HouseholdMoves(db.Database).BringOverAsync(plan, User, CancellationToken.None);

        // Assert
        (await db.ScalarAsync(
            $"SELECT count(*) FROM care_entries WHERE id = '{entry.Id.Value}' AND household_id = '{Account.Value}' AND subject_baby_id = '{FamilyBaby.Value}' AND logged_by = 'user:{User.Value}';"))
            .Should().Be(1);
        (await db.ScalarAsync($"SELECT count(*) FROM baby_profiles WHERE id = '{TestDatabase.Baby.Value}';")).Should().Be(0);
        (await db.ScalarAsync($"SELECT count(*) FROM outbox WHERE entity_id IN ('{TestDatabase.Baby.Value}', '{TestDatabase.Household.Value}');")).Should().Be(0);
        (await db.ScalarAsync($"SELECT count(*) FROM household_settings WHERE household_id = '{TestDatabase.Household.Value}';")).Should().Be(0);
        (await db.ScalarAsync($"SELECT count(*) FROM duplicate_dismissals WHERE household_id = '{Account.Value}';")).Should().Be(1);
        (await db.ScalarAsync($"SELECT count(*) FROM outbox WHERE entity_type = 'CareEntry' AND entity_id = '{entry.Id.Value}';")).Should().Be(1, "the push sends the moved state");
        (await new HouseholdMoves(db.Database).ActiveHouseholdAsync(CancellationToken.None)).Should().Be(Account);
    }

    [Fact]
    public async Task BringOverAsync_AddAsANewBaby_MovesTheProfileWithItsOutboxRow()
    {
        // Arrange
        var (db, _) = await PhoneAsync();
        await using var cleanup = db;
        await PullFamilyAsync(db);
        var plan = new BringOverPlan(TestDatabase.Household, Account, new Dictionary<BabyId, BabyId?> { [TestDatabase.Baby] = null });

        // Act
        await new HouseholdMoves(db.Database).BringOverAsync(plan, User, CancellationToken.None);

        // Assert
        (await db.ScalarAsync($"SELECT count(*) FROM baby_profiles WHERE id = '{TestDatabase.Baby.Value}' AND household_id = '{Account.Value}';")).Should().Be(1);
        (await db.ScalarAsync($"SELECT count(*) FROM outbox WHERE entity_type = 'BabyProfile' AND entity_id = '{TestDatabase.Baby.Value}';")).Should().Be(1);
    }

    [Fact]
    public async Task BringOverAsync_AFailureHalfWay_ChangesNothing()
    {
        // Arrange: the last step (dropping the provisional settings) fails.
        var (db, _) = await PhoneAsync();
        await using var cleanup = db;
        await PullFamilyAsync(db);
        await BreakAsync(db, "CREATE TRIGGER t_break BEFORE DELETE ON household_settings BEGIN SELECT RAISE(ABORT, 'test'); END;");
        var before = await SnapshotAsync(db);
        var plan = new BringOverPlan(TestDatabase.Household, Account, new Dictionary<BabyId, BabyId?> { [TestDatabase.Baby] = FamilyBaby });

        // Act
        var act = async () => await new HouseholdMoves(db.Database).BringOverAsync(plan, User, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<SqliteException>();
        (await SnapshotAsync(db)).Should().Be(before);
    }

    [Fact]
    public async Task FreshBootstrapAsync_PendingChanges_RefusesAndKeepsEverything()
    {
        // Arrange
        var (db, _) = await PhoneAsync();
        await using var cleanup = db;
        var before = await SnapshotAsync(db);

        // Act
        var done = await new HouseholdMoves(db.Database).FreshBootstrapAsync(TestDatabase.Household, CancellationToken.None);

        // Assert
        done.Should().BeFalse();
        (await SnapshotAsync(db)).Should().Be(before);
    }

    [Fact]
    public async Task FreshBootstrapAsync_EmptyOutbox_ClearsTheRecordsKeepsTheSettingsAndStartsFromZero()
    {
        // Arrange
        var (db, _) = await PhoneAsync();
        await using var cleanup = db;
        await db.ExecuteAsync("DELETE FROM outbox;");
        await db.ExecuteAsync($"INSERT INTO sync_cursors (household_id, last_seq) VALUES ('{TestDatabase.Household.Value}', 42);");

        // Act
        var done = await new HouseholdMoves(db.Database).FreshBootstrapAsync(TestDatabase.Household, CancellationToken.None);

        // Assert
        done.Should().BeTrue();
        (await db.ScalarAsync("SELECT (SELECT count(*) FROM care_entries) + (SELECT count(*) FROM baby_profiles) + (SELECT count(*) FROM duplicate_dismissals);")).Should().Be(0);
        (await db.ScalarAsync("SELECT count(*) FROM household_settings;")).Should().Be(1);
        (await new SyncStore(db.Database).GetCursorAsync(TestDatabase.Household, CancellationToken.None)).Should().Be(new SyncCursor(0, null));
    }

    [Fact]
    public async Task DeleteHouseholdAsync_ARemovedHousehold_GoesWithItsOutboxAndLeavesTheOtherHousehold()
    {
        // Arrange
        var (db, _) = await PhoneAsync();
        await using var cleanup = db;
        await PullFamilyAsync(db);

        // Act
        await new HouseholdMoves(db.Database).DeleteHouseholdAsync(TestDatabase.Household, CancellationToken.None);

        // Assert
        (await db.ScalarAsync($"SELECT count(*) FROM care_entries WHERE household_id = '{TestDatabase.Household.Value}';")).Should().Be(0);
        (await db.ScalarAsync("SELECT count(*) FROM outbox;")).Should().Be(0);
        (await db.ScalarAsync($"SELECT count(*) FROM baby_profiles WHERE household_id = '{Account.Value}';")).Should().Be(1);
    }

    [Fact]
    public async Task SyncState_ConsentAndSoftLock_RoundTripPerAccountAndHousehold()
    {
        // Arrange
        await using var db = await TestDatabase.CreateAsync();
        var state = new SyncState(db.Database);

        // Act
        await state.SetConsentAsync(User, ConsentState.Given, CancellationToken.None);
        await state.SetSoftLockAsync(Account, true, CancellationToken.None);

        // Assert
        (await state.GetConsentAsync(User, CancellationToken.None)).Should().Be(ConsentState.Given);
        (await state.GetConsentAsync(new UserId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000c2")), CancellationToken.None)).Should().Be(ConsentState.Unknown);
        (await state.IsSoftLockedAsync(Account, CancellationToken.None)).Should().BeTrue();
        await state.SetSoftLockAsync(Account, false, CancellationToken.None);
        (await state.IsSoftLockedAsync(Account, CancellationToken.None)).Should().BeFalse();
    }
}
```

(Add `public async Task ExecuteAsync(string sql)` to `TestDatabase` if it lacks one: open, `ExecuteAsync(sql, CancellationToken.None)`.)

`tests/Coo.Mobile.Application.Tests/BringEntriesOverTests.cs`:

```csharp
using Coo.Domain.Common;
using Coo.Mobile.Application.Auth;
using Coo.Mobile.Application.Sync;
using NSubstitute;
using static Coo.Mobile.Application.Tests.SyncCycleTests;

namespace Coo.Mobile.Application.Tests;

/// <summary>Spec 011 §4: the choice, and the plan that one transaction carries out.</summary>
public sealed class BringEntriesOverTests : IDisposable
{
    private static readonly BabyId LocalBaby = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000b1"));
    private static readonly BabyId FamilyBaby = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000b9"));

    private readonly MemorySessions _sessions = new();
    private readonly Account _account;
    private readonly IHouseholdMoves _moves = Substitute.For<IHouseholdMoves>();
    private readonly ISyncControl _sync = Substitute.For<ISyncControl>();

    public BringEntriesOverTests()
    {
        _account = new Account(_sessions);
        _moves.ActiveHouseholdAsync(Arg.Any<CancellationToken>()).Returns(Provisional);
        _moves.SummaryAsync(Provisional, Arg.Any<CancellationToken>()).Returns(new LocalSummary(42, [new(LocalBaby, "Maja")]));
        _moves.SummaryAsync(Household, Arg.Any<CancellationToken>()).Returns(new LocalSummary(300, [new(FamilyBaby, "Maja")]));
    }

    public void Dispose() => _account.Dispose();

    private BringEntriesOver UseCase() => new(_sessions, _moves, _account, _sync);

    private Task JoinPendingAsync() => _account.SignedInAsync(
        new StoredSession("access", DateTimeOffset.UnixEpoch, "refresh", Profile, Household, JoinPending: true), CancellationToken.None);

    [Fact]
    public async Task PrepareAsync_JoinPending_ReturnsThisPhonesEntriesAndTheFamilysBabies()
    {
        // Arrange
        await JoinPendingAsync();

        // Act
        var choice = await UseCase().PrepareAsync(CancellationToken.None);

        // Assert
        choice!.Local.EntryCount.Should().Be(42);
        choice.Family.Should().ContainSingle().Which.Id.Should().Be(FamilyBaby);
    }

    [Fact]
    public async Task ExecuteAsync_MappedToAFamilyBaby_CarriesThePlanAndCompletesTheJoin()
    {
        // Arrange
        await JoinPendingAsync();

        // Act
        var done = await UseCase().ExecuteAsync(new Dictionary<BabyId, BabyId?> { [LocalBaby] = FamilyBaby }, CancellationToken.None);

        // Assert
        done.Should().BeTrue();
        await _moves.Received(1).BringOverAsync(
            Arg.Is<BringOverPlan>(p => p.From == Provisional && p.To == Household && p.Babies[LocalBaby] == FamilyBaby), User, Arg.Any<CancellationToken>());
        _account.State.Should().Be(new SignedIn(Profile));
        _sync.Received(1).RequestSync();
    }

    [Fact]
    public async Task ExecuteAsync_MappedToABabyOutsideTheFamily_AddsItAsNew()
    {
        // Arrange
        await JoinPendingAsync();

        // Act
        await UseCase().ExecuteAsync(new Dictionary<BabyId, BabyId?> { [LocalBaby] = new BabyId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000bb")) }, CancellationToken.None);

        // Assert
        await _moves.Received(1).BringOverAsync(Arg.Is<BringOverPlan>(p => p.Babies[LocalBaby] == null), User, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_NotJoinPending_DoesNothing()
    {
        // Arrange
        await _account.SignedInAsync(new StoredSession("access", DateTimeOffset.UnixEpoch, "refresh", Profile, Household, JoinPending: false), CancellationToken.None);

        // Act
        var done = await UseCase().ExecuteAsync(new Dictionary<BabyId, BabyId?>(), CancellationToken.None);

        // Assert
        done.Should().BeFalse();
        await _moves.DidNotReceiveWithAnyArgs().BringOverAsync(default!, default, default);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Coo.Mobile.Infrastructure.Tests --filter FullyQualifiedName~HouseholdMovesTests && dotnet test tests/Coo.Mobile.Application.Tests --filter FullyQualifiedName~BringEntriesOverTests`
Expected: FAIL to compile (`HouseholdMoves`, `SyncState`, `BringEntriesOver` not found). Paste the first error.

- [ ] **Step 3: The moves and the state**

`src/Mobile/Coo.Mobile.Infrastructure/Persistence/HouseholdMoves.cs`:

```csharp
using System.Globalization;
using Coo.Domain.Common;
using Coo.Mobile.Application.Sync;
using Microsoft.Data.Sqlite;

namespace Coo.Mobile.Infrastructure.Persistence;

/// <summary>
/// Records changing household on this phone (Spec 011 §4, §11; ADR 0003 §7). Each operation is one transaction: a failure
/// part-way changes nothing. Versions never move: the moved records were never on the server, and the outbox names
/// records, so the next push sends their moved state.
/// </summary>
internal sealed class HouseholdMoves(LocalDatabase database) : IHouseholdMoves
{
    private const string ActiveKey = "active_household";

    public async Task<HouseholdId> ActiveHouseholdAsync(CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        var named = (string?)await connection.ScalarAsync(
            """
            SELECT a.value FROM app_state a JOIN household_settings s ON s.household_id = a.value AND s.is_deleted = 0
            WHERE a.key = 'active_household';
            """,
            cancellationToken);
        if (Columns.ParseGuid(named) is { } active)
            return new HouseholdId(active);

        var first = (string?)await connection.ScalarAsync(
            "SELECT household_id FROM household_settings WHERE is_deleted = 0 ORDER BY household_id LIMIT 1;", cancellationToken)
            ?? throw new InvalidOperationException("There is no household on this phone: start-up creates one first.");
        await SetActiveAsync(connection, null, first, cancellationToken);
        return new HouseholdId(Guid.Parse(first));
    }

    public async Task MoveAllAsync(HouseholdId from, HouseholdId to, UserId user, CancellationToken cancellationToken)
    {
        if (from == to)
            return;
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var (f, t) = (Columns.Id(from.Value), Columns.Id(to.Value));
        foreach (var table in new[] { "care_entries", "baby_profiles", "duplicate_dismissals" })
            await connection.ExecuteAsync($"UPDATE {table} SET household_id = $to WHERE household_id = $from;", cancellationToken, transaction, ("$to", t), ("$from", f));

        // The settings row is keyed by its household: the row and its outbox rows take the new ID.
        await connection.ExecuteAsync("UPDATE household_settings SET household_id = $to WHERE household_id = $from;", cancellationToken, transaction, ("$to", t), ("$from", f));
        await connection.ExecuteAsync(
            "UPDATE outbox SET entity_id = $to WHERE entity_type = 'HouseholdSettings' AND entity_id = $from;", cancellationToken, transaction, ("$to", t), ("$from", f));
        await SyncStore.RewriteThisDeviceAsync(connection, transaction, to, user, cancellationToken);
        await SetActiveAsync(connection, transaction, t, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<LocalSummary> SummaryAsync(HouseholdId household, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        var h = ("$h", (object?)Columns.Id(household.Value));
        var count = Convert.ToInt32(
            await connection.ScalarAsync("SELECT count(*) FROM care_entries WHERE household_id = $h AND is_deleted = 0;", cancellationToken, null, h),
            CultureInfo.InvariantCulture);
        var babies = new List<LocalBaby>();
        await using var command = connection.Command(
            "SELECT id, name FROM baby_profiles WHERE household_id = $h AND is_deleted = 0 ORDER BY coalesce(birth_date, due_date), name;", null, h);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (Columns.ParseGuid(reader.GetString(0)) is { } id)
                babies.Add(new LocalBaby(new BabyId(id), reader.GetString(1)));
        }

        return new LocalSummary(count, babies);
    }

    public async Task BringOverAsync(BringOverPlan plan, UserId user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var (f, t) = (Columns.Id(plan.From.Value), Columns.Id(plan.To.Value));

        foreach (var (local, target) in plan.Babies)
        {
            var l = Columns.Id(local.Value);
            if (target is { } mapped)
            {
                // A mapped baby: its entries point at the family's baby; its own profile and outbox rows go (the household's win).
                await connection.ExecuteAsync(
                    "UPDATE care_entries SET subject_baby_id = $target WHERE household_id = $from AND subject_baby_id = $local;",
                    cancellationToken, transaction, ("$target", Columns.Id(mapped.Value)), ("$from", f), ("$local", l));
                await connection.ExecuteAsync("DELETE FROM outbox WHERE entity_type = 'BabyProfile' AND entity_id = $local;", cancellationToken, transaction, ("$local", l));
                await connection.ExecuteAsync("DELETE FROM baby_profiles WHERE id = $local;", cancellationToken, transaction, ("$local", l));
            }
            else
            {
                // Add as a new baby: the profile moves with its outbox row.
                await connection.ExecuteAsync("UPDATE baby_profiles SET household_id = $to WHERE id = $local;", cancellationToken, transaction, ("$to", t), ("$local", l));
            }
        }

        await connection.ExecuteAsync("UPDATE care_entries SET household_id = $to WHERE household_id = $from;", cancellationToken, transaction, ("$to", t), ("$from", f));
        await connection.ExecuteAsync("UPDATE duplicate_dismissals SET household_id = $to WHERE household_id = $from;", cancellationToken, transaction, ("$to", t), ("$from", f));

        // The provisional settings and their outbox rows go: the household's settings win.
        await connection.ExecuteAsync("DELETE FROM outbox WHERE entity_type = 'HouseholdSettings' AND entity_id = $from;", cancellationToken, transaction, ("$from", f));
        await connection.ExecuteAsync("DELETE FROM household_settings WHERE household_id = $from;", cancellationToken, transaction, ("$from", f));

        await SyncStore.RewriteThisDeviceAsync(connection, transaction, plan.To, user, cancellationToken);
        await SetActiveAsync(connection, transaction, t, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<bool> FreshBootstrapAsync(HouseholdId household, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var h = ("$h", (object?)Columns.Id(household.Value));
        var pending = Convert.ToInt64(
            await connection.ScalarAsync($"SELECT count(*) FROM outbox o WHERE {SyncStore.InHousehold};", cancellationToken, transaction, h),
            CultureInfo.InvariantCulture);
        if (pending > 0)
            return false;

        foreach (var table in new[] { "care_entries", "baby_profiles", "duplicate_dismissals", "sync_rejections" })
            await connection.ExecuteAsync($"DELETE FROM {table} WHERE household_id = $h;", cancellationToken, transaction, h);

        // A cursor of 0 with no head: the phone is empty now, so the server may skip tombstones (ruling 20).
        await connection.ExecuteAsync(
            """
            INSERT INTO sync_cursors (household_id, last_seq, bootstrap_head) VALUES ($h, 0, NULL)
            ON CONFLICT (household_id) DO UPDATE SET last_seq = 0, bootstrap_head = NULL;
            """,
            cancellationToken, transaction, h);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task DeleteHouseholdAsync(HouseholdId household, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var h = ("$h", (object?)Columns.Id(household.Value));
        await connection.ExecuteAsync($"DELETE FROM outbox AS o WHERE {SyncStore.InHousehold};", cancellationToken, transaction, h);
        foreach (var table in new[] { "care_entries", "baby_profiles", "duplicate_dismissals", "household_settings", "sync_rejections", "sync_cursors" })
            await connection.ExecuteAsync($"DELETE FROM {table} WHERE household_id = $h;", cancellationToken, transaction, h);
        await connection.ExecuteAsync(
            "DELETE FROM app_state WHERE key = 'softlock:' || $h OR (key = 'active_household' AND value = $h);", cancellationToken, transaction, h);
        await transaction.CommitAsync(cancellationToken);
    }

    private static Task SetActiveAsync(SqliteConnection connection, SqliteTransaction? transaction, string household, CancellationToken cancellationToken) =>
        connection.ExecuteAsync(
            "INSERT INTO app_state (key, value) VALUES ($key, $v) ON CONFLICT (key) DO UPDATE SET value = excluded.value;",
            cancellationToken, transaction, ("$key", ActiveKey), ("$v", household));
}
```

(In `DeleteHouseholdAsync`, the outbox rows must go **before** the records, because `InHousehold` finds them through
the records.)

`src/Mobile/Coo.Mobile.Infrastructure/Persistence/SyncState.cs`:

```csharp
using Coo.Contracts.Sync;
using Coo.Domain.Common;
using Coo.Mobile.Application.Sync;

namespace Coo.Mobile.Infrastructure.Persistence;

/// <summary>Consent per account and the soft-lock per household, as <c>app_state</c> rows (Spec 011 §3, §6.5).</summary>
internal sealed class SyncState(LocalDatabase database) : ISyncState
{
    private const string Declined = "declined";

    public async Task<ConsentState> GetConsentAsync(UserId user, CancellationToken cancellationToken) =>
        await GetAsync("consent:" + Columns.Id(user.Value), cancellationToken) switch
        {
            CareDataConsent.CurrentWording => ConsentState.Given,
            Declined => ConsentState.Declined,
            _ => ConsentState.Unknown,
        };

    public Task SetConsentAsync(UserId user, ConsentState state, CancellationToken cancellationToken) => state switch
    {
        ConsentState.Given => SetAsync("consent:" + Columns.Id(user.Value), CareDataConsent.CurrentWording, cancellationToken),
        ConsentState.Declined => SetAsync("consent:" + Columns.Id(user.Value), Declined, cancellationToken),
        _ => RemoveAsync("consent:" + Columns.Id(user.Value), cancellationToken),
    };

    public async Task<bool> IsSoftLockedAsync(HouseholdId household, CancellationToken cancellationToken) =>
        await GetAsync("softlock:" + Columns.Id(household.Value), cancellationToken) is not null;

    public Task SetSoftLockAsync(HouseholdId household, bool locked, CancellationToken cancellationToken) => locked
        ? SetAsync("softlock:" + Columns.Id(household.Value), "1", cancellationToken)
        : RemoveAsync("softlock:" + Columns.Id(household.Value), cancellationToken);

    private async Task<string?> GetAsync(string key, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        return (string?)await connection.ScalarAsync("SELECT value FROM app_state WHERE key = $key;", cancellationToken, null, ("$key", key));
    }

    private async Task SetAsync(string key, string value, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await connection.ExecuteAsync(
            "INSERT INTO app_state (key, value) VALUES ($key, $value) ON CONFLICT (key) DO UPDATE SET value = excluded.value;",
            cancellationToken, null, ("$key", key), ("$value", value));
    }

    private async Task RemoveAsync(string key, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await connection.ExecuteAsync("DELETE FROM app_state WHERE key = $key;", cancellationToken, null, ("$key", key));
    }
}
```

In `SyncStore`, change `private const string InHousehold` to `internal const string InHousehold`.

In `AddMobileInfrastructure`, after `IDuplicateDismissalStore`:

```csharp
        // Sync (Spec 011 §6).
        services.AddSingleton<ISyncStore, SyncStore>();
        services.AddSingleton<IHouseholdMoves, HouseholdMoves>();
        services.AddSingleton<ISyncState, SyncState>();
```

- [ ] **Step 4: The use case**

`src/Mobile/Coo.Mobile.Application/Sync/BringEntriesOver.cs`:

```csharp
using Coo.Domain.Common;
using Coo.Mobile.Application.Auth;

namespace Coo.Mobile.Application.Sync;

public sealed record BringOverChoice(LocalSummary Local, IReadOnlyList<LocalBaby> Family);

/// <summary>
/// "Bring my entries over" (Spec 011 §4): each local baby to a family baby, or added as new, then one transaction moves
/// everything and the phone shows the family's log. "Keep them separate" is simply not calling it (Q4).
/// </summary>
public sealed class BringEntriesOver(ISessionStore sessions, IHouseholdMoves moves, Account account, ISyncControl sync)
{
    /// <summary>What the sheet shows; null when this phone is not join-pending.</summary>
    public async Task<BringOverChoice?> PrepareAsync(CancellationToken cancellationToken)
    {
        if (await sessions.GetAsync(cancellationToken) is not { JoinPending: true } session)
            return null;
        var local = await moves.ActiveHouseholdAsync(cancellationToken);
        if (local == session.AccountHousehold)
            return null;
        var family = await moves.SummaryAsync(session.AccountHousehold, cancellationToken);
        return new BringOverChoice(await moves.SummaryAsync(local, cancellationToken), family.Babies);
    }

    /// <summary>A target that is not one of the family's babies counts as "Add as a new baby".</summary>
    public async Task<bool> ExecuteAsync(IReadOnlyDictionary<BabyId, BabyId?> mapping, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        var generation = account.Generation;
        if (await sessions.GetAsync(cancellationToken) is not { JoinPending: true } session)
            return false;
        var local = await moves.ActiveHouseholdAsync(cancellationToken);
        if (local != session.AccountHousehold)
        {
            var family = (await moves.SummaryAsync(session.AccountHousehold, cancellationToken)).Babies.Select(b => b.Id).ToHashSet();
            var babies = (await moves.SummaryAsync(local, cancellationToken)).Babies.ToDictionary(
                b => b.Id,
                b => mapping.TryGetValue(b.Id, out var target) && target is { } t && family.Contains(t) ? t : (BabyId?)null);
            await moves.BringOverAsync(new BringOverPlan(local, session.AccountHousehold, babies), session.Profile.UserId, cancellationToken);
        }

        // Signed out meanwhile: the move stands, and the next sign-in finds this phone already in the account's household.
        if (!await account.TryCompleteJoinAsync(generation, cancellationToken))
            return false;
        sync.RequestSync();
        return true;
    }
}
```

Register it in `AddMobileApplication`: `services.AddTransient<BringEntriesOver>();`.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/Coo.Mobile.Infrastructure.Tests && dotnet test tests/Coo.Mobile.Application.Tests`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
dotnet format Coo.Tests.slnf
git add src/Mobile tests/Coo.Mobile.Infrastructure.Tests tests/Coo.Mobile.Application.Tests
git commit -m "Phone: bring entries over and created as one transaction each, Fresh bootstrap, removal, consent and soft-lock state (Spec 011 §4, §11)"
```

---
### Task 15: The phone's sync HTTP client

**Files:**
- Create: `src/Mobile/Coo.Mobile.Infrastructure/Sync/SyncJsonContext.cs`, `SyncHttp.cs`, `SyncClient.cs`
- Modify: `src/Mobile/Coo.Mobile.Infrastructure/Auth/AccountClient.cs` (the consent from `/api/me`)
- Modify: `src/Mobile/Coo.Mobile.Infrastructure/ServiceCollectionExtensions.cs` (registers `ISyncApi`, `IConsentApi`)
- Test: `tests/Coo.Mobile.Infrastructure.Tests/SyncClientTests.cs` (create); `AccountClientTests` lines in `AuthClientTests.cs`

**Interfaces:**
- Consumes: Task 6's wire; Task 11's `ISyncApi`, `IConsentApi`, exceptions and payloads; the existing
  `AuthenticatingHandler` and `TokenRefresher`.
- Produces: `internal sealed class SyncClient(HttpClient http) : ISyncApi, IConsentApi`. Its `HttpClient` runs through
  `AuthenticatingHandler` (the same construction as `AccountClient`'s).

- [ ] **Step 1: Write the failing tests**

`tests/Coo.Mobile.Infrastructure.Tests/SyncClientTests.cs`:

```csharp
using System.Net;
using System.Text;
using Coo.Contracts.Sync;
using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;
using Coo.Mobile.Application.Auth;
using Coo.Mobile.Application.Sync;
using Coo.Mobile.Infrastructure.Sync;

namespace Coo.Mobile.Infrastructure.Tests;

/// <summary>Spec 011 §7.1 on the phone: only the server's own codes are refusals; anything else is a failed cycle.</summary>
public sealed class SyncClientTests
{
    private static readonly HouseholdId Household = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a1"));
    private static readonly DeviceId Phone = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000d1"));
    private static readonly Guid Change = Guid.Parse("7a1c0000-0000-7000-8000-000000000c01");
    private static readonly Guid Entry = Guid.Parse("7a1c0000-0000-7000-8000-000000000e01");

    private static OutgoingChange Upsert(string details = """{"v":1,"contents":"wet"}""") => new(
        Change, SyncEntityTypes.CareEntry, Entry, false, new HlcStamp(1_790_000_000_000, 2, Phone), 1,
        new CareEntryPayload("diaper", Guid.Parse("7a1c0000-0000-7000-8000-0000000000b1"), null, false,
            new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.FromHours(2)), null, "user:7a1c0000-0000-7000-8000-0000000000c1", "manual", null, "n", details));

    [Fact]
    public async Task PushAsync_Body_CarriesTheChangeWithItsDetailsAsAnObject()
    {
        // Arrange
        string? sent = null;
        var client = Client(async r =>
        {
            sent = await r.Content!.ReadAsStringAsync();
            return await Json(HttpStatusCode.OK, $$"""{"results":[{"changeId":"{{Change}}","result":"applied","reason":null}]}""");
        });

        // Act
        await client.PushAsync(Household, Phone, [Upsert()], CancellationToken.None);

        // Assert
        sent.Should().Contain("\"schemaVersion\":1").And.Contain("\"operation\":\"upsert\"")
            .And.Contain("\"details\":{\"v\":1,\"contents\":\"wet\"}").And.Contain("\"wallClockMs\":1790000000000");
    }

    [Fact]
    public async Task PushAsync_Delete_SendsNoPayload()
    {
        // Arrange
        string? sent = null;
        var client = Client(async r =>
        {
            sent = await r.Content!.ReadAsStringAsync();
            return await Json(HttpStatusCode.OK, """{"results":[]}""");
        });

        // Act
        await client.PushAsync(Household, Phone, [Upsert() with { IsDelete = true, Payload = null }], CancellationToken.None);

        // Assert
        sent.Should().Contain("\"operation\":\"delete\"").And.Contain("\"careEntry\":null");
    }

    [Fact]
    public async Task PushAsync_Results_AreMappedAndAnUnknownResultIsLeftUndecided()
    {
        // Arrange
        var other = Guid.Parse("7a1c0000-0000-7000-8000-000000000c02");
        var client = Client(_ => Json(HttpStatusCode.OK,
            $$"""{"results":[{"changeId":"{{Change}}","result":"rejected","reason":"time.end-not-after-start"},{"changeId":"{{other}}","result":"postponed","reason":null}]}"""));

        // Act
        var results = await client.PushAsync(Household, Phone, [Upsert()], CancellationToken.None);

        // Assert
        results.Should().Equal(new PushResult(Change, PushOutcome.Rejected, "time.end-not-after-start"));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, "")]
    [InlineData(HttpStatusCode.TooManyRequests, "")]
    [InlineData(HttpStatusCode.Forbidden, "<html>Blocked by the network</html>")]
    [InlineData(HttpStatusCode.Forbidden, """{"code":"something-else"}""")]
    public async Task PushAsync_Bare403OrAnOutage_IsUnavailable(HttpStatusCode status, string body)
    {
        // Arrange: a proxy's 403 deletes nothing (ADR 0002 §10 step 1; Review Focus 3).
        var client = Client(_ => Raw(status, body.StartsWith('{') ? "application/json" : "text/html", body));

        // Act
        Func<Task> act = async () => await client.PushAsync(Household, Phone, [Upsert()], CancellationToken.None);

        // Assert
        await act.Should().ThrowExactlyAsync<SyncUnavailableException>();
    }

    [Fact]
    public async Task PushAsync_RevokedProblemForThisHousehold_IsMembershipRevoked()
    {
        // Arrange
        var client = Client(_ => Raw(HttpStatusCode.Forbidden, "application/problem+json",
            $$"""{"type":"{{SyncErrors.MembershipRevokedType}}","title":"membership-revoked","status":403,"householdId":"{{Household}}"}"""));

        // Act
        Func<Task> act = async () => await client.PushAsync(Household, Phone, [Upsert()], CancellationToken.None);

        // Assert
        var refused = (await act.Should().ThrowExactlyAsync<SyncRefusedException>()).Which;
        refused.Refusal.Should().Be(SyncRefusal.MembershipRevoked);
        refused.Household.Should().Be(Household);
    }

    [Fact]
    public async Task PushAsync_RevokedProblemForAnotherHousehold_IsUnavailable()
    {
        // Arrange
        var client = Client(_ => Raw(HttpStatusCode.Forbidden, "application/problem+json",
            $$"""{"type":"{{SyncErrors.MembershipRevokedType}}","status":403,"householdId":"7a1c0000-0000-7000-8000-0000000000a9"}"""));

        // Act
        Func<Task> act = async () => await client.PushAsync(Household, Phone, [Upsert()], CancellationToken.None);

        // Assert
        await act.Should().ThrowExactlyAsync<SyncUnavailableException>();
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, """{"code":"sync.consent-required"}""", SyncRefusal.ConsentRequired)]
    [InlineData(HttpStatusCode.Forbidden, """{"code":"sync.not-a-member"}""", SyncRefusal.NotAMember)]
    [InlineData((HttpStatusCode)426, """{"code":"client-too-old"}""", SyncRefusal.ClientTooOld)]
    [InlineData(HttpStatusCode.BadRequest, """{"code":"sync.batch-too-large"}""", SyncRefusal.Malformed)]
    public async Task PushAsync_ServerCode_IsTheMatchingRefusal(HttpStatusCode status, string body, SyncRefusal refusal)
    {
        // Arrange
        var client = Client(_ => Raw(status, "application/json", body));

        // Act
        Func<Task> act = async () => await client.PushAsync(Household, Phone, [Upsert()], CancellationToken.None);

        // Assert
        (await act.Should().ThrowExactlyAsync<SyncRefusedException>()).Which.Refusal.Should().Be(refusal);
    }

    [Fact]
    public async Task PullAsync_HtmlBody_IsUnavailable()
    {
        // Arrange: a captive portal's 200 page (Review Focus 3).
        var client = Client(_ => Raw(HttpStatusCode.OK, "text/html", "<html>Sign in to the Wi-Fi</html>"));

        // Act
        Func<Task> act = async () => await client.PullAsync(Household, 0, null, CancellationToken.None);

        // Assert
        await act.Should().ThrowExactlyAsync<SyncUnavailableException>();
    }

    [Fact]
    public async Task PullAsync_NoNetworkOrTheRefreshFailing_IsUnavailable()
    {
        // Arrange: the refreshing handler throws AuthUnavailableException when it cannot refresh.
        var offline = Client(_ => throw new HttpRequestException("Network is unreachable"));
        var refresh = Client(_ => throw new AuthUnavailableException("refresh failed"));

        // Act
        Func<Task> first = async () => await offline.PullAsync(Household, 0, null, CancellationToken.None);
        Func<Task> second = async () => await refresh.PullAsync(Household, 0, null, CancellationToken.None);

        // Assert
        await first.Should().ThrowExactlyAsync<SyncUnavailableException>();
        await second.Should().ThrowExactlyAsync<SyncUnavailableException>();
    }

    [Fact]
    public async Task PullAsync_Page_KeepsTheDetailsAsReceivedAndSkipsATypeThisBuildDoesNotKnow()
    {
        // Arrange
        Uri? asked = null;
        var client = Client(r =>
        {
            asked = r.RequestUri;
            return Json(HttpStatusCode.OK,
                $$"""
                {"records":[
                  {"entityType":"CareEntry","entityId":"{{Entry}}","version":{"wallClockMs":5,"counter":1,"deviceId":"{{Phone}}"},"isDeleted":false,"seq":7,
                   "careEntry":{"kind":"medication","subjectBabyId":"7a1c0000-0000-7000-8000-0000000000b1","subjectPerson":null,"isSession":false,
                     "startAt":"2026-10-01T09:00:00+02:00","endAt":null,"loggedBy":"user:7a1c0000-0000-7000-8000-0000000000c1","source":"manual",
                     "sourcePromptVersion":null,"note":null,"details":{"v":1,"drug":"vitamin D","doseDrops":1}}},
                  {"entityType":"Milestone","entityId":"{{Entry}}","version":{"wallClockMs":6,"counter":0,"deviceId":"{{Phone}}"},"isDeleted":false,"seq":8}
                ],"cursor":8,"hasMore":true,"bootstrapHead":9}
                """);
        });

        // Act
        var page = await client.PullAsync(Household, 0, null, CancellationToken.None);

        // Assert
        asked!.PathAndQuery.Should().Be($"/api/sync/pull?household={Household}&cursor=0&schemaVersion=1");
        var record = page.Records.Should().ContainSingle().Subject;
        ((CareEntryPayload)record.Payload).DetailsJson.Should().Be("""{"v":1,"drug":"vitamin D","doseDrops":1}""");
        ((CareEntryPayload)record.Payload).StartAt.Offset.Should().Be(TimeSpan.FromHours(2));
        page.Should().Match<PulledPage>(p => p.Cursor == 8 && p.HasMore && p.BootstrapHead == 9);
    }

    [Fact]
    public async Task PullAsync_DuringABootstrap_SendsTheHead()
    {
        // Arrange
        Uri? asked = null;
        var client = Client(r =>
        {
            asked = r.RequestUri;
            return Json(HttpStatusCode.OK, """{"records":[],"cursor":500,"hasMore":false,"bootstrapHead":0}""");
        });

        // Act
        await client.PullAsync(Household, 500, 0, CancellationToken.None);

        // Assert
        asked!.Query.Should().Contain("&bootstrapHead=0");
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, """{"status":"revoked"}""", MembershipStatus.Revoked)]
    [InlineData(HttpStatusCode.OK, """{"status":"active"}""", MembershipStatus.Active)]
    [InlineData(HttpStatusCode.NotFound, "", MembershipStatus.Unknown)]
    [InlineData(HttpStatusCode.OK, "<html>portal</html>", MembershipStatus.Unknown)]
    [InlineData(HttpStatusCode.OK, """{"status":"maybe"}""", MembershipStatus.Unknown)]
    public async Task GetMembershipAsync_Answer_OnlyAClearStatementCounts(HttpStatusCode status, string body, MembershipStatus expected)
    {
        // Arrange: delete only on a 200 that says revoked (ADR 0002 §10 step 3).
        var client = Client(_ => Raw(status, body.StartsWith('{') ? "application/json" : "text/html", body));

        // Act
        var answer = await client.GetMembershipAsync(Household, CancellationToken.None);

        // Assert
        answer.Should().Be(expected);
    }

    [Fact]
    public async Task RecordAsync_Consent_SendsTheCurrentWordingAppVersionAndLanguage()
    {
        // Arrange
        string? sent = null;
        var client = Client(async r =>
        {
            sent = await r.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });

        // Act
        await client.RecordAsync("0.11.0", "pl", CancellationToken.None);

        // Assert
        sent.Should().Be("""{"purpose":"care-data","wording":"care-data-v1","appVersion":"0.11.0","language":"pl"}""");
    }

    private static SyncClient Client(Func<HttpRequestMessage, Task<HttpResponseMessage>> server) =>
        new(new HttpClient(new FakeServer(server)) { BaseAddress = new Uri("https://api.test/") });

    private static Task<HttpResponseMessage> Raw(HttpStatusCode status, string mediaType, string body)
    {
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mediaType) { CharSet = "utf-8" };
        return Task.FromResult(new HttpResponseMessage(status) { Content = content });
    }

    private static Task<HttpResponseMessage> Json(HttpStatusCode status, string body) =>
        Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
}
```

Add to `AuthClientTests.cs` a test for `AccountClient.GetMeAsync` with
`"consent":{"wording":"care-data-v1","givenAt":"2026-10-02T12:00:00+00:00"}` in the body, asserting
`overview.Consent == new ConsentInfo("care-data-v1", new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero))`, and one
without the field asserting `Consent` is null (a 0.10.0 server).

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Coo.Mobile.Infrastructure.Tests --filter FullyQualifiedName~SyncClientTests`
Expected: FAIL to compile (`Coo.Mobile.Infrastructure.Sync` not found). Paste the first error.

- [ ] **Step 3: Implement**

`src/Mobile/Coo.Mobile.Infrastructure/Sync/SyncJsonContext.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;
using Coo.Contracts.Sync;

namespace Coo.Mobile.Infrastructure.Sync;

/// <summary>Any refusal body: <c>{ code }</c>, or the membership-revoked problem's <c>type</c> and <c>householdId</c>.</summary>
internal sealed record SyncErrorBody(string? Code, string? Type, Guid? HouseholdId);

/// <summary>Source-generated for the trimmed Android build, like <c>AuthJsonContext</c>. Web defaults: camelCase.</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(PushRequest))]
[JsonSerializable(typeof(PushResponse))]
[JsonSerializable(typeof(PullResponse))]
[JsonSerializable(typeof(MembershipStatusResponse))]
[JsonSerializable(typeof(ConsentRequest))]
[JsonSerializable(typeof(SyncErrorBody))]
internal sealed partial class SyncJsonContext : JsonSerializerContext;
```

`src/Mobile/Coo.Mobile.Infrastructure/Sync/SyncHttp.cs`:

```csharp
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Coo.Contracts.Sync;
using Coo.Domain.Common;
using Coo.Mobile.Application.Auth;
using Coo.Mobile.Application.Sync;

namespace Coo.Mobile.Infrastructure.Sync;

/// <summary>
/// How a sync answer becomes a result (Spec 011 §7.1, ADR 0002 §10). Only the server's own codes are refusals, and only
/// the exact membership-revoked problem for the household asked about counts as one; a bare 403, a portal's page, a body
/// that cannot be read, no network or a refresh that could not run are "unavailable": a failed cycle that changes nothing.
/// </summary>
internal static class SyncHttp
{
    public static async Task<HttpResponseMessage> SendAsync(HttpClient http, HttpRequestMessage request, HouseholdId household, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException e)
        {
            throw new SyncUnavailableException("The server could not be reached.", e);
        }
        catch (TaskCanceledException e) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SyncUnavailableException("The server did not answer in time.", e);
        }
        catch (AuthUnavailableException e)
        {
            throw new SyncUnavailableException("The session could not be refreshed now.", e);
        }

        if (response.IsSuccessStatusCode)
            return response;

        using (response)
        {
            var status = (int)response.StatusCode;
            var body = await TryReadErrorAsync(response, cancellationToken);
            if (status == StatusCodes426 && body?.Code == SyncErrors.ClientTooOld)
                throw new SyncRefusedException(SyncRefusal.ClientTooOld, null);
            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                if (body?.Type == SyncErrors.MembershipRevokedType && body.HouseholdId == household.Value)
                    throw new SyncRefusedException(SyncRefusal.MembershipRevoked, household);
                if (body?.Code == SyncErrors.ConsentRequired)
                    throw new SyncRefusedException(SyncRefusal.ConsentRequired, null);
                if (body?.Code == SyncErrors.NotAMember)
                    throw new SyncRefusedException(SyncRefusal.NotAMember, null);
            }

            if (response.StatusCode == HttpStatusCode.BadRequest && body?.Code is SyncErrors.Malformed or SyncErrors.BatchTooLarge)
                throw new SyncRefusedException(SyncRefusal.Malformed, null);

            throw new SyncUnavailableException(string.Create(CultureInfo.InvariantCulture, $"The server answered {status}."));
        }
    }

    public static async Task<T> ReadAsync<T>(HttpResponseMessage response, JsonTypeInfo<T> type, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync(type, cancellationToken)
                ?? throw new SyncUnavailableException("The server sent an empty answer.");
        }
        catch (Exception e) when (IsUnreadable(e))
        {
            throw new SyncUnavailableException("The server sent an unreadable answer.", e);
        }
    }

    private const int StatusCodes426 = 426;

    private static async Task<SyncErrorBody?> TryReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync(SyncJsonContext.Default.SyncErrorBody, cancellationToken);
        }
        catch (Exception e) when (IsUnreadable(e))
        {
            return null;
        }
    }

    /// <summary>What reading a body can throw when it is not the JSON the API sends (Increment 10 lesson).</summary>
    private static bool IsUnreadable(Exception e) =>
        e is JsonException or NotSupportedException or InvalidOperationException or HttpRequestException or IOException;
}
```

(`ReadFromJsonAsync` refuses `application/problem+json`? It accepts any `+json` media type and `application/json`; a
`text/html` body throws `NotSupportedException` or `JsonException`, both "unreadable".)

`src/Mobile/Coo.Mobile.Infrastructure/Sync/SyncClient.cs`:

```csharp
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Coo.Contracts.Sync;
using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;
using Coo.Mobile.Application.Sync;

namespace Coo.Mobile.Infrastructure.Sync;

/// <summary>
/// <c>/api/sync</c>, <c>/api/households/{id}/membership</c> and <c>/api/consents</c>, through the refreshing handler.
/// Contracts are mapped here: details become a JSON object on the wire and a string again on the way in, exactly as
/// received (ADR 0010 §3). A record of a type this build does not know is skipped; the cursor still moves past it.
/// </summary>
internal sealed class SyncClient(HttpClient http) : ISyncApi, IConsentApi
{
    private static readonly Uri PushUri = new("api/sync/push", UriKind.Relative);
    private static readonly Uri ConsentsUri = new("api/consents", UriKind.Relative);

    public async Task<IReadOnlyList<PushResult>> PushAsync(
        HouseholdId household, DeviceId device, IReadOnlyList<OutgoingChange> changes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changes);
        var body = new PushRequest(household.Value, device.Value, SchemaVersions.Current, [.. changes.Select(ToDto)]);
        using var request = new HttpRequestMessage(HttpMethod.Post, PushUri) { Content = JsonContent.Create(body, SyncJsonContext.Default.PushRequest) };
        using var response = await SyncHttp.SendAsync(http, request, household, cancellationToken);
        var answer = await SyncHttp.ReadAsync(response, SyncJsonContext.Default.PushResponse, cancellationToken);
        return [.. (answer.Results ?? []).Select(FromDto).OfType<PushResult>()];
    }

    public async Task<PulledPage> PullAsync(HouseholdId household, long cursor, long? bootstrapHead, CancellationToken cancellationToken)
    {
        var uri = new Uri(
            string.Create(CultureInfo.InvariantCulture,
                $"api/sync/pull?household={household.Value:D}&cursor={cursor}&schemaVersion={SchemaVersions.Current}{(bootstrapHead is { } head ? $"&bootstrapHead={head}" : "")}"),
            UriKind.Relative);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await SyncHttp.SendAsync(http, request, household, cancellationToken);
        var page = await SyncHttp.ReadAsync(response, SyncJsonContext.Default.PullResponse, cancellationToken);
        return new PulledPage([.. (page.Records ?? []).Select(FromDto).OfType<PulledRecord>()], page.Cursor, page.HasMore, page.BootstrapHead);
    }

    public async Task<MembershipStatus> GetMembershipAsync(HouseholdId household, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"api/households/{household.Value:D}/membership", UriKind.Relative));
            using var response = await SyncHttp.SendAsync(http, request, household, cancellationToken);
            var answer = await SyncHttp.ReadAsync(response, SyncJsonContext.Default.MembershipStatusResponse, cancellationToken);
            return answer.Status switch
            {
                MembershipStatuses.Revoked => MembershipStatus.Revoked,
                MembershipStatuses.Active => MembershipStatus.Active,
                _ => MembershipStatus.Unknown,
            };
        }
        catch (Exception e) when (e is SyncUnavailableException or SyncRefusedException)
        {
            // Only a 200 that says "revoked" may lead to a deletion; anything else keeps the soft-lock (ADR 0002 §10).
            return MembershipStatus.Unknown;
        }
    }

    public async Task RecordAsync(string appVersion, string language, CancellationToken cancellationToken)
    {
        var body = new ConsentRequest(CareDataConsent.Purpose, CareDataConsent.CurrentWording, appVersion, language);
        using var request = new HttpRequestMessage(HttpMethod.Post, ConsentsUri) { Content = JsonContent.Create(body, SyncJsonContext.Default.ConsentRequest) };
        using var response = await SyncHttp.SendAsync(http, request, default, cancellationToken);
    }

    private static SyncChangeDto ToDto(OutgoingChange change)
    {
        var version = new HlcStampDto(change.Version.WallClockMs, change.Version.Counter, change.Version.DeviceId.Value);
        var dto = new SyncChangeDto(change.ChangeId, change.EntityType, change.EntityId, change.IsDelete ? SyncOperations.Delete : SyncOperations.Upsert, version);
        return change.Payload switch
        {
            CareEntryPayload c => dto with
            {
                CareEntry = new CareEntryDto(c.Kind, c.SubjectBabyId, c.SubjectPerson, c.IsSession, c.StartAt, c.EndAt, c.LoggedBy, c.Source,
                    c.SourcePromptVersion, c.Note, Element(c.DetailsJson)),
            },
            BabyProfilePayload b => dto with { BabyProfile = new BabyProfileDto(b.Name, b.Nicknames, b.BirthDate, b.DueDate) },
            HouseholdSettingsPayload s => dto with { HouseholdSettings = new HouseholdSettingsDto(s.VolumeUnit, s.TimeZoneId, s.HiddenKinds) },
            DismissalPayload d => dto with { DuplicateDismissal = new DuplicateDismissalDto(d.EntryA, d.EntryB) },
            _ => dto,
        };
    }

    private static PushResult? FromDto(ChangeResultDto result) => result.Result switch
    {
        ChangeResults.Applied => new PushResult(result.ChangeId, PushOutcome.Applied, null),
        ChangeResults.Superseded => new PushResult(result.ChangeId, PushOutcome.Superseded, null),
        ChangeResults.Rejected => new PushResult(result.ChangeId, PushOutcome.Rejected, result.Reason),
        _ => null,
    };

    private static PulledRecord? FromDto(SyncRecordDto record)
    {
        if (record.Version is null)
            return null;
        var version = new HlcStamp(record.Version.WallClockMs, record.Version.Counter, new DeviceId(record.Version.DeviceId));
        RecordPayload? payload = record.EntityType switch
        {
            SyncEntityTypes.CareEntry when record.CareEntry is { } c => new CareEntryPayload(
                c.Kind, c.SubjectBabyId, c.SubjectPerson, c.IsSession, c.StartAt, c.EndAt, c.LoggedBy, c.Source, c.SourcePromptVersion, c.Note,
                c.Details.GetRawText()),
            SyncEntityTypes.BabyProfile when record.BabyProfile is { } b => new BabyProfilePayload(b.Name, b.Nicknames ?? [], b.BirthDate, b.DueDate),
            SyncEntityTypes.HouseholdSettings when record.HouseholdSettings is { } s => new HouseholdSettingsPayload(s.VolumeUnit, s.TimeZoneId, s.HiddenKinds ?? []),
            SyncEntityTypes.DuplicateDismissal when record.DuplicateDismissal is { } d => new DismissalPayload(d.EntryA, d.EntryB),
            _ => null,
        };
        return payload is null ? null : new PulledRecord(record.EntityType, record.EntityId, version, record.IsDeleted, payload);
    }

    private static JsonElement Element(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
```

(A server record with a `Guid.Empty` device would throw in `new DeviceId(...)`; the server never stores one (validated on
push). If the analyzer asks, guard it with `record.Version.DeviceId == Guid.Empty ? null : …`.)

`AccountClient.GetMeAsync` maps the consent:

```csharp
        return new AccountOverview(
            new AccountProfile(new UserId(me.User.Id), me.User.DisplayName, me.User.Email),
            me.Household is null ? null : new HouseholdId(me.Household.HouseholdId),
            me.Household?.Role,
            [.. me.Devices.Select(d => new DeviceSessionInfo(d.DeviceName, d.LastUsedAt, d.IsCurrent))],
            me.Consent is null ? null : new ConsentInfo(me.Consent.Wording, me.Consent.GivenAt));
```

In `AddMobileInfrastructure`, one factory for clients that carry the access token, used by both:

```csharp
        HttpClient Authenticated(IServiceProvider sp) =>
            new(new AuthenticatingHandler(sp.GetRequiredService<TokenRefresher>()) { InnerHandler = new HttpClientHandler() })
            {
                BaseAddress = apiBaseAddress,
                Timeout = HttpTimeout,
            };

        services.AddSingleton<IAccountApi>(sp => new AccountClient(Authenticated(sp)));

        // Sync (Spec 011 §7.1): the same refreshing handler.
        services.AddSingleton(sp => new SyncClient(Authenticated(sp)));
        services.AddSingleton<ISyncApi>(sp => sp.GetRequiredService<SyncClient>());
        services.AddSingleton<IConsentApi>(sp => sp.GetRequiredService<SyncClient>());
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Coo.Mobile.Infrastructure.Tests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
dotnet format Coo.Tests.slnf
git add src/Mobile/Coo.Mobile.Infrastructure tests/Coo.Mobile.Infrastructure.Tests
git commit -m "Phone sync client: only the server's codes refuse, a bare 403 or a portal page fails the cycle, details as received (Spec 011 §7.1)"
```

---
### Task 16: The strings, and the sync line in the Account section

**Files:**
- Modify: `eng/strings/strings.py` (+ generated `src/Mobile/Coo.Mobile.Presentation/Resources/AppResources*.resx`)
- Modify: `src/Mobile/Coo.Mobile.Presentation/Localization/Strings.cs` (`Plural` with more arguments)
- Create: `src/Mobile/Coo.Mobile.Presentation/Settings/SyncLine.cs`
- Modify: `src/Mobile/Coo.Mobile.Presentation/Settings/AccountViewModel.cs`
- Test: `tests/Coo.Mobile.Presentation.Tests/SyncLineTests.cs` (create); modify `AccountViewModelTests.cs`

**Interfaces:**
- Consumes: `ISyncStatus`, `SyncStatus`, `CycleOutcome` (Task 12); `IClockFormat` (existing); `Formats.Time` (existing).
- Produces:
  - Every string of this increment (keys below), in seven languages.
  - `Strings.Plural(string key, long n, params object[] more)` (`{0}` is n, `{1}`… the rest).
  - `SyncLineAction { None, TurnOnSharing, ChooseWhatToDo }`, `SyncLineView(string Text, SyncLineAction Action)`,
    `SyncLine.For(AccountState account, SyncStatus status, bool is24Hour)`.
  - `AccountViewModel(Account, SignIn, SignOut, DeleteAccount, RefreshAccount, IHouseholdStore, ISyncStatus, IClockFormat)`
    with `StatusLine` (now the sync line when signed in), `LineAction`, `LineActionText`, `LineActionCommand`, and the
    events `ConsentRequested`, `BringOverRequested`.

- [ ] **Step 1: The strings**

In `eng/strings/strings.py`, **replace** the `'Account.JoinPending'` row (spec §8.3) and add, after it:

```python
'Account.JoinPending': ("Signed in. The entries on this phone aren't in your family's log yet.", 'Angemeldet. Die Einträge auf diesem Handy sind noch nicht im Verlauf deiner Familie.', 'Connecté. Les entrées de ce téléphone ne sont pas encore dans le journal de votre famille.', 'Sesión iniciada. Los registros de este teléfono aún no están en el registro de tu familia.', 'Accesso effettuato. Le voci su questo telefono non sono ancora nel registro della tua famiglia.', 'Zalogowano. Wpisów z tego telefonu nie ma jeszcze w dzienniku rodziny.', 'Ingelogd. De items op deze telefoon staan nog niet in het logboek van je gezin.'),
'Account.ChooseWhatToDo': ('Choose what to do with them', 'Entscheiden, was damit passiert', 'Choisir quoi en faire', 'Elegir qué hacer con ellos', 'Scegli cosa farne', 'Zdecyduj, co z nimi zrobić', 'Kies wat je ermee doet'),

'Consent.Title': ('Share your log with your family?', 'Deine Einträge mit der Familie teilen?', 'Partager votre journal avec votre famille ?', '¿Compartir tu registro con tu familia?', 'Condividere il registro con la famiglia?', 'Udostępnić dziennik rodzinie?', 'Je logboek delen met je gezin?'),
'Consent.Body': ("To share, Coo keeps your baby's care log (feeds, diapers, sleep and pumping, with their times, amounts and notes), your baby's profile and your household settings on Coo's server in Germany. This is health information about your child, so we only store it with your permission.", 'Zum Teilen speichert Coo die Einträge deines Babys (Mahlzeiten, Windeln, Schlaf und Abpumpen, mit Zeiten, Mengen und Notizen), das Profil deines Babys und die Einstellungen deines Haushalts auf dem Server von Coo in Deutschland. Das sind Gesundheitsdaten über dein Kind, deshalb speichern wir sie nur mit deiner Erlaubnis.', "Pour le partage, Coo conserve le journal de soins de votre bébé (repas, couches, sommeil et tire-lait, avec leurs heures, quantités et notes), le profil de votre bébé et les réglages de votre foyer sur le serveur de Coo en Allemagne. Ce sont des données de santé concernant votre enfant : nous ne les conservons qu’avec votre autorisation.", 'Para compartir, Coo guarda el registro de cuidados de tu bebé (tomas, pañales, sueño y extracciones, con sus horas, cantidades y notas), el perfil de tu bebé y los ajustes de tu hogar en el servidor de Coo en Alemania. Son datos de salud de tu hijo, por eso solo los guardamos con tu permiso.', 'Per la condivisione, Coo conserva il registro delle cure del tuo bambino (poppate, pannolini, sonno e tiralatte, con orari, quantità e note), il profilo del bambino e le impostazioni della famiglia sul server di Coo in Germania. Sono dati sanitari su tuo figlio, quindi li conserviamo solo con il tuo permesso.', 'Aby udostępniać, Coo przechowuje dziennik opieki nad dzieckiem (karmienia, pieluchy, sen i odciąganie, z godzinami, ilościami i notatkami), profil dziecka i ustawienia domowe na serwerze Coo w Niemczech. To informacje o zdrowiu Twojego dziecka, dlatego przechowujemy je tylko za Twoją zgodą.', 'Om te delen bewaart Coo het verzorgingslogboek van je baby (voedingen, luiers, slaap en kolven, met tijden, hoeveelheden en notities), het profiel van je baby en de instellingen van je huishouden op de server van Coo in Duitsland. Dit is gezondheidsinformatie over je kind, dus we bewaren het alleen met je toestemming.'),
'Consent.Body2': ('Everyone in your household can see it and add to it. You can withdraw your permission at any time by deleting your account in Settings. The log on this phone always stays here.', 'Alle in deinem Haushalt können sie sehen und ergänzen. Du kannst deine Erlaubnis jederzeit widerrufen, indem du dein Konto in den Einstellungen löschst. Die Einträge auf diesem Handy bleiben immer hier.', 'Toutes les personnes de votre foyer peuvent le voir et y ajouter des entrées. Vous pouvez retirer votre autorisation à tout moment en supprimant votre compte dans Réglages. Le journal de ce téléphone reste toujours ici.', 'Todas las personas de tu hogar pueden verlo y añadir registros. Puedes retirar tu permiso en cualquier momento eliminando tu cuenta en Ajustes. El registro de este teléfono siempre se queda aquí.', 'Tutti nella tua famiglia possono vederlo e aggiungere voci. Puoi revocare il permesso in qualsiasi momento eliminando il tuo account in Impostazioni. Il registro su questo telefono resta sempre qui.', 'Wszyscy domownicy mogą go oglądać i dodawać wpisy. Zgodę możesz wycofać w każdej chwili, usuwając konto w Ustawieniach. Dziennik na tym telefonie zawsze tu zostaje.', 'Iedereen in je huishouden kan het zien en aanvullen. Je kunt je toestemming altijd intrekken door je account te verwijderen in Instellingen. Het logboek op deze telefoon blijft altijd hier.'),
'Consent.Privacy': ('Privacy policy', 'Datenschutzerklärung', 'Politique de confidentialité', 'Política de privacidad', 'Informativa sulla privacy', 'Polityka prywatności', 'Privacybeleid'),
'Consent.Agree': ('Agree and share', 'Zustimmen und teilen', 'Accepter et partager', 'Aceptar y compartir', 'Accetta e condividi', 'Zgadzam się i udostępniam', 'Akkoord en delen'),
'Consent.NotNow': ('Not now', 'Jetzt nicht', 'Plus tard', 'Ahora no', 'Non ora', 'Nie teraz', 'Niet nu'),
'Consent.Failed': ("Sharing couldn't be turned on. Check your connection and try again.", 'Teilen konnte nicht eingeschaltet werden. Prüfe deine Verbindung und versuche es noch einmal.', 'Le partage n’a pas pu être activé. Vérifiez votre connexion et réessayez.', 'No se ha podido activar la opción de compartir. Comprueba la conexión y vuelve a intentarlo.', 'Non è stato possibile attivare la condivisione. Controlla la connessione e riprova.', 'Nie udało się włączyć udostępniania. Sprawdź połączenie i spróbuj ponownie.', 'Delen kon niet worden ingeschakeld. Controleer je verbinding en probeer het opnieuw.'),

'Sync.UpToDate': ('Up to date · {0}', 'Aktuell · {0}', 'À jour · {0}', 'Al día · {0}', 'Aggiornato · {0}', 'Aktualne · {0}', 'Bijgewerkt · {0}'),
'Sync.WaitingNoChanges': ('Waiting for a connection.', 'Warte auf eine Verbindung.', 'En attente de connexion.', 'Esperando conexión.', 'In attesa di connessione.', 'Czekam na połączenie.', 'Wachten op verbinding.'),
'Sync.Off': ('Sharing is off. Your log is only on this phone.', 'Teilen ist aus. Deine Einträge sind nur auf diesem Handy.', 'Le partage est désactivé. Votre journal est uniquement sur ce téléphone.', 'Compartir está desactivado. Tu registro solo está en este teléfono.', 'La condivisione è disattivata. Il tuo registro è solo su questo telefono.', 'Udostępnianie jest wyłączone. Twój dziennik jest tylko na tym telefonie.', 'Delen staat uit. Je logboek staat alleen op deze telefoon.'),
'Sync.TurnOn': ('Turn on sharing', 'Teilen einschalten', 'Activer le partage', 'Activar compartir', 'Attiva la condivisione', 'Włącz udostępnianie', 'Delen inschakelen'),
'Sync.UpdateApp': ('Update Coo to keep sharing. Your changes are safe on this phone.', 'Aktualisiere Coo, um weiter zu teilen. Deine Änderungen sind auf diesem Handy sicher.', 'Mettez Coo à jour pour continuer à partager. Vos modifications sont en sécurité sur ce téléphone.', 'Actualiza Coo para seguir compartiendo. Tus cambios están a salvo en este teléfono.', 'Aggiorna Coo per continuare a condividere. Le tue modifiche sono al sicuro su questo telefono.', 'Zaktualizuj Coo, aby dalej udostępniać. Twoje zmiany są bezpieczne na tym telefonie.', 'Werk Coo bij om te blijven delen. Je wijzigingen zijn veilig op deze telefoon.'),

'BringOver.Family': ("Your family's log has: {0}.", 'Im Verlauf deiner Familie: {0}.', 'Le journal de votre famille contient : {0}.', 'El registro de tu familia tiene: {0}.', 'Il registro della tua famiglia ha: {0}.', 'W dzienniku rodziny: {0}.', 'Het logboek van je gezin heeft: {0}.'),
'BringOver.FamilyEmpty': ("Your family's log has no baby yet.", 'Im Verlauf deiner Familie ist noch kein Baby.', 'Le journal de votre famille n’a pas encore de bébé.', 'El registro de tu familia aún no tiene ningún bebé.', 'Il registro della tua famiglia non ha ancora un bambino.', 'W dzienniku rodziny nie ma jeszcze dziecka.', 'Het logboek van je gezin heeft nog geen baby.'),
'BringOver.Bring': ('Bring my entries over', 'Meine Einträge übernehmen', 'Transférer mes entrées', 'Traer mis registros', 'Porta le mie voci', 'Przenieś moje wpisy', 'Mijn items overzetten'),
'BringOver.Keep': ('Keep them separate', 'Getrennt lassen', 'Les garder séparées', 'Mantenerlos separados', 'Tienile separate', 'Zostaw je osobno', 'Apart houden'),
'BringOver.Which': ('{0} on this phone is', '{0} auf diesem Handy ist', '{0} sur ce téléphone est', '{0} en este teléfono es', '{0} su questo telefono è', '{0} na tym telefonie to', '{0} op deze telefoon is'),
'BringOver.AddNew': ('Add as a new baby', 'Als neues Baby hinzufügen', 'Ajouter comme nouveau bébé', 'Añadir como bebé nuevo', 'Aggiungi come nuovo bambino', 'Dodaj jako nowe dziecko', 'Toevoegen als nieuwe baby'),
'BringOver.Confirm': ('Bring them over', 'Übernehmen', 'Transférer', 'Traerlos', 'Portale', 'Przenieś', 'Overzetten'),
'BringOver.Failed': ("That didn't work. Nothing was changed.", 'Das hat nicht geklappt. Es wurde nichts geändert.', 'Cela n’a pas fonctionné. Rien n’a été modifié.', 'No ha funcionado. No se ha cambiado nada.', 'Non ha funzionato. Non è stato cambiato nulla.', 'Nie udało się. Nic nie zostało zmienione.', 'Dat is niet gelukt. Er is niets veranderd.'),

'Attention.Rejected': ('Not accepted by the server', 'Vom Server nicht angenommen', 'Refusé par le serveur', 'No aceptado por el servidor', 'Non accettato dal server', 'Nie przyjęte przez serwer', 'Niet geaccepteerd door de server'),
'Attention.ShowMore': ('Show more', 'Mehr anzeigen', 'Afficher plus', 'Mostrar más', 'Mostra altro', 'Pokaż więcej', 'Meer tonen'),

'Error.sync.schema-unsupported': ('This change needs a newer version of Coo. Update the app.', 'Für diese Änderung brauchst du eine neuere Coo-Version. Aktualisiere die App.', 'Cette modification nécessite une version plus récente de Coo. Mettez l’app à jour.', 'Este cambio necesita una versión más reciente de Coo. Actualiza la app.', 'Questa modifica richiede una versione più recente di Coo. Aggiorna l’app.', 'Ta zmiana wymaga nowszej wersji Coo. Zaktualizuj aplikację.', 'Voor deze wijziging is een nieuwere versie van Coo nodig. Werk de app bij.'),
'Error.sync.stamp-in-future': ("This phone's clock is ahead. Check the date and time, then edit the entry.", 'Die Uhr dieses Handys geht vor. Prüfe Datum und Uhrzeit und bearbeite dann den Eintrag.', 'L’horloge de ce téléphone est en avance. Vérifiez la date et l’heure, puis modifiez l’entrée.', 'El reloj de este teléfono va adelantado. Revisa la fecha y la hora y luego edita el registro.', 'L’orologio di questo telefono è avanti. Controlla data e ora, poi modifica la voce.', 'Zegar tego telefonu się spieszy. Sprawdź datę i godzinę, a potem edytuj wpis.', 'De klok van deze telefoon loopt voor. Controleer de datum en tijd en bewerk dan het item.'),
'Error.sync.household-mismatch': ("This entry already belongs to another family's log.", 'Dieser Eintrag gehört schon zum Verlauf einer anderen Familie.', 'Cette entrée appartient déjà au journal d’une autre famille.', 'Este registro ya pertenece al registro de otra familia.', 'Questa voce appartiene già al registro di un’altra famiglia.', 'Ten wpis należy już do dziennika innej rodziny.', 'Dit item hoort al bij het logboek van een ander gezin.'),
'Error.sync.person-unresolved': ("Who logged this isn't known yet. Edit the entry to send it again.", 'Wer das eingetragen hat, ist noch unbekannt. Bearbeite den Eintrag, um ihn erneut zu senden.', 'On ne sait pas encore qui a noté cette entrée. Modifiez-la pour l’envoyer à nouveau.', 'Aún no se sabe quién hizo este registro. Edítalo para enviarlo de nuevo.', 'Non si sa ancora chi ha registrato questa voce. Modificala per inviarla di nuovo.', 'Nie wiadomo jeszcze, kto dodał ten wpis. Edytuj go, aby wysłać ponownie.', 'Nog niet bekend wie dit heeft gelogd. Bewerk het item om het opnieuw te versturen.'),
'Error.sync.role-forbidden': ("Your role in this family can't change this.", 'Deine Rolle in dieser Familie erlaubt diese Änderung nicht.', 'Votre rôle dans cette famille ne permet pas cette modification.', 'Tu rol en esta familia no permite este cambio.', 'Il tuo ruolo in questa famiglia non consente questa modifica.', 'Twoja rola w tej rodzinie nie pozwala na tę zmianę.', 'Je rol in dit gezin staat deze wijziging niet toe.'),
'Error.sync.change-malformed': ("The server couldn't read this change.", 'Der Server konnte diese Änderung nicht lesen.', 'Le serveur n’a pas pu lire cette modification.', 'El servidor no ha podido leer este cambio.', 'Il server non è riuscito a leggere questa modifica.', 'Serwer nie mógł odczytać tej zmiany.', 'De server kon deze wijziging niet lezen.'),
```

and to `P`:

```python
'Sync.Sharing': {'en': {'one': 'Sharing {0} change…', 'other': 'Sharing {0} changes…'},
                 'de': {'one': '{0} Änderung wird geteilt…', 'other': '{0} Änderungen werden geteilt…'},
                 'fr': {'one': 'Partage de {0} modification…', 'other': 'Partage de {0} modifications…'},
                 'es': {'one': 'Compartiendo {0} cambio…', 'other': 'Compartiendo {0} cambios…'},
                 'it': {'one': 'Condivisione di {0} modifica…', 'other': 'Condivisione di {0} modifiche…'},
                 'pl': {'one': 'Udostępnianie {0} zmiany…', 'few': 'Udostępnianie {0} zmian…', 'many': 'Udostępnianie {0} zmian…'},
                 'nl': {'one': '{0} wijziging wordt gedeeld…', 'other': '{0} wijzigingen worden gedeeld…'}},
'Sync.Waiting': {'en': {'one': 'Waiting for a connection. {0} change is safe on this phone.', 'other': 'Waiting for a connection. {0} changes are safe on this phone.'},
                 'de': {'one': 'Warte auf eine Verbindung. {0} Änderung ist auf diesem Handy sicher.', 'other': 'Warte auf eine Verbindung. {0} Änderungen sind auf diesem Handy sicher.'},
                 'fr': {'one': 'En attente de connexion. {0} modification est en sécurité sur ce téléphone.', 'other': 'En attente de connexion. {0} modifications sont en sécurité sur ce téléphone.'},
                 'es': {'one': 'Esperando conexión. {0} cambio está a salvo en este teléfono.', 'other': 'Esperando conexión. {0} cambios están a salvo en este teléfono.'},
                 'it': {'one': 'In attesa di connessione. {0} modifica è al sicuro su questo telefono.', 'other': 'In attesa di connessione. {0} modifiche sono al sicuro su questo telefono.'},
                 'pl': {'one': 'Czekam na połączenie. {0} zmiana jest bezpieczna na tym telefonie.', 'few': 'Czekam na połączenie. {0} zmiany są bezpieczne na tym telefonie.', 'many': 'Czekam na połączenie. {0} zmian jest bezpiecznych na tym telefonie.'},
                 'nl': {'one': 'Wachten op verbinding. {0} wijziging is veilig op deze telefoon.', 'other': 'Wachten op verbinding. {0} wijzigingen zijn veilig op deze telefoon.'}},
'BringOver.Entries': {'en': {'one': 'This phone has {0} entry for {1}.', 'other': 'This phone has {0} entries for {1}.'},
                      'de': {'one': 'Auf diesem Handy ist {0} Eintrag für {1}.', 'other': 'Auf diesem Handy sind {0} Einträge für {1}.'},
                      'fr': {'one': 'Ce téléphone contient {0} entrée pour {1}.', 'other': 'Ce téléphone contient {0} entrées pour {1}.'},
                      'es': {'one': 'Este teléfono tiene {0} registro de {1}.', 'other': 'Este teléfono tiene {0} registros de {1}.'},
                      'it': {'one': 'Questo telefono ha {0} voce per {1}.', 'other': 'Questo telefono ha {0} voci per {1}.'},
                      'pl': {'one': 'Na tym telefonie jest {0} wpis dla: {1}.', 'few': 'Na tym telefonie są {0} wpisy dla: {1}.', 'many': 'Na tym telefonie jest {0} wpisów dla: {1}.'},
                      'nl': {'one': 'Deze telefoon heeft {0} item voor {1}.', 'other': 'Deze telefoon heeft {0} items voor {1}.'}},
```

Run `python3 eng/strings/generate.py`. The existing localization tests (`LocalizationTests`) check every key in every
language, plural categories and placeholders; they cover the new keys with no new test.

- [ ] **Step 2: Write the failing tests**

`tests/Coo.Mobile.Presentation.Tests/SyncLineTests.cs`:

```csharp
using Coo.Domain.Common;
using Coo.Domain.Households;
using Coo.Mobile.Application.Auth;
using Coo.Mobile.Application.Preferences;
using Coo.Mobile.Application.Sync;
using Coo.Mobile.Presentation.Formatting;
using Coo.Mobile.Presentation.Localization;
using Coo.Mobile.Presentation.Settings;

namespace Coo.Mobile.Presentation.Tests;

/// <summary>Spec 011 §2: the sync line's states. It never says "up to date" while the outbox holds anything.</summary>
[Collection(nameof(StringsState))]
public sealed class SyncLineTests : IDisposable
{
    private static readonly AccountProfile Profile = new(new UserId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000c1")), "Andrzej", "andrzej@example.com");
    private static readonly AccountState Signed = new SignedIn(Profile);
    private static readonly DateTimeOffset At = new(2026, 10, 2, 14, 32, 0, TimeSpan.FromHours(2));

    public SyncLineTests() => Strings.Use(new LanguagePreference("en"), null);

    public void Dispose() => Strings.Use(LanguagePreference.System, null);

    private static SyncStatus Status(CycleOutcome? last, int pending = 0, bool running = false, DateTimeOffset? at = null) =>
        SyncStatus.Initial with { Last = last, Pending = pending, IsRunning = running, LastSyncedAt = at };

    [Fact]
    public void For_SyncedWithNothingPending_SaysUpToDateWithTheTime()
    {
        // Act
        var line = SyncLine.For(Signed, Status(CycleOutcome.Synced, at: At), is24Hour: true);

        // Assert
        line.Should().Be(new SyncLineView($"Up to date · {new Formats(true, VolumeUnit.Millilitres).Time(At)}", SyncLineAction.None));
    }

    [Theory]
    [InlineData(1, "Sharing 1 change…")]
    [InlineData(3, "Sharing 3 changes…")]
    public void For_ChangesPending_NeverSaysUpToDate(int pending, string expected)
    {
        // Act
        var line = SyncLine.For(Signed, Status(CycleOutcome.Synced, pending, at: At), is24Hour: true);

        // Assert
        line.Text.Should().Be(expected);
    }

    [Fact]
    public void For_FailedWithTwoPending_SaysTheyAreSafe()
    {
        // Act
        var line = SyncLine.For(Signed, Status(CycleOutcome.Failed, 2), is24Hour: true);

        // Assert
        line.Text.Should().Be("Waiting for a connection. 2 changes are safe on this phone.");
    }

    [Fact]
    public void For_FailedWithNothingPending_SaysOnlyThatItWaits()
    {
        // Act
        var line = SyncLine.For(Signed, Status(CycleOutcome.Failed), is24Hour: true);

        // Assert
        line.Text.Should().Be("Waiting for a connection.");
    }

    [Fact]
    public void For_RetryingAfterAFailureWithChangesPending_SaysSharing()
    {
        // Act
        var line = SyncLine.For(Signed, Status(CycleOutcome.Failed, 2, running: true), is24Hour: true);

        // Assert
        line.Text.Should().Be("Sharing 2 changes…");
    }

    [Theory]
    [InlineData(CycleOutcome.ConsentDeclined)]
    [InlineData(CycleOutcome.ConsentNeeded)]
    public void For_NoConsent_SaysSharingIsOffAndOffersToTurnItOn(CycleOutcome outcome)
    {
        // Act
        var line = SyncLine.For(Signed, Status(outcome, 4), is24Hour: true);

        // Assert
        line.Should().Be(new SyncLineView("Sharing is off. Your log is only on this phone.", SyncLineAction.TurnOnSharing));
    }

    [Fact]
    public void For_ClientTooOld_AsksForAnUpdate()
    {
        // Act
        var line = SyncLine.For(Signed, Status(CycleOutcome.ClientTooOld, 4), is24Hour: true);

        // Assert
        line.Text.Should().Be("Update Coo to keep sharing. Your changes are safe on this phone.");
    }

    [Fact]
    public void For_JoinPending_SaysTheEntriesAreNotInTheFamilysLogAndOffersTheChoice()
    {
        // Act
        var line = SyncLine.For(new JoinPending(Profile, new HouseholdId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a9"))), SyncStatus.Initial, is24Hour: true);

        // Assert
        line.Should().Be(new SyncLineView("Signed in. The entries on this phone aren't in your family's log yet.", SyncLineAction.ChooseWhatToDo));
    }

    [Fact]
    public void For_SessionRefused_SaysSignInAgain()
    {
        // Act
        var line = SyncLine.For(new SignedOut(SessionExpired: true), Status(CycleOutcome.NotSignedIn, 3), is24Hour: true);

        // Assert
        line.Text.Should().Be("Sign in again to keep sharing.");
    }

    [Fact]
    public void For_NeverSyncedAndNothingPending_IsEmpty()
    {
        // Act
        var line = SyncLine.For(Signed, SyncStatus.Initial, is24Hour: true);

        // Assert
        line.Should().Be(new SyncLineView("", SyncLineAction.None));
    }
}
```

In `tests/Coo.Mobile.Presentation.Tests/AccountViewModelTests.cs`: `Model()` passes two more arguments,
`_sync` (a field `private readonly ISyncStatus _sync = Substitute.For<ISyncStatus>();`, with
`_sync.Status.Returns(SyncStatus.Initial);` in the constructor) and `_clock` (`Substitute.For<IClockFormat>()`).
`State_JoinPending_SaysTheEntriesJoinLater` becomes `State_JoinPending_SaysTheEntriesAreNotInTheFamilysLogYet` and
expects the new `Account.JoinPending` English text. Add:

```csharp
    [Fact]
    public async Task LineActionCommand_SharingOff_AsksForTheConsentSheet()
    {
        // Arrange
        await _account.SignedInAsync(new StoredSession("a", Expires, "r", Profile, Household, JoinPending: false), CancellationToken.None);
        _sync.Status.Returns(SyncStatus.Initial with { Last = CycleOutcome.ConsentDeclined });
        using var model = Model();
        var asked = 0;
        model.ConsentRequested += (_, _) => asked++;

        // Act
        model.LineActionCommand.Execute(null);

        // Assert
        model.LineActionText.Should().Be("Turn on sharing");
        asked.Should().Be(1);
    }

    [Fact]
    public async Task StatusChanged_FromTheEngine_RaisesTheLineProperties()
    {
        // Arrange
        await _account.SignedInAsync(new StoredSession("a", Expires, "r", Profile, Household, JoinPending: false), CancellationToken.None);
        using var model = Model();
        var changed = new List<string?>();
        model.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        _sync.Status.Returns(SyncStatus.Initial with { Last = CycleOutcome.Failed, Pending = 2 });

        // Act
        _sync.StatusChanged += Raise.Event();

        // Assert
        changed.Should().Contain(nameof(AccountViewModel.StatusLine));
        model.StatusLine.Should().Be("Waiting for a connection. 2 changes are safe on this phone.");
    }
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test tests/Coo.Mobile.Presentation.Tests --filter "FullyQualifiedName~SyncLineTests|FullyQualifiedName~AccountViewModelTests"`
Expected: FAIL to compile (`SyncLine`, the new constructor). Paste the first error.

- [ ] **Step 4: Implement**

`Strings.cs`:

```csharp
    /// <summary>A plural string with more arguments: <c>{0}</c> is <paramref name="n"/>, <c>{1}</c>… are <paramref name="more"/>.</summary>
    public static string Plural(string key, long n, params object[] more) =>
        string.Format(Culture, Get($"{key}.{PluralRules.Select(Language, n)}"), [n, .. more]);
```

`src/Mobile/Coo.Mobile.Presentation/Settings/SyncLine.cs`:

```csharp
using Coo.Domain.Households;
using Coo.Mobile.Application.Auth;
using Coo.Mobile.Application.Sync;
using Coo.Mobile.Presentation.Formatting;
using Coo.Mobile.Presentation.Localization;

namespace Coo.Mobile.Presentation.Settings;

public enum SyncLineAction
{
    None,
    TurnOnSharing,
    ChooseWhatToDo,
}

public sealed record SyncLineView(string Text, SyncLineAction Action);

/// <summary>
/// The line under the account (Spec 011 §2), the only sync indicator. Pure: the state, the status and the clock format
/// in, the text out. It never says "up to date" while the outbox holds anything; "Sharing n changes…" only when n &gt; 0
/// (ruling 11), so the 30-second pull does not flicker it.
/// </summary>
public static class SyncLine
{
    public static SyncLineView For(AccountState account, SyncStatus status, bool is24Hour)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(status);
        switch (account)
        {
            case SignedOut { SessionExpired: true }:
                return new SyncLineView(Strings.Get("Account.SessionExpired"), SyncLineAction.None);
            case SignedOut:
                return new SyncLineView(Strings.Get("Account.SignInHint"), SyncLineAction.None);
            case JoinPending:
                return new SyncLineView(Strings.Get("Account.JoinPending"), SyncLineAction.ChooseWhatToDo);
        }

        return status.Last switch
        {
            CycleOutcome.ConsentDeclined or CycleOutcome.ConsentNeeded => new SyncLineView(Strings.Get("Sync.Off"), SyncLineAction.TurnOnSharing),
            CycleOutcome.ClientTooOld => new SyncLineView(Strings.Get("Sync.UpdateApp"), SyncLineAction.None),
            _ when status.IsRunning && status.Pending > 0 => Sharing(status.Pending),
            CycleOutcome.Failed or CycleOutcome.SoftLocked or CycleOutcome.Removed => new SyncLineView(
                status.Pending > 0 ? Strings.Plural("Sync.Waiting", status.Pending) : Strings.Get("Sync.WaitingNoChanges"), SyncLineAction.None),
            _ when status.Pending > 0 => Sharing(status.Pending),
            _ when status.LastSyncedAt is { } at => new SyncLineView(
                Strings.Format("Sync.UpToDate", new Formats(is24Hour, VolumeUnit.Millilitres).Time(at)), SyncLineAction.None),
            _ => new SyncLineView("", SyncLineAction.None),
        };
    }

    private static SyncLineView Sharing(int pending) => new(Strings.Plural("Sync.Sharing", pending), SyncLineAction.None);
}
```

`AccountViewModel` (constructor gains `ISyncStatus sync, IClockFormat clock`; it subscribes in the constructor and
unsubscribes in `Dispose`):

```csharp
    public string StatusLine => Line.Text;

    public SyncLineAction LineAction => Line.Action;

    /// <summary>The button under the line: "Turn on sharing" or "Choose what to do with them"; empty for none.</summary>
    public string LineActionText => LineAction switch
    {
        SyncLineAction.TurnOnSharing => Strings.Get("Sync.TurnOn"),
        SyncLineAction.ChooseWhatToDo => Strings.Get("Account.ChooseWhatToDo"),
        _ => "",
    };

    /// <summary>The page opens the consent sheet (Spec 011 §3).</summary>
    public event EventHandler? ConsentRequested;

    /// <summary>The page opens the bring-over sheet (Spec 011 §4).</summary>
    public event EventHandler? BringOverRequested;

    private SyncLineView Line => SyncLine.For(State, _sync.Status, _clock.Is24Hour);

    /// <summary>Written by hand rather than with <c>[RelayCommand]</c>: the generated name would clash with <see cref="LineAction"/>.</summary>
    public IRelayCommand LineActionCommand => _lineActionCommand ??= new RelayCommand(() =>
    {
        switch (LineAction)
        {
            case SyncLineAction.TurnOnSharing:
                ConsentRequested?.Invoke(this, EventArgs.Empty);
                break;
            case SyncLineAction.ChooseWhatToDo:
                BringOverRequested?.Invoke(this, EventArgs.Empty);
                break;
        }
    });

    private IRelayCommand? _lineActionCommand;

    // Raised from the engine's background thread; the page dispatches (SettingsPage.OnAccountChanged).
    private void OnSyncChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(StatusLine));
        OnPropertyChanged(nameof(LineAction));
        OnPropertyChanged(nameof(LineActionText));
    }
```

The `[NotifyPropertyChangedFor]` on `_state` gains `nameof(LineAction), nameof(LineActionText)`.
The old `StatusLine` switch is gone: `SyncLine.For` covers the signed-out and join-pending lines.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/Coo.Mobile.Presentation.Tests` and `TZ=UTC dotnet test tests/Coo.Mobile.Presentation.Tests`
Expected: PASS (the localization parity tests included).

- [ ] **Step 6: Commit**

```bash
dotnet format Coo.Tests.slnf
git add eng/strings src/Mobile/Coo.Mobile.Presentation tests/Coo.Mobile.Presentation.Tests
git commit -m "The sync line and every sync string in seven languages; join-pending says what it means now (Spec 011 §2, §8.3)"
```

---
### Task 17: The consent and bring-over view models

**Files:**
- Create: `src/Mobile/Coo.Mobile.Presentation/Settings/ConsentViewModel.cs`, `src/Mobile/Coo.Mobile.Presentation/Settings/BringOverViewModel.cs`
- Modify: `src/Mobile/Coo.Mobile.Presentation/ServiceCollectionExtensions.cs` (both transient)
- Test: `tests/Coo.Mobile.Presentation.Tests/ConsentAndBringOverTests.cs` (create)

**Interfaces:**
- Consumes: `RecordConsent`, `DeclineConsent` (Task 11), `BringEntriesOver`, `BringOverChoice`, `LocalBaby` (Task 14),
  `IExternalLauncher`, `IAboutInfo`, `AppVersionText`, `SettingsViewModel.PrivacyUrl` (existing), `NameKey.From` (Domain).
- Produces:
  - `ConsentViewModel(RecordConsent, DeclineConsent, IExternalLauncher, IAboutInfo)`: static texts `Title`, `Body`,
    `Body2`, `PrivacyText`, `AgreeText`, `NotNowText`; `IsBusy`, `Toast`; `AgreeCommand`, `NotNowCommand`,
    `OpenPrivacyCommand`; `event EventHandler? Closed`.
  - `BabyOption(BabyId? Target, string Label)`; `BabyMapping(LocalBaby Local, IReadOnlyList<BabyOption> Options, int selected)`
    with `Prompt` and observable `Selected`.
  - `BringOverViewModel(BringEntriesOver)`: `LoadAsync()`, `Summary`, `Family`, `Mappings`, `IsMapping`, `IsBusy`,
    `Toast`, static `BringText`, `KeepText`, `ConfirmText`; `BringCommand`, `ConfirmCommand`, `KeepCommand`;
    `event EventHandler? Closed`.

- [ ] **Step 1: Write the failing tests**

`tests/Coo.Mobile.Presentation.Tests/ConsentAndBringOverTests.cs`:

```csharp
using Coo.Domain.Common;
using Coo.Mobile.Application.Auth;
using Coo.Mobile.Application.Preferences;
using Coo.Mobile.Application.Sync;
using Coo.Mobile.Presentation.Localization;
using Coo.Mobile.Presentation.Settings;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Coo.Mobile.Presentation.Tests;

/// <summary>Spec 011 §3 (the consent sheet) and §4 (bring my entries over, keep them separate).</summary>
[Collection(nameof(StringsState))]
public sealed class ConsentAndBringOverTests : IDisposable
{
    private static readonly AccountProfile Profile = new(new UserId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000c1")), "Andrzej", "andrzej@example.com");
    private static readonly HouseholdId Family = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a9"));
    private static readonly HouseholdId Mine = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a1"));
    private static readonly BabyId MyMaja = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000b1"));
    private static readonly BabyId FamilyMaja = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000b9"));

    private readonly ISessionStore _sessions = Substitute.For<ISessionStore>();
    private readonly ISyncState _state = Substitute.For<ISyncState>();
    private readonly IConsentApi _consentApi = Substitute.For<IConsentApi>();
    private readonly ISyncControl _sync = Substitute.For<ISyncControl>();
    private readonly IExternalLauncher _launcher = Substitute.For<IExternalLauncher>();
    private readonly IAboutInfo _about = Substitute.For<IAboutInfo>();
    private readonly IHouseholdMoves _moves = Substitute.For<IHouseholdMoves>();

    public ConsentAndBringOverTests()
    {
        Strings.Use(new LanguagePreference("en"), null);
        _sessions.GetAsync(Arg.Any<CancellationToken>()).Returns(new StoredSession("a", DateTimeOffset.UnixEpoch, "r", Profile, Family, JoinPending: true));
        _about.Informational.Returns("0.11.0+abcdef1234");
        _moves.ActiveHouseholdAsync(Arg.Any<CancellationToken>()).Returns(Mine);
        _moves.SummaryAsync(Mine, Arg.Any<CancellationToken>()).Returns(new LocalSummary(42, [new LocalBaby(MyMaja, "Maja")]));
        _moves.SummaryAsync(Family, Arg.Any<CancellationToken>()).Returns(new LocalSummary(300, [new LocalBaby(FamilyMaja, "Maja")]));
    }

    public void Dispose() => Strings.Use(LanguagePreference.System, null);

    private ConsentViewModel Consent()
    {
        var gate = new ConsentGate(_state, Substitute.For<IAccountApi>(), _consentApi);
        return new ConsentViewModel(new RecordConsent(_sessions, gate, _sync), new DeclineConsent(_sessions, gate, _sync), _launcher, _about);
    }

    private BringOverViewModel BringOver(Account account) => new(new BringEntriesOver(_sessions, _moves, account, _sync));

    [Fact]
    public void Texts_English_AreTheSpecsWording()
    {
        // Assert
        ConsentViewModel.Title.Should().Be("Share your log with your family?");
        ConsentViewModel.Body.Should().StartWith("To share, Coo keeps your baby's care log (feeds, diapers, sleep and pumping");
        ConsentViewModel.Body2.Should().EndWith("The log on this phone always stays here.");
        ConsentViewModel.AgreeText.Should().Be("Agree and share");
        ConsentViewModel.NotNowText.Should().Be("Not now");
    }

    [Fact]
    public async Task AgreeCommand_Recorded_SendsTheVersionAndLanguageAndCloses()
    {
        // Arrange
        var model = Consent();
        var closed = 0;
        model.Closed += (_, _) => closed++;

        // Act
        await model.AgreeCommand.ExecuteAsync(null);

        // Assert
        await _consentApi.Received(1).RecordAsync("0.11.0", "en", Arg.Any<CancellationToken>());
        closed.Should().Be(1);
        _sync.Received(1).RequestSync();
    }

    [Fact]
    public async Task AgreeCommand_Offline_SaysSoAndStaysOpen()
    {
        // Arrange
        _consentApi.RecordAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).ThrowsAsync(new SyncUnavailableException("offline"));
        var model = Consent();
        var closed = 0;
        model.Closed += (_, _) => closed++;

        // Act
        await model.AgreeCommand.ExecuteAsync(null);

        // Assert
        closed.Should().Be(0);
        model.Toast.Should().Be("Sharing couldn't be turned on. Check your connection and try again.");
        model.IsBusy.Should().BeFalse();
    }

    [Fact]
    public async Task NotNowCommand_Declines_SendsNothingAndCloses()
    {
        // Arrange
        var model = Consent();
        var closed = 0;
        model.Closed += (_, _) => closed++;

        // Act
        await model.NotNowCommand.ExecuteAsync(null);

        // Assert
        await _state.Received(1).SetConsentAsync(Profile.UserId, ConsentState.Declined, Arg.Any<CancellationToken>());
        await _consentApi.DidNotReceiveWithAnyArgs().RecordAsync(default!, default!, default);
        closed.Should().Be(1);
    }

    [Fact]
    public async Task OpenPrivacyCommand_OpensThePolicy()
    {
        // Act
        await Consent().OpenPrivacyCommand.ExecuteAsync(null);

        // Assert
        await _launcher.Received(1).OpenBrowserAsync(SettingsViewModel.PrivacyUrl);
    }

    [Fact]
    public async Task LoadAsync_JoinPending_SaysHowManyEntriesAndWhoIsInTheFamilysLog()
    {
        // Arrange
        using var account = new Account(_sessions);
        var model = BringOver(account);

        // Act
        await model.LoadAsync();

        // Assert
        model.Summary.Should().Be("This phone has 42 entries for Maja.");
        model.Family.Should().Be("Your family's log has: Maja.");
        var mapping = model.Mappings.Should().ContainSingle().Subject;
        mapping.Prompt.Should().Be("Maja on this phone is");
        mapping.Options.Select(o => o.Label).Should().Equal("Maja", "Add as a new baby");
        mapping.Selected.Should().Be(0, "the same name is the same baby by default");
    }

    [Fact]
    public async Task ConfirmCommand_AfterMapping_BringsTheEntriesOverAndCloses()
    {
        // Arrange
        using var account = new Account(_sessions);
        await account.LoadAsync(CancellationToken.None);
        var model = BringOver(account);
        await model.LoadAsync();
        var closed = 0;
        model.Closed += (_, _) => closed++;

        // Act
        await model.BringCommand.ExecuteAsync(null);
        await model.ConfirmCommand.ExecuteAsync(null);

        // Assert
        await _moves.Received(1).BringOverAsync(Arg.Is<BringOverPlan>(p => p.Babies[MyMaja] == FamilyMaja), Profile.UserId, Arg.Any<CancellationToken>());
        closed.Should().Be(1);
    }

    [Fact]
    public async Task ConfirmCommand_TheTransactionFails_SaysNothingChangedAndStaysOpen()
    {
        // Arrange
        _moves.BringOverAsync(default!, default, default).ReturnsForAnyArgs(Task.FromException(new IOException("disk full")));
        using var account = new Account(_sessions);
        var model = BringOver(account);
        await model.LoadAsync();

        // Act
        await model.ConfirmCommand.ExecuteAsync(null);

        // Assert
        model.Toast.Should().Be("That didn't work. Nothing was changed.");
        model.IsBusy.Should().BeFalse();
    }

    [Fact]
    public async Task KeepCommand_KeepsThemSeparate_ClosesAndMovesNothing()
    {
        // Arrange
        using var account = new Account(_sessions);
        var model = BringOver(account);
        await model.LoadAsync();
        var closed = 0;
        model.Closed += (_, _) => closed++;

        // Act
        model.KeepCommand.Execute(null);

        // Assert
        closed.Should().Be(1);
        await _moves.DidNotReceiveWithAnyArgs().BringOverAsync(default!, default, default);
    }

    [Fact]
    public async Task LoadAsync_DifferentNamesOneBabyEach_DefaultsToTheFamilysBaby()
    {
        // Arrange: the phone's setup used the default name "Baby".
        _moves.SummaryAsync(Mine, Arg.Any<CancellationToken>()).Returns(new LocalSummary(3, [new LocalBaby(MyMaja, "Baby")]));
        using var account = new Account(_sessions);
        var model = BringOver(account);

        // Act
        await model.LoadAsync();

        // Assert
        model.Mappings.Single().Selected.Should().Be(0);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Coo.Mobile.Presentation.Tests --filter FullyQualifiedName~ConsentAndBringOverTests`
Expected: FAIL to compile. Paste the first error.

- [ ] **Step 3: Implement**

`src/Mobile/Coo.Mobile.Presentation/Settings/ConsentViewModel.cs`:

```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Coo.Mobile.Application.Sync;
using Coo.Mobile.Presentation.Localization;

namespace Coo.Mobile.Presentation.Settings;

/// <summary>
/// The consent sheet (Spec 011 §3, wording <c>care-data-v1</c>). Agree records it on the server with the app version and
/// language, then a cycle starts; Not now (or Back, which the page maps here) sends nothing.
/// </summary>
public sealed partial class ConsentViewModel(RecordConsent record, DeclineConsent decline, IExternalLauncher launcher, IAboutInfo about) : ObservableObject
{
    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _toast = "";

    public event EventHandler? Closed;

    public static string Title => Strings.Get("Consent.Title");

    public static string Body => Strings.Get("Consent.Body");

    public static string Body2 => Strings.Get("Consent.Body2");

    public static string PrivacyText => Strings.Get("Consent.Privacy");

    public static string AgreeText => Strings.Get("Consent.Agree");

    public static string NotNowText => Strings.Get("Consent.NotNow");

    [RelayCommand]
    private async Task AgreeAsync()
    {
        if (IsBusy)
            return;
        IsBusy = true;
        Toast = "";
        try
        {
            var version = AppVersionText.Parse(about.Informational).Version;
            if (await record.ExecuteAsync(version, Strings.Language, CancellationToken.None))
                Closed?.Invoke(this, EventArgs.Empty);
            else
                Toast = Strings.Get("Consent.Failed");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task NotNowAsync()
    {
        await decline.ExecuteAsync(CancellationToken.None);
        Closed?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private Task OpenPrivacyAsync() => launcher.OpenBrowserAsync(SettingsViewModel.PrivacyUrl);
}
```

`src/Mobile/Coo.Mobile.Presentation/Settings/BringOverViewModel.cs`:

```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Coo.Domain.Common;
using Coo.Domain.Households;
using Coo.Mobile.Application.Sync;
using Coo.Mobile.Presentation.Localization;

namespace Coo.Mobile.Presentation.Settings;

public sealed record BabyOption(BabyId? Target, string Label);

/// <summary>One local baby and where it goes: a family baby, or "Add as a new baby" (the last option).</summary>
public sealed partial class BabyMapping(LocalBaby local, IReadOnlyList<BabyOption> options, int selected) : ObservableObject
{
    [ObservableProperty]
    private int _selected = selected;

    public LocalBaby Local => local;

    public IReadOnlyList<BabyOption> Options => options;

    public string Prompt => Strings.Format("BringOver.Which", local.Name);
}

/// <summary>
/// "This phone has 42 entries for Maja. Your family's log has: Maja." (Spec 011 §4). Bring my entries over asks where
/// each local baby goes, then one transaction moves everything; Keep them separate closes and changes nothing (Q4).
/// </summary>
public sealed partial class BringOverViewModel(BringEntriesOver bringOver) : ObservableObject
{
    [ObservableProperty]
    private string _summary = "";

    [ObservableProperty]
    private string _family = "";

    [ObservableProperty]
    private IReadOnlyList<BabyMapping> _mappings = [];

    [ObservableProperty]
    private bool _isMapping;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _toast = "";

    public event EventHandler? Closed;

    public static string BringText => Strings.Get("BringOver.Bring");

    public static string KeepText => Strings.Get("BringOver.Keep");

    public static string ConfirmText => Strings.Get("BringOver.Confirm");

    public async Task LoadAsync()
    {
        var choice = await bringOver.PrepareAsync(CancellationToken.None);
        if (choice is null)
        {
            Closed?.Invoke(this, EventArgs.Empty);
            return;
        }

        Summary = Strings.Plural("BringOver.Entries", choice.Local.EntryCount, string.Join(", ", choice.Local.Babies.Select(b => b.Name)));
        Family = choice.Family.Count == 0
            ? Strings.Get("BringOver.FamilyEmpty")
            : Strings.Format("BringOver.Family", string.Join(", ", choice.Family.Select(b => b.Name)));
        IReadOnlyList<BabyOption> options = [.. choice.Family.Select(b => new BabyOption(b.Id, b.Name)), new BabyOption(null, Strings.Get("BringOver.AddNew"))];
        Mappings = [.. choice.Local.Babies.Select(b => new BabyMapping(b, options, DefaultFor(b, choice)))];
    }

    /// <summary>The same name (as NameKey compares names) is the same baby; one baby on each side is the same baby; else new.</summary>
    private static int DefaultFor(LocalBaby local, BringOverChoice choice)
    {
        var key = NameKey.From(local.Name);
        var byName = choice.Family.ToList().FindIndex(f => NameKey.From(f.Name) == key);
        if (byName >= 0)
            return byName;
        return choice.Family.Count == 1 && choice.Local.Babies.Count == 1 ? 0 : choice.Family.Count;
    }

    [RelayCommand]
    private async Task BringAsync()
    {
        if (Mappings.Count == 0)
            await ConfirmAsync();
        else
            IsMapping = true;
    }

    [RelayCommand]
    private async Task ConfirmAsync()
    {
        if (IsBusy)
            return;
        IsBusy = true;
        Toast = "";
        try
        {
            var mapping = Mappings.ToDictionary(m => m.Local.Id, m => m.Options[Math.Clamp(m.Selected, 0, m.Options.Count - 1)].Target);
            if (await bringOver.ExecuteAsync(mapping, CancellationToken.None))
                Closed?.Invoke(this, EventArgs.Empty);
            else
                Toast = Strings.Get("BringOver.Failed");
        }
#pragma warning disable CA1031 // The transaction rolled back (Spec 011 §4): say so; never crash the sheet.
        catch (Exception)
#pragma warning restore CA1031
        {
            Toast = Strings.Get("BringOver.Failed");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Keep() => Closed?.Invoke(this, EventArgs.Empty);
}
```

Register both in `AddMobilePresentation`: `services.AddTransient<Settings.ConsentViewModel>(); services.AddTransient<Settings.BringOverViewModel>();`.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Coo.Mobile.Presentation.Tests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
dotnet format Coo.Tests.slnf
git add src/Mobile/Coo.Mobile.Presentation tests/Coo.Mobile.Presentation.Tests
git commit -m "Consent and bring-over view models: agree records it, not now sends nothing, bring over maps each baby (Spec 011 §3, §4)"
```

---

### Task 18: "Not accepted by the server" in Needs attention, and pages refreshing after a pull

**Files:**
- Modify: `src/Mobile/Coo.Mobile.Application/CareLog/Attention.cs` (`GetAttention` lists rejected entries)
- Modify: `src/Mobile/Coo.Mobile.Presentation/Attention/AttentionViewModel.cs` (the group, 50 at a time)
- Modify: `src/Mobile/Coo.Mobile.Presentation/Today/Undo.cs` (`ExternalChange()`)
- Test: `tests/Coo.Mobile.Presentation.Tests/AttentionRejectedTests.cs` (create); modify
  `tests/Coo.Mobile.Application.Tests/DayQueriesTests.cs` (the new `GetAttention` argument)

**Interfaces:**
- Consumes: `ISyncStore.ListRejectionsAsync` (Task 11/13), `Strings.Error` (existing).
- Produces:
  - `RejectedEntry(StoredEntry Entry, string Reason)`; `AttentionSummary(…, IReadOnlyList<RejectedEntry> Rejected)` whose
    `Count` includes them; `GetAttention(ICareLogStore, IDuplicateDismissalStore, ISyncStore)`.
  - `AttentionViewModel.Rejected` (`IReadOnlyList<AttentionEntry>`), `RejectedTotal`, `HasMoreRejected`,
    `ShowMoreRejectedCommand`, `EditRequested` (`EventHandler<EntryId>`), `AttentionViewModel.RejectedPageSize = 50`.
  - `UndoService.ExternalChange()`: raises `DataChanged`, so pages refresh after a pull.

- [ ] **Step 1: Write the failing tests**

In `tests/Coo.Mobile.Application.Tests/DayQueriesTests.cs`, each `new GetAttention(store, dismissals)` becomes
`new GetAttention(store, dismissals, NoRejections())` with:

```csharp
    private static Coo.Mobile.Application.Sync.ISyncStore NoRejections()
    {
        var sync = NSubstitute.Substitute.For<Coo.Mobile.Application.Sync.ISyncStore>();
        sync.ListRejectionsAsync(NSubstitute.Arg.Any<HouseholdId>(), NSubstitute.Arg.Any<CancellationToken>()).Returns([]);
        return sync;
    }
```

`tests/Coo.Mobile.Presentation.Tests/AttentionRejectedTests.cs`:

```csharp
using Coo.Contracts.Sync;
using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Duplicates;
using Coo.Domain.CareLog.Kinds.Diaper;
using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;
using Coo.Domain.Households;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Application.Households;
using Coo.Mobile.Application.Preferences;
using Coo.Mobile.Application.Sync;
using Coo.Mobile.Presentation.Attention;
using Coo.Mobile.Presentation.Localization;
using Coo.Mobile.Presentation.Today;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Coo.Mobile.Presentation.Tests;

/// <summary>Spec 011 §2: rejected changes fill "Not accepted by the server", with the reason, Edit and Delete; usable at hundreds.</summary>
[Collection(nameof(StringsState))]
public sealed class AttentionRejectedTests : IDisposable
{
    private static readonly HouseholdId Household = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a1"));
    private static readonly BabyId Baby = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000b1"));
    private static readonly DeviceId Phone = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000d1"));
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 14, 0, 0, TimeSpan.FromHours(2));

    private readonly FakeTimeProvider _time = new(Now);
    private readonly ICareLogStore _store = Substitute.For<ICareLogStore>();
    private readonly IDuplicateDismissalStore _dismissals = Substitute.For<IDuplicateDismissalStore>();
    private readonly ISyncStore _sync = Substitute.For<ISyncStore>();
    private readonly IHouseholdStore _households = Substitute.For<IHouseholdStore>();
    private readonly IVersionClock _clock = Substitute.For<IVersionClock>();

    public AttentionRejectedTests()
    {
        Strings.Use(new LanguagePreference("en"), null);
        _households.ListHouseholdsAsync(Arg.Any<CancellationToken>()).Returns(
            [HouseholdSettings.Create(Household, VolumeUnit.Millilitres, "Europe/Amsterdam", new HlcStamp(1, 0, Phone)).Value]);
        _store.ListAsync(Household, Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns([]);
        _dismissals.ListAsync(Household, Arg.Any<CancellationToken>()).Returns(new HashSet<EntryPair>());
    }

    public void Dispose() => Strings.Use(LanguagePreference.System, null);

    private CareEntry Diaper(int n)
    {
        var id = new EntryId(Guid.Parse("7a1c0000-0000-7000-9000-" + n.ToString("D12", System.Globalization.CultureInfo.InvariantCulture)));
        return CareEntry.Create(id, Household, EntrySubject.Baby(Baby), new DiaperDetails(DiaperContents.Wet), EntryTiming.Point(Now.AddMinutes(-n)),
            PersonRef.ThisDevicesPerson, EntrySource.Manual, new HlcStamp(n + 1, 0, Phone), Now).Value;
    }

    private void Rejected(int count)
    {
        var entries = Enumerable.Range(1, count).Select(Diaper).ToList();
        _sync.ListRejectionsAsync(Household, Arg.Any<CancellationToken>()).Returns(
            [.. entries.Select(e => new SyncRejection(SyncEntityTypes.CareEntry, e.Id.Value, "time.end-not-after-start", Now))]);
        foreach (var entry in entries)
            _store.FindAsync(entry.Id, Arg.Any<CancellationToken>()).Returns(new LiveEntry(entry));
    }

    private AttentionViewModel Model()
    {
        var clockFormat = Substitute.For<IClockFormat>();
        clockFormat.Is24Hour.Returns(true);
        var undo = new UndoService(new DeleteEntry(_store, _clock), new ReviseEntry(_store, _clock, _time), _store, _time);
        return new AttentionViewModel(_households, new GetAttention(_store, _dismissals, _sync), new DismissDuplicate(_dismissals, _clock, _time), undo, clockFormat, _time);
    }

    [Fact]
    public async Task RefreshAsync_ARejectedEntry_IsListedWithItsReasonAndCounted()
    {
        // Arrange
        Rejected(1);
        var model = Model();

        // Act
        await model.RefreshAsync();

        // Assert
        var item = model.Rejected.Should().ContainSingle().Subject;
        item.Reason.Should().Be("The end must be after the start.");
        model.IsEmpty.Should().BeFalse();
        (await new GetAttention(_store, _dismissals, _sync).ExecuteAsync(Household, Now, CancellationToken.None)).Count.Should().Be(1);
    }

    [Fact]
    public async Task RefreshAsync_ThreeHundredRejected_ShowsFiftyAndMoreOnRequest()
    {
        // Arrange: a parent who ignored updates for a month (ADR 0002 §11).
        Rejected(300);
        var model = Model();
        await model.RefreshAsync();
        var first = model.Rejected.Count;

        // Act
        model.ShowMoreRejectedCommand.Execute(null);

        // Assert
        first.Should().Be(AttentionViewModel.RejectedPageSize);
        model.Rejected.Count.Should().Be(2 * AttentionViewModel.RejectedPageSize);
        model.RejectedTotal.Should().Be(300);
        model.HasMoreRejected.Should().BeTrue();
    }

    [Fact]
    public async Task EditCommand_ARejectedEntry_AsksThePageToOpenIt()
    {
        // Arrange
        Rejected(1);
        var model = Model();
        await model.RefreshAsync();
        EntryId? opened = null;
        model.EditRequested += (_, id) => opened = id;

        // Act
        model.EditRejectedCommand.Execute(model.Rejected[0].Id);

        // Assert
        opened.Should().Be(model.Rejected[0].Id);
    }

    [Fact]
    public async Task RefreshAsync_RejectedEntryDeletedSince_IsNotListed()
    {
        // Arrange
        Rejected(1);
        var entry = Diaper(1);
        entry.Delete(new HlcStamp(100, 0, Phone));
        _store.FindAsync(entry.Id, Arg.Any<CancellationToken>()).Returns(new LiveEntry(entry));
        var model = Model();

        // Act
        await model.RefreshAsync();

        // Assert
        model.Rejected.Should().BeEmpty();
    }

    [Fact]
    public void ExternalChange_FromSync_RaisesDataChanged()
    {
        // Arrange
        using var undo = new UndoService(new DeleteEntry(_store, _clock), new ReviseEntry(_store, _clock, _time), _store, _time);
        var raised = 0;
        undo.DataChanged += (_, _) => raised++;

        // Act
        undo.ExternalChange();

        // Assert
        raised.Should().Be(1);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Coo.Mobile.Presentation.Tests --filter FullyQualifiedName~AttentionRejectedTests`
Expected: FAIL to compile. Paste the first error.

- [ ] **Step 3: Implement**

`src/Mobile/Coo.Mobile.Application/CareLog/Attention.cs`:

```csharp
/// <summary>A change the server did not accept (ADR 0002 §5): the entry stays, with the reason's code.</summary>
public sealed record RejectedEntry(StoredEntry Entry, string Reason);

/// <summary>What needs a person (Spec 007 §6, Spec 011 §2): duplicates, unreadable entries, entries from a newer app, rejected changes.</summary>
public sealed record AttentionSummary(
    IReadOnlyList<DuplicatePair> Duplicates,
    IReadOnlyList<QuarantinedEntry> Quarantined,
    IReadOnlyList<ReadOnlyEntry> ReadOnly,
    IReadOnlyList<RejectedEntry> Rejected)
{
    public int Count => Duplicates.Count + Quarantined.Count + ReadOnly.Count + Rejected.Count;
}

public sealed class GetAttention(ICareLogStore store, IDuplicateDismissalStore dismissals, Coo.Mobile.Application.Sync.ISyncStore sync)
{
    public static readonly TimeSpan Window = TimeSpan.FromDays(7);

    public async Task<AttentionSummary> ExecuteAsync(HouseholdId household, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var stored = await store.ListAsync(household, now - Window, now.AddDays(1), cancellationToken);
        var live = stored.OfType<LiveEntry>().Select(l => l.Entry).ToList();
        var dismissed = await dismissals.ListAsync(household, cancellationToken);
        var pairs = DuplicateDetector.Find(live, live, dismissed, now);

        // Rejected care entries, whatever their date; deleted ones are done with. Other types are counted on the debug
        // screen only (ruling 14).
        var rejected = new List<RejectedEntry>();
        foreach (var rejection in await sync.ListRejectionsAsync(household, cancellationToken))
        {
            if (rejection.EntityType != Coo.Contracts.Sync.SyncEntityTypes.CareEntry)
                continue;
            if (await store.FindAsync(new EntryId(rejection.EntityId), cancellationToken) is { } entry && entry is not LiveEntry { Entry.IsDeleted: true })
                rejected.Add(new RejectedEntry(entry, rejection.Reason));
        }

        return new AttentionSummary(pairs, [.. stored.OfType<QuarantinedEntry>()], [.. stored.OfType<ReadOnlyEntry>()], rejected);
    }
}
```

`AttentionViewModel`:

```csharp
    public const int RejectedPageSize = 50;

    private IReadOnlyList<AttentionEntry> _allRejected = [];
    private int _rejectedShown = RejectedPageSize;

    [ObservableProperty]
    private IReadOnlyList<AttentionEntry> _rejected = [];

    public int RejectedTotal => _allRejected.Count;

    public bool HasMoreRejected => Rejected.Count < _allRejected.Count;

    /// <summary>The page opens the edit form for a rejected entry (fixing it writes a new version, which goes back out).</summary>
    public event EventHandler<EntryId>? EditRequested;

    // In RefreshAsync, after Newer:
        _allRejected = [.. attention.Rejected.Where(r => r.Entry.Id != pending)
            .Select(r => new AttentionEntry(r.Entry.Id, r.Entry.KindCode, formats.Time(r.Entry.Start), Strings.Error(r.Reason)))];
        ShowRejected();
        IsEmpty = Duplicates.Count + Unreadable.Count + Newer.Count + _allRejected.Count == 0;

    [RelayCommand]
    private void ShowMoreRejected()
    {
        _rejectedShown += RejectedPageSize;
        ShowRejected();
    }

    [RelayCommand]
    private void EditRejected(EntryId id) => EditRequested?.Invoke(this, id);

    private void ShowRejected()
    {
        Rejected = [.. _allRejected.Take(_rejectedShown)];
        OnPropertyChanged(nameof(RejectedTotal));
        OnPropertyChanged(nameof(HasMoreRejected));
    }
```

(Replace the existing `IsEmpty = …` line with the one above.)

`UndoService`:

```csharp
    /// <summary>Data changed under the pages without an action of theirs (a sync pull, a move): they refresh.</summary>
    public void ExternalChange() => DataChanged?.Invoke(this, EventArgs.Empty);
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Coo.Mobile.Application.Tests && dotnet test tests/Coo.Mobile.Presentation.Tests`
Expected: PASS. Today's "n need attention" badge now includes rejected entries (it sums `AttentionSummary.Count`).

- [ ] **Step 5: Commit**

```bash
dotnet format Coo.Tests.slnf
git add src/Mobile tests/Coo.Mobile.Application.Tests tests/Coo.Mobile.Presentation.Tests
git commit -m "Needs attention: Not accepted by the server, with the reason, Edit and Delete, fifty at a time (Spec 011 §2)"
```

---
### Task 19: On the phone: connectivity, lifecycle, the consent and bring-over pages, the sync line, the rejected group, 0.11.0

**Files:**
- Modify: `src/Mobile/Coo.Mobile.Application/Sync/Ports.cs` (`IConnectivityWatcher`)
- Create: `src/Mobile/Coo.Mobile.Platform/Sync/AndroidConnectivity.cs`; modify `src/Mobile/Coo.Mobile.Platform/ServiceCollectionExtensions.cs`
- Create: `src/Mobile/Coo.Mobile/Sync/SyncPrompter.cs`, `src/Mobile/Coo.Mobile/Pages/ConsentPage.cs`, `src/Mobile/Coo.Mobile/Pages/BringOverPage.cs`
- Modify: `src/Mobile/Coo.Mobile/App.xaml.cs`, `StartupPage.cs`, `MauiProgram.cs`, `Pages/SettingsPage.cs`,
  `Pages/AttentionPage.cs`, `Coo.Mobile.csproj` (0.11.0, versionCode 14)

**Interfaces:**
- Consumes: `SyncEngine` (Task 12), `ConsentViewModel`, `BringOverViewModel` (Task 17), `AccountViewModel.LineAction*`
  (Task 16), `AttentionViewModel.Rejected*` and `UndoService.ExternalChange` (Task 18).
- Produces: `IConnectivityWatcher { event EventHandler? Regained; void Start(); }`; `SyncPrompter.Attach()`,
  `ShowConsentAsync()`, `ShowBringOverAsync()`; the APK at 0.11.0 (14).

This task has no unit tests: the host and the Android callback are not in `Coo.Tests.slnf` (ADR 0001). Its checks are
the build, the architecture tests, and the smoke run in Step 8; the deliverables in Task 23 exercise it fully.

- [ ] **Step 1: The connectivity port and its Android adapter**

Append to `src/Mobile/Coo.Mobile.Application/Sync/Ports.cs`:

```csharp
/// <summary>Connectivity regained (Spec 011 §6.3): a cycle at once, and the backoff resets.</summary>
public interface IConnectivityWatcher
{
    event EventHandler? Regained;

    void Start();
}
```

`src/Mobile/Coo.Mobile.Platform/Sync/AndroidConnectivity.cs`:

```csharp
using Android.Content;
using Android.Net;
using Coo.Mobile.Application.Sync;

namespace Coo.Mobile.Platform.Sync;

/// <summary>
/// Android's default-network callback (API 24+; Coo's minimum is 26). The callback is Java calling .NET: nothing may
/// escape it, or the app dies (Increment 10 lesson). Without a signal the 30-second pull still runs.
/// </summary>
internal sealed class AndroidConnectivity(Func<Context> context) : IConnectivityWatcher, IDisposable
{
    private Callback? _callback;
    private ConnectivityManager? _manager;

    public event EventHandler? Regained;

    public void Start()
    {
        if (_callback is not null)
            return;
        try
        {
            _manager = context().GetSystemService(Context.ConnectivityService) as ConnectivityManager;
            if (_manager is null)
                return;
            _callback = new Callback(this);
            _manager.RegisterDefaultNetworkCallback(_callback);
        }
#pragma warning disable CA1031 // No signal is fine: the periodic pull covers it.
        catch (Exception)
#pragma warning restore CA1031
        {
            _callback = null;
        }
    }

    public void Dispose()
    {
        try
        {
            if (_callback is not null)
                _manager?.UnregisterNetworkCallback(_callback);
        }
#pragma warning disable CA1031 // Shutting down: nothing to do about a callback Android already dropped.
        catch (Exception)
#pragma warning restore CA1031
        {
        }

        _callback?.Dispose();
    }

    private void Raise() => Regained?.Invoke(this, EventArgs.Empty);

    private sealed class Callback(AndroidConnectivity owner) : ConnectivityManager.NetworkCallback
    {
        public override void OnAvailable(Network network)
        {
            try
            {
                owner.Raise();
            }
#pragma warning disable CA1031 // A Java callback: a managed exception escaping here would crash the process.
            catch (Exception)
#pragma warning restore CA1031
            {
            }
        }
    }
}
```

In `AddMobilePlatform`: `services.AddSingleton<IConnectivityWatcher>(_ => new Sync.AndroidConnectivity(() => Android.App.Application.Context));`

- [ ] **Step 2: The consent page**

`src/Mobile/Coo.Mobile/Pages/ConsentPage.cs`:

```csharp
using Coo.Mobile.Components;
using Coo.Mobile.Presentation.Settings;

namespace Coo.Mobile.Pages;

/// <summary>
/// The consent sheet (Spec 011 §3) as a modal page, so it opens over whatever is showing (ruling 15). Back is "Not now".
/// Every handler is async void and catches everything.
/// </summary>
public sealed class ConsentPage : ContentPage
{
    private readonly ConsentViewModel _model;
    private readonly Label _toast = new() { IsVisible = false };

    public ConsentPage(ConsentViewModel model)
    {
        _model = model;
        SetDynamicResource(BackgroundColorProperty, "Ground");
        Shell.SetNavBarIsVisible(this, false);
        model.Closed += (_, _) => Dispatcher.Dispatch(async () => await CloseAsync());
        model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ConsentViewModel.Toast))
                Dispatcher.Dispatch(() =>
                {
                    _toast.Text = _model.Toast;
                    _toast.IsVisible = _model.Toast.Length > 0;
                });
        };

        var privacy = Ui.Text(ConsentViewModel.PrivacyText, "BodyStrong", "Accent");
        privacy.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(async () => await RunAsync(() => _model.OpenPrivacyCommand.ExecuteAsync(null))) });
        var agree = new Button { Text = ConsentViewModel.AgreeText };
        agree.SetDynamicResource(StyleProperty, "PrimaryButton");
        agree.Clicked += async (_, _) => await RunAsync(() => _model.AgreeCommand.ExecuteAsync(null));
        var notNow = new Button { Text = ConsentViewModel.NotNowText };
        notNow.SetDynamicResource(StyleProperty, "GhostButton");
        notNow.Clicked += async (_, _) => await RunAsync(() => _model.NotNowCommand.ExecuteAsync(null));
        _toast.SetDynamicResource(Label.StyleProperty, "Meta");

        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Spacing = 16,
                Padding = new Thickness(24, 48, 24, 32),
                Children =
                {
                    Ui.Text(ConsentViewModel.Title, "Title"),
                    Ui.Text(ConsentViewModel.Body, "Body"),
                    Ui.Text(ConsentViewModel.Body2, "Body"),
                    privacy,
                    _toast,
                    agree,
                    notNow,
                },
            },
        };
    }

    protected override bool OnBackButtonPressed()
    {
        Dispatcher.Dispatch(async () => await RunAsync(() => _model.NotNowCommand.ExecuteAsync(null)));
        return true;
    }

    private async Task CloseAsync()
    {
        try
        {
            if (Navigation.ModalStack.Contains(this))
                await Navigation.PopModalAsync();
        }
#pragma warning disable CA1031 // Closing twice (a double tap) must not crash.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    private async Task RunAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
#pragma warning disable CA1031 // async void handlers: nothing may escape.
        catch (Exception)
#pragma warning restore CA1031
        {
            _toast.Text = Coo.Mobile.Presentation.Localization.Strings.Get("Error.Generic");
            _toast.IsVisible = true;
        }
    }
}
```

(`Ui.Text(text, style, colour)` is the existing helper in `Components`; if its third parameter is not a colour key,
use `Ui.Text(text, "BodyStrong")` and set `TextColor` from the `Accent` resource with `SetDynamicResource`.)

- [ ] **Step 3: The bring-over page**

`src/Mobile/Coo.Mobile/Pages/BringOverPage.cs`:

```csharp
using Coo.Mobile.Components;
using Coo.Mobile.Presentation.Settings;

namespace Coo.Mobile.Pages;

/// <summary>"Bring my entries over" / "Keep them separate" (Spec 011 §4), then where each local baby goes. Back is "Keep".</summary>
public sealed class BringOverPage : ContentPage
{
    private readonly BringOverViewModel _model;
    private readonly ContentView _body = new();
    private bool _loaded;

    public BringOverPage(BringOverViewModel model)
    {
        _model = model;
        SetDynamicResource(BackgroundColorProperty, "Ground");
        Shell.SetNavBarIsVisible(this, false);
        Content = new ScrollView { Content = _body };
        model.Closed += (_, _) => Dispatcher.Dispatch(async () => await CloseAsync());
        model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(BringOverViewModel.IsMapping) or nameof(BringOverViewModel.Toast) or nameof(BringOverViewModel.Mappings))
                Dispatcher.Dispatch(Render);
        };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_loaded)
            return;
        _loaded = true;
        await RunAsync(_model.LoadAsync);
        Render();
    }

    protected override bool OnBackButtonPressed()
    {
        _model.KeepCommand.Execute(null);
        return true;
    }

    private void Render()
    {
        var stack = new VerticalStackLayout { Spacing = 16, Padding = new Thickness(24, 48, 24, 32) };
        stack.Add(Ui.Text(_model.Summary, "Title"));
        stack.Add(Ui.Text(_model.Family, "Body"));
        if (!_model.IsMapping)
        {
            stack.Add(Primary(BringOverViewModel.BringText, () => _model.BringCommand.ExecuteAsync(null)));
            stack.Add(Ghost(BringOverViewModel.KeepText, () =>
            {
                _model.KeepCommand.Execute(null);
                return Task.CompletedTask;
            }));
        }
        else
        {
            foreach (var mapping in _model.Mappings)
            {
                var picker = new Picker { ItemsSource = mapping.Options.Select(o => o.Label).ToList(), SelectedIndex = mapping.Selected };
                picker.SelectedIndexChanged += (_, _) => mapping.Selected = picker.SelectedIndex;
                stack.Add(Ui.Text(mapping.Prompt, "BodyStrong"));
                stack.Add(picker);
            }

            stack.Add(Primary(BringOverViewModel.ConfirmText, () => _model.ConfirmCommand.ExecuteAsync(null)));
        }

        if (_model.Toast.Length > 0)
            stack.Add(Ui.Text(_model.Toast, "Meta"));
        _body.Content = stack;
    }

    private Button Primary(string text, Func<Task> action) => Styled(text, "PrimaryButton", action);

    private Button Ghost(string text, Func<Task> action) => Styled(text, "GhostButton", action);

    private Button Styled(string text, string style, Func<Task> action)
    {
        var button = new Button { Text = text };
        button.SetDynamicResource(StyleProperty, style);
        button.Clicked += async (_, _) => await RunAsync(action);
        return button;
    }

    private async Task CloseAsync()
    {
        try
        {
            if (Navigation.ModalStack.Contains(this))
                await Navigation.PopModalAsync();
        }
#pragma warning disable CA1031 // Closing twice must not crash.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    private static async Task RunAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
#pragma warning disable CA1031 // async void handlers: nothing may escape; the view model already shows its own failures.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }
}
```

- [ ] **Step 4: The prompter**

`src/Mobile/Coo.Mobile/Sync/SyncPrompter.cs`:

```csharp
using Coo.Mobile.Application.Auth;
using Coo.Mobile.Application.Sync;
using Coo.Mobile.Pages;

namespace Coo.Mobile.Sync;

/// <summary>
/// Opens the consent sheet when a cycle finds no consent (Spec 011 §3: "right after a successful sign-in, in place, and
/// whenever a cycle would start for an account without consent"), at most once per app run and sign-in; and the
/// bring-over sheet once when join-pending (§4). Settings opens either at any time. One sheet at a time.
/// </summary>
internal sealed class SyncPrompter(SyncEngine engine, Account account, IServiceProvider services)
{
    private bool _consentShown;
    private bool _bringOverShown;
    private bool _showing;

    public void Attach()
    {
        engine.StatusChanged += OnStatusChanged;
        account.Changed += (_, _) =>
        {
            if (account.State is SignedOut)
                (_consentShown, _bringOverShown) = (false, false);
        };
    }

    public Task ShowConsentAsync() => ShowAsync(services.GetRequiredService<ConsentPage>());

    public Task ShowBringOverAsync() => ShowAsync(services.GetRequiredService<BringOverPage>());

    private void OnStatusChanged(object? sender, EventArgs e) => MainThread.BeginInvokeOnMainThread(async () =>
    {
        try
        {
            switch (engine.Status.Last)
            {
                case CycleOutcome.ConsentNeeded when !_consentShown:
                    _consentShown = true;
                    await ShowConsentAsync();
                    break;
                case CycleOutcome.JoinPending when !_bringOverShown:
                    _bringOverShown = true;
                    await ShowBringOverAsync();
                    break;
            }
        }
#pragma warning disable CA1031 // A sheet that cannot open now is offered again from Settings; never a crash.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    });

    private async Task ShowAsync(Page page)
    {
        if (_showing || Microsoft.Maui.Controls.Application.Current?.Windows is not { Count: > 0 } windows || windows[0].Page is not { } root)
            return;
        _showing = true;
        page.Disappearing += (_, _) => _showing = false;
        try
        {
            await root.Navigation.PushModalAsync(page);
        }
        catch
        {
            _showing = false;
            throw;
        }
    }
}
```

- [ ] **Step 5: Lifecycle, start-up and wiring**

`MauiProgram.cs`: register `builder.Services.AddTransient<ConsentPage>(); builder.Services.AddTransient<BringOverPage>();
builder.Services.AddSingleton<Sync.SyncPrompter>();`.

`App.xaml.cs`, in `CreateWindow`, after the `Resumed` theme line:

```csharp
        // Sync triggers (Spec 011 §6.3): resume, and one last cycle on the way to the background (Android may cut it; safe).
        window.Resumed += (_, _) => Lifecycle(e => e.Resumed());
        window.Stopped += (_, _) => Lifecycle(e => e.Backgrounded());
```

with

```csharp
    private void Lifecycle(Action<Coo.Mobile.Application.Sync.SyncEngine> action)
    {
        try
        {
            action(_services.GetRequiredService<Coo.Mobile.Application.Sync.SyncEngine>());
        }
#pragma warning disable CA1031 // A window event handler: a failure here must not end the app; the 30-second pull goes on.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }
```

`StartupPage.cs`: once the first real page is set (in both branches: right after `Window.Page = AppShell` and inside the
setup `Completed` handler before `window.Page = …AppShell`), call `StartSync();`:

```csharp
    /// <summary>Sync starts once a real page shows (Spec 011 §6.3): the engine, connectivity, the prompts, page refreshes.</summary>
    private void StartSync()
    {
        try
        {
            var engine = _services.GetRequiredService<Coo.Mobile.Application.Sync.SyncEngine>();
            var undo = _services.GetRequiredService<Coo.Mobile.Presentation.Today.UndoService>();
            engine.DataChanged += (_, _) => undo.ExternalChange();
            _services.GetRequiredService<Sync.SyncPrompter>().Attach();
            var connectivity = _services.GetRequiredService<Coo.Mobile.Application.Sync.IConnectivityWatcher>();
            connectivity.Regained += (_, _) => engine.ConnectivityRegained();
            connectivity.Start();
            engine.Start();
        }
#pragma warning disable CA1031 // Logging never depends on sync (ADR 0002): a failure here leaves the app as it was.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }
```

Guard it with a `_syncStarted` flag so the setup path cannot start it twice.

`Coo.Mobile.csproj`: `<ApplicationDisplayVersion>0.11.0</ApplicationDisplayVersion>` and `<ApplicationVersion>14</ApplicationVersion>`.

- [ ] **Step 6: The Account section and Needs attention**

`SettingsPage.AccountViews()`, signed in: after the status line,

```csharp
        if (_account.LineActionText.Length > 0)
            yield return Row(Ui.Text(_account.LineActionText, "Body"), () =>
            {
                _account.LineActionCommand.Execute(null);
                return Task.CompletedTask;
            });
```

and in `OnAppearing` / `OnDisappearing` subscribe / unsubscribe:

```csharp
        _account.ConsentRequested += OnConsentRequested;
        _account.BringOverRequested += OnBringOverRequested;

    private void OnConsentRequested(object? sender, EventArgs e) =>
        Dispatcher.Dispatch(async () => await RunTappedAsync(() => _services.GetRequiredService<Sync.SyncPrompter>().ShowConsentAsync()));

    private void OnBringOverRequested(object? sender, EventArgs e) =>
        Dispatcher.Dispatch(async () => await RunTappedAsync(() => _services.GetRequiredService<Sync.SyncPrompter>().ShowBringOverAsync()));
```

`AttentionPage`: re-render on `Rejected` changes too (`if (e.PropertyName is nameof(AttentionViewModel.Duplicates) or
nameof(AttentionViewModel.Rejected))`), subscribe in the constructor

```csharp
        model.EditRequested += (_, id) => Dispatcher.Dispatch(async () =>
        {
            try
            {
                var page = services.GetRequiredService<EditPage>();
                await page.OpenAsync(id);
                await Navigation.PushAsync(page);
            }
#pragma warning disable CA1031 // async void: nothing may escape.
            catch (Exception)
#pragma warning restore CA1031
            {
            }
        });
```

(the constructor gains `IServiceProvider services`), and in `Render()`, after the newer-version group:

```csharp
        if (_model.Rejected.Count > 0)
        {
            stack.Add(Ui.Text(Strings.Get("Attention.Rejected"), "Section"));
            foreach (var item in _model.Rejected)
            {
                stack.Add(new Card(new VerticalStackLayout
                {
                    Spacing = 8,
                    Children =
                    {
                        Ui.Text(item.Text, "BodyStrong"),
                        Ui.Text(item.Reason, "Meta"),
                        new HorizontalStackLayout
                        {
                            Spacing = 8,
                            Children =
                            {
                                Small(Strings.Get("Edit.Title"), _model.EditRejectedCommand, parameter: item.Id),
                                Small(Strings.Get("Edit.Delete"), _model.DeleteEntryCommand, parameter: item.Id),
                            },
                        },
                    },
                }));
            }

            if (_model.HasMoreRejected)
                stack.Add(Small(Strings.Get("Attention.ShowMore"), _model.ShowMoreRejectedCommand));
        }
```

- [ ] **Step 7: Build**

```sh
export DOTNET_ROOT=$HOME/dotnet-maui PATH=$HOME/dotnet-maui:$PATH JAVA_HOME=$HOME/Library/Java/JavaVirtualMachines/jdk-17.0.20+8/Contents/Home
dotnet restore src/Mobile/Coo.Mobile/Coo.Mobile.csproj
dotnet build src/Mobile/Coo.Mobile/Coo.Mobile.csproj -c Debug -f net10.0-android --no-restore \
  -p:UseDefaultPublishRuntimeIdentifier=false -p:EmbedAssembliesIntoApk=true \
  -p:AndroidSdkDirectory=$HOME/android-sdk -p:JavaSdkDirectory=$JAVA_HOME
dotnet test Coo.Tests.slnf
```

Expected: 0 warnings, 0 errors; all tests PASS (the architecture tests: Platform still references no MAUI, Presentation
none).

- [ ] **Step 8: Smoke-run on the Pixel** (the agent drives it with adb)

```sh
~/android-sdk/platform-tools/adb -s 5B130DLCR00011 install -r src/Mobile/Coo.Mobile/bin/Debug/net10.0-android/com.lechdigital.coo-Signed.apk
~/android-sdk/platform-tools/adb -s 5B130DLCR00011 logcat -c
~/android-sdk/platform-tools/adb -s 5B130DLCR00011 shell monkey -p com.lechdigital.coo -c android.intent.category.LAUNCHER 1
~/android-sdk/platform-tools/adb -s 5B130DLCR00011 logcat -d | grep -iE "FATAL|AndroidRuntime|Unhandled" || echo "no crash"
```

Expected: the app opens on Today; `no crash`. If the Pixel is still signed in from Increment 10 (no consent recorded
yet), the consent sheet opens over Today by itself (Review Focus 1); if it is signed out, sign in from Settings (pick
**adzius.lech@gmail.com**) and the sheet opens right after. Press Back ("Not now"): Settings → Account shows "Sharing is
off. Your log is only on this phone." and **Turn on sharing**. Nothing was uploaded (Task 20's debug counts show the
outbox unchanged). Leave consent for Task 23.

- [ ] **Step 9: Commit**

```bash
dotnet format Coo.Tests.slnf
git add src/Mobile
git commit -m "Phone: connectivity and lifecycle triggers, the consent and bring-over pages, the sync line's action, the rejected group; 0.11.0 (14) (Spec 011 §2–§6)"
```

---

### Task 20: The debug screen: sync counts, the last cycle, Sync now, Fresh bootstrap (Spec 011 §11)

**Files:**
- Modify: `src/Mobile/Coo.Mobile/DebugScreen/DebugPage.cs`

**Interfaces:**
- Consumes: `SyncEngine.Status`, `StatusChanged`, `CountsAsync`, `SyncNowAsync`, `FreshBootstrapAsync` (Task 12).
- Produces: a "Sync (Spec 011 §11)" section on the debug screen.

- [ ] **Step 1: The section**

`DebugPage`'s constructor gains `SyncEngine sync` (field `_sync`) and a label `_syncLine`. Add, after the account rows:

```csharp
                    new Label { Text = "Sync (Spec 011 §11)", FontSize = 17, Margin = new Thickness(0, 12, 0, 0) },
                    _syncLine,
                    Row(Button("Sync now", async () =>
                        {
                            await _sync.SyncNowAsync(CancellationToken.None);
                            await ShowSyncAsync();
                        }),
                        Button("Fresh bootstrap", async () =>
                        {
                            // Only with an empty outbox (Spec 011 §11): clears this household here and pulls it from 0.
                            _status.Text = await _sync.FreshBootstrapAsync(CancellationToken.None)
                                ? "Fresh bootstrap: done"
                                : "Fresh bootstrap refused: the outbox is not empty (or not signed in)";
                            await ShowSyncAsync();
                            await RefreshAsync();
                        })),
```

and:

```csharp
    private async Task ShowSyncAsync()
    {
        var counts = await _sync.CountsAsync(CancellationToken.None);
        var s = _sync.Status;
        _syncLine.Text = counts is null
            ? "signed out"
            : $"cursor {counts.Cursor} · outbox {counts.Outbox} · rejected {counts.Rejections}\n"
              + $"last: {s.Last?.ToString() ?? "none"}{(s.LastError is null ? "" : $" ({s.LastError})")}"
              + $"{(s.LastSyncedAt is { } at ? $" · synced {at:HH:mm:ss}" : "")}{(s.IsRunning ? " · running" : "")}";
    }

    private void OnSyncStatus(object? sender, EventArgs e) => Dispatcher.Dispatch(async () =>
    {
        try
        {
            await ShowSyncAsync();
        }
        catch (Exception ex)
        {
            _syncLine.Text = ex.GetType().Name;
        }
    });
```

Subscribe `_sync.StatusChanged += OnSyncStatus;` in `OnAppearing` (and call `await ShowSyncAsync();` there), and
unsubscribe in an `OnDisappearing` override.

- [ ] **Step 2: Build, install, look**

Build and install the Debug APK as in Task 19 Step 7–8; open the debug screen (three single taps on "coo"):
the Sync section shows `cursor 0 · outbox <n> · rejected 0` and `last: ConsentDeclined` (Back was pressed in Task 19).

- [ ] **Step 3: Commit**

```bash
git add src/Mobile/Coo.Mobile/DebugScreen/DebugPage.cs
git commit -m "Debug screen: sync counts, the last cycle, Sync now and Fresh bootstrap (Spec 011 §11)"
```

---

### Task 21: The emulator as the second phone

**Files:**
- Modify: `BUILD.md` (a section "The second phone: an emulator with Google Play")

**Interfaces:**
- Consumes: the Android SDK at `~/android-sdk` (cmdline-tools present; no emulator or system image yet).
- Produces: an AVD `coo-second` (Pixel 9, Android 16 / API 36, Google Play, arm64), signed in to
  **adzius.lech@gmail.com**, with the 0.11.0 Debug APK installed.

Confirmed offline on 2026-10-02 with `sdkmanager --list`: `emulator` 37.2.12 and
`system-images;android-36;google_apis_playstore;arm64-v8a` exist; `avdmanager list device` has `pixel_9`.

- [ ] **Step 1: Install the emulator and the image**

```sh
export JAVA_HOME=$HOME/Library/Java/JavaVirtualMachines/jdk-17.0.20+8/Contents/Home
SDK=$HOME/android-sdk
yes | $SDK/cmdline-tools/latest/bin/sdkmanager --sdk_root=$SDK --licenses >/dev/null
$SDK/cmdline-tools/latest/bin/sdkmanager --sdk_root=$SDK "emulator" "system-images;android-36;google_apis_playstore;arm64-v8a"
```

Expected: both install (about 2 GB; minutes).

- [ ] **Step 2: Create the AVD**

```sh
echo no | $SDK/cmdline-tools/latest/bin/avdmanager create avd -n coo-second \
  -k "system-images;android-36;google_apis_playstore;arm64-v8a" -d pixel_9
$SDK/cmdline-tools/latest/bin/avdmanager list avd | grep -A2 coo-second
```

Expected: `Name: coo-second`, `Path: …/.android/avd/coo-second.avd`.

- [ ] **Step 3: Boot it**

```sh
$SDK/emulator/emulator -avd coo-second -netdelay none -netspeed full -no-snapshot-save >/tmp/coo-emulator.log 2>&1 &
$SDK/platform-tools/adb wait-for-device
until [ "$($SDK/platform-tools/adb -e shell getprop sys.boot_completed | tr -d '\r')" = "1" ]; do sleep 2; done
$SDK/platform-tools/adb devices
```

(Run the boot loop as a background command and watch it with the Monitor tool; the `sleep` inside the loop is a shell
poll, not test synchronisation.) Expected: two devices, `5B130DLCR00011` and `emulator-5554`.

- [ ] **Step 4: Sign the Google account in on the emulator (the owner, once)**

Google's sign-in needs the password and 2-step verification, which the agent must not handle. Ask the owner:

> In the emulator window: Settings → Passwords, passkeys & accounts → Add account → Google, and sign in as
> **adzius.lech@gmail.com**. Then open the Play Store once and accept its terms.

Then verify (agent): `$SDK/platform-tools/adb -e shell dumpsys account | grep -c "com.google"` → `1` or more.

- [ ] **Step 5: Install Coo and give the emulator its own entries**

```sh
$SDK/platform-tools/adb -e install -r src/Mobile/Coo.Mobile/bin/Debug/net10.0-android/com.lechdigital.coo-Signed.apk
$SDK/platform-tools/adb -e shell monkey -p com.lechdigital.coo -c android.intent.category.LAUNCHER 1
```

On the emulator (agent with `adb -e shell input tap …` and `uiautomator dump`, or the owner): complete setup with a baby
named **Maja** (the same name as on the Pixel) and log two diapers. Do **not** sign in yet (deliverable 5 needs the
emulator join-pending with its own entries).

- [ ] **Step 6: `BUILD.md`**

Add a section "The second phone: an emulator with Google Play" with Steps 1–5's commands, the note that the owner signs
the Google account in once (and that the emulator's Credential Manager sheet then offers the account like the Pixel's),
that the same debug-signed APK works because the debug key's SHA-1 is registered with the Android OAuth client, and
`adb -e` / `adb -d` to address the emulator / the Pixel.

- [ ] **Step 7: Commit**

```bash
git add BUILD.md
git commit -m "BUILD.md: the Android emulator with Google Play as the second phone (Spec 011 §11)"
```

---
### Task 22: The privacy policy and the deletion page, in the present tense (other repository)

**Files (repository `/Users/andrzej.lech/Code/private/lech-digital`, GitHub Pages from `main`; pushing deploys):**
- Modify: `projects/coo/privacy/index.html`, `projects/coo/delete-account/index.html`, `sitemap.xml`

**Interfaces:**
- Consumes: what 0.11.0 does (Tasks 1–20) and the consent wording (`care-data-v1`).
- Produces: a policy that is true for 0.11.0 before 0.11.0 reaches anyone but the owner (spec §8.3).

Content rule: only true facts about 0.11.0. In 0.11.0 sharing means the phones signed in to the same account; there
are no invitations yet (11b), so the page must not mention inviting anyone as something that exists. Read the site's
`CLAUDE.md` first; no build step, hand-written HTML, keep the existing markup and classes.

- [ ] **Step 1: Edit the privacy policy** (`projects/coo/privacy/index.html`)

| Where | New text (keep the HTML entities and tags around it as they are) |
|---|---|
| `<meta name="description">` | "Coo keeps your baby's log on your phone. No ads, no analytics, no trackers. What is stored when you sign in and choose to share, and how to delete your account." |
| The lede | "…so this policy says exactly where it is kept, and what changes when you sign in and choose to share it with your family." |
| "Last updated" | the day of this edit |
| In short, bullet 1 | "Coo works **without an account**, and until you choose to share, everything you enter **stays on your phone**. We cannot see it." |
| In short, bullet 4 | "Signing in is **optional**. If you sign in and **agree to share**, your family's log is stored on our server in Germany, and only then." |
| "Data stored on your phone", first line | "Unless you choose to share, everything Coo holds is stored **on your phone only**:" (the list stays) |
| Its last paragraph | "Without sharing, it stays on the phone, and we cannot see it. Android backup is disabled for Coo, so none of it is copied into your Google backup." |
| Section label "If you sign in (coming soon)" | "If you sign in" |
| Its first paragraph | "Signing in is optional. You need it only to share your log between phones. Sign-in is with your Google account, through Android's own account sheet." (the identifier paragraph stays) |
| Section label "When sharing arrives" | "When you share" |
| Its first paragraph | "Before anything is uploaded, Coo asks for your permission. When you agree to share, your baby's care log (feeds, diapers, sleep and pumping, with their times, amounts and notes), your baby's profile and your household settings (units, time zone and the kinds of entry you have hidden) are stored on our server, so that the phones signed in to your household can share them. Everyone in your household can see the log and add to it. Your theme, quiet hours and language stay on each phone." |
| New paragraph after it | "We keep a record of your permission: when you gave it, which wording you agreed to, and the app version and language you used. For 30 days the server also keeps a technical record of each change it received (an identifier, and whether it was accepted), so that a change sent twice is applied only once." |
| (The Hetzner paragraph stays.) | |
| Legal basis, second paragraph | "…We store it on our server only with your **explicit consent**, which you give in the app before anything is uploaded (Article 9(2)(a)). If you choose "Not now", nothing is uploaded and your log stays on your phone. You can withdraw your consent at any time by deleting your account." |
| Data retention, add | "Technical records of individual changes are deleted after 30 days." |
| Deleting your account, second paragraph | "Deleting your account removes your account, your sign-ins and sessions, the record of your consent, and your household membership. If you are the household's last parent, the household and its whole shared log are deleted too." |
| Changes to this policy | "…before adding any feature that uses data differently — for example inviting other family members, Premium or AI features — …" |
| Summary | "No account needed. Your baby's log stays on your phone unless you choose to share, and Android backup is off. No ads, no analytics, no trackers. If you sign in and agree to share, your household's log is kept on our server in Germany until you delete your account." |

Check that nothing says "coming soon", "will", "when sharing arrives" or "people you invite":

```sh
cd /Users/andrzej.lech/Code/private/lech-digital
grep -n -iE "coming soon|when sharing arrives|will be optional|people you invite|already describes" projects/coo/privacy/index.html || echo "clean"
```

Expected: `clean`.

- [ ] **Step 2: The deletion page** (`projects/coo/delete-account/index.html`)

- "In the app": "Open **Settings → Account → Delete account**. This option appears once you have signed in."
- "What is deleted": add `<li>the record of your consent to sharing;</li>` before the membership item.

- [ ] **Step 3: `sitemap.xml`**: the `lastmod` of the privacy and delete-account URLs becomes today's date.

- [ ] **Step 4: Look at it locally**

```sh
cd /Users/andrzej.lech/Code/private/lech-digital
python3 -m http.server 8822 >/dev/null 2>&1 &
curl -s http://localhost:8822/projects/coo/privacy/ | grep -c "When you share"
curl -s http://localhost:8822/projects/coo/delete-account/ | grep -c "consent"
kill %1
```

Expected: `1` and `1` or more. Open the page in a browser if one is available and read it top to bottom once.

- [ ] **Step 5: Commit and push (deploys)**

```bash
cd /Users/andrzej.lech/Code/private/lech-digital
git config user.name   # must print adziusmaster; else: git config user.name adziusmaster && git config user.email adzius.lech@gmail.com
gh api user --jq .login   # must print adziusmaster
git add projects/coo/privacy/index.html projects/coo/delete-account/index.html sitemap.xml
git commit -m "Coo: the privacy policy in the present tense for sharing (0.11.0): consent before upload, what is stored, 30-day change records"
git push origin main
```

(No `Co-Authored-By` trailer.) After about a minute:

```sh
curl -s https://lechdigital.nl/projects/coo/privacy/ | grep -c "When you share"
curl -s https://lechdigital.nl/projects/coo/privacy/ | grep -ciE "coming soon|when sharing arrives"
```

Expected: `1`, then `0`.

---

### Task 23: On the two phones, the record, push and CI

**Files:**
- Modify: `docs/specs/011-sync/spec.md` (new §13 "Built and verified"), `docs/adrs/0002-offline-first-sync-protocol.md`
  (an "Amended by" line for ruling 20), `docs/ROADMAP.md` (row 11: Done), `docs/HANDOVER-2026-09-30.md` (Resume here),
  `BUILD.md` (lessons, if any)

**Interfaces:**
- Consumes: everything above; production at 0.11.0 (Task 8); the policy live (Task 22); the emulator (Task 21).

- [ ] **Step 1: Redeploy if the server changed after Task 8**

```sh
DEPLOYED=$(curl -s https://api.coo.lechdigital.nl/health | sed -E 's/.*\+([0-9a-f]+).*/\1/')
git diff --stat "$DEPLOYED" HEAD -- src/Backend src/Shared Dockerfile deploy
```

If that lists files, run `./deploy/deploy-prod.sh --confirm` again (clean tree, Docker running) and expect
`Healthy: …0.11.0+<new commit>…`.

- [ ] **Step 2: The trimmed Release build with the debug screen, on both phones**

```sh
dotnet build src/Mobile/Coo.Mobile/Coo.Mobile.csproj -c Release -f net10.0-android --no-restore \
  -p:UseDefaultPublishRuntimeIdentifier=false -p:AndroidPackageFormat=apk -p:CooDebugScreen=true \
  -p:AndroidSdkDirectory=$HOME/android-sdk -p:JavaSdkDirectory=$JAVA_HOME
APK=src/Mobile/Coo.Mobile/bin/Release/net10.0-android/com.lechdigital.coo-Signed.apk
~/android-sdk/platform-tools/adb -s 5B130DLCR00011 install -r $APK
~/android-sdk/platform-tools/adb -s emulator-5554 install -r $APK
```

Expected: no trim (IL) warnings (the new `SyncJsonContext` is source-generated); both install over the Debug build
and keep their data.

- [ ] **Step 3: Walk the deliverables** (spec §11), in this order. The agent drives both phones with adb
  (`uiautomator dump` to find controls, `input tap`, `screencap` to look); the owner's hands are needed only where noted.
  Record each result (pass, or the finding).

| # | Who | How |
|---|---|---|
| 1 | Agent | Pixel: Settings → **Turn on sharing** → the sheet; **Not now** → debug screen: outbox unchanged, last `ConsentDeclined`. Again **Turn on sharing** → **Agree and share** → within seconds the debug screen shows outbox 0 and a cursor > 0; read-only check: `ssh coldstart-prod "docker exec coo-postgres psql -U coo -d coo -tAc 'SELECT (SELECT count(*) FROM care_entries), (SELECT count(*) FROM consents)'"` → entries > 0, consents 1 |
| 5 | Agent; owner if Google's sheet needs a tap the agent cannot make | Emulator (two diapers, baby "Maja", signed out): Settings → Sign in with Google → **adzius.lech@gmail.com**. No consent sheet (the account has one, Spec 011 §3); the bring-over sheet: "This phone has 2 entries for Maja. Your family's log has: Maja." → **Bring my entries over** → "Maja on this phone is" **Maja** → **Bring them over** → the emulator shows the family's log with its two diapers in it; within 30 s they are on the Pixel |
| 2 | Agent | Log a diaper on the Pixel: on the emulator within 30 s; log a bottle on the emulator: on the Pixel within 30 s |
| 3 | Agent (owner toggles airplane mode on the Pixel if `adb shell cmd connectivity airplane-mode enable` is refused) | Both offline (emulator: `adb -e shell cmd connectivity airplane-mode enable`): edit one feed on each (the Pixel second, so later), delete a diaper on the emulator. Both online: the Pixel's edit on both, the diaper gone on both |
| 4 | Agent | A diaper on each phone a minute apart: after the next pull, "Probably the same" in Needs attention on both; **Keep both** on one: gone from both after the next pull |
| 7 | Agent | Pixel offline, log twice: Settings shows "Waiting for a connection. 2 changes are safe on this phone." Online again: "Up to date · hh:mm" within seconds (connectivity regained) |
| 6 | Agent | Emulator debug screen → **Fresh bootstrap** (outbox empty): the log returns with every entry, and without the deleted diaper of #3; the counts match the Pixel's |
| 8 | Owner waits; agent logs on the emulator every few minutes | 20 minutes: still signed in on both; debug "Account (/api/me)" answers on both (tokens refreshed with the proposed next token, no sign-out) |
| 9 | Agent | `curl -s https://api.coo.lechdigital.nl/health` → `0.11.0+…`; Task 22's live check passed |
| 10 | Agent | Step 6 below |

Every finding becomes a fix with a test where one is possible, in its own commit ("Fix: … (owner, Pixel 10 / emulator)").

- [ ] **Step 4: The record**

- Spec 011: a new **§13 "Built and verified"**, headed with the date and "Pixel 10 and an Android 16 emulator": the
  deliverables with results; this plan's "Rulings on what the spec leaves open" (all 20); the findings and fixes.
- ADR 0002: under the status lines, "Amended by: Spec 011 ruling 20 (a phone that already holds records pulls from
  cursor 0 with `bootstrapHead=0`; §6's skip applies only to an empty phone)".
- `docs/ROADMAP.md` row 11 → "Done (0.11.0)".
- `docs/HANDOVER-2026-09-30.md` "Resume here": Increment 11 done, 0.11.0 deployed, the policy updated; next is 11b
  (members and invitations: the removal sheet and hiding a soft-locked household are waiting for it, ruling 13);
  the emulator `coo-second` is the second phone (`BUILD.md`).
- `BUILD.md` "MAUI lessons": anything the phones taught (for example how the modal consent page behaved over Shell).

- [ ] **Step 5: The full check**

```sh
dotnet test Coo.Tests.slnf
TZ=UTC dotnet test Coo.Tests.slnf
dotnet format Coo.Tests.slnf --verify-no-changes
```

Expected: all PASS twice; no format changes.

- [ ] **Step 6: Commit, push, and watch CI**

```bash
git add docs BUILD.md
git commit -m "Increment 11: sync, verified on the Pixel 10 and an emulator; deployed as 0.11.0 (Spec 011)"
gh api user --jq .login            # must print adziusmaster (the repo pushes over HTTPS through gh)
git push
gh run watch
```

Expected: `adziusmaster`; the push succeeds; CI "Build, format and test" green, including the API image build.

---

## Self-review (done while writing; kept for the reviewer)

**Spec coverage.** §1 → Tasks 7, 23. §2 sync line → 16, 19; offline unchanged (no change needed: writes stay local);
conflicts silent → 1, 5; duplicates after pull → 18/19 (`DataChanged` → pages refresh → `GetAttention` runs the
detector); deletions/undo → 5, 7; rejected group → 13, 18, 19. §3 consent → 2, 5, 6 (server), 11, 14, 15, 16, 17, 19
(phone), 22 (policy). §4 bring-over and `created` → 11 (`created` in the cycle), 14 (transactions), 17, 19 (UI).
§5 four types, unknown kinds, `this-device` → 1, 5, 13, 14. §6.1 push → 11, 13; §6.2 pull → 5, 11, 13; §6.3 cycle and
triggers → 11, 12, 19; §6.4 validation → 1, 2; §6.5 membership-revoked → 2, 5, 6, 11, 12, 15. §7.1 endpoints → 6;
§7.2 data → 4, 5. §8.1 → 9 (+11 cycle generation); §8.2 → 3, 9; §8.3 copy → 16, 22. §9 placement → as listed (one
deviation: the connectivity *port* is in `Coo.Mobile.Application`, its adapter in `Platform`; lifecycle events come from
the host's window events, because Platform has no MAUI reference). §10 tests → each row has its tasks: Api.Application
(1, 2, 3), Api.Infrastructure (4, 5), Api integration (6, 7), Mobile.Application (9, 11, 12, 14), Mobile.Infrastructure
(9, 10, 13, 14, 15), Presentation and Localization (16, 17, 18). §11 deliverables → 20, 21, 23. §12 Q1–Q8 → rulings and
tasks above.

**Placeholder scan.** No "TBD"/"TODO". Two places name an adaptation to existing helpers rather than code (the
`AuthApiTests` fixture field name; `Ui.Text`'s colour parameter); both say exactly what to check.

**Type consistency.** Checked names across tasks: `SyncEntityTypes`, `CareDataConsent.CurrentWording`,
`ChangeResult.Applied/Superseded/Rejected(reason)`, `PushOutcome`, `OutgoingChange.LastOutboxSeq`, `SyncCursor`,
`SyncCounts(Cursor, Outbox, Rejections)`, `CycleReport.Household`, `SyncStatus.Removal`, `ISyncControl.RequestSync`,
`Account.TryCompleteJoinAsync`, `SyncStore.InHousehold` / `RewriteThisDeviceAsync`, `BringOverChoice.Family`.
