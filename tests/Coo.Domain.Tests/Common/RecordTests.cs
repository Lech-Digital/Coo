using Coo.Domain.CareLog;
using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;

namespace Coo.Domain.Tests.Common;

public sealed class RecordTests
{
    [Fact]
    public void Delete_LiveRecord_WritesTombstoneWithNewVersion()
    {
        // Arrange
        var entry = A.Diaper(A.Now);
        var version = A.Stamp();

        // Act
        var result = entry.Delete(version);

        // Assert
        result.IsSuccess.Should().BeTrue();
        entry.IsDeleted.Should().BeTrue();
        entry.Version.Should().Be(version);
    }

    [Fact]
    public void Delete_AlreadyDeleted_FailsWithAlreadyDeleted()
    {
        // Arrange
        var entry = A.Diaper(A.Now);
        entry.Delete(A.Stamp());

        // Act
        var result = entry.Delete(A.Stamp());

        // Assert
        result.Error!.Code.Should().Be(RecordErrors.AlreadyDeleted);
    }

    [Fact]
    public void SetNote_OnDeletedEntry_FailsWithRecordDeleted()
    {
        // Arrange
        var entry = A.Diaper(A.Now);
        entry.Delete(A.Stamp());

        // Act
        var result = entry.SetNote(null, A.Stamp());

        // Assert
        result.Error!.Code.Should().Be(RecordErrors.Deleted);
    }

    [Fact]
    public void SetNote_VersionNotGreaterThanCurrent_FailsWithStaleVersion()
    {
        // Arrange
        var entry = A.Diaper(A.Now);
        var stale = new HlcStamp(entry.Version.WallClockMs, entry.Version.Counter, entry.Version.DeviceId);

        // Act
        var result = entry.SetNote(Note.From("wet through").Value, stale);

        // Assert
        result.Error!.Code.Should().Be(RecordErrors.StaleVersion);
        entry.Note.Should().BeNull("a failed write changes nothing");
    }

    [Fact]
    public void Pick_EitherArgumentOrder_ReturnsSameWinner()
    {
        // Arrange
        var older = A.Diaper(A.Now);
        var newer = A.Diaper(A.Now);

        // Act
        var forward = LastWriterWins.Pick(older, newer);
        var backward = LastWriterWins.Pick(newer, older);

        // Assert
        forward.Should().BeSameAs(newer);
        backward.Should().BeSameAs(newer);
    }

    [Fact]
    public void Constructor_EmptyGuid_Throws()
    {
        // Arrange
        var empty = Guid.Empty;

        // Act
        var act = () => new EntryId(empty);

        // Assert
        act.Should().Throw<ArgumentException>();
    }
}
