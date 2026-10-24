using Coo.Api.Application.AiParsing;
using Coo.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace Coo.Api.Infrastructure.Persistence;

internal sealed class AiParsingStore(IDbContextFactory<CooDbContext> contexts) : IAiParsingStore
{
    public async Task<(IReadOnlyList<string> Babies, IReadOnlyList<string> Caregivers)> NamesAsync(HouseholdId household, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var babies = await db.BabyProfiles.AsNoTracking()
            .Where(b => b.HouseholdId == household.Value && !b.IsDeleted)
            .Select(b => new { b.Name, b.Nicknames })
            .ToListAsync(cancellationToken);
        var caregivers = await db.Memberships.AsNoTracking()
            .Where(m => m.HouseholdId == household.Value && m.RevokedAt == null)
            .Join(db.Users, m => m.UserId, u => u.Id, (m, u) => u.DisplayName)
            .ToListAsync(cancellationToken);
        return (
            [.. babies.SelectMany(b => b.Nicknames.Prepend(b.Name)).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct()],
            [.. caregivers.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!).Distinct()]);
    }

    public async Task<int> CountAsync(HouseholdId household, DateOnly day, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        // One statement, so two phones parsing at once both count.
        var counts = await db.Database.SqlQuery<int>($"""
            INSERT INTO ai_parse_usages (household_id, date, parse_count) VALUES ({household.Value}, {day}, 1)
            ON CONFLICT (household_id, date) DO UPDATE SET parse_count = ai_parse_usages.parse_count + 1
            RETURNING parse_count AS "Value"
            """).ToListAsync(cancellationToken);
        return counts.Single();
    }

    public async Task CountVoiceAsync(HouseholdId household, DateOnly day, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO ai_parse_usages (household_id, date, parse_count, voice_count) VALUES ({household.Value}, {day}, 0, 1)
            ON CONFLICT (household_id, date) DO UPDATE SET voice_count = ai_parse_usages.voice_count + 1
            """, cancellationToken);
    }

    public async Task<int> UsedVoiceAsync(HouseholdId household, DateOnly day, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        return await db.AiParseUsages.AsNoTracking()
            .Where(u => u.HouseholdId == household.Value && u.Date == day)
            .Select(u => u.VoiceCount)
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<int> UsedAsync(HouseholdId household, DateOnly day, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        return await db.AiParseUsages.AsNoTracking()
            .Where(u => u.HouseholdId == household.Value && u.Date == day)
            .Select(u => u.ParseCount)
            .SingleOrDefaultAsync(cancellationToken);
    }
}
