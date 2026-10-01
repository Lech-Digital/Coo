namespace Coo.Api.Hosting;

/// <summary>
/// Settings that must exist before the API starts (Spec 008 §3). A missing one fails at start-up, with its name,
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
}
