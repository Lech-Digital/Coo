namespace Coo.Api.Hosting;

/// <summary>The sign-in settings after the start-up check (Spec 010 §4.3).</summary>
internal sealed record AuthSettings(string? GoogleWebClientId, string ActiveKeyId, IReadOnlyDictionary<string, byte[]> SigningKeys);
