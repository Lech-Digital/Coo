namespace Coo.Api.Hosting;

/// <summary>
/// Settings that must exist before the API starts (Spec 008 §3, Spec 010 §4.3). A missing one fails at start-up, with its name,
/// never at runtime.
/// </summary>
internal static class StartupChecks
{
    public const string ConnectionStringName = "Coo";


    public static string RequireConnectionString(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException(
                $"ConnectionStrings:{ConnectionStringName} is not set. In production it comes from deploy/docker-compose.prod.yml "
                + "(ConnectionStrings__Coo); locally, set it with `dotnet user-secrets` or an environment variable.")
            : value;

    public const string GoogleWebClientIdKey = "Auth:GoogleWebClientId";
    public const string SigningKeysSection = "Auth:SigningKeys";
    public const string ActiveSigningKeyIdKey = "Auth:ActiveSigningKeyId";
    public const string DevelopmentKeyId = "development";
    public const int MinimumKeyBytes = 32;

    /// <summary>
    /// The sign-in settings (Spec 010 §4.3, ADR 0003 §5). Production refuses to start without the Google Web client ID or
    /// a signing key, so a missing setting fails here, never by accepting or refusing every sign-in later. A key that is
    /// set but unusable fails in every environment. Elsewhere, missing settings mean no client ID (every sign-in is
    /// refused) and <paramref name="developmentKey"/>, so tokens end with the process.
    /// </summary>
    public static AuthSettings SelectAuth(
        string? googleWebClientId, IEnumerable<KeyValuePair<string, string?>> signingKeys, string? activeKeyId, bool isProduction,
        byte[] developmentKey)
    {
        ArgumentNullException.ThrowIfNull(signingKeys);
        ArgumentNullException.ThrowIfNull(developmentKey);

        var keys = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var (id, value) in signingKeys)
        {
            if (!string.IsNullOrWhiteSpace(value))
                keys[id] = DecodeKey(id, value);
        }

        if (keys.Count > 2)
            throw new InvalidOperationException($"{SigningKeysSection} has {keys.Count} keys; at most two are allowed (one signing, one being retired).");

        var clientId = string.IsNullOrWhiteSpace(googleWebClientId) ? null : googleWebClientId.Trim();
        if (isProduction && clientId is null)
        {
            throw new InvalidOperationException(
                $"{GoogleWebClientIdKey} is not set. In production it comes from /opt/coo/.env (AUTH_GOOGLE_WEB_CLIENT_ID) "
                + "through deploy/docker-compose.prod.yml (DEPLOY.md, \"Sign-in settings\").");
        }

        if (isProduction && keys.Count == 0)
        {
            throw new InvalidOperationException(
                $"{SigningKeysSection} has no key. In production it comes from /opt/coo/.env (AUTH_SIGNING_KEY_K1) "
                + "through deploy/docker-compose.prod.yml (DEPLOY.md, \"Sign-in settings\").");
        }

        if (keys.Count == 0)
            return new AuthSettings(clientId, DevelopmentKeyId, new Dictionary<string, byte[]>(StringComparer.Ordinal) { [DevelopmentKeyId] = developmentKey });

        string active;
        if (!string.IsNullOrWhiteSpace(activeKeyId))
            active = activeKeyId.Trim();
        else if (keys.Count == 1)
            active = keys.Keys.Single();
        else
            throw new InvalidOperationException($"{SigningKeysSection} has two keys: set {ActiveSigningKeyIdKey} to the one that signs.");

        return keys.ContainsKey(active)
            ? new AuthSettings(clientId, active, keys)
            : throw new InvalidOperationException($"{ActiveSigningKeyIdKey} is '{active}', which is not one of the keys in {SigningKeysSection}.");
    }

    private static byte[] DecodeKey(string id, string value)
    {
        byte[] key;
        try
        {
            key = Convert.FromBase64String(value.Trim());
        }
        catch (FormatException)
        {
            throw new InvalidOperationException($"{SigningKeysSection}:{id} is not base64. Generate a key with: openssl rand -base64 32");
        }

        return key.Length >= MinimumKeyBytes
            ? key
            : throw new InvalidOperationException(
                $"{SigningKeysSection}:{id} has {key.Length} bytes; at least {MinimumKeyBytes} are required. Generate a key with: openssl rand -base64 32");
    }
}
