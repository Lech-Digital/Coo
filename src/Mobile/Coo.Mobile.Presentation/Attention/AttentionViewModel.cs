using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Coo.Domain.Common;
using Coo.Domain.Households;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Application.Households;
using Coo.Mobile.Application.Preferences;
using Coo.Mobile.Presentation.Formatting;
using Coo.Mobile.Presentation.Localization;
using Coo.Mobile.Presentation.Today;

namespace Coo.Mobile.Presentation.Attention;

public sealed record DuplicateItem(
    string KindCode, string FirstText, string SecondText, IAsyncRelayCommand KeepBoth, IAsyncRelayCommand KeepFirst, IAsyncRelayCommand KeepSecond);

public sealed record AttentionEntry(EntryId Id, string KindCode, string Text, string Reason);

/// <summary>The Needs attention page (Spec 007 §6): groups in order, each with its own actions. Nothing interrupts.</summary>
public sealed partial class AttentionViewModel(
    IHouseholdStore households, GetAttention getAttention, DismissDuplicate dismiss, UndoService undo, IClockFormat clock, TimeProvider time)
    : ObservableObject
{
    private HouseholdId? _household;

    [ObservableProperty]
    private IReadOnlyList<DuplicateItem> _duplicates = [];

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
        var pending = undo.PendingDelete;

        Duplicates = [.. attention.Duplicates
            .Where(p => p.First.Id != pending && p.Second.Id != pending)
            .Select(p => new DuplicateItem(
                p.First.KindCode.Value,
                $"{formats.Time(p.First.Timing.OccurredAt)} · {formats.Title(p.First, now)}",
                $"{formats.Time(p.Second.Timing.OccurredAt)} · {formats.Title(p.Second, now)}",
                new AsyncRelayCommand(async () =>
                {
                    await dismiss.ExecuteAsync(household.Id, p.Pair, CancellationToken.None);
                    await RefreshAsync();
                }),
                new AsyncRelayCommand(() => undo.DeleteWithUndoAsync(p.Second.Id, formats.Title(p.Second, now))),
                new AsyncRelayCommand(() => undo.DeleteWithUndoAsync(p.First.Id, formats.Title(p.First, now)))))];
        Unreadable = [.. attention.Quarantined.Where(q => q.Id != pending)
            .Select(q => new AttentionEntry(q.Id, q.KindCode, formats.Time(q.Start), Strings.Get("Edit.Unreadable")))];
        Newer = [.. attention.ReadOnly.Where(r => r.Id != pending)
            .Select(r => new AttentionEntry(r.Id, r.KindCode, formats.Time(r.Start), Strings.Get("Edit.Newer")))];
        _allRejected = [.. attention.Rejected.Where(r => r.Entry.Id != pending)
            .Select(r => new AttentionEntry(r.Entry.Id, r.Entry.KindCode, formats.Time(r.Entry.Start), Strings.Error(r.Reason)))];
        ShowRejected();
        IsEmpty = Duplicates.Count + Unreadable.Count + Newer.Count + _allRejected.Count == 0;
    }

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
}
