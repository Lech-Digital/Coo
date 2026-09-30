# Spec 002 — Solution skeleton

Date: 2026-09-30

Status: **Draft — for review**

Depends on: ADR 0001 (layout and dependency rules), ADR 0010 (the `Coo.Codecs` project), and the
sibling apps' `BUILD.md` (the verified toolchain).

Delivered by: **Increment 1** (`docs/ROADMAP.md`).

## 1. Purpose

This spec creates an empty solution that already **enforces** the architecture:

- every project from ADR 0001, plus `Coo.Codecs` from ADR 0010;
- shared build settings, and central package management;
- compile-time guards: warnings as errors, and banned APIs;
- architecture tests;
- CI;
- a MAUI host that installs and opens on a phone, and an API host that answers a health check.

There is no domain code yet. That is Increment 2, which builds Spec 001.

## 2. Repository layout

```
CooApp/
├── Coo.slnx                     # every project, including the MAUI host and Platform
├── Coo.Tests.slnf               # every project except Coo.Mobile and Coo.Mobile.Platform
├── global.json
├── Directory.Build.props
├── Directory.Packages.props
├── NuGet.config
├── .editorconfig
├── BannedSymbols.txt            # shared banned-API list (§5)
├── BUILD.md                     # the verified build recipe (§8)
├── .github/workflows/ci.yml
├── src/
│   ├── Shared/
│   │   ├── Coo.Domain/
│   │   ├── Coo.Contracts/
│   │   └── Coo.Codecs/          # ADR 0010 §7
│   ├── Backend/
│   │   ├── Coo.Api.Application/
│   │   ├── Coo.Api.Infrastructure/
│   │   └── Coo.Api/
│   └── Mobile/
│       ├── Coo.Mobile.Application/
│       ├── Coo.Mobile.Infrastructure/
│       ├── Coo.Mobile.Platform/
│       ├── Coo.Mobile.Presentation/
│       └── Coo.Mobile/
└── tests/
    ├── Coo.Architecture.Tests/
    └── Coo.Domain.Tests/
```

**Test projects are created when their first test is written**, not all at once. Increment 1 needs
only the two above. The others in ADR 0001 §1 (and `Coo.Codecs.Tests`) arrive with the increments
that give them something to test. An empty test project would only prove that it compiles.

**Source projects are all created now**, even empty ones. The architecture tests then guard the
whole dependency graph from the first commit, and no later increment has to argue about where a
project goes.

`wearables/` and `deploy/` are created by the increments that need them (the watch, and the first
deployment).

## 3. Projects

| Project | SDK / TFM | References | Packages in Increment 1 |
|---|---|---|---|
| `Coo.Domain` | `Microsoft.NET.Sdk` / `net10.0` | — | — |
| `Coo.Contracts` | `net10.0` | — | — |
| `Coo.Codecs` | `net10.0` | Domain, Contracts | — |
| `Coo.Api.Application` | `net10.0` | Domain, Contracts, Codecs | — |
| `Coo.Api.Infrastructure` | `net10.0` | Api.Application | — |
| `Coo.Api` | `Microsoft.NET.Sdk.Web` / `net10.0` | Api.Application, Api.Infrastructure | — |
| `Coo.Mobile.Application` | `net10.0` | Domain, Contracts, Codecs | — |
| `Coo.Mobile.Infrastructure` | `net10.0` | Mobile.Application | — |
| `Coo.Mobile.Platform` | `net10.0-android` | Mobile.Application | — |
| `Coo.Mobile.Presentation` | `net10.0` | Mobile.Application | — |
| `Coo.Mobile` | MAUI / `net10.0-android` | Presentation, Infrastructure, Platform | `Microsoft.Maui.Controls` |

- **Packages are added only when code uses them**, for example EF Core in the persistence increment,
  and CommunityToolkit.Mvvm with the first view model. A package that nothing uses is only an
  upgrade chore.
- Each Infrastructure and Platform project exposes one public `AddXxx(this IServiceCollection)`
  (ADR 0001 §2). In Increment 1 it is an empty method, and the host calls it. That needs
  `Microsoft.Extensions.DependencyInjection.Abstractions`, the one package these projects take now.
- Root namespaces equal the project names.
- `Coo.Domain` declares the `InternalsVisibleTo` list from Spec 001 §3.5, in its csproj.

### 3.1 The MAUI host

- `ApplicationId` is `com.lechdigital.coo`, `ApplicationTitle` is `Coo`, `ApplicationDisplayVersion`
  is `0.1.0` and `ApplicationVersion` is `1`.
- It single-targets `net10.0-android`, never multi-targets (ADR 0001 §3), with
  `MauiXamlInflator=SourceGen`, as in the sibling apps.
- **`SupportedOSPlatformVersion` is 26** (Android 8.0). The sibling apps use 23. We choose 26
  because notification channels, which ADR 0007 relies on, exist from API 26, so we write no
  pre-26 code paths. Devices below Android 8 are a negligible share of Play installs.
- **One placeholder page.** It shows "Coo", the version, and the build configuration. Its only job
  is to prove that the toolchain produces an app that installs and starts. Styles come with
  Spec 003.
- `MauiProgram` is the composition root. It calls each project's `AddXxx()` and registers
  `TimeProvider.System`, the only place on the mobile side where the real clock is created.

### 3.2 The API host

- A Minimal API with one endpoint: `GET /health` → `200 {"status":"ok","version":"0.1.0"}`.
- It registers `TimeProvider.System`, the only place on the backend where the real clock is created.
- There is no database, no auth and no Docker yet. Those arrive with the sync and deployment
  increments.

## 4. Shared build settings

### 4.1 `global.json`

```json
{ "sdk": { "version": "10.0.203", "rollForward": "latestPatch" } }
```

This pins the band that both the global SDK and `~/dotnet-maui` have (10.0.203). CI uses the same
file through `actions/setup-dotnet` with `global-json-file`.

### 4.2 `Directory.Build.props`

- `Nullable=enable`, `ImplicitUsings=enable`, `LangVersion=latest`.
- `TreatWarningsAsErrors=true`, `AnalysisLevel=latest-recommended`,
  `EnforceCodeStyleInBuild=true`. The build fails on a warning, not just on an error.
- `Deterministic=true`, and `ContinuousIntegrationBuild=true` when `CI=true`.
- The banned-API analyzer (§5) is added to every project, with the root `BannedSymbols.txt`.
- For test projects (detected by the `.Tests` suffix): `IsPackable=false`, the xUnit usings, and a
  relaxed analyzer set for test naming (`CA1707`, underscores in test names).

### 4.3 `Directory.Packages.props`

Central package management (`ManagePackageVersionsCentrally=true`). The starting versions are the
ones verified in the sibling apps:

| Package | Version | Note |
|---|---|---|
| `xunit` | 2.9.3 | |
| `xunit.runner.visualstudio` | 3.1.4 | |
| `Microsoft.NET.Test.Sdk` | 17.14.1 | |
| `NSubstitute` | 6.2.0 | |
| `FluentAssertions` | **7.2.0, pinned below 8** | Version 8 and later are under a commercial licence. The pin is `[7.2.0, 8.0.0)`, with a comment explaining why. |
| `coverlet.collector` | 6.0.4 | |
| `Microsoft.CodeAnalysis.BannedApiAnalyzers` | latest 3.3.x | |
| `NetArchTest.Rules` | latest 1.3.x | For type-level architecture rules |
| `Microsoft.Extensions.DependencyInjection.Abstractions` | 10.0.x | |
| `Microsoft.Maui.Controls` | the version matching the installed workload | Verified in Increment 1. If CPM and `$(MauiVersion)` conflict, the host uses `VersionOverride`, and `BUILD.md` records it. |

### 4.4 `NuGet.config`

`<clear/>` and nuget.org only, copied from the sibling apps, with their comment about the
unreachable corporate feed.

### 4.5 `.editorconfig`

- The standard .NET conventions: file-scoped namespaces, `var` where the type is obvious, and
  `_camelCase` private fields.
- Naming rules as errors for public members.
- CI runs `dotnet format --verify-no-changes` over `Coo.Tests.slnf`.

## 5. Compile-time guards: banned APIs

`BannedSymbols.txt` turns the time and async rules into **build errors**, which is earlier and
cheaper than a test:

| Banned | Why | Allowed in |
|---|---|---|
| `DateTime.Now`, `DateTime.UtcNow`, `DateTime.Today`, `DateTimeOffset.Now`, `DateTimeOffset.UtcNow` | Time is injected (ADR 0001 §2, ADR 0004 §10) | nowhere |
| `TimeProvider.System` | Only the composition roots create the real clock | `Coo.Api`, `Coo.Mobile` (their own banned list omits it) |
| `Guid.NewGuid()` | Record IDs are UUIDv7 (ADR 0002 §1) | nowhere. Use `Guid.CreateVersion7(timeProvider.GetUtcNow())`. |
| `Guid.CreateVersion7()` with no argument | It reads the system clock | nowhere |
| `Task.Result`, `Task.Wait()`, `Task.GetAwaiter().GetResult()` | Async all the way (CS-DATA-03) | nowhere |
| `System.Random` constructors, `Random.Shared` | Makes tests non-deterministic | nowhere in `src/` |

The two hosts use a `BannedSymbols.Hosts.txt` without the `TimeProvider.System` line.

Increment 1's verification includes a deliberate violation (§9), to prove the guard fires.

## 6. Architecture tests (`tests/Coo.Architecture.Tests`)

Two techniques are used, each where it is reliable:

**Project-graph tests** parse every `*.csproj` under `src/` as XML. That is exact and fast, and
does not depend on what the compiler happened to keep in an assembly.

| Test | Rule |
|---|---|
| `ProjectReferences_EveryProject_MatchesAllowedGraph` | Each project's `ProjectReference`s equal the table in §3, exactly: no fewer and no more. A new project must be added to the test's table, so the graph never changes by accident. |
| `PackageReferences_DomainContractsCodecs_ReferenceNoPackage` | ADR 0001 §2, CS-HEX-01. The analyzer is exempt, because it is a build-time asset. |
| `ProjectReferences_Backend_NeverReferenceMobile` and the reverse | ADR 0001 §2 |
| `TargetFramework_OnlyPlatformAndHost_TargetAndroid` | ADR 0001 §3 |
| `TargetFramework_NoProject_MultiTargets` | The iOS restore trap from the sibling apps |
| `TestsSolutionFilter_ContainsEveryProjectExceptAndroidOnes` | `Coo.Tests.slnf` never silently misses a project |

**Type-level tests** use `NetArchTest` over the built assemblies:

| Test | Rule |
|---|---|
| `Domain_Types_DoNotDependOnSystemTextJson` | The Domain never sees JSON (ADR 0010) |
| `Infrastructure_Types_AreInternalExceptServiceCollectionExtensions` | CS-MOD-01 |
| `Domain_InternalsVisibleTo_IsExactlyTheApprovedList` | Spec 001 §3.5 |

Test names follow `MethodName_StateUnderTest_ExpectedBehavior`, with AAA comments, as always.

The ADR 0010 completeness tests (every kind registered in every layer) and the `Rehydrate` rule
are added in **Increment 2**, together with the first kinds they check.

## 7. CI (`.github/workflows/ci.yml`)

Runs on pushes to `main` and on pull requests, on `ubuntu-latest`:

1. `actions/setup-dotnet` with `global-json-file: global.json`;
2. `dotnet restore Coo.Tests.slnf`;
3. `dotnet format Coo.Tests.slnf --verify-no-changes --no-restore`;
4. `dotnet build Coo.Tests.slnf -c Release --no-restore`;
5. `dotnet test Coo.Tests.slnf -c Release --no-build`.

The Android app is not built in CI, as in the sibling apps: the MAUI workload adds minutes to every
run. It is built locally (§8). A manually triggered Android job can be added with the first
release build.

**This needs a GitHub remote.** The workflow file is committed in Increment 1. Creating the
repository (for example a private `adziusmaster/Coo`) and pushing to it is the owner's call
(§11, Q1).

## 8. `BUILD.md`

This is the sibling apps' verified recipe, adapted to Coo:

- **The environment:** `DOTNET_ROOT=~/dotnet-maui`, JDK 17 at its existing path, the Android SDK at
  `~/android-sdk` (API 36 installed).
- **Tests on the global SDK:** `dotnet test Coo.Tests.slnf`. No MAUI workload is needed.
- **The Android Debug build and install:** the `dotnet build -f net10.0-android` line with
  `-p:UseDefaultPublishRuntimeIdentifier=false`, `AndroidSdkDirectory` and `JavaSdkDirectory`,
  then `adb install`.
- **The API:** `dotnet run --project src/Backend/Coo.Api`, and `curl localhost:<port>/health`.
- **The gotchas that are designed out** (`NETSDK1147`, `NETSDK1005`), and why the fix is structural
  here (single-targeting, the solution filter).

Every command in `BUILD.md` is run once as part of Increment 1 before it is written down. The file
records only what was verified.

## 9. Increment 1 — deliverables and how to test them

| # | Deliverable | How you check it |
|---|---|---|
| 1 | The solution builds with zero warnings | `dotnet build Coo.Tests.slnf -c Release` |
| 2 | The architecture tests pass | `dotnet test Coo.Tests.slnf`: all green |
| 3 | The guards actually fire | Three deliberate violations, each shown failing and then reverted: `DateTime.UtcNow` in `Coo.Domain` (a build error), a `ProjectReference` from Domain to Contracts (a test failure), and a `net10.0-ios` target (a test failure) |
| 4 | The API answers | `dotnet run --project src/Backend/Coo.Api`, then `GET /health` returns `200 {"status":"ok"…}` |
| 5 | The app installs and opens | An Android Debug build on your phone or an emulator shows the placeholder page, "Coo 0.1.0 · Debug" |
| 6 | The formatting gate passes | `dotnet format --verify-no-changes` |
| 7 | CI is green | After the remote exists (Q1) |
| 8 | `BUILD.md` exists, with only verified commands | Reading it, and running it |

## 10. Out of scope

Domain code, EF Core, Docker, the database, auth, styles and fonts, localization, Wear OS, and
release signing each arrive with their own increment (`docs/ROADMAP.md`).

## 11. Decisions for the owner

| # | Question | Recommendation |
|---|---|---|
| Q1 | Create a private GitHub repository `adziusmaster/Coo` and push, so that CI runs? | **Yes**, as for PurePrep. It is your account, so I will push only after you say so. |
| Q2 | Minimum Android version 26 (Android 8.0) instead of the siblings' 23 | **26**: it avoids pre-notification-channel code paths |
| Q3 | Create test projects when their first test exists, not all nine now | **Yes** |
| Q4 | FluentAssertions pinned below version 8, for the licence | **Yes**. The alternative is switching to Shouldly or AwesomeAssertions, but your standards name FluentAssertions. |
