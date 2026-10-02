using System.Text.Json;
using System.Text.Json.Serialization;
using Coo.Contracts.Auth;

namespace Coo.Mobile.Infrastructure.Auth;

/// <summary>Source-generated, so the trimmed Android build keeps it (as <c>CodecJsonContext</c>). Web defaults: camelCase.</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(NonceResponse))]
[JsonSerializable(typeof(GoogleSignInRequest))]
[JsonSerializable(typeof(SessionResponse))]
[JsonSerializable(typeof(RefreshRequest))]
[JsonSerializable(typeof(TokensResponse))]
[JsonSerializable(typeof(MeResponse))]
[JsonSerializable(typeof(DeleteAccountRequest))]
[JsonSerializable(typeof(ErrorResponse))]
internal sealed partial class AuthJsonContext : JsonSerializerContext;
