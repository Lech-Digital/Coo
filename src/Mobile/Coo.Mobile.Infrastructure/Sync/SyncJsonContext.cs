using System.Text.Json;
using System.Text.Json.Serialization;
using Coo.Contracts.Sync;

namespace Coo.Mobile.Infrastructure.Sync;

/// <summary>Any refusal body: <c>{ code }</c>, or the membership-revoked problem's <c>type</c> and <c>householdId</c>.</summary>
internal sealed record SyncErrorBody(string? Code, string? Type, Guid? HouseholdId);

/// <summary>Source-generated for the trimmed Android build, like <c>AuthJsonContext</c>. Web defaults: camelCase.</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(PushRequest))]
[JsonSerializable(typeof(PushResponse))]
[JsonSerializable(typeof(PullResponse))]
[JsonSerializable(typeof(MembershipStatusResponse))]
[JsonSerializable(typeof(ConsentRequest))]
[JsonSerializable(typeof(SyncErrorBody))]
internal sealed partial class SyncJsonContext : JsonSerializerContext;
