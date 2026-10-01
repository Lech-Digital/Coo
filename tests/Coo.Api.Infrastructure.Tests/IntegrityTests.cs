using Coo.Api.Application.CareLog;
using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Kinds.Diaper;
using Coo.Domain.Common;

namespace Coo.Api.Infrastructure.Tests;

/// <summary>G4 and G5 (ADR 0010): a row that does not decode is reported by ID, never dropped and never a crash.</summary>
[Collection(PostgresTests.Name)]
public sealed class IntegrityTests(PostgresServer server)
{
    [Fact]
    public async Task Find_RowWhoseDetailsNoLongerDecode_IsUnreadableWithAReason()
    {
        // Arrange: a valid v1 document as far as the database knows, with a value no build ever wrote.
        await using var db = await ServerDatabase.CreateAsync(server);
        var id = await SavedDiaperAsync(db);
        await db.ExecuteAsync($$"""UPDATE care_entries SET details = '{"v":1,"contents":"glitter"}' WHERE id = '{{id.Value}}';""");

        // Act
        var stored = await db.Store.FindAsync(id, CancellationToken.None);

        // Assert
        var unreadable = stored.Should().BeOfType<UnreadableServerEntry>().Subject;
        unreadable.Kind.Should().Be("diaper");
        unreadable.Reason.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task VerifyIntegrity_OneBadRowAmongGoodOnes_ReportsExactlyThatRow()
    {
        // Arrange
        await using var db = await ServerDatabase.CreateAsync(server);
        var good = await SavedDiaperAsync(db);
        var bad = await SavedDiaperAsync(db);
        await db.ExecuteAsync($$"""UPDATE care_entries SET details = '{"v":1,"contents":"glitter"}' WHERE id = '{{bad.Value}}';""");
        var verify = new VerifyIntegrity(db.Store);

        // Act
        var report = await verify.ExecuteAsync(CancellationToken.None);

        // Assert
        report.Checked.Should().Be(2);
        report.IsClean.Should().BeFalse();
        report.Failures.Select(f => f.Id).Should().Equal(bad);
        (await db.Store.FindAsync(good, CancellationToken.None)).Should().BeOfType<LiveServerEntry>("one bad row never hides the others");
    }

    [Fact]
    public async Task VerifyIntegrity_EmptyDatabase_IsClean()
    {
        // Arrange
        await using var db = await ServerDatabase.CreateAsync(server);

        // Act
        var report = await new VerifyIntegrity(db.Store).ExecuteAsync(CancellationToken.None);

        // Assert
        report.Checked.Should().Be(0);
        report.IsClean.Should().BeTrue();
    }

    private static async Task<EntryId> SavedDiaperAsync(ServerDatabase db)
    {
        await db.Households.EnsureAsync(ServerDatabase.Household, CancellationToken.None);
        var entry = CareEntry.Create(new EntryId(db.NewId()), ServerDatabase.Household, EntrySubject.Baby(ServerDatabase.Baby),
            new DiaperDetails(DiaperContents.Wet), EntryTiming.Point(db.Time.GetUtcNow()), PersonRef.ThisDevicesPerson, EntrySource.Manual,
            db.NextStamp(), db.Time.GetUtcNow()).Value;
        await db.Store.SaveAsync(entry, CancellationToken.None);
        return entry.Id;
    }
}
