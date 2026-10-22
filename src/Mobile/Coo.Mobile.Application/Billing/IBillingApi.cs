namespace Coo.Mobile.Application.Billing;

/// <summary>The server's side of Premium (Spec 012): a purchase is reported there, so the whole family has it.</summary>
public interface IBillingApi
{
    /// <summary>Whether the household has Premium on the server; null when the server could not be asked.</summary>
    Task<bool?> CheckEntitlementAsync(Guid householdId, CancellationToken cancellationToken);

    /// <summary>Hands a Play purchase to the server to verify and record; false when it was refused or not reached.</summary>
    Task<bool> VerifyReceiptAsync(Guid householdId, string productId, string purchaseToken, CancellationToken cancellationToken);
}
