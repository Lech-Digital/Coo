using System.Net.Http.Json;
using Coo.Contracts.Households;
using Coo.Mobile.Application.Households;

namespace Coo.Mobile.Infrastructure.Households;

internal sealed class BabyAvatarClient(HttpClient http) : IBabyAvatarApi
{
    public async Task<BabyAvatarDto?> GetAvatarAsync(Guid householdId, Guid babyId, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.GetAsync($"api/households/{householdId}/babies/{babyId}/avatar", cancellationToken);
            if (!response.IsSuccessStatusCode)
                return null;

            return await response.Content.ReadFromJsonAsync<BabyAvatarDto>(cancellationToken: cancellationToken);
        }
        catch
        {
            return null;
        }
    }

    public async Task<byte[]?> GetAvatarImageAsync(Guid householdId, Guid babyId, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.GetAsync($"api/households/{householdId}/babies/{babyId}/avatar/image", cancellationToken);
            if (!response.IsSuccessStatusCode)
                return null;

            return await response.Content.ReadAsByteArrayAsync(cancellationToken);
        }
        catch
        {
            return null;
        }
    }

    public async Task<BabyAvatarDto?> SetAvatarAsync(Guid householdId, Guid babyId, SetBabyAvatarRequest request, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.PutAsJsonAsync($"api/households/{householdId}/babies/{babyId}/avatar", request, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return null;

            return await response.Content.ReadFromJsonAsync<BabyAvatarDto>(cancellationToken: cancellationToken);
        }
        catch
        {
            return null;
        }
    }
}
