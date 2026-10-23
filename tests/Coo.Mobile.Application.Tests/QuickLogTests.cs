using Coo.Contracts.AiParsing;
using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Kinds.Diaper;
using Coo.Domain.CareLog.Kinds.Feeding;
using Coo.Domain.CareLog.Kinds.Sleep;
using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;
using Coo.Domain.Households;
using Coo.Mobile.Application.AiLogging;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Application.Households;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Xunit;

namespace Coo.Mobile.Application.Tests;

public sealed class QuickLogTests
{
    private static readonly HouseholdId Household = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a1"));
    private static readonly BabyId Baby = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000b1"));
    private static readonly DeviceId Device = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000d1"));
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 14, 0, 0, TimeSpan.FromHours(2));
    private readonly FakeTimeProvider _time = new(Now);
    private readonly MemoryStore _store = new();
    private readonly IAiParseApi _api = Substitute.For<IAiParseApi>();
    private readonly MemoryNotes _notes = new();
    private readonly IHouseholdStore _households = Substitute.For<IHouseholdStore>();
    private long _wall = 1;

    public QuickLogTests()
    {
        _households.ListHouseholdsAsync(Arg.Any<CancellationToken>()).Returns(
            [HouseholdSettings.Create(Household, VolumeUnit.Millilitres, "Europe/Amsterdam", new HlcStamp(1, 0, Device)).Value]);
        _households.ListBabiesAsync(Household, Arg.Any<CancellationToken>()).Returns(
            [BabyProfile.Create(Baby, Household, "Lena", [], new DateOnly(2026, 8, 20), null, new HlcStamp(1, 0, Device), new DateOnly(2026, 10, 23)).Value]);
    }

    [Fact]
    public async Task Prepare_ServerUnreachable_KeepsTheWordsAsAPendingNote()
    {
        // Arrange
        _api.ParseAsync(Arg.Any<AiParseRequest>(), Arg.Any<CancellationToken>()).Returns((AiParseResponse?)null);

        // Act
        var prepared = await Prepare().ExecuteAsync("120 ml at two", Now, "en", CancellationToken.None);

        // Assert
        prepared.KeptAsPendingNote.Should().BeTrue();
        prepared.Review.Should().BeNull();
        _notes.Notes.Should().ContainSingle().Which.Should().Match<PendingNote>(n => n.Text == "120 ml at two" && n.CapturedAt == Now);
    }

    [Fact]
    public async Task Prepare_DiaperThePartnerAlreadyLogged_IsShownButNotSelected()
    {
        // Arrange: the partner logged a wet diaper at 13:58; the dictation says the same.
        _store.Add(Make(new DiaperDetails(DiaperContents.Wet), EntryTiming.Point(Now.AddMinutes(-2))));
        Answer(Proposal(AiActions.DiaperLog, Now.AddMinutes(-1), contents: "wet"));

        // Act
        var prepared = await Prepare().ExecuteAsync("wet diaper", Now, "en", CancellationToken.None);

        // Assert
        var item = prepared.Review!.Items.Should().ContainSingle().Subject;
        item.Duplicate.Should().NotBeNull();
        item.IsSelectedByDefault.Should().BeFalse();
    }

    [Fact]
    public async Task Prepare_RunningLeftFeedAndLastSide_GoIntoTheContext()
    {
        // Arrange
        _store.Add(Make(BreastFeed.Start(BreastSide.Left, Now.AddMinutes(-5)), null));
        AiParseRequest? sent = null;
        _api.ParseAsync(Arg.Do<AiParseRequest>(r => sent = r), Arg.Any<CancellationToken>()).Returns(new AiParseResponse([], [], "v1"));

        // Act
        await Prepare().ExecuteAsync("other side", Now, "en", CancellationToken.None);

        // Assert
        sent!.Context.Running.Should().Equal(AiRunningKinds.BreastLeft);
        sent.Context.LastBreastSide.Should().Be("left");
        sent.BabyId.Should().Be(Baby.Value);
    }

    [Fact]
    public async Task Apply_BreastStartLeft_StartsATimerMarkedAsAiParsed()
    {
        // Arrange
        Answer(Proposal(AiActions.BreastStart, Now, side: "left"));
        var review = (await Prepare().ExecuteAsync("Lena started on the left", Now, "en", CancellationToken.None)).Review!;

        // Act
        var results = await Apply().ExecuteAsync(review, review.Items, null, CancellationToken.None);

        // Assert
        results.Should().Equal((string?)null);
        var started = _store.Entries.Should().ContainSingle().Subject;
        started.Details.Should().BeOfType<BreastFeed>().Which.LastSide.Should().Be(BreastSide.Left);
        started.Timing.IsInProgress.Should().BeTrue();
        started.Source.Kind.Should().Be(EntrySourceKind.AiParsed);
    }

    [Fact]
    public async Task Apply_WokeUp_EndsTheRunningSleepAndRemovesThePendingNote()
    {
        // Arrange
        var sleep = Make(new SleepDetails(), EntryTiming.Session(TimeRange.Open(Now.AddHours(-1))));
        _store.Add(sleep);
        var note = new PendingNote(Guid.CreateVersion7(Now), "she woke up", Now);
        await _notes.AddAsync(note, CancellationToken.None);
        Answer(Proposal(AiActions.SleepEnd, Now));
        var review = (await Prepare().ExecuteAsync("she woke up", Now, "en", CancellationToken.None)).Review!;

        // Act
        var results = await Apply().ExecuteAsync(review, review.Items, note.Id, CancellationToken.None);

        // Assert
        results.Should().Equal((string?)null);
        ((SessionTiming)_store.Entries.Single().Timing).Range.End.Should().Be(Now);
        _notes.Notes.Should().BeEmpty();
    }

    [Fact]
    public async Task Apply_SwitchWithNothingRunning_SaysSoAndWritesNothing()
    {
        // Arrange
        Answer(Proposal(AiActions.BreastSwitch, Now));
        var review = (await Prepare().ExecuteAsync("other side", Now, "en", CancellationToken.None)).Review!;

        // Act
        var results = await Apply().ExecuteAsync(review, review.Items, null, CancellationToken.None);

        // Assert
        results.Should().Equal(AiWarnings.NothingRunning);
        _store.Entries.Should().BeEmpty();
    }

    private PrepareQuickLog Prepare() => new(_households, new GetToday(_store), _store, _api, _notes, _time);

    private ApplyQuickLog Apply()
    {
        var clock = Substitute.For<IVersionClock>();
        clock.NextAsync(Arg.Any<CancellationToken>()).Returns(_ => new HlcStamp(++_wall, 0, Device));
        return new ApplyQuickLog(_households, new GetToday(_store), new LogEntry(_store, clock, _time), new ReviseEntry(_store, clock, _time), _notes, _time);
    }

    private void Answer(params AiProposal[] proposals) =>
        _api.ParseAsync(Arg.Any<AiParseRequest>(), Arg.Any<CancellationToken>()).Returns(new AiParseResponse(proposals, [], "v1"));

    private static AiProposal Proposal(string action, DateTimeOffset at, string? side = null, string? contents = null) =>
        new(action, at, null, side, null, null, null, null, contents, "", []);

    private CareEntry Make(EntryDetails details, EntryTiming? timing) =>
        CareEntry.Create(
            new EntryId(Guid.CreateVersion7(Now.AddMilliseconds(++_wall))), Household, EntrySubject.Baby(Baby), details, timing,
            PersonRef.ThisDevicesPerson, EntrySource.Manual, new HlcStamp(++_wall, 0, Device), Now).Value;

    private sealed class MemoryStore : ICareLogStore
    {
        private readonly Dictionary<EntryId, CareEntry> _entries = [];

        public IReadOnlyList<CareEntry> Entries => [.. _entries.Values];

        public void Add(CareEntry entry) => _entries[entry.Id] = entry;

        public Task SaveAsync(CareEntry entry, CancellationToken cancellationToken)
        {
            _entries[entry.Id] = entry;
            return Task.CompletedTask;
        }

        public Task<StoredEntry?> FindAsync(EntryId id, CancellationToken cancellationToken) =>
            Task.FromResult<StoredEntry?>(_entries.TryGetValue(id, out var e) ? new LiveEntry(e) : null);

        public Task<IReadOnlyList<StoredEntry>> ListAsync(HouseholdId household, DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<StoredEntry>>([.. _entries.Values.Where(e => e.Timing.OccurredAt >= windowStart && e.Timing.OccurredAt < windowEnd).Select(e => new LiveEntry(e))]);

        public Task<bool> DeleteAsync(EntryId id, HlcStamp version, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> WriteAllAsync(CareLogWrites changes, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<StoreCounts> CountsAsync(HouseholdId household, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<int> CountAllEntriesAsync(HouseholdId household, CancellationToken cancellationToken) => Task.FromResult(_entries.Count);
    }

    private sealed class MemoryNotes : IPendingNoteStore
    {
        public List<PendingNote> Notes { get; } = [];

        public Task<IReadOnlyList<PendingNote>> ListAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<PendingNote>>(Notes);

        public Task AddAsync(PendingNote note, CancellationToken cancellationToken)
        {
            Notes.Add(note);
            return Task.CompletedTask;
        }

        public Task RemoveAsync(Guid id, CancellationToken cancellationToken)
        {
            Notes.RemoveAll(n => n.Id == id);
            return Task.CompletedTask;
        }
    }
}
