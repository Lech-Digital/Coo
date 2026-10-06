using Coo.Contracts.Sync;
using Coo.Domain.Common;
using Coo.Mobile.Application.Sync;

namespace Coo.Mobile.Infrastructure.Persistence;

/// <summary>Consent per account, the soft-lock per household and §8.4's replacement family, as <c>app_state</c> rows (Spec 011 §3, §6.5).</summary>
internal sealed class SyncState(LocalDatabase database) : ISyncState
{
    /// <summary>Written by <see cref="FamilyRecords.DeleteAndReplaceAsync"/>, moved by a re-key (<see cref="HouseholdMoves.MoveAllAsync"/>).</summary>
    internal const string ReplacementKey = "replacement_household";

    private const string Declined = "declined";

    public async Task<ConsentState> GetConsentAsync(UserId user, CancellationToken cancellationToken) =>
        await GetAsync("consent:" + Columns.Id(user.Value), cancellationToken) switch
        {
            CareDataConsent.CurrentWording => ConsentState.Given,
            Declined => ConsentState.Declined,
            _ => ConsentState.Unknown,
        };

    public Task SetConsentAsync(UserId user, ConsentState state, CancellationToken cancellationToken) => state switch
    {
        ConsentState.Given => SetAsync("consent:" + Columns.Id(user.Value), CareDataConsent.CurrentWording, cancellationToken),
        ConsentState.Declined => SetAsync("consent:" + Columns.Id(user.Value), Declined, cancellationToken),
        _ => RemoveAsync("consent:" + Columns.Id(user.Value), cancellationToken),
    };

    public async Task<bool> IsSoftLockedAsync(HouseholdId household, CancellationToken cancellationToken) =>
        await GetAsync("softlock:" + Columns.Id(household.Value), cancellationToken) is not null;

    public Task SetSoftLockAsync(HouseholdId household, bool locked, CancellationToken cancellationToken) => locked
        ? SetAsync("softlock:" + Columns.Id(household.Value), "1", cancellationToken)
        : RemoveAsync("softlock:" + Columns.Id(household.Value), cancellationToken);

    public async Task<HouseholdId?> GetReplacementHouseholdAsync(CancellationToken cancellationToken) =>
        Columns.ParseGuid(await GetAsync(ReplacementKey, cancellationToken)) is { } id ? new HouseholdId(id) : null;

    public async Task ClearReplacementHouseholdAsync(HouseholdId household, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await connection.ExecuteAsync(
            "DELETE FROM app_state WHERE key = $key AND value = $value;", cancellationToken, null, ("$key", ReplacementKey), ("$value", Columns.Id(household.Value)));
    }

    private async Task<string?> GetAsync(string key, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        return (string?)await connection.ScalarAsync("SELECT value FROM app_state WHERE key = $key;", cancellationToken, null, ("$key", key));
    }

    private async Task SetAsync(string key, string value, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await connection.ExecuteAsync(
            "INSERT INTO app_state (key, value) VALUES ($key, $value) ON CONFLICT (key) DO UPDATE SET value = excluded.value;",
            cancellationToken, null, ("$key", key), ("$value", value));
    }

    private async Task RemoveAsync(string key, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await connection.ExecuteAsync("DELETE FROM app_state WHERE key = $key;", cancellationToken, null, ("$key", key));
    }
}
