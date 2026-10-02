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
