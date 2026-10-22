# The Log day chart (Increment 9b) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or
> superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the Log ribbon with a rhythm band (sleep filled, feeds of their real length, diapers and pumps below,
quiet hours, now) and open a zoomed day, 6 hours per screen, when the band is tapped, with labels and a way into each
entry.

**Architecture:** Application's `GetDay` marks gain the entry ID and a session's real end. Presentation gains a pure
`DayChart` (chart items with labels, awake stretches, quiet-hour bands, hour ticks, the range title, hit testing, the
screen-reader summary), and `LogViewModel` exposes its results. The MAUI host redraws `RibbonView` as the band, adds a
`DayDetailView`, and shows it in the existing `Sheet` on the Log page, which owns and closes it.

**Tech Stack:** .NET 10, MAUI (Android) `GraphicsView`, CommunityToolkit.Mvvm, xUnit 2.9 + NSubstitute +
FluentAssertions.

**Spec:** `docs/specs/009b-log-day-chart/spec.md` (accepted 2026-10-02).

## Global Constraints

- Commits authored as `adziusmaster <adzius.lech@gmail.com>` (repo-local config). **No `Co-Authored-By` trailer.**
- Zero warnings (`TreatWarningsAsErrors`); the architecture tests stay green.
- No `TimeProvider.System`, `DateTime.Now` or `DateTimeOffset.Now` outside the composition root (`BannedSymbols.txt`).
- No MAUI types in `Coo.Mobile.Application` or `Coo.Mobile.Presentation` (ADR 0001).
- Every user-facing string goes in `eng/strings/strings.py` in all seven languages (en, de, fr, es, it, pl, nl), then
  `python3 eng/strings/generate.py`. Copy must be true for the public release.
- Times on the chart are in the **household time zone**, never the test machine's or the phone's (CI runs in UTC).
- Tests: `MethodName_StateUnderTest_ExpectedBehavior`, strict `// Arrange` / `// Act` / `// Assert`, mock only
  outbound ports, build Domain objects for real. Tests that set a language join `[Collection(nameof(StringsState))]`
  and reset with `Strings.Use(LanguagePreference.System, null)` in `Dispose`.
- No stored data changes: no migrations, no outbox rows.
- Run tests with `dotnet test Coo.Tests.slnf` (global SDK). Build the app with the MAUI SDK (`BUILD.md`) and
  **`-p:EmbedAssembliesIntoApk=true`** when installing with `adb install`: without it a Debug APK has no assemblies
  and crashes at launch ("No assemblies found … Fast Deployment").
- Colours from the live `Palette` only (`WashiPalette.For(theme.Current)`), never literals.

## Review Focus

1. **A feed during a sleep** (a dream feed): tapping it in the zoomed day must open the feed, not the sleep around it.
   Pinned in Task 3 (`HitTest_FeedInsideSleep_PicksTheFeed`).
2. **Two overlapping sleeps** (a duplicate kept with "Keep both"): no negative or zero awake stretch between them.
   Pinned in Task 2 (`Build_OverlappingSleeps_AddsNoAwakeBetweenThem`).
3. **The autumn clock change** (a 25-hour day): the hours run 00, 01, 02, 02, 03 … 24 and the bands stay in place.
   Pinned in Task 3 (`Hours_AutumnClockChange_Has26TicksWithTwoTwos`).
4. **A return to Log within the same minute** must not redraw the page (the owner's Pixel 10 finding): the "now"
   position is rounded to the minute. Pinned in Task 4 (`RefreshAsync_TwentySecondsLater_RaisesNothing`).
5. **An empty day**: the band draws empty, the zoomed day opens with only the hours, and a tap finds nothing. Pinned in
   Task 3 (`HitTest_NoItems_ReturnsNull`) and Task 2 (`Build_NoEntries_ReturnsNoItems`).

---

## File Structure

| File | Responsibility |
|---|---|
| `src/Mobile/Coo.Mobile.Application/CareLog/Queries.cs` (modify) | `RibbonMark` gains `EntryId`; feeds and pumps use their session's end |
| `src/Mobile/Coo.Mobile.Presentation/Formatting/Formats.cs` (modify) | `Time(instant, zone)`: a time in a given zone |
| `src/Mobile/Coo.Mobile.Presentation/Log/DayChart.cs` (create) | Pure: `ChartItem`, `ChartBand`, `HourTick`; `Build`, `QuietBands`, `Hours`, `RangeTitle`, `HitTest`, `Summary` |
| `src/Mobile/Coo.Mobile.Presentation/Log/LogViewModel.cs` (modify) | Exposes `ChartItems`, `QuietBands`, `Hours`, `NowAt`, `ChartSummary`, `RangeTitle(from, to)`; drops `Ribbon` |
| `eng/strings/strings.py` (modify) + generated `.resx` | `Chart.*` keys |
| `src/Mobile/Coo.Mobile/Components/RibbonView.cs` (rewrite) | The band, and `Tapped(fraction)` |
| `src/Mobile/Coo.Mobile/Components/DayDetailView.cs` (create) | The zoomed day: scrolling canvas, labels, title, position bar, `EntryTapped` |
| `src/Mobile/Coo.Mobile/Pages/LogPage.cs` (modify) | Band wiring, "now" updates without a full redraw, the sheet, Back |
| `tests/Coo.Mobile.Application.Tests/DayQueriesTests.cs` (modify) | Mark tests |
| `tests/Coo.Mobile.Presentation.Tests/DayChartTests.cs` (create) | `DayChart` tests |
| `tests/Coo.Mobile.Presentation.Tests/LogViewModelTests.cs` (modify) | View model tests |
| `docs/design/design-system.md`, `docs/specs/006-today-and-quick-logging/spec.md` (modify) | The "Day ribbon" entry and Spec 006 §7 point to Spec 009b |

---

### Task 1: `GetDay` marks carry their entry and a session's end

**Files:**
- Modify: `src/Mobile/Coo.Mobile.Application/CareLog/Queries.cs:22-23` and the `switch` at `:105-123`
- Test: `tests/Coo.Mobile.Application.Tests/DayQueriesTests.cs`

**Interfaces:**
- Produces: `public sealed record RibbonMark(string KindCode, double From, double To, bool IsBlock, EntryId EntryId);`
  For a sleep, a breast feed and a pump, `To` is the session's end (a running one: now on today, the day's end on a
  past day), clamped to the day. For a diaper and a bottle, `To == From`.

- [ ] **Step 1: Write the failing tests**

Add to `DayQueriesTests`, and give the test class the `Breast` and `Pump` helpers below (next to `Bottle`). Change
`Make`'s `timing` parameter to `EntryTiming?` (a breast feed takes its timing from its segments).

```csharp
    [Fact]
    public async Task ExecuteAsync_BreastFeed_MarkRunsFromStartToEndWithItsEntryId()
    {
        // Arrange
        var feed = Breast(At(10, 0), 30);
        var store = new FakeStore(feed);

        // Act
        var day = await new GetDay(store).ExecuteAsync(Household, Baby, new DateOnly(2026, 10, 1), Amsterdam, Now, CancellationToken.None);

        // Assert
        var mark = day.Ribbon.Should().ContainSingle().Which;
        mark.EntryId.Should().Be(feed.Id);
        mark.From.Should().BeApproximately(10.0 / 24, 1e-9);
        mark.To.Should().BeApproximately(10.5 / 24, 1e-9);
    }

    [Fact]
    public async Task ExecuteAsync_Bottle_MarkIsAPoint()
    {
        // Arrange
        var store = new FakeStore(Bottle(At(8, 0)));

        // Act
        var day = await new GetDay(store).ExecuteAsync(Household, Baby, new DateOnly(2026, 10, 1), Amsterdam, Now, CancellationToken.None);

        // Assert
        var mark = day.Ribbon.Should().ContainSingle().Which;
        mark.To.Should().Be(mark.From);
    }

    [Fact]
    public async Task ExecuteAsync_PumpSession_MarkRunsToItsEnd()
    {
        // Arrange
        var store = new FakeStore(Pump(At(9, 0), At(9, 30)));

        // Act
        var day = await new GetDay(store).ExecuteAsync(Household, Baby, new DateOnly(2026, 10, 1), Amsterdam, Now, CancellationToken.None);

        // Assert
        day.Ribbon.Should().ContainSingle().Which.To.Should().BeApproximately(9.5 / 24, 1e-9);
    }

    [Fact]
    public async Task ExecuteAsync_SleepRunningToday_MarkEndsAtNow()
    {
        // Arrange: now is 14:00.
        var store = new FakeStore(Sleep(At(13, 0), null));

        // Act
        var day = await new GetDay(store).ExecuteAsync(Household, Baby, new DateOnly(2026, 10, 1), Amsterdam, Now, CancellationToken.None);

        // Assert
        day.Ribbon.Should().ContainSingle().Which.To.Should().BeApproximately(14.0 / 24, 1e-9);
    }

    [Fact]
    public async Task ExecuteAsync_SleepRunningSinceYesterday_YesterdaysMarkEndsAtMidnight()
    {
        // Arrange
        var store = new FakeStore(Sleep(At(23, 0).AddDays(-1), null));

        // Act
        var day = await new GetDay(store).ExecuteAsync(Household, Baby, new DateOnly(2026, 9, 30), Amsterdam, Now, CancellationToken.None);

        // Assert
        day.Ribbon.Should().ContainSingle().Which.To.Should().Be(1);
    }
```

Helpers (add `using Coo.Domain.CareLog.Kinds.Pump;`):

```csharp
    private CareEntry Breast(DateTimeOffset start, int rightMinutes) =>
        Make(EntrySubject.Baby(Baby), BreastFeed.FromSideDurations(start, [(BreastSide.Right, rightMinutes)]).Value, null);

    private CareEntry Pump(DateTimeOffset start, DateTimeOffset end) =>
        Make(EntrySubject.Caregiver(PersonRef.ThisDevicesPerson), new PumpDetails(), EntryTiming.Session(TimeRange.Create(start, end).Value));
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test Coo.Tests.slnf --filter "FullyQualifiedName~DayQueriesTests"`
Expected: build error, `RibbonMark` has no `EntryId`.

- [ ] **Step 3: Implement**

In `Queries.cs`:

```csharp
/// <summary>
/// One mark on the day chart, as fractions of the day (Spec 006 §7, Spec 009b §2): a sleep block, or a feed, diaper or
/// pump. A session runs to its end (a running one to now, or the day's end); a point has <c>To == From</c>.
/// </summary>
public sealed record RibbonMark(string KindCode, double From, double To, bool IsBlock, EntryId EntryId);
```

and in the `switch` of `GetDay.ExecuteAsync`:

```csharp
                case SleepDetails:
                    sleep += day.Overlap(start, end);
                    ribbon.Add(new RibbonMark(entry.KindCode.Value, day.Fraction(start), day.Fraction(end), IsBlock: true, entry.Id));
                    break;
                case FeedingDetails when day.Contains(start):
                    feeds++;
                    ribbon.Add(new RibbonMark(entry.KindCode.Value, day.Fraction(start), day.Fraction(end), IsBlock: false, entry.Id));
                    break;
                case DiaperDetails when day.Contains(start):
                    diapers++;
                    ribbon.Add(new RibbonMark(entry.KindCode.Value, day.Fraction(start), day.Fraction(start), IsBlock: false, entry.Id));
                    break;
                case PumpDetails pump when day.Contains(start):
                    pumped += pump.Yield?.Total.Millilitres ?? 0m;
                    ribbon.Add(new RibbonMark(entry.KindCode.Value, day.Fraction(start), day.Fraction(end), IsBlock: false, entry.Id));
                    break;
```

(`end` already equals `start` for a point, and is now or the day's end for a running session.) Fix any other
`new RibbonMark(` call sites the build reports (`grep -rn "new RibbonMark(" src tests`).

- [ ] **Step 4: Run the tests**

Run: `dotnet test Coo.Tests.slnf --filter "FullyQualifiedName~DayQueriesTests"`
Expected: PASS, including the existing midnight and totals tests.

- [ ] **Step 5: Commit**

```bash
git add src/Mobile/Coo.Mobile.Application/CareLog/Queries.cs tests/Coo.Mobile.Application.Tests/DayQueriesTests.cs
git commit -m "GetDay: chart marks carry their entry and a session's end (Spec 009b §4)"
```

---

### Task 2: `DayChart.Build`: the items, their labels, the awake stretches

**Files:**
- Modify: `src/Mobile/Coo.Mobile.Presentation/Formatting/Formats.cs` (add `Time(instant, zone)`)
- Create: `src/Mobile/Coo.Mobile.Presentation/Log/DayChart.cs`
- Modify: `eng/strings/strings.py`, then regenerate
- Test: `tests/Coo.Mobile.Presentation.Tests/DayChartTests.cs` (create)

**Interfaces:**
- Consumes: `RibbonMark` (Task 1), `DaySummary`, `LocalDay`, `StoredEntry`/`LiveEntry`, `Formats`.
- Produces:
  ```csharp
  public enum ChartShape { Sleep, Awake, Feed, Diaper, Pump }
  public sealed record ChartItem(ChartShape Shape, string KindCode, double From, double To, EntryId? EntryId, string Label);
  public static IReadOnlyList<ChartItem> DayChart.Build(DaySummary day, Formats formats, TimeZoneInfo zone, Func<string, bool> isShown);
  public string Formats.Time(DateTimeOffset instant, TimeZoneInfo zone);
  ```
  Items are ordered by `From`. `Awake` items have no `EntryId` and `KindCode` `"sleep"`.

- [ ] **Step 1: Add the string**

In `eng/strings/strings.py`, after the `'Duration.Minutes'` row:

```python
'Chart.Awake': ('awake {0}', 'wach {0}', 'éveil {0}', 'vigilia {0}', 'veglia {0}', 'czuwanie {0}', 'wakker {0}'),
```

Run: `python3 eng/strings/generate.py`

- [ ] **Step 2: Write the failing tests**

Create `tests/Coo.Mobile.Presentation.Tests/DayChartTests.cs`:

```csharp
using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Kinds.Diaper;
using Coo.Domain.CareLog.Kinds.Feeding;
using Coo.Domain.CareLog.Kinds.Pump;
using Coo.Domain.CareLog.Kinds.Sleep;
using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;
using Coo.Domain.Households;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Application.Preferences;
using Coo.Mobile.Presentation.Formatting;
using Coo.Mobile.Presentation.Localization;
using Coo.Mobile.Presentation.Log;
using NSubstitute;

namespace Coo.Mobile.Presentation.Tests;

[Collection(nameof(StringsState))]
public sealed class DayChartTests : IDisposable
{
    private static readonly TimeZoneInfo Amsterdam = TimeZoneInfo.FindSystemTimeZoneById("Europe/Amsterdam");
    private static readonly TimeSpan Cest = TimeSpan.FromHours(2);
    private static readonly HouseholdId Household = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a1"));
    private static readonly BabyId Baby = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000b1"));
    private static readonly DeviceId Device = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000d1"));
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 23, 0, 0, Cest);
    private static readonly DateOnly Date = new(2026, 10, 1);
    private long _wall = 1;

    public DayChartTests() => Strings.Use(new LanguagePreference("en"), null);

    public void Dispose() => Strings.Use(LanguagePreference.System, null);

    [Fact]
    public async Task Build_NoEntries_ReturnsNoItems()
    {
        // Arrange
        var day = await Day();

        // Act
        var items = DayChart.Build(day, Ml, Amsterdam, _ => true);

        // Assert
        items.Should().BeEmpty();
    }

    [Fact]
    public async Task Build_TwoSleeps_AddsTheAwakeStretchBetweenThem()
    {
        // Arrange
        var day = await Day(Sleep(At(9, 0), At(10, 0)), Sleep(At(11, 40), At(12, 0)));

        // Act
        var items = DayChart.Build(day, Ml, Amsterdam, _ => true);

        // Assert
        var awake = items.Should().ContainSingle(i => i.Shape == ChartShape.Awake).Which;
        awake.From.Should().BeApproximately(10.0 / 24, 1e-9);
        awake.To.Should().BeApproximately((11 + 40 / 60.0) / 24, 1e-9);
        awake.Label.Should().Be("awake 1 h 40 m");
        awake.EntryId.Should().BeNull();
    }

    [Fact]
    public async Task Build_OneSleep_AddsNoAwakeBeforeOrAfterIt()
    {
        // Arrange
        var day = await Day(Sleep(At(9, 0), At(10, 0)));

        // Act
        var items = DayChart.Build(day, Ml, Amsterdam, _ => true);

        // Assert
        items.Should().ContainSingle().Which.Shape.Should().Be(ChartShape.Sleep);
    }

    [Fact]
    public async Task Build_OverlappingSleeps_AddsNoAwakeBetweenThem()
    {
        // Arrange: a duplicate kept with "Keep both".
        var day = await Day(Sleep(At(9, 0), At(10, 0)), Sleep(At(9, 30), At(10, 30)));

        // Act
        var items = DayChart.Build(day, Ml, Amsterdam, _ => true);

        // Assert
        items.Should().NotContain(i => i.Shape == ChartShape.Awake);
    }

    [Fact]
    public async Task Build_Sleep_LabelsItsLength()
    {
        // Arrange
        var day = await Day(Sleep(At(15, 0), At(16, 30)));

        // Act
        var items = DayChart.Build(day, Ml, Amsterdam, _ => true);

        // Assert
        items.Single().Label.Should().Be("1 h 30 m");
    }

    [Fact]
    public async Task Build_SleepAcrossMidnight_IsCutAndLabelledWithTheDaysPart()
    {
        // Arrange: 22:00 yesterday to 02:00 today.
        var day = await Day(Sleep(At(22, 0).AddDays(-1), At(2, 0)));

        // Act
        var items = DayChart.Build(day, Ml, Amsterdam, _ => true);

        // Assert
        var sleep = items.Single();
        sleep.From.Should().Be(0);
        sleep.Label.Should().Be("2 h 0 m");
    }

    [Fact]
    public async Task Build_BreastFeed_LabelsTimeSideAndLength()
    {
        // Arrange
        var feed = Breast(At(14, 10), (BreastSide.Right, 30));
        var day = await Day(feed);

        // Act
        var items = DayChart.Build(day, Ml, Amsterdam, _ => true);

        // Assert
        var item = items.Single();
        item.Shape.Should().Be(ChartShape.Feed);
        item.EntryId.Should().Be(feed.Id);
        item.Label.Should().Be("14:10 R 30 m");
    }

    [Fact]
    public async Task Build_BreastFeedBothSides_LabelsEachSide()
    {
        // Arrange
        var day = await Day(Breast(At(14, 10), (BreastSide.Left, 10), (BreastSide.Right, 5)));

        // Act
        var items = DayChart.Build(day, Ml, Amsterdam, _ => true);

        // Assert
        items.Single().Label.Should().Be("14:10 L 10 m · R 5 m");
    }

    [Fact]
    public async Task Build_BreastFeedInPolish_UsesPolishSideAndUnits()
    {
        // Arrange
        Strings.Use(new LanguagePreference("pl"), null);
        var day = await Day(Breast(At(14, 10), (BreastSide.Right, 30)));

        // Act
        var items = DayChart.Build(day, Ml, Amsterdam, _ => true);

        // Assert
        items.Single().Label.Should().Be("14:10 P 30 min");
    }

    [Fact]
    public async Task Build_BottleInAnOuncesHousehold_LabelsOunces()
    {
        // Arrange
        var day = await Day(Bottle(At(17, 20), 120));

        // Act
        var items = DayChart.Build(day, new Formats(true, VolumeUnit.Ounces), Amsterdam, _ => true);

        // Assert
        items.Single().Label.Should().Be("17:20 4.1 oz");
    }

    [Fact]
    public async Task Build_BottleWithTwelveHourClock_LabelsMillilitres()
    {
        // Arrange
        var day = await Day(Bottle(At(17, 20), 120));

        // Act
        var items = DayChart.Build(day, new Formats(false, VolumeUnit.Millilitres), Amsterdam, _ => true);

        // Assert
        items.Single().Label.Should().Be("5:20 PM 120 ml");
    }

    [Theory]
    [InlineData(DiaperContents.Wet, "Wet")]
    [InlineData(DiaperContents.Dirty, "Dirty")]
    [InlineData(DiaperContents.Mixed, "Mixed")]
    public async Task Build_Diaper_LabelsItsContents(DiaperContents contents, string expected)
    {
        // Arrange
        var day = await Day(Diaper(At(14, 45), contents));

        // Act
        var items = DayChart.Build(day, Ml, Amsterdam, _ => true);

        // Assert
        items.Single().Should().Match<ChartItem>(i => i.Shape == ChartShape.Diaper && i.Label == expected);
    }

    [Fact]
    public async Task Build_PumpWithYield_LabelsTimeAndVolume()
    {
        // Arrange
        var day = await Day(Pump(At(21, 0), At(21, 20), 90));

        // Act
        var items = DayChart.Build(day, Ml, Amsterdam, _ => true);

        // Assert
        items.Single().Label.Should().Be("21:00 90 ml");
    }

    [Fact]
    public async Task Build_PumpWithoutYield_LabelsTheTimeOnly()
    {
        // Arrange
        var day = await Day(Pump(At(21, 0), At(21, 20), null));

        // Act
        var items = DayChart.Build(day, Ml, Amsterdam, _ => true);

        // Assert
        items.Single().Label.Should().Be("21:00");
    }

    [Fact]
    public async Task Build_HiddenKind_LeavesTheItems()
    {
        // Arrange
        var day = await Day(Pump(At(21, 0), At(21, 20), 90), Diaper(At(14, 45), DiaperContents.Wet));

        // Act
        var items = DayChart.Build(day, Ml, Amsterdam, kind => kind != "pump");

        // Assert
        items.Should().ContainSingle().Which.Shape.Should().Be(ChartShape.Diaper);
    }

    [Fact]
    public void Time_InTheHouseholdZone_IgnoresTheMachinesZone()
    {
        // Arrange: 12:10 UTC is 14:10 in Amsterdam, whatever zone the test machine is in.
        var instant = new DateTimeOffset(2026, 10, 1, 12, 10, 0, TimeSpan.Zero);

        // Act
        var text = Ml.Time(instant, Amsterdam);

        // Assert
        text.Should().Be("14:10");
    }

    private static Formats Ml => new(true, VolumeUnit.Millilitres);

    private static DateTimeOffset At(int hour, int minute) => new(2026, 10, 1, hour, minute, 0, Cest);

    private static async Task<DaySummary> Day(params CareEntry[] entries)
    {
        var store = Substitute.For<ICareLogStore>();
        store.ListAsync(default, default, default, default)
            .ReturnsForAnyArgs([.. entries.Select(e => (StoredEntry)new LiveEntry(e))]);
        return await new GetDay(store).ExecuteAsync(Household, Baby, Date, Amsterdam, Now, CancellationToken.None);
    }

    private CareEntry Sleep(DateTimeOffset start, DateTimeOffset end) =>
        Make(EntrySubject.Baby(Baby), new SleepDetails(), EntryTiming.Session(TimeRange.Create(start, end).Value));

    private CareEntry Breast(DateTimeOffset start, params (BreastSide Side, int Minutes)[] sides) =>
        Make(EntrySubject.Baby(Baby), BreastFeed.FromSideDurations(start, sides).Value, null);

    private CareEntry Bottle(DateTimeOffset at, int ml) =>
        Make(EntrySubject.Baby(Baby), BottleFeed.Of(Milk.Formula, Volume.FromMillilitres(ml).Value).Value, EntryTiming.Point(at));

    private CareEntry Diaper(DateTimeOffset at, DiaperContents contents) =>
        Make(EntrySubject.Baby(Baby), new DiaperDetails(contents), EntryTiming.Point(at));

    private CareEntry Pump(DateTimeOffset start, DateTimeOffset end, int? ml) =>
        Make(EntrySubject.Caregiver(PersonRef.ThisDevicesPerson),
            new PumpDetails(ml is { } v ? PumpYield.OfTotal(Volume.FromMillilitres(v).Value) : null),
            EntryTiming.Session(TimeRange.Create(start, end).Value));

    private CareEntry Make(EntrySubject subject, EntryDetails details, EntryTiming? timing) =>
        CareEntry.Create(new EntryId(Guid.CreateVersion7(Now.AddMilliseconds(++_wall))), Household, subject, details, timing,
            PersonRef.ThisDevicesPerson, EntrySource.Manual, new HlcStamp(++_wall, 0, Device), Now).Value;
}
```

If `Diaper.Dirty`'s English text in `strings.py` is not `Dirty`, use the text there.

- [ ] **Step 3: Run them to verify they fail**

Run: `dotnet test Coo.Tests.slnf --filter "FullyQualifiedName~DayChartTests"`
Expected: build error, `DayChart` does not exist.

- [ ] **Step 4: Implement**

In `Formats.cs`, after `Time(DateTimeOffset)`:

```csharp
    /// <summary>"14:05" or "2:05 PM" in the given zone: the chart uses the household's, so it reads the same on every phone.</summary>
    public string Time(DateTimeOffset instant, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(instant, zone).ToString(is24Hour ? "HH:mm" : "h:mm tt", Strings.Culture);
```

Create `src/Mobile/Coo.Mobile.Presentation/Log/DayChart.cs`:

```csharp
using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Kinds.Diaper;
using Coo.Domain.CareLog.Kinds.Feeding;
using Coo.Domain.CareLog.Kinds.Pump;
using Coo.Domain.CareLog.Kinds.Sleep;
using Coo.Domain.Common;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Presentation.Formatting;
using Coo.Mobile.Presentation.Localization;

namespace Coo.Mobile.Presentation.Log;

public enum ChartShape
{
    Sleep,
    Awake,
    Feed,
    Diaper,
    Pump,
}

/// <summary>One thing on the day chart, as fractions of the day, with its label for the zoomed day (Spec 009b §3).</summary>
public sealed record ChartItem(ChartShape Shape, string KindCode, double From, double To, EntryId? EntryId, string Label);

/// <summary>The day chart's content (Spec 009b), pure: the band and the zoomed day draw what this returns.</summary>
public static class DayChart
{
    /// <summary>
    /// The day's sleeps, feeds, diapers and pumps, and the awake stretches between consecutive sleeps (none before the
    /// first or after the last: the day before or after may hold the sleep that bounds them, Q6). Hidden kinds leave.
    /// </summary>
    public static IReadOnlyList<ChartItem> Build(DaySummary day, Formats formats, TimeZoneInfo zone, Func<string, bool> isShown)
    {
        ArgumentNullException.ThrowIfNull(day);
        ArgumentNullException.ThrowIfNull(formats);
        ArgumentNullException.ThrowIfNull(isShown);

        var entries = day.Entries.OfType<LiveEntry>().ToDictionary(e => e.Id, e => e.Entry);
        var items = new List<ChartItem>();
        foreach (var mark in day.Ribbon.Where(m => isShown(m.KindCode)))
        {
            if (!entries.TryGetValue(mark.EntryId, out var entry))
                continue;
            var time = formats.Time(entry.Timing.OccurredAt, zone);
            ChartItem? item = entry.Details switch
            {
                SleepDetails => new(ChartShape.Sleep, mark.KindCode, mark.From, mark.To, mark.EntryId, Formats.Duration(Span(day.Day, mark.From, mark.To))),
                BreastFeed when entry.Timing.IsInProgress => new(ChartShape.Feed, mark.KindCode, mark.From, mark.To, mark.EntryId, $"{time} {Strings.Get("Entry.Running")}"),
                BreastFeed f => new(ChartShape.Feed, mark.KindCode, mark.From, mark.To, mark.EntryId, $"{time} {Sides(f)}"),
                BottleFeed b => new(ChartShape.Feed, mark.KindCode, mark.From, mark.To, mark.EntryId, $"{time} {formats.Volume(b.Total)}"),
                DiaperDetails d => new(ChartShape.Diaper, mark.KindCode, mark.From, mark.To, mark.EntryId, Formats.Contents(d.Contents)),
                PumpDetails { Yield: { } y } => new(ChartShape.Pump, mark.KindCode, mark.From, mark.To, mark.EntryId, $"{time} {formats.Volume(y.Total)}"),
                PumpDetails => new(ChartShape.Pump, mark.KindCode, mark.From, mark.To, mark.EntryId, time),
                _ => null,
            };
            if (item is not null)
                items.Add(item);
        }

        items.AddRange(AwakeStretches(items, day.Day));
        return [.. items.OrderBy(i => i.From).ThenBy(i => i.Shape)];
    }

    private static IEnumerable<ChartItem> AwakeStretches(List<ChartItem> items, LocalDay day)
    {
        var sleeps = items.Where(i => i.Shape == ChartShape.Sleep).OrderBy(i => i.From).ToList();
        if (sleeps.Count < 2)
            yield break;
        var awakeFrom = sleeps[0].To;
        foreach (var next in sleeps.Skip(1))
        {
            var gap = Span(day, awakeFrom, next.From);
            if (gap >= TimeSpan.FromMinutes(1))
                yield return new ChartItem(ChartShape.Awake, "sleep", awakeFrom, next.From, null, Strings.Format("Chart.Awake", Formats.Duration(gap)));
            awakeFrom = Math.Max(awakeFrom, next.To);
        }
    }

    /// <summary>"R 30 m", or "L 10 m · R 5 m": each side used, with its time.</summary>
    private static string Sides(BreastFeed feed)
    {
        var parts = new List<string>();
        if (feed.LeftDuration > TimeSpan.Zero)
            parts.Add($"{Formats.Side(BreastSide.Left, shortForm: true)} {Formats.Duration(feed.LeftDuration)}");
        if (feed.RightDuration > TimeSpan.Zero)
            parts.Add($"{Formats.Side(BreastSide.Right, shortForm: true)} {Formats.Duration(feed.RightDuration)}");
        return string.Join(" · ", parts);
    }

    internal static TimeSpan Span(LocalDay day, double from, double to) =>
        TimeSpan.FromTicks((long)Math.Round((to - from) * day.Length.Ticks));
}
```

- [ ] **Step 5: Run the tests**

Run: `dotnet test Coo.Tests.slnf --filter "FullyQualifiedName~DayChartTests|FullyQualifiedName~LocalizationTests"`
Expected: PASS (the parity test sees `Chart.Awake` in all seven languages).

- [ ] **Step 6: Commit**

```bash
git add eng/strings src/Mobile/Coo.Mobile.Presentation tests/Coo.Mobile.Presentation.Tests/DayChartTests.cs
git commit -m "DayChart: chart items with labels and awake stretches (Spec 009b §3)"
```

---

### Task 3: `DayChart`: quiet hours, hours, the range title, hit testing, the summary

**Files:**
- Modify: `src/Mobile/Coo.Mobile.Presentation/Log/DayChart.cs`
- Modify: `eng/strings/strings.py`, then regenerate
- Test: `tests/Coo.Mobile.Presentation.Tests/DayChartTests.cs`

**Interfaces:**
- Consumes: `ChartItem`, `ChartShape` (Task 2), `QuietHours` (`Coo.Mobile.Application.Preferences`), `LocalDay`.
- Produces:
  ```csharp
  public sealed record ChartBand(double From, double To);
  public sealed record HourTick(double At, int Hour, string Label);
  public static IReadOnlyList<ChartBand> DayChart.QuietBands(QuietHours quiet, LocalDay day, TimeZoneInfo zone);
  public static IReadOnlyList<HourTick> DayChart.Hours(LocalDay day, TimeZoneInfo zone, Formats formats);
  public static string DayChart.RangeTitle(LocalDay day, bool isToday, double from, double to, Formats formats, TimeZoneInfo zone);
  public static ChartItem? DayChart.HitTest(IReadOnlyList<ChartItem> items, double at, double tolerance);
  public static string DayChart.Summary(IReadOnlyList<ChartItem> items, LocalDay day, Func<string, bool> isShown);
  ```
  `HourTick.Hour` is the local hour (0–23), and 24 for the day's end. `RangeTitle` rounds both ends to 5 minutes.

- [ ] **Step 1: Add the strings**

In `eng/strings/strings.py`, after `'Chart.Awake'` (the summary lists counts as numbers, so it needs no plural forms;
the spec's wording was an example):

```python
'Chart.Range': ('{0}, {1} – {2}', '{0}, {1} – {2}', '{0}, {1} – {2}', '{0}, {1} – {2}', '{0}, {1} – {2}', '{0}, {1} – {2}', '{0}, {1} – {2}'),
'Chart.SummarySleep': ('Sleep {0}', 'Schlaf {0}', 'Sommeil {0}', 'Sueño {0}', 'Sonno {0}', 'Sen {0}', 'Slaap {0}'),
'Chart.SummaryFeeds': ('Feeds {0}', 'Mahlzeiten {0}', 'Repas {0}', 'Tomas {0}', 'Pasti {0}', 'Karmienia {0}', 'Voedingen {0}'),
'Chart.SummaryDiapers': ('Diapers {0}', 'Windeln {0}', 'Couches {0}', 'Pañales {0}', 'Pannolini {0}', 'Pieluszki {0}', 'Luiers {0}'),
'Chart.SummaryPumps': ('Pumping {0}', 'Abpumpen {0}', 'Tirages {0}', 'Extracciones {0}', 'Tiralatte {0}', 'Odciąganie {0}', 'Kolven {0}'),
'Chart.Hint': ('Double-tap for the day in detail', 'Doppeltippen für den Tag im Detail', 'Touchez deux fois pour le détail de la journée', 'Toca dos veces para ver el día en detalle', 'Tocca due volte per il dettaglio della giornata', 'Stuknij dwukrotnie, aby zobaczyć szczegóły dnia', 'Dubbeltik voor de dag in detail'),
```

Run: `python3 eng/strings/generate.py`

- [ ] **Step 2: Write the failing tests**

Add to `DayChartTests` (add `using Coo.Mobile.Application.Preferences;` if missing):

```csharp
    [Fact]
    public void QuietBands_Overnight_AreTwoBandsAtTheDaysEnds()
    {
        // Arrange
        var day = DayMath.Day(Date, Amsterdam);

        // Act
        var bands = DayChart.QuietBands(QuietHours.Default, day, Amsterdam);

        // Assert
        bands.Should().HaveCount(2);
        bands[0].Should().Be(new ChartBand(0, 7.0 / 24));
        bands[1].From.Should().BeApproximately(22.0 / 24, 1e-9);
        bands[1].To.Should().Be(1);
    }

    [Fact]
    public void QuietBands_WithinTheDay_IsOneBand()
    {
        // Arrange
        var day = DayMath.Day(Date, Amsterdam);

        // Act
        var bands = DayChart.QuietBands(new QuietHours(new TimeOnly(13, 0), new TimeOnly(15, 0)), day, Amsterdam);

        // Assert
        bands.Should().ContainSingle();
        bands[0].From.Should().BeApproximately(13.0 / 24, 1e-9);
        bands[0].To.Should().BeApproximately(15.0 / 24, 1e-9);
    }

    [Fact]
    public void QuietBands_StartEqualsEnd_IsNone()
    {
        // Arrange
        var day = DayMath.Day(Date, Amsterdam);

        // Act
        var bands = DayChart.QuietBands(new QuietHours(new TimeOnly(22, 0), new TimeOnly(22, 0)), day, Amsterdam);

        // Assert
        bands.Should().BeEmpty();
    }

    [Fact]
    public void Hours_OrdinaryDay_Has25TicksFrom00To24()
    {
        // Arrange
        var day = DayMath.Day(Date, Amsterdam);

        // Act
        var hours = DayChart.Hours(day, Amsterdam, Ml);

        // Assert
        hours.Should().HaveCount(25);
        hours[0].Should().Be(new HourTick(0, 0, "00:00"));
        hours[13].Label.Should().Be("13:00");
        hours[^1].Hour.Should().Be(24);
        hours[^1].At.Should().Be(1);
    }

    [Fact]
    public void Hours_AutumnClockChange_Has26TicksWithTwoTwos()
    {
        // Arrange: on 25 October 2026 Amsterdam goes from 03:00 back to 02:00.
        var day = DayMath.Day(new DateOnly(2026, 10, 25), Amsterdam);

        // Act
        var hours = DayChart.Hours(day, Amsterdam, Ml);

        // Assert
        hours.Should().HaveCount(26);
        hours.Select(h => h.Hour).Take(5).Should().Equal(0, 1, 2, 2, 3);
    }

    [Fact]
    public void RangeTitle_Today_NamesTodayAndRoundsToFiveMinutes()
    {
        // Arrange
        var day = DayMath.Day(Date, Amsterdam);

        // Act
        var title = DayChart.RangeTitle(day, isToday: true, (12 * 60 + 31) / 1440.0, (18 * 60 + 29) / 1440.0, Ml, Amsterdam);

        // Assert
        title.Should().Be("Today, 12:30 – 18:30");
    }

    [Fact]
    public void RangeTitle_PastDay_NamesTheDate()
    {
        // Arrange
        var day = DayMath.Day(new DateOnly(2026, 9, 30), Amsterdam);

        // Act
        var title = DayChart.RangeTitle(day, isToday: false, 3 / 24.0, 9 / 24.0, Ml, Amsterdam);

        // Assert
        title.Should().Be("Wed 30 Sep, 03:00 – 09:00");
    }

    [Fact]
    public void HitTest_NoItems_ReturnsNull()
    {
        // Arrange
        IReadOnlyList<ChartItem> items = [];

        // Act
        var hit = DayChart.HitTest(items, 0.5, 0.01);

        // Assert
        hit.Should().BeNull();
    }

    [Fact]
    public void HitTest_FeedInsideSleep_PicksTheFeed()
    {
        // Arrange: a dream feed at 23:00 inside a sleep from 21:00 to 24:00.
        var sleep = new ChartItem(ChartShape.Sleep, "sleep", 21 / 24.0, 1, new EntryId(Guid.CreateVersion7()), "3 h 0 m");
        var feed = new ChartItem(ChartShape.Feed, "feeding", 23 / 24.0, 23.25 / 24, new EntryId(Guid.CreateVersion7()), "23:00 R 15 m");

        // Act
        var hit = DayChart.HitTest([sleep, feed], 23.1 / 24, 0.002);

        // Assert
        hit.Should().Be(feed);
    }

    [Fact]
    public void HitTest_NearAPoint_FindsItWithinTheTolerance()
    {
        // Arrange
        var diaper = new ChartItem(ChartShape.Diaper, "diaper", 0.5, 0.5, new EntryId(Guid.CreateVersion7()), "Wet");

        // Act
        var near = DayChart.HitTest([diaper], 0.505, 0.008);
        var far = DayChart.HitTest([diaper], 0.52, 0.008);

        // Assert
        near.Should().Be(diaper);
        far.Should().BeNull();
    }

    [Fact]
    public void HitTest_AwakeStretch_IsNeverHit()
    {
        // Arrange
        var awake = new ChartItem(ChartShape.Awake, "sleep", 0.4, 0.5, null, "awake 2 h 24 m");

        // Act
        var hit = DayChart.HitTest([awake], 0.45, 0.01);

        // Assert
        hit.Should().BeNull();
    }

    [Fact]
    public async Task Summary_VisibleKinds_ListsSleepAndCounts()
    {
        // Arrange
        var day = await Day(Sleep(At(9, 0), At(10, 30)), Diaper(At(11, 0), DiaperContents.Wet), Pump(At(21, 0), At(21, 20), 90));
        var items = DayChart.Build(day, Ml, Amsterdam, kind => kind != "pump");

        // Act
        var summary = DayChart.Summary(items, day.Day, kind => kind != "pump");

        // Assert
        summary.Should().Be("Sleep 1 h 30 m; Feeds 0; Diapers 1");
    }
```

- [ ] **Step 3: Run them to verify they fail**

Run: `dotnet test Coo.Tests.slnf --filter "FullyQualifiedName~DayChartTests"`
Expected: build errors for `QuietBands`, `Hours`, `RangeTitle`, `HitTest`, `Summary`, `ChartBand`, `HourTick`.

- [ ] **Step 4: Implement**

Add to `DayChart.cs` (`using Coo.Mobile.Application.Preferences;`), records next to `ChartItem`:

```csharp
/// <summary>A stretch of the day, as fractions: a quiet-hours wash.</summary>
public sealed record ChartBand(double From, double To);

/// <summary>An hour boundary: where it falls, the local hour (24 for the day's end), and its label ("13:00", "1:00 PM").</summary>
public sealed record HourTick(double At, int Hour, string Label);
```

and methods inside `DayChart`:

```csharp
    /// <summary>Quiet hours as washes behind the band (Spec 009b §2): one band, two across midnight, none when start equals end.</summary>
    public static IReadOnlyList<ChartBand> QuietBands(QuietHours quiet, LocalDay day, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(quiet);
        ArgumentNullException.ThrowIfNull(day);
        ArgumentNullException.ThrowIfNull(zone);
        if (quiet.Start == quiet.End)
            return [];

        double At(TimeOnly time)
        {
            var local = day.Date.ToDateTime(time);
            return day.Fraction(new DateTimeOffset(local, zone.GetUtcOffset(local)));
        }

        return quiet.Start < quiet.End
            ? [new ChartBand(At(quiet.Start), At(quiet.End))]
            : [new ChartBand(0, At(quiet.End)), new ChartBand(At(quiet.Start), 1)];
    }

    /// <summary>Every hour boundary of the day, in the household zone: 25 on most days, 24 or 26 at the clock changes.</summary>
    public static IReadOnlyList<HourTick> Hours(LocalDay day, TimeZoneInfo zone, Formats formats)
    {
        ArgumentNullException.ThrowIfNull(day);
        ArgumentNullException.ThrowIfNull(formats);
        var ticks = new List<HourTick>();
        for (var instant = day.Start; instant <= day.End; instant = instant.AddHours(1))
        {
            var hour = instant == day.End ? 24 : TimeZoneInfo.ConvertTime(instant, zone).Hour;
            ticks.Add(new HourTick(day.Fraction(instant), hour, formats.Time(instant, zone)));
        }

        return ticks;
    }

    /// <summary>The zoomed day's title (Spec 009b §3): "Today, 12:30 – 18:30", "Wed 30 Sep, 03:00 – 09:00".</summary>
    public static string RangeTitle(LocalDay day, bool isToday, double from, double to, Formats formats, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(day);
        ArgumentNullException.ThrowIfNull(formats);
        DateTimeOffset At(double fraction)
        {
            var instant = day.Start + Span(day, 0, fraction);
            var fiveMinutes = TimeSpan.FromMinutes(5).Ticks;
            return new DateTimeOffset((instant.UtcTicks + fiveMinutes / 2) / fiveMinutes * fiveMinutes, TimeSpan.Zero);
        }

        var name = isToday ? Strings.Get("Section.Today") : day.Date.ToString("ddd d MMM", Strings.Culture);
        return Strings.Format("Chart.Range", name, formats.Time(At(from), zone), formats.Time(At(to), zone));
    }

    /// <summary>
    /// The item under a tap in the zoomed day (Spec 009b §3): the nearest within the tolerance; where several are as near,
    /// the shortest, so a feed during a sleep opens the feed. Awake stretches are not entries and are never hit.
    /// </summary>
    public static ChartItem? HitTest(IReadOnlyList<ChartItem> items, double at, double tolerance)
    {
        ArgumentNullException.ThrowIfNull(items);
        return items
            .Where(i => i.EntryId is not null)
            .Select(i => (Item: i, Distance: at < i.From ? i.From - at : at > i.To ? at - i.To : 0))
            .Where(c => c.Distance <= tolerance)
            .OrderBy(c => c.Distance)
            .ThenBy(c => c.Item.To - c.Item.From)
            .Select(c => c.Item)
            .FirstOrDefault();
    }

    /// <summary>What a screen reader says for the band (Spec 009b §2): "Sleep 14 h 5 m; Feeds 7; Diapers 7; Pumping 2".</summary>
    public static string Summary(IReadOnlyList<ChartItem> items, LocalDay day, Func<string, bool> isShown)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(isShown);
        var parts = new List<string>();
        if (isShown("sleep"))
        {
            var sleep = items.Where(i => i.Shape == ChartShape.Sleep).Aggregate(TimeSpan.Zero, (sum, i) => sum + Span(day, i.From, i.To));
            parts.Add(Strings.Format("Chart.SummarySleep", Formats.Duration(sleep)));
        }

        if (isShown("feeding"))
            parts.Add(Strings.Format("Chart.SummaryFeeds", items.Count(i => i.Shape == ChartShape.Feed)));
        if (isShown("diaper"))
            parts.Add(Strings.Format("Chart.SummaryDiapers", items.Count(i => i.Shape == ChartShape.Diaper)));
        if (isShown("pump"))
            parts.Add(Strings.Format("Chart.SummaryPumps", items.Count(i => i.Shape == ChartShape.Pump)));
        return string.Join("; ", parts);
    }
```

- [ ] **Step 5: Run the tests**

Run: `dotnet test Coo.Tests.slnf --filter "FullyQualifiedName~DayChartTests|FullyQualifiedName~LocalizationTests"`
Expected: PASS. If `RangeTitle_PastDay_NamesTheDate` fails only on the abbreviation (.NET's English culture writes
`Wed 30 Sep`), adjust nothing else: the format string is the spec's.

- [ ] **Step 6: Commit**

```bash
git add eng/strings src/Mobile/Coo.Mobile.Presentation tests/Coo.Mobile.Presentation.Tests/DayChartTests.cs
git commit -m "DayChart: quiet hours, hours, range title, hit test, summary (Spec 009b §2-§3)"
```

---

### Task 4: `LogViewModel` exposes the chart

**Files:**
- Modify: `src/Mobile/Coo.Mobile.Presentation/Log/LogViewModel.cs`
- Test: `tests/Coo.Mobile.Presentation.Tests/LogViewModelTests.cs`

**Interfaces:**
- Consumes: everything `DayChart` produces (Tasks 2–3); `ThemeController.Settings.QuietHours`.
- Produces (on `LogViewModel`, replacing `Ribbon`):
  ```csharp
  public IReadOnlyList<ChartItem> ChartItems { get; }
  public IReadOnlyList<ChartBand> QuietBands { get; }
  public IReadOnlyList<HourTick> Hours { get; }
  public double? NowAt { get; }          // today only, rounded to the minute
  public string ChartSummary { get; }
  public string RangeTitle(double from, double to);
  ```
  Constructor: `LogViewModel(IHouseholdStore households, GetDay getDay, IClockFormat clock, UndoService undo, ThemeController theme, TimeProvider time)`.

- [ ] **Step 1: Write the failing tests**

In `LogViewModelTests`, change `Model()` to take a time and to build the new constructor, and add the tests:

```csharp
    [Fact]
    public async Task RefreshAsync_Today_PutsNowAt14()
    {
        // Arrange
        var model = Model();

        // Act
        await model.RefreshAsync();

        // Assert
        model.NowAt.Should().BeApproximately(14.0 / 24, 1e-9);
    }

    [Fact]
    public async Task RefreshAsync_PastDay_HasNoNow()
    {
        // Arrange
        var model = Model();
        await model.RefreshAsync();

        // Act
        await model.PreviousCommand.ExecuteAsync(null);

        // Assert
        model.NowAt.Should().BeNull();
    }

    [Fact]
    public async Task RefreshAsync_DefaultQuietHours_ShadesBothEndsOfTheDay()
    {
        // Arrange
        var model = Model();

        // Act
        await model.RefreshAsync();

        // Assert
        model.QuietBands.Should().HaveCount(2);
        model.Hours.Should().HaveCount(25);
    }

    [Fact]
    public async Task RefreshAsync_TwentySecondsLater_RaisesNothing()
    {
        // Arrange: the owner's Pixel 10 finding — a return to Log must not redraw it.
        var time = new MovableTime(Now);
        var model = Model(time);
        await model.RefreshAsync();
        var changed = new List<string?>();
        model.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        time.Now = Now.AddSeconds(20);

        // Act
        await model.RefreshAsync();

        // Assert
        changed.Should().BeEmpty();
    }

    [Fact]
    public async Task RangeTitle_AfterRefresh_NamesToday()
    {
        // Arrange
        var model = Model();
        await model.RefreshAsync();

        // Act
        var title = model.RangeTitle(12.5 / 24, 18.5 / 24);

        // Assert
        title.Should().Contain("12:30").And.Contain("18:30");
    }
```

Replace the factory and the time fake:

```csharp
    private static LogViewModel Model(TimeProvider? timeProvider = null)
    {
        var households = Substitute.For<IHouseholdStore>();
        var settings = HouseholdSettings.Create(Household, VolumeUnit.Millilitres, "Europe/Amsterdam", new HlcStamp(1, 0, Device)).Value;
        var baby = BabyProfile.Create(Baby, Household, "Zosia", [], new DateOnly(2026, 5, 1), null, new HlcStamp(1, 0, Device), new DateOnly(2026, 10, 1)).Value;
        households.ListHouseholdsAsync(Arg.Any<CancellationToken>()).Returns([settings]);
        households.ListBabiesAsync(Household, Arg.Any<CancellationToken>()).Returns([baby]);
        var store = Substitute.For<ICareLogStore>();
        store.ListAsync(default, default, default, default).ReturnsForAnyArgs(Array.Empty<StoredEntry>());
        var clock = Substitute.For<IClockFormat>();
        clock.Is24Hour.Returns(true);
        var time = timeProvider ?? new MovableTime(Now);
        var versions = Substitute.For<IVersionClock>();
        var undo = new UndoService(new DeleteEntry(store, versions), new ReviseEntry(store, versions, time), store, time);
        var theme = new Coo.Mobile.Presentation.Theme.ThemeController(
            Substitute.For<IDevicePreferences>(), Substitute.For<Coo.Mobile.Presentation.Theme.IOsAppearance>(), time);
        return new LogViewModel(households, new GetDay(store), clock, undo, theme, time);
    }

    private sealed class MovableTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();

        public override TimeZoneInfo LocalTimeZone { get; } = TimeZoneInfo.CreateCustomTimeZone("plus2", now.Offset, "plus2", "plus2");
    }
```

(Delete the old `FixedTime` class from this file; `IDevicePreferences` is in `Coo.Mobile.Application.Preferences`,
already imported. If `IOsAppearance` lives in another namespace, the build says where.) The `RangeTitle` test runs in
whatever language the machine has; it asserts only the times.

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test Coo.Tests.slnf --filter "FullyQualifiedName~LogViewModelTests"`
Expected: build errors (`NowAt`, `QuietBands`, `Hours`, `RangeTitle`, constructor).

- [ ] **Step 3: Implement**

In `LogViewModel.cs` (add `using Coo.Mobile.Presentation.Theme;`), change the primary constructor to
`(IHouseholdStore households, GetDay getDay, IClockFormat clock, UndoService undo, ThemeController theme, TimeProvider time)`,
replace the `_ribbon` field with:

```csharp
    [ObservableProperty]
    private IReadOnlyList<ChartItem> _chartItems = [];

    [ObservableProperty]
    private IReadOnlyList<ChartBand> _quietBands = [];

    [ObservableProperty]
    private IReadOnlyList<HourTick> _hours = [];

    /// <summary>Where now falls on today's chart, rounded to the minute so a quick return to Log changes nothing; null on other days.</summary>
    [ObservableProperty]
    private double? _nowAt;

    [ObservableProperty]
    private string _chartSummary = "";

    private (LocalDay Day, bool IsToday, Formats Formats, TimeZoneInfo Zone)? _shown;

    /// <summary>The zoomed day's title for the part in view (Spec 009b §3).</summary>
    public string RangeTitle(double from, double to) =>
        _shown is { } s ? DayChart.RangeTitle(s.Day, s.IsToday, from, to, s.Formats, s.Zone) : "";
```

and in `RefreshAsync`, replace `Ribbon = Same(Ribbon, day.Ribbon);` with:

```csharp
        bool IsShown(string kind) => KindVisibility.IsShown(household, kind);
        var isToday = Date == today;
        _shown = (day.Day, isToday, formats, zone);
        var items = DayChart.Build(day, formats, zone, IsShown);
        ChartItems = Same(ChartItems, items);
        QuietBands = Same(QuietBands, DayChart.QuietBands(theme.Settings.QuietHours, day.Day, zone));
        Hours = Same(Hours, DayChart.Hours(day.Day, zone, formats));
        NowAt = isToday ? Math.Round(day.Day.Fraction(now) * day.Day.Length.TotalMinutes) / day.Day.Length.TotalMinutes : null;
        ChartSummary = DayChart.Summary(items, day.Day, IsShown);
```

(`[ObservableProperty]` raises nothing when a value is unchanged, so `NowAt` and `ChartSummary` stay quiet within the
minute.)

- [ ] **Step 4: Run all tests**

Run: `dotnet test Coo.Tests.slnf`
Expected: PASS. The architecture tests still pass (no MAUI in Presentation).

- [ ] **Step 5: Commit**

```bash
git add src/Mobile/Coo.Mobile.Presentation/Log/LogViewModel.cs tests/Coo.Mobile.Presentation.Tests/LogViewModelTests.cs
git commit -m "LogViewModel: the day chart's items, quiet hours, hours and now (Spec 009b §2)"
```

---

### Task 5: The band on Log

**Files:**
- Rewrite: `src/Mobile/Coo.Mobile/Components/RibbonView.cs`
- Modify: `src/Mobile/Coo.Mobile/Pages/LogPage.cs` (`Render`, `OnChanged`)

**Interfaces:**
- Consumes: `LogViewModel.ChartItems`, `QuietBands`, `Hours`, `NowAt`, `ChartSummary` (Task 4); `Palette`.
- Produces: `RibbonView.Show(IReadOnlyList<ChartItem> items, IReadOnlyList<ChartBand> quiet, IReadOnlyList<HourTick> hours, double? now, Palette palette)`;
  `event EventHandler<double>? Tapped` (the tapped fraction of the day, 0–1).

There is no unit test for drawing (MAUI host); the check is on the phone.

- [ ] **Step 1: Rewrite `RibbonView.cs`**

```csharp
using Coo.Mobile.Presentation.Log;
using Coo.Mobile.Presentation.Theme;

namespace Coo.Mobile.Components;

/// <summary>
/// The rhythm band (Spec 009b §2): sleep fills the band, feeds are pills of their real length above it, diapers dots
/// and pumps squares below it; quiet hours are a wash behind it, today has a "now" line. Drawn in the live palette.
/// A tap reports where in the day it fell.
/// </summary>
public sealed class RibbonView : GraphicsView
{
    private readonly BandDrawable _drawable = new();
    private PointF? _down;

    public RibbonView()
    {
        Drawable = _drawable;
        HeightRequest = 112;
        StartInteraction += (_, e) => _down = e.Touches.FirstOrDefault();
        EndInteraction += (_, e) =>
        {
            var up = e.Touches.FirstOrDefault();
            if (_down is { } down && e.IsInsideBounds && Width > 0 && Math.Abs(up.X - down.X) < 16)
                Tapped?.Invoke(this, Math.Clamp(up.X / Width, 0, 1));
            _down = null;
        };
    }

    /// <summary>The fraction of the day that was tapped.</summary>
    public event EventHandler<double>? Tapped;

    public void Show(IReadOnlyList<ChartItem> items, IReadOnlyList<ChartBand> quiet, IReadOnlyList<HourTick> hours, double? now, Palette palette)
    {
        _drawable.Items = items;
        _drawable.Quiet = quiet;
        _drawable.Hours = hours;
        _drawable.Now = now;
        _drawable.Palette = palette;
        Invalidate();
    }

    private sealed class BandDrawable : IDrawable
    {
        private const float Top = 34, BandHeight = 36;

        public IReadOnlyList<ChartItem> Items { get; set; } = [];

        public IReadOnlyList<ChartBand> Quiet { get; set; } = [];

        public IReadOnlyList<HourTick> Hours { get; set; } = [];

        public double? Now { get; set; }

        public Palette Palette { get; set; } = WashiPalette.Day;

        public void Draw(ICanvas canvas, RectF rect)
        {
            var left = rect.X + 1;
            var width = rect.Width - 2;
            float X(double fraction) => left + (float)(fraction * width);
            Color C(string hex) => Color.FromArgb(hex);

            foreach (var band in Quiet)
            {
                canvas.FillColor = C(Palette.SleepTint).WithAlpha(0.45f);
                canvas.FillRoundedRectangle(X(band.From), 14, X(band.To) - X(band.From), 84, 10);
            }

            var bar = new RectF(left, Top, width, BandHeight);
            canvas.FillColor = C(Palette.Surface);
            canvas.FillRoundedRectangle(bar, 12);
            canvas.StrokeColor = C(Palette.Line);
            canvas.StrokeSize = 1;
            canvas.DrawRoundedRectangle(bar, 12);
            foreach (var tick in Hours.Where(h => h.Hour % 3 == 0 && h.At is > 0 and < 1))
            {
                canvas.StrokeColor = C(Palette.Line);
                canvas.DrawLine(X(tick.At), Top + 2, X(tick.At), Top + BandHeight - 2);
            }

            foreach (var item in Items)
            {
                var x = X(item.From);
                switch (item.Shape)
                {
                    case ChartShape.Sleep:
                        canvas.FillColor = C(Palette.SleepMark).WithAlpha(0.85f);
                        canvas.FillRoundedRectangle(x + 0.5f, Top + 3, Math.Max(3, X(item.To) - x - 1), BandHeight - 6, 9);
                        break;
                    case ChartShape.Feed:
                        canvas.FillColor = C(Palette.FeedMark);
                        canvas.FillRoundedRectangle(x, Top - 16, Math.Max(7, X(item.To) - x), 11, 5.5f);
                        break;
                    case ChartShape.Diaper:
                        canvas.FillColor = C(Palette.DiaperMark);
                        canvas.FillCircle(x, Top + BandHeight + 10, 4.5f);
                        break;
                    case ChartShape.Pump:
                        canvas.FillColor = C(Palette.PumpMark);
                        canvas.FillRoundedRectangle(x - 4.5f, Top + BandHeight + 18, 9, 9, 2.5f);
                        break;
                }
            }

            if (Now is { } now)
            {
                canvas.StrokeColor = C(Palette.Accent);
                canvas.StrokeSize = 2;
                canvas.DrawLine(X(now), 14, X(now), 96);
                canvas.FillColor = C(Palette.Accent);
                canvas.FillCircle(X(now), 14, 3.5f);
            }

            foreach (var tick in Hours.Where(h => h.Hour % 3 == 0))
            {
                var major = tick.Hour % 6 == 0;
                canvas.FontColor = C(Palette.Ink2).WithAlpha(major ? 1f : 0.7f);
                canvas.FontSize = major ? 11 : 9;
                var x = X(tick.At);
                var align = tick.At <= 0 ? HorizontalAlignment.Left : tick.At >= 1 ? HorizontalAlignment.Right : HorizontalAlignment.Center;
                var boxX = tick.At <= 0 ? x : tick.At >= 1 ? x - 40 : x - 20;
                canvas.DrawString(tick.Hour.ToString("00", System.Globalization.CultureInfo.InvariantCulture), boxX, 98, 40, 14, align, VerticalAlignment.Top);
            }
        }
    }
}
```

- [ ] **Step 2: Wire it in `LogPage`**

Add a field `private RibbonView? _ribbon;`. In `Render`, replace the two ribbon lines with:

```csharp
        _ribbon = new RibbonView();
        ShowRibbon();
        SemanticProperties.SetDescription(_ribbon, _model.ChartSummary);
        SemanticProperties.SetHint(_ribbon, Strings.Get("Chart.Hint"));
```

and use `_ribbon` in the `Children` list where `ribbon` was (the legend already follows it, above the totals). Add:

```csharp
    private void ShowRibbon() =>
        _ribbon?.Show(_model.ChartItems, _model.QuietBands, _model.Hours, _model.NowAt, WashiPalette.For(_theme.Current));
```

Replace `OnChanged` so a moved "now" redraws only the band:

```csharp
    // "Now" moves every minute: it redraws the band, not the page.
    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LogViewModel.NowAt))
            Dispatcher.Dispatch(ShowRibbon);
        else
            QueueRender();
    }
```

- [ ] **Step 3: Build, install, check on the phone**

```sh
export DOTNET_ROOT=$HOME/dotnet-maui PATH=$HOME/dotnet-maui:$PATH JAVA_HOME=$HOME/Library/Java/JavaVirtualMachines/jdk-17.0.20+8/Contents/Home
dotnet build src/Mobile/Coo.Mobile/Coo.Mobile.csproj -c Debug -f net10.0-android -p:UseDefaultPublishRuntimeIdentifier=false \
  -p:EmbedAssembliesIntoApk=true -p:AndroidSdkDirectory=$HOME/android-sdk -p:JavaSdkDirectory=$JAVA_HOME
~/android-sdk/platform-tools/adb install -r src/Mobile/Coo.Mobile/bin/Debug/net10.0-android/com.lechdigital.coo-Signed.apk
```

Expected on Log: sleep filled in, feed pills of their length above, diaper dots and pump squares below, the 3-hour
labels, quiet hours shaded at both ends, the green "now" line on today and not on yesterday. Then in Settings switch
to Night: the band follows. (Screenshot: `adb exec-out screencap -p > band.png`.) **Ask the owner before driving the
phone; do not log anything on their real data.**

- [ ] **Step 4: Commit**

```bash
git add src/Mobile/Coo.Mobile/Components/RibbonView.cs src/Mobile/Coo.Mobile/Pages/LogPage.cs
git commit -m "Log: the rhythm band (Spec 009b §2)"
```

---

### Task 6: The zoomed day

**Files:**
- Create: `src/Mobile/Coo.Mobile/Components/DayDetailView.cs`
- Modify: `src/Mobile/Coo.Mobile/Pages/LogPage.cs`

**Interfaces:**
- Consumes: `RibbonView.Tapped` (Task 5); `LogViewModel.ChartItems`, `Hours`, `NowAt`, `RangeTitle` (Task 4);
  `DayChart.HitTest` (Task 3); `Sheet` (`ShowAsync(View)`, `HideAsync()`, `Closed`, `IsVisible`); `LogPage.OpenEntry(EntryId)`.
- Produces: `DayDetailView.Show(items, hours, now, palette, Func<double, double, string> rangeTitle, double openAt)`,
  `DayDetailView.Update(items, hours, now, palette)` (no re-scroll), `event EventHandler<EntryId>? EntryTapped`.

- [ ] **Step 1: Create `DayDetailView.cs`**

```csharp
using Coo.Domain.Common;
using Coo.Mobile.Presentation.Log;
using Coo.Mobile.Presentation.Theme;

namespace Coo.Mobile.Components;

/// <summary>
/// The zoomed day (Spec 009b §3): 6 hours per screen, swiped sideways, every hour labelled, each item labelled with
/// its time and length, the awake stretches named, a position bar for the whole day. A tap on an item reports its entry.
/// </summary>
public sealed class DayDetailView : ContentView
{
    private const double HoursPerScreen = 6;
    private readonly ScrollView _scroll = new() { Orientation = ScrollOrientation.Horizontal, HorizontalScrollBarVisibility = ScrollBarVisibility.Never };
    private readonly GraphicsView _canvas = new() { HeightRequest = 190 };
    private readonly GraphicsView _position = new() { HeightRequest = 6 };
    private readonly Label _title = Ui.Text("", "Section");
    private readonly DetailDrawable _drawable = new();
    private readonly PositionDrawable _bar = new();
    private Func<double, double, string> _rangeTitle = (_, _) => "";
    private double _openAt;
    private bool _scrollPending;
    private PointF? _down;

    public DayDetailView()
    {
        _canvas.Drawable = _drawable;
        _position.Drawable = _bar;
        _scroll.Content = _canvas;
        _scroll.Scrolled += (_, _) => ShowRange();
        _scroll.SizeChanged += (_, _) => Fit();
        _canvas.StartInteraction += (_, e) => _down = e.Touches.FirstOrDefault();
        _canvas.EndInteraction += OnTap;
        Content = new VerticalStackLayout { Spacing = 12, Children = { _title, _scroll, _position } };
    }

    /// <summary>An item was tapped: its entry.</summary>
    public event EventHandler<EntryId>? EntryTapped;

    public void Show(IReadOnlyList<ChartItem> items, IReadOnlyList<HourTick> hours, double? now, Palette palette, Func<double, double, string> rangeTitle, double openAt)
    {
        _rangeTitle = rangeTitle;
        _openAt = openAt;
        _scrollPending = true;
        Update(items, hours, now, palette);
        Fit();
    }

    /// <summary>New data or colours while open: redraw where the parent already is.</summary>
    public void Update(IReadOnlyList<ChartItem> items, IReadOnlyList<HourTick> hours, double? now, Palette palette)
    {
        _drawable.Items = items;
        _drawable.Hours = hours;
        _drawable.Now = now;
        _drawable.Palette = palette;
        _bar.Palette = palette;
        _canvas.Invalidate();
        _position.Invalidate();
    }

    private double CanvasWidth => _scroll.Width * 24 / HoursPerScreen;

    // The canvas is 4× the viewport; on the first layout after Show, the tapped time sits a third of the way in.
    private void Fit()
    {
        if (_scroll.Width <= 0)
            return;
        if (Math.Abs(_canvas.WidthRequest - CanvasWidth) > 0.5)
            _canvas.WidthRequest = CanvasWidth;
        if (!_scrollPending)
            return;
        _scrollPending = false;
        var x = Math.Clamp(_openAt * CanvasWidth - _scroll.Width / 3, 0, CanvasWidth - _scroll.Width);
        Dispatcher.Dispatch(async () =>
        {
            await _scroll.ScrollToAsync(x, 0, animated: false);
            ShowRange();
        });
    }

    private void ShowRange()
    {
        if (CanvasWidth <= 0)
            return;
        var from = _scroll.ScrollX / CanvasWidth;
        var to = Math.Min(1, (_scroll.ScrollX + _scroll.Width) / CanvasWidth);
        _title.Text = _rangeTitle(from, to);
        _bar.From = from;
        _bar.To = to;
        _position.Invalidate();
    }

    private void OnTap(object? sender, TouchEventArgs e)
    {
        var up = e.Touches.FirstOrDefault();
        var down = _down;
        _down = null;
        if (down is null || !e.IsInsideBounds || CanvasWidth <= 0 || Math.Abs(up.X - down.Value.X) > 12)
            return;
        // A hit area at least 24 dp wide (Spec 009b §3): 12 dp either side of the finger.
        if (DayChart.HitTest(_drawable.Items, up.X / CanvasWidth, 12 / CanvasWidth) is { EntryId: { } id })
            EntryTapped?.Invoke(this, id);
    }

    private sealed class DetailDrawable : IDrawable
    {
        private const float Top = 76, BandHeight = 40;

        public IReadOnlyList<ChartItem> Items { get; set; } = [];

        public IReadOnlyList<HourTick> Hours { get; set; } = [];

        public double? Now { get; set; }

        public Palette Palette { get; set; } = WashiPalette.Day;

        public void Draw(ICanvas canvas, RectF rect)
        {
            float X(double fraction) => rect.X + (float)(fraction * rect.Width);
            Color C(string hex) => Color.FromArgb(hex);
            canvas.FontSize = 11;

            foreach (var tick in Hours)
            {
                canvas.StrokeColor = C(Palette.Line);
                canvas.StrokeSize = 1;
                canvas.DrawLine(X(tick.At), 20, X(tick.At), 160);
                canvas.FontColor = C(Palette.Ink2);
                canvas.DrawString(tick.Label, X(tick.At) - 30, 166, 60, 16, HorizontalAlignment.Center, VerticalAlignment.Top);
            }

            canvas.FillColor = C(Palette.Surface);
            canvas.FillRoundedRectangle(rect.X, Top, rect.Width, BandHeight, 12);
            canvas.StrokeColor = C(Palette.Line);
            canvas.DrawRoundedRectangle(rect.X, Top, rect.Width, BandHeight, 12);

            // Two label rows above the feeds, one below for diapers and pumps; a label that would overlap is left out.
            var above = new[] { float.MinValue, float.MinValue };
            var below = float.MinValue;
            foreach (var item in Items)
            {
                var x = X(item.From);
                var w = Math.Max(0, X(item.To) - x);
                var textWidth = canvas.GetStringSize(item.Label, Microsoft.Maui.Graphics.Font.Default, 11).Width;
                switch (item.Shape)
                {
                    case ChartShape.Sleep:
                        canvas.FillColor = C(Palette.SleepMark).WithAlpha(0.85f);
                        canvas.FillRoundedRectangle(x + 0.5f, Top + 3, Math.Max(3, w - 1), BandHeight - 6, 9);
                        if (textWidth <= w - 12)
                        {
                            canvas.FontColor = C(Palette.Surface);
                            canvas.DrawString(item.Label, x + 6, Top + 12, w - 12, 16, HorizontalAlignment.Left, VerticalAlignment.Top);
                        }

                        break;
                    case ChartShape.Awake:
                        if (textWidth <= w - 12)
                        {
                            canvas.FontColor = C(Palette.Ink2);
                            canvas.DrawString(item.Label, x + 6, Top + 12, w - 12, 16, HorizontalAlignment.Center, VerticalAlignment.Top);
                        }

                        break;
                    case ChartShape.Feed:
                        canvas.FillColor = C(Palette.FeedMark);
                        canvas.FillRoundedRectangle(x, Top - 18, Math.Max(7, w), 12, 6);
                        var row = x >= above[0] + 4 ? 0 : x >= above[1] + 4 ? 1 : -1;
                        if (row >= 0)
                        {
                            canvas.FontColor = C(Palette.FeedInk);
                            canvas.DrawString(item.Label, x, Top - 36 - (row * 16), textWidth + 2, 16, HorizontalAlignment.Left, VerticalAlignment.Top);
                            above[row] = x + textWidth;
                        }

                        break;
                    case ChartShape.Diaper:
                    case ChartShape.Pump:
                        var diaper = item.Shape == ChartShape.Diaper;
                        canvas.FillColor = C(diaper ? Palette.DiaperMark : Palette.PumpMark);
                        if (diaper)
                            canvas.FillCircle(x, Top + BandHeight + 12, 5);
                        else
                            canvas.FillRoundedRectangle(x - 5, Top + BandHeight + 7, 10, 10, 3);
                        if (x - 4 >= below + 4)
                        {
                            canvas.FontColor = C(diaper ? Palette.DiaperInk : Palette.PumpInk);
                            canvas.DrawString(item.Label, x + 9, Top + BandHeight + 5, textWidth + 2, 16, HorizontalAlignment.Left, VerticalAlignment.Top);
                            below = x + 9 + textWidth;
                        }

                        break;
                }
            }

            if (Now is { } now)
            {
                canvas.StrokeColor = C(Palette.Accent);
                canvas.StrokeSize = 2;
                canvas.DrawLine(X(now), 20, X(now), 160);
            }
        }
    }

    private sealed class PositionDrawable : IDrawable
    {
        public double From { get; set; }

        public double To { get; set; }

        public Palette Palette { get; set; } = WashiPalette.Day;

        public void Draw(ICanvas canvas, RectF rect)
        {
            canvas.FillColor = Color.FromArgb(Palette.Line);
            canvas.FillRoundedRectangle(rect, 3);
            canvas.FillColor = Color.FromArgb(Palette.Ink2);
            canvas.FillRoundedRectangle(rect.X + (float)(From * rect.Width), rect.Y, (float)((To - From) * rect.Width), rect.Height, 3);
        }
    }
}
```

If `Ui.Text` is not reachable from `Coo.Mobile.Components`, use the namespace the build names (it is used in
`Pages/LogPage.cs`).

- [ ] **Step 2: The sheet on the Log page**

In `LogPage.cs`, add fields:

```csharp
    private readonly Sheet _sheet = new();
    private readonly DayDetailView _detail = new();
```

In the constructor, add the sheet to the root grid after the `UndoToastView`, and wire it:

```csharp
        root.Add(_sheet);

        // The page owns this sheet and closes it itself; closing never goes through a view model (Spec 009b §3).
        _sheet.Closed += async (_, _) => await _sheet.HideAsync();
        _detail.EntryTapped += async (_, id) =>
        {
            await _sheet.HideAsync();
            OpenEntry(id);
        };
```

In `Render`, after creating `_ribbon`:

```csharp
        _ribbon.Tapped += (_, at) => OpenDetail(at);
```

and at the end of `Render`, keep an open zoomed day current (an undo or a delete commits while it is open):

```csharp
        if (_sheet.IsVisible)
            _detail.Update(_model.ChartItems, _model.Hours, _model.NowAt, WashiPalette.For(_theme.Current));
```

Add:

```csharp
    private async void OpenDetail(double at)
    {
        _detail.Show(_model.ChartItems, _model.Hours, _model.NowAt, WashiPalette.For(_theme.Current), _model.RangeTitle, at);
        await _sheet.ShowAsync(_detail);
    }

    /// <summary>Back closes the zoomed day before it leaves the page.</summary>
    protected override bool OnBackButtonPressed()
    {
        if (_sheet.IsVisible)
        {
            _ = _sheet.HideAsync();
            return true;
        }

        return base.OnBackButtonPressed();
    }
```

In `ShowRibbon`, also keep an open zoomed day's "now" and colours current:

```csharp
    private void ShowRibbon()
    {
        var palette = WashiPalette.For(_theme.Current);
        _ribbon?.Show(_model.ChartItems, _model.QuietBands, _model.Hours, _model.NowAt, palette);
        if (_sheet.IsVisible)
            _detail.Update(_model.ChartItems, _model.Hours, _model.NowAt, palette);
    }
```

- [ ] **Step 3: Build, install, check on the phone**

Build and install as in Task 5 Step 3. On Log, with the owner's go-ahead:
1. Tap the band at about 14:00: the sheet opens with 14:00 about a third in; sleep lengths, "awake …", feed labels,
   diaper contents show.
2. Swipe left and right: the title ("Today, 12:30 – 18:30") and the position bar follow; a swipe does not open an entry.
3. Tap a feed: its edit page opens; Back (without saving) returns to Log with the sheet closed.
4. Open the sheet; Back closes it and Coo stays in front. Open it; tap above it: it closes.
5. Settings → Night, back to Log, open the sheet: night colours.
6. ‹ to yesterday, tap the band: yesterday's date in the title, no now line.

- [ ] **Step 4: Commit**

```bash
git add src/Mobile/Coo.Mobile/Components/DayDetailView.cs src/Mobile/Coo.Mobile/Pages/LogPage.cs
git commit -m "Log: tap the band for the zoomed day (Spec 009b §3)"
```

---

### Task 7: Docs, full check, CI

**Files:**
- Modify: `docs/design/design-system.md` (the "Day ribbon" row)
- Modify: `docs/specs/006-today-and-quick-logging/spec.md` (§7, the ribbon bullet)
- Modify: `docs/ROADMAP.md` (9b status)

- [ ] **Step 1: Update the design system and Spec 006**

In `docs/design/design-system.md`, replace the "Day ribbon" row with:

```markdown
| **Day ribbon** | A 24-hour rhythm band (Spec 009b). Sleep fills the band in `SleepMark`; feeds are `FeedMark` pills of their real length above it; diapers are `DiaperMark` dots and pumps `PumpMark` squares below it. Quiet hours are a `SleepTint` wash, today has an `Accent` "now" line, and labels mark every 3 hours. A tap opens the zoomed day: 6 hours per screen, swiped, with every item labelled. |
```

In Spec 006 §7, after the ribbon bullet, add: `Replaced by the rhythm band and the zoomed day in Spec 009b.`

- [ ] **Step 2: Run everything**

Run: `dotnet test Coo.Tests.slnf` and `dotnet format Coo.Tests.slnf --verify-no-changes`
Expected: all green, no format changes.

- [ ] **Step 3: The deliverables on the phone**

Walk Spec 009b §6, 1–7, in day and night, with the owner's go-ahead. Hide Pumping in Settings: the squares and the
legend entry go; show it again afterwards.

- [ ] **Step 4: Mark the increment done and commit**

In `docs/ROADMAP.md`, set 9b's status to `Done`.

```bash
git add docs/design/design-system.md docs/specs/006-today-and-quick-logging/spec.md docs/ROADMAP.md
git commit -m "Docs: the rhythm band and the zoomed day (Spec 009b); Increment 9b done"
```

- [ ] **Step 5: Push and watch CI**

```bash
git push
gh run watch --repo Lech-Digital/Coo
```

Expected: green.
