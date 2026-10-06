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

    public const string FingerprintsSection = "AppLinks:Sha256CertFingerprints";

    private static readonly System.Text.RegularExpressions.Regex Fingerprint =
        new("^([0-9A-F]{2}:){31}[0-9A-F]{2}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// The signing certificates' SHA-256 fingerprints for <c>assetlinks.json</c> (Spec 011b §4.2): the debug key's now, the Play
    /// app-signing key's in Increment 18. A malformed one fails in every environment; Production refuses to start without one.
    /// </summary>
    public static IReadOnlyList<string> SelectFingerprints(IEnumerable<string?> values, bool isProduction)
    {
        ArgumentNullException.ThrowIfNull(values);
        var list = new List<string>();
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
                continue;
            var normalised = value.Trim().ToUpperInvariant();
            if (!Fingerprint.IsMatch(normalised))
                throw new InvalidOperationException($"{FingerprintsSection} has '{value}': each must be 32 colon-separated hex bytes (keytool -list -v, SHA256).");
            list.Add(normalised);
        }

        return isProduction && list.Count == 0
            ? throw new InvalidOperationException(
                $"{FingerprintsSection} has no fingerprint. In production it comes from /opt/coo/.env (APPLINKS_SHA256_DEBUG) "
                + "through deploy/docker-compose.prod.yml (DEPLOY.md, \"App Links\").")
            : list;
    }

    public const string CodePepperKey = "Invitations:CodePepper";

    /// <summary>
    /// The pepper for invitation-code hashes (Spec 011b §10.3): base64, at least <see cref="MinimumKeyBytes"/> bytes. Production
    /// refuses to start without it, so a code is never hashed unkeyed; a malformed one fails in every environment. Elsewhere
    /// the missing pepper is <paramref name="developmentPepper"/>, so codes end with the process.
    /// </summary>
    public static byte[] SelectCodePepper(string? value, bool isProduction, byte[] developmentPepper)
    {
        ArgumentNullException.ThrowIfNull(developmentPepper);
        if (string.IsNullOrWhiteSpace(value))
        {
            return isProduction
                ? throw new InvalidOperationException(
                    $"{CodePepperKey} is not set. In production it comes from /opt/coo/.env (INVITATIONS_CODE_PEPPER), which deploy-prod.sh "
                    + "creates, through deploy/docker-compose.prod.yml (DEPLOY.md, \"Invitation codes\").")
                : developmentPepper;
        }

        byte[] pepper;
        try
        {
            pepper = Convert.FromBase64String(value.Trim());
        }
        catch (FormatException)
        {
            throw new InvalidOperationException($"{CodePepperKey} is not base64. Generate one with: openssl rand -base64 32");
        }

        return pepper.Length >= MinimumKeyBytes
            ? pepper
            : throw new InvalidOperationException(
                $"{CodePepperKey} has {pepper.Length} bytes; at least {MinimumKeyBytes} are required. Generate one with: openssl rand -base64 32");
    }

    public const string PerUserPerHourKey = "Invitations:PerUserPerHour";
    public const string PerAddressPerHourKey = "Invitations:PerAddressPerHour";
    public const string CreatePerUserPerDayKey = "Invitations:CreatePerUserPerDay";

    /// <summary>
    /// The invitation limits (Spec 011b §10.2): unset means the default; a set value must be a positive integer, so a typo
    /// fails at start-up with its name instead of refusing every call or throwing at the first request.
    /// </summary>
    public static InvitationLimits SelectInvitationLimits(string? perUserPerHour, string? perAddressPerHour, string? createPerUserPerDay) => new(
        PositiveInteger(PerUserPerHourKey, perUserPerHour, InvitationLimits.Default.PerUserPerHour),
        PositiveInteger(PerAddressPerHourKey, perAddressPerHour, InvitationLimits.Default.PerAddressPerHour),
        PositiveInteger(CreatePerUserPerDayKey, createPerUserPerDay, InvitationLimits.Default.CreatePerUserPerDay));

    private static int PositiveInteger(string key, string? value, int fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;
        return int.TryParse(value.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number) && number > 0
            ? number
            : throw new InvalidOperationException($"{key} is '{value}': it must be a positive whole number.");
    }
}
