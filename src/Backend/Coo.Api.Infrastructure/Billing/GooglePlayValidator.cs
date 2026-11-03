using System.Net;
using Coo.Api.Application.Billing;
using Google;
using Google.Apis.AndroidPublisher.v3;
using Google.Apis.AndroidPublisher.v3.Data;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Coo.Api.Infrastructure.Billing;

internal sealed partial class GooglePlayValidator(IConfiguration configuration, TimeProvider timeProvider, ILogger<GooglePlayValidator> logger) : IGooglePlayValidator
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Google Play could not be asked about a purchase of {ProductId}: {ExceptionType} {Status}")]
    private static partial void LogUnavailable(ILogger logger, string productId, string exceptionType, int status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "GooglePlay:CredentialsJson is not set: purchases cannot be verified")]
    private static partial void LogNotConfigured(ILogger logger);

    private readonly string? _credentialsJson = configuration["GooglePlay:CredentialsJson"];
    private readonly string _packageName = configuration["GooglePlay:PackageName"] ?? "com.lechdigital.coo";

    public Task<GooglePlayPurchaseInfo?> VerifyPurchaseAsync(string productId, string purchaseToken, CancellationToken cancellationToken) =>
        CallAsync(productId, service => Products.IsOneTime(productId)
            ? VerifyOneTimeAsync(service, productId, purchaseToken, cancellationToken)
            : VerifySubscriptionAsync(service, productId, purchaseToken, cancellationToken));

    public Task AcknowledgeAsync(string productId, string purchaseToken, CancellationToken cancellationToken) =>
        CallAsync<object?>(productId, async service =>
        {
            if (Products.IsOneTime(productId))
                await service.Purchases.Products.Acknowledge(new ProductPurchasesAcknowledgeRequest(), _packageName, productId, purchaseToken).ExecuteAsync(cancellationToken);
            else
                await service.Purchases.Subscriptions.Acknowledge(new SubscriptionPurchasesAcknowledgeRequest(), _packageName, productId, purchaseToken).ExecuteAsync(cancellationToken);
            return null;
        });

    /// <summary>
    /// One call to the Play Developer API. Google not knowing the token (404, 410, or 400 for a malformed one) is an answer:
    /// null. Anything else (not configured, unauthorised, 5xx, a timeout) is not, and throws
    /// <see cref="PlayUnavailableException"/>, so nothing is decided on it (fail closed, ADR 0004 §5).
    /// </summary>
    private async Task<T?> CallAsync<T>(string productId, Func<AndroidPublisherService, Task<T?>> call)
    {
        if (string.IsNullOrWhiteSpace(_credentialsJson))
        {
            LogNotConfigured(logger);
            throw new PlayUnavailableException("GooglePlay:CredentialsJson is not set.");
        }

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
            return await call(service);
        }
        catch (GoogleApiException e) when (e.HttpStatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone or HttpStatusCode.BadRequest)
        {
            return default;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            LogUnavailable(logger, productId, e.GetType().Name, e is GoogleApiException api ? (int)api.HttpStatusCode : 0);
            throw new PlayUnavailableException($"Google Play could not be asked about {productId}.", e);
        }
    }

    private async Task<GooglePlayPurchaseInfo?> VerifySubscriptionAsync(AndroidPublisherService service, string productId, string purchaseToken, CancellationToken cancellationToken)
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
            "SUBSCRIPTION_STATE_PENDING" => SubscriptionStatus.Paused,
            _ => SubscriptionStatus.Expired
        };

        var autoRenewing = purchase.LineItems?.Any(li => li.AutoRenewingPlan?.AutoRenewEnabled == true) ?? false;

        return new GooglePlayPurchaseInfo(
            purchaseToken,
            purchase.LineItems?.FirstOrDefault()?.ProductId ?? productId,
            expiry,
            status,
            autoRenewing,
            purchase.ExternalAccountIdentifiers?.ObfuscatedExternalAccountId,
            purchase.AcknowledgementState == "ACKNOWLEDGEMENT_STATE_PENDING");
    }

    /// <summary>
    /// Lifetime is a one-time product: purchases.products, not subscriptionsv2. A completed purchase never expires; a
    /// refunded or voided one (purchase state 1) is over, and a pending one (2, cash payments) is not Premium yet.
    /// </summary>
    private async Task<GooglePlayPurchaseInfo?> VerifyOneTimeAsync(AndroidPublisherService service, string productId, string purchaseToken, CancellationToken cancellationToken)
    {
        var purchase = await service.Purchases.Products.Get(_packageName, productId, purchaseToken).ExecuteAsync(cancellationToken);
        if (purchase is null)
            return null;

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var (status, expiry) = purchase.PurchaseState switch
        {
            0 => (SubscriptionStatus.Active, Products.NeverExpires),
            2 => (SubscriptionStatus.Paused, now),
            _ => (SubscriptionStatus.Expired, now),
        };
        return new GooglePlayPurchaseInfo(
            purchaseToken,
            purchase.ProductId ?? productId,
            expiry,
            status,
            false,
            purchase.ObfuscatedExternalAccountId,
            purchase.AcknowledgementState == 0);
    }
}
