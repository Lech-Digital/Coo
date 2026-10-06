using Coo.Domain.CareLog;
using Coo.Domain.Common;
using Coo.Mobile.Application.Auth;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Application.Households;
using Coo.Mobile.Presentation.Localization;

namespace Coo.Mobile.Presentation.Today;

/// <summary>
/// Who logged an entry, as Today's and Log's rows say it (walk fix F4): "You" for the signed-in person and for entries logged
/// on this phone before it was shared; another member by first name, or full name when two members share it; anyone the
/// family's list does not hold (a member removed since) is "Another parent". Signed out, every entry is "You", as before.
/// </summary>
public sealed class WhoLabels
{
    private readonly UserId? _me;
    private readonly Dictionary<UserId, string> _names;

    private WhoLabels(UserId? me, Dictionary<UserId, string> names) => (_me, _names) = (me, names);

    public static WhoLabels SignedOut { get; } = new(null, []);

    public static WhoLabels For(UserId me, IReadOnlyList<FamilyMemberName> members)
    {
        ArgumentNullException.ThrowIfNull(members);
        var named = members.Where(m => m.Name.Trim().Length > 0).ToList();
        var shared = named.GroupBy(m => First(m.Name), StringComparer.CurrentCultureIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key)
            .ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        var names = new Dictionary<UserId, string>();
        foreach (var member in named)
            names[member.UserId] = shared.Contains(First(member.Name)) ? member.Name.Trim() : First(member.Name);
        return new WhoLabels(me, names);
    }

    public string For(StoredEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return (_me, LoggedBy(entry)) switch
        {
            (null, _) or (_, null or ThisDevicesPersonRef) => Strings.Get("Who.You"),
            ({ } me, UserPerson { UserId: var user }) when user == me => Strings.Get("Who.You"),
            (_, UserPerson { UserId: var user }) when _names.TryGetValue(user, out var name) => name,
            _ => Strings.Get("Who.Other"),
        };
    }

    private static PersonRef? LoggedBy(StoredEntry entry) => entry switch
    {
        LiveEntry live => live.Entry.LoggedBy,
        ReadOnlyEntry readOnly => readOnly.LoggedBy,
        _ => null,
    };

    private static string First(string name) => name.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
}

/// <summary>The labels for a family: the signed-in person and the family's member names as the phone last heard them.</summary>
public sealed class WhoLabelsSource(ISessionStore sessions, IFamilyStore families)
{
    /// <summary>Never throws: a failed read keeps today's "You" for every entry.</summary>
    public async Task<WhoLabels> ForAsync(HouseholdId household)
    {
        try
        {
            if (await sessions.GetAsync(CancellationToken.None) is not { } session)
                return WhoLabels.SignedOut;
            return WhoLabels.For(session.Profile.UserId, await families.ListMemberNamesAsync(household, CancellationToken.None));
        }
#pragma warning disable CA1031 // The label is a courtesy: a failed local read must never stop Today or Log from drawing.
        catch (Exception)
#pragma warning restore CA1031
        {
            return WhoLabels.SignedOut;
        }
    }
}
