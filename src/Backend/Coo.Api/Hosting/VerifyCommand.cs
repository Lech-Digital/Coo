using System.Globalization;
using Coo.Api.Application.CareLog;

namespace Coo.Api.Hosting;

/// <summary>
/// <c>dotnet Coo.Api.dll verify</c>: the integrity scan on demand (ADR 0010 G5). Exit code 0 when every row decodes,
/// 1 when any does not, so a script or a cron job can act on it.
/// </summary>
internal static class VerifyCommand
{
    public static async Task<int> RunAsync(IServiceProvider services, TextWriter output, CancellationToken cancellationToken)
    {
        var report = await services.GetRequiredService<VerifyIntegrity>().ExecuteAsync(cancellationToken);
        foreach (var failure in report.Failures)
            await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"FAIL {failure.Id.Value} {failure.Kind} {failure.Reason}"));
        await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"{report.Checked} rows checked, {report.Failures.Count} failures"));
        return report.IsClean ? 0 : 1;
    }
}
