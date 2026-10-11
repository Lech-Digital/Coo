namespace Coo.Api.Application.Households;

public sealed record BabyAvatarData(string AvatarType, string? PresetKey, byte[]? ImageData, long Version);

public interface IBabyAvatarStore
{
    Task<BabyAvatarData?> GetAvatarAsync(Guid householdId, Guid babyId, CancellationToken cancellationToken);
    Task<BabyAvatarData> SetAvatarAsync(Guid householdId, Guid babyId, string avatarType, string? presetKey, byte[]? imageData, Guid updatedBy, CancellationToken cancellationToken);
}
