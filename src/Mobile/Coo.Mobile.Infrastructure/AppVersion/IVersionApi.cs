namespace Coo.Mobile.Infrastructure.AppVersion;

internal interface IVersionApi
{
    Task<AppVersionInfo?> GetVersionInfoAsync(CancellationToken cancellationToken);
}
