using System.ComponentModel;
using Coo.Domain.Households;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Presentation.Formatting;
using Coo.Mobile.Presentation.Theme;

namespace Coo.Mobile.Presentation.Today;

/// <summary>Where the family is (Spec 006 §3a): a baby on the way, or a baby here.</summary>
public enum HomeStage
{
    Expecting,
    Born,
}

/// <summary>Everything a section may show, computed once per refresh or tick.</summary>
public sealed record HomeSnapshot(
    HouseholdSettings Household, BabyProfile Baby, TodaySummary? Today, DateTimeOffset Now, PaletteKind Palette, Formats Formats);

/// <summary>
/// One block of Today (Spec 006 §3a, ADR 0010 §5). Today only lays out the sections that apply to the current stage and
/// palette, in order; a new feature (pregnancy tips, a contraction timer) adds a section, never a special case.
/// </summary>
public interface IHomeSection : INotifyPropertyChanged
{
    int Order { get; }

    bool AppliesTo(HomeStage stage, PaletteKind palette);

    void Update(HomeSnapshot snapshot);
}
