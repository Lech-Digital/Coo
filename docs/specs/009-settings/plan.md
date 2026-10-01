# Settings (Increment 9) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or
> superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A third tab, Settings, where the parent edits the baby, hides kinds, picks ml or oz, the theme, quiet hours
and the language, and sees About; a sun or moon on Today switches night and day until the next change. Today and
Log follow every change.

**Architecture:** Domain gains one atomic `BabyProfile.Revise`. Application gains three use cases
(`UpdateBabyProfile`, `ChangeVolumeUnit`, `SetHiddenKinds`) and a per-device language preference. Presentation gains
a `LanguageController` (like `ThemeController`), a pure `LanguageRule`, `KindVisibility`, and the Settings and baby
view models with two small ports (`IAboutInfo`, `IExternalLauncher`). The MAUI host adds the pages, the tab, the
port implementations (MAUI Essentials) and the licence texts.

**Tech Stack:** .NET 10, MAUI (Android), CommunityToolkit.Mvvm, SQLite (`app_state`), xUnit 2.9 + NSubstitute +
FluentAssertions.

**Spec:** `docs/specs/009-settings/spec.md` (accepted 2026-10-01).

## Global Constraints

- Commits authored as `adziusmaster <adzius.lech@gmail.com>` (repo-local config). **No `Co-Authored-By` trailer.**
- Zero warnings (`TreatWarningsAsErrors` via `Directory.Build.props`); the architecture tests stay green.
- No `TimeProvider.System`, `DateTime.Now` or `DateTimeOffset.Now` outside the composition root (`BannedSymbols.txt`).
- No MAUI types in `Coo.Mobile.Application` or `Coo.Mobile.Presentation` (ADR 0001).
- Every user-facing string goes in `eng/strings/strings.py` in all seven languages (en, de, fr, es, it, pl, nl), then
  `python3 eng/strings/generate.py`. **Copy must be true for the public release** (no "only on this phone", no
  "coming later").
- Tests: `MethodName_StateUnderTest_ExpectedBehavior`, strict `// Arrange` / `// Act` / `// Assert`, mock only
  outbound ports, build Domain objects for real.
- Household-wide changes (baby, unit, hidden kinds) save with a **new HLC version** and **their outbox row**, through
  `IHouseholdStore`. Per-device changes (theme, quiet hours, language) go to `app_state` and never to the outbox.
- App version **0.9.0**, versionCode **12**.
- Feedback address `andrzej@lechdigital.nl`; privacy URL `https://coo.lechdigital.nl/privacy`.
- Run all tests with `dotnet test Coo.Tests.slnf` (Docker Desktop running for the PostgreSQL tests).

## Review Focus

1. **Switching back to System after an explicit language** must show Android's language, not the last explicit
   choice (the process culture was overwritten). Pinned in Task 4 (`SetAsync_BackToSystem_UsesTheCapturedSystemCulture`).
2. **Android's own per-app language changing while an explicit Coo language is set** must not override Coo's choice.
   Pinned in Task 4 (`SystemChanged_WithExplicitChoice_KeepsTheChoiceAndRaisesNothing`).
3. **Hiding a kind with a running timer** keeps the timer's card and its Stop button (Q8). Pinned in Task 5
   (`Update_PumpHiddenWhilePumping_KeepsTheRunningCard`).
4. **A household that already hides every known kind** (synced from a newer client that knew more kinds) must still
   be able to show one again; only hiding is refused. Pinned in Task 2
   (`ExecuteAsync_ShowKindWhenAllHidden_Succeeds`).
5. **Saving the baby page twice without changes, or with only spaces around the name,** must not fail on a stale
   version or store the spaces. Pinned in Task 1 (`Revise_NameWithSurroundingSpaces_StoresItTrimmed`) and Task 6
   (`SaveAsync_TwiceInARow_SucceedsBothTimes`).
6. **Tapping the sun twice at 3 AM** must return to night and leave no override behind, rather than storing a
   "night until 07:00" that Auto would have chosen anyway. Pinned in Task 5b (`ToggleAsync_TwiceInAuto_ClearsTheOverride`).

---

## File Structure

| File | Responsibility |
|---|---|
| `src/Shared/Coo.Domain/Households/BabyProfile.cs` (modify) | `Revise`: all profile fields in one write |
| `src/Mobile/Coo.Mobile.Application/Households/Settings.cs` (create) | `BabyProfileChange`, `UpdateBabyProfile`, `ChangeVolumeUnit`, `SetHiddenKinds`, `SettingsErrors` |
| `src/Mobile/Coo.Mobile.Application/Preferences/DevicePreferences.cs` (modify) | `LanguagePreference`; language methods on `IDevicePreferences` |
| `src/Mobile/Coo.Mobile.Infrastructure/Persistence/DevicePreferences.cs` (modify) | `language` in `app_state` |
| `src/Mobile/Coo.Mobile.Presentation/Localization/LanguageRule.cs` (create) | Pure: preference + system culture → culture; the language list |
| `src/Mobile/Coo.Mobile.Presentation/Localization/LanguageController.cs` (create) | Loads, stores and applies the language; raises `Changed` |
| `src/Mobile/Coo.Mobile.Presentation/Localization/Strings.cs` (modify) | `Culture` follows the controller's choice |
| `src/Mobile/Coo.Mobile.Presentation/Today/KindVisibility.cs` (create) | Which kinds a household shows |
| `src/Mobile/Coo.Mobile.Presentation/Today/Sections.cs`, `TodayViewModel.cs`, `Log/LogViewModel.cs` (modify) | Filter tiles, night facts and quick row, totals, "Earlier…" |
| `src/Mobile/Coo.Mobile.Presentation/Theme/AppearanceRule.cs`, `ThemeController.cs` (modify) | The Today switch: an override until the next change |
| `src/Mobile/Coo.Mobile.Presentation/Settings/Ports.cs` (create) | `IAboutInfo`, `IExternalLauncher`, `AppVersionText` |
| `src/Mobile/Coo.Mobile.Presentation/Settings/Licences.cs` (create) | The licences catalogue |
| `src/Mobile/Coo.Mobile.Presentation/Settings/SettingsViewModel.cs` (create) | The Settings tab |
| `src/Mobile/Coo.Mobile.Presentation/Settings/BabyProfileViewModel.cs` (create) | The baby page |
| `src/Mobile/Coo.Mobile/Pages/SettingsPage.cs`, `BabyPage.cs`, `LicencesPage.cs` (create) | The pages |
| `src/Mobile/Coo.Mobile/Settings/MauiAboutInfo.cs`, `MauiExternalLauncher.cs` (create) | Port implementations (MAUI Essentials) |
| `src/Mobile/Coo.Mobile/Resources/Raw/licences/*.txt` (create) | The licence texts |
| `src/Mobile/Coo.Mobile/Resources/Images/tab_settings.svg` (create) | The tab icon |
| `src/Mobile/Coo.Mobile/AppShell.cs`, `App.xaml.cs`, `StartupPage.cs`, `MauiProgram.cs`, `Platforms/Android/MainActivity.cs`, `Coo.Mobile.csproj` (modify) | Tab, language start-up and reload, DI, version |
| `eng/strings/strings.py` (modify) | New strings |

**Deviation from spec §6, recorded in Task 9:** the two port implementations live in the host (`Coo.Mobile`), not
`Coo.Mobile.Platform`. They use MAUI Essentials (`Email`, `Browser`, `Clipboard`, `DeviceInfo`, `AppInfo`), and
`Coo.Mobile.Platform` deliberately has no MAUI reference.

---

### Task 1: `BabyProfile.Revise` (Domain)

**Files:**
- Modify: `src/Shared/Coo.Domain/Households/BabyProfile.cs` (after `SetDueDate`)
- Test: `tests/Coo.Domain.Tests/Households/BabyTests.cs`

**Interfaces:**
- Produces: `public Result Revise(string name, IEnumerable<string> nicknames, DateOnly? birthDate, DateOnly? dueDate, HlcStamp version, DateOnly today)`.
  A born baby must be given a birth date; an expected baby must be given none (it becomes born only through
  `RecordBirth`). On failure nothing changes.

- [ ] **Step 1: Write the failing tests** (append to `BabyTests`)

```csharp
    [Fact]
    public void Revise_AllFields_ChangesThemUnderOneVersion()
    {
        // Arrange
        var baby = BabyProfile.Create(A.Baby, A.Household, "Zosia", [], Today.AddMonths(-2), null, A.Stamp(), Today).Value;
        var version = A.Stamp();

        // Act
        var result = baby.Revise("Zofia", ["Zo", "Mimi"], Today.AddMonths(-2).AddDays(-1), Today.AddMonths(-1), version, Today);

        // Assert
        result.IsSuccess.Should().BeTrue();
        baby.Name.Should().Be("Zofia");
        baby.Nicknames.Should().Equal("Zo", "Mimi");
        baby.BirthDate.Should().Be(Today.AddMonths(-2).AddDays(-1));
        baby.DueDate.Should().Be(Today.AddMonths(-1));
        baby.Version.Should().Be(version);
    }

    [Fact]
    public void Revise_NameOfSpaces_FailsAndChangesNothing()
    {
        // Arrange
        var baby = BabyProfile.Create(A.Baby, A.Household, "Zosia", ["Zo"], Today.AddMonths(-2), null, A.Stamp(), Today).Value;
        var before = baby.Version;

        // Act
        var result = baby.Revise("   ", [], Today.AddMonths(-2), null, A.Stamp(), Today);

        // Assert
        result.Error!.Code.Should().Be(BabyErrors.NameInvalid);
        baby.Name.Should().Be("Zosia");
        baby.Nicknames.Should().Equal("Zo");
        baby.Version.Should().Be(before);
    }

    [Fact]
    public void Revise_BirthDateForExpectedBaby_FailsWithNotBornYet()
    {
        // Arrange
        var baby = BabyProfile.Create(A.Baby, A.Household, "Baby", [], null, Today.AddDays(30), A.Stamp(), Today).Value;

        // Act
        var result = baby.Revise("Baby", [], Today, Today.AddDays(30), A.Stamp(), Today);

        // Assert
        result.Error!.Code.Should().Be(BabyErrors.NotBornYet);
        baby.IsBorn.Should().BeFalse();
    }

    [Fact]
    public void Revise_NameWithSurroundingSpaces_StoresItTrimmed()
    {
        // Arrange
        var baby = BabyProfile.Create(A.Baby, A.Household, "Zosia", [], Today.AddMonths(-2), null, A.Stamp(), Today).Value;

        // Act
        var result = baby.Revise("  Zofia ", [" Zo "], Today.AddMonths(-2), null, A.Stamp(), Today);

        // Assert
        result.IsSuccess.Should().BeTrue();
        baby.Name.Should().Be("Zofia");
        baby.Nicknames.Should().Equal("Zo");
    }

    [Fact]
    public void Revise_StaleVersion_FailsWithStaleVersion()
    {
        // Arrange
        var stamp = A.Stamp();
        var baby = BabyProfile.Create(A.Baby, A.Household, "Zosia", [], Today.AddMonths(-2), null, stamp, Today).Value;

        // Act
        var result = baby.Revise("Zofia", [], Today.AddMonths(-2), null, stamp, Today);

        // Assert
        result.Error!.Code.Should().Be(RecordErrors.StaleVersion);
    }
```

Add `using Coo.Domain.Common;` to the test file if `RecordErrors` is not yet in scope.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Coo.Domain.Tests --filter "FullyQualifiedName~BabyTests.Revise"`
Expected: build error, `'BabyProfile' does not contain a definition for 'Revise'`.

- [ ] **Step 3: Implement** (in `BabyProfile`, after `SetDueDate`)

```csharp
    /// <summary>
    /// Changes the name, nicknames and dates in one write (Spec 009 §3.1). A born baby keeps a birth date; an expected
    /// baby has none, and becomes born only through <see cref="RecordBirth"/>.
    /// </summary>
    public Result Revise(string name, IEnumerable<string> nicknames, DateOnly? birthDate, DateOnly? dueDate, HlcStamp version, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(nicknames);
        var cleanName = CleanName(name);
        var cleanNicknames = nicknames.Select(CleanName).ToList();
        return Apply(
            version,
            () => Result.FirstFailure(
                () => Result.Check(IsBorn == birthDate.HasValue, IsBorn ? BabyErrors.BirthOrDueDateRequired : BabyErrors.NotBornYet),
                () => CheckName(cleanName),
                () => CheckNicknames(cleanName, cleanNicknames),
                () => CheckDates(birthDate, dueDate, today)),
            () =>
            {
                Name = cleanName!;
                Nicknames = cleanNicknames!.AsReadOnly()!;
                BirthDate = birthDate;
                DueDate = dueDate;
            });
    }
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Coo.Domain.Tests --filter "FullyQualifiedName~BabyTests"`
Expected: PASS, all of `BabyTests`.

- [ ] **Step 5: Commit**

```bash
git add src/Shared/Coo.Domain/Households/BabyProfile.cs tests/Coo.Domain.Tests/Households/BabyTests.cs
git commit -m "Domain: BabyProfile.Revise, every profile field in one write (Spec 009 §3.1)"
```

---

### Task 2: The settings use cases (Application)

**Files:**
- Create: `src/Mobile/Coo.Mobile.Application/Households/Settings.cs`
- Modify: `src/Mobile/Coo.Mobile.Application/ServiceCollectionExtensions.cs`
- Test: `tests/Coo.Mobile.Application.Tests/SettingsUseCaseTests.cs`

**Interfaces:**
- Consumes: `BabyProfile.Revise` (Task 1); `IHouseholdStore.SaveBabyAsync`, `SaveSettingsAsync`; `IVersionClock.NextAsync`.
- Produces:
  - `public sealed record BabyProfileChange(string Name, IReadOnlyList<string> Nicknames, DateOnly? BirthDate, DateOnly? DueDate);`
  - `UpdateBabyProfile.ExecuteAsync(BabyProfile baby, BabyProfileChange change, CancellationToken) : Task<Result>`
  - `ChangeVolumeUnit.ExecuteAsync(HouseholdSettings settings, VolumeUnit unit, CancellationToken) : Task<Result>` (same unit: no write)
  - `SetHiddenKinds.ExecuteAsync(HouseholdSettings settings, EntryKindCode kind, bool hidden, CancellationToken) : Task<Result>`
  - `SettingsErrors.AllKindsHidden = "settings.all-kinds-hidden"`

- [ ] **Step 1: Write the failing tests**

```csharp
using Coo.Domain.CareLog;
using Coo.Domain.Common.Versioning;
using Coo.Domain.Households;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Application.Households;
using NSubstitute;

namespace Coo.Mobile.Application.Tests;

/// <summary>Spec 009 §3.1–§3.3. Only the outbound ports are mocked.</summary>
public sealed class SettingsUseCaseTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.FromHours(2));
    private static readonly DateOnly Today = new(2026, 10, 1);
    private static readonly DeviceId Device = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000d1"));
    private static readonly HouseholdId Household = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a1"));
    private static readonly BabyId Baby = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000b1"));

    private readonly IHouseholdStore _households = Substitute.For<IHouseholdStore>();
    private readonly IVersionClock _clock = Substitute.For<IVersionClock>();
    private readonly FixedTime _time = new(Now);
    private long _wall = 1_790_000_000_000;

    public SettingsUseCaseTests() =>
        _clock.NextAsync(Arg.Any<CancellationToken>()).Returns(_ => new HlcStamp(++_wall, 0, Device));

    [Fact]
    public async Task ExecuteAsync_ValidBabyChange_SavesOnceWithANewVersion()
    {
        // Arrange
        var baby = Born();
        var before = baby.Version;
        var useCase = new UpdateBabyProfile(_households, _clock, _time);

        // Act
        var result = await useCase.ExecuteAsync(baby, new BabyProfileChange("Zofia", ["Zo"], Today.AddMonths(-2), null), CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        baby.Version.Should().BeGreaterThan(before);
        await _households.Received(1).SaveBabyAsync(baby, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_DueDateOutOfRange_ReturnsErrorAndSavesNothing()
    {
        // Arrange
        var useCase = new UpdateBabyProfile(_households, _clock, _time);

        // Act
        var result = await useCase.ExecuteAsync(Born(), new BabyProfileChange("Zosia", [], Today.AddMonths(-2), Today.AddYears(1)), CancellationToken.None);

        // Assert
        result.Error!.Code.Should().Be(BabyErrors.DueDateOutOfRange);
        await _households.DidNotReceiveWithAnyArgs().SaveBabyAsync(default!, default);
    }

    [Fact]
    public async Task ExecuteAsync_NewUnit_SavesTheSettings()
    {
        // Arrange
        var settings = Settings();
        var useCase = new ChangeVolumeUnit(_households, _clock);

        // Act
        var result = await useCase.ExecuteAsync(settings, VolumeUnit.Ounces, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        settings.VolumeUnit.Should().Be(VolumeUnit.Ounces);
        await _households.Received(1).SaveSettingsAsync(settings, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_SameUnit_WritesNothing()
    {
        // Arrange
        var useCase = new ChangeVolumeUnit(_households, _clock);

        // Act
        var result = await useCase.ExecuteAsync(Settings(), VolumeUnit.Millilitres, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        await _households.DidNotReceiveWithAnyArgs().SaveSettingsAsync(default!, default);
    }

    [Fact]
    public async Task ExecuteAsync_HidePump_SavesItAmongTheHiddenKinds()
    {
        // Arrange
        var settings = Settings();
        var useCase = new SetHiddenKinds(_households, _clock);

        // Act
        var result = await useCase.ExecuteAsync(settings, Code("pump"), hidden: true, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        settings.HiddenKinds.Should().Equal(Code("pump"));
        await _households.Received(1).SaveSettingsAsync(settings, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_HideTheLastVisibleKind_FailsWithAllKindsHidden()
    {
        // Arrange
        var settings = Settings(hidden: ["feeding", "diaper", "sleep"]);
        var useCase = new SetHiddenKinds(_households, _clock);

        // Act
        var result = await useCase.ExecuteAsync(settings, Code("pump"), hidden: true, CancellationToken.None);

        // Assert
        result.Error!.Code.Should().Be(SettingsErrors.AllKindsHidden);
        await _households.DidNotReceiveWithAnyArgs().SaveSettingsAsync(default!, default);
    }

    [Fact]
    public async Task ExecuteAsync_UnknownCodeFromANewerClient_IsKept()
    {
        // Arrange
        var settings = Settings(hidden: ["medication"]);
        var useCase = new SetHiddenKinds(_households, _clock);

        // Act
        await useCase.ExecuteAsync(settings, Code("pump"), hidden: true, CancellationToken.None);

        // Assert
        settings.HiddenKinds.Should().BeEquivalentTo([Code("medication"), Code("pump")]);
    }

    [Fact]
    public async Task ExecuteAsync_ShowKindWhenAllHidden_Succeeds()
    {
        // Arrange: a newer client hid every kind this build knows.
        var settings = Settings(hidden: ["feeding", "diaper", "sleep", "pump"]);
        var useCase = new SetHiddenKinds(_households, _clock);

        // Act
        var result = await useCase.ExecuteAsync(settings, Code("sleep"), hidden: false, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        settings.HiddenKinds.Should().NotContain(Code("sleep"));
    }

    private static EntryKindCode Code(string value) => EntryKindCode.From(value).Value;

    private static BabyProfile Born() =>
        BabyProfile.Create(Baby, Household, "Zosia", [], Today.AddMonths(-2), null, new HlcStamp(1, 0, Device), Today).Value;

    private static HouseholdSettings Settings(string[]? hidden = null) =>
        HouseholdSettings.Create(Household, VolumeUnit.Millilitres, "Europe/Amsterdam", new HlcStamp(1, 0, Device),
            (hidden ?? []).Select(Code)).Value;

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();

        public override TimeZoneInfo LocalTimeZone { get; } = TimeZoneInfo.CreateCustomTimeZone("plus2", now.Offset, "plus2", "plus2");
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Coo.Mobile.Application.Tests --filter "FullyQualifiedName~SettingsUseCaseTests"`
Expected: build error, `UpdateBabyProfile` not found.

- [ ] **Step 3: Implement** `src/Mobile/Coo.Mobile.Application/Households/Settings.cs`

```csharp
using Coo.Domain.CareLog;
using Coo.Domain.Common;
using Coo.Domain.Households;
using Coo.Mobile.Application.CareLog;

namespace Coo.Mobile.Application.Households;

public static class SettingsErrors
{
    /// <summary>At least one kind stays visible (Spec 009 §3.2).</summary>
    public const string AllKindsHidden = "settings.all-kinds-hidden";
}

/// <summary>What the baby page saves (Spec 009 §3.1).</summary>
public sealed record BabyProfileChange(string Name, IReadOnlyList<string> Nicknames, DateOnly? BirthDate, DateOnly? DueDate);

/// <summary>The baby page's Save: every field in one write, with its outbox row.</summary>
public sealed class UpdateBabyProfile(IHouseholdStore households, IVersionClock clock, TimeProvider time)
{
    public async Task<Result> ExecuteAsync(BabyProfile baby, BabyProfileChange change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(baby);
        ArgumentNullException.ThrowIfNull(change);
        var today = DateOnly.FromDateTime(time.GetLocalNow().DateTime);
        var result = baby.Revise(change.Name, change.Nicknames, change.BirthDate, change.DueDate, await clock.NextAsync(cancellationToken), today);
        if (result.IsSuccess)
            await households.SaveBabyAsync(baby, cancellationToken);
        return result;
    }
}

/// <summary>ml or oz for the household (Spec 009 §3.3). Display only: stored amounts stay in ml.</summary>
public sealed class ChangeVolumeUnit(IHouseholdStore households, IVersionClock clock)
{
    public async Task<Result> ExecuteAsync(HouseholdSettings settings, VolumeUnit unit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.VolumeUnit == unit)
            return Result.Success;

        var result = settings.SetVolumeUnit(unit, await clock.NextAsync(cancellationToken));
        if (result.IsSuccess)
            await households.SaveSettingsAsync(settings, cancellationToken);
        return result;
    }
}

/// <summary>
/// Shows or hides one kind (Spec 009 §3.2, ADR 0010 §6). Codes this build does not know are kept, and hiding the last
/// visible known kind is refused; showing one is always allowed.
/// </summary>
public sealed class SetHiddenKinds(IHouseholdStore households, IVersionClock clock)
{
    public async Task<Result> ExecuteAsync(HouseholdSettings settings, EntryKindCode kind, bool hidden, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var next = new HashSet<EntryKindCode>(settings.HiddenKinds);
        if (!(hidden ? next.Add(kind) : next.Remove(kind)))
            return Result.Success;
        if (hidden && EntryKinds.All.All(k => next.Contains(k.Code)))
            return Result.Failure(SettingsErrors.AllKindsHidden);

        var result = settings.SetHiddenKinds(next, await clock.NextAsync(cancellationToken));
        if (result.IsSuccess)
            await households.SaveSettingsAsync(settings, cancellationToken);
        return result;
    }
}
```

Register in `AddMobileApplication`, after `RecordBirth`:

```csharp
        services.AddTransient<UpdateBabyProfile>();
        services.AddTransient<ChangeVolumeUnit>();
        services.AddTransient<SetHiddenKinds>();
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Coo.Mobile.Application.Tests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Mobile/Coo.Mobile.Application tests/Coo.Mobile.Application.Tests/SettingsUseCaseTests.cs
git commit -m "Application: UpdateBabyProfile, ChangeVolumeUnit, SetHiddenKinds (Spec 009 §3.1-§3.3)"
```

---

### Task 3: The language preference (Application port, SQLite)

**Files:**
- Modify: `src/Mobile/Coo.Mobile.Application/Preferences/DevicePreferences.cs`
- Modify: `src/Mobile/Coo.Mobile.Infrastructure/Persistence/DevicePreferences.cs`
- Test: `tests/Coo.Mobile.Infrastructure.Tests/DeviceAndMigrationTests.cs` (class `DevicePreferencesTests`)

**Interfaces:**
- Produces:
  - `public sealed record LanguagePreference(string? Language)` with `static LanguagePreference System` and `bool IsSystem`.
  - On `IDevicePreferences`: `Task<LanguagePreference> GetLanguageAsync(CancellationToken)`, `Task SetLanguageAsync(LanguagePreference, CancellationToken)`.
  - Stored as `app_state.language` = `system` or a two-letter lower-case tag. Anything else reads as System.

- [ ] **Step 1: Write the failing tests** (append to `DevicePreferencesTests`)

```csharp
    [Fact]
    public async Task GetLanguageAsync_NothingStored_ReturnsSystem()
    {
        // Arrange
        await using var db = await TestDatabase.CreateAsync();

        // Act
        var language = await new DevicePreferences(db.Database).GetLanguageAsync(CancellationToken.None);

        // Assert
        language.Should().Be(Coo.Mobile.Application.Preferences.LanguagePreference.System);
    }

    [Fact]
    public async Task SetThenGet_Polish_RoundTrips()
    {
        // Arrange
        await using var db = await TestDatabase.CreateAsync();
        var polish = new Coo.Mobile.Application.Preferences.LanguagePreference("pl");

        // Act
        await new DevicePreferences(db.Database).SetLanguageAsync(polish, CancellationToken.None);
        var read = await new DevicePreferences(db.Database).GetLanguageAsync(CancellationToken.None);

        // Assert
        read.Should().Be(polish);
    }

    [Fact]
    public async Task GetLanguageAsync_UnreadableValue_ReturnsSystem()
    {
        // Arrange
        await using var db = await TestDatabase.CreateAsync();
        await using (var connection = await db.OpenAsync())
            await connection.ExecuteAsync("INSERT INTO app_state (key, value) VALUES ('language', 'Klingon!');", CancellationToken.None);

        // Act
        var language = await new DevicePreferences(db.Database).GetLanguageAsync(CancellationToken.None);

        // Assert
        language.IsSystem.Should().BeTrue();
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Coo.Mobile.Infrastructure.Tests --filter "FullyQualifiedName~DevicePreferencesTests"`
Expected: build error, `LanguagePreference` not found.

- [ ] **Step 3: Implement**

In `Coo.Mobile.Application/Preferences/DevicePreferences.cs`, before `IDevicePreferences`:

```csharp
/// <summary>
/// The app's language on this phone (ADR 0006 §1): System (Android's language if Coo has it), or a two-letter tag.
/// Per device, never synced. Which tags are supported is the presentation's business.
/// </summary>
public sealed record LanguagePreference(string? Language)
{
    public static LanguagePreference System { get; } = new((string?)null);

    public bool IsSystem => Language is null;
}
```

and on `IDevicePreferences`:

```csharp
    Task<LanguagePreference> GetLanguageAsync(CancellationToken cancellationToken);

    Task SetLanguageAsync(LanguagePreference preference, CancellationToken cancellationToken);
```

In `Coo.Mobile.Infrastructure/Persistence/DevicePreferences.cs` add the key and the two methods:

```csharp
    private const string LanguageKey = "language";
    private const string SystemLanguage = "system";
```

```csharp
    public async Task<LanguagePreference> GetLanguageAsync(CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        var value = (string?)await connection.ScalarAsync("SELECT value FROM app_state WHERE key = $k;", cancellationToken, null, ("$k", LanguageKey));
        return value is { Length: 2 } && value.All(char.IsAsciiLetterLower) ? new LanguagePreference(value) : LanguagePreference.System;
    }

    public async Task SetLanguageAsync(LanguagePreference preference, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(preference);
        await using var connection = await database.OpenAsync(cancellationToken);
        await connection.ExecuteAsync(
            "INSERT INTO app_state (key, value) VALUES ($k, $v) ON CONFLICT (key) DO UPDATE SET value = excluded.value;",
            cancellationToken, null, ("$k", LanguageKey), ("$v", preference.Language ?? SystemLanguage));
    }
```

(If `ExecuteAsync` has no overload taking a null transaction, match the call shape used by `ScalarAsync` above;
check `Persistence/LocalDatabase.cs`.) Update the class summary to "Per-device settings in `app_state`: appearance
and language; never synced."

Any test fake of `IDevicePreferences` elsewhere (grep `IDevicePreferences` in `tests/`) gets the two members; an
NSubstitute substitute needs nothing.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Coo.Mobile.Infrastructure.Tests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Mobile/Coo.Mobile.Application/Preferences src/Mobile/Coo.Mobile.Infrastructure/Persistence/DevicePreferences.cs tests/Coo.Mobile.Infrastructure.Tests
git commit -m "Store the app language per device in app_state (Spec 009 §3.5)"
```

---

### Task 4: `LanguageRule`, `LanguageController`, and `Strings.Culture` (Presentation)

**Files:**
- Create: `src/Mobile/Coo.Mobile.Presentation/Localization/LanguageRule.cs`
- Create: `src/Mobile/Coo.Mobile.Presentation/Localization/LanguageController.cs`
- Modify: `src/Mobile/Coo.Mobile.Presentation/Localization/Strings.cs`
- Modify: `src/Mobile/Coo.Mobile.Presentation/ServiceCollectionExtensions.cs`
- Test: `tests/Coo.Mobile.Presentation.Tests/LanguageTests.cs`

**Interfaces:**
- Consumes: `IDevicePreferences.GetLanguageAsync` / `SetLanguageAsync`, `LanguagePreference` (Task 3).
- Produces:
  - `LanguageRule.Resolve(LanguagePreference preference, CultureInfo system) : CultureInfo`
  - `LanguageRule.Options(CultureInfo system) : IReadOnlyList<LanguageOption>`; `public sealed record LanguageOption(LanguagePreference Preference, string Label);`
  - `Strings.Use(LanguagePreference preference, CultureInfo? system)`; with `system` null, `Strings.Culture` reads `CultureInfo.CurrentUICulture` as it does today.
  - `LanguageController` (singleton): `LanguagePreference Preference`, `CultureInfo SystemCulture`, `Task StartAsync(CultureInfo system, CancellationToken)`,
    `Task SetAsync(LanguagePreference, CancellationToken)`, `void SystemChanged(CultureInfo system)`, `event EventHandler? Changed` (raised only when `Strings.Culture` changes).

- [ ] **Step 1: Write the failing tests**

`Strings` holds process-wide state, so the controller tests run in a collection that never runs in parallel, and
reset it.

```csharp
using System.Globalization;
using Coo.Mobile.Application.Preferences;
using Coo.Mobile.Presentation.Localization;
using NSubstitute;

namespace Coo.Mobile.Presentation.Tests;

public sealed class LanguageRuleTests
{
    [Theory]
    [InlineData(null, "pl-PL", "pl")]
    [InlineData(null, "ja-JP", "en")]
    [InlineData("de", "pl-PL", "de")]
    [InlineData("xx", "nl-NL", "nl")]
    public void Resolve_PreferenceAndSystem_PicksTheLanguage(string? chosen, string system, string expected)
    {
        // Arrange
        var preference = new LanguagePreference(chosen);

        // Act
        var culture = LanguageRule.Resolve(preference, CultureInfo.GetCultureInfo(system));

        // Assert
        culture.TwoLetterISOLanguageName.Should().Be(expected);
    }

    [Fact]
    public void Options_PolishPhone_StartsWithSystemNamingPolishThenTheSevenEndonyms()
    {
        // Arrange
        var system = CultureInfo.GetCultureInfo("pl-PL");

        // Act
        var options = LanguageRule.Options(system);

        // Assert
        options.Should().HaveCount(8);
        options[0].Preference.IsSystem.Should().BeTrue();
        options[0].Label.Should().Contain("Polski");
        options.Skip(1).Select(o => o.Label).Should().Equal("English", "Deutsch", "Français", "Español", "Italiano", "Polski", "Nederlands");
    }
}

[CollectionDefinition(nameof(StringsState), DisableParallelization = true)]
public sealed class StringsState;

[Collection(nameof(StringsState))]
public sealed class LanguageControllerTests : IDisposable
{
    private static readonly CultureInfo Dutch = CultureInfo.GetCultureInfo("nl-NL");
    private readonly IDevicePreferences _preferences = Substitute.For<IDevicePreferences>();

    public void Dispose() => Strings.Use(LanguagePreference.System, null);

    [Fact]
    public async Task StartAsync_StoredPolish_MakesStringsPolish()
    {
        // Arrange
        _preferences.GetLanguageAsync(Arg.Any<CancellationToken>()).Returns(new LanguagePreference("pl"));
        var controller = new LanguageController(_preferences);

        // Act
        await controller.StartAsync(Dutch, CancellationToken.None);

        // Assert
        Strings.Language.Should().Be("pl");
    }

    [Fact]
    public async Task SetAsync_BackToSystem_UsesTheCapturedSystemCulture()
    {
        // Arrange: Polish chosen on a Dutch phone, then System again.
        _preferences.GetLanguageAsync(Arg.Any<CancellationToken>()).Returns(new LanguagePreference("pl"));
        var controller = new LanguageController(_preferences);
        await controller.StartAsync(Dutch, CancellationToken.None);
        var raised = 0;
        controller.Changed += (_, _) => raised++;

        // Act
        await controller.SetAsync(LanguagePreference.System, CancellationToken.None);

        // Assert
        Strings.Language.Should().Be("nl");
        raised.Should().Be(1);
        await _preferences.Received(1).SetLanguageAsync(LanguagePreference.System, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SystemChanged_WithExplicitChoice_KeepsTheChoiceAndRaisesNothing()
    {
        // Arrange
        _preferences.GetLanguageAsync(Arg.Any<CancellationToken>()).Returns(new LanguagePreference("de"));
        var controller = new LanguageController(_preferences);
        await controller.StartAsync(Dutch, CancellationToken.None);
        var raised = 0;
        controller.Changed += (_, _) => raised++;

        // Act
        controller.SystemChanged(CultureInfo.GetCultureInfo("fr-FR"));

        // Assert
        Strings.Language.Should().Be("de");
        raised.Should().Be(0);
    }

    [Fact]
    public async Task SystemChanged_OnSystem_FollowsAndRaisesChanged()
    {
        // Arrange
        _preferences.GetLanguageAsync(Arg.Any<CancellationToken>()).Returns(LanguagePreference.System);
        var controller = new LanguageController(_preferences);
        await controller.StartAsync(Dutch, CancellationToken.None);
        var raised = 0;
        controller.Changed += (_, _) => raised++;

        // Act
        controller.SystemChanged(CultureInfo.GetCultureInfo("fr-FR"));

        // Assert
        Strings.Language.Should().Be("fr");
        raised.Should().Be(1);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Coo.Mobile.Presentation.Tests --filter "FullyQualifiedName~Language"`
Expected: build error, `LanguageRule` not found.

- [ ] **Step 3: Implement**

`Localization/LanguageRule.cs`:

```csharp
using System.Globalization;
using Coo.Mobile.Application.Preferences;

namespace Coo.Mobile.Presentation.Localization;

/// <summary>A language choice in the list (Spec 009 §3.5).</summary>
public sealed record LanguageOption(LanguagePreference Preference, string Label);

/// <summary>Which language applies, as a pure function (ADR 0006 §1): the choice if Coo has it, else System, else English.</summary>
public static class LanguageRule
{
    /// <summary>Each language named in its own language, in <see cref="Strings.Supported"/> order.</summary>
    private static readonly Dictionary<string, string> Endonyms = new()
    {
        ["en"] = "English",
        ["de"] = "Deutsch",
        ["fr"] = "Français",
        ["es"] = "Español",
        ["it"] = "Italiano",
        ["pl"] = "Polski",
        ["nl"] = "Nederlands",
    };

    public static CultureInfo Resolve(LanguagePreference preference, CultureInfo system)
    {
        ArgumentNullException.ThrowIfNull(preference);
        ArgumentNullException.ThrowIfNull(system);
        if (preference.Language is { } chosen && Strings.Supported.Contains(chosen))
            return CultureInfo.GetCultureInfo(chosen);
        return Strings.Supported.Contains(system.TwoLetterISOLanguageName) ? system : CultureInfo.GetCultureInfo("en");
    }

    public static IReadOnlyList<LanguageOption> Options(CultureInfo system)
    {
        var resolved = Resolve(LanguagePreference.System, system).TwoLetterISOLanguageName;
        return
        [
            new(LanguagePreference.System, Strings.Format("Settings.LanguageSystem", Endonyms[resolved])),
            .. Strings.Supported.Select(code => new LanguageOption(new LanguagePreference(code), Endonyms[code])),
        ];
    }

    public static string Label(LanguagePreference preference, CultureInfo system) =>
        Options(system).First(o => o.Preference == preference || (o.Preference.IsSystem && !Strings.Supported.Contains(preference.Language ?? ""))).Label;
}
```

`Strings.cs`: replace the `Culture` property with:

```csharp
    private static LanguagePreference _preference = LanguagePreference.System;
    private static CultureInfo? _system;

    /// <summary>
    /// The language in effect (ADR 0006 §1, Spec 009 §3.5): the stored choice, else the OS language if Coo supports
    /// it, else English. Background code asks this rather than assuming the app has set a culture.
    /// </summary>
    public static CultureInfo Culture => LanguageRule.Resolve(_preference, _system ?? CultureInfo.CurrentUICulture);

    /// <summary>
    /// Set by <see cref="LanguageController"/> only. <paramref name="system"/> is Android's language as captured before
    /// Coo changed the process culture; null reads <see cref="CultureInfo.CurrentUICulture"/>.
    /// </summary>
    public static void Use(LanguagePreference preference, CultureInfo? system)
    {
        _preference = preference ?? throw new ArgumentNullException(nameof(preference));
        _system = system;
    }
```

Add `using Coo.Mobile.Application.Preferences;` to `Strings.cs`.

`Localization/LanguageController.cs`:

```csharp
using System.Globalization;
using Coo.Mobile.Application.Preferences;

namespace Coo.Mobile.Presentation.Localization;

/// <summary>
/// Loads, stores and applies the app language (Spec 009 §3.5). It raises <see cref="Changed"/> only when the language
/// in effect changes; the host then rebuilds its pages. It knows nothing about MAUI.
/// </summary>
public sealed class LanguageController(IDevicePreferences preferences)
{
    public LanguagePreference Preference { get; private set; } = LanguagePreference.System;

    /// <summary>Android's language, as the host last reported it.</summary>
    public CultureInfo SystemCulture { get; private set; } = CultureInfo.CurrentUICulture;

    public event EventHandler? Changed;

    /// <summary>Call before the first page is built, with the OS culture captured at process start.</summary>
    public async Task StartAsync(CultureInfo system, CancellationToken cancellationToken)
    {
        SystemCulture = system ?? throw new ArgumentNullException(nameof(system));
        Preference = await preferences.GetLanguageAsync(cancellationToken);
        Strings.Use(Preference, SystemCulture);
    }

    public async Task SetAsync(LanguagePreference preference, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(preference);
        await preferences.SetLanguageAsync(preference, cancellationToken);
        Apply(() => Preference = preference);
    }

    /// <summary>Android's language changed. An explicit choice in Coo wins over it.</summary>
    public void SystemChanged(CultureInfo system)
    {
        ArgumentNullException.ThrowIfNull(system);
        Apply(() => SystemCulture = system);
    }

    private void Apply(Action change)
    {
        var before = Strings.Culture.Name;
        change();
        Strings.Use(Preference, SystemCulture);
        if (Strings.Culture.Name != before)
            Changed?.Invoke(this, EventArgs.Empty);
    }
}
```

Register in `AddMobilePresentation`, after `ThemeController`:

```csharp
        services.AddSingleton<LanguageController>();
```

(with `using Coo.Mobile.Presentation.Localization;`).

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Coo.Mobile.Presentation.Tests`
Expected: PASS, including the existing `LocalizationTests` and `TodayTests` (English by default: `_system` is null
outside the collection).

- [ ] **Step 5: Commit**

```bash
git add src/Mobile/Coo.Mobile.Presentation tests/Coo.Mobile.Presentation.Tests/LanguageTests.cs
git commit -m "Language: System or a chosen language, applied through LanguageController (Spec 009 §3.5)"
```

---

### Task 5: Hidden kinds on Today and Log (Presentation)

**Files:**
- Create: `src/Mobile/Coo.Mobile.Presentation/Today/KindVisibility.cs`
- Modify: `src/Mobile/Coo.Mobile.Presentation/Today/Sections.cs` (`LogTilesSection.Update`, `NightSection.Update`)
- Modify: `src/Mobile/Coo.Mobile.Presentation/Today/TodayViewModel.cs` (add `IsShown`)
- Modify: `src/Mobile/Coo.Mobile.Presentation/Log/LogViewModel.cs` (`Totals`)
- Modify: `src/Mobile/Coo.Mobile/Pages/TodayPage.cs` (`OpenEarlier`, and the "Earlier…" link below the tiles)
- Test: `tests/Coo.Mobile.Presentation.Tests/HiddenKindsTests.cs`

**Interfaces:**
- Produces: `KindVisibility.IsShown(HouseholdSettings household, string kindCode) : bool`;
  `TodayViewModel.IsShown(string kindCode) : bool`.
- Rules: tiles, night facts, night quick buttons, Log totals and the "Earlier…" choices drop hidden kinds. Running
  timers, the hero and the timelines are untouched.

- [ ] **Step 1: Write the failing tests**

```csharp
using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Kinds.Pump;
using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;
using Coo.Domain.Households;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Presentation.Formatting;
using Coo.Mobile.Presentation.Theme;
using Coo.Mobile.Presentation.Today;
using NSubstitute;

namespace Coo.Mobile.Presentation.Tests;

/// <summary>Spec 009 §3.2.</summary>
public sealed class HiddenKindsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 14, 0, 0, TimeSpan.FromHours(2));
    private static readonly HouseholdId Household = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a1"));
    private static readonly BabyId Baby = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000b1"));
    private static readonly DeviceId Device = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000d1"));

    private readonly ICareLogStore _store = Substitute.For<ICareLogStore>();
    private readonly IVersionClock _clock = Substitute.For<IVersionClock>();

    [Fact]
    public void IsShown_PumpHidden_IsFalseForPumpOnly()
    {
        // Arrange
        var household = Settings("pump");

        // Act
        var pump = KindVisibility.IsShown(household, "pump");
        var diaper = KindVisibility.IsShown(household, "diaper");

        // Assert
        pump.Should().BeFalse();
        diaper.Should().BeTrue();
    }

    [Fact]
    public void Update_PumpHidden_HasNoPumpTile()
    {
        // Arrange
        var section = new LogTilesSection(Actions());

        // Act
        section.Update(Snapshot(Settings("pump"), Summary([]), PaletteKind.Day));

        // Assert
        section.Tiles.Select(t => t.KindCode).Should().Equal("feeding", "diaper", "sleep");
    }

    [Fact]
    public void Update_PumpHiddenWhilePumping_KeepsTheRunningCard()
    {
        // Arrange
        var pumping = Entry(PumpDetails.Start(Now.AddMinutes(-5)));
        var section = new RunningSection(Actions());

        // Act
        section.Update(Snapshot(Settings("pump"), Summary([pumping]), PaletteKind.Day));

        // Assert
        section.Items.Should().ContainSingle().Which.KindCode.Should().Be("pump");
    }

    [Fact]
    public void Update_NightWithDiaperHidden_HasNoDiaperFactOrButtons()
    {
        // Arrange
        var section = new NightSection(Actions());

        // Act
        section.Update(Snapshot(Settings("diaper"), Summary([]), PaletteKind.Night));

        // Assert
        section.Facts.Select(f => f.KindCode).Should().NotContain("diaper");
        section.Quick.Select(q => q.KindCode).Should().NotContain("diaper");
    }

    private HomeActions Actions()
    {
        var time = new FixedTime(Now);
        return new HomeActions(new LogEntry(_store, _clock, time), new ReviseEntry(_store, _clock, time), time)
        {
            Context = new HomeContext(Household, Baby),
        };
    }

    private static HouseholdSettings Settings(params string[] hidden) =>
        HouseholdSettings.Create(Household, VolumeUnit.Millilitres, "Europe/Amsterdam", new HlcStamp(1, 0, Device),
            hidden.Select(h => EntryKindCode.From(h).Value)).Value;

    private static TodaySummary Summary(IReadOnlyList<CareEntry> running) =>
        new(running, null, null, null, null, Now.AddHours(-1), [], DayMath.Day(new DateOnly(2026, 10, 1), TimeZoneInfo.Utc));

    private static HomeSnapshot Snapshot(HouseholdSettings household, TodaySummary summary, PaletteKind palette)
    {
        var baby = BabyProfile.Create(Baby, Household, "Zosia", [], new DateOnly(2026, 5, 1), null, new HlcStamp(1, 0, Device), new DateOnly(2026, 10, 1)).Value;
        return new HomeSnapshot(household, baby, summary, Now, palette, new Formats(true, VolumeUnit.Millilitres));
    }

    private CareEntry Entry(EntryDetails details) =>
        CareEntry.Create(new EntryId(Guid.CreateVersion7(Now)), Household, EntrySubject.Baby(Baby), details, null,
            PersonRef.ThisDevicesPerson, EntrySource.Timer, new HlcStamp(2, 0, Device), Now).Value;

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();

        public override TimeZoneInfo LocalTimeZone { get; } = TimeZoneInfo.CreateCustomTimeZone("plus2", now.Offset, "plus2", "plus2");
    }
}
```

The `TodaySummary` constructor and `PumpDetails.Start` must match the real signatures: copy the shape from
`TodayTests.Update_NightWithRunningFeed_ShowsTimerFactsAndQuickRow` (it builds a `TodaySummary` with a running
entry), and check `Coo.Domain/CareLog/Kinds/Pump/PumpDetails.cs` for the factory that starts a session.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Coo.Mobile.Presentation.Tests --filter "FullyQualifiedName~HiddenKindsTests"`
Expected: build error, `KindVisibility` not found.

- [ ] **Step 3: Implement**

`Today/KindVisibility.cs`:

```csharp
using Coo.Domain.CareLog;
using Coo.Domain.Households;

namespace Coo.Mobile.Presentation.Today;

/// <summary>
/// Which kinds the household shows (Spec 009 §3.2, ADR 0010 §6). Hiding removes ways to log and totals, never
/// entries or running timers.
/// </summary>
public static class KindVisibility
{
    public static bool IsShown(HouseholdSettings household, string kindCode)
    {
        ArgumentNullException.ThrowIfNull(household);
        return EntryKindCode.From(kindCode) is not { IsSuccess: true } code || !household.HiddenKinds.Contains(code.Value);
    }
}
```

`LogTilesSection.Update`: build the list as today into `all`, then

```csharp
        IReadOnlyList<TileItem> next = [.. all.Where(tile => KindVisibility.IsShown(snapshot.Household, tile.KindCode))];
```

`NightSection.Update`: the same filter on `facts` and `quick` before the `Running.Changed` checks:

```csharp
        facts = [.. facts.Where(f => KindVisibility.IsShown(s.Household, f.KindCode))];
```

```csharp
        quick = [.. quick.Where(q => KindVisibility.IsShown(s.Household, q.KindCode))];
```

`TodayViewModel`:

```csharp
    /// <summary>For the page's "Earlier…" choices (Spec 009 §3.2).</summary>
    public bool IsShown(string kindCode) => _household is null || KindVisibility.IsShown(_household, kindCode);
```

`LogViewModel.RefreshAsync`: wrap the totals list:

```csharp
        Totals = [.. new TotalItem[]
        {
            new("feeding", day.Totals.Feeds.ToString(Strings.Culture), PluralWord("Totals.Feeds", day.Totals.Feeds)),
            new("diaper", day.Totals.Diapers.ToString(Strings.Culture), PluralWord("Totals.Diapers", day.Totals.Diapers)),
            new("sleep", Formats.Duration(day.Totals.Sleep), Strings.Get("Totals.Sleep")),
            new("pump", pumped.Item1, pumped.Item2),
        }.Where(t => KindVisibility.IsShown(household, t.KindCode))];
```

`TodayPage.OpenEarlier`: build the buttons, keep only shown kinds, and lay the rest out two per row. Replace the two
`Row(...)` calls with:

```csharp
        var buttons = new[]
        {
            ("feeding", EarlierButton("feeding", "Kind.Feed")),
            ("feeding", EarlierButton("feeding", "Feed.Bottle", bottle: true)),
            ("diaper", EarlierButton("diaper", "Kind.Diaper")),
            ("sleep", EarlierButton("sleep", "Kind.Sleep")),
            ("pump", EarlierButton("pump", "Kind.Pump")),
        }.Where(b => _model.IsShown(b.Item1)).Select(b => (View)b.Item2).Chunk(2).Select(pair => Row(pair)).ToArray();
```

and pass `buttons` where the rows were. Check `Row`'s signature in `TodayPage.cs` (a `params View[]` helper) and
adjust the call. The "Earlier…" link under the feed sheet (`EarlierLink("feeding")`) needs no change: the feed tile,
and so its sheet, is gone when feeding is hidden.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Coo.Mobile.Presentation.Tests` and `dotnet build src/Mobile/Coo.Mobile -c Debug`
Expected: PASS; build with 0 warnings.

- [ ] **Step 5: Commit**

```bash
git add src/Mobile tests/Coo.Mobile.Presentation.Tests/HiddenKindsTests.cs
git commit -m "Hidden kinds drop their tiles, totals and Earlier choices; timers stay (Spec 009 §3.2)"
```

---

### Task 5b: Night ↔ day from Today (Spec 009 §3.7, Q9)

**Files:**
- Modify: `src/Mobile/Coo.Mobile.Application/Preferences/DevicePreferences.cs` (`AppearanceOverride`, `AppearancePreferences.Override`)
- Modify: `src/Mobile/Coo.Mobile.Infrastructure/Persistence/DevicePreferences.cs` (two more keys)
- Modify: `src/Mobile/Coo.Mobile.Presentation/Theme/AppearanceRule.cs`, `Theme/ThemeController.cs`
- Modify: `src/Mobile/Coo.Mobile.Presentation/Today/TodayViewModel.cs` (`ToggleThemeCommand`)
- Modify: `src/Mobile/Coo.Mobile/Pages/SectionViews.cs`, `Pages/TodayPage.cs` (the button)
- Test: `tests/Coo.Mobile.Presentation.Tests/AppearanceRuleTests.cs`, `ThemeControllerTests.cs`; `tests/Coo.Mobile.Infrastructure.Tests/DeviceAndMigrationTests.cs`

**Interfaces:**
- Produces:
  - `public sealed record AppearanceOverride(AppearanceMode Palette, DateTimeOffset Until);` (`Palette` is `Day` or `Night`)
  - `AppearancePreferences(AppearanceMode Mode, QuietHours QuietHours, AppearanceOverride? Override = null)`
  - `AppearanceRule.Resolve(AppearanceMode mode, DateTimeOffset now, QuietHours quietHours, bool osIsDark, AppearanceOverride? @override)`:
    in Auto, an override with `now < Until` wins. The existing `TimeOnly` overload stays and passes no override.
  - `ThemeController.ToggleAsync(CancellationToken)`; `ThemeController.SetAsync` stays, and callers that change the
    mode or quiet hours pass `Override = null`.
  - `TodayViewModel.ToggleThemeCommand` (`IAsyncRelayCommand`).
- Rules: in Auto, the override lasts until `AppearanceRule.NextChange(...)` (the next quiet-hours boundary), or 12 h
  with no quiet hours. If the toggled palette equals what Auto would choose anyway, the override is cleared. With
  Day or Night fixed, the toggle stores the other mode. The controller's timer fires at the earlier of the boundary
  and `Until`.

- [ ] **Step 1: Write the failing tests**

In `AppearanceRuleTests`:

```csharp
    [Fact]
    public void Resolve_AutoInQuietHoursWithActiveDayOverride_IsDay()
    {
        // Arrange
        var now = new DateTimeOffset(2026, 10, 1, 3, 0, 0, TimeSpan.FromHours(2));
        var dayUntilEvening = new AppearanceOverride(AppearanceMode.Day, now.Date.AddHours(22));

        // Act
        var palette = AppearanceRule.Resolve(AppearanceMode.Auto, now, QuietHours.Default, osIsDark: false, dayUntilEvening);

        // Assert
        palette.Should().Be(PaletteKind.Day);
    }

    [Fact]
    public void Resolve_ExpiredOverride_FollowsAuto()
    {
        // Arrange
        var now = new DateTimeOffset(2026, 10, 1, 23, 0, 0, TimeSpan.FromHours(2));
        var expired = new AppearanceOverride(AppearanceMode.Day, now.AddHours(-1));

        // Act
        var palette = AppearanceRule.Resolve(AppearanceMode.Auto, now, QuietHours.Default, osIsDark: false, expired);

        // Assert
        palette.Should().Be(PaletteKind.Night);
    }
```

In `ThemeControllerTests` (reuse the file's existing time and OS fakes; the cases below need a clock at 03:00 local
and a substitute `IDevicePreferences` that returns `AppearancePreferences.Default`):

```csharp
    [Fact]
    public async Task ToggleAsync_AutoAtThreeAm_ShowsDayUntilTheNextQuietHoursStartAndStoresIt()
    {
        // Arrange: 03:00, quiet hours 22:00–07:00, so Auto shows night.
        var controller = Controller(at: new DateTimeOffset(2026, 10, 1, 3, 0, 0, TimeSpan.FromHours(2)));
        await controller.StartAsync(CancellationToken.None);

        // Act
        await controller.ToggleAsync(CancellationToken.None);

        // Assert
        controller.Current.Should().Be(PaletteKind.Day);
        controller.Settings.Mode.Should().Be(AppearanceMode.Auto);
        controller.Settings.Override.Should().Be(new AppearanceOverride(AppearanceMode.Day, new DateTimeOffset(2026, 10, 1, 7, 0, 0, TimeSpan.FromHours(2))));
        await _preferences.Received(1).SetAppearanceAsync(controller.Settings, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ToggleAsync_FixedNight_StoresDayMode()
    {
        // Arrange
        _preferences.GetAppearanceAsync(Arg.Any<CancellationToken>()).Returns(AppearancePreferences.Default with { Mode = AppearanceMode.Night });
        var controller = Controller(at: new DateTimeOffset(2026, 10, 1, 15, 0, 0, TimeSpan.FromHours(2)));
        await controller.StartAsync(CancellationToken.None);

        // Act
        await controller.ToggleAsync(CancellationToken.None);

        // Assert
        controller.Settings.Mode.Should().Be(AppearanceMode.Day);
        controller.Settings.Override.Should().BeNull();
        controller.Current.Should().Be(PaletteKind.Day);
    }

    [Fact]
    public async Task ToggleAsync_TwiceInAuto_ClearsTheOverride()
    {
        // Arrange
        var controller = Controller(at: new DateTimeOffset(2026, 10, 1, 3, 0, 0, TimeSpan.FromHours(2)));
        await controller.StartAsync(CancellationToken.None);

        // Act
        await controller.ToggleAsync(CancellationToken.None);
        await controller.ToggleAsync(CancellationToken.None);

        // Assert
        controller.Current.Should().Be(PaletteKind.Night);
        controller.Settings.Override.Should().BeNull();
    }
```

The day override in the first test lasts until **07:00**: that is the next quiet-hours boundary after 03:00 (the
end of the quiet hours), when Auto turns to day anyway. (Spec §3.7's "day at 03:00 lasts until 22:00" is the effect:
at 07:00 Auto agrees with the override, so the parent sees day all the way to 22:00.)

`Controller(at:)` builds `new ThemeController(_preferences, _os, new FixedTime(at))`; add it if the file does not
have such a helper, with a `FixedTime` whose `LocalTimeZone` is a fixed +02:00 zone, as in `TodayTests`.

In `DevicePreferencesTests`:

```csharp
    [Fact]
    public async Task SetThenGet_DayOverride_RoundTrips()
    {
        // Arrange
        await using var db = await TestDatabase.CreateAsync();
        var withOverride = Coo.Mobile.Application.Preferences.AppearancePreferences.Default with
        {
            Override = new Coo.Mobile.Application.Preferences.AppearanceOverride(
                Coo.Mobile.Application.Preferences.AppearanceMode.Day, new DateTimeOffset(2026, 10, 1, 7, 0, 0, TimeSpan.FromHours(2))),
        };

        // Act
        await new DevicePreferences(db.Database).SetAppearanceAsync(withOverride, CancellationToken.None);
        var read = await new DevicePreferences(db.Database).GetAppearanceAsync(CancellationToken.None);

        // Assert
        read.Override!.Palette.Should().Be(withOverride.Override.Palette);
        read.Override.Until.Should().Be(withOverride.Override.Until);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Coo.Mobile.Presentation.Tests --filter "FullyQualifiedName~Appearance|FullyQualifiedName~ThemeController"`
Expected: build error, `AppearanceOverride` not found.

- [ ] **Step 3: Implement**

`Application/Preferences/DevicePreferences.cs`:

```csharp
/// <summary>"Day for now" from Today (Spec 009 §3.7): in Auto, this palette until <see cref="Until"/>.</summary>
public sealed record AppearanceOverride(AppearanceMode Palette, DateTimeOffset Until);

/// <summary>This phone's appearance settings. Per device, never synced: parents differ (Spec 005 §6.3).</summary>
public sealed record AppearancePreferences(AppearanceMode Mode, QuietHours QuietHours, AppearanceOverride? Override = null)
{
    public static AppearancePreferences Default { get; } = new(AppearanceMode.Auto, QuietHours.Default);
}
```

`Infrastructure/Persistence/DevicePreferences.cs`: keys `appearance.override_palette` (`Day` / `Night`) and
`appearance.override_until` (`DateTimeOffset.ToString("O", CultureInfo.InvariantCulture)`). On read, an override is
returned only when both parse and the palette is `Day` or `Night`. On write, a null override writes both keys with
an empty value (read back as no override), inside the existing transaction.

`Theme/AppearanceRule.cs`: add

```csharp
    public static PaletteKind Resolve(AppearanceMode mode, DateTimeOffset now, QuietHours quietHours, bool osIsDark, AppearanceOverride? @override) =>
        mode == AppearanceMode.Auto && @override is { } o && now < o.Until
            ? (o.Palette == AppearanceMode.Night ? PaletteKind.Night : PaletteKind.Day)
            : Resolve(mode, TimeOnly.FromDateTime(now.DateTime), quietHours, osIsDark);
```

`Theme/ThemeController.cs`:

```csharp
    /// <summary>
    /// The sun or moon on Today (Spec 009 §3.7). In Auto, the other palette until the next quiet-hours boundary (12 h
    /// without quiet hours); with Day or Night fixed, the other mode.
    /// </summary>
    public Task ToggleAsync(CancellationToken cancellationToken)
    {
        var target = Current == PaletteKind.Night ? AppearanceMode.Day : AppearanceMode.Night;
        if (_settings.Mode != AppearanceMode.Auto)
            return SetAsync(_settings with { Mode = target, Override = null }, cancellationToken);

        var now = time.GetLocalNow();
        var auto = AppearanceRule.Resolve(AppearanceMode.Auto, TimeOnly.FromDateTime(now.DateTime), _settings.QuietHours, os.IsDark);
        if ((auto == PaletteKind.Night) == (target == AppearanceMode.Night))
            return SetAsync(_settings with { Override = null }, cancellationToken);

        var until = AppearanceRule.NextChange(AppearanceMode.Auto, now, _settings.QuietHours, time.LocalTimeZone) ?? now.AddHours(12);
        return SetAsync(_settings with { Override = new AppearanceOverride(target, until) }, cancellationToken);
    }
```

In `Evaluate`, resolve with the override:

```csharp
        var next = AppearanceRule.Resolve(_settings.Mode, now, _settings.QuietHours, os.IsDark, _settings.Override);
```

and in `Schedule`, fire at the earlier of the boundary and an active override's `Until`:

```csharp
        var boundary = AppearanceRule.NextChange(_settings.Mode, now, _settings.QuietHours, time.LocalTimeZone);
        var until = _settings.Override is { } o && o.Until > now ? o.Until : (DateTimeOffset?)null;
        if ((boundary, until) switch { ({ } b, { } u) => b < u ? b : u, ({ } b, null) => b, (null, { } u) => u, _ => (DateTimeOffset?)null } is { } at)
        {
            var due = at - now + TimeSpan.FromSeconds(1);
            _timer = time.CreateTimer(_ => Evaluate(force: false), null, due, Timeout.InfiniteTimeSpan);
        }
```

`TodayViewModel`:

```csharp
    [RelayCommand]
    private Task ToggleTheme() => _theme.ToggleAsync(CancellationToken.None);
```

`SettingsViewModel` (Task 6): `SetMode` and `SaveQuietHours` pass `Override = null` in their `with` expressions, so a
choice in Settings ends any temporary switch (§3.7).

`SectionHost` gains `Action ToggleTheme`; `TodayPage` passes `() => _model.ToggleThemeCommand.Execute(null)`. In
`SectionViews.Header`, add a 44 dp tappable icon at the end of the top bar: a moon (`Icon` geometry, `Ink2`); in
`SectionViews.Night`, a sun next to the title (`Night.Ink2` colour key used by the night title). Use the existing
`Icon` component; add `moon` and `sun` paths to `Theme/IconGeometry.cs` in the style of its other glyphs, and their
`AutomationProperties.Name` from new strings `Theme.ToDay` ("Switch to day colours") and `Theme.ToNight` ("Switch to
night colours"), added to `strings.py` in Task 7 with these translations:

```python
'Theme.ToDay': ('Switch to day colours', 'Zu Tagfarben wechseln', 'Passer aux couleurs de jour', 'Cambiar a colores de día', 'Passa ai colori del giorno', 'Przełącz na kolory dzienne', 'Naar dagkleuren'),
'Theme.ToNight': ('Switch to night colours', 'Zu Nachtfarben wechseln', 'Passer aux couleurs de nuit', 'Cambiar a colores de noche', 'Passa ai colori della notte', 'Przełącz na kolory nocne', 'Naar nachtkleuren'),
```

If `IconGeometryTests` checks that every glyph has a test or a size, extend it for `moon` and `sun`.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Coo.Mobile.Presentation.Tests && dotnet test tests/Coo.Mobile.Infrastructure.Tests`
Expected: PASS, the existing `AppearanceRuleTests` and `ThemeControllerTests` included.

- [ ] **Step 5: Commit**

```bash
git add src/Mobile tests
git commit -m "Night and day from Today, until the next change (Spec 009 §3.7)"
```

---

### Task 6: The Settings and baby view models, ports and licences (Presentation)

**Files:**
- Create: `src/Mobile/Coo.Mobile.Presentation/Settings/Ports.cs`
- Create: `src/Mobile/Coo.Mobile.Presentation/Settings/Licences.cs`
- Create: `src/Mobile/Coo.Mobile.Presentation/Settings/SettingsViewModel.cs`
- Create: `src/Mobile/Coo.Mobile.Presentation/Settings/BabyProfileViewModel.cs`
- Modify: `src/Mobile/Coo.Mobile.Presentation/ServiceCollectionExtensions.cs`
- Test: `tests/Coo.Mobile.Presentation.Tests/SettingsTests.cs`

**Interfaces:**
- Consumes: Task 2's use cases, Task 4's `LanguageController`/`LanguageRule`, `ThemeController.SetAsync`/`Settings`,
  `IHouseholdStore`, `Formats.Age`.
- Produces:
  - `public interface IAboutInfo { string Informational { get; } string OsVersion { get; } }`
  - `public interface IExternalLauncher { Task<bool> TryComposeEmailAsync(string to, string subject, string body); Task OpenBrowserAsync(Uri uri); Task CopyAsync(string text); }`
  - `AppVersionText.Parse(string informational) : (string Version, string Commit)`
  - `Licences.All : IReadOnlyList<Licence>`; `public sealed record Licence(string Name, string LicenceName, string AssetFile, IReadOnlyList<string> PackageIds);`
  - `SettingsViewModel`: `Task RefreshAsync()`, properties `BabyName`, `BabyLine`, `Kinds` (`IReadOnlyList<KindToggle>`),
    `IsOunces`, `Mode`, `QuietStart`/`QuietEnd` (`TimeSpan`), `LanguageLabel`, `Languages`, `VersionLine`, `Toast`;
    commands `SetUnitCommand(VolumeUnit)`, `ToggleKindCommand(KindToggle)`, `SetModeCommand(AppearanceMode)`,
    `SaveQuietHoursCommand`, `ChooseLanguageCommand(LanguageOption)`, `SendFeedbackCommand`, `OpenPrivacyCommand`.
  - `public sealed record KindToggle(string Code, string Name, bool IsShown, bool CanChange);`
  - `BabyProfileViewModel`: `Task LoadAsync()`, `Name`, `Nicknames` (comma-separated), `IsBorn`, `BirthDate`,
    `HasDueDate`, `DueDate`, `Error`, `SaveCommand`, `event EventHandler? Saved`.

- [ ] **Step 1: Write the failing tests**

```csharp
using Coo.Domain.CareLog;
using Coo.Domain.Common.Versioning;
using Coo.Domain.Households;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Application.Households;
using Coo.Mobile.Application.Preferences;
using Coo.Mobile.Presentation.Localization;
using Coo.Mobile.Presentation.Settings;
using Coo.Mobile.Presentation.Theme;
using NSubstitute;

namespace Coo.Mobile.Presentation.Tests;

/// <summary>Spec 009 §3. Ports are substitutes; the use cases and Domain objects are real.</summary>
public sealed class SettingsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 14, 0, 0, TimeSpan.FromHours(2));
    private static readonly HouseholdId Household = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a1"));
    private static readonly BabyId Baby = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000b1"));
    private static readonly DeviceId Device = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000d1"));

    private readonly IHouseholdStore _households = Substitute.For<IHouseholdStore>();
    private readonly IVersionClock _clock = Substitute.For<IVersionClock>();
    private readonly IDevicePreferences _preferences = Substitute.For<IDevicePreferences>();
    private readonly IAboutInfo _about = Substitute.For<IAboutInfo>();
    private readonly IExternalLauncher _launcher = Substitute.For<IExternalLauncher>();
    private readonly FixedTime _time = new(Now);
    private readonly HouseholdSettings _settings;
    private readonly BabyProfile _baby;
    private long _wall = 10;

    public SettingsTests()
    {
        _clock.NextAsync(Arg.Any<CancellationToken>()).Returns(_ => new HlcStamp(++_wall, 0, Device));
        _settings = HouseholdSettings.Create(Household, VolumeUnit.Millilitres, "Europe/Amsterdam", new HlcStamp(1, 0, Device),
            ["feeding", "diaper", "sleep"].Select(c => EntryKindCode.From(c).Value)).Value;
        _baby = BabyProfile.Create(Baby, Household, "Zosia", [], new DateOnly(2026, 8, 20), null, new HlcStamp(1, 0, Device), new DateOnly(2026, 10, 1)).Value;
        _households.ListHouseholdsAsync(Arg.Any<CancellationToken>()).Returns([_settings]);
        _households.ListBabiesAsync(Household, Arg.Any<CancellationToken>()).Returns([_baby]);
        _preferences.GetAppearanceAsync(Arg.Any<CancellationToken>()).Returns(AppearancePreferences.Default);
        _about.Informational.Returns("0.9.0+e03dbdfb884414aa26d8c9f9e01726c9c7a08c35");
        _about.OsVersion.Returns("16");
    }

    [Theory]
    [InlineData("0.9.0+e03dbdfb884414aa26d8c9f9e01726c9c7a08c35", "0.9.0", "e03dbdf")]
    [InlineData("0.9.0", "0.9.0", "")]
    public void Parse_InformationalVersion_SplitsVersionAndShortCommit(string informational, string version, string commit)
    {
        // Arrange
        var text = informational;

        // Act
        var parsed = AppVersionText.Parse(text);

        // Assert
        parsed.Should().Be((version, commit));
    }

    [Fact]
    public async Task RefreshAsync_OnlyPumpVisible_LocksThePumpSwitch()
    {
        // Arrange
        var model = Model();

        // Act
        await model.RefreshAsync();

        // Assert
        var pump = model.Kinds.Single(k => k.Code == "pump");
        pump.IsShown.Should().BeTrue();
        pump.CanChange.Should().BeFalse();
        model.Kinds.Where(k => k.Code != "pump").Should().OnlyContain(k => !k.IsShown && k.CanChange);
    }

    [Fact]
    public async Task SetUnit_Ounces_SavesAndShowsOunces()
    {
        // Arrange
        var model = Model();
        await model.RefreshAsync();

        // Act
        await model.SetUnitCommand.ExecuteAsync(VolumeUnit.Ounces);

        // Assert
        model.IsOunces.Should().BeTrue();
        await _households.Received(1).SaveSettingsAsync(_settings, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RefreshAsync_Version_ShowsVersionAndShortCommit()
    {
        // Arrange
        var model = Model();

        // Act
        await model.RefreshAsync();

        // Assert
        model.VersionLine.Should().Contain("0.9.0").And.Contain("e03dbdf");
    }

    [Fact]
    public async Task SendFeedback_NoMailApp_CopiesTheAddressAndSaysSo()
    {
        // Arrange
        _launcher.TryComposeEmailAsync(default!, default!, default!).ReturnsForAnyArgs(false);
        var model = Model();
        await model.RefreshAsync();

        // Act
        await model.SendFeedbackCommand.ExecuteAsync(null);

        // Assert
        await _launcher.Received(1).CopyAsync(SettingsViewModel.FeedbackAddress);
        model.Toast.Should().Contain(SettingsViewModel.FeedbackAddress);
    }

    [Fact]
    public async Task SaveAsync_NameOfSpaces_ShowsTheErrorAndDoesNotRaiseSaved()
    {
        // Arrange
        var model = new BabyProfileViewModel(_households, new UpdateBabyProfile(_households, _clock, _time), _time);
        await model.LoadAsync();
        var saved = false;
        model.Saved += (_, _) => saved = true;
        model.Name = "   ";

        // Act
        await model.SaveCommand.ExecuteAsync(null);

        // Assert
        model.Error.Should().NotBeEmpty();
        saved.Should().BeFalse();
    }

    [Fact]
    public async Task SaveAsync_TwiceInARow_SucceedsBothTimes()
    {
        // Arrange
        var model = new BabyProfileViewModel(_households, new UpdateBabyProfile(_households, _clock, _time), _time);
        await model.LoadAsync();
        model.Nicknames = "Zo, Mimi";

        // Act
        await model.SaveCommand.ExecuteAsync(null);
        await model.SaveCommand.ExecuteAsync(null);

        // Assert
        model.Error.Should().BeEmpty();
        _baby.Nicknames.Should().Equal("Zo", "Mimi");
        await _households.Received(2).SaveBabyAsync(_baby, Arg.Any<CancellationToken>());
    }

    private SettingsViewModel Model() => new(
        _households, new ChangeVolumeUnit(_households, _clock), new SetHiddenKinds(_households, _clock),
        new ThemeController(_preferences, Substitute.For<IOsAppearance>(), _time), new LanguageController(_preferences),
        _about, _launcher, _time);

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();

        public override TimeZoneInfo LocalTimeZone { get; } = TimeZoneInfo.CreateCustomTimeZone("plus2", now.Offset, "plus2", "plus2");
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Coo.Mobile.Presentation.Tests --filter "FullyQualifiedName~SettingsTests"`
Expected: build error, namespace `Coo.Mobile.Presentation.Settings` not found.

- [ ] **Step 3: Implement**

`Settings/Ports.cs`:

```csharp
namespace Coo.Mobile.Presentation.Settings;

/// <summary>What About shows about the build and the phone (Spec 009 §3.6).</summary>
public interface IAboutInfo
{
    /// <summary>The assembly's informational version, for example <c>0.9.0+e03dbdf…</c>.</summary>
    string Informational { get; }

    string OsVersion { get; }
}

/// <summary>Leaving the app: mail, browser, clipboard (Spec 009 §3.6).</summary>
public interface IExternalLauncher
{
    /// <summary>False when the phone has no mail app.</summary>
    Task<bool> TryComposeEmailAsync(string to, string subject, string body);

    Task OpenBrowserAsync(Uri uri);

    Task CopyAsync(string text);
}

public static class AppVersionText
{
    /// <summary>"0.9.0+e03dbdfb…" → ("0.9.0", "e03dbdf"), the way the API's /health reports it.</summary>
    public static (string Version, string Commit) Parse(string informational)
    {
        ArgumentNullException.ThrowIfNull(informational);
        var plus = informational.IndexOf('+', StringComparison.Ordinal);
        return plus < 0
            ? (informational, "")
            : (informational[..plus], informational[(plus + 1)..][..Math.Min(7, informational.Length - plus - 1)]);
    }
}
```

`Settings/Licences.cs`:

```csharp
namespace Coo.Mobile.Presentation.Settings;

/// <summary>One shipped component and its licence text, bundled as <c>Resources/Raw/licences/{AssetFile}</c>.</summary>
public sealed record Licence(string Name, string LicenceName, string AssetFile, IReadOnlyList<string> PackageIds);

/// <summary>
/// What the app ships (Spec 009 §3.6). A test fails when a mobile project references a package that is not listed
/// here, so the page cannot fall behind.
/// </summary>
public static class Licences
{
    public static IReadOnlyList<Licence> All { get; } =
    [
        new("Figtree", "SIL Open Font License 1.1", "figtree-ofl.txt", []),
        new(".NET and .NET MAUI", "MIT", "dotnet-mit.txt", ["Microsoft.Maui.Controls", "Microsoft.Extensions.DependencyInjection.Abstractions", "Microsoft.Data.Sqlite.Core"]),
        new("CommunityToolkit.Mvvm", "MIT", "communitytoolkit-mit.txt", ["CommunityToolkit.Mvvm"]),
        new("SQLitePCLRaw", "Apache License 2.0", "sqlitepclraw-apache2.txt", ["SQLitePCLRaw.bundle_e_sqlite3"]),
        new("SQLite", "Public domain", "sqlite-public-domain.txt", []),
    ];
}
```

`Settings/SettingsViewModel.cs`:

```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Coo.Domain.CareLog;
using Coo.Domain.Households;
using Coo.Mobile.Application.Households;
using Coo.Mobile.Application.Preferences;
using Coo.Mobile.Presentation.Formatting;
using Coo.Mobile.Presentation.Localization;
using Coo.Mobile.Presentation.Theme;

namespace Coo.Mobile.Presentation.Settings;

/// <summary>One "What you log" switch (Spec 009 §3.2). The last visible kind cannot be switched off.</summary>
public sealed record KindToggle(string Code, string Name, bool IsShown, bool CanChange);

/// <summary>The Settings tab (Spec 009 §3). It knows nothing about MAUI.</summary>
public sealed partial class SettingsViewModel(
    IHouseholdStore households, ChangeVolumeUnit changeUnit, SetHiddenKinds setHidden, ThemeController theme,
    LanguageController language, IAboutInfo about, IExternalLauncher launcher, TimeProvider time) : ObservableObject
{
    public const string FeedbackAddress = "andrzej@lechdigital.nl";
    public static readonly Uri PrivacyUrl = new("https://coo.lechdigital.nl/privacy");

    private static readonly (string Code, string NameKey)[] KindNames =
        [("feeding", "Kind.Feed"), ("diaper", "Kind.Diaper"), ("sleep", "Kind.Sleep"), ("pump", "Kind.Pump")];

    private HouseholdSettings? _household;

    [ObservableProperty]
    private string _babyName = "";

    [ObservableProperty]
    private string _babyLine = "";

    [ObservableProperty]
    private IReadOnlyList<KindToggle> _kinds = [];

    [ObservableProperty]
    private bool _isOunces;

    [ObservableProperty]
    private AppearanceMode _mode;

    [ObservableProperty]
    private TimeSpan _quietStart;

    [ObservableProperty]
    private TimeSpan _quietEnd;

    [ObservableProperty]
    private string _languageLabel = "";

    [ObservableProperty]
    private string _versionLine = "";

    [ObservableProperty]
    private string _toast = "";

    public IReadOnlyList<LanguageOption> Languages => LanguageRule.Options(language.SystemCulture);

    public async Task RefreshAsync()
    {
        _household = (await households.ListHouseholdsAsync(CancellationToken.None)).FirstOrDefault(h => !h.IsDeleted);
        if (_household is null)
            return;
        var babies = await households.ListBabiesAsync(_household.Id, CancellationToken.None);
        if (babies.Count > 0)
        {
            var baby = babies[0];
            BabyName = baby.Name;
            BabyLine = baby.IsBorn
                ? Formats.Age(baby, DateOnly.FromDateTime(time.GetLocalNow().DateTime))
                : Strings.Get("Expecting.Eyebrow");
        }

        var shown = KindNames.Count(k => Today.KindVisibility.IsShown(_household, k.Code));
        Kinds = [.. KindNames.Select(k =>
        {
            var isShown = Today.KindVisibility.IsShown(_household, k.Code);
            return new KindToggle(k.Code, Strings.Get(k.NameKey), isShown, CanChange: !isShown || shown > 1);
        })];
        IsOunces = _household.VolumeUnit == VolumeUnit.Ounces;
        Mode = theme.Settings.Mode;
        (QuietStart, QuietEnd) = (theme.Settings.QuietHours.Start.ToTimeSpan(), theme.Settings.QuietHours.End.ToTimeSpan());
        LanguageLabel = LanguageRule.Label(language.Preference, language.SystemCulture);
        var (version, commit) = AppVersionText.Parse(about.Informational);
        VersionLine = Strings.Format("Settings.Version", version, commit);
    }

    [RelayCommand]
    private async Task SetUnit(VolumeUnit unit)
    {
        if (_household is null)
            return;
        var result = await changeUnit.ExecuteAsync(_household, unit, CancellationToken.None);
        if (result.IsFailure)
            Toast = Strings.Error(result.Error!.Code);
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task ToggleKind(KindToggle toggle)
    {
        if (_household is null || !toggle.CanChange)
            return;
        var result = await setHidden.ExecuteAsync(_household, EntryKindCode.From(toggle.Code).Value, hidden: toggle.IsShown, CancellationToken.None);
        if (result.IsFailure)
            Toast = Strings.Error(result.Error!.Code);
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task SetMode(AppearanceMode mode)
    {
        await theme.SetAsync(theme.Settings with { Mode = mode, Override = null }, CancellationToken.None);
        Mode = mode;
    }

    [RelayCommand]
    private Task SaveQuietHours() =>
        theme.SetAsync(theme.Settings with { QuietHours = new QuietHours(TimeOnly.FromTimeSpan(QuietStart), TimeOnly.FromTimeSpan(QuietEnd)), Override = null }, CancellationToken.None);

    /// <summary>The host rebuilds its pages when the language in effect changes (LanguageController.Changed).</summary>
    [RelayCommand]
    private Task ChooseLanguage(LanguageOption option) => language.SetAsync(option.Preference, CancellationToken.None);

    [RelayCommand]
    private async Task SendFeedback()
    {
        var (version, commit) = AppVersionText.Parse(about.Informational);
        var body = Strings.Format("Settings.FeedbackBody", version, commit, about.OsVersion);
        if (await launcher.TryComposeEmailAsync(FeedbackAddress, Strings.Get("Settings.FeedbackSubject"), body))
            return;
        await launcher.CopyAsync(FeedbackAddress);
        Toast = Strings.Format("Settings.FeedbackCopied", FeedbackAddress);
    }

    [RelayCommand]
    private Task OpenPrivacy() => launcher.OpenBrowserAsync(PrivacyUrl);
}
```

`Settings/BabyProfileViewModel.cs`:

```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Coo.Domain.Households;
using Coo.Mobile.Application.Households;
using Coo.Mobile.Presentation.Localization;

namespace Coo.Mobile.Presentation.Settings;

/// <summary>The baby page (Spec 009 §3.1): every field, one Save.</summary>
public sealed partial class BabyProfileViewModel(IHouseholdStore households, UpdateBabyProfile update, TimeProvider time) : ObservableObject
{
    private BabyProfile? _baby;

    [ObservableProperty]
    private string _name = "";

    /// <summary>Comma-separated, as typed.</summary>
    [ObservableProperty]
    private string _nicknames = "";

    [ObservableProperty]
    private bool _isBorn;

    [ObservableProperty]
    private DateTime _birthDate;

    [ObservableProperty]
    private bool _hasDueDate;

    [ObservableProperty]
    private DateTime _dueDate;

    [ObservableProperty]
    private string _error = "";

    public DateTime Today => time.GetLocalNow().Date;

    public event EventHandler? Saved;

    public async Task LoadAsync()
    {
        var household = (await households.ListHouseholdsAsync(CancellationToken.None)).First(h => !h.IsDeleted);
        _baby = (await households.ListBabiesAsync(household.Id, CancellationToken.None))[0];
        Name = _baby.Name;
        Nicknames = string.Join(", ", _baby.Nicknames);
        IsBorn = _baby.IsBorn;
        BirthDate = (_baby.BirthDate ?? DateOnly.FromDateTime(Today)).ToDateTime(TimeOnly.MinValue);
        HasDueDate = _baby.DueDate is not null;
        DueDate = (_baby.DueDate ?? DateOnly.FromDateTime(Today)).ToDateTime(TimeOnly.MinValue);
    }

    [RelayCommand]
    private async Task Save()
    {
        if (_baby is null)
            return;
        var nicknames = Nicknames.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var change = new BabyProfileChange(
            Name, nicknames, IsBorn ? DateOnly.FromDateTime(BirthDate) : null,
            !IsBorn || HasDueDate ? DateOnly.FromDateTime(DueDate) : null);
        var result = await update.ExecuteAsync(_baby, change, CancellationToken.None);
        Error = result.IsFailure ? Strings.Error(result.Error!.Code) : "";
        if (result.IsSuccess)
            Saved?.Invoke(this, EventArgs.Empty);
    }
}
```

Register in `AddMobilePresentation`:

```csharp
        services.AddSingleton<Settings.SettingsViewModel>();
        services.AddTransient<Settings.BabyProfileViewModel>();
```

`SettingsViewModel` is a singleton like `LogViewModel`: the host rebuilds pages on a language change, and the model
is re-read on appearing.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Coo.Mobile.Presentation.Tests`
Expected: PASS. (The version line and the toast use the new string keys; until Task 7 they fall back to the key
name, which still contains the formatted values only after Task 7. If `RefreshAsync_Version_…` or
`SendFeedback_…` fail on a missing key, do Task 7 first, then re-run.)

- [ ] **Step 5: Commit**

```bash
git add src/Mobile/Coo.Mobile.Presentation tests/Coo.Mobile.Presentation.Tests/SettingsTests.cs
git commit -m "Settings and baby view models, About ports, licences catalogue (Spec 009 §3)"
```

---

### Task 7: The strings, in seven languages

**Files:**
- Modify: `eng/strings/strings.py`
- Generated: `src/Mobile/Coo.Mobile.Presentation/Resources/AppResources*.resx` (by `generate.py`)
- Test: the existing `LocalizationTests` (parity and placeholders)

- [ ] **Step 1: Add the rows** to `S` in `eng/strings/strings.py`, after `'Tab.Log'`:

```python
'Tab.Settings': ('Settings', 'Einstellungen', 'Réglages', 'Ajustes', 'Impostazioni', 'Ustawienia', 'Instellingen'),
```

after the `Error.baby.*` rows:

```python
'Error.baby.nickname-duplicate': ('Each name can be used only once.', 'Jeder Name darf nur einmal vorkommen.', 'Chaque prénom ne peut figurer qu’une fois.', 'Cada nombre solo puede aparecer una vez.', 'Ogni nome può comparire una sola volta.', 'Każde imię może wystąpić tylko raz.', 'Elke naam mag maar één keer voorkomen.'),
'Error.baby.not-born-yet': ('Record the birth on Today first.', 'Trage zuerst auf „Heute“ die Geburt ein.', 'Enregistrez d’abord la naissance dans Aujourd’hui.', 'Registra primero el nacimiento en Hoy.', 'Registra prima la nascita in Oggi.', 'Najpierw zapisz narodziny w zakładce Dziś.', 'Leg eerst de geboorte vast bij Vandaag.'),
'Error.settings.all-kinds-hidden': ('Keep at least one type visible.', 'Lass mindestens eine Art sichtbar.', 'Gardez au moins un type visible.', 'Deja al menos un tipo visible.', 'Lascia visibile almeno un tipo.', 'Zostaw widoczny co najmniej jeden rodzaj.', 'Laat ten minste één soort zichtbaar.'),
```

and a new block:

```python
'Settings.Baby': ('Baby', 'Baby', 'Bébé', 'Bebé', 'Bambino', 'Dziecko', 'Baby'),
'Settings.Kinds': ('What you log', 'Was du einträgst', 'Ce que vous notez', 'Lo que registras', 'Cosa registri', 'Co zapisujesz', 'Wat je bijhoudt'),
'Settings.KindsHint': ('Hidden types keep their past entries.', 'Ausgeblendete Arten behalten ihre Einträge.', 'Les types masqués gardent leurs entrées.', 'Los tipos ocultos conservan sus registros.', 'I tipi nascosti conservano le loro voci.', 'Ukryte rodzaje zachowują swoje wpisy.', 'Verborgen soorten houden hun eerdere items.'),
'Settings.Units': ('Units', 'Einheiten', 'Unités', 'Unidades', 'Unità', 'Jednostki', 'Eenheden'),
'Settings.Volume': ('Volume', 'Menge', 'Volume', 'Volumen', 'Volume', 'Objętość', 'Volume'),
'Settings.ClockHint': ("Time format follows your phone's settings.", 'Das Uhrzeitformat folgt den Einstellungen deines Handys.', 'Le format de l’heure suit les réglages du téléphone.', 'El formato de hora sigue los ajustes del teléfono.', 'Il formato dell’ora segue le impostazioni del telefono.', 'Format godziny jest taki jak w ustawieniach telefonu.', 'De tijdnotatie volgt de instellingen van je telefoon.'),
'Settings.Appearance': ('Appearance', 'Darstellung', 'Apparence', 'Apariencia', 'Aspetto', 'Wygląd', 'Weergave'),
'Settings.Theme.Auto': ('Auto', 'Auto', 'Auto', 'Auto', 'Auto', 'Auto', 'Auto'),
'Settings.Theme.Day': ('Day', 'Tag', 'Jour', 'Día', 'Giorno', 'Dzień', 'Dag'),
'Settings.Theme.Night': ('Night', 'Nacht', 'Nuit', 'Noche', 'Notte', 'Noc', 'Nacht'),
'Settings.AutoHint': ('Night colours during quiet hours, or when your phone is in dark mode.', 'Nachtfarben während der Ruhezeit oder wenn dein Handy im dunklen Modus ist.', 'Couleurs de nuit pendant les heures calmes, ou quand le téléphone est en mode sombre.', 'Colores de noche en las horas de calma, o cuando el teléfono está en modo oscuro.', 'Colori notturni nelle ore di quiete, o quando il telefono è in modalità scura.', 'Nocne kolory w godzinach ciszy albo gdy telefon ma tryb ciemny.', 'Nachtkleuren tijdens de rustige uren, of als je telefoon in de donkere modus staat.'),
'Settings.QuietHours': ('Quiet hours', 'Ruhezeit', 'Heures calmes', 'Horas de calma', 'Ore di quiete', 'Godziny ciszy', 'Rustige uren'),
'Settings.QuietFrom': ('From', 'Von', 'De', 'Desde', 'Dalle', 'Od', 'Van'),
'Settings.QuietTo': ('To', 'Bis', 'À', 'Hasta', 'Alle', 'Do', 'Tot'),
'Settings.QuietHint': ('Night colours and quieter notifications during these hours.', 'In dieser Zeit Nachtfarben und leisere Benachrichtigungen.', 'Couleurs de nuit et notifications plus discrètes pendant ces heures.', 'Colores de noche y avisos más discretos durante estas horas.', 'Colori notturni e notifiche più discrete in queste ore.', 'W tych godzinach nocne kolory i cichsze powiadomienia.', 'Nachtkleuren en stillere meldingen tijdens deze uren.'),
'Settings.Language': ('Language', 'Sprache', 'Langue', 'Idioma', 'Lingua', 'Język', 'Taal'),
'Settings.LanguageSystem': ('System ({0})', 'System ({0})', 'Système ({0})', 'Sistema ({0})', 'Sistema ({0})', 'Systemowy ({0})', 'Systeem ({0})'),
'Settings.About': ('About', 'Über Coo', 'À propos', 'Acerca de', 'Informazioni', 'O aplikacji', 'Over Coo'),
'Settings.Version': ('Coo {0} ({1})', 'Coo {0} ({1})', 'Coo {0} ({1})', 'Coo {0} ({1})', 'Coo {0} ({1})', 'Coo {0} ({1})', 'Coo {0} ({1})'),
'Settings.Licences': ('Open-source licences', 'Open-Source-Lizenzen', 'Licences open source', 'Licencias de código abierto', 'Licenze open source', 'Licencje open source', 'Opensourcelicenties'),
'Settings.Feedback': ('Send feedback', 'Feedback senden', 'Envoyer un avis', 'Enviar comentarios', 'Invia un feedback', 'Wyślij opinię', 'Feedback sturen'),
'Settings.FeedbackSubject': ('Coo feedback', 'Feedback zu Coo', 'Avis sur Coo', 'Comentarios sobre Coo', 'Feedback su Coo', 'Opinia o Coo', 'Feedback over Coo'),
'Settings.FeedbackBody': ('\n\n—\nCoo {0} ({1}), Android {2}', '\n\n—\nCoo {0} ({1}), Android {2}', '\n\n—\nCoo {0} ({1}), Android {2}', '\n\n—\nCoo {0} ({1}), Android {2}', '\n\n—\nCoo {0} ({1}), Android {2}', '\n\n—\nCoo {0} ({1}), Android {2}', '\n\n—\nCoo {0} ({1}), Android {2}'),
'Settings.FeedbackCopied': ('No mail app found. Address copied: {0}', 'Keine Mail-App gefunden. Adresse kopiert: {0}', 'Aucune app de messagerie. Adresse copiée : {0}', 'No hay app de correo. Dirección copiada: {0}', 'Nessuna app di posta. Indirizzo copiato: {0}', 'Brak aplikacji poczty. Skopiowano adres: {0}', 'Geen mailapp gevonden. Adres gekopieerd: {0}'),
'Settings.Privacy': ('Privacy policy', 'Datenschutzerklärung', 'Politique de confidentialité', 'Política de privacidad', 'Informativa sulla privacy', 'Polityka prywatności', 'Privacybeleid'),
'Baby.Nicknames': ('Other names you call them', 'Weitere Namen, die ihr benutzt', 'Autres petits noms', 'Otros nombres que usáis', 'Altri nomi che usate', 'Inne imiona, których używacie', 'Andere namen die jullie gebruiken'),
'Baby.NicknamesHint': ('Separate with commas', 'Mit Kommas trennen', 'Séparés par des virgules', 'Separados por comas', 'Separati da virgole', 'Oddziel przecinkami', 'Scheid met komma’s'),
```

Check the generator escapes `\n` correctly in `.resx` (`escape` keeps it as a real newline, which `.resx` preserves
with `xml:space="preserve"`).

- [ ] **Step 2: Generate and run the parity tests**

Run: `python3 eng/strings/generate.py && dotnet test tests/Coo.Mobile.Presentation.Tests`
Expected: each `.resx` line count rises by the new rows; all tests PASS, including Task 6's.

- [ ] **Step 3: Commit**

```bash
git add eng/strings/strings.py src/Mobile/Coo.Mobile.Presentation/Resources
git commit -m "Settings strings in seven languages (Spec 009)"
```

---

### Task 8: The pages, the tab, language start-up, and About (host)

**Files:**
- Create: `src/Mobile/Coo.Mobile/Pages/SettingsPage.cs`, `BabyPage.cs`, `LicencesPage.cs`
- Create: `src/Mobile/Coo.Mobile/Settings/MauiAboutInfo.cs`, `MauiExternalLauncher.cs`
- Create: `src/Mobile/Coo.Mobile/Resources/Images/tab_settings.svg`
- Create: `src/Mobile/Coo.Mobile/Resources/Raw/licences/figtree-ofl.txt`, `dotnet-mit.txt`, `communitytoolkit-mit.txt`, `sqlitepclraw-apache2.txt`, `sqlite-public-domain.txt`
- Modify: `src/Mobile/Coo.Mobile/AppShell.cs`, `App.xaml.cs`, `StartupPage.cs`, `MauiProgram.cs`, `Platforms/Android/MainActivity.cs`, `Coo.Mobile.csproj`
- Test: `tests/Coo.Mobile.Presentation.Tests/LicencesTests.cs`

**Interfaces:**
- Consumes: everything above.
- Produces: the Settings tab with `Route = "settings"`; `App.ApplyLanguage()` (rebuilds the shell and returns to
  `//settings` when asked).

- [ ] **Step 1: Write the failing licences test**

```csharp
using System.Xml.Linq;
using Coo.Mobile.Presentation.Settings;

namespace Coo.Mobile.Presentation.Tests;

public sealed class LicencesTests
{
    [Fact]
    public void All_EveryMobilePackageReference_HasALicenceEntry()
    {
        // Arrange
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Coo.slnx")))
            root = root.Parent;
        var packages = Directory.GetFiles(Path.Combine(root!.FullName, "src", "Mobile"), "*.csproj", SearchOption.AllDirectories)
            .SelectMany(p => XDocument.Load(p).Descendants("PackageReference"))
            .Select(e => (string?)e.Attribute("Include"))
            .OfType<string>()
            .Distinct();

        // Act
        var missing = packages.Except(Licences.All.SelectMany(l => l.PackageIds)).ToList();

        // Assert
        missing.Should().BeEmpty("every shipped package is listed on the licences page (Spec 009 §3.6)");
    }

    [Fact]
    public void All_EveryEntry_HasItsBundledText()
    {
        // Arrange
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Coo.slnx")))
            root = root.Parent;
        var folder = Path.Combine(root!.FullName, "src", "Mobile", "Coo.Mobile", "Resources", "Raw", "licences");

        // Act
        var missing = Licences.All.Where(l => !File.Exists(Path.Combine(folder, l.AssetFile))).Select(l => l.AssetFile).ToList();

        // Assert
        missing.Should().BeEmpty();
    }
}
```

- [ ] **Step 2: Run it to verify the second test fails**

Run: `dotnet test tests/Coo.Mobile.Presentation.Tests --filter "FullyQualifiedName~LicencesTests"`
Expected: the first PASSes (all five packages are listed); the second FAILS, listing the five missing `.txt` files.

- [ ] **Step 3: Bundle the licence texts**

- `figtree-ofl.txt`: copy `src/Mobile/Coo.Mobile/Resources/Fonts/OFL.txt`.
- `dotnet-mit.txt`: the MIT licence with "Copyright (c) .NET Foundation and Contributors" (the text in
  `~/.nuget/packages/microsoft.maui.controls/<version>/` if a `LICENSE*` file is there; otherwise the standard MIT
  text with that copyright line).
- `communitytoolkit-mit.txt`: MIT, "Copyright © .NET Foundation and Contributors".
- `sqlitepclraw-apache2.txt`: the Apache License 2.0 full text, with "Copyright 2014-2024 SourceGear, LLC" at the top.
  Take it from `~/.nuget/packages/sqlitepclraw.bundle_e_sqlite3/<version>/` (or `sqlitepclraw.core`) if present.
- `sqlite-public-domain.txt`: "The SQLite source code is in the public domain. https://sqlite.org/copyright.html",
  followed by SQLite's blessing ("May you do good and not evil…").

Files in `Resources/Raw` are bundled as MAUI assets by the default `MauiAsset` glob; confirm in
`Coo.Mobile.csproj` (add `<MauiAsset Include="Resources\Raw\**" LogicalName="%(RecursiveDir)%(Filename)%(Extension)" />`
if the project does not use the default).

Run the test again: both PASS.

- [ ] **Step 4: Port implementations**

`Settings/MauiAboutInfo.cs`:

```csharp
using System.Reflection;
using Coo.Mobile.Presentation.Settings;

namespace Coo.Mobile.Settings;

/// <summary>The build's informational version (SourceLink adds the commit) and Android's version.</summary>
internal sealed class MauiAboutInfo : IAboutInfo
{
    public string Informational { get; } =
        typeof(MauiAboutInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? AppInfo.Current.VersionString;

    public string OsVersion => DeviceInfo.Current.VersionString;
}
```

`Settings/MauiExternalLauncher.cs`:

```csharp
using Coo.Mobile.Presentation.Settings;

namespace Coo.Mobile.Settings;

internal sealed class MauiExternalLauncher : IExternalLauncher
{
    public async Task<bool> TryComposeEmailAsync(string to, string subject, string body)
    {
        if (!Email.Default.IsComposeSupported)
            return false;
        try
        {
            await Email.Default.ComposeAsync(new EmailMessage(subject, body, to));
            return true;
        }
        catch (FeatureNotSupportedException)
        {
            return false;
        }
    }

    public Task OpenBrowserAsync(Uri uri) => Browser.Default.OpenAsync(uri, BrowserLaunchMode.SystemPreferred);

    public Task CopyAsync(string text) => Clipboard.Default.SetTextAsync(text);
}
```

Android 11+ needs a `<queries>` entry for `Email.IsComposeSupported` to see mail apps. Add to `AndroidManifest.xml`
inside `<manifest>`:

```xml
  <queries>
    <intent>
      <action android:name="android.intent.action.SENDTO" />
      <data android:scheme="mailto" />
    </intent>
  </queries>
```

- [ ] **Step 5: The pages**

Build them in code, like `SetupPage` and `LogPage`: `Ui.Text(…, "Section")` headers, rows in
`Ui.Rounded(…, "Surface", radius, padding, "Line")`, `PrimaryButton`/`GhostButton` styles, `Shell.SetNavBarIsVisible(this, false)`
on the tab page, an `UndoToastView`-style toast is not needed: show `Toast` with the same `ShowToast` pattern as
`TodayPage` (a label that fades after 3 s).

`Pages/SettingsPage.cs`, top to bottom (each section rebuilds from the model on `PropertyChanged`, as `LogPage.Render` does):

1. Title `Strings.Get("Tab.Settings")` in `Title` style.
2. **Baby:** a row with `BabyName` (Body, bold) and `BabyLine` (Ink2) and a chevron `›`; tap →
   `Navigation.PushAsync(services.GetRequiredService<BabyPage>())`.
3. **What you log:** a row per `KindToggle`: the name, a `Switch` with `IsToggled = IsShown`, `IsEnabled = CanChange`,
   `Toggled += (_, _) => model.ToggleKindCommand.Execute(toggle)`; then `Settings.KindsHint` in Ink2.
4. **Units:** `Settings.Volume` with two buttons `Unit.Ml` / `Unit.Oz` (selected one `PrimaryButton`, the other
   `GhostButton`), commands `SetUnitCommand` with `VolumeUnit.Millilitres` / `VolumeUnit.Ounces`; then `Settings.ClockHint`.
5. **Appearance:** three buttons Auto / Day / Night (`SetModeCommand`), `Settings.AutoHint`; `Settings.QuietHours` with
   two `TimePicker`s bound two-way to `QuietStart` / `QuietEnd` (`Format` = `HH:mm` when `IClockFormat.Is24Hour`, else `h:mm tt`),
   each `PropertyChanged` on `Time` → `SaveQuietHoursCommand.Execute(null)`; then `Settings.QuietHint`.
6. **Language:** a row with `LanguageLabel` and `›`; tap → `DisplayActionSheet(Strings.Get("Settings.Language"), null, null, labels)`
   over `model.Languages`, then `ChooseLanguageCommand.ExecuteAsync(chosen option)`.
7. **About:** `VersionLine` (Ink2); rows `Settings.Licences` (push `LicencesPage`), `Settings.Feedback`
   (`SendFeedbackCommand`), `Settings.Privacy` (`OpenPrivacyCommand`).

`OnAppearing` → `await model.RefreshAsync()`.

`Pages/BabyPage.cs` (pushed, nav bar visible, title `Settings.Baby`): `Setup.Name` + `Entry` (Name),
`Baby.Nicknames` + `Entry` (Nicknames, placeholder `Baby.NicknamesHint`), then when `IsBorn`: `Setup.BirthDate` +
`DatePicker` (max `Today`), a `CheckBox` + `Setup.BornEarly` bound to `HasDueDate`, and `Setup.DueDate` + `DatePicker`
shown when `HasDueDate`; when expected: `Setup.DueDate` + `DatePicker` (max `Today.AddDays(308)`). The error label,
then `Action.Save` as `PrimaryButton` → `SaveCommand`. `Saved += … Navigation.PopAsync()`. Reuse `SetupPage`'s
`DatePicker` helper by copying it (it is private there).

`Pages/LicencesPage.cs` (pushed, title `Settings.Licences`): for each `Licences.All` entry, a card with `Name`
(Body, bold), `LicenceName` (Ink2), and the text read with
`await FileSystem.OpenAppPackageFileAsync($"licences/{l.AssetFile}")` into a `Label` in `Label` style, collapsed
until the card is tapped.

`Resources/Images/tab_settings.svg` (the same stroke style as `tab_log.svg`):

```xml
<svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="#2A2823" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round"><path d="M4 7h9M17 7h3M4 17h3M11 17h9"/><circle cx="15" cy="7" r="2"/><circle cx="9" cy="17" r="2"/></svg>
```

- [ ] **Step 6: The tab, start-up and the language rebuild**

`AppShell.cs`: give each `ShellContent` a `Route` (`"today"`, `"log"`) and add the third:

```csharp
                new ShellContent
                {
                    Title = Strings.Get("Tab.Settings"),
                    Icon = "tab_settings.png",
                    Route = "settings",
                    ContentTemplate = new DataTemplate(services.GetRequiredService<SettingsPage>),
                },
```

and update its summary to "Three tabs: Today, Log and Settings (Spec 006 §2, Spec 009 §2)".

`App.xaml.cs`: capture Android's culture before anything changes it, and replace `ReloadForLanguage` with:

```csharp
    /// <summary>Android's language at process start, before Coo set a culture of its own.</summary>
    public static System.Globalization.CultureInfo SystemCulture { get; } = System.Globalization.CultureInfo.CurrentUICulture;

    /// <summary>
    /// The language in effect changed (Spec 009 §3.5): apply it to the process, and rebuild the pages so every string
    /// is re-read. The data and the palette are untouched. From Settings, the rebuilt shell opens on Settings.
    /// </summary>
    public void ApplyLanguage(bool returnToSettings)
    {
        var culture = Coo.Mobile.Presentation.Localization.Strings.Culture;
        System.Globalization.CultureInfo.CurrentUICulture = culture;
        System.Globalization.CultureInfo.CurrentCulture = culture;
        System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = culture;
        System.Globalization.CultureInfo.DefaultThreadCurrentCulture = culture;

        if (Windows.Count == 0 || Windows[0].Page is not AppShell)
            return;
        var shell = _services.GetRequiredService<AppShell>();
        Windows[0].Page = shell;
        if (returnToSettings)
            shell.Dispatcher.Dispatch(async () => await shell.GoToAsync("//settings"));
    }
```

Initialise `SystemCulture` by touching it in the `App` constructor (first line: `_ = SystemCulture;`) so it is
captured before `StartupPage` runs.

`MainActivity.OnConfigurationChanged`: replace the `ReloadForLanguage(language)` call with

```csharp
            IPlatformApplication.Current?.Services.GetRequiredService<Coo.Mobile.Presentation.Localization.LanguageController>()
                .SystemChanged(System.Globalization.CultureInfo.GetCultureInfo(language!));
```

(the controller raises `Changed` only if the language in effect changes).

`StartupPage.OnAppearing`, after `await _household.ExecuteAsync(...)` and before the theme lines:

```csharp
        // The language (Spec 009 §3.5), before the first real page is built.
        var language = _services.GetRequiredService<Coo.Mobile.Presentation.Localization.LanguageController>();
        await language.StartAsync(App.SystemCulture, CancellationToken.None);
        var app = (App)Microsoft.Maui.Controls.Application.Current!;
        app.ApplyLanguage(returnToSettings: false);
        language.Changed += (_, _) => MainThread.BeginInvokeOnMainThread(() => app.ApplyLanguage(returnToSettings: Shell.Current?.CurrentState.Location.OriginalString.Contains("settings", StringComparison.Ordinal) == true));
```

`MauiProgram.cs`: register

```csharp
        builder.Services.AddSingleton<Coo.Mobile.Presentation.Settings.IAboutInfo, Settings.MauiAboutInfo>();
        builder.Services.AddSingleton<Coo.Mobile.Presentation.Settings.IExternalLauncher, Settings.MauiExternalLauncher>();
        builder.Services.AddTransient<SettingsPage>();
        builder.Services.AddTransient<BabyPage>();
        builder.Services.AddTransient<LicencesPage>();
```

`Coo.Mobile.csproj`: `ApplicationDisplayVersion` `0.9.0`, `ApplicationVersion` `12`, and right after them
`<Version>$(ApplicationDisplayVersion)</Version>` so the informational version is `0.9.0+<commit>`.

- [ ] **Step 7: Build, test, and check on the Mac**

Run: `dotnet build src/Mobile/Coo.Mobile -c Release && dotnet test Coo.Tests.slnf`
Expected: 0 warnings; all tests PASS (architecture tests included: the new Presentation files use no MAUI types).

- [ ] **Step 8: Commit**

```bash
git add src/Mobile tests/Coo.Mobile.Presentation.Tests/LicencesTests.cs
git commit -m "Settings tab, baby and licences pages, language start-up, version 0.9.0 (Spec 009)"
```

---

### Task 9: On the phone, docs, and the findings

**Files:**
- Modify: `docs/design/design-system.md` (bottom navigation row; "settings" leaves "Components still to be designed")
- Modify: `docs/specs/009-settings/spec.md` (§6 deviation; §10 Built and verified)
- Modify: `docs/ROADMAP.md` (row 9: Done), `docs/HANDOVER-2026-09-30.md` (Resume here)

- [ ] **Step 1: Install on the Pixel 10** the way `BUILD.md` describes (Release, the owner's device), and walk the
  spec §8 deliverables 1–7 with the owner. Record every finding and its fix, as Spec 007 §12 does.

- [ ] **Step 2: Update the docs**

- `design-system.md` bottom navigation: "Five tabs at most: Today, Log, Insights, Family, Settings. Today, Log and
  Settings ship first; Insights and Family arrive with their features." Remove "settings" from the to-be-designed
  list.
- Spec 009 §6, the ports row: "implemented in `Coo.Mobile` (MAUI Essentials); `Coo.Mobile.Platform` has no MAUI
  reference". Spec 009 §3.4: the quiet-hours line is "Night colours and quieter notifications during these hours."
- Spec 009 new §10 "Built and verified (date, Pixel 10)": the deliverables table and the findings.
- ROADMAP row 9 → Done; handover "Resume here" → next is Increment 10, Identity and households (Spec 010).

- [ ] **Step 3: Push and confirm CI**

```bash
git add docs
git commit -m "Increment 9: Settings, verified on a Pixel 10 (Spec 009)"
git push
gh run watch
```

Expected: CI green.
