using Coo.Api.Application.Billing;
using Coo.Api.Infrastructure.Persistence;
using Coo.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace Coo.Api.Infrastructure.Billing;

internal sealed class BillingStore(IDbContextFactory<CooDbContext> contexts, TimeProvider timeProvider) : IBillingStore
{
    public async Task SaveSubscriptionAsync(Subscription subscription, CancellationToken cancellationToken)
    {
        await using var database = await contexts.CreateDbContextAsync(cancellationToken);
        var record = await database.Subscriptions.FirstOrDefaultAsync(s => s.PurchaseToken == subscription.PurchaseToken, cancellationToken);
        if (record is null)
        {
            record = new SubscriptionRecord
            {
                PurchaseToken = subscription.PurchaseToken,
                HouseholdId = subscription.HouseholdId.Value,
                UserId = subscription.UserId,
                ProductId = subscription.ProductId,
                Status = subscription.Status.ToString(),
                ExpiryDate = subscription.ExpiryDate,
                CreatedAt = subscription.CreatedAt,
                UpdatedAt = subscription.UpdatedAt
            };
            database.Subscriptions.Add(record);
        }
        else
        {
            record.Status = subscription.Status.ToString();
            record.ExpiryDate = subscription.ExpiryDate;
            record.UpdatedAt = subscription.UpdatedAt;
        }

        await database.SaveChangesAsync(cancellationToken);
    }

    public async Task<Subscription?> FindSubscriptionAsync(string purchaseToken, CancellationToken cancellationToken)
    {
        await using var database = await contexts.CreateDbContextAsync(cancellationToken);
        var record = await database.Subscriptions.AsNoTracking().FirstOrDefaultAsync(s => s.PurchaseToken == purchaseToken, cancellationToken);
        if (record is null) return null;

        return new Subscription(
            record.PurchaseToken,
            new HouseholdId(record.HouseholdId),
            record.UserId,
            record.ProductId,
            Enum.Parse<SubscriptionStatus>(record.Status),
            record.ExpiryDate,
            record.CreatedAt,
            record.UpdatedAt
        );
    }

    public async Task<bool> IsHouseholdPremiumAsync(HouseholdId householdId, CancellationToken cancellationToken)
    {
        await using var database = await contexts.CreateDbContextAsync(cancellationToken);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        return await database.Subscriptions
            .AsNoTracking()
            .AnyAsync(s => s.HouseholdId == householdId.Value &&
                           (s.Status == nameof(SubscriptionStatus.Active) || s.Status == nameof(SubscriptionStatus.GracePeriod)) &&
                           s.ExpiryDate > now,
                cancellationToken);
    }
}
