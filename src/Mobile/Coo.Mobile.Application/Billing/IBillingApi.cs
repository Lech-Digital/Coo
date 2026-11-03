namespace Coo.Mobile.Application.Billing;

/// <summary>The server's side of Premium (Spec 012): a purchase is reported there, so the whole family has it.</summary>
public interface IBillingApi
{
    /// <summary>Whether the household has Premium on the server; null when the server could not be asked.</summary>
    Task<bool?> CheckEntitlementAsync(Guid householdId, CancellationToken cancellationToken);

    /// <summary>Hands a Play purchase to the server to verify and record for <paramref name="householdId"/>'s family.</summary>
    Task<ReceiptOutcome> VerifyReceiptAsync(Guid householdId, string productId, string purchaseToken, CancellationToken cancellationToken);
}

/// <summary>What the server said about a purchase reported for a family.</summary>
public enum ReceiptOutcome
{
    /// <summary>Recorded: the family has Premium from it.</summary>
    Recorded,

    /// <summary>
    /// Refused: bought for another family, unknown to Google, or the caller is not in the family. This Google account's
    /// purchase is then not this family's Premium (one phone, several families, owner's test 2026-11-03).
    /// </summary>
    Refused,

    /// <summary>Nothing was decided: offline, signed out, or the server or Google unavailable.</summary>
    Unreachable,
}
