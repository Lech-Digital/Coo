using Android.App;
using Coo.Mobile.Application.Auth;
using Coo.Mobile.Application.Preferences;
using Coo.Mobile.Platform.Auth;
using Microsoft.Extensions.DependencyInjection;

namespace Coo.Mobile.Platform;

/// <summary>The one public entry point of this project (ADR 0001 §2).</summary>
public static class ServiceCollectionExtensions
{
    /// <param name="currentActivity">The activity the account sheet opens over; the host passes MAUI's current activity.</param>
    /// <param name="googleWebClientId">Coo's Web client ID: the audience of the Google ID token (Spec 010 §6).</param>
    public static IServiceCollection AddMobilePlatform(this IServiceCollection services, Func<Activity?> currentActivity, string googleWebClientId)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(currentActivity);
        ArgumentException.ThrowIfNullOrWhiteSpace(googleWebClientId);
        services.AddSingleton<IClockFormat, AndroidClockFormat>();
        services.AddSingleton<IDeviceName, AndroidDeviceName>();
        services.AddSingleton<ISignInProvider>(new CredentialManagerSignInProvider(currentActivity, googleWebClientId));
        return services;
    }
}
