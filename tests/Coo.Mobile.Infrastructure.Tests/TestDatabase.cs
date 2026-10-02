using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Kinds.Diaper;
using Coo.Domain.Common;
using Coo.Mobile.Application.Sync;
using Coo.Mobile.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace Coo.Mobile.Infrastructure.Tests;

/// <summary>A real SQLite file in a temp folder, migrated and started like the app does (Spec 004 §10).</summary>
internal sealed class TestDatabase : IAsyncDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "coo-tests", Guid.CreateVersion7(DateTimeOffset.UnixEpoch).ToString("N"));

    private TestDatabase()
    {
        Directory.CreateDirectory(_folder);
        Database = new LocalDatabase(Path.Combine(_folder, "coo.db"));
        Device = new DeviceState(Database, Time);
        Store = new CareLogStore(Database, Time, Writes);
        Households = new HouseholdStore(Database, Time, Writes);
    }

    public static readonly HouseholdId Household = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a1"));
    public static readonly BabyId Baby = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000b1"));
    public static readonly UserId Parent = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000c1"));

    public ManualTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.FromHours(2)));

    public LocalDatabase Database { get; }

    public LocalWrites Writes { get; } = new();

    public DeviceState Device { get; }

    public CareLogStore Store { get; }

    public HouseholdStore Households { get; }

    public static async Task<TestDatabase> CreateAsync(string appVersion = "1.0.0")
    {
        var db = new TestDatabase();
        await db.StartupAsync(appVersion);
        return db;
    }

    public Task<Coo.Mobile.Application.CareLog.StartupResult> StartupAsync(string appVersion) =>
        new AppStartup(Database, new DataMigrationRunner([], Time), Device, appVersion).RunAsync(CancellationToken.None);

    public async Task<SqliteConnection> OpenAsync() => await Database.OpenAsync(CancellationToken.None);

    public async Task<CareEntry> DiaperAsync(DateTimeOffset at, DiaperContents contents = DiaperContents.Wet)
    {
        var entry = CareEntry.Create(
            await NewIdAsync(), Household, EntrySubject.Baby(Baby), new DiaperDetails(contents), EntryTiming.Point(at),
            PersonRef.User(Parent), EntrySource.Manual, await Device.NextAsync(CancellationToken.None), Time.GetUtcNow()).Value;
        await Store.SaveAsync(entry, CancellationToken.None);
        return entry;
    }

    public Task<EntryId> NewIdAsync()
    {
        Time.Advance(TimeSpan.FromMilliseconds(1));
        return Task.FromResult(new EntryId(Guid.CreateVersion7(Time.GetUtcNow())));
    }

    public async Task ExecuteAsync(string sql)
    {
        await using var connection = await OpenAsync();
        await connection.ExecuteAsync(sql, CancellationToken.None);
    }

    public async Task<long> ScalarAsync(string sql)
    {
        await using var connection = await OpenAsync();
        return (long)(await connection.ScalarAsync(sql, CancellationToken.None))!;
    }

    public async ValueTask DisposeAsync()
    {
        Device.Dispose();
        SqliteConnection.ClearAllPools();
        await Task.Yield();
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // A temp folder left behind is harmless.
        }
    }
}

/// <summary>A clock the test moves by hand.</summary>
internal sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now.ToUniversalTime();

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.FindSystemTimeZoneById("Europe/Amsterdam");

    public void Advance(TimeSpan by) => _now += by;
}
