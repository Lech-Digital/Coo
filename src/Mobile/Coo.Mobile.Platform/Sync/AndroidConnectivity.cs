using Android.Content;
using Android.Net;
using Coo.Mobile.Application.Sync;

namespace Coo.Mobile.Platform.Sync;

/// <summary>
/// Android's default-network callback (API 24+; Coo's minimum is 26). The callback is Java calling .NET: nothing may
/// escape it, or the app dies (Increment 10 lesson). Without a signal the 30-second pull still runs.
/// </summary>
internal sealed class AndroidConnectivity(Func<Context> context) : IConnectivityWatcher, IDisposable
{
    private Callback? _callback;
    private ConnectivityManager? _manager;

    public event EventHandler? Regained;

    public void Start()
    {
        if (_callback is not null)
            return;
        try
        {
            _manager = context().GetSystemService(Context.ConnectivityService) as ConnectivityManager;
            if (_manager is null)
                return;
            _callback = new Callback(this);
            _manager.RegisterDefaultNetworkCallback(_callback);
        }
#pragma warning disable CA1031 // No signal is fine: the periodic pull covers it.
        catch (Exception)
#pragma warning restore CA1031
        {
            _callback?.Dispose();
            _callback = null;
        }
    }

    public void Dispose()
    {
        try
        {
            if (_callback is not null)
                _manager?.UnregisterNetworkCallback(_callback);
        }
#pragma warning disable CA1031 // Shutting down: nothing to do about a callback Android already dropped.
        catch (Exception)
#pragma warning restore CA1031
        {
        }

        _callback?.Dispose();
        _callback = null;
    }

    private void Raise() => Regained?.Invoke(this, EventArgs.Empty);

    private sealed class Callback(AndroidConnectivity owner) : ConnectivityManager.NetworkCallback
    {
        public override void OnAvailable(Network network)
        {
            try
            {
                owner.Raise();
            }
#pragma warning disable CA1031 // A Java callback: a managed exception escaping here would crash the process.
            catch (Exception)
#pragma warning restore CA1031
            {
            }
        }
    }
}
