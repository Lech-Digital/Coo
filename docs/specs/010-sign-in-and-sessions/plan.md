# Sign-in and sessions (Increment 10) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or
> superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Sign in with Google from Settings; the server verifies the Google ID token (fail closed), takes over the
phone's provisional household, and issues per-device sessions (a 15-minute JWT and a rotating refresh token with reuse
detection); the same account works on a second phone; sign-out and account deletion work in the app; the API is
deployed with its new settings; the app is 0.10.0.

**Architecture:** Domain gains `Role`, `Membership` and `HouseholdSuccession` (ADR 0003 §6, §8). `Coo.Api.Application/Auth`
holds the sign-in, refresh, sign-out, me and delete-account use cases behind outbound ports (`IExternalIdentityVerifier`,
`IAuthNonceStore`, `IDeviceSessionStore`, `IAccountStore`, `ITokenIssuer`). `Coo.Api.Infrastructure` implements them:
ASP.NET Core Identity (no passwords, `Guid` keys) and the new tables in `Persistence`, the JWT issuer, the bearer
validation and the Google verifier in `Identity`. `Coo.Api` checks the settings at start-up (a pure function), maps the
endpoints, and rate-limits `/api/auth/*`. On the phone, `Coo.Mobile.Application/Auth` holds the account state and the
`SignIn` / `SignOut` / `DeleteAccount` / `RefreshAccount` use cases; `Coo.Mobile.Infrastructure/Auth` the HTTP clients,
the single-flight refreshing `DelegatingHandler` and the session codec; `Coo.Mobile.Platform/Auth` the Credential
Manager adapter and the device name; the host the `SecureStorage` adapter and the Account section in Settings.

**Tech Stack:** .NET 10, ASP.NET Core 10 (minimal APIs, JWT bearer, built-in rate limiter), ASP.NET Core Identity with
EF Core 10 + Npgsql, `Google.Apis.Auth`, `Microsoft.IdentityModel.JsonWebTokens`, MAUI 10.0.0 (Android),
AndroidX Credential Manager bindings, CommunityToolkit.Mvvm, xUnit 2.9 + NSubstitute + FluentAssertions 7,
Testcontainers PostgreSQL 17, `WebApplicationFactory`.

**Spec:** `docs/specs/010-sign-in-and-sessions/spec.md` (accepted 2026-10-02). Also read ADR 0003 (identity, sessions,
households), ADR 0001 §2 (dependency rules), and Spec 010a §5 (the Credential Manager findings).

## Global Constraints

- Work on `main` (owner's rule: only developer, unreleased). One commit per task; push at the end (Task 16); CI runs on
  push to `main`.
- Commits authored as `adziusmaster <adzius.lech@gmail.com>` (repo-local config, already set). **No `Co-Authored-By`
  trailer** and no "Generated with" line.
- Zero warnings (`TreatWarningsAsErrors`); `dotnet format Coo.Tests.slnf --verify-no-changes` passes; the architecture
  tests stay green.
- No `TimeProvider.System`, `DateTime.Now/UtcNow`, `DateTimeOffset.Now/UtcNow`, `Guid.NewGuid()`, parameterless
  `Guid.CreateVersion7()` or `System.Random` outside the two hosts (`BannedSymbols.txt`). Random secrets use
  `RandomNumberGenerator`. Times come from the injected `TimeProvider`.
- No MAUI types in `Coo.Mobile.Application`, `Coo.Mobile.Presentation` or `Coo.Mobile.Platform` (ADR 0001;
  `Coo.Mobile.Platform` deliberately has no MAUI reference). No `System.Text.Json` in the two Application projects or
  Presentation.
- In `Coo.Api.Infrastructure`, EF Core and Npgsql are used only in the `Coo.Api.Infrastructure.Persistence` namespace
  (and `ServiceCollectionExtensions`). Infrastructure types are `internal` except `ServiceCollectionExtensions`.
- Every user-facing string goes in `eng/strings/strings.py` in all seven languages (en, de, fr, es, it, pl, nl), then
  `python3 eng/strings/generate.py`. Copy is the spec's English, verbatim.
- Tests: `MethodName_StateUnderTest_ExpectedBehavior`, strict `// Arrange` / `// Act` / `// Assert`, mock only outbound
  ports (hand-written in-memory fakes or NSubstitute), build Domain objects for real. Tests that set a language join
  `[Collection(nameof(StringsState))]` and reset with `Strings.Use(LanguagePreference.System, null)` in `Dispose`.
- `CancellationToken` on every IO-bound method, passed down to IO.
- Error responses are codes, never server text: `auth.google-token-invalid`, `auth.refresh-invalid`, `auth.rate-limited`,
  `account.confirmation-required` (JSON `{ "code": "…" }`).
- Lifetimes (Q5): access token **15 minutes**; refresh token **180 days sliding** since last use; nonce **5 minutes**,
  single use; nonce rows deleted after **1 day**.
- Rate limit: **20 requests per minute per client address** on `/api/auth/*`, `429` with `auth.rate-limited`.
- Mobile package versions, exactly (Spec 010a §5): `Xamarin.AndroidX.Credentials` **1.5.0**,
  `Xamarin.AndroidX.Credentials.PlayServicesAuth` **1.5.0**, `Xamarin.GoogleAndroid.Libraries.Identity.GoogleId`
  **1.1.0.8**. Newer ones fail restore with MAUI 10.0.0 (`NU1608` / `NU1605` as errors).
- Build constants: Web client ID `282211746968-l54aogbkec1gf1g0rp0n7bk2mhbg2c7r.apps.googleusercontent.com`
  (project `coo-app-510411`), API base `https://api.coo.lechdigital.nl/`.
- Privacy URL `https://lechdigital.nl/projects/coo/privacy` (Q4). The deletion page
  `https://lechdigital.nl/projects/coo/delete-account` lives in the website repository; this plan does not build it.
- App version **0.10.0**, versionCode **13**; API version **0.10.0**.
- Run tests with `dotnet test Coo.Tests.slnf` on the global SDK (**Docker Desktop running** for the PostgreSQL tests).
  Before pushing, also `TZ=UTC dotnet test Coo.Tests.slnf`. Build the app with the MAUI SDK (`BUILD.md`); a Debug APK
  installed with `adb install` needs **`-p:EmbedAssembliesIntoApk=true`**.
- The agent never SSH-writes to `coldstart-prod` and never deploys. The owner runs those commands with `!`.

## Review Focus

1. **An access token after sign-out** (or after the phone signed in again and replaced its session) is still
   cryptographically valid for up to 15 minutes. `/api/me` with it must be refused at once (deliverable 6), so the bearer
   validation checks the device session on every request. Pinned in Task 8
   (`Me_AfterSignOut_RefusesTheOldAccessToken`).
2. **A refresh answered `429` or `5xx`** (the rate limit, a deploy restarting the API) means "try later". It must never
   sign the person out. Pinned in Task 11 (`RefreshAsync_TooManyRequests_ThrowsUnavailableNotRejected`) and
   (`SendAsync_RefreshUnreachable_KeepsTheSessionAndThrows`).
3. **Signing in again on the same phone** after a sign-out, or after an earlier `created` outcome, is `existing`, not
   `joined`, and replaces that phone's session instead of adding a second device row. Pinned in Task 2
   (`ExecuteAsync_SamePhoneAfterCreated_IsExisting`) and Task 5 (`CreateAsync_SameUserAndDevice_ReplacesTheSession`).
4. **Deleting the account from the second phone** (join pending): the household that goes is the account's, whose baby
   this phone has never seen, so the confirmation must not name this phone's baby. Pinned in Task 12
   (`DeletePromptAsync_JoinPending_DoesNotNameThisPhonesBaby`).
5. **A request with a body that is retried after a `401`** (`DELETE /api/account` with `{ "confirm": true }`) must be
   resent with the same body, or the server answers `account.confirmation-required`. Pinned in Task 11
   (`SendAsync_401OnARequestWithABody_RetriesWithTheSameBody`).
6. **Signing out while offline** must still sign out on the phone (the person asked for it); the server's revoke is best
   effort. Pinned in Task 10 (`ExecuteAsync_ServerUnreachable_StillSignsOutHere`).

## Rulings on what the spec leaves open

Recorded here so the reviewer can check them against the spec; Task 16 copies them into the spec's "Built and verified"
section.

- **`existing` versus `joined`:** the same phone is recognised when the request's household ID is the account's
  household, **or** this device ID has had a session for this user before (even a revoked one). Anything else is
  `joined`.
- **A malformed sign-in request** (empty device or household ID, empty token) is refused like a bad token: `401`
  `auth.google-token-invalid`. No new error code.
- **Signing keys:** `Auth:SigningKeys:<kid>` (one or two, base64, at least 32 bytes) plus an optional
  `Auth:ActiveSigningKeyId`, required only when two keys are configured. Outside Production, missing settings fall back to
  a random per-process key and no client ID (every sign-in is refused); Production refuses to start.
- **The bearer validation checks the device session per request** (Review Focus 1). Membership is not a claim (ADR 0003
  §4); this is the same per-request check ADR 0002 needs for membership later.
- **Where the code lives (deviations from spec §6, recorded in Task 16):** Identity's EF types (`CooUser`, the new
  records) live in `Coo.Api.Infrastructure/Persistence`, because the architecture test allows EF only there; the token
  issuer, bearer setup and Google verifier live in `Coo.Api.Infrastructure/Identity`. The session store is split: the
  codec (`SessionStore`) is in `Coo.Mobile.Infrastructure/Auth`, tested, behind an `ISecretStore` port that the host
  implements with MAUI `SecureStorage` (`Coo.Mobile/Auth/MauiSecretStore.cs`), as Spec 009 did for MAUI Essentials.
- **`households.created_by`** is nullable with `ON DELETE SET NULL`: households created before this increment have no
  creator, and a household that survives its creator (11b) keeps its row.
- **The deletion confirmation without a baby name** ("…your family's shared log. The entries on this phone stay here.")
  is used when the phone is join-pending or has no baby (Review Focus 4).
- **`401` without a token** carries no body (the bearer handler's default); the four codes cover the spec's errors.
- **Both settings go into the server's `.env`** as the spec says, though the client ID is not a secret.

---

## File Structure

| File | Responsibility |
|---|---|
| `src/Shared/Coo.Domain/Households/Membership.cs` (create) | `Role`, `Membership` and the ADR 0003 §6 rights |
| `src/Shared/Coo.Domain/Households/HouseholdSuccession.cs` (create) | What happens to a household when a member leaves (ADR 0003 §8) |
| `src/Shared/Coo.Contracts/Auth/AuthContracts.cs` (create) | Wire DTOs for `/api/auth`, `/api/me`, `/api/account` |
| `src/Backend/Coo.Api.Application/Auth/Ports.cs` (create) | The outbound ports and their records |
| `src/Backend/Coo.Api.Application/Auth/AuthSecrets.cs`, `AuthErrors.cs` (create) | Random tokens and their hashes; the error codes |
| `src/Backend/Coo.Api.Application/Auth/SignIn.cs` (create) | `IssueNonce`, `SignInWithGoogle` (the four outcomes) |
| `src/Backend/Coo.Api.Application/Auth/Sessions.cs` (create) | `RefreshSession`, `SignOutSession`, `PurgeAuthNonces` |
| `src/Backend/Coo.Api.Application/Auth/AccountUseCases.cs` (create) | `GetMe`, `DeleteAccount` |
| `src/Backend/Coo.Api.Infrastructure/Persistence/Records.cs`, `CooDbContext.cs` (modify) | `CooUser`, new tables, Identity's tables |
| `src/Backend/Coo.Api.Infrastructure/Persistence/Migrations/*_SignInAndSessions.cs` (generated) | The migration |
| `src/Backend/Coo.Api.Infrastructure/Persistence/AuthNonceStore.cs`, `DeviceSessionStore.cs`, `AccountStore.cs`, `RoleText.cs` (create) | The stores |
| `src/Backend/Coo.Api.Infrastructure/Identity/*.cs` (create) | `SigningKeyRing`, `JwtTokenIssuer`, `AccessTokenValidation`, `JwtBearerSetup`, `GoogleIdTokenVerifier` |
| `src/Backend/Coo.Api.Infrastructure/ServiceCollectionExtensions.cs` (modify) | Stores, Identity, `AddApiIdentity` |
| `src/Backend/Coo.Api/Hosting/StartupChecks.cs` (modify), `AuthSettings.cs` (create) | `SelectAuth`: the settings, and Production's refusal |
| `src/Backend/Coo.Api/Hosting/AuthEndpoints.cs`, `AuthNonceCleanupJob.cs` (create) | Endpoints, rate limiter, daily nonce clean-up |
| `src/Backend/Coo.Api/Program.cs`, `Coo.Api.csproj` (modify) | Wiring; version 0.10.0 |
| `tests/Coo.Api.Application.Tests/**` (create) | The new test project: fakes, sign-in, sessions, account |
| `tests/Coo.Api.Infrastructure.Tests/AuthStoreTests.cs`, `TokenTests.cs` (create), `PostgresServer.cs`, `SchemaGuardTests.cs` (modify) | Stores, migration, tokens, verifier |
| `tests/Coo.Api.IntegrationTests/TestApi.cs`, `AuthApiTests.cs`, `StartupChecksTests.cs` (create), `ApiTests.cs` (modify) | End to end |
| `deploy/docker-compose.prod.yml`, `deploy/.env.example`, `deploy/deploy-prod.sh`, `DEPLOY.md` (modify) | Production settings |
| `src/Mobile/Coo.Mobile.Application/Auth/Ports.cs`, `Account.cs`, `UseCases.cs` (create) | Ports, account state, use cases |
| `src/Mobile/Coo.Mobile.Infrastructure/Auth/*.cs` (create) | `SessionStore`, `AuthJsonContext`, `AuthHttp`, `AuthClient`, `AccountClient`, `TokenRefresher`, `AuthenticatingHandler` |
| `src/Mobile/Coo.Mobile.Presentation/Settings/AccountViewModel.cs` (create), `SettingsViewModel.cs` (modify) | The Account section; the privacy URL |
| `src/Mobile/Coo.Mobile.Presentation/Today/TapBurst.cs` (create) | Three quick taps (the debug entry) |
| `src/Mobile/Coo.Mobile.Platform/Auth/*.cs` (create) | `CredentialManagerSignInProvider`, `AndroidDeviceName` |
| `src/Mobile/Coo.Mobile/Auth/MauiSecretStore.cs`, `BuildConstants.cs` (create) | `SecureStorage`; the build constants |
| `src/Mobile/Coo.Mobile/Pages/SettingsPage.cs`, `Pages/SectionViews.cs`, `DebugScreen/DebugPage.cs`, `StartupPage.cs`, `MauiProgram.cs`, `Coo.Mobile.csproj` (modify) | The UI, the debug entry, wiring, version |
| `eng/strings/strings.py` (modify) + generated `.resx` | `Account.*`, `Action.Cancel` |
| `Directory.Packages.props`, `.github/dependabot.yml`, `Coo.slnx`, `Coo.Tests.slnf` (modify) | Packages, the new test project |

---

### Task 1: `Role`, `Membership` and `HouseholdSuccession` (Domain)

**Files:**
- Create: `src/Shared/Coo.Domain/Households/Membership.cs`
- Create: `src/Shared/Coo.Domain/Households/HouseholdSuccession.cs`
- Test: `tests/Coo.Domain.Tests/Households/MembershipTests.cs`

**Interfaces:**
- Consumes: `HouseholdId`, `UserId`, `GuidOrder` (`Coo.Domain.Common`, existing; `UserId` is reused as spec §4.2's
  `UserId`).
- Produces:
  - `public enum Role { Owner, Parent, Caregiver }`
  - `public sealed class Membership` with `HouseholdId HouseholdId`, `UserId UserId`, `Role Role`,
    `DateTimeOffset JoinedAt`, `bool IsParent` (Owner or Parent), `bool CanInvite`, `bool CanManageBaby`,
    `bool CanManageBilling`, `bool CanRemove(Membership other)`, and
    `public static Membership Create(HouseholdId householdId, UserId userId, Role role, DateTimeOffset joinedAt)`.
  - `public abstract record Succession;` with `HouseholdRemains`, `HouseholdDeleted`, `OwnershipPasses(UserId NewOwner)`.
  - `public static class HouseholdSuccession { public static Succession WhenLeaving(IReadOnlyCollection<Membership> members, UserId leaving); }`

- [ ] **Step 1: Write the failing tests**

`tests/Coo.Domain.Tests/Households/MembershipTests.cs`:

```csharp
using Coo.Domain.Common;
using Coo.Domain.Households;

namespace Coo.Domain.Tests.Households;

/// <summary>ADR 0003 §6 (what each role may do) and §8 (what happens to a household when a member leaves).</summary>
public sealed class MembershipTests
{
    private static readonly UserId Nanny = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000c3"));
    private static readonly HouseholdId OtherHousehold = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a2"));

    [Theory]
    [InlineData(Role.Owner, true)]
    [InlineData(Role.Parent, true)]
    [InlineData(Role.Caregiver, false)]
    public void Create_EachRole_GrantsTheRightsOfTheAdrTable(Role role, bool parentRights)
    {
        // Arrange
        var joinedAt = A.Now;

        // Act
        var membership = Membership.Create(A.Household, A.Parent, role, joinedAt);

        // Assert
        membership.IsParent.Should().Be(parentRights);
        membership.CanInvite.Should().Be(parentRights);
        membership.CanManageBaby.Should().Be(parentRights);
        membership.CanManageBilling.Should().Be(parentRights);
    }

    [Fact]
    public void Create_UndefinedRole_Throws()
    {
        // Arrange
        var role = (Role)42;

        // Act
        var act = () => Membership.Create(A.Household, A.Parent, role, A.Now);

        // Assert
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void CanRemove_ParentRemovingCaregiver_IsAllowed()
    {
        // Arrange
        var parent = Membership.Create(A.Household, A.Parent, Role.Parent, A.Now);
        var nanny = Membership.Create(A.Household, Nanny, Role.Caregiver, A.Now);

        // Act
        var allowed = parent.CanRemove(nanny);

        // Assert
        allowed.Should().BeTrue();
    }

    [Fact]
    public void CanRemove_ParentRemovingTheOwner_IsRefused()
    {
        // Arrange
        var owner = Membership.Create(A.Household, A.Parent, Role.Owner, A.Now);
        var parent = Membership.Create(A.Household, A.OtherParent, Role.Parent, A.Now);

        // Act
        var allowed = parent.CanRemove(owner);

        // Assert
        allowed.Should().BeFalse("the Owner cannot be removed (ADR 0003 §6)");
    }

    [Fact]
    public void CanRemove_CaregiverRemovingAnyone_IsRefused()
    {
        // Arrange
        var nanny = Membership.Create(A.Household, Nanny, Role.Caregiver, A.Now);
        var parent = Membership.Create(A.Household, A.OtherParent, Role.Parent, A.Now);

        // Act
        var allowed = nanny.CanRemove(parent);

        // Assert
        allowed.Should().BeFalse();
    }

    [Fact]
    public void CanRemove_Themselves_IsRefused()
    {
        // Arrange: leaving is not removing; it is HouseholdSuccession's business.
        var parent = Membership.Create(A.Household, A.OtherParent, Role.Parent, A.Now);

        // Act
        var allowed = parent.CanRemove(parent);

        // Assert
        allowed.Should().BeFalse();
    }

    [Fact]
    public void CanRemove_MemberOfAnotherHousehold_IsRefused()
    {
        // Arrange
        var owner = Membership.Create(A.Household, A.Parent, Role.Owner, A.Now);
        var elsewhere = Membership.Create(OtherHousehold, Nanny, Role.Caregiver, A.Now);

        // Act
        var allowed = owner.CanRemove(elsewhere);

        // Assert
        allowed.Should().BeFalse();
    }

    [Fact]
    public void WhenLeaving_OnlyTheOwner_DeletesTheHousehold()
    {
        // Arrange
        Membership[] members = [Membership.Create(A.Household, A.Parent, Role.Owner, A.Now)];

        // Act
        var succession = HouseholdSuccession.WhenLeaving(members, A.Parent);

        // Assert
        succession.Should().BeOfType<HouseholdDeleted>();
    }

    [Fact]
    public void WhenLeaving_OwnerWithTwoParents_PassesOwnershipToTheEarliestInsteadOfDeleting()
    {
        // Arrange: the transfer rule runs before any deletion (ADR 0003, Consequences).
        var later = new UserId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000c4"));
        Membership[] members =
        [
            Membership.Create(A.Household, A.Parent, Role.Owner, A.Now),
            Membership.Create(A.Household, later, Role.Parent, A.Now.AddDays(9)),
            Membership.Create(A.Household, A.OtherParent, Role.Parent, A.Now.AddDays(3)),
        ];

        // Act
        var succession = HouseholdSuccession.WhenLeaving(members, A.Parent);

        // Assert
        succession.Should().Be(new OwnershipPasses(A.OtherParent));
    }

    [Fact]
    public void WhenLeaving_OwnerWithOnlyACaregiver_DeletesTheHousehold()
    {
        // Arrange: the last parent leaves; a nanny alone does not keep the family's log.
        Membership[] members =
        [
            Membership.Create(A.Household, A.Parent, Role.Owner, A.Now),
            Membership.Create(A.Household, Nanny, Role.Caregiver, A.Now.AddDays(1)),
        ];

        // Act
        var succession = HouseholdSuccession.WhenLeaving(members, A.Parent);

        // Assert
        succession.Should().BeOfType<HouseholdDeleted>();
    }

    [Theory]
    [InlineData(Role.Parent)]
    [InlineData(Role.Caregiver)]
    public void WhenLeaving_NonOwnerWhileTheOwnerStays_LeavesTheHouseholdAsItIs(Role role)
    {
        // Arrange
        Membership[] members =
        [
            Membership.Create(A.Household, A.Parent, Role.Owner, A.Now),
            Membership.Create(A.Household, A.OtherParent, role, A.Now.AddDays(1)),
        ];

        // Act
        var succession = HouseholdSuccession.WhenLeaving(members, A.OtherParent);

        // Assert
        succession.Should().BeOfType<HouseholdRemains>();
    }

    [Fact]
    public void WhenLeaving_NotAMember_Throws()
    {
        // Arrange
        Membership[] members = [Membership.Create(A.Household, A.Parent, Role.Owner, A.Now)];

        // Act
        var act = () => HouseholdSuccession.WhenLeaving(members, Nanny);

        // Assert
        act.Should().Throw<ArgumentException>();
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test tests/Coo.Domain.Tests --filter "FullyQualifiedName~MembershipTests"`
Expected: build error, `Membership`, `Role` and `HouseholdSuccession` do not exist.

- [ ] **Step 3: Implement**

`src/Shared/Coo.Domain/Households/Membership.cs`:

```csharp
using Coo.Domain.Common;

namespace Coo.Domain.Households;

/// <summary>What a member may do in a household (ADR 0003 §6). Every role may log and view.</summary>
public enum Role
{
    /// <summary>The creator; exactly one per household.</summary>
    Owner,

    Parent,

    /// <summary>For example a nanny: logs and views, nothing else.</summary>
    Caregiver,
}

/// <summary>
/// A user's place in a household (ADR 0003 §6). A user may belong to several households (the nanny case). Until
/// Increment 11b every household has only its Owner.
/// </summary>
public sealed class Membership
{
    private Membership(HouseholdId householdId, UserId userId, Role role, DateTimeOffset joinedAt)
    {
        HouseholdId = householdId;
        UserId = userId;
        Role = role;
        JoinedAt = joinedAt;
    }

    public HouseholdId HouseholdId { get; }

    public UserId UserId { get; }

    public Role Role { get; }

    public DateTimeOffset JoinedAt { get; }

    /// <summary>An Owner or a Parent: "a parent" in ADR 0003 §8's deletion rules.</summary>
    public bool IsParent => Role is Role.Owner or Role.Parent;

    public bool CanInvite => IsParent;

    public bool CanManageBaby => IsParent;

    public bool CanManageBilling => IsParent;

    public static Membership Create(HouseholdId householdId, UserId userId, Role role, DateTimeOffset joinedAt) =>
        Enum.IsDefined(role)
            ? new Membership(householdId, userId, role, joinedAt)
            : throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown role.");

    /// <summary>An Owner or Parent may remove another member of the same household, except the Owner.</summary>
    public bool CanRemove(Membership other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return IsParent && other.HouseholdId == HouseholdId && other.UserId != UserId && other.Role != Role.Owner;
    }
}
```

`src/Shared/Coo.Domain/Households/HouseholdSuccession.cs`:

```csharp
using Coo.Domain.Common;

namespace Coo.Domain.Households;

/// <summary>What happens to a household when one of its members leaves (ADR 0003 §8).</summary>
public abstract record Succession;

/// <summary>The household goes on as it is.</summary>
public sealed record HouseholdRemains : Succession;

/// <summary>The leaver was the last parent: the household and all of its care data go.</summary>
public sealed record HouseholdDeleted : Succession;

/// <summary>The Owner leaves while parents remain: the parent who joined earliest becomes the Owner.</summary>
public sealed record OwnershipPasses(UserId NewOwner) : Succession;

public static class HouseholdSuccession
{
    private static readonly IComparer<Guid> ByGuidOrder = Comparer<Guid>.Create(GuidOrder.Compare);

    /// <summary>
    /// The ownership transfer is decided before any deletion: a household is deleted only when no Owner or Parent
    /// would remain.
    /// </summary>
    public static Succession WhenLeaving(IReadOnlyCollection<Membership> members, UserId leaving)
    {
        ArgumentNullException.ThrowIfNull(members);
        var leaver = members.SingleOrDefault(m => m.UserId == leaving)
            ?? throw new ArgumentException("The leaving user is not a member of this household.", nameof(leaving));

        var next = members
            .Where(m => m.UserId != leaving && m.IsParent)
            .OrderBy(m => m.JoinedAt)
            .ThenBy(m => m.UserId.Value, ByGuidOrder)
            .FirstOrDefault();

        if (next is null)
            return new HouseholdDeleted();
        return leaver.Role == Role.Owner ? new OwnershipPasses(next.UserId) : new HouseholdRemains();
    }
}
```

If `CA1012` fires on `Succession`, wrap it in `#pragma warning disable CA1012 // Abstract records need a constructor the subtypes can reach.` /
`#pragma warning restore CA1012`, as `CareLog/EntryDetails.cs` does.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Coo.Domain.Tests --filter "FullyQualifiedName~MembershipTests"`
Expected: PASS (15 tests). Then `dotnet test tests/Coo.Architecture.Tests`: PASS (the Domain still references nothing).

- [ ] **Step 5: Commit**

```bash
git add src/Shared/Coo.Domain/Households tests/Coo.Domain.Tests/Households/MembershipTests.cs
git commit -m "Domain: Role, Membership and HouseholdSuccession (Spec 010 §4.2, ADR 0003 §6, §8)"
```

---

### Task 2: The sign-in use cases and the new `Coo.Api.Application.Tests` project

**Files:**
- Create: `tests/Coo.Api.Application.Tests/Coo.Api.Application.Tests.csproj`
- Modify: `Coo.slnx` (add the project under `/tests/`), `Coo.Tests.slnf` (add its path)
- Create: `src/Backend/Coo.Api.Application/Auth/Ports.cs`
- Create: `src/Backend/Coo.Api.Application/Auth/AuthSecrets.cs`
- Create: `src/Backend/Coo.Api.Application/Auth/AuthErrors.cs`
- Create: `src/Backend/Coo.Api.Application/Auth/SignIn.cs`
- Test: `tests/Coo.Api.Application.Tests/Fakes.cs`, `tests/Coo.Api.Application.Tests/SignInTests.cs`

**Interfaces:**
- Consumes: Task 1's `Membership`, `Role`; `HouseholdId`, `UserId`, `DeviceId`, `Result<T>`, `DomainError`
  (`Coo.Domain.Common`).
- Produces (namespace `Coo.Api.Application.Auth`, all `public`):
  - `AuthErrors.GoogleTokenInvalid = "auth.google-token-invalid"`, `RefreshInvalid = "auth.refresh-invalid"`,
    `RateLimited = "auth.rate-limited"`, `ConfirmationRequired = "account.confirmation-required"`.
  - `AuthSecrets.NewToken() : string` (256 random bits, base64url) and `AuthSecrets.Hash(string) : string` (SHA-256,
    lowercase hex).
  - `AccessTokenClaims.User = "sub"`, `AccessTokenClaims.Session = "sid"`.
  - `readonly record struct DeviceSessionId(Guid Value)` (not empty).
  - `record ExternalIdentity(string Subject, string Email, string DisplayName, string Nonce)`;
    `interface IExternalIdentityVerifier { Task<ExternalIdentity?> VerifyAsync(string idToken, CancellationToken cancellationToken); }`
  - `interface IAuthNonceStore { Task AddAsync(string nonceHash, DateTimeOffset issuedAt, CancellationToken cancellationToken); Task<bool> TryConsumeAsync(string nonceHash, DateTimeOffset issuedNotBefore, DateTimeOffset now, CancellationToken cancellationToken); Task<int> DeleteIssuedBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken); }`
  - `record DeviceSession(DeviceSessionId Id, UserId UserId, DeviceId DeviceId, string DeviceName, DateTimeOffset CreatedAt, DateTimeOffset LastUsedAt, DateTimeOffset? RevokedAt)` with `bool IsRevoked`;
    `enum RefreshMatch { Current, Previous }`; `record RefreshLookup(DeviceSession Session, RefreshMatch Match)`;
    `interface IDeviceSessionStore { Task CreateAsync(DeviceSession session, string refreshHash, CancellationToken cancellationToken); Task<RefreshLookup?> FindByRefreshHashAsync(string refreshHash, CancellationToken cancellationToken); Task<bool> RotateAsync(DeviceSessionId id, string expectedHash, string newHash, DateTimeOffset now, CancellationToken cancellationToken); Task RevokeAsync(DeviceSessionId id, DateTimeOffset now, CancellationToken cancellationToken); Task<bool> IsActiveAsync(DeviceSessionId id, CancellationToken cancellationToken); Task<IReadOnlyList<DeviceSession>> ListForUserAsync(UserId user, CancellationToken cancellationToken); Task<bool> ExistsForDeviceAsync(UserId user, DeviceId device, CancellationToken cancellationToken); }`
  - `record UserAccount(UserId Id, string Email, string DisplayName)`;
    `record NewAccount(UserId UserId, string GoogleSubject, string Email, string DisplayName, HouseholdId Household, DateTimeOffset CreatedAt)`;
    `record OwnershipTransfer(HouseholdId Household, UserId NewOwner)`;
    `record AccountDeletionPlan(IReadOnlyList<HouseholdId> HouseholdsToDelete, IReadOnlyList<OwnershipTransfer> OwnershipTransfers)`;
    `interface IAccountStore { Task<UserAccount?> FindByGoogleSubjectAsync(string subject, CancellationToken cancellationToken); Task<UserAccount?> FindAsync(UserId id, CancellationToken cancellationToken); Task<bool> HouseholdExistsAsync(HouseholdId id, CancellationToken cancellationToken); Task CreateAccountAsync(NewAccount account, CancellationToken cancellationToken); Task CreateHouseholdAsync(HouseholdId household, UserId owner, DateTimeOffset now, CancellationToken cancellationToken); Task UpdateProfileAsync(UserAccount account, CancellationToken cancellationToken); Task<IReadOnlyList<Membership>> ListMembershipsOfUserAsync(UserId user, CancellationToken cancellationToken); Task<IReadOnlyList<Membership>> ListMembersAsync(HouseholdId household, CancellationToken cancellationToken); Task DeleteAccountAsync(UserId user, AccountDeletionPlan plan, CancellationToken cancellationToken); }`
  - `record AccessToken(string Value, DateTimeOffset ExpiresAt)`;
    `interface ITokenIssuer { AccessToken Issue(UserId user, DeviceSessionId session, DateTimeOffset now); }`
  - `record GoogleSignInCommand(string IdToken, string DeviceName, DeviceId DeviceId, HouseholdId ProvisionalHouseholdId)`;
    `enum SignInOutcome { Adopted, Created, Existing, Joined }`;
    `record IssuedSession(DeviceSessionId Id, AccessToken AccessToken, string RefreshToken)`;
    `record SignInResult(IssuedSession Session, UserAccount User, HouseholdId Household, SignInOutcome Outcome)`.
  - `sealed class IssueNonce(IAuthNonceStore nonces, TimeProvider time)` with `Task<string> ExecuteAsync(CancellationToken)`.
  - `sealed class SignInWithGoogle(IExternalIdentityVerifier verifier, IAuthNonceStore nonces, IAccountStore accounts, IDeviceSessionStore sessions, ITokenIssuer tokens, TimeProvider time)` with
    `Task<Result<SignInResult>> ExecuteAsync(GoogleSignInCommand command, CancellationToken cancellationToken)`,
    `static readonly TimeSpan NonceLifetime` (5 min), `const int DeviceNameMaxLength = 60`, and
    `public static Task<IssuedSession> StartSessionAsync(IDeviceSessionStore sessions, ITokenIssuer tokens, UserId user, DeviceId device, string deviceName, DateTimeOffset now, CancellationToken cancellationToken)`
    (a new session and its first tokens; a previous session of the user on that device is replaced).

- [ ] **Step 1: Create the test project and register it**

`tests/Coo.Api.Application.Tests/Coo.Api.Application.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.TimeProvider.Testing" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="../../src/Backend/Coo.Api.Application/Coo.Api.Application.csproj" />
  </ItemGroup>

</Project>
```

In `Coo.slnx`, inside `<Folder Name="/tests/">`, add (alphabetically first):

```xml
    <Project Path="tests/Coo.Api.Application.Tests/Coo.Api.Application.Tests.csproj" />
```

In `Coo.Tests.slnf`, add `"tests/Coo.Api.Application.Tests/Coo.Api.Application.Tests.csproj",` before
`"tests/Coo.Api.Infrastructure.Tests/Coo.Api.Infrastructure.Tests.csproj"`. (`SolutionFilterTests` fails if the filter
misses a project; the Domain already lists `Coo.Api.Application.Tests` as a friend assembly.)

- [ ] **Step 2: Write the fakes**

`tests/Coo.Api.Application.Tests/Fakes.cs`:

```csharp
using System.Globalization;
using Coo.Api.Application.Auth;
using Coo.Domain.Common;
using Coo.Domain.Households;
using Microsoft.Extensions.Time.Testing;

namespace Coo.Api.Application.Tests;

// The outbound ports, in memory. Only ports are faked; Domain objects are real.

/// <summary>Google, as far as the use cases can tell: a token it issued verifies; any other does not.</summary>
internal sealed class FakeGoogle : IExternalIdentityVerifier
{
    private readonly Dictionary<string, ExternalIdentity> _tokens = new(StringComparer.Ordinal);

    public string Issue(string subject, string nonce)
    {
        var token = "token-" + (_tokens.Count + 1).ToString(CultureInfo.InvariantCulture);
        _tokens[token] = new ExternalIdentity(subject, subject + "@example.com", "Parent " + subject, nonce);
        return token;
    }

    public Task<ExternalIdentity?> VerifyAsync(string idToken, CancellationToken cancellationToken) =>
        Task.FromResult(_tokens.GetValueOrDefault(idToken));
}

internal sealed class FakeNonces : IAuthNonceStore
{
    public Dictionary<string, (DateTimeOffset IssuedAt, DateTimeOffset? ConsumedAt)> Rows { get; } = new(StringComparer.Ordinal);

    public Task AddAsync(string nonceHash, DateTimeOffset issuedAt, CancellationToken cancellationToken)
    {
        Rows[nonceHash] = (issuedAt, null);
        return Task.CompletedTask;
    }

    public Task<bool> TryConsumeAsync(string nonceHash, DateTimeOffset issuedNotBefore, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!Rows.TryGetValue(nonceHash, out var row) || row.ConsumedAt is not null || row.IssuedAt < issuedNotBefore)
            return Task.FromResult(false);
        Rows[nonceHash] = (row.IssuedAt, now);
        return Task.FromResult(true);
    }

    public Task<int> DeleteIssuedBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        var old = Rows.Where(r => r.Value.IssuedAt < cutoff).Select(r => r.Key).ToList();
        foreach (var key in old)
            Rows.Remove(key);
        return Task.FromResult(old.Count);
    }
}

internal sealed class FakeSessions : IDeviceSessionStore
{
    public List<Row> Rows { get; } = [];

    public Task CreateAsync(DeviceSession session, string refreshHash, CancellationToken cancellationToken)
    {
        Rows.RemoveAll(r => r.Session.UserId == session.UserId && r.Session.DeviceId == session.DeviceId);
        Rows.Add(new Row(session, refreshHash, null));
        return Task.CompletedTask;
    }

    public Task<RefreshLookup?> FindByRefreshHashAsync(string refreshHash, CancellationToken cancellationToken)
    {
        var current = Rows.FirstOrDefault(r => r.RefreshHash == refreshHash);
        if (current is not null)
            return Task.FromResult<RefreshLookup?>(new RefreshLookup(current.Session, RefreshMatch.Current));
        var previous = Rows.FirstOrDefault(r => r.PreviousRefreshHash == refreshHash);
        return Task.FromResult(previous is null ? null : new RefreshLookup(previous.Session, RefreshMatch.Previous));
    }

    public Task<bool> RotateAsync(DeviceSessionId id, string expectedHash, string newHash, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var index = Rows.FindIndex(r => r.Session.Id == id && r.RefreshHash == expectedHash && !r.Session.IsRevoked);
        if (index < 0)
            return Task.FromResult(false);
        var row = Rows[index];
        Rows[index] = new Row(row.Session with { LastUsedAt = now }, newHash, expectedHash);
        return Task.FromResult(true);
    }

    public Task RevokeAsync(DeviceSessionId id, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var index = Rows.FindIndex(r => r.Session.Id == id && !r.Session.IsRevoked);
        if (index >= 0)
            Rows[index] = Rows[index] with { Session = Rows[index].Session with { RevokedAt = now } };
        return Task.CompletedTask;
    }

    public Task<bool> IsActiveAsync(DeviceSessionId id, CancellationToken cancellationToken) =>
        Task.FromResult(Rows.Any(r => r.Session.Id == id && !r.Session.IsRevoked));

    public Task<IReadOnlyList<DeviceSession>> ListForUserAsync(UserId user, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DeviceSession>>([.. Rows.Where(r => r.Session.UserId == user).Select(r => r.Session)]);

    public Task<bool> ExistsForDeviceAsync(UserId user, DeviceId device, CancellationToken cancellationToken) =>
        Task.FromResult(Rows.Any(r => r.Session.UserId == user && r.Session.DeviceId == device));

    public Row Of(DeviceSessionId id) => Rows.Single(r => r.Session.Id == id);

    internal sealed record Row(DeviceSession Session, string RefreshHash, string? PreviousRefreshHash);
}

internal sealed class FakeAccounts : IAccountStore
{
    public Dictionary<string, UserAccount> BySubject { get; } = new(StringComparer.Ordinal);

    public HashSet<HouseholdId> Households { get; } = [];

    public List<Membership> Memberships { get; } = [];

    public (UserId User, AccountDeletionPlan Plan)? Deleted { get; private set; }

    public Task<UserAccount?> FindByGoogleSubjectAsync(string subject, CancellationToken cancellationToken) =>
        Task.FromResult(BySubject.GetValueOrDefault(subject));

    public Task<UserAccount?> FindAsync(UserId id, CancellationToken cancellationToken) =>
        Task.FromResult(BySubject.Values.FirstOrDefault(u => u.Id == id));

    public Task<bool> HouseholdExistsAsync(HouseholdId id, CancellationToken cancellationToken) =>
        Task.FromResult(Households.Contains(id));

    public Task CreateAccountAsync(NewAccount account, CancellationToken cancellationToken)
    {
        BySubject[account.GoogleSubject] = new UserAccount(account.UserId, account.Email, account.DisplayName);
        return CreateHouseholdAsync(account.Household, account.UserId, account.CreatedAt, cancellationToken);
    }

    public Task CreateHouseholdAsync(HouseholdId household, UserId owner, DateTimeOffset now, CancellationToken cancellationToken)
    {
        Households.Add(household);
        Memberships.Add(Membership.Create(household, owner, Role.Owner, now));
        return Task.CompletedTask;
    }

    public Task UpdateProfileAsync(UserAccount account, CancellationToken cancellationToken)
    {
        var subject = BySubject.Single(p => p.Value.Id == account.Id).Key;
        BySubject[subject] = account;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Membership>> ListMembershipsOfUserAsync(UserId user, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Membership>>([.. Memberships.Where(m => m.UserId == user)]);

    public Task<IReadOnlyList<Membership>> ListMembersAsync(HouseholdId household, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Membership>>([.. Memberships.Where(m => m.HouseholdId == household)]);

    public Task DeleteAccountAsync(UserId user, AccountDeletionPlan plan, CancellationToken cancellationToken)
    {
        Deleted = (user, plan);
        foreach (var subject in BySubject.Where(p => p.Value.Id == user).Select(p => p.Key).ToList())
            BySubject.Remove(subject);
        Memberships.RemoveAll(m => m.UserId == user || plan.HouseholdsToDelete.Contains(m.HouseholdId));
        foreach (var household in plan.HouseholdsToDelete)
            Households.Remove(household);
        return Task.CompletedTask;
    }
}

internal sealed class FakeTokens : ITokenIssuer
{
    public AccessToken Issue(UserId user, DeviceSessionId session, DateTimeOffset now) =>
        new("access-" + session.Value.ToString("N", CultureInfo.InvariantCulture), now + TimeSpan.FromMinutes(15));
}

/// <summary>The use cases over the fakes, and a whole sign-in as the phone does it.</summary>
internal sealed class AuthKit
{
    public static readonly DateTimeOffset Start = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    public FakeTimeProvider Time { get; } = new(Start);

    public FakeGoogle Google { get; } = new();

    public FakeNonces Nonces { get; } = new();

    public FakeSessions Sessions { get; } = new();

    public FakeAccounts Accounts { get; } = new();

    public FakeTokens Tokens { get; } = new();

    public IssueNonce IssueNonce() => new(Nonces, Time);

    public SignInWithGoogle SignIn() => new(Google, Nonces, Accounts, Sessions, Tokens, Time);

    /// <summary>A nonce from the server, a Google token carrying it, then <c>/api/auth/google</c>.</summary>
    public async Task<SignInResult> SignInAsync(string subject, DeviceId device, HouseholdId household)
    {
        var nonce = await IssueNonce().ExecuteAsync(CancellationToken.None);
        var result = await SignIn().ExecuteAsync(
            new GoogleSignInCommand(Google.Issue(subject, nonce), "Pixel 10", device, household), CancellationToken.None);
        return result.Value;
    }
}
```

- [ ] **Step 3: Write the failing sign-in tests**

`tests/Coo.Api.Application.Tests/SignInTests.cs`:

```csharp
using Coo.Api.Application.Auth;
using Coo.Domain.Common;
using Coo.Domain.Households;

namespace Coo.Api.Application.Tests;

/// <summary>Spec 010 §3.1 (fail closed) and §3.2 (the four outcomes). The Google verifier is a fake of the port.</summary>
public sealed class SignInTests
{
    private static readonly DeviceId PhoneA = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000d1"));
    private static readonly DeviceId PhoneB = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000d2"));
    private static readonly HouseholdId HouseholdA = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a1"));
    private static readonly HouseholdId HouseholdB = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a2"));

    private readonly AuthKit _kit = new();

    [Fact]
    public async Task ExecuteAsync_NewUserUnusedHouseholdId_AdoptsThePhonesHouseholdAsOwner()
    {
        // Arrange
        var subject = "google-1";

        // Act
        var result = await _kit.SignInAsync(subject, PhoneA, HouseholdA);

        // Assert
        result.Outcome.Should().Be(SignInOutcome.Adopted);
        result.Household.Should().Be(HouseholdA);
        _kit.Accounts.Memberships.Should().ContainSingle().Which.Should().Match<Membership>(m =>
            m.HouseholdId == HouseholdA && m.UserId == result.User.Id && m.Role == Role.Owner);
        _kit.Sessions.Rows.Should().ContainSingle().Which.Session.DeviceName.Should().Be("Pixel 10");
    }

    [Fact]
    public async Task ExecuteAsync_NewUserHouseholdIdTaken_CreatesAHouseholdWithANewId()
    {
        // Arrange: practically never (UUIDv7), but the server must not join a stranger's household.
        _kit.Accounts.Households.Add(HouseholdA);

        // Act
        var result = await _kit.SignInAsync("google-1", PhoneA, HouseholdA);

        // Assert
        result.Outcome.Should().Be(SignInOutcome.Created);
        result.Household.Should().NotBe(HouseholdA);
        _kit.Accounts.Households.Should().Contain(result.Household);
    }

    [Fact]
    public async Task ExecuteAsync_ExistingUserSamePhone_IsExistingAndKeepsOneSessionForThePhone()
    {
        // Arrange
        var first = await _kit.SignInAsync("google-1", PhoneA, HouseholdA);

        // Act
        var again = await _kit.SignInAsync("google-1", PhoneA, HouseholdA);

        // Assert
        again.Outcome.Should().Be(SignInOutcome.Existing);
        again.Household.Should().Be(first.Household);
        _kit.Sessions.Rows.Should().ContainSingle().Which.Session.Id.Should().Be(again.Session.Id);
    }

    [Fact]
    public async Task ExecuteAsync_ExistingUserOtherPhone_IsJoinedAndAddsOnlyASession()
    {
        // Arrange: the owner's account on his girlfriend's phone, which has its own provisional household.
        var first = await _kit.SignInAsync("google-1", PhoneA, HouseholdA);

        // Act
        var second = await _kit.SignInAsync("google-1", PhoneB, HouseholdB);

        // Assert
        second.Outcome.Should().Be(SignInOutcome.Joined);
        second.Household.Should().Be(first.Household);
        _kit.Accounts.Households.Should().Equal(HouseholdA);
        _kit.Sessions.Rows.Select(r => r.Session.DeviceId).Should().BeEquivalentTo([PhoneA, PhoneB]);
    }

    [Fact]
    public async Task ExecuteAsync_SamePhoneAfterCreated_IsExisting()
    {
        // Arrange: the phone's ID was taken, so the account's household has another ID; the phone still sends its own.
        _kit.Accounts.Households.Add(HouseholdA);
        await _kit.SignInAsync("google-1", PhoneA, HouseholdA);

        // Act
        var again = await _kit.SignInAsync("google-1", PhoneA, HouseholdA);

        // Assert
        again.Outcome.Should().Be(SignInOutcome.Existing, "the device has had a session for this user before");
        _kit.Sessions.Rows.Should().ContainSingle();
    }

    [Fact]
    public async Task ExecuteAsync_UsedNonce_IsRefusedAndCreatesNothing()
    {
        // Arrange: a second Google token carrying a nonce that the first sign-in consumed.
        var nonce = await _kit.IssueNonce().ExecuteAsync(CancellationToken.None);
        await _kit.SignIn().ExecuteAsync(new GoogleSignInCommand(_kit.Google.Issue("google-1", nonce), "Pixel 10", PhoneA, HouseholdA), CancellationToken.None);
        var replay = new GoogleSignInCommand(_kit.Google.Issue("google-2", nonce), "Pixel 9", PhoneB, HouseholdB);

        // Act
        var result = await _kit.SignIn().ExecuteAsync(replay, CancellationToken.None);

        // Assert
        result.Error!.Code.Should().Be(AuthErrors.GoogleTokenInvalid);
        _kit.Accounts.BySubject.Should().NotContainKey("google-2");
        _kit.Sessions.Rows.Should().ContainSingle();
    }

    [Fact]
    public async Task ExecuteAsync_NonceOlderThanFiveMinutes_IsRefusedAndCreatesNothing()
    {
        // Arrange
        var nonce = await _kit.IssueNonce().ExecuteAsync(CancellationToken.None);
        _kit.Time.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));

        // Act
        var result = await _kit.SignIn().ExecuteAsync(
            new GoogleSignInCommand(_kit.Google.Issue("google-1", nonce), "Pixel 10", PhoneA, HouseholdA), CancellationToken.None);

        // Assert
        result.Error!.Code.Should().Be(AuthErrors.GoogleTokenInvalid);
        _kit.Accounts.BySubject.Should().BeEmpty();
        _kit.Sessions.Rows.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_UnknownNonce_IsRefusedAndCreatesNothing()
    {
        // Arrange: a valid Google token whose nonce this server never issued.
        var token = _kit.Google.Issue("google-1", "a-nonce-from-elsewhere");

        // Act
        var result = await _kit.SignIn().ExecuteAsync(new GoogleSignInCommand(token, "Pixel 10", PhoneA, HouseholdA), CancellationToken.None);

        // Assert
        result.Error!.Code.Should().Be(AuthErrors.GoogleTokenInvalid);
        _kit.Accounts.Households.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_TokenTheVerifierRejects_IsRefusedAndLeavesTheNonceUnused()
    {
        // Arrange: a bad signature, the wrong audience, an unverified email, or Google's keys unreachable all look
        // the same here: the verifier returns nothing.
        var nonce = await _kit.IssueNonce().ExecuteAsync(CancellationToken.None);

        // Act
        var result = await _kit.SignIn().ExecuteAsync(new GoogleSignInCommand("forged", "Pixel 10", PhoneA, HouseholdA), CancellationToken.None);

        // Assert
        result.Error!.Code.Should().Be(AuthErrors.GoogleTokenInvalid);
        _kit.Nonces.Rows[AuthSecrets.Hash(nonce)].ConsumedAt.Should().BeNull();
        _kit.Accounts.BySubject.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_NewSession_StoresOnlyTheRefreshTokensHash()
    {
        // Arrange
        var subject = "google-1";

        // Act
        var result = await _kit.SignInAsync(subject, PhoneA, HouseholdA);

        // Assert
        var row = _kit.Sessions.Of(result.Session.Id);
        row.RefreshHash.Should().Be(AuthSecrets.Hash(result.Session.RefreshToken)).And.NotBe(result.Session.RefreshToken);
        result.Session.AccessToken.ExpiresAt.Should().Be(AuthKit.Start + TimeSpan.FromMinutes(15));
    }

    [Theory]
    [InlineData("", "Phone")]
    [InlineData("   ", "Phone")]
    [InlineData("  Pixel 10  ", "Pixel 10")]
    public async Task ExecuteAsync_DeviceName_IsTrimmedWithAFallback(string sent, string stored)
    {
        // Arrange
        var nonce = await _kit.IssueNonce().ExecuteAsync(CancellationToken.None);

        // Act
        var result = await _kit.SignIn().ExecuteAsync(
            new GoogleSignInCommand(_kit.Google.Issue("google-1", nonce), sent, PhoneA, HouseholdA), CancellationToken.None);

        // Assert
        _kit.Sessions.Of(result.Value.Session.Id).Session.DeviceName.Should().Be(stored);
    }

    [Fact]
    public async Task ExecuteAsync_IssuedNonce_StoresOnlyItsHash()
    {
        // Arrange
        var issue = _kit.IssueNonce();

        // Act
        var nonce = await issue.ExecuteAsync(CancellationToken.None);

        // Assert
        _kit.Nonces.Rows.Should().ContainKey(AuthSecrets.Hash(nonce)).And.NotContainKey(nonce);
        nonce.Length.Should().Be(43, "256 bits in base64url without padding");
    }
}
```

- [ ] **Step 4: Run them to verify they fail**

Run: `dotnet test tests/Coo.Api.Application.Tests`
Expected: build errors, the `Coo.Api.Application.Auth` types do not exist.

- [ ] **Step 5: Implement the ports, the secrets and the errors**

`src/Backend/Coo.Api.Application/Auth/AuthErrors.cs`:

```csharp
namespace Coo.Api.Application.Auth;

/// <summary>The error codes of Spec 010 §4.1. Once released, a code is never renamed (ADR 0006).</summary>
public static class AuthErrors
{
    public const string GoogleTokenInvalid = "auth.google-token-invalid";
    public const string RefreshInvalid = "auth.refresh-invalid";
    public const string RateLimited = "auth.rate-limited";
    public const string ConfirmationRequired = "account.confirmation-required";
}

/// <summary>The access token's claims (ADR 0003 §4). The key ID travels in the token's header as <c>kid</c>.</summary>
public static class AccessTokenClaims
{
    public const string User = "sub";
    public const string Session = "sid";
}
```

`src/Backend/Coo.Api.Application/Auth/AuthSecrets.cs`:

```csharp
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Coo.Api.Application.Auth;

/// <summary>Nonces and refresh tokens (ADR 0003 §4): 256 random bits; the database keeps only their SHA-256.</summary>
public static class AuthSecrets
{
    public static string NewToken() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    public static string Hash(string secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
    }
}
```

`src/Backend/Coo.Api.Application/Auth/Ports.cs`:

```csharp
using Coo.Domain.Common;
using Coo.Domain.Households;

namespace Coo.Api.Application.Auth;

/// <summary>One device session (Spec 010 §4.2): one per phone per user.</summary>
public readonly record struct DeviceSessionId
{
    public DeviceSessionId(Guid value) =>
        Value = value == Guid.Empty ? throw new ArgumentException("A session ID cannot be Guid.Empty.", nameof(value)) : value;

    public Guid Value { get; }

    public override string ToString() => Value.ToString();
}

/// <summary>What a verified Google ID token says about the person (ADR 0003 §3: only these).</summary>
public sealed record ExternalIdentity(string Subject, string Email, string DisplayName, string Nonce);

/// <summary>
/// Verifies a Google ID token (ADR 0003 §5): signature, <c>iss</c>, <c>aud</c>, expiry, <c>email_verified</c>. Returns
/// null on any failure, including Google's keys being unreachable. The nonce is checked by the use case.
/// </summary>
public interface IExternalIdentityVerifier
{
    Task<ExternalIdentity?> VerifyAsync(string idToken, CancellationToken cancellationToken);
}

/// <summary><c>auth_nonces</c>: single-use sign-in nonces, by hash.</summary>
public interface IAuthNonceStore
{
    Task AddAsync(string nonceHash, DateTimeOffset issuedAt, CancellationToken cancellationToken);

    /// <summary>Marks the nonce used, only if it is unused and was issued at or after <paramref name="issuedNotBefore"/>. True when this call consumed it.</summary>
    Task<bool> TryConsumeAsync(string nonceHash, DateTimeOffset issuedNotBefore, DateTimeOffset now, CancellationToken cancellationToken);

    Task<int> DeleteIssuedBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken);
}

public sealed record DeviceSession(
    DeviceSessionId Id, UserId UserId, DeviceId DeviceId, string DeviceName, DateTimeOffset CreatedAt, DateTimeOffset LastUsedAt,
    DateTimeOffset? RevokedAt)
{
    public bool IsRevoked => RevokedAt is not null;
}

/// <summary>Whether a refresh token is the session's current one, or the one it replaced (presenting that is theft).</summary>
public enum RefreshMatch
{
    Current,
    Previous,
}

public sealed record RefreshLookup(DeviceSession Session, RefreshMatch Match);

/// <summary><c>device_sessions</c> (ADR 0003 §4): refresh tokens are stored only as hashes.</summary>
public interface IDeviceSessionStore
{
    /// <summary>Stores a new session. An earlier session of the same user on the same device is replaced.</summary>
    Task CreateAsync(DeviceSession session, string refreshHash, CancellationToken cancellationToken);

    Task<RefreshLookup?> FindByRefreshHashAsync(string refreshHash, CancellationToken cancellationToken);

    /// <summary>
    /// Atomically, only if the session is not revoked and its current hash is <paramref name="expectedHash"/>: the current
    /// hash becomes the previous one, <paramref name="newHash"/> the current one, and the session was last used now.
    /// </summary>
    Task<bool> RotateAsync(DeviceSessionId id, string expectedHash, string newHash, DateTimeOffset now, CancellationToken cancellationToken);

    Task RevokeAsync(DeviceSessionId id, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>For every authenticated request: a revoked or replaced session is refused at once.</summary>
    Task<bool> IsActiveAsync(DeviceSessionId id, CancellationToken cancellationToken);

    /// <summary>Every session of the user, revoked ones included.</summary>
    Task<IReadOnlyList<DeviceSession>> ListForUserAsync(UserId user, CancellationToken cancellationToken);

    /// <summary>Whether this device has had a session for this user, revoked or not.</summary>
    Task<bool> ExistsForDeviceAsync(UserId user, DeviceId device, CancellationToken cancellationToken);
}

public sealed record UserAccount(UserId Id, string Email, string DisplayName);

public sealed record NewAccount(UserId UserId, string GoogleSubject, string Email, string DisplayName, HouseholdId Household, DateTimeOffset CreatedAt);

public sealed record OwnershipTransfer(HouseholdId Household, UserId NewOwner);

public sealed record AccountDeletionPlan(IReadOnlyList<HouseholdId> HouseholdsToDelete, IReadOnlyList<OwnershipTransfer> OwnershipTransfers);

/// <summary>Users (ASP.NET Core Identity, ADR 0003 §3), households and memberships.</summary>
public interface IAccountStore
{
    Task<UserAccount?> FindByGoogleSubjectAsync(string subject, CancellationToken cancellationToken);

    Task<UserAccount?> FindAsync(UserId id, CancellationToken cancellationToken);

    Task<bool> HouseholdExistsAsync(HouseholdId id, CancellationToken cancellationToken);

    /// <summary>In one transaction: the user, their Google login, the household (created by them), and their Owner membership.</summary>
    Task CreateAccountAsync(NewAccount account, CancellationToken cancellationToken);

    /// <summary>A household and its Owner membership, for a user who has none.</summary>
    Task CreateHouseholdAsync(HouseholdId household, UserId owner, DateTimeOffset now, CancellationToken cancellationToken);

    Task UpdateProfileAsync(UserAccount account, CancellationToken cancellationToken);

    Task<IReadOnlyList<Membership>> ListMembershipsOfUserAsync(UserId user, CancellationToken cancellationToken);

    Task<IReadOnlyList<Membership>> ListMembersAsync(HouseholdId household, CancellationToken cancellationToken);

    /// <summary>
    /// In one transaction: the ownership transfers, the households to delete with all of their care data, then the user
    /// with their logins, device sessions and memberships (Spec 010 §5).
    /// </summary>
    Task DeleteAccountAsync(UserId user, AccountDeletionPlan plan, CancellationToken cancellationToken);
}

public sealed record AccessToken(string Value, DateTimeOffset ExpiresAt);

/// <summary>Issues the 15-minute access token (ADR 0003 §4), signed with the active key.</summary>
public interface ITokenIssuer
{
    AccessToken Issue(UserId user, DeviceSessionId session, DateTimeOffset now);
}
```

- [ ] **Step 6: Implement the use cases**

`src/Backend/Coo.Api.Application/Auth/SignIn.cs`:

```csharp
using Coo.Domain.Common;

namespace Coo.Api.Application.Auth;

public sealed record GoogleSignInCommand(string IdToken, string DeviceName, DeviceId DeviceId, HouseholdId ProvisionalHouseholdId);

/// <summary>Which case of Spec 010 §3.2 happened.</summary>
public enum SignInOutcome
{
    Adopted,
    Created,
    Existing,
    Joined,
}

public sealed record IssuedSession(DeviceSessionId Id, AccessToken AccessToken, string RefreshToken);

public sealed record SignInResult(IssuedSession Session, UserAccount User, HouseholdId Household, SignInOutcome Outcome);

/// <summary><c>POST /api/auth/nonce</c>: a fresh single-use nonce. Only its hash is stored.</summary>
public sealed class IssueNonce(IAuthNonceStore nonces, TimeProvider time)
{
    public async Task<string> ExecuteAsync(CancellationToken cancellationToken)
    {
        var nonce = AuthSecrets.NewToken();
        await nonces.AddAsync(AuthSecrets.Hash(nonce), time.GetUtcNow(), cancellationToken);
        return nonce;
    }
}

/// <summary><c>POST /api/auth/google</c> (Spec 010 §3).</summary>
public sealed class SignInWithGoogle(
    IExternalIdentityVerifier verifier, IAuthNonceStore nonces, IAccountStore accounts, IDeviceSessionStore sessions,
    ITokenIssuer tokens, TimeProvider time)
{
    public const int DeviceNameMaxLength = 60;
    public static readonly TimeSpan NonceLifetime = TimeSpan.FromMinutes(5);

    public async Task<Result<SignInResult>> ExecuteAsync(GoogleSignInCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        // Fails closed (ADR 0003 §5): a token the verifier does not vouch for, or a nonce that is not fresh and unused,
        // creates nothing. The nonce is consumed only once the token has verified.
        var identity = await verifier.VerifyAsync(command.IdToken, cancellationToken);
        if (identity is null)
            return Result<SignInResult>.Failure(AuthErrors.GoogleTokenInvalid);
        var now = time.GetUtcNow();
        if (!await nonces.TryConsumeAsync(AuthSecrets.Hash(identity.Nonce), now - NonceLifetime, now, cancellationToken))
            return Result<SignInResult>.Failure(AuthErrors.GoogleTokenInvalid);

        var (user, household, outcome) = await FindOrCreateAsync(identity, command, now, cancellationToken);
        var session = await StartSessionAsync(sessions, tokens, user.Id, command.DeviceId, DeviceName(command.DeviceName), now, cancellationToken);
        return new SignInResult(session, user, household, outcome);
    }

    /// <summary>A new device session and its first tokens. A previous session of this user on this device is replaced.</summary>
    public static async Task<IssuedSession> StartSessionAsync(
        IDeviceSessionStore sessions, ITokenIssuer tokens, UserId user, DeviceId device, string deviceName, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(tokens);
        var id = new DeviceSessionId(Guid.CreateVersion7(now));
        var refreshToken = AuthSecrets.NewToken();
        await sessions.CreateAsync(new DeviceSession(id, user, device, deviceName, now, now, null), AuthSecrets.Hash(refreshToken), cancellationToken);
        return new IssuedSession(id, tokens.Issue(user, id, now), refreshToken);
    }

    private async Task<(UserAccount User, HouseholdId Household, SignInOutcome Outcome)> FindOrCreateAsync(
        ExternalIdentity identity, GoogleSignInCommand command, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var user = await accounts.FindByGoogleSubjectAsync(identity.Subject, cancellationToken);
        if (user is null)
        {
            var (household, outcome) = await HouseholdForNewAccountAsync(command.ProvisionalHouseholdId, now, cancellationToken);
            user = new UserAccount(new UserId(Guid.CreateVersion7(now)), identity.Email, identity.DisplayName);
            await accounts.CreateAccountAsync(
                new NewAccount(user.Id, identity.Subject, identity.Email, identity.DisplayName, household, now), cancellationToken);
            return (user, household, outcome);
        }

        if (user.Email != identity.Email || user.DisplayName != identity.DisplayName)
        {
            user = user with { Email = identity.Email, DisplayName = identity.DisplayName };
            await accounts.UpdateProfileAsync(user, cancellationToken);
        }

        // One household per user in this increment (Spec 010 §3.2): the earliest membership.
        var membership = (await accounts.ListMembershipsOfUserAsync(user.Id, cancellationToken)).OrderBy(m => m.JoinedAt).FirstOrDefault();
        if (membership is null)
        {
            var (household, outcome) = await HouseholdForNewAccountAsync(command.ProvisionalHouseholdId, now, cancellationToken);
            await accounts.CreateHouseholdAsync(household, user.Id, now, cancellationToken);
            return (user, household, outcome);
        }

        // The same phone again: it already holds the account's household, or it has had a session for this user.
        var samePhone = membership.HouseholdId == command.ProvisionalHouseholdId
            || await sessions.ExistsForDeviceAsync(user.Id, command.DeviceId, cancellationToken);
        return (user, membership.HouseholdId, samePhone ? SignInOutcome.Existing : SignInOutcome.Joined);
    }

    /// <summary>The phone's provisional ID when it is unused (ADR 0003 §7), else a new one.</summary>
    private async Task<(HouseholdId Household, SignInOutcome Outcome)> HouseholdForNewAccountAsync(
        HouseholdId provisional, DateTimeOffset now, CancellationToken cancellationToken) =>
        await accounts.HouseholdExistsAsync(provisional, cancellationToken)
            ? (new HouseholdId(Guid.CreateVersion7(now)), SignInOutcome.Created)
            : (provisional, SignInOutcome.Adopted);

    private static string DeviceName(string? sent)
    {
        var name = (sent ?? "").Trim();
        if (name.Length == 0)
            return "Phone";
        return name.Length <= DeviceNameMaxLength ? name : name[..DeviceNameMaxLength];
    }
}
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test tests/Coo.Api.Application.Tests && dotnet test tests/Coo.Architecture.Tests`
Expected: PASS (14 sign-in tests); the architecture tests PASS, including `SolutionFilterTests` and
`Application_Types_DoNotDependOnSystemTextJson`.

- [ ] **Step 8: Commit**

```bash
git add Coo.slnx Coo.Tests.slnf src/Backend/Coo.Api.Application/Auth tests/Coo.Api.Application.Tests
git commit -m "Api.Application: sign-in with Google, fail closed, the four outcomes (Spec 010 §3)"
```

---

### Task 3: Refresh, sign-out, me, account deletion and the nonce clean-up (Application)

**Files:**
- Create: `src/Backend/Coo.Api.Application/Auth/Sessions.cs`
- Create: `src/Backend/Coo.Api.Application/Auth/AccountUseCases.cs`
- Modify: `tests/Coo.Api.Application.Tests/Fakes.cs` (`AuthKit` factories)
- Test: `tests/Coo.Api.Application.Tests/SessionTests.cs`, `tests/Coo.Api.Application.Tests/AccountTests.cs`

**Interfaces:**
- Consumes: Task 2's ports, `AuthSecrets`, `AuthErrors`, `SignInWithGoogle`; Task 1's `HouseholdSuccession`,
  `HouseholdDeleted`, `OwnershipPasses`.
- Produces:
  - `record RefreshResult(AccessToken AccessToken, string RefreshToken)`;
    `sealed class RefreshSession(IDeviceSessionStore sessions, ITokenIssuer tokens, TimeProvider time)` with
    `static readonly TimeSpan IdleLimit` (180 days) and `Task<Result<RefreshResult>> ExecuteAsync(string refreshToken, CancellationToken)`.
  - `sealed class SignOutSession(IDeviceSessionStore sessions, TimeProvider time)` with `Task ExecuteAsync(DeviceSessionId session, CancellationToken)`.
  - `sealed class PurgeAuthNonces(IAuthNonceStore nonces, TimeProvider time)` with `static readonly TimeSpan KeepFor` (1 day) and `Task<int> ExecuteAsync(CancellationToken)`.
  - `record AccountOverview(UserAccount User, Membership? Membership, IReadOnlyList<DeviceSession> Devices)`;
    `sealed class GetMe(IAccountStore accounts, IDeviceSessionStore sessions)` with `Task<AccountOverview?> ExecuteAsync(UserId user, CancellationToken)` (active sessions only, most recently used first).
  - `sealed class DeleteAccount(IAccountStore accounts)` with `Task<Result> ExecuteAsync(UserId user, bool confirmed, CancellationToken)`.
  - `AuthKit.Refresh()`, `AuthKit.SignOut()`, `AuthKit.Purge()`, `AuthKit.GetMe()`, `AuthKit.DeleteAccount()`.

- [ ] **Step 1: Extend `AuthKit`** (in `Fakes.cs`, after `SignIn()`):

```csharp
    public RefreshSession Refresh() => new(Sessions, Tokens, Time);

    public SignOutSession SignOut() => new(Sessions, Time);

    public PurgeAuthNonces Purge() => new(Nonces, Time);

    public GetMe GetMe() => new(Accounts, Sessions);

    public DeleteAccount DeleteAccount() => new(Accounts);
```

- [ ] **Step 2: Write the failing tests**

`tests/Coo.Api.Application.Tests/SessionTests.cs`:

```csharp
using Coo.Api.Application.Auth;
using Coo.Domain.Common;

namespace Coo.Api.Application.Tests;

/// <summary>Spec 010 §3.3: rotation on every use, reuse detection, the 180-day sliding lifetime, sign-out.</summary>
public sealed class SessionTests
{
    private static readonly DeviceId Phone = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000d1"));
    private static readonly HouseholdId Household = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a1"));

    private readonly AuthKit _kit = new();

    [Fact]
    public async Task ExecuteAsync_CurrentToken_RotatesAndIssuesNewTokens()
    {
        // Arrange
        var signedIn = await _kit.SignInAsync("google-1", Phone, Household);
        _kit.Time.Advance(TimeSpan.FromMinutes(20));

        // Act
        var result = await _kit.Refresh().ExecuteAsync(signedIn.Session.RefreshToken, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.RefreshToken.Should().NotBe(signedIn.Session.RefreshToken);
        result.Value.AccessToken.ExpiresAt.Should().Be(_kit.Time.GetUtcNow() + TimeSpan.FromMinutes(15));
        var row = _kit.Sessions.Of(signedIn.Session.Id);
        row.RefreshHash.Should().Be(AuthSecrets.Hash(result.Value.RefreshToken));
        row.PreviousRefreshHash.Should().Be(AuthSecrets.Hash(signedIn.Session.RefreshToken));
        row.Session.LastUsedAt.Should().Be(_kit.Time.GetUtcNow());
    }

    [Fact]
    public async Task ExecuteAsync_PreviousTokenPresentedAgain_RevokesTheSession()
    {
        // Arrange: someone kept the old refresh token and uses it after the phone rotated it.
        var signedIn = await _kit.SignInAsync("google-1", Phone, Household);
        var rotated = await _kit.Refresh().ExecuteAsync(signedIn.Session.RefreshToken, CancellationToken.None);

        // Act
        var reused = await _kit.Refresh().ExecuteAsync(signedIn.Session.RefreshToken, CancellationToken.None);

        // Assert
        reused.Error!.Code.Should().Be(AuthErrors.RefreshInvalid);
        _kit.Sessions.Of(signedIn.Session.Id).Session.IsRevoked.Should().BeTrue();
        (await _kit.Refresh().ExecuteAsync(rotated.Value.RefreshToken, CancellationToken.None)).Error!.Code
            .Should().Be(AuthErrors.RefreshInvalid, "the thief's use revokes the phone's session too");
    }

    [Fact]
    public async Task ExecuteAsync_After180DaysIdle_IsRefused()
    {
        // Arrange
        var signedIn = await _kit.SignInAsync("google-1", Phone, Household);
        _kit.Time.Advance(TimeSpan.FromDays(180) + TimeSpan.FromMinutes(1));

        // Act
        var result = await _kit.Refresh().ExecuteAsync(signedIn.Session.RefreshToken, CancellationToken.None);

        // Assert
        result.Error!.Code.Should().Be(AuthErrors.RefreshInvalid);
    }

    [Fact]
    public async Task ExecuteAsync_UsedWithin180Days_SlidesTheLifetime()
    {
        // Arrange: used on day 179, then again 179 days later; 358 days in all.
        var signedIn = await _kit.SignInAsync("google-1", Phone, Household);
        _kit.Time.Advance(TimeSpan.FromDays(179));
        var first = await _kit.Refresh().ExecuteAsync(signedIn.Session.RefreshToken, CancellationToken.None);
        _kit.Time.Advance(TimeSpan.FromDays(179));

        // Act
        var second = await _kit.Refresh().ExecuteAsync(first.Value.RefreshToken, CancellationToken.None);

        // Assert
        second.IsSuccess.Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("never-issued")]
    public async Task ExecuteAsync_UnknownToken_IsRefused(string token)
    {
        // Arrange
        await _kit.SignInAsync("google-1", Phone, Household);

        // Act
        var result = await _kit.Refresh().ExecuteAsync(token, CancellationToken.None);

        // Assert
        result.Error!.Code.Should().Be(AuthErrors.RefreshInvalid);
    }

    [Fact]
    public async Task ExecuteAsync_AfterSignOut_IsRefused()
    {
        // Arrange
        var signedIn = await _kit.SignInAsync("google-1", Phone, Household);
        await _kit.SignOut().ExecuteAsync(signedIn.Session.Id, CancellationToken.None);

        // Act
        var result = await _kit.Refresh().ExecuteAsync(signedIn.Session.RefreshToken, CancellationToken.None);

        // Assert
        result.Error!.Code.Should().Be(AuthErrors.RefreshInvalid);
        (await _kit.Sessions.IsActiveAsync(signedIn.Session.Id, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_NoncesOlderThanADay_AreDeletedAndNewerKept()
    {
        // Arrange
        await _kit.IssueNonce().ExecuteAsync(CancellationToken.None);
        _kit.Time.Advance(TimeSpan.FromHours(25));
        var fresh = await _kit.IssueNonce().ExecuteAsync(CancellationToken.None);

        // Act
        var deleted = await _kit.Purge().ExecuteAsync(CancellationToken.None);

        // Assert
        deleted.Should().Be(1);
        _kit.Nonces.Rows.Keys.Should().Equal(AuthSecrets.Hash(fresh));
    }
}
```

`tests/Coo.Api.Application.Tests/AccountTests.cs`:

```csharp
using Coo.Api.Application.Auth;
using Coo.Domain.Common;
using Coo.Domain.Households;

namespace Coo.Api.Application.Tests;

/// <summary>Spec 010 §4.1 (<c>/api/me</c>) and §5 (deleting the account).</summary>
public sealed class AccountTests
{
    private static readonly DeviceId PhoneA = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000d1"));
    private static readonly DeviceId PhoneB = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000d2"));
    private static readonly HouseholdId Household = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a1"));
    private static readonly HouseholdId Other = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a2"));

    private readonly AuthKit _kit = new();

    [Fact]
    public async Task ExecuteAsync_TwoPhonesOneSignedOut_ShowsTheOwnerAndTheActivePhoneOnly()
    {
        // Arrange
        var a = await _kit.SignInAsync("google-1", PhoneA, Household);
        var b = await _kit.SignInAsync("google-1", PhoneB, Other);
        await _kit.SignOut().ExecuteAsync(a.Session.Id, CancellationToken.None);

        // Act
        var me = await _kit.GetMe().ExecuteAsync(a.User.Id, CancellationToken.None);

        // Assert
        me!.User.Email.Should().Be("google-1@example.com");
        me.Membership!.HouseholdId.Should().Be(Household);
        me.Membership.Role.Should().Be(Role.Owner);
        me.Devices.Should().ContainSingle().Which.Id.Should().Be(b.Session.Id);
    }

    [Fact]
    public async Task ExecuteAsync_UnknownUser_ReturnsNull()
    {
        // Arrange
        var stranger = new UserId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000c9"));

        // Act
        var me = await _kit.GetMe().ExecuteAsync(stranger, CancellationToken.None);

        // Assert
        me.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteAsync_NotConfirmed_DeletesNothing()
    {
        // Arrange
        var signedIn = await _kit.SignInAsync("google-1", PhoneA, Household);

        // Act
        var result = await _kit.DeleteAccount().ExecuteAsync(signedIn.User.Id, confirmed: false, CancellationToken.None);

        // Assert
        result.Error!.Code.Should().Be(AuthErrors.ConfirmationRequired);
        _kit.Accounts.Deleted.Should().BeNull();
        _kit.Accounts.Households.Should().Contain(Household);
    }

    [Fact]
    public async Task ExecuteAsync_SoleOwner_DeletesTheUserAndTheHousehold()
    {
        // Arrange
        var signedIn = await _kit.SignInAsync("google-1", PhoneA, Household);

        // Act
        var result = await _kit.DeleteAccount().ExecuteAsync(signedIn.User.Id, confirmed: true, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        _kit.Accounts.Deleted!.Value.User.Should().Be(signedIn.User.Id);
        _kit.Accounts.Deleted.Value.Plan.HouseholdsToDelete.Should().Equal(Household);
        _kit.Accounts.Deleted.Value.Plan.OwnershipTransfers.Should().BeEmpty();
        _kit.Accounts.BySubject.Should().BeEmpty();
        _kit.Accounts.Memberships.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_OwnerWithAnotherParent_PassesOwnershipAndKeepsTheHousehold()
    {
        // Arrange: from 11b a second parent can exist; the rule is in place already (ADR 0003 §8).
        var signedIn = await _kit.SignInAsync("google-1", PhoneA, Household);
        var partner = new UserId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000c2"));
        _kit.Accounts.Memberships.Add(Membership.Create(Household, partner, Role.Parent, AuthKit.Start.AddDays(1)));

        // Act
        await _kit.DeleteAccount().ExecuteAsync(signedIn.User.Id, confirmed: true, CancellationToken.None);

        // Assert
        _kit.Accounts.Deleted!.Value.Plan.HouseholdsToDelete.Should().BeEmpty();
        _kit.Accounts.Deleted.Value.Plan.OwnershipTransfers.Should().Equal(new OwnershipTransfer(Household, partner));
    }
}
```

- [ ] **Step 3: Run them to verify they fail**

Run: `dotnet test tests/Coo.Api.Application.Tests`
Expected: build errors, `RefreshSession`, `SignOutSession`, `PurgeAuthNonces`, `GetMe`, `DeleteAccount` do not exist.

- [ ] **Step 4: Implement**

`src/Backend/Coo.Api.Application/Auth/Sessions.cs`:

```csharp
using Coo.Domain.Common;

namespace Coo.Api.Application.Auth;

public sealed record RefreshResult(AccessToken AccessToken, string RefreshToken);

/// <summary><c>POST /api/auth/refresh</c> (ADR 0003 §4): rotates on every use; the previous token again means theft.</summary>
public sealed class RefreshSession(IDeviceSessionStore sessions, ITokenIssuer tokens, TimeProvider time)
{
    public static readonly TimeSpan IdleLimit = TimeSpan.FromDays(180);

    public async Task<Result<RefreshResult>> ExecuteAsync(string refreshToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
            return Result<RefreshResult>.Failure(AuthErrors.RefreshInvalid);

        var hash = AuthSecrets.Hash(refreshToken);
        var found = await sessions.FindByRefreshHashAsync(hash, cancellationToken);
        if (found is null || found.Session.IsRevoked)
            return Result<RefreshResult>.Failure(AuthErrors.RefreshInvalid);

        var now = time.GetUtcNow();
        if (found.Match == RefreshMatch.Previous || now - found.Session.LastUsedAt > IdleLimit)
        {
            await sessions.RevokeAsync(found.Session.Id, now, cancellationToken);
            return Result<RefreshResult>.Failure(AuthErrors.RefreshInvalid);
        }

        // A concurrent refresh with the same token loses here; its token is now the previous one.
        var next = AuthSecrets.NewToken();
        if (!await sessions.RotateAsync(found.Session.Id, hash, AuthSecrets.Hash(next), now, cancellationToken))
            return Result<RefreshResult>.Failure(AuthErrors.RefreshInvalid);
        return new RefreshResult(tokens.Issue(found.Session.UserId, found.Session.Id, now), next);
    }
}

/// <summary><c>POST /api/auth/sign-out</c>: revokes this device session. Nothing on the phone is deleted (ADR 0003 §4).</summary>
public sealed class SignOutSession(IDeviceSessionStore sessions, TimeProvider time)
{
    public Task ExecuteAsync(DeviceSessionId session, CancellationToken cancellationToken) =>
        sessions.RevokeAsync(session, time.GetUtcNow(), cancellationToken);
}

/// <summary>The daily clean-up (Spec 010 §4.2): nonce rows older than a day go.</summary>
public sealed class PurgeAuthNonces(IAuthNonceStore nonces, TimeProvider time)
{
    public static readonly TimeSpan KeepFor = TimeSpan.FromDays(1);

    public Task<int> ExecuteAsync(CancellationToken cancellationToken) =>
        nonces.DeleteIssuedBeforeAsync(time.GetUtcNow() - KeepFor, cancellationToken);
}
```

`src/Backend/Coo.Api.Application/Auth/AccountUseCases.cs`:

```csharp
using Coo.Domain.Common;
using Coo.Domain.Households;

namespace Coo.Api.Application.Auth;

public sealed record AccountOverview(UserAccount User, Membership? Membership, IReadOnlyList<DeviceSession> Devices);

/// <summary><c>GET /api/me</c>: the user, their household and role, and their active device sessions.</summary>
public sealed class GetMe(IAccountStore accounts, IDeviceSessionStore sessions)
{
    public async Task<AccountOverview?> ExecuteAsync(UserId user, CancellationToken cancellationToken)
    {
        var account = await accounts.FindAsync(user, cancellationToken);
        if (account is null)
            return null;
        var membership = (await accounts.ListMembershipsOfUserAsync(user, cancellationToken)).OrderBy(m => m.JoinedAt).FirstOrDefault();
        var devices = (await sessions.ListForUserAsync(user, cancellationToken))
            .Where(s => !s.IsRevoked)
            .OrderByDescending(s => s.LastUsedAt)
            .ToList();
        return new AccountOverview(account, membership, devices);
    }
}

/// <summary>
/// <c>DELETE /api/account</c> (Spec 010 §5, ADR 0003 §8). For each household the user belongs to, the succession rule
/// decides first: ownership passes to the earliest parent, or, when they were the last parent, the household and its
/// care data go. Then the user, their logins, sessions and memberships go, all in one transaction.
/// </summary>
public sealed class DeleteAccount(IAccountStore accounts)
{
    public async Task<Result> ExecuteAsync(UserId user, bool confirmed, CancellationToken cancellationToken)
    {
        if (!confirmed)
            return Result.Failure(AuthErrors.ConfirmationRequired);

        var deletions = new List<HouseholdId>();
        var transfers = new List<OwnershipTransfer>();
        foreach (var membership in await accounts.ListMembershipsOfUserAsync(user, cancellationToken))
        {
            var members = await accounts.ListMembersAsync(membership.HouseholdId, cancellationToken);
            switch (HouseholdSuccession.WhenLeaving(members, user))
            {
                case HouseholdDeleted:
                    deletions.Add(membership.HouseholdId);
                    break;
                case OwnershipPasses passes:
                    transfers.Add(new OwnershipTransfer(membership.HouseholdId, passes.NewOwner));
                    break;
            }
        }

        await accounts.DeleteAccountAsync(user, new AccountDeletionPlan(deletions, transfers), cancellationToken);
        return Result.Success;
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/Coo.Api.Application.Tests`
Expected: PASS (all sign-in, session and account tests).

- [ ] **Step 6: Commit**

```bash
git add src/Backend/Coo.Api.Application/Auth tests/Coo.Api.Application.Tests
git commit -m "Api.Application: refresh with reuse detection, sign-out, me, account deletion, nonce clean-up (Spec 010 §3.3, §5)"
```

---
### Task 4: The schema: Identity's tables, `auth_nonces`, `device_sessions`, `memberships`, `households.created_by`

**Files:**
- Modify: `Directory.Packages.props`, `src/Backend/Coo.Api.Infrastructure/Coo.Api.Infrastructure.csproj`
- Modify: `src/Backend/Coo.Api.Infrastructure/Persistence/Records.cs`, `Persistence/CooDbContext.cs`
- Generated: `src/Backend/Coo.Api.Infrastructure/Persistence/Migrations/<timestamp>_SignInAndSessions.cs`,
  `.Designer.cs`, and the updated `CooDbContextModelSnapshot.cs`
- Modify: `tests/Coo.Api.Infrastructure.Tests/PostgresServer.cs` (`MigrateToAsync`), `SchemaGuardTests.cs`
- Test: `tests/Coo.Api.Infrastructure.Tests/SignInSchemaTests.cs`

**Interfaces:**
- Produces (all `internal`, namespace `Coo.Api.Infrastructure.Persistence`):
  - `sealed class CooUser : IdentityUser<Guid>` with `string DisplayName { get; set; }`.
  - `sealed class AuthNonceRecord` (`NonceHash`, `IssuedAt`, `ConsumedAt?`), `sealed class DeviceSessionRecord` (`Id`,
    `UserId`, `DeviceId`, `DeviceName`, `RefreshHash`, `PreviousRefreshHash?`, `CreatedAt`, `LastUsedAt`, `RevokedAt?`),
    `sealed class MembershipRecord` (`HouseholdId`, `UserId`, `Role` as text, `JoinedAt`); `HouseholdRecord.CreatedBy` (`Guid?`).
  - `CooDbContext : IdentityUserContext<CooUser, Guid>` with `AuthNonces`, `DeviceSessions`, `Memberships` (plus the
    inherited `Users`, `UserLogins`, `UserClaims`, `UserTokens`).
  - Test helper: `ServerDatabase.MigrateToAsync(string migration)`.

- [ ] **Step 1: Add the package**

In `Directory.Packages.props`, under the server storage lines:

```xml
    <!-- Users without passwords (ADR 0003 §3, Spec 010 §4.2). Same patch as EF Core and Mvc.Testing. -->
    <PackageVersion Include="Microsoft.AspNetCore.Identity.EntityFrameworkCore" Version="10.0.12" />
```

In `Coo.Api.Infrastructure.csproj`, next to the Npgsql reference:

```xml
    <PackageReference Include="Microsoft.AspNetCore.Identity.EntityFrameworkCore" />
```

Confirm the version exists before relying on it: `dotnet package search Microsoft.AspNetCore.Identity.EntityFrameworkCore --exact-match --format json | grep '"version": "10.0.12"'`.
If 10.0.12 is not there, use the newest 10.0.x that is, and keep it equal to `Microsoft.AspNetCore.Mvc.Testing`.

- [ ] **Step 2: Write the failing schema tests**

Add to `ServerDatabase` in `tests/Coo.Api.Infrastructure.Tests/PostgresServer.cs` (with
`using Microsoft.EntityFrameworkCore.Infrastructure;` and `using Microsoft.EntityFrameworkCore.Migrations;`):

```csharp
    /// <summary>Migrates up to, and including, <paramref name="migration"/> only: production as it was before a change.</summary>
    public async Task MigrateToAsync(string migration)
    {
        var contexts = _services.GetRequiredService<IDbContextFactory<CooDbContext>>();
        await using var context = await contexts.CreateDbContextAsync();
        await context.Database.GetService<IMigrator>().MigrateAsync(migration);
    }
```

In `SchemaGuardTests.Migrate_EmptyDatabase_CreatesTablesAndRegistersEveryKindVersion`, replace the tables assertion
with a set comparison (collation-independent):

```csharp
        ((string)tables!).Split(',').Should().BeEquivalentTo(
            "AspNetUserClaims", "AspNetUserLogins", "AspNetUserTokens", "AspNetUsers", "auth_nonces", "care_entries",
            "device_sessions", "entry_kind_versions", "households", "memberships");
```

`tests/Coo.Api.Infrastructure.Tests/SignInSchemaTests.cs`:

```csharp
using Npgsql;

namespace Coo.Api.Infrastructure.Tests;

/// <summary>Spec 010 §4.2: the new tables and columns, and an upgrade of a database from before this increment.</summary>
[Collection(PostgresTests.Name)]
public sealed class SignInSchemaTests(PostgresServer server)
{
    [Fact]
    public async Task Migrate_EmptyDatabase_CreatesTheSignInColumns()
    {
        // Arrange
        await using var db = await ServerDatabase.CreateAsync(server);

        // Act
        var columns = await db.ScalarAsync(
            """
            SELECT string_agg(table_name || '.' || column_name, ',')
            FROM information_schema.columns
            WHERE table_schema = 'public'
              AND (table_name IN ('auth_nonces', 'device_sessions', 'memberships')
                   OR (table_name = 'households' AND column_name = 'created_by'));
            """);

        // Assert
        ((string)columns!).Split(',').Should().BeEquivalentTo(
            "auth_nonces.nonce_hash", "auth_nonces.issued_at", "auth_nonces.consumed_at",
            "device_sessions.id", "device_sessions.user_id", "device_sessions.device_id", "device_sessions.device_name",
            "device_sessions.refresh_hash", "device_sessions.previous_refresh_hash", "device_sessions.created_at",
            "device_sessions.last_used_at", "device_sessions.revoked_at",
            "memberships.household_id", "memberships.user_id", "memberships.role", "memberships.joined_at",
            "households.created_by");
    }

    [Fact]
    public async Task Migrate_FromTheInitialSchemaWithAHousehold_KeepsItWithoutACreator()
    {
        // Arrange: production before Increment 10 may hold households already.
        await using var db = await ServerDatabase.CreateAsync(server, migrate: false);
        await db.MigrateToAsync("20261001131623_InitialSchema");
        await db.Households.EnsureAsync(ServerDatabase.Household, CancellationToken.None);

        // Act
        await db.MigrateAsync();

        // Assert
        (await db.ScalarAsync($"SELECT created_by IS NULL FROM households WHERE id = '{ServerDatabase.Household.Value}';"))
            .Should().Be(true);
    }

    [Fact]
    public async Task Insert_SecondOwnerInOneHousehold_IsRefusedByTheDatabase()
    {
        // Arrange: exactly one Owner per household (ADR 0003 §6), whatever code tries.
        await using var db = await ServerDatabase.CreateAsync(server);
        await db.Households.EnsureAsync(ServerDatabase.Household, CancellationToken.None);
        var first = db.NewId();
        var second = db.NewId();
        await db.ExecuteAsync($"""
            INSERT INTO "AspNetUsers" ("Id", "DisplayName", "EmailConfirmed", "PhoneNumberConfirmed", "TwoFactorEnabled", "LockoutEnabled", "AccessFailedCount")
            VALUES ('{first}', 'A', true, false, false, false, 0), ('{second}', 'B', true, false, false, false, 0);
            INSERT INTO memberships (household_id, user_id, role, joined_at) VALUES ('{ServerDatabase.Household.Value}', '{first}', 'owner', now());
            """);

        // Act
        Func<Task> act = () => db.ExecuteAsync(
            $"INSERT INTO memberships (household_id, user_id, role, joined_at) VALUES ('{ServerDatabase.Household.Value}', '{second}', 'owner', now());");

        // Assert
        await act.Should().ThrowAsync<PostgresException>();
    }
}
```

- [ ] **Step 3: Run them to verify they fail**

Run: `dotnet test tests/Coo.Api.Infrastructure.Tests --filter "FullyQualifiedName~SchemaGuardTests|FullyQualifiedName~SignInSchemaTests"`
Expected: FAIL: the new tables and columns do not exist.

- [ ] **Step 4: Add the records**

Append to `src/Backend/Coo.Api.Infrastructure/Persistence/Records.cs` (add `using Microsoft.AspNetCore.Identity;` at the
top, and change the file's header comment from "three tables" to "tables"):

```csharp
/// <summary>
/// <c>AspNetUsers</c> (ADR 0003 §3): ASP.NET Core Identity's user, with <c>Guid</c> keys and never a password. Only
/// what Spec 010 §4.2 allows is kept: the email (Identity's column), the display name, and the Google <c>sub</c> as the
/// login in <c>AspNetUserLogins</c>.
/// </summary>
internal sealed class CooUser : IdentityUser<Guid>
{
    public string DisplayName { get; set; } = "";
}

/// <summary><c>auth_nonces</c>: single-use sign-in nonces, by SHA-256 (Spec 010 §4.2).</summary>
internal sealed class AuthNonceRecord
{
    public required string NonceHash { get; init; }

    public required DateTime IssuedAt { get; init; }

    public DateTime? ConsumedAt { get; init; }
}

/// <summary><c>device_sessions</c>: one per phone per user; the current and the previous refresh token, by hash.</summary>
internal sealed class DeviceSessionRecord
{
    public required Guid Id { get; init; }

    public required Guid UserId { get; init; }

    public required Guid DeviceId { get; init; }

    public required string DeviceName { get; init; }

    public required string RefreshHash { get; init; }

    public string? PreviousRefreshHash { get; init; }

    public required DateTime CreatedAt { get; init; }

    public required DateTime LastUsedAt { get; init; }

    public DateTime? RevokedAt { get; init; }
}

/// <summary><c>memberships</c>: a user's role in a household (ADR 0003 §6). The role is <c>owner</c>, <c>parent</c> or <c>caregiver</c>.</summary>
internal sealed class MembershipRecord
{
    public required Guid HouseholdId { get; init; }

    public required Guid UserId { get; init; }

    public required string Role { get; init; }

    public required DateTime JoinedAt { get; init; }
}
```

and in `HouseholdRecord`, after `CreatedAt`:

```csharp
    /// <summary>The Owner who created it (Spec 010 §4.2). Null for households created before sign-in existed.</summary>
    public Guid? CreatedBy { get; init; }
```

- [ ] **Step 5: Extend the context**

In `CooDbContext.cs`: add `using Microsoft.AspNetCore.Identity.EntityFrameworkCore;`, change the declaration to

```csharp
internal sealed class CooDbContext(DbContextOptions<CooDbContext> options) : IdentityUserContext<CooUser, Guid>(options)
```

add the sets after `CareEntries`:

```csharp
    public DbSet<AuthNonceRecord> AuthNonces => Set<AuthNonceRecord>();

    public DbSet<DeviceSessionRecord> DeviceSessions => Set<DeviceSessionRecord>();

    public DbSet<MembershipRecord> Memberships => Set<MembershipRecord>();
```

and in `OnModelCreating`: call `base.OnModelCreating(modelBuilder);` as the **first** line (Identity's tables), add
to the `HouseholdRecord` block:

```csharp
            e.Property(h => h.CreatedBy).HasColumnName("created_by");
            e.HasOne<CooUser>().WithMany()
                .HasForeignKey(h => h.CreatedBy)
                .HasConstraintName("fk_households_created_by")
                .OnDelete(DeleteBehavior.SetNull);
```

and after the `CareEntryRecord` block:

```csharp
        modelBuilder.Entity<CooUser>(e => e.Property(u => u.DisplayName).HasMaxLength(200));

        modelBuilder.Entity<AuthNonceRecord>(e =>
        {
            e.ToTable("auth_nonces");
            e.HasKey(n => n.NonceHash);
            e.Property(n => n.NonceHash).HasColumnName("nonce_hash");
            e.Property(n => n.IssuedAt).HasColumnName("issued_at").HasColumnType("timestamptz");
            e.Property(n => n.ConsumedAt).HasColumnName("consumed_at").HasColumnType("timestamptz");
            e.HasIndex(n => n.IssuedAt).HasDatabaseName("ix_auth_nonces_issued_at");
        });

        modelBuilder.Entity<DeviceSessionRecord>(e =>
        {
            e.ToTable("device_sessions", t => t.HasCheckConstraint(
                "ck_device_sessions_hashes_differ", "previous_refresh_hash IS NULL OR previous_refresh_hash <> refresh_hash"));
            e.HasKey(s => s.Id);
            e.Property(s => s.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(s => s.UserId).HasColumnName("user_id");
            e.Property(s => s.DeviceId).HasColumnName("device_id");
            e.Property(s => s.DeviceName).HasColumnName("device_name").HasMaxLength(60);
            e.Property(s => s.RefreshHash).HasColumnName("refresh_hash");
            e.Property(s => s.PreviousRefreshHash).HasColumnName("previous_refresh_hash");
            e.Property(s => s.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            e.Property(s => s.LastUsedAt).HasColumnName("last_used_at").HasColumnType("timestamptz");
            e.Property(s => s.RevokedAt).HasColumnName("revoked_at").HasColumnType("timestamptz");
            e.HasOne<CooUser>().WithMany()
                .HasForeignKey(s => s.UserId)
                .HasConstraintName("fk_device_sessions_users")
                .OnDelete(DeleteBehavior.Cascade);

            // One session per phone per user; signing in again on the phone replaces it (Spec 010 §4.2).
            e.HasIndex(s => new { s.UserId, s.DeviceId }).IsUnique().HasDatabaseName("ix_device_sessions_user_device");
            e.HasIndex(s => s.RefreshHash).IsUnique().HasDatabaseName("ix_device_sessions_refresh_hash");
            e.HasIndex(s => s.PreviousRefreshHash).HasDatabaseName("ix_device_sessions_previous_refresh_hash");
        });

        modelBuilder.Entity<MembershipRecord>(e =>
        {
            e.ToTable("memberships", t => t.HasCheckConstraint("ck_memberships_role", "role IN ('owner', 'parent', 'caregiver')"));
            e.HasKey(m => new { m.HouseholdId, m.UserId });
            e.Property(m => m.HouseholdId).HasColumnName("household_id");
            e.Property(m => m.UserId).HasColumnName("user_id");
            e.Property(m => m.Role).HasColumnName("role");
            e.Property(m => m.JoinedAt).HasColumnName("joined_at").HasColumnType("timestamptz");
            e.HasOne<HouseholdRecord>().WithMany()
                .HasForeignKey(m => m.HouseholdId)
                .HasConstraintName("fk_memberships_households")
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne<CooUser>().WithMany()
                .HasForeignKey(m => m.UserId)
                .HasConstraintName("fk_memberships_users")
                .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(m => m.UserId).HasDatabaseName("ix_memberships_user");

            // Exactly one Owner per household (ADR 0003 §6).
            e.HasIndex(m => m.HouseholdId).IsUnique().HasFilter("role = 'owner'").HasDatabaseName("ix_memberships_one_owner");
        });
```

Update the class summary: "The schema, in one place (Spec 008 §4, Spec 010 §4.2). Identity's tables keep Identity's
names (`AspNetUsers`, …); ours are snake_case."

- [ ] **Step 6: Generate the migration**

```sh
dotnet tool restore
dotnet tool run dotnet-ef migrations add SignInAndSessions --project src/Backend/Coo.Api.Infrastructure \
  --startup-project src/Backend/Coo.Api.Infrastructure --output-dir Persistence/Migrations \
  --namespace Coo.Api.Infrastructure.Persistence.Migrations
```

Then, as `BUILD.md` says: make the two generated migration classes `internal`; if EF wrote the snapshot into a
`Coo/Api/...` folder, move it over `Persistence/Migrations/CooDbContextModelSnapshot.cs` and delete the folder. Read the
generated `Up`: it must create `AspNetUsers`, `AspNetUserClaims`, `AspNetUserLogins`, `AspNetUserTokens`, `auth_nonces`,
`device_sessions`, `memberships`, add `households.created_by` as a **nullable** `uuid`, and create the constraints and
indexes above. It must not touch `care_entries` or `entry_kind_versions`. If it also creates `AspNetUserPasskeys`,
Identity is on its schema version 3 (passkeys, .NET 10): that table is then one "Identity needs" (spec §4.2), so add it
to the expected set in `SchemaGuardTests` rather than fighting the default.

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test tests/Coo.Api.Infrastructure.Tests`
Expected: PASS, the existing store and integrity tests included.

- [ ] **Step 8: Commit**

```bash
git add Directory.Packages.props src/Backend/Coo.Api.Infrastructure tests/Coo.Api.Infrastructure.Tests
git commit -m "Server schema: Identity without passwords, auth_nonces, device_sessions, memberships, households.created_by (Spec 010 §4.2)"
```

---

### Task 5: The stores: nonces, device sessions, accounts

**Files:**
- Create: `src/Backend/Coo.Api.Infrastructure/Persistence/AuthNonceStore.cs`
- Create: `src/Backend/Coo.Api.Infrastructure/Persistence/DeviceSessionStore.cs`
- Create: `src/Backend/Coo.Api.Infrastructure/Persistence/AccountStore.cs`
- Create: `src/Backend/Coo.Api.Infrastructure/Persistence/RoleText.cs`
- Modify: `src/Backend/Coo.Api.Infrastructure/ServiceCollectionExtensions.cs`
- Modify: `tests/Coo.Api.Infrastructure.Tests/PostgresServer.cs` (`Nonces`, `Sessions`, `NewScope`)
- Test: `tests/Coo.Api.Infrastructure.Tests/AuthStoreTests.cs`

**Interfaces:**
- Consumes: Task 2's `IAuthNonceStore`, `IDeviceSessionStore`, `IAccountStore` and records; Task 4's records and context.
- Produces: `AddApiInfrastructure` now also registers `IAuthNonceStore` and `IDeviceSessionStore` (singletons, Npgsql),
  a scoped `CooDbContext`, ASP.NET Core Identity's user store (`AddIdentityCore<CooUser>().AddEntityFrameworkStores<CooDbContext>()`),
  and `IAccountStore` (**scoped**: it shares the scoped context with Identity's `UserManager`, so an account is created in
  one transaction). Test helpers: `ServerDatabase.Nonces`, `ServerDatabase.Sessions`, `ServerDatabase.NewScope()`.

- [ ] **Step 1: Extend the test helper**

In `ServerDatabase` (`PostgresServer.cs`, add `using Coo.Api.Application.Auth;`):

```csharp
    public IAuthNonceStore Nonces => _services.GetRequiredService<IAuthNonceStore>();

    public IDeviceSessionStore Sessions => _services.GetRequiredService<IDeviceSessionStore>();

    /// <summary>A request's worth of scoped services: <see cref="IAccountStore"/> lives here.</summary>
    public AsyncServiceScope NewScope() => _services.CreateAsyncScope();
```

- [ ] **Step 2: Write the failing tests**

`tests/Coo.Api.Infrastructure.Tests/AuthStoreTests.cs`:

```csharp
using Coo.Api.Application.Auth;
using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Kinds.Diaper;
using Coo.Domain.Common;
using Microsoft.Extensions.DependencyInjection;

namespace Coo.Api.Infrastructure.Tests;

/// <summary>The sign-in stores against real PostgreSQL 17 (Spec 010 §4.2, §7).</summary>
[Collection(PostgresTests.Name)]
public sealed class AuthStoreTests(PostgresServer server)
{
    private static readonly UserId User = new(Guid.Parse("7a1c0000-0000-7000-8000-00000000c0c1"));
    private static readonly UserId Partner = new(Guid.Parse("7a1c0000-0000-7000-8000-00000000c0c2"));
    private static readonly HouseholdId PartnersHousehold = new(Guid.Parse("7a1c0000-0000-7000-8000-00000000c0a2"));
    private static readonly DeviceId Phone = new(Guid.Parse("7a1c0000-0000-7000-8000-00000000c0d2"));
    private static readonly DateTimeOffset T0 = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task TryConsumeAsync_FreshNonce_SucceedsOnceOnly()
    {
        // Arrange
        await using var db = await ServerDatabase.CreateAsync(server);
        await db.Nonces.AddAsync("hash-1", T0, CancellationToken.None);

        // Act
        var first = await db.Nonces.TryConsumeAsync("hash-1", T0.AddMinutes(-5), T0.AddMinutes(1), CancellationToken.None);
        var second = await db.Nonces.TryConsumeAsync("hash-1", T0.AddMinutes(-5), T0.AddMinutes(2), CancellationToken.None);

        // Assert
        first.Should().BeTrue();
        second.Should().BeFalse();
    }

    [Fact]
    public async Task TryConsumeAsync_IssuedBeforeTheWindow_Fails()
    {
        // Arrange
        await using var db = await ServerDatabase.CreateAsync(server);
        await db.Nonces.AddAsync("hash-1", T0, CancellationToken.None);

        // Act
        var consumed = await db.Nonces.TryConsumeAsync("hash-1", T0.AddSeconds(1), T0.AddMinutes(6), CancellationToken.None);

        // Assert
        consumed.Should().BeFalse();
        (await db.ScalarAsync("SELECT consumed_at IS NULL FROM auth_nonces;")).Should().Be(true);
    }

    [Fact]
    public async Task DeleteIssuedBeforeAsync_OldAndNewRows_DeletesOnlyTheOld()
    {
        // Arrange
        await using var db = await ServerDatabase.CreateAsync(server);
        await db.Nonces.AddAsync("old", T0, CancellationToken.None);
        await db.Nonces.AddAsync("new", T0.AddDays(2), CancellationToken.None);

        // Act
        var deleted = await db.Nonces.DeleteIssuedBeforeAsync(T0.AddDays(1), CancellationToken.None);

        // Assert
        deleted.Should().Be(1);
        (await db.ScalarAsync("SELECT string_agg(nonce_hash, ',') FROM auth_nonces;")).Should().Be("new");
    }

    [Fact]
    public async Task FindByRefreshHashAsync_AfterRotation_FindsTheOldHashAsPrevious()
    {
        // Arrange
        await using var db = await ServerDatabase.CreateAsync(server);
        await CreateAccountAsync(db, User, "sub-1", ServerDatabase.Household);
        var session = Session(User, Phone);
        await db.Sessions.CreateAsync(session, "h1", CancellationToken.None);

        // Act
        var rotated = await db.Sessions.RotateAsync(session.Id, "h1", "h2", T0.AddMinutes(20), CancellationToken.None);
        var current = await db.Sessions.FindByRefreshHashAsync("h2", CancellationToken.None);
        var previous = await db.Sessions.FindByRefreshHashAsync("h1", CancellationToken.None);

        // Assert
        rotated.Should().BeTrue();
        current!.Match.Should().Be(RefreshMatch.Current);
        current.Session.LastUsedAt.Should().Be(T0.AddMinutes(20));
        previous!.Match.Should().Be(RefreshMatch.Previous);
        previous.Session.Id.Should().Be(session.Id);
        (await db.Sessions.FindByRefreshHashAsync("h0", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task RotateAsync_StaleExpectedHash_ChangesNothing()
    {
        // Arrange: two refreshes raced; the first one won.
        await using var db = await ServerDatabase.CreateAsync(server);
        await CreateAccountAsync(db, User, "sub-1", ServerDatabase.Household);
        var session = Session(User, Phone);
        await db.Sessions.CreateAsync(session, "h1", CancellationToken.None);
        await db.Sessions.RotateAsync(session.Id, "h1", "h2", T0.AddMinutes(1), CancellationToken.None);

        // Act
        var rotated = await db.Sessions.RotateAsync(session.Id, "h1", "h3", T0.AddMinutes(2), CancellationToken.None);

        // Assert
        rotated.Should().BeFalse();
        (await db.Sessions.FindByRefreshHashAsync("h2", CancellationToken.None))!.Match.Should().Be(RefreshMatch.Current);
    }

    [Fact]
    public async Task CreateAsync_SameUserAndDevice_ReplacesTheSession()
    {
        // Arrange: signed out on the phone, then signed in again.
        await using var db = await ServerDatabase.CreateAsync(server);
        await CreateAccountAsync(db, User, "sub-1", ServerDatabase.Household);
        var old = Session(User, Phone);
        await db.Sessions.CreateAsync(old, "h1", CancellationToken.None);
        await db.Sessions.RevokeAsync(old.Id, T0.AddMinutes(5), CancellationToken.None);
        var fresh = Session(User, Phone, db.NewId());

        // Act
        await db.Sessions.CreateAsync(fresh, "h9", CancellationToken.None);

        // Assert
        var sessions = await db.Sessions.ListForUserAsync(User, CancellationToken.None);
        sessions.Should().ContainSingle().Which.Id.Should().Be(fresh.Id);
        (await db.Sessions.IsActiveAsync(old.Id, CancellationToken.None)).Should().BeFalse();
        (await db.Sessions.ExistsForDeviceAsync(User, Phone, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task RevokeAsync_Session_IsNoLongerActiveButStillListed()
    {
        // Arrange
        await using var db = await ServerDatabase.CreateAsync(server);
        await CreateAccountAsync(db, User, "sub-1", ServerDatabase.Household);
        var session = Session(User, Phone);
        await db.Sessions.CreateAsync(session, "h1", CancellationToken.None);

        // Act
        await db.Sessions.RevokeAsync(session.Id, T0.AddMinutes(5), CancellationToken.None);

        // Assert
        (await db.Sessions.IsActiveAsync(session.Id, CancellationToken.None)).Should().BeFalse();
        (await db.Sessions.ListForUserAsync(User, CancellationToken.None)).Should().ContainSingle()
            .Which.RevokedAt.Should().Be(T0.AddMinutes(5));
    }

    [Fact]
    public async Task CreateAccountAsync_NewUser_StoresTheUserWithoutAPasswordItsGoogleLoginTheHouseholdAndTheOwner()
    {
        // Arrange
        await using var db = await ServerDatabase.CreateAsync(server);

        // Act
        await CreateAccountAsync(db, User, "sub-1", ServerDatabase.Household);

        // Assert
        await using var scope = db.NewScope();
        var accounts = scope.ServiceProvider.GetRequiredService<IAccountStore>();
        (await accounts.FindByGoogleSubjectAsync("sub-1", CancellationToken.None)).Should().Be(new UserAccount(User, "sub-1@example.com", "Parent sub-1"));
        (await accounts.HouseholdExistsAsync(ServerDatabase.Household, CancellationToken.None)).Should().BeTrue();
        var membership = (await accounts.ListMembershipsOfUserAsync(User, CancellationToken.None)).Should().ContainSingle().Subject;
        membership.HouseholdId.Should().Be(ServerDatabase.Household);
        membership.Role.Should().Be(Coo.Domain.Households.Role.Owner);
        (await db.ScalarAsync($"""SELECT "PasswordHash" IS NULL FROM "AspNetUsers" WHERE "Id" = '{User.Value}';""")).Should().Be(true);
        (await db.ScalarAsync("""SELECT "LoginProvider" || ':' || "ProviderKey" FROM "AspNetUserLogins";""")).Should().Be("Google:sub-1");
        (await db.ScalarAsync($"SELECT created_by FROM households WHERE id = '{ServerDatabase.Household.Value}';")).Should().Be(User.Value);
    }

    [Fact]
    public async Task DeleteAccountAsync_SoleOwner_RemovesTheUserLoginsSessionsMembershipHouseholdAndCareEntries()
    {
        // Arrange
        await using var db = await ServerDatabase.CreateAsync(server);
        await CreateAccountAsync(db, User, "sub-1", ServerDatabase.Household);
        await db.Sessions.CreateAsync(Session(User, Phone), "h1", CancellationToken.None);
        var now = db.Time.GetUtcNow();
        var entry = CareEntry.Create(new EntryId(db.NewId()), ServerDatabase.Household, EntrySubject.Baby(ServerDatabase.Baby),
            new DiaperDetails(DiaperContents.Wet), EntryTiming.Point(now), PersonRef.ThisDevicesPerson, EntrySource.Manual,
            db.NextStamp(), now).Value;
        await db.Store.SaveAsync(entry, CancellationToken.None);

        // Act
        await using (var scope = db.NewScope())
        {
            await scope.ServiceProvider.GetRequiredService<IAccountStore>().DeleteAccountAsync(
                User, new AccountDeletionPlan([ServerDatabase.Household], []), CancellationToken.None);
        }

        // Assert
        foreach (var table in new[] { "\"AspNetUsers\"", "\"AspNetUserLogins\"", "device_sessions", "memberships", "households", "care_entries" })
            (await db.ScalarAsync($"SELECT count(*) FROM {table};")).Should().Be(0L, $"{table} must be empty");
    }

    [Fact]
    public async Task DeleteAccountAsync_OwnershipTransfer_PromotesThePartnerAndKeepsTheHousehold()
    {
        // Arrange: a partner who is a Parent in the owner's household (possible from 11b).
        await using var db = await ServerDatabase.CreateAsync(server);
        await CreateAccountAsync(db, User, "sub-1", ServerDatabase.Household);
        await CreateAccountAsync(db, Partner, "sub-2", PartnersHousehold);
        await db.ExecuteAsync(
            $"INSERT INTO memberships (household_id, user_id, role, joined_at) VALUES ('{ServerDatabase.Household.Value}', '{Partner.Value}', 'parent', now());");

        // Act
        await using (var scope = db.NewScope())
        {
            await scope.ServiceProvider.GetRequiredService<IAccountStore>().DeleteAccountAsync(
                User, new AccountDeletionPlan([], [new OwnershipTransfer(ServerDatabase.Household, Partner)]), CancellationToken.None);
        }

        // Assert
        (await db.ScalarAsync($"SELECT string_agg(user_id::text || ':' || role, ',') FROM memberships WHERE household_id = '{ServerDatabase.Household.Value}';"))
            .Should().Be($"{Partner.Value}:owner");
        (await db.ScalarAsync($"SELECT created_by IS NULL FROM households WHERE id = '{ServerDatabase.Household.Value}';")).Should().Be(true);
    }

    private static DeviceSession Session(UserId user, DeviceId device, Guid? id = null) =>
        new(new DeviceSessionId(id ?? Guid.Parse("7a1c0000-0000-7000-8000-00000000c0f1")), user, device, "Pixel 10", T0, T0, null);

    private static async Task CreateAccountAsync(ServerDatabase db, UserId user, string subject, HouseholdId household)
    {
        await using var scope = db.NewScope();
        await scope.ServiceProvider.GetRequiredService<IAccountStore>().CreateAccountAsync(
            new NewAccount(user, subject, subject + "@example.com", "Parent " + subject, household, T0), CancellationToken.None);
    }
}
```

- [ ] **Step 3: Run them to verify they fail**

Run: `dotnet test tests/Coo.Api.Infrastructure.Tests --filter "FullyQualifiedName~AuthStoreTests"`
Expected: FAIL: no service for `IAuthNonceStore` (and the other stores).

- [ ] **Step 4: Implement the stores**

`src/Backend/Coo.Api.Infrastructure/Persistence/RoleText.cs`:

```csharp
using Coo.Domain.Households;

namespace Coo.Api.Infrastructure.Persistence;

/// <summary>How a role is stored in <c>memberships.role</c>; the check constraint allows exactly these.</summary>
internal static class RoleText
{
    public const string Owner = "owner";
    public const string Parent = "parent";
    public const string Caregiver = "caregiver";

    public static Role Parse(string value) => value switch
    {
        Owner => Role.Owner,
        Parent => Role.Parent,
        Caregiver => Role.Caregiver,
        _ => throw new InvalidOperationException($"Unknown role '{value}' in memberships."),
    };
}
```

`src/Backend/Coo.Api.Infrastructure/Persistence/AuthNonceStore.cs`:

```csharp
using Coo.Api.Application.Auth;
using Npgsql;

namespace Coo.Api.Infrastructure.Persistence;

/// <summary>
/// <c>auth_nonces</c> (Spec 010 §4.2). Consuming is one conditional UPDATE, so a nonce is used at most once even when
/// two requests race.
/// </summary>
internal sealed class AuthNonceStore(NpgsqlDataSource dataSource) : IAuthNonceStore
{
    public async Task AddAsync(string nonceHash, DateTimeOffset issuedAt, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("INSERT INTO auth_nonces (nonce_hash, issued_at) VALUES (@hash, @issued_at);");
        command.Parameters.AddWithValue("hash", nonceHash);
        command.Parameters.AddWithValue("issued_at", issuedAt.UtcDateTime);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> TryConsumeAsync(string nonceHash, DateTimeOffset issuedNotBefore, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            "UPDATE auth_nonces SET consumed_at = @now WHERE nonce_hash = @hash AND consumed_at IS NULL AND issued_at >= @not_before;");
        command.Parameters.AddWithValue("now", now.UtcDateTime);
        command.Parameters.AddWithValue("hash", nonceHash);
        command.Parameters.AddWithValue("not_before", issuedNotBefore.UtcDateTime);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<int> DeleteIssuedBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("DELETE FROM auth_nonces WHERE issued_at < @cutoff;");
        command.Parameters.AddWithValue("cutoff", cutoff.UtcDateTime);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
```

`src/Backend/Coo.Api.Infrastructure/Persistence/DeviceSessionStore.cs`:

```csharp
using Coo.Api.Application.Auth;
using Coo.Domain.Common;
using Npgsql;

namespace Coo.Api.Infrastructure.Persistence;

/// <summary><c>device_sessions</c> (ADR 0003 §4). Rotation is one conditional UPDATE, so two refreshes cannot both win.</summary>
internal sealed class DeviceSessionStore(NpgsqlDataSource dataSource) : IDeviceSessionStore
{
    private const string SelectColumns = "SELECT id, user_id, device_id, device_name, created_at, last_used_at, revoked_at";

    private const string FindSql = SelectColumns + ", refresh_hash = @hash FROM device_sessions "
        + "WHERE refresh_hash = @hash OR previous_refresh_hash = @hash LIMIT 1;";

    private const string ListSql = SelectColumns + " FROM device_sessions WHERE user_id = @user ORDER BY created_at;";

    public async Task CreateAsync(DeviceSession session, string refreshHash, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var delete = new NpgsqlCommand("DELETE FROM device_sessions WHERE user_id = @user AND device_id = @device;", connection, transaction))
        {
            delete.Parameters.AddWithValue("user", session.UserId.Value);
            delete.Parameters.AddWithValue("device", session.DeviceId.Value);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var insert = new NpgsqlCommand(
            "INSERT INTO device_sessions (id, user_id, device_id, device_name, refresh_hash, created_at, last_used_at) "
            + "VALUES (@id, @user, @device, @name, @hash, @created_at, @last_used_at);", connection, transaction))
        {
            insert.Parameters.AddWithValue("id", session.Id.Value);
            insert.Parameters.AddWithValue("user", session.UserId.Value);
            insert.Parameters.AddWithValue("device", session.DeviceId.Value);
            insert.Parameters.AddWithValue("name", session.DeviceName);
            insert.Parameters.AddWithValue("hash", refreshHash);
            insert.Parameters.AddWithValue("created_at", session.CreatedAt.UtcDateTime);
            insert.Parameters.AddWithValue("last_used_at", session.LastUsedAt.UtcDateTime);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<RefreshLookup?> FindByRefreshHashAsync(string refreshHash, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(FindSql);
        command.Parameters.AddWithValue("hash", refreshHash);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;
        return new RefreshLookup(Read(reader), reader.GetBoolean(7) ? RefreshMatch.Current : RefreshMatch.Previous);
    }

    public async Task<bool> RotateAsync(DeviceSessionId id, string expectedHash, string newHash, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            "UPDATE device_sessions SET previous_refresh_hash = refresh_hash, refresh_hash = @new, last_used_at = @now "
            + "WHERE id = @id AND refresh_hash = @expected AND revoked_at IS NULL;");
        command.Parameters.AddWithValue("new", newHash);
        command.Parameters.AddWithValue("now", now.UtcDateTime);
        command.Parameters.AddWithValue("id", id.Value);
        command.Parameters.AddWithValue("expected", expectedHash);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task RevokeAsync(DeviceSessionId id, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("UPDATE device_sessions SET revoked_at = @now WHERE id = @id AND revoked_at IS NULL;");
        command.Parameters.AddWithValue("now", now.UtcDateTime);
        command.Parameters.AddWithValue("id", id.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> IsActiveAsync(DeviceSessionId id, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("SELECT EXISTS (SELECT 1 FROM device_sessions WHERE id = @id AND revoked_at IS NULL);");
        command.Parameters.AddWithValue("id", id.Value);
        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    public async Task<IReadOnlyList<DeviceSession>> ListForUserAsync(UserId user, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(ListSql);
        command.Parameters.AddWithValue("user", user.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var sessions = new List<DeviceSession>();
        while (await reader.ReadAsync(cancellationToken))
            sessions.Add(Read(reader));
        return sessions;
    }

    public async Task<bool> ExistsForDeviceAsync(UserId user, DeviceId device, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("SELECT EXISTS (SELECT 1 FROM device_sessions WHERE user_id = @user AND device_id = @device);");
        command.Parameters.AddWithValue("user", user.Value);
        command.Parameters.AddWithValue("device", device.Value);
        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    private static DeviceSession Read(NpgsqlDataReader reader) => new(
        new DeviceSessionId(reader.GetGuid(0)), new UserId(reader.GetGuid(1)), new DeviceId(reader.GetGuid(2)), reader.GetString(3),
        Utc(reader.GetDateTime(4)), Utc(reader.GetDateTime(5)), reader.IsDBNull(6) ? null : Utc(reader.GetDateTime(6)));

    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}
```

`src/Backend/Coo.Api.Infrastructure/Persistence/AccountStore.cs`:

```csharp
using System.Globalization;
using Coo.Api.Application.Auth;
using Coo.Domain.Common;
using Coo.Domain.Households;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Coo.Api.Infrastructure.Persistence;

/// <summary>
/// Users through ASP.NET Core Identity (ADR 0003 §3), households and memberships. Scoped: it shares the request's
/// <see cref="CooDbContext"/> with Identity's user store, so the user, their login, the household and the membership
/// are written in one transaction. Identity's <see cref="UserManager{TUser}"/> takes no cancellation token; reads go
/// through the context, which does.
/// </summary>
internal sealed class AccountStore(CooDbContext context, UserManager<CooUser> users) : IAccountStore
{
    public const string GoogleProvider = "Google";

    public async Task<UserAccount?> FindByGoogleSubjectAsync(string subject, CancellationToken cancellationToken)
    {
        var user = await context.Users.AsNoTracking()
            .Where(u => context.UserLogins.Any(l => l.UserId == u.Id && l.LoginProvider == GoogleProvider && l.ProviderKey == subject))
            .SingleOrDefaultAsync(cancellationToken);
        return user is null ? null : ToAccount(user);
    }

    public async Task<UserAccount?> FindAsync(UserId id, CancellationToken cancellationToken)
    {
        var user = await context.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == id.Value, cancellationToken);
        return user is null ? null : ToAccount(user);
    }

    public Task<bool> HouseholdExistsAsync(HouseholdId id, CancellationToken cancellationToken) =>
        context.Households.AnyAsync(h => h.Id == id.Value, cancellationToken);

    public async Task CreateAccountAsync(NewAccount account, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var user = new CooUser
        {
            Id = account.UserId.Value,
            UserName = "google-" + account.GoogleSubject,
            Email = account.Email,
            EmailConfirmed = true,
            DisplayName = account.DisplayName,
        };
        Check(await users.CreateAsync(user));
        Check(await users.AddLoginAsync(user, new UserLoginInfo(GoogleProvider, account.GoogleSubject, GoogleProvider)));
        AddHousehold(account.Household, account.UserId, account.CreatedAt);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task CreateHouseholdAsync(HouseholdId household, UserId owner, DateTimeOffset now, CancellationToken cancellationToken)
    {
        AddHousehold(household, owner, now);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateProfileAsync(UserAccount account, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);
        var user = await users.FindByIdAsync(account.Id.Value.ToString("D", CultureInfo.InvariantCulture));
        if (user is null)
            return;
        user.Email = account.Email;
        user.DisplayName = account.DisplayName;
        Check(await users.UpdateAsync(user));
    }

    public async Task<IReadOnlyList<Membership>> ListMembershipsOfUserAsync(UserId user, CancellationToken cancellationToken) =>
        [.. (await context.Memberships.AsNoTracking().Where(m => m.UserId == user.Value).ToListAsync(cancellationToken)).Select(ToMembership)];

    public async Task<IReadOnlyList<Membership>> ListMembersAsync(HouseholdId household, CancellationToken cancellationToken) =>
        [.. (await context.Memberships.AsNoTracking().Where(m => m.HouseholdId == household.Value).ToListAsync(cancellationToken)).Select(ToMembership)];

    public async Task DeleteAccountAsync(UserId user, AccountDeletionPlan plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        // The leaver's memberships first, so a promoted parent never meets a second Owner (ix_memberships_one_owner).
        await context.Memberships.Where(m => m.UserId == user.Value).ExecuteDeleteAsync(cancellationToken);
        foreach (var transfer in plan.OwnershipTransfers)
        {
            await context.Memberships
                .Where(m => m.HouseholdId == transfer.Household.Value && m.UserId == transfer.NewOwner.Value)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.Role, RoleText.Owner), cancellationToken);
        }

        foreach (var household in plan.HouseholdsToDelete)
        {
            await context.CareEntries.Where(c => c.HouseholdId == household.Value).ExecuteDeleteAsync(cancellationToken);
            await context.Households.Where(h => h.Id == household.Value).ExecuteDeleteAsync(cancellationToken);
        }

        // Logins, claims, tokens and device sessions go with the user (ON DELETE CASCADE); created_by becomes NULL.
        await context.Users.Where(u => u.Id == user.Value).ExecuteDeleteAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private void AddHousehold(HouseholdId household, UserId owner, DateTimeOffset now)
    {
        context.Households.Add(new HouseholdRecord { Id = household.Value, CreatedAt = now.UtcDateTime, CreatedBy = owner.Value });
        context.Memberships.Add(new MembershipRecord
        {
            HouseholdId = household.Value,
            UserId = owner.Value,
            Role = RoleText.Owner,
            JoinedAt = now.UtcDateTime,
        });
    }

    private static UserAccount ToAccount(CooUser user) => new(new UserId(user.Id), user.Email ?? "", user.DisplayName);

    private static Membership ToMembership(MembershipRecord row) => Membership.Create(
        new HouseholdId(row.HouseholdId), new UserId(row.UserId), RoleText.Parse(row.Role),
        new DateTimeOffset(DateTime.SpecifyKind(row.JoinedAt, DateTimeKind.Utc)));

    private static void Check(IdentityResult result)
    {
        if (!result.Succeeded)
            throw new InvalidOperationException("Identity refused the account: " + string.Join(", ", result.Errors.Select(e => e.Code)));
    }
}
```

- [ ] **Step 5: Register them**

In `ServiceCollectionExtensions.AddApiInfrastructure` (add `using Coo.Api.Application.Auth;`), after the existing
registrations and before `AddHostedService<DatabaseStartup>()`:

```csharp
        services.AddSingleton<IAuthNonceStore, AuthNonceStore>();
        services.AddSingleton<IDeviceSessionStore, DeviceSessionStore>();

        // Identity's user store and AccountStore share one scoped context per request, so an account is created in one
        // transaction (Spec 010 §4.2). Registered explicitly; AddDbContextFactory's own scoped registration is the same.
        services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<CooDbContext>>().CreateDbContext());
        services.AddIdentityCore<CooUser>().AddEntityFrameworkStores<CooDbContext>();
        services.AddScoped<IAccountStore, AccountStore>();
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test tests/Coo.Api.Infrastructure.Tests && dotnet test tests/Coo.Architecture.Tests`
Expected: PASS (10 new store tests, every earlier test); the architecture tests PASS (the stores are `internal`, in
`Persistence`).

- [ ] **Step 7: Commit**

```bash
git add src/Backend/Coo.Api.Infrastructure tests/Coo.Api.Infrastructure.Tests
git commit -m "Server stores: nonces, device sessions with rotation, accounts through Identity (Spec 010 §4.2)"
```

---

### Task 6: Tokens and the Google verifier (`Coo.Api.Infrastructure/Identity`)

**Files:**
- Modify: `Directory.Packages.props`, `src/Backend/Coo.Api.Infrastructure/Coo.Api.Infrastructure.csproj`
- Create: `src/Backend/Coo.Api.Infrastructure/Identity/SigningKeyRing.cs`
- Create: `src/Backend/Coo.Api.Infrastructure/Identity/JwtTokenIssuer.cs`
- Create: `src/Backend/Coo.Api.Infrastructure/Identity/AccessTokenValidation.cs`
- Create: `src/Backend/Coo.Api.Infrastructure/Identity/JwtBearerSetup.cs`
- Create: `src/Backend/Coo.Api.Infrastructure/Identity/GoogleIdTokenVerifier.cs`
- Modify: `src/Backend/Coo.Api.Infrastructure/ServiceCollectionExtensions.cs` (`AddApiIdentity`)
- Test: `tests/Coo.Api.Infrastructure.Tests/TokenTests.cs`

**Interfaces:**
- Consumes: Task 2's `ITokenIssuer`, `AccessToken`, `AccessTokenClaims`, `IExternalIdentityVerifier`,
  `ExternalIdentity`, `IDeviceSessionStore`, `DeviceSessionId`.
- Produces:
  - `internal sealed class SigningKeyRing(string activeKeyId, IReadOnlyDictionary<string, byte[]> keys)` with
    `IReadOnlyList<SecurityKey> Keys` and `SigningCredentials Active`.
  - `internal sealed class JwtTokenIssuer(SigningKeyRing keys) : ITokenIssuer` with `const string Issuer = "coo-api"`,
    `const string Audience = "coo-app"`, `static readonly TimeSpan Lifetime` (15 min).
  - `internal static class AccessTokenValidation { public static TokenValidationParameters Parameters(SigningKeyRing keys, TimeProvider time); }`
    (the token's `kid` must name a configured key; lifetime checked against the injected clock, 30 s skew).
  - `internal sealed class JwtBearerSetup(SigningKeyRing keys, TimeProvider time, IDeviceSessionStore sessions) : IConfigureNamedOptions<JwtBearerOptions>`
    (refuses a token whose device session is not active).
  - `internal sealed partial class GoogleIdTokenVerifier(string? webClientId, TimeProvider time, ILogger<GoogleIdTokenVerifier> logger) : IExternalIdentityVerifier`
    with `internal static ExternalIdentity? ToIdentity(GoogleJsonWebSignature.Payload payload)`.
  - `public static IServiceCollection AddApiIdentity(this IServiceCollection services, string? googleWebClientId, string activeKeyId, IReadOnlyDictionary<string, byte[]> signingKeys)`.

- [ ] **Step 1: Add the packages**

In `Directory.Packages.props`, under the server lines:

```xml
    <!-- Sign-in (Spec 010 §4): our own JWTs, and Google ID token verification with cached Google keys (ADR 0003 §5). -->
    <PackageVersion Include="Microsoft.AspNetCore.Authentication.JwtBearer" Version="10.0.12" />
    <PackageVersion Include="Google.Apis.Auth" Version="1.75.0" />
```

In `Coo.Api.Infrastructure.csproj`:

```xml
  <ItemGroup>
    <!-- JwtBearerOptions and Identity's ASP.NET Core types. -->
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
  </ItemGroup>
```

and, with the other package references:

```xml
    <PackageReference Include="Microsoft.AspNetCore.Authentication.JwtBearer" />
    <PackageReference Include="Google.Apis.Auth" />
```

Confirm the versions as in Task 4 (`dotnet package search <id> --exact-match`). `Google.Apis.Auth` may be newer than
1.75.0 on nuget.org; any 1.7x works with the code below. If restore reports `NU1901`–`NU1904` (a vulnerable transitive
package, an error here), pin that package to its patched version in `Directory.Packages.props` with a comment naming the
advisory.

- [ ] **Step 2: Write the failing tests**

`tests/Coo.Api.Infrastructure.Tests/TokenTests.cs`:

```csharp
using System.Globalization;
using Coo.Api.Application.Auth;
using Coo.Api.Infrastructure.Identity;
using Coo.Domain.Common;
using Google.Apis.Auth;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Coo.Api.Infrastructure.Tests;

/// <summary>Spec 010 §3.3 and §7: the real issuer and validator, two key IDs, an unknown key; the verifier fails closed.</summary>
public sealed class TokenTests
{
    private static readonly byte[] KeyOne = [.. Enumerable.Repeat((byte)1, 32)];
    private static readonly byte[] KeyTwo = [.. Enumerable.Repeat((byte)2, 32)];
    private static readonly UserId User = new(Guid.Parse("7a1c0000-0000-7000-8000-00000000c0c1"));
    private static readonly DeviceSessionId Session = new(Guid.Parse("7a1c0000-0000-7000-8000-00000000c0f1"));

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task Validate_TokensFromEitherOfTwoKeys_AreAcceptedWithTheirClaims()
    {
        // Arrange: k1 signed before the rotation, k2 signs now; both stay valid while both are configured.
        var beforeRotation = new JwtTokenIssuer(Ring("k1", ("k1", KeyOne)));
        var afterRotation = new JwtTokenIssuer(Ring("k2", ("k1", KeyOne), ("k2", KeyTwo)));
        var oldToken = beforeRotation.Issue(User, Session, _time.GetUtcNow());
        var newToken = afterRotation.Issue(User, Session, _time.GetUtcNow());
        var parameters = AccessTokenValidation.Parameters(Ring("k2", ("k1", KeyOne), ("k2", KeyTwo)), _time);

        // Act
        var results = new[] { await ValidateAsync(oldToken.Value, parameters), await ValidateAsync(newToken.Value, parameters) };

        // Assert
        results.Should().OnlyContain(r => r.IsValid);
        results[1].Claims[AccessTokenClaims.User].Should().Be(User.Value.ToString("D", CultureInfo.InvariantCulture));
        results[1].Claims[AccessTokenClaims.Session].Should().Be(Session.Value.ToString("D", CultureInfo.InvariantCulture));
        new JsonWebToken(oldToken.Value).Kid.Should().Be("k1");
        new JsonWebToken(newToken.Value).Kid.Should().Be("k2");
        newToken.ExpiresAt.Should().Be(_time.GetUtcNow() + TimeSpan.FromMinutes(15));
    }

    [Fact]
    public async Task Validate_TokenSignedWithAnUnknownKey_IsRefused()
    {
        // Arrange
        var stranger = new JwtTokenIssuer(Ring("k9", ("k9", KeyTwo)));
        var token = stranger.Issue(User, Session, _time.GetUtcNow());

        // Act
        var result = await ValidateAsync(token.Value, AccessTokenValidation.Parameters(Ring("k1", ("k1", KeyOne)), _time));

        // Assert
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Validate_KnownKeyIdWithTheWrongSecret_IsRefused()
    {
        // Arrange: a forger who guessed the key ID but not the key.
        var forger = new JwtTokenIssuer(Ring("k1", ("k1", KeyTwo)));
        var token = forger.Issue(User, Session, _time.GetUtcNow());

        // Act
        var result = await ValidateAsync(token.Value, AccessTokenValidation.Parameters(Ring("k1", ("k1", KeyOne)), _time));

        // Assert
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Validate_After15MinutesAndTheSkew_IsRefused()
    {
        // Arrange
        var issuer = new JwtTokenIssuer(Ring("k1", ("k1", KeyOne)));
        var token = issuer.Issue(User, Session, _time.GetUtcNow());
        _time.Advance(TimeSpan.FromMinutes(15) + TimeSpan.FromSeconds(31));

        // Act
        var result = await ValidateAsync(token.Value, AccessTokenValidation.Parameters(Ring("k1", ("k1", KeyOne)), _time));

        // Assert
        result.IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData("not-a-jwt")]
    [InlineData("")]
    public async Task VerifyAsync_GarbageToken_ReturnsNull(string token)
    {
        // Arrange
        var verifier = new GoogleIdTokenVerifier("client.apps.googleusercontent.com", _time, NullLogger<GoogleIdTokenVerifier>.Instance);

        // Act
        var identity = await verifier.VerifyAsync(token, CancellationToken.None);

        // Assert
        identity.Should().BeNull();
    }

    [Fact]
    public async Task VerifyAsync_NoClientIdConfigured_RefusesEverything()
    {
        // Arrange: outside Production without settings; never "accept everything" (ADR 0003 §5).
        var verifier = new GoogleIdTokenVerifier(null, _time, NullLogger<GoogleIdTokenVerifier>.Instance);

        // Act
        var identity = await verifier.VerifyAsync("eyJhbGciOiJSUzI1NiJ9.e30.c2ln", CancellationToken.None);

        // Assert
        identity.Should().BeNull();
    }

    [Fact]
    public void ToIdentity_VerifiedGooglePayload_ReturnsTheIdentity()
    {
        // Arrange
        var payload = Payload();

        // Act
        var identity = GoogleIdTokenVerifier.ToIdentity(payload);

        // Assert
        identity.Should().Be(new ExternalIdentity("1234567890", "parent@example.com", "A Parent", "nonce-1"));
    }

    [Fact]
    public void ToIdentity_EmailNotVerified_ReturnsNull()
    {
        // Arrange
        var payload = Payload();
        payload.EmailVerified = false;

        // Act
        var identity = GoogleIdTokenVerifier.ToIdentity(payload);

        // Assert
        identity.Should().BeNull();
    }

    [Theory]
    [InlineData("https://evil.example.com", "nonce-1")]
    [InlineData("https://accounts.google.com", "")]
    public void ToIdentity_WrongIssuerOrNoNonce_ReturnsNull(string issuer, string nonce)
    {
        // Arrange
        var payload = Payload();
        payload.Issuer = issuer;
        payload.Nonce = nonce;

        // Act
        var identity = GoogleIdTokenVerifier.ToIdentity(payload);

        // Assert
        identity.Should().BeNull();
    }

    private static GoogleJsonWebSignature.Payload Payload() => new()
    {
        Issuer = "https://accounts.google.com",
        Subject = "1234567890",
        Email = "parent@example.com",
        EmailVerified = true,
        Name = "A Parent",
        Nonce = "nonce-1",
    };

    private static SigningKeyRing Ring(string active, params (string Id, byte[] Key)[] keys) =>
        new(active, keys.ToDictionary(k => k.Id, k => k.Key, StringComparer.Ordinal));

    private static Task<TokenValidationResult> ValidateAsync(string token, TokenValidationParameters parameters) =>
        new JsonWebTokenHandler().ValidateTokenAsync(token, parameters);
}
```

- [ ] **Step 3: Run them to verify they fail**

Run: `dotnet test tests/Coo.Api.Infrastructure.Tests --filter "FullyQualifiedName~TokenTests"`
Expected: build errors, the `Coo.Api.Infrastructure.Identity` types do not exist.

- [ ] **Step 4: Implement**

`src/Backend/Coo.Api.Infrastructure/Identity/SigningKeyRing.cs`:

```csharp
using Microsoft.IdentityModel.Tokens;

namespace Coo.Api.Infrastructure.Identity;

/// <summary>
/// The HMAC-SHA256 keys (ADR 0003 §4): one or two, each with its ID. One signs; all of them validate, so a key can be
/// rotated without signing anyone out. The host has already checked the sizes (StartupChecks.SelectAuth).
/// </summary>
internal sealed class SigningKeyRing
{
    public SigningKeyRing(string activeKeyId, IReadOnlyDictionary<string, byte[]> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        Keys = [.. keys.Select(k => (SecurityKey)new SymmetricSecurityKey(k.Value) { KeyId = k.Key })];
        var active = Keys.SingleOrDefault(k => k.KeyId == activeKeyId)
            ?? throw new ArgumentException($"The active signing key '{activeKeyId}' is not configured.", nameof(activeKeyId));
        Active = new SigningCredentials(active, SecurityAlgorithms.HmacSha256);
    }

    public IReadOnlyList<SecurityKey> Keys { get; }

    public SigningCredentials Active { get; }
}
```

`src/Backend/Coo.Api.Infrastructure/Identity/JwtTokenIssuer.cs`:

```csharp
using System.Globalization;
using Coo.Api.Application.Auth;
using Coo.Domain.Common;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Coo.Api.Infrastructure.Identity;

/// <summary>
/// The access token (ADR 0003 §4): a JWT, HMAC-SHA256, 15 minutes. Its claims are the user and the device session; the
/// key ID is in the header. No household claim: membership is checked per request.
/// </summary>
internal sealed class JwtTokenIssuer(SigningKeyRing keys) : ITokenIssuer
{
    public const string Issuer = "coo-api";
    public const string Audience = "coo-app";
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    private readonly JsonWebTokenHandler _handler = new();

    public AccessToken Issue(UserId user, DeviceSessionId session, DateTimeOffset now)
    {
        var expires = now + Lifetime;
        var token = _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            Claims = new Dictionary<string, object>
            {
                [AccessTokenClaims.User] = user.Value.ToString("D", CultureInfo.InvariantCulture),
                [AccessTokenClaims.Session] = session.Value.ToString("D", CultureInfo.InvariantCulture),
            },
            SigningCredentials = keys.Active,
        });
        return new AccessToken(token, expires);
    }
}
```

`src/Backend/Coo.Api.Infrastructure/Identity/AccessTokenValidation.cs`:

```csharp
using Coo.Api.Application.Auth;
using Microsoft.IdentityModel.Tokens;

namespace Coo.Api.Infrastructure.Identity;

/// <summary>How an access token is checked. The same parameters serve the bearer handler and the tests.</summary>
internal static class AccessTokenValidation
{
    public static TokenValidationParameters Parameters(SigningKeyRing keys, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(time);
        return new TokenValidationParameters
        {
            ValidIssuer = JwtTokenIssuer.Issuer,
            ValidAudience = JwtTokenIssuer.Audience,
            IssuerSigningKeys = keys.Keys,
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            RequireSignedTokens = true,
            RequireExpirationTime = true,
            ValidateIssuerSigningKey = true,

            // The token's kid must name a configured key: a token signed with any other key is refused.
            TryAllIssuerSigningKeys = false,
            ClockSkew = TimeSpan.FromSeconds(30),

            // The injected clock, not the machine's (ADR 0001 §2).
            LifetimeValidator = (notBefore, expires, _, parameters) =>
            {
                var now = time.GetUtcNow().UtcDateTime;
                return expires is { } end && end > now - parameters.ClockSkew
                    && (notBefore is not { } start || start <= now + parameters.ClockSkew);
            },
            NameClaimType = AccessTokenClaims.User,
        };
    }
}
```

`src/Backend/Coo.Api.Infrastructure/Identity/JwtBearerSetup.cs`:

```csharp
using Coo.Api.Application.Auth;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;

namespace Coo.Api.Infrastructure.Identity;

/// <summary>
/// The bearer handler's settings. Beyond the signature and lifetime, the device session must still be active: a
/// signed-out or replaced session is refused at once, not when its token expires (Spec 010 §8, deliverable 6).
/// </summary>
internal sealed class JwtBearerSetup(SigningKeyRing keys, TimeProvider time, IDeviceSessionStore sessions) : IConfigureNamedOptions<JwtBearerOptions>
{
    public void Configure(string? name, JwtBearerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (name != JwtBearerDefaults.AuthenticationScheme)
            return;
        options.MapInboundClaims = false;
        options.TokenValidationParameters = AccessTokenValidation.Parameters(keys, time);
        options.Events = new JwtBearerEvents { OnTokenValidated = OnTokenValidatedAsync };
    }

    public void Configure(JwtBearerOptions options) => Configure(JwtBearerDefaults.AuthenticationScheme, options);

    private async Task OnTokenValidatedAsync(TokenValidatedContext context)
    {
        var session = context.Principal?.FindFirst(AccessTokenClaims.Session)?.Value;
        if (!Guid.TryParse(session, out var id) || id == Guid.Empty
            || !await sessions.IsActiveAsync(new DeviceSessionId(id), context.HttpContext.RequestAborted))
            context.Fail("The device session is not active.");
    }
}
```

`src/Backend/Coo.Api.Infrastructure/Identity/GoogleIdTokenVerifier.cs`:

```csharp
using Coo.Api.Application.Auth;
using Google.Apis.Auth;
using Microsoft.Extensions.Logging;

namespace Coo.Api.Infrastructure.Identity;

/// <summary>
/// Verifies a Google ID token (ADR 0003 §5) with Google's published keys, which the library caches. The signature,
/// <c>iss</c>, <c>aud</c> (Coo's Web client ID) and expiry are checked by the library, <c>email_verified</c> and the
/// presence of a nonce here. Every failure, including Google's keys being unreachable, returns null: fail closed.
/// </summary>
internal sealed partial class GoogleIdTokenVerifier(string? webClientId, TimeProvider time, ILogger<GoogleIdTokenVerifier> logger)
    : IExternalIdentityVerifier
{
    public async Task<ExternalIdentity?> VerifyAsync(string idToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(webClientId) || string.IsNullOrWhiteSpace(idToken))
            return null;
        try
        {
            var payload = await GoogleJsonWebSignature.ValidateAsync(idToken, new GoogleJsonWebSignature.ValidationSettings
            {
                Audience = [webClientId],
                Clock = new InjectedClock(time),
                IssuedAtClockTolerance = TimeSpan.FromMinutes(1),
                ExpirationTimeClockTolerance = TimeSpan.FromMinutes(1),
            });
            return ToIdentity(payload);
        }
        catch (Exception e) when (!cancellationToken.IsCancellationRequested)
        {
            LogRefused(logger, e.GetType().Name);
            return null;
        }
    }

    internal static ExternalIdentity? ToIdentity(GoogleJsonWebSignature.Payload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.Issuer is not ("accounts.google.com" or "https://accounts.google.com")
            || payload.EmailVerified != true
            || string.IsNullOrEmpty(payload.Subject) || string.IsNullOrEmpty(payload.Email) || string.IsNullOrEmpty(payload.Nonce))
            return null;
        return new ExternalIdentity(payload.Subject, payload.Email, string.IsNullOrWhiteSpace(payload.Name) ? payload.Email : payload.Name, payload.Nonce);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Google ID token refused ({Reason})")]
    private static partial void LogRefused(ILogger logger, string reason);

    /// <summary>Google's library asks for the time through its own interface; it gets the injected clock.</summary>
    private sealed class InjectedClock(TimeProvider time) : Google.Apis.Util.IClock
    {
        public DateTime Now => time.GetLocalNow().DateTime;

        public DateTime UtcNow => time.GetUtcNow().UtcDateTime;
    }
}
```

If the installed `Google.Apis.Auth` marks `IClock.Now` obsolete (`CS0618`, an error here), keep the member and add
`[Obsolete("Required by Google.Apis.Util.IClock.")]` to it. If `GoogleJsonWebSignature.ValidateAsync` has an overload
taking a `CancellationToken`, pass `cancellationToken` (CA2016 will say so).

- [ ] **Step 5: `AddApiIdentity`**

Add to `ServiceCollectionExtensions` (with `using Coo.Api.Infrastructure.Identity;`,
`using Microsoft.AspNetCore.Authentication.JwtBearer;`, `using Microsoft.Extensions.Logging;`,
`using Microsoft.Extensions.Options;`):

```csharp
    /// <summary>
    /// Sign-in (Spec 010 §4): the access-token issuer and the bearer validation (one or two signing keys, one of them
    /// active), and the Google ID token verifier for <paramref name="googleWebClientId"/>. A null client ID refuses every
    /// sign-in. The host checks the settings at start-up and calls <c>AddAuthentication().AddJwtBearer()</c> itself.
    /// </summary>
    public static IServiceCollection AddApiIdentity(
        this IServiceCollection services, string? googleWebClientId, string activeKeyId, IReadOnlyDictionary<string, byte[]> signingKeys)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton(new SigningKeyRing(activeKeyId, signingKeys));
        services.AddSingleton<ITokenIssuer, JwtTokenIssuer>();
        services.AddSingleton<IExternalIdentityVerifier>(sp => new GoogleIdTokenVerifier(
            googleWebClientId, sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<GoogleIdTokenVerifier>>()));
        services.AddSingleton<IConfigureOptions<JwtBearerOptions>, JwtBearerSetup>();
        return services;
    }
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test tests/Coo.Api.Infrastructure.Tests && dotnet test tests/Coo.Architecture.Tests`
Expected: PASS (11 token and verifier tests; the `Identity` namespace uses neither EF Core nor Npgsql; every type is
`internal`).

- [ ] **Step 7: Commit**

```bash
git add Directory.Packages.props src/Backend/Coo.Api.Infrastructure tests/Coo.Api.Infrastructure.Tests/TokenTests.cs
git commit -m "Server tokens: HMAC JWT with two key IDs, session-checked bearer validation, Google verifier failing closed (Spec 010 §3)"
```

---
### Task 7: The sign-in settings at start-up, and Production's refusal

**Files:**
- Create: `src/Backend/Coo.Api/Hosting/AuthSettings.cs`
- Modify: `src/Backend/Coo.Api/Hosting/StartupChecks.cs`
- Modify: `src/Backend/Coo.Api/Program.cs`, `src/Backend/Coo.Api/Coo.Api.csproj`
- Create: `tests/Coo.Api.IntegrationTests/TestApi.cs`
- Modify: `tests/Coo.Api.IntegrationTests/ApiTests.cs`
- Test: `tests/Coo.Api.IntegrationTests/StartupChecksTests.cs`

**Interfaces:**
- Consumes: Task 6's `AddApiIdentity`.
- Produces:
  - `internal sealed record AuthSettings(string? GoogleWebClientId, string ActiveKeyId, IReadOnlyDictionary<string, byte[]> SigningKeys)`.
  - `StartupChecks.GoogleWebClientIdKey = "Auth:GoogleWebClientId"`, `SigningKeysSection = "Auth:SigningKeys"`,
    `ActiveSigningKeyIdKey = "Auth:ActiveSigningKeyId"`, `DevelopmentKeyId = "development"`, `MinimumKeyBytes = 32`, and
    `public static AuthSettings SelectAuth(string? googleWebClientId, IEnumerable<KeyValuePair<string, string?>> signingKeys, string? activeKeyId, bool isProduction, byte[] developmentKey)`.
  - Test helper `TestApi.Create(string connectionString, IExternalIdentityVerifier? google = null, int? permitsPerMinute = 1000, Action<IWebHostBuilder>? configure = null)`,
    `TestApi.ClientId`, `TestApi.SigningKey`.
  - The API is version **0.10.0**; JWT bearer authentication and authorization are in the pipeline.

- [ ] **Step 1: Write the failing tests**

`tests/Coo.Api.IntegrationTests/TestApi.cs`:

```csharp
using System.Globalization;
using Coo.Api.Application.Auth;
using Coo.Api.Hosting;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Coo.Api.IntegrationTests;

/// <summary>The API as production runs it (Production environment), with test settings.</summary>
internal static class TestApi
{
    public const string ClientId = "test-client.apps.googleusercontent.com";
    public static readonly string SigningKey = Convert.ToBase64String([.. Enumerable.Repeat((byte)7, 32)]);

    /// <param name="google">Replaces the real Google verifier (ADR 0003 §5's port), when given.</param>
    /// <param name="permitsPerMinute">The auth rate limit; null keeps the production default of 20.</param>
    public static WebApplicationFactory<Program> Create(
        string connectionString, IExternalIdentityVerifier? google = null, int? permitsPerMinute = 1000, Action<IWebHostBuilder>? configure = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
        {
            host.UseEnvironment("Production");
            host.UseSetting($"ConnectionStrings:{StartupChecks.ConnectionStringName}", connectionString);
            host.UseSetting(StartupChecks.GoogleWebClientIdKey, ClientId);
            host.UseSetting(StartupChecks.SigningKeysSection + ":k1", SigningKey);
            if (permitsPerMinute is { } permits)
                host.UseSetting("Auth:PermitsPerMinute", permits.ToString(CultureInfo.InvariantCulture));
            if (google is not null)
                host.ConfigureTestServices(services => services.AddSingleton(google));
            configure?.Invoke(host);
        });
}
```

In `ApiTests.cs`, replace the body of `Api(string connectionString)` with `TestApi.Create(connectionString);` (drop the now
unused usings), and in `Health_DatabaseReady_Returns200WithTheVersionAndAMigratedDatabase` expect
`StartWith("0.10.0")`.

`tests/Coo.Api.IntegrationTests/StartupChecksTests.cs`:

```csharp
using Coo.Api.Hosting;
using Microsoft.AspNetCore.Hosting;

namespace Coo.Api.IntegrationTests;

/// <summary>Spec 010 §4.3: the choice is a pure function, and Production refuses to start without the settings.</summary>
public sealed class StartupChecksTests
{
    private const string ClientId = "client.apps.googleusercontent.com";
    private static readonly byte[] DevelopmentKey = [.. Enumerable.Repeat((byte)9, 32)];
    private static readonly string KeyA = Convert.ToBase64String([.. Enumerable.Repeat((byte)1, 32)]);
    private static readonly string KeyB = Convert.ToBase64String([.. Enumerable.Repeat((byte)2, 32)]);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SelectAuth_ProductionWithoutTheClientId_ThrowsNamingTheSetting(string? clientId)
    {
        // Arrange: the dangerous case. Without an audience to check, sign-in must not start at all.
        var keys = Keys(("k1", KeyA));

        // Act
        var act = () => StartupChecks.SelectAuth(clientId, keys, null, isProduction: true, DevelopmentKey);

        // Assert
        act.Should().Throw<InvalidOperationException>().WithMessage("*Auth:GoogleWebClientId*");
    }

    [Fact]
    public void SelectAuth_ProductionWithoutKeys_ThrowsNamingTheSetting()
    {
        // Arrange: a random key per process would sign everyone out at every restart.
        var keys = Keys();

        // Act
        var act = () => StartupChecks.SelectAuth(ClientId, keys, null, isProduction: true, DevelopmentKey);

        // Assert
        act.Should().Throw<InvalidOperationException>().WithMessage("*Auth:SigningKeys*");
    }

    [Fact]
    public void SelectAuth_ProductionComplete_ReturnsTheSettings()
    {
        // Arrange
        var keys = Keys(("k1", KeyA));

        // Act
        var settings = StartupChecks.SelectAuth(ClientId, keys, null, isProduction: true, DevelopmentKey);

        // Assert
        settings.GoogleWebClientId.Should().Be(ClientId);
        settings.ActiveKeyId.Should().Be("k1");
        settings.SigningKeys["k1"].Should().Equal(Convert.FromBase64String(KeyA));
    }

    [Fact]
    public void SelectAuth_DevelopmentWithoutSettings_UsesTheDevelopmentKeyAndNoClientId()
    {
        // Arrange
        var keys = Keys();

        // Act
        var settings = StartupChecks.SelectAuth(null, keys, null, isProduction: false, DevelopmentKey);

        // Assert
        settings.GoogleWebClientId.Should().BeNull("with no audience, every sign-in is refused, never accepted");
        settings.ActiveKeyId.Should().Be(StartupChecks.DevelopmentKeyId);
        settings.SigningKeys[StartupChecks.DevelopmentKeyId].Should().Equal(DevelopmentKey);
    }

    [Theory]
    [InlineData("c2hvcnQ=")]
    [InlineData("not base64!")]
    public void SelectAuth_UnusableKey_ThrowsEvenOutsideProduction(string key)
    {
        // Arrange: a key that is set but unusable is always a mistake.
        var keys = Keys(("k1", key));

        // Act
        var act = () => StartupChecks.SelectAuth(ClientId, keys, null, isProduction: false, DevelopmentKey);

        // Assert
        act.Should().Throw<InvalidOperationException>().WithMessage("*Auth:SigningKeys:k1*openssl rand -base64 32*");
    }

    [Fact]
    public void SelectAuth_TwoKeysWithoutTheActiveOne_Throws()
    {
        // Arrange
        var keys = Keys(("k1", KeyA), ("k2", KeyB));

        // Act
        var act = () => StartupChecks.SelectAuth(ClientId, keys, null, isProduction: true, DevelopmentKey);

        // Assert
        act.Should().Throw<InvalidOperationException>().WithMessage("*Auth:ActiveSigningKeyId*");
    }

    [Fact]
    public void SelectAuth_TwoKeysWithTheActiveOne_SignsWithItAndKeepsBoth()
    {
        // Arrange: a rotation in progress (ADR 0003 §4).
        var keys = Keys(("k1", KeyA), ("k2", KeyB));

        // Act
        var settings = StartupChecks.SelectAuth(ClientId, keys, "k2", isProduction: true, DevelopmentKey);

        // Assert
        settings.ActiveKeyId.Should().Be("k2");
        settings.SigningKeys.Keys.Should().BeEquivalentTo("k1", "k2");
    }

    [Fact]
    public void SelectAuth_ActiveKeyNotConfigured_Throws()
    {
        // Arrange
        var keys = Keys(("k1", KeyA));

        // Act
        var act = () => StartupChecks.SelectAuth(ClientId, keys, "k2", isProduction: true, DevelopmentKey);

        // Assert
        act.Should().Throw<InvalidOperationException>().WithMessage("*'k2'*");
    }

    [Fact]
    public void SelectAuth_ThreeKeys_Throws()
    {
        // Arrange
        var keys = Keys(("k1", KeyA), ("k2", KeyB), ("k3", KeyA));

        // Act
        var act = () => StartupChecks.SelectAuth(ClientId, keys, "k3", isProduction: true, DevelopmentKey);

        // Assert
        act.Should().Throw<InvalidOperationException>().WithMessage("*at most two*");
    }

    [Fact]
    public async Task Start_ProductionWithoutTheClientId_FailsAtStartUp()
    {
        // Arrange: no database is needed; start-up fails before it is touched.
        await using var api = TestApi.Create("Host=unused", configure: h => h.UseSetting(StartupChecks.GoogleWebClientIdKey, ""));

        // Act
        var act = () => api.CreateClient();

        // Assert
        act.Should().Throw<InvalidOperationException>().WithMessage("*Auth:GoogleWebClientId*");
    }

    [Fact]
    public async Task Start_ProductionWithoutKeys_FailsAtStartUp()
    {
        // Arrange
        await using var api = TestApi.Create("Host=unused", configure: h => h.UseSetting(StartupChecks.SigningKeysSection + ":k1", ""));

        // Act
        var act = () => api.CreateClient();

        // Assert
        act.Should().Throw<InvalidOperationException>().WithMessage("*Auth:SigningKeys*");
    }

    private static List<KeyValuePair<string, string?>> Keys(params (string Id, string Value)[] keys) =>
        [.. keys.Select(k => KeyValuePair.Create(k.Id, (string?)k.Value))];
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test tests/Coo.Api.IntegrationTests`
Expected: build error, `StartupChecks.SelectAuth` and its constants do not exist.

- [ ] **Step 3: Implement the selection**

`src/Backend/Coo.Api/Hosting/AuthSettings.cs`:

```csharp
namespace Coo.Api.Hosting;

/// <summary>The sign-in settings after the start-up check (Spec 010 §4.3).</summary>
internal sealed record AuthSettings(string? GoogleWebClientId, string ActiveKeyId, IReadOnlyDictionary<string, byte[]> SigningKeys);
```

Add to `StartupChecks` (it stays `internal static`; update its summary to "Settings that must exist before the API
starts (Spec 008 §3, Spec 010 §4.3)"):

```csharp
    public const string GoogleWebClientIdKey = "Auth:GoogleWebClientId";
    public const string SigningKeysSection = "Auth:SigningKeys";
    public const string ActiveSigningKeyIdKey = "Auth:ActiveSigningKeyId";
    public const string DevelopmentKeyId = "development";
    public const int MinimumKeyBytes = 32;

    /// <summary>
    /// The sign-in settings (Spec 010 §4.3, ADR 0003 §5). Production refuses to start without the Google Web client ID or
    /// a signing key, so a missing setting fails here, never by accepting or refusing every sign-in later. A key that is
    /// set but unusable fails in every environment. Elsewhere, missing settings mean no client ID (every sign-in is
    /// refused) and <paramref name="developmentKey"/>, so tokens end with the process.
    /// </summary>
    public static AuthSettings SelectAuth(
        string? googleWebClientId, IEnumerable<KeyValuePair<string, string?>> signingKeys, string? activeKeyId, bool isProduction,
        byte[] developmentKey)
    {
        ArgumentNullException.ThrowIfNull(signingKeys);
        ArgumentNullException.ThrowIfNull(developmentKey);

        var keys = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var (id, value) in signingKeys)
        {
            if (!string.IsNullOrWhiteSpace(value))
                keys[id] = DecodeKey(id, value);
        }

        if (keys.Count > 2)
            throw new InvalidOperationException($"{SigningKeysSection} has {keys.Count} keys; at most two are allowed (one signing, one being retired).");

        var clientId = string.IsNullOrWhiteSpace(googleWebClientId) ? null : googleWebClientId.Trim();
        if (isProduction && clientId is null)
        {
            throw new InvalidOperationException(
                $"{GoogleWebClientIdKey} is not set. In production it comes from /opt/coo/.env (AUTH_GOOGLE_WEB_CLIENT_ID) "
                + "through deploy/docker-compose.prod.yml (DEPLOY.md, \"Sign-in settings\").");
        }

        if (isProduction && keys.Count == 0)
        {
            throw new InvalidOperationException(
                $"{SigningKeysSection} has no key. In production it comes from /opt/coo/.env (AUTH_SIGNING_KEY_K1) "
                + "through deploy/docker-compose.prod.yml (DEPLOY.md, \"Sign-in settings\").");
        }

        if (keys.Count == 0)
            return new AuthSettings(clientId, DevelopmentKeyId, new Dictionary<string, byte[]>(StringComparer.Ordinal) { [DevelopmentKeyId] = developmentKey });

        string active;
        if (!string.IsNullOrWhiteSpace(activeKeyId))
            active = activeKeyId.Trim();
        else if (keys.Count == 1)
            active = keys.Keys.Single();
        else
            throw new InvalidOperationException($"{SigningKeysSection} has two keys: set {ActiveSigningKeyIdKey} to the one that signs.");

        return keys.ContainsKey(active)
            ? new AuthSettings(clientId, active, keys)
            : throw new InvalidOperationException($"{ActiveSigningKeyIdKey} is '{active}', which is not one of the keys in {SigningKeysSection}.");
    }

    private static byte[] DecodeKey(string id, string value)
    {
        byte[] key;
        try
        {
            key = Convert.FromBase64String(value.Trim());
        }
        catch (FormatException)
        {
            throw new InvalidOperationException($"{SigningKeysSection}:{id} is not base64. Generate a key with: openssl rand -base64 32");
        }

        return key.Length >= MinimumKeyBytes
            ? key
            : throw new InvalidOperationException(
                $"{SigningKeysSection}:{id} has {key.Length} bytes; at least {MinimumKeyBytes} are required. Generate a key with: openssl rand -base64 32");
    }
```

- [ ] **Step 4: Wire it**

`Coo.Api.csproj`: `<Version>0.10.0</Version>`, and

```xml
  <ItemGroup>
    <PackageReference Include="Microsoft.AspNetCore.Authentication.JwtBearer" />
  </ItemGroup>
```

`Program.cs`: add `using System.Security.Cryptography;` and `using Microsoft.AspNetCore.Authentication.JwtBearer;`;
after `AddApiInfrastructure(...)`:

```csharp
// Sign-in (Spec 010 §4.3): Production refuses to start without the Google Web client ID and a signing key.
var auth = StartupChecks.SelectAuth(
    builder.Configuration[StartupChecks.GoogleWebClientIdKey],
    builder.Configuration.GetSection(StartupChecks.SigningKeysSection).GetChildren().Select(c => KeyValuePair.Create(c.Key, c.Value)),
    builder.Configuration[StartupChecks.ActiveSigningKeyIdKey],
    builder.Environment.IsProduction(),
    RandomNumberGenerator.GetBytes(StartupChecks.MinimumKeyBytes));
builder.Services.AddApiIdentity(auth.GoogleWebClientId, auth.ActiveKeyId, auth.SigningKeys);
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
builder.Services.AddAuthorization();
```

and after `app.UseForwardedHeaders(forwarded);`:

```csharp
app.UseAuthentication();
app.UseAuthorization();
```

Update the version comment above `var version` to `"0.10.0+<commit>"`.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/Coo.Api.IntegrationTests`
Expected: PASS (14 start-up tests and the 4 existing API tests, now on 0.10.0).

- [ ] **Step 6: Commit**

```bash
git add src/Backend/Coo.Api tests/Coo.Api.IntegrationTests
git commit -m "API: sign-in settings checked at start-up; Production refuses to start without them; 0.10.0 (Spec 010 §4.3)"
```

---

### Task 8: The endpoints, the rate limit and the daily nonce clean-up

**Files:**
- Create: `src/Shared/Coo.Contracts/Auth/AuthContracts.cs`
- Create: `src/Backend/Coo.Api/Hosting/AuthEndpoints.cs`
- Create: `src/Backend/Coo.Api/Hosting/AuthNonceCleanupJob.cs`
- Modify: `src/Backend/Coo.Api/Program.cs`
- Test: `tests/Coo.Api.IntegrationTests/AuthApiTests.cs`

**Interfaces:**
- Consumes: Tasks 2–3's use cases; Task 7's `TestApi`.
- Produces:
  - `Coo.Contracts.Auth` (public records, camelCase on the wire): `NonceResponse(string Nonce)`,
    `GoogleSignInRequest(string IdToken, string DeviceName, Guid DeviceId, Guid ProvisionalHouseholdId)`,
    `UserDto(Guid Id, string Email, string DisplayName)`,
    `SessionResponse(string AccessToken, DateTimeOffset AccessTokenExpiresAt, string RefreshToken, UserDto User, Guid HouseholdId, string Outcome)`,
    `RefreshRequest(string RefreshToken)`, `TokensResponse(string AccessToken, DateTimeOffset AccessTokenExpiresAt, string RefreshToken)`,
    `MembershipDto(Guid HouseholdId, string Role)`, `DeviceSessionDto(Guid Id, string DeviceName, DateTimeOffset LastUsedAt, bool IsCurrent)`,
    `MeResponse(UserDto User, MembershipDto? Household, IReadOnlyList<DeviceSessionDto> Devices)`,
    `DeleteAccountRequest(bool Confirm)`, `ErrorResponse(string Code)`; `AuthOutcomes.Adopted|Created|Existing|Joined`
    (`"adopted"`, …) and `AuthRoles.Owner|Parent|Caregiver` (`"owner"`, …).
  - Endpoints: `POST /api/auth/nonce`, `POST /api/auth/google`, `POST /api/auth/refresh`, `POST /api/auth/sign-out`
    (bearer), `GET /api/me` (bearer), `DELETE /api/account` (bearer). `/api/auth/*` is rate-limited per client address.
  - `AuthEndpoints.DefaultPermitsPerMinute = 20`, configurable as `Auth:PermitsPerMinute` (tests only).

- [ ] **Step 1: The contracts**

`src/Shared/Coo.Contracts/Auth/AuthContracts.cs`:

```csharp
namespace Coo.Contracts.Auth;

// Wire DTOs for sign-in and sessions (Spec 010 §3, §4.1). Versioned by adding, never by renaming (ADR 0001 §1).

public sealed record NonceResponse(string Nonce);

public sealed record GoogleSignInRequest(string IdToken, string DeviceName, Guid DeviceId, Guid ProvisionalHouseholdId);

public sealed record UserDto(Guid Id, string Email, string DisplayName);

/// <summary><see cref="Outcome"/> is one of <see cref="AuthOutcomes"/>.</summary>
public sealed record SessionResponse(
    string AccessToken, DateTimeOffset AccessTokenExpiresAt, string RefreshToken, UserDto User, Guid HouseholdId, string Outcome);

public sealed record RefreshRequest(string RefreshToken);

public sealed record TokensResponse(string AccessToken, DateTimeOffset AccessTokenExpiresAt, string RefreshToken);

/// <summary><see cref="Role"/> is one of <see cref="AuthRoles"/>.</summary>
public sealed record MembershipDto(Guid HouseholdId, string Role);

public sealed record DeviceSessionDto(Guid Id, string DeviceName, DateTimeOffset LastUsedAt, bool IsCurrent);

public sealed record MeResponse(UserDto User, MembershipDto? Household, IReadOnlyList<DeviceSessionDto> Devices);

public sealed record DeleteAccountRequest(bool Confirm);

/// <summary>Every refusal: a code, never server text (ADR 0006).</summary>
public sealed record ErrorResponse(string Code);

public static class AuthOutcomes
{
    public const string Adopted = "adopted";
    public const string Created = "created";
    public const string Existing = "existing";
    public const string Joined = "joined";
}

public static class AuthRoles
{
    public const string Owner = "owner";
    public const string Parent = "parent";
    public const string Caregiver = "caregiver";
}
```

- [ ] **Step 2: Write the failing end-to-end tests**

`tests/Coo.Api.IntegrationTests/AuthApiTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Coo.Api.Application.Auth;
using Coo.Contracts.Auth;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Coo.Api.IntegrationTests;

/// <summary>A Google that verifies "subject|email|nonce" and nothing else. The real one cannot run in a test.</summary>
internal sealed class FakeGoogle : IExternalIdentityVerifier
{
    public static string Token(string subject, string nonce) => $"{subject}|{subject}@example.com|{nonce}";

    public Task<ExternalIdentity?> VerifyAsync(string idToken, CancellationToken cancellationToken)
    {
        var parts = idToken.Split('|');
        return Task.FromResult(parts.Length == 3 ? new ExternalIdentity(parts[0], parts[1], "Parent " + parts[0], parts[2]) : null);
    }
}

/// <summary>One PostgreSQL and one API for a test class; every test uses its own subjects, phones and households.</summary>
public sealed class AuthApi : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17").Build();

    public WebApplicationFactory<Program> Factory { get; private set; } = null!;

    public string ConnectionString => _postgres.GetConnectionString();

    internal FakeGoogle Google { get; } = new();

    public async Task InitializeAsync()
    {
        try
        {
            await _postgres.StartAsync();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            throw new InvalidOperationException(
                "The PostgreSQL tests need a running Docker: start Docker Desktop (Spec 008 §9). CI has Docker.", e);
        }

        Factory = TestApi.Create(ConnectionString, Google);
    }

    public async Task DisposeAsync()
    {
        if (Factory is not null)
            await Factory.DisposeAsync();
        await _postgres.DisposeAsync();
    }
}

internal static class Calls
{
    public static readonly Uri Nonce = new("/api/auth/nonce", UriKind.Relative);
    public static readonly Uri Google = new("/api/auth/google", UriKind.Relative);
    public static readonly Uri Refresh = new("/api/auth/refresh", UriKind.Relative);
    public static readonly Uri SignOut = new("/api/auth/sign-out", UriKind.Relative);
    public static readonly Uri Me = new("/api/me", UriKind.Relative);
    public static readonly Uri Account = new("/api/account", UriKind.Relative);

    /// <summary>What the phone does: a nonce, a Google token carrying it, then <c>/api/auth/google</c>.</summary>
    public static async Task<SessionResponse> SignInAsync(HttpClient client, string subject, Guid device, Guid household)
    {
        var nonce = await (await client.PostAsync(Nonce, null)).Content.ReadFromJsonAsync<NonceResponse>();
        var response = await client.PostAsJsonAsync(Google, new GoogleSignInRequest(FakeGoogle.Token(subject, nonce!.Nonce), "Pixel 10", device, household));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<SessionResponse>())!;
    }

    public static Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, Uri uri, string? accessToken, object? body = null)
    {
        var request = new HttpRequestMessage(method, uri) { Content = body is null ? null : JsonContent.Create(body) };
        if (accessToken is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client.SendAsync(request);
    }

    public static async Task<string?> CodeAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<ErrorResponse>())?.Code;

    public static Guid Id(int n) => Guid.Parse("7a1c0000-0000-7000-8000-" + n.ToString("D12", System.Globalization.CultureInfo.InvariantCulture));
}

/// <summary>Spec 010 §4.1 and §8, end to end with a fake Google.</summary>
public sealed class AuthApiTests(AuthApi api) : IClassFixture<AuthApi>
{
    [Fact]
    public async Task SignIn_NewUser_AdoptsThePhonesHouseholdAndMeShowsTheOwner()
    {
        // Arrange
        using var client = api.Factory.CreateClient();
        var household = Calls.Id(101);

        // Act
        var session = await Calls.SignInAsync(client, "sub-adopt", Calls.Id(1), household);
        var me = await (await Calls.SendAsync(client, HttpMethod.Get, Calls.Me, session.AccessToken)).Content.ReadFromJsonAsync<MeResponse>();

        // Assert
        session.Outcome.Should().Be(AuthOutcomes.Adopted);
        session.HouseholdId.Should().Be(household);
        session.User.Email.Should().Be("sub-adopt@example.com");
        me!.Household.Should().Be(new MembershipDto(household, AuthRoles.Owner));
        me.Devices.Should().ContainSingle().Which.Should().Match<DeviceSessionDto>(d => d.IsCurrent && d.DeviceName == "Pixel 10");
    }

    [Fact]
    public async Task SignIn_SameAccountOnASecondPhone_IsJoinedAndMeShowsOneHouseholdAndTwoDevices()
    {
        // Arrange: deliverable 3.
        using var client = api.Factory.CreateClient();
        var first = await Calls.SignInAsync(client, "sub-join", Calls.Id(2), Calls.Id(102));

        // Act
        var second = await Calls.SignInAsync(client, "sub-join", Calls.Id(3), Calls.Id(103));
        var me = await (await Calls.SendAsync(client, HttpMethod.Get, Calls.Me, second.AccessToken)).Content.ReadFromJsonAsync<MeResponse>();

        // Assert
        second.Outcome.Should().Be(AuthOutcomes.Joined);
        second.HouseholdId.Should().Be(first.HouseholdId);
        me!.Household!.HouseholdId.Should().Be(Calls.Id(102));
        me.Devices.Should().HaveCount(2);
    }

    [Fact]
    public async Task Me_WithoutAToken_Returns401()
    {
        // Arrange
        using var client = api.Factory.CreateClient();

        // Act
        var response = await Calls.SendAsync(client, HttpMethod.Get, Calls.Me, accessToken: null);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Google_TokenGoogleDidNotVerify_Returns401WithTheCode()
    {
        // Arrange
        using var client = api.Factory.CreateClient();

        // Act
        var response = await client.PostAsJsonAsync(Calls.Google, new GoogleSignInRequest("forged", "Pixel 10", Calls.Id(4), Calls.Id(104)));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Calls.CodeAsync(response)).Should().Be(AuthErrors.GoogleTokenInvalid);
    }

    [Fact]
    public async Task Google_EmptyDeviceId_Returns401WithTheCode()
    {
        // Arrange
        using var client = api.Factory.CreateClient();
        var nonce = await (await client.PostAsync(Calls.Nonce, null)).Content.ReadFromJsonAsync<NonceResponse>();

        // Act
        var response = await client.PostAsJsonAsync(Calls.Google,
            new GoogleSignInRequest(FakeGoogle.Token("sub-empty", nonce!.Nonce), "Pixel 10", Guid.Empty, Calls.Id(105)));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Calls.CodeAsync(response)).Should().Be(AuthErrors.GoogleTokenInvalid);
    }

    [Fact]
    public async Task Refresh_ReusedToken_RevokesTheSession()
    {
        // Arrange
        using var client = api.Factory.CreateClient();
        var session = await Calls.SignInAsync(client, "sub-reuse", Calls.Id(6), Calls.Id(106));
        var rotated = await (await client.PostAsJsonAsync(Calls.Refresh, new RefreshRequest(session.RefreshToken)))
            .Content.ReadFromJsonAsync<TokensResponse>();

        // Act
        var reused = await client.PostAsJsonAsync(Calls.Refresh, new RefreshRequest(session.RefreshToken));

        // Assert
        reused.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Calls.CodeAsync(reused)).Should().Be(AuthErrors.RefreshInvalid);
        (await client.PostAsJsonAsync(Calls.Refresh, new RefreshRequest(rotated!.RefreshToken))).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized, "the session is revoked, so the phone's newer token is refused too");
        (await Calls.SendAsync(client, HttpMethod.Get, Calls.Me, rotated.AccessToken)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Me_AfterSignOut_RefusesTheOldAccessToken()
    {
        // Arrange: deliverable 6; the access token itself has 15 minutes left.
        using var client = api.Factory.CreateClient();
        var session = await Calls.SignInAsync(client, "sub-signout", Calls.Id(7), Calls.Id(107));

        // Act
        var signOut = await Calls.SendAsync(client, HttpMethod.Post, Calls.SignOut, session.AccessToken);
        var me = await Calls.SendAsync(client, HttpMethod.Get, Calls.Me, session.AccessToken);

        // Assert
        signOut.StatusCode.Should().Be(HttpStatusCode.NoContent);
        me.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await client.PostAsJsonAsync(Calls.Refresh, new RefreshRequest(session.RefreshToken))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task DeleteAccount_WithoutConfirmation_Returns400WithTheCodeAndKeepsTheAccount()
    {
        // Arrange
        using var client = api.Factory.CreateClient();
        var session = await Calls.SignInAsync(client, "sub-noconfirm", Calls.Id(8), Calls.Id(108));

        // Act
        var response = await Calls.SendAsync(client, HttpMethod.Delete, Calls.Account, session.AccessToken, new DeleteAccountRequest(false));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Calls.CodeAsync(response)).Should().Be(AuthErrors.ConfirmationRequired);
        (await Calls.SendAsync(client, HttpMethod.Get, Calls.Me, session.AccessToken)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task DeleteAccount_Confirmed_ForgetsTheUserAndTheHousehold()
    {
        // Arrange: deliverable 7.
        using var client = api.Factory.CreateClient();
        var session = await Calls.SignInAsync(client, "sub-delete", Calls.Id(9), Calls.Id(109));

        // Act
        var response = await Calls.SendAsync(client, HttpMethod.Delete, Calls.Account, session.AccessToken, new DeleteAccountRequest(true));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await Calls.SendAsync(client, HttpMethod.Get, Calls.Me, session.AccessToken)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await CountAsync($"""SELECT count(*) FROM "AspNetUsers" WHERE "Id" = '{session.User.Id}';""")).Should().Be(0);
        (await CountAsync($"SELECT count(*) FROM households WHERE id = '{Calls.Id(109)}';")).Should().Be(0);
    }

    private async Task<long> CountAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(api.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}

/// <summary>Spec 010 §4.1: 20 requests per minute per client address on <c>/api/auth/*</c>.</summary>
public sealed class RateLimitTests(AuthApi api) : IClassFixture<AuthApi>
{
    [Fact]
    public async Task Nonce_21stRequestInAMinute_Returns429WithTheCode()
    {
        // Arrange: the production default, not the test fixture's raised limit.
        await using var factory = TestApi.Create(api.ConnectionString, api.Google, permitsPerMinute: null);
        using var client = factory.CreateClient();
        for (var i = 0; i < 20; i++)
            (await client.PostAsync(Calls.Nonce, null)).StatusCode.Should().Be(HttpStatusCode.OK);

        // Act
        var response = await client.PostAsync(Calls.Nonce, null);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        (await Calls.CodeAsync(response)).Should().Be(AuthErrors.RateLimited);
    }
}
```

- [ ] **Step 3: Run them to verify they fail**

Run: `dotnet test tests/Coo.Api.IntegrationTests --filter "FullyQualifiedName~AuthApiTests|FullyQualifiedName~RateLimitTests"`
Expected: FAIL: every endpoint answers `404`.

- [ ] **Step 4: The endpoints and the rate limiter**

`src/Backend/Coo.Api/Hosting/AuthEndpoints.cs`:

```csharp
using System.Globalization;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Coo.Api.Application.Auth;
using Coo.Contracts.Auth;
using Coo.Domain.Common;
using Coo.Domain.Households;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Coo.Api.Hosting;

/// <summary>
/// Sign-in and sessions (Spec 010 §4.1). Contracts are mapped to the use cases here; refusals are codes, never server
/// text. <c>/api/auth/*</c> is rate-limited per client address (forwarded headers are trusted, Spec 008).
/// </summary>
internal static class AuthEndpoints
{
    public const string RateLimitPolicy = "auth";
    public const int DefaultPermitsPerMinute = 20;

    public static IServiceCollection AddAuthRateLimiting(this IServiceCollection services, int permitsPerMinute) =>
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = (context, cancellationToken) =>
                new ValueTask(context.HttpContext.Response.WriteAsJsonAsync(new ErrorResponse(AuthErrors.RateLimited), cancellationToken));
            options.AddPolicy(RateLimitPolicy, http => RateLimitPartition.GetFixedWindowLimiter(
                http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = permitsPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });

    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/api/auth").RequireRateLimiting(RateLimitPolicy);

        auth.MapPost("/nonce", async (IssueNonce issue, CancellationToken cancellationToken) =>
            Results.Ok(new NonceResponse(await issue.ExecuteAsync(cancellationToken))));

        auth.MapPost("/google", async Task<IResult> (GoogleSignInRequest request, SignInWithGoogle signIn, CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.IdToken) || request.DeviceId == Guid.Empty || request.ProvisionalHouseholdId == Guid.Empty)
                return Refused(AuthErrors.GoogleTokenInvalid, StatusCodes.Status401Unauthorized);

            var result = await signIn.ExecuteAsync(
                new GoogleSignInCommand(request.IdToken, request.DeviceName ?? "", new DeviceId(request.DeviceId), new HouseholdId(request.ProvisionalHouseholdId)),
                cancellationToken);
            if (result.IsFailure)
                return Refused(result.Error!.Code, StatusCodes.Status401Unauthorized);

            var s = result.Value;
            return Results.Ok(new SessionResponse(
                s.Session.AccessToken.Value, s.Session.AccessToken.ExpiresAt, s.Session.RefreshToken, User(s.User), s.Household.Value, Outcome(s.Outcome)));
        });

        auth.MapPost("/refresh", async Task<IResult> (RefreshRequest request, RefreshSession refresh, CancellationToken cancellationToken) =>
        {
            var result = await refresh.ExecuteAsync(request.RefreshToken ?? "", cancellationToken);
            return result.IsFailure
                ? Refused(result.Error!.Code, StatusCodes.Status401Unauthorized)
                : Results.Ok(new TokensResponse(result.Value.AccessToken.Value, result.Value.AccessToken.ExpiresAt, result.Value.RefreshToken));
        });

        auth.MapPost("/sign-out", async Task<IResult> (ClaimsPrincipal principal, SignOutSession signOut, CancellationToken cancellationToken) =>
        {
            await signOut.ExecuteAsync(SessionOf(principal), cancellationToken);
            return Results.NoContent();
        }).RequireAuthorization();

        app.MapGet("/api/me", async Task<IResult> (ClaimsPrincipal principal, GetMe getMe, CancellationToken cancellationToken) =>
        {
            var current = SessionOf(principal);
            var me = await getMe.ExecuteAsync(UserOf(principal), cancellationToken);
            if (me is null)
                return Results.Unauthorized();
            return Results.Ok(new MeResponse(
                User(me.User),
                me.Membership is null ? null : new MembershipDto(me.Membership.HouseholdId.Value, RoleName(me.Membership.Role)),
                [.. me.Devices.Select(d => new DeviceSessionDto(d.Id.Value, d.DeviceName, d.LastUsedAt, d.Id == current))]));
        }).RequireAuthorization();

        app.MapDelete("/api/account", async Task<IResult> (
            [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] DeleteAccountRequest? request, ClaimsPrincipal principal,
            DeleteAccount deleteAccount, CancellationToken cancellationToken) =>
        {
            var result = await deleteAccount.ExecuteAsync(UserOf(principal), request?.Confirm == true, cancellationToken);
            return result.IsFailure ? Refused(result.Error!.Code, StatusCodes.Status400BadRequest) : Results.NoContent();
        }).RequireAuthorization();
    }

    private static IResult Refused(string code, int status) => Results.Json(new ErrorResponse(code), statusCode: status);

    private static UserDto User(UserAccount user) => new(user.Id.Value, user.Email, user.DisplayName);

    // The bearer handler has validated the token and its session (JwtBearerSetup), so both claims are present.
    private static UserId UserOf(ClaimsPrincipal principal) =>
        new(Guid.Parse(principal.FindFirst(AccessTokenClaims.User)!.Value, CultureInfo.InvariantCulture));

    private static DeviceSessionId SessionOf(ClaimsPrincipal principal) =>
        new(Guid.Parse(principal.FindFirst(AccessTokenClaims.Session)!.Value, CultureInfo.InvariantCulture));

    private static string Outcome(SignInOutcome outcome) => outcome switch
    {
        SignInOutcome.Adopted => AuthOutcomes.Adopted,
        SignInOutcome.Created => AuthOutcomes.Created,
        SignInOutcome.Existing => AuthOutcomes.Existing,
        SignInOutcome.Joined => AuthOutcomes.Joined,
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Unknown outcome."),
    };

    private static string RoleName(Role role) => role switch
    {
        Role.Owner => AuthRoles.Owner,
        Role.Parent => AuthRoles.Parent,
        Role.Caregiver => AuthRoles.Caregiver,
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown role."),
    };
}
```

`src/Backend/Coo.Api/Hosting/AuthNonceCleanupJob.cs`:

```csharp
using Coo.Api.Application.Auth;

namespace Coo.Api.Hosting;

/// <summary>The daily clean-up of nonce rows older than a day (Spec 010 §4.2). The first run is 15 minutes after start-up.</summary>
internal sealed partial class AuthNonceCleanupJob(PurgeAuthNonces purge, TimeProvider time, ILogger<AuthNonceCleanupJob> logger) : BackgroundService
{
    private static readonly TimeSpan FirstRun = TimeSpan.FromMinutes(15);
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
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    // One failed clean-up must not stop the next one, or the API.
                    LogFailed(logger, e);
                }

                await Task.Delay(Interval, time, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Auth nonces: {Count} old rows deleted")]
    private static partial void LogPurged(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Auth nonce clean-up failed")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
```

- [ ] **Step 5: Wire them**

In `Program.cs` (add `using Coo.Api.Application.Auth;`), after `builder.Services.AddAuthorization();`:

```csharp
builder.Services.AddAuthRateLimiting(builder.Configuration.GetValue("Auth:PermitsPerMinute", AuthEndpoints.DefaultPermitsPerMinute));
builder.Services.AddSingleton<IssueNonce>();
builder.Services.AddSingleton<RefreshSession>();
builder.Services.AddSingleton<SignOutSession>();
builder.Services.AddSingleton<PurgeAuthNonces>();

// Scoped: they use IAccountStore, which shares the request's database context with Identity.
builder.Services.AddScoped<SignInWithGoogle>();
builder.Services.AddScoped<GetMe>();
builder.Services.AddScoped<DeleteAccount>();
```

inside `if (!verifyOnly)` (make it a block):

```csharp
{
    builder.Services.AddHostedService<IntegrityScanJob>();
    builder.Services.AddHostedService<AuthNonceCleanupJob>();
}
```

after `app.UseForwardedHeaders(forwarded);` and **before** `app.UseAuthentication();` (the limiter partitions on the
forwarded address):

```csharp
app.UseRateLimiter();
```

and after the `/health` mapping:

```csharp
app.MapAuthEndpoints();
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test Coo.Tests.slnf`
Expected: PASS everywhere (10 new end-to-end tests). Then `dotnet format Coo.Tests.slnf --verify-no-changes`: no changes.

- [ ] **Step 7: Check it by hand against a local PostgreSQL** (optional but quick, Docker running)

```sh
docker run -d --name coo-pg-local -e POSTGRES_PASSWORD=local -p 5433:5432 postgres:17
ConnectionStrings__Coo="Host=localhost;Port=5433;Database=postgres;Username=postgres;Password=local" \
  dotnet run --project src/Backend/Coo.Api --urls http://localhost:5310 &
sleep 8
curl -s -X POST localhost:5310/api/auth/nonce                         # {"nonce":"…43 characters…"}
curl -s -o /dev/null -w "%{http_code}\n" localhost:5310/api/me        # 401
curl -s -X POST localhost:5310/api/auth/google -H 'Content-Type: application/json' \
  -d '{"idToken":"x","deviceName":"Mac","deviceId":"7a1c0000-0000-7000-8000-000000000001","provisionalHouseholdId":"7a1c0000-0000-7000-8000-000000000002"}'
                                                                      # {"code":"auth.google-token-invalid"}
kill %1; docker rm -f coo-pg-local
```

- [ ] **Step 8: Commit**

```bash
git add src/Shared/Coo.Contracts/Auth src/Backend/Coo.Api tests/Coo.Api.IntegrationTests
git commit -m "API: /api/auth nonce, google, refresh, sign-out; /api/me; DELETE /api/account; rate limit; nonce clean-up (Spec 010 §4.1)"
```

---

### Task 9: Production settings, and the deploy (owner)

**Files:**
- Modify: `deploy/docker-compose.prod.yml`, `deploy/.env.example`, `deploy/deploy-prod.sh`, `DEPLOY.md`

**Interfaces:**
- Consumes: Task 7's settings names (`Auth:GoogleWebClientId`, `Auth:SigningKeys:<kid>`, `Auth:ActiveSigningKeyId`).
- Produces: the server's `.env` gains `AUTH_GOOGLE_WEB_CLIENT_ID` and `AUTH_SIGNING_KEY_K1` (written by `deploy-prod.sh`
  on the server, the key generated there and never printed); `coo-api` receives them as `Auth__GoogleWebClientId` and
  `Auth__SigningKeys__k1`. Production 0.10.0 serves the new endpoints.

- [ ] **Step 1: The compose file**

In `deploy/docker-compose.prod.yml`, under `coo-api.environment`, after `ConnectionStrings__Coo`:

```yaml
      # Sign-in (Spec 010 §4.3). The API refuses to start without them; compose refuses to start without the .env lines.
      Auth__GoogleWebClientId: ${AUTH_GOOGLE_WEB_CLIENT_ID:?set AUTH_GOOGLE_WEB_CLIENT_ID in .env}
      Auth__SigningKeys__k1: ${AUTH_SIGNING_KEY_K1:?set AUTH_SIGNING_KEY_K1 in .env}
```

`coo-api` reaches Google's key endpoint through `coldstart_default`, which is not `internal`; nothing else changes.

- [ ] **Step 2: The `.env` template**

Append to `deploy/.env.example`:

```sh
# Sign-in (Spec 010 §4.3). deploy-prod.sh adds both when they are missing.
# The Google Cloud "Web application" client of project coo-app-510411 (docs/google-sign-in-setup.md §6). Not a secret.
AUTH_GOOGLE_WEB_CLIENT_ID=
# The access-token signing key, 32 random bytes in base64: openssl rand -base64 32. Generated on the server, never printed.
AUTH_SIGNING_KEY_K1=
```

- [ ] **Step 3: The deploy script adds them on the server**

In `deploy/deploy-prod.sh`, after `REMOTE_DIR=…`:

```sh
GOOGLE_WEB_CLIENT_ID="${COO_GOOGLE_WEB_CLIENT_ID:-282211746968-l54aogbkec1gf1g0rp0n7bk2mhbg2c7r.apps.googleusercontent.com}"
```

and replace the "First deploy only" `ssh` block with:

```sh
# ---- The folders and the secrets ----------------------------------------------------------------
# Generated on the server and written straight into .env (mode 600); never printed. Existing values are kept.
echo "==> Preparing $HOST:$REMOTE_DIR"
ssh "$HOST" "set -e; mkdir -p $REMOTE_DIR/deploy $REMOTE_DIR/backups $REMOTE_DIR/eng
  umask 077
  if [ ! -f $REMOTE_DIR/.env ]; then
    printf 'POSTGRES_PASSWORD=%s\n' \"\$(openssl rand -hex 32)\" > $REMOTE_DIR/.env
    echo '    Created .env with a new database password.'
  fi
  if ! grep -q '^AUTH_GOOGLE_WEB_CLIENT_ID=.' $REMOTE_DIR/.env; then
    sed -i '/^AUTH_GOOGLE_WEB_CLIENT_ID=/d' $REMOTE_DIR/.env
    printf 'AUTH_GOOGLE_WEB_CLIENT_ID=%s\n' '$GOOGLE_WEB_CLIENT_ID' >> $REMOTE_DIR/.env
    echo '    Added the Google Web client ID to .env.'
  fi
  if ! grep -q '^AUTH_SIGNING_KEY_K1=.' $REMOTE_DIR/.env; then
    sed -i '/^AUTH_SIGNING_KEY_K1=/d' $REMOTE_DIR/.env
    printf 'AUTH_SIGNING_KEY_K1=%s\n' \"\$(openssl rand -base64 32)\" >> $REMOTE_DIR/.env
    echo '    Added a new access-token signing key to .env.'
  fi"
```

(`$GOOGLE_WEB_CLIENT_ID` expands on the Mac, inside single quotes on the server; `\$(openssl …)` runs on the server.)

- [ ] **Step 4: Check the files without touching the server**

```sh
bash -n deploy/deploy-prod.sh && echo "script ok"
printf 'POSTGRES_PASSWORD=x\nAUTH_GOOGLE_WEB_CLIENT_ID=y\nAUTH_SIGNING_KEY_K1=z\n' > /tmp/coo-env-check
docker compose --env-file /tmp/coo-env-check -f deploy/docker-compose.prod.yml config | grep -E 'Auth__(GoogleWebClientId|SigningKeys__k1)'
printf 'POSTGRES_PASSWORD=x\n' > /tmp/coo-env-check
docker compose --env-file /tmp/coo-env-check -f deploy/docker-compose.prod.yml config 2>&1 | grep -c 'set AUTH_'
rm /tmp/coo-env-check
```

Expected: `script ok`; the two `Auth__…` lines with `y` and `z`; then `1` or more (compose refuses without the lines).

- [ ] **Step 5: `DEPLOY.md`**

Add a section after "Every deploy":

```markdown
## Sign-in settings (Spec 010 §4.3)

`coo-api` needs two values from `/opt/coo/.env`, and **refuses to start without them** (as it does without the
database password):

| `.env` | Becomes | What |
|---|---|---|
| `AUTH_GOOGLE_WEB_CLIENT_ID` | `Auth__GoogleWebClientId` | The Web client of Google Cloud project `coo-app-510411` (`docs/google-sign-in-setup.md` §6): the `aud` every Google ID token must carry. Not a secret. |
| `AUTH_SIGNING_KEY_K1` | `Auth__SigningKeys__k1` | The HMAC key that signs access tokens: 32 random bytes, base64. |

`deploy-prod.sh` adds both when they are missing: the client ID from the script, the key with `openssl rand -base64 32`
**on the server**, never printed. Check that they are there (names only):

    ssh coldstart-prod "grep -o '^AUTH_[A-Z0-9_]*=' /opt/coo/.env"

**Rotating the signing key** without signing anyone out (ADR 0003 §4): add `AUTH_SIGNING_KEY_K2` to `.env` (generated on
the server as above), add `Auth__SigningKeys__k2: ${AUTH_SIGNING_KEY_K2}` and `Auth__ActiveSigningKeyId: k2` to the
compose file, and deploy. After at least 15 minutes (the access-token lifetime), remove `k1` from both and deploy again.
Refresh tokens are not signed, so they survive any rotation.

**If every sign-in fails with `auth.google-token-invalid`:** the API logs `Google ID token refused (<reason>)`. An
`HttpRequestException` means the container cannot reach `www.googleapis.com` (fail closed, ADR 0003 §5); an
`InvalidJwtException` naming the audience means the client ID in `.env` is not the one the app was built with.
```

- [ ] **Step 6: Commit**

```bash
git add deploy DEPLOY.md
git commit -m "Deploy: sign-in settings in the server's .env, added by deploy-prod.sh; DEPLOY.md (Spec 010 §4.3)"
```

- [ ] **Step 7: The owner deploys** (the agent may not deploy or SSH-write to `coldstart-prod`)

Ask the owner to run, in this session, with Docker Desktop running:

```text
! ./deploy/deploy-prod.sh --confirm
! ssh coldstart-prod "grep -o '^AUTH_[A-Z0-9_]*=' /opt/coo/.env"
```

Expected: the script prints "Added the Google Web client ID to .env.", "Added a new access-token signing key to .env."
and ends with `Healthy: {"status":"ok","version":"0.10.0+<commit>","database":"ok"}`; the `grep` prints
`AUTH_GOOGLE_WEB_CLIENT_ID=` and `AUTH_SIGNING_KEY_K1=`.

- [ ] **Step 8: Smoke-test production** (read-only; the agent may run these)

```sh
curl -s https://api.coo.lechdigital.nl/health
curl -s -X POST https://api.coo.lechdigital.nl/api/auth/nonce
curl -s -o /dev/null -w "%{http_code}\n" https://api.coo.lechdigital.nl/api/me
curl -s -X POST https://api.coo.lechdigital.nl/api/auth/google -H 'Content-Type: application/json' \
  -d '{"idToken":"x","deviceName":"curl","deviceId":"7a1c0000-0000-7000-8000-000000000001","provisionalHouseholdId":"7a1c0000-0000-7000-8000-000000000002"}'
```

Expected: `"version":"0.10.0+…"`; `{"nonce":"…"}`; `401`; `{"code":"auth.google-token-invalid"}`.

---
### Task 10: The phone's account state and use cases (`Coo.Mobile.Application/Auth`)

**Files:**
- Create: `src/Mobile/Coo.Mobile.Application/Auth/Ports.cs`
- Create: `src/Mobile/Coo.Mobile.Application/Auth/Account.cs`
- Create: `src/Mobile/Coo.Mobile.Application/Auth/UseCases.cs`
- Modify: `src/Mobile/Coo.Mobile.Application/ServiceCollectionExtensions.cs`
- Test: `tests/Coo.Mobile.Application.Tests/AuthUseCaseTests.cs`

**Interfaces:**
- Consumes: `IHouseholdStore` (`Households/Households.cs`), `IDeviceIdentity` (`CareLog/Ports.cs`), `HouseholdId`,
  `UserId`, `DeviceId`.
- Produces (namespace `Coo.Mobile.Application.Auth`, all `public`):
  - `record AccountProfile(UserId UserId, string Name, string Email)`;
    `record StoredSession(string AccessToken, DateTimeOffset AccessTokenExpiresAt, string RefreshToken, AccountProfile Profile, HouseholdId AccountHousehold, bool JoinPending)`.
  - `interface ISessionStore { Task<StoredSession?> GetAsync(CancellationToken); Task SaveAsync(StoredSession, CancellationToken); Task ClearAsync(CancellationToken); }`
  - `interface ISecretStore { Task<string?> GetAsync(string key, CancellationToken); Task SetAsync(string key, string value, CancellationToken); Task RemoveAsync(string key, CancellationToken); }`
  - `interface ISignInProvider { Task<string?> GetIdTokenAsync(string nonce, CancellationToken); }` (null: cancelled;
    throws `SignInUnavailableException`); `interface IDeviceName { string Name { get; } }`.
  - `enum SignInOutcome { Adopted, Created, Existing, Joined }`;
    `record SessionTokens(string AccessToken, DateTimeOffset AccessTokenExpiresAt, string RefreshToken)`;
    `record GoogleSignIn(string IdToken, string DeviceName, DeviceId DeviceId, HouseholdId ProvisionalHouseholdId)`;
    `record SignInGrant(SessionTokens Tokens, AccountProfile Profile, HouseholdId Household, SignInOutcome Outcome)`;
    `interface IAuthApi { Task<string> GetNonceAsync(CancellationToken); Task<SignInGrant> SignInWithGoogleAsync(GoogleSignIn, CancellationToken); Task<SessionTokens> RefreshAsync(string refreshToken, CancellationToken); }`
  - `record DeviceSessionInfo(string Name, DateTimeOffset LastUsedAt, bool IsThisDevice)`;
    `record AccountOverview(AccountProfile Profile, HouseholdId? Household, string? Role, IReadOnlyList<DeviceSessionInfo> Devices)`;
    `interface IAccountApi { Task SignOutAsync(CancellationToken); Task<AccountOverview> GetMeAsync(CancellationToken); Task DeleteAccountAsync(CancellationToken); }`
  - Exceptions: `AuthUnavailableException` (network, timeout, `429`, `5xx`: try later), `AuthRejectedException` with
    `string Code` (the server refused), `SignInUnavailableException` (Google's sheet failed).
  - `abstract record AccountState`; `SignedOut(bool SessionExpired)`, `SignedIn(AccountProfile Profile)`,
    `JoinPending(AccountProfile Profile, HouseholdId AccountHousehold)`.
  - `sealed class Account(ISessionStore sessions)` with `AccountState State`, `event EventHandler? Changed`,
    `Task<AccountState> LoadAsync(CancellationToken)`, `Task SignedInAsync(StoredSession, CancellationToken)`,
    `Task SignedOutAsync(bool sessionExpired, CancellationToken)`, `static AccountState From(StoredSession?)`.
  - `enum SignInResult { SignedIn, Cancelled, Failed }`;
    `sealed class SignIn(IAuthApi api, ISignInProvider google, IHouseholdStore households, IDeviceIdentity device, IDeviceName deviceName, Account account)` with `Task<SignInResult> ExecuteAsync(CancellationToken)`;
    `sealed class SignOut(IAccountApi api, Account account)` with `Task ExecuteAsync(CancellationToken)`;
    `sealed class DeleteAccount(IAccountApi api, Account account)` with `Task<bool> ExecuteAsync(CancellationToken)`;
    `sealed class RefreshAccount(IAccountApi api, ISessionStore sessions, Account account)` with `Task ExecuteAsync(CancellationToken)`.
  - `AddMobileApplication` registers `Account` (singleton) and the four use cases (transient).

- [ ] **Step 1: Write the failing tests**

`tests/Coo.Mobile.Application.Tests/AuthUseCaseTests.cs`:

```csharp
using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;
using Coo.Domain.Households;
using Coo.Mobile.Application.Auth;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Application.Households;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Coo.Mobile.Application.Tests;

/// <summary>The session store, in memory: an outbound port.</summary>
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

/// <summary>Spec 010 §2, §3.2 (what each outcome does on the phone) and §3.3. Only the outbound ports are substitutes.</summary>
public sealed class AuthUseCaseTests
{
    private static readonly DeviceId Device = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000d1"));
    private static readonly HouseholdId Local = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a1"));
    private static readonly HouseholdId AccountsHousehold = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a9"));
    private static readonly AccountProfile Profile = new(new UserId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000c1")), "Andrzej", "andrzej@example.com");
    private static readonly DateTimeOffset Expires = new(2026, 10, 2, 12, 15, 0, TimeSpan.Zero);
    private static readonly SessionTokens Tokens = new("access-1", Expires, "refresh-1");

    private readonly IAuthApi _api = Substitute.For<IAuthApi>();
    private readonly IAccountApi _accountApi = Substitute.For<IAccountApi>();
    private readonly ISignInProvider _google = Substitute.For<ISignInProvider>();
    private readonly IHouseholdStore _households = Substitute.For<IHouseholdStore>();
    private readonly IDeviceIdentity _device = Substitute.For<IDeviceIdentity>();
    private readonly IDeviceName _deviceName = Substitute.For<IDeviceName>();
    private readonly MemorySessions _sessions = new();
    private readonly Account _account;

    public AuthUseCaseTests()
    {
        _account = new Account(_sessions);
        var settings = HouseholdSettings.Create(Local, VolumeUnit.Millilitres, "Europe/Amsterdam", new HlcStamp(1, 0, Device)).Value;
        _households.ListHouseholdsAsync(Arg.Any<CancellationToken>()).Returns([settings]);
        _device.GetAsync(Arg.Any<CancellationToken>()).Returns(Device);
        _deviceName.Name.Returns("Pixel 10");
        _api.GetNonceAsync(Arg.Any<CancellationToken>()).Returns("nonce-1");
        _google.GetIdTokenAsync("nonce-1", Arg.Any<CancellationToken>()).Returns("id-token");
    }

    [Theory]
    [InlineData(SignInOutcome.Adopted)]
    [InlineData(SignInOutcome.Existing)]
    public async Task ExecuteAsync_OutcomeKeepsThePhonesHousehold_SignsInWithoutRekeyingAnything(SignInOutcome outcome)
    {
        // Arrange
        Grants(outcome, Local);

        // Act
        var result = await SignIn().ExecuteAsync(CancellationToken.None);

        // Assert
        result.Should().Be(SignInResult.SignedIn);
        _account.State.Should().Be(new SignedIn(Profile));
        _sessions.Stored!.AccountHousehold.Should().Be(Local);
        await _api.Received(1).SignInWithGoogleAsync(new GoogleSignIn("id-token", "Pixel 10", Device, Local), Arg.Any<CancellationToken>());
        await _households.DidNotReceiveWithAnyArgs().SaveSettingsAsync(default!, default);
    }

    [Fact]
    public async Task ExecuteAsync_Created_SignsInAndRecordsTheNewHousehold()
    {
        // Arrange: Increment 11 moves the local records to the new ID before the first sync.
        Grants(SignInOutcome.Created, AccountsHousehold);

        // Act
        var result = await SignIn().ExecuteAsync(CancellationToken.None);

        // Assert
        result.Should().Be(SignInResult.SignedIn);
        _account.State.Should().Be(new SignedIn(Profile));
        _sessions.Stored!.AccountHousehold.Should().Be(AccountsHousehold);
        _sessions.Stored.JoinPending.Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_Joined_IsJoinPendingAndKeepsThePhonesEntries()
    {
        // Arrange: the same account on a second phone (Q1).
        Grants(SignInOutcome.Joined, AccountsHousehold);

        // Act
        var result = await SignIn().ExecuteAsync(CancellationToken.None);

        // Assert
        result.Should().Be(SignInResult.SignedIn);
        _account.State.Should().Be(new JoinPending(Profile, AccountsHousehold));
        await _households.DidNotReceiveWithAnyArgs().SaveSettingsAsync(default!, default);
    }

    [Fact]
    public async Task ExecuteAsync_SheetCancelled_ChangesNothing()
    {
        // Arrange
        _google.GetIdTokenAsync("nonce-1", Arg.Any<CancellationToken>()).Returns((string?)null);

        // Act
        var result = await SignIn().ExecuteAsync(CancellationToken.None);

        // Assert
        result.Should().Be(SignInResult.Cancelled);
        _account.State.Should().Be(new SignedOut(SessionExpired: false));
        await _api.DidNotReceiveWithAnyArgs().SignInWithGoogleAsync(default!, default);
    }

    [Fact]
    public async Task ExecuteAsync_ServerUnreachable_FailsAndStoresNothing()
    {
        // Arrange
        _api.GetNonceAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new AuthUnavailableException("offline"));

        // Act
        var result = await SignIn().ExecuteAsync(CancellationToken.None);

        // Assert
        result.Should().Be(SignInResult.Failed);
        _sessions.Stored.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteAsync_ServerRefusesTheToken_FailsAndStaysSignedOut()
    {
        // Arrange
        _api.SignInWithGoogleAsync(Arg.Any<GoogleSignIn>(), Arg.Any<CancellationToken>()).ThrowsAsync(new AuthRejectedException("auth.google-token-invalid"));

        // Act
        var result = await SignIn().ExecuteAsync(CancellationToken.None);

        // Assert
        result.Should().Be(SignInResult.Failed);
        _account.State.Should().BeOfType<SignedOut>();
    }

    [Fact]
    public async Task ExecuteAsync_GoogleSheetFails_Fails()
    {
        // Arrange: no Google account on the phone, Play services missing, and so on.
        _google.GetIdTokenAsync("nonce-1", Arg.Any<CancellationToken>()).ThrowsAsync(new SignInUnavailableException("NoCredentialException"));

        // Act
        var result = await SignIn().ExecuteAsync(CancellationToken.None);

        // Assert
        result.Should().Be(SignInResult.Failed);
    }

    [Fact]
    public async Task ExecuteAsync_SignOut_RevokesOnTheServerAndSignsOutHere()
    {
        // Arrange
        await SignedInAsync();

        // Act
        await new SignOut(_accountApi, _account).ExecuteAsync(CancellationToken.None);

        // Assert
        await _accountApi.Received(1).SignOutAsync(Arg.Any<CancellationToken>());
        _account.State.Should().Be(new SignedOut(SessionExpired: false));
        _sessions.Stored.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteAsync_ServerUnreachable_StillSignsOutHere()
    {
        // Arrange: the person asked to sign out; the server's session ends on its own (180 days idle).
        await SignedInAsync();
        _accountApi.SignOutAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new AuthUnavailableException("offline"));

        // Act
        await new SignOut(_accountApi, _account).ExecuteAsync(CancellationToken.None);

        // Assert
        _account.State.Should().Be(new SignedOut(SessionExpired: false));
        _sessions.Stored.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteAsync_DeleteAccountSucceeds_ClearsTheSessionOnly()
    {
        // Arrange: the local log stays (Q2); DeleteAccount has no access to it at all.
        await SignedInAsync();

        // Act
        var deleted = await new DeleteAccount(_accountApi, _account).ExecuteAsync(CancellationToken.None);

        // Assert
        deleted.Should().BeTrue();
        _account.State.Should().Be(new SignedOut(SessionExpired: false));
    }

    [Fact]
    public async Task ExecuteAsync_DeleteAccountOffline_FailsAndStaysSignedIn()
    {
        // Arrange
        await SignedInAsync();
        _accountApi.DeleteAccountAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new AuthUnavailableException("offline"));

        // Act
        var deleted = await new DeleteAccount(_accountApi, _account).ExecuteAsync(CancellationToken.None);

        // Assert
        deleted.Should().BeFalse();
        _account.State.Should().Be(new SignedIn(Profile));
    }

    [Fact]
    public async Task ExecuteAsync_RefreshAccountWithANewName_UpdatesTheProfile()
    {
        // Arrange
        await SignedInAsync();
        var renamed = Profile with { Name = "Andrzej L." };
        _accountApi.GetMeAsync(Arg.Any<CancellationToken>()).Returns(new AccountOverview(renamed, Local, "owner", []));

        // Act
        await new RefreshAccount(_accountApi, _sessions, _account).ExecuteAsync(CancellationToken.None);

        // Assert
        _account.State.Should().Be(new SignedIn(renamed));
        _sessions.Stored!.AccessToken.Should().Be("access-1");
    }

    [Fact]
    public async Task ExecuteAsync_RefreshAccountOffline_KeepsTheAccount()
    {
        // Arrange: airplane mode (deliverable 5).
        await SignedInAsync();
        _accountApi.GetMeAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new AuthUnavailableException("offline"));

        // Act
        await new RefreshAccount(_accountApi, _sessions, _account).ExecuteAsync(CancellationToken.None);

        // Assert
        _account.State.Should().Be(new SignedIn(Profile));
    }

    [Fact]
    public async Task ExecuteAsync_RefreshAccountSignedOut_AsksNothing()
    {
        // Arrange
        var refresh = new RefreshAccount(_accountApi, _sessions, _account);

        // Act
        await refresh.ExecuteAsync(CancellationToken.None);

        // Assert
        await _accountApi.DidNotReceiveWithAnyArgs().GetMeAsync(default);
    }

    [Fact]
    public async Task LoadAsync_StoredJoinedSession_IsJoinPending()
    {
        // Arrange: the app restarted after signing in on the second phone.
        await _sessions.SaveAsync(new StoredSession("a", Expires, "r", Profile, AccountsHousehold, JoinPending: true), CancellationToken.None);

        // Act
        var state = await _account.LoadAsync(CancellationToken.None);

        // Assert
        state.Should().Be(new JoinPending(Profile, AccountsHousehold));
    }

    private SignIn SignIn() => new(_api, _google, _households, _device, _deviceName, _account);

    private void Grants(SignInOutcome outcome, HouseholdId household) =>
        _api.SignInWithGoogleAsync(Arg.Any<GoogleSignIn>(), Arg.Any<CancellationToken>()).Returns(new SignInGrant(Tokens, Profile, household, outcome));

    private Task SignedInAsync() =>
        _account.SignedInAsync(new StoredSession("access-1", Expires, "refresh-1", Profile, Local, JoinPending: false), CancellationToken.None);
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test tests/Coo.Mobile.Application.Tests --filter "FullyQualifiedName~AuthUseCaseTests"`
Expected: build errors, the `Coo.Mobile.Application.Auth` types do not exist.

- [ ] **Step 3: The ports**

`src/Mobile/Coo.Mobile.Application/Auth/Ports.cs`:

```csharp
using Coo.Domain.Common;

namespace Coo.Mobile.Application.Auth;

public sealed record AccountProfile(UserId UserId, string Name, string Email);

/// <summary>
/// What the phone keeps after signing in (Spec 010 §3.3): both tokens, who is signed in, the account's household, and
/// whether this phone's entries are still to join it at the first sync (Spec 010 §3.2, Q1).
/// </summary>
public sealed record StoredSession(
    string AccessToken, DateTimeOffset AccessTokenExpiresAt, string RefreshToken, AccountProfile Profile, HouseholdId AccountHousehold,
    bool JoinPending);

/// <summary>The session on this phone. Unreadable means none: the person signs in again; it never crashes.</summary>
public interface ISessionStore
{
    Task<StoredSession?> GetAsync(CancellationToken cancellationToken);

    Task SaveAsync(StoredSession session, CancellationToken cancellationToken);

    Task ClearAsync(CancellationToken cancellationToken);
}

/// <summary>Encrypted key-value storage backed by the Android Keystore (MAUI <c>SecureStorage</c> in the host).</summary>
public interface ISecretStore
{
    Task<string?> GetAsync(string key, CancellationToken cancellationToken);

    Task SetAsync(string key, string value, CancellationToken cancellationToken);

    Task RemoveAsync(string key, CancellationToken cancellationToken);
}

/// <summary>Android's Google account sheet (Credential Manager, Spec 010a): a Google ID token carrying the nonce.</summary>
public interface ISignInProvider
{
    /// <summary>Null when the person closed the sheet. Throws <see cref="SignInUnavailableException"/> when it failed.</summary>
    Task<string?> GetIdTokenAsync(string nonce, CancellationToken cancellationToken);
}

/// <summary>The phone's model, for example "Pixel 10", shown in the account's device list.</summary>
public interface IDeviceName
{
    string Name { get; }
}

/// <summary>Which case of Spec 010 §3.2 the server reported.</summary>
public enum SignInOutcome
{
    Adopted,
    Created,
    Existing,
    Joined,
}

public sealed record SessionTokens(string AccessToken, DateTimeOffset AccessTokenExpiresAt, string RefreshToken);

public sealed record GoogleSignIn(string IdToken, string DeviceName, DeviceId DeviceId, HouseholdId ProvisionalHouseholdId);

public sealed record SignInGrant(SessionTokens Tokens, AccountProfile Profile, HouseholdId Household, SignInOutcome Outcome);

/// <summary><c>/api/auth</c> calls that need no access token.</summary>
public interface IAuthApi
{
    Task<string> GetNonceAsync(CancellationToken cancellationToken);

    Task<SignInGrant> SignInWithGoogleAsync(GoogleSignIn request, CancellationToken cancellationToken);

    Task<SessionTokens> RefreshAsync(string refreshToken, CancellationToken cancellationToken);
}

public sealed record DeviceSessionInfo(string Name, DateTimeOffset LastUsedAt, bool IsThisDevice);

public sealed record AccountOverview(AccountProfile Profile, HouseholdId? Household, string? Role, IReadOnlyList<DeviceSessionInfo> Devices);

/// <summary>Calls made with the access token, refreshed as needed by the infrastructure.</summary>
public interface IAccountApi
{
    Task SignOutAsync(CancellationToken cancellationToken);

    Task<AccountOverview> GetMeAsync(CancellationToken cancellationToken);

    Task DeleteAccountAsync(CancellationToken cancellationToken);
}

/// <summary>The server could not be asked: no network, a timeout, <c>429</c> or <c>5xx</c>. "Try later", never "signed out".</summary>
public sealed class AuthUnavailableException : Exception
{
    public AuthUnavailableException()
    {
    }

    public AuthUnavailableException(string message)
        : base(message)
    {
    }

    public AuthUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The server answered and refused; <see cref="Code"/> is its error code (Spec 010 §4.1).</summary>
public sealed class AuthRejectedException : Exception
{
    public AuthRejectedException()
        : this("unknown")
    {
    }

    public AuthRejectedException(string message)
        : base(message)
    {
    }

    public AuthRejectedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public string Code => Message;
}

/// <summary>Android's account sheet failed (no Google account, Play services unavailable, …).</summary>
public sealed class SignInUnavailableException : Exception
{
    public SignInUnavailableException()
    {
    }

    public SignInUnavailableException(string message)
        : base(message)
    {
    }

    public SignInUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
```

- [ ] **Step 4: The account state**

`src/Mobile/Coo.Mobile.Application/Auth/Account.cs`:

```csharp
using Coo.Domain.Common;

namespace Coo.Mobile.Application.Auth;

/// <summary>The Account section's states (Spec 010 §6).</summary>
public abstract record AccountState;

/// <param name="SessionExpired">The server refused the session (revoked, reused, idle): "Sign in again to keep sharing."</param>
public sealed record SignedOut(bool SessionExpired) : AccountState;

public sealed record SignedIn(AccountProfile Profile) : AccountState;

/// <summary>The same account on another phone (Spec 010 §3.2, Q1): this phone's entries join at the first sync.</summary>
public sealed record JoinPending(AccountProfile Profile, HouseholdId AccountHousehold) : AccountState;

/// <summary>
/// Who is signed in on this phone, in step with the session store. One per app. Being signed out never blocks logging
/// (ADR 0003 §4), and nothing here touches local care data.
/// </summary>
public sealed class Account(ISessionStore sessions)
{
    public AccountState State { get; private set; } = new SignedOut(SessionExpired: false);

    public event EventHandler? Changed;

    public async Task<AccountState> LoadAsync(CancellationToken cancellationToken)
    {
        Set(From(await sessions.GetAsync(cancellationToken)));
        return State;
    }

    public async Task SignedInAsync(StoredSession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        await sessions.SaveAsync(session, cancellationToken);
        Set(From(session));
    }

    /// <summary>The tokens go; every local record and the outbox stay (ADR 0003 §4).</summary>
    public async Task SignedOutAsync(bool sessionExpired, CancellationToken cancellationToken)
    {
        await sessions.ClearAsync(cancellationToken);
        Set(new SignedOut(sessionExpired));
    }

    public static AccountState From(StoredSession? session) => session switch
    {
        null => new SignedOut(SessionExpired: false),
        { JoinPending: true } => new JoinPending(session.Profile, session.AccountHousehold),
        _ => new SignedIn(session.Profile),
    };

    private void Set(AccountState state)
    {
        if (state == State)
            return;
        State = state;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
```

If `CA1012` fires on `AccountState`, wrap it as in Task 1.

- [ ] **Step 5: The use cases**

`src/Mobile/Coo.Mobile.Application/Auth/UseCases.cs`:

```csharp
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Application.Households;

namespace Coo.Mobile.Application.Auth;

public enum SignInResult
{
    SignedIn,
    Cancelled,
    Failed,
}

/// <summary>
/// The one sign-in (Spec 010 §2, Q3). Settings calls it now; sharing (11b) and Premium (12) call this same use case
/// where the person is, then carry on. A nonce from the server, Google's sheet, then the server's session.
/// </summary>
public sealed class SignIn(
    IAuthApi api, ISignInProvider google, IHouseholdStore households, IDeviceIdentity device, IDeviceName deviceName, Account account)
{
    public async Task<SignInResult> ExecuteAsync(CancellationToken cancellationToken)
    {
        try
        {
            var nonce = await api.GetNonceAsync(cancellationToken);
            var idToken = await google.GetIdTokenAsync(nonce, cancellationToken);
            if (idToken is null)
                return SignInResult.Cancelled;

            var household = (await households.ListHouseholdsAsync(cancellationToken)).First(h => !h.IsDeleted).Id;
            var grant = await api.SignInWithGoogleAsync(
                new GoogleSignIn(idToken, deviceName.Name, await device.GetAsync(cancellationToken), household), cancellationToken);

            // No local record is re-keyed in any outcome (Spec 010 §3.2): "created" and "joined" are resolved by sync (11).
            await account.SignedInAsync(
                new StoredSession(grant.Tokens.AccessToken, grant.Tokens.AccessTokenExpiresAt, grant.Tokens.RefreshToken, grant.Profile,
                    grant.Household, JoinPending: grant.Outcome == SignInOutcome.Joined),
                cancellationToken);
            return SignInResult.SignedIn;
        }
        catch (Exception e) when (e is AuthUnavailableException or AuthRejectedException or SignInUnavailableException)
        {
            return SignInResult.Failed;
        }
    }
}

/// <summary>Sign out on this phone (Spec 010 §2). The server's revoke is best effort; the phone signs out regardless.</summary>
public sealed class SignOut(IAccountApi api, Account account)
{
    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        try
        {
            await api.SignOutAsync(cancellationToken);
        }
        catch (Exception e) when (e is AuthUnavailableException or AuthRejectedException)
        {
            // Offline or already refused: the server's session ends on its own after 180 idle days.
        }

        await account.SignedOutAsync(sessionExpired: false, cancellationToken);
    }
}

/// <summary>Spec 010 §5 on the phone: the server forgets the account; the local log stays (Q2). False when it failed.</summary>
public sealed class DeleteAccount(IAccountApi api, Account account)
{
    public async Task<bool> ExecuteAsync(CancellationToken cancellationToken)
    {
        try
        {
            await api.DeleteAccountAsync(cancellationToken);
        }
        catch (Exception e) when (e is AuthUnavailableException or AuthRejectedException)
        {
            return false;
        }

        await account.SignedOutAsync(sessionExpired: false, cancellationToken);
        return true;
    }
}

/// <summary>
/// Settings opened: the server's name and email replace the stored ones. Offline, nothing changes (deliverable 5); a
/// refused session has already been signed out by the refreshing handler.
/// </summary>
public sealed class RefreshAccount(IAccountApi api, ISessionStore sessions, Account account)
{
    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        if (account.State is SignedOut)
            return;

        AccountOverview me;
        try
        {
            me = await api.GetMeAsync(cancellationToken);
        }
        catch (Exception e) when (e is AuthUnavailableException or AuthRejectedException)
        {
            return;
        }

        var session = await sessions.GetAsync(cancellationToken);
        if (session is not null && session.Profile != me.Profile)
            await account.SignedInAsync(session with { Profile = me.Profile }, cancellationToken);
    }
}
```

- [ ] **Step 6: Register them**

In `AddMobileApplication` (add `using Coo.Mobile.Application.Auth;`), before `return services;`:

```csharp
        // Sign-in and sessions (Spec 010 §6).
        services.AddSingleton<Account>();
        services.AddTransient<SignIn>();
        services.AddTransient<SignOut>();
        services.AddTransient<DeleteAccount>();
        services.AddTransient<RefreshAccount>();
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test tests/Coo.Mobile.Application.Tests && dotnet test tests/Coo.Architecture.Tests`
Expected: PASS (17 new tests); no `System.Text.Json` or MAUI in `Coo.Mobile.Application`.

- [ ] **Step 8: Commit**

```bash
git add src/Mobile/Coo.Mobile.Application tests/Coo.Mobile.Application.Tests/AuthUseCaseTests.cs
git commit -m "Mobile.Application: account state, sign-in, sign-out, delete account, refresh account (Spec 010 §2, §3.2, §6)"
```

---

### Task 11: The HTTP clients, the single-flight refreshing handler, the session codec (`Coo.Mobile.Infrastructure/Auth`)

**Files:**
- Create: `src/Mobile/Coo.Mobile.Infrastructure/Auth/SessionStore.cs`
- Create: `src/Mobile/Coo.Mobile.Infrastructure/Auth/AuthJsonContext.cs`
- Create: `src/Mobile/Coo.Mobile.Infrastructure/Auth/AuthHttp.cs`
- Create: `src/Mobile/Coo.Mobile.Infrastructure/Auth/AuthClient.cs`
- Create: `src/Mobile/Coo.Mobile.Infrastructure/Auth/AccountClient.cs`
- Create: `src/Mobile/Coo.Mobile.Infrastructure/Auth/TokenRefresher.cs`
- Create: `src/Mobile/Coo.Mobile.Infrastructure/Auth/AuthenticatingHandler.cs`
- Modify: `src/Mobile/Coo.Mobile.Infrastructure/ServiceCollectionExtensions.cs`
- Test: `tests/Coo.Mobile.Infrastructure.Tests/AuthFakes.cs`, `SessionStoreTests.cs`, `AuthClientTests.cs`, `AuthenticatingHandlerTests.cs`

**Interfaces:**
- Consumes: Task 10's ports, `Account`, exceptions; Task 8's `Coo.Contracts.Auth` DTOs.
- Produces (all `internal` except the extension):
  - `SessionStore(ISecretStore secrets) : ISessionStore` with `const string Key = "coo.session"`,
    `static string Format(StoredSession)`, `static StoredSession? Parse(string)`.
  - `AuthClient(HttpClient http) : IAuthApi`; `AccountClient(HttpClient http) : IAccountApi`.
  - `TokenRefresher(ISessionStore sessions, IAuthApi api, Account account, TimeProvider time) : IDisposable` with
    `static readonly TimeSpan Margin` (1 min), `Task<string?> GetAccessTokenAsync(CancellationToken)`,
    `Task<string?> RefreshAsync(string staleAccessToken, CancellationToken)`.
  - `AuthenticatingHandler(TokenRefresher refresher) : DelegatingHandler`.
  - `AddMobileInfrastructure(this IServiceCollection services, string databasePath, string appVersion, Uri apiBaseAddress)`
    (new third parameter) registers `ISessionStore`, `IAuthApi`, `TokenRefresher`, `IAccountApi`. The host registers
    `ISecretStore`.

- [ ] **Step 1: Write the fakes and the failing tests**

`tests/Coo.Mobile.Infrastructure.Tests/AuthFakes.cs`:

```csharp
using System.Collections.Concurrent;
using Coo.Mobile.Application.Auth;

namespace Coo.Mobile.Infrastructure.Tests;

/// <summary>The secret store, in memory: an outbound port. Concurrent, because the handler tests call it from ten requests.</summary>
internal sealed class MemorySecrets : ISecretStore
{
    public ConcurrentDictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

    public Task<string?> GetAsync(string key, CancellationToken cancellationToken) =>
        Task.FromResult(Values.TryGetValue(key, out var value) ? value : null);

    public Task SetAsync(string key, string value, CancellationToken cancellationToken)
    {
        Values[key] = value;
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string key, CancellationToken cancellationToken)
    {
        Values.TryRemove(key, out _);
        return Task.CompletedTask;
    }
}

/// <summary>The API, as far as an HttpClient can tell.</summary>
internal sealed class FakeServer(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    public int Calls { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++;
        return respond(request);
    }
}
```

`tests/Coo.Mobile.Infrastructure.Tests/SessionStoreTests.cs`:

```csharp
using Coo.Domain.Common;
using Coo.Mobile.Application.Auth;
using Coo.Mobile.Infrastructure.Auth;

namespace Coo.Mobile.Infrastructure.Tests;

/// <summary>The session as one SecureStorage value (Spec 010 §3.3). Unreadable is "signed out", never a crash.</summary>
public sealed class SessionStoreTests
{
    private static readonly StoredSession Session = new(
        "access-1", new DateTimeOffset(2026, 10, 2, 12, 15, 0, TimeSpan.Zero), "refresh-1",
        new AccountProfile(new UserId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000c1")), "Andrzej", "andrzej@example.com"),
        new HouseholdId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a1")), JoinPending: true);

    private readonly MemorySecrets _secrets = new();

    [Fact]
    public async Task SaveAsyncThenGetAsync_Session_RoundTrips()
    {
        // Arrange
        var store = new SessionStore(_secrets);

        // Act
        await store.SaveAsync(Session, CancellationToken.None);
        var loaded = await store.GetAsync(CancellationToken.None);

        // Assert
        loaded.Should().Be(Session);
    }

    [Fact]
    public async Task SaveAsync_NameWithTheSeparator_IsStoredReadably()
    {
        // Arrange
        var store = new SessionStore(_secrets);
        var odd = Session with { Profile = Session.Profile with { Name = "A\u001fB" } };

        // Act
        await store.SaveAsync(odd, CancellationToken.None);
        var loaded = await store.GetAsync(CancellationToken.None);

        // Assert
        loaded!.Profile.Name.Should().Be("A B");
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("2\u001fa\u001f0\u001fr\u001f7a1c0000-0000-7000-8000-0000000000c1\u001fn\u001fe\u001f7a1c0000-0000-7000-8000-0000000000a1\u001f0")]
    [InlineData("1\u001fa\u001f0\u001fr\u001fnot-a-guid\u001fn\u001fe\u001f7a1c0000-0000-7000-8000-0000000000a1\u001f0")]
    [InlineData("1\u001f\u001f0\u001fr\u001f7a1c0000-0000-7000-8000-0000000000c1\u001fn\u001fe\u001f7a1c0000-0000-7000-8000-0000000000a1\u001f0")]
    public async Task GetAsync_UnreadableValue_ReturnsNull(string stored)
    {
        // Arrange: a value from a future version, a Keystore restored badly, a half-written value.
        _secrets.Values[SessionStore.Key] = stored;
        var store = new SessionStore(_secrets);

        // Act
        var loaded = await store.GetAsync(CancellationToken.None);

        // Assert
        loaded.Should().BeNull();
    }

    [Fact]
    public async Task ClearAsync_StoredSession_RemovesIt()
    {
        // Arrange
        var store = new SessionStore(_secrets);
        await store.SaveAsync(Session, CancellationToken.None);

        // Act
        await store.ClearAsync(CancellationToken.None);

        // Assert
        _secrets.Values.Should().BeEmpty();
    }
}
```

`tests/Coo.Mobile.Infrastructure.Tests/AuthClientTests.cs`:

```csharp
using System.Net;
using System.Text;
using Coo.Domain.Common;
using Coo.Mobile.Application.Auth;
using Coo.Mobile.Infrastructure.Auth;

namespace Coo.Mobile.Infrastructure.Tests;

/// <summary>How HTTP answers become results (Spec 010 §3.3): only a refusal is a refusal; anything else is "try later".</summary>
public sealed class AuthClientTests
{
    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task RefreshAsync_TooManyRequests_ThrowsUnavailableNotRejected(HttpStatusCode status)
    {
        // Arrange: the rate limit, or a deploy restarting the API, must never sign anyone out.
        var client = Client(_ => Json(status, """{"code":"auth.rate-limited"}"""));

        // Act
        Func<Task> act = async () => await client.RefreshAsync("refresh-1", CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<AuthUnavailableException>();
    }

    [Fact]
    public async Task RefreshAsync_RefreshInvalid_ThrowsRejectedWithTheCode()
    {
        // Arrange
        var client = Client(_ => Json(HttpStatusCode.Unauthorized, """{"code":"auth.refresh-invalid"}"""));

        // Act
        Func<Task> act = async () => await client.RefreshAsync("refresh-1", CancellationToken.None);

        // Assert
        (await act.Should().ThrowAsync<AuthRejectedException>()).Which.Code.Should().Be("auth.refresh-invalid");
    }

    [Fact]
    public async Task GetNonceAsync_NoNetwork_ThrowsUnavailable()
    {
        // Arrange
        var client = Client(_ => throw new HttpRequestException("Network is unreachable"));

        // Act
        Func<Task> act = async () => await client.GetNonceAsync(CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<AuthUnavailableException>();
    }

    [Fact]
    public async Task SignInWithGoogleAsync_Joined_SendsTheRequestAndMapsTheGrant()
    {
        // Arrange
        string? sent = null;
        var client = Client(async request =>
        {
            sent = await request.Content!.ReadAsStringAsync();
            return await Json(HttpStatusCode.OK,
                """
                {"accessToken":"a","accessTokenExpiresAt":"2026-10-02T12:15:00+00:00","refreshToken":"r",
                 "user":{"id":"7a1c0000-0000-7000-8000-0000000000c1","email":"andrzej@example.com","displayName":"Andrzej"},
                 "householdId":"7a1c0000-0000-7000-8000-0000000000a9","outcome":"joined"}
                """);
        });
        var request = new GoogleSignIn("id-token", "Pixel 10",
            new DeviceId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000d1")), new HouseholdId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a1")));

        // Act
        var grant = await client.SignInWithGoogleAsync(request, CancellationToken.None);

        // Assert
        sent.Should().Be("""{"idToken":"id-token","deviceName":"Pixel 10","deviceId":"7a1c0000-0000-7000-8000-0000000000d1","provisionalHouseholdId":"7a1c0000-0000-7000-8000-0000000000a1"}""");
        grant.Outcome.Should().Be(SignInOutcome.Joined);
        grant.Household.Should().Be(new HouseholdId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a9")));
        grant.Profile.Should().Be(new AccountProfile(new UserId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000c1")), "Andrzej", "andrzej@example.com"));
        grant.Tokens.Should().Be(new SessionTokens("a", new DateTimeOffset(2026, 10, 2, 12, 15, 0, TimeSpan.Zero), "r"));
    }

    private static AuthClient Client(Func<HttpRequestMessage, Task<HttpResponseMessage>> server) =>
        new(new HttpClient(new FakeServer(server)) { BaseAddress = new Uri("https://api.test/") });

    private static Task<HttpResponseMessage> Json(HttpStatusCode status, string body) =>
        Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
}
```

`tests/Coo.Mobile.Infrastructure.Tests/AuthenticatingHandlerTests.cs`:

```csharp
using System.Net;
using Coo.Domain.Common;
using Coo.Mobile.Application.Auth;
using Coo.Mobile.Infrastructure.Auth;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Coo.Mobile.Infrastructure.Tests;

/// <summary>Spec 010 §3.3: one refresh at a time; a network failure is "try later"; a refused refresh signs out.</summary>
public sealed class AuthenticatingHandlerTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
    private static readonly Uri Me = new("api/me", UriKind.Relative);
    private static readonly StoredSession Session = new(
        "old", Now.AddMinutes(10), "refresh-1",
        new AccountProfile(new UserId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000c1")), "Andrzej", "andrzej@example.com"),
        new HouseholdId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a1")), JoinPending: false);

    private readonly SessionStore _sessions = new(new MemorySecrets());
    private readonly IAuthApi _api = Substitute.For<IAuthApi>();
    private readonly ManualTimeProvider _time = new(Now);
    private readonly Account _account;
    private readonly TokenRefresher _refresher;

    public AuthenticatingHandlerTests()
    {
        _account = new Account(_sessions);
        _refresher = new TokenRefresher(_sessions, _api, _account, _time);
    }

    public void Dispose() => _refresher.Dispose();

    [Fact]
    public async Task SendAsync_ValidToken_SendsItAsBearerWithoutRefreshing()
    {
        // Arrange
        await _account.SignedInAsync(Session, CancellationToken.None);
        string? sentWith = null;
        using var client = Client(r =>
        {
            sentWith = r.Headers.Authorization?.ToString();
            return Ok();
        });

        // Act
        var response = await client.GetAsync(Me);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        sentWith.Should().Be("Bearer old");
        await _api.DidNotReceiveWithAnyArgs().RefreshAsync(default!, default);
    }

    [Fact]
    public async Task SendAsync_ManyConcurrent401s_RefreshesOnce()
    {
        // Arrange: ten calls with the expired token; the refresh is held until all ten have been refused.
        await _account.SignedInAsync(Session, CancellationToken.None);
        var refused = 0;
        var allRefused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _api.RefreshAsync("refresh-1", Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            await allRefused.Task;
            return new SessionTokens("new", Now.AddMinutes(15), "refresh-2");
        });
        using var client = Client(r =>
        {
            if (r.Headers.Authorization?.Parameter != "old")
                return Ok();
            if (Interlocked.Increment(ref refused) == 10)
                allRefused.SetResult();
            return Status(HttpStatusCode.Unauthorized);
        });

        // Act
        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => client.GetAsync(Me)));

        // Assert
        responses.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.OK);
        await _api.Received(1).RefreshAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        (await _sessions.GetAsync(CancellationToken.None))!.RefreshToken.Should().Be("refresh-2");
    }

    [Fact]
    public async Task SendAsync_TokenAboutToExpire_RefreshesBeforeSending()
    {
        // Arrange: 30 seconds left, less than the one-minute margin.
        await _account.SignedInAsync(Session with { AccessTokenExpiresAt = Now.AddSeconds(30) }, CancellationToken.None);
        _api.RefreshAsync("refresh-1", Arg.Any<CancellationToken>()).Returns(new SessionTokens("new", Now.AddMinutes(15), "refresh-2"));
        var tokens = new List<string?>();
        using var client = Client(r =>
        {
            tokens.Add(r.Headers.Authorization?.Parameter);
            return Ok();
        });

        // Act
        await client.GetAsync(Me);

        // Assert
        tokens.Should().Equal("new");
    }

    [Fact]
    public async Task SendAsync_RefreshUnreachable_KeepsTheSessionAndThrows()
    {
        // Arrange
        await _account.SignedInAsync(Session, CancellationToken.None);
        _api.RefreshAsync("refresh-1", Arg.Any<CancellationToken>()).ThrowsAsync(new AuthUnavailableException("offline"));
        using var client = Client(_ => Status(HttpStatusCode.Unauthorized));

        // Act
        Func<Task> act = async () => await client.GetAsync(Me);

        // Assert
        await act.Should().ThrowAsync<AuthUnavailableException>();
        (await _sessions.GetAsync(CancellationToken.None)).Should().Be(Session);
        _account.State.Should().Be(new SignedIn(Session.Profile));
    }

    [Fact]
    public async Task SendAsync_RefreshRejected_SignsOutAsExpiredAndReturns401()
    {
        // Arrange: the session was revoked on the server. Only the tokens go; local data is not this code's to touch.
        await _account.SignedInAsync(Session, CancellationToken.None);
        _api.RefreshAsync("refresh-1", Arg.Any<CancellationToken>()).ThrowsAsync(new AuthRejectedException("auth.refresh-invalid"));
        using var client = Client(_ => Status(HttpStatusCode.Unauthorized));

        // Act
        var response = await client.GetAsync(Me);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _account.State.Should().Be(new SignedOut(SessionExpired: true));
        (await _sessions.GetAsync(CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task SendAsync_401OnARequestWithABody_RetriesWithTheSameBody()
    {
        // Arrange: DELETE /api/account { "confirm": true } with a token that just expired.
        await _account.SignedInAsync(Session, CancellationToken.None);
        _api.RefreshAsync("refresh-1", Arg.Any<CancellationToken>()).Returns(new SessionTokens("new", Now.AddMinutes(15), "refresh-2"));
        var bodies = new List<string>();
        using var client = Client(async r =>
        {
            bodies.Add(await r.Content!.ReadAsStringAsync());
            return r.Headers.Authorization?.Parameter == "old" ? new HttpResponseMessage(HttpStatusCode.Unauthorized) : new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        using var request = new HttpRequestMessage(HttpMethod.Delete, new Uri("api/account", UriKind.Relative))
        {
            Content = new StringContent("""{"confirm":true}""", System.Text.Encoding.UTF8, "application/json"),
        };

        // Act
        var response = await client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        bodies.Should().Equal("""{"confirm":true}""", """{"confirm":true}""");
    }

    [Fact]
    public async Task SendAsync_SignedOut_Returns401WithoutCallingTheServer()
    {
        // Arrange
        var server = new FakeServer(_ => Ok());
        using var client = new HttpClient(new AuthenticatingHandler(_refresher) { InnerHandler = server }) { BaseAddress = new Uri("https://api.test/") };

        // Act
        var response = await client.GetAsync(Me);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        server.Calls.Should().Be(0);
    }

    private HttpClient Client(Func<HttpRequestMessage, Task<HttpResponseMessage>> server) =>
        new(new AuthenticatingHandler(_refresher) { InnerHandler = new FakeServer(server) }) { BaseAddress = new Uri("https://api.test/") };

    private static Task<HttpResponseMessage> Ok() => Status(HttpStatusCode.OK);

    private static Task<HttpResponseMessage> Status(HttpStatusCode status) => Task.FromResult(new HttpResponseMessage(status));
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test tests/Coo.Mobile.Infrastructure.Tests --filter "FullyQualifiedName~SessionStoreTests|FullyQualifiedName~AuthClientTests|FullyQualifiedName~AuthenticatingHandlerTests"`
Expected: build errors, the `Coo.Mobile.Infrastructure.Auth` types do not exist.

- [ ] **Step 3: The session codec**

`src/Mobile/Coo.Mobile.Infrastructure/Auth/SessionStore.cs`:

```csharp
using System.Globalization;
using Coo.Domain.Common;
using Coo.Mobile.Application.Auth;

namespace Coo.Mobile.Infrastructure.Auth;

/// <summary>
/// The session as one value in the secret store (Spec 010 §3.3: SecureStorage, backed by the Android Keystore). One
/// value is written atomically; fields are separated by U+001F. Reading is total: anything unreadable is no session, so
/// the person signs in again instead of Coo crashing at start-up.
/// </summary>
internal sealed class SessionStore(ISecretStore secrets) : ISessionStore
{
    public const string Key = "coo.session";
    private const char Separator = '\u001f';
    private const string Version = "1";
    private const long LatestUnixMs = 253_402_300_799_999;

    public async Task<StoredSession?> GetAsync(CancellationToken cancellationToken)
    {
        var text = await secrets.GetAsync(Key, cancellationToken);
        return text is null ? null : Parse(text);
    }

    public Task SaveAsync(StoredSession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        return secrets.SetAsync(Key, Format(session), cancellationToken);
    }

    public Task ClearAsync(CancellationToken cancellationToken) => secrets.RemoveAsync(Key, cancellationToken);

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
        session.JoinPending ? "1" : "0");

    internal static StoredSession? Parse(string text)
    {
        var f = text.Split(Separator);
        if (f.Length != 9 || f[0] != Version || f[1].Length == 0 || f[3].Length == 0 || f[8] is not ("0" or "1")
            || !long.TryParse(f[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var expires) || expires is < 0 or > LatestUnixMs
            || !Guid.TryParseExact(f[4], "D", out var user) || user == Guid.Empty
            || !Guid.TryParseExact(f[7], "D", out var household) || household == Guid.Empty)
            return null;
        return new StoredSession(
            f[1], DateTimeOffset.FromUnixTimeMilliseconds(expires), f[3], new AccountProfile(new UserId(user), f[5], f[6]),
            new HouseholdId(household), f[8] == "1");
    }

    private static string Clean(string value) => value.Replace(Separator, ' ');
}
```

- [ ] **Step 4: JSON and HTTP**

`src/Mobile/Coo.Mobile.Infrastructure/Auth/AuthJsonContext.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;
using Coo.Contracts.Auth;

namespace Coo.Mobile.Infrastructure.Auth;

/// <summary>Source-generated, so the trimmed Android build keeps it (as <c>CodecJsonContext</c>). Web defaults: camelCase.</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(NonceResponse))]
[JsonSerializable(typeof(GoogleSignInRequest))]
[JsonSerializable(typeof(SessionResponse))]
[JsonSerializable(typeof(RefreshRequest))]
[JsonSerializable(typeof(TokensResponse))]
[JsonSerializable(typeof(MeResponse))]
[JsonSerializable(typeof(DeleteAccountRequest))]
[JsonSerializable(typeof(ErrorResponse))]
internal sealed partial class AuthJsonContext : JsonSerializerContext;
```

`src/Mobile/Coo.Mobile.Infrastructure/Auth/AuthHttp.cs`:

```csharp
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Coo.Mobile.Application.Auth;

namespace Coo.Mobile.Infrastructure.Auth;

/// <summary>
/// How an answer becomes a result (Spec 010 §3.3). A refusal (4xx other than 429) throws <see cref="AuthRejectedException"/>
/// with the server's code; no network, a timeout, 429 and 5xx throw <see cref="AuthUnavailableException"/>: try later.
/// </summary>
internal static class AuthHttp
{
    public static async Task<HttpResponseMessage> SendAsync(HttpClient http, HttpRequestMessage request, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException e)
        {
            throw new AuthUnavailableException("The server could not be reached.", e);
        }
        catch (TaskCanceledException e) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AuthUnavailableException("The server did not answer in time.", e);
        }

        if (response.IsSuccessStatusCode)
            return response;

        using (response)
        {
            var status = (int)response.StatusCode;
            if (response.StatusCode == HttpStatusCode.TooManyRequests || status >= 500)
                throw new AuthUnavailableException(string.Create(CultureInfo.InvariantCulture, $"The server answered {status}."));
            throw new AuthRejectedException(await CodeAsync(response, status, cancellationToken));
        }
    }

    public static async Task<T> ReadAsync<T>(HttpResponseMessage response, JsonTypeInfo<T> type, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync(type, cancellationToken)
                ?? throw new AuthUnavailableException("The server sent an empty answer.");
        }
        catch (JsonException e)
        {
            throw new AuthUnavailableException("The server sent an unreadable answer.", e);
        }
    }

    private static async Task<string> CodeAsync(HttpResponseMessage response, int status, CancellationToken cancellationToken)
    {
        try
        {
            var error = await response.Content.ReadFromJsonAsync(AuthJsonContext.Default.ErrorResponse, cancellationToken);
            if (!string.IsNullOrEmpty(error?.Code))
                return error.Code;
        }
        catch (JsonException)
        {
            // No code in the body (the bearer handler's bare 401): the status stands in for it.
        }

        return string.Create(CultureInfo.InvariantCulture, $"http-{status}");
    }
}
```

`src/Mobile/Coo.Mobile.Infrastructure/Auth/AuthClient.cs`:

```csharp
using System.Net.Http.Json;
using Coo.Contracts.Auth;
using Coo.Domain.Common;
using Coo.Mobile.Application.Auth;

namespace Coo.Mobile.Infrastructure.Auth;

/// <summary><c>/api/auth</c> without an access token (Spec 010 §4.1). Contracts are mapped to the application's records here.</summary>
internal sealed class AuthClient(HttpClient http) : IAuthApi
{
    private static readonly Uri NonceUri = new("api/auth/nonce", UriKind.Relative);
    private static readonly Uri GoogleUri = new("api/auth/google", UriKind.Relative);
    private static readonly Uri RefreshUri = new("api/auth/refresh", UriKind.Relative);

    public async Task<string> GetNonceAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, NonceUri);
        using var response = await AuthHttp.SendAsync(http, request, cancellationToken);
        return (await AuthHttp.ReadAsync(response, AuthJsonContext.Default.NonceResponse, cancellationToken)).Nonce;
    }

    public async Task<SignInGrant> SignInWithGoogleAsync(GoogleSignIn request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var body = new GoogleSignInRequest(request.IdToken, request.DeviceName, request.DeviceId.Value, request.ProvisionalHouseholdId.Value);
        using var message = new HttpRequestMessage(HttpMethod.Post, GoogleUri) { Content = JsonContent.Create(body, AuthJsonContext.Default.GoogleSignInRequest) };
        using var response = await AuthHttp.SendAsync(http, message, cancellationToken);
        var s = await AuthHttp.ReadAsync(response, AuthJsonContext.Default.SessionResponse, cancellationToken);
        return new SignInGrant(
            new SessionTokens(s.AccessToken, s.AccessTokenExpiresAt, s.RefreshToken),
            new AccountProfile(new UserId(s.User.Id), s.User.DisplayName, s.User.Email),
            new HouseholdId(s.HouseholdId),
            Outcome(s.Outcome));
    }

    public async Task<SessionTokens> RefreshAsync(string refreshToken, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, RefreshUri)
        {
            Content = JsonContent.Create(new RefreshRequest(refreshToken), AuthJsonContext.Default.RefreshRequest),
        };
        using var response = await AuthHttp.SendAsync(http, message, cancellationToken);
        var t = await AuthHttp.ReadAsync(response, AuthJsonContext.Default.TokensResponse, cancellationToken);
        return new SessionTokens(t.AccessToken, t.AccessTokenExpiresAt, t.RefreshToken);
    }

    private static SignInOutcome Outcome(string value) => value switch
    {
        AuthOutcomes.Adopted => SignInOutcome.Adopted,
        AuthOutcomes.Created => SignInOutcome.Created,
        AuthOutcomes.Existing => SignInOutcome.Existing,
        AuthOutcomes.Joined => SignInOutcome.Joined,
        _ => throw new AuthUnavailableException("The server reported an outcome this version does not know."),
    };
}
```

`src/Mobile/Coo.Mobile.Infrastructure/Auth/AccountClient.cs`:

```csharp
using System.Net.Http.Json;
using Coo.Contracts.Auth;
using Coo.Domain.Common;
using Coo.Mobile.Application.Auth;

namespace Coo.Mobile.Infrastructure.Auth;

/// <summary>Calls with the access token; its <see cref="HttpClient"/> runs through <see cref="AuthenticatingHandler"/>.</summary>
internal sealed class AccountClient(HttpClient http) : IAccountApi
{
    private static readonly Uri SignOutUri = new("api/auth/sign-out", UriKind.Relative);
    private static readonly Uri MeUri = new("api/me", UriKind.Relative);
    private static readonly Uri AccountUri = new("api/account", UriKind.Relative);

    public async Task SignOutAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, SignOutUri);
        using var response = await AuthHttp.SendAsync(http, request, cancellationToken);
    }

    public async Task<AccountOverview> GetMeAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, MeUri);
        using var response = await AuthHttp.SendAsync(http, request, cancellationToken);
        var me = await AuthHttp.ReadAsync(response, AuthJsonContext.Default.MeResponse, cancellationToken);
        return new AccountOverview(
            new AccountProfile(new UserId(me.User.Id), me.User.DisplayName, me.User.Email),
            me.Household is null ? null : new HouseholdId(me.Household.HouseholdId),
            me.Household?.Role,
            [.. me.Devices.Select(d => new DeviceSessionInfo(d.DeviceName, d.LastUsedAt, d.IsCurrent))]);
    }

    public async Task DeleteAccountAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, AccountUri)
        {
            Content = JsonContent.Create(new DeleteAccountRequest(Confirm: true), AuthJsonContext.Default.DeleteAccountRequest),
        };
        using var response = await AuthHttp.SendAsync(http, request, cancellationToken);
    }
}
```

- [ ] **Step 5: The refresher and the handler**

`src/Mobile/Coo.Mobile.Infrastructure/Auth/TokenRefresher.cs`:

```csharp
using Coo.Mobile.Application.Auth;

namespace Coo.Mobile.Infrastructure.Auth;

/// <summary>
/// One refresh at a time (Spec 010 §3.3). Callers that saw the same expired token wait for the refresh in flight and get
/// its result instead of starting their own. A network failure propagates as <see cref="AuthUnavailableException"/> and
/// keeps the session; a refused refresh signs out as expired and keeps every local record.
/// </summary>
internal sealed class TokenRefresher(ISessionStore sessions, IAuthApi api, Account account, TimeProvider time) : IDisposable
{
    public static readonly TimeSpan Margin = TimeSpan.FromMinutes(1);

    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>The token to send, refreshed first when it expires within a minute. Null when signed out.</summary>
    public async Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        var session = await sessions.GetAsync(cancellationToken);
        if (session is null)
            return null;
        return session.AccessTokenExpiresAt - time.GetUtcNow() > Margin
            ? session.AccessToken
            : await RefreshAsync(session.AccessToken, cancellationToken);
    }

    /// <summary>A new access token in place of <paramref name="staleAccessToken"/>; null when the session is gone.</summary>
    public async Task<string?> RefreshAsync(string staleAccessToken, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var session = await sessions.GetAsync(cancellationToken);
            if (session is null)
                return null;
            if (session.AccessToken != staleAccessToken)
                return session.AccessToken;

            SessionTokens tokens;
            try
            {
                tokens = await api.RefreshAsync(session.RefreshToken, cancellationToken);
            }
            catch (AuthRejectedException)
            {
                await account.SignedOutAsync(sessionExpired: true, cancellationToken);
                return null;
            }

            await sessions.SaveAsync(
                session with { AccessToken = tokens.AccessToken, AccessTokenExpiresAt = tokens.AccessTokenExpiresAt, RefreshToken = tokens.RefreshToken },
                cancellationToken);
            return tokens.AccessToken;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}
```

`src/Mobile/Coo.Mobile.Infrastructure/Auth/AuthenticatingHandler.cs`:

```csharp
using System.Net;
using System.Net.Http.Headers;

namespace Coo.Mobile.Infrastructure.Auth;

/// <summary>
/// Adds the access token, refreshing it when it is about to expire, and once more after a <c>401</c> (Spec 010 §3.3).
/// Signed out, it answers <c>401</c> itself without touching the network. A retried request carries the same body.
/// </summary>
internal sealed class AuthenticatingHandler(TokenRefresher refresher) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = await refresher.GetAccessTokenAsync(cancellationToken);
        if (token is null)
            return new HttpResponseMessage(HttpStatusCode.Unauthorized) { RequestMessage = request };

        // Read the body before the first send: a request is sent once, so a retry needs its own copy.
        var body = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await base.SendAsync(request, cancellationToken);
        if (response.StatusCode != HttpStatusCode.Unauthorized)
            return response;

        var fresh = await refresher.RefreshAsync(token, cancellationToken);
        if (fresh is null)
            return response;

        response.Dispose();
        var retry = Copy(request, body);
        retry.Headers.Authorization = new AuthenticationHeaderValue("Bearer", fresh);
        return await base.SendAsync(retry, cancellationToken);
    }

    /// <summary>The retry is owned by its response (<c>response.RequestMessage</c>), like the original.</summary>
    private static HttpRequestMessage Copy(HttpRequestMessage request, byte[]? body)
    {
        var copy = new HttpRequestMessage(request.Method, request.RequestUri) { Version = request.Version };
        foreach (var header in request.Headers.Where(h => h.Key != "Authorization"))
            copy.Headers.TryAddWithoutValidation(header.Key, header.Value);
        if (body is not null)
        {
            copy.Content = new ByteArrayContent(body);
            foreach (var header in request.Content!.Headers.Where(h => h.Key != "Content-Length"))
                copy.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return copy;
    }
}
```

If `CA2000` fires on the retry or the synthesized `401`, suppress it on that line with the justification in the summary
(`#pragma warning disable CA2000 // Owned by the returned response.`).

- [ ] **Step 6: Register them**

In `src/Mobile/Coo.Mobile.Infrastructure/ServiceCollectionExtensions.cs` (add `using Coo.Mobile.Application.Auth;` and
`using Coo.Mobile.Infrastructure.Auth;`): add the parameter and its doc, the check, and the registrations:

```csharp
    /// <param name="apiBaseAddress">The API, ending in a slash: <c>https://api.coo.lechdigital.nl/</c> (Spec 010 §6).</param>
    public static IServiceCollection AddMobileInfrastructure(this IServiceCollection services, string databasePath, string appVersion, Uri apiBaseAddress)
```

```csharp
        ArgumentNullException.ThrowIfNull(apiBaseAddress);
```

before `return services;`:

```csharp
        // Sign-in and sessions (Spec 010 §6). The host registers ISecretStore (SecureStorage).
        services.AddSingleton<ISessionStore, SessionStore>();
        services.AddSingleton<IAuthApi>(_ => new AuthClient(new HttpClient { BaseAddress = apiBaseAddress, Timeout = HttpTimeout }));
        services.AddSingleton(sp => new TokenRefresher(
            sp.GetRequiredService<ISessionStore>(), sp.GetRequiredService<IAuthApi>(), sp.GetRequiredService<Account>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IAccountApi>(sp => new AccountClient(
            new HttpClient(new AuthenticatingHandler(sp.GetRequiredService<TokenRefresher>()) { InnerHandler = new HttpClientHandler() })
            {
                BaseAddress = apiBaseAddress,
                Timeout = HttpTimeout,
            }));
```

and the field at the top of the class:

```csharp
    private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(20);
```

(On Android, `HttpClientHandler` is backed by the native `AndroidMessageHandler`.)

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test tests/Coo.Mobile.Infrastructure.Tests && dotnet test tests/Coo.Architecture.Tests`
Expected: PASS (20 new tests); every Infrastructure type except `ServiceCollectionExtensions` is `internal`; SQLite stays
inside `Persistence`. The host does not compile yet (the new `apiBaseAddress` argument): Task 13 passes it.

- [ ] **Step 8: Commit**

```bash
git add src/Mobile/Coo.Mobile.Infrastructure tests/Coo.Mobile.Infrastructure.Tests
git commit -m "Mobile.Infrastructure: auth clients, single-flight refreshing handler, session codec (Spec 010 §3.3, §6)"
```

---

### Task 12: The Account section's view model, the privacy link, and the strings

**Files:**
- Create: `src/Mobile/Coo.Mobile.Presentation/Settings/AccountViewModel.cs`
- Modify: `src/Mobile/Coo.Mobile.Presentation/Settings/SettingsViewModel.cs` (`PrivacyUrl`)
- Modify: `src/Mobile/Coo.Mobile.Presentation/ServiceCollectionExtensions.cs`
- Modify: `eng/strings/strings.py`; generated `src/Mobile/Coo.Mobile.Presentation/Resources/AppResources*.resx`
- Test: `tests/Coo.Mobile.Presentation.Tests/AccountViewModelTests.cs`, `tests/Coo.Mobile.Presentation.Tests/SettingsTests.cs`

**Interfaces:**
- Consumes: Task 10's `Account`, states, `SignIn`, `SignOut`, `DeleteAccount`, `RefreshAccount`, ports.
- Produces:
  - `public sealed record ConfirmPrompt(string Title, string Message, string Accept, string Cancel)`.
  - `public sealed partial class AccountViewModel(...) : ObservableObject, IDisposable` with `AccountState State`
    (observable), `bool IsSignedIn`, `string Name`, `string Email`, `string StatusLine` (computed from `State` and the
    current language), `bool IsBusy`, `string Toast`, `ConfirmPrompt SignOutPrompt`, `Task<ConfirmPrompt> DeletePromptAsync()`,
    `Task RefreshAsync()`, and commands `SignInCommand`, `SignOutCommand`, `DeleteAccountCommand` (`IAsyncRelayCommand`).
  - `SettingsViewModel.PrivacyUrl` = `https://lechdigital.nl/projects/coo/privacy`.
  - String keys `Account.Title`, `Account.SignIn`, `Account.SignInHint`, `Account.SignInFailed`, `Account.SignOut`,
    `Account.SignOutConfirm`, `Account.Delete`, `Account.DeleteConfirm` (`{0}` = the baby's name),
    `Account.DeleteConfirmNoBaby`, `Account.DeleteFailed`, `Account.JoinPending`, `Account.SessionExpired`, `Action.Cancel`.

- [ ] **Step 1: The strings**

Add to `S` in `eng/strings/strings.py`, after the `'Settings.Privacy'` row:

```python
'Account.Title': ('Account', 'Konto', 'Compte', 'Cuenta', 'Account', 'Konto', 'Account'),
'Account.SignIn': ('Sign in with Google', 'Mit Google anmelden', 'Se connecter avec Google', 'Iniciar sesión con Google', 'Accedi con Google', 'Zaloguj się przez Google', 'Inloggen met Google'),
'Account.SignInHint': ('Keep your log safe and share it with family.', 'Sichere deine Einträge und teile sie mit der Familie.', 'Gardez votre journal en sécurité et partagez-le avec la famille.', 'Guarda tu registro a salvo y compártelo con la familia.', 'Tieni al sicuro il tuo registro e condividilo con la famiglia.', 'Zabezpiecz swój dziennik i udostępnij go rodzinie.', 'Bewaar je logboek veilig en deel het met familie.'),
'Account.SignInFailed': ("Sign-in didn't work. Check your connection and try again.", 'Die Anmeldung hat nicht geklappt. Prüfe deine Verbindung und versuche es noch einmal.', 'La connexion a échoué. Vérifiez votre connexion et réessayez.', 'No se ha podido iniciar sesión. Comprueba la conexión y vuelve a intentarlo.', 'Accesso non riuscito. Controlla la connessione e riprova.', 'Nie udało się zalogować. Sprawdź połączenie i spróbuj ponownie.', 'Inloggen is niet gelukt. Controleer je verbinding en probeer het opnieuw.'),
'Account.SignOut': ('Sign out', 'Abmelden', 'Se déconnecter', 'Cerrar sesión', 'Esci', 'Wyloguj się', 'Uitloggen'),
'Account.SignOutConfirm': ('Sign out on this phone? Your log stays on this phone.', 'Auf diesem Handy abmelden? Deine Einträge bleiben auf diesem Handy.', 'Se déconnecter sur ce téléphone ? Votre journal reste sur ce téléphone.', '¿Cerrar sesión en este teléfono? Tu registro se queda en este teléfono.', 'Uscire su questo telefono? Il tuo registro resta su questo telefono.', 'Wylogować się na tym telefonie? Twój dziennik zostanie na tym telefonie.', 'Uitloggen op deze telefoon? Je logboek blijft op deze telefoon.'),
'Account.Delete': ('Delete account', 'Konto löschen', 'Supprimer le compte', 'Eliminar cuenta', 'Elimina account', 'Usuń konto', 'Account verwijderen'),
'Account.DeleteConfirm': ("This deletes your Coo account and, because you are the only parent, your family's shared log for {0}. The entries on this phone stay here.", 'Damit werden dein Coo-Konto und, weil du der einzige Elternteil bist, die gemeinsamen Einträge deiner Familie für {0} gelöscht. Die Einträge auf diesem Handy bleiben hier.', 'Cela supprime votre compte Coo et, comme vous êtes le seul parent, le journal partagé de votre famille pour {0}. Les entrées de ce téléphone restent ici.', 'Esto elimina tu cuenta de Coo y, como eres el único progenitor, el registro compartido de tu familia de {0}. Los registros de este teléfono se quedan aquí.', "Questo elimina il tuo account Coo e, poiché sei l'unico genitore, il registro condiviso della tua famiglia per {0}. Le voci su questo telefono restano qui.", 'To usunie Twoje konto Coo oraz, ponieważ jesteś jedynym rodzicem, wspólny dziennik Twojej rodziny: {0}. Wpisy na tym telefonie zostaną tutaj.', 'Hiermee verwijder je je Coo-account en, omdat jij de enige ouder bent, het gedeelde logboek van je gezin voor {0}. De items op deze telefoon blijven hier.'),
'Account.DeleteConfirmNoBaby': ("This deletes your Coo account and, because you are the only parent, your family's shared log. The entries on this phone stay here.", 'Damit werden dein Coo-Konto und, weil du der einzige Elternteil bist, die gemeinsamen Einträge deiner Familie gelöscht. Die Einträge auf diesem Handy bleiben hier.', 'Cela supprime votre compte Coo et, comme vous êtes le seul parent, le journal partagé de votre famille. Les entrées de ce téléphone restent ici.', 'Esto elimina tu cuenta de Coo y, como eres el único progenitor, el registro compartido de tu familia. Los registros de este teléfono se quedan aquí.', "Questo elimina il tuo account Coo e, poiché sei l'unico genitore, il registro condiviso della tua famiglia. Le voci su questo telefono restano qui.", 'To usunie Twoje konto Coo oraz, ponieważ jesteś jedynym rodzicem, wspólny dziennik Twojej rodziny. Wpisy na tym telefonie zostaną tutaj.', 'Hiermee verwijder je je Coo-account en, omdat jij de enige ouder bent, het gedeelde logboek van je gezin. De items op deze telefoon blijven hier.'),
'Account.DeleteFailed': ("Your account wasn't deleted. Check your connection and try again.", 'Dein Konto wurde nicht gelöscht. Prüfe deine Verbindung und versuche es noch einmal.', "Votre compte n'a pas été supprimé. Vérifiez votre connexion et réessayez.", 'Tu cuenta no se ha eliminado. Comprueba la conexión y vuelve a intentarlo.', 'Il tuo account non è stato eliminato. Controlla la connessione e riprova.', 'Konto nie zostało usunięte. Sprawdź połączenie i spróbuj ponownie.', 'Je account is niet verwijderd. Controleer je verbinding en probeer het opnieuw.'),
'Account.JoinPending': ("Signed in. Your entries on this phone join your family's log when sharing arrives.", 'Angemeldet. Deine Einträge auf diesem Handy kommen zu denen deiner Familie, sobald Teilen verfügbar ist.', 'Connecté. Les entrées de ce téléphone rejoindront le journal de votre famille dès que le partage sera disponible.', 'Sesión iniciada. Tus registros de este teléfono se unirán al registro de tu familia cuando llegue la opción de compartir.', 'Accesso effettuato. Le voci su questo telefono si uniranno al registro della tua famiglia quando arriverà la condivisione.', 'Zalogowano. Wpisy z tego telefonu dołączą do dziennika rodziny, gdy pojawi się udostępnianie.', 'Ingelogd. Je items op deze telefoon komen bij het logboek van je gezin zodra delen beschikbaar is.'),
'Account.SessionExpired': ('Sign in again to keep sharing.', 'Melde dich erneut an, um weiter zu teilen.', 'Reconnectez-vous pour continuer à partager.', 'Vuelve a iniciar sesión para seguir compartiendo.', 'Accedi di nuovo per continuare a condividere.', 'Zaloguj się ponownie, aby dalej udostępniać.', 'Log opnieuw in om te blijven delen.'),
'Action.Cancel': ('Cancel', 'Abbrechen', 'Annuler', 'Cancelar', 'Annulla', 'Anuluj', 'Annuleren'),
```

Run: `python3 eng/strings/generate.py`
Expected: each `AppResources*.resx` line count printed rises by 13.

- [ ] **Step 2: Write the failing tests**

`tests/Coo.Mobile.Presentation.Tests/AccountViewModelTests.cs`:

```csharp
using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;
using Coo.Domain.Households;
using Coo.Mobile.Application.Auth;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Application.Households;
using Coo.Mobile.Application.Preferences;
using Coo.Mobile.Presentation.Localization;
using Coo.Mobile.Presentation.Settings;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Coo.Mobile.Presentation.Tests;

/// <summary>Spec 010 §2 and §5: the Account section's states and texts; the deletion confirmation names the baby.</summary>
[Collection(nameof(StringsState))]
public sealed class AccountViewModelTests : IDisposable
{
    private static readonly DeviceId Device = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000d1"));
    private static readonly HouseholdId Household = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a1"));
    private static readonly HouseholdId AccountsHousehold = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a9"));
    private static readonly AccountProfile Profile = new(new UserId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000c1")), "Andrzej", "andrzej@example.com");
    private static readonly DateTimeOffset Expires = new(2026, 10, 2, 12, 15, 0, TimeSpan.Zero);

    private readonly IAuthApi _api = Substitute.For<IAuthApi>();
    private readonly IAccountApi _accountApi = Substitute.For<IAccountApi>();
    private readonly ISignInProvider _google = Substitute.For<ISignInProvider>();
    private readonly IHouseholdStore _households = Substitute.For<IHouseholdStore>();
    private readonly IDeviceIdentity _device = Substitute.For<IDeviceIdentity>();
    private readonly IDeviceName _deviceName = Substitute.For<IDeviceName>();
    private readonly MemorySessions _sessions = new();
    private readonly Account _account;

    public AccountViewModelTests()
    {
        Strings.Use(new LanguagePreference("en"), null);
        _account = new Account(_sessions);
        var settings = HouseholdSettings.Create(Household, VolumeUnit.Millilitres, "Europe/Amsterdam", new HlcStamp(1, 0, Device)).Value;
        var baby = BabyProfile.Create(new BabyId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000b1")), Household, "Maja", [],
            new DateOnly(2026, 8, 20), null, new HlcStamp(1, 0, Device), new DateOnly(2026, 10, 2)).Value;
        _households.ListHouseholdsAsync(Arg.Any<CancellationToken>()).Returns([settings]);
        _households.ListBabiesAsync(Household, Arg.Any<CancellationToken>()).Returns([baby]);
        _device.GetAsync(Arg.Any<CancellationToken>()).Returns(Device);
        _deviceName.Name.Returns("Pixel 10");
        _api.GetNonceAsync(Arg.Any<CancellationToken>()).Returns("nonce-1");
        _google.GetIdTokenAsync("nonce-1", Arg.Any<CancellationToken>()).Returns("id-token");
    }

    public void Dispose() => Strings.Use(LanguagePreference.System, null);

    [Fact]
    public void Constructor_SignedOut_OffersSignInWithTheHint()
    {
        // Arrange
        var account = _account;

        // Act
        using var model = Model();

        // Assert
        account.State.Should().BeOfType<SignedOut>();
        model.IsSignedIn.Should().BeFalse();
        model.StatusLine.Should().Be("Keep your log safe and share it with family.");
    }

    [Fact]
    public async Task SignInCommand_Succeeds_ShowsTheNameAndEmail()
    {
        // Arrange
        using var model = Model();
        _api.SignInWithGoogleAsync(Arg.Any<GoogleSignIn>(), Arg.Any<CancellationToken>())
            .Returns(new SignInGrant(new SessionTokens("a", Expires, "r"), Profile, Household, SignInOutcome.Adopted));

        // Act
        await model.SignInCommand.ExecuteAsync(null);

        // Assert
        model.IsSignedIn.Should().BeTrue();
        model.Name.Should().Be("Andrzej");
        model.Email.Should().Be("andrzej@example.com");
        model.StatusLine.Should().BeEmpty();
        model.Toast.Should().BeEmpty();
    }

    [Fact]
    public async Task SignInCommand_Fails_SaysSoAndStaysSignedOut()
    {
        // Arrange
        using var model = Model();
        _api.GetNonceAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new AuthUnavailableException("offline"));

        // Act
        await model.SignInCommand.ExecuteAsync(null);

        // Assert
        model.IsSignedIn.Should().BeFalse();
        model.Toast.Should().Be("Sign-in didn't work. Check your connection and try again.");
    }

    [Fact]
    public async Task SignInCommand_Cancelled_SaysNothing()
    {
        // Arrange
        using var model = Model();
        _google.GetIdTokenAsync("nonce-1", Arg.Any<CancellationToken>()).Returns((string?)null);

        // Act
        await model.SignInCommand.ExecuteAsync(null);

        // Assert
        model.IsSignedIn.Should().BeFalse();
        model.Toast.Should().BeEmpty();
    }

    [Fact]
    public async Task State_JoinPending_SaysTheEntriesJoinLater()
    {
        // Arrange
        using var model = Model();

        // Act
        await _account.SignedInAsync(new StoredSession("a", Expires, "r", Profile, AccountsHousehold, JoinPending: true), CancellationToken.None);

        // Assert
        model.IsSignedIn.Should().BeTrue();
        model.StatusLine.Should().Be("Signed in. Your entries on this phone join your family's log when sharing arrives.");
    }

    [Fact]
    public async Task State_SessionRefused_AsksToSignInAgain()
    {
        // Arrange: the refreshing handler found the session revoked.
        await _account.SignedInAsync(new StoredSession("a", Expires, "r", Profile, Household, JoinPending: false), CancellationToken.None);
        using var model = Model();

        // Act
        await _account.SignedOutAsync(sessionExpired: true, CancellationToken.None);

        // Assert
        model.IsSignedIn.Should().BeFalse();
        model.StatusLine.Should().Be("Sign in again to keep sharing.");
    }

    [Fact]
    public async Task DeletePromptAsync_SignedIn_NamesTheBaby()
    {
        // Arrange
        await _account.SignedInAsync(new StoredSession("a", Expires, "r", Profile, Household, JoinPending: false), CancellationToken.None);
        using var model = Model();

        // Act
        var prompt = await model.DeletePromptAsync();

        // Assert
        prompt.Message.Should().Be(
            "This deletes your Coo account and, because you are the only parent, your family's shared log for Maja. The entries on this phone stay here.");
        prompt.Accept.Should().Be("Delete account");
        prompt.Cancel.Should().Be("Cancel");
    }

    [Fact]
    public async Task DeletePromptAsync_JoinPending_DoesNotNameThisPhonesBaby()
    {
        // Arrange: this phone's Maja is not in the account's household, which is the one that goes.
        await _account.SignedInAsync(new StoredSession("a", Expires, "r", Profile, AccountsHousehold, JoinPending: true), CancellationToken.None);
        using var model = Model();

        // Act
        var prompt = await model.DeletePromptAsync();

        // Assert
        prompt.Message.Should().NotContain("Maja").And.EndWith("The entries on this phone stay here.");
    }

    [Fact]
    public void SignOutPrompt_Always_SaysTheLogStays()
    {
        // Arrange
        using var model = Model();

        // Act
        var prompt = model.SignOutPrompt;

        // Assert
        prompt.Should().Be(new ConfirmPrompt("Sign out", "Sign out on this phone? Your log stays on this phone.", "Sign out", "Cancel"));
    }

    [Fact]
    public async Task SignOutCommand_SignedIn_ShowsSignedOutAndTouchesNoLocalData()
    {
        // Arrange
        await _account.SignedInAsync(new StoredSession("a", Expires, "r", Profile, Household, JoinPending: false), CancellationToken.None);
        using var model = Model();

        // Act
        await model.SignOutCommand.ExecuteAsync(null);

        // Assert
        model.IsSignedIn.Should().BeFalse();
        await _households.DidNotReceiveWithAnyArgs().SaveSettingsAsync(default!, default);
        await _households.DidNotReceiveWithAnyArgs().SaveBabyAsync(default!, default);
    }

    [Fact]
    public async Task DeleteAccountCommand_Offline_SaysItWasNotDeleted()
    {
        // Arrange
        await _account.SignedInAsync(new StoredSession("a", Expires, "r", Profile, Household, JoinPending: false), CancellationToken.None);
        _accountApi.DeleteAccountAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new AuthUnavailableException("offline"));
        using var model = Model();

        // Act
        await model.DeleteAccountCommand.ExecuteAsync(null);

        // Assert
        model.IsSignedIn.Should().BeTrue();
        model.Toast.Should().Be("Your account wasn't deleted. Check your connection and try again.");
    }

    private AccountViewModel Model() => new(
        _account,
        new SignIn(_api, _google, _households, _device, _deviceName, _account),
        new SignOut(_accountApi, _account),
        new DeleteAccount(_accountApi, _account),
        new RefreshAccount(_accountApi, _sessions, _account),
        _households);

    private sealed class MemorySessions : ISessionStore
    {
        private StoredSession? _stored;

        public Task<StoredSession?> GetAsync(CancellationToken cancellationToken) => Task.FromResult(_stored);

        public Task SaveAsync(StoredSession session, CancellationToken cancellationToken)
        {
            _stored = session;
            return Task.CompletedTask;
        }

        public Task ClearAsync(CancellationToken cancellationToken)
        {
            _stored = null;
            return Task.CompletedTask;
        }
    }
}
```

Add to `SettingsTests`:

```csharp
    [Fact]
    public async Task OpenPrivacyCommand_Tapped_OpensCoosPageOnLechDigital()
    {
        // Arrange: Spec 010 Q4; coo.lechdigital.nl has no DNS.
        var model = Model();

        // Act
        await model.OpenPrivacyCommand.ExecuteAsync(null);

        // Assert
        await _launcher.Received(1).OpenBrowserAsync(new Uri("https://lechdigital.nl/projects/coo/privacy"));
    }
```

- [ ] **Step 3: Run them to verify they fail**

Run: `dotnet test tests/Coo.Mobile.Presentation.Tests --filter "FullyQualifiedName~AccountViewModelTests|FullyQualifiedName~SettingsTests"`
Expected: build error, `AccountViewModel` does not exist (and, once it does, the privacy test fails on the old URL).

- [ ] **Step 4: Implement**

In `SettingsViewModel.cs`:

```csharp
    public static readonly Uri PrivacyUrl = new("https://lechdigital.nl/projects/coo/privacy");
```

`src/Mobile/Coo.Mobile.Presentation/Settings/AccountViewModel.cs`:

```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Coo.Mobile.Application.Auth;
using Coo.Mobile.Application.Households;
using Coo.Mobile.Presentation.Localization;

namespace Coo.Mobile.Presentation.Settings;

/// <summary>A yes/no question the page asks with Android's dialog.</summary>
public sealed record ConfirmPrompt(string Title, string Message, string Accept, string Cancel);

/// <summary>
/// The Account section at the top of Settings (Spec 010 §2). It follows <see cref="Account"/>, so a refused session
/// found in the background shows here too. Texts are read on each use, so a language change needs no reload.
/// </summary>
public sealed partial class AccountViewModel : ObservableObject, IDisposable
{
    private readonly Account _account;
    private readonly SignIn _signIn;
    private readonly SignOut _signOut;
    private readonly DeleteAccount _deleteAccount;
    private readonly RefreshAccount _refresh;
    private readonly IHouseholdStore _households;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSignedIn), nameof(Name), nameof(Email), nameof(StatusLine))]
    private AccountState _state = new SignedOut(SessionExpired: false);

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _toast = "";

    public AccountViewModel(
        Account account, SignIn signIn, SignOut signOut, DeleteAccount deleteAccount, RefreshAccount refresh, IHouseholdStore households)
    {
        _account = account;
        _signIn = signIn;
        _signOut = signOut;
        _deleteAccount = deleteAccount;
        _refresh = refresh;
        _households = households;
        _account.Changed += OnAccountChanged;
        State = account.State;
    }

    public bool IsSignedIn => State is not SignedOut;

    public string Name => Profile?.Name ?? "";

    public string Email => Profile?.Email ?? "";

    /// <summary>The line under "Sign in with Google", or under the account.</summary>
    public string StatusLine => State switch
    {
        SignedOut { SessionExpired: true } => Strings.Get("Account.SessionExpired"),
        SignedOut => Strings.Get("Account.SignInHint"),
        JoinPending => Strings.Get("Account.JoinPending"),
        _ => "",
    };

    public ConfirmPrompt SignOutPrompt =>
        new(Strings.Get("Account.SignOut"), Strings.Get("Account.SignOutConfirm"), Strings.Get("Account.SignOut"), Strings.Get("Action.Cancel"));

    private AccountProfile? Profile => State switch
    {
        SignedIn s => s.Profile,
        JoinPending j => j.Profile,
        _ => null,
    };

    public void Dispose() => _account.Changed -= OnAccountChanged;

    /// <summary>Settings opened: the latest name and email from the server. Offline, nothing changes.</summary>
    public Task RefreshAsync() => _refresh.ExecuteAsync(CancellationToken.None);

    /// <summary>
    /// The deletion confirmation names the baby whose shared log goes (Spec 010 §5). A join-pending phone has never seen
    /// the account's household, so it names none.
    /// </summary>
    public async Task<ConfirmPrompt> DeletePromptAsync()
    {
        var names = new List<string>();
        if (State is not JoinPending)
        {
            var household = (await _households.ListHouseholdsAsync(CancellationToken.None)).FirstOrDefault(h => !h.IsDeleted);
            if (household is not null)
                names.AddRange((await _households.ListBabiesAsync(household.Id, CancellationToken.None)).Where(b => !b.IsDeleted).Select(b => b.Name));
        }

        var message = names.Count == 0
            ? Strings.Get("Account.DeleteConfirmNoBaby")
            : Strings.Format("Account.DeleteConfirm", string.Join(", ", names));
        return new ConfirmPrompt(Strings.Get("Account.Delete"), message, Strings.Get("Account.Delete"), Strings.Get("Action.Cancel"));
    }

    [RelayCommand]
    private async Task SignInAsync()
    {
        if (IsBusy)
            return;
        IsBusy = true;
        try
        {
            if (await _signIn.ExecuteAsync(CancellationToken.None) == SignInResult.Failed)
                Toast = Strings.Get("Account.SignInFailed");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private Task SignOutAsync() => _signOut.ExecuteAsync(CancellationToken.None);

    [RelayCommand]
    private async Task DeleteAccountAsync()
    {
        if (!await _deleteAccount.ExecuteAsync(CancellationToken.None))
            Toast = Strings.Get("Account.DeleteFailed");
    }

    private void OnAccountChanged(object? sender, EventArgs e) => State = _account.State;
}
```

In `src/Mobile/Coo.Mobile.Presentation/ServiceCollectionExtensions.cs`, after the `SettingsViewModel` line:

```csharp
        services.AddSingleton<Settings.AccountViewModel>();
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/Coo.Mobile.Presentation.Tests && dotnet test tests/Coo.Architecture.Tests`
Expected: PASS: 11 account tests, the privacy test, `LocalizationTests` (every new key in seven languages with matching
`{0}`), and `Presentation_Types_DoNotDependOnMaui`.

- [ ] **Step 6: Commit**

```bash
git add eng/strings/strings.py src/Mobile/Coo.Mobile.Presentation tests/Coo.Mobile.Presentation.Tests
git commit -m "Account section view model and strings in seven languages; privacy link on lechdigital.nl (Spec 010 §2, §5, Q4)"
```

---
### Task 13: Credential Manager, the device name, `SecureStorage`, and the wiring (Platform and host)

**Files:**
- Modify: `Directory.Packages.props`, `.github/dependabot.yml`
- Modify: `src/Mobile/Coo.Mobile.Platform/Coo.Mobile.Platform.csproj`, `src/Mobile/Coo.Mobile.Platform/ServiceCollectionExtensions.cs`
- Create: `src/Mobile/Coo.Mobile.Platform/Auth/CredentialManagerSignInProvider.cs`
- Create: `src/Mobile/Coo.Mobile.Platform/Auth/AndroidDeviceName.cs`
- Create: `src/Mobile/Coo.Mobile/BuildConstants.cs`, `src/Mobile/Coo.Mobile/Auth/MauiSecretStore.cs`
- Modify: `src/Mobile/Coo.Mobile/MauiProgram.cs`
- Modify: `src/Mobile/Coo.Mobile.Presentation/Settings/Licences.cs`
- Create: `src/Mobile/Coo.Mobile/Resources/Raw/licences/androidx-credentials-apache2.txt`, `google-identity-notice.txt`
- Test: the existing `tests/Coo.Mobile.Presentation.Tests/LicencesTests.cs`; the Android build

**Interfaces:**
- Consumes: Task 10's `ISignInProvider`, `IDeviceName`, `ISecretStore`, `SignInUnavailableException`; Task 11's
  `AddMobileInfrastructure(..., Uri apiBaseAddress)`.
- Produces:
  - `AddMobilePlatform(this IServiceCollection services, Func<Android.App.Activity?> currentActivity, string googleWebClientId)`
    registering `IClockFormat`, `IDeviceName` (`AndroidDeviceName`: `Build.Model`, "Pixel 10") and `ISignInProvider`
    (`CredentialManagerSignInProvider`).
  - `internal static class BuildConstants` (host) with `GoogleWebClientId` and `ApiBaseAddress`.
  - `internal sealed class MauiSecretStore : ISecretStore` (host, MAUI `SecureStorage`).

- [ ] **Step 1: Add the packages, and watch the licences test fail**

In `Directory.Packages.props`, under `Microsoft.Maui.Controls`:

```xml
    <!-- Sign in with Google (Spec 010a §5): exactly these. Newer ones pull AndroidX libraries newer than MAUI 10.0.0 pins
         (NU1608) or need a newer GoogleId (NU1605), errors here. Revisit with the next MAUI upgrade. -->
    <PackageVersion Include="Xamarin.AndroidX.Credentials" Version="1.5.0" />
    <PackageVersion Include="Xamarin.AndroidX.Credentials.PlayServicesAuth" Version="1.5.0" />
    <PackageVersion Include="Xamarin.GoogleAndroid.Libraries.Identity.GoogleId" Version="1.1.0.8" />
```

In `Coo.Mobile.Platform.csproj`:

```xml
    <PackageReference Include="Xamarin.AndroidX.Credentials" />
    <PackageReference Include="Xamarin.AndroidX.Credentials.PlayServicesAuth" />
    <PackageReference Include="Xamarin.GoogleAndroid.Libraries.Identity.GoogleId" />
```

In `.github/dependabot.yml`, under `ignore:`:

```yaml
      # Pinned by Spec 010a §5: newer ones clash with MAUI 10.0.0. Bumped by hand with MAUI.
      - dependency-name: Xamarin.AndroidX.Credentials*
      - dependency-name: Xamarin.GoogleAndroid.Libraries.Identity.GoogleId
```

Run: `dotnet test tests/Coo.Mobile.Presentation.Tests --filter "FullyQualifiedName~LicencesTests"`
Expected: FAIL: `All_EveryMobilePackageReference_HasALicenceEntry` lists the three new package IDs.

- [ ] **Step 2: The licence entries and texts**

In `Licences.All`, after the SQLite line:

```csharp
        new("AndroidX Credential Manager", "Apache License 2.0 (bindings: MIT)", "androidx-credentials-apache2.txt",
            ["Xamarin.AndroidX.Credentials", "Xamarin.AndroidX.Credentials.PlayServicesAuth"]),
        new("Google Identity Services (Sign in with Google)", "Android Software Development Kit License (bindings: MIT)", "google-identity-notice.txt",
            ["Xamarin.GoogleAndroid.Libraries.Identity.GoogleId"]),
```

Create the texts from the restored packages (they are in the NuGet cache once the mobile projects restore):

```sh
P=~/.nuget/packages
D=src/Mobile/Coo.Mobile/Resources/Raw/licences
cat $P/xamarin.androidx.credentials/1.5.0/LICENSE.md $P/xamarin.androidx.credentials/1.5.0/THIRD-PARTY-NOTICES.txt \
    $P/xamarin.androidx.credentials.playservicesauth/1.5.0/THIRD-PARTY-NOTICES.txt > $D/androidx-credentials-apache2.txt
cat $P/xamarin.googleandroid.libraries.identity.googleid/1.1.0.8/LICENSE.md \
    $P/xamarin.googleandroid.libraries.identity.googleid/1.1.0.8/THIRD-PARTY-NOTICES.txt > $D/google-identity-notice.txt
```

(If a package folder is missing, run `dotnet restore src/Mobile/Coo.Mobile/Coo.Mobile.csproj` with the MAUI SDK first,
Step 6's environment.)

Run: `dotnet test tests/Coo.Mobile.Presentation.Tests --filter "FullyQualifiedName~LicencesTests"`
Expected: PASS (both tests).

- [ ] **Step 3: The Platform adapters**

`src/Mobile/Coo.Mobile.Platform/Auth/CredentialManagerSignInProvider.cs`:

```csharp
using Android.App;
using Android.OS;
using Android.Runtime;
using AndroidX.Core.Content;
using AndroidX.Credentials;
using Coo.Mobile.Application.Auth;
using Xamarin.GoogleAndroid.Libraries.Identity.GoogleId;

namespace Coo.Mobile.Platform.Auth;

/// <summary>
/// Android's own Google account sheet through Credential Manager (ADR 0003 §2), as Spec 010a proved it in a trimmed
/// Release build on the Pixel 10: a Google ID token whose audience is Coo's Web client and whose nonce is the server's.
/// </summary>
internal sealed class CredentialManagerSignInProvider(Func<Activity?> currentActivity, string webClientId) : ISignInProvider
{
    private const string CancelledError = "androidx.credentials.exceptions.GetCredentialCancellationException";

    public async Task<string?> GetIdTokenAsync(string nonce, CancellationToken cancellationToken)
    {
        var activity = currentActivity() ?? throw new SignInUnavailableException("There is no activity to show the account sheet on.");
        var option = new GetGoogleIdOption.Builder()
            .SetServerClientId(webClientId)
            .SetFilterByAuthorizedAccounts(false)
            .SetNonce(nonce)
            .Build();
        var request = new GetCredentialRequest.Builder().AddCredentialOption(option).Build();
        var callback = new Callback();
        using var signal = new CancellationSignal();
        using var registration = cancellationToken.Register(signal.Cancel);

        // Context.MainExecutor needs API 28; Coo's minimum is 26, hence ContextCompat (Spec 010a §5).
        CredentialManager.Create(activity).GetCredentialAsync(activity, request, signal, ContextCompat.GetMainExecutor(activity)!, callback);
        return await callback.Result;
    }

    private sealed class Callback : Java.Lang.Object, ICredentialManagerCallback
    {
        private readonly TaskCompletionSource<string?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<string?> Result => _result.Task;

        public void OnResult(Java.Lang.Object? result)
        {
            var credential = result?.JavaCast<GetCredentialResponse>()?.Credential;
            if (credential is not null && credential.Type == GoogleIdTokenCredential.TypeGoogleIdTokenCredential)
                _result.TrySetResult(GoogleIdTokenCredential.CreateFrom(credential.Data).IdToken);
            else
                _result.TrySetException(new SignInUnavailableException("The account sheet returned no Google ID token."));
        }

        // The error is a Java exception object: read through its class name, not cast to Throwable (Spec 010a §5).
        public void OnError(Java.Lang.Object? error)
        {
            if (error?.Class.Name == CancelledError)
                _result.TrySetResult(null);
            else
                _result.TrySetException(new SignInUnavailableException(error?.Class.Name + ": " + error));
        }
    }
}
```

`src/Mobile/Coo.Mobile.Platform/Auth/AndroidDeviceName.cs`:

```csharp
using Coo.Mobile.Application.Auth;

namespace Coo.Mobile.Platform.Auth;

/// <summary>The model, as Settings → About phone shows it: "Pixel 10". Shown in the account's device list.</summary>
internal sealed class AndroidDeviceName : IDeviceName
{
    public string Name => string.IsNullOrWhiteSpace(Android.OS.Build.Model) ? "Android" : Android.OS.Build.Model;
}
```

`src/Mobile/Coo.Mobile.Platform/ServiceCollectionExtensions.cs`:

```csharp
using Android.App;
using Coo.Mobile.Application.Auth;
using Coo.Mobile.Application.Preferences;
using Coo.Mobile.Platform.Auth;
using Microsoft.Extensions.DependencyInjection;

namespace Coo.Mobile.Platform;

/// <summary>The one public entry point of this project (ADR 0001 §2).</summary>
public static class ServiceCollectionExtensions
{
    /// <param name="currentActivity">The activity the account sheet opens over; the host passes MAUI's current activity.</param>
    /// <param name="googleWebClientId">Coo's Web client ID: the audience of the Google ID token (Spec 010 §6).</param>
    public static IServiceCollection AddMobilePlatform(this IServiceCollection services, Func<Activity?> currentActivity, string googleWebClientId)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(currentActivity);
        ArgumentException.ThrowIfNullOrWhiteSpace(googleWebClientId);
        services.AddSingleton<IClockFormat, AndroidClockFormat>();
        services.AddSingleton<IDeviceName, AndroidDeviceName>();
        services.AddSingleton<ISignInProvider>(new CredentialManagerSignInProvider(currentActivity, googleWebClientId));
        return services;
    }
}
```

If a binding member name differs from Spec 010a §5's recorded surface (`GetCredentialAsync`,
`GoogleIdTokenCredential.TypeGoogleIdTokenCredential`, `CreateFrom`), check the binding with the IDE's "go to
definition" on `Xamarin.AndroidX.Credentials` 1.5.0 and use its name; the spike ran exactly these packages.

- [ ] **Step 4: The host's pieces**

`src/Mobile/Coo.Mobile/BuildConstants.cs`:

```csharp
namespace Coo.Mobile;

/// <summary>
/// Built into every APK (Spec 010 §6). Neither is a secret: a client ID identifies the app to Google
/// (docs/google-sign-in-setup.md §6), and the API is public.
/// </summary>
internal static class BuildConstants
{
    /// <summary>The Web client of Google Cloud project coo-app-510411: the audience the server checks.</summary>
    public const string GoogleWebClientId = "282211746968-l54aogbkec1gf1g0rp0n7bk2mhbg2c7r.apps.googleusercontent.com";

    public static readonly Uri ApiBaseAddress = new("https://api.coo.lechdigital.nl/");
}
```

`src/Mobile/Coo.Mobile/Auth/MauiSecretStore.cs`:

```csharp
using Coo.Mobile.Application.Auth;

namespace Coo.Mobile.Auth;

/// <summary>
/// MAUI <c>SecureStorage</c>, backed by the Android Keystore (Spec 010 §3.3). It lives in the host because
/// <c>Coo.Mobile.Platform</c> has no MAUI reference (the deviation of Spec 009 §6, recorded in Spec 010 §10). A Keystore
/// that can no longer decrypt (restored to another phone, reset) reads as "signed out", never as a crash.
/// </summary>
internal sealed class MauiSecretStore : ISecretStore
{
    public async Task<string?> GetAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            return await SecureStorage.Default.GetAsync(key);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            SecureStorage.Default.Remove(key);
            return null;
        }
    }

    public Task SetAsync(string key, string value, CancellationToken cancellationToken) => SecureStorage.Default.SetAsync(key, value);

    public Task RemoveAsync(string key, CancellationToken cancellationToken)
    {
        SecureStorage.Default.Remove(key);
        return Task.CompletedTask;
    }
}
```

In `MauiProgram.cs`, replace the infrastructure and platform lines with:

```csharp
        builder.Services.AddMobileInfrastructure(
            databasePath: Path.Combine(FileSystem.AppDataDirectory, "coo.db"),
            appVersion: $"{AppInfo.Current.VersionString}+{AppInfo.Current.BuildString}",
            apiBaseAddress: BuildConstants.ApiBaseAddress);
        builder.Services.AddMobilePlatform(() => Microsoft.Maui.ApplicationModel.Platform.CurrentActivity, BuildConstants.GoogleWebClientId);
        builder.Services.AddSingleton<Coo.Mobile.Application.Auth.ISecretStore, Auth.MauiSecretStore>();
```

(Inside the `Coo.Mobile` namespace, `Platform` and `Application` name projects: spell out the MAUI types, BUILD.md.)

- [ ] **Step 5: Run the tests**

Run: `dotnet test Coo.Tests.slnf`
Expected: PASS everywhere (the host and Platform are not in the filter; the licences tests read their csproj files).

- [ ] **Step 6: Build the app with the MAUI SDK**

```sh
export DOTNET_ROOT=$HOME/dotnet-maui
export PATH=$HOME/dotnet-maui:$PATH
export JAVA_HOME=$HOME/Library/Java/JavaVirtualMachines/jdk-17.0.20+8/Contents/Home
dotnet restore src/Mobile/Coo.Mobile/Coo.Mobile.csproj
dotnet build src/Mobile/Coo.Mobile/Coo.Mobile.csproj -c Debug -f net10.0-android --no-restore \
  -p:UseDefaultPublishRuntimeIdentifier=false -p:EmbedAssembliesIntoApk=true \
  -p:AndroidSdkDirectory=$HOME/android-sdk -p:JavaSdkDirectory=$JAVA_HOME
```

Expected: restore without `NU1608` or `NU1605`; `Build succeeded`, `0 Warning(s)`.

- [ ] **Step 7: Commit**

```bash
git add Directory.Packages.props .github/dependabot.yml src/Mobile/Coo.Mobile.Platform src/Mobile/Coo.Mobile \
  src/Mobile/Coo.Mobile.Presentation/Settings/Licences.cs
git commit -m "Credential Manager sign-in provider, device name, SecureStorage secret store, wiring and licences (Spec 010 §6, Spec 010a)"
```

---

### Task 14: The Account section in Settings, the start-up load, and version 0.10.0

**Files:**
- Modify: `src/Mobile/Coo.Mobile/Pages/SettingsPage.cs`
- Modify: `src/Mobile/Coo.Mobile/StartupPage.cs`
- Modify: `src/Mobile/Coo.Mobile/Coo.Mobile.csproj`

**Interfaces:**
- Consumes: Task 12's `AccountViewModel`, `ConfirmPrompt`; Task 10's `Account`.
- Produces: Settings opens with an Account section (signed out: one row and its line; signed in: name and email, the
  status line, Sign out, Delete account, each confirmed with Android's dialog). The app is 0.10.0 (13).

- [ ] **Step 1: Load the account at start-up**

In `StartupPage.OnAppearing`, right after `await _household.ExecuteAsync(CancellationToken.None);`:

```csharp
        // Who is signed in (Spec 010 §3.3), read once from SecureStorage. Signed in or not, Today opens the same way.
        await _services.GetRequiredService<Coo.Mobile.Application.Auth.Account>().LoadAsync(CancellationToken.None);
```

- [ ] **Step 2: The section**

In `SettingsPage.cs`: take `AccountViewModel account` in the constructor (after `SettingsViewModel model`), keep it in
`private readonly AccountViewModel _account;`, and update the class summary to "The Settings tab (Spec 009 §2–§3,
Spec 010 §2): account, baby, what you log, units, appearance, language, about."

In `OnAppearing`, after `_language.Changed += OnLanguageChanged;`:

```csharp
        _account.PropertyChanged += OnAccountChanged;
```

and at its end:

```csharp
        // The latest name and email, without holding up the page; offline nothing changes (Spec 010 deliverable 5).
        Dispatcher.Dispatch(async () => await _account.RefreshAsync());
```

In `OnDisappearing`: `_account.PropertyChanged -= OnAccountChanged;`.

Add:

```csharp
    // The account can change from a background refresh (a refused session), so everything goes through the dispatcher.
    private void OnAccountChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => Dispatcher.Dispatch(() =>
    {
        if (e.PropertyName == nameof(AccountViewModel.Toast))
        {
            if (!string.IsNullOrEmpty(_account.Toast))
                _undo.Say(_account.Toast);
            _account.Toast = "";
            return;
        }

        QueueRender();
    });

    /// <summary>The Account section, first in Settings (Spec 010 §2).</summary>
    private IEnumerable<View> AccountViews()
    {
        yield return Header("Account.Title");
        if (!_account.IsSignedIn)
        {
            yield return Row(
                new VerticalStackLayout { Spacing = 2, Children = { Ui.Text(Strings.Get("Account.SignIn"), "BodyStrong"), Ui.Text(_account.StatusLine, "Meta", "Ink2") } },
                () => _account.SignInCommand.ExecuteAsync(null));
            yield break;
        }

        yield return new Card(new VerticalStackLayout { Spacing = 2, Children = { Ui.Text(_account.Name, "BodyStrong"), Ui.Text(_account.Email, "Meta", "Ink2") } });
        if (_account.StatusLine.Length > 0)
            yield return Ui.Text(_account.StatusLine, "Meta", "Ink2");
        yield return Row(Ui.Text(Strings.Get("Account.SignOut"), "Body"), ConfirmSignOutAsync);
        yield return Row(Ui.Text(Strings.Get("Account.Delete"), "Body"), ConfirmDeleteAsync);
    }

    private async Task ConfirmSignOutAsync()
    {
        var prompt = _account.SignOutPrompt;
        if (await DisplayAlertAsync(prompt.Title, prompt.Message, prompt.Accept, prompt.Cancel))
            await _account.SignOutCommand.ExecuteAsync(null);
    }

    private async Task ConfirmDeleteAsync()
    {
        var prompt = await _account.DeletePromptAsync();
        if (await DisplayAlertAsync(prompt.Title, prompt.Message, prompt.Accept, prompt.Cancel))
            await _account.DeleteAccountCommand.ExecuteAsync(null);
    }
```

In `Render()`, right after the title line:

```csharp
        foreach (var view in AccountViews())
            stack.Add(view);
```

- [ ] **Step 3: The version**

In `Coo.Mobile.csproj`: `<ApplicationDisplayVersion>0.10.0</ApplicationDisplayVersion>`,
`<ApplicationVersion>13</ApplicationVersion>`, and the comment above `<Version>` to "0.10.0+<commit>".

- [ ] **Step 4: Build, install, look**

Build the Debug APK as in Task 13 Step 6, then:

```sh
~/android-sdk/platform-tools/adb install -r src/Mobile/Coo.Mobile/bin/Debug/net10.0-android/com.lechdigital.coo-Signed.apk
~/android-sdk/platform-tools/adb shell am start -n com.lechdigital.coo/crc64f635aa2239ac8606.MainActivity
```

Open Settings (tap the tab) and take a screenshot:
`~/android-sdk/platform-tools/adb exec-out screencap -p > /tmp/coo-settings.png`, then read it.
Expected: "Account" first, the row "Sign in with Google" with "Keep your log safe and share it with family." under it;
About shows "Coo 0.10.0 (…)"; Privacy policy opens `lechdigital.nl/projects/coo/privacy` in the browser.
`adb logcat -d | grep -i -E "exception|crash" | grep -i coo` shows nothing new.

- [ ] **Step 5: Commit**

```bash
git add src/Mobile/Coo.Mobile
git commit -m "Settings: the Account section with sign-in, sign-out and delete, confirmed; account loaded at start-up; 0.10.0 (Spec 010 §2)"
```

---

### Task 15: A debug-screen entry that works on Android, and the account on the debug screen

**Files:**
- Create: `src/Mobile/Coo.Mobile.Presentation/Today/TapBurst.cs`
- Modify: `src/Mobile/Coo.Mobile/Pages/SectionViews.cs` (`AttachLongPress`)
- Modify: `src/Mobile/Coo.Mobile/DebugScreen/DebugPage.cs`
- Test: `tests/Coo.Mobile.Presentation.Tests/TapBurstTests.cs`

**Interfaces:**
- Consumes: Task 10's `Account`, `ISessionStore`, `IAccountApi`, exceptions; Task 13's `BuildConstants`.
- Produces: `public sealed class TapBurst(TimeProvider time)` with `const int Taps = 3`, `static readonly TimeSpan Window`
  (2 s) and `bool Tap()`. The debug screen opens after three quick taps on the "coo" wordmark (day) or the night title,
  and shows the account: `/api/me`, "Keep token", "Old token → /api/me".

- [ ] **Step 1: Confirm the cause on the phone** (systematic debugging; nothing here is committed)

Hypothesis (from MAUI's Android gesture code): a `TapGestureRecognizer` on Android is told only about single and double
taps, so `NumberOfTapsRequired = 3` never fires, whoever taps. Test it: in `AttachLongPress`, change `3` to `2`
temporarily, build and install the Debug APK (Task 13 Step 6, then `adb install -r …`), and double-tap the wordmark.
Find its position with
`~/android-sdk/platform-tools/adb shell uiautomator dump /sdcard/ui.xml && ~/android-sdk/platform-tools/adb shell cat /sdcard/ui.xml | grep -o 'text="coo"[^>]*bounds="[^"]*"'`
and tap it twice with `adb shell "input tap X Y; input tap X Y"`.
Expected: the debug screen opens with 2 and did not with 3. If it does **not** open with 2 either, the cause is
elsewhere (the label's gesture area, a parent swallowing taps): stop and use superpowers:systematic-debugging before
going on. Revert the change.

- [ ] **Step 2: Write the failing tests**

`tests/Coo.Mobile.Presentation.Tests/TapBurstTests.cs`:

```csharp
using Coo.Mobile.Presentation.Today;
using Microsoft.Extensions.Time.Testing;

namespace Coo.Mobile.Presentation.Tests;

/// <summary>The debug entry (Spec 006 Q5): three taps within two seconds, counted from single taps.</summary>
public sealed class TapBurstTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Tap_ThreeWithinTwoSeconds_FiresOnTheThird()
    {
        // Arrange
        var burst = new TapBurst(_time);

        // Act
        var first = burst.Tap();
        _time.Advance(TimeSpan.FromMilliseconds(600));
        var second = burst.Tap();
        _time.Advance(TimeSpan.FromMilliseconds(600));
        var third = burst.Tap();

        // Assert
        new[] { first, second, third }.Should().Equal(false, false, true);
    }

    [Fact]
    public void Tap_ThreeSpreadOverMoreThanTwoSeconds_DoesNotFire()
    {
        // Arrange: an ordinary tap now and then on the wordmark must never open it.
        var burst = new TapBurst(_time);

        // Act
        burst.Tap();
        _time.Advance(TimeSpan.FromMilliseconds(1500));
        burst.Tap();
        _time.Advance(TimeSpan.FromMilliseconds(1500));
        var third = burst.Tap();

        // Assert
        third.Should().BeFalse();
    }

    [Fact]
    public void Tap_AfterFiring_CountsAgainFromZero()
    {
        // Arrange
        var burst = new TapBurst(_time);
        burst.Tap();
        burst.Tap();
        burst.Tap();

        // Act
        var fourth = burst.Tap();
        var fifth = burst.Tap();

        // Assert
        fourth.Should().BeFalse();
        fifth.Should().BeFalse();
    }
}
```

Run: `dotnet test tests/Coo.Mobile.Presentation.Tests --filter "FullyQualifiedName~TapBurstTests"`
Expected: build error, `TapBurst` does not exist.

- [ ] **Step 3: Implement**

`src/Mobile/Coo.Mobile.Presentation/Today/TapBurst.cs`:

```csharp
namespace Coo.Mobile.Presentation.Today;

/// <summary>
/// Three quick taps, the stand-in for a long press that opens the debug screen (Spec 006 Q5). On Android, MAUI tells a
/// TapGestureRecognizer only about single and double taps, so <c>NumberOfTapsRequired = 3</c> never fired (found in
/// Spec 010a on the Pixel 10). Single taps are counted instead. Two seconds leaves room for <c>adb shell input tap</c>.
/// </summary>
public sealed class TapBurst(TimeProvider time)
{
    public const int Taps = 3;
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(2);

    private readonly Queue<DateTimeOffset> _recent = new();

    /// <summary>True on the tap that completes three within the window; counting then starts again.</summary>
    public bool Tap()
    {
        var now = time.GetUtcNow();
        _recent.Enqueue(now);
        while (now - _recent.Peek() > Window)
            _recent.Dequeue();
        if (_recent.Count < Taps)
            return false;
        _recent.Clear();
        return true;
    }
}
```

In `SectionViews.cs`, replace the body of `AttachLongPress`:

```csharp
    private static void AttachLongPress(View view, Action? action)
    {
#if DEBUG_SCREEN
        if (action is null)
            return;
        // Three quick taps open the debug screen in debug builds (Spec 006 Q5), counted from single taps: on Android MAUI
        // never reports a third tap to NumberOfTapsRequired = 3 (Spec 010a §5, the Pixel 10).
        var burst = new TapBurst(TimeProvider.System);
        view.GestureRecognizers.Add(new TapGestureRecognizer
        {
            Command = new Command(() =>
            {
                if (burst.Tap())
                    action();
            }),
        });
#endif
    }
```

Run the tests: PASS (3).

- [ ] **Step 4: The account on the debug screen**

In `DebugPage.cs`: add `using Coo.Mobile.Application.Auth;`; constructor parameters `IAccountApi accountApi,
ISessionStore sessions, Account account` (before `TimeProvider time`), kept in fields `_accountApi`, `_sessions`,
`_account`; a field `private string? _keptToken;` and a label `private readonly Label _accountLine = new() { FontSize = 13 };`.
Add to the page's children, after the `Style gallery` row:

```csharp
                    new Label { Text = "Account (Spec 010 §8)", FontSize = 17, Margin = new Thickness(0, 12, 0, 0) },
                    _accountLine,
                    Row(Button("Account (/api/me)", ShowServerAccountAsync),
                        Button("Keep token", KeepTokenAsync),
                        Button("Old token → /api/me", TryOldTokenAsync)),
```

At the start of `RefreshAsync()`:

```csharp
        _accountLine.Text = $"{_account.State} · this phone's household {_household}";
```

and the three handlers:

```csharp
    /// <summary>Deliverables 2 and 3: the server's view of this account.</summary>
    private async Task ShowServerAccountAsync()
    {
        try
        {
            var me = await _accountApi.GetMeAsync(CancellationToken.None);
            var devices = string.Join("\n", me.Devices.Select(d =>
                $"· {d.Name}, last used {d.LastUsedAt.ToLocalTime():yyyy-MM-dd HH:mm}{(d.IsThisDevice ? " (this phone)" : "")}"));
            _status.Text = $"{me.Profile.Email}\nhousehold {me.Household} as {me.Role}\nthis phone's household {_household}\n{devices}";
        }
        catch (Exception e) when (e is AuthUnavailableException or AuthRejectedException)
        {
            _status.Text = $"/api/me: {e.GetType().Name} {e.Message}";
        }
    }

    /// <summary>Deliverable 6, part 1: remember the current access token before signing out.</summary>
    private async Task KeepTokenAsync()
    {
        _keptToken = (await _sessions.GetAsync(CancellationToken.None))?.AccessToken;
        _status.Text = _keptToken is null ? "Signed out: there is no token." : "Token kept. Sign out in Settings, then try it.";
    }

    /// <summary>Deliverable 6, part 2: the kept token after sign-out must get 401.</summary>
    private async Task TryOldTokenAsync()
    {
        if (_keptToken is null)
        {
            _status.Text = "Keep a token first.";
            return;
        }

        using var http = new HttpClient { BaseAddress = BuildConstants.ApiBaseAddress };
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("api/me", UriKind.Relative));
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _keptToken);
        try
        {
            using var response = await http.SendAsync(request);
            _status.Text = $"Old token → {(int)response.StatusCode} {response.StatusCode}";
        }
        catch (HttpRequestException e)
        {
            _status.Text = "Old token: " + e.Message;
        }
    }
```

Update the page summary's last sentence to "Kept for the on-phone checks (Spec 010 §8)." (it says "Deleted when the
Today screen arrives", which is no longer true).

- [ ] **Step 5: Verify on the phone**

Build and install the Debug APK (Task 13 Step 6, `adb install -r …`). Open Today, find the wordmark (Step 1's
`uiautomator` line) and tap it three times: `adb shell "input tap X Y; input tap X Y; input tap X Y"`.
Expected: the debug screen opens, with the "Account" block showing `SignedOut { SessionExpired = False }`. Ask the
owner to try it by hand too (three quick taps): it opens. Also in night colours (the moon on Today) on the night title.

- [ ] **Step 6: Commit**

```bash
git add src/Mobile/Coo.Mobile.Presentation/Today/TapBurst.cs tests/Coo.Mobile.Presentation.Tests/TapBurstTests.cs \
  src/Mobile/Coo.Mobile/Pages/SectionViews.cs src/Mobile/Coo.Mobile/DebugScreen/DebugPage.cs
git commit -m "Fix: the debug screen opens with three taps on Android (single taps counted); the account on the debug screen (Spec 010a §5, Spec 010 §8)"
```

---

### Task 16: On the phones, the docs, push and CI

**Files:**
- Modify: `docs/specs/010-sign-in-and-sessions/spec.md` (new §10 "Built and verified")
- Modify: `docs/ROADMAP.md` (row 10: Done), `docs/HANDOVER-2026-09-30.md` (Resume here), `BUILD.md` (lessons),
  `docs/specs/009-settings/spec.md` (§3.6: the privacy link moved, Spec 010 Q4)

**Interfaces:**
- Consumes: everything above; production at 0.10.0 (Task 9).

- [ ] **Step 1: Build the trimmed Release with the debug screen and install it** (BUILD.md, "A trimmed Release build
  with the debug screen"; signed with the debug key, whose SHA-1 is registered with the Android client)

```sh
dotnet build src/Mobile/Coo.Mobile/Coo.Mobile.csproj -c Release -f net10.0-android --no-restore \
  -p:UseDefaultPublishRuntimeIdentifier=false -p:AndroidPackageFormat=apk -p:CooDebugScreen=true \
  -p:AndroidSdkDirectory=$HOME/android-sdk -p:JavaSdkDirectory=$JAVA_HOME
~/android-sdk/platform-tools/adb install -r src/Mobile/Coo.Mobile/bin/Release/net10.0-android/com.lechdigital.coo-Signed.apk
```

Expected: no trim (IL) warnings; the app starts.

- [ ] **Step 2: Walk the deliverables with the owner** (spec §8). **The owner's hands** are needed wherever Google's
  sheet asks for an account, for the second phone, for the 20-minute wait and for airplane mode. Record each result.

| # | Who | How |
|---|---|---|
| 1 | Owner picks the account | Settings → Sign in with Google → pick the account: the section shows the name and email |
| 2 | Agent (adb) or owner | Debug screen (three taps on "coo") → "Account (/api/me)": the household equals "this phone's household", "as owner" |
| 3 | Owner, on his girlfriend's phone | Install the same APK there (`adb -s <her serial> install -r …`, USB debugging on), sign in with **his** account: Settings says "Signed in. Your entries on this phone join your family's log when sharing arrives."; her Today is unchanged; her debug screen's "/api/me" lists two devices and his household |
| 4 | Owner waits | Leave the app 20 minutes, come back, open Settings: still signed in, no sheet; "/api/me" on the debug screen answers (the token was refreshed) |
| 5 | Owner | Airplane mode: log a diaper on Today; Settings still shows the account (no error) |
| 6 | Agent or owner | Debug screen "Keep token"; Settings → Sign out → confirm: the signed-out state, Today unchanged; debug screen "Old token → /api/me": `401 Unauthorized` |
| 7 | Owner | Sign in again (it is "existing"), then Settings → Delete account: the confirmation names the baby; confirm: signed out, Today unchanged. Then the owner runs `! ssh coldstart-prod "docker exec coo-postgres psql -U coo -d coo -tAc 'SELECT count(*) FROM \"AspNetUsers\"' -tAc 'SELECT count(*) FROM memberships'"`: `0` and `0` (if the girlfriend's phone is still signed in with the same account, it now shows "Sign in again to keep sharing." at its next refresh) |
| 8 | Agent | `curl -s -o /dev/null -w "%{http_code}\n" https://lechdigital.nl/projects/coo/delete-account` → `200` (the website repository; if it is not live yet, record it as the owner's open item: it does not block this increment's code) |
| 9 | Agent | `curl -s https://api.coo.lechdigital.nl/health` → `0.10.0+…`; Task 9 Step 7's `grep` showed both `.env` names |
| 10 | Agent | Step 5 below |

Every finding becomes a fix with a test where one is possible, in its own commit ("Fix: … (owner, Pixel 10)"), as in
Spec 009.

- [ ] **Step 3: The docs**

- Spec 010: a new **§10 "Built and verified"**, headed with the date of the phone check and "Pixel 10": the deliverables table with results; the
  rulings from this plan's "Rulings on what the spec leaves open"; the two deviations from §6 (Identity's EF types in
  `Persistence`; the session codec in Infrastructure behind `ISecretStore`, `SecureStorage` in the host); the findings
  and fixes.
- `docs/ROADMAP.md` row 10 → "Done".
- `docs/HANDOVER-2026-09-30.md` "Resume here": Increment 10 done (0.10.0 deployed); next is Increment 11, Sync
  (Spec 011 to draft); production now needs the two `AUTH_*` values in `/opt/coo/.env` (DEPLOY.md).
- `BUILD.md`, "MAUI lessons": "**`TapGestureRecognizer.NumberOfTapsRequired = 3` never fires on Android**: MAUI reports
  only single and double taps there. Count single taps (`TapBurst`)." and "**The Credential Manager bindings are pinned**
  to 1.5.0 / 1.5.0 / 1.1.0.8 (Spec 010a §5); newer ones fail restore with MAUI 10.0.0."
- Spec 009 §3.6: after the privacy line, "(Moved to https://lechdigital.nl/projects/coo/privacy by Spec 010, Q4.)"

- [ ] **Step 4: The full check**

```sh
dotnet test Coo.Tests.slnf
TZ=UTC dotnet test Coo.Tests.slnf
dotnet format Coo.Tests.slnf --verify-no-changes
```

Expected: all PASS twice; no format changes.

- [ ] **Step 5: Commit, push, and watch CI**

```bash
git add docs BUILD.md
git commit -m "Increment 10: sign-in and sessions, verified on two phones; deployed as 0.10.0 (Spec 010)"
gh api user --jq .login            # must print adziusmaster (the repo pushes over HTTPS through gh)
git push
gh run watch
```

Expected: `adziusmaster`; the push succeeds; CI "Build, format and test" green, including the API image build.
