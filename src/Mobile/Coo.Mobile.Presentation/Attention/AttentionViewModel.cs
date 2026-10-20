using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Coo.Contracts.Sync;
using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Kinds.Feeding;
using Coo.Domain.Common;
using Coo.Domain.Households;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Application.Households;
using Coo.Mobile.Application.Preferences;
using Coo.Mobile.Application.Sync;
using Coo.Mobile.Presentation.Formatting;
using Coo.Mobile.Presentation.Localization;
using Coo.Mobile.Presentation.Today;

namespace Coo.Mobile.Presentation.Attention;

/// <summary>One entry of a group card: "11:22 · Breast · right 10 m", with "Keep only this one".</summary>
public sealed record GroupMember(EntryId Id, string Text, IAsyncRelayCommand KeepOnly);

/// <summary>
/// A group card (Spec 011d §4): its entries, "Merge into one feed" when the feeds can be merged (null otherwise), why
/// they cannot when that is worth saying (overlap, notes too long), and "Keep all".
/// </summary>
public sealed record GroupItem(string KindCode, IReadOnlyList<GroupMember> Members, IAsyncRelayCommand? Merge, string? Why, IAsyncRelayCommand KeepAll);

public sealed record AttentionEntry(EntryId Id, string KindCode, string Text, string Reason);

/// <summary>The Needs attention page (Spec 007 §6): groups in order, each with its own actions. Nothing interrupts.</summary>
public sealed partial class AttentionViewModel(
    IHouseholdStore households,
    GetAttention getAttention,
    DismissDuplicate dismiss,
    MergeFeeds merge,
    UndoService undo,
    IClockFormat clock,
    TimeProvider time,
    AutoResolveAttention? autoResolve = null,
    ISyncStore? sync = null)
    : ObservableObject
{
    private HouseholdId? _household;

    /// <summary>"Feeds close together" (Spec 011d §6): groups of breast feeds or bottle feeds.</summary>
    [ObservableProperty]
    private IReadOnlyList<GroupItem> _feedGroups = [];

    /// <summary>"Probably the same": groups of other entries logged twice.</summary>
    [ObservableProperty]
    private IReadOnlyList<GroupItem> _otherGroups = [];

    [ObservableProperty]
    private IReadOnlyList<AttentionEntry> _unreadable = [];

    [ObservableProperty]
    private IReadOnlyList<AttentionEntry> _newer = [];

    public const int RejectedPageSize = 50;

    private IReadOnlyList<AttentionEntry> _allRejected = [];
    private int _rejectedShown = RejectedPageSize;

    [ObservableProperty]
    private IReadOnlyList<AttentionEntry> _rejected = [];

    [ObservableProperty]
    private bool _isEmpty;

    [ObservableProperty]
    private bool _canResolveAll;

    public int RejectedTotal => _allRejected.Count;

    public bool HasMoreRejected => Rejected.Count < _allRejected.Count;

    /// <summary>The page opens the edit form for a rejected entry (fixing it writes a new version, which goes back out).</summary>
    public event EventHandler<EntryId>? EditRequested;

    public async Task RefreshAsync()
    {
        var household = (await households.ListHouseholdsAsync(CancellationToken.None)).FirstOrDefault(h => !h.IsDeleted);
        if (household is null)
            return;
        _household = household.Id;
        var now = time.GetLocalNow();
        var formats = new Formats(clock.Is24Hour, household.VolumeUnit);
        var attention = await getAttention.ExecuteAsync(household.Id, now, CancellationToken.None);
        bool Pending(EntryId id) => undo.IsPendingDelete(id);
        string Line(CareEntry e) => $"{formats.Time(e.Timing.OccurredAt, time.LocalTimeZone)} · {formats.Title(e, now)}";

        // A group with an entry waiting for its delete waits too: what it would offer is about to change.
        var groups = attention.Groups.Where(g => !g.Entries.Any(e => Pending(e.Id))).Select(g => Card(g, household.Id, formats, now, Line)).ToList();
        FeedGroups = [.. groups.Where(g => g.Feeds).Select(g => g.Item)];
        OtherGroups = [.. groups.Where(g => !g.Feeds).Select(g => g.Item)];
        Unreadable = [.. attention.Quarantined.Where(q => !Pending(q.Id))
            .Select(q => new AttentionEntry(q.Id, q.KindCode, formats.Time(q.Start), Strings.Get("Edit.Unreadable")))];
        Newer = [.. attention.ReadOnly.Where(r => !Pending(r.Id))
            .Select(r => new AttentionEntry(r.Id, r.KindCode, formats.Time(r.Start), Strings.Get("Edit.Newer")))];
        _allRejected = [.. attention.Rejected.Where(r => !Pending(r.Entry.Id))
            .Select(r => new AttentionEntry(r.Entry.Id, r.Entry.KindCode, formats.Time(r.Entry.Start), Strings.Error(r.Reason)))];
        ShowRejected();
        IsEmpty = FeedGroups.Count + OtherGroups.Count + Unreadable.Count + Newer.Count + _allRejected.Count == 0;
        CanResolveAll = FeedGroups.Any(g => g.Merge is not null) || OtherGroups.Count > 0;
    }

    private (bool Feeds, GroupItem Item) Card(AttentionGroup group, HouseholdId household, Formats formats, DateTimeOffset now, Func<CareEntry, string> line)
    {
        var members = group.Entries.Select(kept => new GroupMember(kept.Id, line(kept), new AsyncRelayCommand(async () =>
        {
            // "Keep only this one": the others go, under one undo (Spec 011d §4).
            var others = group.Entries.Where(e => e.Id != kept.Id).ToList();
            await undo.DeleteWithUndoAsync([.. others.Select(e => e.Id)], formats.Title(others[0], now));
        }))).ToList();

        IAsyncRelayCommand? mergeCommand = group.CanMerge
            ? new AsyncRelayCommand(async () =>
            {
#pragma warning disable CA1031 // A tapped button must not crash the app; the merge is one transaction, so nothing changed.
                try
                {
                    var result = await merge.ExecuteAsync([.. group.Entries.Select(e => e.Id)], CancellationToken.None);
                    if (result.Merged is { } merged)
                        undo.OfferUndoForMerge(merged, Strings.Get("Attention.Merged"));
                    else
                        undo.Say(Why(result.Refusal) ?? Strings.Error(null));
                }
                catch (Exception)
                {
                    undo.Say(Strings.Error(null));
                }
#pragma warning restore CA1031

                await RefreshAsync();
            })
            : null;

        var keepAll = new AsyncRelayCommand(async () =>
        {
            await dismiss.ExecuteAsync(household, group.OpenPairs, CancellationToken.None);
            await RefreshAsync();
        });

        return (group.IsFeeds, new GroupItem(group.Entries[0].KindCode.Value, members, mergeCommand, Why(group.MergeRefusal), keepAll));
    }

    /// <summary>The refusals worth a line on the card (Spec 011d §4); the others just leave the merge out.</summary>
    private static string? Why(FeedMergeRefusal? refusal) => refusal switch
    {
        FeedMergeRefusal.Overlap => Strings.Get("Attention.Overlap"),
        FeedMergeRefusal.NoteTooLong => Strings.Get("Attention.NoteTooLong"),
        _ => null,
    };

    [RelayCommand]
    private void ShowMoreRejected()
    {
        _rejectedShown += RejectedPageSize;
        ShowRejected();
    }

    [RelayCommand]
    private Task DeleteRejected(EntryId id) =>
        undo.DeleteWithUndoAsync(id, _allRejected.FirstOrDefault(r => r.Id == id)?.Text ?? Strings.Get("Entry.Quarantined"));

    [RelayCommand]
    private void EditRejected(EntryId id) => EditRequested?.Invoke(this, id);

    private void ShowRejected()
    {
        Rejected = [.. _allRejected.Take(_rejectedShown)];
        OnPropertyChanged(nameof(RejectedTotal));
        OnPropertyChanged(nameof(HasMoreRejected));
    }

    [RelayCommand]
    private Task DeleteEntry(EntryId id) => undo.DeleteWithUndoAsync(id, Strings.Get("Entry.Quarantined"));

    [RelayCommand]
    private async Task AutoResolveAll()
    {
        if (_household is not { } h)
            return;

        if (autoResolve is not null)
        {
            var now = time.GetLocalNow();
            await autoResolve.ExecuteAsync(h, now, CancellationToken.None);
        }
        else
        {
            foreach (var group in FeedGroups.Where(g => g.Merge is not null))
            {
                if (group.Merge is not null)
                    await group.Merge.ExecuteAsync(null);
            }
        }

        await RefreshAsync();
    }

    [RelayCommand]
    private async Task DismissRejected(EntryId id)
    {
        if (_household is { } h && sync is not null)
            await sync.DeleteRejectionAsync(h, SyncEntityTypes.CareEntry, id.Value, CancellationToken.None);

        await DeleteRejected(id);
    }
}
