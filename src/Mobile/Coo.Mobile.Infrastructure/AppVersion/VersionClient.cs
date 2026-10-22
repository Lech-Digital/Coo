using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Coo.Mobile.Application.AppVersion;

namespace Coo.Mobile.Infrastructure.AppVersion;

internal sealed class VersionClient(HttpClient http) : IAppVersionApi
{
    public async Task<AppVersionPolicy?> GetAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.GetAsync(new Uri("api/app-version", UriKind.Relative), cancellationToken);
            if (!response.IsSuccessStatusCode)
                return null;
            var info = await response.Content.ReadFromJsonAsync(VersionContext.Default.AppVersionInfo, cancellationToken);
            return info is null ? null : new AppVersionPolicy(info.Latest, info.Minimum);
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (TaskCanceledException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

internal sealed record AppVersionInfo(
    [property: JsonPropertyName("latest")] int Latest,
    [property: JsonPropertyName("minimum")] int Minimum,
    [property: JsonPropertyName("isForced")] bool IsForced
);

[JsonSerializable(typeof(AppVersionInfo))]
internal sealed partial class VersionContext : JsonSerializerContext;
