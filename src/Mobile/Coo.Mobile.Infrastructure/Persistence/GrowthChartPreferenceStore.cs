using Coo.Mobile.Application.Growth;

namespace Coo.Mobile.Infrastructure.Persistence;

/// <summary>The growth chart's standard (girls' or boys'), in <c>app_state</c>; never synced.</summary>
internal sealed class GrowthChartPreferenceStore(LocalDatabase database) : IGrowthChartPreference
{
    private const string Key = "growth.chart_sex";

    public async Task<ChartSex?> GetAsync(CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        var value = (string?)await connection.ScalarAsync("SELECT value FROM app_state WHERE key = $k;", cancellationToken, null, ("$k", Key));
        return Enum.TryParse<ChartSex>(value, out var sex) && Enum.IsDefined(sex) ? sex : null;
    }

    public async Task SetAsync(ChartSex sex, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await connection.ExecuteAsync(
            "INSERT INTO app_state (key, value) VALUES ($k, $v) ON CONFLICT (key) DO UPDATE SET value = excluded.value;",
            cancellationToken, null, ("$k", Key), ("$v", sex.ToString()));
    }
}
