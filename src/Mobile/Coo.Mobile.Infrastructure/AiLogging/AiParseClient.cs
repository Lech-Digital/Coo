using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Coo.Contracts.AiParsing;
using Coo.Mobile.Application.AiLogging;

namespace Coo.Mobile.Infrastructure.AiLogging;

internal sealed class AiParseClient(HttpClient http) : IAiParseApi
{
    private static readonly Uri ParseUri = new("api/ai/parse", UriKind.Relative);
    private static readonly Uri ParseImageUri = new("api/ai/parse-image", UriKind.Relative);

    public Task<AiParseResponse?> ParseAsync(AiParseRequest request, CancellationToken cancellationToken) =>
        PostAsync(ParseUri, JsonContent.Create(request, AiParsingJsonContext.Default.AiParseRequest), cancellationToken);

    public Task<AiParseResponse?> ParseImageAsync(AiImageRequest request, CancellationToken cancellationToken) =>
        PostAsync(ParseImageUri, JsonContent.Create(request, AiParsingJsonContext.Default.AiImageRequest), cancellationToken);

    private async Task<AiParseResponse?> PostAsync(Uri uri, HttpContent content, CancellationToken cancellationToken)
    {
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, uri) { Content = content };
            using var response = await http.SendAsync(message, cancellationToken);
            // Not signed in, or not sharing with this family: waiting would not help, so it is not a pending note.
            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
                return new AiParseResponse([], [], "", "ai.signed-out");
            if (!response.IsSuccessStatusCode)
                return null;
            return await response.Content.ReadFromJsonAsync(AiParsingJsonContext.Default.AiParseResponse, cancellationToken);
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException { InnerException: TimeoutException })
        {
            return null;
        }
    }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(AiParseRequest))]
[JsonSerializable(typeof(AiImageRequest))]
[JsonSerializable(typeof(AiParseResponse))]
[JsonSerializable(typeof(List<PendingNote>))]
internal sealed partial class AiParsingJsonContext : JsonSerializerContext;
