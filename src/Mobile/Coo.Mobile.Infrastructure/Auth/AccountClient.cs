using System.Net.Http.Json;
using Coo.Contracts.Auth;
using Coo.Domain.Common;
using Coo.Mobile.Application.Auth;

namespace Coo.Mobile.Infrastructure.Auth;

/// <summary>Calls with the access token; its <see cref="HttpClient"/> runs through <see cref="AuthenticatingHandler"/>.</summary>
internal sealed class AccountClient(HttpClient http) : IAccountApi
{
    private static readonly Uri SignOutUri = new("api/auth/sign-out", UriKind.Relative);
    private static readonly Uri MeUri = new("api/me", UriKind.Relative);
    private static readonly Uri AccountUri = new("api/account", UriKind.Relative);

    public async Task SignOutAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, SignOutUri);
        using var response = await AuthHttp.SendAsync(http, request, cancellationToken);
    }

    public async Task<AccountOverview> GetMeAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, MeUri);
        using var response = await AuthHttp.SendAsync(http, request, cancellationToken);
        var me = await AuthHttp.ReadAsync(response, AuthJsonContext.Default.MeResponse, cancellationToken);
        return new AccountOverview(
            new AccountProfile(new UserId(me.User.Id), me.User.DisplayName, me.User.Email),
            me.Household is null ? null : new HouseholdId(me.Household.HouseholdId),
            me.Household?.Role,
            [.. me.Devices.Select(d => new DeviceSessionInfo(d.DeviceName, d.LastUsedAt, d.IsCurrent))],
            me.Consent is null ? null : new ConsentInfo(me.Consent.Wording, me.Consent.GivenAt));
    }

    public async Task DeleteAccountAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, AccountUri)
        {
            Content = JsonContent.Create(new DeleteAccountRequest(Confirm: true), AuthJsonContext.Default.DeleteAccountRequest),
        };
        using var response = await AuthHttp.SendAsync(http, request, cancellationToken);
    }
}
