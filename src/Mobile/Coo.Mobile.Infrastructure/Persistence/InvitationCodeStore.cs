using Coo.Domain.Common;
using Coo.Domain.Households;
using Coo.Mobile.Application.Households;
using Microsoft.Data.Sqlite;

namespace Coo.Mobile.Infrastructure.Persistence;

/// <summary>
/// The codes of the invitations this phone made (walk fix F3), in <c>app_state</c> under <c>invitation_code:{family}:{invitation}</c>.
/// The code is never logged.
/// </summary>
internal sealed class InvitationCodeStore(LocalDatabase database) : IInvitationCodes
{
    internal const string KeyPrefix = "invitation_code:";

    private long _remembered;

    public long Remembered => Interlocked.Read(ref _remembered);

    internal static string Prefix(HouseholdId household) => $"{KeyPrefix}{Columns.Id(household.Value)}:";

    public async Task<IReadOnlyDictionary<Guid, InvitationCode>> ListAsync(HouseholdId household, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        return await ReadAsync(connection, null, household, cancellationToken);
    }

    public async Task RememberAsync(HouseholdId household, Guid invitationId, InvitationCode code, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(code);
        await using var connection = await database.OpenAsync(cancellationToken);
        await connection.ExecuteAsync(
            "INSERT INTO app_state (key, value) VALUES ($k, $v) ON CONFLICT (key) DO UPDATE SET value = excluded.value;",
            cancellationToken, null, ("$k", Prefix(household) + Columns.Id(invitationId)), ("$v", code.Value));
        Interlocked.Increment(ref _remembered);
    }

    public async Task KeepOnlyAsync(HouseholdId household, IReadOnlyCollection<Guid> open, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(open);
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        foreach (var gone in (await ReadAsync(connection, transaction, household, cancellationToken)).Keys.Where(id => !open.Contains(id)))
        {
            await connection.ExecuteAsync(
                "DELETE FROM app_state WHERE key = $k;", cancellationToken, transaction, ("$k", Prefix(household) + Columns.Id(gone)));
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task<Dictionary<Guid, InvitationCode>> ReadAsync(
        SqliteConnection connection, SqliteTransaction? transaction, HouseholdId household, CancellationToken cancellationToken)
    {
        var prefix = Prefix(household);
        await using var command = connection.Command(
            "SELECT key, value FROM app_state WHERE substr(key, 1, length($p)) = $p;", transaction, ("$p", prefix));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var codes = new Dictionary<Guid, InvitationCode>();
        while (await reader.ReadAsync(cancellationToken))
        {
            if (Columns.ParseGuid(reader.GetString(0)[prefix.Length..]) is { } id && InvitationCode.Parse(reader.GetString(1)) is { IsSuccess: true } code)
                codes[id] = code.Value;
        }

        return codes;
    }
}
