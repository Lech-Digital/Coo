using System.Text.Json.Serialization;
using Coo.Mobile.Application.Advisor;

namespace Coo.Mobile.Infrastructure.Advisor;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AskAdvisorRequest))]
[JsonSerializable(typeof(AskAdvisorResponse))]
[JsonSerializable(typeof(AdvisorMessageHistory))]
[JsonSerializable(typeof(List<AdvisorMessageHistory>))]
[JsonSerializable(typeof(AdvisorThreadSummary))]
[JsonSerializable(typeof(List<AdvisorThreadSummary>))]
[JsonSerializable(typeof(AdvisorThread))]
[JsonSerializable(typeof(AdvisorMessageItem))]
[JsonSerializable(typeof(List<AdvisorMessageItem>))]
internal partial class AdvisorJsonContext : JsonSerializerContext
{
}
