using Coo.Contracts.Households;

namespace Coo.Mobile.Application.Households;

public interface IBabyAvatarApi
{
    Task<BabyAvatarDto?> GetAvatarAsync(Guid householdId, Guid babyId, CancellationToken cancellationToken);
    Task<byte[]?> GetAvatarImageAsync(Guid householdId, Guid babyId, CancellationToken cancellationToken);
    Task<BabyAvatarDto?> SetAvatarAsync(Guid householdId, Guid babyId, SetBabyAvatarRequest request, CancellationToken cancellationToken);
}
