using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Coo.Contracts.Sync;
using Coo.Domain.Common;
using Coo.Mobile.Application.Auth;
using Coo.Mobile.Application.Sync;

namespace Coo.Mobile.Infrastructure.Sync;

/// <summary>
/// How a sync answer becomes a result (Spec 011 §7.1, ADR 0002 §10). Only the server's own codes are refusals, and only
/// the exact membership-revoked problem for the household asked about counts as one; a bare 403, a portal's page, a body
/// that cannot be read, no network or a refresh that could not run are "unavailable": a failed cycle that changes nothing.
/// </summary>
internal static class SyncHttp
{
    public static async Task<HttpResponseMessage> SendAsync(HttpClient http, HttpRequestMessage request, HouseholdId household, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException e)
        {
            throw new SyncUnavailableException("The server could not be reached.", e);
        }
        catch (TaskCanceledException e) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SyncUnavailableException("The server did not answer in time.", e);
        }
        catch (AuthUnavailableException e)
        {
            throw new SyncUnavailableException("The session could not be refreshed now.", e);
        }

        if (response.IsSuccessStatusCode)
            return response;

        using (response)
        {
            var status = (int)response.StatusCode;
            var body = await TryReadErrorAsync(response, cancellationToken);
            if (status == StatusCodes426 && body?.Code == SyncErrors.ClientTooOld)
                throw new SyncRefusedException(SyncRefusal.ClientTooOld, null);
            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                if (body?.Type == SyncErrors.MembershipRevokedType && body.HouseholdId == household.Value)
                    throw new SyncRefusedException(SyncRefusal.MembershipRevoked, household);
                if (body?.Code == SyncErrors.ConsentRequired)
                    throw new SyncRefusedException(SyncRefusal.ConsentRequired, null);
                if (body?.Code == SyncErrors.NotAMember)
                    throw new SyncRefusedException(SyncRefusal.NotAMember, null);
            }

            if (response.StatusCode == HttpStatusCode.BadRequest && body?.Code is SyncErrors.Malformed or SyncErrors.BatchTooLarge)
                throw new SyncRefusedException(SyncRefusal.Malformed, null);

            throw new SyncUnavailableException(string.Create(CultureInfo.InvariantCulture, $"The server answered {status}."));
        }
    }

    public static async Task<T> ReadAsync<T>(HttpResponseMessage response, JsonTypeInfo<T> type, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync(type, cancellationToken)
                ?? throw new SyncUnavailableException("The server sent an empty answer.");
        }
        catch (Exception e) when (IsUnreadable(e))
        {
            throw new SyncUnavailableException("The server sent an unreadable answer.", e);
        }
    }

    private const int StatusCodes426 = 426;

    private static async Task<SyncErrorBody?> TryReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync(SyncJsonContext.Default.SyncErrorBody, cancellationToken);
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
