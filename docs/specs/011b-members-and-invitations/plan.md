# Members and invitations (Increment 11b) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or
> superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A second person joins a family with their own Google account as a Parent (by link or code, consent first),
brings their phone's entries over or keeps them separate, several families live on one phone with a simple switcher,
and members can be listed, removed and can leave; the removed phone gets its removal sheet; the API is deployed as
0.12.0 and the app is 0.12.0 (versionCode 15).

**Architecture:** `Coo.Domain/Households` gains `InvitationCode`, `Invitation`, `HouseholdLeaving` and
`Membership.CanWithdraw`. On the server, `Coo.Api.Application/Households` holds the use cases behind two ports:
`IInvitationStore` (reads, purge) and `IHouseholdMembershipStore`, whose `RunLockedAsync` opens one transaction, locks
the families (`SELECT … FOR UPDATE`, ascending ID) and hands the use case an `IMembershipTransaction`, so every decision
(accept, remove, leave, account deletion) is made from reads inside the locks, in Application code.
`Coo.Api.Infrastructure/Persistence` implements them over Npgsql (one EF migration, `MembersAndInvitations`);
`Coo.Api/Hosting` maps the endpoints, the chained rate limits, `assetlinks.json` and the `/i/{code}` page. On the phone,
`Coo.Mobile.Application/Households` adds `Families` (the account's families, labels, switching, removal from the
phone), `JoinFamily` (the pending invitation, preview, accept, the choice), `BringFamilyEntriesOver` (move or copy) and
the member use cases behind `IHouseholdApi` and `IFamilyStore`; `SyncCycle` runs the home family and then every other
account family; `Coo.Mobile.Infrastructure` adds migration 0005 (`family_memberships`), the copy transaction and
`HouseholdClient`; Presentation adds the Family section, the Family page, the join, enter-a-code, bring-over and
removal view models; the host declares the App Link and shows the pages.

**Tech Stack:** .NET 10, ASP.NET Core 10 minimal APIs and `Microsoft.AspNetCore.RateLimiting`, EF Core 10 + Npgsql
(migrations; raw `NpgsqlCommand` for the stores), MAUI 10.0.0 (Android), Microsoft.Data.Sqlite, CommunityToolkit.Mvvm,
System.Text.Json source generation, xUnit 2.9 + NSubstitute + FluentAssertions 7,
`Microsoft.Extensions.TimeProvider.Testing` (`FakeTimeProvider`), Testcontainers PostgreSQL 17,
`WebApplicationFactory`.

**Spec:** `docs/specs/011b-members-and-invitations/spec.md` (accepted 2026-10-06; §15.3 answered; §14 recast for the
emulator as the Owner's family and the Pixel as the second account). Also read ADR 0003 §6–§8 (amended by this spec),
ADR 0002 §10 (`membership-revoked`), ADR 0006 (localization), Spec 010 §3.2 (sign-in outcomes), Spec 011 §4, §6, §13 and
its plan (`docs/specs/011-sync/plan.md`) for the code this builds on.

## Global Constraints

- Work on `main` (owner's rule: only developer, unreleased). One commit per task; push at the end (Task 27); CI runs on
  push to `main`. Tasks run in their numbers' order: the phone's storage and HTTP adapters (Tasks 11–13) come before the
  cycle and use cases that need them (Tasks 14–17), so the host's composition test stays green after every task.
- Commits authored as `adziusmaster <adzius.lech@gmail.com>` (repo-local config, already set). **No `Co-Authored-By`
  trailer** and no "Generated with" line. The website repository (Task 26) commits as the same author.
- Zero warnings (`TreatWarningsAsErrors`); `dotnet format Coo.Tests.slnf --verify-no-changes` passes; the architecture
  tests stay green (no new project; the project graph does not change).
- No `TimeProvider.System`, `DateTime.Now/UtcNow`, `DateTimeOffset.Now/UtcNow`, `Guid.NewGuid()`, parameterless
  `Guid.CreateVersion7()` or `System.Random` outside the two hosts (`BannedSymbols.txt`). The invitation code's
  randomness is `RandomNumberGenerator.GetInt32`, passed into the Domain as `Func<int, int>`. Times come from the
  injected `TimeProvider`; new IDs are `Guid.CreateVersion7(time.GetUtcNow())`.
- No MAUI types in `Coo.Mobile.Application`, `Coo.Mobile.Presentation` or `Coo.Mobile.Platform`. No `System.Text.Json`
  in `Coo.Mobile.Application`, `Coo.Api.Application` or `Coo.Mobile.Presentation`. No EF Core or Npgsql outside
  `Coo.Api.Infrastructure`; no SQLite outside `Coo.Mobile.Infrastructure.Persistence`. Infrastructure types are
  `internal` except `ServiceCollectionExtensions`. The Domain gains no dependency (CS-HEX-01).
- **The app says "family", never "household"**, in every language (spec §2, R10). Every user-facing string goes in
  `eng/strings/strings.py` in all seven languages (en, de, fr, es, it, pl, nl), then `python3 eng/strings/generate.py`.
  English copy is the spec's, verbatim, except where a ruling below says otherwise. Every error code the phone can
  receive has an `Error.<code>` string (ADR 0006 §3). Copy must be true for the real release (owner's rule).
- Tests: `MethodName_StateUnderTest_ExpectedBehavior`, strict `// Arrange` / `// Act` / `// Assert`, mock only outbound
  ports (NSubstitute or hand-written in-memory fakes), build Domain objects for real. Tests that set a language join
  `[Collection(nameof(StringsState))]` and reset with `Strings.Use(LanguagePreference.System, null)` in `Dispose`.
  **No sleeps as synchronisation:** time moves with `FakeTimeProvider.Advance`; concurrency is held with
  `TaskCompletionSource` or PostgreSQL's own lock-wait state; background work is awaited through
  `SyncScheduler.WhenIdleAsync()`.
- **TDD with a captured RED:** every task's "verify it fails" step is run, and the first failing line per test is pasted
  into the task's review notes before the implementation is written.
- `CancellationToken` on every IO-bound method, passed down to IO.
- **Fire-and-forget code catches everything** (Increments 10 and 11): MAUI `Clicked`/`Command` handlers, `async void`
  overrides, `Dispatcher.Dispatch(async …)`, `MainThread.BeginInvokeOnMainThread(async …)`, `_ = SomethingAsync()` and
  Android callbacks (`OnNewIntent`) each end in a catch-all (`#pragma warning disable CA1031` with a one-line reason).
  An escaped exception crashes the app.
- **Engine and background events dispatch to the main thread** before touching a page (Increment 11): `SyncEngine`'s
  `StatusChanged`/`DataChanged`, `Account.Changed` and the invitation inbox are raised off the UI thread; every handler in
  the host goes through `Dispatcher.Dispatch` or `MainThread.BeginInvokeOnMainThread`.
- **Phone HTTP:** `JsonException`, `NotSupportedException`, `InvalidOperationException`, `HttpRequestException` and
  `IOException` while reading a body mean "unavailable", never a crash; only the server's explicit codes mean "refused".
- **Single-transaction SQL** for every membership change on the server (spec §10.4) and for the copy and the
  delete-and-replace on the phone; **a unique index backs every uniqueness** the code relies on (`invitations.code_hash`).
- Error responses are codes, never server text (ADR 0006): `{ "code": "…" }`.
- Numbers (spec §3, §10.2): code **8** characters from `ABCDEFGHJKMNPQRSTUVWXYZ23456789`; valid **72 h**; used once;
  at most **5** open invitations per family; **20** creations per user per day; preview and accept share one chained
  limiter, **10 per user per hour** and **30 per client address per hour**, fixed windows; a pending invitation older
  than **72 h** is dropped at start.
- Links: `https://api.coo.lechdigital.nl/i/{code}` (unhyphenated code). App Link: scheme `https`, host
  `api.coo.lechdigital.nl`, path prefix `/i/`, `autoVerify="true"`. Package `com.lechdigital.coo`. The debug key's
  SHA-256 (from `~/Library/Application Support/Xamarin/Mono for Android/debug.keystore`, alias `androiddebugkey`):
  `6E:7E:3C:4E:88:95:35:CE:FE:90:1C:8C:D2:C5:37:D1:E4:2C:FB:91:4D:B7:2D:BB:F5:7A:C8:BD:15:3A:28:7C`.
- NuGet: no new package versions (`System.Threading.RateLimiting` ships with ASP.NET Core). If a package ever has to be
  added, confirm the version with `dotnet package search <id> --exact-match` first.
- App version **0.12.0**, versionCode **15** (`Coo.Mobile.csproj`, 0.11.0 was 14); API version **0.12.0**.
- Run tests with `dotnet test Coo.Tests.slnf` on the global SDK (**Docker Desktop running** for PostgreSQL). Before
  pushing, also `TZ=UTC dotnet test Coo.Tests.slnf`. Build the app with the MAUI SDK (`BUILD.md`).
- **Production:** the owner allows the agent to deploy (`./deploy/deploy-prod.sh --confirm`; its interlocks need a clean,
  committed backend and green tests). Read-only `ssh coldstart-prod` queries are fine; the deploy script itself writes
  the new `.env` line (Task 8); no other SSH writes.
- **Devices** (spec §14): the emulator `coo-second` (`emulator-5554`) is **adzius.lech@gmail.com**, the Owner's family;
  the Pixel 10 (`5B130DLCR00011`) signs in as **andrzej.lech.andrzej@gmail.com**, the joiner. The girlfriend's phone is
  "later, owner": no task touches it. Phone data is test data.

## Review Focus

1. **A code typed or pasted in any shape** — lower case, spaces, a hyphen, the invitation link pasted into "Enter a
   code" — must be accepted when it holds a well-formed code, and nothing else may cause a request. Pinned in Task 1
   (`Parse_LowerCaseWithSpacesAndHyphen_Normalises`) and Task 16 (`ReceiveAsync_PastedLink_StoresTheCode`,
   `ReceiveAsync_Garbage_StoresNothing`).
2. **The app killed between Join and the choice** (or after a lost accept answer): at the next start the flow reopens at
   the choice, and no cycle meanwhile pushes or pulls the never-synced source (a pull would give it a cursor and turn a
   move into a copy). Pinned in Task 14 (`RunAsync_JoiningWithANeverSyncedSource_NeitherPushesNorPullsIt`) and Task 16
   (`StageAsync_JoiningMarkerStored_ReopensAtTheChoice`).
3. **Bring my entries over tapped twice, or retried after a crash mid-copy:** the entries are copied once. Pinned in
   Task 12 (`CopyAsync_SecondTimeForThePair_CopiesNothing`, `CopyAsync_FailurePartWay_ChangesNothing`) and Task 16
   (`BringAsync_AlreadyCopied_ReportsNothingNew`).
4. **Offline during Remove, Withdraw, Leave or Delete, or the answer lost:** nothing changes on the phone, the Family page
   says "That didn't work…", and the retried call answers 200 with the same outcome. Pinned in Task 3
   (`ExecuteAsync_LeaveRetriedAfterLeaving_AnswersLeftAgain`), Task 17 (`ExecuteAsync_LeaveUnanswered_DeletesNothingOnThePhone`)
   and Task 20 (`RemoveCommand_Offline_SaysItDidNotWorkAndChangesNothing`).
5. **A captive portal's 200 HTML page, a proxy's bare 403 or a 502 on a household call:** "unavailable" (connection
   message), never a server refusal like "This invitation can't be used". Pinned in Task 13
   (`PreviewAsync_HtmlPage_IsUnavailable`, `AcceptAsync_Bare403_IsUnavailable`).

## Rulings on what the spec leaves open

Recorded so the reviewer can check them against the spec; Task 27 copies them into the spec's "Built and verified".

1. **The household-mismatch stop is retired.** With the switcher, the shown family is legitimately not the home family
   (another account family, or a local family of no account). A cycle syncs the home family whenever it is an active
   membership and not join-pending, then every other account family; **a shown family that is not an account family is
   never pushed** (it is local, §6.3). That is how "Spec 011's `household-mismatch` stop still guards the home family"
   (§7.2) is kept: nothing but the home family's own records ever goes into it (`NextBatchAsync(home)`).
   `SyncCycle.HouseholdMismatchError` stays as a constant and its sync-line row stays, unreachable.
2. **The source is the joined family itself** (a phone that held that family's log under another account, the Pixel's X
   in §14): nothing is asked; the family is shown.
3. **The source is a family of another account** (synced, not one of the account's families): nothing is asked (never
   offered, Spec 011 Task 14); the joined family is shown and the source stays as it was.
4. **A `created` sign-in during the flow** is treated like `existing`/`joined` (§5.3): the invitation's choice replaces
   the `created` move; the marker is cleared when the flow ends and nothing moves.
5. **Mid-join, a never-synced family is neither pushed nor pulled** (§5.3 says "pushes nothing"); a pull would create its
   cursor, `HasEverSyncedAsync` would turn true, and §6.1's move would become a copy. The joined family is pulled.
6. **Pending counts the families that sync** (the home family and the account families), not local-only ones: a local
   family's outbox can never drain, and "Up to date" would never be said again (§7.2's "every family on the phone").
7. **The home family moves** to the earliest-joined remaining account family when it stops being an active membership
   (`Account.TrySetHomeAsync`). With none (§8.4) the cycle creates the shown family on the server when it has never
   synced (`POST /api/households`), re-keys it once on `household.id-taken`, and on `household.already-has-family`
   refreshes the list (the family stays on the phone only).
8. **Copied records get fresh stamps from one reserved range:** the phone takes one stamp, advances its clock past
   `counter + n` (n = the source's record count) **before** the copy transaction, and stamps the copies with
   consecutive counters. A crash after the commit can never leave the clock behind a stored stamp.
9. **"N entries can't be brought over"** counts live entries that were not copied (quarantined, unknown kind, a baby that
   no longer exists); tombstones are not counted (the person never sees them).
10. **Gender-neutral copy:** "Remove {0} from the family? Their phones stop sharing this family's log, and it is removed
    from them. Entries they added stay." (the spec's example says "Her … she", which Coo cannot know).
11. **Baby names are joined with ", "**, as the existing bring-over and delete sheets do; `TextInfo.ListSeparator` is a
    data separator (";" in German).
12. **`/api/me`'s kept field is `household`** (the wire name of `MeResponse.Household`), not `membership`; it stays, and
    `memberships` is added.
13. **`household.malformed`** (400) answers an unreadable body on `POST /api/households` and `POST
    /api/households/{id}/invitations`; the phone never sends one. An unreadable preview or accept body is
    `invitation.invalid` (no oracle).
14. **An invitation cannot be for the Owner role** (Domain): ownership only comes from creating a family.
15. **Leave is idempotent:** a revoked membership answers `left`; a family that no longer exists answers `deleted`.
    The Owner-with-members refusal is checked before the last-family refusal.
16. **Rate limits** are a chained global limiter partitioned only for endpoints carrying `InvitationCodeLimit` metadata,
    plus a `invitation-create` policy; the numbers come from `Invitations:*` settings with the spec's defaults, so the
    integration tests can lower them.
17. **Account deletion decides under the locks** through the transaction port: the `DeleteAccount` use case runs inside
    `RunLockedAsync`, reads the active members there, and calls `HouseholdSuccession.WhenLeaving` there (spec §10.4's
    guarantee, with the decision in Application rather than Infrastructure). `AccountDeletionPlan` and
    `IAccountStore.DeleteAccountAsync` go.
18. **Sharing a text and asking for consent are Presentation ports** (`IExternalLauncher.ShareTextAsync`,
    `IConsentPrompt`), beside the existing browser and mail launcher, not Application ports.
19. **`family_memberships` is schema migration 0005**, not a data migration.
20. **Cycle outcome precedence:** Failed > ClientTooOld > Removed (any family) > SoftLocked (the shown family, or the home
    family while join-pending) > JoinPending > Synced. SoftLocked and Removed still back off, as in Spec 011.
21. **A push answered without a result for every change, and `sync.malformed`/`sync.batch-too-large`,** are `no-progress`
    (stop until a wake, §9); Spec 011 backed off after an undecided push.
22. **`sync.not-a-member`** stops only that family until a wake (`SyncStops`); the status says whether the shown family is
    stopped (`ShownStopped`), and the line says "Sharing stopped: this family is no longer on Coo's server…".
23. **The removal sheet** shows one removal at a time (the first reported); Back closes it and it opens again at the next
    start or resume.
24. **The Family page's offline list** is the last list fetched in this app run (kept in memory by `LoadMembers`).
25. **Rate-limited preview or accept keeps the pending invitation** (the flow reopens at the next start within 72 h);
    "no answer" keeps it with **Try again**; `invitation.invalid` clears it.
26. **A malformed code typed into "Enter a code"** says "Check the code: it's 8 letters and numbers, like ABCD-EFGH."
    without a request (the spec gives no text).
27. **Sign-out clears the family list and the joining marker** and keeps a pending invitation: the flow reopens with its
    sign-in step.
28. **Invite after signing in invites into the home family** (after a `created` sign-in the shown family is not the
    account's).
29. **The delete confirmation without a baby** reads "Delete this family? Its log, N entries, is deleted from Coo's
    server and this phone. This can't be undone."

---

## File Structure

| File | Responsibility |
|---|---|
| `src/Shared/Coo.Domain/Households/InvitationCode.cs`, `Invitation.cs`, `HouseholdLeaving.cs` (create); `Membership.cs` (modify) | The code, the invitation, the leaving rule, `CanWithdraw` |
| `src/Shared/Coo.Contracts/Households/HouseholdContracts.cs` (create); `Auth/AuthContracts.cs` (modify) | Wire DTOs, codes, the link base; `MeResponse.Memberships` |
| `src/Backend/Coo.Api.Application/Households/Ports.cs`, `InvitationUseCases.cs`, `MemberUseCases.cs` (create) | Ports and use cases |
| `src/Backend/Coo.Api.Application/Auth/AccountUseCases.cs`, `Ports.cs`, `AccountStoreExtensions.cs`; `Sync/UseCases.cs` (modify) | `GetMe` memberships, `DeleteAccount` under locks, the purge |
| `src/Backend/Coo.Api.Infrastructure/Persistence/Records.cs`, `CooDbContext.cs`, `AccountStore.cs`, `ServiceCollectionExtensions.cs` (modify); `HouseholdMembershipStore.cs`, `InvitationStore.cs`, `Migrations/*_MembersAndInvitations.cs` (create) | Schema, stores, active-only reads |
| `src/Backend/Coo.Api/Hosting/HouseholdEndpoints.cs`, `AppLinkEndpoints.cs` (create); `AuthEndpoints.cs`, `StartupChecks.cs`, `Program.cs`, `Coo.Api.csproj` (modify) | Endpoints, limits, App Links, 0.12.0 |
| `deploy/docker-compose.prod.yml`, `deploy/deploy-prod.sh`, `deploy/.env.example`, `DEPLOY.md` (modify) | The fingerprint setting |
| `tests/Coo.Domain.Tests/Households/InvitationTests.cs`, `HouseholdLeavingTests.cs` (create); `MembershipTests.cs` (modify) | Domain tests |
| `tests/Coo.Api.Application.Tests/HouseholdFakes.cs`, `InvitationUseCaseTests.cs`, `MemberUseCaseTests.cs` (create); `AccountTests.cs`, `Fakes.cs` (modify) | Application tests |
| `tests/Coo.Api.Infrastructure.Tests/MembersSchemaTests.cs`, `HouseholdMembershipStoreTests.cs` (create); `AuthStoreTests.cs`, `SyncStoreTests.cs`, `PostgresServer.cs` (modify) | PostgreSQL tests |
| `tests/Coo.Api.IntegrationTests/HouseholdApiTests.cs`, `AppLinkTests.cs`, `InvitationRateLimitTests.cs` (create); `TestApi.cs`, `StartupChecksTests.cs`, `SyncHistoryCleanupJobTests.cs` (modify) | End to end |
| `src/Mobile/Coo.Mobile.Application/Households/FamilyPorts.cs`, `Families.cs`, `JoinFamily.cs`, `BringFamilyEntriesOver.cs`, `MemberUseCases.cs`, `ProvisionalSettings.cs` (create); `Households.cs` (modify) | Families on the phone |
| `src/Mobile/Coo.Mobile.Application/Sync/SyncCycle.cs`, `SyncEngine.cs`, `Model.cs`, `Ports.cs` (modify); `SyncStops.cs` (create); `Auth/Account.cs`, `Auth/Ports.cs`, `ServiceCollectionExtensions.cs` (modify) | The multi-family cycle, the engine, the home family |
| `src/Mobile/Coo.Mobile.Infrastructure/Persistence/Migrations/0005-family-memberships.sql`, `FamilyStore.cs` (create); `HouseholdMoves.cs`, `SyncStore.cs` (modify) | SQLite |
| `src/Mobile/Coo.Mobile.Infrastructure/Households/HouseholdClient.cs`, `HouseholdHttp.cs`, `HouseholdJsonContext.cs` (create); `Auth/AccountClient.cs`, `Sync/SyncHttp.cs`, `ServiceCollectionExtensions.cs` (modify) | HTTP |
| `eng/strings/strings.py` (modify) + generated `.resx` | Copy in seven languages |
| `src/Mobile/Coo.Mobile.Presentation/Settings/FamilyLabels.cs`, `FamilySectionViewModel.cs`, `FamilyPageViewModel.cs`, `FamilyBringOverViewModel.cs`, `JoinViewModel.cs`, `EnterCodeViewModel.cs`, `RemovalViewModel.cs`, `Today/PausedSection.cs` (create); `Settings/Ports.cs`, `Settings/SyncLine.cs`, `Setup/SetupViewModel.cs`, `ServiceCollectionExtensions.cs` (modify) | View models |
| `src/Mobile/Coo.Mobile/Platforms/Android/MainActivity.cs`, `StartupPage.cs`, `MauiProgram.cs`, `Sync/SyncPrompter.cs`, `Settings/MauiExternalLauncher.cs`, `Pages/SettingsPage.cs`, `Pages/SetupPage.cs`, `Pages/SectionViews.cs`, `DebugScreen/DebugPage.cs`, `Coo.Mobile.csproj` (modify); `Households/InvitationInbox.cs`, `Households/MauiConsentPrompt.cs`, `Pages/FamilyPage.cs`, `Pages/JoinPage.cs`, `Pages/EnterCodePage.cs`, `Pages/FamilyBringOverPage.cs`, `Pages/BringOverViews.cs`, `Pages/RemovalPage.cs` (create) | The host |
| `tests/Coo.Mobile.Application.Tests/FamiliesTests.cs`, `MultiFamilyCycleTests.cs`, `JoinFamilyTests.cs`, `BringFamilyEntriesOverTests.cs`, `MemberUseCaseTests.cs` (create); `SyncCycleTests.cs`, `SyncEngineTests.cs` (modify) | Phone application tests |
| `tests/Coo.Mobile.Infrastructure.Tests/FamilyStoreTests.cs`, `CopyEntriesTests.cs`, `HouseholdClientTests.cs` (create); `HouseholdSwitchScenarioTests.cs`, `HouseholdMovesTests.cs`, `migrations-manifest.txt` (modify) | Phone infrastructure tests |
| `tests/Coo.Mobile.Presentation.Tests/FamilyLabelTests.cs`, `FamilySectionTests.cs`, `FamilyPageTests.cs`, `JoinViewModelTests.cs`, `RemovalTests.cs` (create); `SyncLineTests.cs`, `LocalizationTests.cs` (modify) | Presentation tests |
| `docs/**`, `BUILD.md` (modify) | The record |
| `/Users/andrzej.lech/Code/private/lech-digital/projects/coo/privacy/index.html`, `delete-account/index.html`, `sitemap.xml` (modify, other repo) | The policy for 0.12.0 |

---
## The server

### Task 1: The invitation code, the invitation, the leaving rule (`Coo.Domain/Households`)

**Files:**
- Create: `src/Shared/Coo.Domain/Households/InvitationCode.cs`, `src/Shared/Coo.Domain/Households/Invitation.cs`,
  `src/Shared/Coo.Domain/Households/HouseholdLeaving.cs`
- Modify: `src/Shared/Coo.Domain/Households/Membership.cs` (add `CanWithdraw`; drop "Until Increment 11b" from the summary)
- Test: `tests/Coo.Domain.Tests/Households/InvitationTests.cs`, `tests/Coo.Domain.Tests/Households/HouseholdLeavingTests.cs`,
  `tests/Coo.Domain.Tests/Households/MembershipTests.cs`

**Interfaces:**
- Consumes: `HouseholdId`, `UserId`, `Result`, `Result<T>`, `DomainError` (`Coo.Domain.Common`); `Membership`, `Role`.
- Produces:
  - `public sealed record InvitationCode` — `const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789"`, `const int Length = 8`,
    `string Value` (8 capitals, unhyphenated), `string Display` (`"ABCD-EFGH"`), `static Result<InvitationCode> Parse(string? text)`
    (failure code `Invitation.InvalidCode` = `"invitation.invalid"`), `static InvitationCode FromRandom(Func<int, int> nextIndex)`
    (`nextIndex(31)` returns 0..30), `ToString()` = `Display`.
  - `public readonly record struct InvitationId(Guid Value)` (not `Guid.Empty`).
  - `public sealed class Invitation` — `static readonly TimeSpan Lifetime = 72 h`, `const string InvalidCode = "invitation.invalid"`,
    `Id`, `HouseholdId`, `Role`, `CreatedBy`, `CreatedAt`, `ExpiresAt`, `AcceptedBy?`, `AcceptedAt?`, `RevokedAt?`,
    `bool IsOpen`, `bool IsUsable(DateTimeOffset now)`, `bool WasAcceptedBy(UserId user)`, `Result Accept(UserId user, DateTimeOffset now)`,
    `void Withdraw(DateTimeOffset now)`, `static Invitation Create(InvitationId id, HouseholdId household, Role role, UserId createdBy, DateTimeOffset now)`,
    `internal static Invitation Rehydrate(InvitationId, HouseholdId, Role, UserId createdBy, DateTimeOffset createdAt, DateTimeOffset expiresAt, UserId? acceptedBy, DateTimeOffset? acceptedAt, DateTimeOffset? revokedAt)`.
  - `public enum LeaveDecision { MembershipEnds, HouseholdDeleted, RefusedOwnerWithMembers, RefusedLastFamily }` and
    `public static class HouseholdLeaving { static LeaveDecision Decide(IReadOnlyCollection<Membership> members, UserId leaver, bool hasOtherFamily) }`.
  - `Membership.CanWithdraw(Invitation invitation)`.

- [ ] **Step 1: Write the failing tests**

`tests/Coo.Domain.Tests/Households/InvitationTests.cs`:

```csharp
using Coo.Domain.Common;
using Coo.Domain.Households;

namespace Coo.Domain.Tests.Households;

/// <summary>Spec 011b §3 and §10.6: the code (alphabet, normalisation, length) and the invitation (72 h, used once, withdrawn).</summary>
public sealed class InvitationTests
{
    private static readonly InvitationId Id = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000e1"));

    [Fact]
    public void Alphabet_Always_HasThirtyOneUnambiguousSymbols()
    {
        // Act
        var alphabet = InvitationCode.Alphabet;

        // Assert
        alphabet.Should().HaveLength(31);
        alphabet.Distinct().Should().HaveCount(31);
        alphabet.Should().NotContainAny("0", "O", "1", "I", "L");
    }

    [Fact]
    public void Parse_LowerCaseWithSpacesAndHyphen_Normalises()
    {
        // Arrange
        const string typed = " abcd-ef gh ";

        // Act
        var code = InvitationCode.Parse(typed);

        // Assert
        code.Value.Value.Should().Be("ABCDEFGH");
        code.Value.Display.Should().Be("ABCD-EFGH");
        code.Value.ToString().Should().Be("ABCD-EFGH");
    }

    [Theory]
    [InlineData("ABCD-EFG0")]
    [InlineData("ABCD-EFGO")]
    [InlineData("ABCD-EFG1")]
    [InlineData("ABCD-EFGI")]
    [InlineData("ABCD-EFGL")]
    [InlineData("abcd-efgl")]
    public void Parse_AnAmbiguousCharacter_IsRefused(string text)
    {
        // Act
        var code = InvitationCode.Parse(text);

        // Assert
        code.Error!.Code.Should().Be(Invitation.InvalidCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ABCD-EFG")]
    [InlineData("ABCD-EFGHJ")]
    [InlineData("ABCD_EFGH")]
    [InlineData("ÄBCD-EFGH")]
    public void Parse_WrongLengthOrCharacter_IsRefused(string? text)
    {
        // Act
        var code = InvitationCode.Parse(text);

        // Assert
        code.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void FromRandom_IndicesGiven_UsesTheAlphabetInOrder()
    {
        // Arrange: the randomness is passed in (CS-HEX-01: the Domain has no RNG of its own).
        var next = 0;
        int Index(int max)
        {
            max.Should().Be(31);
            return next++;
        }

        // Act
        var code = InvitationCode.FromRandom(Index);

        // Assert
        code.Value.Should().Be("ABCDEFGH");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(31)]
    public void FromRandom_IndexOutOfRange_Throws(int index)
    {
        // Act
        var act = () => InvitationCode.FromRandom(_ => index);

        // Assert
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void IsUsable_AtExactlySeventyTwoHours_IsFalse()
    {
        // Arrange
        var invitation = Invitation.Create(Id, A.Household, Role.Parent, A.Parent, A.Now);

        // Act
        var justBefore = invitation.IsUsable(A.Now.AddHours(72).AddTicks(-1));
        var atExpiry = invitation.IsUsable(A.Now.AddHours(72));

        // Assert
        invitation.ExpiresAt.Should().Be(A.Now.AddHours(72));
        justBefore.Should().BeTrue();
        atExpiry.Should().BeFalse();
    }

    [Fact]
    public void Accept_Twice_IsRefusedTheSecondTime()
    {
        // Arrange
        var invitation = Invitation.Create(Id, A.Household, Role.Parent, A.Parent, A.Now);

        // Act
        var first = invitation.Accept(A.OtherParent, A.Now.AddHours(1));
        var second = invitation.Accept(A.OtherParent, A.Now.AddHours(2));

        // Assert
        first.IsSuccess.Should().BeTrue();
        second.Error!.Code.Should().Be(Invitation.InvalidCode);
        invitation.AcceptedAt.Should().Be(A.Now.AddHours(1));
        invitation.WasAcceptedBy(A.OtherParent).Should().BeTrue();
        invitation.IsOpen.Should().BeFalse();
    }

    [Fact]
    public void Accept_AfterWithdraw_IsRefused()
    {
        // Arrange
        var invitation = Invitation.Create(Id, A.Household, Role.Parent, A.Parent, A.Now);
        invitation.Withdraw(A.Now.AddMinutes(5));

        // Act
        var result = invitation.Accept(A.OtherParent, A.Now.AddMinutes(6));

        // Assert
        result.IsFailure.Should().BeTrue();
        invitation.RevokedAt.Should().Be(A.Now.AddMinutes(5));
    }

    [Fact]
    public void Withdraw_AcceptedInvitation_ChangesNothing()
    {
        // Arrange: an accepted invitation stays as it was, so a retried accept stays idempotent (spec §10.3).
        var invitation = Invitation.Create(Id, A.Household, Role.Parent, A.Parent, A.Now);
        invitation.Accept(A.OtherParent, A.Now.AddHours(1));

        // Act
        invitation.Withdraw(A.Now.AddHours(2));

        // Assert
        invitation.RevokedAt.Should().BeNull();
    }

    [Fact]
    public void Create_ForTheOwnerRole_Throws()
    {
        // Act
        var act = () => Invitation.Create(Id, A.Household, Role.Owner, A.Parent, A.Now);

        // Assert
        act.Should().Throw<ArgumentOutOfRangeException>("ownership comes only from creating a family (ruling 14)");
    }
}
```

`tests/Coo.Domain.Tests/Households/HouseholdLeavingTests.cs`:

```csharp
using Coo.Domain.Common;
using Coo.Domain.Households;

namespace Coo.Domain.Tests.Households;

/// <summary>Spec 011b §8.3: each row of the leaving table.</summary>
public sealed class HouseholdLeavingTests
{
    private static readonly UserId Nanny = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000c3"));

    [Theory]
    [InlineData(Role.Parent)]
    [InlineData(Role.Caregiver)]
    public void Decide_NonOwnerWithAnotherFamily_EndsTheMembership(Role role)
    {
        // Arrange
        Membership[] members =
        [
            Membership.Create(A.Household, A.Parent, Role.Owner, A.Now),
            Membership.Create(A.Household, A.OtherParent, role, A.Now.AddDays(1)),
        ];

        // Act
        var decision = HouseholdLeaving.Decide(members, A.OtherParent, hasOtherFamily: true);

        // Assert
        decision.Should().Be(LeaveDecision.MembershipEnds);
    }

    [Fact]
    public void Decide_OwnerAloneWithAnotherFamily_DeletesTheFamily()
    {
        // Arrange
        Membership[] members = [Membership.Create(A.Household, A.Parent, Role.Owner, A.Now)];

        // Act
        var decision = HouseholdLeaving.Decide(members, A.Parent, hasOtherFamily: true);

        // Assert
        decision.Should().Be(LeaveDecision.HouseholdDeleted);
    }

    [Fact]
    public void Decide_OwnerWithACaregiver_IsRefused()
    {
        // Arrange: ownership transfer is deferred (ADR 0003 §6).
        Membership[] members =
        [
            Membership.Create(A.Household, A.Parent, Role.Owner, A.Now),
            Membership.Create(A.Household, Nanny, Role.Caregiver, A.Now.AddDays(1)),
        ];

        // Act
        var decision = HouseholdLeaving.Decide(members, A.Parent, hasOtherFamily: true);

        // Assert
        decision.Should().Be(LeaveDecision.RefusedOwnerWithMembers);
    }

    [Fact]
    public void Decide_OwnerWithMembersAndNoOtherFamily_IsRefusedAsOwnerFirst()
    {
        // Arrange: ruling 15, the more specific refusal wins.
        Membership[] members =
        [
            Membership.Create(A.Household, A.Parent, Role.Owner, A.Now),
            Membership.Create(A.Household, A.OtherParent, Role.Parent, A.Now.AddDays(1)),
        ];

        // Act
        var decision = HouseholdLeaving.Decide(members, A.Parent, hasOtherFamily: false);

        // Assert
        decision.Should().Be(LeaveDecision.RefusedOwnerWithMembers);
    }

    [Theory]
    [InlineData(Role.Owner)]
    [InlineData(Role.Parent)]
    public void Decide_OnlyFamily_IsRefused(Role role)
    {
        // Arrange
        Membership[] members = role == Role.Owner
            ? [Membership.Create(A.Household, A.Parent, Role.Owner, A.Now)]
            : [Membership.Create(A.Household, A.OtherParent, Role.Owner, A.Now), Membership.Create(A.Household, A.Parent, Role.Parent, A.Now.AddDays(1))];

        // Act
        var decision = HouseholdLeaving.Decide(members, A.Parent, hasOtherFamily: false);

        // Assert
        decision.Should().Be(LeaveDecision.RefusedLastFamily);
    }

    [Fact]
    public void Decide_NotAMember_Throws()
    {
        // Arrange
        Membership[] members = [Membership.Create(A.Household, A.Parent, Role.Owner, A.Now)];

        // Act
        var act = () => HouseholdLeaving.Decide(members, Nanny, hasOtherFamily: true);

        // Assert
        act.Should().Throw<ArgumentException>();
    }
}
```

Append to `tests/Coo.Domain.Tests/Households/MembershipTests.cs` (inside the class; add `private static readonly InvitationId InvitationKey = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000e1"));` at the top):

```csharp
    [Theory]
    [InlineData(Role.Owner, true)]
    [InlineData(Role.Parent, true)]
    [InlineData(Role.Caregiver, false)]
    public void CanWithdraw_EachRoleOfTheSameFamily_FollowsTheParentRight(Role role, bool expected)
    {
        // Arrange: any Owner or Parent may withdraw, not only the inviter (spec §15.2 Q3).
        var invitation = Invitation.Create(InvitationKey, A.Household, Role.Parent, A.OtherParent, A.Now);
        var member = Membership.Create(A.Household, A.Parent, role, A.Now);

        // Act
        var allowed = member.CanWithdraw(invitation);

        // Assert
        allowed.Should().Be(expected);
    }

    [Fact]
    public void CanWithdraw_InvitationOfAnotherFamily_IsRefused()
    {
        // Arrange
        var invitation = Invitation.Create(InvitationKey, OtherHousehold, Role.Parent, A.OtherParent, A.Now);
        var owner = Membership.Create(A.Household, A.Parent, Role.Owner, A.Now);

        // Act
        var allowed = owner.CanWithdraw(invitation);

        // Assert
        allowed.Should().BeFalse();
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Coo.Domain.Tests --filter "FullyQualifiedName~Households"`
Expected: FAIL to compile: `The type or namespace name 'InvitationCode' could not be found` (paste the first line per test
into the review notes).

- [ ] **Step 3: Write the implementation**

`src/Shared/Coo.Domain/Households/InvitationCode.cs`:

```csharp
using Coo.Domain.Common;

namespace Coo.Domain.Households;

/// <summary>
/// An invitation's code (Spec 011b §3, ADR 0003 §6): 8 characters from an alphabet without 0/O and 1/I/L. Entered in any
/// case; whitespace and hyphens are ignored. The Domain never draws randomness itself: <see cref="FromRandom"/> is given it.
/// </summary>
public sealed record InvitationCode
{
    public const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
    public const int Length = 8;

    private InvitationCode(string value) => Value = value;

    /// <summary>The 8 characters, capitals, no hyphen: what the link carries and the server hashes.</summary>
    public string Value { get; }

    /// <summary>"ABCD-EFGH": how people read and type it.</summary>
    public string Display => $"{Value[..4]}-{Value[4..]}";

    public static Result<InvitationCode> Parse(string? text)
    {
        if (text is null)
            return Result<InvitationCode>.Failure(Invitation.InvalidCode);
        var chars = text.Where(c => !char.IsWhiteSpace(c) && c != '-').Select(char.ToUpperInvariant).ToArray();
        return chars.Length == Length && chars.All(c => Alphabet.Contains(c, StringComparison.Ordinal))
            ? new InvitationCode(new string(chars))
            : Result<InvitationCode>.Failure(Invitation.InvalidCode);
    }

    /// <param name="nextIndex">Given the alphabet's size, a uniformly random index below it (the server passes
    /// <c>RandomNumberGenerator.GetInt32</c>).</param>
    public static InvitationCode FromRandom(Func<int, int> nextIndex)
    {
        ArgumentNullException.ThrowIfNull(nextIndex);
        var chars = new char[Length];
        for (var i = 0; i < Length; i++)
        {
            var index = nextIndex(Alphabet.Length);
            if (index < 0 || index >= Alphabet.Length)
                throw new ArgumentOutOfRangeException(nameof(nextIndex), index, "The index must be below the alphabet's size.");
            chars[i] = Alphabet[index];
        }

        return new InvitationCode(new string(chars));
    }

    public override string ToString() => Display;
}
```

`src/Shared/Coo.Domain/Households/Invitation.cs`:

```csharp
using Coo.Domain.Common;

namespace Coo.Domain.Households;

public readonly record struct InvitationId
{
    public InvitationId(Guid value) =>
        Value = value == Guid.Empty ? throw new ArgumentException("An ID cannot be Guid.Empty.", nameof(value)) : value;

    public Guid Value { get; }

    public override string ToString() => Value.ToString();
}

/// <summary>
/// An invitation to join a family (Spec 011b §3, ADR 0003 §6): valid for 72 hours, used once, withdrawable. The code
/// itself is never kept here: the server stores only its hash.
/// </summary>
public sealed class Invitation
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(72);

    /// <summary>One code for unknown, expired, used, withdrawn and gone: no oracle (spec §5.2).</summary>
    public const string InvalidCode = "invitation.invalid";

    private Invitation(
        InvitationId id, HouseholdId householdId, Role role, UserId createdBy, DateTimeOffset createdAt, DateTimeOffset expiresAt,
        UserId? acceptedBy, DateTimeOffset? acceptedAt, DateTimeOffset? revokedAt)
    {
        Id = id;
        HouseholdId = householdId;
        Role = role;
        CreatedBy = createdBy;
        CreatedAt = createdAt;
        ExpiresAt = expiresAt;
        AcceptedBy = acceptedBy;
        AcceptedAt = acceptedAt;
        RevokedAt = revokedAt;
    }

    public InvitationId Id { get; }

    public HouseholdId HouseholdId { get; }

    public Role Role { get; }

    public UserId CreatedBy { get; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset ExpiresAt { get; }

    public UserId? AcceptedBy { get; private set; }

    public DateTimeOffset? AcceptedAt { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    /// <summary>Neither used nor withdrawn (it may still have expired).</summary>
    public bool IsOpen => AcceptedAt is null && RevokedAt is null;

    public static Invitation Create(InvitationId id, HouseholdId household, Role role, UserId createdBy, DateTimeOffset now) =>
        role is Role.Parent or Role.Caregiver
            ? new Invitation(id, household, role, createdBy, now, now + Lifetime, null, null, null)
            : throw new ArgumentOutOfRangeException(nameof(role), role, "An invitation is for a Parent or a Caregiver.");

    internal static Invitation Rehydrate(
        InvitationId id, HouseholdId household, Role role, UserId createdBy, DateTimeOffset createdAt, DateTimeOffset expiresAt,
        UserId? acceptedBy, DateTimeOffset? acceptedAt, DateTimeOffset? revokedAt) =>
        new(id, household, role, createdBy, createdAt, expiresAt, acceptedBy, acceptedAt, revokedAt);

    /// <summary>Open and before its expiry; at exactly 72 hours it is no longer usable.</summary>
    public bool IsUsable(DateTimeOffset now) => IsOpen && now < ExpiresAt;

    public bool WasAcceptedBy(UserId user) => AcceptedAt is not null && AcceptedBy == user;

    public Result Accept(UserId user, DateTimeOffset now)
    {
        if (!IsUsable(now))
            return Result.Failure(InvalidCode);
        AcceptedBy = user;
        AcceptedAt = now;
        return Result.Success;
    }

    /// <summary>Only an open invitation is withdrawn; a used or already withdrawn one stays as it was.</summary>
    public void Withdraw(DateTimeOffset now)
    {
        if (IsOpen)
            RevokedAt = now;
    }
}
```

`src/Shared/Coo.Domain/Households/HouseholdLeaving.cs`:

```csharp
using Coo.Domain.Common;

namespace Coo.Domain.Households;

/// <summary>What leaving a family does (Spec 011b §8.3).</summary>
public enum LeaveDecision
{
    /// <summary>A Parent or Caregiver: their membership ends.</summary>
    MembershipEnds,

    /// <summary>The Owner as the only active member: the family goes with all of its records.</summary>
    HouseholdDeleted,

    /// <summary>The Owner while others remain: that would be an ownership transfer, deferred (ADR 0003 §6).</summary>
    RefusedOwnerWithMembers,

    /// <summary>The account's only family: "stop sharing everything" is account deletion.</summary>
    RefusedLastFamily,
}

public static class HouseholdLeaving
{
    /// <param name="members">The family's active members, the leaver included.</param>
    /// <param name="hasOtherFamily">Whether the leaver is an active member of another family.</param>
    public static LeaveDecision Decide(IReadOnlyCollection<Membership> members, UserId leaver, bool hasOtherFamily)
    {
        ArgumentNullException.ThrowIfNull(members);
        var me = members.SingleOrDefault(m => m.UserId == leaver)
            ?? throw new ArgumentException("The leaving user is not a member of this household.", nameof(leaver));
        if (me.Role == Role.Owner && members.Count > 1)
            return LeaveDecision.RefusedOwnerWithMembers;
        if (!hasOtherFamily)
            return LeaveDecision.RefusedLastFamily;
        return me.Role == Role.Owner ? LeaveDecision.HouseholdDeleted : LeaveDecision.MembershipEnds;
    }
}
```

In `src/Shared/Coo.Domain/Households/Membership.cs`, replace the class summary's last sentence ("Until Increment 11b every
household has only its Owner.") with "Increment 11b adds Parents who join by invitation." and add after `CanRemove`:

```csharp
    /// <summary>An Owner or Parent may withdraw any open invitation of the same household, not only their own (Spec 011b §15.2 Q3).</summary>
    public bool CanWithdraw(Invitation invitation)
    {
        ArgumentNullException.ThrowIfNull(invitation);
        return IsParent && invitation.HouseholdId == HouseholdId;
    }
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Coo.Domain.Tests` and `dotnet test tests/Coo.Architecture.Tests`
Expected: PASS (the Domain still references nothing: `PackageReferences_SharedCoreProject_ReferencesNoPackage`).

- [ ] **Step 5: Commit**

```bash
git add src/Shared/Coo.Domain/Households tests/Coo.Domain.Tests/Households
git commit -m "Domain: the invitation code, the invitation and the leaving rule (Spec 011b §10.6)"
```

---

### Task 2: The contracts, the ports, and inviting, withdrawing, listing and removing (`Coo.Api.Application/Households`)

**Files:**
- Create: `src/Shared/Coo.Contracts/Households/HouseholdContracts.cs`,
  `src/Backend/Coo.Api.Application/Households/Ports.cs`, `src/Backend/Coo.Api.Application/Households/MemberUseCases.cs`,
  `src/Backend/Coo.Api.Application/Households/InvitationUseCases.cs` (this task: `CreateInvitation`, `WithdrawInvitation`)
- Test: `tests/Coo.Api.Application.Tests/HouseholdFakes.cs`, `tests/Coo.Api.Application.Tests/InvitationUseCaseTests.cs`,
  `tests/Coo.Api.Application.Tests/MemberUseCaseTests.cs`

**Interfaces:**
- Consumes: Task 1's Domain types; `MembershipLookup`, `IMembershipStore`, `IConsentStore`, `ConsentRecord`
  (`Coo.Api.Application.Sync`); `AuthSecrets.Hash` (`Coo.Api.Application.Auth`).
- Produces (Contracts, `namespace Coo.Contracts.Households`):
  - `CreateInvitationRequest(string Role)`, `InvitationCreatedResponse(Guid InvitationId, string Code, string Link, DateTimeOffset ExpiresAt)`,
    `MemberDto(Guid UserId, string Name, string Email, string Role, DateTimeOffset JoinedAt, bool IsYou)`,
    `InvitationDto(Guid InvitationId, string Role, Guid CreatedBy, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt)`,
    `MembersResponse(IReadOnlyList<MemberDto> Members, IReadOnlyList<InvitationDto> Invitations)`,
    `LeaveRequest(bool Confirm)`, `LeaveResponse(string Outcome)`, `CreateHouseholdRequest(Guid HouseholdId)`,
    `InvitationCodeRequest(string Code)`,
    `InvitationPreviewResponse(Guid HouseholdId, string InviterName, IReadOnlyList<string> BabyNames, string Role, DateTimeOffset ExpiresAt, bool AlreadyMember)`,
    `AcceptInvitationResponse(Guid HouseholdId, string Role, string Outcome)`;
    `static class HouseholdErrors` (the ten codes, `Malformed = "household.malformed"`, `RateLimited = "auth.rate-limited"`), `static class LeaveOutcomes`
    (`Left = "left"`, `Deleted = "deleted"`), `static class AcceptOutcomes` (`Joined`, `Rejoined`, `AlreadyMember = "already-member"`),
    `static class InvitationLinks` (`const string Base = "https://api.coo.lechdigital.nl/i/"`, `static string For(string code)`).
- Produces (Application, `namespace Coo.Api.Application.Households`): the ports exactly as below; `CreateInvitation`,
  `WithdrawInvitation`, `ListMembers`, `RemoveMember` with the signatures below; results `CreatedInvitation`,
  `MembersView`.

- [ ] **Step 1: The contracts (no behaviour, so no test of their own; the integration tests of Task 6 cover the wire)**

`src/Shared/Coo.Contracts/Households/HouseholdContracts.cs`:

```csharp
namespace Coo.Contracts.Households;

// Wire DTOs for members and invitations (Spec 011b §10.1). Versioned by adding, never by renaming (ADR 0001 §1).
// Roles are AuthRoles strings; outcomes are the constants below.

public sealed record CreateInvitationRequest(string Role);

/// <summary><see cref="Code"/> is the plain code, unhyphenated; it exists only in this answer (spec §3).</summary>
public sealed record InvitationCreatedResponse(Guid InvitationId, string Code, string Link, DateTimeOffset ExpiresAt);

public sealed record MemberDto(Guid UserId, string Name, string Email, string Role, DateTimeOffset JoinedAt, bool IsYou);

public sealed record InvitationDto(Guid InvitationId, string Role, Guid CreatedBy, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);

/// <summary><see cref="Invitations"/> is empty for a Caregiver.</summary>
public sealed record MembersResponse(IReadOnlyList<MemberDto> Members, IReadOnlyList<InvitationDto> Invitations);

public sealed record LeaveRequest(bool Confirm);

/// <summary><see cref="Outcome"/> is one of <see cref="LeaveOutcomes"/>.</summary>
public sealed record LeaveResponse(string Outcome);

public sealed record CreateHouseholdRequest(Guid HouseholdId);

/// <summary>The code travels in a body, never a path or query (spec §4.3): it stays out of every log.</summary>
public sealed record InvitationCodeRequest(string Code);

public sealed record InvitationPreviewResponse(
    Guid HouseholdId, string InviterName, IReadOnlyList<string> BabyNames, string Role, DateTimeOffset ExpiresAt, bool AlreadyMember);

/// <summary><see cref="Outcome"/> is one of <see cref="AcceptOutcomes"/>.</summary>
public sealed record AcceptInvitationResponse(Guid HouseholdId, string Role, string Outcome);

/// <summary>Spec 011b §10.1. Once released, a code is never renamed (ADR 0006).</summary>
public static class HouseholdErrors
{
    public const string InvitationInvalid = "invitation.invalid";
    public const string InvitationRoleUnsupported = "invitation.role-unsupported";
    public const string InvitationTooMany = "invitation.too-many";
    public const string Forbidden = "household.forbidden";
    public const string MemberNotFound = "household.member-not-found";
    public const string OwnerCannotLeave = "household.owner-cannot-leave";
    public const string LastFamily = "household.last-family";
    public const string ConfirmationRequired = "household.confirmation-required";
    public const string IdTaken = "household.id-taken";
    public const string AlreadyHasFamily = "household.already-has-family";

    /// <summary>An unreadable body (ruling 13). The app never sends one.</summary>
    public const string Malformed = "household.malformed";

    /// <summary>Reused from sign-in (Spec 010 §4.1): the limiter's answer to preview, accept and creating invitations.</summary>
    public const string RateLimited = "auth.rate-limited";
}

public static class LeaveOutcomes
{
    public const string Left = "left";
    public const string Deleted = "deleted";
}

public static class AcceptOutcomes
{
    public const string Joined = "joined";
    public const string Rejoined = "rejoined";
    public const string AlreadyMember = "already-member";
}

/// <summary>Invitation links live on the API's host (spec §4.1, R1): moving them needs an app update.</summary>
public static class InvitationLinks
{
    public const string Host = "api.coo.lechdigital.nl";
    public const string PathPrefix = "/i/";
    public const string Base = "https://" + Host + PathPrefix;

    public static string For(string code) => Base + code;
}
```

- [ ] **Step 2: Write the failing tests**

`tests/Coo.Api.Application.Tests/HouseholdFakes.cs` (an in-memory store with the transaction's semantics; only ports are
faked):

```csharp
using Coo.Api.Application.Households;
using Coo.Api.Application.Sync;
using Coo.Domain.Common;
using Coo.Domain.Households;

namespace Coo.Api.Application.Tests;

/// <summary>
/// <see cref="IHouseholdMembershipStore"/> in memory. A transaction works on copies and commits them only when the work
/// returns, so a throw inside it changes nothing, like the real one.
/// </summary>
internal sealed class FakeHouseholds : IHouseholdMembershipStore, IInvitationStore
{
    internal sealed record MemberRow(HouseholdId Household, UserId User, Role Role, DateTimeOffset JoinedAt, DateTimeOffset? RevokedAt);

    internal sealed record InvitationRow(Invitation Invitation, string Hash);

    public HashSet<HouseholdId> Households { get; } = [];

    public List<MemberRow> Members { get; } = [];

    public List<InvitationRow> Invitations { get; } = [];

    public Dictionary<UserId, (string Name, string Email)> Users { get; } = [];

    public Dictionary<HouseholdId, List<string>> Babies { get; } = [];

    /// <summary>Households holding any record (entries, babies, settings, dismissals), for the confirm rule.</summary>
    public HashSet<HouseholdId> WithRecords { get; } = [];

    public List<UserId> DeletedUsers { get; } = [];

    /// <summary>Runs inside the next transaction, after the locks: a concurrent request committing first.</summary>
    public Action? InsideNextTransaction { get; set; }

    /// <summary>Makes the next promotion touch no row (the real store's row count of 0).</summary>
    public bool PromotionTouchesNothing { get; set; }

    public List<IReadOnlyList<HouseholdId>> LockedSets { get; } = [];

    public void AddFamily(HouseholdId household, UserId owner, DateTimeOffset at, string name = "Andrzej")
    {
        Households.Add(household);
        Users.TryAdd(owner, (name, name.ToLowerInvariant() + "@example.com"));
        Members.Add(new MemberRow(household, owner, Role.Owner, at, null));
    }

    public void AddMember(HouseholdId household, UserId user, Role role, DateTimeOffset at, string name = "Kasia")
    {
        Users.TryAdd(user, (name, name.ToLowerInvariant() + "@example.com"));
        Members.Add(new MemberRow(household, user, role, at, null));
    }

    public async Task<T> RunLockedAsync<T>(IReadOnlyCollection<HouseholdId> households, Func<IMembershipTransaction, Task<T>> work, CancellationToken cancellationToken)
    {
        LockedSets.Add([.. households.Order(Comparer<HouseholdId>.Create((a, b) => GuidOrder.Compare(a.Value, b.Value)))]);
        var hook = InsideNextTransaction;
        InsideNextTransaction = null;
        hook?.Invoke();
        var tx = new Transaction(this, [.. households.Where(Households.Contains)]);
        var result = await work(tx);
        tx.Commit();
        return result;
    }

    public Task<IReadOnlyList<MemberProfile>> ListActiveMembersAsync(HouseholdId household, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<MemberProfile>>([.. Members.Where(m => m.Household == household && m.RevokedAt is null)
            .OrderBy(m => m.JoinedAt)
            .Select(m => new MemberProfile(m.User, Users[m.User].Name, Users[m.User].Email, m.Role, m.JoinedAt))]);

    public Task<IReadOnlyList<OpenInvitation>> ListOpenInvitationsAsync(HouseholdId household, DateTimeOffset now, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<OpenInvitation>>([.. Invitations.Select(r => r.Invitation)
            .Where(i => i.HouseholdId == household && i.IsUsable(now))
            .OrderBy(i => i.CreatedAt)
            .Select(i => new OpenInvitation(i.Id, i.Role, i.CreatedBy, i.CreatedAt, i.ExpiresAt))]);

    public Task<IReadOnlyList<Membership>> ListActiveMembershipsOfUserAsync(UserId user, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Membership>>([.. Members.Where(m => m.User == user && m.RevokedAt is null).OrderBy(m => m.JoinedAt).Select(ToMembership)]);

    public Task<FoundInvitation?> FindByHashAsync(string codeHash, CancellationToken cancellationToken)
    {
        var row = Invitations.FirstOrDefault(r => r.Hash == codeHash);
        return Task.FromResult(row is null ? null : new FoundInvitation(row.Invitation, Users[row.Invitation.CreatedBy].Name));
    }

    public Task<IReadOnlyList<string>> ListBabyNamesAsync(HouseholdId household, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>(Babies.TryGetValue(household, out var names) ? names : []);

    public Task<int> PurgeExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken) =>
        Task.FromResult(Invitations.RemoveAll(r => r.Invitation.ExpiresAt < now));

    internal static Membership ToMembership(MemberRow row) => Membership.Create(row.Household, row.User, row.Role, row.JoinedAt);

    private sealed class Transaction(FakeHouseholds store, HashSet<HouseholdId> locked) : IMembershipTransaction
    {
        private readonly List<MemberRow> _members = [.. store.Members];
        private readonly List<InvitationRow> _invitations = [.. store.Invitations];
        private readonly HashSet<HouseholdId> _households = [.. store.Households];
        private readonly List<UserId> _deletedUsers = [];

        public bool IsLocked(HouseholdId household) => locked.Contains(household);

        public Task<MembershipLookup> FindMembershipAsync(HouseholdId household, UserId user, CancellationToken cancellationToken)
        {
            var row = _members.FirstOrDefault(m => m.Household == household && m.User == user);
            return Task.FromResult(row is null ? MembershipLookup.None : new MembershipLookup(ToMembership(row), row.RevokedAt is not null));
        }

        public Task<IReadOnlyList<Membership>> ActiveMembersAsync(HouseholdId household, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Membership>>([.. _members.Where(m => m.Household == household && m.RevokedAt is null).Select(ToMembership)]);

        public Task<IReadOnlyList<Membership>> ActiveMembershipsOfUserAsync(UserId user, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Membership>>([.. _members.Where(m => m.User == user && m.RevokedAt is null).Select(ToMembership)]);

        public Task<Invitation?> FindInvitationForUpdateAsync(string codeHash, CancellationToken cancellationToken) =>
            Task.FromResult(_invitations.FirstOrDefault(r => r.Hash == codeHash)?.Invitation);

        public Task<Invitation?> FindInvitationForUpdateAsync(HouseholdId household, InvitationId id, CancellationToken cancellationToken) =>
            Task.FromResult(_invitations.FirstOrDefault(r => r.Invitation.Id == id && r.Invitation.HouseholdId == household)?.Invitation);

        public Task<int> CountOpenInvitationsAsync(HouseholdId household, DateTimeOffset now, CancellationToken cancellationToken) =>
            Task.FromResult(_invitations.Count(r => r.Invitation.HouseholdId == household && r.Invitation.IsUsable(now)));

        public Task AddInvitationAsync(Invitation invitation, string codeHash, CancellationToken cancellationToken)
        {
            _invitations.Add(new InvitationRow(invitation, codeHash));
            return Task.CompletedTask;
        }

        // The Domain object is the row: its state is already changed.
        public Task SaveInvitationAsync(Invitation invitation, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task AddMembershipAsync(Membership membership, CancellationToken cancellationToken)
        {
            _members.Add(new MemberRow(membership.HouseholdId, membership.UserId, membership.Role, membership.JoinedAt, null));
            return Task.CompletedTask;
        }

        public Task ReactivateMembershipAsync(HouseholdId household, UserId user, Role role, DateTimeOffset joinedAt, CancellationToken cancellationToken)
        {
            var i = _members.FindIndex(m => m.Household == household && m.User == user);
            _members[i] = _members[i] with { Role = role, JoinedAt = joinedAt, RevokedAt = null };
            return Task.CompletedTask;
        }

        public Task RevokeMembershipAsync(HouseholdId household, UserId user, DateTimeOffset now, CancellationToken cancellationToken)
        {
            var i = _members.FindIndex(m => m.Household == household && m.User == user && m.RevokedAt is null);
            if (i >= 0)
                _members[i] = _members[i] with { RevokedAt = now };
            return Task.CompletedTask;
        }

        public Task<int> WithdrawOpenInvitationsByAsync(HouseholdId household, UserId createdBy, DateTimeOffset now, CancellationToken cancellationToken)
        {
            var open = _invitations.Where(r => r.Invitation.HouseholdId == household && r.Invitation.CreatedBy == createdBy && r.Invitation.IsOpen).ToList();
            foreach (var row in open)
                row.Invitation.Withdraw(now);
            return Task.FromResult(open.Count);
        }

        public Task<int> PromoteToOwnerAsync(HouseholdId household, UserId user, CancellationToken cancellationToken)
        {
            if (store.PromotionTouchesNothing)
                return Task.FromResult(0);
            var i = _members.FindIndex(m => m.Household == household && m.User == user && m.RevokedAt is null);
            if (i < 0)
                return Task.FromResult(0);
            _members[i] = _members[i] with { Role = Role.Owner };
            return Task.FromResult(1);
        }

        public Task<bool> HoldsAnyRecordAsync(HouseholdId household, CancellationToken cancellationToken) =>
            Task.FromResult(store.WithRecords.Contains(household));

        public Task<bool> TryCreateHouseholdAsync(HouseholdId household, UserId owner, DateTimeOffset now, CancellationToken cancellationToken)
        {
            if (!_households.Add(household))
                return Task.FromResult(false);
            _members.Add(new MemberRow(household, owner, Role.Owner, now, null));
            return Task.FromResult(true);
        }

        public Task DeleteHouseholdAsync(HouseholdId household, CancellationToken cancellationToken)
        {
            _households.Remove(household);
            _members.RemoveAll(m => m.Household == household);
            _invitations.RemoveAll(r => r.Invitation.HouseholdId == household);
            return Task.CompletedTask;
        }

        public Task DeleteMembershipsOfUserAsync(UserId user, CancellationToken cancellationToken)
        {
            _members.RemoveAll(m => m.User == user);
            return Task.CompletedTask;
        }

        public Task DeleteUserAsync(UserId user, CancellationToken cancellationToken)
        {
            _members.RemoveAll(m => m.User == user);
            _invitations.RemoveAll(r => r.Invitation.CreatedBy == user);
            _deletedUsers.Add(user);
            return Task.CompletedTask;
        }

        public void Commit()
        {
            store.Members.Clear();
            store.Members.AddRange(_members);
            store.Invitations.Clear();
            store.Invitations.AddRange(_invitations);
            store.Households.Clear();
            store.Households.UnionWith(_households);
            store.DeletedUsers.AddRange(_deletedUsers);
        }
    }
}

/// <summary>The household use cases over the fakes.</summary>
internal sealed class HouseholdKit
{
    public static readonly DateTimeOffset Start = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
    public static readonly HouseholdId Family = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a1"));
    public static readonly HouseholdId Other = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a2"));
    public static readonly UserId Owner = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000c1"));
    public static readonly UserId Partner = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000c2"));
    public static readonly UserId Nanny = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000c3"));

    public Microsoft.Extensions.Time.Testing.FakeTimeProvider Time { get; } = new(Start);

    public FakeHouseholds Store { get; } = new();

    public FakeConsents Consents { get; } = new();

    private int _draws = -1;

    /// <summary>Codes are drawn from a counter, so each invitation of a test has its own: ABCDEFGH, then JKMNPQRS, …</summary>
    public CreateInvitation Create() => new(Store, Time, _ => Interlocked.Increment(ref _draws) % 31);

    public WithdrawInvitation Withdraw() => new(Store, Time);

    public ListMembers List() => new(Store, Time);

    public RemoveMember Remove() => new(Store, Time);

    public async Task GiveConsentAsync(UserId user) => await Consents.RecordAsync(
        new ConsentRecord(user, Contracts.Sync.CareDataConsent.Purpose, Contracts.Sync.CareDataConsent.CurrentWording, Start, "0.12.0", "en"),
        CancellationToken.None);

    /// <summary>The fake store's rows as <see cref="IMembershipStore"/> (the sync endpoints' per-request check) sees them.</summary>
    internal sealed class MembershipsOver(FakeHouseholds store) : FakeMemberships
    {
        public override Task<MembershipLookup> FindAsync(HouseholdId household, UserId user, CancellationToken cancellationToken)
        {
            var row = store.Members.FirstOrDefault(m => m.Household == household && m.User == user);
            return Task.FromResult(row is null ? MembershipLookup.None : new MembershipLookup(FakeHouseholds.ToMembership(row), row.RevokedAt is not null));
        }
    }
}
```

In `tests/Coo.Api.Application.Tests/SyncFakes.cs`, make `FakeMemberships` inheritable: `internal class FakeMemberships : IMembershipStore`
and its `FindAsync` `public virtual`. (No behaviour change for existing tests.)

`tests/Coo.Api.Application.Tests/InvitationUseCaseTests.cs` (this task's part; Task 3 appends preview and accept):

```csharp
using Coo.Api.Application.Auth;
using Coo.Api.Application.Households;
using Coo.Contracts.Households;
using Coo.Domain.Households;
using static Coo.Api.Application.Tests.HouseholdKit;

namespace Coo.Api.Application.Tests;

/// <summary>Spec 011b §3, §5 and §10.1 (Api.Application row): inviting, withdrawing, previewing, accepting.</summary>
public sealed class InvitationUseCaseTests
{
    private readonly HouseholdKit _kit = new();

    public InvitationUseCaseTests() => _kit.Store.AddFamily(Family, Owner, Start.AddDays(-30));

    [Fact]
    public async Task ExecuteAsync_ParentRole_CreatesAnInvitationStoredOnlyByHash()
    {
        // Act
        var created = await _kit.Create().ExecuteAsync(Owner, Family, "parent", CancellationToken.None);

        // Assert
        created.Value.Code.Value.Should().Be("ABCDEFGH", "the test's randomness counts 0, 1, 2, …");
        created.Value.ExpiresAt.Should().Be(Start.AddHours(72));
        var row = _kit.Store.Invitations.Should().ContainSingle().Subject;
        row.Hash.Should().Be(AuthSecrets.Hash("ABCDEFGH"));
        row.Invitation.Role.Should().Be(Role.Parent);
        row.Invitation.CreatedBy.Should().Be(Owner);
        _kit.Store.LockedSets.Should().ContainSingle().Which.Should().Equal(Family);
    }

    [Theory]
    [InlineData("caregiver")]
    [InlineData("owner")]
    [InlineData("")]
    [InlineData(null)]
    public async Task ExecuteAsync_AnyRoleButParent_IsRoleUnsupported(string? role)
    {
        // Act
        var created = await _kit.Create().ExecuteAsync(Owner, Family, role, CancellationToken.None);

        // Assert
        created.Error!.Code.Should().Be(HouseholdErrors.InvitationRoleUnsupported);
        _kit.Store.Invitations.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_FiveOpenInvitations_IsTooMany()
    {
        // Arrange
        for (var i = 0; i < 5; i++)
            (await _kit.Create().ExecuteAsync(Owner, Family, "parent", CancellationToken.None)).IsSuccess.Should().BeTrue();

        // Act
        var sixth = await _kit.Create().ExecuteAsync(Owner, Family, "parent", CancellationToken.None);

        // Assert
        sixth.Error!.Code.Should().Be(HouseholdErrors.InvitationTooMany);
        _kit.Store.Invitations.Should().HaveCount(5);
    }

    [Fact]
    public async Task ExecuteAsync_ExpiredOnesDoNotCount_CreatesASixth()
    {
        // Arrange
        for (var i = 0; i < 5; i++)
            await _kit.Create().ExecuteAsync(Owner, Family, "parent", CancellationToken.None);
        _kit.Time.Advance(TimeSpan.FromHours(72));

        // Act
        var created = await _kit.Create().ExecuteAsync(Owner, Family, "parent", CancellationToken.None);

        // Assert
        created.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_CaregiverInvites_IsForbidden()
    {
        // Arrange
        _kit.Store.AddMember(Family, Nanny, Role.Caregiver, Start.AddDays(-1));

        // Act
        var created = await _kit.Create().ExecuteAsync(Nanny, Family, "parent", CancellationToken.None);

        // Assert
        created.Error!.Code.Should().Be(HouseholdErrors.Forbidden);
    }

    [Fact]
    public async Task ExecuteAsync_NotAMemberOrGone_IsForbidden()
    {
        // Act
        var stranger = await _kit.Create().ExecuteAsync(Partner, Family, "parent", CancellationToken.None);
        var gone = await _kit.Create().ExecuteAsync(Owner, Other, "parent", CancellationToken.None);

        // Assert
        stranger.Error!.Code.Should().Be(HouseholdErrors.Forbidden);
        gone.Error!.Code.Should().Be(HouseholdErrors.Forbidden);
    }

    [Fact]
    public async Task ExecuteAsync_ParentWithdrawsTheOwnersInvitation_WithdrawsIt()
    {
        // Arrange: any Owner or Parent may withdraw (spec §15.2 Q3).
        _kit.Store.AddMember(Family, Partner, Role.Parent, Start.AddDays(-1));
        var created = (await _kit.Create().ExecuteAsync(Owner, Family, "parent", CancellationToken.None)).Value;

        // Act
        var result = await _kit.Withdraw().ExecuteAsync(Partner, Family, created.Id, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        _kit.Store.Invitations.Single().Invitation.RevokedAt.Should().Be(Start);
    }

    [Fact]
    public async Task ExecuteAsync_WithdrawUnknownOrAlreadyWithdrawn_SucceedsAndChangesNothing()
    {
        // Arrange
        var created = (await _kit.Create().ExecuteAsync(Owner, Family, "parent", CancellationToken.None)).Value;
        await _kit.Withdraw().ExecuteAsync(Owner, Family, created.Id, CancellationToken.None);
        _kit.Time.Advance(TimeSpan.FromMinutes(1));

        // Act
        var again = await _kit.Withdraw().ExecuteAsync(Owner, Family, created.Id, CancellationToken.None);
        var unknown = await _kit.Withdraw().ExecuteAsync(Owner, Family, new InvitationId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000e9")), CancellationToken.None);

        // Assert
        again.IsSuccess.Should().BeTrue();
        unknown.IsSuccess.Should().BeTrue();
        _kit.Store.Invitations.Single().Invitation.RevokedAt.Should().Be(Start, "the first withdrawal's time stays");
    }

    [Fact]
    public async Task ExecuteAsync_CaregiverWithdraws_IsForbidden()
    {
        // Arrange
        _kit.Store.AddMember(Family, Nanny, Role.Caregiver, Start.AddDays(-1));
        var created = (await _kit.Create().ExecuteAsync(Owner, Family, "parent", CancellationToken.None)).Value;

        // Act
        var result = await _kit.Withdraw().ExecuteAsync(Nanny, Family, created.Id, CancellationToken.None);

        // Assert
        result.Error!.Code.Should().Be(HouseholdErrors.Forbidden);
        _kit.Store.Invitations.Single().Invitation.IsOpen.Should().BeTrue();
    }
}
```

`tests/Coo.Api.Application.Tests/MemberUseCaseTests.cs` (this task's part; Task 3 appends leaving and creating):

```csharp
using Coo.Api.Application.Households;
using Coo.Contracts.Households;
using Coo.Domain.Households;
using static Coo.Api.Application.Tests.HouseholdKit;

namespace Coo.Api.Application.Tests;

/// <summary>Spec 011b §8 and §10.1: the member list, removing, leaving, creating a family.</summary>
public sealed class MemberUseCaseTests
{
    private readonly HouseholdKit _kit = new();

    public MemberUseCaseTests()
    {
        _kit.Store.AddFamily(Family, Owner, Start.AddDays(-30), "Andrzej");
        _kit.Store.AddMember(Family, Partner, Role.Parent, Start.AddDays(-2), "Kasia");
    }

    [Fact]
    public async Task ExecuteAsync_ParentAsks_ListsActiveMembersAndOpenInvitations()
    {
        // Arrange
        _kit.Store.AddMember(Family, Nanny, Role.Caregiver, Start.AddDays(-1), "Ola");
        await _kit.Remove().ExecuteAsync(Owner, Family, Nanny, CancellationToken.None);
        await _kit.Create().ExecuteAsync(Owner, Family, "parent", CancellationToken.None);

        // Act
        var view = await _kit.List().ExecuteAsync(Partner, Family, CancellationToken.None);

        // Assert
        view.Value.Members.Select(m => m.Name).Should().Equal("Andrzej", "Kasia");
        view.Value.Invitations.Should().ContainSingle();
    }

    [Fact]
    public async Task ExecuteAsync_CaregiverAsks_SeesMembersButNoInvitations()
    {
        // Arrange
        _kit.Store.AddMember(Family, Nanny, Role.Caregiver, Start.AddDays(-1), "Ola");
        await _kit.Create().ExecuteAsync(Owner, Family, "parent", CancellationToken.None);

        // Act
        var view = await _kit.List().ExecuteAsync(Nanny, Family, CancellationToken.None);

        // Assert
        view.Value.Members.Should().HaveCount(3);
        view.Value.Invitations.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_RemovedMemberAsks_IsForbidden()
    {
        // Arrange
        await _kit.Remove().ExecuteAsync(Owner, Family, Partner, CancellationToken.None);

        // Act
        var view = await _kit.List().ExecuteAsync(Partner, Family, CancellationToken.None);

        // Assert
        view.Error!.Code.Should().Be(HouseholdErrors.Forbidden);
    }

    [Fact]
    public async Task ExecuteAsync_OwnerRemovesTheParent_RevokesAndWithdrawsTheirInvitations()
    {
        // Arrange
        await _kit.Create().ExecuteAsync(Partner, Family, "parent", CancellationToken.None);
        await _kit.Create().ExecuteAsync(Owner, Family, "parent", CancellationToken.None);

        // Act
        var result = await _kit.Remove().ExecuteAsync(Owner, Family, Partner, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        _kit.Store.Members.Single(m => m.User == Partner).RevokedAt.Should().Be(Start, "the row stays for membership-revoked");
        _kit.Store.Invitations.Single(r => r.Invitation.CreatedBy == Partner).Invitation.RevokedAt.Should().Be(Start);
        _kit.Store.Invitations.Single(r => r.Invitation.CreatedBy == Owner).Invitation.IsOpen.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_ParentRemovesTheOwner_IsForbidden()
    {
        // Act
        var result = await _kit.Remove().ExecuteAsync(Partner, Family, Owner, CancellationToken.None);

        // Assert
        result.Error!.Code.Should().Be(HouseholdErrors.Forbidden);
        _kit.Store.Members.Single(m => m.User == Owner).RevokedAt.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteAsync_RemoveOneself_IsForbidden()
    {
        // Act
        var result = await _kit.Remove().ExecuteAsync(Partner, Family, Partner, CancellationToken.None);

        // Assert
        result.Error!.Code.Should().Be(HouseholdErrors.Forbidden, "leaving is its own call");
    }

    [Fact]
    public async Task ExecuteAsync_RemoveSomeoneNotInTheFamily_IsMemberNotFound()
    {
        // Act
        var result = await _kit.Remove().ExecuteAsync(Owner, Family, Nanny, CancellationToken.None);

        // Assert
        result.Error!.Code.Should().Be(HouseholdErrors.MemberNotFound);
    }

    [Fact]
    public async Task ExecuteAsync_CaregiverRemoves_IsForbiddenBeforeLookingUpTheTarget()
    {
        // Arrange
        _kit.Store.AddMember(Family, Nanny, Role.Caregiver, Start.AddDays(-1), "Ola");

        // Act
        var result = await _kit.Remove().ExecuteAsync(Nanny, Family, Owner, CancellationToken.None);

        // Assert
        result.Error!.Code.Should().Be(HouseholdErrors.Forbidden);
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test tests/Coo.Api.Application.Tests --filter "FullyQualifiedName~InvitationUseCaseTests|FullyQualifiedName~MemberUseCaseTests"`
Expected: FAIL to compile (`IHouseholdMembershipStore` not found).

- [ ] **Step 4: Write the ports and the use cases**

`src/Backend/Coo.Api.Application/Households/Ports.cs`:

```csharp
using Coo.Api.Application.Sync;
using Coo.Domain.Common;
using Coo.Domain.Households;

namespace Coo.Api.Application.Households;

/// <summary>A member as the Family page shows them (spec §2): name and email come from their account.</summary>
public sealed record MemberProfile(UserId UserId, string Name, string Email, Role Role, DateTimeOffset JoinedAt);

public sealed record OpenInvitation(InvitationId Id, Role Role, UserId CreatedBy, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);

public sealed record FoundInvitation(Invitation Invitation, string InviterName);

/// <summary><c>invitations</c> outside a transaction: the preview's reads and the daily purge.</summary>
public interface IInvitationStore
{
    Task<FoundInvitation?> FindByHashAsync(string codeHash, CancellationToken cancellationToken);

    /// <summary>The live baby profiles' names, oldest first.</summary>
    Task<IReadOnlyList<string>> ListBabyNamesAsync(HouseholdId household, CancellationToken cancellationToken);

    /// <summary>Every invitation whose expiry has passed, used and withdrawn ones included (spec §10.3).</summary>
    Task<int> PurgeExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken);
}

/// <summary>Memberships and invitations. Every change runs in <see cref="RunLockedAsync"/> (spec §10.4).</summary>
public interface IHouseholdMembershipStore
{
    /// <summary>
    /// One transaction: the existing households among <paramref name="households"/> are locked (<c>FOR UPDATE</c>, in
    /// ascending ID order), then <paramref name="work"/> runs and the transaction commits. A throw rolls everything back.
    /// </summary>
    Task<T> RunLockedAsync<T>(IReadOnlyCollection<HouseholdId> households, Func<IMembershipTransaction, Task<T>> work, CancellationToken cancellationToken);

    /// <summary>Active members, earliest joined first, with their names and emails.</summary>
    Task<IReadOnlyList<MemberProfile>> ListActiveMembersAsync(HouseholdId household, CancellationToken cancellationToken);

    /// <summary>Neither used nor withdrawn, and not expired at <paramref name="now"/>; oldest first.</summary>
    Task<IReadOnlyList<OpenInvitation>> ListOpenInvitationsAsync(HouseholdId household, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Earliest joined first.</summary>
    Task<IReadOnlyList<Membership>> ListActiveMembershipsOfUserAsync(UserId user, CancellationToken cancellationToken);
}

/// <summary>Reads and writes inside <see cref="IHouseholdMembershipStore.RunLockedAsync"/>'s transaction.</summary>
public interface IMembershipTransaction
{
    /// <summary>Whether the household existed and is locked by this transaction.</summary>
    bool IsLocked(HouseholdId household);

    /// <summary>The row, revoked or not (ADR 0002 §10's distinction).</summary>
    Task<MembershipLookup> FindMembershipAsync(HouseholdId household, UserId user, CancellationToken cancellationToken);

    Task<IReadOnlyList<Membership>> ActiveMembersAsync(HouseholdId household, CancellationToken cancellationToken);

    Task<IReadOnlyList<Membership>> ActiveMembershipsOfUserAsync(UserId user, CancellationToken cancellationToken);

    /// <summary>The invitation with this code hash, locked.</summary>
    Task<Invitation?> FindInvitationForUpdateAsync(string codeHash, CancellationToken cancellationToken);

    Task<Invitation?> FindInvitationForUpdateAsync(HouseholdId household, InvitationId id, CancellationToken cancellationToken);

    Task<int> CountOpenInvitationsAsync(HouseholdId household, DateTimeOffset now, CancellationToken cancellationToken);

    Task AddInvitationAsync(Invitation invitation, string codeHash, CancellationToken cancellationToken);

    /// <summary>Writes <c>accepted_by</c>, <c>accepted_at</c> and <c>revoked_at</c>.</summary>
    Task SaveInvitationAsync(Invitation invitation, CancellationToken cancellationToken);

    Task AddMembershipAsync(Membership membership, CancellationToken cancellationToken);

    /// <summary>A revoked row comes back: <c>revoked_at = NULL</c>, the role, a new <c>joined_at</c>.</summary>
    Task ReactivateMembershipAsync(HouseholdId household, UserId user, Role role, DateTimeOffset joinedAt, CancellationToken cancellationToken);

    Task RevokeMembershipAsync(HouseholdId household, UserId user, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>The user's open invitations for the household are withdrawn; returns how many.</summary>
    Task<int> WithdrawOpenInvitationsByAsync(HouseholdId household, UserId createdBy, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Returns the number of rows changed; the caller requires exactly one (spec §10.4).</summary>
    Task<int> PromoteToOwnerAsync(HouseholdId household, UserId user, CancellationToken cancellationToken);

    /// <summary>Any care entry, baby profile, settings or dismissal, tombstones included.</summary>
    Task<bool> HoldsAnyRecordAsync(HouseholdId household, CancellationToken cancellationToken);

    /// <summary>The household and its Owner membership; false (and nothing written) when the ID is taken.</summary>
    Task<bool> TryCreateHouseholdAsync(HouseholdId household, UserId owner, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>The household with all of its records, memberships and invitations.</summary>
    Task DeleteHouseholdAsync(HouseholdId household, CancellationToken cancellationToken);

    /// <summary>Every membership row of the user, revoked ones included.</summary>
    Task DeleteMembershipsOfUserAsync(UserId user, CancellationToken cancellationToken);

    /// <summary>The user with their logins, sessions, consents and invitations sent (ON DELETE CASCADE).</summary>
    Task DeleteUserAsync(UserId user, CancellationToken cancellationToken);
}
```

`src/Backend/Coo.Api.Application/Households/InvitationUseCases.cs` (this task: create and withdraw; Task 3 appends preview
and accept to the same file):

```csharp
using Coo.Api.Application.Auth;
using Coo.Contracts.Auth;
using Coo.Contracts.Households;
using Coo.Domain.Common;
using Coo.Domain.Households;

namespace Coo.Api.Application.Households;

public sealed record CreatedInvitation(InvitationId Id, InvitationCode Code, DateTimeOffset ExpiresAt);

/// <summary>
/// <c>POST /api/households/{id}/invitations</c> (spec §3). An Owner or Parent; Parent invitations only (R2), so a
/// Caregiver invitation later is an Application change. At most 5 open invitations per family, checked under the lock.
/// </summary>
public sealed class CreateInvitation(IHouseholdMembershipStore store, TimeProvider time, Func<int, int> nextIndex)
{
    public const int MaxOpenPerFamily = 5;

    public Task<Result<CreatedInvitation>> ExecuteAsync(UserId user, HouseholdId household, string? role, CancellationToken cancellationToken)
    {
        if (role != AuthRoles.Parent)
            return Task.FromResult(Result<CreatedInvitation>.Failure(HouseholdErrors.InvitationRoleUnsupported));

        return store.RunLockedAsync([household], async tx =>
        {
            if (!tx.IsLocked(household))
                return Result<CreatedInvitation>.Failure(HouseholdErrors.Forbidden);
            var me = await tx.FindMembershipAsync(household, user, cancellationToken);
            if (me.Membership is not { CanInvite: true } || me.IsRevoked)
                return Result<CreatedInvitation>.Failure(HouseholdErrors.Forbidden);

            var now = time.GetUtcNow();
            if (await tx.CountOpenInvitationsAsync(household, now, cancellationToken) >= MaxOpenPerFamily)
                return Result<CreatedInvitation>.Failure(HouseholdErrors.InvitationTooMany);

            var code = InvitationCode.FromRandom(nextIndex);
            var invitation = Invitation.Create(new InvitationId(Guid.CreateVersion7(now)), household, Role.Parent, user, now);
            await tx.AddInvitationAsync(invitation, AuthSecrets.Hash(code.Value), cancellationToken);
            return Result<CreatedInvitation>.Ok(new CreatedInvitation(invitation.Id, code, invitation.ExpiresAt));
        }, cancellationToken);
    }
}

/// <summary><c>DELETE /api/households/{id}/invitations/{invitationId}</c>: already withdrawn, used, expired or unknown is a success.</summary>
public sealed class WithdrawInvitation(IHouseholdMembershipStore store, TimeProvider time)
{
    public Task<Result> ExecuteAsync(UserId user, HouseholdId household, InvitationId invitationId, CancellationToken cancellationToken) =>
        store.RunLockedAsync([household], async tx =>
        {
            if (!tx.IsLocked(household))
                return Result.Failure(HouseholdErrors.Forbidden);
            var me = await tx.FindMembershipAsync(household, user, cancellationToken);
            if (me.Membership is not { IsParent: true } member || me.IsRevoked)
                return Result.Failure(HouseholdErrors.Forbidden);
            var invitation = await tx.FindInvitationForUpdateAsync(household, invitationId, cancellationToken);
            if (invitation is null || !member.CanWithdraw(invitation))
                return Result.Success;
            invitation.Withdraw(time.GetUtcNow());
            await tx.SaveInvitationAsync(invitation, cancellationToken);
            return Result.Success;
        }, cancellationToken);
}
```

`src/Backend/Coo.Api.Application/Households/MemberUseCases.cs` (this task: list and remove; Task 3 appends leave and create):

```csharp
using Coo.Contracts.Households;
using Coo.Domain.Common;
using Coo.Domain.Households;

namespace Coo.Api.Application.Households;

public sealed record MembersView(IReadOnlyList<MemberProfile> Members, IReadOnlyList<OpenInvitation> Invitations);

/// <summary><c>GET /api/households/{id}/members</c>: any active member; invitations only for an Owner or Parent.</summary>
public sealed class ListMembers(IHouseholdMembershipStore store, TimeProvider time)
{
    public async Task<Result<MembersView>> ExecuteAsync(UserId user, HouseholdId household, CancellationToken cancellationToken)
    {
        var mine = await store.ListActiveMembershipsOfUserAsync(user, cancellationToken);
        if (mine.FirstOrDefault(m => m.HouseholdId == household) is not { } me)
            return Result<MembersView>.Failure(HouseholdErrors.Forbidden);
        var members = await store.ListActiveMembersAsync(household, cancellationToken);
        IReadOnlyList<OpenInvitation> invitations = me.IsParent
            ? await store.ListOpenInvitationsAsync(household, time.GetUtcNow(), cancellationToken)
            : [];
        return new MembersView(members, invitations);
    }
}

/// <summary>
/// <c>DELETE /api/households/{id}/members/{userId}</c> (spec §8.1): the membership is revoked (the row stays, so ADR 0002
/// §10's answer works) and the removed member's open invitations for the family are withdrawn, in one transaction.
/// </summary>
public sealed class RemoveMember(IHouseholdMembershipStore store, TimeProvider time)
{
    public Task<Result> ExecuteAsync(UserId user, HouseholdId household, UserId target, CancellationToken cancellationToken) =>
        store.RunLockedAsync([household], async tx =>
        {
            if (!tx.IsLocked(household))
                return Result.Failure(HouseholdErrors.Forbidden);
            var me = await tx.FindMembershipAsync(household, user, cancellationToken);
            if (me.Membership is not { IsParent: true } remover || me.IsRevoked)
                return Result.Failure(HouseholdErrors.Forbidden);
            var them = await tx.FindMembershipAsync(household, target, cancellationToken);
            if (them.Membership is not { } removed || them.IsRevoked)
                return Result.Failure(HouseholdErrors.MemberNotFound);
            if (!remover.CanRemove(removed))
                return Result.Failure(HouseholdErrors.Forbidden);

            var now = time.GetUtcNow();
            await tx.RevokeMembershipAsync(household, target, now, cancellationToken);
            await tx.WithdrawOpenInvitationsByAsync(household, target, now, cancellationToken);
            return Result.Success;
        }, cancellationToken);
}
```

The kit's `Create()` passes a counter as the randomness; the host passes `RandomNumberGenerator.GetInt32` (Task 6).

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/Coo.Api.Application.Tests --filter "FullyQualifiedName~InvitationUseCaseTests|FullyQualifiedName~MemberUseCaseTests"`
Expected: PASS. (The kit's `Leave()`, `CreateHousehold()`, `Preview()` and `Accept()` come with Task 3's classes.)

- [ ] **Step 6: Commit**

```bash
git add src/Shared/Coo.Contracts/Households src/Backend/Coo.Api.Application/Households tests/Coo.Api.Application.Tests
git commit -m "Api.Application: invite a parent, withdraw, list and remove members, under the family's lock (Spec 011b §3, §8.1)"
```

---

### Task 3: Leaving, creating a family, previewing and accepting (`Coo.Api.Application/Households`)

**Files:**
- Modify: `src/Backend/Coo.Api.Application/Households/MemberUseCases.cs` (append `LeaveHousehold`, `CreateHousehold`),
  `src/Backend/Coo.Api.Application/Households/InvitationUseCases.cs` (append `PreviewInvitation`, `AcceptInvitation`)
- Test: `tests/Coo.Api.Application.Tests/HouseholdFakes.cs` (add the kit's `Leave()`, `CreateHousehold()`, `Preview()`,
  `Accept()`), `tests/Coo.Api.Application.Tests/MemberUseCaseTests.cs`, `tests/Coo.Api.Application.Tests/InvitationUseCaseTests.cs`

**Interfaces:**
- Consumes: Task 2's ports, `HouseholdErrors`, `IConsentStore`, `IMembershipStore`, `CareDataConsent`, `SyncErrors`.
- Produces:
  - `public enum LeaveOutcome { Left, Deleted }`; `LeaveHousehold(IHouseholdMembershipStore store, TimeProvider time)` with
    `Task<Result<LeaveOutcome>> ExecuteAsync(UserId user, HouseholdId household, bool confirm, CancellationToken)`.
  - `CreateHousehold(IHouseholdMembershipStore store, TimeProvider time)` with
    `Task<Result> ExecuteAsync(UserId user, HouseholdId household, CancellationToken)`.
  - `public sealed record InvitationPreview(HouseholdId Household, string InviterName, IReadOnlyList<string> BabyNames, Role Role, DateTimeOffset ExpiresAt, bool AlreadyMember)`;
    `PreviewInvitation(IInvitationStore invitations, IMembershipStore memberships, TimeProvider time)` with
    `Task<Result<InvitationPreview>> ExecuteAsync(UserId user, string? code, CancellationToken)`.
  - `public enum AcceptOutcome { Joined, Rejoined, AlreadyMember }`, `public sealed record AcceptResult(HouseholdId Household, Role Role, AcceptOutcome Outcome)`;
    `AcceptInvitation(IInvitationStore invitations, IHouseholdMembershipStore store, IConsentStore consents, TimeProvider time)` with
    `Task<Result<AcceptResult>> ExecuteAsync(UserId user, string? code, CancellationToken)`.

- [ ] **Step 1: Write the failing tests**

Add to `HouseholdKit` in `HouseholdFakes.cs` (they were left out of Task 2 so it compiled):

```csharp
    public LeaveHousehold Leave() => new(Store, Time);

    public CreateHousehold CreateHousehold() => new(Store, Time);

    public PreviewInvitation Preview() => new(Store, new MembershipsOver(Store), Time);

    public AcceptInvitation Accept() => new(Store, Store, Consents, Time);
```

Append to `MemberUseCaseTests` (constructor: `Owner` owns `Family`, `Partner` is its Parent):

```csharp
    [Fact]
    public async Task ExecuteAsync_ParentWithAnotherFamilyLeaves_EndsTheMembershipAndWithdrawsTheirInvitations()
    {
        // Arrange
        _kit.Store.AddFamily(Other, Partner, Start.AddDays(-60), "Kasia");
        await _kit.Create().ExecuteAsync(Partner, Family, "parent", CancellationToken.None);

        // Act
        var result = await _kit.Leave().ExecuteAsync(Partner, Family, confirm: false, CancellationToken.None);

        // Assert
        result.Value.Should().Be(LeaveOutcome.Left);
        _kit.Store.Members.Single(m => m.User == Partner && m.Household == Family).RevokedAt.Should().Be(Start);
        _kit.Store.Invitations.Single().Invitation.RevokedAt.Should().Be(Start);
    }

    [Fact]
    public async Task ExecuteAsync_LeaveRetriedAfterLeaving_AnswersLeftAgain()
    {
        // Arrange: Review Focus 4, the first answer was lost.
        _kit.Store.AddFamily(Other, Partner, Start.AddDays(-60), "Kasia");
        await _kit.Leave().ExecuteAsync(Partner, Family, confirm: false, CancellationToken.None);

        // Act
        var again = await _kit.Leave().ExecuteAsync(Partner, Family, confirm: false, CancellationToken.None);

        // Assert
        again.Value.Should().Be(LeaveOutcome.Left);
    }

    [Fact]
    public async Task ExecuteAsync_ParentsOnlyFamily_IsLastFamily()
    {
        // Act
        var result = await _kit.Leave().ExecuteAsync(Partner, Family, confirm: true, CancellationToken.None);

        // Assert
        result.Error!.Code.Should().Be(HouseholdErrors.LastFamily);
        _kit.Store.Members.Single(m => m.User == Partner).RevokedAt.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteAsync_OwnerWithMembersLeaves_IsOwnerCannotLeave()
    {
        // Arrange
        _kit.Store.AddFamily(Other, Owner, Start.AddDays(-90));

        // Act
        var result = await _kit.Leave().ExecuteAsync(Owner, Family, confirm: true, CancellationToken.None);

        // Assert
        result.Error!.Code.Should().Be(HouseholdErrors.OwnerCannotLeave);
    }

    [Fact]
    public async Task ExecuteAsync_OwnerAloneWithoutConfirmOnAnEmptyFamily_DeletesIt()
    {
        // Arrange: §6.1's automatic leave of an emptied adopted family.
        _kit.Store.AddFamily(Other, Nanny, Start.AddDays(-1), "Ola");
        _kit.Store.AddFamily(new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a3")), Nanny, Start.AddDays(-9), "Ola");

        // Act
        var result = await _kit.Leave().ExecuteAsync(Nanny, Other, confirm: false, CancellationToken.None);

        // Assert
        result.Value.Should().Be(LeaveOutcome.Deleted);
        _kit.Store.Households.Should().NotContain(Other);
    }

    [Fact]
    public async Task ExecuteAsync_OwnerAloneWithoutConfirmOnAFamilyWithRecords_IsConfirmationRequired()
    {
        // Arrange: another phone of hers pushed into it (spec §6.1).
        _kit.Store.AddFamily(Other, Nanny, Start.AddDays(-1), "Ola");
        _kit.Store.AddFamily(new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a3")), Nanny, Start.AddDays(-9), "Ola");
        _kit.Store.WithRecords.Add(Other);

        // Act
        var unconfirmed = await _kit.Leave().ExecuteAsync(Nanny, Other, confirm: false, CancellationToken.None);
        var confirmed = await _kit.Leave().ExecuteAsync(Nanny, Other, confirm: true, CancellationToken.None);

        // Assert
        unconfirmed.Error!.Code.Should().Be(HouseholdErrors.ConfirmationRequired);
        confirmed.Value.Should().Be(LeaveOutcome.Deleted);
        _kit.Store.Households.Should().NotContain(Other);
    }

    [Fact]
    public async Task ExecuteAsync_LeaveAFamilyThatIsGone_AnswersDeleted()
    {
        // Act: a retried delete whose first answer was lost (ruling 15).
        var result = await _kit.Leave().ExecuteAsync(Partner, Other, confirm: true, CancellationToken.None);

        // Assert
        result.Value.Should().Be(LeaveOutcome.Deleted);
    }

    [Fact]
    public async Task ExecuteAsync_LeaveAFamilyNeverJoined_IsForbidden()
    {
        // Arrange
        _kit.Store.AddFamily(Other, Nanny, Start.AddDays(-1), "Ola");

        // Act
        var result = await _kit.Leave().ExecuteAsync(Partner, Other, confirm: true, CancellationToken.None);

        // Assert
        result.Error!.Code.Should().Be(HouseholdErrors.Forbidden);
    }

    [Fact]
    public async Task ExecuteAsync_CreateForAUserWithNoActiveFamily_CreatesItWithThemAsOwner()
    {
        // Arrange: §8.4, removed from their only family.
        await _kit.Remove().ExecuteAsync(Owner, Family, Partner, CancellationToken.None);

        // Act
        var result = await _kit.CreateHousehold().ExecuteAsync(Partner, Other, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        _kit.Store.Members.Should().ContainSingle(m => m.Household == Other && m.User == Partner && m.Role == Role.Owner);
    }

    [Fact]
    public async Task ExecuteAsync_CreateWithATakenId_IsIdTaken()
    {
        // Arrange
        await _kit.Remove().ExecuteAsync(Owner, Family, Partner, CancellationToken.None);

        // Act
        var result = await _kit.CreateHousehold().ExecuteAsync(Partner, Family, CancellationToken.None);

        // Assert
        result.Error!.Code.Should().Be(HouseholdErrors.IdTaken);
    }

    [Fact]
    public async Task ExecuteAsync_CreateWhileInAFamily_IsAlreadyHasFamily()
    {
        // Act
        var result = await _kit.CreateHousehold().ExecuteAsync(Partner, Other, CancellationToken.None);

        // Assert
        result.Error!.Code.Should().Be(HouseholdErrors.AlreadyHasFamily);
        _kit.Store.Households.Should().NotContain(Other);
    }
```

Append to `InvitationUseCaseTests` (the constructor's family: `Owner` owns `Family`; `Partner` is a stranger):

```csharp
    private async Task<string> InviteAsync() =>
        (await _kit.Create().ExecuteAsync(Owner, Family, "parent", CancellationToken.None)).Value.Code.Display;

    [Fact]
    public async Task ExecuteAsync_PreviewAValidCode_NamesTheInviterAndTheBabies()
    {
        // Arrange
        _kit.Store.Babies[Family] = ["Maja"];
        var code = await InviteAsync();

        // Act
        var preview = await _kit.Preview().ExecuteAsync(Partner, code.ToLowerInvariant(), CancellationToken.None);

        // Assert
        preview.Value.Should().BeEquivalentTo(new InvitationPreview(Family, "Andrzej", ["Maja"], Role.Parent, Start.AddHours(72), AlreadyMember: false));
    }

    [Theory]
    [InlineData("ZZZZ-ZZZZ")]
    [InlineData("not a code")]
    [InlineData(null)]
    public async Task ExecuteAsync_PreviewUnknownOrMalformed_IsInvalid(string? code)
    {
        // Act
        var preview = await _kit.Preview().ExecuteAsync(Partner, code, CancellationToken.None);

        // Assert
        preview.Error!.Code.Should().Be(HouseholdErrors.InvitationInvalid);
    }

    [Fact]
    public async Task ExecuteAsync_PreviewByAMember_SaysAlreadyMember()
    {
        // Arrange
        var code = await InviteAsync();

        // Act
        var preview = await _kit.Preview().ExecuteAsync(Owner, code, CancellationToken.None);

        // Assert
        preview.Value.AlreadyMember.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_AcceptWithConsent_JoinsAsParentAndUsesTheCode()
    {
        // Arrange
        var code = await InviteAsync();
        await _kit.GiveConsentAsync(Partner);

        // Act
        var accepted = await _kit.Accept().ExecuteAsync(Partner, code, CancellationToken.None);

        // Assert
        accepted.Value.Should().Be(new AcceptResult(Family, Role.Parent, AcceptOutcome.Joined));
        _kit.Store.Members.Should().ContainSingle(m => m.User == Partner && m.Household == Family && m.Role == Role.Parent && m.RevokedAt == null);
        _kit.Store.Invitations.Single().Invitation.WasAcceptedBy(Partner).Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_AcceptWithoutConsent_IsConsentRequiredAndChangesNothing()
    {
        // Arrange
        var code = await InviteAsync();

        // Act
        var accepted = await _kit.Accept().ExecuteAsync(Partner, code, CancellationToken.None);

        // Assert
        accepted.Error!.Code.Should().Be(Contracts.Sync.SyncErrors.ConsentRequired);
        _kit.Store.Members.Should().NotContain(m => m.User == Partner);
        _kit.Store.Invitations.Single().Invitation.IsOpen.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_AcceptRetriedAfterALostAnswer_AnswersJoinedAgain()
    {
        // Arrange: spec §5.2, the same user re-presenting a code they used, still a member.
        var code = await InviteAsync();
        await _kit.GiveConsentAsync(Partner);
        await _kit.Accept().ExecuteAsync(Partner, code, CancellationToken.None);
        _kit.Time.Advance(TimeSpan.FromMinutes(1));

        // Act
        var again = await _kit.Accept().ExecuteAsync(Partner, code, CancellationToken.None);

        // Assert
        again.Value.Outcome.Should().Be(AcceptOutcome.Joined);
        _kit.Store.Members.Count(m => m.User == Partner).Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_AcceptACodeSomeoneElseUsed_IsInvalid()
    {
        // Arrange
        var code = await InviteAsync();
        await _kit.GiveConsentAsync(Partner);
        await _kit.GiveConsentAsync(Nanny);
        await _kit.Accept().ExecuteAsync(Partner, code, CancellationToken.None);

        // Act
        var second = await _kit.Accept().ExecuteAsync(Nanny, code, CancellationToken.None);

        // Assert
        second.Error!.Code.Should().Be(HouseholdErrors.InvitationInvalid);
    }

    [Fact]
    public async Task ExecuteAsync_AcceptOwnUsedCodeAfterBeingRemoved_IsInvalid()
    {
        // Arrange
        var code = await InviteAsync();
        await _kit.GiveConsentAsync(Partner);
        await _kit.Accept().ExecuteAsync(Partner, code, CancellationToken.None);
        await _kit.Remove().ExecuteAsync(Owner, Family, Partner, CancellationToken.None);

        // Act
        var again = await _kit.Accept().ExecuteAsync(Partner, code, CancellationToken.None);

        // Assert
        again.Error!.Code.Should().Be(HouseholdErrors.InvitationInvalid);
    }

    [Fact]
    public async Task ExecuteAsync_AcceptANewCodeAfterBeingRemoved_Rejoins()
    {
        // Arrange
        await _kit.GiveConsentAsync(Partner);
        await _kit.Accept().ExecuteAsync(Partner, await InviteAsync(), CancellationToken.None);
        await _kit.Remove().ExecuteAsync(Owner, Family, Partner, CancellationToken.None);
        _kit.Time.Advance(TimeSpan.FromHours(1));

        // Act
        var rejoined = await _kit.Accept().ExecuteAsync(Partner, await InviteAsync(), CancellationToken.None);

        // Assert
        rejoined.Value.Outcome.Should().Be(AcceptOutcome.Rejoined);
        var row = _kit.Store.Members.Single(m => m.User == Partner);
        row.RevokedAt.Should().BeNull();
        row.JoinedAt.Should().Be(Start.AddHours(1));
    }

    [Fact]
    public async Task ExecuteAsync_AcceptByAMember_IsAlreadyMemberAndLeavesTheCodeUnused()
    {
        // Arrange
        var code = await InviteAsync();
        await _kit.GiveConsentAsync(Owner);

        // Act
        var accepted = await _kit.Accept().ExecuteAsync(Owner, code, CancellationToken.None);

        // Assert
        accepted.Value.Outcome.Should().Be(AcceptOutcome.AlreadyMember);
        _kit.Store.Invitations.Single().Invitation.IsOpen.Should().BeTrue();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExecuteAsync_AcceptExpiredOrWithdrawn_IsInvalid(bool expired)
    {
        // Arrange
        var created = (await _kit.Create().ExecuteAsync(Owner, Family, "parent", CancellationToken.None)).Value;
        await _kit.GiveConsentAsync(Partner);
        if (expired)
            _kit.Time.Advance(TimeSpan.FromHours(72));
        else
            await _kit.Withdraw().ExecuteAsync(Owner, Family, created.Id, CancellationToken.None);

        // Act
        var accepted = await _kit.Accept().ExecuteAsync(Partner, created.Code.Value, CancellationToken.None);

        // Assert
        accepted.Error!.Code.Should().Be(HouseholdErrors.InvitationInvalid);
    }

    [Fact]
    public async Task ExecuteAsync_FamilyDeletedBeforeTheLock_IsInvalid()
    {
        // Arrange: the found invitation's family goes before the accept locks it (a concurrent account deletion).
        var code = await InviteAsync();
        await _kit.GiveConsentAsync(Partner);
        _kit.Store.InsideNextTransaction = () => _kit.Store.Households.Remove(Family);

        // Act
        var accepted = await _kit.Accept().ExecuteAsync(Partner, code, CancellationToken.None);

        // Assert
        accepted.Error!.Code.Should().Be(HouseholdErrors.InvitationInvalid);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Coo.Api.Application.Tests --filter "FullyQualifiedName~InvitationUseCaseTests|FullyQualifiedName~MemberUseCaseTests"`
Expected: FAIL to compile (`LeaveHousehold` not found).

- [ ] **Step 3: Write the use cases**

Append to `MemberUseCases.cs`:

```csharp
public enum LeaveOutcome
{
    Left,
    Deleted,
}

/// <summary>
/// <c>POST /api/households/{id}/leave</c> (spec §8.3). Idempotent: a revoked membership answers <c>left</c> again, a family
/// that no longer exists answers <c>deleted</c> (ruling 15). <c>confirm: false</c> deletes an Owner-only family only when
/// it holds no record.
/// </summary>
public sealed class LeaveHousehold(IHouseholdMembershipStore store, TimeProvider time)
{
    public Task<Result<LeaveOutcome>> ExecuteAsync(UserId user, HouseholdId household, bool confirm, CancellationToken cancellationToken) =>
        store.RunLockedAsync([household], async tx =>
        {
            if (!tx.IsLocked(household))
                return Result<LeaveOutcome>.Ok(LeaveOutcome.Deleted);
            var me = await tx.FindMembershipAsync(household, user, cancellationToken);
            if (me.Membership is null)
                return Result<LeaveOutcome>.Failure(HouseholdErrors.Forbidden);
            if (me.IsRevoked)
                return Result<LeaveOutcome>.Ok(LeaveOutcome.Left);

            var members = await tx.ActiveMembersAsync(household, cancellationToken);
            var hasOther = (await tx.ActiveMembershipsOfUserAsync(user, cancellationToken)).Any(m => m.HouseholdId != household);
            switch (HouseholdLeaving.Decide(members, user, hasOther))
            {
                case LeaveDecision.RefusedOwnerWithMembers:
                    return Result<LeaveOutcome>.Failure(HouseholdErrors.OwnerCannotLeave);
                case LeaveDecision.RefusedLastFamily:
                    return Result<LeaveOutcome>.Failure(HouseholdErrors.LastFamily);
                case LeaveDecision.HouseholdDeleted:
                    if (!confirm && await tx.HoldsAnyRecordAsync(household, cancellationToken))
                        return Result<LeaveOutcome>.Failure(HouseholdErrors.ConfirmationRequired);
                    await tx.DeleteHouseholdAsync(household, cancellationToken);
                    return Result<LeaveOutcome>.Ok(LeaveOutcome.Deleted);
                default:
                    var now = time.GetUtcNow();
                    await tx.RevokeMembershipAsync(household, user, now, cancellationToken);
                    await tx.WithdrawOpenInvitationsByAsync(household, user, now, cancellationToken);
                    return Result<LeaveOutcome>.Ok(LeaveOutcome.Left);
            }
        }, cancellationToken);
}

/// <summary>
/// <c>POST /api/households</c> (spec §8.4): a user with no active family gets one with the phone's ID, as sign-in's
/// adoption does. <c>id-taken</c> makes the phone re-key; <c>already-has-family</c> means they were invited back.
/// </summary>
public sealed class CreateHousehold(IHouseholdMembershipStore store, TimeProvider time)
{
    public Task<Result> ExecuteAsync(UserId user, HouseholdId household, CancellationToken cancellationToken) =>
        store.RunLockedAsync([], async tx =>
        {
            if ((await tx.ActiveMembershipsOfUserAsync(user, cancellationToken)).Count > 0)
                return Result.Failure(HouseholdErrors.AlreadyHasFamily);
            return await tx.TryCreateHouseholdAsync(household, user, time.GetUtcNow(), cancellationToken)
                ? Result.Success
                : Result.Failure(HouseholdErrors.IdTaken);
        }, cancellationToken);
}
```

Append to `InvitationUseCases.cs` (add `using Coo.Api.Application.Sync;` and `using Coo.Contracts.Sync;` at the top):

```csharp
public sealed record InvitationPreview(
    HouseholdId Household, string InviterName, IReadOnlyList<string> BabyNames, Role Role, DateTimeOffset ExpiresAt, bool AlreadyMember);

/// <summary>
/// <c>POST /api/invitations/preview</c> (spec §5.1 step 2). One answer, <c>invitation.invalid</c>, for unknown, expired,
/// used, withdrawn and malformed codes. A code this caller already used, while still a member, previews as already-member.
/// </summary>
public sealed class PreviewInvitation(IInvitationStore invitations, IMembershipStore memberships, TimeProvider time)
{
    public async Task<Result<InvitationPreview>> ExecuteAsync(UserId user, string? code, CancellationToken cancellationToken)
    {
        var parsed = InvitationCode.Parse(code);
        if (parsed.IsFailure || await invitations.FindByHashAsync(AuthSecrets.Hash(parsed.Value.Value), cancellationToken) is not { } found)
            return Result<InvitationPreview>.Failure(HouseholdErrors.InvitationInvalid);

        var invitation = found.Invitation;
        var me = await memberships.FindAsync(invitation.HouseholdId, user, cancellationToken);
        var active = me.Membership is not null && !me.IsRevoked;
        if (!invitation.IsUsable(time.GetUtcNow()) && !(active && invitation.WasAcceptedBy(user)))
            return Result<InvitationPreview>.Failure(HouseholdErrors.InvitationInvalid);

        var babies = await invitations.ListBabyNamesAsync(invitation.HouseholdId, cancellationToken);
        return new InvitationPreview(invitation.HouseholdId, found.InviterName, babies, invitation.Role, invitation.ExpiresAt, active);
    }
}

public enum AcceptOutcome
{
    Joined,
    Rejoined,
    AlreadyMember,
}

public sealed record AcceptResult(HouseholdId Household, Role Role, AcceptOutcome Outcome);

/// <summary>
/// <c>POST /api/invitations/accept</c> (spec §10.4): find by hash, lock the family, re-read the invitation, then decide.
/// No membership without consent (R4): <c>sync.consent-required</c> changes nothing.
/// </summary>
public sealed class AcceptInvitation(IInvitationStore invitations, IHouseholdMembershipStore store, IConsentStore consents, TimeProvider time)
{
    public async Task<Result<AcceptResult>> ExecuteAsync(UserId user, string? code, CancellationToken cancellationToken)
    {
        var parsed = InvitationCode.Parse(code);
        if (parsed.IsFailure)
            return Result<AcceptResult>.Failure(HouseholdErrors.InvitationInvalid);
        var hash = AuthSecrets.Hash(parsed.Value.Value);
        if (await invitations.FindByHashAsync(hash, cancellationToken) is not { } found)
            return Result<AcceptResult>.Failure(HouseholdErrors.InvitationInvalid);
        var household = found.Invitation.HouseholdId;
        var consented = await consents.FindAsync(user, CareDataConsent.Purpose, CareDataConsent.CurrentWording, cancellationToken) is not null;

        return await store.RunLockedAsync([household], async tx =>
        {
            if (!tx.IsLocked(household) || await tx.FindInvitationForUpdateAsync(hash, cancellationToken) is not { } invitation)
                return Result<AcceptResult>.Failure(HouseholdErrors.InvitationInvalid);

            var me = await tx.FindMembershipAsync(household, user, cancellationToken);
            var active = me.Membership is not null && !me.IsRevoked;
            if (active && invitation.WasAcceptedBy(user))
                return Result<AcceptResult>.Ok(new AcceptResult(household, me.Membership!.Role, AcceptOutcome.Joined));

            var now = time.GetUtcNow();
            if (!invitation.IsUsable(now))
                return Result<AcceptResult>.Failure(HouseholdErrors.InvitationInvalid);
            if (!consented)
                return Result<AcceptResult>.Failure(SyncErrors.ConsentRequired);
            if (active)
                return Result<AcceptResult>.Ok(new AcceptResult(household, me.Membership!.Role, AcceptOutcome.AlreadyMember));

            AcceptOutcome outcome;
            if (me.IsRevoked)
            {
                await tx.ReactivateMembershipAsync(household, user, invitation.Role, now, cancellationToken);
                outcome = AcceptOutcome.Rejoined;
            }
            else
            {
                await tx.AddMembershipAsync(Membership.Create(household, user, invitation.Role, now), cancellationToken);
                outcome = AcceptOutcome.Joined;
            }

            invitation.Accept(user, now);
            await tx.SaveInvitationAsync(invitation, cancellationToken);
            return Result<AcceptResult>.Ok(new AcceptResult(household, invitation.Role, outcome));
        }, cancellationToken);
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Coo.Api.Application.Tests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Backend/Coo.Api.Application/Households tests/Coo.Api.Application.Tests
git commit -m "Api.Application: leave, create a family, preview and accept an invitation (Spec 011b §5, §8.3, §8.4, §10.4)"
```

---

### Task 4: The `invitations` table and the stores (`Coo.Api.Infrastructure/Persistence`)

**Files:**
- Modify: `src/Backend/Coo.Api.Infrastructure/Persistence/Records.cs` (add `InvitationRecord`),
  `src/Backend/Coo.Api.Infrastructure/Persistence/CooDbContext.cs` (map it),
  `src/Backend/Coo.Api.Infrastructure/ServiceCollectionExtensions.cs` (register the stores)
- Create: `src/Backend/Coo.Api.Infrastructure/Persistence/Migrations/<timestamp>_MembersAndInvitations.cs` and its
  `.Designer.cs` (generated), the updated `CooDbContextModelSnapshot.cs` (generated),
  `src/Backend/Coo.Api.Infrastructure/Persistence/InvitationStore.cs`,
  `src/Backend/Coo.Api.Infrastructure/Persistence/HouseholdMembershipStore.cs`
- Test: `tests/Coo.Api.Infrastructure.Tests/MembersSchemaTests.cs`, `tests/Coo.Api.Infrastructure.Tests/HouseholdMembershipStoreTests.cs`,
  `tests/Coo.Api.Infrastructure.Tests/PostgresServer.cs` (expose the new stores)

**Interfaces:**
- Consumes: Task 2's ports; `RoleText`, `ServerColumns.Utc`, `MembershipLookup`, `Invitation.Rehydrate` (Domain internals
  are visible to `Coo.Api.Infrastructure`).
- Produces: `InvitationStore : IInvitationStore` and `HouseholdMembershipStore : IHouseholdMembershipStore` (both singletons
  over `NpgsqlDataSource`); `ServerDatabase.Invitations` and `ServerDatabase.HouseholdStore` in the tests.

- [ ] **Step 1: Write the failing tests**

In `tests/Coo.Api.Infrastructure.Tests/PostgresServer.cs` add to `ServerDatabase` (with `using Coo.Api.Application.Households;`):

```csharp
    public IInvitationStore Invitations => _services.GetRequiredService<IInvitationStore>();

    public IHouseholdMembershipStore HouseholdStore => _services.GetRequiredService<IHouseholdMembershipStore>();
```

`tests/Coo.Api.Infrastructure.Tests/MembersSchemaTests.cs`:

```csharp
using Npgsql;

namespace Coo.Api.Infrastructure.Tests;

/// <summary>Spec 011b §10.3: the invitations table and its guards.</summary>
[Collection(PostgresTests.Name)]
public sealed class MembersSchemaTests(PostgresServer server)
{
    [Fact]
    public async Task Migrate_EmptyDatabase_CreatesTheInvitationsTable()
    {
        // Arrange
        await using var db = await ServerDatabase.CreateAsync(server);

        // Act
        var columns = (string)(await db.ScalarAsync(
            "SELECT string_agg(column_name, ',' ORDER BY column_name) FROM information_schema.columns WHERE table_name = 'invitations';"))!;
        var indexes = (string)(await db.ScalarAsync(
            "SELECT string_agg(indexname, ',' ORDER BY indexname) FROM pg_indexes WHERE tablename = 'invitations';"))!;

        // Assert
        columns.Split(',').Should().BeEquivalentTo(
            "accepted_at", "accepted_by", "code_hash", "created_at", "created_by", "expires_at", "household_id", "id", "revoked_at", "role");
        indexes.Split(',').Should().Contain(["ix_invitations_code_hash", "ix_invitations_household_id"]);
    }

    [Fact]
    public async Task Insert_SameCodeHashTwice_IsRefused()
    {
        // Arrange
        await using var db = await ServerDatabase.CreateAsync(server);
        await SeedAsync(db);
        await db.ExecuteAsync(Insert(db.NewId(), "h-1"));

        // Act
        var act = async () => await db.ExecuteAsync(Insert(db.NewId(), "h-1"));

        // Assert
        (await act.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
    }

    [Fact]
    public async Task Insert_OwnerRole_IsRefusedByTheCheck()
    {
        // Arrange
        await using var db = await ServerDatabase.CreateAsync(server);
        await SeedAsync(db);

        // Act
        var act = async () => await db.ExecuteAsync(Insert(db.NewId(), "h-2", role: "owner"));

        // Assert
        (await act.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
    }

    [Fact]
    public async Task DeleteHousehold_WithInvitations_CascadesThem()
    {
        // Arrange
        await using var db = await ServerDatabase.CreateAsync(server);
        await SeedAsync(db);
        await db.ExecuteAsync(Insert(db.NewId(), "h-3"));

        // Act
        await db.ExecuteAsync($"DELETE FROM households WHERE id = '{ServerDatabase.Household.Value}';");

        // Assert
        (await db.ScalarAsync("SELECT count(*) FROM invitations;")).Should().Be(0L);
    }

    internal static readonly Guid Owner = Guid.Parse("7a1c0000-0000-7000-8000-00000000c0c1");

    internal static async Task SeedAsync(ServerDatabase db)
    {
        await db.ExecuteAsync(
            $"""
            INSERT INTO "AspNetUsers" ("Id", "UserName", "NormalizedUserName", "Email", "EmailConfirmed", "DisplayName", "PhoneNumberConfirmed",
                "TwoFactorEnabled", "LockoutEnabled", "AccessFailedCount")
            VALUES ('{Owner}', 'google:o', 'GOOGLE:O', 'o@example.com', true, 'Andrzej', false, false, false, 0);
            INSERT INTO households (id, next_seq, created_at, created_by) VALUES ('{ServerDatabase.Household.Value}', 1, now(), '{Owner}');
            INSERT INTO memberships (household_id, user_id, role, joined_at) VALUES ('{ServerDatabase.Household.Value}', '{Owner}', 'owner', now());
            """);
    }

    private static string Insert(Guid id, string hash, string role = "parent") =>
        $"""
        INSERT INTO invitations (id, household_id, role, code_hash, created_by, created_at, expires_at)
        VALUES ('{id}', '{ServerDatabase.Household.Value}', '{role}', '{hash}', '{Owner}', now(), now() + interval '72 hours');
        """;
}
```

`tests/Coo.Api.Infrastructure.Tests/HouseholdMembershipStoreTests.cs` (this task: the store's own semantics; Task 5
appends account deletion and the concurrency test):

```csharp
using Coo.Api.Application.Households;
using Coo.Domain.Common;
using Coo.Domain.Households;
using Npgsql;

namespace Coo.Api.Infrastructure.Tests;

/// <summary>Spec 011b §10.3–§10.4 against real PostgreSQL 17: the transaction, the locks, the reads.</summary>
[Collection(PostgresTests.Name)]
public sealed class HouseholdMembershipStoreTests(PostgresServer server)
{
    private static readonly HouseholdId Family = ServerDatabase.Household;
    private static readonly UserId Owner = new(MembersSchemaTests.Owner);
    private static readonly UserId Partner = new(Guid.Parse("7a1c0000-0000-7000-8000-00000000c0c2"));

    [Fact]
    public async Task RunLockedAsync_AddInvitationThenFindByHash_ReadsItBackWithTheInvitersName()
    {
        // Arrange
        await using var db = await ServerDatabase.CreateAsync(server);
        await MembersSchemaTests.SeedAsync(db);
        var now = db.Time.GetUtcNow();
        var invitation = Invitation.Create(new InvitationId(db.NewId()), Family, Role.Parent, Owner, now);

        // Act
        await db.HouseholdStore.RunLockedAsync([Family], async tx =>
        {
            await tx.AddInvitationAsync(invitation, "hash-a", CancellationToken.None);
            return 0;
        }, CancellationToken.None);
        var found = await db.Invitations.FindByHashAsync("hash-a", CancellationToken.None);

        // Assert
        found!.InviterName.Should().Be("Andrzej");
        found.Invitation.Id.Should().Be(invitation.Id);
        found.Invitation.ExpiresAt.Should().Be(now.AddHours(72));
        found.Invitation.IsUsable(now).Should().BeTrue();
    }

    [Fact]
    public async Task RunLockedAsync_WorkThrows_RollsEverythingBack()
    {
        // Arrange
        await using var db = await ServerDatabase.CreateAsync(server);
        await MembersSchemaTests.SeedAsync(db);
        var invitation = Invitation.Create(new InvitationId(db.NewId()), Family, Role.Parent, Owner, db.Time.GetUtcNow());

        // Act
        var act = async () => await db.HouseholdStore.RunLockedAsync<int>([Family], async tx =>
        {
            await tx.AddInvitationAsync(invitation, "hash-b", CancellationToken.None);
            throw new InvalidOperationException("boom");
        }, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
        (await db.ScalarAsync("SELECT count(*) FROM invitations;")).Should().Be(0L);
    }

    [Fact]
    public async Task RunLockedAsync_AMissingHousehold_IsNotLocked()
    {
        // Arrange
        await using var db = await ServerDatabase.CreateAsync(server);
        await MembersSchemaTests.SeedAsync(db);
        var missing = new HouseholdId(db.NewId());

        // Act
        var (family, gone) = await db.HouseholdStore.RunLockedAsync([missing, Family], tx =>
            Task.FromResult((tx.IsLocked(Family), tx.IsLocked(missing))), CancellationToken.None);

        // Assert
        family.Should().BeTrue();
        gone.Should().BeFalse();
    }

    [Fact]
    public async Task RunLockedAsync_HeldByAnotherTransaction_WaitsForIt()
    {
        // Arrange: two membership changes to one family serialise (spec §10.4).
        await using var db = await ServerDatabase.CreateAsync(server);
        await MembersSchemaTests.SeedAsync(db);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = db.HouseholdStore.RunLockedAsync([Family], async _ =>
        {
            entered.SetResult();
            await release.Task;
            return 1;
        }, CancellationToken.None);
        await entered.Task;

        // Act
        var second = db.HouseholdStore.RunLockedAsync([Family], _ => Task.FromResult(2), CancellationToken.None);
        await WaitForLockWaitersAsync(db, 1);
        var finishedEarly = second.IsCompleted;
        release.SetResult();

        // Assert
        finishedEarly.Should().BeFalse();
        (await first).Should().Be(1);
        (await second).Should().Be(2);
    }

    [Fact]
    public async Task Transaction_RevokeReactivateAndActiveReads_SeeOnlyActiveRows()
    {
        // Arrange
        await using var db = await ServerDatabase.CreateAsync(server);
        await MembersSchemaTests.SeedAsync(db);
        await AddUserAsync(db, Partner, "Kasia");
        var now = db.Time.GetUtcNow();

        // Act
        var (afterAdd, afterRevoke, lookup, afterReactivate) = await db.HouseholdStore.RunLockedAsync([Family], async tx =>
        {
            await tx.AddMembershipAsync(Membership.Create(Family, Partner, Role.Parent, now), CancellationToken.None);
            var added = (await tx.ActiveMembersAsync(Family, CancellationToken.None)).Count;
            await tx.RevokeMembershipAsync(Family, Partner, now.AddHours(1), CancellationToken.None);
            var revoked = (await tx.ActiveMembersAsync(Family, CancellationToken.None)).Count;
            var row = await tx.FindMembershipAsync(Family, Partner, CancellationToken.None);
            await tx.ReactivateMembershipAsync(Family, Partner, Role.Parent, now.AddHours(2), CancellationToken.None);
            var back = (await tx.ActiveMembershipsOfUserAsync(Partner, CancellationToken.None)).Single();
            return (added, revoked, row, back);
        }, CancellationToken.None);

        // Assert
        afterAdd.Should().Be(2);
        afterRevoke.Should().Be(1);
        lookup.IsRevoked.Should().BeTrue();
        afterReactivate.JoinedAt.Should().Be(now.AddHours(2));
        (await db.HouseholdStore.ListActiveMembersAsync(Family, CancellationToken.None)).Select(m => m.Name).Should().Equal("Andrzej", "Kasia");
    }

    [Fact]
    public async Task Transaction_WithdrawOpenInvitationsBy_LeavesOthersAndUsedOnes()
    {
        // Arrange
        await using var db = await ServerDatabase.CreateAsync(server);
        await MembersSchemaTests.SeedAsync(db);
        await AddUserAsync(db, Partner, "Kasia");
        var now = db.Time.GetUtcNow();
        var mine = Invitation.Create(new InvitationId(db.NewId()), Family, Role.Parent, Partner, now);
        var used = Invitation.Create(new InvitationId(db.NewId()), Family, Role.Parent, Partner, now);
        var theirs = Invitation.Create(new InvitationId(db.NewId()), Family, Role.Parent, Owner, now);
        used.Accept(Owner, now);

        // Act
        var withdrawn = await db.HouseholdStore.RunLockedAsync([Family], async tx =>
        {
            await tx.AddInvitationAsync(mine, "m", CancellationToken.None);
            await tx.AddInvitationAsync(used, "u", CancellationToken.None);
            await tx.SaveInvitationAsync(used, CancellationToken.None);
            await tx.AddInvitationAsync(theirs, "t", CancellationToken.None);
            return await tx.WithdrawOpenInvitationsByAsync(Family, Partner, now.AddMinutes(1), CancellationToken.None);
        }, CancellationToken.None);

        // Assert
        withdrawn.Should().Be(1);
        (await db.HouseholdStore.ListOpenInvitationsAsync(Family, now.AddMinutes(2), CancellationToken.None)).Should().ContainSingle()
            .Which.CreatedBy.Should().Be(Owner);
    }

    [Fact]
    public async Task Transaction_HoldsAnyRecord_CountsSettingsAndTombstones()
    {
        // Arrange
        await using var db = await ServerDatabase.CreateAsync(server);
        await MembersSchemaTests.SeedAsync(db);
        var before = await db.HouseholdStore.RunLockedAsync([Family], tx => tx.HoldsAnyRecordAsync(Family, CancellationToken.None), CancellationToken.None);
        await db.ExecuteAsync(
            $$"""
            INSERT INTO household_settings (household_id, volume_unit, time_zone_id, hidden_kinds, version_wall_ms, version_counter, version_device, is_deleted, seq)
            VALUES ('{{Family.Value}}', 'ml', 'Europe/Amsterdam', '{}', 1, 0, '{{ServerDatabase.Device.Value}}', true, 1);
            """);

        // Act
        var after = await db.HouseholdStore.RunLockedAsync([Family], tx => tx.HoldsAnyRecordAsync(Family, CancellationToken.None), CancellationToken.None);

        // Assert
        before.Should().BeFalse();
        after.Should().BeTrue();
    }

    [Fact]
    public async Task Transaction_TryCreateHouseholdWithATakenId_WritesNothing()
    {
        // Arrange
        await using var db = await ServerDatabase.CreateAsync(server);
        await MembersSchemaTests.SeedAsync(db);
        await AddUserAsync(db, Partner, "Kasia");

        // Act
        var created = await db.HouseholdStore.RunLockedAsync([], tx =>
            tx.TryCreateHouseholdAsync(Family, Partner, db.Time.GetUtcNow(), CancellationToken.None), CancellationToken.None);

        // Assert
        created.Should().BeFalse();
        (await db.ScalarAsync($"SELECT count(*) FROM memberships WHERE user_id = '{Partner.Value}';")).Should().Be(0L);
    }

    [Fact]
    public async Task Transaction_DeleteHousehold_TakesItsEntriesMembershipsAndInvitations()
    {
        // Arrange
        await using var db = await ServerDatabase.CreateAsync(server);
        await MembersSchemaTests.SeedAsync(db);
        var now = db.Time.GetUtcNow();
        await db.Store.SaveAsync(Domain.CareLog.CareEntry.Create(new Domain.Common.EntryId(db.NewId()), Family,
            Domain.CareLog.EntrySubject.Baby(ServerDatabase.Baby), new Domain.CareLog.Kinds.Diaper.DiaperDetails(Domain.CareLog.Kinds.Diaper.DiaperContents.Wet),
            Domain.CareLog.EntryTiming.Point(now), Domain.CareLog.PersonRef.User(Owner), Domain.CareLog.EntrySource.Manual, db.NextStamp(), now).Value,
            CancellationToken.None);

        // Act
        await db.HouseholdStore.RunLockedAsync([Family], async tx =>
        {
            await tx.AddInvitationAsync(Invitation.Create(new InvitationId(db.NewId()), Family, Role.Parent, Owner, now), "d", CancellationToken.None);
            await tx.DeleteHouseholdAsync(Family, CancellationToken.None);
            return 0;
        }, CancellationToken.None);

        // Assert
        (await db.ScalarAsync(
            "SELECT (SELECT count(*) FROM households) + (SELECT count(*) FROM care_entries) + (SELECT count(*) FROM memberships) + (SELECT count(*) FROM invitations);"))
            .Should().Be(0L);
    }

    [Fact]
    public async Task ListBabyNamesAsync_LiveAndDeletedProfiles_ListsTheLiveOnes()
    {
        // Arrange
        await using var db = await ServerDatabase.CreateAsync(server);
        await MembersSchemaTests.SeedAsync(db);
        await db.ExecuteAsync(
            $$"""
            INSERT INTO baby_profiles (id, household_id, name, nicknames, birth_date, version_wall_ms, version_counter, version_device, is_deleted, seq)
            VALUES ('{{db.NewId()}}', '{{Family.Value}}', 'Maja', '{}', '2026-08-20', 1, 0, '{{ServerDatabase.Device.Value}}', false, 1),
                   ('{{db.NewId()}}', '{{Family.Value}}', 'Old', '{}', '2026-08-20', 1, 0, '{{ServerDatabase.Device.Value}}', true, 2);
            """);

        // Act
        var names = await db.Invitations.ListBabyNamesAsync(Family, CancellationToken.None);

        // Assert
        names.Should().Equal("Maja");
    }

    internal static async Task AddUserAsync(ServerDatabase db, UserId user, string name) => await db.ExecuteAsync(
        $"""
        INSERT INTO "AspNetUsers" ("Id", "UserName", "NormalizedUserName", "Email", "EmailConfirmed", "DisplayName", "PhoneNumberConfirmed",
            "TwoFactorEnabled", "LockoutEnabled", "AccessFailedCount")
        VALUES ('{user.Value}', 'google:{name}', 'GOOGLE:{name.ToUpperInvariant()}', '{name.ToLowerInvariant()}@example.com', true, '{name}', false, false, false, 0);
        """);

    /// <summary>Waits on PostgreSQL's own state, not on time.</summary>
    internal static async Task WaitForLockWaitersAsync(ServerDatabase db, int count)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while ((long)(await db.ScalarAsync("SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock';"))! < count)
            deadline.Token.ThrowIfCancellationRequested();
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run (Docker Desktop running): `dotnet test tests/Coo.Api.Infrastructure.Tests --filter "FullyQualifiedName~MembersSchemaTests|FullyQualifiedName~HouseholdMembershipStoreTests"`
Expected: FAIL: `ServerDatabase.Invitations` and `HouseholdStore` resolve no registered service (the stores do not exist
yet), or the migration test finds no `invitations` table.

- [ ] **Step 3: The record, the mapping and the migration**

Append to `Records.cs`:

```csharp
/// <summary><c>invitations</c> (Spec 011b §10.3): only the code's SHA-256 is kept; rows go a day after they expire.</summary>
internal sealed class InvitationRecord
{
    public required Guid Id { get; init; }

    public required Guid HouseholdId { get; init; }

    public required string Role { get; init; }

    public required string CodeHash { get; init; }

    public required Guid CreatedBy { get; init; }

    public required DateTime CreatedAt { get; init; }

    public required DateTime ExpiresAt { get; init; }

    public Guid? AcceptedBy { get; init; }

    public DateTime? AcceptedAt { get; init; }

    public DateTime? RevokedAt { get; init; }
}
```

In `CooDbContext` add `public DbSet<InvitationRecord> Invitations => Set<InvitationRecord>();` and at the end of
`OnModelCreating`:

```csharp
        modelBuilder.Entity<InvitationRecord>(e =>
        {
            e.ToTable("invitations", t => t.HasCheckConstraint("ck_invitations_role", "role IN ('parent', 'caregiver')"));
            e.HasKey(i => i.Id);
            e.Property(i => i.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(i => i.HouseholdId).HasColumnName("household_id");
            e.Property(i => i.Role).HasColumnName("role");
            e.Property(i => i.CodeHash).HasColumnName("code_hash");
            e.Property(i => i.CreatedBy).HasColumnName("created_by");
            e.Property(i => i.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            e.Property(i => i.ExpiresAt).HasColumnName("expires_at").HasColumnType("timestamptz");
            e.Property(i => i.AcceptedBy).HasColumnName("accepted_by");
            e.Property(i => i.AcceptedAt).HasColumnName("accepted_at").HasColumnType("timestamptz");
            e.Property(i => i.RevokedAt).HasColumnName("revoked_at").HasColumnType("timestamptz");
            e.HasOne<HouseholdRecord>().WithMany().HasForeignKey(i => i.HouseholdId)
                .HasConstraintName("fk_invitations_households").OnDelete(DeleteBehavior.Cascade);
            e.HasOne<CooUser>().WithMany().HasForeignKey(i => i.CreatedBy)
                .HasConstraintName("fk_invitations_created_by").OnDelete(DeleteBehavior.Cascade);
            e.HasOne<CooUser>().WithMany().HasForeignKey(i => i.AcceptedBy)
                .HasConstraintName("fk_invitations_accepted_by").OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(i => i.CodeHash).IsUnique().HasDatabaseName("ix_invitations_code_hash");
            e.HasIndex(i => i.HouseholdId).HasDatabaseName("ix_invitations_household_id");
            e.HasIndex(i => i.ExpiresAt).HasDatabaseName("ix_invitations_expires_at");
        });
```

(`memberships` already has `ix_memberships_user` on `user_id`, so spec §10.3's "an index on `user_id` if absent" needs
nothing.)

Generate the migration (`BUILD.md`, "A new migration"):

```sh
dotnet tool restore
dotnet tool run dotnet-ef migrations add MembersAndInvitations --project src/Backend/Coo.Api.Infrastructure \
  --startup-project src/Backend/Coo.Api.Infrastructure --output-dir Persistence/Migrations \
  --namespace Coo.Api.Infrastructure.Persistence.Migrations
```

Then: make the generated `MembersAndInvitations` and its designer class `internal`; move the regenerated
`CooDbContextModelSnapshot.cs` from `Coo/Api/...` back into `Persistence/Migrations` (replacing the old one, also
`internal`). Read the generated `Up`: it must create only `invitations` with its three indexes, its check and its three
foreign keys, and nothing else. If it touches any other table, the model and the snapshot disagree: stop and fix the model.

- [ ] **Step 4: The stores**

`src/Backend/Coo.Api.Infrastructure/Persistence/InvitationStore.cs`:

```csharp
using Coo.Api.Application.Households;
using Coo.Domain.Common;
using Coo.Domain.Households;
using Npgsql;

namespace Coo.Api.Infrastructure.Persistence;

/// <summary><c>invitations</c> outside a transaction (Spec 011b §10.3): the preview's reads and the daily purge.</summary>
internal sealed class InvitationStore(NpgsqlDataSource dataSource) : IInvitationStore
{
    internal const string Columns = "i.id, i.household_id, i.role, i.created_by, i.created_at, i.expires_at, i.accepted_by, i.accepted_at, i.revoked_at";

    public async Task<FoundInvitation?> FindByHashAsync(string codeHash, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            $"""SELECT {Columns}, u."DisplayName" FROM invitations i JOIN "AspNetUsers" u ON u."Id" = i.created_by WHERE i.code_hash = @hash;""");
        command.Parameters.AddWithValue("hash", codeHash);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? new FoundInvitation(Read(reader), reader.GetString(9)) : null;
    }

    public async Task<IReadOnlyList<string>> ListBabyNamesAsync(HouseholdId household, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT name FROM baby_profiles WHERE household_id = @h AND NOT is_deleted ORDER BY coalesce(birth_date, due_date), name;");
        command.Parameters.AddWithValue("h", household.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var names = new List<string>();
        while (await reader.ReadAsync(cancellationToken))
            names.Add(reader.GetString(0));
        return names;
    }

    public async Task<int> PurgeExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("DELETE FROM invitations WHERE expires_at < @now;");
        command.Parameters.AddWithValue("now", now.UtcDateTime);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>The first nine columns of <see cref="Columns"/>, in order.</summary>
    internal static Invitation Read(NpgsqlDataReader reader) => Invitation.Rehydrate(
        new InvitationId(reader.GetGuid(0)), new HouseholdId(reader.GetGuid(1)), RoleText.Parse(reader.GetString(2)), new UserId(reader.GetGuid(3)),
        ServerColumns.Utc(reader.GetDateTime(4)), ServerColumns.Utc(reader.GetDateTime(5)),
        reader.IsDBNull(6) ? null : new UserId(reader.GetGuid(6)),
        reader.IsDBNull(7) ? null : ServerColumns.Utc(reader.GetDateTime(7)),
        reader.IsDBNull(8) ? null : ServerColumns.Utc(reader.GetDateTime(8)));
}
```

`src/Backend/Coo.Api.Infrastructure/Persistence/HouseholdMembershipStore.cs`:

```csharp
using Coo.Api.Application.Households;
using Coo.Api.Application.Sync;
using Coo.Domain.Common;
using Coo.Domain.Households;
using Npgsql;
using NpgsqlTypes;

namespace Coo.Api.Infrastructure.Persistence;

/// <summary>
/// Memberships and invitations (Spec 011b §10.3–§10.4). Every change runs in one transaction that first locks the
/// households' rows (<c>FOR UPDATE</c>, ascending ID order: PostgreSQL's <c>uuid</c> order is <see cref="GuidOrder"/>'s),
/// so two changes to one family serialise, and with a sync apply too (it locks the same counter row).
/// </summary>
internal sealed class HouseholdMembershipStore(NpgsqlDataSource dataSource) : IHouseholdMembershipStore
{
    public async Task<T> RunLockedAsync<T>(IReadOnlyCollection<HouseholdId> households, Func<IMembershipTransaction, Task<T>> work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(households);
        ArgumentNullException.ThrowIfNull(work);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var locked = new HashSet<HouseholdId>();
        if (households.Count > 0)
        {
            await using var command = new NpgsqlCommand("SELECT id FROM households WHERE id = ANY(@ids) ORDER BY id FOR UPDATE;", connection, transaction);
            command.Parameters.AddWithValue("ids", households.Select(h => h.Value).Distinct().ToArray());
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                locked.Add(new HouseholdId(reader.GetGuid(0)));
        }

        var result = await work(new Transaction(connection, transaction, locked));
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<IReadOnlyList<MemberProfile>> ListActiveMembersAsync(HouseholdId household, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT m.user_id, u."DisplayName", coalesce(u."Email", ''), m.role, m.joined_at
            FROM memberships m JOIN "AspNetUsers" u ON u."Id" = m.user_id
            WHERE m.household_id = @h AND m.revoked_at IS NULL ORDER BY m.joined_at, m.user_id;
            """);
        command.Parameters.AddWithValue("h", household.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var members = new List<MemberProfile>();
        while (await reader.ReadAsync(cancellationToken))
        {
            members.Add(new MemberProfile(new UserId(reader.GetGuid(0)), reader.GetString(1), reader.GetString(2),
                RoleText.Parse(reader.GetString(3)), ServerColumns.Utc(reader.GetDateTime(4))));
        }

        return members;
    }

    public async Task<IReadOnlyList<OpenInvitation>> ListOpenInvitationsAsync(HouseholdId household, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT id, role, created_by, created_at, expires_at FROM invitations
            WHERE household_id = @h AND accepted_at IS NULL AND revoked_at IS NULL AND expires_at > @now ORDER BY created_at, id;
            """);
        command.Parameters.AddWithValue("h", household.Value);
        command.Parameters.AddWithValue("now", now.UtcDateTime);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var open = new List<OpenInvitation>();
        while (await reader.ReadAsync(cancellationToken))
        {
            open.Add(new OpenInvitation(new InvitationId(reader.GetGuid(0)), RoleText.Parse(reader.GetString(1)), new UserId(reader.GetGuid(2)),
                ServerColumns.Utc(reader.GetDateTime(3)), ServerColumns.Utc(reader.GetDateTime(4))));
        }

        return open;
    }

    public async Task<IReadOnlyList<Membership>> ListActiveMembershipsOfUserAsync(UserId user, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await ReadMembershipsAsync(connection, null, "user_id = @u AND revoked_at IS NULL", ("u", user.Value), cancellationToken);
    }

    internal static async Task<IReadOnlyList<Membership>> ReadMembershipsAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, string where, (string Name, Guid Value) parameter, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"SELECT household_id, user_id, role, joined_at FROM memberships WHERE {where} ORDER BY joined_at, user_id;", connection, transaction);
        command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var list = new List<Membership>();
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(Membership.Create(new HouseholdId(reader.GetGuid(0)), new UserId(reader.GetGuid(1)), RoleText.Parse(reader.GetString(2)),
                ServerColumns.Utc(reader.GetDateTime(3))));
        }

        return list;
    }

    private static string RoleName(Role role) => role switch
    {
        Role.Owner => RoleText.Owner,
        Role.Parent => RoleText.Parent,
        Role.Caregiver => RoleText.Caregiver,
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown role."),
    };

    private sealed class Transaction(NpgsqlConnection connection, NpgsqlTransaction transaction, HashSet<HouseholdId> locked) : IMembershipTransaction
    {
        public bool IsLocked(HouseholdId household) => locked.Contains(household);

        public async Task<MembershipLookup> FindMembershipAsync(HouseholdId household, UserId user, CancellationToken cancellationToken)
        {
            await using var command = Command("SELECT role, joined_at, revoked_at FROM memberships WHERE household_id = @h AND user_id = @u FOR UPDATE;");
            command.Parameters.AddWithValue("h", household.Value);
            command.Parameters.AddWithValue("u", user.Value);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                return MembershipLookup.None;
            var membership = Membership.Create(household, user, RoleText.Parse(reader.GetString(0)), ServerColumns.Utc(reader.GetDateTime(1)));
            return new MembershipLookup(membership, IsRevoked: !reader.IsDBNull(2));
        }

        public Task<IReadOnlyList<Membership>> ActiveMembersAsync(HouseholdId household, CancellationToken cancellationToken) =>
            ReadMembershipsAsync(connection, transaction, "household_id = @h AND revoked_at IS NULL", ("h", household.Value), cancellationToken);

        public Task<IReadOnlyList<Membership>> ActiveMembershipsOfUserAsync(UserId user, CancellationToken cancellationToken) =>
            ReadMembershipsAsync(connection, transaction, "user_id = @u AND revoked_at IS NULL", ("u", user.Value), cancellationToken);

        public async Task<Invitation?> FindInvitationForUpdateAsync(string codeHash, CancellationToken cancellationToken)
        {
            await using var command = Command($"SELECT {InvitationStore.Columns} FROM invitations i WHERE i.code_hash = @hash FOR UPDATE;");
            command.Parameters.AddWithValue("hash", codeHash);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken) ? InvitationStore.Read(reader) : null;
        }

        public async Task<Invitation?> FindInvitationForUpdateAsync(HouseholdId household, InvitationId id, CancellationToken cancellationToken)
        {
            await using var command = Command($"SELECT {InvitationStore.Columns} FROM invitations i WHERE i.id = @id AND i.household_id = @h FOR UPDATE;");
            command.Parameters.AddWithValue("id", id.Value);
            command.Parameters.AddWithValue("h", household.Value);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken) ? InvitationStore.Read(reader) : null;
        }

        public async Task<int> CountOpenInvitationsAsync(HouseholdId household, DateTimeOffset now, CancellationToken cancellationToken)
        {
            await using var command = Command(
                "SELECT count(*) FROM invitations WHERE household_id = @h AND accepted_at IS NULL AND revoked_at IS NULL AND expires_at > @now;");
            command.Parameters.AddWithValue("h", household.Value);
            command.Parameters.AddWithValue("now", now.UtcDateTime);
            return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
        }

        public async Task AddInvitationAsync(Invitation invitation, string codeHash, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(invitation);
            await using var command = Command(
                """
                INSERT INTO invitations (id, household_id, role, code_hash, created_by, created_at, expires_at)
                VALUES (@id, @h, @role, @hash, @by, @at, @expires);
                """);
            command.Parameters.AddWithValue("id", invitation.Id.Value);
            command.Parameters.AddWithValue("h", invitation.HouseholdId.Value);
            command.Parameters.AddWithValue("role", RoleName(invitation.Role));
            command.Parameters.AddWithValue("hash", codeHash);
            command.Parameters.AddWithValue("by", invitation.CreatedBy.Value);
            command.Parameters.AddWithValue("at", invitation.CreatedAt.UtcDateTime);
            command.Parameters.AddWithValue("expires", invitation.ExpiresAt.UtcDateTime);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        public async Task SaveInvitationAsync(Invitation invitation, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(invitation);
            await using var command = Command("UPDATE invitations SET accepted_by = @by, accepted_at = @at, revoked_at = @revoked WHERE id = @id;");
            command.Parameters.AddWithValue("id", invitation.Id.Value);
            command.Parameters.AddWithValue("by", NpgsqlDbType.Uuid, (object?)invitation.AcceptedBy?.Value ?? DBNull.Value);
            command.Parameters.AddWithValue("at", NpgsqlDbType.TimestampTz, (object?)invitation.AcceptedAt?.UtcDateTime ?? DBNull.Value);
            command.Parameters.AddWithValue("revoked", NpgsqlDbType.TimestampTz, (object?)invitation.RevokedAt?.UtcDateTime ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        public async Task AddMembershipAsync(Membership membership, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(membership);
            await using var command = Command("INSERT INTO memberships (household_id, user_id, role, joined_at) VALUES (@h, @u, @role, @at);");
            command.Parameters.AddWithValue("h", membership.HouseholdId.Value);
            command.Parameters.AddWithValue("u", membership.UserId.Value);
            command.Parameters.AddWithValue("role", RoleName(membership.Role));
            command.Parameters.AddWithValue("at", membership.JoinedAt.UtcDateTime);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        public async Task ReactivateMembershipAsync(HouseholdId household, UserId user, Role role, DateTimeOffset joinedAt, CancellationToken cancellationToken)
        {
            await using var command = Command(
                "UPDATE memberships SET revoked_at = NULL, role = @role, joined_at = @at WHERE household_id = @h AND user_id = @u;");
            command.Parameters.AddWithValue("h", household.Value);
            command.Parameters.AddWithValue("u", user.Value);
            command.Parameters.AddWithValue("role", RoleName(role));
            command.Parameters.AddWithValue("at", joinedAt.UtcDateTime);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        public async Task RevokeMembershipAsync(HouseholdId household, UserId user, DateTimeOffset now, CancellationToken cancellationToken)
        {
            await using var command = Command(
                "UPDATE memberships SET revoked_at = @now WHERE household_id = @h AND user_id = @u AND revoked_at IS NULL;");
            command.Parameters.AddWithValue("h", household.Value);
            command.Parameters.AddWithValue("u", user.Value);
            command.Parameters.AddWithValue("now", now.UtcDateTime);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        public async Task<int> WithdrawOpenInvitationsByAsync(HouseholdId household, UserId createdBy, DateTimeOffset now, CancellationToken cancellationToken)
        {
            await using var command = Command(
                "UPDATE invitations SET revoked_at = @now WHERE household_id = @h AND created_by = @u AND accepted_at IS NULL AND revoked_at IS NULL;");
            command.Parameters.AddWithValue("h", household.Value);
            command.Parameters.AddWithValue("u", createdBy.Value);
            command.Parameters.AddWithValue("now", now.UtcDateTime);
            return await command.ExecuteNonQueryAsync(cancellationToken);
        }

        public async Task<int> PromoteToOwnerAsync(HouseholdId household, UserId user, CancellationToken cancellationToken)
        {
            await using var command = Command(
                "UPDATE memberships SET role = 'owner' WHERE household_id = @h AND user_id = @u AND revoked_at IS NULL;");
            command.Parameters.AddWithValue("h", household.Value);
            command.Parameters.AddWithValue("u", user.Value);
            return await command.ExecuteNonQueryAsync(cancellationToken);
        }

        public async Task<bool> HoldsAnyRecordAsync(HouseholdId household, CancellationToken cancellationToken)
        {
            await using var command = Command(
                """
                SELECT EXISTS (SELECT 1 FROM care_entries WHERE household_id = @h) OR EXISTS (SELECT 1 FROM baby_profiles WHERE household_id = @h)
                    OR EXISTS (SELECT 1 FROM household_settings WHERE household_id = @h) OR EXISTS (SELECT 1 FROM duplicate_dismissals WHERE household_id = @h);
                """);
            command.Parameters.AddWithValue("h", household.Value);
            return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
        }

        public async Task<bool> TryCreateHouseholdAsync(HouseholdId household, UserId owner, DateTimeOffset now, CancellationToken cancellationToken)
        {
            await using var insert = Command(
                "INSERT INTO households (id, next_seq, created_at, created_by) VALUES (@h, 1, @now, @u) ON CONFLICT (id) DO NOTHING;");
            insert.Parameters.AddWithValue("h", household.Value);
            insert.Parameters.AddWithValue("now", now.UtcDateTime);
            insert.Parameters.AddWithValue("u", owner.Value);
            if (await insert.ExecuteNonQueryAsync(cancellationToken) == 0)
                return false;
            await AddMembershipAsync(Membership.Create(household, owner, Role.Owner, now), cancellationToken);
            return true;
        }

        public async Task DeleteHouseholdAsync(HouseholdId household, CancellationToken cancellationToken)
        {
            // care_entries restricts the household's deletion (Spec 008 §4); everything else cascades.
            await using var entries = Command("DELETE FROM care_entries WHERE household_id = @h;");
            entries.Parameters.AddWithValue("h", household.Value);
            await entries.ExecuteNonQueryAsync(cancellationToken);
            await using var row = Command("DELETE FROM households WHERE id = @h;");
            row.Parameters.AddWithValue("h", household.Value);
            await row.ExecuteNonQueryAsync(cancellationToken);
        }

        public async Task DeleteMembershipsOfUserAsync(UserId user, CancellationToken cancellationToken)
        {
            await using var command = Command("DELETE FROM memberships WHERE user_id = @u;");
            command.Parameters.AddWithValue("u", user.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        public async Task DeleteUserAsync(UserId user, CancellationToken cancellationToken)
        {
            // Logins, claims, tokens, device sessions, consents and invitations sent go with the user (ON DELETE CASCADE);
            // households.created_by and invitations.accepted_by become NULL.
            await using var command = Command("""DELETE FROM "AspNetUsers" WHERE "Id" = @u;""");
            command.Parameters.AddWithValue("u", user.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        private NpgsqlCommand Command(string sql) => new(sql, connection, transaction);
    }
}
```

Register both in `ServiceCollectionExtensions.AddApiInfrastructure`, after the sync stores:

```csharp
        // Members and invitations (Spec 011b §10): singletons over the data source, like the sync stores.
        services.AddSingleton<IInvitationStore, InvitationStore>();
        services.AddSingleton<IHouseholdMembershipStore, HouseholdMembershipStore>();
```

(add `using Coo.Api.Application.Households;`).

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/Coo.Api.Infrastructure.Tests --filter "FullyQualifiedName~MembersSchemaTests|FullyQualifiedName~HouseholdMembershipStoreTests"`
Expected: PASS, and the rest of `Coo.Tests.slnf` stays green (nothing else changed).

- [ ] **Step 6: Commit**

```bash
git add src/Backend/Coo.Api.Infrastructure tests/Coo.Api.Infrastructure.Tests/MembersSchemaTests.cs \
  tests/Coo.Api.Infrastructure.Tests/HouseholdMembershipStoreTests.cs tests/Coo.Api.Infrastructure.Tests/PostgresServer.cs
git commit -m "Api.Infrastructure: the invitations table and the locked membership store (Spec 011b §10.3, §10.4)"
```

---

### Task 5: Active memberships everywhere, and account deletion decided under the locks (Spec 010 §10.6 carries)

**Files:**
- Modify: `src/Backend/Coo.Api.Application/Auth/AccountUseCases.cs`, `src/Backend/Coo.Api.Application/Auth/Ports.cs`
  (remove `OwnershipTransfer`, `AccountDeletionPlan`, `IAccountStore.DeleteAccountAsync`),
  `src/Backend/Coo.Api.Application/Auth/AccountStoreExtensions.cs` (doc: active only),
  `src/Backend/Coo.Api.Application/Sync/UseCases.cs` (`PurgeSyncHistory` purges invitations too),
  `src/Backend/Coo.Api.Infrastructure/Persistence/AccountStore.cs` (active-only reads; remove `DeleteAccountAsync`)
- Test: `tests/Coo.Api.Application.Tests/AccountTests.cs`, `tests/Coo.Api.Application.Tests/Fakes.cs`,
  `tests/Coo.Api.Infrastructure.Tests/AuthStoreTests.cs`, `tests/Coo.Api.Infrastructure.Tests/SyncStoreTests.cs`,
  `tests/Coo.Api.Infrastructure.Tests/HouseholdMembershipStoreTests.cs` (append), `tests/Coo.Api.IntegrationTests/SyncHistoryCleanupJobTests.cs`

**Interfaces:**
- Consumes: `IHouseholdMembershipStore`, `IMembershipTransaction`, `FakeHouseholds` (Task 2), Task 4's stores,
  `AcceptInvitation` (Task 3), `HouseholdSuccession`.
- Produces:
  - `AccountOverview(UserAccount User, Membership? Membership, IReadOnlyList<DeviceSession> Devices, ConsentRecord? Consent = null, IReadOnlyList<Membership>? Memberships = null)`
    — `Memberships` is the active ones, earliest joined first; `Membership` the first of them.
  - `GetMe(IAccountStore accounts, IDeviceSessionStore sessions, IConsentStore consents)` unchanged signature (its reads are
    active-only now).
  - `DeleteAccount(IHouseholdMembershipStore store)` — `Task<Result> ExecuteAsync(UserId user, bool confirmed, CancellationToken)`.
  - `PurgeSyncHistory(ISyncStore store, IInvitationStore invitations, TimeProvider time)`.
  - `IAccountStore.ListMembershipsOfUserAsync`/`ListMembersAsync` return active rows only (sign-in, `GetMe`, Spec 011b §10.3).

- [ ] **Step 1: Write the failing tests**


In `tests/Coo.Api.Application.Tests/Fakes.cs`: remove `FakeAccounts.Deleted` and `FakeAccounts.DeleteAccountAsync`, and add
to `AuthKit`:

```csharp
    public FakeHouseholds Households { get; } = new();
```

and change `DeleteAccount()` to `public DeleteAccount DeleteAccount() => new(Households);`. `FakeAccounts.ListMembershipsOfUserAsync`
stays (sign-in and `GetMe` read it).

Replace the three deletion tests in `tests/Coo.Api.Application.Tests/AccountTests.cs`
(`ExecuteAsync_NotConfirmed_DeletesNothing`, `ExecuteAsync_SoleOwner_DeletesTheUserAndTheHousehold`,
`ExecuteAsync_OwnerWithAnotherParent_PassesOwnershipAndKeepsTheHousehold`) with:

```csharp
    private static readonly UserId Me = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000c1"));
    private static readonly UserId Partner = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000c2"));

    [Fact]
    public async Task ExecuteAsync_NotConfirmed_DeletesNothing()
    {
        // Arrange
        _kit.Households.AddFamily(Household, Me, AuthKit.Start);

        // Act
        var result = await _kit.DeleteAccount().ExecuteAsync(Me, confirmed: false, CancellationToken.None);

        // Assert
        result.Error!.Code.Should().Be(AuthErrors.ConfirmationRequired);
        _kit.Households.DeletedUsers.Should().BeEmpty();
        _kit.Households.Households.Should().Contain(Household);
    }

    [Fact]
    public async Task ExecuteAsync_SoleOwner_DeletesTheUserAndTheHouseholdUnderItsLock()
    {
        // Arrange
        _kit.Households.AddFamily(Household, Me, AuthKit.Start);

        // Act
        var result = await _kit.DeleteAccount().ExecuteAsync(Me, confirmed: true, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        _kit.Households.LockedSets.Should().ContainSingle().Which.Should().Equal(Household);
        _kit.Households.Households.Should().NotContain(Household);
        _kit.Households.DeletedUsers.Should().Equal(Me);
        _kit.Households.Members.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_ParentJoinsBetweenTheReadAndTheLock_OwnershipPassesToTheJoiner()
    {
        // Arrange: the plan is decided from reads inside the transaction (spec §10.4): a concurrent accept that commits first counts.
        _kit.Households.AddFamily(Household, Me, AuthKit.Start);
        _kit.Households.InsideNextTransaction = () => _kit.Households.AddMember(Household, Partner, Role.Parent, AuthKit.Start.AddDays(1));

        // Act
        await _kit.DeleteAccount().ExecuteAsync(Me, confirmed: true, CancellationToken.None);

        // Assert
        _kit.Households.Households.Should().Contain(Household);
        _kit.Households.Members.Should().ContainSingle().Which.Should().Match<FakeHouseholds.MemberRow>(m => m.User == Partner && m.Role == Role.Owner);
    }

    [Fact]
    public async Task ExecuteAsync_PromotionTouchesNoRow_ThrowsAndRollsBackEverything()
    {
        // Arrange: spec §10.4's row-count check; the phone's retry runs again from fresh reads.
        _kit.Households.AddFamily(Household, Me, AuthKit.Start);
        _kit.Households.AddMember(Household, Partner, Role.Parent, AuthKit.Start.AddDays(1));
        _kit.Households.PromotionTouchesNothing = true;

        // Act
        Func<Task> act = async () => await _kit.DeleteAccount().ExecuteAsync(Me, confirmed: true, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
        _kit.Households.DeletedUsers.Should().BeEmpty();
        _kit.Households.Members.Should().HaveCount(2);
    }

    [Fact]
    public async Task ExecuteAsync_RemovedFromAFamilyBefore_OnlyActiveFamiliesAreDecided()
    {
        // Arrange: a revoked row does not make Me "a parent" of that family.
        _kit.Households.AddFamily(Other, Partner, AuthKit.Start);
        _kit.Households.Members.Add(new FakeHouseholds.MemberRow(Other, Me, Role.Parent, AuthKit.Start, AuthKit.Start.AddDays(1)));
        _kit.Households.AddFamily(Household, Me, AuthKit.Start);

        // Act
        await _kit.DeleteAccount().ExecuteAsync(Me, confirmed: true, CancellationToken.None);

        // Assert
        _kit.Households.Households.Should().Equal(Other);
        _kit.Households.Members.Should().ContainSingle(m => m.User == Partner && m.Role == Role.Owner);
    }

    [Fact]
    public async Task ExecuteAsync_MeWithTwoFamilies_ListsBothActiveMembershipsEarliestFirst()
    {
        // Arrange
        var signedIn = await _kit.SignInAsync("google-1", PhoneA, Household);
        _kit.Accounts.Memberships.Add(Membership.Create(Other, signedIn.User.Id, Role.Parent, AuthKit.Start.AddDays(2)));

        // Act
        var me = await _kit.GetMe().ExecuteAsync(signedIn.User.Id, CancellationToken.None);

        // Assert
        me!.Memberships!.Select(m => m.HouseholdId).Should().Equal(Household, Other);
        me.Membership!.HouseholdId.Should().Be(Household);
    }
```

(Keep `using Coo.Domain.Households;` at the top; `FakeHouseholds.MemberRow` is `internal sealed record` in Task 2.)



Replace `DeleteAccountAsync_SoleOwner_RemovesTheUserLoginsSessionsMembershipHouseholdAndCareEntries` and
`DeleteAccountAsync_OwnershipTransfer_PromotesThePartnerAndKeepsTheHousehold` in `AuthStoreTests.cs` with the same
scenarios through the use case (keep their arrange code):

```csharp
    [Fact]
    public async Task ExecuteAsync_SoleOwner_RemovesTheUserLoginsSessionsMembershipHouseholdAndCareEntries()
    {
        // Arrange (as before: account, session, one diaper)
        await using var db = await ServerDatabase.CreateAsync(server);
        await CreateAccountAsync(db, User, "sub-1", ServerDatabase.Household);
        await db.Sessions.CreateAsync(Session(User, Phone), "h1", CancellationToken.None);
        var now = db.Time.GetUtcNow();
        var entry = CareEntry.Create(new EntryId(db.NewId()), ServerDatabase.Household, EntrySubject.Baby(ServerDatabase.Baby),
            new DiaperDetails(DiaperContents.Wet), EntryTiming.Point(now), PersonRef.ThisDevicesPerson, EntrySource.Manual,
            db.NextStamp(), now).Value;
        await db.Store.SaveAsync(entry, CancellationToken.None);

        // Act
        await new DeleteAccount(db.HouseholdStore).ExecuteAsync(User, confirmed: true, CancellationToken.None);

        // Assert
        foreach (var table in new[] { "\"AspNetUsers\"", "\"AspNetUserLogins\"", "device_sessions", "memberships", "households", "care_entries" })
            (await db.ScalarAsync($"SELECT count(*) FROM {table};")).Should().Be(0L, $"{table} must be empty");
    }

    [Fact]
    public async Task ExecuteAsync_OwnerWithAPartner_PromotesThePartnerAndKeepsTheHousehold()
    {
        // Arrange
        await using var db = await ServerDatabase.CreateAsync(server);
        await CreateAccountAsync(db, User, "sub-1", ServerDatabase.Household);
        await CreateAccountAsync(db, Partner, "sub-2", PartnersHousehold);
        await db.ExecuteAsync(
            $"INSERT INTO memberships (household_id, user_id, role, joined_at) VALUES ('{ServerDatabase.Household.Value}', '{Partner.Value}', 'parent', now());");

        // Act
        await new DeleteAccount(db.HouseholdStore).ExecuteAsync(User, confirmed: true, CancellationToken.None);

        // Assert
        (await db.ScalarAsync($"SELECT string_agg(user_id::text || ':' || role, ',') FROM memberships WHERE household_id = '{ServerDatabase.Household.Value}';"))
            .Should().Be($"{Partner.Value}:owner");
        (await db.ScalarAsync($"SELECT created_by IS NULL FROM households WHERE id = '{ServerDatabase.Household.Value}';")).Should().Be(true);
    }

    [Fact]
    public async Task ListMembershipsOfUserAsync_ARevokedRow_IsLeftOut()
    {
        // Arrange: a removed user must not sign in to the family that removed them (spec §10.3).
        await using var db = await ServerDatabase.CreateAsync(server);
        await CreateAccountAsync(db, User, "sub-1", ServerDatabase.Household);
        await CreateAccountAsync(db, Partner, "sub-2", PartnersHousehold);
        await db.ExecuteAsync(
            $"INSERT INTO memberships (household_id, user_id, role, joined_at, revoked_at) VALUES ('{ServerDatabase.Household.Value}', '{Partner.Value}', 'parent', now(), now());");

        // Act
        await using var scope = db.NewScope();
        var accounts = scope.ServiceProvider.GetRequiredService<IAccountStore>();
        var partners = await accounts.ListMembershipsOfUserAsync(Partner, CancellationToken.None);
        var members = await accounts.ListMembersAsync(ServerDatabase.Household, CancellationToken.None);

        // Assert
        partners.Should().ContainSingle().Which.HouseholdId.Should().Be(PartnersHousehold);
        members.Should().ContainSingle().Which.UserId.Should().Be(User);
    }
```

In `SyncStoreTests.DeleteAccountAsync_LastParent_RemovesTheSyncedRowsAndTheConsent`, replace the act with
`await new DeleteAccount(db.HouseholdStore).ExecuteAsync(user, confirmed: true, CancellationToken.None);` and rename the
test `ExecuteAsync_LastParent_RemovesTheSyncedRowsAndTheConsent`.

Append to `tests/Coo.Api.Infrastructure.Tests/HouseholdMembershipStoreTests.cs` (Task 4's file; add `using Coo.Api.Application.Auth;` and `using Coo.Api.Application.Sync;`):

```csharp
    [Fact]
    public async Task ExecuteAsync_PromotionTouchingNoRow_RollsTheDeletionBack()
    {
        // Arrange: spec §10.4. The partner is revoked between the read and the promotion, so the UPDATE finds no active row.
        await using var db = await ServerDatabase.CreateAsync(server);
        await MembersSchemaTests.SeedAsync(db);
        await AddUserAsync(db, Partner, "Kasia");
        await db.ExecuteAsync($"INSERT INTO memberships (household_id, user_id, role, joined_at) VALUES ('{Family.Value}', '{Partner.Value}', 'parent', now());");
        var store = new RevokingBeforePromotion(db.HouseholdStore, Partner);

        // Act
        var act = async () => await new DeleteAccount(store).ExecuteAsync(Owner, confirmed: true, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
        (await db.ScalarAsync($"""SELECT count(*) FROM "AspNetUsers" WHERE "Id" = '{Owner.Value}';""")).Should().Be(1L);
        (await db.ScalarAsync("SELECT count(*) FROM memberships WHERE revoked_at IS NULL;")).Should().Be(2L);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExecuteAsync_AcceptAndDeleteAccountAtOnce_EndConsistently(bool acceptFirst)
    {
        // Arrange: spec §12's concurrency test. The first of the two holds the family's lock until released; the second
        // queues behind it (PostgreSQL's own lock wait, not a sleep).
        await using var db = await ServerDatabase.CreateAsync(server);
        await MembersSchemaTests.SeedAsync(db);
        await AddUserAsync(db, Partner, "Kasia");
        var now = db.Time.GetUtcNow();
        await db.HouseholdStore.RunLockedAsync([Family], async tx =>
        {
            await tx.AddInvitationAsync(Invitation.Create(new InvitationId(db.NewId()), Family, Role.Parent, Owner, now), AuthSecrets.Hash("ABCDEFGH"), CancellationToken.None);
            return 0;
        }, CancellationToken.None);
        await db.Consents.RecordAsync(new ConsentRecord(Partner, Contracts.Sync.CareDataConsent.Purpose, Contracts.Sync.CareDataConsent.CurrentWording, now, null, null), CancellationToken.None);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = new HoldingStore(db.HouseholdStore, release.Task);
        var accept = new AcceptInvitation(db.Invitations, acceptFirst ? held : db.HouseholdStore, db.Consents, db.Time);
        var delete = new DeleteAccount(acceptFirst ? db.HouseholdStore : held);

        // Act
        var first = acceptFirst
            ? (Task)accept.ExecuteAsync(Partner, "ABCDEFGH", CancellationToken.None)
            : delete.ExecuteAsync(Owner, confirmed: true, CancellationToken.None);
        await held.Entered;
        var second = acceptFirst
            ? (Task)delete.ExecuteAsync(Owner, confirmed: true, CancellationToken.None)
            : accept.ExecuteAsync(Partner, "ABCDEFGH", CancellationToken.None);
        await WaitForLockWaitersAsync(db, 1);
        release.SetResult();
        await Task.WhenAll(first, second);

        // Assert
        if (acceptFirst)
        {
            // The joiner counted: ownership passed to her, the family stays.
            (await db.ScalarAsync($"SELECT string_agg(user_id::text || ':' || role, ',') FROM memberships WHERE household_id = '{Family.Value}';"))
                .Should().Be($"{Partner.Value}:owner");
        }
        else
        {
            // The family went first: the accept found it gone.
            var result = await (Task<Result<AcceptResult>>)second;
            result.Error!.Code.Should().Be(Contracts.Households.HouseholdErrors.InvitationInvalid);
            (await db.ScalarAsync("SELECT count(*) FROM households;")).Should().Be(0L);
            (await db.ScalarAsync($"SELECT count(*) FROM memberships WHERE user_id = '{Partner.Value}';")).Should().Be(0L);
        }
    }

    [Fact]
    public async Task ExecuteAsync_PurgeAfterExpiry_DeletesExpiredUsedAndWithdrawnInvitationsOnly()
    {
        // Arrange
        await using var db = await ServerDatabase.CreateAsync(server);
        await MembersSchemaTests.SeedAsync(db);
        var now = db.Time.GetUtcNow();
        await db.HouseholdStore.RunLockedAsync([Family], async tx =>
        {
            var used = Invitation.Create(new InvitationId(db.NewId()), Family, Role.Parent, Owner, now.AddHours(-80));
            used.Accept(Owner, now.AddHours(-79));
            await tx.AddInvitationAsync(used, "used", CancellationToken.None);
            await tx.SaveInvitationAsync(used, CancellationToken.None);
            await tx.AddInvitationAsync(Invitation.Create(new InvitationId(db.NewId()), Family, Role.Parent, Owner, now.AddHours(-73)), "old", CancellationToken.None);
            await tx.AddInvitationAsync(Invitation.Create(new InvitationId(db.NewId()), Family, Role.Parent, Owner, now), "fresh", CancellationToken.None);
            return 0;
        }, CancellationToken.None);

        // Act
        await new PurgeSyncHistory(db.Sync, db.Invitations, db.Time).ExecuteAsync(CancellationToken.None);

        // Assert
        (await db.ScalarAsync("SELECT string_agg(code_hash, ',') FROM invitations;")).Should().Be("fresh");
    }

    /// <summary>Holds the lock it takes until <paramref name="release"/> completes; <see cref="Entered"/> says it holds it.</summary>
    private sealed class HoldingStore(IHouseholdMembershipStore inner, Task release) : IHouseholdMembershipStore
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;

        public Task<T> RunLockedAsync<T>(IReadOnlyCollection<HouseholdId> households, Func<IMembershipTransaction, Task<T>> work, CancellationToken cancellationToken) =>
            inner.RunLockedAsync(households, async tx =>
            {
                _entered.TrySetResult();
                await release;
                return await work(tx);
            }, cancellationToken);

        public Task<IReadOnlyList<MemberProfile>> ListActiveMembersAsync(HouseholdId household, CancellationToken cancellationToken) =>
            inner.ListActiveMembersAsync(household, cancellationToken);

        public Task<IReadOnlyList<OpenInvitation>> ListOpenInvitationsAsync(HouseholdId household, DateTimeOffset now, CancellationToken cancellationToken) =>
            inner.ListOpenInvitationsAsync(household, now, cancellationToken);

        public Task<IReadOnlyList<Membership>> ListActiveMembershipsOfUserAsync(UserId user, CancellationToken cancellationToken) =>
            inner.ListActiveMembershipsOfUserAsync(user, cancellationToken);
    }

    /// <summary>Revokes <paramref name="partner"/> inside the transaction right before the promotion, so its UPDATE finds no active row.</summary>
    private sealed class RevokingBeforePromotion(IHouseholdMembershipStore inner, UserId partner) : IHouseholdMembershipStore
    {
        public Task<T> RunLockedAsync<T>(IReadOnlyCollection<HouseholdId> households, Func<IMembershipTransaction, Task<T>> work, CancellationToken cancellationToken) =>
            inner.RunLockedAsync(households, tx => work(new Revoking(tx, partner)), cancellationToken);

        public Task<IReadOnlyList<MemberProfile>> ListActiveMembersAsync(HouseholdId household, CancellationToken cancellationToken) =>
            inner.ListActiveMembersAsync(household, cancellationToken);

        public Task<IReadOnlyList<OpenInvitation>> ListOpenInvitationsAsync(HouseholdId household, DateTimeOffset now, CancellationToken cancellationToken) =>
            inner.ListOpenInvitationsAsync(household, now, cancellationToken);

        public Task<IReadOnlyList<Membership>> ListActiveMembershipsOfUserAsync(UserId user, CancellationToken cancellationToken) =>
            inner.ListActiveMembershipsOfUserAsync(user, cancellationToken);

        private sealed class Revoking(IMembershipTransaction tx, UserId partner) : IMembershipTransaction
        {
            public bool IsLocked(HouseholdId household) => tx.IsLocked(household);

            public Task<MembershipLookup> FindMembershipAsync(HouseholdId household, UserId user, CancellationToken cancellationToken) => tx.FindMembershipAsync(household, user, cancellationToken);

            public Task<IReadOnlyList<Membership>> ActiveMembersAsync(HouseholdId household, CancellationToken cancellationToken) => tx.ActiveMembersAsync(household, cancellationToken);

            public Task<IReadOnlyList<Membership>> ActiveMembershipsOfUserAsync(UserId user, CancellationToken cancellationToken) => tx.ActiveMembershipsOfUserAsync(user, cancellationToken);

            public Task<Invitation?> FindInvitationForUpdateAsync(string codeHash, CancellationToken cancellationToken) => tx.FindInvitationForUpdateAsync(codeHash, cancellationToken);

            public Task<Invitation?> FindInvitationForUpdateAsync(HouseholdId household, InvitationId id, CancellationToken cancellationToken) => tx.FindInvitationForUpdateAsync(household, id, cancellationToken);

            public Task<int> CountOpenInvitationsAsync(HouseholdId household, DateTimeOffset now, CancellationToken cancellationToken) => tx.CountOpenInvitationsAsync(household, now, cancellationToken);

            public Task AddInvitationAsync(Invitation invitation, string codeHash, CancellationToken cancellationToken) => tx.AddInvitationAsync(invitation, codeHash, cancellationToken);

            public Task SaveInvitationAsync(Invitation invitation, CancellationToken cancellationToken) => tx.SaveInvitationAsync(invitation, cancellationToken);

            public Task AddMembershipAsync(Membership membership, CancellationToken cancellationToken) => tx.AddMembershipAsync(membership, cancellationToken);

            public Task ReactivateMembershipAsync(HouseholdId household, UserId user, Role role, DateTimeOffset joinedAt, CancellationToken cancellationToken) => tx.ReactivateMembershipAsync(household, user, role, joinedAt, cancellationToken);

            public Task RevokeMembershipAsync(HouseholdId household, UserId user, DateTimeOffset now, CancellationToken cancellationToken) => tx.RevokeMembershipAsync(household, user, now, cancellationToken);

            public Task<int> WithdrawOpenInvitationsByAsync(HouseholdId household, UserId createdBy, DateTimeOffset now, CancellationToken cancellationToken) => tx.WithdrawOpenInvitationsByAsync(household, createdBy, now, cancellationToken);

            public async Task<int> PromoteToOwnerAsync(HouseholdId household, UserId user, CancellationToken cancellationToken)
            {
                // Inside the same transaction: the owner's own delete already ran, so revoke the partner here and let the
                // UPDATE find no active row.
                await tx.RevokeMembershipAsync(household, partner, new DateTimeOffset(2026, 10, 1, 13, 0, 0, TimeSpan.Zero), cancellationToken);
                return await tx.PromoteToOwnerAsync(household, user, cancellationToken);
            }

            public Task<bool> HoldsAnyRecordAsync(HouseholdId household, CancellationToken cancellationToken) => tx.HoldsAnyRecordAsync(household, cancellationToken);

            public Task<bool> TryCreateHouseholdAsync(HouseholdId household, UserId owner, DateTimeOffset now, CancellationToken cancellationToken) => tx.TryCreateHouseholdAsync(household, owner, now, cancellationToken);

            public Task DeleteHouseholdAsync(HouseholdId household, CancellationToken cancellationToken) => tx.DeleteHouseholdAsync(household, cancellationToken);

            public Task DeleteMembershipsOfUserAsync(UserId user, CancellationToken cancellationToken) => tx.DeleteMembershipsOfUserAsync(user, cancellationToken);

            public Task DeleteUserAsync(UserId user, CancellationToken cancellationToken) => tx.DeleteUserAsync(user, cancellationToken);
        }
    }
```


- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Coo.Api.Application.Tests --filter "FullyQualifiedName~AccountTests"` and
`dotnet test tests/Coo.Api.Infrastructure.Tests --filter "FullyQualifiedName~HouseholdMembershipStoreTests|FullyQualifiedName~AuthStoreTests|FullyQualifiedName~SyncStoreTests"`
Expected: FAIL to compile (`DeleteAccount` has no constructor taking the membership store; `AccountOverview.Memberships`
missing).

- [ ] **Step 3: Write the implementation**


In `src/Backend/Coo.Api.Application/Auth/AccountUseCases.cs`, replace the file's two classes and the record with:

```csharp
using Coo.Api.Application.Households;
using Coo.Api.Application.Sync;
using Coo.Contracts.Sync;
using Coo.Domain.Common;
using Coo.Domain.Households;

namespace Coo.Api.Application.Auth;

/// <param name="Memberships">The active ones, earliest joined first (Spec 011b §10.1); <see cref="Membership"/> is the first.</param>
public sealed record AccountOverview(
    UserAccount User, Membership? Membership, IReadOnlyList<DeviceSession> Devices, ConsentRecord? Consent = null,
    IReadOnlyList<Membership>? Memberships = null);

/// <summary>
/// <c>GET /api/me</c>: the user, their active families and roles, their active device sessions, and their consent to the
/// current wording (Spec 011 §3). <c>household</c> stays the earliest active family, for 0.11.0 phones.
/// </summary>
public sealed class GetMe(IAccountStore accounts, IDeviceSessionStore sessions, IConsentStore consents)
{
    public async Task<AccountOverview?> ExecuteAsync(UserId user, CancellationToken cancellationToken)
    {
        var account = await accounts.FindAsync(user, cancellationToken);
        if (account is null)
            return null;
        var memberships = (await accounts.ListMembershipsOfUserAsync(user, cancellationToken)).OrderBy(m => m.JoinedAt).ToList();
        var devices = (await sessions.ListForUserAsync(user, cancellationToken))
            .Where(s => !s.IsRevoked)
            .OrderByDescending(s => s.LastUsedAt)
            .ToList();
        var consent = await consents.FindAsync(user, CareDataConsent.Purpose, CareDataConsent.CurrentWording, cancellationToken);
        return new AccountOverview(account, memberships.FirstOrDefault(), devices, consent, memberships);
    }
}

/// <summary>
/// <c>DELETE /api/account</c> (Spec 010 §5, ADR 0003 §8; Spec 011b §10.4). Every family the user is an active member of is
/// locked first; the succession rule then decides each from the members read inside the transaction, so a concurrent
/// accept either counts (it committed first) or finds the family gone. A promotion must change exactly one row; anything
/// else throws and rolls the whole deletion back.
/// </summary>
public sealed class DeleteAccount(IHouseholdMembershipStore store)
{
    public async Task<Result> ExecuteAsync(UserId user, bool confirmed, CancellationToken cancellationToken)
    {
        if (!confirmed)
            return Result.Failure(AuthErrors.ConfirmationRequired);

        var families = (await store.ListActiveMembershipsOfUserAsync(user, cancellationToken)).Select(m => m.HouseholdId).ToList();
        return await store.RunLockedAsync(families, async tx =>
        {
            var deletions = new List<HouseholdId>();
            var transfers = new List<(HouseholdId Household, UserId NewOwner)>();
            foreach (var membership in await tx.ActiveMembershipsOfUserAsync(user, cancellationToken))
            {
                var members = await tx.ActiveMembersAsync(membership.HouseholdId, cancellationToken);
                switch (HouseholdSuccession.WhenLeaving(members, user))
                {
                    case HouseholdDeleted:
                        deletions.Add(membership.HouseholdId);
                        break;
                    case OwnershipPasses passes:
                        transfers.Add((membership.HouseholdId, passes.NewOwner));
                        break;
                }
            }

            // The leaver's rows first, so a promoted parent never meets a second Owner (ix_memberships_one_owner).
            await tx.DeleteMembershipsOfUserAsync(user, cancellationToken);
            foreach (var (household, newOwner) in transfers)
            {
                if (await tx.PromoteToOwnerAsync(household, newOwner, cancellationToken) != 1)
                    throw new InvalidOperationException($"Promoting {newOwner} in {household} did not change exactly one row.");
            }

            foreach (var household in deletions)
                await tx.DeleteHouseholdAsync(household, cancellationToken);
            await tx.DeleteUserAsync(user, cancellationToken);
            return Result.Success;
        }, cancellationToken);
    }
}
```

In `src/Backend/Coo.Api.Application/Auth/Ports.cs` delete `OwnershipTransfer`, `AccountDeletionPlan` and
`IAccountStore.DeleteAccountAsync`, and change the two list methods' docs:

```csharp
    /// <summary>The user's active memberships (<c>revoked_at IS NULL</c>, Spec 011b §10.3).</summary>
    Task<IReadOnlyList<Membership>> ListMembershipsOfUserAsync(UserId user, CancellationToken cancellationToken);

    /// <summary>The household's active members.</summary>
    Task<IReadOnlyList<Membership>> ListMembersAsync(HouseholdId household, CancellationToken cancellationToken);
```

In `AccountStoreExtensions.cs` the summary becomes: "The user's earliest **active** membership, or null when they have
none (Spec 011b §10.3: a removed user must not sign in to the family that removed them)."

In `src/Backend/Coo.Api.Application/Sync/UseCases.cs` replace `PurgeSyncHistory` with:

```csharp
/// <summary>
/// The daily clean-up (Spec 011 §7.2, Spec 011b §10.3): change results and conflict records older than 30 days, and every
/// invitation whose expiry has passed.
/// </summary>
public sealed class PurgeSyncHistory(ISyncStore store, Households.IInvitationStore invitations, TimeProvider time)
{
    public static readonly TimeSpan KeepFor = TimeSpan.FromDays(30);

    public async Task<int> ExecuteAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        return await store.PurgeAsync(now - KeepFor, cancellationToken) + await invitations.PurgeExpiredAsync(now, cancellationToken);
    }
}
```

In `tests/Coo.Api.IntegrationTests/SyncHistoryCleanupJobTests.cs` add a field
`private readonly IInvitationStore _invitations = Substitute.For<IInvitationStore>();` (with `using Coo.Api.Application.Households;`)
and change the four `new PurgeSyncHistory(_store, _time)` to `new PurgeSyncHistory(_store, _invitations, _time)`.



**`AccountStore`.** 
Delete `DeleteAccountAsync` from `AccountStore`. Change the two list methods:

```csharp
    public async Task<IReadOnlyList<Membership>> ListMembershipsOfUserAsync(UserId user, CancellationToken cancellationToken) =>
        [.. (await context.Memberships.AsNoTracking().Where(m => m.UserId == user.Value && m.RevokedAt == null).ToListAsync(cancellationToken)).Select(ToMembership)];

    public async Task<IReadOnlyList<Membership>> ListMembersAsync(HouseholdId household, CancellationToken cancellationToken) =>
        [.. (await context.Memberships.AsNoTracking().Where(m => m.HouseholdId == household.Value && m.RevokedAt == null).ToListAsync(cancellationToken)).Select(ToMembership)];
```

Update the class summary: "Users through ASP.NET Core Identity (ADR 0003 §3), households and memberships (active rows
only, Spec 011b §10.3)…". Remove `using Microsoft.EntityFrameworkCore.Storage;` if it became unused.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Coo.Api.Application.Tests`, `dotnet test tests/Coo.Api.Infrastructure.Tests` and
`dotnet test tests/Coo.Api.IntegrationTests --filter "FullyQualifiedName~SyncHistoryCleanupJobTests|FullyQualifiedName~AuthApiTests"`
Expected: PASS. (`Program.cs` still registers `DeleteAccount` as scoped; it now needs only the singleton membership store,
which Task 4 registered, so it resolves. Task 6 moves it beside the household use cases.)

- [ ] **Step 5: Commit**

```bash
git add src/Backend tests/Coo.Api.Application.Tests tests/Coo.Api.Infrastructure.Tests tests/Coo.Api.IntegrationTests/SyncHistoryCleanupJobTests.cs
git commit -m "Api: active memberships everywhere; /api/me lists them; account deletion and a concurrent accept under the family's lock (Spec 011b §10.3, §10.4)"
```

---

### Task 6: The household and invitation endpoints, and `/api/me` memberships

**Files:**
- Create: `src/Backend/Coo.Api/Hosting/HouseholdEndpoints.cs`
- Modify: `src/Backend/Coo.Api/Program.cs`, `src/Backend/Coo.Api/Hosting/AuthEndpoints.cs` (`RoleName` internal, `/api/me`
  memberships), `src/Shared/Coo.Contracts/Auth/AuthContracts.cs` (`MembershipDto.JoinedAt`, `MeResponse.Memberships`)
- Test: `tests/Coo.Api.IntegrationTests/HouseholdApiTests.cs`

**Interfaces:**
- Consumes: Tasks 2–5.
- Produces:
  - Routes (spec §10.1): `POST /api/households/{id}/invitations`, `DELETE /api/households/{id}/invitations/{invitationId}`,
    `GET /api/households/{id}/members`, `DELETE /api/households/{id}/members/{userId}`, `POST /api/households/{id}/leave`,
    `POST /api/households`, `POST /api/invitations/preview`, `POST /api/invitations/accept`; `GET /api/me` adds `memberships`.
  - `MembershipDto(Guid HouseholdId, string Role, DateTimeOffset? JoinedAt = null)`;
    `MeResponse(UserDto User, MembershipDto? Household, IReadOnlyList<DeviceSessionDto> Devices, ConsentDto? Consent = null, IReadOnlyList<MembershipDto>? Memberships = null)`.
  - `HouseholdEndpoints.MapHouseholdEndpoints(this IEndpointRouteBuilder)`; `AuthEndpoints.RoleName(Role)` (internal).

- [ ] **Step 1: Write the failing tests**

`tests/Coo.Api.IntegrationTests/HouseholdApiTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using Coo.Contracts.Auth;
using Coo.Contracts.Households;
using Coo.Contracts.Sync;

namespace Coo.Api.IntegrationTests;

internal static class HouseholdCalls
{
    public static readonly Uri Preview = new("/api/invitations/preview", UriKind.Relative);
    public static readonly Uri Accept = new("/api/invitations/accept", UriKind.Relative);
    public static readonly Uri Households = new("/api/households", UriKind.Relative);

    public static Uri Invitations(Guid household) => new($"/api/households/{household}/invitations", UriKind.Relative);

    public static Uri Invitation(Guid household, Guid invitation) => new($"/api/households/{household}/invitations/{invitation}", UriKind.Relative);

    public static Uri Members(Guid household) => new($"/api/households/{household}/members", UriKind.Relative);

    public static Uri Member(Guid household, Guid user) => new($"/api/households/{household}/members/{user}", UriKind.Relative);

    public static Uri Leave(Guid household) => new($"/api/households/{household}/leave", UriKind.Relative);

    public static async Task<InvitationCreatedResponse> InviteAsync(HttpClient client, string token, Guid household)
    {
        var response = await Calls.SendAsync(client, HttpMethod.Post, Invitations(household), token, new CreateInvitationRequest(AuthRoles.Parent));
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<InvitationCreatedResponse>())!;
    }

    public static async Task<AcceptInvitationResponse> AcceptAsync(HttpClient client, string token, string code)
    {
        var response = await Calls.SendAsync(client, HttpMethod.Post, Accept, token, new InvitationCodeRequest(code));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<AcceptInvitationResponse>())!;
    }
}

/// <summary>Spec 011b §10 and §12 (Api integration row), end to end with a fake Google.</summary>
public sealed class HouseholdApiTests(AuthApi api) : IClassFixture<AuthApi>
{
    [Fact]
    public async Task InvitePreviewAccept_SecondUser_BothPushAndPullEachOthersEntries()
    {
        // Arrange
        using var client = api.Factory.CreateClient();
        var (family, ownerPhone, joinerPhone) = (Calls.Id(7001), Calls.Id(7002), Calls.Id(7003));
        var owner = await SyncCalls.SignInWithConsentAsync(client, "hh-owner", ownerPhone, family);
        var joiner = await SyncCalls.SignInWithConsentAsync(client, "hh-joiner", joinerPhone, Calls.Id(7004));

        // Act
        var invitation = await HouseholdCalls.InviteAsync(client, owner.AccessToken, family);
        var preview = await (await Calls.SendAsync(client, HttpMethod.Post, HouseholdCalls.Preview, joiner.AccessToken,
            new InvitationCodeRequest(invitation.Code))).Content.ReadFromJsonAsync<InvitationPreviewResponse>();
        var accepted = await HouseholdCalls.AcceptAsync(client, joiner.AccessToken, invitation.Code);
        await SyncCalls.PushAsync(client, owner.AccessToken, family, ownerPhone, SyncCalls.Diaper(Calls.Id(7005), Calls.Id(7006), SyncCalls.Wall, ownerPhone, "his"));
        await SyncCalls.PushAsync(client, joiner.AccessToken, family, joinerPhone, SyncCalls.Diaper(Calls.Id(7007), Calls.Id(7008), SyncCalls.Wall, joinerPhone, "hers"));
        var ownersView = await (await Calls.SendAsync(client, HttpMethod.Get, SyncCalls.Pull(family, 0), owner.AccessToken)).Content.ReadFromJsonAsync<PullResponse>();
        var joinersView = await (await Calls.SendAsync(client, HttpMethod.Get, SyncCalls.Pull(family, 0), joiner.AccessToken)).Content.ReadFromJsonAsync<PullResponse>();

        // Assert
        invitation.Code.Should().MatchRegex("^[ABCDEFGHJKMNPQRSTUVWXYZ23456789]{8}$");
        invitation.Link.Should().Be("https://api.coo.lechdigital.nl/i/" + invitation.Code);
        preview!.InviterName.Should().Be("Parent hh-owner");
        preview.AlreadyMember.Should().BeFalse();
        accepted.Should().Be(new AcceptInvitationResponse(family, AuthRoles.Parent, AcceptOutcomes.Joined));
        ownersView!.Records.Select(r => r.CareEntry!.Note).Should().BeEquivalentTo("his", "hers");
        joinersView!.Records.Select(r => r.CareEntry!.Note).Should().BeEquivalentTo("his", "hers");
    }

    [Fact]
    public async Task Push_AParentAndASeededCaregiver_FollowTheRoleRule()
    {
        // Arrange: spec §10.5. A Parent joined through accept may manage the baby; a Caregiver seeded in the database may not.
        using var client = api.Factory.CreateClient();
        var (family, ownerPhone, parentPhone, nannyPhone) = (Calls.Id(7011), Calls.Id(7012), Calls.Id(7013), Calls.Id(7014));
        var owner = await SyncCalls.SignInWithConsentAsync(client, "role-owner", ownerPhone, family);
        var parent = await SyncCalls.SignInWithConsentAsync(client, "role-parent", parentPhone, Calls.Id(7015));
        var nanny = await SyncCalls.SignInWithConsentAsync(client, "role-nanny", nannyPhone, Calls.Id(7016));
        await HouseholdCalls.AcceptAsync(client, parent.AccessToken, (await HouseholdCalls.InviteAsync(client, owner.AccessToken, family)).Code);
        await using (var connection = new Npgsql.NpgsqlConnection(api.ConnectionString))
        {
            await connection.OpenAsync();
            await using var seed = new Npgsql.NpgsqlCommand(
                $"INSERT INTO memberships (household_id, user_id, role, joined_at) VALUES ('{family}', '{nanny.User.Id}', 'caregiver', now());", connection);
            await seed.ExecuteNonQueryAsync();
        }

        // Act
        var parentBaby = await ResultAsync(await SyncCalls.PushAsync(client, parent.AccessToken, family, parentPhone, Baby(Calls.Id(7017), Calls.Id(7018), parentPhone)));
        var parentSettings = await ResultAsync(await SyncCalls.PushAsync(client, parent.AccessToken, family, parentPhone, Settings(Calls.Id(7019), family, parentPhone)));
        var nannyBaby = await ResultAsync(await SyncCalls.PushAsync(client, nanny.AccessToken, family, nannyPhone, Baby(Calls.Id(7020), Calls.Id(7021), nannyPhone)));
        var nannySettings = await ResultAsync(await SyncCalls.PushAsync(client, nanny.AccessToken, family, nannyPhone, Settings(Calls.Id(7022), family, nannyPhone, wall: SyncCalls.Wall + 5)));
        var nannyEntry = await ResultAsync(await SyncCalls.PushAsync(client, nanny.AccessToken, family, nannyPhone, SyncCalls.Diaper(Calls.Id(7023), Calls.Id(7024), SyncCalls.Wall, nannyPhone)));

        // Assert
        parentBaby.Result.Should().Be(ChangeResults.Applied);
        parentSettings.Result.Should().Be(ChangeResults.Applied);
        nannyBaby.Should().Be(new ChangeResultDto(Calls.Id(7020), ChangeResults.Rejected, RejectionReasons.RoleForbidden));
        nannySettings.Reason.Should().Be(RejectionReasons.RoleForbidden);
        nannyEntry.Result.Should().Be(ChangeResults.Applied);
    }

    [Fact]
    public async Task Remove_TheJoiner_TheirNextSyncIsMembershipRevokedAndMembershipSaysRevoked()
    {
        // Arrange
        using var client = api.Factory.CreateClient();
        var (family, phone) = (Calls.Id(7031), Calls.Id(7033));
        var owner = await SyncCalls.SignInWithConsentAsync(client, "rm-owner", Calls.Id(7032), family);
        var joiner = await SyncCalls.SignInWithConsentAsync(client, "rm-joiner", phone, Calls.Id(7034));
        await HouseholdCalls.AcceptAsync(client, joiner.AccessToken, (await HouseholdCalls.InviteAsync(client, owner.AccessToken, family)).Code);
        var stillOpen = await HouseholdCalls.InviteAsync(client, joiner.AccessToken, family);

        // Act
        var removed = await Calls.SendAsync(client, HttpMethod.Delete, HouseholdCalls.Member(family, joiner.User.Id), owner.AccessToken);
        var pull = await Calls.SendAsync(client, HttpMethod.Get, SyncCalls.Pull(family, 0), joiner.AccessToken);
        var status = await (await Calls.SendAsync(client, HttpMethod.Get, SyncCalls.Membership(family), joiner.AccessToken)).Content.ReadFromJsonAsync<MembershipStatusResponse>();
        var members = await (await Calls.SendAsync(client, HttpMethod.Get, HouseholdCalls.Members(family), owner.AccessToken)).Content.ReadFromJsonAsync<MembersResponse>();
        var theirLink = await Calls.SendAsync(client, HttpMethod.Post, HouseholdCalls.Preview, owner.AccessToken, new InvitationCodeRequest(stillOpen.Code));

        // Assert
        removed.StatusCode.Should().Be(HttpStatusCode.NoContent);
        pull.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await pull.Content.ReadFromJsonAsync<ProblemResponse>())!.Should().Be(new ProblemResponse(SyncErrors.MembershipRevokedType, 403, family));
        status!.Status.Should().Be(MembershipStatuses.Revoked);
        members!.Members.Should().ContainSingle().Which.IsYou.Should().BeTrue();
        theirLink.StatusCode.Should().Be(HttpStatusCode.NotFound, "a removed member's open invitations are withdrawn with them");
    }

    [Fact]
    public async Task Members_AStranger_IsForbiddenNotTheSyncProblem()
    {
        // Arrange
        using var client = api.Factory.CreateClient();
        var family = Calls.Id(7041);
        await SyncCalls.SignInWithConsentAsync(client, "mb-owner", Calls.Id(7042), family);
        var stranger = await SyncCalls.SignInWithConsentAsync(client, "mb-stranger", Calls.Id(7043), Calls.Id(7044));

        // Act
        var response = await Calls.SendAsync(client, HttpMethod.Get, HouseholdCalls.Members(family), stranger.AccessToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Calls.CodeAsync(response)).Should().Be(HouseholdErrors.Forbidden);
    }

    [Fact]
    public async Task Accept_WithoutConsent_IsConsentRequired()
    {
        // Arrange
        using var client = api.Factory.CreateClient();
        var family = Calls.Id(7051);
        var owner = await SyncCalls.SignInWithConsentAsync(client, "nc-owner", Calls.Id(7052), family);
        var joiner = await Calls.SignInAsync(client, "nc-joiner", Calls.Id(7053), Calls.Id(7054));
        var invitation = await HouseholdCalls.InviteAsync(client, owner.AccessToken, family);

        // Act
        var response = await Calls.SendAsync(client, HttpMethod.Post, HouseholdCalls.Accept, joiner.AccessToken, new InvitationCodeRequest(invitation.Code));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Calls.CodeAsync(response)).Should().Be(SyncErrors.ConsentRequired);
    }

    [Fact]
    public async Task WithdrawThenAccept_TheCode_IsInvalid()
    {
        // Arrange
        using var client = api.Factory.CreateClient();
        var family = Calls.Id(7061);
        var owner = await SyncCalls.SignInWithConsentAsync(client, "wd-owner", Calls.Id(7062), family);
        var joiner = await SyncCalls.SignInWithConsentAsync(client, "wd-joiner", Calls.Id(7063), Calls.Id(7064));
        var invitation = await HouseholdCalls.InviteAsync(client, owner.AccessToken, family);

        // Act
        var withdrawn = await Calls.SendAsync(client, HttpMethod.Delete, HouseholdCalls.Invitation(family, invitation.InvitationId), owner.AccessToken);
        var again = await Calls.SendAsync(client, HttpMethod.Delete, HouseholdCalls.Invitation(family, invitation.InvitationId), owner.AccessToken);
        var accept = await Calls.SendAsync(client, HttpMethod.Post, HouseholdCalls.Accept, joiner.AccessToken, new InvitationCodeRequest(invitation.Code));

        // Assert
        withdrawn.StatusCode.Should().Be(HttpStatusCode.NoContent);
        again.StatusCode.Should().Be(HttpStatusCode.NoContent);
        accept.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Calls.CodeAsync(accept)).Should().Be(HouseholdErrors.InvitationInvalid);
    }

    [Fact]
    public async Task Leave_ParentWithTheirOwnFamily_LeavesAndMeListsOneMembership()
    {
        // Arrange
        using var client = api.Factory.CreateClient();
        var (family, ownFamily) = (Calls.Id(7071), Calls.Id(7074));
        var owner = await SyncCalls.SignInWithConsentAsync(client, "lv-owner", Calls.Id(7072), family);
        var joiner = await SyncCalls.SignInWithConsentAsync(client, "lv-joiner", Calls.Id(7073), ownFamily);
        await HouseholdCalls.AcceptAsync(client, joiner.AccessToken, (await HouseholdCalls.InviteAsync(client, owner.AccessToken, family)).Code);
        var meBefore = await (await Calls.SendAsync(client, HttpMethod.Get, Calls.Me, joiner.AccessToken)).Content.ReadFromJsonAsync<MeResponse>();

        // Act
        var left = await (await Calls.SendAsync(client, HttpMethod.Post, HouseholdCalls.Leave(family), joiner.AccessToken, new LeaveRequest(false)))
            .Content.ReadFromJsonAsync<LeaveResponse>();
        var meAfter = await (await Calls.SendAsync(client, HttpMethod.Get, Calls.Me, joiner.AccessToken)).Content.ReadFromJsonAsync<MeResponse>();
        var lastFamily = await Calls.SendAsync(client, HttpMethod.Post, HouseholdCalls.Leave(ownFamily), joiner.AccessToken, new LeaveRequest(true));

        // Assert
        meBefore!.Memberships!.Select(m => m.HouseholdId).Should().Equal(ownFamily, family);
        meBefore.Household!.HouseholdId.Should().Be(ownFamily, "household stays the earliest active family for 0.11.0 phones");
        left!.Outcome.Should().Be(LeaveOutcomes.Left);
        meAfter!.Memberships!.Should().ContainSingle().Which.HouseholdId.Should().Be(ownFamily);
        lastFamily.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Calls.CodeAsync(lastFamily)).Should().Be(HouseholdErrors.LastFamily);
    }

    [Fact]
    public async Task CreateHousehold_AfterRemovalFromTheOnlyFamily_IsCreatedThenAlreadyHasFamily()
    {
        // Arrange: spec §8.4.
        using var client = api.Factory.CreateClient();
        var family = Calls.Id(7081);
        var owner = await SyncCalls.SignInWithConsentAsync(client, "ch-owner", Calls.Id(7082), family);
        var joiner = await SyncCalls.SignInWithConsentAsync(client, "ch-joiner", Calls.Id(7083), Calls.Id(7084));
        await HouseholdCalls.AcceptAsync(client, joiner.AccessToken, (await HouseholdCalls.InviteAsync(client, owner.AccessToken, family)).Code);
        await Calls.SendAsync(client, HttpMethod.Post, HouseholdCalls.Leave(Calls.Id(7084)), joiner.AccessToken, new LeaveRequest(true));
        await Calls.SendAsync(client, HttpMethod.Delete, HouseholdCalls.Member(family, joiner.User.Id), owner.AccessToken);

        // Act
        var created = await Calls.SendAsync(client, HttpMethod.Post, HouseholdCalls.Households, joiner.AccessToken, new CreateHouseholdRequest(Calls.Id(7085)));
        var second = await Calls.SendAsync(client, HttpMethod.Post, HouseholdCalls.Households, joiner.AccessToken, new CreateHouseholdRequest(Calls.Id(7086)));
        var taken = await Calls.SendAsync(client, HttpMethod.Post, HouseholdCalls.Households, owner.AccessToken, new CreateHouseholdRequest(family));

        // Assert
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Calls.CodeAsync(second)).Should().Be(HouseholdErrors.AlreadyHasFamily);
        taken.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    private static async Task<ChangeResultDto> ResultAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<PushResponse>())!.Results.Single();

    private static SyncChangeDto Baby(Guid change, Guid baby, Guid device) => new(
        change, SyncEntityTypes.BabyProfile, baby, SyncOperations.Upsert, new HlcStampDto(SyncCalls.Wall, 0, device),
        BabyProfile: new BabyProfileDto("Maja", [], new DateOnly(2026, 8, 20), null));

    private static SyncChangeDto Settings(Guid change, Guid household, Guid device, long wall = SyncCalls.Wall) => new(
        change, SyncEntityTypes.HouseholdSettings, household, SyncOperations.Upsert, new HlcStampDto(wall, 0, device),
        HouseholdSettings: new HouseholdSettingsDto("ml", "Europe/Amsterdam", []));
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Coo.Api.IntegrationTests --filter "FullyQualifiedName~HouseholdApiTests"`
Expected: FAIL to compile (`MeResponse.Memberships` does not exist yet); once it does, the new routes answer 404.

- [ ] **Step 3: The contracts' additions**

In `src/Shared/Coo.Contracts/Auth/AuthContracts.cs`:

```csharp
/// <summary><see cref="Role"/> is one of <see cref="AuthRoles"/>. <see cref="JoinedAt"/> is set in <c>memberships</c> (Spec 011b).</summary>
public sealed record MembershipDto(Guid HouseholdId, string Role, DateTimeOffset? JoinedAt = null);

/// <param name="Household">The earliest active family, kept for 0.11.0 phones.</param>
/// <param name="Memberships">Every active family, earliest joined first (Spec 011b §10.1).</param>
public sealed record MeResponse(
    UserDto User, MembershipDto? Household, IReadOnlyList<DeviceSessionDto> Devices, ConsentDto? Consent = null,
    IReadOnlyList<MembershipDto>? Memberships = null);
```

- [ ] **Step 4: The endpoints**

`src/Backend/Coo.Api/Hosting/HouseholdEndpoints.cs`:

```csharp
using System.Security.Claims;
using Coo.Api.Application.Auth;
using Coo.Api.Application.Households;
using Coo.Contracts.Auth;
using Coo.Contracts.Households;
using Coo.Contracts.Sync;
using Coo.Domain.Common;
using Coo.Domain.Households;

namespace Coo.Api.Hosting;

/// <summary>
/// Members and invitations (Spec 011b §10.1). Every household endpoint checks an active membership per request in its use
/// case; a caller who is not one gets <c>403 household.forbidden</c>, never ADR 0002 §10's problem body. Codes travel in
/// bodies only. Refusals are codes (ADR 0006).
/// </summary>
internal static class HouseholdEndpoints
{
    private static readonly Dictionary<string, int> Statuses = new(StringComparer.Ordinal)
    {
        [HouseholdErrors.InvitationInvalid] = StatusCodes.Status404NotFound,
        [HouseholdErrors.InvitationRoleUnsupported] = StatusCodes.Status400BadRequest,
        [HouseholdErrors.InvitationTooMany] = StatusCodes.Status409Conflict,
        [HouseholdErrors.Forbidden] = StatusCodes.Status403Forbidden,
        [HouseholdErrors.MemberNotFound] = StatusCodes.Status404NotFound,
        [HouseholdErrors.OwnerCannotLeave] = StatusCodes.Status409Conflict,
        [HouseholdErrors.LastFamily] = StatusCodes.Status409Conflict,
        [HouseholdErrors.ConfirmationRequired] = StatusCodes.Status400BadRequest,
        [HouseholdErrors.IdTaken] = StatusCodes.Status409Conflict,
        [HouseholdErrors.AlreadyHasFamily] = StatusCodes.Status409Conflict,
        [HouseholdErrors.Malformed] = StatusCodes.Status400BadRequest,
        [SyncErrors.ConsentRequired] = StatusCodes.Status403Forbidden,
    };

    public static void MapHouseholdEndpoints(this IEndpointRouteBuilder app)
    {
        var households = app.MapGroup("/api/households").RequireAuthorization();

        households.MapPost("/{id:guid}/invitations", async Task<IResult> (
            Guid id, HttpRequest http, ClaimsPrincipal principal, CreateInvitation create, CancellationToken cancellationToken) =>
        {
            if (id == Guid.Empty || await AuthEndpoints.TryReadJsonAsync<CreateInvitationRequest>(http, cancellationToken) is not { } request)
                return AuthEndpoints.Refused(HouseholdErrors.Malformed, StatusCodes.Status400BadRequest);
            var result = await create.ExecuteAsync(AuthEndpoints.UserOf(principal), new HouseholdId(id), request.Role, cancellationToken);
            if (result.IsFailure)
                return Refused(result.Error!.Code);
            var code = result.Value.Code.Value;
            return Results.Json(
                new InvitationCreatedResponse(result.Value.Id.Value, code, InvitationLinks.For(code), result.Value.ExpiresAt),
                statusCode: StatusCodes.Status201Created);
        });

        households.MapDelete("/{id:guid}/invitations/{invitationId:guid}", async Task<IResult> (
            Guid id, Guid invitationId, ClaimsPrincipal principal, WithdrawInvitation withdraw, CancellationToken cancellationToken) =>
        {
            if (id == Guid.Empty || invitationId == Guid.Empty)
                return Refused(HouseholdErrors.Forbidden);
            var result = await withdraw.ExecuteAsync(AuthEndpoints.UserOf(principal), new HouseholdId(id), new InvitationId(invitationId), cancellationToken);
            return result.IsFailure ? Refused(result.Error!.Code) : Results.NoContent();
        });

        households.MapGet("/{id:guid}/members", async Task<IResult> (
            Guid id, ClaimsPrincipal principal, ListMembers list, CancellationToken cancellationToken) =>
        {
            if (id == Guid.Empty)
                return Refused(HouseholdErrors.Forbidden);
            var user = AuthEndpoints.UserOf(principal);
            var result = await list.ExecuteAsync(user, new HouseholdId(id), cancellationToken);
            if (result.IsFailure)
                return Refused(result.Error!.Code);
            return Results.Ok(new MembersResponse(
                [.. result.Value.Members.Select(m => new MemberDto(m.UserId.Value, m.Name, m.Email, AuthEndpoints.RoleName(m.Role), m.JoinedAt, m.UserId == user))],
                [.. result.Value.Invitations.Select(i => new InvitationDto(i.Id.Value, AuthEndpoints.RoleName(i.Role), i.CreatedBy.Value, i.CreatedAt, i.ExpiresAt))]));
        });

        households.MapDelete("/{id:guid}/members/{userId:guid}", async Task<IResult> (
            Guid id, Guid userId, ClaimsPrincipal principal, RemoveMember remove, CancellationToken cancellationToken) =>
        {
            if (id == Guid.Empty || userId == Guid.Empty)
                return Refused(HouseholdErrors.MemberNotFound);
            var result = await remove.ExecuteAsync(AuthEndpoints.UserOf(principal), new HouseholdId(id), new UserId(userId), cancellationToken);
            return result.IsFailure ? Refused(result.Error!.Code) : Results.NoContent();
        });

        households.MapPost("/{id:guid}/leave", async Task<IResult> (
            Guid id, HttpRequest http, ClaimsPrincipal principal, LeaveHousehold leave, CancellationToken cancellationToken) =>
        {
            if (id == Guid.Empty)
                return Refused(HouseholdErrors.Forbidden);
            var confirm = (await AuthEndpoints.TryReadJsonAsync<LeaveRequest>(http, cancellationToken))?.Confirm == true;
            var result = await leave.ExecuteAsync(AuthEndpoints.UserOf(principal), new HouseholdId(id), confirm, cancellationToken);
            return result.IsFailure
                ? Refused(result.Error!.Code)
                : Results.Ok(new LeaveResponse(result.Value == LeaveOutcome.Deleted ? LeaveOutcomes.Deleted : LeaveOutcomes.Left));
        });

        households.MapPost("", async Task<IResult> (
            HttpRequest http, ClaimsPrincipal principal, CreateHousehold create, CancellationToken cancellationToken) =>
        {
            if (await AuthEndpoints.TryReadJsonAsync<CreateHouseholdRequest>(http, cancellationToken) is not { HouseholdId: var id } || id == Guid.Empty)
                return AuthEndpoints.Refused(HouseholdErrors.Malformed, StatusCodes.Status400BadRequest);
            var result = await create.ExecuteAsync(AuthEndpoints.UserOf(principal), new HouseholdId(id), cancellationToken);
            return result.IsFailure ? Refused(result.Error!.Code) : Results.StatusCode(StatusCodes.Status201Created);
        });

        var invitations = app.MapGroup("/api/invitations").RequireAuthorization();

        invitations.MapPost("/preview", async Task<IResult> (
            HttpRequest http, ClaimsPrincipal principal, PreviewInvitation preview, CancellationToken cancellationToken) =>
        {
            var request = await AuthEndpoints.TryReadJsonAsync<InvitationCodeRequest>(http, cancellationToken);
            var result = await preview.ExecuteAsync(AuthEndpoints.UserOf(principal), request?.Code, cancellationToken);
            if (result.IsFailure)
                return Refused(result.Error!.Code);
            var p = result.Value;
            return Results.Ok(new InvitationPreviewResponse(p.Household.Value, p.InviterName, p.BabyNames, AuthEndpoints.RoleName(p.Role), p.ExpiresAt, p.AlreadyMember));
        });

        invitations.MapPost("/accept", async Task<IResult> (
            HttpRequest http, ClaimsPrincipal principal, AcceptInvitation accept, CancellationToken cancellationToken) =>
        {
            var request = await AuthEndpoints.TryReadJsonAsync<InvitationCodeRequest>(http, cancellationToken);
            var result = await accept.ExecuteAsync(AuthEndpoints.UserOf(principal), request?.Code, cancellationToken);
            if (result.IsFailure)
                return Refused(result.Error!.Code);
            var outcome = result.Value.Outcome switch
            {
                AcceptOutcome.Rejoined => AcceptOutcomes.Rejoined,
                AcceptOutcome.AlreadyMember => AcceptOutcomes.AlreadyMember,
                _ => AcceptOutcomes.Joined,
            };
            return Results.Ok(new AcceptInvitationResponse(result.Value.Household.Value, AuthEndpoints.RoleName(result.Value.Role), outcome));
        });
    }

    private static IResult Refused(string code) =>
        AuthEndpoints.Refused(code, Statuses.TryGetValue(code, out var status) ? status : StatusCodes.Status400BadRequest);
}
```

In `AuthEndpoints.cs`: make `RoleName` `internal static`, and change `/api/me`'s answer to:

```csharp
            return Results.Ok(new MeResponse(
                User(me.User),
                me.Membership is null ? null : new MembershipDto(me.Membership.HouseholdId.Value, RoleName(me.Membership.Role)),
                [.. me.Devices.Select(d => new DeviceSessionDto(d.Id.Value, d.DeviceName, d.LastUsedAt, d.Id == current))],
                me.Consent is null ? null : new ConsentDto(me.Consent.Wording, me.Consent.GivenAt),
                [.. (me.Memberships ?? []).Select(m => new MembershipDto(m.HouseholdId.Value, RoleName(m.Role), m.JoinedAt))]));
```

(`household` keeps its old shape, no `joinedAt`, so 0.11.0 phones and `AuthApiTests` see exactly what they saw.)

In `Program.cs` (add `using Coo.Api.Application.Households;`):

```csharp
// Members and invitations (Spec 011b §10). The membership store is a singleton over the data source, so these are too.
builder.Services.AddSingleton(sp => new CreateInvitation(
    sp.GetRequiredService<IHouseholdMembershipStore>(), sp.GetRequiredService<TimeProvider>(), RandomNumberGenerator.GetInt32));
builder.Services.AddSingleton<WithdrawInvitation>();
builder.Services.AddSingleton<ListMembers>();
builder.Services.AddSingleton<RemoveMember>();
builder.Services.AddSingleton<LeaveHousehold>();
builder.Services.AddSingleton<CreateHousehold>();
builder.Services.AddSingleton<PreviewInvitation>();
builder.Services.AddSingleton<AcceptInvitation>();

// Account deletion runs in the membership store's locked transaction (Task 5): no scoped account store any more.
builder.Services.AddSingleton<DeleteAccount>();
```

and remove the old `builder.Services.AddScoped<DeleteAccount>();`. Map `app.MapHouseholdEndpoints();` after
`app.MapSyncEndpoints();`, and update the version comment to `"0.12.0+<commit>"`.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/Coo.Api.IntegrationTests`
Expected: PASS, the existing auth and sync tests included (`AuthApiTests`' `me!.Household` equality still holds).

- [ ] **Step 6: Commit**

```bash
git add src/Backend/Coo.Api src/Shared/Coo.Contracts/Auth tests/Coo.Api.IntegrationTests/HouseholdApiTests.cs
git commit -m "Api: the household and invitation endpoints; /api/me lists every active family (Spec 011b §10.1)"
```

---

### Task 7: The rate limits: preview and accept per user and per address, invitations per day

**Files:**
- Modify: `src/Backend/Coo.Api/Hosting/HouseholdEndpoints.cs`, `src/Backend/Coo.Api/Program.cs` (the limiter, the
  middleware order), `tests/Coo.Api.IntegrationTests/TestApi.cs` (the limits)
- Test: `tests/Coo.Api.IntegrationTests/InvitationRateLimitTests.cs`

**Interfaces:**
- Consumes: Task 6's endpoints; `AuthEndpoints.AddAuthRateLimiting` (its `OnRejected` writes `{ "code": "auth.rate-limited" }`).
- Produces: `InvitationLimits(int PerUserPerHour, int PerAddressPerHour, int CreatePerUserPerDay)` with `Default = new(10, 30, 20)`;
  settings `Invitations:PerUserPerHour`, `Invitations:PerAddressPerHour`, `Invitations:CreatePerUserPerDay`;
  `HouseholdEndpoints.AddInvitationRateLimiting(this IServiceCollection, InvitationLimits)`;
  `TestApi.Create(…, InvitationLimits? limits = null)`: high limits by default, so no other test meets one by accident.

- [ ] **Step 1: Write the failing tests**

In `tests/Coo.Api.IntegrationTests/TestApi.cs`, add the parameter (the API's settings come from configuration; the default
here is high):

```csharp
    /// <param name="limits">The invitation limits; null makes them high enough that no test meets them by accident.</param>
    public static WebApplicationFactory<Program> Create(
        string connectionString, IExternalIdentityVerifier? google = null, int? permitsPerMinute = 1000, Action<IWebHostBuilder>? configure = null,
        InvitationLimits? limits = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
        {
            host.UseEnvironment("Production");
            host.UseSetting($"ConnectionStrings:{StartupChecks.ConnectionStringName}", connectionString);
            host.UseSetting(StartupChecks.GoogleWebClientIdKey, ClientId);
            host.UseSetting(StartupChecks.SigningKeysSection + ":k1", SigningKey);
            var l = limits ?? new InvitationLimits(1000, 1000, 1000);
            host.UseSetting("Invitations:PerUserPerHour", l.PerUserPerHour.ToString(CultureInfo.InvariantCulture));
            host.UseSetting("Invitations:PerAddressPerHour", l.PerAddressPerHour.ToString(CultureInfo.InvariantCulture));
            host.UseSetting("Invitations:CreatePerUserPerDay", l.CreatePerUserPerDay.ToString(CultureInfo.InvariantCulture));
            if (permitsPerMinute is { } permits)
                host.UseSetting("Auth:PermitsPerMinute", permits.ToString(CultureInfo.InvariantCulture));
            if (google is not null)
                host.ConfigureTestServices(services => services.AddSingleton(google));
            configure?.Invoke(host);
        });
```

`tests/Coo.Api.IntegrationTests/InvitationRateLimitTests.cs`:

```csharp
using System.Net;
using Coo.Api.Hosting;
using Coo.Contracts.Households;

namespace Coo.Api.IntegrationTests;

/// <summary>Spec 011b §10.2: per user and per address, failures and successes alike. Each test has its own API (in-memory limiters).</summary>
public sealed class InvitationRateLimitTests(AuthApi api) : IClassFixture<AuthApi>
{
    [Fact]
    public async Task Preview_OverThePerUserLimit_Answers429WithTheCode()
    {
        // Arrange
        await using var factory = TestApi.Create(api.ConnectionString, api.Google, limits: new InvitationLimits(3, 1000, 1000));
        using var client = factory.CreateClient();
        var user = await SyncCalls.SignInWithConsentAsync(client, "rl-user", Calls.Id(7101), Calls.Id(7102));

        // Act
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 4; i++)
            codes.Add((await Calls.SendAsync(client, HttpMethod.Post, HouseholdCalls.Preview, user.AccessToken, new InvitationCodeRequest("ZZZZ-ZZZZ"))).StatusCode);
        var limited = await Calls.SendAsync(client, HttpMethod.Post, HouseholdCalls.Accept, user.AccessToken, new InvitationCodeRequest("ZZZZ-ZZZZ"));

        // Assert
        codes.Should().Equal(HttpStatusCode.NotFound, HttpStatusCode.NotFound, HttpStatusCode.NotFound, (HttpStatusCode)429);
        limited.StatusCode.Should().Be((HttpStatusCode)429, "preview and accept share one limiter");
        (await Calls.CodeAsync(limited)).Should().Be("auth.rate-limited");
    }

    [Fact]
    public async Task Preview_OverThePerAddressLimit_Answers429ForAnotherUserToo()
    {
        // Arrange: every test client comes from one address.
        await using var factory = TestApi.Create(api.ConnectionString, api.Google, limits: new InvitationLimits(1000, 2, 1000));
        using var client = factory.CreateClient();
        var first = await SyncCalls.SignInWithConsentAsync(client, "ra-1", Calls.Id(7111), Calls.Id(7112));
        var second = await SyncCalls.SignInWithConsentAsync(client, "ra-2", Calls.Id(7113), Calls.Id(7114));
        await Calls.SendAsync(client, HttpMethod.Post, HouseholdCalls.Preview, first.AccessToken, new InvitationCodeRequest("ZZZZ-ZZZZ"));
        await Calls.SendAsync(client, HttpMethod.Post, HouseholdCalls.Preview, first.AccessToken, new InvitationCodeRequest("ZZZZ-ZZZZ"));

        // Act
        var response = await Calls.SendAsync(client, HttpMethod.Post, HouseholdCalls.Preview, second.AccessToken, new InvitationCodeRequest("ZZZZ-ZZZZ"));

        // Assert
        response.StatusCode.Should().Be((HttpStatusCode)429);
    }

    [Fact]
    public async Task CreateInvitation_OverTheDailyLimit_Answers429()
    {
        // Arrange
        await using var factory = TestApi.Create(api.ConnectionString, api.Google, limits: new InvitationLimits(1000, 1000, 2));
        using var client = factory.CreateClient();
        var family = Calls.Id(7121);
        var owner = await SyncCalls.SignInWithConsentAsync(client, "rc-owner", Calls.Id(7122), family);
        await HouseholdCalls.InviteAsync(client, owner.AccessToken, family);
        await HouseholdCalls.InviteAsync(client, owner.AccessToken, family);

        // Act
        var third = await Calls.SendAsync(client, HttpMethod.Post, HouseholdCalls.Invitations(family), owner.AccessToken,
            new CreateInvitationRequest(Contracts.Auth.AuthRoles.Parent));

        // Assert
        third.StatusCode.Should().Be((HttpStatusCode)429);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Coo.Api.IntegrationTests --filter "FullyQualifiedName~InvitationRateLimitTests"`
Expected: FAIL to compile (`InvitationLimits` not found).

- [ ] **Step 3: The limiter**

In `HouseholdEndpoints.cs` add `using System.Threading.RateLimiting;`, and above the class:

```csharp
/// <summary>Spec 011b §10.2. In memory, one instance; a restart resets them (accepted).</summary>
public sealed record InvitationLimits(int PerUserPerHour, int PerAddressPerHour, int CreatePerUserPerDay)
{
    public static InvitationLimits Default { get; } = new(10, 30, 20);
}

/// <summary>Marks the endpoints that take a code: preview and accept share one chained limiter.</summary>
internal sealed class InvitationCodeLimit;
```

Inside the class:

```csharp
    public const string CreatePolicy = "invitation-create";

    /// <summary>
    /// The chained limiter for preview and accept (per user, then per client address) and the daily creation limit. It
    /// needs <c>UseRateLimiter</c> after <c>UseAuthentication</c>, so the user partition sees the <c>sub</c> claim.
    /// </summary>
    public static IServiceCollection AddInvitationRateLimiting(this IServiceCollection services, InvitationLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        return services.AddRateLimiter(options =>
        {
            options.GlobalLimiter = PartitionedRateLimiter.CreateChained(
                PartitionedRateLimiter.Create<HttpContext, string>(http => IsCodeCall(http)
                    ? Hourly("user:" + UserKey(http), limits.PerUserPerHour)
                    : RateLimitPartition.GetNoLimiter("none")),
                PartitionedRateLimiter.Create<HttpContext, string>(http => IsCodeCall(http)
                    ? Hourly("address:" + (http.Connection.RemoteIpAddress?.ToString() ?? "unknown"), limits.PerAddressPerHour)
                    : RateLimitPartition.GetNoLimiter("none")));
            options.AddPolicy(CreatePolicy, http => RateLimitPartition.GetFixedWindowLimiter(
                "create:" + UserKey(http),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = limits.CreatePerUserPerDay, Window = TimeSpan.FromDays(1), QueueLimit = 0 }));
        });
    }

    private static bool IsCodeCall(HttpContext http) => http.GetEndpoint()?.Metadata.GetMetadata<InvitationCodeLimit>() is not null;

    private static string UserKey(HttpContext http) => http.User.FindFirst(AccessTokenClaims.User)?.Value ?? "anonymous";

    private static RateLimitPartition<string> Hourly(string key, int permits) =>
        RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions { PermitLimit = permits, Window = TimeSpan.FromHours(1), QueueLimit = 0 });
```

Attach them: the create route ends in `.RequireRateLimiting(CreatePolicy);` and the invitations group becomes
`app.MapGroup("/api/invitations").RequireAuthorization().WithMetadata(new InvitationCodeLimit());`.

In `Program.cs`, after the household use cases:

```csharp
builder.Services.AddInvitationRateLimiting(new InvitationLimits(
    builder.Configuration.GetValue("Invitations:PerUserPerHour", InvitationLimits.Default.PerUserPerHour),
    builder.Configuration.GetValue("Invitations:PerAddressPerHour", InvitationLimits.Default.PerAddressPerHour),
    builder.Configuration.GetValue("Invitations:CreatePerUserPerDay", InvitationLimits.Default.CreatePerUserPerDay)));
```

and reorder the middleware: authentication before the limiter, so a policy can partition on the user (spec §10.2);
forwarded headers stay first:

```csharp
app.UseForwardedHeaders(forwarded);

// After authentication (Spec 011b §10.2): the invitation limiter partitions on the user claim; the address partitions
// still see the forwarded client address, because forwarded headers run first (Spec 010 §4.1).
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();
```

(`AddRateLimiter` configures the same options twice, once for the auth policy and once here; both apply.)

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Coo.Api.IntegrationTests`
Expected: PASS, the auth limiter's tests included (it still partitions on the address).

- [ ] **Step 5: Commit**

```bash
git add src/Backend/Coo.Api tests/Coo.Api.IntegrationTests
git commit -m "Api: chained rate limits for invitation codes and a daily limit for creating them (Spec 011b §10.2)"
```

---

### Task 8: `assetlinks.json`, the fallback page, the fingerprint check, and API 0.12.0

**Files:**
- Create: `src/Backend/Coo.Api/Hosting/AppLinkEndpoints.cs`
- Modify: `src/Backend/Coo.Api/Hosting/StartupChecks.cs` (`FingerprintsSection`, `SelectFingerprints`),
  `src/Backend/Coo.Api/Program.cs`, `src/Backend/Coo.Api/Coo.Api.csproj` (0.12.0), `deploy/docker-compose.prod.yml`,
  `deploy/deploy-prod.sh`, `deploy/.env.example`, `DEPLOY.md`, `tests/Coo.Api.IntegrationTests/TestApi.cs` (the fingerprint)
- Test: `tests/Coo.Api.IntegrationTests/AppLinkTests.cs`, `tests/Coo.Api.IntegrationTests/StartupChecksTests.cs`

**Interfaces:**
- Consumes: `InvitationCode.Parse`, `InvitationLinks`.
- Produces: `StartupChecks.FingerprintsSection = "AppLinks:Sha256CertFingerprints"`;
  `StartupChecks.SelectFingerprints(IEnumerable<string?> values, bool isProduction) → IReadOnlyList<string>`;
  `AppLinkEndpoints.MapAppLinkEndpoints(this IEndpointRouteBuilder app, IReadOnlyList<string> fingerprints)`;
  `GET /.well-known/assetlinks.json`, `GET /i/{**code}`.

- [ ] **Step 1: Write the failing tests**

Append to `StartupChecksTests`:

```csharp
    [Fact]
    public void SelectFingerprints_ProductionWithNone_ThrowsNamingTheSetting()
    {
        // Act
        var act = () => StartupChecks.SelectFingerprints([], isProduction: true);

        // Assert
        act.Should().Throw<InvalidOperationException>().WithMessage("*AppLinks:Sha256CertFingerprints*");
    }

    [Theory]
    [InlineData("6E:7E:3C")]
    [InlineData("6E7E3C4E889535CEFE901C8CD2C537D1E42CFB914DB72DBBF57AC8BD153A287C")]
    [InlineData("GG:7E:3C:4E:88:95:35:CE:FE:90:1C:8C:D2:C5:37:D1:E4:2C:FB:91:4D:B7:2D:BB:F5:7A:C8:BD:15:3A:28:7C")]
    public void SelectFingerprints_Malformed_ThrowsInEveryEnvironment(string value)
    {
        // Act
        var act = () => StartupChecks.SelectFingerprints([value], isProduction: false);

        // Assert
        act.Should().Throw<InvalidOperationException>().WithMessage("*32 colon-separated hex bytes*");
    }

    [Fact]
    public void SelectFingerprints_LowerCaseAndBlanks_AreNormalisedAndSkipped()
    {
        // Act
        var fingerprints = StartupChecks.SelectFingerprints(
            ["6e:7e:3c:4e:88:95:35:ce:fe:90:1c:8c:d2:c5:37:d1:e4:2c:fb:91:4d:b7:2d:bb:f5:7a:c8:bd:15:3a:28:7c", " ", null], isProduction: true);

        // Assert
        fingerprints.Should().Equal(TestApi.Fingerprint);
    }

    [Fact]
    public void SelectFingerprints_DevelopmentWithNone_IsAnEmptyList()
    {
        // Act
        var fingerprints = StartupChecks.SelectFingerprints([], isProduction: false);

        // Assert
        fingerprints.Should().BeEmpty();
    }
```

`tests/Coo.Api.IntegrationTests/AppLinkTests.cs`:

```csharp
using System.Net;
using System.Text.Json;

namespace Coo.Api.IntegrationTests;

/// <summary>Spec 011b §4.2–§4.3: the App Link's file and the page a browser shows.</summary>
public sealed class AppLinkTests(AuthApi api) : IClassFixture<AuthApi>
{
    [Fact]
    public async Task AssetLinks_Anonymous_ListsThePackageAndTheConfiguredFingerprint()
    {
        // Arrange
        using var client = api.Factory.CreateClient(new() { AllowAutoRedirect = false });

        // Act
        var response = await client.GetAsync(new Uri("/.well-known/assetlinks.json", UriKind.Relative));
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        var statement = json.RootElement.EnumerateArray().Single();
        statement.GetProperty("relation").EnumerateArray().Single().GetString().Should().Be("delegate_permission/common.handle_all_urls");
        var target = statement.GetProperty("target");
        target.GetProperty("namespace").GetString().Should().Be("android_app");
        target.GetProperty("package_name").GetString().Should().Be("com.lechdigital.coo");
        target.GetProperty("sha256_cert_fingerprints").EnumerateArray().Select(f => f.GetString()).Should().Equal(TestApi.Fingerprint);
    }

    [Theory]
    [InlineData("/i/ABCDEFGH")]
    [InlineData("/i/abcd-efgh")]
    public async Task Page_AWellFormedCode_ShowsItHyphenatedWithThePlayLinkAndTheHeaders(string path)
    {
        // Arrange
        using var client = api.Factory.CreateClient();

        // Act
        var response = await client.GetAsync(new Uri(path, UriKind.Relative));
        var html = await response.Content.ReadAsStringAsync();

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/html");
        html.Should().Contain("ABCD-EFGH").And.Contain("https://play.google.com/store/apps/details?id=com.lechdigital.coo")
            .And.Contain("Settings → Family → Join a family").And.Contain("https://lechdigital.nl/projects/coo/privacy")
            .And.NotContain("<script").And.NotContain("<img");
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        response.Headers.GetValues("Referrer-Policy").Single().Should().Be("no-referrer");
        response.Headers.GetValues("X-Robots-Tag").Single().Should().Be("noindex");
        response.Headers.GetValues("Content-Security-Policy").Single().Should().Be("default-src 'none'; style-src 'unsafe-inline'");
        response.Headers.Contains("Set-Cookie").Should().BeFalse();
    }

    [Theory]
    [InlineData("/i/ABC")]
    [InlineData("/i/<script>alert(1)</script>")]
    [InlineData("/i/")]
    public async Task Page_AMalformedCode_ShowsThePageWithoutACode(string path)
    {
        // Arrange
        using var client = api.Factory.CreateClient();

        // Act
        var response = await client.GetAsync(new Uri(path, UriKind.Relative));
        var html = await response.Content.ReadAsStringAsync();

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        html.Should().Contain("enter the code from your invitation").And.NotContain("<script>alert");
    }

    [Fact]
    public async Task Page_AnUnknownButWellFormedCode_IsNeverLookedUp()
    {
        // Arrange: no oracle (spec §4.3): a code nobody created reads exactly like one that exists.
        using var client = api.Factory.CreateClient();

        // Act
        var html = await (await client.GetAsync(new Uri("/i/ZZZZZZZZ", UriKind.Relative))).Content.ReadAsStringAsync();

        // Assert
        html.Should().Contain("ZZZZ-ZZZZ").And.NotContain("Andrzej");
    }
}
```

Add one Production start-up test to `StartupChecksTests`, beside `Start_ProductionWithoutKeys_FailsAtStartUp`:

```csharp
    [Fact]
    public async Task Start_ProductionWithoutAFingerprint_FailsAtStartUp()
    {
        // Arrange: as the two tests above, start-up fails before the database is touched.
        await using var api = TestApi.Create("Host=unused", configure: h => h.UseSetting(StartupChecks.FingerprintsSection + ":0", ""));

        // Act
        var act = () => api.CreateClient();

        // Assert
        act.Should().Throw<InvalidOperationException>().WithMessage("*AppLinks:Sha256CertFingerprints*");
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Coo.Api.IntegrationTests --filter "FullyQualifiedName~AppLinkTests|FullyQualifiedName~StartupChecksTests"`
Expected: FAIL to compile (`SelectFingerprints` not found).

- [ ] **Step 3: The check and the endpoints**

Append to `StartupChecks`:

```csharp
    public const string FingerprintsSection = "AppLinks:Sha256CertFingerprints";

    private static readonly System.Text.RegularExpressions.Regex Fingerprint =
        new("^([0-9A-F]{2}:){31}[0-9A-F]{2}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// The signing certificates' SHA-256 fingerprints for <c>assetlinks.json</c> (Spec 011b §4.2): the debug key's now, the Play
    /// app-signing key's in Increment 18. A malformed one fails in every environment; Production refuses to start without one.
    /// </summary>
    public static IReadOnlyList<string> SelectFingerprints(IEnumerable<string?> values, bool isProduction)
    {
        ArgumentNullException.ThrowIfNull(values);
        var list = new List<string>();
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
                continue;
            var normalised = value.Trim().ToUpperInvariant();
            if (!Fingerprint.IsMatch(normalised))
                throw new InvalidOperationException($"{FingerprintsSection} has '{value}': each must be 32 colon-separated hex bytes (keytool -list -v, SHA256).");
            list.Add(normalised);
        }

        return isProduction && list.Count == 0
            ? throw new InvalidOperationException(
                $"{FingerprintsSection} has no fingerprint. In production it comes from /opt/coo/.env (APPLINKS_SHA256_DEBUG) "
                + "through deploy/docker-compose.prod.yml (DEPLOY.md, \"App Links\").")
            : list;
    }
```

`src/Backend/Coo.Api/Hosting/AppLinkEndpoints.cs`:

```csharp
using System.Net;
using System.Text.Json.Serialization;
using Coo.Domain.Households;

namespace Coo.Api.Hosting;

internal sealed record AssetLinkTarget(
    [property: JsonPropertyName("namespace")] string Namespace,
    [property: JsonPropertyName("package_name")] string PackageName,
    [property: JsonPropertyName("sha256_cert_fingerprints")] IReadOnlyList<string> Fingerprints);

internal sealed record AssetLinkStatement(
    [property: JsonPropertyName("relation")] IReadOnlyList<string> Relation,
    [property: JsonPropertyName("target")] AssetLinkTarget Target);

/// <summary>
/// The Android App Link's half on the server (Spec 011b §4.2–§4.3): <c>assetlinks.json</c>, and the page a browser shows when
/// the link does not open Coo. The page never looks the code up: it checks its shape and prints it, names nobody, and
/// says nothing about validity, so it is no oracle. English only for now (R1; seven languages in Increment 18).
/// </summary>
internal static class AppLinkEndpoints
{
    public const string PackageName = "com.lechdigital.coo";

    public static void MapAppLinkEndpoints(this IEndpointRouteBuilder app, IReadOnlyList<string> fingerprints)
    {
        AssetLinkStatement[] statements =
        [
            new(["delegate_permission/common.handle_all_urls"], new AssetLinkTarget("android_app", PackageName, fingerprints)),
        ];
        app.MapGet("/.well-known/assetlinks.json", () => Results.Json(statements, contentType: "application/json"));

        app.MapGet("/i/{**code}", (HttpContext http, string? code) =>
        {
            var headers = http.Response.Headers;
            headers.CacheControl = "no-store";
            headers["Referrer-Policy"] = "no-referrer";
            headers["X-Robots-Tag"] = "noindex";
            headers.ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'";
            var parsed = InvitationCode.Parse(code);
            return Results.Content(Page(parsed.IsSuccess ? parsed.Value.Display : null), "text/html; charset=utf-8");
        });
    }

    internal static string Page(string? code)
    {
        var step2 = code is null
            ? "Open Coo, go to <strong>Settings → Family → Join a family</strong>, and enter the code from your invitation."
            : $"Open Coo, go to <strong>Settings → Family → Join a family</strong>, and enter this code: <strong class=\"code\">{WebUtility.HtmlEncode(code)}</strong>";
        return $$"""
            <!doctype html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <meta name="robots" content="noindex">
            <title>Coo — Baby Log &amp; Sleep</title>
            <style>
            body { font-family: system-ui, -apple-system, "Segoe UI", sans-serif; background: #F1EDE5; color: #2B2620; margin: 0; padding: 32px 20px; line-height: 1.5; }
            main { max-width: 34rem; margin: 0 auto; }
            h1 { font-size: 1.5rem; margin: 0 0 12px; }
            ol { padding-left: 1.25rem; }
            li { margin: 10px 0; }
            .code { font-family: ui-monospace, monospace; font-size: 1.15rem; letter-spacing: 0.08em; }
            a { color: #8A4B22; }
            footer { margin-top: 32px; font-size: 0.9rem; color: #6B6258; }
            </style>
            </head>
            <body>
            <main>
            <h1>Coo — Baby Log &amp; Sleep</h1>
            <p>You've been invited to share a baby's log on Coo, a baby tracker for parents.</p>
            <ol>
            <li>Install Coo from <a href="https://play.google.com/store/apps/details?id={{PackageName}}">Google Play</a>.</li>
            <li>{{step2}}</li>
            </ol>
            <p>The code can be used once, within 72 hours of being sent. You'll need a Google account to join.</p>
            <footer><a href="https://lechdigital.nl/projects/coo/privacy">Privacy policy</a> · <a href="https://lechdigital.nl">lechdigital.nl</a></footer>
            </main>
            </body>
            </html>
            """;
    }
}
```

In `Program.cs`, after the auth settings:

```csharp
// App Links (Spec 011b §4.2): Production refuses to start without a well-formed signing-certificate fingerprint.
var fingerprints = StartupChecks.SelectFingerprints(
    builder.Configuration.GetSection(StartupChecks.FingerprintsSection).GetChildren().Select(c => c.Value),
    builder.Environment.IsProduction());
```

and map `app.MapAppLinkEndpoints(fingerprints);` after `app.MapHouseholdEndpoints();`.

`TestApi.cs` (the API as production runs it) now needs a fingerprint, or no integration test starts:

```csharp
    public const string Fingerprint = "6E:7E:3C:4E:88:95:35:CE:FE:90:1C:8C:D2:C5:37:D1:E4:2C:FB:91:4D:B7:2D:BB:F5:7A:C8:BD:15:3A:28:7C";
```

and, in `Create`, after the signing key: `host.UseSetting(StartupChecks.FingerprintsSection + ":0", Fingerprint);`.

`Coo.Api.csproj`: `<Version>0.12.0</Version>`.

- [ ] **Step 4: The deployment setting**

`deploy/docker-compose.prod.yml`, under the `coo-api` environment, after the sign-in lines:

```yaml
      # App Links (Spec 011b §4.2): the API refuses to start without a signing-certificate fingerprint.
      AppLinks__Sha256CertFingerprints__0: ${APPLINKS_SHA256_DEBUG:?set APPLINKS_SHA256_DEBUG in .env}
```

`deploy/.env.example`, appended:

```sh
# App Links (Spec 011b §4.2). deploy-prod.sh adds it when missing. The SHA-256 of the key that signs the installed APK:
# the debug key until Increment 18 (keytool -list -v, DEPLOY.md "App Links"). Not a secret.
APPLINKS_SHA256_DEBUG=
```

`deploy/deploy-prod.sh`: next to `GOOGLE_WEB_CLIENT_ID`, add

```sh
APPLINKS_SHA256_DEBUG="${COO_APPLINKS_SHA256_DEBUG:-6E:7E:3C:4E:88:95:35:CE:FE:90:1C:8C:D2:C5:37:D1:E4:2C:FB:91:4D:B7:2D:BB:F5:7A:C8:BD:15:3A:28:7C}"
```

and inside the `ssh "$HOST" "set -e; …"` block, after the signing-key block:

```sh
  if ! grep -q '^APPLINKS_SHA256_DEBUG=.' $REMOTE_DIR/.env; then
    sed -i '/^APPLINKS_SHA256_DEBUG=/d' $REMOTE_DIR/.env
    printf 'APPLINKS_SHA256_DEBUG=%s\n' '$APPLINKS_SHA256_DEBUG' >> $REMOTE_DIR/.env
    echo '    Added the App Link fingerprint to .env.'
  fi
```

`DEPLOY.md`: add after "Sync (Spec 011)":

```markdown
## Members and invitations (Spec 011b)

The `MembersAndInvitations` migration runs at start-up: it adds `invitations` (only the code's SHA-256 is stored).

- `SyncHistoryCleanupJob` also deletes every invitation whose expiry has passed (used and withdrawn ones included), so
  no invitation outlives its 72 hours by more than a day. Its log line counts them with the sync rows.
- Preview and accept are limited per user (10 an hour) and per client address (30 an hour); creating invitations to 20
  per user a day (`Invitations:*` settings override them). In memory: a restart resets them.
- Codes travel in request bodies; `/i/{code}` carries one in its path, so **no request lines are logged**
  (`Microsoft.AspNetCore: Warning`) and the Caddy block for `api.coo.lechdigital.nl` has **no `log` directive**. Keep it
  that way.

### App Links

`coo-api` refuses to start without `AppLinks__Sha256CertFingerprints__0`, from `/opt/coo/.env`:

| `.env` | Becomes | What |
|---|---|---|
| `APPLINKS_SHA256_DEBUG` | `AppLinks__Sha256CertFingerprints__0` | The SHA-256 of the key that signs the installed APK: the debug key until Increment 18, when the Play app-signing key's is added as `__1`. Not a secret. |

`deploy-prod.sh` adds it when missing. The value comes from:

    keytool -list -v -keystore "$HOME/Library/Application Support/Xamarin/Mono for Android/debug.keystore" \
      -alias androiddebugkey -storepass android -keypass android | grep SHA256

Check after a deploy:

    curl -s https://api.coo.lechdigital.nl/.well-known/assetlinks.json
    ssh coldstart-prod "docker exec coo-postgres psql -U coo -d coo -tAc \"SELECT count(*) FROM information_schema.tables WHERE table_name = 'invitations'\""

Expected: the JSON lists `com.lechdigital.coo` with the fingerprint; `1`.
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test Coo.Tests.slnf` (Docker running)
Expected: all PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Backend/Coo.Api deploy DEPLOY.md tests/Coo.Api.IntegrationTests
git commit -m "Api: assetlinks.json, the invitation page, the fingerprint start-up check; 0.12.0 (Spec 011b §4.2, §4.3)"
```

---

### Task 9: Deploy the server (0.12.0)

**Files:** none (the deploy uses Task 8's script and `DEPLOY.md`).

**Interfaces:**
- Consumes: Tasks 1–8, committed.
- Produces: production at `0.12.0+<commit>` with `invitations`; the phone tasks build against it.

- [ ] **Step 1: The full check, then deploy** (Docker Desktop running; the owner allows the agent to run this)

```sh
git status --porcelain          # must print nothing
dotnet test Coo.Tests.slnf
./deploy/deploy-prod.sh --confirm
```

Expected: `Added the App Link fingerprint to .env.` (first time only), the script's test run green, and
`Healthy: {"status":"ok","version":"0.12.0+<commit>","database":"ok"}`.

- [ ] **Step 2: Smoke-test production** (read-only)

```sh
curl -s https://api.coo.lechdigital.nl/health
curl -s https://api.coo.lechdigital.nl/.well-known/assetlinks.json
curl -s -D - -o /dev/null https://api.coo.lechdigital.nl/i/ABCD-EFGH | grep -iE "^HTTP|cache-control|x-robots-tag|content-security-policy"
curl -s -o /dev/null -w "%{http_code}\n" -X POST https://api.coo.lechdigital.nl/api/invitations/preview
curl -s -o /dev/null -w "%{http_code}\n" https://api.coo.lechdigital.nl/api/households/7a1c0000-0000-7000-8000-000000000001/members
ssh coldstart-prod "docker exec coo-postgres psql -U coo -d coo -tAc \"SELECT count(*) FROM information_schema.tables WHERE table_name = 'invitations'\""
```

Expected: `"version":"0.12.0+…"`; the JSON with the debug fingerprint; `HTTP/2 200` with `no-store`, `noindex` and the CSP;
`401`; `401`; `1`. The 0.11.0 app on the phones keeps syncing (nothing it calls changed shape).

---
## The phone

### Task 10: The phone's ports, the account's families, and the home family (`Coo.Mobile.Application/Households`)

**Files:**
- Create: `src/Mobile/Coo.Mobile.Application/Households/FamilyPorts.cs`, `src/Mobile/Coo.Mobile.Application/Households/Families.cs`,
  `src/Mobile/Coo.Mobile.Application/Households/ProvisionalSettings.cs`
- Modify: `src/Mobile/Coo.Mobile.Application/Households/Households.cs` (`EnsureProvisionalHousehold` uses `ProvisionalSettings`),
  `src/Mobile/Coo.Mobile.Application/Auth/Account.cs` (`TrySetHomeAsync`), `src/Mobile/Coo.Mobile.Application/Auth/Ports.cs`
  (`MembershipInfo`, `AccountOverview.Memberships`), `src/Mobile/Coo.Mobile.Application/Sync/Ports.cs`
  (`ISyncControl.NotifyDataChanged`), `src/Mobile/Coo.Mobile.Application/Sync/SyncEngine.cs` (implement it),
  `src/Mobile/Coo.Mobile.Application/ServiceCollectionExtensions.cs`
- Test: `tests/Coo.Mobile.Application.Tests/FamiliesTests.cs`, `tests/Coo.Mobile.Application.Tests/AccountGenerationTests.cs` (append)

**Interfaces:**
- Consumes: `IHouseholdStore`, `IHouseholdMoves`, `ISyncState`, `ISessionStore`, `IAccountApi`, `IVersionClock`,
  `IDeviceLocale`, `Account`, `StoredEntry`, `BringOverPlan`, `InvitationCode`, `Role`, `HouseholdErrors`.
- Produces (all `namespace Coo.Mobile.Application.Households` unless noted):
  - `FamilyMembership(HouseholdId Household, Role Role, DateTimeOffset JoinedAt, string OwnerName, int MemberCount)`,
    `PendingInvitation(InvitationCode Code, DateTimeOffset ArrivedAt)`, `JoiningMarker(HouseholdId Joined, HouseholdId Source, string InviterName)`.
  - `IFamilyStore`: `ListAsync(UserId user, CancellationToken) → Task<IReadOnlyList<FamilyMembership>?>` (joined order; null
    before the first refresh for that account), `ReplaceAsync(UserId user, IReadOnlyList<FamilyMembership>, CancellationToken)`,
    `ClearAsync(CancellationToken)` (memberships and the joining marker), `GetPendingInvitationAsync`, `SetPendingInvitationAsync(PendingInvitation?)`,
    `GetJoiningAsync`, `SetJoiningAsync(JoiningMarker?)`, `WasCopiedAsync(HouseholdId source, HouseholdId target)`.
  - `CopyResult(int Copied, int Skipped)`; `IFamilyRecords`: `CountRecordsAsync(HouseholdId)`, `CopyAsync(BringOverPlan plan, UserId user, HlcStamp firstStamp) → Task<CopyResult?>`
    (null when already copied), `DeleteAndReplaceAsync(HouseholdId household, HouseholdSettings replacement)`,
    `CountPendingAsync(IReadOnlyCollection<HouseholdId>) → Task<int>`, `ListUnsharedEntriesAsync(HouseholdId) → Task<IReadOnlyList<StoredEntry>>`.
  - `IHouseholdApi` with the records `CreatedInvitation(Guid InvitationId, InvitationCode Code, Uri Link, DateTimeOffset ExpiresAt)`,
    `FamilyMember(UserId UserId, string Name, string Email, Role Role, DateTimeOffset JoinedAt, bool IsYou)`,
    `FamilyInvitation(Guid InvitationId, Role Role, UserId CreatedBy, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt)`,
    `FamilyMembers(IReadOnlyList<FamilyMember> Members, IReadOnlyList<FamilyInvitation> Invitations)`,
    `InvitationPreview(HouseholdId Household, string InviterName, IReadOnlyList<string> BabyNames, Role Role, DateTimeOffset ExpiresAt, bool AlreadyMember)`,
    `enum AcceptOutcome { Joined, Rejoined, AlreadyMember }`, `AcceptedInvitation(HouseholdId Household, Role Role, AcceptOutcome Outcome)`,
    `enum LeaveOutcome { Left, Deleted }`; exceptions `HouseholdUnavailableException`, `HouseholdRefusedException` (`Code`).
  - `FamilyOnPhone(HouseholdId Id, IReadOnlyList<string> BabyNames, FamilyMembership? Membership, bool IsShown, bool IsSoftLocked, bool IsHome, bool HasEverSynced)`
    with `IsShared`, `IsOwner`, `CanManage`, `MayBeBroughtOver`; `FamilyContents(int EntryCount, IReadOnlyList<string> BabyNames, IReadOnlyList<StoredEntry> Unshared, VolumeUnit Unit)`.
  - `Families`: `RefreshAsync`, `ListAsync`, `ShownAsync`, `SwitchAsync(HouseholdId)`, `HideSoftLockedAsync → bool`,
    `IsShownPausedAloneAsync → bool`, `RemoveFromPhoneAsync(HouseholdId)`, `SyncingHouseholdsAsync → IReadOnlyCollection<HouseholdId>`,
    `BringOverTargetsAsync(HouseholdId source) → IReadOnlyList<FamilyOnPhone>`, `ContentsAsync(HouseholdId) → FamilyContents`.
  - `ProvisionalSettings(IVersionClock clock, IDeviceLocale locale, TimeProvider time)` with `CreateAsync(CancellationToken) → HouseholdSettings`.
  - `Account.TrySetHomeAsync(long generation, HouseholdId household, CancellationToken) → Task<bool>`.
  - `MembershipInfo(HouseholdId Household, Role Role, DateTimeOffset JoinedAt)` and
    `AccountOverview(…, ConsentInfo? Consent = null, IReadOnlyList<MembershipInfo>? Memberships = null)` (`Coo.Mobile.Application.Auth`).
  - `ISyncControl.NotifyDataChanged()` (`SyncEngine` raises `DataChanged`).

- [ ] **Step 1: Write the failing tests**

`tests/Coo.Mobile.Application.Tests/FamiliesTests.cs`:

```csharp
using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;
using Coo.Domain.Households;
using Coo.Mobile.Application.Auth;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Application.Households;
using Coo.Mobile.Application.Sync;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Coo.Mobile.Application.Tests;

/// <summary>Spec 011b §7: the account's families, the shown one, switching, hiding a soft-locked one, removal from the phone.</summary>
public sealed class FamiliesTests : IDisposable
{
    internal static readonly UserId Me = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000c1"));
    internal static readonly AccountProfile Profile = new(Me, "Kasia", "kasia@example.com");
    internal static readonly HouseholdId Home = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a1"));
    internal static readonly HouseholdId Joined = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a2"));
    internal static readonly HouseholdId Local = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a3"));
    internal static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
    private static readonly DeviceId Phone = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000d1"));

    private readonly MemorySessions _sessions = new();
    private readonly IFamilyStore _store = Substitute.For<IFamilyStore>();
    private readonly IAccountApi _accounts = Substitute.For<IAccountApi>();
    private readonly IHouseholdApi _api = Substitute.For<IHouseholdApi>();
    private readonly IHouseholdStore _households = Substitute.For<IHouseholdStore>();
    private readonly IHouseholdMoves _moves = Substitute.For<IHouseholdMoves>();
    private readonly ISyncState _state = Substitute.For<ISyncState>();
    private readonly IFamilyRecords _records = Substitute.For<IFamilyRecords>();
    private readonly IVersionClock _clock = Substitute.For<IVersionClock>();
    private readonly IDeviceLocale _locale = Substitute.For<IDeviceLocale>();
    private readonly FakeTimeProvider _time = new(Now);
    private readonly Account _account;

    public FamiliesTests()
    {
        _account = new Account(_sessions);
        _clock.NextAsync(Arg.Any<CancellationToken>()).Returns(new HlcStamp(1_790_000_000_000, 0, Phone));
        _locale.TimeZoneId.Returns("Europe/Warsaw");
    }

    public void Dispose() => _account.Dispose();

    internal static HouseholdSettings Settings(HouseholdId id) =>
        HouseholdSettings.Create(id, VolumeUnit.Millilitres, "Europe/Amsterdam", new HlcStamp(1_790_000_000_000, 0, Phone)).Value;

    internal static BabyProfile Baby(HouseholdId household, string name) =>
        BabyProfile.Create(new BabyId(Guid.CreateVersion7(Now.AddMinutes(name.Length))), household, name, [], new DateOnly(2026, 8, 20), null,
            new HlcStamp(1_790_000_000_000, 0, Phone), new DateOnly(2026, 10, 6)).Value;

    private Families Families() => new(
        _store, _accounts, _api, _sessions, _households, _moves, _state, _records, new ProvisionalSettings(_clock, _locale, _time));

    private Task SignInAsync() => _account.SignedInAsync(
        new StoredSession("access", Now.AddMinutes(15), "refresh", Profile, Home, JoinPending: false), CancellationToken.None);

    /// <summary>Households on the phone, the first shown; each with the babies named.</summary>
    private void OnPhone(params (HouseholdId Id, string[] Babies)[] families)
    {
        _households.ListHouseholdsAsync(Arg.Any<CancellationToken>()).Returns([.. families.Select(f => Settings(f.Id))]);
        _moves.ActiveHouseholdAsync(Arg.Any<CancellationToken>()).Returns(families[0].Id);
        foreach (var (id, babies) in families)
            _households.ListBabiesAsync(id, Arg.Any<CancellationToken>()).Returns([.. babies.Select(b => Baby(id, b))]);
    }

    [Fact]
    public async Task RefreshAsync_TwoMemberships_StoresThemWithOwnerNamesAndCounts()
    {
        // Arrange
        await SignInAsync();
        _accounts.GetMeAsync(Arg.Any<CancellationToken>()).Returns(new AccountOverview(Profile, Home, "owner", [], null,
            [new MembershipInfo(Joined, Role.Parent, Now.AddDays(1)), new MembershipInfo(Home, Role.Owner, Now)]));
        _api.ListMembersAsync(Home, Arg.Any<CancellationToken>()).Returns(new FamilyMembers(
            [new FamilyMember(Me, "Kasia", "k@example.com", Role.Owner, Now, true)], []));
        _api.ListMembersAsync(Joined, Arg.Any<CancellationToken>()).Returns(new FamilyMembers(
            [new FamilyMember(new UserId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000c9")), "Andrzej", "a@example.com", Role.Owner, Now, false),
             new FamilyMember(Me, "Kasia", "k@example.com", Role.Parent, Now.AddDays(1), true)], []));

        // Act
        var refreshed = await Families().RefreshAsync(CancellationToken.None);

        // Assert
        refreshed.Should().BeTrue();
        await _store.Received(1).ReplaceAsync(Me, Arg.Is<IReadOnlyList<FamilyMembership>>(l =>
            l.Count == 2 && l[0] == new FamilyMembership(Home, Role.Owner, Now, "Kasia", 1)
            && l[1] == new FamilyMembership(Joined, Role.Parent, Now.AddDays(1), "Andrzej", 2)), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RefreshAsync_MembersCallFails_KeepsTheLastKnownNameAndCount()
    {
        // Arrange
        await SignInAsync();
        _accounts.GetMeAsync(Arg.Any<CancellationToken>()).Returns(new AccountOverview(Profile, Home, "owner", [], null,
            [new MembershipInfo(Joined, Role.Parent, Now)]));
        _store.ListAsync(Me, Arg.Any<CancellationToken>()).Returns([new FamilyMembership(Joined, Role.Parent, Now, "Andrzej", 2)]);
        _api.ListMembersAsync(Joined, Arg.Any<CancellationToken>()).ThrowsAsync(new HouseholdUnavailableException("offline"));

        // Act
        await Families().RefreshAsync(CancellationToken.None);

        // Assert
        await _store.Received(1).ReplaceAsync(Me, Arg.Is<IReadOnlyList<FamilyMembership>>(l => l.Single().OwnerName == "Andrzej" && l.Single().MemberCount == 2),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RefreshAsync_Offline_ChangesNothing()
    {
        // Arrange
        await SignInAsync();
        _accounts.GetMeAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new AuthUnavailableException("offline"));

        // Act
        var refreshed = await Families().RefreshAsync(CancellationToken.None);

        // Assert
        refreshed.Should().BeFalse();
        await _store.DidNotReceiveWithAnyArgs().ReplaceAsync(default, default!, default);
    }

    [Fact]
    public async Task RefreshAsync_AnswerForAnotherAccount_ChangesNothing()
    {
        // Arrange: the account changed while /api/me was in flight.
        await SignInAsync();
        var other = new AccountProfile(new UserId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000c8")), "Ola", "o@example.com");
        _accounts.GetMeAsync(Arg.Any<CancellationToken>()).Returns(new AccountOverview(other, Home, "owner", [], null, []));

        // Act
        var refreshed = await Families().RefreshAsync(CancellationToken.None);

        // Assert
        refreshed.Should().BeFalse();
        await _store.DidNotReceiveWithAnyArgs().ReplaceAsync(default, default!, default);
    }

    [Fact]
    public async Task ListAsync_AccountAndLocalFamilies_OrdersAccountFamiliesByJoiningThenLocalOnes()
    {
        // Arrange
        await SignInAsync();
        OnPhone((Local, ["Maja"]), (Joined, ["Maja"]), (Home, []));
        _store.ListAsync(Me, Arg.Any<CancellationToken>()).Returns([
            new FamilyMembership(Home, Role.Owner, Now, "Kasia", 1), new FamilyMembership(Joined, Role.Parent, Now.AddDays(1), "Andrzej", 2)]);
        _state.IsSoftLockedAsync(Joined, Arg.Any<CancellationToken>()).Returns(true);

        // Act
        var families = await Families().ListAsync(CancellationToken.None);

        // Assert
        families.Select(f => f.Id).Should().Equal(Home, Joined, Local);
        families[0].Should().Match<FamilyOnPhone>(f => f.IsHome && f.IsOwner && f.BabyNames.Count == 0 && !f.IsShown);
        families[1].Should().Match<FamilyOnPhone>(f => f.IsSoftLocked && f.CanManage && !f.IsOwner && f.BabyNames.Single() == "Maja");
        families[2].Should().Match<FamilyOnPhone>(f => f.IsShown && !f.IsShared);
    }

    [Fact]
    public async Task HideSoftLockedAsync_ShownIsSoftLockedAndAnotherExists_ShowsTheEarliestJoinedAccountFamily()
    {
        // Arrange: spec §8.2, ruling 13 of Spec 011.
        await SignInAsync();
        OnPhone((Joined, ["Maja"]), (Local, ["Leo"]), (Home, ["Maja"]));
        _store.ListAsync(Me, Arg.Any<CancellationToken>()).Returns([
            new FamilyMembership(Home, Role.Owner, Now, "Kasia", 1), new FamilyMembership(Joined, Role.Parent, Now.AddDays(1), "Andrzej", 2)]);
        _state.IsSoftLockedAsync(Joined, Arg.Any<CancellationToken>()).Returns(true);

        // Act
        var switched = await Families().HideSoftLockedAsync(CancellationToken.None);

        // Assert
        switched.Should().BeTrue();
        await _moves.Received(1).ActivateAsync(Home, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HideSoftLockedAsync_OnlyFamilyIsSoftLocked_StaysAndSaysPaused()
    {
        // Arrange: spec §15.2 Q6, logging carries on.
        await SignInAsync();
        OnPhone((Home, ["Maja"]));
        _store.ListAsync(Me, Arg.Any<CancellationToken>()).Returns([new FamilyMembership(Home, Role.Parent, Now, "Andrzej", 2)]);
        _state.IsSoftLockedAsync(Home, Arg.Any<CancellationToken>()).Returns(true);
        var families = Families();

        // Act
        var switched = await families.HideSoftLockedAsync(CancellationToken.None);
        var paused = await families.IsShownPausedAloneAsync(CancellationToken.None);

        // Assert
        switched.Should().BeFalse();
        paused.Should().BeTrue();
        await _moves.DidNotReceiveWithAnyArgs().ActivateAsync(default, default);
    }

    [Fact]
    public async Task RemoveFromPhoneAsync_TheLastFamily_DeletesAndReplacesItWithANewProvisionalFamilyInOneCall()
    {
        // Arrange: spec §8.4, ActiveHouseholdAsync must never throw after a removal.
        await SignInAsync();
        OnPhone((Joined, ["Maja"]));

        // Act
        await Families().RemoveFromPhoneAsync(Joined, CancellationToken.None);

        // Assert
        await _records.Received(1).DeleteAndReplaceAsync(Joined,
            Arg.Is<HouseholdSettings>(s => s.Id != Joined && s.TimeZoneId == "Europe/Warsaw"), Arg.Any<CancellationToken>());
        await _moves.DidNotReceiveWithAnyArgs().DeleteHouseholdAsync(default, default);
    }

    [Fact]
    public async Task RemoveFromPhoneAsync_TheShownOneOfTwo_DeletesItAndShowsTheEarliestAccountFamily()
    {
        // Arrange
        await SignInAsync();
        OnPhone((Joined, ["Maja"]), (Home, ["Leo"]));
        _store.ListAsync(Me, Arg.Any<CancellationToken>()).Returns([new FamilyMembership(Home, Role.Owner, Now, "Kasia", 1)]);
        _moves.When(m => m.DeleteHouseholdAsync(Joined, Arg.Any<CancellationToken>())).Do(_ => OnPhone((Home, ["Leo"])));

        // Act
        await Families().RemoveFromPhoneAsync(Joined, CancellationToken.None);

        // Assert
        await _moves.Received(1).DeleteHouseholdAsync(Joined, Arg.Any<CancellationToken>());
        await _moves.Received(1).ActivateAsync(Home, Arg.Any<CancellationToken>());
        await _records.DidNotReceiveWithAnyArgs().DeleteAndReplaceAsync(default, default!, default);
    }

    [Fact]
    public async Task SyncingHouseholdsAsync_HomeAndAccountFamilies_LeavesLocalOnesOut()
    {
        // Arrange: ruling 6, a local family's outbox never drains.
        await SignInAsync();
        _store.ListAsync(Me, Arg.Any<CancellationToken>()).Returns([new FamilyMembership(Joined, Role.Parent, Now, "Andrzej", 2)]);

        // Act
        var syncing = await Families().SyncingHouseholdsAsync(CancellationToken.None);

        // Assert
        syncing.Should().BeEquivalentTo([Home, Joined]);
    }

    [Fact]
    public async Task BringOverTargetsAsync_SharedNotSoftLockedOthers_AreTheTargets()
    {
        // Arrange
        await SignInAsync();
        OnPhone((Local, ["Maja"]), (Joined, ["Maja"]), (Home, []));
        _store.ListAsync(Me, Arg.Any<CancellationToken>()).Returns([
            new FamilyMembership(Home, Role.Owner, Now, "Kasia", 1), new FamilyMembership(Joined, Role.Parent, Now.AddDays(1), "Andrzej", 2)]);
        _state.IsSoftLockedAsync(Home, Arg.Any<CancellationToken>()).Returns(true);

        // Act
        var targets = await Families().BringOverTargetsAsync(Local, CancellationToken.None);

        // Assert
        targets.Select(t => t.Id).Should().Equal(Joined);
    }
}
```

Append to `tests/Coo.Mobile.Application.Tests/AccountGenerationTests.cs` (it has a `MemorySessions` and an account helper;
use them):

```csharp
    [Fact]
    public async Task TrySetHomeAsync_SameGeneration_MovesTheHomeFamilyOnly()
    {
        // Arrange
        var sessions = new MemorySessions();
        using var account = new Account(sessions);
        var profile = new AccountProfile(new UserId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000c1")), "A", "a@example.com");
        await account.SignedInAsync(new StoredSession("a", DateTimeOffset.UnixEpoch, "r", profile,
            new HouseholdId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a1")), JoinPending: false), CancellationToken.None);
        var next = new HouseholdId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a2"));

        // Act
        var moved = await account.TrySetHomeAsync(account.Generation, next, CancellationToken.None);
        var stale = await account.TrySetHomeAsync(account.Generation - 1, new HouseholdId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a3")), CancellationToken.None);

        // Assert
        moved.Should().BeTrue();
        stale.Should().BeFalse();
        sessions.Stored!.AccountHousehold.Should().Be(next);
        sessions.Stored.RefreshToken.Should().Be("r");
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Coo.Mobile.Application.Tests --filter "FullyQualifiedName~FamiliesTests|FullyQualifiedName~TrySetHomeAsync"`
Expected: FAIL to compile (`IFamilyStore` not found).

- [ ] **Step 3: The ports**

`src/Mobile/Coo.Mobile.Application/Households/FamilyPorts.cs`:

```csharp
using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;
using Coo.Domain.Households;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Application.Sync;

namespace Coo.Mobile.Application.Households;

/// <summary>One active membership of the signed-in account (Spec 011b §7.1), with what the labels need.</summary>
public sealed record FamilyMembership(HouseholdId Household, Role Role, DateTimeOffset JoinedAt, string OwnerName, int MemberCount);

/// <summary>A code from a link or "Enter a code", kept in <c>app_state</c> with the time it arrived (spec §4.1).</summary>
public sealed record PendingInvitation(InvitationCode Code, DateTimeOffset ArrivedAt);

/// <summary>Accepted, but the choice about this phone's entries is not made yet (spec §5.3).</summary>
public sealed record JoiningMarker(HouseholdId Joined, HouseholdId Source, string InviterName);

/// <summary><c>family_memberships</c> and the join's facts in <c>app_state</c>.</summary>
public interface IFamilyStore
{
    /// <summary>Earliest joined first; null until the list was first refreshed for this account.</summary>
    Task<IReadOnlyList<FamilyMembership>?> ListAsync(UserId user, CancellationToken cancellationToken);

    /// <summary>In one transaction: the list is replaced and marked as this account's.</summary>
    Task ReplaceAsync(UserId user, IReadOnlyList<FamilyMembership> memberships, CancellationToken cancellationToken);

    /// <summary>Sign-out (spec §7.1, ruling 27): the list and the joining marker go; a pending invitation stays.</summary>
    Task ClearAsync(CancellationToken cancellationToken);

    Task<PendingInvitation?> GetPendingInvitationAsync(CancellationToken cancellationToken);

    Task SetPendingInvitationAsync(PendingInvitation? invitation, CancellationToken cancellationToken);

    Task<JoiningMarker?> GetJoiningAsync(CancellationToken cancellationToken);

    Task SetJoiningAsync(JoiningMarker? marker, CancellationToken cancellationToken);

    /// <summary>Spec 011b §6.2's <c>copied_from</c>: the bring-over is not offered again for this pair.</summary>
    Task<bool> WasCopiedAsync(HouseholdId source, HouseholdId target, CancellationToken cancellationToken);
}

public sealed record CopyResult(int Copied, int Skipped);

/// <summary>Whole-family operations on this phone, each in one SQLite transaction.</summary>
public interface IFamilyRecords
{
    /// <summary>Care entries, baby profiles and dismissals of the household, tombstones included: the copy's stamp budget.</summary>
    Task<int> CountRecordsAsync(HouseholdId household, CancellationToken cancellationToken);

    /// <summary>
    /// Spec 011b §6.2: the source's readable live records become new records of the target, stamped from
    /// <paramref name="firstStamp"/> with consecutive counters; the source is untouched; <c>copied_from</c> is written in
    /// the same transaction and the target becomes active. Null, with nothing written, when the pair was copied before.
    /// </summary>
    Task<CopyResult?> CopyAsync(BringOverPlan plan, UserId user, HlcStamp firstStamp, CancellationToken cancellationToken);

    /// <summary>Spec 011b §8.4: the household goes and <paramref name="replacement"/> (with an outbox row) becomes the active one.</summary>
    Task DeleteAndReplaceAsync(HouseholdId household, HouseholdSettings replacement, CancellationToken cancellationToken);

    /// <summary>Distinct records waiting in the outbox across these households (ruling 6).</summary>
    Task<int> CountPendingAsync(IReadOnlyCollection<HouseholdId> households, CancellationToken cancellationToken);

    /// <summary>The household's live care entries that wait in the outbox, oldest first (the removal sheet, spec §8.2).</summary>
    Task<IReadOnlyList<StoredEntry>> ListUnsharedEntriesAsync(HouseholdId household, CancellationToken cancellationToken);
}

public sealed record CreatedInvitation(Guid InvitationId, InvitationCode Code, Uri Link, DateTimeOffset ExpiresAt);

public sealed record FamilyMember(UserId UserId, string Name, string Email, Role Role, DateTimeOffset JoinedAt, bool IsYou);

public sealed record FamilyInvitation(Guid InvitationId, Role Role, UserId CreatedBy, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);

public sealed record FamilyMembers(IReadOnlyList<FamilyMember> Members, IReadOnlyList<FamilyInvitation> Invitations);

public sealed record InvitationPreview(
    HouseholdId Household, string InviterName, IReadOnlyList<string> BabyNames, Role Role, DateTimeOffset ExpiresAt, bool AlreadyMember);

public enum AcceptOutcome
{
    Joined,
    Rejoined,
    AlreadyMember,
}

public sealed record AcceptedInvitation(HouseholdId Household, Role Role, AcceptOutcome Outcome);

public enum LeaveOutcome
{
    Left,
    Deleted,
}

/// <summary>
/// <c>/api/households</c> and <c>/api/invitations</c> through the refreshing handler (Spec 011b §10.1). Throws
/// <see cref="HouseholdUnavailableException"/> when nothing was decided and <see cref="HouseholdRefusedException"/> for a
/// code the server sent.
/// </summary>
public interface IHouseholdApi
{
    Task<CreatedInvitation> CreateInvitationAsync(HouseholdId household, CancellationToken cancellationToken);

    Task WithdrawInvitationAsync(HouseholdId household, Guid invitationId, CancellationToken cancellationToken);

    Task<FamilyMembers> ListMembersAsync(HouseholdId household, CancellationToken cancellationToken);

    Task RemoveMemberAsync(HouseholdId household, UserId member, CancellationToken cancellationToken);

    Task<LeaveOutcome> LeaveAsync(HouseholdId household, bool confirm, CancellationToken cancellationToken);

    Task CreateHouseholdAsync(HouseholdId household, CancellationToken cancellationToken);

    Task<InvitationPreview> PreviewAsync(InvitationCode code, CancellationToken cancellationToken);

    Task<AcceptedInvitation> AcceptAsync(InvitationCode code, CancellationToken cancellationToken);
}

/// <summary>Nothing was decided: no network, a timeout, 5xx, an unreadable or foreign answer (a portal, a bare 403).</summary>
public sealed class HouseholdUnavailableException : Exception
{
    public HouseholdUnavailableException()
    {
    }

    public HouseholdUnavailableException(string message)
        : base(message)
    {
    }

    public HouseholdUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The server refused with one of its codes (<c>HouseholdErrors</c>, <c>auth.rate-limited</c>, <c>sync.consent-required</c>).</summary>
public sealed class HouseholdRefusedException : Exception
{
    public HouseholdRefusedException()
        : this("unknown")
    {
    }

    public HouseholdRefusedException(string message)
        : base(message)
    {
    }

    public HouseholdRefusedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public string Code => Message;
}
```

`src/Mobile/Coo.Mobile.Application/Households/ProvisionalSettings.cs`:

```csharp
using Coo.Domain.Common;
using Coo.Domain.Households;
using Coo.Mobile.Application.CareLog;

namespace Coo.Mobile.Application.Households;

/// <summary>A new provisional family's settings from the device's defaults (ADR 0003 §1, ADR 0006 §5).</summary>
public sealed class ProvisionalSettings(IVersionClock clock, IDeviceLocale locale, TimeProvider time)
{
    public async Task<HouseholdSettings> CreateAsync(CancellationToken cancellationToken)
    {
        var id = new HouseholdId(Guid.CreateVersion7(time.GetUtcNow()));
        var unit = locale.PrefersOunces ? VolumeUnit.Ounces : VolumeUnit.Millilitres;
        var settings = HouseholdSettings.Create(id, unit, locale.TimeZoneId, await clock.NextAsync(cancellationToken));
        return settings.IsSuccess
            ? settings.Value
            : throw new InvalidOperationException($"The device time zone '{locale.TimeZoneId}' is unusable: {settings.Error}.");
    }
}
```

In `Households.cs`, `EnsureProvisionalHousehold` keeps its constructor (two tests build it) and becomes:

```csharp
public sealed class EnsureProvisionalHousehold(IHouseholdStore households, IVersionClock clock, IDeviceLocale locale, TimeProvider time)
{
    public async Task<HouseholdSettings> ExecuteAsync(CancellationToken cancellationToken)
    {
        var existing = await households.ListHouseholdsAsync(cancellationToken);
        if (existing.FirstOrDefault(h => !h.IsDeleted) is { } household)
            return household;

        var settings = await new ProvisionalSettings(clock, locale, time).CreateAsync(cancellationToken);
        await households.SaveSettingsAsync(settings, cancellationToken);
        return settings;
    }
}
```

`src/Mobile/Coo.Mobile.Application/Households/Families.cs`:

```csharp
using Coo.Domain.Common;
using Coo.Domain.Households;
using Coo.Mobile.Application.Auth;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Application.Sync;

namespace Coo.Mobile.Application.Households;

/// <summary>A family on this phone, as the Family section, the switcher and the bring-over see it (Spec 011b §7).</summary>
public sealed record FamilyOnPhone(
    HouseholdId Id, IReadOnlyList<string> BabyNames, FamilyMembership? Membership, bool IsShown, bool IsSoftLocked, bool IsHome, bool HasEverSynced)
{
    /// <summary>One of the signed-in account's families, on the server.</summary>
    public bool IsShared => Membership is not null;

    public bool IsOwner => Membership?.Role == Role.Owner;

    /// <summary>Invite, remove, withdraw (an Owner or a Parent).</summary>
    public bool CanManage => Membership?.Role is Role.Owner or Role.Parent;

    /// <summary>Spec 011b §6.3: a never-synced family moves; an account family that synced copies; another account's never.</summary>
    public bool MayBeBroughtOver => !HasEverSynced || IsShared;
}

/// <summary>What a confirmation or the removal sheet says about a family's log on this phone.</summary>
public sealed record FamilyContents(int EntryCount, IReadOnlyList<string> BabyNames, IReadOnlyList<StoredEntry> Unshared, VolumeUnit Unit);

/// <summary>
/// The account's families on this phone (Spec 011b §7): the list refreshed from <c>/api/me</c> and each family's members,
/// the shown family, switching, hiding a soft-locked family, and taking a family off the phone (§8.4: never the last one
/// without a new provisional family in its place). It raises nothing: callers tell the pages (<see cref="ISyncControl.NotifyDataChanged"/>).
/// </summary>
public sealed class Families(
    IFamilyStore store, IAccountApi accounts, IHouseholdApi api, ISessionStore sessions, IHouseholdStore households, IHouseholdMoves moves,
    ISyncState state, IFamilyRecords records, ProvisionalSettings provisional)
{
    private static readonly IComparer<Guid> ByGuid = Comparer<Guid>.Create(GuidOrder.Compare);

    /// <summary>False when it could not be refreshed (offline, signed out, the account changed meanwhile): the stored list stays.</summary>
    public async Task<bool> RefreshAsync(CancellationToken cancellationToken)
    {
        if (await sessions.GetAsync(cancellationToken) is not { } session)
            return false;
        AccountOverview me;
        try
        {
            me = await accounts.GetMeAsync(cancellationToken);
        }
        catch (Exception e) when (e is AuthUnavailableException or AuthRejectedException)
        {
            return false;
        }

        var user = session.Profile.UserId;
        if (me is null || me.Profile.UserId != user || me.Memberships is null)
            return false;

        var previous = await store.ListAsync(user, cancellationToken) ?? [];
        var list = new List<FamilyMembership>();
        foreach (var membership in me.Memberships.OrderBy(m => m.JoinedAt))
        {
            string owner;
            int count;
            try
            {
                var members = await api.ListMembersAsync(membership.Household, cancellationToken);
                owner = members.Members.FirstOrDefault(m => m.Role == Role.Owner)?.Name ?? "";
                count = Math.Max(1, members.Members.Count);
            }
            catch (Exception e) when (e is HouseholdUnavailableException or HouseholdRefusedException)
            {
                var known = previous.FirstOrDefault(p => p.Household == membership.Household);
                (owner, count) = (known?.OwnerName ?? "", known?.MemberCount ?? 1);
            }

            list.Add(new FamilyMembership(membership.Household, membership.Role, membership.JoinedAt, owner, count));
        }

        if (await sessions.GetAsync(cancellationToken) is not { } again || again.Profile.UserId != user)
            return false;
        await store.ReplaceAsync(user, list, cancellationToken);
        return true;
    }

    /// <summary>Every family on the phone: the account's by joining order, then the others by ID. <see cref="FamilyOnPhone.IsShown"/> marks the shown one.</summary>
    public async Task<IReadOnlyList<FamilyOnPhone>> ListAsync(CancellationToken cancellationToken)
    {
        var onPhone = (await households.ListHouseholdsAsync(cancellationToken)).Where(h => !h.IsDeleted).ToList();
        if (onPhone.Count == 0)
            return [];
        var session = await sessions.GetAsync(cancellationToken);
        var memberships = session is null ? [] : await store.ListAsync(session.Profile.UserId, cancellationToken) ?? [];
        var shown = await moves.ActiveHouseholdAsync(cancellationToken);
        var list = new List<FamilyOnPhone>(onPhone.Count);
        foreach (var household in onPhone)
        {
            var babies = (await households.ListBabiesAsync(household.Id, cancellationToken)).Where(b => !b.IsDeleted).Select(b => b.Name).ToList();
            list.Add(new FamilyOnPhone(
                household.Id, babies, memberships.FirstOrDefault(m => m.Household == household.Id), household.Id == shown,
                await state.IsSoftLockedAsync(household.Id, cancellationToken), session?.AccountHousehold == household.Id,
                await moves.HasEverSyncedAsync(household.Id, cancellationToken)));
        }

        return [.. list
            .OrderBy(f => f.Membership is null ? 1 : 0)
            .ThenBy(f => f.Membership?.JoinedAt ?? DateTimeOffset.MaxValue)
            .ThenBy(f => f.Id.Value, ByGuid)];
    }

    public async Task<FamilyOnPhone?> ShownAsync(CancellationToken cancellationToken) =>
        (await ListAsync(cancellationToken)).FirstOrDefault(f => f.IsShown);

    /// <summary>The switcher (§7.3): only the shown family changes; nothing moves.</summary>
    public Task SwitchAsync(HouseholdId household, CancellationToken cancellationToken) => moves.ActivateAsync(household, cancellationToken);

    /// <summary>§8.2: a soft-locked shown family gives way to another (the earliest-joined account family, else any). True when it switched.</summary>
    public async Task<bool> HideSoftLockedAsync(CancellationToken cancellationToken)
    {
        var families = await ListAsync(cancellationToken);
        if (families.FirstOrDefault(f => f.IsShown) is not { IsSoftLocked: true } || families.FirstOrDefault(f => !f.IsSoftLocked) is not { } next)
            return false;
        await moves.ActivateAsync(next.Id, cancellationToken);
        return true;
    }

    /// <summary>Today's banner (§8.2): the shown family is soft-locked and is the only family on the phone.</summary>
    public async Task<bool> IsShownPausedAloneAsync(CancellationToken cancellationToken) =>
        await ListAsync(cancellationToken) is [{ IsSoftLocked: true }];

    /// <summary>
    /// After a confirmed removal, a leave or a delete (§8.2–§8.4): the family goes from the phone. The last one is replaced by
    /// a new provisional family in the same transaction; otherwise a shown one gives way to the earliest account family.
    /// </summary>
    public async Task RemoveFromPhoneAsync(HouseholdId household, CancellationToken cancellationToken)
    {
        var others = (await households.ListHouseholdsAsync(cancellationToken)).Count(h => !h.IsDeleted && h.Id != household);
        if (others == 0)
        {
            await records.DeleteAndReplaceAsync(household, await provisional.CreateAsync(cancellationToken), cancellationToken);
            return;
        }

        var wasShown = await moves.ActiveHouseholdAsync(cancellationToken) == household;
        await moves.DeleteHouseholdAsync(household, cancellationToken);
        if (wasShown && (await ListAsync(cancellationToken)).FirstOrDefault(f => !f.IsSoftLocked) is { } next)
            await moves.ActivateAsync(next.Id, cancellationToken);
    }

    /// <summary>The families a cycle syncs: the home family and the account's families (ruling 6).</summary>
    public async Task<IReadOnlyCollection<HouseholdId>> SyncingHouseholdsAsync(CancellationToken cancellationToken)
    {
        if (await sessions.GetAsync(cancellationToken) is not { } session)
            return [];
        var set = new HashSet<HouseholdId> { session.AccountHousehold };
        foreach (var membership in await store.ListAsync(session.Profile.UserId, cancellationToken) ?? [])
            set.Add(membership.Household);
        return set;
    }

    /// <summary>Where a family's entries may go (§6.3 "Later"): another account family on the phone that is not soft-locked.</summary>
    public async Task<IReadOnlyList<FamilyOnPhone>> BringOverTargetsAsync(HouseholdId source, CancellationToken cancellationToken) =>
        [.. (await ListAsync(cancellationToken)).Where(f => f.Id != source && f.IsShared && !f.IsSoftLocked)];

    public async Task<FamilyContents> ContentsAsync(HouseholdId household, CancellationToken cancellationToken)
    {
        var summary = await moves.SummaryAsync(household, cancellationToken);
        var unit = (await households.ListHouseholdsAsync(cancellationToken)).FirstOrDefault(h => h.Id == household)?.VolumeUnit ?? VolumeUnit.Millilitres;
        return new FamilyContents(
            summary.EntryCount, [.. summary.Babies.Select(b => b.Name)], await records.ListUnsharedEntriesAsync(household, cancellationToken), unit);
    }
}
```

In `src/Mobile/Coo.Mobile.Application/Auth/Ports.cs`:

```csharp
/// <summary>One active membership as <c>/api/me</c> lists it (Spec 011b §10.1).</summary>
public sealed record MembershipInfo(HouseholdId Household, Role Role, DateTimeOffset JoinedAt);

public sealed record AccountOverview(
    AccountProfile Profile, HouseholdId? Household, string? Role, IReadOnlyList<DeviceSessionInfo> Devices, ConsentInfo? Consent = null,
    IReadOnlyList<MembershipInfo>? Memberships = null);
```

(add `using Coo.Domain.Households;` — `Role` the enum. The existing `AccountOverview.Role` is a `string`; inside the record
write the new record's member type as `Coo.Domain.Households.Role` if the name clashes.)

In `Account.cs`, after `TryClearPendingMoveAsync`:

```csharp
    /// <summary>
    /// The home family moves (Spec 011b §7.1): it stopped being an active membership, or §8.4 created a new one. Only this
    /// session's generation may move it; nothing else changes.
    /// </summary>
    public Task<bool> TrySetHomeAsync(long generation, HouseholdId household, CancellationToken cancellationToken) =>
        TryChangeAsync(generation, s => s with { AccountHousehold = household }, cancellationToken);
```

In `Sync/Ports.cs`, `ISyncControl` gains:

```csharp
    /// <summary>Local data changed outside a cycle (a switch, a join, a leave): the pages show what is now stored.</summary>
    void NotifyDataChanged();
```

and `SyncEngine` implements it: `public void NotifyDataChanged() => Raise(DataChanged);`.

Register in `ServiceCollectionExtensions.AddMobileApplication`, under "Sync (Spec 011).":

```csharp
        // Members and invitations (Spec 011b).
        services.AddTransient<ProvisionalSettings>();
        services.AddSingleton<Families>();
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Coo.Mobile.Application.Tests`
Expected: PASS (substitutes of `ISyncControl` in other tests pick up the new member without change).

- [ ] **Step 5: Commit**

```bash
git add src/Mobile/Coo.Mobile.Application tests/Coo.Mobile.Application.Tests
git commit -m "Mobile.Application: the account's families on the phone, the home family, the household API port (Spec 011b §7)"
```

---

### Task 11: `family_memberships` and the join's facts on the phone (`FamilyStore`, migration 0005)

The phone's storage and HTTP adapters come right after the ports (Tasks 11–13), before the cycle and the engine use them
(Tasks 14–17), so `CompositionTests` (the host's real composition) stays green after every task.

**Files:**
- Create: `src/Mobile/Coo.Mobile.Infrastructure/Persistence/Migrations/0005-family-memberships.sql`,
  `src/Mobile/Coo.Mobile.Infrastructure/Persistence/FamilyStore.cs`
- Modify: `src/Mobile/Coo.Mobile.Infrastructure/Persistence/HouseholdMoves.cs` (deleting a household clears every key and
  row that names it), `src/Mobile/Coo.Mobile.Infrastructure/ServiceCollectionExtensions.cs`,
  `tests/Coo.Mobile.Infrastructure.Tests/migrations-manifest.txt`
- Test: `tests/Coo.Mobile.Infrastructure.Tests/FamilyStoreTests.cs`

**Interfaces:**
- Consumes: Task 10's `IFamilyStore`, `FamilyMembership`, `PendingInvitation`, `JoiningMarker`, `InvitationCode`.
- Produces: `FamilyStore(LocalDatabase database) : IFamilyStore`; `app_state` keys `families_user`, `pending_invitation`
  (`CODE|utcMs`), `joining_household`, `joining_source`, `joining_inviter`, `copied_from:<source>:<target>`;
  `HouseholdMoves.ClearFamilyKeysAsync(SqliteConnection, SqliteTransaction, string household, CancellationToken)` (internal static,
  reused by Task 12).

- [ ] **Step 1: Write the failing tests**

`tests/Coo.Mobile.Infrastructure.Tests/FamilyStoreTests.cs`:

```csharp
using Coo.Domain.Common;
using Coo.Domain.Households;
using Coo.Mobile.Application.Households;
using Coo.Mobile.Infrastructure.Persistence;

namespace Coo.Mobile.Infrastructure.Tests;

/// <summary>Spec 011b §4.1, §5.3, §6.2 and §7.1 on a real SQLite file.</summary>
public sealed class FamilyStoreTests
{
    private static readonly UserId Me = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000c1"));
    private static readonly UserId Other = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000c2"));
    private static readonly HouseholdId Home = TestDatabase.Household;
    private static readonly HouseholdId Joined = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a2"));
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ListAsync_BeforeTheFirstRefresh_IsNull()
    {
        // Arrange
        await using var db = await TestDatabase.CreateAsync();

        // Act
        var list = await new FamilyStore(db.Database).ListAsync(Me, CancellationToken.None);

        // Assert
        list.Should().BeNull("the cycle must tell 'not known yet' from 'no family at all' (§8.4)");
    }

    [Fact]
    public async Task ReplaceAsync_ThenList_ReturnsThemInJoinedOrderForThatAccountOnly()
    {
        // Arrange
        await using var db = await TestDatabase.CreateAsync();
        var store = new FamilyStore(db.Database);

        // Act
        await store.ReplaceAsync(Me, [
            new FamilyMembership(Joined, Role.Parent, Now.AddDays(1), "Andrzej", 2),
            new FamilyMembership(Home, Role.Owner, Now, "Kasia", 1)], CancellationToken.None);
        var mine = await store.ListAsync(Me, CancellationToken.None);
        var theirs = await store.ListAsync(Other, CancellationToken.None);

        // Assert
        mine.Should().Equal(new FamilyMembership(Home, Role.Owner, Now, "Kasia", 1), new FamilyMembership(Joined, Role.Parent, Now.AddDays(1), "Andrzej", 2));
        theirs.Should().BeNull();
    }

    [Fact]
    public async Task ReplaceAsync_AnEmptyList_IsKnownAndEmpty()
    {
        // Arrange: after a removal from the only family (§8.4).
        await using var db = await TestDatabase.CreateAsync();
        var store = new FamilyStore(db.Database);
        await store.ReplaceAsync(Me, [new FamilyMembership(Home, Role.Owner, Now, "Kasia", 1)], CancellationToken.None);

        // Act
        await store.ReplaceAsync(Me, [], CancellationToken.None);

        // Assert
        (await store.ListAsync(Me, CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task ClearAsync_SignOut_ClearsTheListAndTheJoiningMarkerAndKeepsThePendingInvitation()
    {
        // Arrange: ruling 27.
        await using var db = await TestDatabase.CreateAsync();
        var store = new FamilyStore(db.Database);
        var pending = new PendingInvitation(InvitationCode.Parse("ABCDEFGH").Value, Now);
        await store.ReplaceAsync(Me, [new FamilyMembership(Home, Role.Owner, Now, "Kasia", 1)], CancellationToken.None);
        await store.SetJoiningAsync(new JoiningMarker(Joined, Home, "Andrzej | O'Neil"), CancellationToken.None);
        await store.SetPendingInvitationAsync(pending, CancellationToken.None);

        // Act
        await store.ClearAsync(CancellationToken.None);

        // Assert
        (await store.ListAsync(Me, CancellationToken.None)).Should().BeNull();
        (await store.GetJoiningAsync(CancellationToken.None)).Should().BeNull();
        (await store.GetPendingInvitationAsync(CancellationToken.None)).Should().Be(pending);
    }

    [Fact]
    public async Task SetJoiningAsync_ANameWithSeparators_ReadsBackExactly()
    {
        // Arrange
        await using var db = await TestDatabase.CreateAsync();
        var store = new FamilyStore(db.Database);
        var marker = new JoiningMarker(Joined, Home, "Andrzej | O'Neil; Lech");

        // Act
        await store.SetJoiningAsync(marker, CancellationToken.None);
        var read = await store.GetJoiningAsync(CancellationToken.None);
        await store.SetJoiningAsync(null, CancellationToken.None);

        // Assert
        read.Should().Be(marker);
        (await store.GetJoiningAsync(CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task DeleteHouseholdAsync_ARemovedFamily_IsNoLongerNamedAnywhere()
    {
        // Arrange: Spec 011 §13.4's carry, spec §8.2: acknowledging clears it everywhere.
        await using var db = await TestDatabase.CreateAsync();
        var store = new FamilyStore(db.Database);
        await store.ReplaceAsync(Me, [new FamilyMembership(Joined, Role.Parent, Now, "Andrzej", 2), new FamilyMembership(Home, Role.Owner, Now, "Kasia", 1)], CancellationToken.None);
        await store.SetJoiningAsync(new JoiningMarker(Joined, Home, "Andrzej"), CancellationToken.None);
        await db.ExecuteAsync(
            $"""
            INSERT INTO app_state (key, value) VALUES ('softlock:{Joined.Value}', '1'), ('copied_from:{Home.Value}:{Joined.Value}', '1'),
                ('copied_from:{Joined.Value}:{Home.Value}', '1'), ('softlock:{Home.Value}', '1');
            """);

        // Act
        await new HouseholdMoves(db.Database, db.Time).DeleteHouseholdAsync(Joined, CancellationToken.None);

        // Assert
        (await db.ScalarAsync($"SELECT count(*) FROM app_state WHERE instr(key, '{Joined.Value}') > 0 OR value = '{Joined.Value}';")).Should().Be(0);
        (await db.ScalarAsync($"SELECT count(*) FROM app_state WHERE key = 'softlock:{Home.Value}';")).Should().Be(1, "another family's keys stay");
        (await store.GetJoiningAsync(CancellationToken.None)).Should().BeNull();
        (await store.ListAsync(Me, CancellationToken.None)).Should().ContainSingle().Which.Household.Should().Be(Home);
    }

    [Fact]
    public async Task WasCopiedAsync_MarkerForThePair_IsTrueOnlyForThatDirection()
    {
        // Arrange
        await using var db = await TestDatabase.CreateAsync();
        await db.ExecuteAsync($"INSERT INTO app_state (key, value) VALUES ('copied_from:{Home.Value}:{Joined.Value}', '1');");
        var store = new FamilyStore(db.Database);

        // Act
        var forward = await store.WasCopiedAsync(Home, Joined, CancellationToken.None);
        var back = await store.WasCopiedAsync(Joined, Home, CancellationToken.None);

        // Assert
        forward.Should().BeTrue();
        back.Should().BeFalse();
    }

    [Fact]
    public async Task Migrate_EmptyDatabase_CreatesFamilyMembershipsWithItsGuards()
    {
        // Arrange
        await using var db = await TestDatabase.CreateAsync();

        // Act
        var act = async () => await db.ExecuteAsync(
            "INSERT INTO family_memberships (household_id, role, joined_utc_ms, owner_name, member_count) VALUES ('x', 'admin', 1, 'A', 1);");

        // Assert
        await act.Should().ThrowAsync<Microsoft.Data.Sqlite.SqliteException>();
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Coo.Mobile.Infrastructure.Tests --filter "FullyQualifiedName~FamilyStoreTests"`
Expected: FAIL to compile (`FamilyStore` not found).

- [ ] **Step 3: The migration**

`src/Mobile/Coo.Mobile.Infrastructure/Persistence/Migrations/0005-family-memberships.sql`:

```sql
-- 0005 (Spec 011b §7.1): the signed-in account's active family memberships, refreshed from the server, with what the
-- labels need (the Owner's display name, the member count). Sign-out clears it; app_state.families_user names the account
-- it was refreshed for, so "not refreshed yet" and "no family at all" read differently.

CREATE TABLE family_memberships (
    household_id  TEXT    NOT NULL PRIMARY KEY,
    role          TEXT    NOT NULL CHECK (role IN ('owner', 'parent', 'caregiver')),
    joined_utc_ms INTEGER NOT NULL,
    owner_name    TEXT    NOT NULL,
    member_count  INTEGER NOT NULL CHECK (member_count >= 1)
) STRICT;
```

Add its checksum to `tests/Coo.Mobile.Infrastructure.Tests/migrations-manifest.txt` (it is released with 0.12.0):

```sh
echo "$(shasum -a 256 src/Mobile/Coo.Mobile.Infrastructure/Persistence/Migrations/0005-family-memberships.sql | cut -d' ' -f1) 0005-family-memberships.sql" \
  >> tests/Coo.Mobile.Infrastructure.Tests/migrations-manifest.txt
```

(The manifest hashes the script's UTF-8 text as the test reads it; the file must end with a newline and use LF.)

- [ ] **Step 4: The store, and deletion clearing the family's keys**

`src/Mobile/Coo.Mobile.Infrastructure/Persistence/FamilyStore.cs`:

```csharp
using System.Globalization;
using Coo.Domain.Common;
using Coo.Domain.Households;
using Coo.Mobile.Application.Households;
using Microsoft.Data.Sqlite;

namespace Coo.Mobile.Infrastructure.Persistence;

/// <summary><c>family_memberships</c> and the join's facts in <c>app_state</c> (Spec 011b §4.1, §5.3, §6.2, §7.1).</summary>
internal sealed class FamilyStore(LocalDatabase database) : IFamilyStore
{
    internal const string FamiliesUserKey = "families_user";
    internal const string PendingKey = "pending_invitation";
    internal const string JoiningHouseholdKey = "joining_household";
    internal const string JoiningSourceKey = "joining_source";
    internal const string JoiningInviterKey = "joining_inviter";

    internal static string CopiedKey(HouseholdId source, HouseholdId target) => $"copied_from:{Columns.Id(source.Value)}:{Columns.Id(target.Value)}";

    public async Task<IReadOnlyList<FamilyMembership>?> ListAsync(UserId user, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        if ((string?)await connection.ScalarAsync("SELECT value FROM app_state WHERE key = $k;", cancellationToken, null, ("$k", FamiliesUserKey))
            != Columns.Id(user.Value))
            return null;
        await using var command = connection.Command(
            "SELECT household_id, role, joined_utc_ms, owner_name, member_count FROM family_memberships ORDER BY joined_utc_ms, household_id;");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var list = new List<FamilyMembership>();
        while (await reader.ReadAsync(cancellationToken))
        {
            if (Columns.ParseGuid(reader.GetString(0)) is not { } id || ParseRole(reader.GetString(1)) is not { } role)
                continue;
            list.Add(new FamilyMembership(new HouseholdId(id), role, DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2)),
                reader.GetString(3), (int)reader.GetInt64(4)));
        }

        return list;
    }

    public async Task ReplaceAsync(UserId user, IReadOnlyList<FamilyMembership> memberships, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(memberships);
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await connection.ExecuteAsync("DELETE FROM family_memberships;", cancellationToken, transaction);
        foreach (var m in memberships)
        {
            await connection.ExecuteAsync(
                "INSERT INTO family_memberships (household_id, role, joined_utc_ms, owner_name, member_count) VALUES ($h, $role, $at, $owner, $count);",
                cancellationToken, transaction,
                ("$h", Columns.Id(m.Household.Value)), ("$role", RoleText(m.Role)), ("$at", m.JoinedAt.ToUnixTimeMilliseconds()),
                ("$owner", m.OwnerName), ("$count", Math.Max(1, m.MemberCount)));
        }

        await SetAsync(connection, transaction, FamiliesUserKey, Columns.Id(user.Value), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task ClearAsync(CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await connection.ExecuteAsync("DELETE FROM family_memberships;", cancellationToken, transaction);
        await connection.ExecuteAsync(
            "DELETE FROM app_state WHERE key IN ($u, $h, $s, $i);", cancellationToken, transaction,
            ("$u", FamiliesUserKey), ("$h", JoiningHouseholdKey), ("$s", JoiningSourceKey), ("$i", JoiningInviterKey));
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<PendingInvitation?> GetPendingInvitationAsync(CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        var value = (string?)await connection.ScalarAsync("SELECT value FROM app_state WHERE key = $k;", cancellationToken, null, ("$k", PendingKey));
        var parts = value?.Split('|');
        if (parts is not [var code, var at] || InvitationCode.Parse(code) is not { IsSuccess: true } parsed
            || !long.TryParse(at, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms))
            return null;
        return new PendingInvitation(parsed.Value, DateTimeOffset.FromUnixTimeMilliseconds(ms));
    }

    public async Task SetPendingInvitationAsync(PendingInvitation? invitation, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        if (invitation is null)
            await connection.ExecuteAsync("DELETE FROM app_state WHERE key = $k;", cancellationToken, null, ("$k", PendingKey));
        else
            await SetAsync(connection, null, PendingKey,
                invitation.Code.Value + "|" + invitation.ArrivedAt.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture), cancellationToken);
    }

    public async Task<JoiningMarker?> GetJoiningAsync(CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        var joined = Columns.ParseGuid((string?)await connection.ScalarAsync("SELECT value FROM app_state WHERE key = $k;", cancellationToken, null, ("$k", JoiningHouseholdKey)));
        var source = Columns.ParseGuid((string?)await connection.ScalarAsync("SELECT value FROM app_state WHERE key = $k;", cancellationToken, null, ("$k", JoiningSourceKey)));
        var inviter = (string?)await connection.ScalarAsync("SELECT value FROM app_state WHERE key = $k;", cancellationToken, null, ("$k", JoiningInviterKey));
        return joined is { } j && source is { } s ? new JoiningMarker(new HouseholdId(j), new HouseholdId(s), inviter ?? "") : null;
    }

    public async Task SetJoiningAsync(JoiningMarker? marker, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        if (marker is null)
        {
            await connection.ExecuteAsync(
                "DELETE FROM app_state WHERE key IN ($h, $s, $i);", cancellationToken, transaction,
                ("$h", JoiningHouseholdKey), ("$s", JoiningSourceKey), ("$i", JoiningInviterKey));
        }
        else
        {
            await SetAsync(connection, transaction, JoiningHouseholdKey, Columns.Id(marker.Joined.Value), cancellationToken);
            await SetAsync(connection, transaction, JoiningSourceKey, Columns.Id(marker.Source.Value), cancellationToken);
            await SetAsync(connection, transaction, JoiningInviterKey, marker.InviterName, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<bool> WasCopiedAsync(HouseholdId source, HouseholdId target, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        return await connection.ScalarAsync("SELECT 1 FROM app_state WHERE key = $k;", cancellationToken, null, ("$k", CopiedKey(source, target))) is not null;
    }

    internal static string RoleText(Role role) => role switch
    {
        Role.Owner => "owner",
        Role.Parent => "parent",
        Role.Caregiver => "caregiver",
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown role."),
    };

    internal static Role? ParseRole(string? value) => value switch
    {
        "owner" => Role.Owner,
        "parent" => Role.Parent,
        "caregiver" => Role.Caregiver,
        _ => null,
    };

    private static Task SetAsync(SqliteConnection connection, SqliteTransaction? transaction, string key, string value, CancellationToken cancellationToken) =>
        connection.ExecuteAsync(
            "INSERT INTO app_state (key, value) VALUES ($k, $v) ON CONFLICT (key) DO UPDATE SET value = excluded.value;",
            cancellationToken, transaction, ("$k", key), ("$v", value));
}
```

In `HouseholdMoves.cs`, replace the `app_state` delete at the end of `DeleteHouseholdAsync` with a call to a new helper, and
add the helper:

```csharp
        await ClearFamilyKeysAsync(connection, transaction, Columns.Id(household.Value), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
```

```csharp
    /// <summary>
    /// Spec 011b §8.2: a family taken off the phone is no longer named anywhere: its soft-lock, the active key (when it is the
    /// active one), its copy markers in either direction, a joining marker that names it, and its membership row.
    /// </summary>
    internal static async Task ClearFamilyKeysAsync(SqliteConnection connection, SqliteTransaction transaction, string household, CancellationToken cancellationToken)
    {
        var h = ("$h", (object?)household);
        await connection.ExecuteAsync(
            """
            DELETE FROM app_state WHERE key = 'softlock:' || $h OR (key = 'active_household' AND value = $h)
                OR key LIKE 'copied_from:' || $h || ':%' OR key LIKE 'copied_from:%:' || $h;
            """,
            cancellationToken, transaction, h);
        var namesIt = await connection.ScalarAsync(
            "SELECT 1 FROM app_state WHERE key IN ('joining_household', 'joining_source') AND value = $h;", cancellationToken, transaction, h);
        if (namesIt is not null)
        {
            await connection.ExecuteAsync(
                "DELETE FROM app_state WHERE key IN ('joining_household', 'joining_source', 'joining_inviter');", cancellationToken, transaction);
        }

        await connection.ExecuteAsync("DELETE FROM family_memberships WHERE household_id = $h;", cancellationToken, transaction, h);
    }
```

Register in `AddMobileInfrastructure`, under "Sync (Spec 011 §6).":

```csharp
        // Members and invitations (Spec 011b §7).
        services.AddSingleton<IFamilyStore, FamilyStore>();
```

(add `using Coo.Mobile.Application.Households;`).

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/Coo.Mobile.Infrastructure.Tests`
Expected: PASS (`SchemaTests.Migrate_ReleasedScripts_MatchManifestChecksums` includes 0005; `HouseholdMovesTests`'s
deletion tests still pass).

- [ ] **Step 6: Commit**

```bash
git add src/Mobile/Coo.Mobile.Infrastructure tests/Coo.Mobile.Infrastructure.Tests
git commit -m "Mobile.Infrastructure: family_memberships, the pending invitation and the joining marker; a deleted family is named nowhere (Spec 011b §7.1, §8.2)"
```

---

### Task 12: The copy, the delete-and-replace, Pending across families, unshared entries (`FamilyRecords`)

**Files:**
- Create: `src/Mobile/Coo.Mobile.Infrastructure/Persistence/FamilyRecords.cs`
- Modify: `src/Mobile/Coo.Mobile.Infrastructure/Persistence/HouseholdMoves.cs` (extract the deletion's statements into
  `DeleteHouseholdRowsAsync`), `src/Mobile/Coo.Mobile.Infrastructure/ServiceCollectionExtensions.cs`,
  `tests/Coo.Mobile.Infrastructure.Tests/HouseholdMovesTests.cs` (make `PhoneAsync`, `PullFamilyAsync`, `SnapshotAsync`,
  `BreakAsync` and the `Account`/`FamilyBaby`/`User` fields `internal`)
- Test: `tests/Coo.Mobile.Infrastructure.Tests/CopyEntriesTests.cs`

**Interfaces:**
- Consumes: Task 10's `IFamilyRecords`, `CopyResult`, `BringOverPlan`; `CareEntryRow`, `CareEntryClassifier`, `Columns`,
  `Outbox.AppendAsync`, `SyncStore.InHousehold`, `CareLogStore.ReadRowsAsync`, `StringListJson`, Task 11's
  `HouseholdMoves.ClearFamilyKeysAsync` and `FamilyStore.CopiedKey`.
- Produces: `FamilyRecords(LocalDatabase database, TimeProvider time, LocalWrites writes) : IFamilyRecords`;
  `HouseholdMoves.DeleteHouseholdRowsAsync(SqliteConnection, SqliteTransaction, HouseholdId, CancellationToken)` (internal static).

- [ ] **Step 1: Write the failing tests**

`tests/Coo.Mobile.Infrastructure.Tests/CopyEntriesTests.cs`:

```csharp
using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Duplicates;
using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;
using Coo.Domain.Households;
using Coo.Mobile.Application.Sync;
using Coo.Mobile.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using static Coo.Mobile.Infrastructure.Tests.HouseholdMovesTests;

namespace Coo.Mobile.Infrastructure.Tests;

/// <summary>
/// Spec 011b §6.2 and §8.4 on a real SQLite file: the copy is one transaction, makes new records, keeps the source, and runs
/// once per pair; the last family is replaced in the same transaction that deletes it.
/// </summary>
public sealed class CopyEntriesTests
{
    private static readonly HlcStamp First = new(1_790_000_000_000, 10, new DeviceId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000d9")));

    /// <summary>The phone's household (synced: it has a cursor) with two diapers and a dismissal pairing them; the family pulled beside it.</summary>
    private static async Task<(TestDatabase Db, CareEntry First, CareEntry Second)> SyncedPhoneAsync()
    {
        var (db, first) = await PhoneAsync();
        var second = CareEntry.Create(await db.NewIdAsync(), TestDatabase.Household, EntrySubject.Baby(TestDatabase.Baby),
            new Coo.Domain.CareLog.Kinds.Diaper.DiaperDetails(Coo.Domain.CareLog.Kinds.Diaper.DiaperContents.Dirty), EntryTiming.Point(db.Time.GetLocalNow().AddMinutes(3)),
            PersonRef.User(User), EntrySource.Manual, await db.Device.NextAsync(CancellationToken.None), db.Time.GetUtcNow()).Value;
        await db.Store.SaveAsync(second, CancellationToken.None);
        await new DuplicateDismissalStore(db.Database, db.Time, db.Writes).SaveAsync(DuplicateDismissal.Create(
            new DismissalId(Guid.CreateVersion7(db.Time.GetUtcNow().AddSeconds(1))), TestDatabase.Household, new EntryPair(first.Id, second.Id),
            await db.Device.NextAsync(CancellationToken.None)), CancellationToken.None);
        await db.ExecuteAsync($"INSERT INTO sync_cursors (household_id, last_seq, bootstrap_head) VALUES ('{TestDatabase.Household.Value}', 7, NULL); DELETE FROM outbox;");
        await PullFamilyAsync(db);
        return (db, first, second);
    }

    private static BringOverPlan ToFamily(BabyId? target = null) =>
        new(TestDatabase.Household, Account, new Dictionary<BabyId, BabyId?> { [TestDatabase.Baby] = target ?? FamilyBaby });

    /// <summary>Every stored row that names the source, except the two keys a copy writes on purpose (the active family, the marker).</summary>
    private static async Task<string> SourceRowsAsync(TestDatabase db)
    {
        var all = await SnapshotAsync(db);
        return string.Join('\n', all.Split('\n').Where(l => l.Contains(TestDatabase.Household.Value.ToString(), StringComparison.Ordinal)
            && !l.StartsWith("active_household", StringComparison.Ordinal) && !l.StartsWith("copied_from", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task CopyAsync_SyncedSource_MakesNewRecordsInTheTargetAndLeavesTheSourceUntouched()
    {
        // Arrange
        var (db, first, second) = await SyncedPhoneAsync();
        await using var cleanup = db;
        var sourceBefore = await SourceRowsAsync(db);

        // Act
        var result = await new FamilyRecords(db.Database, db.Time, db.Writes).CopyAsync(ToFamily(), User, First, CancellationToken.None);

        // Assert
        result.Should().Be(new CopyResult(2, 0));
        (await SourceRowsAsync(db)).Should().Be(sourceBefore, "the source is not touched: same records, outbox and cursor");
        (await db.ScalarAsync($"SELECT count(*) FROM care_entries WHERE household_id = '{Account.Value}' AND subject_baby_id = '{FamilyBaby.Value}' AND id NOT IN ('{first.Id.Value}', '{second.Id.Value}');"))
            .Should().Be(2);
        (await db.ScalarAsync($"SELECT count(*) FROM care_entries WHERE household_id = '{Account.Value}' AND logged_by = 'this-device';")).Should().Be(0);
        (await db.ScalarAsync($"SELECT min(version_counter) * 100 + max(version_counter) FROM care_entries WHERE household_id = '{Account.Value}';"))
            .Should().Be(10 * 100 + 11, "the copies take consecutive counters from the reserved stamp (ruling 8)");
        (await db.ScalarAsync($"SELECT count(*) FROM duplicate_dismissals WHERE household_id = '{Account.Value}';")).Should().Be(1);
        (await db.ScalarAsync("SELECT count(*) FROM outbox;")).Should().Be(3, "two entries and a dismissal; the target's settings and the mapped baby need none");
        (await db.ScalarAsync($"SELECT count(*) FROM app_state WHERE key = 'copied_from:{TestDatabase.Household.Value}:{Account.Value}';")).Should().Be(1);
        (await new HouseholdMoves(db.Database, db.Time).ActiveHouseholdAsync(CancellationToken.None)).Should().Be(Account);
    }

    [Fact]
    public async Task CopyAsync_DetailsWithAnUnknownProperty_KeepsThemByteForByte()
    {
        // Arrange: ADR 0010 §3, nothing unknown is lost.
        var (db, first, _) = await SyncedPhoneAsync();
        await using var cleanup = db;
        const string details = """{"v":1,"contents":"wet","futureField":{"a":[1,2]}}""";
        await db.ExecuteAsync($"UPDATE care_entries SET details = '{details}' WHERE id = '{first.Id.Value}';");

        // Act
        await new FamilyRecords(db.Database, db.Time, db.Writes).CopyAsync(ToFamily(), User, First, CancellationToken.None);

        // Assert
        await using var connection = await db.OpenAsync();
        (await connection.ScalarAsync($"SELECT count(*) FROM care_entries WHERE household_id = '{Account.Value}' AND details = '{details}';", CancellationToken.None))
            .Should().Be(1L);
    }

    [Fact]
    public async Task CopyAsync_TombstoneAndAnUnknownKind_AreNotCopiedAndOnlyTheLiveOneIsCounted()
    {
        // Arrange: ruling 9.
        var (db, first, _) = await SyncedPhoneAsync();
        await using var cleanup = db;
        await db.ExecuteAsync($"UPDATE care_entries SET is_deleted = 1 WHERE id = '{first.Id.Value}';");
        await db.ExecuteAsync(
            $$"""
            INSERT INTO care_entries (id, household_id, kind, subject_baby_id, is_session, start_utc_ms, start_offset_min, logged_by, source, details,
                version_wall_ms, version_counter, version_device, integrity, integrity_reason)
            VALUES ('{{Guid.CreateVersion7(db.Time.GetUtcNow().AddHours(1))}}', '{{TestDatabase.Household.Value}}', 'potty', '{{TestDatabase.Baby.Value}}', 0,
                1790000000000, 120, 'user:{{User.Value}}', 'manual', '{"v":1}', 1, 0, '{{TestDatabase.Baby.Value}}', 'read-only', 'entry.kind-unknown');
            """);

        // Act
        var result = await new FamilyRecords(db.Database, db.Time, db.Writes).CopyAsync(ToFamily(), User, First, CancellationToken.None);

        // Assert
        result.Should().Be(new CopyResult(1, 1));
        (await db.ScalarAsync($"SELECT count(*) FROM duplicate_dismissals WHERE household_id = '{Account.Value}';")).Should().Be(0, "only one of its entries was copied");
    }

    [Fact]
    public async Task CopyAsync_BabyAddedAsNew_CreatesANewProfileWithItsOwnIdAndOutboxRow()
    {
        // Arrange
        var (db, _, _) = await SyncedPhoneAsync();
        await using var cleanup = db;
        var plan = new BringOverPlan(TestDatabase.Household, Account, new Dictionary<BabyId, BabyId?> { [TestDatabase.Baby] = null });

        // Act
        await new FamilyRecords(db.Database, db.Time, db.Writes).CopyAsync(plan, User, First, CancellationToken.None);

        // Assert
        (await db.ScalarAsync($"SELECT count(*) FROM baby_profiles WHERE household_id = '{Account.Value}' AND name = 'Maja' AND id <> '{TestDatabase.Baby.Value}';"))
            .Should().Be(2, "the family's own Maja and the new one");
        (await db.ScalarAsync("SELECT count(*) FROM outbox WHERE entity_type = 'BabyProfile';")).Should().Be(1);
        (await db.ScalarAsync($"SELECT count(*) FROM care_entries WHERE household_id = '{Account.Value}' AND subject_baby_id = '{FamilyBaby.Value}';")).Should().Be(0);
    }

    [Fact]
    public async Task CopyAsync_SecondTimeForThePair_CopiesNothing()
    {
        // Arrange: Review Focus 3.
        var (db, _, _) = await SyncedPhoneAsync();
        await using var cleanup = db;
        var records = new FamilyRecords(db.Database, db.Time, db.Writes);
        await records.CopyAsync(ToFamily(), User, First, CancellationToken.None);
        var after = await SnapshotAsync(db);

        // Act
        var again = await records.CopyAsync(ToFamily(), User, First, CancellationToken.None);

        // Assert
        again.Should().BeNull();
        (await SnapshotAsync(db)).Should().Be(after);
    }

    [Fact]
    public async Task CopyAsync_FailurePartWay_ChangesNothing()
    {
        // Arrange: the dismissal is written last; failing there must undo the entries already copied.
        var (db, _, _) = await SyncedPhoneAsync();
        await using var cleanup = db;
        await BreakAsync(db, "CREATE TRIGGER t_break BEFORE INSERT ON duplicate_dismissals BEGIN SELECT RAISE(ABORT, 'test'); END;");
        var before = await SnapshotAsync(db);

        // Act
        var act = async () => await new FamilyRecords(db.Database, db.Time, db.Writes).CopyAsync(ToFamily(), User, First, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<SqliteException>();
        (await SnapshotAsync(db)).Should().Be(before);
    }

    [Fact]
    public async Task DeleteAndReplaceAsync_TheLastFamily_LeavesOnlyTheNewOneActiveWithItsOutboxRow()
    {
        // Arrange: spec §8.4.
        var (db, _) = await PhoneAsync();
        await using var cleanup = db;
        var replacement = HouseholdSettings.Create(new HouseholdId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000af")), VolumeUnit.Ounces, "Europe/Warsaw",
            await db.Device.NextAsync(CancellationToken.None)).Value;

        // Act
        await new FamilyRecords(db.Database, db.Time, db.Writes).DeleteAndReplaceAsync(TestDatabase.Household, replacement, CancellationToken.None);

        // Assert
        (await db.ScalarAsync("SELECT count(*) FROM household_settings;")).Should().Be(1);
        (await db.ScalarAsync($"SELECT count(*) FROM care_entries WHERE household_id = '{TestDatabase.Household.Value}';")).Should().Be(0);
        (await db.ScalarAsync($"SELECT count(*) FROM outbox WHERE entity_type = 'HouseholdSettings' AND entity_id = '{replacement.Id.Value}';")).Should().Be(1);
        (await db.ScalarAsync("SELECT count(*) FROM outbox;")).Should().Be(1);
        (await new HouseholdMoves(db.Database, db.Time).ActiveHouseholdAsync(CancellationToken.None)).Should().Be(replacement.Id);
    }

    [Fact]
    public async Task DeleteAndReplaceAsync_FailurePartWay_ChangesNothing()
    {
        // Arrange
        var (db, _) = await PhoneAsync();
        await using var cleanup = db;
        await BreakAsync(db, "CREATE TRIGGER t_break BEFORE INSERT ON household_settings BEGIN SELECT RAISE(ABORT, 'test'); END;");
        var before = await SnapshotAsync(db);
        var replacement = HouseholdSettings.Create(new HouseholdId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000af")), VolumeUnit.Ounces, "Europe/Warsaw",
            await db.Device.NextAsync(CancellationToken.None)).Value;

        // Act
        var act = async () => await new FamilyRecords(db.Database, db.Time, db.Writes).DeleteAndReplaceAsync(TestDatabase.Household, replacement, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<SqliteException>();
        (await SnapshotAsync(db)).Should().Be(before);
    }

    [Fact]
    public async Task ListUnsharedEntriesAsync_OutboxHoldsOneEntry_ListsItOldestFirstAndPendingCountsIt()
    {
        // Arrange: the removal sheet's "1 entry you logged wasn't shared yet" (spec §8.2).
        var (db, entry) = await PhoneAsync();
        await using var cleanup = db;
        var records = new FamilyRecords(db.Database, db.Time, db.Writes);

        // Act
        var unshared = await records.ListUnsharedEntriesAsync(TestDatabase.Household, CancellationToken.None);
        var pending = await records.CountPendingAsync([TestDatabase.Household, Account], CancellationToken.None);
        var count = await records.CountRecordsAsync(TestDatabase.Household, CancellationToken.None);

        // Assert
        unshared.Should().ContainSingle().Which.Id.Should().Be(entry.Id);
        pending.Should().Be(4, "the settings, the baby, the entry and the dismissal wait in the outbox");
        count.Should().Be(3, "an entry, a baby and a dismissal");
    }
}
```

(`PhoneAsync` saves the entry with `PersonRef.ThisDevicesPerson` and a dismissal pairing it with a missing entry; the copy
skips that dismissal because its second entry is not copied. `SyncedPhoneAsync` adds a second diaper and a dismissal of the
pair, so one dismissal is copied.)

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Coo.Mobile.Infrastructure.Tests --filter "FullyQualifiedName~CopyEntriesTests"`
Expected: FAIL to compile (`FamilyRecords` not found; the helpers are private).

- [ ] **Step 3: Write the implementation**

In `HouseholdMoves.cs`, split `DeleteHouseholdAsync`:

```csharp
    public async Task DeleteHouseholdAsync(HouseholdId household, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await DeleteHouseholdRowsAsync(connection, transaction, household, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>Every record, outbox row, rejection, cursor and key of the household (ADR 0002 §10), inside the caller's transaction.</summary>
    internal static async Task DeleteHouseholdRowsAsync(SqliteConnection connection, SqliteTransaction transaction, HouseholdId household, CancellationToken cancellationToken)
    {
        var h = ("$h", (object?)Columns.Id(household.Value));
        await connection.ExecuteAsync($"DELETE FROM outbox AS o WHERE {SyncStore.InHousehold};", cancellationToken, transaction, h);
        foreach (var table in new[] { "care_entries", "baby_profiles", "duplicate_dismissals", "household_settings", "sync_rejections", "sync_cursors" })
            await connection.ExecuteAsync($"DELETE FROM {table} WHERE household_id = $h;", cancellationToken, transaction, h);
        await ClearFamilyKeysAsync(connection, transaction, Columns.Id(household.Value), cancellationToken);
    }
```

`src/Mobile/Coo.Mobile.Infrastructure/Persistence/FamilyRecords.cs`:

```csharp
using System.Globalization;
using System.Text.Json;
using Coo.Domain.CareLog;
using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;
using Coo.Domain.Households;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Application.Households;
using Coo.Mobile.Application.Sync;
using Microsoft.Data.Sqlite;

namespace Coo.Mobile.Infrastructure.Persistence;

/// <summary>Whole-family operations (Spec 011b §6.2, §7.2, §8.2, §8.4), each one SQLite transaction.</summary>
internal sealed class FamilyRecords(LocalDatabase database, TimeProvider time, LocalWrites writes) : IFamilyRecords
{
    public async Task<int> CountRecordsAsync(HouseholdId household, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        var count = await connection.ScalarAsync(
            """
            SELECT (SELECT count(*) FROM care_entries WHERE household_id = $h) + (SELECT count(*) FROM baby_profiles WHERE household_id = $h)
                + (SELECT count(*) FROM duplicate_dismissals WHERE household_id = $h);
            """,
            cancellationToken, null, ("$h", Columns.Id(household.Value)));
        return Convert.ToInt32(count, CultureInfo.InvariantCulture);
    }

    public async Task<CopyResult?> CopyAsync(BringOverPlan plan, UserId user, HlcStamp firstStamp, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var marker = FamilyStore.CopiedKey(plan.From, plan.To);
        if (await connection.ScalarAsync("SELECT 1 FROM app_state WHERE key = $k;", cancellationToken, transaction, ("$k", marker)) is not null)
            return null;

        var (source, target) = (Columns.Id(plan.From.Value), Columns.Id(plan.To.Value));
        var person = Columns.Person(PersonRef.User(user));
        var counter = firstStamp.Counter;
        HlcStamp Next() => new(firstStamp.WallClockMs, counter++, firstStamp.DeviceId);
        string NewId() => Columns.Id(Guid.CreateVersion7(time.GetUtcNow()));

        // 1. Babies: mapped ones point at the target's; the others become new profiles (spec §6.2's table).
        var targetBabies = new HashSet<string>(StringComparer.Ordinal);
        await using (var command = connection.Command("SELECT id FROM baby_profiles WHERE household_id = $t AND is_deleted = 0;", transaction, ("$t", target)))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                targetBabies.Add(reader.GetString(0));
        }

        var sourceBabies = new List<(string Id, string Name, string Nicknames, string? Birth, string? Due)>();
        await using (var command = connection.Command(
            "SELECT id, name, nicknames, birth_date, due_date FROM baby_profiles WHERE household_id = $s AND is_deleted = 0 ORDER BY id;", transaction, ("$s", source)))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                sourceBabies.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4)));
            }
        }

        var babyMap = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var baby in sourceBabies)
        {
            if (Columns.ParseGuid(baby.Id) is { } id && plan.Babies.TryGetValue(new BabyId(id), out var chosen) && chosen is { } mapped
                && targetBabies.Contains(Columns.Id(mapped.Value)))
            {
                babyMap[baby.Id] = Columns.Id(mapped.Value);
                continue;
            }

            var newId = NewId();
            var stamp = Next();
            await connection.ExecuteAsync(
                """
                INSERT INTO baby_profiles (id, household_id, name, nicknames, birth_date, due_date, version_wall_ms, version_counter, version_device, is_deleted)
                VALUES ($id, $t, $name, $nicknames, $birth, $due, $version_wall_ms, $version_counter, $version_device, 0);
                """,
                cancellationToken, transaction,
                [("$id", newId), ("$t", target), ("$name", baby.Name), ("$nicknames", baby.Nicknames), ("$birth", baby.Birth), ("$due", baby.Due), .. Columns.Stamp(stamp)]);
            await Outbox.AppendAsync(connection, transaction, SyncedEntityType.BabyProfile, Guid.Parse(newId), false, stamp, time, cancellationToken);
            babyMap[baby.Id] = newId;
        }

        // 2. Care entries: live and readable ones only (spec §15.2 Q9); tombstones are neither copied nor counted (ruling 9).
        var rows = new List<CareEntryRow>();
        await using (var command = connection.Command(
            $"SELECT {CareEntryRow.SelectColumns} FROM care_entries WHERE household_id = $s ORDER BY start_utc_ms, id;", transaction, ("$s", source)))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                rows.Add(CareEntryRow.Read(reader));
        }

        var (copied, skipped) = (0, 0);
        var entryMap = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (row.IsDeleted)
                continue;
            string? subjectBaby = null;
            if (CareEntryClassifier.Classify(row).Integrity != CareEntryClassifier.Ok
                || (row.SubjectBabyId is { } baby && !babyMap.TryGetValue(baby, out subjectBaby)))
            {
                skipped++;
                continue;
            }

            var newId = NewId();
            var stamp = Next();
            await connection.ExecuteAsync(
                """
                INSERT INTO care_entries (id, household_id, kind, subject_baby_id, subject_person, is_session, start_utc_ms, start_offset_min,
                    end_utc_ms, end_offset_min, logged_by, source, source_prompt_version, note, details, version_wall_ms, version_counter,
                    version_device, is_deleted, integrity, integrity_reason)
                VALUES ($id, $t, $kind, $baby, $person, $session, $start, $start_offset, $end, $end_offset, $logged_by, $source, $prompt, $note,
                    $details, $version_wall_ms, $version_counter, $version_device, 0, 'ok', NULL);
                """,
                cancellationToken, transaction,
                [
                    ("$id", newId), ("$t", target), ("$kind", row.Kind), ("$baby", subjectBaby),
                    ("$person", row.SubjectPerson == Columns.ThisDevice ? person : row.SubjectPerson),
                    ("$session", row.IsSession ? 1 : 0), ("$start", row.StartUtcMs), ("$start_offset", row.StartOffsetMin), ("$end", row.EndUtcMs),
                    ("$end_offset", row.EndOffsetMin), ("$logged_by", row.LoggedBy == Columns.ThisDevice ? person : row.LoggedBy), ("$source", row.Source),
                    ("$prompt", row.SourcePromptVersion), ("$note", row.Note), ("$details", row.Details), .. Columns.Stamp(stamp),
                ]);
            await Outbox.AppendAsync(connection, transaction, SyncedEntityType.CareEntry, Guid.Parse(newId), false, stamp, time, cancellationToken);
            entryMap[row.Id] = newId;
            copied++;
        }

        // 3. Dismissals whose two entries were both copied pair the two copies.
        var dismissals = new List<(string A, string B)>();
        await using (var command = connection.Command(
            "SELECT entry_a, entry_b FROM duplicate_dismissals WHERE household_id = $s AND is_deleted = 0 ORDER BY id;", transaction, ("$s", source)))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                dismissals.Add((reader.GetString(0), reader.GetString(1)));
        }

        foreach (var (a, b) in dismissals)
        {
            if (!entryMap.TryGetValue(a, out var copyA) || !entryMap.TryGetValue(b, out var copyB))
                continue;
            if (string.CompareOrdinal(copyA, copyB) > 0)
                (copyA, copyB) = (copyB, copyA);
            var newId = NewId();
            var stamp = Next();
            await connection.ExecuteAsync(
                """
                INSERT INTO duplicate_dismissals (id, household_id, entry_a, entry_b, version_wall_ms, version_counter, version_device, is_deleted)
                VALUES ($id, $t, $a, $b, $version_wall_ms, $version_counter, $version_device, 0);
                """,
                cancellationToken, transaction, [("$id", newId), ("$t", target), ("$a", copyA), ("$b", copyB), .. Columns.Stamp(stamp)]);
            await Outbox.AppendAsync(connection, transaction, SyncedEntityType.DuplicateDismissal, Guid.Parse(newId), false, stamp, time, cancellationToken);
        }

        // 4. The marker in the same transaction (a double tap cannot copy twice), and the target becomes the shown family.
        await connection.ExecuteAsync("INSERT INTO app_state (key, value) VALUES ($k, $v);", cancellationToken, transaction,
            ("$k", marker), ("$v", time.GetUtcNow().ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)));
        await SetActiveAsync(connection, transaction, target, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        writes.Notify();
        return new CopyResult(copied, skipped);
    }

    public async Task DeleteAndReplaceAsync(HouseholdId household, HouseholdSettings replacement, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await HouseholdMoves.DeleteHouseholdRowsAsync(connection, transaction, household, cancellationToken);
        await connection.ExecuteAsync(
            """
            INSERT INTO household_settings (household_id, volume_unit, time_zone_id, hidden_kinds, version_wall_ms, version_counter, version_device, is_deleted)
            VALUES ($id, $unit, $tz, $hidden, $version_wall_ms, $version_counter, $version_device, 0);
            """,
            cancellationToken, transaction,
            [
                ("$id", Columns.Id(replacement.Id.Value)), ("$unit", replacement.VolumeUnit == VolumeUnit.Ounces ? "oz" : "ml"), ("$tz", replacement.TimeZoneId),
                ("$hidden", JsonSerializer.Serialize(replacement.HiddenKinds.Select(k => k.Value).Order(StringComparer.Ordinal).ToList(), StringListJson.Default.ListString)),
                .. Columns.Stamp(replacement.Version),
            ]);
        await Outbox.AppendAsync(connection, transaction, SyncedEntityType.HouseholdSettings, replacement.Id.Value, false, replacement.Version, time, cancellationToken);
        await SetActiveAsync(connection, transaction, Columns.Id(replacement.Id.Value), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        writes.Notify();
    }

    public async Task<int> CountPendingAsync(IReadOnlyCollection<HouseholdId> households, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(households);
        await using var connection = await database.OpenAsync(cancellationToken);
        var total = 0;
        foreach (var household in households)
        {
            total += Convert.ToInt32(await connection.ScalarAsync(
                $"SELECT count(*) FROM (SELECT DISTINCT o.entity_type, o.entity_id FROM outbox o WHERE {SyncStore.InHousehold});",
                cancellationToken, null, ("$h", Columns.Id(household.Value))), CultureInfo.InvariantCulture);
        }

        return total;
    }

    public async Task<IReadOnlyList<StoredEntry>> ListUnsharedEntriesAsync(HouseholdId household, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        var rows = await CareLogStore.ReadRowsAsync(
            connection,
            $"""
            SELECT {CareEntryRow.SelectColumns} FROM care_entries c
            WHERE c.household_id = $h AND c.is_deleted = 0
              AND EXISTS (SELECT 1 FROM outbox o WHERE o.entity_type = 'CareEntry' AND o.entity_id = c.id)
            ORDER BY c.start_utc_ms, c.id;
            """,
            cancellationToken, ("$h", Columns.Id(household.Value)));
        return [.. rows.Select(r => CareEntryClassifier.Classify(r).Entry)];
    }

    private static Task SetActiveAsync(SqliteConnection connection, SqliteTransaction transaction, string household, CancellationToken cancellationToken) =>
        connection.ExecuteAsync(
            "INSERT INTO app_state (key, value) VALUES ('active_household', $v) ON CONFLICT (key) DO UPDATE SET value = excluded.value;",
            cancellationToken, transaction, ("$v", household));
}
```

Register `services.AddSingleton<IFamilyRecords, FamilyRecords>();` beside `IFamilyStore`.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Coo.Mobile.Infrastructure.Tests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Mobile/Coo.Mobile.Infrastructure tests/Coo.Mobile.Infrastructure.Tests
git commit -m "Mobile.Infrastructure: copy a synced family's entries in one transaction; replace the last family; Pending and unshared entries (Spec 011b §6.2, §8.2, §8.4)"
```

---

### Task 13: The household client, `/api/me` memberships, and `not-a-member` naming its family

**Files:**
- Create: `src/Mobile/Coo.Mobile.Infrastructure/Households/HouseholdClient.cs`, `src/Mobile/Coo.Mobile.Infrastructure/Households/HouseholdHttp.cs`,
  `src/Mobile/Coo.Mobile.Infrastructure/Households/HouseholdJsonContext.cs`
- Modify: `src/Mobile/Coo.Mobile.Infrastructure/Auth/AccountClient.cs` (memberships), `src/Mobile/Coo.Mobile.Infrastructure/Sync/SyncHttp.cs`
  (`NotAMember` carries the household), `src/Mobile/Coo.Mobile.Infrastructure/ServiceCollectionExtensions.cs`
- Test: `tests/Coo.Mobile.Infrastructure.Tests/HouseholdClientTests.cs`, `tests/Coo.Mobile.Infrastructure.Tests/AuthClientTests.cs` (append),
  `tests/Coo.Mobile.Infrastructure.Tests/SyncClientTests.cs` (one expectation), `tests/Coo.Mobile.Infrastructure.Tests/CompositionTests.cs` (append)

**Interfaces:**
- Consumes: `Coo.Contracts.Households` DTOs, `HouseholdErrors`, `SyncErrors`, `AuthRoles`, Task 10's `IHouseholdApi` and records.
- Produces: `HouseholdClient(HttpClient http) : IHouseholdApi`; `IAccountApi.GetMeAsync` fills `AccountOverview.Memberships`;
  `SyncRefusedException(SyncRefusal.NotAMember, household)`.

- [ ] **Step 1: Write the failing tests**

`tests/Coo.Mobile.Infrastructure.Tests/HouseholdClientTests.cs`:

```csharp
using System.Net;
using System.Text;
using Coo.Contracts.Households;
using Coo.Domain.Common;
using Coo.Domain.Households;
using Coo.Mobile.Application.Households;
using Coo.Mobile.Infrastructure.Households;

namespace Coo.Mobile.Infrastructure.Tests;

/// <summary>Spec 011b §10.1 on the phone: only the server's own codes are refusals; anything else is "unavailable".</summary>
public sealed class HouseholdClientTests
{
    private static readonly HouseholdId Family = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a1"));
    private static readonly InvitationCode Code = InvitationCode.Parse("ABCDEFGH").Value;

    [Fact]
    public async Task PreviewAsync_Answer_MapsItAndSendsTheCodeInTheBody()
    {
        // Arrange
        HttpRequestMessage? sent = null;
        string? body = null;
        var client = Client(async r =>
        {
            (sent, body) = (r, await r.Content!.ReadAsStringAsync());
            return await Json(HttpStatusCode.OK,
                $$"""{"householdId":"{{Family.Value}}","inviterName":"Andrzej","babyNames":["Maja"],"role":"parent","expiresAt":"2026-10-09T12:02:00+00:00","alreadyMember":false}""");
        });

        // Act
        var preview = await client.PreviewAsync(Code, CancellationToken.None);

        // Assert
        sent!.RequestUri!.ToString().Should().Be("https://api.test/api/invitations/preview", "the code never travels in a path or query");
        body.Should().Be("""{"code":"ABCDEFGH"}""");
        preview.Should().BeEquivalentTo(new InvitationPreview(Family, "Andrzej", ["Maja"], Role.Parent, new DateTimeOffset(2026, 10, 9, 12, 2, 0, TimeSpan.Zero), false));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, """{"code":"invitation.invalid"}""", HouseholdErrors.InvitationInvalid)]
    [InlineData((HttpStatusCode)429, """{"code":"auth.rate-limited"}""", HouseholdErrors.RateLimited)]
    [InlineData(HttpStatusCode.Forbidden, """{"code":"sync.consent-required"}""", "sync.consent-required")]
    [InlineData(HttpStatusCode.Conflict, """{"code":"household.last-family"}""", HouseholdErrors.LastFamily)]
    public async Task AcceptAsync_AServerCode_IsARefusalWithThatCode(HttpStatusCode status, string json, string code)
    {
        // Arrange
        var client = Client(_ => Json(status, json));

        // Act
        var act = async () => await client.AcceptAsync(Code, CancellationToken.None);

        // Assert
        (await act.Should().ThrowAsync<HouseholdRefusedException>()).Which.Code.Should().Be(code);
    }

    [Fact]
    public async Task PreviewAsync_HtmlPage_IsUnavailable()
    {
        // Arrange: Review Focus 5, a captive portal answers 200 with its own page.
        var client = Client(_ => Raw(HttpStatusCode.OK, "text/html", "<html>Log in to the Wi-Fi</html>"));

        // Act
        var act = async () => await client.PreviewAsync(Code, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<HouseholdUnavailableException>();
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "")]
    [InlineData(HttpStatusCode.BadGateway, "<html>502</html>")]
    [InlineData(HttpStatusCode.NotFound, """{"code":"something.else"}""")]
    public async Task AcceptAsync_Bare403_IsUnavailable(HttpStatusCode status, string body)
    {
        // Arrange: a proxy's bare 403, a 502, an unknown code: none of them is the server refusing the invitation.
        var client = Client(_ => Raw(status, "text/plain", body));

        // Act
        var act = async () => await client.AcceptAsync(Code, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<HouseholdUnavailableException>();
    }

    [Fact]
    public async Task CreateInvitationAsync_Created_ReturnsTheCodeAndOurLink()
    {
        // Arrange
        string? body = null;
        var client = Client(async r =>
        {
            body = await r.Content!.ReadAsStringAsync();
            return await Json(HttpStatusCode.Created,
                """{"invitationId":"7a1c0000-0000-7000-8000-0000000000e1","code":"ABCDEFGH","link":"https://api.coo.lechdigital.nl/i/ABCDEFGH","expiresAt":"2026-10-09T12:02:00+00:00"}""");
        });

        // Act
        var created = await client.CreateInvitationAsync(Family, CancellationToken.None);

        // Assert
        body.Should().Be("""{"role":"parent"}""");
        created.Code.Display.Should().Be("ABCD-EFGH");
        created.Link.Should().Be(new Uri("https://api.coo.lechdigital.nl/i/ABCDEFGH"));
    }

    [Fact]
    public async Task ListMembersAsync_Answer_MapsMembersAndInvitations()
    {
        // Arrange
        var client = Client(_ => Json(HttpStatusCode.OK,
            """
            {"members":[{"userId":"7a1c0000-0000-7000-8000-0000000000c1","name":"Andrzej","email":"a@example.com","role":"owner","joinedAt":"2026-09-01T00:00:00+00:00","isYou":true}],
             "invitations":[{"invitationId":"7a1c0000-0000-7000-8000-0000000000e1","role":"parent","createdBy":"7a1c0000-0000-7000-8000-0000000000c1","createdAt":"2026-10-06T12:02:00+00:00","expiresAt":"2026-10-09T12:02:00+00:00"}]}
            """));

        // Act
        var members = await client.ListMembersAsync(Family, CancellationToken.None);

        // Assert
        members.Members.Single().Should().Match<FamilyMember>(m => m.Role == Role.Owner && m.IsYou && m.Name == "Andrzej");
        members.Invitations.Single().ExpiresAt.Should().Be(new DateTimeOffset(2026, 10, 9, 12, 2, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task LeaveAsync_Deleted_IsDeletedAndSendsConfirm()
    {
        // Arrange
        string? body = null;
        var client = Client(async r =>
        {
            body = await r.Content!.ReadAsStringAsync();
            return await Json(HttpStatusCode.OK, """{"outcome":"deleted"}""");
        });

        // Act
        var outcome = await client.LeaveAsync(Family, confirm: true, CancellationToken.None);

        // Assert
        outcome.Should().Be(LeaveOutcome.Deleted);
        body.Should().Be("""{"confirm":true}""");
    }

    [Fact]
    public async Task CreateHouseholdAsync_IdTaken_IsARefusal()
    {
        // Arrange
        var client = Client(_ => Json(HttpStatusCode.Conflict, """{"code":"household.id-taken"}"""));

        // Act
        var act = async () => await client.CreateHouseholdAsync(Family, CancellationToken.None);

        // Assert
        (await act.Should().ThrowAsync<HouseholdRefusedException>()).Which.Code.Should().Be(HouseholdErrors.IdTaken);
    }

    private static HouseholdClient Client(Func<HttpRequestMessage, Task<HttpResponseMessage>> server) =>
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

Append to `AuthClientTests.cs` (it has a `Client`/`Json` helper for `AccountClient`; reuse it):

```csharp
    [Fact]
    public async Task GetMeAsync_Memberships_MapsEachActiveFamily()
    {
        // Arrange
        var client = new AccountClient(new HttpClient(new FakeServer(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """
                {"user":{"id":"7a1c0000-0000-7000-8000-0000000000c1","email":"k@example.com","displayName":"Kasia"},
                 "household":{"householdId":"7a1c0000-0000-7000-8000-0000000000a1","role":"owner"},"devices":[],
                 "memberships":[{"householdId":"7a1c0000-0000-7000-8000-0000000000a1","role":"owner","joinedAt":"2026-09-01T00:00:00+00:00"},
                                {"householdId":"7a1c0000-0000-7000-8000-0000000000a2","role":"parent","joinedAt":"2026-10-06T00:00:00+00:00"},
                                {"householdId":"7a1c0000-0000-7000-8000-0000000000a3","role":"admin","joinedAt":"2026-10-06T00:00:00+00:00"}]}
                """, Encoding.UTF8, "application/json"),
        }))) { BaseAddress = new Uri("https://api.test/") });

        // Act
        var me = await client.GetMeAsync(CancellationToken.None);

        // Assert: a role this build does not know is left out, not guessed.
        me.Memberships!.Select(m => (m.Household.Value.ToString(), m.Role)).Should().Equal(
            ("7a1c0000-0000-7000-8000-0000000000a1", Coo.Domain.Households.Role.Owner), ("7a1c0000-0000-7000-8000-0000000000a2", Coo.Domain.Households.Role.Parent));
    }
```

In `SyncClientTests`, the `not-a-member` case's assertion gains the household:
`exception.Household.Should().Be(Household);` (in the theory that maps refusals, add it for `SyncRefusal.NotAMember` and
`MembershipRevoked` only, or add a dedicated fact:)

```csharp
    [Fact]
    public async Task PullAsync_NotAMember_NamesTheHouseholdAskedAbout()
    {
        // Arrange: Spec 011b §9, only that family stops.
        var client = Client(_ => Json(HttpStatusCode.Forbidden, """{"code":"sync.not-a-member"}"""));

        // Act
        var act = async () => await client.PullAsync(Household, 0, null, CancellationToken.None);

        // Assert
        (await act.Should().ThrowAsync<SyncRefusedException>()).Which.Should().Match<SyncRefusedException>(e => e.Refusal == SyncRefusal.NotAMember && e.Household == Household);
    }
```

Append to `CompositionTests` (the host's composition must resolve every new use case):

```csharp
    [Fact]
    public void Resolve_FamilyUseCases_NeedOnlyThePlatformServices()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(new ManualTimeProvider(new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero)));
        services.AddSingleton<ISecretStore>(new MemorySecrets());
        services.AddMobileApplication();
        services.AddMobileInfrastructure(Path.Combine(_folder, "coo.db"), "0.12.0", new Uri("https://api.test/"));
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = false, ValidateScopes = true });

        // Act
        var families = provider.GetRequiredService<Coo.Mobile.Application.Households.Families>();
        var api = provider.GetRequiredService<Coo.Mobile.Application.Households.IHouseholdApi>();

        // Assert
        families.Should().NotBeNull();
        api.Should().NotBeNull();
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Coo.Mobile.Infrastructure.Tests --filter "FullyQualifiedName~HouseholdClientTests|FullyQualifiedName~AuthClientTests|FullyQualifiedName~SyncClientTests|FullyQualifiedName~CompositionTests"`
Expected: FAIL to compile (`HouseholdClient` not found).

- [ ] **Step 3: Write the implementation**

`src/Mobile/Coo.Mobile.Infrastructure/Households/HouseholdJsonContext.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;
using Coo.Contracts.Auth;
using Coo.Contracts.Households;

namespace Coo.Mobile.Infrastructure.Households;

/// <summary>Source-generated for the trimmed Android build, like <c>SyncJsonContext</c>. Web defaults: camelCase.</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(CreateInvitationRequest))]
[JsonSerializable(typeof(InvitationCreatedResponse))]
[JsonSerializable(typeof(MembersResponse))]
[JsonSerializable(typeof(LeaveRequest))]
[JsonSerializable(typeof(LeaveResponse))]
[JsonSerializable(typeof(CreateHouseholdRequest))]
[JsonSerializable(typeof(InvitationCodeRequest))]
[JsonSerializable(typeof(InvitationPreviewResponse))]
[JsonSerializable(typeof(AcceptInvitationResponse))]
[JsonSerializable(typeof(ErrorResponse))]
internal sealed partial class HouseholdJsonContext : JsonSerializerContext;
```

`src/Mobile/Coo.Mobile.Infrastructure/Households/HouseholdHttp.cs`:

```csharp
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Coo.Contracts.Households;
using Coo.Contracts.Sync;
using Coo.Domain.Households;
using Coo.Mobile.Application.Auth;
using Coo.Mobile.Application.Households;

namespace Coo.Mobile.Infrastructure.Households;

/// <summary>
/// How a household answer becomes a result (Spec 011b §10.1). Only the server's own codes are refusals; a bare status, a
/// portal's page, an unreadable body, no network or a refresh that could not run are "unavailable" (Review Focus 5).
/// </summary>
internal static class HouseholdHttp
{
    private static readonly HashSet<string> Codes = new(StringComparer.Ordinal)
    {
        HouseholdErrors.InvitationInvalid, HouseholdErrors.InvitationRoleUnsupported, HouseholdErrors.InvitationTooMany,
        HouseholdErrors.Forbidden, HouseholdErrors.MemberNotFound, HouseholdErrors.OwnerCannotLeave, HouseholdErrors.LastFamily,
        HouseholdErrors.ConfirmationRequired, HouseholdErrors.IdTaken, HouseholdErrors.AlreadyHasFamily, HouseholdErrors.Malformed,
        HouseholdErrors.RateLimited, SyncErrors.ConsentRequired,
    };

    public static async Task<HttpResponseMessage> SendAsync(HttpClient http, HttpRequestMessage request, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException e)
        {
            throw new HouseholdUnavailableException("The server could not be reached.", e);
        }
        catch (TaskCanceledException e) when (!cancellationToken.IsCancellationRequested)
        {
            throw new HouseholdUnavailableException("The server did not answer in time.", e);
        }
        catch (AuthUnavailableException e)
        {
            throw new HouseholdUnavailableException("The session could not be refreshed now.", e);
        }

        if (response.IsSuccessStatusCode)
            return response;

        using (response)
        {
            var code = await TryCodeAsync(response, cancellationToken);
            if (code is not null && Codes.Contains(code))
                throw new HouseholdRefusedException(code);
            throw new HouseholdUnavailableException(string.Create(CultureInfo.InvariantCulture, $"The server answered {(int)response.StatusCode}."));
        }
    }

    public static async Task<T> ReadAsync<T>(HttpResponseMessage response, JsonTypeInfo<T> type, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync(type, cancellationToken)
                ?? throw new HouseholdUnavailableException("The server sent an empty answer.");
        }
        catch (Exception e) when (IsUnreadable(e))
        {
            throw new HouseholdUnavailableException("The server sent an unreadable answer.", e);
        }
    }

    /// <summary>A role this build does not know makes the answer unusable rather than guessed.</summary>
    public static Role ParseRole(string? value) => value switch
    {
        Contracts.Auth.AuthRoles.Owner => Role.Owner,
        Contracts.Auth.AuthRoles.Parent => Role.Parent,
        Contracts.Auth.AuthRoles.Caregiver => Role.Caregiver,
        _ => throw new HouseholdUnavailableException("The server named a role this version does not know."),
    };

    private static async Task<string?> TryCodeAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return (await response.Content.ReadFromJsonAsync(HouseholdJsonContext.Default.ErrorResponse, cancellationToken))?.Code;
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

`src/Mobile/Coo.Mobile.Infrastructure/Households/HouseholdClient.cs`:

```csharp
using System.Net.Http.Json;
using Coo.Contracts.Auth;
using Coo.Contracts.Households;
using Coo.Domain.Common;
using Coo.Domain.Households;
using Coo.Mobile.Application.Households;

namespace Coo.Mobile.Infrastructure.Households;

/// <summary><c>/api/households</c> and <c>/api/invitations</c> through the refreshing handler (Spec 011b §10.1). Codes travel in bodies.</summary>
internal sealed class HouseholdClient(HttpClient http) : IHouseholdApi
{
    private static readonly Uri PreviewUri = new("api/invitations/preview", UriKind.Relative);
    private static readonly Uri AcceptUri = new("api/invitations/accept", UriKind.Relative);
    private static readonly Uri HouseholdsUri = new("api/households", UriKind.Relative);

    public async Task<CreatedInvitation> CreateInvitationAsync(HouseholdId household, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Path(household, "invitations"))
        {
            Content = JsonContent.Create(new CreateInvitationRequest(AuthRoles.Parent), HouseholdJsonContext.Default.CreateInvitationRequest),
        };
        using var response = await HouseholdHttp.SendAsync(http, request, cancellationToken);
        var created = await HouseholdHttp.ReadAsync(response, HouseholdJsonContext.Default.InvitationCreatedResponse, cancellationToken);
        var code = InvitationCode.Parse(created.Code);
        if (code.IsFailure || created.InvitationId == Guid.Empty)
            throw new HouseholdUnavailableException("The server sent a code this version cannot read.");
        return new CreatedInvitation(created.InvitationId, code.Value, new Uri(InvitationLinks.For(code.Value.Value)), created.ExpiresAt);
    }

    public async Task WithdrawInvitationAsync(HouseholdId household, Guid invitationId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, Path(household, $"invitations/{invitationId:D}"));
        using var response = await HouseholdHttp.SendAsync(http, request, cancellationToken);
    }

    public async Task<FamilyMembers> ListMembersAsync(HouseholdId household, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Path(household, "members"));
        using var response = await HouseholdHttp.SendAsync(http, request, cancellationToken);
        var answer = await HouseholdHttp.ReadAsync(response, HouseholdJsonContext.Default.MembersResponse, cancellationToken);
        return new FamilyMembers(
            [.. (answer.Members ?? []).Select(m => new FamilyMember(new UserId(m.UserId), m.Name ?? "", m.Email ?? "", HouseholdHttp.ParseRole(m.Role), m.JoinedAt, m.IsYou))],
            [.. (answer.Invitations ?? []).Select(i => new FamilyInvitation(i.InvitationId, HouseholdHttp.ParseRole(i.Role), new UserId(i.CreatedBy), i.CreatedAt, i.ExpiresAt))]);
    }

    public async Task RemoveMemberAsync(HouseholdId household, UserId member, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, Path(household, $"members/{member.Value:D}"));
        using var response = await HouseholdHttp.SendAsync(http, request, cancellationToken);
    }

    public async Task<LeaveOutcome> LeaveAsync(HouseholdId household, bool confirm, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Path(household, "leave"))
        {
            Content = JsonContent.Create(new LeaveRequest(confirm), HouseholdJsonContext.Default.LeaveRequest),
        };
        using var response = await HouseholdHttp.SendAsync(http, request, cancellationToken);
        var answer = await HouseholdHttp.ReadAsync(response, HouseholdJsonContext.Default.LeaveResponse, cancellationToken);
        return answer.Outcome switch
        {
            LeaveOutcomes.Deleted => LeaveOutcome.Deleted,
            LeaveOutcomes.Left => LeaveOutcome.Left,
            _ => throw new HouseholdUnavailableException("The server named an outcome this version does not know."),
        };
    }

    public async Task CreateHouseholdAsync(HouseholdId household, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, HouseholdsUri)
        {
            Content = JsonContent.Create(new CreateHouseholdRequest(household.Value), HouseholdJsonContext.Default.CreateHouseholdRequest),
        };
        using var response = await HouseholdHttp.SendAsync(http, request, cancellationToken);
    }

    public async Task<InvitationPreview> PreviewAsync(InvitationCode code, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(code);
        using var request = new HttpRequestMessage(HttpMethod.Post, PreviewUri) { Content = CodeBody(code) };
        using var response = await HouseholdHttp.SendAsync(http, request, cancellationToken);
        var p = await HouseholdHttp.ReadAsync(response, HouseholdJsonContext.Default.InvitationPreviewResponse, cancellationToken);
        if (p.HouseholdId == Guid.Empty)
            throw new HouseholdUnavailableException("The server sent a preview without a family.");
        return new InvitationPreview(new HouseholdId(p.HouseholdId), p.InviterName ?? "", p.BabyNames ?? [], HouseholdHttp.ParseRole(p.Role), p.ExpiresAt, p.AlreadyMember);
    }

    public async Task<AcceptedInvitation> AcceptAsync(InvitationCode code, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(code);
        using var request = new HttpRequestMessage(HttpMethod.Post, AcceptUri) { Content = CodeBody(code) };
        using var response = await HouseholdHttp.SendAsync(http, request, cancellationToken);
        var a = await HouseholdHttp.ReadAsync(response, HouseholdJsonContext.Default.AcceptInvitationResponse, cancellationToken);
        var outcome = a.Outcome switch
        {
            AcceptOutcomes.Joined => AcceptOutcome.Joined,
            AcceptOutcomes.Rejoined => AcceptOutcome.Rejoined,
            AcceptOutcomes.AlreadyMember => AcceptOutcome.AlreadyMember,
            _ => throw new HouseholdUnavailableException("The server named an outcome this version does not know."),
        };
        if (a.HouseholdId == Guid.Empty)
            throw new HouseholdUnavailableException("The server sent an answer without a family.");
        return new AcceptedInvitation(new HouseholdId(a.HouseholdId), HouseholdHttp.ParseRole(a.Role), outcome);
    }

    private static Uri Path(HouseholdId household, string rest) => new($"api/households/{household.Value:D}/{rest}", UriKind.Relative);

    private static JsonContent CodeBody(InvitationCode code) =>
        JsonContent.Create(new InvitationCodeRequest(code.Value), HouseholdJsonContext.Default.InvitationCodeRequest);
}
```

In `AccountClient.GetMeAsync`, pass the memberships (unknown roles skipped):

```csharp
        return new AccountOverview(
            new AccountProfile(new UserId(me.User.Id), me.User.DisplayName, me.User.Email),
            me.Household is null ? null : new HouseholdId(me.Household.HouseholdId),
            me.Household?.Role,
            [.. me.Devices.Select(d => new DeviceSessionInfo(d.DeviceName, d.LastUsedAt, d.IsCurrent))],
            me.Consent is null ? null : new ConsentInfo(me.Consent.Wording, me.Consent.GivenAt),
            me.Memberships is null ? null : [.. me.Memberships
                .Where(m => m.HouseholdId != Guid.Empty && m.JoinedAt is not null && FamilyStore.ParseRole(m.Role) is not null)
                .Select(m => new MembershipInfo(new HouseholdId(m.HouseholdId), FamilyStore.ParseRole(m.Role)!.Value, m.JoinedAt!.Value))]);
```

(`FamilyStore.ParseRole` is `internal static` in `Persistence`; add `using Coo.Mobile.Infrastructure.Persistence;`.)

In `SyncHttp.SendAsync`: `throw new SyncRefusedException(SyncRefusal.NotAMember, household);`.

Register in `AddMobileInfrastructure`, after the sync client:

```csharp
        // Members and invitations (Spec 011b §10.1): the same refreshing handler.
        services.AddSingleton<IHouseholdApi>(sp => new HouseholdClient(Authenticated(sp)));
```

(add `using Coo.Mobile.Infrastructure.Households;`).

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Coo.Mobile.Infrastructure.Tests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Mobile/Coo.Mobile.Infrastructure tests/Coo.Mobile.Infrastructure.Tests
git commit -m "Mobile.Infrastructure: the household client; /api/me memberships; not-a-member names its family (Spec 011b §9, §10.1)"
```

---
### Task 14: Every account family syncs (`SyncCycle`)

**Files:**
- Create: `src/Mobile/Coo.Mobile.Application/Sync/SyncStops.cs`
- Modify: `src/Mobile/Coo.Mobile.Application/Sync/SyncCycle.cs` (rewritten), `src/Mobile/Coo.Mobile.Application/Sync/Model.cs`
  (`CycleReport` gains `RefreshFamilies`, `NotAMember`), `src/Mobile/Coo.Mobile.Application/ServiceCollectionExtensions.cs`
  (`SyncStops` singleton)
- Test: `tests/Coo.Mobile.Application.Tests/MultiFamilyCycleTests.cs` (create), `tests/Coo.Mobile.Application.Tests/SyncCycleTests.cs`
  (constructor helper; two tests replaced), `tests/Coo.Mobile.Infrastructure.Tests/HouseholdSwitchScenarioTests.cs` (constructor helper)

**Interfaces:**
- Consumes: `IFamilyStore.ListAsync/GetJoiningAsync/GetPendingInvitationAsync`, `IHouseholdApi.CreateHouseholdAsync`,
  `Account.TrySetHomeAsync`, `HouseholdErrors.IdTaken/AlreadyHasFamily`.
- Produces:
  - `public sealed class SyncStops` — `Stop(HouseholdId)`, `IsStopped(HouseholdId)`, `Clear()`.
  - `SyncCycle(Account account, ISessionStore sessions, ConsentGate consent, ISyncStore store, IHouseholdMoves moves, ISyncState state,
    ISyncApi api, IVersionClock clock, IDeviceIdentity device, TimeProvider time, IFamilyStore families, IHouseholdApi households, SyncStops stops)`.
  - `CycleReport(CycleOutcome Outcome, bool DataChanged = false, string? Error = null, HouseholdId? Household = null, bool RunAgain = false, bool RefreshFamilies = false, IReadOnlyList<HouseholdId>? NotAMember = null)`.
  - `SyncRefusedException` for `NotAMember` carries the household (`SyncHttp`, Task 13).

The cycle, in order (spec §7.2, §5.3, §8.4, §9; rulings 1, 4–7, 20–22):

1. Signed in and consent given (unchanged), else return as today.
2. **Home** (`EnsureHome`): with the family list known and the home family not in it — the earliest-joined account family
   becomes home; with no account family, the shown family is created on the server when it has never synced (re-keyed once
   on `id-taken`; `already-has-family` refreshes the list).
3. **The home step**, only while the home family is an active membership (or the list is not known yet, or join-pending):
   soft-locked → ask `/membership` (as today); join-pending → pull it only (as today); mid-join with the home family never
   synced → skip it entirely; the `created` move only outside a join; then push and pull.
4. **Every other account family**, in joined order (and the joined family if the list does not have it yet): skip stopped
   ones and, mid-join, never-synced ones; soft-locked → ask `/membership`; else push and pull.
5. Per family: `membership-revoked` for that family soft-locks it; `not-a-member` stops it until a wake. Cycle-wide:
   unavailable → Failed; consent required → ConsentNeeded; client too old; `malformed` and an undecided push → `no-progress`.

- [ ] **Step 1: Write the failing tests**

In `SyncCycleTests.cs` add the three new collaborators and pass them:

```csharp
    private readonly IFamilyStore _families = Substitute.For<IFamilyStore>();
    private readonly IHouseholdApi _households = Substitute.For<IHouseholdApi>();
    private readonly SyncStops _stops = new();

    private SyncCycle Cycle() => new(
        _account, _sessions, new ConsentGate(_state, _accounts, _consentApi), _store, _moves, _state, _api, _clock, _device, _time,
        _families, _households, _stops);
```

and, in the constructor, make the family list "not known yet", which is exactly the 0.11.0 behaviour every existing test
expects (NSubstitute would otherwise hand back an empty substitute list for an interface return type, which means "no
family at all" and would run §8.4):

```csharp
        _families.ListAsync(default, default).ReturnsForAnyArgs((IReadOnlyList<FamilyMembership>?)null);
```

Add `using Coo.Mobile.Application.Households;`.

Replace `RunAsync_ActiveDiffersWithoutAMoveMarker_MovesNothingAndFails` with:

```csharp
    [Fact]
    public async Task RunAsync_ShownFamilyIsNotTheHome_SyncsTheHomeAndNeverPushesTheShownOne()
    {
        // Arrange: ruling 1, the household-mismatch stop is retired (a local family shown by the switcher).
        await SignInAsync();
        _moves.ActiveHouseholdAsync(Arg.Any<CancellationToken>()).Returns(Provisional);

        // Act
        var report = await Cycle().RunAsync(CancellationToken.None);

        // Assert
        report.Should().Match<CycleReport>(r => r.Outcome == CycleOutcome.Synced && r.Error == null);
        await _store.Received(1).PrepareForPushAsync(Household, User, Arg.Any<CancellationToken>());
        await _store.DidNotReceive().PrepareForPushAsync(Provisional, Arg.Any<UserId>(), Arg.Any<CancellationToken>());
        await _moves.DidNotReceiveWithAnyArgs().MoveAllAsync(default, default, default, default);
    }
```

Replace `RunAsync_MoveMarkerNamesAnotherHousehold_MovesNothingAndFails` with:

```csharp
    [Fact]
    public async Task RunAsync_MoveMarkerNamesAnotherHousehold_ClearsItMovesNothingAndSyncs()
    {
        // Arrange: the person switched families before the first cycle.
        await SignInAsync(moveFrom: new HouseholdId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a9")));
        _moves.ActiveHouseholdAsync(Arg.Any<CancellationToken>()).Returns(Provisional);

        // Act
        var report = await Cycle().RunAsync(CancellationToken.None);

        // Assert
        report.Outcome.Should().Be(CycleOutcome.Synced);
        _sessions.Stored!.PendingMoveFrom.Should().BeNull();
        await _moves.DidNotReceiveWithAnyArgs().MoveAllAsync(default, default, default, default);
    }
```

In `RunAsync_ServerAnsweredOnlySomeChanges_AppliesThoseAndFails` add the assertion
`report.Error.Should().Be(SyncCycle.NoProgressError, "ruling 21: an undecided push stops until a wake");`.

`tests/Coo.Mobile.Application.Tests/MultiFamilyCycleTests.cs`:

```csharp
using Coo.Contracts.Households;
using Coo.Domain.Common;
using Coo.Domain.Households;
using Coo.Mobile.Application.Auth;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Application.Households;
using Coo.Mobile.Application.Sync;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using static Coo.Mobile.Application.Tests.SyncCycleTests;

namespace Coo.Mobile.Application.Tests;

/// <summary>Spec 011b §5.3, §7.2, §8.4 and §9: the cycle over every account family.</summary>
public sealed class MultiFamilyCycleTests : IDisposable
{
    private static readonly HouseholdId Joined = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a5"));
    private static readonly HouseholdId Third = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a6"));
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    private readonly MemorySessions _sessions = new();
    private readonly Account _account;
    private readonly ISyncState _state = Substitute.For<ISyncState>();
    private readonly IAccountApi _accounts = Substitute.For<IAccountApi>();
    private readonly ISyncStore _store = Substitute.For<ISyncStore>();
    private readonly IHouseholdMoves _moves = Substitute.For<IHouseholdMoves>();
    private readonly ISyncApi _api = Substitute.For<ISyncApi>();
    private readonly IFamilyStore _families = Substitute.For<IFamilyStore>();
    private readonly IHouseholdApi _households = Substitute.For<IHouseholdApi>();
    private readonly SyncStops _stops = new();
    private readonly FakeTimeProvider _time = new(Now);

    public MultiFamilyCycleTests()
    {
        _account = new Account(_sessions);
        _state.GetConsentAsync(User, Arg.Any<CancellationToken>()).Returns(ConsentState.Given);
        _moves.ActiveHouseholdAsync(Arg.Any<CancellationToken>()).Returns(Household);
        _moves.HasEverSyncedAsync(default, default).ReturnsForAnyArgs(true);
        _families.ListAsync(default, default).ReturnsForAnyArgs((IReadOnlyList<FamilyMembership>?)null);
        foreach (var family in new[] { Household, Joined, Third, Provisional })
        {
            _store.NextBatchAsync(family, Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);
            _store.GetCursorAsync(family, Arg.Any<CancellationToken>()).Returns(new SyncCursor(0, null));
            _api.PullAsync(family, Arg.Any<long>(), Arg.Any<long?>(), Arg.Any<CancellationToken>()).Returns(new PulledPage([], 0, false, 0));
        }
    }

    public void Dispose() => _account.Dispose();

    private SyncCycle Cycle() => new(
        _account, _sessions, new ConsentGate(_state, _accounts, Substitute.For<IConsentApi>()), _store, _moves, _state, _api,
        Substitute.For<IVersionClock>(), Device(), _time, _families, _households, _stops);

    private static IDeviceIdentity Device()
    {
        var device = Substitute.For<IDeviceIdentity>();
        device.GetAsync(Arg.Any<CancellationToken>()).Returns(Phone);
        return device;
    }

    private Task SignInAsync(HouseholdId? home = null, bool joinPending = false, HouseholdId? moveFrom = null) => _account.SignedInAsync(
        new StoredSession("access", Now.AddMinutes(15), "refresh", Profile, home ?? Household, joinPending, PendingMoveFrom: moveFrom),
        CancellationToken.None);

    private void Memberships(params HouseholdId[] families) => _families.ListAsync(User, Arg.Any<CancellationToken>()).Returns(
        [.. families.Select((f, i) => new FamilyMembership(f, i == 0 ? Role.Owner : Role.Parent, Now.AddDays(i), "Andrzej", 2))]);

    [Fact]
    public async Task RunAsync_ThreeAccountFamilies_SyncsTheHomeFirstThenTheOthersInJoinedOrder()
    {
        // Arrange
        await SignInAsync();
        Memberships(Household, Third, Joined);
        var order = new List<HouseholdId>();
        _api.PullAsync(default, default, default, default).ReturnsForAnyArgs(call =>
        {
            order.Add(call.Arg<HouseholdId>());
            return new PulledPage([], 0, false, 0);
        });

        // Act
        var report = await Cycle().RunAsync(CancellationToken.None);

        // Assert
        report.Outcome.Should().Be(CycleOutcome.Synced);
        order.Should().Equal(Household, Third, Joined);
        await _store.Received(1).PrepareForPushAsync(Joined, User, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_OneFamilyRevoked_SoftLocksItAndStillSyncsTheOthers()
    {
        // Arrange: spec §7.2, a soft-locked family does not stop the others.
        await SignInAsync();
        Memberships(Household, Joined, Third);
        _api.PullAsync(Joined, Arg.Any<long>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new SyncRefusedException(SyncRefusal.MembershipRevoked, Joined));

        // Act
        var report = await Cycle().RunAsync(CancellationToken.None);

        // Assert
        await _state.Received(1).SetSoftLockAsync(Joined, true, Arg.Any<CancellationToken>());
        await _api.Received(1).PullAsync(Third, Arg.Any<long>(), Arg.Any<long?>(), Arg.Any<CancellationToken>());
        report.Outcome.Should().Be(CycleOutcome.Synced, "the shown family is the home, which synced");
        report.RefreshFamilies.Should().BeTrue();
    }

    [Fact]
    public async Task RunAsync_ASoftLockedFamilyConfirmedRemoved_ReportsItsRemovalAfterSyncingTheOthers()
    {
        // Arrange
        await SignInAsync();
        Memberships(Household, Joined);
        _state.IsSoftLockedAsync(Joined, Arg.Any<CancellationToken>()).Returns(true);
        _api.GetMembershipAsync(Joined, Arg.Any<CancellationToken>()).Returns(MembershipStatus.Revoked);

        // Act
        var report = await Cycle().RunAsync(CancellationToken.None);

        // Assert
        report.Should().Match<CycleReport>(r => r.Outcome == CycleOutcome.Removed && r.Household == Joined);
        await _api.Received(1).PullAsync(Household, Arg.Any<long>(), Arg.Any<long?>(), Arg.Any<CancellationToken>());
        await _store.DidNotReceive().PrepareForPushAsync(Joined, Arg.Any<UserId>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_NotAMember_StopsThatFamilyUntilAWakeAndGoesOn()
    {
        // Arrange: spec §9, the family was deleted under this member.
        await SignInAsync();
        Memberships(Household, Joined, Third);
        _api.PullAsync(Joined, Arg.Any<long>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new SyncRefusedException(SyncRefusal.NotAMember, Joined));
        var cycle = Cycle();

        // Act
        var first = await cycle.RunAsync(CancellationToken.None);
        var second = await Cycle().RunAsync(CancellationToken.None);

        // Assert
        first.NotAMember.Should().Equal(Joined);
        first.RefreshFamilies.Should().BeTrue();
        _stops.IsStopped(Joined).Should().BeTrue();
        await _api.Received(1).PullAsync(Joined, Arg.Any<long>(), Arg.Any<long?>(), Arg.Any<CancellationToken>());
        await _api.Received(2).PullAsync(Third, Arg.Any<long>(), Arg.Any<long?>(), Arg.Any<CancellationToken>());
        second.Outcome.Should().Be(CycleOutcome.Synced);
    }

    [Fact]
    public async Task RunAsync_OneFamilyOffline_EndsTheCycleAsFailed()
    {
        // Arrange: spec §7.2, a failed family (no connection) ends the cycle and backs off.
        await SignInAsync();
        Memberships(Household, Joined, Third);
        _api.PullAsync(Joined, Arg.Any<long>(), Arg.Any<long?>(), Arg.Any<CancellationToken>()).ThrowsAsync(new SyncUnavailableException("offline"));

        // Act
        var report = await Cycle().RunAsync(CancellationToken.None);

        // Assert
        report.Outcome.Should().Be(CycleOutcome.Failed);
        await _api.DidNotReceive().PullAsync(Third, Arg.Any<long>(), Arg.Any<long?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_JoiningWithANeverSyncedSource_NeitherPushesNorPullsIt()
    {
        // Arrange: Review Focus 2 and ruling 5. The source is the adopted home family; the joined family is pulled.
        await SignInAsync();
        _moves.ActiveHouseholdAsync(Arg.Any<CancellationToken>()).Returns(Household);
        _moves.HasEverSyncedAsync(Household, Arg.Any<CancellationToken>()).Returns(false);
        Memberships(Household, Joined);
        _families.GetJoiningAsync(Arg.Any<CancellationToken>()).Returns(new JoiningMarker(Joined, Household, "Andrzej"));

        // Act
        var report = await Cycle().RunAsync(CancellationToken.None);

        // Assert
        report.Outcome.Should().Be(CycleOutcome.Synced);
        await _store.DidNotReceive().PrepareForPushAsync(Household, Arg.Any<UserId>(), Arg.Any<CancellationToken>());
        await _api.DidNotReceive().PullAsync(Household, Arg.Any<long>(), Arg.Any<long?>(), Arg.Any<CancellationToken>());
        await _api.Received(1).PullAsync(Joined, Arg.Any<long>(), Arg.Any<long?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_InvitationPendingAndCreatedMarker_MovesNothing()
    {
        // Arrange: ruling 4, the invitation's choice replaces the created move.
        await SignInAsync(moveFrom: Provisional);
        _moves.ActiveHouseholdAsync(Arg.Any<CancellationToken>()).Returns(Provisional);
        _moves.HasEverSyncedAsync(Household, Arg.Any<CancellationToken>()).Returns(false);
        _moves.HasEverSyncedAsync(Provisional, Arg.Any<CancellationToken>()).Returns(false);
        _families.GetPendingInvitationAsync(Arg.Any<CancellationToken>()).Returns(new PendingInvitation(InvitationCode.Parse("ABCDEFGH").Value, Now));

        // Act
        await Cycle().RunAsync(CancellationToken.None);

        // Assert
        await _moves.DidNotReceiveWithAnyArgs().MoveAllAsync(default, default, default, default);
        await _store.DidNotReceiveWithAnyArgs().PrepareForPushAsync(default, default, default);
        _sessions.Stored!.PendingMoveFrom.Should().Be(Provisional, "the flow clears it when it ends");
    }

    [Fact]
    public async Task RunAsync_HomeNoLongerAMembership_TheEarliestAccountFamilyBecomesHome()
    {
        // Arrange: spec §7.1, the home family was left.
        await SignInAsync();
        Memberships(Joined, Third);
        _moves.ActiveHouseholdAsync(Arg.Any<CancellationToken>()).Returns(Joined);

        // Act
        await Cycle().RunAsync(CancellationToken.None);

        // Assert
        _sessions.Stored!.AccountHousehold.Should().Be(Joined);
        await _api.DidNotReceive().PullAsync(Household, Arg.Any<long>(), Arg.Any<long?>(), Arg.Any<CancellationToken>());
        await _api.Received(1).PullAsync(Joined, Arg.Any<long>(), Arg.Any<long?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_NoActiveFamilyAndAFreshShownFamily_CreatesItOnTheServerAndMakesItHome()
    {
        // Arrange: spec §8.4, after the removal of the only family.
        await SignInAsync();
        Memberships();
        _moves.ActiveHouseholdAsync(Arg.Any<CancellationToken>()).Returns(Provisional);
        _moves.HasEverSyncedAsync(Provisional, Arg.Any<CancellationToken>()).Returns(false);

        // Act
        var report = await Cycle().RunAsync(CancellationToken.None);

        // Assert
        await _households.Received(1).CreateHouseholdAsync(Provisional, Arg.Any<CancellationToken>());
        _sessions.Stored!.AccountHousehold.Should().Be(Provisional);
        report.RefreshFamilies.Should().BeTrue();
        report.RunAgain.Should().BeTrue();
    }

    [Fact]
    public async Task RunAsync_CreatedIdTaken_ReKeysTheFamilyOnceAndCreatesItAgain()
    {
        // Arrange
        await SignInAsync();
        Memberships();
        _moves.ActiveHouseholdAsync(Arg.Any<CancellationToken>()).Returns(Provisional);
        _moves.HasEverSyncedAsync(Provisional, Arg.Any<CancellationToken>()).Returns(false);
        _households.CreateHouseholdAsync(Provisional, Arg.Any<CancellationToken>()).ThrowsAsync(new HouseholdRefusedException(HouseholdErrors.IdTaken));
        HouseholdId? fresh = null;
        _moves.MoveAllAsync(Provisional, Arg.Do<HouseholdId>(id => fresh = id), User, Arg.Any<CancellationToken>()).Returns(true);

        // Act
        await Cycle().RunAsync(CancellationToken.None);

        // Assert
        fresh.Should().NotBeNull().And.NotBe(Provisional);
        await _households.Received(1).CreateHouseholdAsync(fresh!.Value, Arg.Any<CancellationToken>());
        _sessions.Stored!.AccountHousehold.Should().Be(fresh.Value);
    }

    [Fact]
    public async Task RunAsync_CreatedButAlreadyHasFamily_RefreshesTheListAndKeepsTheFamilyLocal()
    {
        // Arrange: invited back meanwhile (spec §8.4).
        await SignInAsync();
        Memberships();
        _moves.ActiveHouseholdAsync(Arg.Any<CancellationToken>()).Returns(Provisional);
        _moves.HasEverSyncedAsync(Provisional, Arg.Any<CancellationToken>()).Returns(false);
        _households.CreateHouseholdAsync(Provisional, Arg.Any<CancellationToken>()).ThrowsAsync(new HouseholdRefusedException(HouseholdErrors.AlreadyHasFamily));

        // Act
        var report = await Cycle().RunAsync(CancellationToken.None);

        // Assert
        report.RefreshFamilies.Should().BeTrue();
        _sessions.Stored!.AccountHousehold.Should().Be(Household);
        await _store.DidNotReceiveWithAnyArgs().PrepareForPushAsync(default, default, default);
    }

    [Fact]
    public async Task RunAsync_MalformedRefusal_StopsAsNoProgress()
    {
        // Arrange: spec §9.
        await SignInAsync();
        _api.PullAsync(Household, Arg.Any<long>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new SyncRefusedException(SyncRefusal.Malformed, null));

        // Act
        var report = await Cycle().RunAsync(CancellationToken.None);

        // Assert
        report.Should().Match<CycleReport>(r => r.Outcome == CycleOutcome.Failed && r.Error == SyncCycle.NoProgressError);
    }

    [Fact]
    public async Task RunAsync_StoppedFamily_IsSkipped()
    {
        // Arrange
        await SignInAsync();
        Memberships(Household, Joined);
        _stops.Stop(Joined);

        // Act
        await Cycle().RunAsync(CancellationToken.None);

        // Assert
        await _api.DidNotReceive().PullAsync(Joined, Arg.Any<long>(), Arg.Any<long?>(), Arg.Any<CancellationToken>());
    }
}
```

In `tests/Coo.Mobile.Infrastructure.Tests/HouseholdSwitchScenarioTests.cs`, pass the new collaborators to the cycle: the
real `FamilyStore` (Task 11), which answers null ("not refreshed yet") until a list is stored, like a phone just upgraded:

```csharp
    private SyncCycle Cycle() => new(
        _account, _sessions, new ConsentGate(_state, Substitute.For<IAccountApi>(), Substitute.For<IConsentApi>()), _store, _moves, _state, _api,
        _db.Device, _db.Device, _db.Time, new FamilyStore(_db.Database), Substitute.For<IHouseholdApi>(), new SyncStops());
```

(add `using Coo.Mobile.Application.Households;`).

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Coo.Mobile.Application.Tests --filter "FullyQualifiedName~SyncCycleTests|FullyQualifiedName~MultiFamilyCycleTests"`
Expected: FAIL to compile (`SyncStops` not found; `SyncCycle` takes 10 arguments).

- [ ] **Step 3: Write the implementation**

`src/Mobile/Coo.Mobile.Application/Sync/SyncStops.cs`:

```csharp
using System.Collections.Concurrent;
using Coo.Domain.Common;

namespace Coo.Mobile.Application.Sync;

/// <summary>
/// Families that answered <c>sync.not-a-member</c> (Spec 011b §9): each stops until the next wake (start, resume, Sync now,
/// an account change), while the others go on. One per app.
/// </summary>
public sealed class SyncStops
{
    private readonly ConcurrentDictionary<HouseholdId, byte> _stopped = new();

    public void Stop(HouseholdId household) => _stopped[household] = 0;

    public bool IsStopped(HouseholdId household) => _stopped.ContainsKey(household);

    public void Clear() => _stopped.Clear();
}
```

`Model.cs`, `CycleReport`:

```csharp
/// <param name="RefreshFamilies">The family list should be refreshed: a soft-lock, a not-a-member, a family created (Spec 011b §7.1).</param>
/// <param name="NotAMember">Families that answered <c>sync.not-a-member</c> this cycle (spec §9).</param>
public sealed record CycleReport(
    CycleOutcome Outcome, bool DataChanged = false, string? Error = null, HouseholdId? Household = null, bool RunAgain = false,
    bool RefreshFamilies = false, IReadOnlyList<HouseholdId>? NotAMember = null);
```

`SyncCycle.cs` (whole file):

```csharp
using Coo.Contracts.Households;
using Coo.Domain.Common;
using Coo.Mobile.Application.Auth;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Application.Households;

namespace Coo.Mobile.Application.Sync;

/// <summary>
/// One sync cycle (Spec 011 §6.3, Spec 011b §7.2): the home family first, then every other account family in joined order,
/// each with its own soft-lock check, push and pull. It captures the account's generation at the start and stops at its next
/// step once it has moved (Spec 011 §8.1). It never runs signed out or without consent. Mid-join, nothing is pushed or
/// pulled for a family that has never synced (§5.3); a shown family that is not an account family is never pushed (ruling 1).
/// It throws only for programming errors; network and server answers become a <see cref="CycleReport"/>.
/// </summary>
public sealed class SyncCycle(
    Account account, ISessionStore sessions, ConsentGate consent, ISyncStore store, IHouseholdMoves moves, ISyncState state,
    ISyncApi api, IVersionClock clock, IDeviceIdentity device, TimeProvider time, IFamilyStore families, IHouseholdApi households,
    SyncStops stops)
{
    public const int PushBatchSize = 200;

    /// <summary>No longer produced (Spec 011b ruling 1); kept so a stored status from 0.11.0 still reads.</summary>
    public const string HouseholdMismatchError = "household-mismatch";

    public const string NoProgressError = "no-progress";

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

        var run = new Run(generation, user);
        try
        {
            var local = await moves.ActiveHouseholdAsync(cancellationToken);

            // Consent and the active household were awaited: a sign-out meanwhile ends the cycle before anything is written.
            if (Moved(run))
                return new CycleReport(CycleOutcome.Interrupted);

            var memberships = await families.ListAsync(user, cancellationToken);
            var joining = await families.GetJoiningAsync(cancellationToken);
            var midJoin = joining is not null || await families.GetPendingInvitationAsync(cancellationToken) is not null;

            var home = await EnsureHomeAsync(session, local, memberships, run, cancellationToken);
            if (run.Interrupted)
                return run.Report();

            var homeActive = memberships is null || session.JoinPending || memberships.Any(m => m.Household == home);
            if (homeActive && !stops.IsStopped(home))
                await HomeStepAsync(session, home, local, midJoin, joining, run, cancellationToken);

            foreach (var family in Others(memberships, home, joining))
            {
                if (run.Interrupted || Moved(run))
                {
                    run.Interrupted = true;
                    break;
                }

                if (stops.IsStopped(family))
                    continue;
                if (midJoin && family != joining?.Joined && !await moves.HasEverSyncedAsync(family, cancellationToken))
                    continue;
                if (await state.IsSoftLockedAsync(family, cancellationToken))
                {
                    await CheckMembershipAsync(family, shown: family == local, run, cancellationToken);
                    continue;
                }

                await SyncFamilyAsync(family, local, run, cancellationToken);
            }

            return run.Report();
        }
        catch (StoppedException e)
        {
            return new CycleReport(CycleOutcome.Failed, run.Changed, e.Message);
        }
        catch (SyncRefusedException e) when (e.Refusal == SyncRefusal.ConsentRequired)
        {
            await consent.ForgetAsync(user, cancellationToken);
            return new CycleReport(CycleOutcome.ConsentNeeded, run.Changed);
        }
        catch (SyncRefusedException e) when (e.Refusal == SyncRefusal.ClientTooOld)
        {
            return new CycleReport(CycleOutcome.ClientTooOld, run.Changed, e.Refusal.ToString());
        }
        catch (SyncRefusedException e) when (e.Refusal == SyncRefusal.Malformed)
        {
            return new CycleReport(CycleOutcome.Failed, run.Changed, NoProgressError);
        }
        catch (SyncRefusedException e)
        {
            return new CycleReport(CycleOutcome.Failed, run.Changed, e.Refusal.ToString());
        }
        catch (SyncUnavailableException)
        {
            return new CycleReport(CycleOutcome.Failed, run.Changed, nameof(SyncUnavailableException));
        }
    }

    /// <summary>The account's other families, in joined order, and the joined family when the list does not have it yet.</summary>
    private static List<HouseholdId> Others(IReadOnlyList<FamilyMembership>? memberships, HouseholdId home, JoiningMarker? joining)
    {
        var others = (memberships ?? []).Select(m => m.Household).Where(h => h != home).ToList();
        if (joining is not null && joining.Joined != home && !others.Contains(joining.Joined))
            others.Add(joining.Joined);
        return others;
    }

    /// <summary>
    /// Spec 011b §7.1 and §8.4: the home family follows the account's memberships. Returns the home family to sync.
    /// </summary>
    private async Task<HouseholdId> EnsureHomeAsync(
        StoredSession session, HouseholdId local, IReadOnlyList<FamilyMembership>? memberships, Run run, CancellationToken cancellationToken)
    {
        var home = session.AccountHousehold;
        if (memberships is null || session.JoinPending || memberships.Any(m => m.Household == home))
            return home;

        if (memberships.Count > 0)
        {
            var next = memberships[0].Household;
            if (!await account.TrySetHomeAsync(run.Generation, next, cancellationToken))
                run.Interrupted = true;
            return next;
        }

        // No active family on the server: the shown family becomes it, if it has never been on a server.
        if (await moves.HasEverSyncedAsync(local, cancellationToken))
            return home;
        var target = local;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                await households.CreateHouseholdAsync(target, cancellationToken);
                if (!await account.TrySetHomeAsync(run.Generation, target, cancellationToken))
                {
                    run.Interrupted = true;
                    return home;
                }

                // The list does not know the new family yet: refresh it, then sync at once.
                (run.RefreshFamilies, run.RunAgain) = (true, true);
                return home;
            }
            catch (HouseholdRefusedException e) when (e.Code == HouseholdErrors.IdTaken && attempt == 0)
            {
                if (Moved(run))
                {
                    run.Interrupted = true;
                    return home;
                }

                var fresh = new HouseholdId(Guid.CreateVersion7(time.GetUtcNow()));
                await moves.MoveAllAsync(target, fresh, run.User, cancellationToken);
                run.Changed = true;
                target = fresh;
            }
            catch (HouseholdRefusedException e) when (e.Code == HouseholdErrors.AlreadyHasFamily)
            {
                run.RefreshFamilies = true;
                return home;
            }
            catch (HouseholdRefusedException e)
            {
                throw new SyncUnavailableException("The family could not be created: " + e.Code, e);
            }
            catch (HouseholdUnavailableException e)
            {
                throw new SyncUnavailableException("The family could not be created now.", e);
            }
        }

        return home;
    }

    private async Task HomeStepAsync(
        StoredSession session, HouseholdId home, HouseholdId local, bool midJoin, JoiningMarker? joining, Run run, CancellationToken cancellationToken)
    {
        if (await state.IsSoftLockedAsync(home, cancellationToken))
        {
            await CheckMembershipAsync(home, shown: home == local || session.JoinPending, run, cancellationToken);
            return;
        }

        if (session.JoinPending)
        {
            if (local == home)
            {
                // The bring-over committed, but the app stopped before the session said so.
                await account.TryCompleteJoinAsync(run.Generation, cancellationToken);
                run.Interrupted = true;
                return;
            }

            run.HomeJoinPending = true;
            await PullAllAsync(home, run, cancellationToken);
            return;
        }

        // Spec 011b §5.3: nothing of a never-synced family moves to a server mid-join; its pull would give it a cursor (ruling 5).
        if (midJoin && joining?.Joined != home && !await moves.HasEverSyncedAsync(home, cancellationToken))
            return;

        if (session.PendingMoveFrom is { } from && !midJoin)
        {
            if (from == local && local != home)
            {
                if (Moved(run))
                {
                    run.Interrupted = true;
                    return;
                }

                // A household that ever synced is never moved: the account's household simply shows instead.
                await moves.MoveAllAsync(local, home, run.User, cancellationToken);
                run.Changed = true;
            }

            await account.TryClearPendingMoveAsync(run.Generation, cancellationToken);
        }

        await SyncFamilyAsync(home, local, run, cancellationToken);
    }

    /// <summary>Push, then pull, for one family; this family's own refusals are handled here and the cycle goes on.</summary>
    private async Task SyncFamilyAsync(HouseholdId household, HouseholdId local, Run run, CancellationToken cancellationToken)
    {
        try
        {
            if (Moved(run))
            {
                run.Interrupted = true;
                return;
            }

            await store.PrepareForPushAsync(household, run.User, cancellationToken);
            var deviceId = await device.GetAsync(cancellationToken);
            Guid? previousFirst = null;
            while (true)
            {
                if (Moved(run))
                {
                    run.Interrupted = true;
                    return;
                }

                var batch = await store.NextBatchAsync(household, PushBatchSize, cancellationToken);
                if (batch.Count == 0)
                    break;

                // Every change of the last batch was answered, so the store must have moved on; if not, stop rather than loop.
                if (batch[0].ChangeId == previousFirst)
                    throw new StoppedException(NoProgressError);
                previousFirst = batch[0].ChangeId;

                var results = await api.PushAsync(household, deviceId, batch, cancellationToken);
                await store.ApplyResultsAsync(household, batch, results, time.GetUtcNow(), cancellationToken);

                // A change the server did not answer stays in the outbox; retrying cannot fix it (Spec 011b §9, ruling 21).
                var answered = results.Select(r => r.ChangeId).ToHashSet();
                if (batch.Any(c => !answered.Contains(c.ChangeId)))
                    throw new StoppedException(NoProgressError);
                if (batch.Count < PushBatchSize)
                    break;
            }

            await PullAllAsync(household, run, cancellationToken);
        }
        catch (SyncRefusedException e) when (e.Refusal == SyncRefusal.MembershipRevoked && e.Household == household)
        {
            // ADR 0002 §10 step 2: soft-lock first; delete nothing. Not for an account this phone no longer holds.
            if (Moved(run))
            {
                run.Interrupted = true;
                return;
            }

            await state.SetSoftLockAsync(household, true, cancellationToken);
            run.RefreshFamilies = true;
            if (household == local)
                run.ShownSoftLocked = true;
        }
        catch (SyncRefusedException e) when (e.Refusal == SyncRefusal.NotAMember && e.Household == household)
        {
            stops.Stop(household);
            run.RefreshFamilies = true;
            run.NotAMember.Add(household);
        }
    }

    private bool Moved(Run run) => account.Generation != run.Generation;

    private async Task CheckMembershipAsync(HouseholdId household, bool shown, Run run, CancellationToken cancellationToken)
    {
        var status = await api.GetMembershipAsync(household, cancellationToken);
        if (Moved(run))
        {
            run.Interrupted = true;
            return;
        }

        switch (status)
        {
            case MembershipStatus.Active:
                // Lifted: the next cycle syncs at once rather than at the next 30-second tick.
                await state.SetSoftLockAsync(household, false, cancellationToken);
                (run.Interrupted, run.RunAgain) = (true, true);
                return;
            case MembershipStatus.Revoked:
                // Confirmed. Deleting waits for the person (ADR 0002 §10; the removal sheet, Spec 011b §8.2).
                run.Removed ??= household;
                return;
            default:
                run.RefreshFamilies = true;
                if (shown)
                    run.ShownSoftLocked = true;
                return;
        }
    }

    private async Task PullAllAsync(HouseholdId household, Run run, CancellationToken cancellationToken)
    {
        while (true)
        {
            if (Moved(run))
            {
                run.Interrupted = true;
                return;
            }

            var cursor = await store.GetCursorAsync(household, cancellationToken);
            var page = await api.PullAsync(household, cursor.Cursor, cursor.BootstrapHead, cancellationToken);
            if (page.HasMore && page.Cursor <= cursor.Cursor)
                throw new SyncUnavailableException("The server reported more records without moving the cursor.");

            // A page for an account this phone no longer holds is not applied.
            if (Moved(run))
            {
                run.Interrupted = true;
                return;
            }

            // Observe first: a crash after the page commits must not leave the clock behind a stored stamp.
            if (page.Records.Count > 0)
            {
                run.Changed = true;
                await clock.ObserveAsync(page.Records.Max(r => r.Version), cancellationToken);
            }

            await store.ApplyPageAsync(household, page, cancellationToken);
            if (!page.HasMore)
                return;
        }
    }

    /// <summary>What one cycle found across its families; the outcome follows ruling 20's precedence.</summary>
    private sealed class Run(long generation, UserId user)
    {
        public long Generation { get; } = generation;

        public UserId User { get; } = user;

        public bool Changed { get; set; }

        public bool Interrupted { get; set; }

        public bool RunAgain { get; set; }

        public bool RefreshFamilies { get; set; }

        public bool HomeJoinPending { get; set; }

        public bool ShownSoftLocked { get; set; }

        public HouseholdId? Removed { get; set; }

        public List<HouseholdId> NotAMember { get; } = [];

        public CycleReport Report()
        {
            var outcome = Interrupted ? CycleOutcome.Interrupted
                : Removed is not null ? CycleOutcome.Removed
                : ShownSoftLocked ? CycleOutcome.SoftLocked
                : HomeJoinPending ? CycleOutcome.JoinPending
                : CycleOutcome.Synced;
            return new CycleReport(outcome, Changed, Household: Removed, RunAgain: RunAgain, RefreshFamilies: RefreshFamilies,
                NotAMember: NotAMember.Count > 0 ? [.. NotAMember] : null);
        }
    }

    /// <summary>A stop that retrying cannot fix: the cycle reports it as Failed with its message as the error.</summary>
    private sealed class StoppedException : Exception
    {
        public StoppedException()
            : this(NoProgressError)
        {
        }

        public StoppedException(string message)
            : base(message)
        {
        }

        public StoppedException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
}
```

Register `services.AddSingleton<SyncStops>();` beside `SyncEngine`.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Coo.Mobile.Application.Tests` and `dotnet test tests/Coo.Mobile.Infrastructure.Tests --filter "FullyQualifiedName~HouseholdSwitchScenarioTests"`
Expected: PASS. Two existing tests whose outcome rules changed must still pass as written:
`RunAsync_SoftLockedWhileJoinPending_OnlyAsksAboutTheMembership` (SoftLocked: the home is soft-locked while join-pending)
and `RunAsync_SoftLockedButActiveAgain_AsksForTheNextCycleAtOnce` (Interrupted with RunAgain). If either fails, the
precedence in `Run.Report` is wrong, not the test. The engine test `Start_HouseholdMismatch_…` fails here and is replaced
in Task 15.

- [ ] **Step 5: Commit**

```bash
git add src/Mobile/Coo.Mobile.Application tests/Coo.Mobile.Application.Tests tests/Coo.Mobile.Infrastructure.Tests/HouseholdSwitchScenarioTests.cs
git commit -m "Mobile.Application: every account family syncs; the home family follows the memberships; mid-join guard (Spec 011b §5.3, §7.2, §8.4, §9)"
```

---

### Task 15: The engine: families refreshed at wakes, Pending across families, the removal acknowledged

**Files:**
- Modify: `src/Mobile/Coo.Mobile.Application/Sync/SyncEngine.cs`
- Test: `tests/Coo.Mobile.Application.Tests/SyncEngineTests.cs`

**Interfaces:**
- Consumes: Task 10's `Families`, `IFamilyStore.ClearAsync`, `IFamilyRecords.CountPendingAsync`; Task 14's `SyncStops`, `CycleReport`.
- Produces:
  - `SyncEngine(SyncCycle cycle, ISyncStore store, IHouseholdMoves moves, ISessionStore sessions, Account account, ConsentGate consent,
    LocalWrites writes, TimeProvider time, Families families, IFamilyStore familyStore, IFamilyRecords records, SyncStops stops, SyncOptions? options = null)`.
  - `SyncStatus` gains `bool ShownStopped = false` (the shown family answered `not-a-member`).
  - `Pending` counts the outbox of the families that sync (ruling 6).
  - `AcknowledgeRemovalAsync` → `Families.RemoveFromPhoneAsync(household)`, refresh, `DataChanged`, a cycle.
  - After every cycle: refresh the list when the report asks; `Families.HideSoftLockedAsync()` (a switch raises `DataChanged`).
  - Every wake also clears `SyncStops` and refreshes the family list before the next cycle; sign-out clears the list.

- [ ] **Step 1: Write the failing tests**

In `SyncEngineTests` add the collaborators and build the engine with them:

```csharp
    private readonly IFamilyStore _familyStore = Substitute.For<IFamilyStore>();
    private readonly IFamilyRecords _records = Substitute.For<IFamilyRecords>();
    private readonly IHouseholdApi _householdApi = Substitute.For<IHouseholdApi>();
    private readonly IHouseholdStore _householdStore = Substitute.For<IHouseholdStore>();
    private readonly SyncStops _stops = new();
```

and in the constructor:

```csharp
        _records.CountPendingAsync(default!, default).ReturnsForAnyArgs(0);
        _familyStore.ListAsync(default, default).ReturnsForAnyArgs((IReadOnlyList<FamilyMembership>?)null);
        _store.NextBatchAsync(Provisional, Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);
        _store.GetCursorAsync(Provisional, Arg.Any<CancellationToken>()).Returns(new SyncCursor(0, null));
        _api.PullAsync(Provisional, Arg.Any<long>(), Arg.Any<long?>(), Arg.Any<CancellationToken>()).Returns(new PulledPage([], 0, false, 0));
        var locale = Substitute.For<IDeviceLocale>();
        locale.TimeZoneId.Returns("Europe/Amsterdam");
        var provisionalClock = Substitute.For<IVersionClock>();
        provisionalClock.NextAsync(Arg.Any<CancellationToken>()).Returns(new Coo.Domain.Common.Versioning.HlcStamp(1_790_000_000_000, 0, Phone));
        var families = new Families(_familyStore, _accounts, _householdApi, _sessions, _householdStore, _moves, _state, _records,
            new ProvisionalSettings(provisionalClock, locale, _time));
        var cycle = new SyncCycle(_account, _sessions, consent, _store, _moves, _state, _api, Substitute.For<IVersionClock>(), device, _time,
            _familyStore, _householdApi, _stops);
        _engine = new SyncEngine(cycle, _store, _moves, _sessions, _account, consent, _writes, _time, families, _familyStore, _records, _stops,
            new SyncOptions(() => 1.0));
```

(add `using Coo.Mobile.Application.Households;`). Change the existing pending expectations: the tests that set
`_store.CountsAsync(Household, …).Returns(new SyncCounts(0, 2, 0))` for the pending count now set
`_records.CountPendingAsync(default!, default).ReturnsForAnyArgs(2);` instead.

Delete `Start_HouseholdMismatch_StatusCarriesItAndTheForegroundTicksDoNotRetry` (ruling 1; `SyncNowAsync_NoProgress_…`
keeps the stop-until-wake behaviour covered). Change `AcknowledgeRemovalAsync_AfterAConfirmedRemoval_DeletesTheHouseholdThenAndOnlyThen`'s
assertion from `_moves.Received(1).DeleteHouseholdAsync(Household, …)` to "removed from the phone": with
`_householdStore.ListHouseholdsAsync(…)` returning only `Household`'s settings, it is `_records.Received(1).DeleteAndReplaceAsync(Household, Arg.Any<HouseholdSettings>(), Arg.Any<CancellationToken>())`.

Append:

```csharp
    [Fact]
    public async Task SyncNowAsync_PendingInTwoFamilies_StatusCountsBoth()
    {
        // Arrange: spec §7.2, Pending across the families that sync.
        await SignInAsync();
        _familyStore.ListAsync(User, Arg.Any<CancellationToken>()).Returns([
            new FamilyMembership(Household, Coo.Domain.Households.Role.Owner, Now, "Andrzej", 1),
            new FamilyMembership(Provisional, Coo.Domain.Households.Role.Parent, Now, "Andrzej", 2)]);
        _records.CountPendingAsync(Arg.Is<IReadOnlyCollection<HouseholdId>>(s => s.Count == 2 && s.Contains(Household) && s.Contains(Provisional)), Arg.Any<CancellationToken>())
            .Returns(3);

        // Act
        await _engine.SyncNowAsync(CancellationToken.None);

        // Assert
        _engine.Status.Pending.Should().Be(3);
    }

    [Fact]
    public async Task SyncNowAsync_ShownFamilyNotAMember_StatusSaysShownStoppedUntilTheNextWake()
    {
        // Arrange
        await SignInAsync();
        _api.PullAsync(Household, Arg.Any<long>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<PulledPage>(new SyncRefusedException(SyncRefusal.NotAMember, Household)));

        // Act
        await _engine.SyncNowAsync(CancellationToken.None);
        var stopped = _engine.Status.ShownStopped;
        _api.PullAsync(Household, Arg.Any<long>(), Arg.Any<long?>(), Arg.Any<CancellationToken>()).Returns(new PulledPage([], 0, false, 0));
        _engine.Resumed();
        await _engine.WhenIdleAsync();

        // Assert
        stopped.Should().BeTrue();
        _engine.Status.ShownStopped.Should().BeFalse("a resume is a wake");
    }

    [Fact]
    public async Task Resumed_AWake_RefreshesTheFamilyListBeforeTheCycle()
    {
        // Arrange
        await SignInAsync();
        _accounts.GetMeAsync(Arg.Any<CancellationToken>()).Returns(new AccountOverview(Profile, Household, "owner", [], null,
            [new MembershipInfo(Household, Coo.Domain.Households.Role.Owner, Now)]));
        _householdApi.ListMembersAsync(Household, Arg.Any<CancellationToken>()).Returns(new FamilyMembers([], []));

        // Act
        _engine.Resumed();
        await _engine.WhenIdleAsync();

        // Assert
        await _familyStore.Received().ReplaceAsync(User, Arg.Any<IReadOnlyList<FamilyMembership>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SignedOut_ClearsTheFamilyList()
    {
        // Arrange
        await SignInAsync();

        // Act
        await _account.SignedOutAsync(sessionExpired: false, CancellationToken.None);
        await _engine.WhenIdleAsync();

        // Assert
        await _familyStore.Received().ClearAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncNowAsync_ShownFamilySoftLockedWithAnother_SwitchesAndTellsThePages()
    {
        // Arrange: spec §8.2, a soft-locked family is hidden whenever another exists.
        await SignInAsync();
        _householdStore.ListHouseholdsAsync(Arg.Any<CancellationToken>()).Returns([FamiliesTests.Settings(Household), FamiliesTests.Settings(Provisional)]);
        _householdStore.ListBabiesAsync(default, default).ReturnsForAnyArgs([]);
        _state.IsSoftLockedAsync(Household, Arg.Any<CancellationToken>()).Returns(true);
        _api.GetMembershipAsync(Household, Arg.Any<CancellationToken>()).Returns(MembershipStatus.Unknown);
        var changed = 0;
        _engine.DataChanged += (_, _) => changed++;

        // Act
        await _engine.SyncNowAsync(CancellationToken.None);

        // Assert
        await _moves.Received(1).ActivateAsync(Provisional, Arg.Any<CancellationToken>());
        changed.Should().BeGreaterThan(0);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Coo.Mobile.Application.Tests --filter "FullyQualifiedName~SyncEngineTests"`
Expected: FAIL to compile (the engine takes nine arguments; `ShownStopped` missing).

- [ ] **Step 3: Write the implementation**

In `SyncEngine.cs`:

`SyncStatus` gains a parameter (keep `Initial` as is; the default is false):

```csharp
/// <param name="Pending">Outbox records of the families that sync (Spec 011b §7.2, ruling 6).</param>
/// <param name="ShownStopped">The shown family answered <c>not-a-member</c> and stopped until a wake (Spec 011b §9).</param>
public sealed record SyncStatus(
    CycleOutcome? Last, DateTimeOffset? LastSyncedAt, int Pending, bool IsRunning, string? LastError, HouseholdId? Removal, bool JoinReady = false,
    bool ShownStopped = false)
```

Constructor: add the four collaborators as fields (`_families`, `_familyStore`, `_records`, `_stops`) and a flag
`private int _refreshFamilies = 1;` (the first cycle refreshes).

`Wake()` becomes:

```csharp
    private void Wake()
    {
        Interlocked.Increment(ref _wakes);
        _stops.Clear();
        Interlocked.Exchange(ref _refreshFamilies, 1);
    }
```

`RunCycleAsync`, before `report = await _cycle.RunAsync(cancellationToken);`:

```csharp
        if (Interlocked.Exchange(ref _refreshFamilies, 0) == 1)
            await RefreshFamiliesAsync(cancellationToken);
```

and after the cycle (after `var pending …` lines are computed is too late for a switch; put this right after the cycle's
try/catch):

```csharp
        if (report.RefreshFamilies)
            await RefreshFamiliesAsync(cancellationToken);
        var switched = await HideSoftLockedAsync(cancellationToken);
        var shownStopped = await ShownStoppedAsync(cancellationToken);
```

In the status update add `ShownStopped = shownStopped,`, and after `if (report.DataChanged) Raise(DataChanged);` add
`else if (switched) Raise(DataChanged);`.

Replace `PendingAsync`:

```csharp
    private async Task<int?> PendingAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _records.CountPendingAsync(await _families.SyncingHouseholdsAsync(cancellationToken), cancellationToken);
        }
#pragma warning disable CA1031 // The count is for the sync line only; the last known one stays.
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
    }
```

Add the helpers:

```csharp
    private async Task RefreshFamiliesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _families.RefreshAsync(cancellationToken);
        }
#pragma warning disable CA1031 // The list is refreshed again at the next wake; a failure never stops a cycle.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    private async Task<bool> HideSoftLockedAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _families.HideSoftLockedAsync(cancellationToken);
        }
#pragma warning disable CA1031 // The next cycle tries again; the shown family stays shown meanwhile.
        catch (Exception)
#pragma warning restore CA1031
        {
            return false;
        }
    }

    private async Task<bool> ShownStoppedAsync(CancellationToken cancellationToken)
    {
        try
        {
            return _stops.IsStopped(await _moves.ActiveHouseholdAsync(cancellationToken));
        }
#pragma warning disable CA1031 // For the sync line only.
        catch (Exception)
#pragma warning restore CA1031
        {
            return false;
        }
    }
```

Replace `AcknowledgeRemovalAsync`:

```csharp
    /// <summary>
    /// The person pressed OK on the removal sheet (Spec 011b §8.2): the family goes from this phone (ADR 0002 §10), replaced
    /// by a new provisional family when it was the last one (§8.4), and the list is refreshed. Nothing is deleted before.
    /// </summary>
    public async Task AcknowledgeRemovalAsync(CancellationToken cancellationToken)
    {
        if (Status.Removal is not { } household)
            return;
        await _families.RemoveFromPhoneAsync(household, cancellationToken);
        Update(s => s with { Removal = null, Last = null });
        await RefreshFamiliesAsync(cancellationToken);
        Raise(DataChanged);
        RequestSync();
    }
```

In `OnAccountChanged`, in the `now is SignedOut` branch, before `_scheduler.Request(…)`:

```csharp
            _ = ClearFamiliesAsync();
```

with

```csharp
    private async Task ClearFamiliesAsync()
    {
        try
        {
            await _familyStore.ClearAsync(CancellationToken.None);
        }
#pragma warning disable CA1031 // Fire-and-forget: the next sign-in replaces the list anyway.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }
```

`CountsAsync` (debug screen) stays on the home family. Register nothing new (the engine is already a singleton; its new
dependencies are registered in Tasks 10–14).

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Coo.Mobile.Application.Tests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Mobile/Coo.Mobile.Application tests/Coo.Mobile.Application.Tests
git commit -m "Mobile.Application: the engine refreshes families at wakes, counts Pending across them, hides a soft-locked family, acknowledges a removal (Spec 011b §7, §8.2, §8.4)"
```

---

### Task 16: Joining: the pending invitation, preview, accept, and what happens to this phone's entries

**Files:**
- Create: `src/Mobile/Coo.Mobile.Application/Households/JoinFamily.cs`, `src/Mobile/Coo.Mobile.Application/Households/BringFamilyEntriesOver.cs`
- Modify: `src/Mobile/Coo.Mobile.Application/ServiceCollectionExtensions.cs`
- Test: `tests/Coo.Mobile.Application.Tests/JoinFamilyTests.cs`, `tests/Coo.Mobile.Application.Tests/BringFamilyEntriesOverTests.cs`

**Interfaces:**
- Consumes: Tasks 10–15; `ConsentGate`, `BringOverPlan`, `BringOverChoice`, `LocalSummary`, `IVersionClock`.
- Produces:
  - `BringFamilyEntriesOver(ISessionStore sessions, Account account, IHouseholdMoves moves, IFamilyRecords records, IFamilyStore families, IHouseholdApi api, IVersionClock clock, Families list)`:
    `PrepareAsync(HouseholdId source, HouseholdId target, CancellationToken) → BringOverChoice`,
    `ExecuteAsync(HouseholdId source, HouseholdId target, IReadOnlyDictionary<BabyId, BabyId?> mapping, CancellationToken) → BringOutcome`,
    `LeaveEmptyAdoptedAsync(HouseholdId source, CancellationToken)`.
    `enum BringPath { Moved, Copied, AlreadyCopied }`, `enum OldFamilyQuestion { None, DeleteOld, LeaveOld }`,
    `BringOutcome(BringPath Path, int Copied, int Skipped, OldFamilyQuestion Question)`.
  - `JoinFamily(IFamilyStore families, IHouseholdApi api, ISessionStore sessions, Account account, ConsentGate consent, IHouseholdMoves moves, IHouseholdStore households, ISyncStore store, ISyncControl sync, Families list, BringFamilyEntriesOver bring, TimeProvider time)`:
    `ReceiveAsync(string? text, CancellationToken) → bool`, `NeedsSignInAsync → bool`, `StageAsync → JoinStage`, `PreviewAsync → JoinPreview`,
    `HasConsentAsync → bool`, `AcceptAsync(string inviterName, CancellationToken) → JoinAccept`, `DecideAsync → JoinChoice`,
    `BringAsync(mapping) → BringOutcome`, `KeepSeparateAsync`, `DeclineAsync`.
    `enum JoinStage { None, Invitation, Choice }`; `enum PreviewStatus { Ready, AlreadyMember, Invalid, RateLimited, Unavailable, NothingPending }`,
    `JoinPreview(PreviewStatus Status, InvitationPreview? Invitation = null)`;
    `enum AcceptStatus { Joined, AlreadyMember, NeedsConsent, Invalid, RateLimited, Unavailable, NothingPending }`, `JoinAccept(AcceptStatus Status)`;
    `enum ChoiceStatus { NotReady, NothingAsked, Ask }`, `JoinChoice(ChoiceStatus Status, string InviterName, BringOverChoice? Offer = null, bool Copies = false, HouseholdId Source = default)`.

- [ ] **Step 1: Write the failing tests**

`tests/Coo.Mobile.Application.Tests/BringFamilyEntriesOverTests.cs`:

```csharp
using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;
using Coo.Domain.Households;
using Coo.Mobile.Application.Auth;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Application.Households;
using Coo.Mobile.Application.Sync;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using static Coo.Mobile.Application.Tests.FamiliesTests;

namespace Coo.Mobile.Application.Tests;

/// <summary>Spec 011b §6.1–§6.2: move a never-synced source, copy a synced one, then the question about the old family.</summary>
public sealed class BringFamilyEntriesOverTests : IDisposable
{
    private static readonly BabyId MyMaja = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000b1"));
    private static readonly BabyId TheirMaja = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000b9"));
    private static readonly DeviceId Phone = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000d1"));

    private readonly MemorySessions _sessions = new();
    private readonly Account _account;
    private readonly IHouseholdMoves _moves = Substitute.For<IHouseholdMoves>();
    private readonly IFamilyRecords _records = Substitute.For<IFamilyRecords>();
    private readonly IFamilyStore _store = Substitute.For<IFamilyStore>();
    private readonly IHouseholdApi _api = Substitute.For<IHouseholdApi>();
    private readonly IVersionClock _clock = Substitute.For<IVersionClock>();
    private readonly IHouseholdStore _households = Substitute.For<IHouseholdStore>();

    public BringFamilyEntriesOverTests()
    {
        _account = new Account(_sessions);
        _moves.SummaryAsync(Home, Arg.Any<CancellationToken>()).Returns(new LocalSummary(42, [new LocalBaby(MyMaja, "Maja")]));
        _moves.SummaryAsync(Joined, Arg.Any<CancellationToken>()).Returns(new LocalSummary(300, [new LocalBaby(TheirMaja, "Maja")]));
        _clock.NextAsync(Arg.Any<CancellationToken>()).Returns(new HlcStamp(1_790_000_000_000, 4, Phone));
        _records.CountRecordsAsync(Home, Arg.Any<CancellationToken>()).Returns(45);
        _households.ListHouseholdsAsync(Arg.Any<CancellationToken>()).Returns([Settings(Home), Settings(Joined)]);
    }

    public void Dispose() => _account.Dispose();

    private BringFamilyEntriesOver UseCase() => new(_sessions, _account, _moves, _records, _store, _api, _clock,
        new Families(_store, Substitute.For<IAccountApi>(), _api, _sessions, _households, _moves, Substitute.For<ISyncState>(), _records,
            new ProvisionalSettings(_clock, Substitute.For<IDeviceLocale>(), new FakeTimeProvider(Now))));

    private Task SignInAsync() => _account.SignedInAsync(
        new StoredSession("access", Now.AddMinutes(15), "refresh", Profile, Home, JoinPending: false), CancellationToken.None);

    private void MemberOf(HouseholdId household, Role role, int members) => _store.ListAsync(Me, Arg.Any<CancellationToken>()).Returns(
        [new FamilyMembership(household, role, Now, "Kasia", members), new FamilyMembership(Joined, Role.Parent, Now.AddDays(1), "Andrzej", 2)]);

    [Fact]
    public async Task ExecuteAsync_NeverSyncedSource_MovesItAndLeavesTheEmptyAdoptedFamily()
    {
        // Arrange: spec §6.1. Her adopted family, emptied by the move, is left with confirm: false.
        await SignInAsync();
        _moves.HasEverSyncedAsync(Home, Arg.Any<CancellationToken>()).Returns(false);
        _moves.BringOverAsync(default!, default, default).ReturnsForAnyArgs(true);
        MemberOf(Home, Role.Owner, 1);
        _moves.SummaryAsync(Home, Arg.Any<CancellationToken>()).Returns(new LocalSummary(42, [new LocalBaby(MyMaja, "Maja")]), new LocalSummary(0, []));

        // Act
        var outcome = await UseCase().ExecuteAsync(Home, Joined, new Dictionary<BabyId, BabyId?> { [MyMaja] = TheirMaja }, CancellationToken.None);

        // Assert
        outcome.Path.Should().Be(BringPath.Moved);
        await _moves.Received(1).BringOverAsync(Arg.Is<BringOverPlan>(p => p.From == Home && p.To == Joined && p.Babies[MyMaja] == TheirMaja), Me, Arg.Any<CancellationToken>());
        await _api.Received(1).LeaveAsync(Home, false, Arg.Any<CancellationToken>());
        await _records.DidNotReceiveWithAnyArgs().CopyAsync(default!, default, default, default);
    }

    [Fact]
    public async Task ExecuteAsync_EmptiedFamilyHasRecordsOnTheServer_StaysAsASeparateFamily()
    {
        // Arrange: another phone of hers had pushed into it: household.confirmation-required.
        await SignInAsync();
        _moves.HasEverSyncedAsync(Home, Arg.Any<CancellationToken>()).Returns(false);
        _moves.BringOverAsync(default!, default, default).ReturnsForAnyArgs(true);
        MemberOf(Home, Role.Owner, 1);
        _moves.SummaryAsync(Home, Arg.Any<CancellationToken>()).Returns(new LocalSummary(42, [new LocalBaby(MyMaja, "Maja")]), new LocalSummary(0, []));
        _api.LeaveAsync(Home, false, Arg.Any<CancellationToken>()).ThrowsAsync(new HouseholdRefusedException(Contracts.Households.HouseholdErrors.ConfirmationRequired));

        // Act
        var outcome = await UseCase().ExecuteAsync(Home, Joined, new Dictionary<BabyId, BabyId?>(), CancellationToken.None);

        // Assert
        outcome.Path.Should().Be(BringPath.Moved);
        await _moves.DidNotReceive().DeleteHouseholdAsync(Home, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_SyncedSourceOwnedAlone_CopiesWithAReservedStampRangeAndAsksToDeleteIt()
    {
        // Arrange: spec §6.2 and ruling 8.
        await SignInAsync();
        _moves.HasEverSyncedAsync(Home, Arg.Any<CancellationToken>()).Returns(true);
        MemberOf(Home, Role.Owner, 1);
        _records.CopyAsync(default!, default, default, default).ReturnsForAnyArgs(new CopyResult(40, 2));

        // Act
        var outcome = await UseCase().ExecuteAsync(Home, Joined, new Dictionary<BabyId, BabyId?> { [MyMaja] = TheirMaja }, CancellationToken.None);

        // Assert
        outcome.Should().Be(new BringOutcome(BringPath.Copied, 40, 2, OldFamilyQuestion.DeleteOld));
        Received.InOrder(() =>
        {
            _clock.ObserveAsync(new HlcStamp(1_790_000_000_000, 49, Phone), Arg.Any<CancellationToken>());
            _records.CopyAsync(Arg.Is<BringOverPlan>(p => p.From == Home && p.To == Joined), Me, new HlcStamp(1_790_000_000_000, 4, Phone), Arg.Any<CancellationToken>());
        });
        await _moves.DidNotReceiveWithAnyArgs().BringOverAsync(default!, default, default);
    }

    [Theory]
    [InlineData(Role.Parent, 2, OldFamilyQuestion.LeaveOld)]
    [InlineData(Role.Owner, 2, OldFamilyQuestion.None)]
    public async Task ExecuteAsync_SyncedSourceByRole_AsksTheRightQuestion(Role role, int members, OldFamilyQuestion expected)
    {
        // Arrange: spec §6.2's table; an Owner with others is asked nothing (§8.3).
        await SignInAsync();
        _moves.HasEverSyncedAsync(Home, Arg.Any<CancellationToken>()).Returns(true);
        MemberOf(Home, role, members);
        _records.CopyAsync(default!, default, default, default).ReturnsForAnyArgs(new CopyResult(40, 0));

        // Act
        var outcome = await UseCase().ExecuteAsync(Home, Joined, new Dictionary<BabyId, BabyId?>(), CancellationToken.None);

        // Assert
        outcome.Question.Should().Be(expected);
    }

    [Fact]
    public async Task BringAsync_AlreadyCopied_ReportsNothingNew()
    {
        // Arrange: Review Focus 3, a second tap or a retry after a crash.
        await SignInAsync();
        _moves.HasEverSyncedAsync(Home, Arg.Any<CancellationToken>()).Returns(true);
        _store.WasCopiedAsync(Home, Joined, Arg.Any<CancellationToken>()).Returns(true);

        // Act
        var outcome = await UseCase().ExecuteAsync(Home, Joined, new Dictionary<BabyId, BabyId?>(), CancellationToken.None);

        // Assert
        outcome.Path.Should().Be(BringPath.AlreadyCopied);
        await _records.DidNotReceiveWithAnyArgs().CopyAsync(default!, default, default, default);
    }

    [Fact]
    public async Task ExecuteAsync_MappingNamesABabyThatIsNotTheTargets_AddsItAsNew()
    {
        // Arrange
        await SignInAsync();
        _moves.HasEverSyncedAsync(Home, Arg.Any<CancellationToken>()).Returns(true);
        _records.CopyAsync(default!, default, default, default).ReturnsForAnyArgs(new CopyResult(1, 0));

        // Act
        await UseCase().ExecuteAsync(Home, Joined, new Dictionary<BabyId, BabyId?> { [MyMaja] = new BabyId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000b7")) },
            CancellationToken.None);

        // Assert
        await _records.Received(1).CopyAsync(Arg.Is<BringOverPlan>(p => p.Babies[MyMaja] == null), Me, Arg.Any<HlcStamp>(), Arg.Any<CancellationToken>());
    }
}
```

`tests/Coo.Mobile.Application.Tests/JoinFamilyTests.cs`:

```csharp
using Coo.Contracts.Households;
using Coo.Contracts.Sync;
using Coo.Domain.Common;
using Coo.Domain.Households;
using Coo.Mobile.Application.Auth;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Application.Households;
using Coo.Mobile.Application.Sync;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using static Coo.Mobile.Application.Tests.FamiliesTests;

namespace Coo.Mobile.Application.Tests;

/// <summary>Spec 011b §4–§6 on the phone: the pending invitation, each answer, the choice.</summary>
public sealed class JoinFamilyTests : IDisposable
{
    private static readonly InvitationCode Code = InvitationCode.Parse("ABCDEFGH").Value;

    private readonly MemorySessions _sessions = new();
    private readonly Account _account;
    private readonly IFamilyStore _store = Substitute.For<IFamilyStore>();
    private readonly IHouseholdApi _api = Substitute.For<IHouseholdApi>();
    private readonly IHouseholdMoves _moves = Substitute.For<IHouseholdMoves>();
    private readonly IHouseholdStore _households = Substitute.For<IHouseholdStore>();
    private readonly ISyncStore _syncStore = Substitute.For<ISyncStore>();
    private readonly ISyncState _state = Substitute.For<ISyncState>();
    private readonly ISyncControl _sync = Substitute.For<ISyncControl>();
    private readonly IAccountApi _accounts = Substitute.For<IAccountApi>();
    private readonly IFamilyRecords _records = Substitute.For<IFamilyRecords>();
    private readonly FakeTimeProvider _time = new(Now);

    public JoinFamilyTests()
    {
        _account = new Account(_sessions);
        _moves.ActiveHouseholdAsync(Arg.Any<CancellationToken>()).Returns(Home);
        _households.ListHouseholdsAsync(Arg.Any<CancellationToken>()).Returns([Settings(Home), Settings(Joined)]);
        _state.GetConsentAsync(Me, Arg.Any<CancellationToken>()).Returns(ConsentState.Given);
    }

    public void Dispose() => _account.Dispose();

    private JoinFamily Join()
    {
        var families = new Families(_store, _accounts, _api, _sessions, _households, _moves, _state, _records,
            new ProvisionalSettings(Substitute.For<IVersionClock>(), Substitute.For<IDeviceLocale>(), _time));
        var bring = new BringFamilyEntriesOver(_sessions, _account, _moves, _records, _store, _api, Substitute.For<IVersionClock>(), families);
        return new JoinFamily(_store, _api, _sessions, _account, new ConsentGate(_state, _accounts, Substitute.For<IConsentApi>()), _moves,
            _households, _syncStore, _sync, families, bring, _time);
    }

    private Task SignInAsync(bool joinPending = false, HouseholdId? moveFrom = null) => _account.SignedInAsync(
        new StoredSession("access", Now.AddMinutes(15), "refresh", Profile, Home, joinPending, PendingMoveFrom: moveFrom), CancellationToken.None);

    private void Pending(DateTimeOffset? at = null) =>
        _store.GetPendingInvitationAsync(Arg.Any<CancellationToken>()).Returns(new PendingInvitation(Code, at ?? Now));

    [Theory]
    [InlineData("abcd-efgh")]
    [InlineData(" ABCD EFGH ")]
    [InlineData("https://api.coo.lechdigital.nl/i/ABCDEFGH")]
    [InlineData("https://api.coo.lechdigital.nl/i/abcd-efgh")]
    public async Task ReceiveAsync_PastedLink_StoresTheCode(string text)
    {
        // Act
        var received = await Join().ReceiveAsync(text, CancellationToken.None);

        // Assert
        received.Should().BeTrue();
        await _store.Received(1).SetPendingInvitationAsync(new PendingInvitation(Code, Now), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("ABCD-EFG")]
    [InlineData("https://example.com/i/ABCDEFGH")]
    [InlineData("Join my family on Coo")]
    [InlineData(null)]
    public async Task ReceiveAsync_Garbage_StoresNothing(string? text)
    {
        // Act
        var received = await Join().ReceiveAsync(text, CancellationToken.None);

        // Assert
        received.Should().BeFalse();
        await _store.DidNotReceiveWithAnyArgs().SetPendingInvitationAsync(default, default);
        await _api.DidNotReceiveWithAnyArgs().PreviewAsync(default!, default);
    }

    [Fact]
    public async Task StageAsync_PendingOlderThanSeventyTwoHours_DropsIt()
    {
        // Arrange
        Pending(Now.AddHours(-72));

        // Act
        var stage = await Join().StageAsync(CancellationToken.None);

        // Assert
        stage.Should().Be(JoinStage.None);
        await _store.Received(1).SetPendingInvitationAsync(null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StageAsync_JoiningMarkerStored_ReopensAtTheChoice()
    {
        // Arrange: Review Focus 2, the app was closed between Join and the choice.
        _store.GetJoiningAsync(Arg.Any<CancellationToken>()).Returns(new JoiningMarker(Joined, Home, "Andrzej"));

        // Act
        var stage = await Join().StageAsync(CancellationToken.None);

        // Assert
        stage.Should().Be(JoinStage.Choice);
    }

    [Fact]
    public async Task PreviewAsync_AlreadyAMember_ClearsThePendingInvitationAndShowsTheFamily()
    {
        // Arrange
        await SignInAsync();
        Pending();
        _api.PreviewAsync(Code, Arg.Any<CancellationToken>()).Returns(new InvitationPreview(Joined, "Andrzej", ["Maja"], Role.Parent, Now.AddHours(72), true));

        // Act
        var preview = await Join().PreviewAsync(CancellationToken.None);

        // Assert
        preview.Status.Should().Be(PreviewStatus.AlreadyMember);
        await _store.Received(1).SetPendingInvitationAsync(null, Arg.Any<CancellationToken>());
        await _moves.Received(1).ActivateAsync(Joined, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(HouseholdErrors.InvitationInvalid, PreviewStatus.Invalid, true)]
    [InlineData(HouseholdErrors.RateLimited, PreviewStatus.RateLimited, false)]
    public async Task PreviewAsync_Refused_MapsTheCode(string code, PreviewStatus expected, bool cleared)
    {
        // Arrange
        await SignInAsync();
        Pending();
        _api.PreviewAsync(Code, Arg.Any<CancellationToken>()).ThrowsAsync(new HouseholdRefusedException(code));

        // Act
        var preview = await Join().PreviewAsync(CancellationToken.None);

        // Assert
        preview.Status.Should().Be(expected);
        await _store.Received(cleared ? 1 : 0).SetPendingInvitationAsync(null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AcceptAsync_Joined_RecordsTheSourceRefreshesAndPullsTheFamily()
    {
        // Arrange
        await SignInAsync();
        Pending();
        _api.AcceptAsync(Code, Arg.Any<CancellationToken>()).Returns(new AcceptedInvitation(Joined, Role.Parent, AcceptOutcome.Joined));

        // Act
        var accepted = await Join().AcceptAsync("Andrzej", CancellationToken.None);

        // Assert
        accepted.Status.Should().Be(AcceptStatus.Joined);
        Received.InOrder(() =>
        {
            _store.SetPendingInvitationAsync(null, Arg.Any<CancellationToken>());
            _store.SetJoiningAsync(new JoiningMarker(Joined, Home, "Andrzej"), Arg.Any<CancellationToken>());
            _sync.SyncNowAsync(Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task AcceptAsync_NoAnswer_KeepsThePendingInvitation()
    {
        // Arrange: spec §5.2, "Try again".
        await SignInAsync();
        Pending();
        _api.AcceptAsync(Code, Arg.Any<CancellationToken>()).ThrowsAsync(new HouseholdUnavailableException("offline"));

        // Act
        var accepted = await Join().AcceptAsync("Andrzej", CancellationToken.None);

        // Assert
        accepted.Status.Should().Be(AcceptStatus.Unavailable);
        await _store.DidNotReceiveWithAnyArgs().SetPendingInvitationAsync(default, default);
        await _store.DidNotReceiveWithAnyArgs().SetJoiningAsync(default, default);
    }

    [Fact]
    public async Task AcceptAsync_ServerSaysConsentRequired_ForgetsTheCachedConsentAndAsksForIt()
    {
        // Arrange
        await SignInAsync();
        Pending();
        _api.AcceptAsync(Code, Arg.Any<CancellationToken>()).ThrowsAsync(new HouseholdRefusedException(SyncErrors.ConsentRequired));

        // Act
        var accepted = await Join().AcceptAsync("Andrzej", CancellationToken.None);

        // Assert
        accepted.Status.Should().Be(AcceptStatus.NeedsConsent);
        await _state.Received(1).SetConsentAsync(Me, ConsentState.Unknown, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DecideAsync_SourceIsTheJoinedFamily_AsksNothingAndFinishes()
    {
        // Arrange: ruling 2, the Pixel held X from its first account.
        await SignInAsync(moveFrom: Local);
        _store.GetJoiningAsync(Arg.Any<CancellationToken>()).Returns(new JoiningMarker(Joined, Joined, "Andrzej"));
        _syncStore.HasPulledAsync(Joined, Arg.Any<CancellationToken>()).Returns(true);

        // Act
        var choice = await Join().DecideAsync(CancellationToken.None);

        // Assert
        choice.Status.Should().Be(ChoiceStatus.NothingAsked);
        await _store.Received(1).SetJoiningAsync(null, Arg.Any<CancellationToken>());
        _sessions.Stored!.PendingMoveFrom.Should().BeNull("ruling 4: the invitation's choice replaced the created move");
        _sync.Received(1).NotifyDataChanged();
    }

    [Fact]
    public async Task DecideAsync_SourceOfAnotherAccount_AsksNothing()
    {
        // Arrange: ruling 3, synced but not one of the account's families.
        await SignInAsync();
        _store.GetJoiningAsync(Arg.Any<CancellationToken>()).Returns(new JoiningMarker(Joined, Local, "Andrzej"));
        _syncStore.HasPulledAsync(Joined, Arg.Any<CancellationToken>()).Returns(true);
        _moves.HasEverSyncedAsync(Local, Arg.Any<CancellationToken>()).Returns(true);

        // Act
        var choice = await Join().DecideAsync(CancellationToken.None);

        // Assert
        choice.Status.Should().Be(ChoiceStatus.NothingAsked);
        await _moves.Received(1).ActivateAsync(Joined, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DecideAsync_SourceWithEntries_AsksAndSaysWhetherItCopies()
    {
        // Arrange
        await SignInAsync();
        _store.GetJoiningAsync(Arg.Any<CancellationToken>()).Returns(new JoiningMarker(Joined, Home, "Andrzej"));
        _syncStore.HasPulledAsync(Joined, Arg.Any<CancellationToken>()).Returns(true);
        _moves.HasEverSyncedAsync(Home, Arg.Any<CancellationToken>()).Returns(true);
        _store.ListAsync(Me, Arg.Any<CancellationToken>()).Returns([new FamilyMembership(Home, Role.Owner, Now, "Kasia", 1)]);
        _moves.SummaryAsync(Home, Arg.Any<CancellationToken>()).Returns(new LocalSummary(42, [new LocalBaby(new BabyId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000b1")), "Maja")]));
        _moves.SummaryAsync(Joined, Arg.Any<CancellationToken>()).Returns(new LocalSummary(3, []));

        // Act
        var choice = await Join().DecideAsync(CancellationToken.None);

        // Assert
        choice.Status.Should().Be(ChoiceStatus.Ask);
        choice.Copies.Should().BeTrue();
        choice.Source.Should().Be(Home);
        choice.Offer!.Local.EntryCount.Should().Be(42);
        await _store.DidNotReceiveWithAnyArgs().SetJoiningAsync(default, default);
    }

    [Fact]
    public async Task DecideAsync_NoEntriesButABabyAndTheTargetHasNone_AsksToBringTheBaby()
    {
        // Arrange: Spec 011's M7 rule.
        await SignInAsync();
        _store.GetJoiningAsync(Arg.Any<CancellationToken>()).Returns(new JoiningMarker(Joined, Home, "Andrzej"));
        _syncStore.HasPulledAsync(Joined, Arg.Any<CancellationToken>()).Returns(true);
        _moves.HasEverSyncedAsync(Home, Arg.Any<CancellationToken>()).Returns(false);
        _moves.SummaryAsync(Home, Arg.Any<CancellationToken>()).Returns(new LocalSummary(0, [new LocalBaby(new BabyId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000b1")), "Maja")]));
        _moves.SummaryAsync(Joined, Arg.Any<CancellationToken>()).Returns(new LocalSummary(0, []));

        // Act
        var choice = await Join().DecideAsync(CancellationToken.None);

        // Assert
        choice.Status.Should().Be(ChoiceStatus.Ask);
        choice.Copies.Should().BeFalse();
    }

    [Fact]
    public async Task DecideAsync_JoinedFamilyNotPulledYet_IsNotReadyAndPullsAgain()
    {
        // Arrange
        await SignInAsync();
        _store.GetJoiningAsync(Arg.Any<CancellationToken>()).Returns(new JoiningMarker(Joined, Home, "Andrzej"));
        _syncStore.HasPulledAsync(Joined, Arg.Any<CancellationToken>()).Returns(false);

        // Act
        var choice = await Join().DecideAsync(CancellationToken.None);

        // Assert
        choice.Status.Should().Be(ChoiceStatus.NotReady);
        await _sync.Received(1).SyncNowAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task KeepSeparateAsync_JoinPendingSession_CompletesTheJoinWithoutABringOver()
    {
        // Arrange: §5.3, the invitation's choice replaces Spec 011's join-pending choice.
        await SignInAsync(joinPending: true);
        _store.GetJoiningAsync(Arg.Any<CancellationToken>()).Returns(new JoiningMarker(Joined, Local, "Andrzej"));

        // Act
        await Join().KeepSeparateAsync(CancellationToken.None);

        // Assert
        _account.State.Should().BeOfType<SignedIn>();
        await _moves.Received(1).ActivateAsync(Joined, Arg.Any<CancellationToken>());
        await _moves.DidNotReceiveWithAnyArgs().BringOverAsync(default!, default, default);
        _sync.Received(1).RequestSync();
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Coo.Mobile.Application.Tests --filter "FullyQualifiedName~JoinFamilyTests|FullyQualifiedName~BringFamilyEntriesOverTests"`
Expected: FAIL to compile (`JoinFamily` not found).

- [ ] **Step 3: Write the implementation**

`src/Mobile/Coo.Mobile.Application/Households/BringFamilyEntriesOver.cs`:

```csharp
using Coo.Domain.Common;
using Coo.Domain.Households;
using Coo.Mobile.Application.Auth;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Application.Sync;

namespace Coo.Mobile.Application.Households;

public enum BringPath
{
    /// <summary>A never-synced source was re-keyed into the target (§6.1).</summary>
    Moved,

    /// <summary>A synced source's records were copied as new records (§6.2).</summary>
    Copied,

    /// <summary>This pair was copied before: nothing was written.</summary>
    AlreadyCopied,
}

/// <summary>What the phone asks about the old family after a copy (§6.2's table).</summary>
public enum OldFamilyQuestion
{
    None,
    DeleteOld,
    LeaveOld,
}

public sealed record BringOutcome(BringPath Path, int Copied, int Skipped, OldFamilyQuestion Question);

/// <summary>
/// "Bring my entries over" into a chosen family (Spec 011b §6): the phone chooses the path, never the person. A source that
/// has never synced moves (one transaction, Spec 011 §4's bring-over); one that synced is copied (ruling 8's stamps) and is
/// never re-keyed (R5). A source of another account is never offered (callers check <see cref="FamilyOnPhone.MayBeBroughtOver"/>).
/// </summary>
public sealed class BringFamilyEntriesOver(
    ISessionStore sessions, Account account, IHouseholdMoves moves, IFamilyRecords records, IFamilyStore families, IHouseholdApi api,
    IVersionClock clock, Families list)
{
    public async Task<BringOverChoice> PrepareAsync(HouseholdId source, HouseholdId target, CancellationToken cancellationToken) =>
        new(await moves.SummaryAsync(source, cancellationToken), (await moves.SummaryAsync(target, cancellationToken)).Babies);

    /// <summary>A target that is not one of the target family's babies counts as "Add as a new baby". A failing write throws (rolled back).</summary>
    public async Task<BringOutcome> ExecuteAsync(
        HouseholdId source, HouseholdId target, IReadOnlyDictionary<BabyId, BabyId?> mapping, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        var generation = account.Generation;
        var session = await sessions.GetAsync(cancellationToken) ?? throw new InvalidOperationException("Signed out: nothing can be brought over.");
        var user = session.Profile.UserId;
        var targetBabies = (await moves.SummaryAsync(target, cancellationToken)).Babies.Select(b => b.Id).ToHashSet();
        var babies = (await moves.SummaryAsync(source, cancellationToken)).Babies.ToDictionary(
            b => b.Id,
            b => mapping.TryGetValue(b.Id, out var chosen) && chosen is { } t && targetBabies.Contains(t) ? t : (BabyId?)null);
        var plan = new BringOverPlan(source, target, babies);
        if (account.Generation != generation)
            throw new InvalidOperationException("The account changed: nothing was brought over.");

        if (!await moves.HasEverSyncedAsync(source, cancellationToken) && await moves.BringOverAsync(plan, user, cancellationToken))
        {
            await LeaveEmptyAdoptedAsync(source, cancellationToken);
            return new BringOutcome(BringPath.Moved, 0, 0, OldFamilyQuestion.None);
        }

        if (await families.WasCopiedAsync(source, target, cancellationToken))
            return new BringOutcome(BringPath.AlreadyCopied, 0, 0, OldFamilyQuestion.None);

        // Ruling 8: the clock moves past every stamp the copy may use before the transaction, so a crash after it commits never
        // leaves the clock behind a stored stamp.
        var count = await records.CountRecordsAsync(source, cancellationToken);
        var first = await clock.NextAsync(cancellationToken);
        await clock.ObserveAsync(first with { Counter = first.Counter + count }, cancellationToken);
        if (await records.CopyAsync(plan, user, first, cancellationToken) is not { } copied)
            return new BringOutcome(BringPath.AlreadyCopied, 0, 0, OldFamilyQuestion.None);

        var membership = (await families.ListAsync(user, cancellationToken) ?? []).FirstOrDefault(m => m.Household == source);
        var question = membership switch
        {
            { Role: Role.Owner, MemberCount: 1 } => OldFamilyQuestion.DeleteOld,
            { Role: Role.Parent or Role.Caregiver } => OldFamilyQuestion.LeaveOld,
            _ => OldFamilyQuestion.None,
        };
        return new BringOutcome(BringPath.Copied, copied.Copied, copied.Skipped, question);
    }

    /// <summary>
    /// §6.1: her account's family that she alone owns, once empty, is left with <c>confirm: false</c>: the server deletes it only
    /// when it holds no record. Refused or offline, it simply stays as a separate family; nothing is discarded, nothing asked.
    /// </summary>
    public async Task LeaveEmptyAdoptedAsync(HouseholdId source, CancellationToken cancellationToken)
    {
        if (await sessions.GetAsync(cancellationToken) is not { } session)
            return;
        var membership = (await families.ListAsync(session.Profile.UserId, cancellationToken) ?? []).FirstOrDefault(m => m.Household == source);
        if (membership is not { Role: Role.Owner, MemberCount: 1 })
            return;
        var left = await moves.SummaryAsync(source, cancellationToken);
        if (left.EntryCount > 0 || left.Babies.Count > 0)
            return;
        try
        {
            await api.LeaveAsync(source, confirm: false, cancellationToken);
        }
        catch (Exception e) when (e is HouseholdRefusedException or HouseholdUnavailableException)
        {
            return;
        }

        await list.RemoveFromPhoneAsync(source, cancellationToken);
        await list.RefreshAsync(cancellationToken);
    }
}
```

`src/Mobile/Coo.Mobile.Application/Households/JoinFamily.cs`:

```csharp
using Coo.Contracts.Households;
using Coo.Contracts.Sync;
using Coo.Domain.Common;
using Coo.Domain.Households;
using Coo.Mobile.Application.Auth;
using Coo.Mobile.Application.Sync;

namespace Coo.Mobile.Application.Households;

/// <summary>Where the join flow opens (spec §5.1): the invitation, or the choice after an accept the app was closed during.</summary>
public enum JoinStage
{
    None,
    Invitation,
    Choice,
}

public enum PreviewStatus
{
    Ready,
    AlreadyMember,
    Invalid,
    RateLimited,
    Unavailable,
    NothingPending,
}

public sealed record JoinPreview(PreviewStatus Status, InvitationPreview? Invitation = null);

public enum AcceptStatus
{
    Joined,
    AlreadyMember,
    NeedsConsent,
    Invalid,
    RateLimited,
    Unavailable,
    NothingPending,
}

public sealed record JoinAccept(AcceptStatus Status);

public enum ChoiceStatus
{
    /// <summary>The joined family's log is not on the phone yet: "Couldn't refresh…", Try again.</summary>
    NotReady,

    NothingAsked,

    Ask,
}

/// <param name="Copies">The source has synced, so bringing it over copies (§6.2) and is followed by the old-family question.</param>
/// <param name="Source">The family shown before joining, whose entries are offered (and which the old-family question is about).</param>
public sealed record JoinChoice(ChoiceStatus Status, string InviterName, BringOverChoice? Offer = null, bool Copies = false, HouseholdId Source = default);

/// <summary>
/// Joining a family (Spec 011b §4–§6). The code becomes the pending invitation; preview, consent, accept; the joined family is
/// pulled; then §6 decides whether to ask anything. "Not now" anywhere clears the pending invitation and changes nothing on
/// the server. The joining marker (§5.3) is kept until the choice is made, so a closed app reopens at the choice.
/// </summary>
public sealed class JoinFamily(
    IFamilyStore families, IHouseholdApi api, ISessionStore sessions, Account account, ConsentGate consent, IHouseholdMoves moves,
    IHouseholdStore households, ISyncStore store, ISyncControl sync, Families list, BringFamilyEntriesOver bring, TimeProvider time)
{
    public static readonly TimeSpan PendingLifetime = TimeSpan.FromHours(72);

    /// <summary>A typed code, a pasted link, or an App Link's URL. False, with nothing stored and no request, when it holds no code.</summary>
    public async Task<bool> ReceiveAsync(string? text, CancellationToken cancellationToken)
    {
        var code = InvitationCode.Parse(CodeIn(text));
        if (code.IsFailure)
            return false;
        await families.SetPendingInvitationAsync(new PendingInvitation(code.Value, time.GetUtcNow()), cancellationToken);
        return true;
    }

    /// <summary>The flow opens with its sign-in step when signed out (spec §5.1 step 1).</summary>
    public async Task<bool> NeedsSignInAsync(CancellationToken cancellationToken) => await sessions.GetAsync(cancellationToken) is null;

    public async Task<JoinStage> StageAsync(CancellationToken cancellationToken)
    {
        if (await families.GetJoiningAsync(cancellationToken) is not null)
            return JoinStage.Choice;
        if (await families.GetPendingInvitationAsync(cancellationToken) is not { } pending)
            return JoinStage.None;
        if (time.GetUtcNow() - pending.ArrivedAt < PendingLifetime)
            return JoinStage.Invitation;
        await families.SetPendingInvitationAsync(null, cancellationToken);
        return JoinStage.None;
    }

    public async Task<JoinPreview> PreviewAsync(CancellationToken cancellationToken)
    {
        if (await families.GetPendingInvitationAsync(cancellationToken) is not { } pending)
            return new JoinPreview(PreviewStatus.NothingPending);
        try
        {
            var preview = await api.PreviewAsync(pending.Code, cancellationToken);
            if (!preview.AlreadyMember)
                return new JoinPreview(PreviewStatus.Ready, preview);
            await families.SetPendingInvitationAsync(null, cancellationToken);
            await ShowAsync(preview.Household, cancellationToken);
            return new JoinPreview(PreviewStatus.AlreadyMember, preview);
        }
        catch (HouseholdRefusedException e) when (e.Code == HouseholdErrors.InvitationInvalid)
        {
            await families.SetPendingInvitationAsync(null, cancellationToken);
            return new JoinPreview(PreviewStatus.Invalid);
        }
        catch (HouseholdRefusedException e) when (e.Code == HouseholdErrors.RateLimited)
        {
            return new JoinPreview(PreviewStatus.RateLimited);
        }
        catch (Exception e) when (e is HouseholdUnavailableException or HouseholdRefusedException)
        {
            return new JoinPreview(PreviewStatus.Unavailable);
        }
    }

    /// <summary>The account's consent to the current wording (asking <c>/api/me</c> when the phone does not know).</summary>
    public async Task<bool> HasConsentAsync(CancellationToken cancellationToken) =>
        await sessions.GetAsync(cancellationToken) is { } session && await consent.CheckAsync(session.Profile.UserId, cancellationToken) == ConsentState.Given;

    public async Task<JoinAccept> AcceptAsync(string inviterName, CancellationToken cancellationToken)
    {
        if (await families.GetPendingInvitationAsync(cancellationToken) is not { } pending)
            return new JoinAccept(AcceptStatus.NothingPending);
        if (await sessions.GetAsync(cancellationToken) is not { } session)
            return new JoinAccept(AcceptStatus.Unavailable);

        // The source is the family shown before joining (§6); recorded before the answer, so a lost answer keeps it.
        var source = await moves.ActiveHouseholdAsync(cancellationToken);
        try
        {
            var accepted = await api.AcceptAsync(pending.Code, cancellationToken);
            await families.SetPendingInvitationAsync(null, cancellationToken);
            if (accepted.Outcome == AcceptOutcome.AlreadyMember)
            {
                await ShowAsync(accepted.Household, cancellationToken);
                return new JoinAccept(AcceptStatus.AlreadyMember);
            }

            await families.SetJoiningAsync(new JoiningMarker(accepted.Household, source, inviterName), cancellationToken);
            await list.RefreshAsync(cancellationToken);
            await sync.SyncNowAsync(cancellationToken);
            return new JoinAccept(AcceptStatus.Joined);
        }
        catch (HouseholdRefusedException e) when (e.Code == HouseholdErrors.InvitationInvalid)
        {
            await families.SetPendingInvitationAsync(null, cancellationToken);
            return new JoinAccept(AcceptStatus.Invalid);
        }
        catch (HouseholdRefusedException e) when (e.Code == HouseholdErrors.RateLimited)
        {
            return new JoinAccept(AcceptStatus.RateLimited);
        }
        catch (HouseholdRefusedException e) when (e.Code == SyncErrors.ConsentRequired)
        {
            await consent.ForgetAsync(session.Profile.UserId, cancellationToken);
            return new JoinAccept(AcceptStatus.NeedsConsent);
        }
        catch (Exception e) when (e is HouseholdUnavailableException or HouseholdRefusedException)
        {
            return new JoinAccept(AcceptStatus.Unavailable);
        }
    }

    /// <summary>§6: whether to ask, once the joined family's log is on the phone.</summary>
    public async Task<JoinChoice> DecideAsync(CancellationToken cancellationToken)
    {
        if (await families.GetJoiningAsync(cancellationToken) is not { } marker)
            return new JoinChoice(ChoiceStatus.NothingAsked, "");
        if (!await store.HasPulledAsync(marker.Joined, cancellationToken))
        {
            await sync.SyncNowAsync(cancellationToken);
            if (!await store.HasPulledAsync(marker.Joined, cancellationToken))
                return new JoinChoice(ChoiceStatus.NotReady, marker.InviterName);
        }

        var source = marker.Source;
        var synced = await moves.HasEverSyncedAsync(source, cancellationToken);
        var session = await sessions.GetAsync(cancellationToken);
        var accountFamily = session is not null
            && (await families.ListAsync(session.Profile.UserId, cancellationToken) ?? []).Any(m => m.Household == source);

        // Rulings 2 and 3: the joined family itself, or a family of another account, is never offered.
        if (source == marker.Joined || (synced && !accountFamily))
        {
            await FinishAsync(marker.Joined, cancellationToken);
            return new JoinChoice(ChoiceStatus.NothingAsked, marker.InviterName);
        }

        var choice = await bring.PrepareAsync(source, marker.Joined, cancellationToken);
        if (choice.Local.EntryCount == 0 && (choice.Local.Babies.Count == 0 || choice.Family.Count > 0))
        {
            // Nothing to bring; an empty, never-synced account family is still left (§6, §6.1).
            if (!synced)
                await bring.LeaveEmptyAdoptedAsync(source, cancellationToken);
            await FinishAsync(marker.Joined, cancellationToken);
            return new JoinChoice(ChoiceStatus.NothingAsked, marker.InviterName);
        }

        return new JoinChoice(ChoiceStatus.Ask, marker.InviterName, choice, Copies: synced, Source: source);
    }

    /// <summary>Bring my entries over into the joined family; the join ends only when it committed (a failure throws, nothing changed).</summary>
    public async Task<BringOutcome> BringAsync(IReadOnlyDictionary<BabyId, BabyId?> mapping, CancellationToken cancellationToken)
    {
        if (await families.GetJoiningAsync(cancellationToken) is not { } marker)
            return new BringOutcome(BringPath.AlreadyCopied, 0, 0, OldFamilyQuestion.None);
        var outcome = await bring.ExecuteAsync(marker.Source, marker.Joined, mapping, cancellationToken);
        await FinishAsync(marker.Joined, cancellationToken);
        return outcome;
    }

    /// <summary>§6.3: both families stay; the joined one is shown.</summary>
    public async Task KeepSeparateAsync(CancellationToken cancellationToken)
    {
        if (await families.GetJoiningAsync(cancellationToken) is { } marker)
            await FinishAsync(marker.Joined, cancellationToken);
    }

    /// <summary>"Not now" (§5.1): the pending invitation goes; nothing changes on the server.</summary>
    public Task DeclineAsync(CancellationToken cancellationToken) => families.SetPendingInvitationAsync(null, cancellationToken);

    /// <summary>
    /// The choice is made: the joined family shows; the joining marker goes; the invitation's choice replaces a
    /// <c>created</c> move (ruling 4) and Spec 011's join-pending choice (§5.3); a cycle runs.
    /// </summary>
    private async Task FinishAsync(HouseholdId joined, CancellationToken cancellationToken)
    {
        var generation = account.Generation;
        await ShowAsync(joined, cancellationToken);
        await families.SetJoiningAsync(null, cancellationToken);
        await account.TryClearPendingMoveAsync(generation, cancellationToken);
        if (account.State is JoinPending)
            await account.TryCompleteJoinAsync(generation, cancellationToken);
        sync.RequestSync();
    }

    /// <summary>The family is shown, if its log is on the phone (it has settings), and the pages are told.</summary>
    private async Task ShowAsync(HouseholdId household, CancellationToken cancellationToken)
    {
        if ((await households.ListHouseholdsAsync(cancellationToken)).Any(h => h.Id == household && !h.IsDeleted))
            await moves.ActivateAsync(household, cancellationToken);
        sync.NotifyDataChanged();
    }

    /// <summary>The code in a typed text or in an invitation link on our host; anything else is returned as is, or null for a foreign link.</summary>
    private static string? CodeIn(string? text)
    {
        if (text is null)
            return null;
        var trimmed = text.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
            return trimmed;
        return uri.Host.Equals(InvitationLinks.Host, StringComparison.OrdinalIgnoreCase)
            && uri.AbsolutePath.StartsWith(InvitationLinks.PathPrefix, StringComparison.OrdinalIgnoreCase)
            ? uri.AbsolutePath[InvitationLinks.PathPrefix.Length..]
            : null;
    }
}
```

Register in `AddMobileApplication`:

```csharp
        services.AddTransient<BringFamilyEntriesOver>();
        services.AddTransient<JoinFamily>();
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Coo.Mobile.Application.Tests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Mobile/Coo.Mobile.Application tests/Coo.Mobile.Application.Tests
git commit -m "Mobile.Application: join a family; bring entries over by move or copy; the old family's question (Spec 011b §4–§6)"
```

---

### Task 17: Members, inviting, withdrawing, removing and leaving on the phone

**Files:**
- Create: `src/Mobile/Coo.Mobile.Application/Households/MemberUseCases.cs`
- Modify: `src/Mobile/Coo.Mobile.Application/ServiceCollectionExtensions.cs`
- Test: `tests/Coo.Mobile.Application.Tests/MemberUseCaseTests.cs`

**Interfaces:**
- Consumes: `IHouseholdApi`, `Families`, `ISyncControl`.
- Produces:
  - `FamilyAction(bool Succeeded, string? Code = null)` with `static FamilyAction Done`, `static FamilyAction Unavailable`
    (`Code` null), `static FamilyAction Refused(string code)`.
  - `LoadFamilyMembers(IHouseholdApi api)` (singleton; keeps the last list per family): `ExecuteAsync(HouseholdId, CancellationToken) → MembersLoad`,
    `MembersLoad(FamilyMembers? Members, bool IsFresh)`.
  - `InviteParent(IHouseholdApi api)`: `ExecuteAsync(HouseholdId, CancellationToken) → InviteResult`, `InviteResult(CreatedInvitation? Invitation, FamilyAction Action)`.
  - `WithdrawFamilyInvitation(IHouseholdApi api)`, `RemoveFamilyMember(IHouseholdApi api, Families families)`:
    `ExecuteAsync(…) → FamilyAction`.
  - `LeaveFamily(IHouseholdApi api, Families families, ISyncControl sync)`: `ExecuteAsync(HouseholdId household, bool confirm, CancellationToken) → FamilyAction`.

- [ ] **Step 1: Write the failing tests**

`tests/Coo.Mobile.Application.Tests/MemberUseCaseTests.cs`:

```csharp
using Coo.Contracts.Households;
using Coo.Domain.Common;
using Coo.Domain.Households;
using Coo.Mobile.Application.Auth;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Application.Households;
using Coo.Mobile.Application.Sync;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using static Coo.Mobile.Application.Tests.FamiliesTests;

namespace Coo.Mobile.Application.Tests;

/// <summary>Spec 011b §2 (the Family page) and §8 on the phone: nothing changes locally until the server said yes.</summary>
public sealed class MemberUseCaseTests : IDisposable
{
    private readonly MemorySessions _sessions = new();
    private readonly Account _account;
    private readonly IHouseholdApi _api = Substitute.For<IHouseholdApi>();
    private readonly IFamilyStore _store = Substitute.For<IFamilyStore>();
    private readonly IHouseholdStore _households = Substitute.For<IHouseholdStore>();
    private readonly IHouseholdMoves _moves = Substitute.For<IHouseholdMoves>();
    private readonly IFamilyRecords _records = Substitute.For<IFamilyRecords>();
    private readonly ISyncControl _sync = Substitute.For<ISyncControl>();

    public MemberUseCaseTests()
    {
        _account = new Account(_sessions);
        _households.ListHouseholdsAsync(Arg.Any<CancellationToken>()).Returns([Settings(Joined), Settings(Home)]);
        _moves.ActiveHouseholdAsync(Arg.Any<CancellationToken>()).Returns(Joined);
    }

    public void Dispose() => _account.Dispose();

    private Families Families() => new(_store, Substitute.For<IAccountApi>(), _api, _sessions, _households, _moves, Substitute.For<ISyncState>(), _records,
        new ProvisionalSettings(Substitute.For<IVersionClock>(), Substitute.For<IDeviceLocale>(), new FakeTimeProvider(Now)));

    [Fact]
    public async Task ExecuteAsync_LoadOfflineAfterAFreshLoad_ReturnsTheLastListAsNotFresh()
    {
        // Arrange: spec §2, offline shows the last list fetched (ruling 24).
        var load = new LoadFamilyMembers(_api);
        var members = new FamilyMembers([new FamilyMember(Me, "Kasia", "k@example.com", Role.Parent, Now, true)], []);
        _api.ListMembersAsync(Joined, Arg.Any<CancellationToken>()).Returns(members);
        await load.ExecuteAsync(Joined, CancellationToken.None);
        _api.ListMembersAsync(Joined, Arg.Any<CancellationToken>()).ThrowsAsync(new HouseholdUnavailableException("offline"));

        // Act
        var again = await load.ExecuteAsync(Joined, CancellationToken.None);

        // Assert
        again.Should().Be(new MembersLoad(members, IsFresh: false));
    }

    [Fact]
    public async Task ExecuteAsync_InviteRefusedTooMany_ReportsTheCode()
    {
        // Arrange
        _api.CreateInvitationAsync(Joined, Arg.Any<CancellationToken>()).ThrowsAsync(new HouseholdRefusedException(HouseholdErrors.InvitationTooMany));

        // Act
        var result = await new InviteParent(_api).ExecuteAsync(Joined, CancellationToken.None);

        // Assert
        result.Invitation.Should().BeNull();
        result.Action.Should().Be(FamilyAction.Refused(HouseholdErrors.InvitationTooMany));
    }

    [Fact]
    public async Task ExecuteAsync_LeaveAnswered_RemovesTheFamilyFromThePhoneAndTellsThePages()
    {
        // Arrange
        _api.LeaveAsync(Joined, true, Arg.Any<CancellationToken>()).Returns(LeaveOutcome.Left);

        // Act
        var result = await new LeaveFamily(_api, Families(), _sync).ExecuteAsync(Joined, confirm: true, CancellationToken.None);

        // Assert
        result.Should().Be(FamilyAction.Done);
        await _moves.Received(1).DeleteHouseholdAsync(Joined, Arg.Any<CancellationToken>());
        _sync.Received(1).NotifyDataChanged();
        _sync.Received(1).RequestSync();
    }

    [Fact]
    public async Task ExecuteAsync_LeaveUnanswered_DeletesNothingOnThePhone()
    {
        // Arrange: Review Focus 4.
        _api.LeaveAsync(Joined, true, Arg.Any<CancellationToken>()).ThrowsAsync(new HouseholdUnavailableException("offline"));

        // Act
        var result = await new LeaveFamily(_api, Families(), _sync).ExecuteAsync(Joined, confirm: true, CancellationToken.None);

        // Assert
        result.Should().Be(FamilyAction.Unavailable);
        await _moves.DidNotReceiveWithAnyArgs().DeleteHouseholdAsync(default, default);
        await _records.DidNotReceiveWithAnyArgs().DeleteAndReplaceAsync(default, default!, default);
    }

    [Fact]
    public async Task ExecuteAsync_LeaveRefusedLastFamily_KeepsEverything()
    {
        // Arrange
        _api.LeaveAsync(Joined, false, Arg.Any<CancellationToken>()).ThrowsAsync(new HouseholdRefusedException(HouseholdErrors.LastFamily));

        // Act
        var result = await new LeaveFamily(_api, Families(), _sync).ExecuteAsync(Joined, confirm: false, CancellationToken.None);

        // Assert
        result.Should().Be(FamilyAction.Refused(HouseholdErrors.LastFamily));
        await _moves.DidNotReceiveWithAnyArgs().DeleteHouseholdAsync(default, default);
    }

    [Fact]
    public async Task ExecuteAsync_RemoveAnswered_RefreshesTheFamilyList()
    {
        // Arrange
        var kasia = new UserId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000c5"));

        // Act
        var result = await new RemoveFamilyMember(_api, Families()).ExecuteAsync(Joined, kasia, CancellationToken.None);

        // Assert
        result.Should().Be(FamilyAction.Done);
        await _api.Received(1).RemoveMemberAsync(Joined, kasia, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_WithdrawOffline_IsUnavailable()
    {
        // Arrange
        var id = Guid.Parse("7a1c0000-0000-7000-8000-0000000000e1");
        _api.WithdrawInvitationAsync(Joined, id, Arg.Any<CancellationToken>()).ThrowsAsync(new HouseholdUnavailableException("offline"));

        // Act
        var result = await new WithdrawFamilyInvitation(_api).ExecuteAsync(Joined, id, CancellationToken.None);

        // Assert
        result.Should().Be(FamilyAction.Unavailable);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Coo.Mobile.Application.Tests --filter "FullyQualifiedName~MemberUseCaseTests"`
Expected: FAIL to compile.

- [ ] **Step 3: Write the implementation**

`src/Mobile/Coo.Mobile.Application/Households/MemberUseCases.cs`:

```csharp
using System.Collections.Concurrent;
using Coo.Domain.Common;
using Coo.Mobile.Application.Sync;

namespace Coo.Mobile.Application.Households;

/// <summary>What a Family page action did: done, the server's code, or no answer (<see cref="Code"/> null).</summary>
public sealed record FamilyAction(bool Succeeded, string? Code = null)
{
    public static FamilyAction Done { get; } = new(true);

    public static FamilyAction Unavailable { get; } = new(false);

    public static FamilyAction Refused(string code) => new(false, code);

    internal static FamilyAction From(Exception e) => e is HouseholdRefusedException refused ? Refused(refused.Code) : Unavailable;
}

public sealed record MembersLoad(FamilyMembers? Members, bool IsFresh);

/// <summary>The Family page's list (spec §2). Offline it returns the last list fetched in this app run (ruling 24). One per app.</summary>
public sealed class LoadFamilyMembers(IHouseholdApi api)
{
    private readonly ConcurrentDictionary<HouseholdId, FamilyMembers> _last = new();

    public async Task<MembersLoad> ExecuteAsync(HouseholdId household, CancellationToken cancellationToken)
    {
        try
        {
            var members = await api.ListMembersAsync(household, cancellationToken);
            _last[household] = members;
            return new MembersLoad(members, IsFresh: true);
        }
        catch (Exception e) when (e is HouseholdUnavailableException or HouseholdRefusedException)
        {
            return new MembersLoad(_last.GetValueOrDefault(household), IsFresh: false);
        }
    }
}

public sealed record InviteResult(CreatedInvitation? Invitation, FamilyAction Action);

/// <summary>Invite a parent (spec §3): the code exists only in this answer and on the share sheet.</summary>
public sealed class InviteParent(IHouseholdApi api)
{
    public async Task<InviteResult> ExecuteAsync(HouseholdId household, CancellationToken cancellationToken)
    {
        try
        {
            return new InviteResult(await api.CreateInvitationAsync(household, cancellationToken), FamilyAction.Done);
        }
        catch (Exception e) when (e is HouseholdUnavailableException or HouseholdRefusedException)
        {
            return new InviteResult(null, FamilyAction.From(e));
        }
    }
}

public sealed class WithdrawFamilyInvitation(IHouseholdApi api)
{
    public async Task<FamilyAction> ExecuteAsync(HouseholdId household, Guid invitationId, CancellationToken cancellationToken)
    {
        try
        {
            await api.WithdrawInvitationAsync(household, invitationId, cancellationToken);
            return FamilyAction.Done;
        }
        catch (Exception e) when (e is HouseholdUnavailableException or HouseholdRefusedException)
        {
            return FamilyAction.From(e);
        }
    }
}

public sealed class RemoveFamilyMember(IHouseholdApi api, Families families)
{
    public async Task<FamilyAction> ExecuteAsync(HouseholdId household, UserId member, CancellationToken cancellationToken)
    {
        try
        {
            await api.RemoveMemberAsync(household, member, cancellationToken);
        }
        catch (Exception e) when (e is HouseholdUnavailableException or HouseholdRefusedException)
        {
            return FamilyAction.From(e);
        }

        await families.RefreshAsync(cancellationToken);
        return FamilyAction.Done;
    }
}

/// <summary>
/// Leave this family, or delete it (spec §8.3; also §6.2's Leave and Delete old family): only after the server's 200 is the
/// family taken off the phone (no soft-lock dance: the person just confirmed). A lost answer changes nothing here; the retry
/// answers 200 with the same outcome.
/// </summary>
public sealed class LeaveFamily(IHouseholdApi api, Families families, ISyncControl sync)
{
    public async Task<FamilyAction> ExecuteAsync(HouseholdId household, bool confirm, CancellationToken cancellationToken)
    {
        try
        {
            await api.LeaveAsync(household, confirm, cancellationToken);
        }
        catch (Exception e) when (e is HouseholdUnavailableException or HouseholdRefusedException)
        {
            return FamilyAction.From(e);
        }

        await families.RemoveFromPhoneAsync(household, cancellationToken);
        await families.RefreshAsync(cancellationToken);
        sync.NotifyDataChanged();
        sync.RequestSync();
        return FamilyAction.Done;
    }
}
```

Register:

```csharp
        services.AddSingleton<LoadFamilyMembers>();
        services.AddTransient<InviteParent>();
        services.AddTransient<WithdrawFamilyInvitation>();
        services.AddTransient<RemoveFamilyMember>();
        services.AddTransient<LeaveFamily>();
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Coo.Mobile.Application.Tests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Mobile/Coo.Mobile.Application tests/Coo.Mobile.Application.Tests
git commit -m "Mobile.Application: the Family page's use cases; leaving changes the phone only after the server's yes (Spec 011b §2, §8)"
```

---
### Task 18: The copy, in seven languages (`eng/strings/strings.py`)

**Files:**
- Modify: `eng/strings/strings.py`, the generated `src/Mobile/Coo.Mobile.Presentation/Resources/AppResources*.resx`
- Test: `tests/Coo.Mobile.Presentation.Tests/LocalizationTests.cs`

**Interfaces:**
- Consumes: the spec's English copy (§2–§9), verbatim except rulings 10, 26 and 29.
- Produces: every key below, used by Tasks 19–24. The app says **family**: de *Familie*, fr *famille*, es *familia*,
  it *famiglia*, pl *rodzina*, nl *gezin*; never *household*, *Haushalt*, *foyer*, *ménage*, *hogar*, *nucleo*,
  *gospodarstwo*, *huishouden*. Menu paths name the existing labels (`Tab.Settings` and the new `Family.Title`,
  `Family.Join`). Polish uses the colon form for a name (spec §15.2 Q10). Copy addressed to the person is gender-neutral.

- [ ] **Step 1: Write the failing tests**

Append to `LocalizationTests`:

```csharp
    public static TheoryData<string, string> HouseholdWords => new()
    {
        { "en", "household" }, { "de", "haushalt" }, { "fr", "foyer|ménage" }, { "es", "hogar" }, { "it", "nucleo" },
        { "pl", "gospodarstw" }, { "nl", "huishoud" },
    };

    [Theory]
    [MemberData(nameof(HouseholdWords))]
    public void Values_EveryLanguage_SayFamilyNeverHousehold(string language, string forbidden)
    {
        // Arrange: Spec 011b §2 and R10.
        var set = Set(language);

        // Act
        var offending = set.Where(e => Regex.IsMatch(e.Value, forbidden, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)).Select(e => e.Key).ToList();

        // Assert
        offending.Should().BeEmpty();
    }

    [Theory]
    [InlineData("invitation.invalid")]
    [InlineData("invitation.role-unsupported")]
    [InlineData("invitation.too-many")]
    [InlineData("household.forbidden")]
    [InlineData("household.member-not-found")]
    [InlineData("household.owner-cannot-leave")]
    [InlineData("household.last-family")]
    [InlineData("household.confirmation-required")]
    [InlineData("household.id-taken")]
    [InlineData("household.already-has-family")]
    [InlineData("household.malformed")]
    [InlineData("auth.rate-limited")]
    [InlineData("sync.consent-required")]
    public void Error_EveryCodeThePhoneCanReceive_HasAString(string code)
    {
        // Arrange: ADR 0006 §3, spec §10.1.
        var generic = Strings.Get("Error.Generic");

        // Act
        var text = Strings.Error(code);

        // Assert
        text.Should().NotBe(generic, $"'{code}' needs its own message");
    }

    [Fact]
    public void Format_ShareText_PutsTheLinkCodeAndExpiryInPlace()
    {
        // Arrange
        var previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");

        try
        {
            // Act
            var text = Strings.Format("Invite.ShareText", "https://api.coo.lechdigital.nl/i/ABCDEFGH", "ABCD-EFGH", "Thu 9 Oct, 14:02");

            // Assert
            text.Should().Be(
                "Join my family on Coo, so we can keep our baby's log together: https://api.coo.lechdigital.nl/i/ABCDEFGH\n\n"
                + "If the link doesn't open Coo, go to Settings → Family → Join a family in Coo and enter the code ABCD-EFGH. "
                + "It can be used once, until Thu 9 Oct, 14:02.");
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Coo.Mobile.Presentation.Tests --filter "FullyQualifiedName~LocalizationTests"`
Expected: FAIL (`Error_EveryCodeThePhoneCanReceive_HasAString` and the share text: no such strings). The household test
passes already (and must keep passing).

- [ ] **Step 3: The strings**

Add to `S` in `eng/strings/strings.py`, after the `'BringOver.Failed'` row (each tuple is en, de, fr, es, it, pl, nl):

```python
'Family.Title': ('Family', 'Familie', 'Famille', 'Familia', 'Famiglia', 'Rodzina', 'Gezin'),
'Family.Showing': ('Showing: {0}', 'Angezeigt: {0}', 'Affichée : {0}', 'Mostrando: {0}', 'Visualizzata: {0}', 'Wyświetlana: {0}', 'Getoond: {0}'),
'Family.Yours': ('Your family', 'Deine Familie', 'Votre famille', 'Tu familia', 'La tua famiglia', 'Twoja rodzina', 'Je gezin'),
'Family.Of': ('Family of {0}', 'Familie von {0}', 'Famille de {0}', 'Familia de {0}', 'Famiglia di {0}', 'Rodzina: {0}', 'Gezin van {0}'),
'Family.NoBaby': ('No baby yet', 'Noch kein Baby', 'Pas encore de bébé', 'Aún no hay bebé', 'Ancora nessun bambino', 'Jeszcze bez dziecka', 'Nog geen baby'),
'Family.LocalOnly': ('On this phone only', 'Nur auf diesem Handy', 'Uniquement sur ce téléphone', 'Solo en este teléfono', 'Solo su questo telefono', 'Tylko na tym telefonie', 'Alleen op deze telefoon'),
'Family.Members': ('Members', 'Mitglieder', 'Membres', 'Miembros', 'Membri', 'Członkowie', 'Leden'),
'Family.OnlyHere': ("This family's log is only on this phone.", 'Die Einträge dieser Familie sind nur auf diesem Handy.', 'Le journal de cette famille est uniquement sur ce téléphone.', 'El registro de esta familia solo está en este teléfono.', 'Il registro di questa famiglia è solo su questo telefono.', 'Dziennik tej rodziny jest tylko na tym telefonie.', 'Het logboek van dit gezin staat alleen op deze telefoon.'),
'Family.Invite': ('Invite a parent', 'Elternteil einladen', 'Inviter un parent', 'Invitar a un progenitor', 'Invita un genitore', 'Zaproś rodzica', 'Een ouder uitnodigen'),
'Family.Join': ('Join a family', 'Einer Familie beitreten', 'Rejoindre une famille', 'Unirse a una familia', 'Unisciti a una famiglia', 'Dołącz do rodziny', 'Bij een gezin komen'),
'Family.BringOver': ('Bring entries over', 'Einträge übernehmen', 'Transférer des entrées', 'Traer registros', 'Porta le voci', 'Przenieś wpisy', 'Items overzetten'),
'Family.Switch': ('Show which family?', 'Welche Familie anzeigen?', 'Afficher quelle famille ?', '¿Qué familia mostrar?', 'Quale famiglia mostrare?', 'Którą rodzinę pokazać?', 'Welk gezin tonen?'),
'Family.RoleOwner': ('Parent · created the family', 'Elternteil · hat die Familie angelegt', 'Parent · a créé la famille', 'Progenitor · creó la familia', 'Genitore · ha creato la famiglia', 'Rodzic · twórca rodziny', 'Ouder · heeft het gezin aangemaakt'),
'Family.RoleParent': ('Parent', 'Elternteil', 'Parent', 'Progenitor', 'Genitore', 'Rodzic', 'Ouder'),
'Family.RoleCaregiver': ('Caregiver', 'Betreuungsperson', 'Personne de garde', 'Cuidador', 'Persona che accudisce', 'Opiekun', 'Verzorger'),
'Family.You': ('{0} (you)', '{0} (du)', '{0} (vous)', '{0} (tú)', '{0} (tu)', '{0} (ty)', '{0} (jij)'),
'Family.Remove': ('Remove', 'Entfernen', 'Retirer', 'Quitar', 'Rimuovi', 'Usuń', 'Verwijderen'),
'Family.RemoveConfirm': ("Remove {0} from the family? Their phones stop sharing this family's log, and it is removed from them. Entries they added stay.", 'Möchtest du {0} aus der Familie entfernen? Die Handys dieser Person teilen die Einträge dieser Familie dann nicht mehr, und sie werden dort entfernt. Einträge dieser Person bleiben.', 'Retirer {0} de la famille ? Ses téléphones ne partageront plus le journal de cette famille, qui en sera supprimé. Les entrées ajoutées par cette personne restent.', '¿Quitar a {0} de la familia? Sus teléfonos dejarán de compartir el registro de esta familia y se eliminará de ellos. Los registros que añadió se quedan.', 'Rimuovere {0} dalla famiglia? I suoi telefoni smetteranno di condividere il registro di questa famiglia, che verrà rimosso. Le voci che ha aggiunto restano.', 'Usunąć osobę {0} z rodziny? Jej telefony przestaną udostępniać dziennik tej rodziny i zostanie on z nich usunięty. Dodane przez nią wpisy zostaną.', '{0} uit het gezin verwijderen? Hun telefoons delen het logboek van dit gezin dan niet meer en het wordt ervan verwijderd. Items die ze hebben toegevoegd blijven.'),
'Family.Invitations': ('Open invitations', 'Offene Einladungen', 'Invitations en attente', 'Invitaciones abiertas', 'Inviti aperti', 'Otwarte zaproszenia', 'Openstaande uitnodigingen'),
'Family.InvitationLine': ('Invitation for a parent · until {0}', 'Einladung für ein Elternteil · bis {0}', "Invitation d'un parent · jusqu'au {0}", 'Invitación para un progenitor · hasta el {0}', 'Invito per un genitore · fino al {0}', 'Zaproszenie dla rodzica · do {0}', 'Uitnodiging voor een ouder · tot {0}'),
'Family.Withdraw': ('Withdraw', 'Zurückziehen', 'Retirer', 'Retirar', 'Ritira', 'Wycofaj', 'Intrekken'),
'Family.WithdrawConfirm': ('Withdraw this invitation? Its link and code stop working.', 'Diese Einladung zurückziehen? Link und Code funktionieren dann nicht mehr.', 'Retirer cette invitation ? Son lien et son code ne fonctionneront plus.', '¿Retirar esta invitación? Su enlace y su código dejarán de funcionar.', 'Ritirare questo invito? Il link e il codice smetteranno di funzionare.', 'Wycofać to zaproszenie? Link i kod przestaną działać.', 'Deze uitnodiging intrekken? De link en de code werken dan niet meer.'),
'Family.Leave': ('Leave this family', 'Diese Familie verlassen', 'Quitter cette famille', 'Salir de esta familia', 'Lascia questa famiglia', 'Opuść tę rodzinę', 'Dit gezin verlaten'),
'Family.LeaveShort': ('Leave', 'Verlassen', 'Quitter', 'Salir', 'Lascia', 'Opuść', 'Verlaten'),
'Family.LeaveConfirm': ('Leave this family? Its log is removed from this phone. Entries you added stay with the family.', 'Diese Familie verlassen? Ihre Einträge werden von diesem Handy entfernt. Deine Einträge bleiben bei der Familie.', 'Quitter cette famille ? Son journal sera supprimé de ce téléphone. Les entrées que vous avez ajoutées restent dans la famille.', '¿Salir de esta familia? Su registro se eliminará de este teléfono. Los registros que añadiste se quedan en la familia.', 'Lasciare questa famiglia? Il suo registro verrà rimosso da questo telefono. Le voci che hai aggiunto restano alla famiglia.', 'Opuścić tę rodzinę? Jej dziennik zostanie usunięty z tego telefonu. Dodane przez Ciebie wpisy zostaną w rodzinie.', 'Dit gezin verlaten? Het logboek wordt van deze telefoon verwijderd. Items die jij hebt toegevoegd blijven bij het gezin.'),
'Family.Delete': ('Delete this family', 'Diese Familie löschen', 'Supprimer cette famille', 'Eliminar esta familia', 'Elimina questa famiglia', 'Usuń tę rodzinę', 'Dit gezin verwijderen'),
'Family.DeleteShort': ('Delete', 'Löschen', 'Supprimer', 'Eliminar', 'Elimina', 'Usuń', 'Verwijderen'),
'Family.RefreshFailed': ("Couldn't refresh. Check your connection.", 'Aktualisieren hat nicht geklappt. Prüfe deine Verbindung.', "Impossible d'actualiser. Vérifiez votre connexion.", 'No se ha podido actualizar. Comprueba la conexión.', 'Aggiornamento non riuscito. Controlla la connessione.', 'Nie udało się odświeżyć. Sprawdź połączenie.', 'Vernieuwen is niet gelukt. Controleer je verbinding.'),
'Family.ActionFailed': ("That didn't work. Check your connection and try again.", 'Das hat nicht geklappt. Prüfe deine Verbindung und versuche es noch einmal.', "Cela n'a pas fonctionné. Vérifiez votre connexion et réessayez.", 'No ha funcionado. Comprueba la conexión y vuelve a intentarlo.', 'Non ha funzionato. Controlla la connessione e riprova.', 'Nie udało się. Sprawdź połączenie i spróbuj ponownie.', 'Dat is niet gelukt. Controleer je verbinding en probeer het opnieuw.'),
'Invite.ShareText': ("Join my family on Coo, so we can keep our baby's log together: {0}\n\nIf the link doesn't open Coo, go to Settings → Family → Join a family in Coo and enter the code {1}. It can be used once, until {2}.", 'Tritt meiner Familie bei Coo bei, damit wir die Einträge unseres Babys gemeinsam führen: {0}\n\nWenn der Link Coo nicht öffnet, geh in Coo zu Einstellungen → Familie → Einer Familie beitreten und gib den Code {1} ein. Er gilt einmal, bis {2}.', "Rejoins ma famille sur Coo pour que nous tenions ensemble le journal de notre bébé : {0}\n\nSi le lien n'ouvre pas Coo, va dans Réglages → Famille → Rejoindre une famille dans Coo et saisis le code {1}. Il peut servir une fois, jusqu'au {2}.", 'Únete a mi familia en Coo para que llevemos juntos el registro de nuestro bebé: {0}\n\nSi el enlace no abre Coo, ve a Ajustes → Familia → Unirse a una familia en Coo e introduce el código {1}. Se puede usar una vez, hasta el {2}.', 'Unisciti alla mia famiglia su Coo, così teniamo insieme il registro del nostro bambino: {0}\n\nSe il link non apre Coo, vai su Impostazioni → Famiglia → Unisciti a una famiglia in Coo e inserisci il codice {1}. Si può usare una volta, fino al {2}.', 'Dołącz do mojej rodziny w Coo, żebyśmy razem prowadzili dziennik naszego dziecka: {0}\n\nJeśli link nie otworzy Coo, przejdź w Coo do Ustawienia → Rodzina → Dołącz do rodziny i wpisz kod {1}. Można go użyć raz, do {2}.', 'Kom bij mijn gezin in Coo, dan houden we samen het logboek van onze baby bij: {0}\n\nAls de link Coo niet opent, ga in Coo naar Instellingen → Gezin → Bij een gezin komen en voer de code {1} in. Hij kan één keer worden gebruikt, tot {2}.'),
'Setup.HaveInvitation': ('Have an invitation? Enter a code', 'Hast du eine Einladung? Code eingeben', 'Vous avez une invitation ? Saisir un code', '¿Tienes una invitación? Introduce un código', 'Hai un invito? Inserisci un codice', 'Masz zaproszenie? Wpisz kod', 'Heb je een uitnodiging? Voer een code in'),
'Join.EnterHint': ('Enter the code from your invitation.', 'Gib den Code aus deiner Einladung ein.', 'Saisissez le code de votre invitation.', 'Introduce el código de tu invitación.', 'Inserisci il codice del tuo invito.', 'Wpisz kod z zaproszenia.', 'Voer de code uit je uitnodiging in.'),
'Join.Malformed': ("Check the code: it's 8 letters and numbers, like ABCD-EFGH.", 'Prüfe den Code: Er hat 8 Buchstaben und Ziffern, etwa ABCD-EFGH.', 'Vérifiez le code : il compte 8 lettres et chiffres, comme ABCD-EFGH.', 'Revisa el código: tiene 8 letras y números, como ABCD-EFGH.', 'Controlla il codice: ha 8 lettere e cifre, come ABCD-EFGH.', 'Sprawdź kod: ma 8 liter i cyfr, np. ABCD-EFGH.', 'Controleer de code: die heeft 8 letters en cijfers, zoals ABCD-EFGH.'),
'Join.SignInTitle': ('Sign in to join a family', 'Melde dich an, um einer Familie beizutreten', 'Connectez-vous pour rejoindre une famille', 'Inicia sesión para unirte a una familia', 'Accedi per unirti a una famiglia', 'Zaloguj się, aby dołączyć do rodziny', 'Log in om bij een gezin te komen'),
'Join.SignInBody': ("You've been invited to a family on Coo. Sign in with your Google account to see the invitation.", 'Du hast eine Einladung in eine Familie bei Coo. Melde dich mit deinem Google-Konto an, um sie zu sehen.', 'Vous avez reçu une invitation dans une famille sur Coo. Connectez-vous avec votre compte Google pour la voir.', 'Tienes una invitación a una familia en Coo. Inicia sesión con tu cuenta de Google para verla.', 'Hai ricevuto un invito in una famiglia su Coo. Accedi con il tuo account Google per vederlo.', 'Masz zaproszenie do rodziny w Coo. Zaloguj się kontem Google, aby je zobaczyć.', 'Je bent uitgenodigd voor een gezin in Coo. Log in met je Google-account om de uitnodiging te zien.'),
'Join.Title': ("Join {0}'s family?", 'Der Familie von {0} beitreten?', 'Rejoindre la famille de {0} ?', '¿Unirte a la familia de {0}?', 'Unirti alla famiglia di {0}?', 'Dołączyć do rodziny: {0}?', 'Bij het gezin van {0} komen?'),
'Join.Body': ("{0} invited you to join as a parent. You'll both see the family's log and add to it.", '{0} hat dich als Elternteil eingeladen. Ihr seht beide die Einträge der Familie und könnt sie ergänzen.', '{0} vous invite à rejoindre la famille en tant que parent. Vous verrez tous les deux le journal de la famille et pourrez le compléter.', '{0} te ha invitado a unirte como progenitor. Los dos veréis el registro de la familia y podréis añadir registros.', '{0} ti ha invitato a unirti come genitore. Entrambi vedrete il registro della famiglia e potrete aggiungere voci.', '{0} zaprasza Cię do rodziny jako rodzica. Oboje będziecie widzieć dziennik rodziny i dodawać wpisy.', '{0} heeft je uitgenodigd als ouder. Jullie zien allebei het logboek van het gezin en kunnen het aanvullen.'),
'Join.Babies': ("Their family's log has: {0}.", 'In den Einträgen dieser Familie: {0}.', 'Le journal de cette famille contient : {0}.', 'El registro de esa familia tiene: {0}.', 'Il registro di quella famiglia ha: {0}.', 'W dzienniku tej rodziny: {0}.', 'Het logboek van dat gezin heeft: {0}.'),
'Join.NoBaby': ("Their family's log has no baby yet.", 'In den Einträgen dieser Familie ist noch kein Baby.', "Le journal de cette famille n'a pas encore de bébé.", 'El registro de esa familia aún no tiene ningún bebé.', 'Il registro di quella famiglia non ha ancora un bambino.', 'W dzienniku tej rodziny nie ma jeszcze dziecka.', 'Het logboek van dat gezin heeft nog geen baby.'),
'Join.Join': ('Join', 'Beitreten', 'Rejoindre', 'Unirme', 'Unisciti', 'Dołącz', 'Meedoen'),
'Join.AlreadyMember': ("You're already in this family.", 'Du bist schon in dieser Familie.', 'Vous faites déjà partie de cette famille.', 'Ya formas parte de esta familia.', 'Fai già parte di questa famiglia.', 'Już należysz do tej rodziny.', 'Je hoort al bij dit gezin.'),
'Join.OK': ('OK', 'OK', 'OK', 'Aceptar', 'OK', 'OK', 'OK'),
'Join.TryAgain': ('Try again', 'Noch einmal versuchen', 'Réessayer', 'Reintentar', 'Riprova', 'Spróbuj ponownie', 'Opnieuw proberen'),
'Join.Later': ('You can join later with the same link, until {0}.', 'Du kannst später mit demselben Link beitreten, bis {0}.', "Vous pourrez rejoindre la famille plus tard avec le même lien, jusqu'au {0}.", 'Puedes unirte más tarde con el mismo enlace, hasta el {0}.', 'Puoi unirti più tardi con lo stesso link, fino al {0}.', 'Możesz dołączyć później tym samym linkiem, do {0}.', 'Je kunt later meedoen met dezelfde link, tot {0}.'),
'Join.Joined': ("You've joined {0}'s family.", 'Du bist jetzt in der Familie von {0}.', 'Vous faites maintenant partie de la famille de {0}.', 'Ya formas parte de la familia de {0}.', 'Ora fai parte della famiglia di {0}.', 'Należysz teraz do rodziny: {0}.', 'Je hoort nu bij het gezin van {0}.'),
'Join.Copied': ("Your entries are now in {0}'s family too.", 'Deine Einträge sind jetzt auch in der Familie von {0}.', 'Vos entrées sont maintenant aussi dans la famille de {0}.', 'Tus registros ahora también están en la familia de {0}.', 'Le tue voci ora sono anche nella famiglia di {0}.', 'Twoje wpisy są teraz także w rodzinie: {0}.', 'Je items staan nu ook in het gezin van {0}.'),
'Join.DeleteOldButton': ('Delete old family', 'Alte Familie löschen', "Supprimer l'ancienne famille", 'Eliminar la familia anterior', 'Elimina la vecchia famiglia', 'Usuń starą rodzinę', 'Oude gezin verwijderen'),
'Join.KeepIt': ('Keep it', 'Behalten', 'La garder', 'Conservarla', 'Tienila', 'Zachowaj', 'Behouden'),
'Join.LeaveOld': ("Your entries are now in {0}'s family too. Leave your old family? Its log is removed from this phone. Entries you added stay with that family.", 'Deine Einträge sind jetzt auch in der Familie von {0}. Deine alte Familie verlassen? Ihre Einträge werden von diesem Handy entfernt. Deine Einträge bleiben bei dieser Familie.', 'Vos entrées sont maintenant aussi dans la famille de {0}. Quitter votre ancienne famille ? Son journal sera supprimé de ce téléphone. Les entrées que vous avez ajoutées restent dans cette famille.', 'Tus registros ahora también están en la familia de {0}. ¿Salir de tu familia anterior? Su registro se eliminará de este teléfono. Los registros que añadiste se quedan en esa familia.', 'Le tue voci ora sono anche nella famiglia di {0}. Lasciare la tua vecchia famiglia? Il suo registro verrà rimosso da questo telefono. Le voci che hai aggiunto restano a quella famiglia.', 'Twoje wpisy są teraz także w rodzinie: {0}. Opuścić starą rodzinę? Jej dziennik zostanie usunięty z tego telefonu. Dodane przez Ciebie wpisy zostaną w tamtej rodzinie.', 'Je items staan nu ook in het gezin van {0}. Je oude gezin verlaten? Het logboek wordt van deze telefoon verwijderd. Items die jij hebt toegevoegd blijven bij dat gezin.'),
'Join.BothFamilies': ('Both families are on this phone. Switch between them in Settings → Family.', 'Beide Familien sind auf diesem Handy. Wechsle zwischen ihnen unter Einstellungen → Familie.', "Les deux familles sont sur ce téléphone. Passez de l'une à l'autre dans Réglages → Famille.", 'Las dos familias están en este teléfono. Cambia entre ellas en Ajustes → Familia.', "Entrambe le famiglie sono su questo telefono. Passa dall'una all'altra in Impostazioni → Famiglia.", 'Obie rodziny są na tym telefonie. Przełączaj się między nimi w Ustawienia → Rodzina.', 'Beide gezinnen staan op deze telefoon. Wissel tussen ze in Instellingen → Gezin.'),
'BringOver.ChooseTarget': ('Bring entries over to which family?', 'Einträge in welche Familie übernehmen?', 'Transférer les entrées vers quelle famille ?', '¿A qué familia traer los registros?', 'In quale famiglia portare le voci?', 'Do której rodziny przenieść wpisy?', 'Items naar welk gezin overzetten?'),
'Removal.Title': ('You were removed from this family', 'Du wurdest aus dieser Familie entfernt', 'Vous ne faites plus partie de cette famille', 'Ya no formas parte de esta familia', 'Non fai più parte di questa famiglia', 'Nie należysz już do tej rodziny', 'Je bent uit dit gezin verwijderd'),
'Removal.Body': ('Someone in the family that logs {0} removed you, so its log will be removed from this phone. Entries you already shared stay with the family.', 'Jemand aus der Familie, die {0} einträgt, hat dich entfernt. Ihre Einträge werden deshalb von diesem Handy entfernt. Einträge, die du schon geteilt hast, bleiben bei der Familie.', 'Une personne de la famille qui suit {0} a mis fin à votre accès : son journal va être supprimé de ce téléphone. Les entrées que vous avez déjà partagées restent dans la famille.', 'Alguien de la familia que registra a {0} te ha quitado, así que su registro se eliminará de este teléfono. Los registros que ya compartiste se quedan en la familia.', 'Qualcuno della famiglia che registra {0} ha chiuso il tuo accesso, quindi il suo registro verrà rimosso da questo telefono. Le voci che hai già condiviso restano alla famiglia.', 'Ktoś z rodziny prowadzącej dziennik: {0} usunął Cię z niej, więc jej dziennik zostanie usunięty z tego telefonu. Udostępnione już wpisy zostaną w rodzinie.', 'Iemand in het gezin dat {0} bijhoudt heeft je verwijderd, dus het logboek wordt van deze telefoon verwijderd. Items die je al hebt gedeeld blijven bij het gezin.'),
'Removal.BodyNoBaby': ('Someone in this family removed you, so its log will be removed from this phone. Entries you already shared stay with the family.', 'Jemand aus dieser Familie hat dich entfernt. Ihre Einträge werden deshalb von diesem Handy entfernt. Einträge, die du schon geteilt hast, bleiben bei der Familie.', 'Une personne de cette famille a mis fin à votre accès : son journal va être supprimé de ce téléphone. Les entrées que vous avez déjà partagées restent dans la famille.', 'Alguien de esta familia te ha quitado, así que su registro se eliminará de este teléfono. Los registros que ya compartiste se quedan en la familia.', 'Qualcuno di questa famiglia ha chiuso il tuo accesso, quindi il suo registro verrà rimosso da questo telefono. Le voci che hai già condiviso restano alla famiglia.', 'Ktoś z tej rodziny usunął Cię z niej, więc jej dziennik zostanie usunięty z tego telefonu. Udostępnione już wpisy zostaną w rodzinie.', 'Iemand in dit gezin heeft je verwijderd, dus het logboek wordt van deze telefoon verwijderd. Items die je al hebt gedeeld blijven bij het gezin.'),
'Removal.Share': ('Share them as text', 'Als Text teilen', 'Les partager en texte', 'Compartirlos como texto', 'Condividile come testo', 'Udostępnij jako tekst', 'Als tekst delen'),
'Removal.Line': ('{0}, {1} · {2}', '{0}, {1} · {2}', '{0}, {1} · {2}', '{0}, {1} · {2}', '{0}, {1} · {2}', '{0}, {1} · {2}', '{0}, {1} · {2}'),
'Sync.NotAMember': ("Sharing stopped: this family is no longer on Coo's server. Your entries are safe on this phone.", 'Teilen wurde angehalten: Diese Familie ist nicht mehr auf dem Server von Coo. Deine Einträge sind auf diesem Handy sicher.', "Le partage est arrêté : cette famille n'est plus sur le serveur de Coo. Vos entrées sont en sécurité sur ce téléphone.", 'Se ha detenido el uso compartido: esta familia ya no está en el servidor de Coo. Tus registros están a salvo en este teléfono.', 'La condivisione si è fermata: questa famiglia non è più sul server di Coo. Le tue voci sono al sicuro su questo telefono.', 'Udostępnianie zatrzymane: tej rodziny nie ma już na serwerze Coo. Twoje wpisy są bezpieczne na tym telefonie.', 'Delen is gestopt: dit gezin staat niet meer op de server van Coo. Je items zijn veilig op deze telefoon.'),
'Today.Paused': ('Sharing with this family is paused. Your entries are safe on this phone.', 'Teilen mit dieser Familie ist pausiert. Deine Einträge sind auf diesem Handy sicher.', 'Le partage avec cette famille est en pause. Vos entrées sont en sécurité sur ce téléphone.', 'Compartir con esta familia está en pausa. Tus registros están a salvo en este teléfono.', 'La condivisione con questa famiglia è in pausa. Le tue voci sono al sicuro su questo telefono.', 'Udostępnianie tej rodzinie jest wstrzymane. Twoje wpisy są bezpieczne na tym telefonie.', 'Delen met dit gezin is gepauzeerd. Je items zijn veilig op deze telefoon.'),
'Error.invitation.invalid': ("This invitation can't be used. It may have expired, been used or been withdrawn. Ask for a new one.", 'Diese Einladung kann nicht verwendet werden. Sie ist vielleicht abgelaufen, schon benutzt oder zurückgezogen. Bitte um eine neue.', "Cette invitation ne peut pas être utilisée. Elle a peut-être expiré, déjà servi ou été retirée. Demandez-en une nouvelle.", 'Esta invitación no se puede usar. Puede que haya caducado, ya se haya usado o se haya retirado. Pide una nueva.', 'Questo invito non può essere usato. Potrebbe essere scaduto, già usato o ritirato. Chiedine uno nuovo.', 'Tego zaproszenia nie można użyć. Mogło wygasnąć, zostać już użyte lub wycofane. Poproś o nowe.', 'Deze uitnodiging kan niet worden gebruikt. Misschien is ze verlopen, al gebruikt of ingetrokken. Vraag om een nieuwe.'),
'Error.invitation.role-unsupported': ('Only parents can be invited for now.', 'Einladen kann man vorerst nur Eltern.', 'Pour le moment, seuls des parents peuvent être invités.', 'Por ahora solo se puede invitar a progenitores.', 'Per ora si possono invitare solo genitori.', 'Na razie można zapraszać tylko rodziców.', 'Voorlopig kunnen alleen ouders worden uitgenodigd.'),
'Error.invitation.too-many': ('This family already has 5 open invitations. Withdraw one to make a new one.', 'Diese Familie hat schon 5 offene Einladungen. Zieh eine zurück, um eine neue zu erstellen.', 'Cette famille a déjà 5 invitations en attente. Retirez-en une pour en créer une nouvelle.', 'Esta familia ya tiene 5 invitaciones abiertas. Retira una para crear otra.', 'Questa famiglia ha già 5 inviti aperti. Ritirane uno per crearne uno nuovo.', 'Ta rodzina ma już 5 otwartych zaproszeń. Wycofaj jedno, aby utworzyć nowe.', 'Dit gezin heeft al 5 openstaande uitnodigingen. Trek er een in om een nieuwe te maken.'),
'Error.household.forbidden': ("You can't do that in this family.", 'Das kannst du in dieser Familie nicht tun.', 'Vous ne pouvez pas faire cela dans cette famille.', 'No puedes hacer eso en esta familia.', 'Non puoi farlo in questa famiglia.', 'Nie możesz tego zrobić w tej rodzinie.', 'Dat kun je in dit gezin niet doen.'),
'Error.household.member-not-found': ('That person is no longer in this family.', 'Diese Person ist nicht mehr in dieser Familie.', 'Cette personne ne fait plus partie de cette famille.', 'Esa persona ya no está en esta familia.', 'Questa persona non fa più parte di questa famiglia.', 'Tej osoby nie ma już w tej rodzinie.', 'Die persoon hoort niet meer bij dit gezin.'),
'Error.household.owner-cannot-leave': ("The person who created the family can't leave it while others are in it.", 'Wer die Familie angelegt hat, kann sie nicht verlassen, solange andere darin sind.', "La personne qui a créé la famille ne peut pas la quitter tant que d'autres en font partie.", 'Quien creó la familia no puede salir de ella mientras haya otras personas.', 'Chi ha creato la famiglia non può lasciarla finché ci sono altre persone.', 'Osoba, która utworzyła rodzinę, nie może jej opuścić, dopóki są w niej inni.', 'Wie het gezin heeft aangemaakt, kan het niet verlaten zolang er anderen in zitten.'),
'Error.household.last-family': ("This is your only family, so it can't be left.", 'Das ist deine einzige Familie, deshalb kannst du sie nicht verlassen.', "C'est votre seule famille : vous ne pouvez pas la quitter.", 'Es tu única familia, así que no puedes salir de ella.', 'È la tua unica famiglia, quindi non puoi lasciarla.', 'To Twoja jedyna rodzina, więc nie można jej opuścić.', 'Dit is je enige gezin, dus je kunt het niet verlaten.'),
'Error.household.confirmation-required': ("This family still has entries on Coo's server. Confirm to delete them.", 'Diese Familie hat noch Einträge auf dem Server von Coo. Bestätige, um sie zu löschen.', 'Cette famille a encore des entrées sur le serveur de Coo. Confirmez pour les supprimer.', 'Esta familia aún tiene registros en el servidor de Coo. Confirma para eliminarlos.', 'Questa famiglia ha ancora voci sul server di Coo. Conferma per eliminarle.', 'Ta rodzina ma jeszcze wpisy na serwerze Coo. Potwierdź, aby je usunąć.', 'Dit gezin heeft nog items op de server van Coo. Bevestig om ze te verwijderen.'),
'Error.household.id-taken': ("That didn't work. Try again.", 'Das hat nicht geklappt. Versuche es noch einmal.', "Cela n'a pas fonctionné. Réessayez.", 'No ha funcionado. Vuelve a intentarlo.', 'Non ha funzionato. Riprova.', 'Nie udało się. Spróbuj ponownie.', 'Dat is niet gelukt. Probeer het opnieuw.'),
'Error.household.already-has-family': ("You're already in a family on Coo.", 'Du bist schon in einer Familie bei Coo.', "Vous faites déjà partie d'une famille sur Coo.", 'Ya formas parte de una familia en Coo.', 'Fai già parte di una famiglia su Coo.', 'Już należysz do rodziny w Coo.', 'Je hoort al bij een gezin in Coo.'),
'Error.household.malformed': ("That didn't work. Try again.", 'Das hat nicht geklappt. Versuche es noch einmal.', "Cela n'a pas fonctionné. Réessayez.", 'No ha funcionado. Vuelve a intentarlo.', 'Non ha funzionato. Riprova.', 'Nie udało się. Spróbuj ponownie.', 'Dat is niet gelukt. Probeer het opnieuw.'),
'Error.auth.rate-limited': ('Too many tries. Wait a while and try again.', 'Zu viele Versuche. Warte eine Weile und versuche es dann noch einmal.', 'Trop de tentatives. Patientez un peu, puis réessayez.', 'Demasiados intentos. Espera un rato y vuelve a intentarlo.', "Troppi tentativi. Aspetta un po' e riprova.", 'Zbyt wiele prób. Odczekaj chwilę i spróbuj ponownie.', 'Te veel pogingen. Wacht even en probeer het opnieuw.'),
'Error.sync.consent-required': ('Agree to share first, then try again.', 'Stimme zuerst dem Teilen zu und versuche es dann noch einmal.', "Acceptez d'abord le partage, puis réessayez.", 'Primero acepta compartir y luego vuelve a intentarlo.', 'Prima accetta la condivisione, poi riprova.', 'Najpierw zgódź się na udostępnianie, a potem spróbuj ponownie.', 'Ga eerst akkoord met delen en probeer het dan opnieuw.'),
```

Add to `P` (plural forms; `{0}` is always the number), after `'BringOver.Entries'`:

```python
'Family.People': {'en': {'one': '{0} person', 'other': '{0} people'}, 'de': {'one': '{0} Person', 'other': '{0} Personen'},
                  'fr': {'one': '{0} personne', 'other': '{0} personnes'}, 'es': {'one': '{0} persona', 'other': '{0} personas'},
                  'it': {'one': '{0} persona', 'other': '{0} persone'}, 'pl': {'one': '{0} osoba', 'few': '{0} osoby', 'many': '{0} osób'},
                  'nl': {'one': '{0} persoon', 'other': '{0} personen'}},
'Family.DeleteConfirm': {'en': {'one': "Delete this family? Its log of {1}, {0} entry, is deleted from Coo's server and this phone. This can't be undone.",
                                'other': "Delete this family? Its log of {1}, {0} entries, is deleted from Coo's server and this phone. This can't be undone."},
                         'de': {'one': 'Diese Familie löschen? Ihre Einträge für {1}, {0} Eintrag, werden vom Server von Coo und von diesem Handy gelöscht. Das lässt sich nicht rückgängig machen.',
                                'other': 'Diese Familie löschen? Ihre Einträge für {1}, {0} Einträge, werden vom Server von Coo und von diesem Handy gelöscht. Das lässt sich nicht rückgängig machen.'},
                         'fr': {'one': "Supprimer cette famille ? Son journal de {1}, {0} entrée, sera supprimé du serveur de Coo et de ce téléphone. C'est irréversible.",
                                'other': "Supprimer cette famille ? Son journal de {1}, {0} entrées, sera supprimé du serveur de Coo et de ce téléphone. C'est irréversible."},
                         'es': {'one': '¿Eliminar esta familia? Su {0} registro de {1} se eliminará del servidor de Coo y de este teléfono. No se puede deshacer.',
                                'other': '¿Eliminar esta familia? Sus {0} registros de {1} se eliminarán del servidor de Coo y de este teléfono. No se puede deshacer.'},
                         'it': {'one': 'Eliminare questa famiglia? Il suo registro di {1}, {0} voce, verrà eliminato dal server di Coo e da questo telefono. Non si può annullare.',
                                'other': 'Eliminare questa famiglia? Il suo registro di {1}, {0} voci, verrà eliminato dal server di Coo e da questo telefono. Non si può annullare.'},
                         'pl': {'one': 'Usunąć tę rodzinę? Jej dziennik ({1}, {0} wpis) zostanie usunięty z serwera Coo i z tego telefonu. Tego nie można cofnąć.',
                                'few': 'Usunąć tę rodzinę? Jej dziennik ({1}, {0} wpisy) zostanie usunięty z serwera Coo i z tego telefonu. Tego nie można cofnąć.',
                                'many': 'Usunąć tę rodzinę? Jej dziennik ({1}, {0} wpisów) zostanie usunięty z serwera Coo i z tego telefonu. Tego nie można cofnąć.'},
                         'nl': {'one': 'Dit gezin verwijderen? Het logboek van {1}, {0} item, wordt van de server van Coo en van deze telefoon verwijderd. Dit kan niet ongedaan worden gemaakt.',
                                'other': 'Dit gezin verwijderen? Het logboek van {1}, {0} items, wordt van de server van Coo en van deze telefoon verwijderd. Dit kan niet ongedaan worden gemaakt.'}},
'Family.DeleteConfirmNoBaby': {'en': {'one': "Delete this family? Its log, {0} entry, is deleted from Coo's server and this phone. This can't be undone.",
                                      'other': "Delete this family? Its log, {0} entries, is deleted from Coo's server and this phone. This can't be undone."},
                               'de': {'one': 'Diese Familie löschen? Ihre Einträge, {0} Eintrag, werden vom Server von Coo und von diesem Handy gelöscht. Das lässt sich nicht rückgängig machen.',
                                      'other': 'Diese Familie löschen? Ihre Einträge, {0} Einträge, werden vom Server von Coo und von diesem Handy gelöscht. Das lässt sich nicht rückgängig machen.'},
                               'fr': {'one': "Supprimer cette famille ? Son journal, {0} entrée, sera supprimé du serveur de Coo et de ce téléphone. C'est irréversible.",
                                      'other': "Supprimer cette famille ? Son journal, {0} entrées, sera supprimé du serveur de Coo et de ce téléphone. C'est irréversible."},
                               'es': {'one': '¿Eliminar esta familia? Su {0} registro se eliminará del servidor de Coo y de este teléfono. No se puede deshacer.',
                                      'other': '¿Eliminar esta familia? Sus {0} registros se eliminarán del servidor de Coo y de este teléfono. No se puede deshacer.'},
                               'it': {'one': 'Eliminare questa famiglia? Il suo registro, {0} voce, verrà eliminato dal server di Coo e da questo telefono. Non si può annullare.',
                                      'other': 'Eliminare questa famiglia? Il suo registro, {0} voci, verrà eliminato dal server di Coo e da questo telefono. Non si può annullare.'},
                               'pl': {'one': 'Usunąć tę rodzinę? Jej dziennik ({0} wpis) zostanie usunięty z serwera Coo i z tego telefonu. Tego nie można cofnąć.',
                                      'few': 'Usunąć tę rodzinę? Jej dziennik ({0} wpisy) zostanie usunięty z serwera Coo i z tego telefonu. Tego nie można cofnąć.',
                                      'many': 'Usunąć tę rodzinę? Jej dziennik ({0} wpisów) zostanie usunięty z serwera Coo i z tego telefonu. Tego nie można cofnąć.'},
                               'nl': {'one': 'Dit gezin verwijderen? Het logboek, {0} item, wordt van de server van Coo en van deze telefoon verwijderd. Dit kan niet ongedaan worden gemaakt.',
                                      'other': 'Dit gezin verwijderen? Het logboek, {0} items, wordt van de server van Coo en van deze telefoon verwijderd. Dit kan niet ongedaan worden gemaakt.'}},
'Family.Unshared': {'en': {'one': "{0} entry on this phone wasn't shared yet and will be lost.", 'other': "{0} entries on this phone weren't shared yet and will be lost."},
                    'de': {'one': '{0} Eintrag auf diesem Handy wurde noch nicht geteilt und geht verloren.', 'other': '{0} Einträge auf diesem Handy wurden noch nicht geteilt und gehen verloren.'},
                    'fr': {'one': "{0} entrée de ce téléphone n'a pas encore été partagée et sera perdue.", 'other': "{0} entrées de ce téléphone n'ont pas encore été partagées et seront perdues."},
                    'es': {'one': '{0} registro de este teléfono aún no se ha compartido y se perderá.', 'other': '{0} registros de este teléfono aún no se han compartido y se perderán.'},
                    'it': {'one': '{0} voce su questo telefono non è ancora stata condivisa e andrà persa.', 'other': '{0} voci su questo telefono non sono ancora state condivise e andranno perse.'},
                    'pl': {'one': '{0} wpis na tym telefonie nie został jeszcze udostępniony i przepadnie.', 'few': '{0} wpisy na tym telefonie nie zostały jeszcze udostępnione i przepadną.', 'many': '{0} wpisów na tym telefonie nie zostało jeszcze udostępnionych i przepadnie.'},
                    'nl': {'one': '{0} item op deze telefoon is nog niet gedeeld en gaat verloren.', 'other': '{0} items op deze telefoon zijn nog niet gedeeld en gaan verloren.'}},
'Join.DeleteOld': {'en': {'one': "Your entries are now in {1}'s family too. Delete your old family? Its {0} entry for {2} is deleted from Coo's server and this phone. The copies stay.",
                          'other': "Your entries are now in {1}'s family too. Delete your old family? Its {0} entries for {2} are deleted from Coo's server and this phone. The copies stay."},
                   'de': {'one': 'Deine Einträge sind jetzt auch in der Familie von {1}. Deine alte Familie löschen? Ihr {0} Eintrag für {2} wird vom Server von Coo und von diesem Handy gelöscht. Die Kopien bleiben.',
                          'other': 'Deine Einträge sind jetzt auch in der Familie von {1}. Deine alte Familie löschen? Ihre {0} Einträge für {2} werden vom Server von Coo und von diesem Handy gelöscht. Die Kopien bleiben.'},
                   'fr': {'one': "Vos entrées sont maintenant aussi dans la famille de {1}. Supprimer votre ancienne famille ? Sa {0} entrée pour {2} sera supprimée du serveur de Coo et de ce téléphone. Les copies restent.",
                          'other': "Vos entrées sont maintenant aussi dans la famille de {1}. Supprimer votre ancienne famille ? Ses {0} entrées pour {2} seront supprimées du serveur de Coo et de ce téléphone. Les copies restent."},
                   'es': {'one': 'Tus registros ahora también están en la familia de {1}. ¿Eliminar tu familia anterior? Su {0} registro de {2} se eliminará del servidor de Coo y de este teléfono. Las copias se quedan.',
                          'other': 'Tus registros ahora también están en la familia de {1}. ¿Eliminar tu familia anterior? Sus {0} registros de {2} se eliminarán del servidor de Coo y de este teléfono. Las copias se quedan.'},
                   'it': {'one': 'Le tue voci ora sono anche nella famiglia di {1}. Eliminare la tua vecchia famiglia? La sua {0} voce per {2} verrà eliminata dal server di Coo e da questo telefono. Le copie restano.',
                          'other': 'Le tue voci ora sono anche nella famiglia di {1}. Eliminare la tua vecchia famiglia? Le sue {0} voci per {2} verranno eliminate dal server di Coo e da questo telefono. Le copie restano.'},
                   'pl': {'one': 'Twoje wpisy są teraz także w rodzinie: {1}. Usunąć starą rodzinę? Jej {0} wpis dla: {2} zostanie usunięty z serwera Coo i z tego telefonu. Kopie zostaną.',
                          'few': 'Twoje wpisy są teraz także w rodzinie: {1}. Usunąć starą rodzinę? Jej {0} wpisy dla: {2} zostaną usunięte z serwera Coo i z tego telefonu. Kopie zostaną.',
                          'many': 'Twoje wpisy są teraz także w rodzinie: {1}. Usunąć starą rodzinę? Jej {0} wpisów dla: {2} zostanie usuniętych z serwera Coo i z tego telefonu. Kopie zostaną.'},
                   'nl': {'one': 'Je items staan nu ook in het gezin van {1}. Je oude gezin verwijderen? Het {0} item voor {2} wordt van de server van Coo en van deze telefoon verwijderd. De kopieën blijven.',
                          'other': 'Je items staan nu ook in het gezin van {1}. Je oude gezin verwijderen? De {0} items voor {2} worden van de server van Coo en van deze telefoon verwijderd. De kopieën blijven.'}},
'Join.Skipped': {'en': {'one': "{0} entry can't be brought over and is deleted with it.", 'other': "{0} entries can't be brought over and are deleted with it."},
                 'de': {'one': '{0} Eintrag kann nicht übernommen werden und wird mit ihr gelöscht.', 'other': '{0} Einträge können nicht übernommen werden und werden mit ihr gelöscht.'},
                 'fr': {'one': "{0} entrée ne peut pas être transférée et sera supprimée avec elle.", 'other': "{0} entrées ne peuvent pas être transférées et seront supprimées avec elle."},
                 'es': {'one': '{0} registro no se puede traer y se eliminará con ella.', 'other': '{0} registros no se pueden traer y se eliminarán con ella.'},
                 'it': {'one': '{0} voce non può essere portata e verrà eliminata con essa.', 'other': '{0} voci non possono essere portate e verranno eliminate con essa.'},
                 'pl': {'one': '{0} wpisu nie da się przenieść i zostanie usunięty razem z nią.', 'few': '{0} wpisów nie da się przenieść i zostaną usunięte razem z nią.', 'many': '{0} wpisów nie da się przenieść i zostaną usunięte razem z nią.'},
                 'nl': {'one': '{0} item kan niet worden overgezet en wordt mee verwijderd.', 'other': '{0} items kunnen niet worden overgezet en worden mee verwijderd.'}},
'Removal.Unshared': {'en': {'one': "{0} entry you logged wasn't shared yet.", 'other': "{0} entries you logged weren't shared yet."},
                     'de': {'one': '{0} Eintrag von dir wurde noch nicht geteilt.', 'other': '{0} Einträge von dir wurden noch nicht geteilt.'},
                     'fr': {'one': "{0} entrée que vous avez notée n'a pas encore été partagée.", 'other': "{0} entrées que vous avez notées n'ont pas encore été partagées."},
                     'es': {'one': '{0} registro que añadiste aún no se ha compartido.', 'other': '{0} registros que añadiste aún no se han compartido.'},
                     'it': {'one': '{0} voce che hai registrato non è ancora stata condivisa.', 'other': '{0} voci che hai registrato non sono ancora state condivise.'},
                     'pl': {'one': '{0} dodany przez Ciebie wpis nie został jeszcze udostępniony.', 'few': '{0} dodane przez Ciebie wpisy nie zostały jeszcze udostępnione.', 'many': '{0} dodanych przez Ciebie wpisów nie zostało jeszcze udostępnionych.'},
                     'nl': {'one': '{0} item dat je hebt gelogd is nog niet gedeeld.', 'other': '{0} items die je hebt gelogd zijn nog niet gedeeld.'}},
```

Then generate the resources:

```sh
python3 eng/strings/generate.py
```

Expected: seven lines, `AppResources.resx N` … with N grown by 77 non-plural keys plus the plural forms.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Coo.Mobile.Presentation.Tests --filter "FullyQualifiedName~LocalizationTests"`
Expected: PASS (every key in every language with the same placeholders; Polish plural categories present; no household word).

- [ ] **Step 5: Commit**

```bash
git add eng/strings/strings.py src/Mobile/Coo.Mobile.Presentation/Resources
git commit -m "Strings: members and invitations in seven languages; the app says family (Spec 011b §2–§9, R10)"
```

---

### Task 19: Labels, the Settings Family section, and the sync line's not-a-member row

**Files:**
- Create: `src/Mobile/Coo.Mobile.Presentation/Settings/FamilyLabels.cs`, `src/Mobile/Coo.Mobile.Presentation/Settings/FamilySectionViewModel.cs`
- Modify: `src/Mobile/Coo.Mobile.Presentation/Settings/Ports.cs` (`IExternalLauncher.ShareTextAsync`, `IConsentPrompt`),
  `src/Mobile/Coo.Mobile.Presentation/Settings/SyncLine.cs`, `src/Mobile/Coo.Mobile.Presentation/ServiceCollectionExtensions.cs`
- Test: `tests/Coo.Mobile.Presentation.Tests/FamilyLabelTests.cs`, `tests/Coo.Mobile.Presentation.Tests/FamilySectionTests.cs`,
  `tests/Coo.Mobile.Presentation.Tests/MemorySessions.cs` (create), `tests/Coo.Mobile.Presentation.Tests/SyncLineTests.cs` (append)

**Interfaces:**
- Consumes: `Families`, `FamilyOnPhone`, `FamilyMember`, `CreatedInvitation`, `InviteParent`, `SignIn`, `ConsentGate`,
  `ISessionStore`, `Account`, `ISyncControl`, `SyncStatus.ShownStopped`.
- Produces:
  - `IExternalLauncher.ShareTextAsync(string text, string title) → Task`; `public interface IConsentPrompt { Task<bool> AskAsync(); }`
    (the host shows Spec 011 §3's sheet and answers whether consent is now given).
  - `FamilyLabels`: `Title(FamilyOnPhone)`, `Second(FamilyOnPhone)`, `Choice(FamilyOnPhone)` ("Maja · Your family"),
    `RoleText(Role)`, `MemberName(FamilyMember)`, `Until(DateTimeOffset, bool is24Hour)` ("Thu 9 Oct, 14:02"),
    `ShareText(CreatedInvitation, bool is24Hour)`.
  - `FamilyChoice(HouseholdId Id, string Label)`.
  - `FamilySectionViewModel(Families families, Account account, SignIn signIn, ConsentGate consent, ISessionStore sessions, IConsentPrompt consentPrompt, InviteParent invite, IExternalLauncher launcher, IClockFormat clock, ISyncControl sync)`:
    properties `ShowsSwitcher`, `ShowingTitle`, `ShowingLine`, `ShowsMembers`, `MembersLine`, `ShowsOnlyHere`, `ShowsInvite`,
    `ShowsBringOver`, `IsBusy`, `Toast`, `Choices`, `ShownId`; `RefreshAsync()`; commands `InviteCommand`, `SwitchCommand(FamilyChoice)`.
  - `SyncLine` says `Sync.NotAMember` when `status.ShownStopped`.

- [ ] **Step 1: Write the failing tests**

`tests/Coo.Mobile.Presentation.Tests/FamilyLabelTests.cs`:

```csharp
using Coo.Domain.Common;
using Coo.Domain.Households;
using Coo.Mobile.Application.Households;
using Coo.Mobile.Application.Preferences;
using Coo.Mobile.Presentation.Localization;
using Coo.Mobile.Presentation.Settings;

namespace Coo.Mobile.Presentation.Tests;

/// <summary>Spec 011b §7.3's labels and §2's member rows.</summary>
[Collection(nameof(StringsState))]
public sealed class FamilyLabelTests : IDisposable
{
    private static readonly HouseholdId Id = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a1"));
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    public FamilyLabelTests() => Strings.Use(new LanguagePreference("en"), null);

    public void Dispose() => Strings.Use(LanguagePreference.System, null);

    private static FamilyOnPhone Family(string[] babies, FamilyMembership? membership) => new(Id, babies, membership, true, false, false, true);

    [Theory]
    [InlineData(new[] { "Maja" }, "Maja")]
    [InlineData(new[] { "Maja", "Leo" }, "Maja, Leo")]
    [InlineData(new string[0], "No baby yet")]
    public void Title_Babies_NamesThemOrSaysNoBabyYet(string[] babies, string expected)
    {
        // Act
        var title = FamilyLabels.Title(Family(babies, null));

        // Assert
        title.Should().Be(expected);
    }

    [Fact]
    public void Second_OwnParentOtherAndLocal_ReadAsTheSpecSays()
    {
        // Act
        var own = FamilyLabels.Second(Family(["Maja"], new FamilyMembership(Id, Role.Owner, Now, "Kasia", 1)));
        var theirs = FamilyLabels.Second(Family(["Maja"], new FamilyMembership(Id, Role.Parent, Now, "Andrzej", 2)));
        var local = FamilyLabels.Second(Family(["Maja"], null));

        // Assert
        own.Should().Be("Your family");
        theirs.Should().Be("Family of Andrzej");
        local.Should().Be("On this phone only");
    }

    [Fact]
    public void Second_PolishOwnerName_UsesTheColonFormSoTheNameStaysNominative()
    {
        // Arrange: spec §15.2 Q10.
        Strings.Use(new LanguagePreference("pl"), null);

        // Act
        var theirs = FamilyLabels.Second(Family(["Maja"], new FamilyMembership(Id, Role.Parent, Now, "Andrzej", 2)));

        // Assert
        theirs.Should().Be("Rodzina: Andrzej");
    }

    [Fact]
    public void MemberRows_OwnerAndYou_ReadAsTheSpecSays()
    {
        // Act
        var role = FamilyLabels.RoleText(Role.Owner);
        var name = FamilyLabels.MemberName(new FamilyMember(new UserId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000c1")), "Andrzej", "a@example.com", Role.Owner, Now, IsYou: true));

        // Assert
        role.Should().Be("Parent · created the family");
        name.Should().Be("Andrzej (you)");
    }

    [Fact]
    public void ShareText_AnInvitation_IsTheSpecsMessage()
    {
        // Arrange
        var invitation = new CreatedInvitation(Guid.Parse("7a1c0000-0000-7000-8000-0000000000e1"), InvitationCode.Parse("ABCDEFGH").Value,
            new Uri("https://api.coo.lechdigital.nl/i/ABCDEFGH"), new DateTimeOffset(2026, 10, 9, 14, 2, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 10, 9))));

        // Act
        var text = FamilyLabels.ShareText(invitation, is24Hour: true);

        // Assert
        text.Should().StartWith("Join my family on Coo, so we can keep our baby's log together: https://api.coo.lechdigital.nl/i/ABCDEFGH")
            .And.Contain("enter the code ABCD-EFGH.").And.EndWith("until Fri 9 Oct, 14:02.");
    }
}
```

`tests/Coo.Mobile.Presentation.Tests/FamilySectionTests.cs`:

```csharp
using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;
using Coo.Domain.Households;
using Coo.Mobile.Application.Auth;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Application.Households;
using Coo.Mobile.Application.Preferences;
using Coo.Mobile.Application.Sync;
using Coo.Mobile.Presentation.Localization;
using Coo.Mobile.Presentation.Settings;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Coo.Mobile.Presentation.Tests;

/// <summary>Spec 011b §2's Settings → Family rows, and inviting from them.</summary>
[Collection(nameof(StringsState))]
public sealed class FamilySectionTests : IDisposable
{
    private static readonly UserId Me = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000c1"));
    private static readonly AccountProfile Profile = new(Me, "Kasia", "k@example.com");
    private static readonly HouseholdId Home = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a1"));
    private static readonly HouseholdId Joined = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a2"));
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
    private static readonly DeviceId Phone = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000d1"));

    private readonly MemorySessions _sessions = new();
    private readonly Account _account;
    private readonly IFamilyStore _store = Substitute.For<IFamilyStore>();
    private readonly IHouseholdApi _api = Substitute.For<IHouseholdApi>();
    private readonly IHouseholdStore _households = Substitute.For<IHouseholdStore>();
    private readonly IHouseholdMoves _moves = Substitute.For<IHouseholdMoves>();
    private readonly ISyncState _state = Substitute.For<ISyncState>();
    private readonly IConsentPrompt _consentPrompt = Substitute.For<IConsentPrompt>();
    private readonly IExternalLauncher _launcher = Substitute.For<IExternalLauncher>();
    private readonly IClockFormat _clock = Substitute.For<IClockFormat>();
    private readonly ISyncControl _sync = Substitute.For<ISyncControl>();

    public FamilySectionTests()
    {
        Strings.Use(new LanguagePreference("en"), null);
        _account = new Account(_sessions);
        _clock.Is24Hour.Returns(true);
        _state.GetConsentAsync(Me, Arg.Any<CancellationToken>()).Returns(ConsentState.Given);
    }

    public void Dispose()
    {
        _account.Dispose();
        Strings.Use(LanguagePreference.System, null);
    }

    private static HouseholdSettings Settings(HouseholdId id) =>
        HouseholdSettings.Create(id, VolumeUnit.Millilitres, "Europe/Amsterdam", new HlcStamp(1_790_000_000_000, 0, Phone)).Value;

    private FamilySectionViewModel Section()
    {
        var families = new Families(_store, Substitute.For<IAccountApi>(), _api, _sessions, _households, _moves, _state,
            Substitute.For<IFamilyRecords>(), new ProvisionalSettings(Substitute.For<IVersionClock>(), Substitute.For<IDeviceLocale>(), new FakeTimeProvider(Now)));
        var signIn = new SignIn(Substitute.For<IAuthApi>(), Substitute.For<ISignInProvider>(), _households, Substitute.For<IDeviceIdentity>(),
            Substitute.For<IDeviceName>(), _account);
        return new FamilySectionViewModel(families, _account, signIn, new ConsentGate(_state, Substitute.For<IAccountApi>(), Substitute.For<IConsentApi>()),
            _sessions, _consentPrompt, new InviteParent(_api), _launcher, _clock, _sync);
    }

    private Task SignInAsync() => _account.SignedInAsync(
        new StoredSession("access", Now.AddMinutes(15), "refresh", Profile, Home, JoinPending: false), CancellationToken.None);

    private void OnPhone(params HouseholdId[] families)
    {
        _households.ListHouseholdsAsync(Arg.Any<CancellationToken>()).Returns([.. families.Select(Settings)]);
        _moves.ActiveHouseholdAsync(Arg.Any<CancellationToken>()).Returns(families[0]);
        _households.ListBabiesAsync(default, default).ReturnsForAnyArgs([]);
    }

    [Fact]
    public async Task RefreshAsync_OneLocalFamilySignedOut_ShowsOnlyHereInviteAndJoin()
    {
        // Arrange
        OnPhone(Home);

        // Act
        var section = Section();
        await section.RefreshAsync();

        // Assert
        section.ShowsSwitcher.Should().BeFalse();
        section.ShowsMembers.Should().BeFalse();
        section.ShowsOnlyHere.Should().BeTrue();
        section.ShowsInvite.Should().BeTrue("signed out, Invite a parent signs in first (§3)");
        section.ShowsBringOver.Should().BeFalse();
    }

    [Fact]
    public async Task RefreshAsync_TwoFamiliesShownSharedAsParent_ShowsTheSwitcherMembersAndInvite()
    {
        // Arrange
        await SignInAsync();
        OnPhone(Joined, Home);
        _store.ListAsync(Me, Arg.Any<CancellationToken>()).Returns([
            new FamilyMembership(Home, Role.Owner, Now, "Kasia", 1), new FamilyMembership(Joined, Role.Parent, Now.AddDays(1), "Andrzej", 2)]);

        // Act
        var section = Section();
        await section.RefreshAsync();

        // Assert
        section.ShowsSwitcher.Should().BeTrue();
        section.ShowingTitle.Should().Be("Showing: No baby yet");
        section.ShowingLine.Should().Be("Family of Andrzej");
        section.ShowsMembers.Should().BeTrue();
        section.MembersLine.Should().Be("2 people");
        section.ShowsInvite.Should().BeTrue();
        section.ShowsBringOver.Should().BeTrue("the shown family synced and is an account family, and another account family exists");
        section.Choices.Select(c => c.Label).Should().Equal("No baby yet · Your family", "No baby yet · Family of Andrzej");
    }

    [Fact]
    public async Task RefreshAsync_ShownFamilySoftLocked_HidesInviteAndLeavesItOutOfTheSwitcher()
    {
        // Arrange
        await SignInAsync();
        OnPhone(Joined, Home);
        _store.ListAsync(Me, Arg.Any<CancellationToken>()).Returns([
            new FamilyMembership(Home, Role.Owner, Now, "Kasia", 1), new FamilyMembership(Joined, Role.Parent, Now.AddDays(1), "Andrzej", 2)]);
        _state.IsSoftLockedAsync(Joined, Arg.Any<CancellationToken>()).Returns(true);

        // Act
        var section = Section();
        await section.RefreshAsync();

        // Assert
        section.ShowsInvite.Should().BeFalse();
        section.Choices.Select(c => c.Id).Should().Equal(Home);
    }

    [Fact]
    public async Task InviteCommand_SignedInShownShared_SharesTheTextWithLinkCodeAndExpiry()
    {
        // Arrange
        await SignInAsync();
        OnPhone(Home);
        _store.ListAsync(Me, Arg.Any<CancellationToken>()).Returns([new FamilyMembership(Home, Role.Owner, Now, "Kasia", 1)]);
        _api.CreateInvitationAsync(Home, Arg.Any<CancellationToken>()).Returns(new CreatedInvitation(Guid.Parse("7a1c0000-0000-7000-8000-0000000000e1"),
            InvitationCode.Parse("ABCDEFGH").Value, new Uri("https://api.coo.lechdigital.nl/i/ABCDEFGH"), Now.AddHours(72)));
        var section = Section();
        await section.RefreshAsync();

        // Act
        await section.InviteCommand.ExecuteAsync(null);

        // Assert
        await _launcher.Received(1).ShareTextAsync(Arg.Is<string>(t => t.Contains("https://api.coo.lechdigital.nl/i/ABCDEFGH") && t.Contains("ABCD-EFGH")), "Invite a parent");
        section.Toast.Should().BeEmpty();
    }

    [Fact]
    public async Task InviteCommand_ConsentRefused_InvitesNobody()
    {
        // Arrange: §3, consent if the account has none, then carries on.
        await SignInAsync();
        OnPhone(Home);
        _state.GetConsentAsync(Me, Arg.Any<CancellationToken>()).Returns(ConsentState.Declined);
        _consentPrompt.AskAsync().Returns(false);
        var section = Section();

        // Act
        await section.InviteCommand.ExecuteAsync(null);

        // Assert
        await _api.DidNotReceiveWithAnyArgs().CreateInvitationAsync(default, default);
        await _launcher.DidNotReceiveWithAnyArgs().ShareTextAsync(default!, default!);
    }

    [Fact]
    public async Task InviteCommand_Offline_SaysItDidNotWork()
    {
        // Arrange
        await SignInAsync();
        OnPhone(Home);
        _store.ListAsync(Me, Arg.Any<CancellationToken>()).Returns([new FamilyMembership(Home, Role.Owner, Now, "Kasia", 1)]);
        _api.CreateInvitationAsync(Home, Arg.Any<CancellationToken>()).Returns(Task.FromException<CreatedInvitation>(new HouseholdUnavailableException("offline")));
        var section = Section();
        await section.RefreshAsync();

        // Act
        await section.InviteCommand.ExecuteAsync(null);

        // Assert
        section.Toast.Should().Be("That didn't work. Check your connection and try again.");
    }

    [Fact]
    public async Task SwitchCommand_AnotherFamily_ActivatesItAndTellsThePages()
    {
        // Arrange
        await SignInAsync();
        OnPhone(Joined, Home);
        var section = Section();

        // Act
        await section.SwitchCommand.ExecuteAsync(new FamilyChoice(Home, "Maja · Your family"));

        // Assert
        await _moves.Received(1).ActivateAsync(Home, Arg.Any<CancellationToken>());
        _sync.Received(1).NotifyDataChanged();
    }
}
```

The Presentation tests have no in-memory session store yet (they substitute `ISessionStore`); the family tests need one that
keeps what `Account` saves. Create `tests/Coo.Mobile.Presentation.Tests/MemorySessions.cs`:

```csharp
using Coo.Mobile.Application.Auth;

namespace Coo.Mobile.Presentation.Tests;

/// <summary>The session store in memory: what <see cref="Account"/> saves is what the use cases read.</summary>
internal sealed class MemorySessions : ISessionStore
{
    public StoredSession? Stored { get; private set; }

    public Task<StoredSession?> GetAsync(CancellationToken cancellationToken) => Task.FromResult(Stored);

    public Task SaveAsync(StoredSession session, CancellationToken cancellationToken)
    {
        Stored = session;
        return Task.CompletedTask;
    }

    public Task ClearAsync(CancellationToken cancellationToken)
    {
        Stored = null;
        return Task.CompletedTask;
    }
}
```

Append to `SyncLineTests`:

```csharp
    [Fact]
    public void For_ShownFamilyNotAMember_SaysSharingStoppedForThatFamily()
    {
        // Act
        var line = SyncLine.For(Signed, Status(CycleOutcome.Synced, at: At) with { ShownStopped = true }, is24Hour: true);

        // Assert
        line.Text.Should().Be("Sharing stopped: this family is no longer on Coo's server. Your entries are safe on this phone.");
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Coo.Mobile.Presentation.Tests --filter "FullyQualifiedName~FamilyLabelTests|FullyQualifiedName~FamilySectionTests|FullyQualifiedName~SyncLineTests"`
Expected: FAIL to compile (`FamilyLabels` not found).

- [ ] **Step 3: Write the implementation**

Append to `Settings/Ports.cs`:

```csharp
/// <summary>Spec 011 §3's consent sheet, shown where the person is (Spec 011b §3, §5.1). True when consent is given once it closes.</summary>
public interface IConsentPrompt
{
    Task<bool> AskAsync();
}
```

and in `IExternalLauncher`:

```csharp
    /// <summary>Android's share sheet with a text (the invitation, spec §3; unshared entries, §8.2). Closing it sends nothing.</summary>
    Task ShareTextAsync(string text, string title);
```

`src/Mobile/Coo.Mobile.Presentation/Settings/FamilyLabels.cs`:

```csharp
using Coo.Domain.Households;
using Coo.Mobile.Application.Households;
using Coo.Mobile.Presentation.Formatting;
using Coo.Mobile.Presentation.Localization;

namespace Coo.Mobile.Presentation.Settings;

public sealed record FamilyChoice(Coo.Domain.Common.HouseholdId Id, string Label);

/// <summary>How a family and its members read (Spec 011b §2, §7.3). Names join with ", " (ruling 11).</summary>
public static class FamilyLabels
{
    public static string Title(FamilyOnPhone family)
    {
        ArgumentNullException.ThrowIfNull(family);
        return family.BabyNames.Count == 0 ? Strings.Get("Family.NoBaby") : string.Join(", ", family.BabyNames);
    }

    public static string Second(FamilyOnPhone family)
    {
        ArgumentNullException.ThrowIfNull(family);
        return family.Membership switch
        {
            null => Strings.Get("Family.LocalOnly"),
            { Role: Role.Owner } => Strings.Get("Family.Yours"),
            { OwnerName.Length: > 0 } m => Strings.Format("Family.Of", m.OwnerName),
            _ => Strings.Get("Family.Title"),
        };
    }

    /// <summary>"Maja · Family of Andrzej": one line for the switcher's action sheet.</summary>
    public static string Choice(FamilyOnPhone family) => $"{Title(family)} · {Second(family)}";

    public static string RoleText(Role role) => Strings.Get(role switch
    {
        Role.Owner => "Family.RoleOwner",
        Role.Parent => "Family.RoleParent",
        _ => "Family.RoleCaregiver",
    });

    public static string MemberName(FamilyMember member)
    {
        ArgumentNullException.ThrowIfNull(member);
        return member.IsYou ? Strings.Format("Family.You", member.Name) : member.Name;
    }

    /// <summary>"Thu 9 Oct, 14:02" in the person's language and clock.</summary>
    public static string Until(DateTimeOffset at, bool is24Hour) =>
        $"{at.ToLocalTime().ToString("ddd d MMM", Strings.Culture)}, {new Formats(is24Hour, VolumeUnit.Millilitres).Time(at)}";

    /// <summary>Spec 011b §3's share text: one resource, three placeholders.</summary>
    public static string ShareText(CreatedInvitation invitation, bool is24Hour)
    {
        ArgumentNullException.ThrowIfNull(invitation);
        return Strings.Format("Invite.ShareText", invitation.Link, invitation.Code.Display, Until(invitation.ExpiresAt, is24Hour));
    }
}
```

`src/Mobile/Coo.Mobile.Presentation/Settings/FamilySectionViewModel.cs`:

```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Coo.Domain.Common;
using Coo.Mobile.Application.Auth;
using Coo.Mobile.Application.Households;
using Coo.Mobile.Application.Preferences;
using Coo.Mobile.Application.Sync;
using Coo.Mobile.Presentation.Localization;

namespace Coo.Mobile.Presentation.Settings;

/// <summary>
/// Settings → Family (Spec 011b §2), right after Account: which family is shown, its members, inviting, joining and bringing
/// entries over. Pure: the page asks it and draws; texts are read on each use.
/// </summary>
public sealed partial class FamilySectionViewModel(
    Families families, Account account, SignIn signIn, ConsentGate consent, ISessionStore sessions, IConsentPrompt consentPrompt,
    InviteParent invite, IExternalLauncher launcher, IClockFormat clock, ISyncControl sync) : ObservableObject
{
    [ObservableProperty]
    private bool _showsSwitcher;

    [ObservableProperty]
    private string _showingTitle = "";

    [ObservableProperty]
    private string _showingLine = "";

    [ObservableProperty]
    private bool _showsMembers;

    [ObservableProperty]
    private string _membersLine = "";

    [ObservableProperty]
    private bool _showsOnlyHere;

    [ObservableProperty]
    private bool _showsInvite;

    [ObservableProperty]
    private bool _showsBringOver;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _toast = "";

    [ObservableProperty]
    private IReadOnlyList<FamilyChoice> _choices = [];

    /// <summary>The shown family, for the Family page and the bring-over.</summary>
    public HouseholdId? ShownId { get; private set; }

    public async Task RefreshAsync()
    {
        var list = await families.ListAsync(CancellationToken.None);
        var shown = list.FirstOrDefault(f => f.IsShown);
        ShownId = shown?.Id;
        ShowsSwitcher = list.Count > 1;
        ShowingTitle = shown is null ? "" : Strings.Format("Family.Showing", FamilyLabels.Title(shown));
        ShowingLine = shown is null ? "" : FamilyLabels.Second(shown);
        ShowsMembers = shown is { IsShared: true };
        MembersLine = shown?.Membership is { } m ? Strings.Plural("Family.People", m.MemberCount) : "";
        ShowsOnlyHere = shown is { IsShared: false };
        ShowsInvite = account.State is SignedOut || shown is { IsShared: true, IsSoftLocked: false, CanManage: true };
        ShowsBringOver = list.Count > 1 && shown is { MayBeBroughtOver: true }
            && list.Any(f => f.Id != shown.Id && f.IsShared && !f.IsSoftLocked);
        Choices = [.. list.Where(f => !f.IsSoftLocked).Select(f => new FamilyChoice(f.Id, FamilyLabels.Choice(f)))];
    }

    /// <summary>The switcher (§7.3): only the shown family changes; Today, Log and Settings follow.</summary>
    [RelayCommand]
    private async Task SwitchAsync(FamilyChoice choice)
    {
        ArgumentNullException.ThrowIfNull(choice);
        await families.SwitchAsync(choice.Id, CancellationToken.None);
        sync.NotifyDataChanged();
        await RefreshAsync();
    }

    /// <summary>§3: signed out, sign-in opens in place, then consent if the account has none, then the share sheet.</summary>
    [RelayCommand]
    private async Task InviteAsync()
    {
        if (IsBusy)
            return;
        IsBusy = true;
        Toast = "";
        try
        {
            var justSignedIn = false;
            if (account.State is SignedOut)
            {
                switch (await signIn.ExecuteAsync(CancellationToken.None))
                {
                    case SignInResult.Cancelled:
                        return;
                    case SignInResult.Failed:
                        Toast = Strings.Get("Account.SignInFailed");
                        return;
                }

                justSignedIn = true;
            }

            if (await sessions.GetAsync(CancellationToken.None) is not { } session)
                return;
            if (await consent.CheckAsync(session.Profile.UserId, CancellationToken.None) != ConsentState.Given && !await consentPrompt.AskAsync())
                return;

            await families.RefreshAsync(CancellationToken.None);
            var shown = await families.ShownAsync(CancellationToken.None);

            // Ruling 28: after signing in, the account's own family; otherwise the shown one, which the row was for.
            var target = !justSignedIn && shown is { IsShared: true } ? shown.Id : session.AccountHousehold;
            var result = await invite.ExecuteAsync(target, CancellationToken.None);
            if (result.Invitation is { } invitation)
                await launcher.ShareTextAsync(FamilyLabels.ShareText(invitation, clock.Is24Hour), Strings.Get("Family.Invite"));
            else
                Toast = result.Action.Code is { } code ? Strings.Error(code) : Strings.Get("Family.ActionFailed");
        }
#pragma warning disable CA1031 // A failed local read or the share sheet refusing must say so, never crash Settings.
        catch (Exception)
#pragma warning restore CA1031
        {
            Toast = Strings.Get("Family.ActionFailed");
        }
        finally
        {
            IsBusy = false;
            await RefreshAsync();
        }
    }
}
```

`SyncLine.For`, in the `status.Last switch`, right after the `ClientTooOld` arm:

```csharp
            _ when status.ShownStopped => new SyncLineView(Strings.Get("Sync.NotAMember"), SyncLineAction.None),
```

Register in Presentation's `ServiceCollectionExtensions`: `services.AddSingleton<Settings.FamilySectionViewModel>();`.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Coo.Mobile.Presentation.Tests`
Expected: PASS. (`ShareText_AnInvitation_IsTheSpecsMessage` builds the expiry in the machine's local offset, so it passes
under `TZ=UTC` too; 9 Oct 2026 is a Friday.)

- [ ] **Step 5: Commit**

```bash
git add src/Mobile/Coo.Mobile.Presentation tests/Coo.Mobile.Presentation.Tests
git commit -m "Presentation: family labels, the Settings Family section, inviting with the share text; the not-a-member line (Spec 011b §2, §3, §7.3, §9)"
```

---

### Task 20: The Family page (members, remove, invitations, withdraw, leave or delete)

**Files:**
- Create: `src/Mobile/Coo.Mobile.Presentation/Settings/FamilyPageViewModel.cs`
- Modify: `src/Mobile/Coo.Mobile.Presentation/ServiceCollectionExtensions.cs`
- Test: `tests/Coo.Mobile.Presentation.Tests/FamilyPageTests.cs`

**Interfaces:**
- Consumes: `LoadFamilyMembers`, `RemoveFamilyMember`, `WithdrawFamilyInvitation`, `InviteParent`, `LeaveFamily`,
  `Families.ListAsync/ContentsAsync`, `FamilyLabels`, `ConfirmPrompt` (existing, `AccountViewModel.cs`).
- Produces: `MemberRow(UserId UserId, string Label, string Email, string Role, bool CanRemove, string Name)`,
  `InvitationRow(Guid Id, string Line)`; `FamilyPageViewModel(…)` with `LoadAsync(HouseholdId)`, properties `Title`,
  `Members`, `Invitations`, `ShowsInvitations`, `ShowsInvite`, `ShowsLeave`, `ShowsDelete`, `RefreshFailed`, `IsBusy`, `Toast`;
  `RemovePrompt(MemberRow)`, `WithdrawPrompt`, `LeavePromptAsync()`, `DeletePromptAsync()`; commands `RemoveCommand(MemberRow)`,
  `WithdrawCommand(InvitationRow)`, `InviteCommand`, `LeaveCommand`, `DeleteCommand`; event `Closed`.

- [ ] **Step 1: Write the failing tests**

`tests/Coo.Mobile.Presentation.Tests/FamilyPageTests.cs`:

```csharp
using Coo.Contracts.Households;
using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;
using Coo.Domain.Households;
using Coo.Mobile.Application.Auth;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Application.Households;
using Coo.Mobile.Application.Preferences;
using Coo.Mobile.Application.Sync;
using Coo.Mobile.Presentation.Localization;
using Coo.Mobile.Presentation.Settings;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Coo.Mobile.Presentation.Tests;

/// <summary>Spec 011b §2's Family page: who sees what, the confirmations, and offline.</summary>
[Collection(nameof(StringsState))]
public sealed class FamilyPageTests : IDisposable
{
    private static readonly UserId Me = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000c1"));
    private static readonly UserId Andrzej = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000c9"));
    private static readonly HouseholdId Home = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a1"));
    private static readonly HouseholdId Joined = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a2"));
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
    private static readonly DeviceId Phone = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000d1"));

    private readonly MemorySessions _sessions = new();
    private readonly Account _account;
    private readonly IFamilyStore _store = Substitute.For<IFamilyStore>();
    private readonly IHouseholdApi _api = Substitute.For<IHouseholdApi>();
    private readonly IHouseholdStore _households = Substitute.For<IHouseholdStore>();
    private readonly IHouseholdMoves _moves = Substitute.For<IHouseholdMoves>();
    private readonly IFamilyRecords _records = Substitute.For<IFamilyRecords>();
    private readonly IExternalLauncher _launcher = Substitute.For<IExternalLauncher>();
    private readonly IClockFormat _clock = Substitute.For<IClockFormat>();
    private readonly ISyncControl _sync = Substitute.For<ISyncControl>();

    public FamilyPageTests()
    {
        Strings.Use(new LanguagePreference("en"), null);
        _account = new Account(_sessions);
        _households.ListHouseholdsAsync(Arg.Any<CancellationToken>()).Returns([Settings(Joined), Settings(Home)]);
        _households.ListBabiesAsync(default, default).ReturnsForAnyArgs([]);
        _moves.ActiveHouseholdAsync(Arg.Any<CancellationToken>()).Returns(Joined);
        _moves.SummaryAsync(default, default).ReturnsForAnyArgs(new LocalSummary(42, [new LocalBaby(new BabyId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000b1")), "Maja")]));
        _records.ListUnsharedEntriesAsync(default, default).ReturnsForAnyArgs([]);
        _api.ListMembersAsync(Joined, Arg.Any<CancellationToken>()).Returns(new FamilyMembers(
            [new FamilyMember(Andrzej, "Andrzej", "a@example.com", Role.Owner, Now, false), new FamilyMember(Me, "Kasia", "k@example.com", Role.Parent, Now.AddDays(1), true)],
            [new FamilyInvitation(Guid.Parse("7a1c0000-0000-7000-8000-0000000000e1"), Role.Parent, Andrzej, Now, Now.AddHours(72))]));
    }

    public void Dispose()
    {
        _account.Dispose();
        Strings.Use(LanguagePreference.System, null);
    }

    private static HouseholdSettings Settings(HouseholdId id) =>
        HouseholdSettings.Create(id, VolumeUnit.Millilitres, "Europe/Amsterdam", new HlcStamp(1_790_000_000_000, 0, Phone)).Value;

    private async Task<FamilyPageViewModel> PageAsync(Role myRole, int members)
    {
        await _account.SignedInAsync(new StoredSession("a", Now, "r", new AccountProfile(Me, "Kasia", "k@example.com"), Home, JoinPending: false), CancellationToken.None);
        _store.ListAsync(Me, Arg.Any<CancellationToken>()).Returns([
            new FamilyMembership(Home, Role.Owner, Now.AddDays(-30), "Kasia", 1), new FamilyMembership(Joined, myRole, Now.AddDays(1), "Andrzej", members)]);
        var families = new Families(_store, Substitute.For<IAccountApi>(), _api, _sessions, _households, _moves, Substitute.For<ISyncState>(), _records,
            new ProvisionalSettings(Substitute.For<IVersionClock>(), Substitute.For<IDeviceLocale>(), new FakeTimeProvider(Now)));
        var page = new FamilyPageViewModel(new LoadFamilyMembers(_api), new RemoveFamilyMember(_api, families), new WithdrawFamilyInvitation(_api),
            new InviteParent(_api), new LeaveFamily(_api, families, _sync), families, _launcher, _clock);
        await page.LoadAsync(Joined);
        return page;
    }

    [Fact]
    public async Task LoadAsync_AsParent_ListsMembersWithRolesAndNoRemoveBesideTheOwner()
    {
        // Act
        var page = await PageAsync(Role.Parent, 2);

        // Assert
        page.Members.Should().BeEquivalentTo([
            new MemberRow(Andrzej, "Andrzej", "a@example.com", "Parent · created the family", CanRemove: false, "Andrzej"),
            new MemberRow(Me, "Kasia (you)", "k@example.com", "Parent", CanRemove: false, "Kasia")]);
        page.ShowsInvitations.Should().BeTrue();
        page.Invitations.Single().Line.Should().StartWith("Invitation for a parent · until ");
        page.ShowsLeave.Should().BeTrue("a Parent with another family may leave");
        page.ShowsDelete.Should().BeFalse();
    }

    [Fact]
    public async Task LoadAsync_Offline_ShowsTheLastListAndSaysItCouldNotRefresh()
    {
        // Arrange
        var page = await PageAsync(Role.Parent, 2);
        _api.ListMembersAsync(Joined, Arg.Any<CancellationToken>()).ThrowsAsync(new HouseholdUnavailableException("offline"));

        // Act
        await page.LoadAsync(Joined);

        // Assert
        page.RefreshFailed.Should().BeTrue();
        page.Members.Should().HaveCount(2);
    }

    [Fact]
    public async Task RemoveCommand_Offline_SaysItDidNotWorkAndChangesNothing()
    {
        // Arrange: Review Focus 4.
        var page = await PageAsync(Role.Owner, 2);
        _api.RemoveMemberAsync(Joined, Andrzej, Arg.Any<CancellationToken>()).ThrowsAsync(new HouseholdUnavailableException("offline"));

        // Act
        await page.RemoveCommand.ExecuteAsync(page.Members[0]);

        // Assert
        page.Toast.Should().Be("That didn't work. Check your connection and try again.");
    }

    [Fact]
    public async Task RemovePrompt_AMember_NamesThemGenderNeutrally()
    {
        // Arrange
        var page = await PageAsync(Role.Owner, 2);

        // Act
        var prompt = page.RemovePrompt(page.Members[1]);

        // Assert
        prompt.Message.Should().Be("Remove Kasia from the family? Their phones stop sharing this family's log, and it is removed from them. Entries they added stay.");
        prompt.Accept.Should().Be("Remove");
    }

    [Fact]
    public async Task DeletePromptAsync_OwnerAloneWithUnsharedEntries_SaysWhatIsDeletedAndWhatIsLost()
    {
        // Arrange
        var page = await PageAsync(Role.Owner, 1);
        _records.ListUnsharedEntriesAsync(Joined, Arg.Any<CancellationToken>()).Returns([new QuarantinedEntry(new EntryId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000f1")), "diaper", Now, "x"),
            new QuarantinedEntry(new EntryId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000f2")), "diaper", Now, "x"),
            new QuarantinedEntry(new EntryId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000f3")), "diaper", Now, "x")]);

        // Act
        var prompt = await page.DeletePromptAsync();

        // Assert
        page.ShowsDelete.Should().BeTrue();
        prompt.Message.Should().Be(
            "Delete this family? Its log of Maja, 42 entries, is deleted from Coo's server and this phone. This can't be undone.\n\n"
            + "3 entries on this phone weren't shared yet and will be lost.");
    }

    [Fact]
    public async Task LeaveCommand_Answered_ClosesThePage()
    {
        // Arrange
        var page = await PageAsync(Role.Parent, 2);
        var closed = 0;
        page.Closed += (_, _) => closed++;
        _api.LeaveAsync(Joined, true, Arg.Any<CancellationToken>()).Returns(LeaveOutcome.Left);

        // Act
        await page.LeaveCommand.ExecuteAsync(null);

        // Assert
        closed.Should().Be(1);
    }

    [Fact]
    public async Task LeaveCommand_RefusedLastFamily_SaysWhyAndStays()
    {
        // Arrange
        var page = await PageAsync(Role.Parent, 2);
        _api.LeaveAsync(Joined, true, Arg.Any<CancellationToken>()).ThrowsAsync(new HouseholdRefusedException(HouseholdErrors.LastFamily));

        // Act
        await page.LeaveCommand.ExecuteAsync(null);

        // Assert
        page.Toast.Should().Be("This is your only family, so it can't be left.");
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Coo.Mobile.Presentation.Tests --filter "FullyQualifiedName~FamilyPageTests"`
Expected: FAIL to compile.

- [ ] **Step 3: Write the implementation**

`src/Mobile/Coo.Mobile.Presentation/Settings/FamilyPageViewModel.cs`:

```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Coo.Domain.Common;
using Coo.Domain.Households;
using Coo.Mobile.Application.Households;
using Coo.Mobile.Application.Preferences;
using Coo.Mobile.Presentation.Localization;

namespace Coo.Mobile.Presentation.Settings;

/// <param name="Label">The name as the row shows it: "Kasia (you)" for oneself.</param>
/// <param name="Name">The plain name, for the confirmation.</param>
public sealed record MemberRow(UserId UserId, string Label, string Email, string Role, bool CanRemove, string Name);

public sealed record InvitationRow(Guid Id, string Line);

/// <summary>
/// The Family page (Spec 011b §2), pushed from Settings → Family → Members. Offline it shows the last list fetched and says
/// it could not refresh; Remove, Withdraw, Invite and Leave then say "That didn't work…" and change nothing.
/// </summary>
public sealed partial class FamilyPageViewModel(
    LoadFamilyMembers load, RemoveFamilyMember remove, WithdrawFamilyInvitation withdraw, InviteParent invite, LeaveFamily leave,
    Families families, IExternalLauncher launcher, IClockFormat clock) : ObservableObject
{
    private HouseholdId _household;
    private FamilyOnPhone? _family;

    [ObservableProperty]
    private string _title = "";

    [ObservableProperty]
    private IReadOnlyList<MemberRow> _members = [];

    [ObservableProperty]
    private IReadOnlyList<InvitationRow> _invitations = [];

    [ObservableProperty]
    private bool _showsInvitations;

    [ObservableProperty]
    private bool _showsInvite;

    [ObservableProperty]
    private bool _showsLeave;

    [ObservableProperty]
    private bool _showsDelete;

    [ObservableProperty]
    private bool _refreshFailed;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _toast = "";

    /// <summary>The family is gone from this phone (left or deleted): the page pops.</summary>
    public event EventHandler? Closed;

    public static string RefreshFailedText => Strings.Get("Family.RefreshFailed");

    public async Task LoadAsync(HouseholdId household)
    {
        _household = household;
        var list = await families.ListAsync(CancellationToken.None);
        _family = list.FirstOrDefault(f => f.Id == household);
        if (_family is null)
        {
            Closed?.Invoke(this, EventArgs.Empty);
            return;
        }

        Title = FamilyLabels.Title(_family);
        var manage = _family.CanManage;
        var hasOther = list.Count(f => f.IsShared) > 1;
        ShowsInvite = manage && !_family.IsSoftLocked;
        ShowsInvitations = manage;
        ShowsLeave = hasOther && _family.Membership?.Role is Role.Parent or Role.Caregiver;
        ShowsDelete = hasOther && _family.Membership is { Role: Role.Owner, MemberCount: 1 };

        var loaded = await load.ExecuteAsync(household, CancellationToken.None);
        RefreshFailed = !loaded.IsFresh;
        if (loaded.Members is not { } members)
            return;
        Members = [.. members.Members.Select(m => new MemberRow(
            m.UserId, FamilyLabels.MemberName(m), m.Email, FamilyLabels.RoleText(m.Role), CanRemove: manage && !m.IsYou && m.Role != Role.Owner, m.Name))];
        Invitations = manage
            ? [.. members.Invitations.Select(i => new InvitationRow(i.InvitationId, Strings.Format("Family.InvitationLine", FamilyLabels.Until(i.ExpiresAt, clock.Is24Hour))))]
            : [];
    }

    public ConfirmPrompt RemovePrompt(MemberRow member)
    {
        ArgumentNullException.ThrowIfNull(member);
        return new ConfirmPrompt(Strings.Get("Family.Remove"), Strings.Format("Family.RemoveConfirm", member.Name), Strings.Get("Family.Remove"), Strings.Get("Action.Cancel"));
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "The page binds to it on the instance.")]
    public ConfirmPrompt WithdrawPrompt =>
        new(Strings.Get("Family.Withdraw"), Strings.Get("Family.WithdrawConfirm"), Strings.Get("Family.Withdraw"), Strings.Get("Action.Cancel"));

    public async Task<ConfirmPrompt> LeavePromptAsync() =>
        new(Strings.Get("Family.Leave"), await WithUnsharedAsync(Strings.Get("Family.LeaveConfirm")), Strings.Get("Family.LeaveShort"), Strings.Get("Action.Cancel"));

    public async Task<ConfirmPrompt> DeletePromptAsync()
    {
        var contents = await families.ContentsAsync(_household, CancellationToken.None);
        var main = contents.BabyNames.Count == 0
            ? Strings.Plural("Family.DeleteConfirmNoBaby", contents.EntryCount)
            : Strings.Plural("Family.DeleteConfirm", contents.EntryCount, string.Join(", ", contents.BabyNames));
        return new ConfirmPrompt(Strings.Get("Family.Delete"), await WithUnsharedAsync(main), Strings.Get("Family.DeleteShort"), Strings.Get("Action.Cancel"));
    }

    [RelayCommand]
    private Task RemoveAsync(MemberRow member) => RunAsync(async () =>
    {
        ArgumentNullException.ThrowIfNull(member);
        return await remove.ExecuteAsync(_household, member.UserId, CancellationToken.None);
    });

    [RelayCommand]
    private Task WithdrawAsync(InvitationRow invitation) => RunAsync(async () =>
    {
        ArgumentNullException.ThrowIfNull(invitation);
        return await withdraw.ExecuteAsync(_household, invitation.Id, CancellationToken.None);
    });

    [RelayCommand]
    private Task InviteAsync() => RunAsync(async () =>
    {
        var result = await invite.ExecuteAsync(_household, CancellationToken.None);
        if (result.Invitation is { } created)
            await launcher.ShareTextAsync(FamilyLabels.ShareText(created, clock.Is24Hour), Strings.Get("Family.Invite"));
        return result.Action;
    });

    [RelayCommand]
    private Task LeaveAsync() => LeaveOrDeleteAsync();

    [RelayCommand]
    private Task DeleteAsync() => LeaveOrDeleteAsync();

    /// <summary>Leave and Delete are one call (§8.3); the person has just confirmed, so <c>confirm: true</c>.</summary>
    private async Task LeaveOrDeleteAsync()
    {
        var left = false;
        await RunAsync(async () =>
        {
            var action = await leave.ExecuteAsync(_household, confirm: true, CancellationToken.None);
            left = action.Succeeded;
            return action;
        }, reload: false);
        if (left)
            Closed?.Invoke(this, EventArgs.Empty);
    }

    private async Task<string> WithUnsharedAsync(string message)
    {
        var unshared = (await families.ContentsAsync(_household, CancellationToken.None)).Unshared.Count;
        return unshared == 0 ? message : message + "\n\n" + Strings.Plural("Family.Unshared", unshared);
    }

    private async Task RunAsync(Func<Task<FamilyAction>> action, bool reload = true)
    {
        if (IsBusy)
            return;
        IsBusy = true;
        Toast = "";
        try
        {
            var result = await action();
            if (!result.Succeeded)
                Toast = result.Code is { } code ? Strings.Error(code) : Strings.Get("Family.ActionFailed");
        }
#pragma warning disable CA1031 // A failed local write must say so, never crash the page.
        catch (Exception)
#pragma warning restore CA1031
        {
            Toast = Strings.Get("Family.ActionFailed");
        }
        finally
        {
            IsBusy = false;
        }

        if (reload)
            await LoadAsync(_household);
    }
}
```

Register `services.AddTransient<Settings.FamilyPageViewModel>();`.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Coo.Mobile.Presentation.Tests --filter "FullyQualifiedName~FamilyPageTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Mobile/Coo.Mobile.Presentation tests/Coo.Mobile.Presentation.Tests
git commit -m "Presentation: the Family page: members, remove, invitations, withdraw, leave or delete (Spec 011b §2, §8)"
```

---

### Task 21: The join flow, "Enter a code", and bringing entries over (`JoinViewModel`, `EnterCodeViewModel`, `FamilyBringOverViewModel`)

**Files:**
- Create: `src/Mobile/Coo.Mobile.Presentation/Settings/JoinViewModel.cs`, `src/Mobile/Coo.Mobile.Presentation/Settings/EnterCodeViewModel.cs`,
  `src/Mobile/Coo.Mobile.Presentation/Settings/FamilyBringOverViewModel.cs`
- Modify: `src/Mobile/Coo.Mobile.Presentation/ServiceCollectionExtensions.cs`
- Test: `tests/Coo.Mobile.Presentation.Tests/JoinViewModelTests.cs`

**Interfaces:**
- Consumes: `JoinFamily` (Task 16, with `NeedsSignInAsync` and `JoinChoice.Source`), `BringFamilyEntriesOver`, `LeaveFamily`, `Families`, `SignIn`, `IConsentPrompt`,
  `BabyMapping`/`BabyOption` (existing, `BringOverViewModel.cs`), `FamilyLabels`.
- Produces:
  - `enum JoinStep { Loading, SignIn, Invitation, Message, Choice }`; `JoinViewModel(JoinFamily join, SignIn signIn, IConsentPrompt consent, IClockFormat clock, FamilyBringOverViewModel bringOver)`:
    `StartAsync()`, `ShowMalformed()`, properties `Step`, `Title`, `Body`, `Babies`, `Message`, `CanRetry`, `IsBusy`, `Toast`,
    `BringOver`; commands `SignInCommand`, `NotNowCommand`, `JoinCommand`, `RetryCommand`, `OkCommand`; event `Finished` (`EventHandler<string>`, the toast).
  - `EnterCodeViewModel(JoinFamily join)`: `Code`, `Error`, `ContinueCommand`, event `Accepted`.
  - `BringOverRequest(HouseholdId Source, BringOverChoice Choice, string FamilyLine, string TargetName, Func<IReadOnlyDictionary<BabyId, BabyId?>, CancellationToken, Task<BringOutcome>> Execute, Func<CancellationToken, Task>? Keep, string DoneToast)`;
    `enum BringOverStep { Choice, Mapping, OldFamily }`; `FamilyBringOverViewModel(LeaveFamily leave)`: `Start(BringOverRequest)`, properties `Step`,
    `Summary`, `FamilyLine`, `Mappings`, `CanKeep`, `Question`, `QuestionNote`, `AnswerText`, `IsBusy`, `Toast`; commands `BringCommand`,
    `ConfirmCommand`, `KeepCommand`, `AnswerCommand`, `KeepOldCommand`; event `Finished` (`EventHandler<string>`).

- [ ] **Step 1: Write the failing tests**

`tests/Coo.Mobile.Presentation.Tests/JoinViewModelTests.cs`:

```csharp
using Coo.Contracts.Households;
using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;
using Coo.Domain.Households;
using Coo.Mobile.Application.Auth;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Application.Households;
using Coo.Mobile.Application.Preferences;
using Coo.Mobile.Application.Sync;
using Coo.Mobile.Presentation.Localization;
using Coo.Mobile.Presentation.Settings;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Coo.Mobile.Presentation.Tests;

/// <summary>Spec 011b §5–§6 as the person sees them: each step, each answer, each Not now.</summary>
[Collection(nameof(StringsState))]
public sealed class JoinViewModelTests : IDisposable
{
    private static readonly UserId Me = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000c1"));
    private static readonly HouseholdId Home = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a1"));
    private static readonly HouseholdId Joined = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a2"));
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
    private static readonly DeviceId Phone = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000d1"));
    private static readonly InvitationCode Code = InvitationCode.Parse("ABCDEFGH").Value;
    private static readonly BabyId MyMaja = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000b1"));
    private static readonly BabyId TheirMaja = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000b9"));

    private readonly MemorySessions _sessions = new();
    private readonly Account _account;
    private readonly IFamilyStore _store = Substitute.For<IFamilyStore>();
    private readonly IHouseholdApi _api = Substitute.For<IHouseholdApi>();
    private readonly IHouseholdStore _households = Substitute.For<IHouseholdStore>();
    private readonly IHouseholdMoves _moves = Substitute.For<IHouseholdMoves>();
    private readonly ISyncStore _syncStore = Substitute.For<ISyncStore>();
    private readonly ISyncState _state = Substitute.For<ISyncState>();
    private readonly IFamilyRecords _records = Substitute.For<IFamilyRecords>();
    private readonly IConsentPrompt _consent = Substitute.For<IConsentPrompt>();
    private readonly IClockFormat _clock = Substitute.For<IClockFormat>();
    private readonly IVersionClock _versions = Substitute.For<IVersionClock>();

    public JoinViewModelTests()
    {
        Strings.Use(new LanguagePreference("en"), null);
        _account = new Account(_sessions);
        _clock.Is24Hour.Returns(true);
        _store.GetPendingInvitationAsync(Arg.Any<CancellationToken>()).Returns(new PendingInvitation(Code, Now));
        _store.ListAsync(Me, Arg.Any<CancellationToken>()).Returns([new FamilyMembership(Home, Role.Owner, Now, "Kasia", 1)]);
        _households.ListHouseholdsAsync(Arg.Any<CancellationToken>()).Returns([Settings(Home), Settings(Joined)]);
        _moves.ActiveHouseholdAsync(Arg.Any<CancellationToken>()).Returns(Home);
        _api.PreviewAsync(Code, Arg.Any<CancellationToken>()).Returns(new InvitationPreview(Joined, "Andrzej", ["Maja"], Role.Parent, Now.AddHours(72), false));
        _api.AcceptAsync(Code, Arg.Any<CancellationToken>()).Returns(new AcceptedInvitation(Joined, Role.Parent, AcceptOutcome.Joined));
        _syncStore.HasPulledAsync(Joined, Arg.Any<CancellationToken>()).Returns(true);
        _versions.NextAsync(Arg.Any<CancellationToken>()).Returns(new HlcStamp(1_790_000_000_000, 0, Phone));
    }

    public void Dispose()
    {
        _account.Dispose();
        Strings.Use(LanguagePreference.System, null);
    }

    private static HouseholdSettings Settings(HouseholdId id) =>
        HouseholdSettings.Create(id, VolumeUnit.Millilitres, "Europe/Amsterdam", new HlcStamp(1_790_000_000_000, 0, Phone)).Value;

    private Task SignInAsync() => _account.SignedInAsync(
        new StoredSession("a", Now, "r", new AccountProfile(Me, "Kasia", "k@example.com"), Home, JoinPending: false), CancellationToken.None);

    private JoinViewModel Join()
    {
        var families = new Families(_store, Substitute.For<IAccountApi>(), _api, _sessions, _households, _moves, _state, _records,
            new ProvisionalSettings(_versions, Substitute.For<IDeviceLocale>(), new FakeTimeProvider(Now)));
        var bring = new BringFamilyEntriesOver(_sessions, _account, _moves, _records, _store, _api, _versions, families);
        var join = new JoinFamily(_store, _api, _sessions, _account, new ConsentGate(_state, Substitute.For<IAccountApi>(), Substitute.For<IConsentApi>()),
            _moves, _households, _syncStore, Substitute.For<ISyncControl>(), families, bring, new FakeTimeProvider(Now));
        var signIn = new SignIn(Substitute.For<IAuthApi>(), Substitute.For<ISignInProvider>(), _households, Substitute.For<IDeviceIdentity>(),
            Substitute.For<IDeviceName>(), _account);
        return new JoinViewModel(join, signIn, _consent, _clock, new FamilyBringOverViewModel(new LeaveFamily(_api, families, Substitute.For<ISyncControl>())));
    }

    [Fact]
    public async Task StartAsync_SignedOut_AsksToSignInFirst()
    {
        // Act
        var model = Join();
        await model.StartAsync();

        // Assert
        model.Step.Should().Be(JoinStep.SignIn);
        JoinViewModel.SignInTitle.Should().Be("Sign in to join a family");
    }

    [Fact]
    public async Task StartAsync_SignedIn_ShowsTheInvitationWithTheInviterAndTheBaby()
    {
        // Arrange
        await SignInAsync();

        // Act
        var model = Join();
        await model.StartAsync();

        // Assert
        model.Step.Should().Be(JoinStep.Invitation);
        model.Title.Should().Be("Join Andrzej's family?");
        model.Body.Should().Be("Andrzej invited you to join as a parent. You'll both see the family's log and add to it.");
        model.Babies.Should().Be("Their family's log has: Maja.");
    }

    [Fact]
    public async Task JoinCommand_ConsentRefused_JoinsNothingAndSaysWhenItCanStillBeUsed()
    {
        // Arrange: §5.1 step 3.
        await SignInAsync();
        _state.GetConsentAsync(Me, Arg.Any<CancellationToken>()).Returns(ConsentState.Declined);
        _consent.AskAsync().Returns(false);
        var model = Join();
        await model.StartAsync();

        // Act
        await model.JoinCommand.ExecuteAsync(null);

        // Assert
        await _api.DidNotReceiveWithAnyArgs().AcceptAsync(default!, default);
        model.Step.Should().Be(JoinStep.Message);
        model.Message.Should().StartWith("You can join later with the same link, until ");
        await _store.Received(1).SetPendingInvitationAsync(null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task JoinCommand_NothingToBring_FinishesWithTheJoinedToast()
    {
        // Arrange
        await SignInAsync();
        _state.GetConsentAsync(Me, Arg.Any<CancellationToken>()).Returns(ConsentState.Given);
        _store.GetJoiningAsync(Arg.Any<CancellationToken>()).Returns(null, new JoiningMarker(Joined, Home, "Andrzej"));
        _moves.HasEverSyncedAsync(Home, Arg.Any<CancellationToken>()).Returns(true);
        _moves.SummaryAsync(Home, Arg.Any<CancellationToken>()).Returns(new LocalSummary(0, []));
        _moves.SummaryAsync(Joined, Arg.Any<CancellationToken>()).Returns(new LocalSummary(3, [new LocalBaby(TheirMaja, "Maja")]));
        var model = Join();
        string? toast = null;
        model.Finished += (_, t) => toast = t;
        await model.StartAsync();

        // Act
        await model.JoinCommand.ExecuteAsync(null);

        // Assert
        toast.Should().Be("You've joined Andrzej's family.");
    }

    [Fact]
    public async Task JoinCommand_NoAnswer_OffersTryAgainAndKeepsTheInvitation()
    {
        // Arrange: §5.2.
        await SignInAsync();
        _state.GetConsentAsync(Me, Arg.Any<CancellationToken>()).Returns(ConsentState.Given);
        _api.AcceptAsync(Code, Arg.Any<CancellationToken>()).ThrowsAsync(new HouseholdUnavailableException("offline"));
        var model = Join();
        await model.StartAsync();

        // Act
        await model.JoinCommand.ExecuteAsync(null);

        // Assert
        model.Step.Should().Be(JoinStep.Message);
        model.Message.Should().Be("That didn't work. Check your connection and try again.");
        model.CanRetry.Should().BeTrue();
        await _store.DidNotReceive().SetPendingInvitationAsync(null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartAsync_InvalidCode_SaysItCannotBeUsed()
    {
        // Arrange
        await SignInAsync();
        _api.PreviewAsync(Code, Arg.Any<CancellationToken>()).ThrowsAsync(new HouseholdRefusedException(HouseholdErrors.InvitationInvalid));

        // Act
        var model = Join();
        await model.StartAsync();

        // Assert
        model.Message.Should().Be("This invitation can't be used. It may have expired, been used or been withdrawn. Ask for a new one.");
        model.CanRetry.Should().BeFalse();
    }

    [Fact]
    public async Task JoinCommand_SyncedSourceWithEntries_AsksThenCopiesThenAsksToDeleteTheOldFamily()
    {
        // Arrange: §6.2 end to end in the view models.
        await SignInAsync();
        _state.GetConsentAsync(Me, Arg.Any<CancellationToken>()).Returns(ConsentState.Given);
        _store.GetJoiningAsync(Arg.Any<CancellationToken>()).Returns(null, new JoiningMarker(Joined, Home, "Andrzej"));
        _moves.HasEverSyncedAsync(Home, Arg.Any<CancellationToken>()).Returns(true);
        _moves.SummaryAsync(Home, Arg.Any<CancellationToken>()).Returns(new LocalSummary(42, [new LocalBaby(MyMaja, "Maja")]));
        _moves.SummaryAsync(Joined, Arg.Any<CancellationToken>()).Returns(new LocalSummary(3, [new LocalBaby(TheirMaja, "Maja")]));
        _records.CopyAsync(default!, default, default, default).ReturnsForAnyArgs(new CopyResult(40, 2));
        var model = Join();
        await model.StartAsync();

        // Act
        await model.JoinCommand.ExecuteAsync(null);
        var summary = model.BringOver.Summary;
        await model.BringOver.BringCommand.ExecuteAsync(null);
        await model.BringOver.ConfirmCommand.ExecuteAsync(null);

        // Assert
        model.Step.Should().Be(JoinStep.Choice);
        summary.Should().Be("This phone has 42 entries for Maja.");
        model.BringOver.Step.Should().Be(BringOverStep.OldFamily);
        model.BringOver.Question.Should().Be("Your entries are now in Andrzej's family too. Delete your old family? Its 42 entries for Maja are deleted from Coo's server and this phone. The copies stay.");
        model.BringOver.QuestionNote.Should().Be("2 entries can't be brought over and are deleted with it.");
        model.BringOver.AnswerText.Should().Be("Delete old family");
    }

    [Fact]
    public async Task ContinueCommand_EnterAMalformedCode_SaysHowACodeLooksAndSendsNothing()
    {
        // Arrange
        var model = new EnterCodeViewModel(new JoinFamily(_store, _api, _sessions, _account,
            new ConsentGate(_state, Substitute.For<IAccountApi>(), Substitute.For<IConsentApi>()), _moves, _households, _syncStore,
            Substitute.For<ISyncControl>(), null!, null!, new FakeTimeProvider(Now))) { Code = "ABCD-EFG" };
        var accepted = 0;
        model.Accepted += (_, _) => accepted++;

        // Act
        await model.ContinueCommand.ExecuteAsync(null);

        // Assert
        model.Error.Should().Be("Check the code: it's 8 letters and numbers, like ABCD-EFGH.");
        accepted.Should().Be(0);
        await _store.DidNotReceiveWithAnyArgs().SetPendingInvitationAsync(default, default);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Coo.Mobile.Presentation.Tests --filter "FullyQualifiedName~JoinViewModelTests"`
Expected: FAIL to compile.

- [ ] **Step 3: Write the implementation**

`src/Mobile/Coo.Mobile.Presentation/Settings/FamilyBringOverViewModel.cs`:

```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Coo.Domain.Common;
using Coo.Domain.Households;
using Coo.Mobile.Application.Households;
using Coo.Mobile.Application.Sync;
using Coo.Mobile.Presentation.Localization;

namespace Coo.Mobile.Presentation.Settings;

/// <param name="Execute">Brings the entries over (the join's <c>JoinFamily.BringAsync</c>, or <c>BringFamilyEntriesOver.ExecuteAsync</c> from Settings).</param>
/// <param name="Keep">"Keep them separate" in the join flow; null in Settings, where Back cancels.</param>
/// <param name="DoneToast">What a move says when it is done.</param>
public sealed record BringOverRequest(
    HouseholdId Source, BringOverChoice Choice, string FamilyLine, string TargetName,
    Func<IReadOnlyDictionary<BabyId, BabyId?>, CancellationToken, Task<BringOutcome>> Execute, Func<CancellationToken, Task>? Keep, string DoneToast);

public enum BringOverStep
{
    Choice,
    Mapping,
    OldFamily,
}

/// <summary>
/// "This phone has 42 entries for Maja." → Bring my entries over (each baby mapped) or Keep them separate → after a copy,
/// the question about the old family (Spec 011b §6). Confirm is busy-guarded; the copy's marker makes a second one a no-op.
/// </summary>
public sealed partial class FamilyBringOverViewModel(LeaveFamily leave) : ObservableObject
{
    private BringOverRequest? _request;

    [ObservableProperty]
    private BringOverStep _step;

    [ObservableProperty]
    private string _summary = "";

    [ObservableProperty]
    private string _familyLine = "";

    [ObservableProperty]
    private IReadOnlyList<BabyMapping> _mappings = [];

    [ObservableProperty]
    private bool _canKeep;

    [ObservableProperty]
    private string _question = "";

    [ObservableProperty]
    private string _questionNote = "";

    [ObservableProperty]
    private string _answerText = "";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _toast = "";

    /// <summary>The flow is over; the argument is the toast to say ("" for none).</summary>
    public event EventHandler<string>? Finished;

    public static string BringText => Strings.Get("BringOver.Bring");

    public static string KeepText => Strings.Get("BringOver.Keep");

    public static string ConfirmText => Strings.Get("BringOver.Confirm");

    public static string KeepOldText => Strings.Get("Join.KeepIt");

    public void Start(BringOverRequest request)
    {
        _request = request ?? throw new ArgumentNullException(nameof(request));
        var choice = request.Choice;
        Summary = Strings.Plural("BringOver.Entries", choice.Local.EntryCount, string.Join(", ", choice.Local.Babies.Select(b => b.Name)));
        FamilyLine = request.FamilyLine;
        IReadOnlyList<BabyOption> options = [.. choice.Family.Select(b => new BabyOption(b.Id, b.Name)), new BabyOption(null, Strings.Get("BringOver.AddNew"))];
        Mappings = [.. choice.Local.Babies.Select(b => new BabyMapping(b, options, DefaultFor(b, choice)))];
        CanKeep = request.Keep is not null;
        (Question, QuestionNote, AnswerText, Toast) = ("", "", "", "");
        Step = BringOverStep.Choice;
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
            Step = BringOverStep.Mapping;
    }

    [RelayCommand]
    private async Task ConfirmAsync()
    {
        if (IsBusy || _request is not { } request)
            return;
        IsBusy = true;
        Toast = "";
        try
        {
            var mapping = Mappings.ToDictionary(m => m.Local.Id, m => m.Options[Math.Clamp(m.Selected, 0, m.Options.Count - 1)].Target);
            var outcome = await request.Execute(mapping, CancellationToken.None);
            switch (outcome)
            {
                case { Path: BringPath.Copied, Question: OldFamilyQuestion.DeleteOld }:
                    Question = Strings.Plural("Join.DeleteOld", request.Choice.Local.EntryCount, request.TargetName,
                        string.Join(", ", request.Choice.Local.Babies.Select(b => b.Name)));
                    QuestionNote = outcome.Skipped > 0 ? Strings.Plural("Join.Skipped", outcome.Skipped) : "";
                    AnswerText = Strings.Get("Join.DeleteOldButton");
                    Step = BringOverStep.OldFamily;
                    break;
                case { Path: BringPath.Copied, Question: OldFamilyQuestion.LeaveOld }:
                    Question = Strings.Format("Join.LeaveOld", request.TargetName);
                    QuestionNote = "";
                    AnswerText = Strings.Get("Family.LeaveShort");
                    Step = BringOverStep.OldFamily;
                    break;
                case { Path: BringPath.Copied }:
                    Finished?.Invoke(this, Strings.Format("Join.Copied", request.TargetName));
                    break;
                default:
                    Finished?.Invoke(this, request.DoneToast);
                    break;
            }
        }
#pragma warning disable CA1031 // The transaction rolled back: say so; never crash the sheet.
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
    private async Task KeepAsync()
    {
        if (IsBusy || _request?.Keep is not { } keep)
            return;
        IsBusy = true;
        try
        {
            await keep(CancellationToken.None);
            Finished?.Invoke(this, Strings.Get("Join.BothFamilies"));
        }
#pragma warning disable CA1031 // Keeping only ends the flow; a failure leaves the joining marker, and the flow opens again.
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

    /// <summary>"Delete old family" or "Leave": §8.3's leave with <c>confirm: true</c>.</summary>
    [RelayCommand]
    private async Task AnswerAsync()
    {
        if (IsBusy || _request is not { } request)
            return;
        IsBusy = true;
        Toast = "";
        try
        {
            var action = await leave.ExecuteAsync(request.Source, confirm: true, CancellationToken.None);
            if (action.Succeeded)
                Finished?.Invoke(this, "");
            else
                Toast = action.Code is { } code ? Strings.Error(code) : Strings.Get("Family.ActionFailed");
        }
#pragma warning disable CA1031 // Say it did not work; the person may try again or keep the old family.
        catch (Exception)
#pragma warning restore CA1031
        {
            Toast = Strings.Get("Family.ActionFailed");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void KeepOld() => Finished?.Invoke(this, "");
}
```

`src/Mobile/Coo.Mobile.Presentation/Settings/JoinViewModel.cs`:

```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Coo.Mobile.Application.Auth;
using Coo.Mobile.Application.Households;
using Coo.Mobile.Application.Preferences;
using Coo.Mobile.Presentation.Localization;

namespace Coo.Mobile.Presentation.Settings;

public enum JoinStep
{
    Loading,
    SignIn,
    Invitation,
    Message,
    Choice,
}

/// <summary>
/// The join flow (Spec 011b §5.1): sign in if needed, the invitation, consent if needed (the host's sheet), accept, then
/// §6's choice through <see cref="BringOver"/>. Not now anywhere clears the pending invitation and changes nothing on the
/// server. The page closes on <see cref="Finished"/>; its argument is the toast.
/// </summary>
public sealed partial class JoinViewModel : ObservableObject
{
    private enum Retry
    {
        None,
        Preview,
        Accept,
        Decide,
    }

    private readonly JoinFamily _join;
    private readonly SignIn _signIn;
    private readonly IConsentPrompt _consent;
    private readonly IClockFormat _clock;
    private InvitationPreview? _preview;
    private Retry _retry;
    private string _okToast = "";

    [ObservableProperty]
    private JoinStep _step = JoinStep.Loading;

    [ObservableProperty]
    private string _title = "";

    [ObservableProperty]
    private string _body = "";

    [ObservableProperty]
    private string _babies = "";

    [ObservableProperty]
    private string _message = "";

    [ObservableProperty]
    private bool _canRetry;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _toast = "";

    public JoinViewModel(JoinFamily join, SignIn signIn, IConsentPrompt consent, IClockFormat clock, FamilyBringOverViewModel bringOver)
    {
        (_join, _signIn, _consent, _clock) = (join, signIn, consent, clock);
        BringOver = bringOver;
        BringOver.Finished += (_, toast) => Finished?.Invoke(this, toast);
    }

    public FamilyBringOverViewModel BringOver { get; }

    public event EventHandler<string>? Finished;

    public static string SignInTitle => Strings.Get("Join.SignInTitle");

    public static string SignInBody => Strings.Get("Join.SignInBody");

    public static string SignInText => Strings.Get("Account.SignIn");

    public static string NotNowText => Strings.Get("Consent.NotNow");

    public static string JoinText => Strings.Get("Join.Join");

    public static string OkText => Strings.Get("Join.OK");

    public static string TryAgainText => Strings.Get("Join.TryAgain");

    /// <summary>Opens at the invitation, at the choice (the app was closed after Join), or finishes when nothing is pending.</summary>
    public async Task StartAsync()
    {
        switch (await _join.StageAsync(CancellationToken.None))
        {
            case JoinStage.Choice:
                await DecideAsync();
                break;
            case JoinStage.Invitation:
                if (await _join.NeedsSignInAsync(CancellationToken.None))
                    Step = JoinStep.SignIn;
                else
                    await PreviewAsync();
                break;
            default:
                Finished?.Invoke(this, "");
                break;
        }
    }

    /// <summary>A link whose path holds no code (spec §4.1).</summary>
    public void ShowMalformed() => ShowMessage(Strings.Error("invitation.invalid"), Retry.None);

    [RelayCommand]
    private async Task SignInAsync()
    {
        if (IsBusy)
            return;
        IsBusy = true;
        Toast = "";
        try
        {
            switch (await _signIn.ExecuteAsync(CancellationToken.None))
            {
                case SignInResult.SignedIn:
                    await PreviewAsync();
                    break;
                case SignInResult.Failed:
                    Toast = Strings.Get("Account.SignInFailed");
                    break;
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task NotNowAsync()
    {
        try
        {
            await _join.DeclineAsync(CancellationToken.None);
        }
#pragma warning disable CA1031 // Not now only forgets a code; the sheet closes regardless.
        catch (Exception)
#pragma warning restore CA1031
        {
        }

        Finished?.Invoke(this, "");
    }

    [RelayCommand]
    private async Task JoinAsync()
    {
        if (IsBusy || _preview is not { } preview)
            return;
        IsBusy = true;
        try
        {
            if (!await _join.HasConsentAsync(CancellationToken.None) && !await _consent.AskAsync())
            {
                await DeclineForConsentAsync(preview);
                return;
            }

            await AcceptAsync(preview, consentAsked: false);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RetryAsync()
    {
        if (IsBusy)
            return;
        IsBusy = true;
        try
        {
            switch (_retry)
            {
                case Retry.Preview:
                    await PreviewAsync();
                    break;
                case Retry.Accept when _preview is { } preview:
                    await AcceptAsync(preview, consentAsked: false);
                    break;
                case Retry.Decide:
                    await DecideAsync();
                    break;
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Ok() => Finished?.Invoke(this, _okToast);

    private async Task PreviewAsync()
    {
        var result = await _join.PreviewAsync(CancellationToken.None);
        switch (result.Status)
        {
            case PreviewStatus.Ready:
                _preview = result.Invitation!;
                Title = Strings.Format("Join.Title", _preview.InviterName);
                Body = Strings.Format("Join.Body", _preview.InviterName);
                Babies = _preview.BabyNames.Count == 0 ? Strings.Get("Join.NoBaby") : Strings.Format("Join.Babies", string.Join(", ", _preview.BabyNames));
                Step = JoinStep.Invitation;
                break;
            case PreviewStatus.AlreadyMember:
                ShowMessage(Strings.Get("Join.AlreadyMember"), Retry.None);
                break;
            case PreviewStatus.Invalid:
                ShowMessage(Strings.Error("invitation.invalid"), Retry.None);
                break;
            case PreviewStatus.RateLimited:
                ShowMessage(Strings.Error("auth.rate-limited"), Retry.None);
                break;
            case PreviewStatus.Unavailable:
                ShowMessage(Strings.Get("Family.ActionFailed"), Retry.Preview);
                break;
            default:
                Finished?.Invoke(this, "");
                break;
        }
    }

    private async Task AcceptAsync(InvitationPreview preview, bool consentAsked)
    {
        var accepted = await _join.AcceptAsync(preview.InviterName, CancellationToken.None);
        switch (accepted.Status)
        {
            case AcceptStatus.Joined:
                await DecideAsync();
                break;
            case AcceptStatus.AlreadyMember:
                ShowMessage(Strings.Get("Join.AlreadyMember"), Retry.None);
                break;
            case AcceptStatus.NeedsConsent when !consentAsked && await _consent.AskAsync():
                await AcceptAsync(preview, consentAsked: true);
                break;
            case AcceptStatus.NeedsConsent:
                await DeclineForConsentAsync(preview);
                break;
            case AcceptStatus.Invalid:
                ShowMessage(Strings.Error("invitation.invalid"), Retry.None);
                break;
            case AcceptStatus.RateLimited:
                ShowMessage(Strings.Error("auth.rate-limited"), Retry.None);
                break;
            case AcceptStatus.Unavailable:
                ShowMessage(Strings.Get("Family.ActionFailed"), Retry.Accept);
                break;
            default:
                Finished?.Invoke(this, "");
                break;
        }
    }

    private async Task DecideAsync()
    {
        var choice = await _join.DecideAsync(CancellationToken.None);
        switch (choice.Status)
        {
            case ChoiceStatus.NothingAsked:
                Finished?.Invoke(this, choice.InviterName.Length == 0 ? "" : Strings.Format("Join.Joined", choice.InviterName));
                break;
            case ChoiceStatus.NotReady:
                ShowMessage(Strings.Get("Family.RefreshFailed"), Retry.Decide);
                break;
            default:
                var family = choice.Offer!.Family;
                BringOver.Start(new BringOverRequest(
                    choice.Source,
                    choice.Offer,
                    family.Count == 0 ? Strings.Get("Join.NoBaby") : Strings.Format("Join.Babies", string.Join(", ", family.Select(b => b.Name))),
                    choice.InviterName,
                    _join.BringAsync,
                    _join.KeepSeparateAsync,
                    Strings.Format("Join.Joined", choice.InviterName)));
                Step = JoinStep.Choice;
                break;
        }
    }

    private async Task DeclineForConsentAsync(InvitationPreview preview)
    {
        await _join.DeclineAsync(CancellationToken.None);
        ShowMessage(Strings.Format("Join.Later", FamilyLabels.Until(preview.ExpiresAt, _clock.Is24Hour)), Retry.None);
    }

    private void ShowMessage(string message, Retry retry)
    {
        (Message, _retry, CanRetry, _okToast) = (message, retry, retry != Retry.None, "");
        Step = JoinStep.Message;
    }
}
```

(`choice.Source` is the joining marker's source, Task 16: the family whose entries are offered and which "Delete old
family"/"Leave" is about.)

`src/Mobile/Coo.Mobile.Presentation/Settings/EnterCodeViewModel.cs`:

```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Coo.Mobile.Application.Households;
using Coo.Mobile.Presentation.Localization;

namespace Coo.Mobile.Presentation.Settings;

/// <summary>"Join a family" · "Enter the code from your invitation." (spec §4.4). A malformed code is refused without a request.</summary>
public sealed partial class EnterCodeViewModel(JoinFamily join) : ObservableObject
{
    [ObservableProperty]
    private string _code = "";

    [ObservableProperty]
    private string _error = "";

    /// <summary>The code is the pending invitation now: the host opens the join flow.</summary>
    public event EventHandler? Accepted;

    public static string Title => Strings.Get("Family.Join");

    public static string Hint => Strings.Get("Join.EnterHint");

    public static string ContinueText => Strings.Get("Setup.Continue");

    [RelayCommand]
    private async Task ContinueAsync()
    {
        if (await join.ReceiveAsync(Code, CancellationToken.None))
        {
            Error = "";
            Accepted?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            Error = Strings.Get("Join.Malformed");
        }
    }
}
```

Register in Presentation's `ServiceCollectionExtensions`:

```csharp
        services.AddTransient<Settings.FamilyBringOverViewModel>();
        services.AddTransient<Settings.JoinViewModel>();
        services.AddTransient<Settings.EnterCodeViewModel>();
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Coo.Mobile.Presentation.Tests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Mobile/Coo.Mobile.Presentation tests/Coo.Mobile.Presentation.Tests
git commit -m "Presentation: the join flow, Enter a code, and bringing entries over with the old family's question (Spec 011b §4.4, §5, §6)"
```

---

### Task 22: The removal sheet, Today's paused banner, and the setup step's "Enter a code"

**Files:**
- Create: `src/Mobile/Coo.Mobile.Presentation/Settings/RemovalViewModel.cs`, `src/Mobile/Coo.Mobile.Presentation/Today/PausedSection.cs`
- Modify: `src/Mobile/Coo.Mobile.Presentation/Setup/SetupViewModel.cs`, `src/Mobile/Coo.Mobile.Presentation/ServiceCollectionExtensions.cs`
- Test: `tests/Coo.Mobile.Presentation.Tests/RemovalTests.cs`

**Interfaces:**
- Consumes: `ISyncStatus`, `SyncEngine.AcknowledgeRemovalAsync`, `Families.ContentsAsync/IsShownPausedAloneAsync`,
  `IExternalLauncher.ShareTextAsync`, `Formats`, `IHomeSection`.
- Produces:
  - `RemovalViewModel(ISyncStatus status, SyncEngine engine, Families families, IExternalLauncher launcher, IClockFormat clock, TimeProvider time)`:
    `LoadAsync()`, `Body`, `Unshared` (the middle paragraph, empty when none), `Lines`, `IsBusy`, `Toast`, `ShareCommand`,
    `OkCommand`, event `Closed`; `static string Title`, `ShareText`, `OkText`.
  - `PausedSection(Families families, ISyncStatus status) : IHomeSection` with `Order = 5`, `string Text` (empty when not paused).
  - `SetupViewModel.EnterCodeRequested` event and `EnterCodeCommand`; `static string HaveInvitationText`.

- [ ] **Step 1: Write the failing tests**

`tests/Coo.Mobile.Presentation.Tests/RemovalTests.cs`:

```csharp
using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Kinds.Feeding;
using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;
using Coo.Domain.Households;
using Coo.Mobile.Application.Auth;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Application.Households;
using Coo.Mobile.Application.Preferences;
using Coo.Mobile.Application.Sync;
using Coo.Mobile.Presentation.Localization;
using Coo.Mobile.Presentation.Settings;
using Coo.Mobile.Presentation.Today;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Coo.Mobile.Presentation.Tests;

/// <summary>Spec 011b §8.2: the removal sheet and Today's paused banner.</summary>
[Collection(nameof(StringsState))]
public sealed class RemovalTests : IDisposable
{
    private static readonly HouseholdId Joined = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a2"));
    private static readonly DeviceId Phone = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000d1"));
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 1, 12, 0, TimeSpan.Zero);

    private readonly ISyncStatus _status = Substitute.For<ISyncStatus>();
    private readonly IHouseholdStore _households = Substitute.For<IHouseholdStore>();
    private readonly IHouseholdMoves _moves = Substitute.For<IHouseholdMoves>();
    private readonly IFamilyRecords _records = Substitute.For<IFamilyRecords>();
    private readonly ISyncState _state = Substitute.For<ISyncState>();
    private readonly IExternalLauncher _launcher = Substitute.For<IExternalLauncher>();
    private readonly IClockFormat _clock = Substitute.For<IClockFormat>();

    public RemovalTests()
    {
        Strings.Use(new LanguagePreference("en"), null);
        _clock.Is24Hour.Returns(true);
        _status.Status.Returns(SyncStatus.Initial with { Removal = Joined });
        _households.ListHouseholdsAsync(Arg.Any<CancellationToken>()).Returns([
            HouseholdSettings.Create(Joined, VolumeUnit.Millilitres, "Europe/Amsterdam", new HlcStamp(1_790_000_000_000, 0, Phone)).Value]);
        _moves.SummaryAsync(Joined, Arg.Any<CancellationToken>()).Returns(new LocalSummary(10, [new LocalBaby(new BabyId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000b1")), "Maja")]));
        _records.ListUnsharedEntriesAsync(Joined, Arg.Any<CancellationToken>()).Returns([]);
    }

    public void Dispose() => Strings.Use(LanguagePreference.System, null);

    private Families Families() => new(Substitute.For<IFamilyStore>(), Substitute.For<IAccountApi>(), Substitute.For<IHouseholdApi>(),
        Substitute.For<ISessionStore>(), _households, _moves, _state, _records,
        new ProvisionalSettings(Substitute.For<IVersionClock>(), Substitute.For<IDeviceLocale>(), new FakeTimeProvider(Now)));

    private RemovalViewModel Removal() => new(_status, null!, Families(), _launcher, _clock, new FakeTimeProvider(Now));

    private static LiveEntry Bottle()
    {
        var entry = CareEntry.Create(new EntryId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000f1")), Joined, EntrySubject.Baby(new BabyId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000b1"))),
            BottleFeed.Of(Milk.Formula, Volume.FromMillilitres(120).Value).Value, EntryTiming.Point(Now), PersonRef.ThisDevicesPerson, EntrySource.Manual,
            new HlcStamp(1_790_000_000_000, 1, Phone), Now).Value;
        entry.SetNote(Note.From("night feed").Value, new HlcStamp(1_790_000_000_000, 2, Phone));
        return new LiveEntry(entry);
    }

    [Fact]
    public async Task LoadAsync_NothingUnshared_SaysWhoRemovedAndShowsNoMiddleParagraph()
    {
        // Act
        var model = Removal();
        await model.LoadAsync();

        // Assert
        RemovalViewModel.Title.Should().Be("You were removed from this family");
        model.Body.Should().Be("Someone in the family that logs Maja removed you, so its log will be removed from this phone. Entries you already shared stay with the family.");
        model.Unshared.Should().BeEmpty();
    }

    [Fact]
    public async Task ShareCommand_OneUnsharedBottle_SharesOneLineInThePersonsLanguageAndClock()
    {
        // Arrange
        _records.ListUnsharedEntriesAsync(Joined, Arg.Any<CancellationToken>()).Returns([Bottle()]);
        var model = Removal();
        await model.LoadAsync();

        // Act
        await model.ShareCommand.ExecuteAsync(null);

        // Assert
        model.Unshared.Should().Be("1 entry you logged wasn't shared yet.");
        var at = Now.ToLocalTime();
        await _launcher.Received(1).ShareTextAsync(
            $"{at.ToString("ddd d MMM", Strings.Culture)}, {at:HH:mm} · Bottle · Formula · 120 ml · night feed", "You were removed from this family");
    }

    [Fact]
    public async Task LoadAsync_NoRemovalPending_ClosesAtOnce()
    {
        // Arrange
        _status.Status.Returns(SyncStatus.Initial);
        var model = Removal();
        var closed = 0;
        model.Closed += (_, _) => closed++;

        // Act
        await model.LoadAsync();

        // Assert
        closed.Should().Be(1);
    }

    [Fact]
    public async Task Text_ShownFamilyIsTheOnlyOneAndSoftLocked_SaysSharingIsPaused()
    {
        // Arrange
        _moves.ActiveHouseholdAsync(Arg.Any<CancellationToken>()).Returns(Joined);
        _households.ListBabiesAsync(default, default).ReturnsForAnyArgs([]);
        _state.IsSoftLockedAsync(Joined, Arg.Any<CancellationToken>()).Returns(true);
        var section = new PausedSection(Families(), _status);

        // Act
        await section.RefreshAsync();

        // Assert
        section.Text.Should().Be("Sharing with this family is paused. Your entries are safe on this phone.");
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Coo.Mobile.Presentation.Tests --filter "FullyQualifiedName~RemovalTests"`
Expected: FAIL to compile.

- [ ] **Step 3: Write the implementation**

`src/Mobile/Coo.Mobile.Presentation/Settings/RemovalViewModel.cs`:

```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Application.Households;
using Coo.Mobile.Application.Preferences;
using Coo.Mobile.Application.Sync;
using Coo.Mobile.Presentation.Formatting;
using Coo.Mobile.Presentation.Localization;

namespace Coo.Mobile.Presentation.Settings;

/// <summary>
/// "You were removed from this family" (Spec 011b §8.2). Nothing is deleted before OK; Share them as text sends the entries
/// that were never shared, one line each, oldest first; Back closes without deleting (the host opens it again later).
/// </summary>
public sealed partial class RemovalViewModel(
    ISyncStatus status, SyncEngine engine, Families families, IExternalLauncher launcher, IClockFormat clock, TimeProvider time) : ObservableObject
{
    [ObservableProperty]
    private string _body = "";

    [ObservableProperty]
    private string _unshared = "";

    [ObservableProperty]
    private IReadOnlyList<string> _lines = [];

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _toast = "";

    public event EventHandler? Closed;

    public static string Title => Strings.Get("Removal.Title");

    public static string ShareText => Strings.Get("Removal.Share");

    public static string OkText => Strings.Get("Join.OK");

    public async Task LoadAsync()
    {
        if (status.Status.Removal is not { } household)
        {
            Closed?.Invoke(this, EventArgs.Empty);
            return;
        }

        var contents = await families.ContentsAsync(household, CancellationToken.None);
        Body = contents.BabyNames.Count == 0
            ? Strings.Get("Removal.BodyNoBaby")
            : Strings.Format("Removal.Body", string.Join(", ", contents.BabyNames));
        var entries = contents.Unshared.OfType<LiveEntry>().ToList();
        Unshared = entries.Count == 0 ? "" : Strings.Plural("Removal.Unshared", entries.Count);
        var formats = new Formats(clock.Is24Hour, contents.Unit);
        var now = time.GetLocalNow();
        Lines = [.. entries.Select(e =>
        {
            var at = e.Start.ToLocalTime();
            var line = Strings.Format("Removal.Line", at.ToString("ddd d MMM", Strings.Culture), formats.Time(e.Start), formats.Title(e.Entry, now));
            return e.Entry.Note is { } note ? line + " · " + note.Text : line;
        })];
    }

    [RelayCommand]
    private async Task ShareAsync()
    {
        if (Lines.Count == 0)
            return;
        try
        {
            await launcher.ShareTextAsync(string.Join("\n", Lines), Title);
        }
#pragma warning disable CA1031 // The share sheet refusing must not close or break the removal sheet.
        catch (Exception)
#pragma warning restore CA1031
        {
            Toast = Strings.Get("Error.Generic");
        }
    }

    [RelayCommand]
    private async Task OkAsync()
    {
        if (IsBusy)
            return;
        IsBusy = true;
        try
        {
            await engine.AcknowledgeRemovalAsync(CancellationToken.None);
            Closed?.Invoke(this, EventArgs.Empty);
        }
#pragma warning disable CA1031 // Nothing was deleted (one transaction): say so; OK can be tapped again.
        catch (Exception)
#pragma warning restore CA1031
        {
            Toast = Strings.Get("Error.Generic");
        }
        finally
        {
            IsBusy = false;
        }
    }
}
```

`src/Mobile/Coo.Mobile.Presentation/Today/PausedSection.cs`:

```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using Coo.Mobile.Application.Households;
using Coo.Mobile.Application.Sync;
using Coo.Mobile.Presentation.Localization;
using Coo.Mobile.Presentation.Theme;

namespace Coo.Mobile.Presentation.Today;

/// <summary>
/// Today's banner (Spec 011b §8.2): only while the shown family is soft-locked and is the only family on the phone; logging
/// carries on (§15.2 Q6). It follows the engine's status itself; its PropertyChanged can come from any thread (the section
/// view dispatches).
/// </summary>
public sealed partial class PausedSection : ObservableObject, IHomeSection
{
    private readonly Families _families;

    [ObservableProperty]
    private string _text = "";

    public PausedSection(Families families, ISyncStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        _families = families;
        status.StatusChanged += (_, _) => _ = RefreshAsync();
    }

    public int Order => 5;

    public bool AppliesTo(HomeStage stage, PaletteKind palette) => true;

    public void Update(HomeSnapshot snapshot) => _ = RefreshAsync();

    public async Task RefreshAsync()
    {
        try
        {
            var paused = await _families.IsShownPausedAloneAsync(CancellationToken.None);
            Text = paused ? Strings.Get("Today.Paused") : "";
        }
#pragma warning disable CA1031 // Fire-and-forget: the banner keeps what it showed; the next status change checks again.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }
}
```

In `SetupViewModel`:

```csharp
    /// <summary>"Have an invitation? Enter a code" (spec §4.4): the host opens the code sheet, then the join flow.</summary>
    public event EventHandler? EnterCodeRequested;

    public static string HaveInvitationText => Strings.Get("Setup.HaveInvitation");

    [RelayCommand]
    private void EnterCode() => EnterCodeRequested?.Invoke(this, EventArgs.Empty);
```

Register in Presentation's `ServiceCollectionExtensions`:

```csharp
        services.AddSingleton<IHomeSection, PausedSection>();
        services.AddTransient<Settings.RemovalViewModel>();
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Coo.Mobile.Presentation.Tests`
Expected: PASS. (`TodayTests` build their own section lists, so the new section does not change them.)

- [ ] **Step 5: Commit**

```bash
git add src/Mobile/Coo.Mobile.Presentation tests/Coo.Mobile.Presentation.Tests
git commit -m "Presentation: the removal sheet with unshared entries as text, Today's paused banner, setup's Enter a code (Spec 011b §4.4, §8.2)"
```

---
### Task 23: On the phone: the App Link, the invitation inbox, the join, code and removal sheets, 0.12.0 (15)

**Files:**
- Modify: `src/Mobile/Coo.Mobile/Platforms/Android/MainActivity.cs` (intent filter with `autoVerify`, `OnNewIntent`),
  `src/Mobile/Coo.Mobile/Sync/SyncPrompter.cs` (the join owns the sheets; the removal sheet; consent on request),
  `src/Mobile/Coo.Mobile/StartupPage.cs` (a link that opened Coo; a brand-new phone; the setup step's code),
  `src/Mobile/Coo.Mobile/Settings/MauiExternalLauncher.cs` (`ShareTextAsync`), `src/Mobile/Coo.Mobile/MauiProgram.cs`,
  `src/Mobile/Coo.Mobile/Pages/SetupPage.cs` (the link), `src/Mobile/Coo.Mobile/Coo.Mobile.csproj` (0.12.0, versionCode 15)
- Create: `src/Mobile/Coo.Mobile/Joining/InvitationInbox.cs`, `src/Mobile/Coo.Mobile/Joining/MauiConsentPrompt.cs`,
  `src/Mobile/Coo.Mobile/Pages/BringOverViews.cs`, `src/Mobile/Coo.Mobile/Pages/JoinPage.cs`, `src/Mobile/Coo.Mobile/Pages/EnterCodePage.cs`,
  `src/Mobile/Coo.Mobile/Pages/RemovalPage.cs`

**Interfaces:**
- Consumes: Tasks 19–22's view models; `JoinFamily`, `ConsentGate`, `ISessionStore`, `UndoService.Say`, `ConsentPage`.
- Produces: `InvitationInbox.Offer(Uri)`, `StoreHeldAsync() → HeldLink`, `MarkReady()`; `SyncPrompter.ShowJoinAsync(bool malformed = false)`,
  `ShowEnterCodeAsync()`, `ShowRemovalAsync()`, `AskConsentAsync() → bool`, `event EventHandler? JoinEnded`; `BringOverViews.Render(...)`;
  the APK at 0.12.0 (15).

This task has no unit tests: the host and the Android callbacks are not in `Coo.Tests.slnf` (ADR 0001). Its checks are the
build, the architecture tests and the smoke run in Step 9; Task 27 exercises it fully. Every handler below is `async void`
or dispatched and **ends in a catch-all**; every event from the engine, the account or Android is **dispatched to the main
thread** before it touches a page (Global Constraints).

- [ ] **Step 1: The App Link (`MainActivity`)**

Add `using Android.Content;` and, above the class (it keeps `LaunchMode.SingleTop`, so a link while Coo runs arrives in
`OnNewIntent`, spec §4.1):

```csharp
[IntentFilter(
    new[] { Intent.ActionView },
    Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable },
    DataScheme = "https", DataHost = "api.coo.lechdigital.nl", DataPathPrefix = "/i/", AutoVerify = true)]
```

At the end of `OnCreate`: `Offer(Intent);`. Add:

```csharp
    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        Offer(intent);
    }

    /// <summary>An invitation link (spec §4.1) goes to the inbox; nothing here may escape into Android (Increment 10 lesson).</summary>
    private static void Offer(Intent? intent)
    {
        try
        {
            if (intent?.Action != Intent.ActionView || intent.Data?.ToString() is not { } text || !Uri.TryCreate(text, UriKind.Absolute, out var link))
                return;
            IPlatformApplication.Current?.Services.GetRequiredService<Joining.InvitationInbox>().Offer(link);
        }
#pragma warning disable CA1031 // An Android callback: a link that cannot be read is simply not offered.
        catch (Exception e)
#pragma warning restore CA1031
        {
            Android.Util.Log.Warn("Coo", $"Invitation link not taken: {e.GetType().Name}");
        }
    }
```

After a build, check the merged manifest has the filter with `android:autoVerify="true"`:

```sh
grep -A8 'autoVerify' src/Mobile/Coo.Mobile/obj/Debug/net10.0-android/AndroidManifest.xml
```

Expected: `<intent-filter android:autoVerify="true">` with `VIEW`, `DEFAULT`, `BROWSABLE`, `https`, `api.coo.lechdigital.nl`, `/i/`.

- [ ] **Step 2: The inbox and the consent prompt**

`src/Mobile/Coo.Mobile/Joining/InvitationInbox.cs`:

```csharp
using Coo.Mobile.Application.Households;

namespace Coo.Mobile.Joining;

public enum HeldLink
{
    None,
    Stored,
    Malformed,
}

/// <summary>
/// Where an App Link waits until the database is open (spec §4.1): <c>OnCreate</c> runs before the start-up migrations, so the
/// link is held in memory, and the start-up page stores it as the pending invitation. Once Coo is running, a link from
/// <c>OnNewIntent</c> is stored and the join flow opens at once (as soon as no other sheet shows).
/// </summary>
internal sealed class InvitationInbox(IServiceProvider services)
{
    private Uri? _held;
    private volatile bool _ready;

    /// <summary>From Android's thread; never throws.</summary>
    public void Offer(Uri link)
    {
        Interlocked.Exchange(ref _held, link);
        if (!_ready)
            return;
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            try
            {
                var held = await StoreHeldAsync();
                if (held != HeldLink.None)
                    await services.GetRequiredService<Sync.SyncPrompter>().ShowJoinAsync(malformed: held == HeldLink.Malformed);
            }
#pragma warning disable CA1031 // Fire-and-forget on the main thread: the link stays pending and the flow opens at the next start.
            catch (Exception e)
#pragma warning restore CA1031
            {
                Android.Util.Log.Warn("Coo", $"Invitation link not handled: {e.GetType().Name}");
            }
        });
    }

    /// <summary>Called by the start-up page once routing is done: later links open the flow directly.</summary>
    public void MarkReady() => _ready = true;

    /// <summary>The held link becomes the pending invitation; malformed links are reported, never stored.</summary>
    public async Task<HeldLink> StoreHeldAsync()
    {
        if (Interlocked.Exchange(ref _held, null) is not { } link)
            return HeldLink.None;
        return await services.GetRequiredService<JoinFamily>().ReceiveAsync(link.ToString(), CancellationToken.None)
            ? HeldLink.Stored
            : HeldLink.Malformed;
    }
}
```

`src/Mobile/Coo.Mobile/Joining/MauiConsentPrompt.cs`:

```csharp
using Coo.Mobile.Presentation.Settings;

namespace Coo.Mobile.Joining;

/// <summary>Spec 011 §3's sheet where the person is (inviting, joining), through the prompter so it is never shown twice.</summary>
internal sealed class MauiConsentPrompt(IServiceProvider services) : IConsentPrompt
{
    public Task<bool> AskAsync() => services.GetRequiredService<Sync.SyncPrompter>().AskConsentAsync();
}
```

In `MauiExternalLauncher`:

```csharp
    public Task ShareTextAsync(string text, string title) =>
        Share.Default.RequestAsync(new ShareTextRequest { Text = text, Title = title });
```

- [ ] **Step 3: The bring-over views (shared by the join sheet and Settings' bring-over)**

`src/Mobile/Coo.Mobile/Pages/BringOverViews.cs`:

```csharp
using Coo.Mobile.Components;
using Coo.Mobile.Presentation.Settings;

namespace Coo.Mobile.Pages;

/// <summary>Spec 011b §6's choice, mapping and old-family question, drawn from <see cref="FamilyBringOverViewModel"/>.</summary>
internal static class BringOverViews
{
    public static View Render(FamilyBringOverViewModel model, Func<Func<Task>, Task> run)
    {
        var stack = new VerticalStackLayout { Spacing = 16 };
        switch (model.Step)
        {
            case BringOverStep.Choice:
                stack.Add(Ui.Text(model.Summary, "Title"));
                stack.Add(Ui.Text(model.FamilyLine, "Body"));
                stack.Add(Button(FamilyBringOverViewModel.BringText, "PrimaryButton", () => run(() => model.BringCommand.ExecuteAsync(null))));
                if (model.CanKeep)
                    stack.Add(Button(FamilyBringOverViewModel.KeepText, "GhostButton", () => run(() => model.KeepCommand.ExecuteAsync(null))));
                break;
            case BringOverStep.Mapping:
                stack.Add(Ui.Text(model.Summary, "Title"));
                foreach (var mapping in model.Mappings)
                {
                    var picker = new Picker { ItemsSource = mapping.Options.Select(o => o.Label).ToList(), SelectedIndex = mapping.Selected };
                    picker.SelectedIndexChanged += (_, _) => mapping.Selected = picker.SelectedIndex;
                    stack.Add(Ui.Text(mapping.Prompt, "BodyStrong"));
                    stack.Add(picker);
                }

                stack.Add(Button(FamilyBringOverViewModel.ConfirmText, "PrimaryButton", () => run(() => model.ConfirmCommand.ExecuteAsync(null))));
                break;
            case BringOverStep.OldFamily:
                stack.Add(Ui.Text(model.Question, "Body"));
                if (model.QuestionNote.Length > 0)
                    stack.Add(Ui.Text(model.QuestionNote, "Body"));
                stack.Add(Button(model.AnswerText, "PrimaryButton", () => run(() => model.AnswerCommand.ExecuteAsync(null))));
                stack.Add(Button(FamilyBringOverViewModel.KeepOldText, "GhostButton", () =>
                {
                    model.KeepOldCommand.Execute(null);
                    return Task.CompletedTask;
                }));
                break;
        }

        if (model.Toast.Length > 0)
            stack.Add(Ui.Text(model.Toast, "Meta"));
        return stack;
    }

    private static Button Button(string text, string style, Func<Task> action)
    {
        var button = new Button { Text = text };
        button.SetDynamicResource(VisualElement.StyleProperty, style);
        button.Clicked += async (_, _) => await action();
        return button;
    }
}
```

(The `run` delegate is the page's catch-all runner, so no `Clicked` handler can throw.)

- [ ] **Step 4: The join sheet**

`src/Mobile/Coo.Mobile/Pages/JoinPage.cs`:

```csharp
using Coo.Mobile.Components;
using Coo.Mobile.Presentation.Localization;
using Coo.Mobile.Presentation.Settings;
using Coo.Mobile.Presentation.Today;

namespace Coo.Mobile.Pages;

/// <summary>
/// The join flow (Spec 011b §5) as one modal page whose content follows the steps. While it is on the modal stack it owns the
/// sheets (the prompter opens nothing else). Back is Not now, except at the choice, where it only closes: the joining marker
/// stays and the flow opens there again at the next start (§5.3).
/// </summary>
public sealed class JoinPage : ContentPage
{
    private readonly JoinViewModel _model;
    private readonly UndoService _undo;
    private readonly ContentView _body = new();
    private bool _started;

    public JoinPage(JoinViewModel model, UndoService undo)
    {
        (_model, _undo) = (model, undo);
        SetDynamicResource(BackgroundColorProperty, "Ground");
        Shell.SetNavBarIsVisible(this, false);
        Content = new ScrollView { Content = _body };
        model.Finished += (_, toast) => Dispatcher.Dispatch(async () => await FinishAsync(toast));
        model.PropertyChanged += (_, _) => Dispatcher.Dispatch(SafeRender);
        model.BringOver.PropertyChanged += (_, _) => Dispatcher.Dispatch(SafeRender);
    }

    /// <summary>Set by the prompter: the link that opened Coo held no code (spec §4.1).</summary>
    public bool Malformed { get; set; }

    /// <summary>The page left the modal stack, whatever ended it.</summary>
    public event EventHandler? Ended;

    protected override async void OnAppearing()
    {
#pragma warning disable CA1031 // async void: nothing may escape.
        try
        {
            base.OnAppearing();
            if (_started)
                return;
            _started = true;
            SafeRender();
            if (Malformed)
                _model.ShowMalformed();
            else
                await _model.StartAsync();
        }
        catch (Exception)
        {
            _undo.Say(Strings.Get("Error.Generic"));
            await CloseAsync();
        }
#pragma warning restore CA1031
    }

    protected override bool OnBackButtonPressed()
    {
        Dispatcher.Dispatch(async () =>
        {
            if (_model.Step == JoinStep.Choice)
                await CloseAsync();
            else
                await RunAsync(() => _model.NotNowCommand.ExecuteAsync(null));
        });
        return true;
    }

    private void SafeRender()
    {
#pragma warning disable CA1031 // A dispatched redraw: nothing may escape.
        try
        {
            Render();
        }
        catch (Exception)
        {
        }
#pragma warning restore CA1031
    }

    private void Render()
    {
        var stack = new VerticalStackLayout { Spacing = 16, Padding = new Thickness(24, 48, 24, 32) };
        switch (_model.Step)
        {
            case JoinStep.Loading:
                stack.Add(new ActivityIndicator { IsRunning = true });
                break;
            case JoinStep.SignIn:
                stack.Add(Ui.Text(JoinViewModel.SignInTitle, "Title"));
                stack.Add(Ui.Text(JoinViewModel.SignInBody, "Body"));
                stack.Add(Button(JoinViewModel.SignInText, "PrimaryButton", () => _model.SignInCommand.ExecuteAsync(null)));
                stack.Add(Button(JoinViewModel.NotNowText, "GhostButton", () => _model.NotNowCommand.ExecuteAsync(null)));
                break;
            case JoinStep.Invitation:
                stack.Add(Ui.Text(_model.Title, "Title"));
                stack.Add(Ui.Text(_model.Body, "Body"));
                stack.Add(Ui.Text(_model.Babies, "Body"));
                stack.Add(Button(JoinViewModel.JoinText, "PrimaryButton", () => _model.JoinCommand.ExecuteAsync(null)));
                stack.Add(Button(JoinViewModel.NotNowText, "GhostButton", () => _model.NotNowCommand.ExecuteAsync(null)));
                break;
            case JoinStep.Message:
                stack.Add(Ui.Text(_model.Message, "Body"));
                if (_model.CanRetry)
                    stack.Add(Button(JoinViewModel.TryAgainText, "PrimaryButton", () => _model.RetryCommand.ExecuteAsync(null)));
                stack.Add(Button(JoinViewModel.OkText, _model.CanRetry ? "GhostButton" : "PrimaryButton", () =>
                {
                    _model.OkCommand.Execute(null);
                    return Task.CompletedTask;
                }));
                break;
            case JoinStep.Choice:
                stack.Add(BringOverViews.Render(_model.BringOver, RunAsync));
                break;
        }

        if (_model.Toast.Length > 0)
            stack.Add(Ui.Text(_model.Toast, "Meta"));
        if (_model.IsBusy || _model.BringOver.IsBusy)
            stack.Add(new ActivityIndicator { IsRunning = true });
        _body.Content = stack;
    }

    private Button Button(string text, string style, Func<Task> action)
    {
        var button = new Button { Text = text };
        button.SetDynamicResource(StyleProperty, style);
        button.Clicked += async (_, _) => await RunAsync(action);
        return button;
    }

    private async Task FinishAsync(string toast)
    {
        await CloseAsync();
        if (toast.Length > 0)
            _undo.Say(toast);
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
        finally
        {
            Ended?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task RunAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
#pragma warning disable CA1031 // async void handlers: nothing may escape; the view models show their own failures.
        catch (Exception)
#pragma warning restore CA1031
        {
            _undo.Say(Strings.Get("Error.Generic"));
        }
    }
}
```

`src/Mobile/Coo.Mobile/Pages/EnterCodePage.cs`:

```csharp
using Coo.Mobile.Components;
using Coo.Mobile.Presentation.Settings;

namespace Coo.Mobile.Pages;

/// <summary>"Join a family" · "Enter the code from your invitation." (spec §4.4): capitals, no autocorrect; Continue opens the flow.</summary>
public sealed class EnterCodePage : ContentPage
{
    private readonly EnterCodeViewModel _model;

    public EnterCodePage(EnterCodeViewModel model)
    {
        _model = model;
        SetDynamicResource(BackgroundColorProperty, "Ground");
        Shell.SetNavBarIsVisible(this, false);
        var field = new Entry
        {
            Placeholder = "ABCD-EFGH",
            Keyboard = Keyboard.Create(KeyboardFlags.CapitalizeCharacter),
            IsSpellCheckEnabled = false,
            IsTextPredictionEnabled = false,
            FontFamily = "FigtreeRegular",
            FontSize = 20,
        };
        field.SetDynamicResource(Entry.TextColorProperty, "Ink");
        field.TextChanged += (_, e) => model.Code = e.NewTextValue ?? "";
        var error = Ui.Text("", "Meta", "FeedInk");
        model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(EnterCodeViewModel.Error))
                Dispatcher.Dispatch(() => error.Text = model.Error);
        };
        var go = new Button { Text = EnterCodeViewModel.ContinueText };
        go.SetDynamicResource(StyleProperty, "PrimaryButton");
        go.Clicked += async (_, _) =>
        {
            try
            {
                await model.ContinueCommand.ExecuteAsync(null);
            }
#pragma warning disable CA1031 // async void: nothing may escape; a failed local write leaves the sheet open.
            catch (Exception)
#pragma warning restore CA1031
            {
            }
        };
        model.Accepted += (_, _) => Dispatcher.Dispatch(async () => await AcceptedAsync());
        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Spacing = 16,
                Padding = new Thickness(24, 48, 24, 32),
                Children = { Ui.Text(EnterCodeViewModel.Title, "Title"), Ui.Text(EnterCodeViewModel.Hint, "Body"), field, error, go },
            },
        };
    }

    /// <summary>Raised once the code is stored; the prompter then opens the join sheet.</summary>
    public event EventHandler? CodeAccepted;

    private async Task AcceptedAsync()
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

        CodeAccepted?.Invoke(this, EventArgs.Empty);
    }
}
```

`src/Mobile/Coo.Mobile/Pages/RemovalPage.cs`:

```csharp
using Coo.Mobile.Components;
using Coo.Mobile.Presentation.Localization;
using Coo.Mobile.Presentation.Settings;

namespace Coo.Mobile.Pages;

/// <summary>
/// "You were removed from this family" (spec §8.2), over whatever shows. Nothing is deleted before OK. Back closes without
/// deleting; the prompter opens it again at the next start or resume. The share sheet keeps this page open.
/// </summary>
public sealed class RemovalPage : ContentPage
{
    private readonly RemovalViewModel _model;
    private readonly ContentView _body = new();
    private bool _loaded;

    public RemovalPage(RemovalViewModel model)
    {
        _model = model;
        SetDynamicResource(BackgroundColorProperty, "Ground");
        Shell.SetNavBarIsVisible(this, false);
        Content = new ScrollView { Content = _body };
        model.Closed += (_, _) => Dispatcher.Dispatch(async () => await CloseAsync());
        model.PropertyChanged += (_, _) => Dispatcher.Dispatch(SafeRender);
    }

    protected override async void OnAppearing()
    {
#pragma warning disable CA1031 // async void: nothing may escape.
        try
        {
            base.OnAppearing();
            if (_loaded)
                return;
            _loaded = true;
            await _model.LoadAsync();
            SafeRender();
        }
        catch (Exception)
        {
            await CloseAsync();
        }
#pragma warning restore CA1031
    }

    protected override bool OnBackButtonPressed()
    {
        Dispatcher.Dispatch(async () => await CloseAsync());
        return true;
    }

    private void SafeRender()
    {
#pragma warning disable CA1031 // A dispatched redraw: nothing may escape.
        try
        {
            var stack = new VerticalStackLayout { Spacing = 16, Padding = new Thickness(24, 48, 24, 32) };
            stack.Add(Ui.Text(RemovalViewModel.Title, "Title"));
            stack.Add(Ui.Text(_model.Body, "Body"));
            if (_model.Unshared.Length > 0)
            {
                stack.Add(Ui.Text(_model.Unshared, "BodyStrong"));
                stack.Add(Button(RemovalViewModel.ShareText, "GhostButton", () => _model.ShareCommand.ExecuteAsync(null)));
            }

            stack.Add(Button(RemovalViewModel.OkText, "PrimaryButton", () => _model.OkCommand.ExecuteAsync(null)));
            if (_model.Toast.Length > 0)
                stack.Add(Ui.Text(_model.Toast, "Meta"));
            _body.Content = stack;
        }
        catch (Exception)
        {
        }
#pragma warning restore CA1031
    }

    private Button Button(string text, string style, Func<Task> action)
    {
        var button = new Button { Text = text };
        button.SetDynamicResource(StyleProperty, style);
        button.Clicked += async (_, _) =>
        {
            try
            {
                await action();
            }
#pragma warning disable CA1031 // async void handlers: nothing may escape.
            catch (Exception)
#pragma warning restore CA1031
            {
                Dispatcher.Dispatch(() => _model.Toast = Strings.Get("Error.Generic"));
            }
        };
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
}
```

- [ ] **Step 5: The prompter: the join owns the sheets, the removal sheet, consent on request**

In `SyncPrompter`: add fields `private bool _joinOffered; private HouseholdId? _removalShown; private Pages.JoinPage? _join;`,
the event `public event EventHandler? JoinEnded;`, and reset in `Resumed`:

```csharp
    /// <summary>The window came back (main thread): a held-back sheet, a removal closed with Back, a join left open get their turn.</summary>
    public void Resumed() => OnMainThread(() =>
    {
        (_joinOffered, _removalShown) = (false, null);
        return PromptAsync();
    });
```

Replace `PromptAsync`:

```csharp
    private async Task PromptAsync()
    {
        // The join flow owns the sheets while it shows (spec §5.1): nothing opens over it but its own consent.
        if (JoinShowing())
            return;

        var status = engine.Status;
        if (status.Removal is { } removed && removed != _removalShown)
        {
            if (await ShowAsync(() => services.GetRequiredService<RemovalPage>()))
                _removalShown = removed;
            return;
        }

        if (!_joinOffered && await services.GetRequiredService<JoinFamily>().StageAsync(CancellationToken.None) != JoinStage.None)
        {
            await ShowJoinAsync();
            return;
        }

        if (status.Last == CycleOutcome.ConsentNeeded && !_consentShown)
            _consentShown = await ShowAsync(() => services.GetRequiredService<ConsentPage>());

        // Only once consent is given and the family's log has been pulled (final review C1): there is something to choose.
        else if (status.JoinReady && account.State is JoinPending && !_bringOverShown)
            _bringOverShown = await ShowAsync(() => services.GetRequiredService<BringOverPage>());
    }
```

Add:

```csharp
    /// <summary>
    /// The join sheet (spec §5): from a link, the code sheet, or at start/resume with an invitation or a choice pending. Once
    /// it opened, it is not offered again by itself until the next resume (Back at the choice keeps the marker).
    /// </summary>
    public async Task<bool> ShowJoinAsync(bool malformed = false)
    {
        var opened = await ShowAsync(() =>
        {
            var page = services.GetRequiredService<JoinPage>();
            page.Malformed = malformed;
            page.Ended += (_, _) =>
            {
                if (ReferenceEquals(_join, page))
                    _join = null;
                JoinEnded?.Invoke(this, EventArgs.Empty);
            };
            _join = page;
            return page;
        });
        if (opened)
            _joinOffered = true;
        return opened;
    }

    /// <summary>Settings → Family → Join a family, and the setup step's link (spec §4.4).</summary>
    public Task<bool> ShowEnterCodeAsync() => ShowAsync(() =>
    {
        var page = services.GetRequiredService<EnterCodePage>();
        page.CodeAccepted += (_, _) => OnMainThread(async () => await ShowJoinAsync());
        return page;
    });

    /// <summary>
    /// The consent sheet asked for by a flow (inviting, joining): it may open over the join sheet, and the flow waits for it to
    /// close. True when consent is given then.
    /// </summary>
    public async Task<bool> AskConsentAsync()
    {
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var opened = await ShowAsync(
            () =>
            {
                var page = services.GetRequiredService<ConsentPage>();
                page.Disappearing += (_, _) => closed.TrySetResult();
                return page;
            },
            overSheet: true);
        if (!opened)
            return false;
        _consentShown = true;
        await closed.Task;
        return await services.GetRequiredService<ISessionStore>().GetAsync(CancellationToken.None) is { } session
            && await services.GetRequiredService<ConsentGate>().CachedAsync(session.Profile.UserId, CancellationToken.None) == ConsentState.Given;
    }

    private bool JoinShowing() =>
        _join is not null && Microsoft.Maui.Controls.Application.Current?.Windows is { Count: > 0 } windows
        && windows[0].Page?.Navigation.ModalStack.Contains(_join) == true;
```

and give `ShowAsync` an `overSheet` parameter: `private async Task<bool> ShowAsync(Func<Page> create, bool overSheet = false)`;
when `overSheet` is true it skips the "another sheet is showing" check and does not replace `_sheet` (the sheet underneath
stays the tracked one). Add `using Coo.Mobile.Application.Households;` and `using Coo.Domain.Common;`. Hook the removal and
join pages' closing into the existing `page.Disappearing += (_, _) => OnMainThread(PromptAsync);` (already in `ShowAsync`).

- [ ] **Step 6: Start-up: a link that opened Coo, a brand-new phone, setup's "Enter a code"**

In `StartupPage.OnAppearing`, after the theme is applied and before the first real page, store a held link:

```csharp
        // An invitation link that opened Coo (spec §4.1) becomes the pending invitation now that the database is open.
        var inbox = _services.GetRequiredService<Joining.InvitationInbox>();
        var held = await inbox.StoreHeldAsync();
        var joinStage = await _services.GetRequiredService<JoinFamily>().StageAsync(CancellationToken.None);
        var openJoin = held == Joining.HeldLink.Malformed || joinStage != JoinStage.None;
```

In the "has a baby" branch, after `StartSync();`:

```csharp
            inbox.MarkReady();
            if (openJoin)
                await OpenJoinAsync(Window, held == Joining.HeldLink.Malformed);
```

In the setup branch (spec §5.3: a brand-new phone opened by an invitation skips the baby step while the flow runs):

```csharp
        var window = Window;
        ShowSetup(window, startSync: true);
        inbox.MarkReady();
        if (openJoin)
            await OpenJoinAsync(window, held == Joining.HeldLink.Malformed);
```

Add:

```csharp
    /// <summary>
    /// The join sheet over whatever shows. When it ends over the setup step with a baby in the shown family, Today opens
    /// instead (spec §5.3); with Not now, setup simply continues. Main thread; catches everything.
    /// </summary>
    private async Task OpenJoinAsync(Window window, bool malformed)
    {
        try
        {
            var prompter = _services.GetRequiredService<Sync.SyncPrompter>();
            prompter.JoinEnded -= OnJoinEnded;
            prompter.JoinEnded += OnJoinEnded;
            await prompter.ShowJoinAsync(malformed);
        }
#pragma warning disable CA1031 // The invitation stays pending; the flow opens again at the next start.
        catch (Exception e)
#pragma warning restore CA1031
        {
            global::Android.Util.Log.Warn("Coo", $"Join flow did not open: {e}");
        }

        void OnJoinEnded(object? sender, EventArgs e) => MainThread.BeginInvokeOnMainThread(async () => await ShowShellIfBabyAsync(window));
    }

    private async Task ShowShellIfBabyAsync(Window window)
    {
        try
        {
            if (window.Page is not Pages.SetupPage)
                return;
            var households = _services.GetRequiredService<IHouseholdStore>();
            if ((await households.ListHouseholdsAsync(CancellationToken.None)).FirstOrDefault(h => !h.IsDeleted) is not { } household
                || (await households.ListBabiesAsync(household.Id, CancellationToken.None)).Count == 0)
                return;
            window.Page = _services.GetRequiredService<AppShell>();
            StartSync();
        }
#pragma warning disable CA1031 // A main-thread callback must not end the app; setup stays.
        catch (Exception e)
#pragma warning restore CA1031
        {
            global::Android.Util.Log.Warn("Coo", $"Today did not open after joining: {e}");
        }
    }
```

In `ShowSetup`, wire the setup step's link (spec §4.4):

```csharp
        var model = (Coo.Mobile.Presentation.Setup.SetupViewModel)setup.BindingContext;
        model.EnterCodeRequested += (_, _) => MainThread.BeginInvokeOnMainThread(async () =>
        {
            try
            {
                var prompter = _services.GetRequiredService<Sync.SyncPrompter>();
                prompter.JoinEnded += (_, _) => MainThread.BeginInvokeOnMainThread(async () => await ShowShellIfBabyAsync(window));
                await prompter.ShowEnterCodeAsync();
            }
#pragma warning disable CA1031 // A main-thread callback: the setup step stays as it is.
            catch (Exception)
#pragma warning restore CA1031
            {
            }
        });
```

(add `using Coo.Mobile.Application.Households;` is already there; `JoinFamily`/`JoinStage` come from it.)

In `SetupPage`, under the Continue button, add the link:

```csharp
        var code = Ui.Text(SetupViewModel.HaveInvitationText, "BodyStrong", "Accent").Tap(l => l.Margin = new Thickness(0, 8, 0, 0));
        code.GestureRecognizers.Add(new TapGestureRecognizer { Command = model.EnterCodeCommand });
```

and add `code` after `go` in the stack's children.

- [ ] **Step 7: Composition and version**

In `MauiProgram`:

```csharp
        builder.Services.AddTransient<JoinPage>();
        builder.Services.AddTransient<EnterCodePage>();
        builder.Services.AddTransient<RemovalPage>();
        builder.Services.AddTransient<FamilyPage>();
        builder.Services.AddTransient<FamilyBringOverPage>();
        builder.Services.AddSingleton<Joining.InvitationInbox>();
        builder.Services.AddSingleton<Coo.Mobile.Presentation.Settings.IConsentPrompt, Joining.MauiConsentPrompt>();
```

(`FamilyPage` and `FamilyBringOverPage` arrive in Task 24; register them there if this task is built alone.) In the
`#if DEBUG` block of `StartupPage.StartSync`, resolve the new sheets too (`JoinPage`, `EnterCodePage`, `RemovalPage`), as
ruling F5 does for the consent and bring-over pages.

`Coo.Mobile.csproj`: `<ApplicationDisplayVersion>0.12.0</ApplicationDisplayVersion>`, `<ApplicationVersion>15</ApplicationVersion>`,
and the comment `e.g. 0.12.0+<commit>`.

- [ ] **Step 8: Build**

```sh
export DOTNET_ROOT=$HOME/dotnet-maui PATH=$HOME/dotnet-maui:$PATH JAVA_HOME=$HOME/Library/Java/JavaVirtualMachines/jdk-17.0.20+8/Contents/Home
dotnet restore src/Mobile/Coo.Mobile/Coo.Mobile.csproj
dotnet build src/Mobile/Coo.Mobile/Coo.Mobile.csproj -c Debug -f net10.0-android --no-restore \
  -p:UseDefaultPublishRuntimeIdentifier=false -p:EmbedAssembliesIntoApk=true \
  -p:AndroidSdkDirectory=$HOME/android-sdk -p:JavaSdkDirectory=$JAVA_HOME
grep -A8 'autoVerify' src/Mobile/Coo.Mobile/obj/Debug/net10.0-android/AndroidManifest.xml
dotnet test Coo.Tests.slnf
```

Expected: 0 warnings, 0 errors; the intent filter as in Step 1; all tests PASS.

- [ ] **Step 9: Smoke-run on the emulator** (the agent drives it)

```sh
ADB=~/android-sdk/platform-tools/adb
$ADB -s emulator-5554 install -r src/Mobile/Coo.Mobile/bin/Debug/net10.0-android/com.lechdigital.coo-Signed.apk
$ADB -s emulator-5554 logcat -c
$ADB -s emulator-5554 shell am start -a android.intent.action.VIEW -d https://api.coo.lechdigital.nl/i/ZZZZ-ZZZZ com.lechdigital.coo
$ADB -s emulator-5554 shell am start -a android.intent.action.VIEW -d https://api.coo.lechdigital.nl/i/notacode com.lechdigital.coo
$ADB -s emulator-5554 logcat -d | grep -iE "FATAL|AndroidRuntime|Unhandled" || echo "no crash"
```

Expected: the first link opens the join sheet and, signed in, "This invitation can't be used…" (the code exists nowhere);
OK closes it. The second opens it with the same message without a request. `no crash`.

- [ ] **Step 10: Commit**

```bash
dotnet format Coo.Tests.slnf
git add src/Mobile
git commit -m "Phone: the App Link with autoVerify, the invitation inbox, the join, code and removal sheets; 0.12.0 (15) (Spec 011b §4, §5, §8.2)"
```

---

### Task 24: On the phone: Settings → Family, the switcher, the Family page, bring entries over, Today's banner

**Files:**
- Modify: `src/Mobile/Coo.Mobile/Pages/SettingsPage.cs` (the Family section after Account), `src/Mobile/Coo.Mobile/Pages/SectionViews.cs`
  (the paused banner), `src/Mobile/Coo.Mobile/MauiProgram.cs` (if Task 23 left the two pages out)
- Create: `src/Mobile/Coo.Mobile/Pages/FamilyPage.cs`, `src/Mobile/Coo.Mobile/Pages/FamilyBringOverPage.cs`

**Interfaces:**
- Consumes: `FamilySectionViewModel` (Task 19), `FamilyPageViewModel` (Task 20), `FamilyBringOverViewModel`,
  `BringFamilyEntriesOver`, `Families`, `ISyncControl`, `PausedSection` (Task 22), `SyncPrompter.ShowEnterCodeAsync` (Task 23).
- Produces: the Family section; `FamilyPage.Open(HouseholdId)`; `FamilyBringOverPage`.

No unit tests (host); checks: build, the architecture tests, the smoke run in Step 6.

- [ ] **Step 1: The Family section in Settings**

In `SettingsPage`: inject `FamilySectionViewModel family` (store `_family`); in `OnAppearing` subscribe
`_family.PropertyChanged += OnFamilyChanged;` and refresh it (`await RunTappedAsync(_family.RefreshAsync)` after
`_model.RefreshAsync()`); unsubscribe in `OnDisappearing`. Refresh it too in `OnDataChanged` and `OnAccountChanged`
(both already dispatch). Add:

```csharp
    // The family list can change from a background refresh; through the dispatcher like the account.
    private void OnFamilyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => Dispatcher.Dispatch(() =>
    {
        if (e.PropertyName == nameof(FamilySectionViewModel.Toast))
        {
            if (!string.IsNullOrEmpty(_family.Toast))
                _undo.Say(_family.Toast);
            _family.Toast = "";
            return;
        }

        QueueRender();
    });
```

In `Render()`, right after the account views:

```csharp
        foreach (var view in FamilyViews())
            stack.Add(view);
```

and:

```csharp
    /// <summary>Settings → Family (Spec 011b §2), right after Account.</summary>
    private IEnumerable<View> FamilyViews()
    {
        yield return Header("Family.Title");
        if (_family.ShowsSwitcher)
        {
            yield return Row(
                new VerticalStackLayout { Spacing = 2, Children = { Ui.Text(_family.ShowingTitle, "BodyStrong"), Ui.Text(_family.ShowingLine, "Meta", "Ink2") } },
                ChooseFamilyAsync);
        }

        if (_family.ShowsMembers && _family.ShownId is { } shown)
        {
            yield return Row(
                new VerticalStackLayout { Spacing = 2, Children = { Ui.Text(Strings.Get("Family.Members"), "BodyStrong"), Ui.Text(_family.MembersLine, "Meta", "Ink2") } },
                async () => await Navigation.PushAsync(FamilyPage.Open(_services, shown)));
        }

        if (_family.ShowsOnlyHere)
            yield return Ui.Text(Strings.Get("Family.OnlyHere"), "Meta", "Ink2");
        if (_family.ShowsInvite)
            yield return Row(Ui.Text(Strings.Get("Family.Invite"), "Body"), () => _family.InviteCommand.ExecuteAsync(null));
        yield return Row(Ui.Text(Strings.Get("Family.Join"), "Body"), () => _services.GetRequiredService<Sync.SyncPrompter>().ShowEnterCodeAsync());
        if (_family.ShowsBringOver)
            yield return Row(Ui.Text(Strings.Get("Family.BringOver"), "Body"), async () => await Navigation.PushModalAsync(_services.GetRequiredService<FamilyBringOverPage>()));
    }

    /// <summary>The switcher (§7.3): "Show which family?", every family but soft-locked ones.</summary>
    private async Task ChooseFamilyAsync()
    {
        var choices = _family.Choices;
        var chosen = await DisplayActionSheetAsync(Strings.Get("Family.Switch"), Strings.Get("Action.Cancel"), null, [.. choices.Select(c => c.Label)]);
        if (choices.FirstOrDefault(c => c.Label == chosen) is { } choice)
            await _family.SwitchCommand.ExecuteAsync(choice);
    }
```

(`Row` already wraps its action in `RunTappedAsync`, the catch-all.)

- [ ] **Step 2: The Family page**

`src/Mobile/Coo.Mobile/Pages/FamilyPage.cs`:

```csharp
using Coo.Domain.Common;
using Coo.Mobile.Components;
using Coo.Mobile.Presentation.Localization;
using Coo.Mobile.Presentation.Settings;
using Coo.Mobile.Presentation.Today;

namespace Coo.Mobile.Pages;

/// <summary>The Family page (Spec 011b §2), pushed from Settings. Every confirmation is Android's dialog; every handler catches everything.</summary>
public sealed class FamilyPage : ContentPage
{
    private readonly FamilyPageViewModel _model;
    private readonly UndoService _undo;
    private readonly ContentView _body = new();
    private HouseholdId _household;
    private bool _confirming;

    public FamilyPage(FamilyPageViewModel model, UndoService undo)
    {
        (_model, _undo) = (model, undo);
        SetDynamicResource(BackgroundColorProperty, "Ground");
        var root = new Grid();
        root.Add(new ScrollView { Content = _body });
        root.Add(new UndoToastView(undo));
        Content = root;
        model.PropertyChanged += (_, e) => Dispatcher.Dispatch(() =>
        {
            if (e.PropertyName == nameof(FamilyPageViewModel.Toast))
            {
                if (_model.Toast.Length > 0)
                    _undo.Say(_model.Toast);
                return;
            }

            SafeRender();
        });
        model.Closed += (_, _) => Dispatcher.Dispatch(async () => await PopAsync());
    }

    public static FamilyPage Open(IServiceProvider services, HouseholdId household)
    {
        ArgumentNullException.ThrowIfNull(services);
        var page = services.GetRequiredService<FamilyPage>();
        page._household = household;
        return page;
    }

    protected override async void OnAppearing()
    {
#pragma warning disable CA1031 // async void: nothing may escape.
        try
        {
            base.OnAppearing();
            await _model.LoadAsync(_household);
            SafeRender();
        }
        catch (Exception)
        {
            _undo.Say(Strings.Get("Error.Generic"));
        }
#pragma warning restore CA1031
    }

    private void SafeRender()
    {
#pragma warning disable CA1031 // A dispatched redraw: nothing may escape.
        try
        {
            Render();
        }
        catch (Exception)
        {
        }
#pragma warning restore CA1031
    }

    private void Render()
    {
        Title = _model.Title;
        var stack = new VerticalStackLayout { Spacing = 12, Padding = new Thickness(24, 24, 24, 96) };
        stack.Add(Ui.Text(_model.Title, "Title"));
        if (_model.RefreshFailed)
            stack.Add(Ui.Text(FamilyPageViewModel.RefreshFailedText, "Meta", "Ink2"));

        stack.Add(Ui.Text(Strings.Get("Family.Members"), "Section"));
        foreach (var member in _model.Members)
        {
            var grid = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto)] };
            grid.Add(new VerticalStackLayout
            {
                Spacing = 2,
                Children = { Ui.Text(member.Label, "BodyStrong"), Ui.Text(member.Email, "Meta", "Ink2"), Ui.Text(member.Role, "Meta", "Ink2") },
            }, 0);
            if (member.CanRemove)
                grid.Add(Small(Strings.Get("Family.Remove"), () => ConfirmAsync(_model.RemovePrompt(member), () => _model.RemoveCommand.ExecuteAsync(member))), 1);
            stack.Add(new Card(grid));
        }

        if (_model.ShowsInvitations && _model.Invitations.Count > 0)
        {
            stack.Add(Ui.Text(Strings.Get("Family.Invitations"), "Section"));
            foreach (var invitation in _model.Invitations)
            {
                var grid = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto)] };
                grid.Add(Ui.Text(invitation.Line, "Body"), 0);
                grid.Add(Small(Strings.Get("Family.Withdraw"), () => ConfirmAsync(_model.WithdrawPrompt, () => _model.WithdrawCommand.ExecuteAsync(invitation))), 1);
                stack.Add(new Card(grid));
            }
        }

        if (_model.ShowsInvite)
            stack.Add(Wide(Strings.Get("Family.Invite"), "PrimaryButton", () => _model.InviteCommand.ExecuteAsync(null)));
        if (_model.ShowsLeave)
            stack.Add(Wide(Strings.Get("Family.Leave"), "GhostButton", async () => await ConfirmAsync(await _model.LeavePromptAsync(), () => _model.LeaveCommand.ExecuteAsync(null))));
        if (_model.ShowsDelete)
            stack.Add(Wide(Strings.Get("Family.Delete"), "GhostButton", async () => await ConfirmAsync(await _model.DeletePromptAsync(), () => _model.DeleteCommand.ExecuteAsync(null))));
        _body.Content = stack;
    }

    private async Task ConfirmAsync(ConfirmPrompt prompt, Func<Task> action)
    {
        if (_confirming)
            return;
        _confirming = true;
        try
        {
            if (await DisplayAlertAsync(prompt.Title, prompt.Message, prompt.Accept, prompt.Cancel))
                await action();
        }
        finally
        {
            _confirming = false;
        }
    }

    private Button Small(string text, Func<Task> action) => Styled(text, "GhostButton", action);

    private Button Wide(string text, string style, Func<Task> action) => Styled(text, style, action);

    private Button Styled(string text, string style, Func<Task> action)
    {
        var button = new Button { Text = text };
        button.SetDynamicResource(StyleProperty, style);
        button.Clicked += async (_, _) =>
        {
            try
            {
                await action();
            }
#pragma warning disable CA1031 // async void handlers: nothing may escape.
            catch (Exception)
#pragma warning restore CA1031
            {
                _undo.Say(Strings.Get("Error.Generic"));
            }
        };
        return button;
    }

    private async Task PopAsync()
    {
        try
        {
            if (Navigation.NavigationStack.Contains(this))
                await Navigation.PopAsync();
        }
#pragma warning disable CA1031 // Popping twice must not crash.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }
}
```

- [ ] **Step 3: Bring entries over from Settings**

`src/Mobile/Coo.Mobile/Pages/FamilyBringOverPage.cs`:

```csharp
using Coo.Mobile.Application.Households;
using Coo.Mobile.Application.Sync;
using Coo.Mobile.Presentation.Localization;
using Coo.Mobile.Presentation.Settings;
using Coo.Mobile.Presentation.Today;

namespace Coo.Mobile.Pages;

/// <summary>
/// Settings → Family → Bring entries over (spec §6.3 "Later"): the shown family into another account family, chosen when
/// there are several; a never-synced source moves, a synced one copies; a family of another account is never offered.
/// </summary>
public sealed class FamilyBringOverPage : ContentPage
{
    private readonly FamilyBringOverViewModel _model;
    private readonly Families _families;
    private readonly BringFamilyEntriesOver _bring;
    private readonly ISyncControl _sync;
    private readonly UndoService _undo;
    private readonly ContentView _body = new();
    private bool _started;

    public FamilyBringOverPage(FamilyBringOverViewModel model, Families families, BringFamilyEntriesOver bring, ISyncControl sync, UndoService undo)
    {
        (_model, _families, _bring, _sync, _undo) = (model, families, bring, sync, undo);
        SetDynamicResource(BackgroundColorProperty, "Ground");
        Shell.SetNavBarIsVisible(this, false);
        Content = new ScrollView { Content = _body };
        model.PropertyChanged += (_, _) => Dispatcher.Dispatch(SafeRender);
        model.Finished += (_, toast) => Dispatcher.Dispatch(async () =>
        {
            await CloseAsync();
            if (toast.Length > 0)
                _undo.Say(toast);
        });
    }

    protected override async void OnAppearing()
    {
#pragma warning disable CA1031 // async void: nothing may escape.
        try
        {
            base.OnAppearing();
            if (_started)
                return;
            _started = true;
            if (await _families.ShownAsync(CancellationToken.None) is not { MayBeBroughtOver: true } source)
            {
                await CloseAsync();
                return;
            }

            var targets = await _families.BringOverTargetsAsync(source.Id, CancellationToken.None);
            var target = targets.Count == 1 ? targets[0] : await ChooseAsync(targets);
            if (target is null)
            {
                await CloseAsync();
                return;
            }

            var choice = await _bring.PrepareAsync(source.Id, target.Id, CancellationToken.None);
            var name = target.Membership?.OwnerName ?? "";
            _model.Start(new BringOverRequest(
                source.Id, choice,
                choice.Family.Count == 0 ? Strings.Get("Join.NoBaby") : Strings.Format("Join.Babies", string.Join(", ", choice.Family.Select(b => b.Name))),
                name,
                async (mapping, ct) =>
                {
                    var outcome = await _bring.ExecuteAsync(source.Id, target.Id, mapping, ct);
                    _sync.NotifyDataChanged();
                    _sync.RequestSync();
                    return outcome;
                },
                Keep: null,
                Strings.Format("Join.Copied", name)));
            SafeRender();
        }
        catch (Exception)
        {
            _undo.Say(Strings.Get("Error.Generic"));
            await CloseAsync();
        }
#pragma warning restore CA1031
    }

    protected override bool OnBackButtonPressed()
    {
        Dispatcher.Dispatch(async () => await CloseAsync());
        return true;
    }

    private async Task<FamilyOnPhone?> ChooseAsync(IReadOnlyList<FamilyOnPhone> targets)
    {
        if (targets.Count == 0)
            return null;
        var labels = targets.Select(FamilyLabels.Choice).ToList();
        var chosen = await DisplayActionSheetAsync(Strings.Get("BringOver.ChooseTarget"), Strings.Get("Action.Cancel"), null, [.. labels]);
        var index = labels.IndexOf(chosen ?? "");
        return index >= 0 ? targets[index] : null;
    }

    private void SafeRender()
    {
#pragma warning disable CA1031 // A dispatched redraw: nothing may escape.
        try
        {
            _body.Content = new VerticalStackLayout
            {
                Padding = new Thickness(24, 48, 24, 32),
                Children = { BringOverViews.Render(_model, RunAsync) },
            };
        }
        catch (Exception)
        {
        }
#pragma warning restore CA1031
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
            _undo.Say(Strings.Get("Error.Generic"));
        }
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
}
```

- [ ] **Step 4: Today's paused banner**

In `SectionViews.Create`, add an arm: `PausedSection s => Live(s, () => Paused(s)),` and:

```csharp
    /// <summary>Spec 011b §8.2: only while the shown family is soft-locked and the only family; empty otherwise.</summary>
    private static View Paused(PausedSection s)
    {
        if (s.Text.Length == 0)
            return new ContentView();
        return Ui.Rounded(Ui.Text(s.Text, "Body"), "Surface", 12, new Thickness(16, 12), "Line").Tap(v => v.Margin = new Thickness(24, 8, 24, 0));
    }
```

(`Ui.Rounded(content, background, radius, padding, border)` is the helper the header chip uses; match its signature.)

- [ ] **Step 5: Build**

As Task 23 Step 8. Expected: 0 warnings, 0 errors; all tests PASS.

- [ ] **Step 6: Smoke-run on the emulator**

Install as in Task 23 Step 9; open Settings: the **Family** section shows after Account with "This family's log is only on
this phone." or **Members**, and **Join a family** opens "Enter the code from your invitation."; type `abcd efg` →
"Check the code: it's 8 letters and numbers, like ABCD-EFGH."; Back closes. `logcat` shows no crash.

- [ ] **Step 7: Commit**

```bash
dotnet format Coo.Tests.slnf
git add src/Mobile
git commit -m "Phone: Settings → Family, the switcher, the Family page, bring entries over, Today's paused banner (Spec 011b §2, §6.3, §7.3, §8.2)"
```

---

### Task 25: The debug screen: families, the pending invitation, the joining marker

**Files:**
- Modify: `src/Mobile/Coo.Mobile/DebugScreen/DebugPage.cs`

**Interfaces:**
- Consumes: `Families.ListAsync/RefreshAsync`, `IFamilyStore.GetPendingInvitationAsync/GetJoiningAsync`, `SyncStatus.ShownStopped`.
- Produces: a "Families (Spec 011b)" block, which Task 27's deliverable 3 reads.

- [ ] **Step 1: The block**

Add a field `private readonly Label _familiesLine = new() { FontSize = 13 };` and, after the sync rows (before "Last 20
entries"):

```csharp
                    new Label { Text = "Families (Spec 011b)", FontSize = 17, Margin = new Thickness(0, 12, 0, 0) },
                    _familiesLine,
                    Row(Button("Refresh families", async () =>
                    {
                        _status.Text = await services.GetRequiredService<Families>().RefreshAsync(CancellationToken.None)
                            ? "Families refreshed" : "Families not refreshed (offline or signed out)";
                        await ShowFamiliesAsync();
                    })),
```

and keep the provider: add `private readonly IServiceProvider _services;` (assigned from the constructor's `services`). Add:

```csharp
    private async Task ShowFamiliesAsync()
    {
        var families = await _services.GetRequiredService<Families>().ListAsync(CancellationToken.None);
        var store = _services.GetRequiredService<IFamilyStore>();
        var pending = await store.GetPendingInvitationAsync(CancellationToken.None);
        var joining = await store.GetJoiningAsync(CancellationToken.None);
        var lines = families.Select(f =>
            $"{(f.IsShown ? "▶ " : "  ")}{f.Id.Value.ToString()[^6..]} · {string.Join(", ", f.BabyNames)} · "
            + $"{f.Membership?.Role.ToString() ?? "not an account family"}{(f.Membership is { } m ? $" ({m.MemberCount}, owner {m.OwnerName})" : "")}"
            + $"{(f.IsHome ? " · home" : "")}{(f.IsSoftLocked ? " · soft-locked" : "")}{(f.HasEverSynced ? " · synced" : " · never synced")}");
        _familiesLine.Text = string.Join("\n", lines)
            + $"\npending invitation: {pending?.Code.Display ?? "none"} · joining: {(joining is null ? "none" : joining.Joined.Value.ToString()[^6..])}"
            + $"{(_sync.Status.ShownStopped ? " · shown family stopped (not a member)" : "")}";
    }
```

Call `await ShowFamiliesAsync();` from `OnAppearing` (next to `ShowSyncAsync`) and from `OnSyncStatus` (inside its
try/catch). Add `using Coo.Mobile.Application.Households;` if it is not there.

- [ ] **Step 2: Build and look**

Build as in Task 23 Step 8 (Debug includes the debug screen); install on the emulator; open the debug screen (three taps
on the "coo" wordmark by day): the Families block lists the emulator's family with "▶", "· home" and the Owner role once
signed in.

- [ ] **Step 3: Commit**

```bash
git add src/Mobile/Coo.Mobile/DebugScreen/DebugPage.cs
git commit -m "Debug screen: the families on the phone, the pending invitation and the joining marker (Spec 011b)"
```

---

### Task 26: The privacy policy and the deletion page for 0.12.0 (other repository)

**Files (repository `/Users/andrzej.lech/Code/private/lech-digital`, GitHub Pages from `main`; pushing deploys):**
- Modify: `projects/coo/privacy/index.html`, `projects/coo/delete-account/index.html`, `sitemap.xml`

**Interfaces:**
- Consumes: what 0.12.0 does (Tasks 1–25).
- Produces: spec §13's text live **before 0.12.0 reaches anyone but the owner** (here: before the girlfriend's phone is
  updated, which is "later, owner"; Task 27's deliverable 1 checks it is live).

Content rule: only true facts about 0.12.0; keep the markup and classes (read the site's `CLAUDE.md`: no build step).

- [ ] **Step 1: The privacy policy** (`projects/coo/privacy/index.html`), exactly spec §13's table:

| Where | New text (keep the entities and tags around it) |
|---|---|
| "Last updated" | the day of this edit |
| In short, bullet 4 | "Signing in is **optional**. If you sign in and **agree to share**, your family's log is stored on our server in Germany, and only then. You can invite another parent to your family, and they see and add to the same log." |
| "If you sign in", first paragraph | "Signing in is optional. You need it to share your log between phones and with the people in your family. Sign-in is with your Google account, through Android's own account sheet." |
| "If you sign in", second paragraph, add | "The people in a family you belong to see your display name and email address in the family's member list." |
| "When you share", after its first paragraph (new) | "**Inviting someone.** A parent in a family can invite another parent with a link or an 8-character code. Each invitation is valid for 72 hours and can be used once. We store a scrambled form of the code (a hash), who sent it, when, and whether it was used or withdrawn, and we delete the invitation within a day after it expires. The person who joins signs in with their own Google account and gives their own permission before anything is stored for them. Everyone in the family, including the person who joins, can see the whole family log, including entries made before they joined, and add to it." |
| "When you share", new paragraph | "**Leaving and removal.** A parent can remove another member (except the person who created the family), and a member can leave a family. When you are removed or leave, the family's log is removed from your phone; the entries you added stay with the family, because they are the family's record of their child." |
| "When you share", new paragraph | "**Bringing entries over.** When you join a family, you can bring the entries already on your phone into it. If those entries were already shared in another family, they are copied, and you choose whether to delete the old family." |
| Data retention, add | "Invitations are deleted within a day after they expire, 72 hours after they are sent. When the last parent leaves or deletes a family, the family and its whole shared log are deleted." |
| Deleting your account, second paragraph | "Deleting your account removes your account, your sign-ins and sessions, the record of your consent, and your family memberships. If you are the last parent of a family, that family and its whole shared log are deleted too. Invitations you sent are deleted." |
| Changes to this policy | "…before adding any feature that uses data differently — for example Premium or AI features — …" |
| Summary | "No account needed. Your baby's log stays on your phone unless you choose to share, and Android backup is off. No ads, no analytics, no trackers. If you sign in and agree to share, your family's log is kept on our server in Germany, shared with the people in your family, until you delete your account." |

Check (spec §13):

```sh
cd /Users/andrzej.lech/Code/private/lech-digital
grep -n -iE "coming soon|will be|for example inviting" projects/coo/privacy/index.html || echo clean
```

Expected: `clean`.

- [ ] **Step 2: The deletion page** (`projects/coo/delete-account/index.html`)

- "What is deleted": `your family membership;` becomes `<li>your family memberships;</li>`, and add `<li>invitations you sent;</li>`.
- "What is not deleted": add `<p>If other parents remain in a family, that family and its shared log stay with them.</p>`
  (as a paragraph or list item, matching the section's existing markup).

- [ ] **Step 3: `sitemap.xml`**: today's `lastmod` for both URLs.

- [ ] **Step 4: Look at it locally**

```sh
cd /Users/andrzej.lech/Code/private/lech-digital
python3 -m http.server 8822 >/dev/null 2>&1 &
curl -s http://localhost:8822/projects/coo/privacy/ | grep -c "Inviting someone"
curl -s http://localhost:8822/projects/coo/delete-account/ | grep -c "invitations you sent"
kill %1
```

Expected: `1` and `1`.

- [ ] **Step 5: Commit and push (deploys)**

```bash
cd /Users/andrzej.lech/Code/private/lech-digital
git config user.name    # must print adziusmaster; else set user.name adziusmaster and user.email adzius.lech@gmail.com locally
gh api user --jq .login # must print adziusmaster
git add projects/coo/privacy/index.html projects/coo/delete-account/index.html sitemap.xml
git commit -m "Coo: the privacy policy for 0.12.0: inviting a parent, the member list, leaving and removal, bringing entries over"
git push origin main
```

(No `Co-Authored-By` trailer.) After about a minute:

```sh
curl -s https://lechdigital.nl/projects/coo/privacy/ | grep -c "Inviting someone"
curl -s https://lechdigital.nl/projects/coo/delete-account/ | grep -c "invitations you sent"
```

Expected: `1`, `1`.

---

### Task 27: On the two devices, the record, push and CI

**Files:**
- Modify: `docs/specs/011b-members-and-invitations/spec.md` (new §16 "Built and verified"), `docs/adrs/0003-identity-and-household-membership.md`
  (an "Amended by" line for rulings 15 and 17 if the wording of §6 or §8 needs it — see Step 5), `docs/ROADMAP.md`
  (row 11b: Done (0.12.0)), `docs/HANDOVER-2026-09-30.md` ("Resume here"), `BUILD.md` (App Links, the second account on
  the Pixel, lessons)

**Interfaces:**
- Consumes: everything above; production at 0.12.0 (Task 9); the policy live (Task 26).

- [ ] **Step 1: Redeploy if the server changed after Task 9**

```sh
DEPLOYED=$(curl -s https://api.coo.lechdigital.nl/health | sed -E 's/.*\+([0-9a-f]+).*/\1/')
git diff --stat "$DEPLOYED" HEAD -- src/Backend src/Shared Dockerfile deploy
```

If that lists files, run `./deploy/deploy-prod.sh --confirm` again (clean tree, Docker running) and expect
`Healthy: …0.12.0+<new commit>…`.

- [ ] **Step 2: The trimmed Release build with the debug screen, on both devices**

```sh
export DOTNET_ROOT=$HOME/dotnet-maui PATH=$HOME/dotnet-maui:$PATH JAVA_HOME=$HOME/Library/Java/JavaVirtualMachines/jdk-17.0.20+8/Contents/Home
dotnet build src/Mobile/Coo.Mobile/Coo.Mobile.csproj -c Release -f net10.0-android --no-restore \
  -p:UseDefaultPublishRuntimeIdentifier=false -p:AndroidPackageFormat=apk -p:CooDebugScreen=true \
  -p:AndroidSdkDirectory=$HOME/android-sdk -p:JavaSdkDirectory=$JAVA_HOME
APK=src/Mobile/Coo.Mobile/bin/Release/net10.0-android/com.lechdigital.coo-Signed.apk
ADB=~/android-sdk/platform-tools/adb
$ADB -s 5B130DLCR00011 install -r $APK
$ADB -s emulator-5554 install -r $APK
```

Expected: no trim (IL) warnings (`HouseholdJsonContext` is source-generated); both install over the earlier build and keep
their data. Boot the emulator first if needed (`BUILD.md`, "The second phone").

- [ ] **Step 3: App Link verification (deliverable 2)**

```sh
$ADB -s 5B130DLCR00011 shell pm verify-app-links --re-verify com.lechdigital.coo
$ADB -s emulator-5554 shell pm verify-app-links --re-verify com.lechdigital.coo
$ADB -s 5B130DLCR00011 shell pm get-app-links com.lechdigital.coo
$ADB -s emulator-5554 shell pm get-app-links com.lechdigital.coo
```

Expected (after a few seconds): `api.coo.lechdigital.nl: verified` on both. If `1024`/`none`, check
`curl -s https://api.coo.lechdigital.nl/.well-known/assetlinks.json` lists the debug key's SHA-256 (Global Constraints) and
that the installed APK is signed with it (`apksigner verify --print-certs $APK`).

- [ ] **Step 4: Walk spec §14** in its order (the table there is the script). The agent drives both devices with adb
  (`uiautomator dump` to find controls, `input tap`, `screencap` to look). In Google's account sheet the agent picks
  **adzius.lech@gmail.com** on the emulator and **andrzej.lech.andrzej@gmail.com** on the Pixel. **If the Pixel's sheet does
  not offer andrzej.lech.andrzej@gmail.com, stop and ask the owner** to add that Google account on the Pixel (Settings →
  Passwords, passkeys & accounts → Add account); the agent never handles a password or 2-step verification.

| # | Who | How (spec §14) |
|---|---|---|
| 1 | Agent | `curl -s https://api.coo.lechdigital.nl/health` → `0.12.0+…`; assetlinks lists the fingerprint; Task 26's live checks pass |
| 2 | Agent | Step 3 above |
| 3 | Agent | Pixel: Sign out; Sign in → the second account → `created`; the consent sheet → Agree; H2 shown, the baby step → Maja, two diapers. Debug screen Families block: H2 "Owner · home · synced", X "not an account family"; the switcher: "Maja · Your family", "Maja · On this phone only" |
| 4 | Agent | Emulator: Settings → Family → Invite a parent → the share sheet (read the link from `uiautomator dump`), Back; Family page lists it with "until …". A second invitation, withdrawn (confirmed); its code typed on the Pixel (Join a family) → "This invitation can't be used…" |
| 5 | Agent | `curl -s https://api.coo.lechdigital.nl/i/<code>` shows the page with the hyphenated code, the Play link and no names; `/i/abc` shows it without a code |
| 6 | Agent | Pixel showing H2: `adb -s 5B130DLCR00011 shell am start -a android.intent.action.VIEW -d <link>` → Coo opens the join sheet: "Join Andrzej's family?", "Their family's log has: Maja." → Join → "This phone has 2 entries for Maja…" → **Keep them separate** → X shown, the toast |
| 7 | Agent (owner toggles airplane mode if `adb shell cmd connectivity airplane-mode enable` is refused) | A bottle on the Pixel (X) appears on the emulator within 30 s; a diaper on the emulator appears on the Pixel; an offline edit on each converges |
| 8 | Agent | Pixel: Showing → H2: Today and Log show H2's; a diaper in H2 never reaches the emulator; back to X, one does |
| 9 | Agent | Emulator Family page: Andrzej "Parent · created the family (you)", the second account "Parent" with Remove; no Leave/Delete. Pixel Family page (X): Remove beside nobody; Leave this family shown |
| 10 | Agent | Emulator: a fresh invitation; Pixel: Join a family → its code → "You're already in this family."; the invitation still listed on the emulator |
| 11 | Agent | Pixel (X): Leave this family → confirmed → H2 shown; emulator's Family page lists only Andrzej |
| 12 | Agent | Emulator: new invitation; Pixel (H2) opens it → Join → `rejoined` (debug/`psql`: `revoked_at` NULL again) → the sheet → Bring my entries over → Maja → Maja → the copies on the emulator within 30 s → "Delete old family?" → **Delete old family** → H2 gone from the Pixel; read-only: `ssh coldstart-prod "docker exec coo-postgres psql -U coo -d coo -tAc \"SELECT count(*) FROM households WHERE id = '<H2>'\""` → `0` |
| 13 | Agent | Pixel airplane mode on, a diaper in X; emulator: remove the second account (confirmed); Pixel airplane mode off: within 30 s the removal sheet, "1 entry you logged wasn't shared yet", Share them as text shows one line; airplane mode on again; **OK** → X gone, the baby step opens (a new family, §8.4). The sync line never said "Waiting for a connection" before the sheet |
| 14 | Agent | Still offline: baby Maja, two diapers; Sign out; airplane mode off. Emulator: a new invitation; Pixel opens it → "Sign in to join a family" → the second account (adopts the new family) → Join → `rejoined` → the sheet → Bring my entries over → they reach the emulator within 30 s; read-only: the adopted family's ID is not in `households`, and `SELECT count(*) FROM memberships WHERE user_id = '<second>' AND revoked_at IS NULL` → `1` |
| 15 | Agent | Step 7 below |

Every finding becomes a fix with a test where one is possible, in its own commit ("Fix: … (Pixel 10 / emulator)"), and a
redeploy when it touches the server.

- [ ] **Step 5: The record**

- Spec 011b: a new **§16 "Built and verified"**, headed with the date and "Pixel 10 (second account) and the emulator
  `coo-second`": the deliverables with results; this plan's "Rulings on what the spec leaves open" (all 29); the findings
  and fixes; the girlfriend's phone listed as "later, owner".
- ADR 0003: under the existing "Amended by" line, add "and Spec 011b §16 (rulings 15 and 17: leave is idempotent; account
  deletion decides under the families' locks)" only if §8's text still says the plan is read before the transaction;
  otherwise no change.
- `docs/ROADMAP.md` row 11b → "Done (0.12.0)".
- `docs/HANDOVER-2026-09-30.md` "Resume here": Increment 11b done, 0.12.0 deployed, the policy updated; the Pixel is now the
  second account (andrzej.lech.andrzej@gmail.com), the emulator the Owner's family; the girlfriend's phone is the owner's
  to update; next is 11c (Washi forms) or 12 (billing).
- `BUILD.md`: a section "App Links (Spec 011b)": `pm get-app-links`, `pm verify-app-links --re-verify`, opening a link with
  `adb shell am start -a android.intent.action.VIEW -d <link>`, and that the debug key's SHA-256 is in production's
  `assetlinks.json`; "MAUI lessons from Increment 11b": anything the devices taught.

- [ ] **Step 6: The full check**

```sh
dotnet test Coo.Tests.slnf
TZ=UTC dotnet test Coo.Tests.slnf
dotnet format Coo.Tests.slnf --verify-no-changes
```

Expected: all PASS twice; no format changes.

- [ ] **Step 7: Commit, push, and watch CI**

```bash
git add docs BUILD.md
git commit -m "Increment 11b: members and invitations, verified on the Pixel 10 and the emulator; deployed as 0.12.0 (Spec 011b)"
gh api user --jq .login            # must print adziusmaster (the repo pushes over HTTPS through gh)
git push
gh run watch
```

Expected: `adziusmaster`; the push succeeds; CI "Build, format and test" green, including the API image build.

---

## Self-review (done while writing; kept for the reviewer)

**Spec coverage.** §1 → Tasks 16, 27. §2 Family section → 19, 24; Family page → 20, 24; offline behaviour → 17, 20
(Review Focus 4); joining sheets → 21, 23; removal sheet → 22, 23; sync line → 19 (`Sync.NotAMember`), 14 (no-progress);
Today banner → 22, 24; never blocks logging → nothing in the join, removal or soft-lock paths gates a write (logging writes
to the shown family, §7.3). §3 inviting → 2 (server), 17, 19, 20 (phone), 18 (share text); the code → 1; hash only → 2, 4;
5 open → 2; 20 a day → 7; withdraw and the departing member's invitations → 2, 3, 4. §4.1 App Link → 23; malformed path →
21, 23; pending in `app_state` with its time → 11, 16; §4.2 assetlinks and the fingerprint check → 8; §4.3 page, headers,
no lookup, no logs → 8 (DEPLOY.md keeps Caddy's no-`log`); §4.4 Enter a code and setup's link → 21, 22, 23. §5.1 flow →
16, 21, 23; consent first → 3 (server refuses), 16, 21; Not now → 16, 21; pending > 72 h dropped, younger reopens → 16, 23;
the flow owns the sheets → 23. §5.2 answers → 3, 13 (client codes), 16, 21. §5.3 no push mid-join → 14 (ruling 5);
joining marker → 11, 16; sign-in finding an account family (and `created`, ruling 4) → 14, 16; brand-new phone → 23.
§6 nothing asked / M7 → 16; §6.1 move and the automatic leave → 16 (`LeaveEmptyAdoptedAsync`); §6.2 copy → 12, 16
(ruling 8), the question by role → 16, 21; §6.3 keep separate and "later" → 16, 21, 24. §7.1 `family_memberships`,
refreshes, sign-out clears, home moves → 10, 11, 14 (`EnsureHome`), 15; §7.2 every family syncs, soft-locked/removed does
not stop the others, failure ends the cycle, Pending across → 12, 14, 15; §7.3 labels and switcher → 10, 19, 24. §8.1
remove → 2, 6, 17, 20; §8.2 removal sheet → 15 (acknowledge), 22, 23; hiding a soft-locked family → 10, 15; paused banner
→ 22, 24; named nowhere → 11; §8.3 leave/delete → 1, 3, 6, 17, 20; §8.4 → 10 (`RemoveFromPhoneAsync`), 12
(`DeleteAndReplaceAsync`), 14 (`POST /api/households`), 3, 6 (server). §9 → 13, 14, 15, 19. §10.1 endpoints → 6, 8;
§10.2 → 7; §10.3 migration, purge, active-only → 4, 5; §10.4 locks, accept order, deletion under locks, promotion row
count → 2, 3, 4, 5; §10.5 role test → 6; §10.6 Domain → 1. §11 placement → as listed (deviations: the share and consent
ports are Presentation ports, ruling 18; `family_memberships` is migration 0005, ruling 19; the phone's storage port is
split into `IFamilyStore` and `IFamilyRecords` so every task builds and the adapters land before their users). §12 tests →
each row: Domain (1), Api.Application (2, 3, 5), Api.Infrastructure (4, 5, the concurrency test with a held lock), Api
integration (6, 7, 8), Mobile.Application (10, 14, 15, 16, 17), Mobile.Infrastructure (11, 12, 13), Presentation and
Localization (18–22). §13 → 26. §14 → 27. §15 → the rulings.

**Placeholder scan.** No "TBD"/"TODO". Three places tell the implementer to match an existing helper rather than repeating
it: `Ui.Rounded`'s exact parameters (Task 24), the EF-generated migration's file name (Task 5), and the ADR line in
Task 27 Step 5, which depends on what §8 says then; each names exactly what to check.

**Type consistency.** Checked across tasks: `InvitationCode.Parse/FromRandom/Display/Value`, `Invitation.InvalidCode`,
`LeaveDecision`, `HouseholdErrors.*` (incl. `RateLimited`, `Malformed`), `IHouseholdMembershipStore.RunLockedAsync` and
`IMembershipTransaction.*`, `CreatedInvitation` (server: `Id, Code, ExpiresAt`; phone: `InvitationId, Code, Link, ExpiresAt`),
`FamilyMembership`, `PendingInvitation`, `JoiningMarker`, `IFamilyStore.ListAsync` returning null before a refresh,
`IFamilyRecords.CopyAsync(plan, user, firstStamp)`, `CopyResult(Copied, Skipped)`, `Families.RemoveFromPhoneAsync`,
`SyncStops`, `CycleReport.RefreshFamilies/NotAMember`, `SyncStatus.ShownStopped`, `JoinChoice.Source`,
`BringOutcome(Path, Copied, Skipped, Question)`, `FamilyAction`, `MemberRow(…, Name)`, `BringOverRequest`,
`SyncPrompter.ShowJoinAsync/ShowEnterCodeAsync/AskConsentAsync/JoinEnded`, `InvitationInbox.StoreHeldAsync/HeldLink`.

**Review Focus.** Each of the five lines has its pinned test in the owning task (Tasks 1, 3, 12, 13, 14, 16, 17, 20),
named in the Review Focus section.
