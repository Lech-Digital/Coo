using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;
using Coo.Mobile.Application.CareLog;

namespace Coo.Mobile.Infrastructure.Persistence;

/// <summary>
/// The device identity and the persisted hybrid logical clock (Spec 004 §4.4). One instance per app: the clock
/// is cached after the first read, and a lock keeps stamps strictly increasing across threads.
/// </summary>
internal sealed class DeviceState(LocalDatabase database, TimeProvider time) : IVersionClock, IDeviceIdentity, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private HybridLogicalClock? _clock;

    public async Task<DeviceId> GetAsync(CancellationToken cancellationToken) => (await ClockAsync(cancellationToken)).DeviceId;

    public async Task<HlcStamp> NextAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var clock = await LoadAsync(cancellationToken);
            var stamp = clock.Tick(time.GetUtcNow().ToUnixTimeMilliseconds());

            // Its own small transaction: if the write that uses this stamp fails, a stamp is skipped, which is
            // harmless. Stamps must increase, not be contiguous (Spec 004 §5).
            await using var connection = await database.OpenAsync(cancellationToken);
            await connection.ExecuteAsync(
                "UPDATE device_state SET hlc_wall_ms = $wall, hlc_counter = $counter WHERE id = 1;",
                cancellationToken, null, ("$wall", stamp.WallClockMs), ("$counter", stamp.Counter));
            return stamp;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ObserveAsync(HlcStamp remote, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var clock = await LoadAsync(cancellationToken);
            var stamp = clock.Observe(remote, time.GetUtcNow().ToUnixTimeMilliseconds());
            await using var connection = await database.OpenAsync(cancellationToken);
            await connection.ExecuteAsync(
                "UPDATE device_state SET hlc_wall_ms = $wall, hlc_counter = $counter WHERE id = 1;",
                cancellationToken, null, ("$wall", stamp.WallClockMs), ("$counter", stamp.Counter));
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    /// <summary>Creates the device identity on first launch. Called once by startup.</summary>
    public async Task EnsureCreatedAsync(CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await connection.ExecuteAsync(
            "INSERT INTO device_state (id, device_id) VALUES (1, $device) ON CONFLICT (id) DO NOTHING;",
            cancellationToken, null, ("$device", Columns.Id(Guid.CreateVersion7(time.GetUtcNow()))));
    }

    private async Task<HybridLogicalClock> ClockAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await LoadAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<HybridLogicalClock> LoadAsync(CancellationToken cancellationToken)
    {
        if (_clock is not null)
            return _clock;

        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.Command("SELECT device_id, hlc_wall_ms, hlc_counter FROM device_state WHERE id = 1;");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException("The device identity has not been created: startup must run first.");

        var device = new DeviceId(Guid.Parse(reader.GetString(0)));
        HlcStamp? last = reader.IsDBNull(1) ? null : new HlcStamp(reader.GetInt64(1), (int)reader.GetInt64(2), device);
        return _clock = new HybridLogicalClock(device, last);
    }
}
