using Coo.Api.Application.Billing;
using Google.Apis.AndroidPublisher.v3;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using Microsoft.Extensions.Configuration;

namespace Coo.Api.Infrastructure.Billing;

internal sealed class GooglePlayValidator(IConfiguration configuration, TimeProvider timeProvider) : IGooglePlayValidator
{
    private readonly string? _credentialsJson = configuration["GooglePlay:CredentialsJson"];
    private readonly string _packageName = configuration["GooglePlay:PackageName"] ?? "com.lechdigital.coo";

    public async Task<GooglePlayPurchaseInfo?> VerifyPurchaseAsync(string productId, string purchaseToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_credentialsJson))
            return null; // Local testing without real credentials

#pragma warning disable CS0618
        var credential = GoogleCredential.FromJson(_credentialsJson)
            .CreateScoped(AndroidPublisherService.Scope.Androidpublisher);
#pragma warning restore CS0618

        using var service = new AndroidPublisherService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "CooApp"
        });

        try
        {
            var purchase = await service.Purchases.Subscriptionsv2.Get(_packageName, purchaseToken).ExecuteAsync(cancellationToken);
            if (purchase == null) return null;

            var expiry = purchase.LineItems?.FirstOrDefault()?.ExpiryTimeDateTimeOffset?.UtcDateTime ?? timeProvider.GetUtcNow().UtcDateTime;
            
            SubscriptionStatus status = purchase.SubscriptionState switch
            {
                "SUBSCRIPTION_STATE_ACTIVE" => SubscriptionStatus.Active,
                "SUBSCRIPTION_STATE_IN_GRACE_PERIOD" => SubscriptionStatus.GracePeriod,
                "SUBSCRIPTION_STATE_PAUSED" => SubscriptionStatus.Paused,
                "SUBSCRIPTION_STATE_CANCELED" => expiry <= timeProvider.GetUtcNow().UtcDateTime ? SubscriptionStatus.Expired : SubscriptionStatus.Canceled,
                "SUBSCRIPTION_STATE_EXPIRED" => SubscriptionStatus.Expired,
                "SUBSCRIPTION_STATE_ON_HOLD" => SubscriptionStatus.Paused,
                _ => SubscriptionStatus.Expired
            };
            
            var autoRenewing = purchase.LineItems?.Any(li => li.AutoRenewingPlan != null) ?? false;

            return new GooglePlayPurchaseInfo(
                purchaseToken,
                productId,
                expiry,
                status,
                autoRenewing
            );
        }
        catch (Exception)
        {
            return null;
        }
    }
}
