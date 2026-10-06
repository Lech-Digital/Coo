using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Coo.Contracts.Households;
using Coo.Contracts.Sync;
using Coo.Domain.Households;
using Coo.Mobile.Application.Auth;
using Coo.Mobile.Application.Households;

namespace Coo.Mobile.Infrastructure.Households;

/// <summary>
/// How a household answer becomes a result (Spec 011b §10.1). Only the server's own codes are refusals; a bare status, a
/// portal's page, an unreadable body, no network or a refresh that could not run are "unavailable" (Review Focus 5).
/// </summary>
internal static class HouseholdHttp
{
    private static readonly HashSet<string> Codes = new(StringComparer.Ordinal)
    {
        HouseholdErrors.InvitationInvalid, HouseholdErrors.InvitationRoleUnsupported, HouseholdErrors.InvitationTooMany,
        HouseholdErrors.Forbidden, HouseholdErrors.MemberNotFound, HouseholdErrors.OwnerCannotLeave, HouseholdErrors.LastFamily,
        HouseholdErrors.ConfirmationRequired, HouseholdErrors.IdTaken, HouseholdErrors.AlreadyHasFamily, HouseholdErrors.Malformed,
        HouseholdErrors.RateLimited, SyncErrors.ConsentRequired,
    };

    public static async Task<HttpResponseMessage> SendAsync(HttpClient http, HttpRequestMessage request, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException e)
        {
            throw new HouseholdUnavailableException("The server could not be reached.", e);
        }
        catch (TaskCanceledException e) when (!cancellationToken.IsCancellationRequested)
        {
            throw new HouseholdUnavailableException("The server did not answer in time.", e);
        }
        catch (AuthUnavailableException e)
        {
            throw new HouseholdUnavailableException("The session could not be refreshed now.", e);
        }

        if (response.IsSuccessStatusCode)
            return response;

        using (response)
        {
            var code = await TryCodeAsync(response, cancellationToken);
            if (code is not null && Codes.Contains(code))
                throw new HouseholdRefusedException(code);
            throw new HouseholdUnavailableException(string.Create(CultureInfo.InvariantCulture, $"The server answered {(int)response.StatusCode}."));
        }
    }

    public static async Task<T> ReadAsync<T>(HttpResponseMessage response, JsonTypeInfo<T> type, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync(type, cancellationToken)
                ?? throw new HouseholdUnavailableException("The server sent an empty answer.");
        }
        catch (Exception e) when (IsUnreadable(e))
        {
            throw new HouseholdUnavailableException("The server sent an unreadable answer.", e);
        }
    }

    /// <summary>A role this build does not know makes the answer unusable rather than guessed.</summary>
    public static Role ParseRole(string? value) =>
        RoleNames.Parse(value) ?? throw new HouseholdUnavailableException("The server named a role this version does not know.");

    private static async Task<string?> TryCodeAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return (await response.Content.ReadFromJsonAsync(HouseholdJsonContext.Default.ErrorResponse, cancellationToken))?.Code;
        }
        catch (Exception e) when (IsUnreadable(e))
        {
            return null;
        }
    }

    /// <summary>What reading a body can throw when it is not the JSON the API sends (Increment 10 lesson).</summary>
    private static bool IsUnreadable(Exception e) =>
        e is JsonException or NotSupportedException or InvalidOperationException or HttpRequestException or IOException;
}
