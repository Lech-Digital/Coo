using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Coo.Mobile.Application;

namespace Coo.Mobile.Infrastructure.AppVersion;

public sealed class VersionClient(HttpClient http) : IVersionApi
{
    public async Task<AppVersionInfo?> GetVersionInfoAsync(CancellationToken cancellationToken)
    {
        try
        {
            var response = await http.GetAsync("/api/app-version", cancellationToken);
            if (!response.IsSuccessStatusCode)
                return null;
            return await response.Content.ReadFromJsonAsync(VersionContext.Default.AppVersionInfo, cancellationToken);
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

public sealed record AppVersionInfo(
    [property: JsonPropertyName("latest")] int Latest,
    [property: JsonPropertyName("minimum")] int Minimum,
    [property: JsonPropertyName("isForced")] bool IsForced
);

[JsonSerializable(typeof(AppVersionInfo))]
internal sealed partial class VersionContext : JsonSerializerContext;
