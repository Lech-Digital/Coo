using System.Text.Json;
using System.Text.Json.Serialization;
using Coo.Contracts.Auth;
using Coo.Contracts.Households;

namespace Coo.Mobile.Infrastructure.Households;

/// <summary>Source-generated for the trimmed Android build, like <c>SyncJsonContext</c>. Web defaults: camelCase.</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(CreateInvitationRequest))]
[JsonSerializable(typeof(InvitationCreatedResponse))]
[JsonSerializable(typeof(MembersResponse))]
[JsonSerializable(typeof(LeaveRequest))]
[JsonSerializable(typeof(LeaveResponse))]
[JsonSerializable(typeof(CreateHouseholdRequest))]
[JsonSerializable(typeof(InvitationCodeRequest))]
[JsonSerializable(typeof(InvitationPreviewResponse))]
[JsonSerializable(typeof(AcceptInvitationResponse))]
[JsonSerializable(typeof(ErrorResponse))]
internal sealed partial class HouseholdJsonContext : JsonSerializerContext;
