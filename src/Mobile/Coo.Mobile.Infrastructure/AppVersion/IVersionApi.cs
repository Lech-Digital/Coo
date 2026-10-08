namespace Coo.Mobile.Infrastructure.AppVersion;

public interface IVersionApi
{
    Task<AppVersionInfo?> GetVersionInfoAsync(CancellationToken cancellationToken);
}
