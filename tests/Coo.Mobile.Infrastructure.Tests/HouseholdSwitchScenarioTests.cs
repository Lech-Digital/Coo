using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Kinds.Diaper;
using Coo.Domain.Common;
using Coo.Domain.Households;
using Coo.Mobile.Application.Auth;
using Coo.Mobile.Application.Households;
using Coo.Mobile.Application.Sync;
using Coo.Mobile.Infrastructure.Persistence;
using NSubstitute;

namespace Coo.Mobile.Infrastructure.Tests;

/// <summary>
/// Final review I1: accounts switching on one phone, with the real cycle, moves and stores on a real SQLite file. Only the
/// server (and the session's secure storage) are fakes. The account household always has settings and is the one shown,
/// and no cycle ever reports a household mismatch.
/// </summary>
public sealed class HouseholdSwitchScenarioTests : IAsyncLifetime, IDisposable
{
    private static readonly AccountProfile AccountA = new(new UserId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000c1")), "Andrzej", "a@example.com");
    private static readonly AccountProfile AccountB = new(new UserId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000c2")), "Ola", "b@example.com");
    private static readonly HouseholdId HouseholdA = TestDatabase.Household;
    private static readonly HouseholdId HouseholdB = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000ab"));

    private readonly MemorySessions _sessions = new();
    private readonly ISyncApi _api = Substitute.For<ISyncApi>();
    private readonly Account _account;
    private TestDatabase _db = null!;
    private HouseholdMoves _moves = null!;
    private SyncStore _store = null!;
    private SyncState _state = null!;
    private EntryId _entry;

    public HouseholdSwitchScenarioTests() => _account = new Account(_sessions);

    public async Task InitializeAsync()
    {
        _db = await TestDatabase.CreateAsync();
        _moves = new HouseholdMoves(_db.Database, _db.Time);
        _store = new SyncStore(_db.Database);
        _state = new SyncState(_db.Database);

        // This phone's household: settings, a baby and an entry; both accounts agreed to share.
        await _db.Households.SaveSettingsAsync(HouseholdSettings.Create(HouseholdA, VolumeUnit.Millilitres, "Europe/Amsterdam",
            await _db.Device.NextAsync(CancellationToken.None)).Value, CancellationToken.None);
        await _db.Households.SaveBabyAsync(BabyProfile.Create(TestDatabase.Baby, HouseholdA, "Maja", [], new DateOnly(2026, 8, 20), null,
            await _db.Device.NextAsync(CancellationToken.None), new DateOnly(2026, 10, 1)).Value, CancellationToken.None);
        _entry = (await _db.DiaperAsync(_db.Time.GetLocalNow(), DiaperContents.Wet)).Id;
        await _state.SetConsentAsync(AccountA.UserId, ConsentState.Given, CancellationToken.None);
        await _state.SetConsentAsync(AccountB.UserId, ConsentState.Given, CancellationToken.None);

        // The server accepts every change and has nothing new to send.
        _api.PushAsync(default, default, default!, default).ReturnsForAnyArgs(call =>
            (IReadOnlyList<PushResult>)[.. call.ArgAt<IReadOnlyList<OutgoingChange>>(2).Select(c => new PushResult(c.ChangeId, PushOutcome.Applied, null))]);
        _api.PullAsync(default, default, default, default).ReturnsForAnyArgs(new PulledPage([], 1, false, null));
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    public void Dispose() => _account.Dispose();

    private SyncCycle Cycle() => new(
        _account, _sessions, new ConsentGate(_state, Substitute.For<IAccountApi>(), Substitute.For<IConsentApi>()), _store, _moves, _state, _api,
        _db.Device, _db.Device, _db.Time, new FamilyStore(_db.Database), Substitute.For<IHouseholdApi>(), new SyncStops(),
        _db.Households);

    private BringEntriesOver BringOver() => new(
        _sessions, _moves, _store, new ConsentGate(_state, Substitute.For<IAccountApi>(), Substitute.For<IConsentApi>()), _account,
        Substitute.For<ISyncControl>());

    /// <summary>As <see cref="SignIn"/> stores the server's answer for this phone's active household.</summary>
    private async Task SignInAsync(AccountProfile profile, HouseholdId household, SignInOutcome outcome)
    {
        var active = await _moves.ActiveHouseholdAsync(CancellationToken.None);
        await _account.SignedInAsync(
            new StoredSession("access", _db.Time.GetUtcNow().AddMinutes(15), "refresh", profile, household,
                JoinPending: outcome == SignInOutcome.Joined || (outcome == SignInOutcome.Existing && household != active),
                PendingMoveFrom: outcome == SignInOutcome.Created && household != active ? active : null),
            CancellationToken.None);
    }

    private async Task SyncedAsAAsync()
    {
        await SignInAsync(AccountA, HouseholdA, SignInOutcome.Created);
        (await Cycle().RunAsync(CancellationToken.None)).Outcome.Should().Be(CycleOutcome.Synced);
    }

    private async Task<long> EntriesAsync(HouseholdId household) =>
        await _db.ScalarAsync($"SELECT count(*) FROM care_entries WHERE household_id = '{household.Value}' AND is_deleted = 0;");

    [Fact]
    public async Task RunAsync_CreatedAfterAnotherAccountSynced_TheNewHouseholdShowsWithSettingsAndNothingMismatches()
    {
        // Arrange: A's household synced; A signs out and B signs in with `created` (the move is refused).
        await SyncedAsAAsync();
        await _account.SignedOutAsync(sessionExpired: false, CancellationToken.None);
        await SignInAsync(AccountB, HouseholdB, SignInOutcome.Created);

        // Act
        var first = await Cycle().RunAsync(CancellationToken.None);
        var second = await Cycle().RunAsync(CancellationToken.None);

        // Assert
        first.Should().Match<CycleReport>(r => r.Outcome == CycleOutcome.Synced && r.Error == null);
        second.Should().Match<CycleReport>(r => r.Outcome == CycleOutcome.Synced && r.Error == null);
        (await _moves.ActiveHouseholdAsync(CancellationToken.None)).Should().Be(HouseholdB);
        var households = await _db.Households.ListHouseholdsAsync(CancellationToken.None);
        households.Select(h => h.Id).Should().Equal(HouseholdB, HouseholdA);
        households[0].TimeZoneId.Should().Be("Europe/Amsterdam");
        await _api.Received(1).PushAsync(HouseholdB, Arg.Any<DeviceId>(),
            Arg.Is<IReadOnlyList<OutgoingChange>>(b => b.Count == 1 && b[0].EntityType == "HouseholdSettings" && b[0].EntityId == HouseholdB.Value),
            Arg.Any<CancellationToken>());
        (await EntriesAsync(HouseholdA)).Should().Be(1, "A's household is untouched");
        (await _db.Households.ListBabiesAsync(HouseholdA, CancellationToken.None)).Should().ContainSingle();
    }

    [Fact]
    public async Task RunAsync_ASignsInAgainAfterB_ShowsAsHouseholdWithNoDataLossAndNoMismatchLoop()
    {
        // Arrange: A synced, then B created (B's household shows), then A again with `existing`.
        await SyncedAsAAsync();
        await _account.SignedOutAsync(sessionExpired: false, CancellationToken.None);
        await SignInAsync(AccountB, HouseholdB, SignInOutcome.Created);
        (await Cycle().RunAsync(CancellationToken.None)).Outcome.Should().Be(CycleOutcome.Synced);
        (await _moves.ActiveHouseholdAsync(CancellationToken.None)).Should().Be(HouseholdB);
        await _account.SignedOutAsync(sessionExpired: false, CancellationToken.None);
        await SignInAsync(AccountA, HouseholdA, SignInOutcome.Existing);

        // Act: the join-pending pull, the bring-over sheet's prepare (B's household synced: nothing is offered), two cycles.
        var joining = await Cycle().RunAsync(CancellationToken.None);
        var choice = await BringOver().PrepareAsync(CancellationToken.None);
        var first = await Cycle().RunAsync(CancellationToken.None);
        var second = await Cycle().RunAsync(CancellationToken.None);

        // Assert
        joining.Outcome.Should().Be(CycleOutcome.JoinPending);
        choice.Should().BeNull();
        _account.State.Should().Be(new SignedIn(AccountA));
        first.Should().Match<CycleReport>(r => r.Outcome == CycleOutcome.Synced && r.Error == null);
        second.Should().Match<CycleReport>(r => r.Outcome == CycleOutcome.Synced && r.Error == null);
        (await _moves.ActiveHouseholdAsync(CancellationToken.None)).Should().Be(HouseholdA);
        (await _db.Households.ListHouseholdsAsync(CancellationToken.None)).Select(h => h.Id).Should().Equal(HouseholdA, HouseholdB);
        (await EntriesAsync(HouseholdA)).Should().Be(1);
        (await _db.Store.FindAsync(_entry, CancellationToken.None)).Should().NotBeNull();
        (await _db.Households.ListBabiesAsync(HouseholdA, CancellationToken.None)).Should().ContainSingle();
    }

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
