using Coo.Contracts.Households;
using Coo.Mobile.Application.Preferences;

namespace Coo.Mobile.Application.Households;

public sealed class BabyAvatarSyncService
{
    private readonly IBabyAvatarApi _api;
    private readonly IDevicePreferences _preferences;
    private readonly string _cacheDirectory;

    public event EventHandler? AvatarChanged;

    public BabyAvatarSyncService(IBabyAvatarApi api, IDevicePreferences preferences, string cacheDirectory)
    {
        _api = api;
        _preferences = preferences;
        _cacheDirectory = cacheDirectory;
    }

    public void NotifyAvatarChanged() => AvatarChanged?.Invoke(this, EventArgs.Empty);

    public async Task<string?> SyncBabyAvatarAsync(Guid householdId, Guid babyId, CancellationToken cancellationToken)
    {
        try
        {
            var remote = await _api.GetAvatarAsync(householdId, babyId, cancellationToken);
            if (remote == null)
                return await _preferences.GetBabyAvatarAsync(babyId, cancellationToken);

            var currentRef = await _preferences.GetBabyAvatarAsync(babyId, cancellationToken);

            if (remote.AvatarType == "none")
            {
                if (currentRef != null)
                {
                    await _preferences.SetBabyAvatarAsync(babyId, null, cancellationToken);
                    AvatarChanged?.Invoke(this, EventArgs.Empty);
                }
                return null;
            }

            if (remote.AvatarType == "preset" && !string.IsNullOrWhiteSpace(remote.PresetKey))
            {
                var newRef = $"preset:{remote.PresetKey}";
                if (currentRef != newRef)
                {
                    await _preferences.SetBabyAvatarAsync(babyId, newRef, cancellationToken);
                    AvatarChanged?.Invoke(this, EventArgs.Empty);
                }
                return newRef;
            }

            if (remote.AvatarType == "custom" && remote.HasImage)
            {
                var targetFile = Path.Combine(_cacheDirectory, $"baby_avatar_{babyId}_{remote.Version}.jpg");
                if (!File.Exists(targetFile))
                {
                    var imageBytes = await _api.GetAvatarImageAsync(householdId, babyId, cancellationToken);
                    if (imageBytes != null && imageBytes.Length > 0)
                    {
                        await File.WriteAllBytesAsync(targetFile, imageBytes, cancellationToken);
                    }
                }

                if (File.Exists(targetFile))
                {
                    var newRef = $"file:{targetFile}";
                    if (currentRef != newRef)
                    {
                        await _preferences.SetBabyAvatarAsync(babyId, newRef, cancellationToken);
                        AvatarChanged?.Invoke(this, EventArgs.Empty);
                    }
                    return newRef;
                }
            }

            return currentRef;
        }
        catch
        {
            // Offline or network error: return current local avatar
            return await _preferences.GetBabyAvatarAsync(babyId, cancellationToken);
        }
    }

    public async Task SetPresetAvatarAsync(Guid householdId, Guid babyId, string presetKey, CancellationToken cancellationToken)
    {
        var localRef = $"preset:{presetKey}";
        await _preferences.SetBabyAvatarAsync(babyId, localRef, cancellationToken);
        AvatarChanged?.Invoke(this, EventArgs.Empty);

        try
        {
            await _api.SetAvatarAsync(householdId, babyId, new SetBabyAvatarRequest
            {
                AvatarType = "preset",
                PresetKey = presetKey
            }, cancellationToken);
        }
        catch
        {
            // Will sync when online
        }
    }

    public async Task SetPhotoAvatarAsync(Guid householdId, Guid babyId, string filePath, CancellationToken cancellationToken)
    {
        var localRef = $"file:{filePath}";
        await _preferences.SetBabyAvatarAsync(babyId, localRef, cancellationToken);
        AvatarChanged?.Invoke(this, EventArgs.Empty);

        try
        {
            var bytes = await File.ReadAllBytesAsync(filePath, cancellationToken);
            var base64 = Convert.ToBase64String(bytes);

            await _api.SetAvatarAsync(householdId, babyId, new SetBabyAvatarRequest
            {
                AvatarType = "custom",
                ImageBase64 = base64
            }, cancellationToken);
        }
        catch
        {
            // Will sync when online
        }
    }

    public async Task ClearAvatarAsync(Guid householdId, Guid babyId, CancellationToken cancellationToken)
    {
        await _preferences.SetBabyAvatarAsync(babyId, null, cancellationToken);
        AvatarChanged?.Invoke(this, EventArgs.Empty);

        try
        {
            await _api.SetAvatarAsync(householdId, babyId, new SetBabyAvatarRequest
            {
                AvatarType = "none"
            }, cancellationToken);
        }
        catch
        {
            // Will sync when online
        }
    }
}
