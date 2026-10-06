using System.Net.Http.Json;
using Coo.Contracts.Auth;
using Coo.Contracts.Households;
using Coo.Domain.Common;
using Coo.Domain.Households;
using Coo.Mobile.Application.Households;

namespace Coo.Mobile.Infrastructure.Households;

/// <summary><c>/api/households</c> and <c>/api/invitations</c> through the refreshing handler (Spec 011b §10.1). Codes travel in bodies.</summary>
internal sealed class HouseholdClient(HttpClient http) : IHouseholdApi
{
    private static readonly Uri PreviewUri = new("api/invitations/preview", UriKind.Relative);
    private static readonly Uri AcceptUri = new("api/invitations/accept", UriKind.Relative);
    private static readonly Uri HouseholdsUri = new("api/households", UriKind.Relative);

    public async Task<CreatedInvitation> CreateInvitationAsync(HouseholdId household, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Path(household, "invitations"))
        {
            Content = JsonContent.Create(new CreateInvitationRequest(RoleNames.Text(Role.Parent)), HouseholdJsonContext.Default.CreateInvitationRequest),
        };
        using var response = await HouseholdHttp.SendAsync(http, request, cancellationToken);
        var created = await HouseholdHttp.ReadAsync(response, HouseholdJsonContext.Default.InvitationCreatedResponse, cancellationToken);
        var code = InvitationCode.Parse(created.Code);
        if (code.IsFailure || created.InvitationId == Guid.Empty)
            throw new HouseholdUnavailableException("The server sent a code this version cannot read.");
        return new CreatedInvitation(created.InvitationId, code.Value, new Uri(InvitationLinks.For(code.Value.Value)), created.ExpiresAt);
    }

    public async Task WithdrawInvitationAsync(HouseholdId household, Guid invitationId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, Path(household, $"invitations/{invitationId:D}"));
        using var response = await HouseholdHttp.SendAsync(http, request, cancellationToken);
    }

    public async Task<FamilyMembers> ListMembersAsync(HouseholdId household, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Path(household, "members"));
        using var response = await HouseholdHttp.SendAsync(http, request, cancellationToken);
        var answer = await HouseholdHttp.ReadAsync(response, HouseholdJsonContext.Default.MembersResponse, cancellationToken);
        return new FamilyMembers(
            [.. (answer.Members ?? []).Select(m => new FamilyMember(new UserId(m.UserId), m.Name ?? "", m.Email ?? "", HouseholdHttp.ParseRole(m.Role), m.JoinedAt, m.IsYou))],
            [.. (answer.Invitations ?? []).Select(i => new FamilyInvitation(i.InvitationId, HouseholdHttp.ParseRole(i.Role), new UserId(i.CreatedBy), i.CreatedAt, i.ExpiresAt))]);
    }

    public async Task RemoveMemberAsync(HouseholdId household, UserId member, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, Path(household, $"members/{member.Value:D}"));
        using var response = await HouseholdHttp.SendAsync(http, request, cancellationToken);
    }

    public async Task<LeaveOutcome> LeaveAsync(HouseholdId household, bool confirm, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Path(household, "leave"))
        {
            Content = JsonContent.Create(new LeaveRequest(confirm), HouseholdJsonContext.Default.LeaveRequest),
        };
        using var response = await HouseholdHttp.SendAsync(http, request, cancellationToken);
        var answer = await HouseholdHttp.ReadAsync(response, HouseholdJsonContext.Default.LeaveResponse, cancellationToken);
        return answer.Outcome switch
        {
            LeaveOutcomes.Deleted => LeaveOutcome.Deleted,
            LeaveOutcomes.Left => LeaveOutcome.Left,
            _ => throw new HouseholdUnavailableException("The server named an outcome this version does not know."),
        };
    }

    public async Task CreateHouseholdAsync(HouseholdId household, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, HouseholdsUri)
        {
            Content = JsonContent.Create(new CreateHouseholdRequest(household.Value), HouseholdJsonContext.Default.CreateHouseholdRequest),
        };
        using var response = await HouseholdHttp.SendAsync(http, request, cancellationToken);
    }

    public async Task<InvitationPreview> PreviewAsync(InvitationCode code, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(code);
        using var request = new HttpRequestMessage(HttpMethod.Post, PreviewUri) { Content = CodeBody(code) };
        using var response = await HouseholdHttp.SendAsync(http, request, cancellationToken);
        var p = await HouseholdHttp.ReadAsync(response, HouseholdJsonContext.Default.InvitationPreviewResponse, cancellationToken);
        if (p.HouseholdId == Guid.Empty)
            throw new HouseholdUnavailableException("The server sent a preview without a family.");
        return new InvitationPreview(new HouseholdId(p.HouseholdId), p.InviterName ?? "", p.BabyNames ?? [], HouseholdHttp.ParseRole(p.Role), p.ExpiresAt, p.AlreadyMember);
    }

    public async Task<AcceptedInvitation> AcceptAsync(InvitationCode code, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(code);
        using var request = new HttpRequestMessage(HttpMethod.Post, AcceptUri) { Content = CodeBody(code) };
        using var response = await HouseholdHttp.SendAsync(http, request, cancellationToken);
        var a = await HouseholdHttp.ReadAsync(response, HouseholdJsonContext.Default.AcceptInvitationResponse, cancellationToken);
        var outcome = a.Outcome switch
        {
            AcceptOutcomes.Joined => AcceptOutcome.Joined,
            AcceptOutcomes.Rejoined => AcceptOutcome.Rejoined,
            AcceptOutcomes.AlreadyMember => AcceptOutcome.AlreadyMember,
            _ => throw new HouseholdUnavailableException("The server named an outcome this version does not know."),
        };
        if (a.HouseholdId == Guid.Empty)
            throw new HouseholdUnavailableException("The server sent an answer without a family.");
        return new AcceptedInvitation(new HouseholdId(a.HouseholdId), HouseholdHttp.ParseRole(a.Role), outcome);
    }

    private static Uri Path(HouseholdId household, string rest) => new($"api/households/{household.Value:D}/{rest}", UriKind.Relative);

    private static JsonContent CodeBody(InvitationCode code) =>
        JsonContent.Create(new InvitationCodeRequest(code.Value), HouseholdJsonContext.Default.InvitationCodeRequest);
}
