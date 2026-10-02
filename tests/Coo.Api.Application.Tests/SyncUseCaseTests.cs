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
