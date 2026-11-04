using Coo.Domain.Common;
using Coo.Mobile.Application.Advisor;
using Coo.Mobile.Application.Billing;

namespace Coo.Mobile.Application.Insights;

/// <summary>The plan's words as the Advisor wrote them, kept on the phone so each is written once (Spec 019 §4).</summary>
public interface ISleepPlanWordsStore
{
    Task<string?> GetAsync(string key, CancellationToken cancellationToken);

    Task SaveAsync(string key, string text, CancellationToken cancellationToken);
}

/// <summary>What the Advisor writes: the plan, or a check-in's note.</summary>
public enum SleepPlanWordsKind
{
    Plan,
    CheckIn,
}

/// <summary>
/// The plan or check-in in the Advisor's words (Spec 019 §4, Premium). Kept by <c>key</c> (the facts' fingerprint), so a
/// plan that has not changed is never written twice. Without Premium, offline, or when the server says no, there are no
/// words: the page keeps the stored texts, and the plan works the same.
/// </summary>
public sealed class WriteSleepPlanWords(IAdvisorApi api, ISleepPlanWordsStore store, IBillingService billing)
{
    /// <summary>The words already written for <paramref name="key"/>: nothing is sent.</summary>
    public async Task<string?> KeptAsync(string key, CancellationToken cancellationToken) =>
        await store.GetAsync(key, cancellationToken) is { Length: > 0 } kept ? kept : null;

    /// <param name="facts">The computed plan in English, the baby written as {baby}.</param>
    public async Task<string?> ExecuteAsync(
        HouseholdId household, BabyId baby, SleepPlanWordsKind kind, string key, string facts, string language, string timeZoneId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(facts);
        if (await KeptAsync(key, cancellationToken) is { } kept)
            return kept;
        if (!billing.IsPremiumCached)
            return null;

        SleepPlanWordsResponse response;
        try
        {
            response = await api.WriteSleepPlanAsync(new SleepPlanWordsRequest
            {
                HouseholdId = household.Value,
                BabyId = baby.Value,
                Kind = kind == SleepPlanWordsKind.CheckIn ? "checkin" : "plan",
                Facts = facts,
                TimeZoneId = timeZoneId,
                Language = language,
            }, cancellationToken);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            return null;
        }

        if (response.ErrorCode is not null || string.IsNullOrWhiteSpace(response.Text))
            return null;
        await store.SaveAsync(key, response.Text, cancellationToken);
        return response.Text;
    }
}
