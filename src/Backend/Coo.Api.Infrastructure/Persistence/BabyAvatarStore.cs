using Coo.Api.Application.Households;
using Microsoft.EntityFrameworkCore;

namespace Coo.Api.Infrastructure.Persistence;

internal sealed class BabyAvatarStore(IDbContextFactory<CooDbContext> contexts, TimeProvider time) : IBabyAvatarStore
{
    public async Task<BabyAvatarData?> GetAvatarAsync(Guid householdId, Guid babyId, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var record = await db.Set<BabyAvatarRecord>()
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.BabyId == babyId && a.HouseholdId == householdId, cancellationToken);

        if (record == null)
            return null;

        return new BabyAvatarData(record.AvatarType, record.PresetKey, record.ImageData, record.Version);
    }

    public async Task<BabyAvatarData> SetAvatarAsync(
        Guid householdId, Guid babyId, string avatarType, string? presetKey, byte[]? imageData, Guid updatedBy, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var record = await db.Set<BabyAvatarRecord>()
            .FirstOrDefaultAsync(a => a.BabyId == babyId && a.HouseholdId == householdId, cancellationToken);

        var now = time.GetUtcNow();
        var version = now.ToUnixTimeMilliseconds();

        if (record == null)
        {
            record = new BabyAvatarRecord
            {
                BabyId = babyId,
                HouseholdId = householdId,
                AvatarType = avatarType,
                PresetKey = presetKey,
                ImageData = imageData,
                Version = version,
                UpdatedAt = now.UtcDateTime,
                UpdatedBy = updatedBy,
            };
            db.Set<BabyAvatarRecord>().Add(record);
        }
        else
        {
            record.AvatarType = avatarType;
            record.PresetKey = presetKey;
            record.ImageData = avatarType == "custom" ? (imageData ?? record.ImageData) : null;
            record.Version = version;
            record.UpdatedAt = now.UtcDateTime;
            record.UpdatedBy = updatedBy;
        }

        await db.SaveChangesAsync(cancellationToken);
        return new BabyAvatarData(record.AvatarType, record.PresetKey, record.ImageData, record.Version);
    }
}
